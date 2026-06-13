using System.Collections.Concurrent;
using FormForge.Api.Infrastructure.EventBus;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FormForge.Api.Features.Permissions;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class PermissionService : IPermissionService
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;

    // Secondary index: roleId → set of userIds whose permissions are currently cached
    // with that role. Populated on cache write; used to find affected cache keys when
    // RolePermissionsChanged fires (IMemoryCache does not support key enumeration).
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, byte>> _roleUserMap = new();

    // Per-user cache-bust version. Incremented on UserRoleAssignmentChanged,
    // UserDeactivated, and (indirectly) RolePermissionsChanged. A compute that
    // captures versionAtStart before the DB read and sees a different version
    // before its cache write knows a bust occurred during compute and discards
    // its (potentially stale) result. (Story 2.6 review patch #2.)
    private readonly ConcurrentDictionary<Guid, long> _userVersions = new();

    public PermissionService(
        IServiceScopeFactory scopeFactory,
        IMemoryCache cache,
        IDomainEventBus bus)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(bus);

        _scopeFactory = scopeFactory;
        _cache = cache;

        bus.Subscribe<UserRoleAssignmentChanged>(OnUserRoleAssignmentChanged);
        bus.Subscribe<RolePermissionsChanged>(OnRolePermissionsChanged);
        bus.Subscribe<UserDeactivated>(OnUserDeactivated);
        bus.Subscribe<UserReactivated>(OnUserReactivated);
    }

    // Namespaced cache key so PermissionService cannot collide with any other
    // Singleton service caching by Guid in the shared IMemoryCache. (Patch #9.)
    private static string CacheKey(Guid userId) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"permissions:{userId:N}");

    public async Task<EffectivePermissions> GetEffectivePermissionsAsync(Guid userId, CancellationToken ct)
    {
        var key = CacheKey(userId);

        if (_cache.TryGetValue<EffectivePermissions>(key, out var cached) && cached is not null)
        {
            return cached;
        }

        // Capture the user's bust-version BEFORE the DB read. If a bust event
        // for this user fires any time between this read and the post-write
        // re-check below, we evict the (potentially stale) entry we just wrote.
        var versionAtStart = _userVersions.GetOrAdd(userId, 0L);

        var permissions = await ComputePermissionsAsync(userId, ct).ConfigureAwait(false);

        // Register in _roleUserMap BEFORE the cache write so that any
        // RolePermissionsChanged firing after this point sees this user, bumps
        // _userVersions[userId], and our post-write check evicts the entry.
        foreach (var roleId in permissions.RoleIds)
        {
            _roleUserMap.GetOrAdd(roleId, _ => new ConcurrentDictionary<Guid, byte>())
                        .TryAdd(userId, 0);
        }

        using (var entry = _cache.CreateEntry(key))
        {
            entry.Value = permissions;
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            // Natural eviction (TTL, memory pressure, Replaced) must clean up the
            // secondary index so it doesn't grow without bound. (Patch #4.)
            entry.RegisterPostEvictionCallback(OnEntryEvicted, userId);
        }

        // If a bust raced our compute, discard. Idempotent with the bust handler's
        // own _cache.Remove call.
        if (_userVersions.GetOrAdd(userId, 0L) != versionAtStart)
        {
            _cache.Remove(key);
        }

        return permissions;
    }

    public async Task<CrudFlags> GetCrudFlagsAsync(Guid userId, string resourceId, CancellationToken ct)
    {
        // ThrowIfNullOrWhiteSpace (not …OrEmpty) so a route value of "   " fails
        // fast as ArgumentException rather than silently Trim()-ing to "" and
        // misleading the caller with a 403 on a malformed resourceId.
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);

        var permissions = await GetEffectivePermissionsAsync(userId, ct).ConfigureAwait(false);

        // Platform-admin gets full CRUD on any resource, including designerIds with no
        // role_permissions rows yet — avoids pre-seeding permissions for every future
        // dynamic table before it is provisioned.
        if (permissions.RoleIds.Contains(PlatformAdminRoleId))
        {
            return new CrudFlags(CanCreate: true, CanRead: true, CanUpdate: true, CanDelete: true, CanExport: true);
        }

        // The PerResource dictionary uses OrdinalIgnoreCase (see ComputePermissionsAsync)
        // so we only normalize whitespace here. Lower-casing is no longer required since
        // any non-API DB writer that stored mixed-case rows still resolves correctly. (Patch #7.)
        var normalizedId = resourceId.Trim();
        return permissions.PerResource.TryGetValue(normalizedId, out var flags) ? flags : default;
    }

    private async Task<EffectivePermissions> ComputePermissionsAsync(Guid userId, CancellationToken ct)
    {
        // Singleton service → Scoped DbContext via IServiceScopeFactory (AR-36 pattern).
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();

        // Story 2.8: IsActive must reflect users.is_active, not a hard-coded true.
        // FirstOrDefaultAsync<bool> returns false (default) if the userId is unknown
        // (e.g., a JWT outliving a deleted user) — which is the correct "deny all"
        // posture. One extra round-trip per cache miss; acceptable given the 30 s TTL.
        var isActive = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.IsActive)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var roleIds = await db.UserRoles
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.RoleId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // OrdinalIgnoreCase so a future non-API DB writer that stored mixed-case
        // resource_id (e.g., a migration backfill) doesn't silently make the row
        // unreachable from the lowercase-normalizing API path. (Patch #7.)
        var perResource = new Dictionary<string, (bool c, bool r, bool u, bool d, bool e)>(StringComparer.OrdinalIgnoreCase);

        if (roleIds.Count > 0)
        {
            var perms = await db.RolePermissions
                .Where(rp => roleIds.Contains(rp.RoleId))
                .Select(rp => new { rp.ResourceId, rp.CanCreate, rp.CanRead, rp.CanUpdate, rp.CanDelete, rp.CanExport })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var perm in perms)
            {
                if (!perResource.TryGetValue(perm.ResourceId, out var existing))
                {
                    perResource[perm.ResourceId] = (perm.CanCreate, perm.CanRead, perm.CanUpdate, perm.CanDelete, perm.CanExport);
                }
                else
                {
                    // Union across roles: any role granting a flag grants it to the user (FR-4).
                    perResource[perm.ResourceId] = (
                        existing.c || perm.CanCreate,
                        existing.r || perm.CanRead,
                        existing.u || perm.CanUpdate,
                        existing.d || perm.CanDelete,
                        existing.e || perm.CanExport);
                }
            }
        }

        var result = perResource.ToDictionary(
            kvp => kvp.Key,
            kvp => new CrudFlags(kvp.Value.c, kvp.Value.r, kvp.Value.u, kvp.Value.d, kvp.Value.e),
            StringComparer.OrdinalIgnoreCase);

        // Story 8.2 (FR-56 / AR-58) — dataset-management is a platform-level capability
        // unioned across the user's roles: any role with can_manage_datasets grants it.
        // A simple EXISTS subquery on the already-resolved roleIds; no .Include needed.
        var canManageDatasets = roleIds.Count > 0
            && await db.Roles
                .Where(r => roleIds.Contains(r.Id) && r.CanManageDatasets)
                .AnyAsync(ct)
                .ConfigureAwait(false);

        return new EffectivePermissions(
            UserId: userId,
            ComputedAt: DateTimeOffset.UtcNow,
            IsActive: isActive,
            PerResource: result,
            RoleIds: new HashSet<Guid>(roleIds),
            CanManageDatasets: canManageDatasets);
    }

    private void OnEntryEvicted(object key, object? value, EvictionReason reason, object? state)
    {
        if (state is not Guid evictedUserId)
        {
            return;
        }

        foreach (var bucket in _roleUserMap.Values)
        {
            bucket.TryRemove(evictedUserId, out _);
        }
    }

    private void OnUserRoleAssignmentChanged(UserRoleAssignmentChanged e)
    {
        _userVersions.AddOrUpdate(e.UserId, 1L, static (_, v) => v + 1);
        _cache.Remove(CacheKey(e.UserId));
        // OnEntryEvicted handles _roleUserMap cleanup when the entry existed; if
        // it didn't, an in-flight compute will see the bumped version and discard.
    }

    private void OnRolePermissionsChanged(RolePermissionsChanged e)
    {
        if (!_roleUserMap.TryGetValue(e.RoleId, out var users))
        {
            return;
        }

        foreach (var userId in users.Keys)
        {
            _userVersions.AddOrUpdate(userId, 1L, static (_, v) => v + 1);
            _cache.Remove(CacheKey(userId));
        }
    }

    private void OnUserDeactivated(UserDeactivated e)
    {
        _userVersions.AddOrUpdate(e.UserId, 1L, static (_, v) => v + 1);
        _cache.Remove(CacheKey(e.UserId));
    }

    // Reactivation must bust the cache too — without this, a reactivated user
    // whose JWT survived the toggle still sees IsActive=false in cached permissions
    // for up to the 30 s TTL, and `usePermission` hides every UI control during
    // that window. (Story 2.8 code review patch P4.)
    private void OnUserReactivated(UserReactivated e)
    {
        _userVersions.AddOrUpdate(e.UserId, 1L, static (_, v) => v + 1);
        _cache.Remove(CacheKey(e.UserId));
    }
}
