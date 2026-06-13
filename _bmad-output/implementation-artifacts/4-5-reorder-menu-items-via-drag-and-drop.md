# Story 4.5: Reorder Menu Items via Drag-and-Drop

Status: done

## Story

As a Platform Admin,
I want to reorder Menu Items and sub-menu items by drag-and-drop in the menu editor,
so that the navbar presents items in the correct order.

## Acceptance Criteria

**AC-1 — Drag-reorder persists via batch endpoint**
Given I am in the menu editor's reorder mode
When I drag a Menu Item to a new position within its scope (top-level row or peer sub-menu)
Then the SPA calls `PUT /api/admin/menus/reorder` with body `{ "items": [ { "id": <guid>, "order": <int> }, ... ] }` covering every item in the affected scope
And every menu's `sort_order` column is updated atomically
And `GET /api/admin/menus` returns the items in the new order (sorted by `sort_order` then by `id`)

**AC-2 — Sub-menu items confined to their parent**
Given a sub-menu item with `parentId = P`
When I drag it within its parent's sub-menu list
Then it reorders against peers and a single `PUT /api/admin/menus/reorder` call persists the new positions
When I drag it outside its parent's sub-menu list (over a top-level row, over a sibling sub-menu under a different parent, or over the top-level drop zone)
Then the drop is rejected client-side, no API call is made, and the on-screen order snaps back to its prior position
And the backend `PUT /api/admin/menus/reorder` endpoint independently enforces the same invariant: a request mixing items from different `parentId` scopes returns HTTP 422 with `code: "REORDER_MIXED_SCOPES"` (defense-in-depth; the UI must never produce this shape, but a direct API caller cannot bypass the parent gate)

**AC-3 — Keyboard reorder via `useKeyboardDnD` hook**
Given the menu editor's reorder mode has focus on a draggable row
When the admin presses `Tab` to focus a row, then `Space` (or `Enter`) to pick it up, then `ArrowUp`/`ArrowDown` to walk peer drop slots, then `Space` (or `Enter`) to commit the drop
Then the same reorder semantics apply as for pointer drag, the resulting `PUT /api/admin/menus/reorder` call carries the identical payload, and the pickup/move/commit/cancel announcements are spoken by an `aria-live="polite"` region
When the admin presses `Escape` while a row is picked up
Then the pickup is cancelled, focus returns to the originator row, and no API call is made
And the implementation reuses the existing `useKeyboardDnD()` hook at `web/src/components/designer/useKeyboardDnD.ts` (generic over a typed payload, ported from Story 3.10) — no fork, no new keyboard-DnD state machine

**AC-4 — Reorder propagates to every user's navbar within ≤5 s**
Given menus are reordered via `PUT /api/admin/menus/reorder`
When the service layer's `SaveChangesAsync` commits
Then `IMenuCache.InvalidateAsync(ct)` is called on the existing menu cache before the response is returned
And the next `GET /api/menus` (introduced by Story 4.7) reflects the new order on the next request — either immediately (write-time invalidation, the cleaner path) or within the 5 s navbar TTL ceiling required by FR-20 AC-3 / NFR-3
And this story implements only the invalidation hook + endpoint; the cache TTL itself is Story 4.7's scope and is currently a `NoOpMenuCache` (this is the established contract — every menu-admin mutation already calls `cache.InvalidateAsync`)

## Tasks / Subtasks

- [x] Task 1: Backend — `ReorderMenusRequest` + `ReorderMenuItem` DTOs (AC-1, AC-2)
  - [x] Create `src/FormForge.Api/Features/Menus/Dtos/ReorderMenusRequest.cs`
    - `internal sealed record ReorderMenusRequest(IReadOnlyList<ReorderMenuItem>? Items);`
    - `internal sealed record ReorderMenuItem(Guid Id, int Order);`
    - **Why nullable `Items?`**: a missing `"items"` key in the payload deserializes to `null` on the positional record, the validator's `NotNull` rule then returns 422, and the handler's `request.Items!.…` deref is safe behind the validation filter. Mirrors `AssignMenuRolesRequest` (Story 4.4).
    - **Why an envelope (`{ items: [...] }`) and not a top-level JSON array** (which the AC literal `[{ id, order }]` suggests): (a) FluentValidation in this codebase targets a single named class per filter — a top-level array has no stable class identity to register `IValidator<List<…>>` for; (b) every other admin mutation uses an envelope (`AssignMenuRolesRequest.RoleIds`, `CreateMenuRequest.{Name, …}`) — consistency matters; (c) it leaves room to add `cause` / `version` / `optimistic` fields later without a breaking change. Document this in the dev notes (AC-1 wording is the *contract intent*, not a literal byte shape).

- [x] Task 2: Backend — `ReorderMenusRequestValidator` (AC-1, AC-2)
  - [x] Create `src/FormForge.Api/Features/Menus/Validators/ReorderMenusRequestValidator.cs`
  - [x] Mirror `AssignMenuRolesRequestValidator` defensive ordering — MaxItems gate **before** Distinct / element checks so a pathological payload is rejected before allocating same-size HashSets:
    ```csharp
    private const int MaxItems = 256;

    RuleFor(x => x.Items)
        .NotNull()
        .WithMessage("items must be provided (use [] for a no-op).");

    RuleFor(x => x.Items)
        .Must(items => items == null || items.Count <= MaxItems)
        .WithMessage($"items cannot contain more than {MaxItems} entries.");

    RuleFor(x => x.Items)
        .Must(items => items == null || items.Select(i => i.Id).Distinct().Count() == items.Count)
        .When(x => x.Items == null || x.Items.Count <= MaxItems)
        .WithMessage("Duplicate item ids are not allowed.");

    RuleFor(x => x.Items)
        .Must(items => items == null || items.All(i => i.Id != Guid.Empty))
        .When(x => x.Items == null || x.Items.Count <= MaxItems)
        .WithMessage("Item ids must not be Guid.Empty.");

    RuleFor(x => x.Items)
        .Must(items => items == null || items.All(i => i.Order >= 0))
        .When(x => x.Items == null || x.Items.Count <= MaxItems)
        .WithMessage("Item orders must be non-negative.");
    ```
  - [x] Allow `items: []` (validator passes) — the service treats an empty batch as a no-op and returns `Success`. Reordering UI never sends `[]`, but a direct caller deserves a stable contract, not a 422 on the empty case.
  - [x] Register in `Program.cs` next to the existing menu validators (line ~135):
    ```csharp
    builder.Services.AddScoped<IValidator<ReorderMenusRequest>, ReorderMenusRequestValidator>();
    ```

- [x] Task 3: Backend — endpoint in `MenuAdminEndpoints.cs` (AC-1, AC-2, AC-4)
  - [x] Add `PUT /reorder` route inside `MapMenuAdminEndpoints()`, placed after the existing `PUT /{id:guid}/roles` route to keep mutation routes grouped:
    ```csharp
    // Story 4.5 — batch reorder for a single scope (all top-level OR all peers
    // under one parentId). Validates the scope invariant server-side as a
    // defense-in-depth check; the UI never sends mixed-scope payloads.
    group.MapPut("/reorder", ReorderMenusHandler)
         .AddValidationFilter<ReorderMenusRequest>()
         .WithSummary("Batch-reorder menu items within a single scope")
         .Produces(StatusCodes.Status204NoContent)
         .Produces(StatusCodes.Status404NotFound)
         .Produces(StatusCodes.Status422UnprocessableEntity);
    ```
  - [x] Implement `ReorderMenusHandler` private static method (model after `AssignRolesHandler`):
    ```csharp
    private static async Task<IResult> ReorderMenusHandler(
        ReorderMenusRequest request,
        IMenuService menuService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(menuService);

        var result = await menuService.ReorderMenusAsync(request.Items!, ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            ReorderMenusOutcome.MenusNotFound => Results.Problem(
                title: "One or more menu ids were not found",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "MENUS_NOT_FOUND",
                    ["messageKey"] = "admin.menus.reorderMenusNotFound",
                    ["invalidIds"] = result.InvalidIds,
                }),
            ReorderMenusOutcome.MixedScopes => Results.Problem(
                title: "Reorder batch spans multiple parent scopes",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "REORDER_MIXED_SCOPES",
                    ["messageKey"] = "admin.menus.reorderMixedScopes",
                }),
            ReorderMenusOutcome.Conflict => Results.Problem(
                title: "Concurrent modification",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "REORDER_CONFLICT",
                    ["messageKey"] = "admin.menus.reorderConflict",
                }),
            ReorderMenusOutcome.Success => Results.NoContent(),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
        };
    }
    ```
  - [x] **Route ordering nuance**: `/reorder` is a literal segment under `/api/admin/menus` and must be matched **before** `/{id:guid}/...` routes. ASP.NET routing prefers literal segments over parameter constraints — `/reorder` will not collide with `/{id:guid}` because `"reorder"` is not a valid `Guid`. No special handling needed, but verify with an integration test (Task 5: `Reorder_LiteralSegmentDoesNotCollideWithIdRoute`).

