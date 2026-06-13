# Story 4.4: Assign Roles to Menu Item

Status: done

## Story

As a Platform Admin,
I want to configure which Roles can access a Menu Item,
so that only authorized users see and interact with that data module.

## Acceptance Criteria

**AC-1 — Role assignment persists**
Given a Menu Item being edited
When I PUT `/api/admin/menus/{id}/roles` with `{ "roleIds": [roleId, ...] }`
Then the `menu_role_assignments` rows are replaced (full sync) and the change persists
And `GET /api/admin/menus/{id}` immediately returns `allowedRoleIds` containing the assigned IDs

**AC-2 — Navbar visibility gate (contract)**
Given a Menu Item with `allowedRoles`
When a user's navbar is rendered (Story 4.7)
Then the user sees the item only if at least one of their Roles is in `allowedRoles`

**AC-3 — Invalid role rejected**
Given a PUT request containing a `roleId` that does not exist in the `roles` table
When the server processes it
Then the response is HTTP 422 with `code: "ROLES_NOT_FOUND"`

**AC-4 — Empty list clears all assignments**
Given a Menu Item with existing role assignments
When I PUT `/api/admin/menus/{id}/roles` with `{ "roleIds": [] }`
Then all `menu_role_assignments` rows for that menu are deleted

**AC-5 — Menu not found**
Given a `menuId` that does not exist
When I PUT `/api/admin/menus/{menuId}/roles`
Then the response is HTTP 404 with `code: "MENU_NOT_FOUND"`

## Tasks / Subtasks

- [x] Task 1: Backend — `AssignMenuRolesRequest` DTO (AC-1, AC-3)
  - [x] Create `src/FormForge.Api/Features/Menus/Dtos/AssignMenuRolesRequest.cs`
    - `internal sealed record AssignMenuRolesRequest(IReadOnlyList<Guid>? RoleIds);`

- [x] Task 2: Backend — `AssignMenuRolesRequestValidator` (AC-1, AC-3)
  - [x] Create `src/FormForge.Api/Features/Menus/Validators/AssignMenuRolesRequestValidator.cs`
  - [x] Mirror `AssignRolesRequestValidator` rules: NotNull, no duplicates, no Guid.Empty, max 256 entries
  - [x] Register in `Program.cs`: `builder.Services.AddScoped<IValidator<AssignMenuRolesRequest>, AssignMenuRolesRequestValidator>();`
    - Insert near the existing `CreateMenuRequest`/`UpdateMenuRequest` validator registrations (lines 133–134)

- [x] Task 3: Backend — endpoint in `MenuAdminEndpoints.cs` (AC-1, AC-3, AC-4, AC-5)
  - [x] Add `PUT /{id:guid}/roles` route inside `MapMenuAdminEndpoints()` after the existing `MapDelete` call:
    ```csharp
    group.MapPut("/{id:guid}/roles", AssignRolesHandler)
         .AddValidationFilter<AssignMenuRolesRequest>()
         .WithSummary("Replace the allowed-roles set for a menu item")
         .Produces(StatusCodes.Status204NoContent)
         .Produces(StatusCodes.Status404NotFound)
         .Produces(StatusCodes.Status422UnprocessableEntity);
    ```
  - [x] Implement `AssignRolesHandler` private static method
    - Calls `menuService.AssignMenuRolesAsync(id, request.RoleIds!, ct)`
    - Maps `AssignMenuRolesOutcome.Success` → 204 No Content
    - Maps `AssignMenuRolesOutcome.MenuNotFound` → `MenuNotFoundProblem()`
    - Maps `AssignMenuRolesOutcome.RolesNotFound` → 422 with `code: "ROLES_NOT_FOUND"`

