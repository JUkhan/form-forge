using FormForge.Api.Common;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Features.Roles.Dtos;
using FormForge.Api.Infrastructure.EventBus;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FormForge.Api.Features.Roles;

internal enum CreateRoleOutcome { Success, DuplicateName }
internal sealed record CreateRoleResult(CreateRoleOutcome Outcome, Guid? RoleId = null, RoleResponse? Role = null);

internal enum UpdateRoleOutcome { Success, NotFound, DuplicateName, SystemProtected }
internal sealed record UpdateRoleResult(UpdateRoleOutcome Outcome);

internal enum DeleteRoleOutcome { Success, NotFound, HasAssignments, SystemProtected }
internal sealed record DeleteRoleResult(DeleteRoleOutcome Outcome);

internal interface IRoleService
{
    Task<PagedResult<RoleListItem>> GetRolesAsync(
        int page, int pageSize, string? sort, string? search, string? system, CancellationToken ct);
    Task<RoleResponse?> GetRoleAsync(Guid id, CancellationToken ct);
    Task<CreateRoleResult> CreateRoleAsync(CreateRoleRequest request, CancellationToken ct);
    Task<UpdateRoleResult> UpdateRoleAsync(Guid id, UpdateRoleRequest request, CancellationToken ct);
    Task<DeleteRoleResult> DeleteRoleAsync(Guid id, CancellationToken ct);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class RoleService(FormForgeDbContext db, IDomainEventBus bus) : IRoleService
{
    public async Task<PagedResult<RoleListItem>> GetRolesAsync(
        int page,
        int pageSize,
        string? sort,
        string? search,
        string? system,
        CancellationToken ct)
    {
        IQueryable<Role> query = db.Roles;

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(r =>
                EF.Functions.ILike(r.Name, term)
                || (r.Description != null && EF.Functions.ILike(r.Description, term)));
        }

        // system/custom maps onto IsSystem; unrecognized values are ignored.
        if (string.Equals(system, "system", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(r => r.IsSystem);
        }
        else if (string.Equals(system, "custom", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(r => !r.IsSystem);
        }

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);

        // Compute Skip in long arithmetic and clamp to int.MaxValue so a very large
        // page (e.g. page=int.MaxValue, pageSize=100) doesn't overflow into a negative
        // int and crash Skip with ArgumentOutOfRangeException. (Story 2.4 review.)
        var skip = (int)Math.Min(int.MaxValue, ((long)page - 1L) * pageSize);

        var items = await ApplySort(query, sort)
            .Skip(skip)
            .Take(pageSize)
            .Select(r => new RoleListItem(
                r.Id,
                r.Name,
                r.Description,
                r.Permissions.Count,
                r.IsSystem))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new PagedResult<RoleListItem>(items, total, page, pageSize);
    }

    // Whitelisted single-column sort ("field:dir"); Name is the stable tiebreaker.
    // Unknown/empty/malformed sort falls back to name asc (the legacy default).
    private static IQueryable<Role> ApplySort(IQueryable<Role> q, string? sort)
    {
        var field = "name";
        var desc = false;
        if (!string.IsNullOrWhiteSpace(sort))
        {
            var parts = sort.Split(':');
            if (parts.Length == 2)
            {
                field = parts[0];
                desc = string.Equals(parts[1], "desc", StringComparison.OrdinalIgnoreCase);
            }
        }

        return field switch
        {
            "description" => Tie(q, r => r.Description, desc),
            "permissionCount" => Tie(q, r => r.Permissions.Count, desc),
            "system" => Tie(q, r => r.IsSystem, desc),
            "name" => desc ? q.OrderByDescending(r => r.Name) : q.OrderBy(r => r.Name),
            _ => q.OrderBy(r => r.Name),
        };

        static IQueryable<Role> Tie<TKey>(
            IQueryable<Role> src,
            System.Linq.Expressions.Expression<Func<Role, TKey>> key,
            bool descending) =>
            (descending ? src.OrderByDescending(key) : src.OrderBy(key)).ThenBy(r => r.Name);
    }

    public async Task<RoleResponse?> GetRoleAsync(Guid id, CancellationToken ct)
    {
        var role = await db.Roles
            .Include(r => r.Permissions)
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            .ConfigureAwait(false);

        return role is null ? null : ToResponse(role);
    }

    public async Task<CreateRoleResult> CreateRoleAsync(CreateRoleRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalized = request.Name.Trim().ToLowerInvariant();

        var exists = await db.Roles
            .AnyAsync(r => r.Name == normalized, ct)
            .ConfigureAwait(false);

        if (exists)
        {
            return new CreateRoleResult(CreateRoleOutcome.DuplicateName);
        }

        var role = new Role
        {
            Name = normalized,
            Description = NormalizeDescription(request.Description),
            IsSystem = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        foreach (var p in request.Permissions)
        {
            role.Permissions.Add(new RolePermission
            {
                ResourceId = p.ResourceId.Trim().ToLowerInvariant(),
                CanCreate = p.CanCreate,
                CanRead = p.CanRead,
                CanUpdate = p.CanUpdate,
                CanDelete = p.CanDelete,
                CanExport = p.CanExport,
            });
        }

        db.Roles.Add(role);

        // Wrap SaveChanges to translate the race-window unique-violation on
        // uq_roles_name back into the documented 409 outcome. Without this,
        // two concurrent POSTs with the same name both pass the AnyAsync
        // pre-check and the second insert surfaces as a 500. (Story 2.4 review.)
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException { SqlState: "23505" } pg
            && string.Equals(pg.ConstraintName, "uq_roles_name", StringComparison.Ordinal))
        {
            return new CreateRoleResult(CreateRoleOutcome.DuplicateName);
        }

        // Return the persisted response directly so the handler doesn't need a
        // second roundtrip — which had a race window producing 201 with null body
        // if a concurrent DELETE landed in between. (Story 2.4 review.)
        return new CreateRoleResult(CreateRoleOutcome.Success, role.Id, ToResponse(role));
    }

    public async Task<UpdateRoleResult> UpdateRoleAsync(Guid id, UpdateRoleRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var role = await db.Roles
            .Include(r => r.Permissions)
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            .ConfigureAwait(false);

        if (role is null)
        {
            return new UpdateRoleResult(UpdateRoleOutcome.NotFound);
        }

        if (role.IsSystem)
        {
            return new UpdateRoleResult(UpdateRoleOutcome.SystemProtected);
        }

        var normalized = request.Name.Trim().ToLowerInvariant();

        var nameConflict = await db.Roles
            .AnyAsync(r => r.Name == normalized && r.Id != id, ct)
            .ConfigureAwait(false);

        if (nameConflict)
        {
            return new UpdateRoleResult(UpdateRoleOutcome.DuplicateName);
        }

        role.Name = normalized;
        role.Description = NormalizeDescription(request.Description);
        // Story 8.2 — dataset-management capability. System roles never reach here
        // (guarded by the IsSystem early-return above), so no extra guard is needed.
        // Nullable: omitting the field from the PUT body preserves the existing value
        // (safe for clients that predate the field); sending false explicitly clears it.
        if (request.CanManageDatasets.HasValue)
            role.CanManageDatasets = request.CanManageDatasets.Value;
        role.UpdatedAt = DateTimeOffset.UtcNow;

        // Replace permissions atomically: remove all, re-add from request.
        // Explicit RemoveRange (vs collection.Clear) makes the DELETE deterministic
        // and visible in the generated SQL — the FK ON DELETE CASCADE would also
        // work, but the explicit form keeps the intent obvious here.
        db.RolePermissions.RemoveRange(role.Permissions);
        foreach (var p in request.Permissions)
        {
            role.Permissions.Add(new RolePermission
            {
                ResourceId = p.ResourceId.Trim().ToLowerInvariant(),
                CanCreate = p.CanCreate,
                CanRead = p.CanRead,
                CanUpdate = p.CanUpdate,
                CanDelete = p.CanDelete,
                CanExport = p.CanExport,
            });
        }

        // Translate the race-window unique-violation on uq_roles_name into the
        // documented 409 outcome (same as CreateRoleAsync). (Story 2.4 review.)
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException { SqlState: "23505" } pg
            && string.Equals(pg.ConstraintName, "uq_roles_name", StringComparison.Ordinal))
        {
            return new UpdateRoleResult(UpdateRoleOutcome.DuplicateName);
        }

        // Publish after commit so PermissionService busts every cached user holding
        // this role. Only UpdateRoleAsync needs this: CreateRoleAsync has no holders;
        // DeleteRoleAsync is guarded by HasAssignments.
        bus.Publish(new RolePermissionsChanged(id));
        return new UpdateRoleResult(UpdateRoleOutcome.Success);
    }

