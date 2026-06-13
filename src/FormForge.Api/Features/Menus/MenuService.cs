using FormForge.Api.Common;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Features.Menus.Dtos;
using FormForge.Api.Features.Permissions;
using FormForge.Api.Features.Provisioning;
using FormForge.Api.Features.SchemaRegistry;
using FormForge.Api.Infrastructure.EventBus;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FormForge.Api.Features.Menus;

internal enum CreateMenuOutcome { Success, ParentNotFound, MaxDepthExceeded }
internal sealed record CreateMenuResult(CreateMenuOutcome Outcome, Guid? MenuId = null, MenuResponse? Menu = null);

internal enum UpdateMenuOutcome { Success, NotFound }
internal sealed record UpdateMenuResult(UpdateMenuOutcome Outcome);

internal enum DeleteMenuOutcome { Success, NotFound, HasChildren }
internal sealed record DeleteMenuResult(DeleteMenuOutcome Outcome);

internal enum AssignMenuRolesOutcome { Success, MenuNotFound, RolesNotFound, Conflict }
internal sealed record AssignMenuRolesResult(
    AssignMenuRolesOutcome Outcome,
    IReadOnlyList<Guid>? InvalidIds = null);

internal enum ReorderMenusOutcome { Success, MenusNotFound, MixedScopes, Conflict }
internal sealed record ReorderMenusResult(
    ReorderMenusOutcome Outcome,
    IReadOnlyList<Guid>? InvalidIds = null);

internal enum ToggleMenuActiveOutcome { Success, NotFound }
internal sealed record ToggleMenuActiveResult(ToggleMenuActiveOutcome Outcome);

// Story 5.2 — outcome of PUT /api/admin/menus/{id}/binding.
//   MenuNotFound       → 404 MENU_NOT_FOUND
//   DesignerNotFound   → 422 DESIGNER_VERSION_NOT_FOUND
//   VersionNotPublished→ 422 VERSION_NOT_PUBLISHED
//   RepeaterCycle      → 422 REPEATER_CYCLE  (Story 5.5)
//   Success            → 202 (provisioning continues async)
internal enum BindMenuOutcome { Success, MenuNotFound, DesignerNotFound, VersionNotPublished, RepeaterCycle, InvalidPgType }
// Story B — Detail names the offending field + reason for the InvalidPgType outcome.
internal sealed record BindMenuResult(BindMenuOutcome Outcome, string? Detail = null);

// Story 5.2 — outcome of POST /api/admin/menus/{id}/binding/retry.
//   MenuNotFound → 404 MENU_NOT_FOUND
//   NoBinding    → 422 MENU_NO_BINDING (caller cannot retry an unbound menu)
//   Success      → 202 (re-enqueued with same DesignerId/Version)
internal enum RetryBindingOutcome { Success, MenuNotFound, NoBinding }
internal sealed record RetryBindingResult(RetryBindingOutcome Outcome);

// Outcome of PUT /api/admin/menus/{id}/route-path.
//   MenuNotFound → 404 MENU_NOT_FOUND
//   Success      → 204 (route path set/cleared; any Designer binding cleared)
internal enum SetRoutePathOutcome { Success, MenuNotFound }
internal sealed record SetRoutePathResult(SetRoutePathOutcome Outcome);