- [x] Task 4: Backend — `AssignMenuRolesAsync` service method (AC-1, AC-3, AC-4, AC-5)
  - [x] Add enum to `MenuService.cs`:
    ```csharp
    internal enum AssignMenuRolesOutcome { Success, MenuNotFound, RolesNotFound }
    internal sealed record AssignMenuRolesResult(AssignMenuRolesOutcome Outcome, IReadOnlyList<Guid>? InvalidIds = null);
    ```
  - [x] Add `Task<AssignMenuRolesResult> AssignMenuRolesAsync(Guid menuId, IReadOnlyList<Guid> roleIds, CancellationToken ct);` to `IMenuService`
  - [x] Implement in `MenuService`:
    1. Check menu exists (`db.Menus.AnyAsync`) — return `MenuNotFound` if not
    2. If `roleIds.Count > 0`: validate all exist in `db.Roles`; collect invalid IDs; return `RolesNotFound` with invalid list if any
    3. `db.MenuRoleAssignments.RemoveRange(db.MenuRoleAssignments.Where(x => x.MenuId == menuId))`
    4. `db.MenuRoleAssignments.AddRange(distinctRoleIds.Select(rid => new MenuRoleAssignment { MenuId = menuId, RoleId = rid, CreatedAt = DateTimeOffset.UtcNow }))`
    5. `await db.SaveChangesAsync(ct)` (EF batches delete+insert atomically)
    6. `await cache.InvalidateAsync(ct)`
    7. Return `Success`
  - [x] Deduplicate roleIds: `var distinctRoleIds = roleIds.Distinct().ToList();` (validator rejects dups from HTTP, but guard for non-HTTP callers)

- [x] Task 5: Backend — integration tests (AC-1, AC-3, AC-4, AC-5)
  - [x] Add to `src/FormForge.Api.Tests/Features/Menus/MenuIntegrationTests.cs`:
    - `AssignMenuRoles_Unauthenticated_Returns401`
    - `AssignMenuRoles_AsNonAdmin_Returns403`
    - `AssignMenuRoles_ValidRoleIds_Returns204AndRoundTrips`
      - Create menu; PUT roles with `PlatformAdminRoleId`; GET menu; assert `AllowedRoleIds` contains that ID
    - `AssignMenuRoles_EmptyList_ClearsAssignments`
      - Create menu; PUT roles with `[PlatformAdminRoleId]`; PUT roles with `[]`; GET menu; assert `AllowedRoleIds` is empty
    - `AssignMenuRoles_MenuNotFound_Returns404`
    - `AssignMenuRoles_InvalidRoleId_Returns422WithRolesNotFound`
  - [x] All tests follow the existing pattern (`LoginAsync` → `HttpRequestMessage` with Bearer → assert)
  - [x] Test Count Baseline: 267 before this story; expect 273 after (+6) — actual 273/273

- [x] Task 6: Frontend — `useAssignMenuRolesMutation` (AC-1)
  - [x] Add to `web/src/features/admin/menus/menuAdminMutations.ts`:
    ```ts
    export function useAssignMenuRolesMutation(menuId: string) {
      const queryClient = useQueryClient()
      const { t } = useTranslation()
      return useMutation({
        mutationFn: (body: { roleIds: string[] }) =>
          httpClient.put<void>(`/api/admin/menus/${menuId}/roles`, body),
        onSuccess: () => {
          invalidateAllMenus(queryClient)
          void queryClient.invalidateQueries({ queryKey: [...MENU_DETAIL_QUERY_KEY, menuId] })
          toast.success(t('admin.menus.rolesAssignSuccess'))
        },
      })
    }
    ```

- [x] Task 7: Frontend — `RoleAssignmentSection` in `menus.$menuId.tsx` (AC-1)
  - [x] Extend `MenuDetailContentProps.menu` to include `allowedRoleIds: string[]`
    - `useMenuDetailQuery` already returns `MenuItem` which has `allowedRoleIds: string[]`; the prop type just needs to be widened
  - [x] In `MenuDetailContent`, after the existing form and icon picker:
    ```tsx
    <RoleAssignmentSection
      key={menu.allowedRoleIds.join(',')}
      currentRoleIds={menu.allowedRoleIds}
      menuId={menuId}
    />
    ```
    - Use `key` trick to remount on role-membership change (consistent with `users.$userId.tsx` pattern)
  - [x] Implement `RoleAssignmentSection` in the same file (below `IconPickerSection`, per project convention of co-located sub-components):
    ```tsx
    interface RoleAssignmentSectionProps {
      currentRoleIds: string[]
      menuId: string
    }
    ```
    - Import `useRolesQuery, type RoleListItem` from `web/src/features/admin/roles/useRolesQuery`
    - Local state: `const [selected, setSelected] = useState<Set<string>>(() => new Set(currentRoleIds))`
    - The `key` prop on the parent call ensures `useState` initializer re-runs when assignment changes
    - Toggle handler: add/remove from the Set
    - Save button: calls `assignMutation.mutateAsync({ roleIds: Array.from(selected) })`
    - Error handling: 404 → `admin.menus.notFoundError`; generic → `admin.menus.rolesAssignError`
    - Show role list as checkboxes; show loading spinner while `rolesQuery.isLoading`