    // Empty/whitespace descriptions normalize to null so GET round-trips
    // are idempotent — a client posting "" sees null on read, not "". (Story 2.4 review.)
    private static string? NormalizeDescription(string? raw) =>
        raw?.Trim() is { Length: > 0 } d ? d : null;

    public async Task<DeleteRoleResult> DeleteRoleAsync(Guid id, CancellationToken ct)
    {
        var role = await db.Roles
            .Include(r => r.UserRoles)
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            .ConfigureAwait(false);

        if (role is null)
        {
            return new DeleteRoleResult(DeleteRoleOutcome.NotFound);
        }

        if (role.IsSystem)
        {
            return new DeleteRoleResult(DeleteRoleOutcome.SystemProtected);
        }

        if (role.UserRoles.Count > 0)
        {
            return new DeleteRoleResult(DeleteRoleOutcome.HasAssignments);
        }

        db.Roles.Remove(role);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new DeleteRoleResult(DeleteRoleOutcome.Success);
    }

    private static RoleResponse ToResponse(Role role) =>
        new(
            role.Id,
            role.Name,
            role.Description,
            role.IsSystem,
            role.CreatedAt,
            role.UpdatedAt,
            // Deterministic order on ResourceId so successive GETs (and SPA diffing)
            // don't see spurious changes from Postgres heap order. (Story 2.4 review.)
            role.Permissions
                .OrderBy(p => p.ResourceId, StringComparer.Ordinal)
                .Select(p => new PermissionRecord(p.ResourceId, p.CanCreate, p.CanRead, p.CanUpdate, p.CanDelete, p.CanExport))
                .ToList(),
            role.CanManageDatasets);
}