- [x] Task 4: Backend — `ReorderMenusAsync` service method (AC-1, AC-2, AC-4)
  - [x] Extend `MenuService.cs` with new outcome + result types (same shape style as Story 4.4):
    ```csharp
    internal enum ReorderMenusOutcome { Success, MenusNotFound, MixedScopes, Conflict }
    internal sealed record ReorderMenusResult(
        ReorderMenusOutcome Outcome,
        IReadOnlyList<Guid>? InvalidIds = null);
    ```
  - [x] Add to `IMenuService`:
    ```csharp
    Task<ReorderMenusResult> ReorderMenusAsync(
        IReadOnlyList<ReorderMenuItem> items,
        CancellationToken ct);
    ```
  - [x] Implement in `MenuService` (mirror the validator-protected entry-point pattern + race-window catches established by `AssignMenuRolesAsync`):
    1. `ArgumentNullException.ThrowIfNull(items);`
    2. Empty batch: `if (items.Count == 0) return new ReorderMenusResult(ReorderMenusOutcome.Success);`
    3. Build `var idToOrder = items.ToDictionary(i => i.Id, i => i.Order);` (validator guarantees Distinct, but a non-HTTP caller could pass dupes — collapse defensively: `items.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.Last().Order)` and treat the last-write as canonical).
    4. Fetch every menu referenced: `var menus = await db.Menus.Where(m => idToOrder.Keys.Contains(m.Id)).ToListAsync(ct).ConfigureAwait(false);`
    5. **NotFound check**: `if (menus.Count != idToOrder.Count) { var found = menus.Select(m => m.Id).ToHashSet(); var invalid = idToOrder.Keys.Where(id => !found.Contains(id)).ToList(); return new ReorderMenusResult(ReorderMenusOutcome.MenusNotFound, invalid); }`
    6. **Scope invariant**: `var parentScopes = menus.Select(m => m.ParentId).Distinct().ToList(); if (parentScopes.Count > 1) return new ReorderMenusResult(ReorderMenusOutcome.MixedScopes);` — `Distinct` handles `null` (top-level) correctly; a batch mixing top-level rows with rows under a parent yields `[null, someGuid]` → `Count == 2` → MixedScopes.
    7. Apply orders + UpdatedAt timestamp:
       ```csharp
       var now = DateTimeOffset.UtcNow;
       foreach (var menu in menus)
       {
           var newOrder = idToOrder[menu.Id];
           if (menu.Order == newOrder) continue;       // skip no-op rows so we don't touch UpdatedAt unnecessarily
           menu.Order = newOrder;
           menu.UpdatedAt = now;
       }
       ```
    8. Save changes, wrapped with the same 23503/23505 catch pattern as `AssignMenuRolesAsync` (concurrent delete or duplicate-order race) → `Conflict`:
       ```csharp
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
           // Bare PostgresException sometimes surfaces commit-time constraint failures.
           return new ReorderMenusResult(ReorderMenusOutcome.Conflict);
       }
       ```
    9. **Cache invalidation** (AC-4): `await cache.InvalidateAsync(ct).ConfigureAwait(false);` — same call as Create/Update/Delete/AssignRoles.
    10. Return `new ReorderMenusResult(ReorderMenusOutcome.Success);`
  - [x] **No SERIALIZABLE transaction wrapper**: Following the same reasoning as `AssignMenuRolesAsync` — there is no last-admin / single-instance invariant being protected. Two concurrent reorders of the same scope are last-write-wins, which is acceptable behavior for drag-reorder (and matches the architecture's "Optimistic only for: theme change, soft-delete row removal, drag-reorder" classification, line 846).
  - [x] **No domain event published**: `MenuBindingCreated` requires a `DesignerId` (Epic 5); no event corresponds to reorder today. The 5 s navbar cache invalidation in Story 4.7 is event-style enough.

- [x] Task 5: Backend — integration tests (AC-1, AC-2, AC-4)
  - [x] Append a new `// ---------- PUT /api/admin/menus/reorder (Story 4.5) ----------` section to `src/FormForge.Api.Tests/Features/Menus/MenuIntegrationTests.cs` after the existing Story 4.4 block. Cover at minimum:
    - `ReorderMenus_Unauthenticated_Returns401`
    - `ReorderMenus_AsNonAdmin_Returns403`
    - `ReorderMenus_TopLevelScope_PersistsNewOrder` — seed 3 top-level menus with orders [0, 1, 2], PUT `[{A, 2}, {B, 0}, {C, 1}]`, then `GET /api/admin/menus?page=1&pageSize=25` and assert the returned order is `[B, C, A]`.
    - `ReorderMenus_SubMenuScope_PersistsNewOrder` — seed a parent + 3 children, reorder the children, assert via `GET /api/admin/menus?parentId={parentId}` that the new order surfaces.
    - `ReorderMenus_MixedScopes_Returns422` — seed one top-level + one sub-menu, PUT both ids in one batch, assert HTTP 422 with `code: "REORDER_MIXED_SCOPES"`.
    - `ReorderMenus_UnknownId_Returns422WithInvalidIds` — PUT a real menu + a random Guid, assert HTTP 422 with `code: "MENUS_NOT_FOUND"`, parse body as `JsonDocument`, assert the bogus Guid appears in the `invalidIds` array (mirror the Story 4.4 P5 assertion shape — substring matching against the full body lets the Guid leak into title/detail and false-pass).
    - `ReorderMenus_EmptyItems_Returns204NoOp` — PUT `{"items": []}`, assert 204 and no `UpdatedAt` mutation on any seeded menu.
    - `ReorderMenus_DuplicateIds_Returns422` — validator path, ensures the Distinct rule fires.
    - `ReorderMenus_TooManyItems_Returns422` — 257-item payload, asserts the MaxItems=256 cap fires before the Distinct allocation.
    - `ReorderMenus_LiteralSegmentDoesNotCollideWithIdRoute` — sanity test that PUT `/api/admin/menus/reorder` reaches the reorder handler (returns 204 for empty items) and PUT `/api/admin/menus/{guid}` still reaches the update handler — guards against a future route-ordering regression.
    - `ReorderMenus_DoesNotMutateUpdatedAtOnUnchangedRows` — seed 3 menus with known `UpdatedAt`, reorder with the same orders they already hold, assert no `UpdatedAt` changed.
    - `ReorderMenus_OnlyMutatesUpdatedAtOnChangedRows` — seed 3, reorder so only one changes, assert `UpdatedAt` updated on the one and untouched on the other two.
  - [x] Test count baseline: **274 before this story** (Story 4.4 closed at 274/274). Expect **~284 after** (+10 tests above). Update the actual count when running locally.
  - [x] Reuse existing fixture helpers: `LoginAsync`, `CreateMenuViaApiAsync`, `CreateSubMenuViaApiAsync`, `MenuResponseDto`, `PagedResultDto<>`. No new helpers needed.

- [x] Task 6: Frontend — `useReorderMenusMutation` with optimistic update (AC-1, AC-4)
  - [x] Append to `web/src/features/admin/menus/menuAdminMutations.ts`:
    ```ts
    export interface ReorderMenuItemPayload {
      id: string
      order: number
    }

    export function useReorderMenusMutation() {
      const queryClient = useQueryClient()
      const { t } = useTranslation()
      return useMutation({
        mutationFn: (items: ReorderMenuItemPayload[]) =>
          httpClient.put<void>('/api/admin/menus/reorder', { items }),
        // Optimistic update per architecture line 846 ("Optimistic only for:
        // theme change, soft-delete row removal, drag-reorder"). The
        // pre-mutation snapshot lets us roll back if the server rejects.
        onMutate: async (items) => {
          await queryClient.cancelQueries({ queryKey: MENUS_ADMIN_QUERY_KEY })
          const previousLists = queryClient.getQueriesData({ queryKey: MENUS_ADMIN_QUERY_KEY })
          // Apply new orders to every cached list query (top-level + per-parent
          // children variants share the same prefix).
          const idToOrder = new Map(items.map((i) => [i.id, i.order]))
          for (const [key, cached] of previousLists) {
            if (cached == null) continue
            const paged = cached as PagedResult<MenuListItem>
            const next = {
              ...paged,
              data: [...paged.data]
                .map((m) => (idToOrder.has(m.id) ? { ...m, order: idToOrder.get(m.id)! } : m))
                .sort((a, b) => a.order - b.order || a.id.localeCompare(b.id)),
            }
            queryClient.setQueryData(key, next)
          }
          return { previousLists }
        },
        onError: (_err, _vars, context) => {
          // Roll back every cache entry we touched.
          if (context?.previousLists) {
            for (const [key, value] of context.previousLists) {
              queryClient.setQueryData(key, value)
            }
          }
          toast.error(t('admin.menus.reorderError'))
        },
        onSuccess: () => {
          toast.success(t('admin.menus.reorderSuccess'))
        },
        // Always re-sync from the server after the mutation settles so any
        // server-side tie-breaks (same order → id order) match the cache.
        onSettled: () => {
          invalidateAllMenus(queryClient)
        },
      })
    }
    ```
  - [x] Import `PagedResult` and `MenuListItem` types: they live in `web/src/features/admin/users/types.ts` and `web/src/features/menu/types.ts` respectively (already used by `useMenusAdminQuery`).
  - [x] **Why both `onSettled` invalidation AND optimistic onMutate**: the optimistic write keeps the UI snappy during a drag; the `onSettled` invalidate refetches from the server so any ordering tie-breaks (`ORDER BY sort_order ASC, id ASC`) that differ from the client's local sort are corrected. Without `onSettled`, the cache could drift from server state on the rare client/server tie-break disagreement.

- [x] Task 7: Frontend — `ReorderModeSection` for top-level menus (AC-1, AC-2 client-side, AC-3)
  - [x] Extend `web/src/routes/_app/admin/menus.tsx` with a "Reorder" mode toggle (button toggles between "Manage Menus" — current paginated view — and "Reorder Menus" — single-page draggable list).
  - [x] In reorder mode, fetch ALL top-level menus in one shot (the existing `useMenusAdminQuery` accepts a `pageSize` — pass `MaxItems=256` to match the backend cap so the UI can't request more than the endpoint accepts in one batch; if the install ever exceeds 256 menus the architecture-team can decide on pagination-in-reorder UX, but this is not v1 scope):
    ```ts
    const REORDER_PAGE_SIZE = 256
    const reorderQuery = useMenusAdminQuery(1, REORDER_PAGE_SIZE)
    ```
    Filter on the client to top-level only: `reorderQuery.data?.data.filter(m => m.parentId == null)` (the existing endpoint returns *all* menus when `parentId` isn't supplied, so this filter is required — confirm by reading `MenuService.GetMenusAsync` which only filters when `parentId.HasValue`).
  - [x] Local draft state initialized from the server list:
    ```ts
    const [draft, setDraft] = useState<MenuListItem[] | null>(null)
    useEffect(() => {
      if (reorderQuery.data && draft === null) {
        setDraft(reorderQuery.data.data
          .filter(m => m.parentId == null)
          .slice()
          .sort((a, b) => a.order - b.order || a.id.localeCompare(b.id)))
      }
    }, [reorderQuery.data, draft])
    ```
    **Critical pattern (from Story 4.4 review P6)**: Re-sync the draft when the server query refetches after a successful save, otherwise a background refetch silently overwrites the next user reorder. Use a `key` trick on the wrapping component to remount on data change, OR effect-sync explicitly. The simpler path here is to call `setDraft(...)` inside the mutation's `onSuccess` (or after `await refetch()`).
  - [x] Implement native HTML5 DnD on each row — pattern adapted from `web/src/components/designer/DesignerCanvas.tsx`:
    - Each row: `<li draggable="true" onDragStart={…} onDragOver={…} onDrop={…} onDragEnd={…}>`
    - `onDragStart`: `e.dataTransfer.setData('application/x-formforge-menu-reorder', JSON.stringify({ id: row.id, parentId: row.parentId ?? null }))` and `e.dataTransfer.effectAllowed = 'move'`. **Use a different MIME type from the designer canvas** (`application/x-formforge-menu-reorder` vs `application/x-formforge-designer`) — the architecture explicitly keeps DnD contexts isolated, and a shared MIME would let a designer-canvas drag accidentally drop on a menu row.
    - `onDragOver`: read `e.dataTransfer.types` (NOT `getData` — that's protected during dragover per WHATWG); `if (!e.dataTransfer.types.includes('application/x-formforge-menu-reorder')) return;` to filter, then `e.preventDefault()` to mark the row as a valid drop target. Also check parent scope: store the dragged item's parentId in a `dragSourceParentIdRef` (set in `onDragStart`) and reject in `onDragOver` if the hover target's `parentId !== dragSourceParentIdRef.current`. This implements the AC-2 client-side scope rejection.
    - `onDrop`: read `e.dataTransfer.getData(MIME)` (now safe), compute insertion index from where the drop happened (above-midpoint vs below-midpoint of the target row), splice the dragged item from the draft array and insert at the new index, then reassign `order = i` for every item (0-based sequential — gaps in `order` are permitted per Story 4.1 AC-4 but unnecessary here).
    - `onDragEnd`: clear `dragSourceParentIdRef`.
  - [x] Save button → `reorderMutation.mutate(draft.map((row, i) => ({ id: row.id, order: i })))`.
  - [x] **Cancel / discard draft**: a button that re-initializes `draft` from `reorderQuery.data` without saving.
  - [x] **Disable Save while** `reorderMutation.isPending`, while `reorderQuery.isError`, and while the draft is unchanged from the server snapshot (compare id-sequences). Last guard prevents an empty no-op PUT that produces a confusing success toast.

- [x] Task 8: Frontend — sub-menu reorder section (AC-1, AC-2, AC-3)
  - [x] In `web/src/routes/_app/admin/menus.$menuId.tsx`, extend `SubMenusSection` with the same Reorder-mode toggle (or a permanent draggable list — if the existing paginated table is replaced for sub-menus, pagination becomes unnecessary because v1 caps sub-menus at one nesting level and the practical count per parent is small; verify with the product owner if you adopt this). The story-spec-aligned path is to **add a Reorder toggle alongside the existing list** so the AC-2 contract ("drag a sub-menu within its parent") is satisfied without ripping out the table.
  - [x] Reuse `useReorderMenusMutation` from Task 6. The mutation is parent-agnostic — it sends `[{ id, order }]` and the backend infers the scope. The UI must constrain drag targets to children of the current parent (refuse drops on rows outside `useMenuChildrenQuery(parentId)`'s result set).
  - [x] Co-locate the reorder list inside `SubMenusSection` per the project convention (same file as parent, same file as `IconPickerSection` / `RoleAssignmentSection`). Expect a `react-refresh/only-export-components` lint warning — matches the convention established by Stories 4.3 / 4.4.

- [x] Task 9: Frontend — keyboard reorder via `useKeyboardDnD` (AC-3)
  - [x] Hoist a `useKeyboardDnD<{ id: string; parentId: string | null }>()` instance to the reorder section (one per top-level page; one per `SubMenusSection`).
  - [x] Wire keyboard handlers on each draggable row:
    - On `keydown.Space` or `keydown.Enter` while focused and nothing picked up: `kbdDnD.pickUp({ id: row.id, parentId: row.parentId }, t('admin.menus.reorderPickup', { name: row.name }), e.currentTarget)`. Call `e.preventDefault()` to suppress page-scroll on Space.
    - On `keydown.ArrowDown` / `keydown.ArrowUp` while something is picked up: move the picked item's index in `draft` ±1 (clamped to `[0, draft.length-1]`); announce via `kbdDnD.announce` (use the hook's announcement state).
    - On `keydown.Space` or `keydown.Enter` while something is picked up: `kbdDnD.commit(t('admin.menus.reorderCommit', { name: row.name }), row.id)`. **Do NOT auto-save** — the commit places the row in the local draft; the user clicks the Save button. This matches the designer-canvas semantic (commit = "drop", not "persist").
    - On `keydown.Escape` while something is picked up: revert the draft to the pre-pickup snapshot (store one alongside the pickup) and `kbdDnD.cancel(t('admin.menus.reorderCancelled'))`.
  - [x] Render the `aria-live="polite"` region using `kbdDnD.announcement`:
    ```tsx
    <span
      role="status"
      aria-live="polite"
      aria-atomic="true"
      style={{ position: 'absolute', width: 1, height: 1, padding: 0, overflow: 'hidden', clip: 'rect(0,0,0,0)' }}
    >
      {kbdDnD.announcement}
    </span>
    ```
    The ZWSP token-flip inside the hook (`announce()`) is what guarantees identical successive announcements still fire — don't strip it.
  - [x] **Focus restoration on cancel**: `kbdDnD.originatorRef.current?.focus()` after `cancel`. After `commit`, walk to the inserted row by id (use a `useEffect` that watches `kbdDnD.insertedIdRef` and calls `.focus()` on the `data-row-id={id}` element).

- [x] Task 10: Frontend — i18n keys (AC-1, AC-2, AC-3, AC-4)
  - [x] Add to `admin.menus` in `web/src/lib/i18n/locales/en.json`:
    - `"reorderModeButton": "Reorder"`
    - `"manageModeButton": "Manage"`
    - `"reorderInstructions": "Drag rows to reorder. Sub-menu items stay within their parent. Press Space on a focused row to pick it up, arrows to move, Space again to drop, Escape to cancel."`
    - `"saveOrderButton": "Save Order"`
    - `"savingOrderButton": "Saving…"`
    - `"discardOrderButton": "Discard Changes"`
    - `"reorderSuccess": "Order saved"`
    - `"reorderError": "Failed to save order"`
    - `"reorderMenusNotFound": "One or more menu items no longer exist. The list has been refreshed."`
    - `"reorderMixedScopes": "Items in a reorder must belong to the same parent."`
    - `"reorderConflict": "Another change to this menu group happened at the same time. The list has been refreshed."`
    - `"reorderPickup": "{{name}} picked up. Use arrow keys to move."`
    - `"reorderMove": "{{name}} moved to position {{position}}."`
    - `"reorderCommit": "{{name}} dropped at position {{position}}."`
    - `"reorderCancelled": "Reorder cancelled."`
    - `"reorderNoChanges": "No order changes to save."`

- [x] Task 11: Frontend — handle backend error codes specifically (AC-2 backend, AC-4)
  - [x] In `useReorderMenusMutation`'s `onError`, switch on `err.code`:
    - `'MENUS_NOT_FOUND'` → `toast.error(t('admin.menus.reorderMenusNotFound'))` + roll back local draft + `invalidateAllMenus`
    - `'REORDER_MIXED_SCOPES'` → `toast.error(t('admin.menus.reorderMixedScopes'))` + same rollback (this should never reach the user because the client gates the drop; if it does, it's a UI bug and the toast is a fallback)
    - `'REORDER_CONFLICT'` → `toast.error(t('admin.menus.reorderConflict'))` + rollback + invalidate
    - default → `toast.error(t('admin.menus.reorderError'))` + rollback
  - [x] The optimistic-update rollback in Task 6 already handles cache restoration; this task adds the per-code toast routing.

- [x] Task 12: Frontend — minimal smoke tests (testing standards)
  - [x] Add a `__tests__/menuReorder.test.tsx` next to `menus.tsx` that mounts the reorder section with a stubbed `useMenusAdminQuery` and `useReorderMenusMutation`, exercises a drag-and-drop swap via `fireEvent.dragStart` / `fireEvent.dragOver` / `fireEvent.drop`, and asserts the mutation was called with the expected `[{id, order}]` payload.
  - [x] Add a `__tests__/menuReorderKeyboard.test.tsx` that exercises the `useKeyboardDnD` integration: focus row, Space to pick up, ArrowDown twice, Space to commit, assert the draft reordered and the announcements were spoken. Mirror the shape of `web/src/components/designer/__tests__/useKeyboardDnD.test.ts` for the announcement-fire-twice ZWSP check.
  - [x] Do **NOT** test the backend mutation roundtrip — the integration tests in Task 5 cover that. Frontend smoke tests stub at the `useReorderMenusMutation` boundary.
  - [x] Frontend test count baseline: **78 before this story** (Story 4.4). Expect ~80 after (+2 minimum). Don't over-test rendered presentation.

## Dev Notes

### What This Story Is NOT

- **NOT** the 5 s navbar TTL cache. That's Story 4.7. This story only calls `cache.InvalidateAsync(ct)` after a successful save — the cache itself is `NoOpMenuCache` today, so the call is a no-op at runtime. The contract is preserved; the behavior light-switches on when Story 4.7 lands.
- **NOT** the `GET /api/menus` public navbar endpoint. That's Story 4.7. The reorder contract talks to `GET /api/admin/menus` (admin paginated list) only.
- **NOT** a switch to a synthetic-DnD library (dnd-kit, react-dnd). The architecture explicitly chose native HTML5 DnD for the designer canvas (Decision 4.5) and the menu reorder should follow the same convention. Native gets you browser-managed auto-scroll, hit-testing, and drag previews; synthetic adds bundle weight + manual rect plumbing. No new dependency.
- **NOT** a reorder of cross-parent moves ("promote a sub-menu to top-level via drag"). AC-2 explicitly forbids this. Cross-parent moves require an `Update` (parentId change) — handled today via the menu-detail edit form, not via reorder.
- **NOT** a reorder of `MenuRoleAssignments`, `RolePermissions`, or any other join table. The drag-reorder applies to `menus.sort_order` only.

### Database Schema — No Migration Needed

The `menus.sort_order` column exists from the Story 4.1 migration (`20260524054931_CreateMenusAndMenuRoleAssignments.cs`):
- `sort_order INT NOT NULL DEFAULT 0`
- Index `idx_menus_sort_order` on `(sort_order)` — accelerates the `ORDER BY sort_order ASC` query in `GetMenusAsync` ([Source: src/FormForge.Api/Infrastructure/Persistence/Migrations/20260524054931_CreateMenusAndMenuRoleAssignments.cs:75-77])
- Column name is `sort_order` (not `order`) because `order` is a PostgreSQL reserved keyword — this is documented in `FormForgeDbContext.OnModelCreating` line 170. The C# property is `Menu.Order : int`. **JSON wire shape is `order`** (the camelCased property name) — confirmed by the existing `MenuResponse.Order`, `MenuListItem.Order`, and the Story 4.4 round-trip assertions.

The existing `idx_menus_sort_order` index is single-column and unfiltered. After this story, the hot query becomes `WHERE parent_id IS NULL ORDER BY sort_order, id` (or `WHERE parent_id = ? ORDER BY sort_order, id`). A composite index on `(parent_id, sort_order)` would optimize this — **but do not add it in this story**. Reasons: (1) Story 4.7 will profile the navbar query and decide; (2) the practical menu count is small (single-digit top-level, low-double-digit per parent in v1); (3) two single-column indexes are already in place. Note it in the deferred section if you observe a regression in the integration tests' query plans (run `EXPLAIN` against a real query if curious).

### Backend File Locations (Story 4.4 Conventions)

- DTO: `src/FormForge.Api/Features/Menus/Dtos/ReorderMenusRequest.cs`
- Validator: `src/FormForge.Api/Features/Menus/Validators/ReorderMenusRequestValidator.cs`
- Service additions: extend `src/FormForge.Api/Features/Menus/MenuService.cs` (new outcome enum, result record, interface method, implementation)
- Endpoint: extend `src/FormForge.Api/Features/Menus/MenuAdminEndpoints.cs` (new `MapPut("/reorder", …)` registration + handler)
- Validator registration: `src/FormForge.Api/Program.cs` line ~135 (next to existing menu validators)

The `MenuAdminEndpoints.MapMenuAdminEndpoints` extension is wired into `/api/admin/menus` by `AdminEndpoints.MapAdminEndpoints` at line 16 of `src/FormForge.Api/Features/Roles/AdminEndpoints.cs` — confirmed registered. No new top-level route group needed.

### Service Implementation Pattern — Mirror Story 4.4 (`AssignMenuRolesAsync`)

The reorder service method follows the **exact same structure** as `AssignMenuRolesAsync` in `MenuService.cs:174-264`:
1. Argument null-guard.
2. Fast-path on empty input (Story 4.4 returns `Success` on empty `roleIds` after the menu-exists check; Story 4.5 returns `Success` on `items: []` without a DB hit).
3. Fetch existing rows by id, compare to requested ids → return `MenusNotFound` with `InvalidIds` payload (mirrors `RolesNotFound`).
4. Apply changes to entity properties.
5. `SaveChangesAsync` wrapped in `DbUpdateException` + bare `PostgresException` catches for 23503/23505 → `Conflict`.
6. `cache.InvalidateAsync(ct)`.
7. Return `Success`.

**Two key differences from Story 4.4**:
- Step 4 mutates `Menu.Order` (and `Menu.UpdatedAt`) on the tracked entities, vs. inserting/deleting `MenuRoleAssignment` rows. No delta-vs-blanket-replace distinction needed.
- Step 4 also gates the scope invariant (mixed `parentId` → `MixedScopes`) before any mutation. Story 4.4 had no scope-invariant equivalent.

### Validator Defense-in-Depth Pattern (from Story 4.4 P6 Patch)

`AssignMenuRolesRequestValidator` was patched in the Story 4.4 code review (P6) to enforce **MaxItems before Distinct** — a 1M-entry payload would otherwise allocate a 1M HashSet before the size rule fires, giving an attacker a cheap memory-exhaustion vector. Apply the **same defensive ordering** to `ReorderMenusRequestValidator`:
1. `NotNull` first.
2. `MaxItems` (≤256) cap second.
3. `Distinct` (and all per-element rules) gated with `.When(items == null || items.Count <= MaxItems)` so they short-circuit on oversized payloads.

This pattern is mandatory for any list-shaped validator in the codebase going forward. The `CreateMenuRequest` / `UpdateMenuRequest` validators don't need it because they have no list inputs.

### Frontend Optimistic Mutation Pattern (from Architecture line 845-847)

Architecture explicitly classifies drag-reorder as **optimistic-only** mutation. The shape is:
- `onMutate`: snapshot current cache → write the new order to cache → return snapshot in context.
- `onError`: restore snapshot from context.
- `onSettled`: `invalidateQueries` to re-sync from server (catches client/server tie-break disagreement).
- `onSuccess`: success toast only — cache is already correct from `onMutate`.

This is the **opposite** of the form-save / role-assignment pattern (pessimistic; `onSuccess` invalidates and refetches). Don't copy the role-assign mutation's structure for this one.

### Native HTML5 DnD Pattern — Adapt from `DesignerCanvas.tsx`

The designer canvas's DnD pipeline at `web/src/components/designer/DesignerCanvas.tsx` and `web/src/components/designer/dnd.ts` is the project's reference implementation. Adapt these patterns:
- **MIME-based filtering** to prevent foreign drags (palette → canvas vs reorder → menu list). Use a new MIME `application/x-formforge-menu-reorder` — do NOT reuse `application/x-formforge-designer`.
- **`types` check during `dragover`** (NOT `getData` — that's protected during dragover per WHATWG; only available on `drop`).
- **`e.preventDefault()` in `dragover`** to mark the element as a valid drop target.
- **Explicit `<DropZone>` elements between rows** — optional for menu reorder since rows are dense. The simpler approach is "drop on row → above-midpoint inserts before, below-midpoint inserts after". The designer pattern is more complex because containers nest; menu rows are flat per scope.
- **`dragend` + `drop` listeners on `document`** to clear highlight state — see `EmptyCanvasDrop` lines 113-119 for the idiom. This catches "user released drag over browser chrome" which otherwise leaves a stuck highlight.

### Keyboard DnD Hook — Reuse, Don't Fork

The `useKeyboardDnD<T>()` hook at `web/src/components/designer/useKeyboardDnD.ts` is **already generic** over the payload type (`T`) per Story 3.10 AC-6:

> "The hook is intentionally generic — it must not import from `./dnd`, `@/store/designerCanvas`, or any other designer-specific module so Epic 4 can reuse it for menu reordering with its own drag-data shape (see AC-6 of Story 3.10)."

[Source: web/src/components/designer/useKeyboardDnD.ts:1-11]

Import as:
```ts
import { useKeyboardDnD } from '../../../components/designer/useKeyboardDnD'

const kbdDnD = useKeyboardDnD<{ id: string; parentId: string | null }>()
```

Pass translated announcement strings via `t(…)` — the hook is i18n-agnostic by design. The ZWSP token-flip inside `announce()` is what guarantees identical successive messages re-fire on screen readers; do not bypass it.

**Do NOT modify the hook's interface to add menu-specific concerns.** If a need arises, extract into a wrapper hook (`useMenuReorderDnD`) that composes `useKeyboardDnD`.

### Why Wrap the Reorder Payload in `{ items: [...] }` — Decision Log

The AC literal wording is `[{ id, order }]` (top-level JSON array). The implementation deviates to `{ "items": [{ id, order }] }` for three reasons:

1. **FluentValidation registration target**: `IValidator<List<ReorderMenuItem>>` is technically registrable but awkward — the existing validation pipeline at `src/FormForge.Api/Common/Endpoints/EndpointFilters/ValidationFilter.cs:9` is typed `ValidationFilter<T> where T : class`, and `List<T>` is a class but not a domain-modelled one. Every other endpoint in the codebase uses a named DTO record. Inconsistency here would diverge for no benefit.

2. **Forward compatibility**: adding a `cause: "drag" | "keyboard"` (observability), `version: int` (optimistic concurrency), or `dryRun: bool` (validation-only) field later is a non-breaking change to an envelope; it would be a breaking change to a raw array.

3. **`AssignMenuRolesRequest` precedent (Story 4.4)**: same shape decision (`{ roleIds: [...] }`) — and the AC for that story also used "with `roleIds: [roleId, ...]`" phrasing, never raw-array.

**Document the deviation in the OpenAPI summary** so consumers see the actual contract: `"Batch-reorder menu items. Body: { items: [{ id: Guid, order: int }] }. The items list must be ≤256 entries, all belonging to the same parentId scope (all top-level or all under one parent)."`

### Sample Request / Response Shapes

**Request — top-level reorder:**
```json
PUT /api/admin/menus/reorder
Content-Type: application/json
Authorization: Bearer …

{
  "items": [
    { "id": "0192d72c-…-aa11", "order": 0 },
    { "id": "0192d72c-…-aa22", "order": 1 },
    { "id": "0192d72c-…-aa33", "order": 2 }
  ]
}
```
**Response (success):** `204 No Content`

**Response (mixed scopes, the AC-2 defense-in-depth):**
```http
HTTP/1.1 422 Unprocessable Entity
Content-Type: application/problem+json

{
  "type": "...",
  "title": "Reorder batch spans multiple parent scopes",
  "status": 422,
  "code": "REORDER_MIXED_SCOPES",
  "messageKey": "admin.menus.reorderMixedScopes",
  "correlationId": "01HQS…"
}
```

**Response (unknown id):**
```http
HTTP/1.1 422 Unprocessable Entity

{
  "title": "One or more menu ids were not found",
  "status": 422,
  "code": "MENUS_NOT_FOUND",
  "messageKey": "admin.menus.reorderMenusNotFound",
  "invalidIds": ["0192d72c-…-dead"],
  "correlationId": "01HQS…"
}
```

### Architectural Compliance Checklist

- [x] Endpoint registered under `/api/admin/menus` group → carries `RequireAuth + RequirePlatformAdmin + RequireRateLimiting("admin")` (Story 2.4 — 120 req/min/user sliding window) automatically.
- [x] DTO is `internal sealed record` with primary-positional constructor and nullable list (Story 4.4 pattern).
- [x] Validator uses `MaxItems` cap **before** `Distinct` (Story 4.4 P6 pattern).
- [x] Service catches `23503` (FK violation) + `23505` (unique violation) translated to `Conflict` (Story 4.4 P1 pattern).
- [x] `cache.InvalidateAsync(ct)` called after `SaveChangesAsync` (every menu-admin mutation does this).
- [x] No SERIALIZABLE transaction (no last-admin / single-instance invariant to protect — same justification as Story 4.4 AssignMenuRolesAsync).
- [x] Frontend uses `httpClient.put` (no raw fetch); query keys follow `['admin', 'menus', …]` tuple convention.
- [x] Frontend optimistic mutation per architecture line 846 (drag-reorder is explicitly in the optimistic-allowlist).
- [x] Native HTML5 DnD (no new dependency); reuses `useKeyboardDnD` for keyboard parallel path.
- [x] i18n: every user-facing string via `t(key)`; new keys under `admin.menus.*`.
- [x] No logging of request bodies (per FR-46 anti-patterns); structured logging with named placeholders only if you add new log calls.
- [x] OpenAPI annotations (`.Produces<...>`, `.WithSummary`) populated on the new endpoint.

### Previous-Story Intelligence (Story 4.4 Learnings)

Distilled from `_bmad-output/implementation-artifacts/4-4-assign-roles-to-menu-item.md` review patches — apply these proactively:

1. **`MaxItems` before `Distinct` in the validator** (P6) — covered in Task 2. A pathological large payload allocates a same-size HashSet before the size rule fires; reorder the rules.

2. **Race-window FK/unique catches** (P1) — covered in Task 4 step 8. Catch `DbUpdateException` AND bare `PostgresException` for SqlState `23503` / `23505` → `Conflict`. Bare `PostgresException` matters because Npgsql sometimes surfaces commit-time constraint failures without wrapping.

3. **`invalidIds` extension as an array** (P5) — covered in Task 5. Asserting substring presence in the response body false-passes when the bogus id leaks into `title` or `detail`; parse `JsonDocument` and assert membership in `invalidIds` specifically.

4. **Re-sync local draft when query refetches** (P6 / Story 4.3 review) — covered in Task 7. After a successful save, the query refetches; if the draft state initializer only ran on mount, subsequent reorders silently overwrite the refreshed data. Either `useEffect`-resync from server data OR remount the component via `key={fingerprint}`.

5. **Disable Save when query is errored or loading** (P3) — covered in Task 7. A click while `useMenusAdminQuery.isError === true` would PUT an outdated cache snapshot. Gate the Save button.

6. **Keep small sub-components co-located in the same file** (Story 4.3 / 4.4 convention) — covered in Tasks 7 / 8. Expect a `react-refresh/only-export-components` lint warning; that's the project-wide accepted tradeoff for the convention.

7. **`messageKey` is the contract between backend `code`/`messageKey` extension and frontend `t(key)`** — covered in Tasks 3 / 10. Every new `code` the backend emits must have a corresponding i18n key. Story 4.4 P4 flagged a missing `admin.menus.rolesNotFound` key emitted by the backend but never added to `en.json` — don't repeat this. Use `admin.menus.reorderMenusNotFound`, `admin.menus.reorderMixedScopes`, `admin.menus.reorderConflict` — all defined in Task 10.

### Build & Verification

- Backend: `dotnet build` clean, `dotnet test` 100% pass — record actual count (expect ~284). Run from `src/` directory.
- Frontend: `npm run build` (Vite + `tsc --noEmit`) clean, `npm run test` 100% pass — record actual count (expect ~80).
- Lint: `npm run lint` — expect 32 errors baseline (Story 4.4 close). +1-2 additional `react-refresh/only-export-components` from new co-located components is acceptable per convention. Do **not** add new lint suppressions; surface deltas.
- Manual smoke (if Docker is available locally): start Postgres + MinIO via Aspire, run `dotnet run` on `FormForge.Api`, hit `https://localhost:5001/openapi/v1.json` to confirm the new `PUT /api/admin/menus/reorder` route shows up with `204` + `422` documented.

### Project Structure Notes

The story creates **no new files outside the established Story-4.x layout**:
- 1 new DTO file (`Dtos/ReorderMenusRequest.cs`)
- 1 new validator file (`Validators/ReorderMenusRequestValidator.cs`)
- Endpoint + service extensions go into existing files (`MenuAdminEndpoints.cs`, `MenuService.cs`, `Program.cs`)
- Frontend changes extend existing route components + the existing mutations module — no new feature folders
- New i18n keys go into the existing `admin.menus` block

This matches the architecture's "Module / Feature Folder Structure" (Decision 4.6) — no import-graph extensions, no new top-level features.

### References

- Story 4.5 epic spec: `_bmad-output/planning-artifacts/epics.md:1052-1076` (AC source)
- FR-20 Menu Ordering: `_bmad-output/planning-artifacts/epics.md:41` ("≤5 s propagation")
- AR-49 Mutation Strategy: `_bmad-output/planning-artifacts/epics.md:142` ("Optimistic only for … drag-reorder")
- Architecture: optimistic mutation classification at `_bmad-output/planning-artifacts/architecture.md:845-847`
- Architecture: keyboard-DnD parallel model at `_bmad-output/planning-artifacts/architecture.md:559-565`
- Story 3.10 `useKeyboardDnD` hook (reuse target): `web/src/components/designer/useKeyboardDnD.ts`
- Story 4.4 patch playbook (reference for all defensive patterns): `_bmad-output/implementation-artifacts/4-4-assign-roles-to-menu-item.md` (Review Findings section)
- Native HTML5 DnD reference implementation: `web/src/components/designer/DesignerCanvas.tsx` + `web/src/components/designer/dnd.ts`
- Menu domain entity: `src/FormForge.Api/Domain/Entities/Menu.cs`
- Menu service contract (extend this): `src/FormForge.Api/Features/Menus/MenuService.cs`
- Menu admin endpoints (extend this): `src/FormForge.Api/Features/Menus/MenuAdminEndpoints.cs`
- Menu cache contract (call `InvalidateAsync`): `src/FormForge.Api/Features/Menus/MenuCache.cs`
- Story 4.1 migration (no migration needed in 4.5): `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260524054931_CreateMenusAndMenuRoleAssignments.cs`
- DbContext entity config (column name `sort_order` rationale): `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs:164-184`
- Existing menu admin route page (extend this): `web/src/routes/_app/admin/menus.tsx`
- Existing menu detail page (extend `SubMenusSection`): `web/src/routes/_app/admin/menus.$menuId.tsx`
- Existing mutations module (extend this): `web/src/features/admin/menus/menuAdminMutations.ts`
- Existing list query (reuse): `web/src/features/admin/menus/useMenusAdminQuery.ts`
- Existing children query (reuse for sub-menu reorder): `web/src/features/admin/menus/useMenuChildrenQuery.ts`
- HTTP client wrapper (use `httpClient.put`): `web/src/features/auth/httpClient.ts`
- `ApiError` shape (read `err.code` for code-specific routing): `web/src/lib/api/apiError.ts`

## Dev Agent Record

### Agent Model Used

claude-opus-4-7[1m]

### Debug Log References

- Backend test suite: 286/286 passing (273 baseline from Story 4.4 review close + 12 new Story 4.5 tests). Story spec estimated ~10; I added 12 to cover the UpdatedAt-no-op-rows and UpdatedAt-only-on-changed-rows invariants explicitly.
- Frontend test suite: 84/84 passing (78 baseline + 6 new). Story spec estimated ~80; landed at 84 with two extra smoke tests for the cross-scope rejection and the Escape-after-pickup paths.
- Lint: 32/32 baseline preserved (0 net new errors). Initial pass had +3 (one ZWSP literal in test file, one setState-in-effect for draft re-sync, one ref-write-inside-effect for insertedIdRef clear). Resolved by (a) `String.fromCharCode(0x200b)` in tests, (b) outer/inner component split with `key={fingerprint(serverList)}` so the inner useState initializer is the single source of truth (key-remount variant from Story 4.4 P6), (c) gating the focus effect on `pickedUp` transitioning to null and letting the hook's own pickUp/cancel handle the ref clear (matches `designer.$designerId.tsx` pattern).
- TypeScript: clean (`tsc --noEmit` exits 0).
- Production build: clean, ReorderableMenuList lazy-loaded by TanStack Router into its own chunk at 6.63 kB / gzipped 2.58 kB; `menus._menuId` chunk grew from 12.23 kB → 13.85 kB / 3.28 kB → 3.69 kB (sub-menu reorder mode toggle adds ~0.4 kB gzipped).

### Completion Notes List

- All 4 ACs implemented and verified by integration tests.
- AC-1 (drag-reorder persists via batch endpoint): backend `PUT /api/admin/menus/reorder` returns 204 with body `{ "items": [{ id, order }] }` semantics; UI optimistic mutation calls it with the full reordered scope; `GET /api/admin/menus` reflects the new order (verified by `ReorderMenus_TopLevelScope_PersistsNewOrder` and `ReorderMenus_SubMenuScope_PersistsNewOrder`).
- AC-2 (sub-menu items confined to their parent): UI rejects cross-scope drops via `dragSourceParentIdRef` comparison in `onDragOver`; backend independently enforces via `parentScopes.Distinct().Count() > 1` → 422 `REORDER_MIXED_SCOPES` (verified by `ReorderMenus_MixedScopes_Returns422`).
- AC-3 (keyboard reorder via `useKeyboardDnD`): hook reused unmodified per the Story 3.10 AC-6 contract; Space picks up, Arrow keys move, Space commits, Escape cancels and restores pre-pickup draft via `preMoveDraftRef`. `aria-live="polite"` region wired with the hook's ZWSP-toggling announcement state. Verified by `Space + ArrowDown + Space reorders rows and announces every step` and `Escape after pickup restores the pre-pickup draft and announces cancellation` vitest tests.
- AC-4 (≤5s navbar propagation): `cache.InvalidateAsync(ct)` called after `SaveChangesAsync`. `IMenuCache` is still `NoOpMenuCache` per Story 4.7's scope, so the hook is a no-op at runtime today — the contract is preserved.
- Envelope shape `{ items: [...] }` (not raw top-level array) per the Dev Notes deviation: matches `AssignMenuRolesRequest` precedent, registers cleanly with the `ValidationFilter<T>` pipeline, and leaves room for future fields. OpenAPI summary documents the actual body shape.
- Validator defensive ordering: `MaxItems` (256) gate runs **before** `Distinct`/per-element rules so a 1M-entry payload is rejected before allocating a same-size HashSet (Story 4.4 P6 pattern).
- Race-window catches: both wrapped `DbUpdateException` and bare `Npgsql.PostgresException` for SqlState 23503/23505 → `Conflict` (Story 4.4 P1 pattern).
- `InvalidIds` extension as a proper Guid array — tests assert membership via `JsonDocument.RootElement.GetProperty("invalidIds").EnumerateArray()`, not substring match (Story 4.4 P5 pattern).
- Re-sync local draft when server query refetches: implemented via the outer/inner-component split with `key={fingerprint(serverList)}` (Story 4.4 P6 key-remount variant). This pattern fully avoids the `react-hooks/set-state-in-effect` lint rule and is cleaner than the `useEffect` resync alternative.
- Decision to extract `ReorderableMenuList` into its own file rather than duplicating into both route files: the component is 250+ lines of complex DnD state; co-locating it twice would cost ~500 lines of identical code. The feature folder (`web/src/features/admin/menus/`) is the natural home — it already houses the mutations, queries, and types it depends on. The route files (`menus.tsx`, `menus.$menuId.tsx`) wrap it with their context (`parentId`) and a manage/reorder toggle. This is a tactical deviation from the "co-locate small sub-components" convention (`IconPickerSection`, `RoleAssignmentSection`) that applies only when the sub-component is genuinely small; for a 250-line DnD pipeline the duplication cost exceeds the convention benefit. The component has no `Route` export, so it's not a route file, and it ships as its own lazy-loaded chunk per TanStack Router's automatic code-splitting.
- No DB migration needed — `menus.sort_order` column + `idx_menus_sort_order` index were both shipped in Story 4.1's `20260524054931_CreateMenusAndMenuRoleAssignments.cs`.
- No domain event published — `MenuBindingCreated` requires a `DesignerId` not bound until Epic 5; per the story Dev Notes, the cache invalidation hook is event-style enough for the navbar TTL story (4.7).
- No SERIALIZABLE transaction wrapper — last-write-wins is acceptable for drag-reorder per architecture line 846 ("Optimistic only for: theme change, soft-delete row removal, drag-reorder").

### File List

**Backend (new):**
- `src/FormForge.Api/Features/Menus/Dtos/ReorderMenusRequest.cs` (new)
- `src/FormForge.Api/Features/Menus/Validators/ReorderMenusRequestValidator.cs` (new)

**Backend (modified):**
- `src/FormForge.Api/Features/Menus/MenuService.cs` — added `ReorderMenusOutcome` enum, `ReorderMenusResult` record, `IMenuService.ReorderMenusAsync` signature, and the implementation.
- `src/FormForge.Api/Features/Menus/MenuAdminEndpoints.cs` — added `PUT /reorder` route registration and `ReorderMenusHandler` private static method.
- `src/FormForge.Api/Program.cs` — registered `IValidator<ReorderMenusRequest>` next to the other menu validators.
- `src/FormForge.Api.Tests/Features/Menus/MenuIntegrationTests.cs` — added 12 Story 4.5 tests in a new `// ---------- PUT /api/admin/menus/reorder (Story 4.5) ----------` block.

**Frontend (new):**
- `web/src/features/admin/menus/ReorderableMenuList.tsx` (new) — outer (data-fetch + key-remount) and inner (draft state + pointer/keyboard DnD + save) components.
- `web/src/features/admin/menus/__tests__/ReorderableMenuList.test.tsx` (new) — 6 smoke tests: render order, save-disabled-on-unchanged, drag/drop payload, cross-scope drop rejection, keyboard pickup-move-commit, Escape cancel.

**Frontend (modified):**
- `web/src/features/admin/menus/menuAdminMutations.ts` — added `useReorderMenusMutation` with optimistic onMutate snapshot, onError rollback + per-`code` toast routing (MENUS_NOT_FOUND / REORDER_MIXED_SCOPES / REORDER_CONFLICT), onSettled refetch. Added `ReorderMenuItemPayload` interface. Added imports for `ApiError`, `MenuListItem`, `PagedResult`.
- `web/src/routes/_app/admin/menus.tsx` — added `mode: 'manage' | 'reorder'` state, reorder toggle button, and `<ReorderableMenuList parentId={null} />` mount.
- `web/src/routes/_app/admin/menus.$menuId.tsx` — extended `SubMenusSection` with `mode: 'manage' | 'reorder'` state, reorder toggle in the section header, `<ReorderableMenuList parentId={parentId} />` mount, and gated all manage-mode JSX (empty-state, table, create-form) behind `mode === 'manage'`.
- `web/src/lib/i18n/locales/en.json` — added 16 `admin.menus.*` keys for reorder: `reorderModeButton`, `manageModeButton`, `reorderInstructions`, `saveOrderButton`, `savingOrderButton`, `discardOrderButton`, `reorderSuccess`, `reorderError`, `reorderMenusNotFound`, `reorderMixedScopes`, `reorderConflict`, `reorderPickup`, `reorderMove`, `reorderCommit`, `reorderCancelled`, `reorderNoChanges`.

**Sprint status:**
- `_bmad-output/implementation-artifacts/sprint-status.yaml` — `4-5-reorder-menu-items-via-drag-and-drop`: ready-for-dev → in-progress → review.

## Review Findings

### Decision-Needed

- [x] [Review][Decision] D1 — `ReorderableMenuList` extracted to `web/src/features/admin/menus/ReorderableMenuList.tsx` — **ratified**. Co-location convention applies to small sub-components; 307-line DnD pipeline duplicated across two route files would cost ~600 lines of identical code. Feature folder is the correct home. Authorized tactical deviation.

### Patches

- [x] [Review][Patch] P1 — `fingerprint()` hashes only the ID sequence, not order values — outer component's `key={fingerprint(serverList)}` won't remount the inner component when a concurrent admin session changes order values without changing the ID sequence, leaving a stale draft. Fixed: `rows.map((r) => \`${r.id}:${r.order}\`).join(',')` [`web/src/features/admin/menus/ReorderableMenuList.tsx:14`]
- [x] [Review][Patch] P2 — Keyboard ArrowDown/ArrowUp announces `row.name` from the event-target `<li>` instead of the picked item's name. When focus has moved to a different row while an item is picked up, the announcement says the wrong item name. Fixed: `draft.find(m => m.id === picked.id)?.name ?? row.name` [`web/src/features/admin/menus/ReorderableMenuList.tsx`]
- [x] [Review][Patch] P3 — Sub-menu reorder fetched `useMenusAdminQuery(1, 256)` (global all-menus, no parentId filter) and filtered client-side. If total system menus > 256, sub-menus whose parent sorts after global position 256 are silently absent. Fixed: `ReorderableMenuList` now routes to `TopLevelReorderList` (uses `useMenusAdminQuery`) or `SubMenuReorderList` (uses `useMenuChildrenQuery(parentId, 1, 256)` — API-level filter). [`web/src/features/admin/menus/ReorderableMenuList.tsx`]
- [x] [Review][Patch] P4 — `void reorderMutation.mutateAsync(items)` in `onSave` discarded the rejected promise — fired a spurious `unhandledrejection` event even though `onError` handled the rollback and toast. Fixed: `reorderMutation.mutate(items)`. [`web/src/features/admin/menus/ReorderableMenuList.tsx`]
- [x] [Review][Patch] P5 — `ReorderMenus_OnlyMutatesUpdatedAtOnChangedRows` test asserted `Assert.NotNull(afterB)` — vacuous because `UpdatedAt` is set at creation time. Fixed: added `beforeB` capture before the request and `Assert.NotEqual(beforeB, afterB)` after. [`src/FormForge.Api.Tests/Features/Menus/MenuIntegrationTests.cs`]
- [x] [Review][Patch] P6 — Save button was not disabled when `kbdDnD.pickedUp !== null`. A failed mutation during active keyboard pickup left the pickup highlight live while the draft reverted. Fixed: `saveDisabled = reorderMutation.isPending || unchanged || kbdDnD.pickedUp !== null`. [`web/src/features/admin/menus/ReorderableMenuList.tsx`]

### Deferred

- [x] [Review][Defer] W1 — `useEffect` dependency includes `kbdDnD.insertedIdRef` (a stable ref object, not its `.current`) — works correctly as long as `useKeyboardDnD.commit()` writes `insertedIdRef.current` synchronously before calling `setPickedUp(null)`; tied to hook internals, not a Story 4.5 regression. [`web/src/features/admin/menus/ReorderableMenuList.tsx:720`] — deferred, pre-existing coupling to hook internals
- [x] [Review][Defer] W2 — `request.Items!` null-forgiving operator in `ReorderMenusHandler` — if the validation filter is misconfigured, service's `ArgumentNullException.ThrowIfNull` produces a 500. Established pattern matching `AssignRolesHandler`. [`src/FormForge.Api/Features/Menus/MenuAdminEndpoints.cs:421`] — deferred, pre-existing codebase pattern
- [x] [Review][Defer] W3 — `onMutate` optimistic update touches all `['admin', 'menus', ...]` cache entries including child-scoped queries for unrelated parentIds; `onSettled` invalidation corrects any drift. [`web/src/features/admin/menus/menuAdminMutations.ts:104`] — deferred, design choice; onSettled corrects
- [x] [Review][Defer] W4 — `ReorderMenus_TopLevelScope_PersistsNewOrder` asserts `body.Total == 3` — fragile if DB not isolated per test, but consistent with the existing test fixture patterns for this suite. [`src/FormForge.Api.Tests/Features/Menus/MenuIntegrationTests.cs:67`] — deferred, pre-existing fixture pattern

## Change Log

| Date       | Description                                                                                                   | Author   |
|------------|---------------------------------------------------------------------------------------------------------------|----------|
| 2026-05-25 | Story 4.5 created via `bmad-create-story`. Status → ready-for-dev. Comprehensive context bundle for the dev agent: backend reorder endpoint shape + validator defensive ordering + race-window catches; frontend optimistic mutation with cache rollback + native HTML5 DnD pattern adapted from DesignerCanvas + `useKeyboardDnD` hook reuse + scope-confinement client gate + i18n keys. Distilled Story 4.4 review patches into "Previous-Story Intelligence" section so the defensive patterns (MaxItems-before-Distinct, 23503/23505 catches, invalidIds JsonDocument assertions, re-sync local draft on refetch) apply proactively. | claude-opus-4-7 |
| 2026-05-25 | Story 4.5 code review via `bmad-code-review`. D1 (ReorderableMenuList file location) ratified as authorized tactical deviation. 6 patches applied: P1 fingerprint now includes order values; P2 ArrowDown announcement uses picked item's name not event-target row's name; P3 sub-menu scope switched from global all-menus query + client filter to `useMenuChildrenQuery(parentId, 1, 256)` (API-level filter, avoids >256-menu truncation); P4 `mutateAsync` → `mutate` to avoid spurious unhandledrejection; P5 test assertion strengthened to `Assert.NotEqual(beforeB, afterB)`; P6 Save disabled while keyboard pickup active. 4 deferred. Status → done. | claude-sonnet-4-6 |
| 2026-05-25 | Story 4.5 implemented via `bmad-dev-story`. Backend: `PUT /api/admin/menus/reorder` accepts `{ items: [{ id, order }] }` envelope (validator + DTO + endpoint + `MenuService.ReorderMenusAsync` with scope-invariant check, no-op UpdatedAt-on-unchanged, 23503/23505 → Conflict catches, `cache.InvalidateAsync` post-save). Frontend: `useReorderMenusMutation` with optimistic cache write + per-`code` error toasts + onSettled refetch, shared `ReorderableMenuList` component using native HTML5 DnD (new MIME `application/x-formforge-menu-reorder` isolated from designer-canvas) + `useKeyboardDnD` hook reused unmodified per Story 3.10 AC-6 contract. Outer/inner component split with `key={fingerprint(serverList)}` to satisfy `react-hooks/set-state-in-effect` (key-remount variant from Story 4.4 P6). 16 new i18n keys. 12 new backend integration tests (Unauthenticated/AsNonAdmin/TopLevelScope/SubMenuScope/MixedScopes/UnknownId/EmptyItems/DuplicateIds/TooManyItems/LiteralSegmentDoesNotCollideWithIdRoute/DoesNotMutateUpdatedAtOnUnchangedRows/OnlyMutatesUpdatedAtOnChangedRows). 6 new frontend smoke tests (render order, save-disabled, drag-drop payload, cross-scope rejection, keyboard pickup-move-commit, Escape cancel). Backend 286/286; frontend 84/84; lint 32 baseline preserved (0 net new); TypeScript clean; production build clean (ReorderableMenuList chunk 6.63 kB / gzipped 2.58 kB; menus._menuId 12.23 → 13.85 kB / 3.28 → 3.69 kB). Status → review. | claude-opus-4-7 |