- [x] Task 8: Frontend — i18n keys (AC-1)
  - [x] Add to `admin.menus` block in `web/src/lib/i18n/locales/en.json`:
    - `"rolesSectionTitle": "Allowed Roles"`
    - `"rolesHelp": "Users must have at least one of these roles to see this menu item. An empty list hides it from all users."`
    - `"saveRolesButton": "Save Roles"`
    - `"savingRolesButton": "Saving…"`
    - `"rolesAssignSuccess": "Roles updated"`
    - `"rolesAssignError": "Failed to update roles"`

### Review Findings

_Code review run: 2026-05-24 — Blind Hunter + Edge Case Hunter + Acceptance Auditor (3 parallel layers, ~40 raw findings → 15 after triage)._

**Decision-Needed** — 0 outstanding (3 resolved → promoted to patches P7/P8/P9 below).

**Patch** (9) — unambiguous fixes:

- [x] [Review][Patch] `AssignMenuRolesAsync` lacks DbUpdateException catch — concurrent PUT/delete races bubble to 500 [src/FormForge.Api/Features/Menus/MenuService.cs:174-235]. Mirror `UserService.AssignRolesAsync` (lines 151-173): catch 23503/23505/40001 → return new `AssignMenuRolesOutcome.Conflict` → 409. SERIALIZABLE wrapper is optional (no last-admin invariant), but the exception catch is mandatory.
- [x] [Review][Patch] `RoleAssignmentSection` lacks `availableRoles.length === 0` empty-state [web/src/routes/_app/admin/menus.$menuId.tsx:604-620]. Mirror `users.$userId.tsx:317` — render `t('admin.menus.rolesEmpty')` when empty; add the i18n key.
- [x] [Review][Patch] Save button enables on `rolesQuery` error/refetch — silent destructive clear [web/src/routes/_app/admin/menus.$menuId.tsx:632]. Gate `disabled` on `rolesQuery.isError`, `rolesQuery.isFetching`, and `availableRoles.length === 0` in addition to the current `isSaving || isLoading`.
- [x] [Review][Patch] `admin.menus.rolesNotFound` messageKey emitted by backend but missing from `en.json` [src/FormForge.Api/Features/Menus/MenuAdminEndpoints.cs:189]. Add the key under `admin.menus` in `web/src/lib/i18n/locales/en.json` for contract consistency.
- [x] [Review][Patch] `AssignMenuRoles_InvalidRoleId_Returns422WithRolesNotFound` asserts substring, not field [src/FormForge.Api.Tests/Features/Menus/MenuIntegrationTests.cs:572-591]. Parse body as `JsonDocument` and assert the bogus Guid appears in the `invalidIds` array specifically — substring match passes even if the Guid leaks into title/detail.
- [x] [Review][Patch] Validator `Distinct().Count()` runs before MaxRoleIds cap [src/FormForge.Api/Features/Menus/Validators/AssignMenuRolesRequestValidator.cs:20-30]. A 1M-entry payload allocates a 1M HashSet before the size rule fires. Move MaxRoleIds before Distinct, or guard Distinct with `.When(ids => ids != null && ids.Count <= MaxRoleIds)`. (Same shape exists in `AssignRolesRequestValidator` — deferred separately.)
- [x] [Review][Patch] P7 (from D1): `AssignMenuRolesAsync` preserves `CreatedAt` for unchanged rows [src/FormForge.Api/Features/Menus/MenuService.cs:210-229]. Replace the blanket `RemoveRange(existing) + AddRange(distinctRoleIds)` with a delta: `existing.Where(e => !distinctRoleIds.Contains(e.RoleId))` → RemoveRange; `distinctRoleIds.Where(rid => existing.All(e => e.RoleId != rid))` → AddRange (only the truly-new). Unchanged rows retain their original `CreatedAt`. Add `AssignMenuRoles_ReassignSameRole_PreservesCreatedAt` integration test.
- [x] [Review][Patch] P8 (from D2): Empty-list save requires inline warning + double-click within 3s [web/src/routes/_app/admin/menus.$menuId.tsx:625-636]. When `selected.size === 0`, render a yellow warning band ("This will hide the menu from all users") and require two Save clicks within 3 seconds. Use a `pendingConfirmRef` (timestamp) to track the first click; clear on timeout or successful second click. Add `admin.menus.rolesEmptyWarning` i18n key.
- [x] [Review][Patch] P9 (from D3): Merge hidden-assigned roles + caption [web/src/routes/_app/admin/menus.$menuId.tsx:589-619] + [web/src/features/admin/roles/useRolesQuery.ts]. (1) In `RoleAssignmentSection`, compute `hiddenAssignedIds = currentRoleIds.filter(id => !availableRoles.some(r => r.id === id))`. For each hidden ID, render a pre-checked row labeled by role name (fetch via a new `useRoleByIdsQuery` batch hook, OR fall back to `t('admin.menus.unknownRole', { id })`). User can uncheck to remove; can never silently drop. (2) When `availableRoles.length === 100`, render a caption `{t('admin.menus.rolesCatalogCapped')}` ("Showing first 100 roles. Roles beyond this set cannot be added here — manage them in the Roles admin first."). Add 2 i18n keys.