internal interface IMenuService
{
    Task<PagedResult<MenuListItem>> GetMenusAsync(
        int page, int pageSize, Guid? parentId, string? sort, string? search, string? active, CancellationToken ct);
    Task<MenuResponse?> GetMenuAsync(Guid id, CancellationToken ct);
    Task<CreateMenuResult> CreateMenuAsync(CreateMenuRequest request, CancellationToken ct);
    Task<UpdateMenuResult> UpdateMenuAsync(Guid id, UpdateMenuRequest request, CancellationToken ct);
    Task<DeleteMenuResult> DeleteMenuAsync(Guid id, CancellationToken ct);
    Task<AssignMenuRolesResult> AssignMenuRolesAsync(Guid menuId, IReadOnlyList<Guid> roleIds, CancellationToken ct);
    Task<ReorderMenusResult> ReorderMenusAsync(IReadOnlyList<ReorderMenuItem> items, CancellationToken ct);
    Task<ToggleMenuActiveResult> ToggleMenuActiveAsync(Guid id, bool isActive, CancellationToken ct);
    // Story 4.7 — permission-filtered, isActive-filtered, nested tree for the navbar.
    Task<IReadOnlyList<NavMenuItem>> GetNavMenusForUserAsync(Guid userId, CancellationToken ct);
    // Story 5.2 — bind/retry/diff for Designer ↔ menu wiring.
    Task<BindMenuResult> BindDesignerAsync(Guid menuId, string designerId, int version, Guid? actorId, CancellationToken ct);
    Task<RetryBindingResult> RetryBindingAsync(Guid menuId, Guid? actorId, CancellationToken ct);
    Task<BindingDiffResponse?> GetBindingDiffAsync(Guid menuId, int targetVersion, CancellationToken ct);
    // Custom route path — alternative to a Designer binding (mutually exclusive).
    Task<SetRoutePathResult> SetRoutePathAsync(Guid menuId, string? routePath, CancellationToken ct);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class MenuService(
    FormForgeDbContext db,
    IMenuCache cache,
    IPermissionService permissions,
    IProvisioningService provisioning,
    BindingDiffService diffService,
    CycleDetector cycleDetector,
    IDomainEventBus eventBus) : IMenuService
{
    public async Task<PagedResult<MenuListItem>> GetMenusAsync(
        int page,
        int pageSize,
        Guid? parentId,
        string? sort,
        string? search,
        string? active,
        CancellationToken ct)
    {
        var query = parentId.HasValue
            ? db.Menus.Where(m => m.ParentId == parentId.Value)
            : db.Menus.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(m => EF.Functions.ILike(m.Name, term));
        }

        if (string.Equals(active, "active", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(m => m.IsActive);
        }
        else if (string.Equals(active, "inactive", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(m => !m.IsActive);
        }

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);

        var skip = (int)Math.Min(int.MaxValue, ((long)page - 1L) * pageSize);

        var items = await ApplySort(query, sort)
            .Skip(skip)
            .Take(pageSize)
            .Select(m => new MenuListItem(m.Id, m.Name, m.Order, m.IsActive, m.ParentId, m.CreatedAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new PagedResult<MenuListItem>(items, total, page, pageSize);
    }

    // Whitelisted single-column sort ("field:dir") for the admin "manage" table.
    // `order` is intentionally NOT sortable — it's the manual drag-reorder axis,
    // and the default (no sort param) MUST stay Order→Id so the reorder list and
    // the navbar keep their hand-curated sequence. Tiebreaker: Order then Id.
    private static IQueryable<Menu> ApplySort(IQueryable<Menu> q, string? sort)
    {
        if (string.IsNullOrWhiteSpace(sort))
        {
            return q.OrderBy(m => m.Order).ThenBy(m => m.Id);
        }

        var parts = sort.Split(':');
        var field = parts.Length == 2 ? parts[0] : string.Empty;
        var desc = parts.Length == 2 && string.Equals(parts[1], "desc", StringComparison.OrdinalIgnoreCase);

        return field switch
        {
            "name" => Tie(q, m => m.Name, desc),
            "status" => Tie(q, m => m.IsActive, desc),
            "createdAt" => Tie(q, m => m.CreatedAt, desc),
            _ => q.OrderBy(m => m.Order).ThenBy(m => m.Id),
        };

        static IQueryable<Menu> Tie<TKey>(
            IQueryable<Menu> src,
            System.Linq.Expressions.Expression<Func<Menu, TKey>> key,
            bool descending) =>
            (descending ? src.OrderByDescending(key) : src.OrderBy(key))
                .ThenBy(m => m.Order)
                .ThenBy(m => m.Id);
    }

    public async Task<MenuResponse?> GetMenuAsync(Guid id, CancellationToken ct)
    {
        var menu = await db.Menus
            .Include(m => m.RoleAssignments)
            .FirstOrDefaultAsync(m => m.Id == id, ct)
            .ConfigureAwait(false);

        return menu is null ? null : ToResponse(menu);
    }

    public async Task<CreateMenuResult> CreateMenuAsync(CreateMenuRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ParentId.HasValue)
        {
            var parent = await db.Menus
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == request.ParentId.Value, ct)
                .ConfigureAwait(false);
            if (parent is null)
            {
                return new CreateMenuResult(CreateMenuOutcome.ParentNotFound);
            }
            if (parent.ParentId.HasValue)
            {
                return new CreateMenuResult(CreateMenuOutcome.MaxDepthExceeded);
            }
        }

        var menu = new Menu
        {
            Name = request.Name.Trim(),
            Order = request.Order,
            Icon = request.Icon is { ValueKind: System.Text.Json.JsonValueKind.Null } ? null : request.Icon?.ToString(),
            IsActive = request.IsActive,
            ParentId = request.ParentId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.Menus.Add(menu);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23503" })
        {
            // Parent was deleted between the AsNoTracking lookup and SaveChanges; FK ON DELETE RESTRICT fired.
            return new CreateMenuResult(CreateMenuOutcome.ParentNotFound);
        }
        // Story 4.7: post-commit cache bust uses CancellationToken.None so a request
        // cancelling between SaveChanges and Invalidate still busts the cache. Pure
        // in-memory CTS swap — no I/O, opting out of cancellation is safe and correct.
        // Same pattern applies to every mutation in this service. (deferred-work.md:19.)
        await cache.InvalidateAsync(CancellationToken.None).ConfigureAwait(false);

        // AllowedRoleIds is always [] at create time; Story 4.4 populates via a separate assignment call.
        // Story 5.2 — binding fields are always null at create time (binding is a separate PUT call).
        return new CreateMenuResult(CreateMenuOutcome.Success, menu.Id, new MenuResponse(
            menu.Id, menu.Name, menu.Order, ParseIcon(menu.Icon), menu.IsActive, menu.ParentId,
            [], menu.CreatedAt, menu.UpdatedAt,
            DesignerId: null, BoundVersion: null, ProvisioningStatus: null, ProvisioningError: null,
            RoutePath: null));
    }

    public async Task<UpdateMenuResult> UpdateMenuAsync(Guid id, UpdateMenuRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var menu = await db.Menus
            .FirstOrDefaultAsync(m => m.Id == id, ct)
            .ConfigureAwait(false);

        if (menu is null)
        {
            return new UpdateMenuResult(UpdateMenuOutcome.NotFound);
        }

        menu.Name = request.Name.Trim();
        menu.Order = request.Order;
        menu.Icon = request.Icon is { ValueKind: System.Text.Json.JsonValueKind.Null } ? null : request.Icon?.ToString();
        menu.IsActive = request.IsActive;
        menu.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await cache.InvalidateAsync(CancellationToken.None).ConfigureAwait(false);

        return new UpdateMenuResult(UpdateMenuOutcome.Success);
    }

    public async Task<DeleteMenuResult> DeleteMenuAsync(Guid id, CancellationToken ct)
    {
        var menu = await db.Menus
            .FirstOrDefaultAsync(m => m.Id == id, ct)
            .ConfigureAwait(false);

        if (menu is null)
        {
            return new DeleteMenuResult(DeleteMenuOutcome.NotFound);
        }

        var hasChildren = await db.Menus.AnyAsync(m => m.ParentId == id, ct).ConfigureAwait(false);
        if (hasChildren)
        {
            return new DeleteMenuResult(DeleteMenuOutcome.HasChildren);
        }

        db.Menus.Remove(menu);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23503" })
        {
            // A child was inserted after the AnyAsync check — FK ON DELETE RESTRICT fired.
            return new DeleteMenuResult(DeleteMenuOutcome.HasChildren);
        }
        await cache.InvalidateAsync(CancellationToken.None).ConfigureAwait(false);

        return new DeleteMenuResult(DeleteMenuOutcome.Success);
    }

    public async Task<AssignMenuRolesResult> AssignMenuRolesAsync(
        Guid menuId,
        IReadOnlyList<Guid> roleIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roleIds);

        var menuExists = await db.Menus
            .AnyAsync(m => m.Id == menuId, ct)
            .ConfigureAwait(false);

        if (!menuExists)
        {
            return new AssignMenuRolesResult(AssignMenuRolesOutcome.MenuNotFound);
        }

        // Second-layer Distinct: the validator rejects duplicate roleIds from HTTP
        // requests, but a non-HTTP caller could pass dups and trip the (MenuId, RoleId)
        // PK on insert. Mirrors UserService.AssignRolesAsync defense-in-depth.
        var distinctRoleIds = roleIds.Distinct().ToList();

        if (distinctRoleIds.Count > 0)
        {
            var foundRoleIds = await db.Roles
                .Where(r => distinctRoleIds.Contains(r.Id))
                .Select(r => r.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (foundRoleIds.Count != distinctRoleIds.Count)
            {
                var invalidIds = distinctRoleIds.Except(foundRoleIds).ToList();
                return new AssignMenuRolesResult(AssignMenuRolesOutcome.RolesNotFound, invalidIds);
            }
        }

        // Delta sync, not blanket replace: only remove rows whose role is no
        // longer in the new set, and only add rows whose role isn't already
        // present. Preserves CreatedAt on unchanged rows so it remains a faithful
        // "originally assigned at" audit timestamp. (Story 4.4 bmad review P7.)
        // No SERIALIZABLE wrapper — there is no last-admin-style invariant; the
        // 23503/23505 catch below translates race-window failures into 409.
        var existing = await db.MenuRoleAssignments
            .Where(x => x.MenuId == menuId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var distinctSet = new HashSet<Guid>(distinctRoleIds);
        var existingRoleIds = new HashSet<Guid>(existing.Select(e => e.RoleId));

        var toRemove = existing.Where(e => !distinctSet.Contains(e.RoleId)).ToList();
        if (toRemove.Count > 0)
        {
            db.MenuRoleAssignments.RemoveRange(toRemove);
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var roleId in distinctRoleIds)
        {
            if (existingRoleIds.Contains(roleId)) continue;
            db.MenuRoleAssignments.Add(new MenuRoleAssignment
            {
                MenuId = menuId,
                RoleId = roleId,
                CreatedAt = now,
            });
        }

        // Translate race-window FK / PK violations into a clean 409. 23503 = FK
        // violation (menu or role deleted between our check and SaveChanges).
        // 23505 = (MenuId, RoleId) PK collision from a concurrent PUT to the
        // same menu. Mirrors UserService.AssignRolesAsync defense-in-depth.
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is Npgsql.PostgresException { SqlState: "23503" or "23505" })
        {
            return new AssignMenuRolesResult(AssignMenuRolesOutcome.Conflict);
        }
        catch (Npgsql.PostgresException pg) when (pg.SqlState is "23503" or "23505")
        {
            // Npgsql sometimes surfaces commit-time constraint failures bare rather
            // than wrapping them in DbUpdateException. Mirror the wrapping catch.
            return new AssignMenuRolesResult(AssignMenuRolesOutcome.Conflict);
        }
        await cache.InvalidateAsync(CancellationToken.None).ConfigureAwait(false);

        return new AssignMenuRolesResult(AssignMenuRolesOutcome.Success);
    }

    public async Task<ReorderMenusResult> ReorderMenusAsync(
        IReadOnlyList<ReorderMenuItem> items,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);

        // Fast-path: no items → nothing to mutate, nothing to invalidate. The
        // validator allows [] for callers that want a stable contract on empty.
        if (items.Count == 0)
        {
            return new ReorderMenusResult(ReorderMenusOutcome.Success);
        }

        // Defensive Distinct via last-write-wins: the validator rejects duplicate
        // ids on the HTTP path, but a non-HTTP caller could pass dupes. Collapse
        // here so the dictionary build below cannot throw.
        var idToOrder = items
            .GroupBy(i => i.Id)
            .ToDictionary(g => g.Key, g => g.Last().Order);

        var idsToFetch = idToOrder.Keys.ToList();
        var menus = await db.Menus
            .Where(m => idsToFetch.Contains(m.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (menus.Count != idToOrder.Count)
        {
            var found = menus.Select(m => m.Id).ToHashSet();
            var invalid = idToOrder.Keys.Where(id => !found.Contains(id)).ToList();
            return new ReorderMenusResult(ReorderMenusOutcome.MenusNotFound, invalid);
        }

        // Scope invariant — every fetched menu must share the same ParentId so a
        // batch cannot accidentally promote a sub-menu to top-level (or vice
        // versa). Distinct() treats null (top-level) as a distinct scope from
        // any Guid, which is exactly the semantic we want.
        var parentScopes = menus.Select(m => m.ParentId).Distinct().ToList();
        if (parentScopes.Count > 1)
        {
            return new ReorderMenusResult(ReorderMenusOutcome.MixedScopes);
        }

        // Apply orders + UpdatedAt only on rows that actually changed. Skipping
        // no-op rows keeps UpdatedAt faithful as an "order last changed at"
        // timestamp.
        var now = DateTimeOffset.UtcNow;
        foreach (var menu in menus)
        {
            var newOrder = idToOrder[menu.Id];
            if (menu.Order == newOrder) continue;
            menu.Order = newOrder;
            menu.UpdatedAt = now;
        }

        // 23503 = FK violation (a menu was deleted between fetch and save).
        // 23505 = unique violation (no current unique index on sort_order, but
        // catch defensively in case a future migration adds one). Mirrors
        // AssignMenuRolesAsync defense-in-depth.
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is Npgsql.PostgresException { SqlState: "23503" or "23505" })
        {
            return new ReorderMenusResult(ReorderMenusOutcome.Conflict);
        }
        catch (Npgsql.PostgresException pg) when (pg.SqlState is "23503" or "23505")
        {
            // Npgsql sometimes surfaces commit-time constraint failures bare.
            return new ReorderMenusResult(ReorderMenusOutcome.Conflict);
        }

        await cache.InvalidateAsync(CancellationToken.None).ConfigureAwait(false);

        return new ReorderMenusResult(ReorderMenusOutcome.Success);
    }

    public async Task<ToggleMenuActiveResult> ToggleMenuActiveAsync(Guid id, bool isActive, CancellationToken ct)
    {
        var menu = await db.Menus
            .FirstOrDefaultAsync(m => m.Id == id, ct)
            .ConfigureAwait(false);

        if (menu is null)
        {
            return new ToggleMenuActiveResult(ToggleMenuActiveOutcome.NotFound);
        }

        menu.IsActive = isActive;
        menu.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await cache.InvalidateAsync(CancellationToken.None).ConfigureAwait(false);

        return new ToggleMenuActiveResult(ToggleMenuActiveOutcome.Success);
    }

    // Story 5.2 — bind a Published Designer version to a menu item. Synchronous
    // path writes (DesignerId, BoundVersion, ProvisioningStatus="Pending") and
    // returns Success; the actual table provisioning then runs asynchronously
    // in ProvisioningBackgroundService. Re-binds (v1 → v2) reuse this same path —
    // Story 5.4 will diff the resulting schemas in the BackgroundService body.
    public async Task<BindMenuResult> BindDesignerAsync(
        Guid menuId, string designerId, int version, Guid? actorId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(designerId);

        var menu = await db.Menus.FirstOrDefaultAsync(m => m.Id == menuId, ct).ConfigureAwait(false);
        if (menu is null) return new BindMenuResult(BindMenuOutcome.MenuNotFound);

        // Verify the (designerId, version) tuple resolves to a Published version.
        // AsNoTracking — we only read Status, never mutate the schema row from here.
        var schemaVersion = await db.ComponentSchemaVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.DesignerId == designerId && v.Version == version, ct)
            .ConfigureAwait(false);

        if (schemaVersion is null) return new BindMenuResult(BindMenuOutcome.DesignerNotFound);
        if (schemaVersion.Status != "Published") return new BindMenuResult(BindMenuOutcome.VersionNotPublished);

        // FR-54 AC-3: VIEW-mode components are not provisioned — skip DDL and mark
        // NotApplicable. Checked BEFORE cycle detection and pgType validation (both
        // are DDL-prep concerns irrelevant for VIEW; skipping them avoids unnecessary
        // DB round-trips). designerMode would be null only on a data-integrity break
        // (ComponentSchema row missing despite a matching version) — in that case we
        // fall through to the CRUD provisioning path, which fails gracefully.
        var designerMode = await db.ComponentSchemas
            .AsNoTracking()
            .Where(s => s.DesignerId == designerId)
            .Select(s => s.Mode)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (designerMode == "VIEW")
        {
            menu.DesignerId = designerId;
            menu.BoundVersion = version;
            menu.ProvisioningStatus = "NotApplicable";
            menu.ProvisioningError = null;
            menu.RoutePath = null;
            menu.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await cache.InvalidateAsync(CancellationToken.None).ConfigureAwait(false);
            eventBus.Publish(new MenuBindingCreated(designerId));
            // Do NOT call provisioning.EnqueueAsync — VIEW bindings never provision a table.
            return new BindMenuResult(BindMenuOutcome.Success);
        }

        // Story 5.5 — pre-bind cycle check on the Repeater reference graph. Runs
        // BEFORE any write so a 422 REPEATER_CYCLE doesn't leave the menu Pending
        // forever (no DDL emitted, no provisioning job enqueued — AC-4).
        if (await cycleDetector.HasCycleAsync(designerId, version, ct).ConfigureAwait(false))
            return new BindMenuResult(BindMenuOutcome.RepeaterCycle);

        // Story B — reject a bind whose RootElement contains a malformed authored
        // pgType BEFORE any write. RootElementParser would otherwise silently fall
        // back to the component default at provision time, dropping the author's
        // intended column type without feedback. Runs after the cycle check so the
        // menu never goes Pending on an invalid type (no DDL emitted — symmetric
        // with the REPEATER_CYCLE guard).
        var invalidPgType = DesignerPgTypeValidator.FindFirstInvalid(schemaVersion.RootElement);
        if (invalidPgType is not null)
            return new BindMenuResult(
                BindMenuOutcome.InvalidPgType,
                $"Field '{invalidPgType.FieldKey}' has an invalid type '{invalidPgType.PgType}': {invalidPgType.Message}");

        // Story 5.4 AC-4: capture the previous BoundVersion BEFORE the overwrite so
        // the enqueued job carries the real (from → to) transition. null on a
        // first-time bind → audit log writes FROM_VERSION = NULL (CREATE path).
        var fromVersion = menu.BoundVersion;

        menu.DesignerId = designerId;
        menu.BoundVersion = version;
        menu.ProvisioningStatus = "Pending";
        menu.ProvisioningError = null;
        // Binding a Designer and a custom route path are mutually exclusive — clear
        // any previously-set route so a bound menu always routes to /data/{designerId}.
        menu.RoutePath = null;
        menu.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await cache.InvalidateAsync(CancellationToken.None).ConfigureAwait(false);

        // Publish AFTER commit — no subscriber needs eviction today (architecture
        // line 377: "MenuBindingCreated(designerId) — no eviction"), but the event
        // is declared per AR-47 so future subscribers (Story 5.3 schema audit etc.)
        // have a defined seam without touching MenuService again.
        eventBus.Publish(new MenuBindingCreated(designerId));

        // Enqueue AFTER commit — the BackgroundService refetches the row, so it
        // would otherwise observe an uncommitted state. CancellationToken.None on
        // the enqueue: a cancellation between commit and enqueue would otherwise
        // leave the row Pending with no consumer, requiring a Retry to recover.
        await provisioning
            .EnqueueAsync(
                new ProvisioningJob(menu.Id, designerId, version, actorId, FromVersion: fromVersion),
                CancellationToken.None)
            .ConfigureAwait(false);

        return new BindMenuResult(BindMenuOutcome.Success);
    }

    // Story 5.2 — re-enqueue provisioning for an existing binding. Does NOT
    // change DesignerId/BoundVersion; only resets ProvisioningStatus → Pending
    // and clears ProvisioningError, so a Retry is a pure "try again with the
    // same inputs" semantic.
    public async Task<RetryBindingResult> RetryBindingAsync(Guid menuId, Guid? actorId, CancellationToken ct)
    {
        var menu = await db.Menus.FirstOrDefaultAsync(m => m.Id == menuId, ct).ConfigureAwait(false);
        if (menu is null) return new RetryBindingResult(RetryBindingOutcome.MenuNotFound);
        if (menu.DesignerId is null || menu.BoundVersion is null)
        {
            return new RetryBindingResult(RetryBindingOutcome.NoBinding);
        }

        // FR-54: VIEW-mode bindings are intentionally NotApplicable — no table was
        // ever provisioned, so there is nothing to retry. Treat the same as NoBinding
        // (422 MENU_NO_BINDING) so the caller sees a clear rejection rather than a
        // provisioning job that would fail or produce a confusing Pending→Error cycle.
        if (menu.ProvisioningStatus == "NotApplicable")
        {
            return new RetryBindingResult(RetryBindingOutcome.NoBinding);
        }

        menu.ProvisioningStatus = "Pending";
        menu.ProvisioningError = null;
        menu.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        // Skip cache.InvalidateAsync — the menu cache stores only the navbar tree,
        // which has no binding fields. A retry doesn't touch IsActive/Name/Order,
        // so there's no navbar-visible change to bust.
        await provisioning.EnqueueAsync(
            new ProvisioningJob(menu.Id, menu.DesignerId, menu.BoundVersion.Value, actorId),
            CancellationToken.None).ConfigureAwait(false);

        return new RetryBindingResult(RetryBindingOutcome.Success);
    }

    // Set (or clear) a menu's custom route path. Mutually exclusive with the Designer
    // binding: a non-null/non-blank path clears DesignerId + the provisioning fields
    // (the menu unlinks from the designer but the provisioned table is left intact —
    // dropping tables is out of scope here). A null/blank path just clears RoutePath.
    // Unlike Retry, this MUST bust the menu cache: RoutePath is part of the navbar tree
    // (NavMenuItem.RoutePath), so a stale cache would keep routing the old way.
    public async Task<SetRoutePathResult> SetRoutePathAsync(Guid menuId, string? routePath, CancellationToken ct)
    {
        var menu = await db.Menus.FirstOrDefaultAsync(m => m.Id == menuId, ct).ConfigureAwait(false);
        if (menu is null) return new SetRoutePathResult(SetRoutePathOutcome.MenuNotFound);

        var trimmed = string.IsNullOrWhiteSpace(routePath) ? null : routePath.Trim();

        menu.RoutePath = trimmed;
        if (trimmed is not null)
        {
            // Clear any Designer binding so exactly one target is live at a time.
            menu.DesignerId = null;
            menu.BoundVersion = null;
            menu.ProvisioningStatus = null;
            menu.ProvisioningError = null;
        }
        menu.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await cache.InvalidateAsync(CancellationToken.None).ConfigureAwait(false);

        return new SetRoutePathResult(SetRoutePathOutcome.Success);
    }

    // Story 5.2 — read-only column diff preview before applying a re-bind.
    // Returns null when the menu doesn't exist OR has no current binding —
    // both surfaced as 404 by the handler (no current state to diff against).
    public async Task<BindingDiffResponse?> GetBindingDiffAsync(
        Guid menuId, int targetVersion, CancellationToken ct)
    {
        var menu = await db.Menus.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == menuId, ct).ConfigureAwait(false);
        if (menu is null || menu.DesignerId is null || menu.BoundVersion is null) return null;

        return await diffService
            .ComputeAsync(menu.DesignerId, menu.BoundVersion.Value, targetVersion, ct)
            .ConfigureAwait(false);
    }