**Deferred** (6) — pre-existing, cross-cutting, or future-story scope:

- [x] [Review][Defer] Cross-user concurrent edit detection [web/src/routes/_app/admin/menus.$menuId.tsx:550-639] — deferred, pre-existing tradeoff (`key={…}` remount discards in-progress local edits when another admin saves; the opposite Story 4.3 P6 bug was the other direction). A "stale state, refresh?" prompt is the right fix but out of scope.
- [x] [Review][Defer] `ApiError` drops `invalidIds` extension [web/src/lib/api/apiError.ts + web/src/features/auth/httpClient.ts:113-123] — deferred, pre-existing. Cross-cutting: the same drop affects the Users assign-roles 422 response. Needs a single widening of `ApiError` to carry extensions.
- [x] [Review][Defer] EF `Contains` translates to flat IN list [src/FormForge.Api/Features/Menus/MenuService.cs:198] — deferred, pre-existing. Cross-cutting EF pattern across the codebase; 256-cap mitigates here.
- [x] [Review][Defer] Cache invalidation outside DB transaction [src/FormForge.Api/Features/Menus/MenuService.cs:231-232] — deferred, pre-existing pattern. `IMenuCache` is `NoOpMenuCache` today; forward-looking for Story 4.7's 5 s TTL cache.
- [x] [Review][Defer] Checkbox `<input>` has no `id`/`htmlFor` linkage [web/src/routes/_app/admin/menus.$menuId.tsx:609-617] — deferred, pre-existing. Cross-cuts with `users.$userId.tsx:322-326`; fix both together in Story 7.4 (Accessibility).
- [x] [Review][Defer] Sub-menu role-gate semantics undefined [web/src/routes/_app/admin/menus.$menuId.tsx:213-222] — deferred, forward-looking. Story 4.7 navbar gate must specify behavior for sub-menus whose parent has different role assignments.

## Dev Notes

### No DB Migration Required

`menu_role_assignments` table already exists, created in the Story 4.1 migration
(`20260524054931_CreateMenusAndMenuRoleAssignments.cs`). Schema:
- Composite PK: `(menu_id, role_id)`
- FK to `menus(id)` ON DELETE CASCADE
- FK to `roles(id)` ON DELETE CASCADE
- Index on `role_id`

`Menu.RoleAssignments` navigation property is already configured in `FormForgeDbContext.OnModelCreating` and loaded by `GetMenuAsync` via `.Include(m => m.RoleAssignments)`. `ToResponse()` already projects to `AllowedRoleIds`. This feature is entirely "un-wiring" existing scaffolding.

### Backend: File Locations and Naming

Follow existing story naming conventions:
- DTO: `src/FormForge.Api/Features/Menus/Dtos/AssignMenuRolesRequest.cs`
- Validator: `src/FormForge.Api/Features/Menus/Validators/AssignMenuRolesRequestValidator.cs`
- Existing validators dir: `src/FormForge.Api/Features/Menus/Validators/` (contains `CreateMenuRequestValidator.cs`, `UpdateMenuRequestValidator.cs`)
- Existing user analog: `src/FormForge.Api/Features/Users/Dtos/AssignRolesRequest.cs` + `Validators/AssignRolesRequestValidator.cs`

### Backend: Validator Rules (exact match with `AssignRolesRequestValidator`)

```csharp
RuleFor(x => x.RoleIds).NotNull()
    .WithMessage("roleIds must be provided (use [] for empty).");
RuleFor(x => x.RoleIds)
    .Must(ids => ids == null || ids.Distinct().Count() == ids.Count)
    .WithMessage("Duplicate roleId values are not allowed.");
RuleFor(x => x.RoleIds)
    .Must(ids => ids == null || ids.All(id => id != Guid.Empty))
    .WithMessage("roleIds entries must not be Guid.Empty.");
RuleFor(x => x.RoleIds)
    .Must(ids => ids == null || ids.Count <= MaxRoleIds)  // MaxRoleIds = 256
    .WithMessage("roleIds cannot contain more than 256 entries.");
```

The `AddValidationFilter<AssignMenuRolesRequest>()` fluent call wires the validator through the existing `EndpointFilters` pipeline (same as all other endpoints in this app).

### Backend: Service Implementation Pattern

Model after `UserService.AssignRolesAsync` but without the serializable-transaction + last-admin-lockout guard (no equivalent constraint for menu items — any role combination is valid).

```csharp
internal enum AssignMenuRolesOutcome { Success, MenuNotFound, RolesNotFound }
internal sealed record AssignMenuRolesResult(
    AssignMenuRolesOutcome Outcome,
    IReadOnlyList<Guid>? InvalidIds = null);
```

Implementation sketch for `AssignMenuRolesAsync`:

```csharp
public async Task<AssignMenuRolesResult> AssignMenuRolesAsync(
    Guid menuId,
    IReadOnlyList<Guid> roleIds,
    CancellationToken ct)
{
    ArgumentNullException.ThrowIfNull(roleIds);

    var menuExists = await db.Menus.AnyAsync(m => m.Id == menuId, ct).ConfigureAwait(false);
    if (!menuExists)
        return new AssignMenuRolesResult(AssignMenuRolesOutcome.MenuNotFound);

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

    // Full replace: delete existing rows, insert new ones.
    // EF batches these into a single SaveChangesAsync roundtrip (atomic by default transaction).
    var existing = await db.MenuRoleAssignments
        .Where(x => x.MenuId == menuId)
        .ToListAsync(ct)
        .ConfigureAwait(false);
    db.MenuRoleAssignments.RemoveRange(existing);

    db.MenuRoleAssignments.AddRange(distinctRoleIds.Select(rid =>
        new MenuRoleAssignment { MenuId = menuId, RoleId = rid, CreatedAt = DateTimeOffset.UtcNow }));

    await db.SaveChangesAsync(ct).ConfigureAwait(false);
    await cache.InvalidateAsync(ct).ConfigureAwait(false);

    return new AssignMenuRolesResult(AssignMenuRolesOutcome.Success);
}
```

### Backend: Endpoint Handler Pattern

```csharp
private static async Task<IResult> AssignRolesHandler(
    Guid id,
    AssignMenuRolesRequest request,
    IMenuService menuService,
    CancellationToken ct)
{
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(menuService);

    var result = await menuService.AssignMenuRolesAsync(id, request.RoleIds!, ct).ConfigureAwait(false);

    return result.Outcome switch
    {
        AssignMenuRolesOutcome.MenuNotFound => MenuNotFoundProblem(),
        AssignMenuRolesOutcome.RolesNotFound => Results.Problem(
            title: "One or more role IDs were not found",
            statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "ROLES_NOT_FOUND",
                ["messageKey"] = "admin.menus.rolesNotFound",
                ["invalidIds"] = result.InvalidIds,
            }),
        AssignMenuRolesOutcome.Success => Results.NoContent(),
        _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
    };
}
```

### Backend: Cache Invalidation

`IMenuCache` is currently a no-op (`NoOpMenuCache`). Story 4.7 implements the 5 s TTL cache. The call to `cache.InvalidateAsync(ct)` after every mutation is the established pattern (already done in `CreateMenuAsync`, `UpdateMenuAsync`, `DeleteMenuAsync`). Do the same in `AssignMenuRolesAsync`. No extra thought needed here.

### Frontend: `RoleAssignmentSection` Design