    // Story 4.7 — permission + isActive filtered nested tree for the navbar.
    // 5 s in-memory cache per user; total bust on every menu mutation via IMenuCache.
    public async Task<IReadOnlyList<NavMenuItem>> GetNavMenusForUserAsync(Guid userId, CancellationToken ct)
    {
        var cached = await cache.TryGetAsync(userId, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        // EffectivePermissions.RoleIds is the canonical 30 s-cached role set; do NOT
        // query db.UserRoles directly — that bypasses the permission cache and
        // duplicates seed-data role logic. (PermissionService.cs:138-142.)
        var effective = await permissions.GetEffectivePermissionsAsync(userId, ct).ConfigureAwait(false);
        var roleIds = effective.RoleIds;
        var isPlatformAdmin = roleIds.Contains(WellKnownRoles.PlatformAdminId);

        // Why filter in-memory: v1 menu scale is small (single-digit top-level, low
        // double-digit per parent per PRD). A LINQ-to-Entities .Where(...Any(...)) on
        // RoleAssignments translates to a correlated subquery per row; the
        // Include + LINQ-to-Objects path is faster and easier to reason about at this
        // scale. Re-evaluate if menu count grows beyond ~100.
        var menus = await db.Menus
            .Include(m => m.RoleAssignments)
            .Where(m => m.IsActive)
            .OrderBy(m => m.Order)
            .ThenBy(m => m.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Pass 1a — direct visibility per the role-intersection rule.
        var directlyVisible = new HashSet<Guid>();
        foreach (var menu in menus)
        {
            if (isPlatformAdmin
                || menu.RoleAssignments.Any(ra => roleIds.Contains(ra.RoleId)))
            {
                directlyVisible.Add(menu.Id);
            }
        }

        // Pass 1b — promote ancestors of any directly-visible sub-menu so the
        // tree can actually render it. Without this, assigning a role to a
        // sub-menu without ALSO assigning it to its top-level parent silently
        // drops the sub-menu from the nav (Pass 2/3 can't attach a child whose
        // parent isn't in the visible set). This overrides Story 4.7 AC-1's
        // "drop, do not promote" rule because admins consistently expected
        // role-on-child to be sufficient. IsActive-driven invisibility is NOT
        // promoted across: the parent is filtered out of `menus` at the DB
        // level, so menuById.TryGetValue returns false and the walk halts.
        var menuById = menus.ToDictionary(m => m.Id);
        var effectivelyVisible = new HashSet<Guid>(directlyVisible);
        foreach (var startId in directlyVisible)
        {
            var current = menuById[startId];
            while (current.ParentId.HasValue
                && menuById.TryGetValue(current.ParentId.Value, out var parent))
            {
                // Set.Add returns false when the id was already present, which
                // means we've already walked above this ancestor — short-circuit.
                if (!effectivelyVisible.Add(parent.Id)) break;
                current = parent;
            }
        }

        // Build visibleOrdered preserving (Order, Id) sequence from `menus`.
        var visibleOrdered = new List<Menu>(effectivelyVisible.Count);
        foreach (var menu in menus)
        {
            if (effectivelyVisible.Contains(menu.Id))
            {
                visibleOrdered.Add(menu);
            }
        }

        // Pass 2 — pre-allocate a mutable child list for every visible menu so the
        // NavMenuItem we construct below can capture it by reference. The list is
        // populated in Pass 3 in (Order, Id) sequence (visibleOrdered's natural order).
        var childListsByParent = new Dictionary<Guid, List<NavMenuItem>>(visibleOrdered.Count);
        foreach (var menu in visibleOrdered)
        {
            childListsByParent[menu.Id] = [];
        }

        // Pass 3 — build NavMenuItem nodes and append to the right parent list.
        // Top-level (ParentId == null) become roots; sub-menus attach as Children of
        // their parent only if the parent is also effectively visible (after the
        // Pass 1b promotion). The remaining "drop" branch only fires when the
        // parent is inactive (IsActive=false drops it before Pass 1), so an
        // active sub-menu under an inactive parent is still hidden — Story 4.7
        // AC-1's IsActive semantics survive, but its role-only "drop" rule was
        // softened to match admin expectations.
        var roots = new List<NavMenuItem>();
        foreach (var menu in visibleOrdered)
        {
            var node = new NavMenuItem(
                menu.Id,
                menu.Name,
                menu.Order,
                ParseIcon(menu.Icon),
                menu.ParentId,
                menu.DesignerId,
                menu.RoutePath,
                Children: childListsByParent[menu.Id]);

            if (menu.ParentId is null)
            {
                roots.Add(node);
            }
            else if (childListsByParent.TryGetValue(menu.ParentId.Value, out var siblingList))
            {
                siblingList.Add(node);
            }
            // else: parent is hidden — drop this sub-menu (do not promote).
        }

        await cache.SetAsync(userId, roots, CancellationToken.None).ConfigureAwait(false);
        return roots;
    }

    private static MenuResponse ToResponse(Menu menu) =>
        new(
            menu.Id,
            menu.Name,
            menu.Order,
            ParseIcon(menu.Icon),
            menu.IsActive,
            menu.ParentId,
            menu.RoleAssignments.Select(r => r.RoleId).ToList(),
            menu.CreatedAt,
            menu.UpdatedAt,
            // Story 5.2 — binding fields, all nullable; null for unbound section headers.
            menu.DesignerId,
            menu.BoundVersion,
            menu.ProvisioningStatus,
            menu.ProvisioningError,
            menu.RoutePath);

    // Menu.Icon is stored as a JSON string (TEXT column). MenuResponse.Icon is JsonElement? so the
    // serialized response is a JSON object — not an escaped string — matching the frontend MenuIcon type.
    // P2: JsonException on a corrupt DB row returns null instead of propagating a 500.
    private static System.Text.Json.JsonElement? ParseIcon(string? iconJson)
    {
        if (iconJson is null) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(iconJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