Model after `RoleAssignment` component in `web/src/routes/_app/admin/users.$userId.tsx`. Key differences:
- Import `useRolesQuery` from `../../features/admin/roles/useRolesQuery` (already available, fetches all roles with pageSize=100)
- Import `useAssignMenuRolesMutation` from `../../../features/admin/menus/menuAdminMutations`
- No "last admin lockout" concern
- Use `key` trick on the call site — `<RoleAssignmentSection key={menu.allowedRoleIds.join(',')} currentRoleIds={menu.allowedRoleIds} menuId={menuId} />` — to remount when allowedRoleIds membership changes after a save (consistent with user detail page)

The component should be in `menus.$menuId.tsx` (same file as `IconPickerSection`), placed after `IconPickerSection` in the component tree and after `SubMenusSection`.

### Frontend: `MenuDetailContentProps` Type Extension

The `MenuDetailContent` props type currently reads:
```ts
interface MenuDetailContentProps {
  menu: { id: string; name: string; order: number; isActive: boolean; parentId: string | null; icon: MenuIcon | null }
  menuId: string
}
```

Add `allowedRoleIds: string[]` to the inline `menu` shape:
```ts
menu: { id: string; name: string; order: number; isActive: boolean; parentId: string | null; icon: MenuIcon | null; allowedRoleIds: string[] }
```

`useMenuDetailQuery` returns `MenuItem` which already has `allowedRoleIds: string[]`, so the data is already available. `MenuDetailPage` passes `menu={menuQuery.data}` — no change needed in the parent component.

### Frontend: `useRolesQuery` Already Exists

`web/src/features/admin/roles/useRolesQuery.ts` exports `useRolesQuery()` and `RoleListItem` (id, name, description, permissionCount, isSystem). Use this directly — no new role-fetching hook needed.

### `MenuBindingCreated` Event — NOT Fired in This Story

The AC references `MenuBindingCreated` per AR-11. The existing event record is:
```csharp
// src/FormForge.Api/Infrastructure/EventBus/IDomainEventBus.cs
internal sealed record MenuBindingCreated(string DesignerId);
```

This event requires a `DesignerId` (the Designer's safe identifier). Story 4.4 assigns Roles to a Menu Item, but the menu has no `DesignerId` until a schema is bound to it in Epic 5. **Do not fire `MenuBindingCreated` in this story.** Epic 5 fires it when `PUT /api/admin/menus/{id}/binding` is implemented. The event infrastructure (`IDomainEventBus`, `InProcessEventBus`) is already in place for that future use.

There is also a duplicate record at `src/FormForge.Api/Features/Menus/Events/MenuBindingCreated.cs` (same shape, different namespace). This was likely a forward-looking stub. Do not touch or fire either variant.

### Integration Test Patterns

All tests reuse the existing `MenuIntegrationTests` class infrastructure:
- `InitializeAsync` seeds `platform-admin` role (ID `00000000-0000-0000-0000-000000000001`) and two test users
- Use `PlatformAdminRoleId` constant already in the test class
- `CreateMenuViaApiAsync` helper already exists for creating a menu in a test
- Response DTO `MenuResponseDto` already includes `IReadOnlyList<Guid> AllowedRoleIds`

For `AssignMenuRoles_ValidRoleIds_Returns204AndRoundTrips`:
```csharp
var menuId = await CreateMenuViaApiAsync(token, "Role Test", 0);

using var assignRequest = new HttpRequestMessage(
    HttpMethod.Put, $"/api/admin/menus/{menuId}/roles")
{
    Content = JsonContent.Create(new { roleIds = new[] { PlatformAdminRoleId } }),
};
assignRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
using var assignResponse = await _client!.SendAsync(assignRequest);
Assert.Equal(HttpStatusCode.NoContent, assignResponse.StatusCode);

// Round-trip via GET
using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/menus/{menuId}");
getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
using var getResponse = await _client!.SendAsync(getRequest);
var body = await getResponse.Content.ReadFromJsonAsync<MenuResponseDto>();
Assert.NotNull(body);
Assert.Contains(PlatformAdminRoleId, body.AllowedRoleIds);
```

### `Program.cs` Validator Registration

Add at line ~135 (after `UpdateMenuRequest` validator, before the first designer validator):
```csharp
builder.Services.AddScoped<IValidator<AssignMenuRolesRequest>, AssignMenuRolesRequestValidator>();
```

### What This Story Does NOT Implement

- **Navbar filtering** (Story 4.7) — `GET /api/menus` filtering by `allowedRoles` membership is not part of this story; the endpoint/cache is Story 4.7's scope.
- **UpdateMenuRequest role sync** — roles are managed exclusively via the new `PUT /api/admin/menus/{id}/roles` endpoint; the main `PUT /api/admin/menus/{id}` stays as-is (name/order/icon/isActive only).
- **`MenuBindingCreated` event publishing** — see note above; this is Epic 5.

## Dev Agent Record

### Agent Model Used

claude-opus-4-7[1m]

### Debug Log References

- Backend test run blocked initially because the Docker daemon was not running; the Testcontainers-based `PostgresFixture` could not start a Postgres container. User started Docker Desktop, after which the full suite ran green: 273/273 passing (267 baseline + 6 new).
- Frontend lint count: 32 errors (was 29 at Story 4.3 close). +3 since Story 4.3 — of which +1 is mine (the new `RoleAssignmentSection` adds another `react-refresh/only-export-components` trigger in `menus.$menuId.tsx`, expected per the story spec's "keep RoleAssignmentSection in the same file" directive). The remaining +2 are from intervening commits (3e4cfe2 login bootstrap fix, 16768fb memory), not from this story.

### Completion Notes List

- Implemented per spec without deviation. All 5 ACs satisfied:
  - AC-1: `PUT /api/admin/menus/{id}/roles` replaces the assignment set (full sync); `GET /api/admin/menus/{id}` round-trips `allowedRoleIds`. Asserted by `AssignMenuRoles_ValidRoleIds_Returns204AndRoundTrips`.
  - AC-2: Server-side contract — `MenuResponse.AllowedRoleIds` already projected via `Menu.RoleAssignments` navigation property (Story 4.1 scaffolding). Story 4.7 will consume this for navbar visibility filtering.
  - AC-3: Invalid roleId → 422 `ROLES_NOT_FOUND` with `invalidIds` extension. Asserted by `AssignMenuRoles_InvalidRoleId_Returns422WithRolesNotFound`.
  - AC-4: Empty list deletes all assignments. Asserted by `AssignMenuRoles_EmptyList_ClearsAssignments`.
  - AC-5: Unknown menuId → 404 `MENU_NOT_FOUND` (`MenuNotFoundProblem()` helper, same shape as the rest of the menu endpoints). Asserted by `AssignMenuRoles_MenuNotFound_Returns404`.
- Service implementation diverges intentionally from `UserService.AssignRolesAsync` in two ways: (1) no SERIALIZABLE transaction wrapper — there is no last-admin-style invariant to protect for menu role assignments, so the default implicit transaction around `SaveChangesAsync` is sufficient; (2) no domain event published — `MenuBindingCreated` requires a `DesignerId` which isn't bound until Epic 5, and the story explicitly directs us not to fire it. Second-layer `Distinct()` defense-in-depth and FluentValidation rules mirror the user analog exactly.
- Cache invalidation: `cache.InvalidateAsync(ct)` is called after `SaveChangesAsync` per the established pattern; `IMenuCache` is `NoOpMenuCache` today and will become 5 s TTL in Story 4.7.
- Frontend `RoleAssignmentSection` mirrors `users.$userId.tsx → RoleAssignment` exactly: parent passes `key={…sorted ids…}` so the local `Set<string>` draft initializes from props once per assignment change, avoiding `setState`-in-effect. Error handling: 404 → `notFoundError`, anything else → `rolesAssignError`. Co-located in `menus.$menuId.tsx` per the story spec's directive.
- Verified results: backend 273/273, frontend 78/78, TS+vite build clean, lint 32 (+1 from my changes, expected).

### File List

**New files:**
- `src/FormForge.Api/Features/Menus/Dtos/AssignMenuRolesRequest.cs`
- `src/FormForge.Api/Features/Menus/Validators/AssignMenuRolesRequestValidator.cs`

**Modified files:**
- `src/FormForge.Api/Features/Menus/MenuAdminEndpoints.cs` — add `PUT /{id:guid}/roles` route + `AssignRolesHandler`
- `src/FormForge.Api/Features/Menus/MenuService.cs` — add `AssignMenuRolesOutcome`, `AssignMenuRolesResult`, `AssignMenuRolesAsync`; extend `IMenuService`
- `src/FormForge.Api/Program.cs` — register `AssignMenuRolesRequestValidator`
- `src/FormForge.Api.Tests/Features/Menus/MenuIntegrationTests.cs` — +6 tests
- `web/src/features/admin/menus/menuAdminMutations.ts` — add `useAssignMenuRolesMutation`
- `web/src/routes/_app/admin/menus.$menuId.tsx` — add `RoleAssignmentSection`, extend `MenuDetailContentProps.menu` type
- `web/src/lib/i18n/locales/en.json` — +6 keys under `admin.menus`

## Change Log

| Date       | Description                                                                                                   | Author   |
|------------|---------------------------------------------------------------------------------------------------------------|----------|
| 2026-05-24 | Story 4.4 implementation complete — backend endpoint + service + 6 integration tests; frontend section + i18n. Backend 273/273, frontend 78/78, build clean. Status → review. | claude-opus-4-7 |
| 2026-05-25 | Story 4.4 bmad code review (Blind Hunter + Edge Case Hunter + Acceptance Auditor, 3 parallel layers, ~40 raw → 15 triaged). Applied 9 patches: P1 add `AssignMenuRolesOutcome.Conflict` + DbUpdateException catch for 23503/23505 → 409 ASSIGNMENT_CONFLICT (mirrors `UserService.AssignRolesAsync` defense-in-depth, but no SERIALIZABLE wrapper — no last-admin invariant), endpoint switch gains the Conflict case. P2 empty-state `t('admin.menus.rolesEmpty')`. P3 Save button gates on `rolesQuery.isError \|\| isCatalogEmpty` in addition to existing `isSaving \|\| isLoading` — silent destructive clear on query error is now impossible. P4 added `admin.menus.rolesNotFound` i18n key (backend emitted it but FE was missing it). P5 tightened `AssignMenuRoles_InvalidRoleId_Returns422WithRolesNotFound` to parse JsonDocument and assert the bogus Guid appears in the `invalidIds` extension specifically (was substring match against full body). P6 reordered validator rules so MaxRoleIds=256 cap fires BEFORE Distinct(), plus `.When(ids.Count <= MaxRoleIds)` guards on Distinct + no-empty-Guid rules so a pathologically large payload never allocates a same-size HashSet. P7 (from D1): delta-sync — `AssignMenuRolesAsync` now removes only rows whose RoleId is no longer in the new set and inserts only rows whose RoleId isn't already present, preserving `CreatedAt` on unchanged rows. New `AssignMenuRoles_ReassignSameRole_PreservesCreatedAt` integration test (PUT same set twice with 50ms gap, assert CreatedAt unchanged). P8 (from D2): empty-list save now requires a two-click confirmation within 3 seconds — uses `useState<emptyConfirmArmed>` + `useRef<timer>` with a `setTimeout(3000)` that auto-cancels the armed state; refactored away from `Date.now()` to avoid the `react-hooks/purity` lint that the initial approach tripped. Yellow inline warning band shown while armed; any checkbox toggle cancels the armed state and clears the timer. Cleanup `useEffect` clears the pending timer on unmount. P9 (from D3): merge hidden-assigned roles — computes `hiddenAssignedIds = currentRoleIds.filter(id => !visibleRoleIds.has(id))` and renders each as a pre-checked, italicized `t('admin.menus.unknownRole', { id })` row that the admin can uncheck to remove but full-sync save can never silently drop. New `rolesCatalogCapped` caption appears when `availableRoles.length === 100` directing admins to the Roles admin for additional roles. Frontend handler also maps `err.code === 'ASSIGNMENT_CONFLICT'` to the new `admin.menus.assignmentConflict` toast. +6 i18n keys (rolesNotFound, rolesEmpty, rolesEmptyWarning, rolesCatalogCapped, unknownRole, assignmentConflict). Backend full suite 274/274 (was 273 baseline + 1 new test from P7); frontend 78/78; TypeScript clean; production build clean; lint 32 errors (matched baseline — initial draft of P8 added 1 react-hooks/purity error from Date.now() which was resolved by the setTimeout refactor). Deferred 6 items to deferred-work.md (concurrent-edit detection, ApiError extension drop, EF Contains pattern, cache outside txn, checkbox htmlFor a11y, sub-menu role-gate semantics). Status → done. | claude-opus-4-7 |
