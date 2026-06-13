# Story 4.6: Activate and Deactivate Menu Items

Status: done

## Story

As a Platform Admin,
I want to toggle a Menu Item's `isActive` flag,
so that I can hide items without deleting them.

## Acceptance Criteria

1. **Given** a Menu Item with `isActive: true`, **When** I set it to `false` via the admin UI, **Then** the toggle persists server-side and the item is excluded from the navbar for all users (including Platform Admins browsing in normal view). _(Navbar rendering is Story 4.7; this story ensures the flag is correctly persisted and the admin UI exposes the toggle.)_

2. **Given** a Platform Admin in Admin > Menus, **When** they view the menus list, **Then** inactive items remain visible (no isActive filter on the admin endpoint) and the inline toggle button allows quick activate/deactivate without opening the full edit form.

## Tasks / Subtasks

- [x] Task 1 — Backend: DTO + Validator (AC: 1, 2)
  - [x] Create `src/FormForge.Api/Features/Menus/Dtos/ToggleMenuActiveRequest.cs` — positional record `ToggleMenuActiveRequest(bool? IsActive)`. Nullable bool so a missing `isActive` key deserializes to null → validator NotNull rule fires 422 instead of silently treating it as `false`. Mirrors `AssignMenuRolesRequest(IReadOnlyList<Guid>? RoleIds)` nullable-envelope pattern.
  - [x] Create `src/FormForge.Api/Features/Menus/Validators/ToggleMenuActiveRequestValidator.cs` — `AbstractValidator<ToggleMenuActiveRequest>` with a single `RuleFor(x => x.IsActive).NotNull()` rule. Registered by DI scan (no manual registration needed — same as all other validators).

- [x] Task 2 — Backend: Service method (AC: 1)
  - [x] Add `internal enum ToggleMenuActiveOutcome { Success, NotFound }` and `internal sealed record ToggleMenuActiveResult(ToggleMenuActiveOutcome Outcome)` to `MenuService.cs` (at top with the other outcome enums).
  - [x] Add `Task<ToggleMenuActiveResult> ToggleMenuActiveAsync(Guid id, bool isActive, CancellationToken ct)` to `IMenuService`.
  - [x] Implement `ToggleMenuActiveAsync` in `MenuService`: (1) `ArgumentNullException.ThrowIfNull` is not needed for a bool, skip; (2) `FirstOrDefaultAsync(m => m.Id == id, ct)` → return `NotFound` if null; (3) set `menu.IsActive = isActive`; (4) set `menu.UpdatedAt = DateTimeOffset.UtcNow`; (5) `SaveChangesAsync(ct)`; (6) `cache.InvalidateAsync(ct)` — same post-mutation cache bust as every other write; (7) return `Success`. No FK/PK constraint catches needed — this mutation only touches a single row's bool column, no FK relationships involved.

- [x] Task 3 — Backend: Endpoint registration (AC: 1, 2)
  - [x] In `MenuAdminEndpoints.MapMenuAdminEndpoints`, add after the `/reorder` route and before the `/upload-icon` route:
    ```csharp
    group.MapPatch("/{id:guid}/active", ToggleActiveHandler)
         .AddValidationFilter<ToggleMenuActiveRequest>()
         .WithSummary("Activate or deactivate a menu item. Body: { isActive: bool }.")
         .Produces(StatusCodes.Status204NoContent)
         .Produces(StatusCodes.Status404NotFound)
         .Produces(StatusCodes.Status422UnprocessableEntity);
    ```
  - [x] Implement `ToggleActiveHandler(Guid id, ToggleMenuActiveRequest request, IMenuService menuService, CancellationToken ct)` — `ArgumentNullException.ThrowIfNull(request)` + `ArgumentNullException.ThrowIfNull(menuService)`. `isActive` is non-null after validation filter: `request.IsActive!.Value`. Switch on outcome → `NotFound` returns `MenuNotFoundProblem()` (reuse the existing helper), `Success` returns `Results.NoContent()`.
  - [x] **Route collision check**: `/{id:guid}/active` is a Guid-constrained path + literal segment. ASP.NET routing prefers literals over parameter-constrained segments; within a `{id:guid}` prefix, `/active` is a distinct literal sibling to `/roles` — no collision. "active" is not a valid GUID so the existing `/{id:guid}` PUT route will never capture it.

- [x] Task 4 — Backend: Integration tests (AC: 1, 2)
  - [x] `ToggleMenuActive_Unauthenticated_Returns401` — PATCH `/api/admin/menus/{guid}/active` without auth → 401.
  - [x] `ToggleMenuActive_AsNonAdmin_Returns403` — PATCH with viewer JWT → 403.
  - [x] `ToggleMenuActive_ToInactive_Returns204AndPersists` — create menu (isActive defaults to true), PATCH `{ isActive: false }` → 204, GET menu → assert `isActive = false`.
  - [x] `ToggleMenuActive_ToActive_Returns204AndPersists` — seed menu with `IsActive = false`, PATCH `{ isActive: true }` → 204, GET menu → assert `isActive = true` and `UpdatedAt` is non-null (proves `UpdatedAt` is stamped).
  - [x] `ToggleMenuActive_NotFound_Returns404` — PATCH with random Guid → 404, assert `code: "MENU_NOT_FOUND"` in response body.
  - [x] Test helper: use the existing `CreateMenuViaApiAsync` helper for create, and the existing `LoginAsync("admin@example.com", "Password1!")` pattern. Direct DB seed for the `IsActive = false` scenario (same as the UpdatedAt tests in Story 4.5).

- [x] Task 5 — Frontend: Add `patch` to `httpClient` (AC: 1)
  - [x] In `web/src/features/auth/httpClient.ts`, the exported `httpClient` object is missing `patch`. The `HttpMethod` union type already includes `'PATCH'` (line 5). Add one line to the export object:
    ```ts
    patch: <T>(path: string, body?: unknown) => request<T>('PATCH', path, body),
    ```
    Place it after `put` and before `delete`, keeping the object alphabetically ordered (`get`, `post`, `put`, `patch`, `delete`) — or logically by HTTP verb order. Either is fine; match the existing style.

- [x] Task 6 — Frontend: Mutation hook (AC: 1)
  - [x] In `web/src/features/admin/menus/menuAdminMutations.ts`, add `useToggleMenuActiveMutation`:
    ```ts
    export function useToggleMenuActiveMutation() {
      const queryClient = useQueryClient()
      const { t } = useTranslation()
      return useMutation({
        mutationFn: ({ menuId, isActive }: { menuId: string; isActive: boolean }) =>
          httpClient.patch<void>(`/api/admin/menus/${menuId}/active`, { isActive }),
        onSuccess: (_data, { isActive }) => {
          invalidateAllMenus(queryClient)
          toast.success(isActive ? t('admin.menus.activateSuccess') : t('admin.menus.deactivateSuccess'))
        },
        onError: () => {
          toast.error(t('admin.menus.toggleActiveError'))
        },
      })
    }
    ```
  - [x] **Pessimistic mutation** — activation/deactivation is NOT in the optimistic allowlist (architecture: "Optimistic only for: theme change, soft-delete row removal, drag-reorder"). No `onMutate`/rollback needed.
  - [x] The `onSuccess` handler invalidates `MENUS_ADMIN_QUERY_KEY` via the existing `invalidateAllMenus(queryClient)` helper — the same helper used by `useDeleteMenuMutation`, `useUpdateMenuMutation`, etc. No `MENU_DETAIL_QUERY_KEY` invalidation needed for a list-level toggle; detail page already has isActive on the full update form which re-fetches on navigation.
  - [x] The mutation accepts `{ menuId, isActive }` as a single payload object (not two separate parameters) so TanStack Query's `variables` typing is clean.

- [x] Task 7 — Frontend: Inline toggle in top-level menus list (AC: 2)
  - [x] In `web/src/routes/_app/admin/menus.tsx`, add `useToggleMenuActiveMutation` to the import from `menuAdminMutations`.
  - [x] Instantiate the mutation at the `MenusPage` level: `const toggleActiveMutation = useToggleMenuActiveMutation()`.
  - [x] In the table, the existing `isActive` column (line 107–117) currently shows a static badge. **Replace** the static badge cell with a `<button>` that:
    - Shows `t('admin.menus.deactivateButton')` when `menu.isActive` is true, and `t('admin.menus.activateButton')` when false.
    - `onClick` calls `toggleActiveMutation.mutate({ menuId: menu.id, isActive: !menu.isActive })`.
    - `disabled={toggleActiveMutation.isPending}` — disables all toggle buttons while any mutation is in flight (single shared mutation instance).
    - Keep the existing color-coded badge style as the button's visual (or add a small `<span>` badge + button side by side — match the project's inline action style from the Delete button pattern).
  - [x] **Add an "Actions" `<th>` column header** to the table head to accommodate the toggle button. The table currently has 4 columns (Name, Order, isActive, Created). Add a 5th "Actions" column.

- [x] Task 8 — Frontend: Inline toggle in sub-menus list (AC: 2)
  - [x] In `web/src/routes/_app/admin/menus.$menuId.tsx`, in `SubMenusSection` (around line 465–490), the sub-menu table currently shows the isActive status as plain text (line 485). Apply the same toggle button pattern as Task 7.
  - [x] Import `useToggleMenuActiveMutation` from `menuAdminMutations` at the top of the file.
  - [x] Instantiate `const toggleActiveMutation = useToggleMenuActiveMutation()` inside `SubMenusSection`.
  - [x] Replace the static `{child.isActive ? t('admin.menus.isActiveLabel') : t('admin.menus.isInactiveLabel')}` text cell with the same toggle button.
  - [x] Add the "Actions" `<th>` to the sub-menu table thead.

- [x] Task 9 — Frontend: i18n keys (AC: 1, 2)
  - [x] In `web/src/lib/i18n/locales/en.json`, add 5 keys inside the `admin.menus` object. Place them after `rolesAssignError` (end of Story 4.4 block) and before `reorderModeButton` (Story 4.5 block):
    ```json
    "activateButton": "Activate",
    "deactivateButton": "Deactivate",
    "activateSuccess": "Menu item activated",
    "deactivateSuccess": "Menu item deactivated",
    "toggleActiveError": "Failed to update active status"
    ```

## Dev Notes

### What Already Exists — Do NOT Recreate

- `Menu.IsActive` entity property — already on the entity, defaults to `true` (Menu.cs:9).
- `isActive` in ALL existing DTOs — `CreateMenuRequest`, `UpdateMenuRequest`, `MenuResponse`, `MenuListItem` all already carry `isActive` (Dtos/ directory).
- `UpdateMenuAsync` already sets `menu.IsActive = request.IsActive` (MenuService.cs:139) — the full update path already handles isActive. This story adds a dedicated lightweight toggle path so the admin doesn't need the full payload.
- Admin list `GetMenusAsync` already returns ALL menus (no isActive filter on the admin endpoint — the admin must see inactive items per AC-2). **Do not add an isActive filter** to `GetMenusAsync`.
- `isActive` badge already rendered in `menus.tsx` table (lines 107–117) — this story replaces the static badge with an action button.
- `isActive` text already rendered in sub-menu table in `menus.$menuId.tsx` (line 485) — same replacement.
- `isActive` checkbox already exists in the full edit form on the detail page (`menus.$menuId.tsx`) — leave that untouched.
- No DB migration needed: `is_active BOOLEAN NOT NULL DEFAULT TRUE` was added in `20260524054931_CreateMenusAndMenuRoleAssignments.cs` (Story 4.1).

### Why a Dedicated PATCH Endpoint (Not the Full PUT)

The existing `PUT /{id}` requires a full `UpdateMenuRequest` payload (name + order + icon + isActive). The admin menus list only has `MenuListItem` (no icon, no full detail), making a list-level inline toggle require a detail fetch + round-trip before calling PUT. A dedicated `PATCH /{id:guid}/active` accepts only `{ isActive: bool }` and is idempotent — simpler, more efficient, semantically correct.

### Why PATCH not Two Separate PUT Routes

The User feature uses `PUT /{id}/deactivate` and `PUT /{id}/reactivate` because user deactivation has a special side effect (refresh token revocation). Menu activation/deactivation has no such side effect — it's a single bool field toggle. A single PATCH with an explicit `{ isActive: bool }` body is simpler and more transparent.

### httpClient PATCH Support

`httpClient` at `web/src/features/auth/httpClient.ts` does NOT currently expose `patch`. The `HttpMethod` union type already includes `'PATCH'` (line 5) and the underlying `request<T>()` function handles any `HttpMethod`. Add `patch: <T>(path: string, body?: unknown) => request<T>('PATCH', path, body)` to the exported object. One line change.

### Validator Null-Bool Pattern (Task 1)

`ToggleMenuActiveRequest(bool? IsActive)` mirrors the `AssignMenuRolesRequest(IReadOnlyList<Guid>? RoleIds)` nullable-key pattern. A missing `isActive` key in the JSON body deserializes to `null` (not `false`) because the record property is `bool?`. The `NotNull()` rule fires before the handler, returning 422 with `ValidationProblemDetails`. The handler then reads `request.IsActive!.Value` safely (null-forgiving is safe here because the filter already checked it — same as `request.Items!` in `ReorderMenusHandler`).

### Service Method Simplicity

`ToggleMenuActiveAsync` is the simplest service method in the feature — no FK lookups, no batch operations, no scope checks, no 23503/23505 catches (the mutation only touches a single row's bool column). Pattern: fetch → null check → set two fields → save → invalidate → return outcome. Always stamp `UpdatedAt` (unlike `ReorderMenusAsync` which skips no-op rows; here the admin intent is always a deliberate state change).

### Route Placement in MenuAdminEndpoints

Add the new `MapPatch` call between `MapPut("/reorder", ...)` and `MapPost("/upload-icon", ...)`. This keeps mutation routes grouped together. Comment the route with `// Story 4.6`.

### Test Patterns (Task 4)

Follow the 5-test per-verb pattern established in Story 4.4 (Unauthenticated/NonAdmin + positive cases + negative cases). Use `CreateMenuViaApiAsync` helper for the create step (already in the test file). The `ToActive_Returns204AndPersists` test seeds a menu directly into the DB with `IsActive = false` (same EF direct-seed pattern used in `GetMenus_WithItems_ReturnsOrderedBySortOrderThenId`). Do NOT use `CreateMenuViaApiAsync` for seeding inactive menus — the endpoint defaults `isActive: true`.

For `ToggleMenuActive_NotFound_Returns404`: parse the response as `JsonDocument` and assert `code == "MENU_NOT_FOUND"` using the `JsonDocument.RootElement.GetProperty("code").GetString()` pattern (established in `UnknownId_Returns422WithInvalidIds` in the same test class).

### Frontend Mutation Design (Task 6)

`useToggleMenuActiveMutation()` takes no parameters (unlike `useUpdateMenuMutation(menuId: string)`) because the menuId is supplied per-call in the mutation payload. This avoids instantiating separate hook instances per row. One shared instance at the parent page level handles all rows — `isPending` naturally blocks the entire table while any toggle is in flight, which is the correct UX (prevents race conditions from rapid sequential toggles on different rows).

### Pessimistic Pattern Rationale

Architecture line 846: "Optimistic only for: theme change, soft-delete row removal, drag-reorder." Activation/deactivation is not in the allowlist. Use pessimistic: no `onMutate`/rollback. `invalidateAllMenus` in `onSuccess` re-fetches the list, which is fast (single page of data).

### Previous Story Intelligence (Story 4.5)

Key patterns to carry forward:
- `ArgumentNullException.ThrowIfNull(request)` + `ArgumentNullException.ThrowIfNull(menuService)` at the top of every handler (P1 from 4.4 review).
- Always call `cache.InvalidateAsync(ct)` after a successful `SaveChangesAsync` call (established pattern for all menu mutations).
- Handler null-forgiving (`request.IsActive!.Value`) is safe only after `AddValidationFilter<T>()` has validated it — same pattern as `request.Items!` in `ReorderMenusHandler` (MenuAdminEndpoints.cs:227), reviewed and accepted as a pre-existing codebase pattern (Story 4.5 deferred W2).
- Test assertion: parse `code` from problem-detail JSON body when asserting 404 (Story 4.4 P5 pattern).
- `invalidateAllMenus` helper (line 11–13 of `menuAdminMutations.ts`) is the correct invalidation path — all list mutations use it.

### Project Structure Notes

**Backend new files:**
- `src/FormForge.Api/Features/Menus/Dtos/ToggleMenuActiveRequest.cs`
- `src/FormForge.Api/Features/Menus/Validators/ToggleMenuActiveRequestValidator.cs`

**Backend modified files:**
- `src/FormForge.Api/Features/Menus/MenuService.cs` — add outcome enum, result record, interface method, implementation
- `src/FormForge.Api/Features/Menus/MenuAdminEndpoints.cs` — add PATCH route + handler
- `src/FormForge.Api.Tests/Features/Menus/MenuIntegrationTests.cs` — add 5 tests

**Frontend modified files:**
- `web/src/features/auth/httpClient.ts` — add `patch` method
- `web/src/features/admin/menus/menuAdminMutations.ts` — add `useToggleMenuActiveMutation`
- `web/src/routes/_app/admin/menus.tsx` — add toggle button column
- `web/src/routes/_app/admin/menus.$menuId.tsx` — add toggle button to sub-menu table
- `web/src/lib/i18n/locales/en.json` — add 5 keys

**No migration needed** — `is_active` column already shipped in Story 4.1's `20260524054931_CreateMenusAndMenuRoleAssignments.cs`.

### Expected Test Counts

- Backend: 286 (baseline) + 5 = **291 total**
- Frontend: 84 (baseline, no new frontend tests for this story — inline toggle is too simple to test via smoke tests and is covered by backend integration tests)

### References

- Architecture: Route group pattern (section 3.5, line 470–481)
- Architecture: Optimistic mutation allowlist (line 846)
- Architecture: Error envelope shape (section 3.1, lines 426–440)
- Architecture: i18n key naming pattern (section 4.8, lines 609–613)
- Previous story: Story 4.5 dev notes — validator null pattern, handler null-forgiving, cache invalidation
- Previous story: Story 4.4 review P5 — JSON problem-detail assertion pattern in tests
- `MenuService.cs:139` — existing `UpdateMenuAsync` sets `menu.IsActive = request.IsActive`
- `MenuAdminEndpoints.cs:261–269` — `MenuNotFoundProblem()` helper to reuse in `ToggleActiveHandler`
- `menuAdminMutations.ts:11–13` — `invalidateAllMenus` helper to reuse in new mutation
- `httpClient.ts:5` — `HttpMethod` type already includes `'PATCH'`

## Dev Agent Record

### Agent Model Used

claude-opus-4-7

### Debug Log References

- Initial test pass had 3 failures with HTTP 500 — root cause: `IValidator<ToggleMenuActiveRequest>` not resolved by DI. Story Dev Notes (Task 1) incorrectly claimed validators are picked up by a DI scan; this codebase actually registers each validator explicitly in `Program.cs`. Added manual registration alongside the other Menu validators (Program.cs:136) and all 5 ToggleMenuActive tests pass.
- Frontend test run shows 82/84 — 2 failures in `ReorderableMenuList.test.tsx` are pre-existing on HEAD f5f7f47 (verified by stashing the Story 4.6 changes and re-running; the same 2 fail). They originate from the Story 4.5 review patch that changed source from `mutateAsync(items)` to `mutate(items)` (per the "P4" comment in `ReorderableMenuList.tsx:250-253`) without updating the test mock — outside Story 4.6 scope.

### Completion Notes List

- Backend: 286 → 291 tests (target met). All 5 new tests cover the 5 AC paths (401/403/204-to-inactive/204-to-active+UpdatedAt-stamped/404-with-MENU_NOT_FOUND code).
- Validator registration in Program.cs:136 is the one deviation from the story's Task 1 plan. The "DI scan" wording in the story was wrong; manual registration is the codebase convention (confirmed across CreateMenuRequest, UpdateMenuRequest, AssignMenuRolesRequest, ReorderMenusRequest).
- `ToggleActiveHandler` placed between `MenuNotFoundProblem()` helper and `UploadIconHandler` (one structural slot up from where the story suggested), grouping it with the other write handlers (Reorder/AssignRoles/Delete) rather than next to the upload handler.
- Removed the stale `<td />` cell that was paired with `colSpan={4}` in the empty-state row of `menus.tsx`; updated `colSpan={5}` to match the new 5-column header without the trailing empty cell.
- AC-1 (toggle persists, navbar exclusion deferred to Story 4.7): covered by `ToggleMenuActive_ToInactive_Returns204AndPersists` and `ToggleMenuActive_ToActive_Returns204AndPersists`. The admin endpoint correctly does not filter by `isActive` (verified at MenuService.cs:43-63), so AC-2's "inactive items remain visible in admin" is satisfied by the existing list code; only the inline toggle button was new.
- Frontend mutation is pessimistic per architecture line 846 — no optimistic update wired in. `onSuccess` invalidates `MENUS_ADMIN_QUERY_KEY` via the existing `invalidateAllMenus` helper.
- `i18n` key `admin.menus.actionsLabel` already exists at `en.json:42` (in the users block) and was reused for the new column header — story spec only mentioned the 5 new keys, but the table column header benefits from the existing key.
- Production build: `menus` chunk 6.00 kB gzipped 1.89 kB (was ~5.5 kB, +~0.5 kB from toggle column); `menus._menuId` chunk 14.00 kB gzipped 3.65 kB (was 13.85/3.69, virtually unchanged — sub-menu toggle column adds <100 bytes gzipped).
- Lint: 32 errors at baseline (matches Story 4.5 baseline; 0 net new from Story 4.6).

### File List

**Created:**
- `src/FormForge.Api/Features/Menus/Dtos/ToggleMenuActiveRequest.cs`
- `src/FormForge.Api/Features/Menus/Validators/ToggleMenuActiveRequestValidator.cs`

**Modified (backend):**
- `src/FormForge.Api/Features/Menus/MenuService.cs` — added `ToggleMenuActiveOutcome` enum, `ToggleMenuActiveResult` record, interface method, implementation
- `src/FormForge.Api/Features/Menus/MenuAdminEndpoints.cs` — added `PATCH /{id:guid}/active` route registration + `ToggleActiveHandler`
- `src/FormForge.Api/Program.cs` — registered `IValidator<ToggleMenuActiveRequest>` in DI
- `src/FormForge.Api.Tests/Features/Menus/MenuIntegrationTests.cs` — added 5 PATCH active integration tests

**Modified (frontend):**
- `web/src/features/auth/httpClient.ts` — added `patch` method to the `httpClient` export
- `web/src/features/admin/menus/menuAdminMutations.ts` — added `useToggleMenuActiveMutation`
- `web/src/routes/_app/admin/menus.tsx` — added Actions column with toggle button, removed stale `<td />` in empty-state row, updated `colSpan` 4 → 5
- `web/src/routes/_app/admin/menus.$menuId.tsx` — added Actions column with toggle button in `SubMenusSection`
- `web/src/lib/i18n/locales/en.json` — added 5 keys: `activateButton`, `deactivateButton`, `activateSuccess`, `deactivateSuccess`, `toggleActiveError`

### Change Log

| Date       | Author      | Change                                                                                                                                                            |
| ---------- | ----------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 2026-05-25 | claude-opus-4-7 | Implemented Story 4.6 — `PATCH /api/admin/menus/{id}/active` endpoint, service method, validator, 5 integration tests, frontend mutation hook, inline toggle buttons in top-level and sub-menu admin lists, 5 i18n keys. Status → review. |
| 2026-05-25 | claude-opus-4-7 | Code review (3-reviewer parallel pass — Blind Hunter + Edge Case Hunter + Acceptance Auditor): 24 raw → 16 unique → 5 patch / 6 defer / 9 dismiss. HIGH: missing `admin.menus.actionsLabel` key (column header renders literal). MED: no validator-422 test, no 404 handling on FE mutation, missing aria-label on toggle button. LOW: ToInactive test missing UpdatedAt assertion. |
| 2026-05-25 | claude-opus-4-7 | Applied all 5 review patches: P1 add `admin.menus.actionsLabel` i18n key, P2 add `_MissingIsActive_Returns422` Theory test (291 → 293 backend tests), P3 add per-code 404 routing + list invalidation in `useToggleMenuActiveMutation.onError`, P4 add `aria-label` + 2 new aria i18n keys on both toggle buttons, P5 add `UpdatedAt` assertion to ToInactive test. Backend 293/293, TypeScript clean, lint 32 baseline preserved. Status → done. |

## Review Findings

### Patch

- [x] [Review][Patch][HIGH] `admin.menus.actionsLabel` i18n key does not exist — `web/src/lib/i18n/locales/en.json` defines `actionsLabel` only under `admin.users` (line 42); under `admin.menus` (lines 125–207) it is absent. `i18next` is configured without `parseMissingKeyHandler` (`web/src/lib/i18n/config.ts`) and does NOT cross-flow between sibling namespaces, so `t('admin.menus.actionsLabel')` in `web/src/routes/_app/admin/menus.tsx:87` and `web/src/routes/_app/admin/menus.$menuId.tsx:475` will render the literal string `admin.menus.actionsLabel` as the column header in both admin tables. **APPLIED:** added `"actionsLabel": "Actions"` to the `admin.menus` block at `en.json:127`.
- [x] [Review][Patch][MED] No 422 test for missing/null `isActive` body — DTO uses `bool? IsActive` specifically so a missing `isActive` key → NotNull rule → 422, but no test sends `{}` or `{ "isActive": null }`. **APPLIED:** added `ToggleMenuActive_MissingIsActive_Returns422` Theory test in `MenuIntegrationTests.cs` (after `_NotFound_Returns404`) with two InlineData rows covering both payloads. Backend 291 → 293.
- [x] [Review][Patch][MED] Frontend toggle has no 404 handling — `useToggleMenuActiveMutation.onError` was a single generic toast. **APPLIED:** branched on `ApiError.code === 'MENU_NOT_FOUND'` → `notFoundError` toast + `invalidateAllMenus(queryClient)` to clear the stale row. Mirrors `useReorderMenusMutation` per-code routing pattern (`menuAdminMutations.ts:167-176`).
- [x] [Review][Patch][MED] Toggle button missing accessible name binding action to menu — bare "Activate" / "Deactivate" button text gave screen-reader users N identical buttons with no row context. **APPLIED:** added 2 i18n keys (`activateButtonAria` / `deactivateButtonAria` with `{{name}}` interpolation) and wired `aria-label={t('...ButtonAria', { name: menu.name })}` on both toggle buttons in `menus.tsx:130-135` and `menus.$menuId.tsx:496-501`.
- [x] [Review][Patch][LOW] `ToInactive_Returns204AndPersists` test does not assert `UpdatedAt` was stamped — asymmetric coverage with the `ToActive` test. **APPLIED:** added `Assert.NotNull(body.UpdatedAt)` after the `Assert.False(body.IsActive)` line in the ToInactive test (`MenuIntegrationTests.cs:1214`).

### Deferred

- [x] [Review][Defer][LOW] Sub-menu Actions cell duplicates state-text cell — `web/src/routes/_app/admin/menus.$menuId.tsx:487-489` and `490-502` show both "Active/Inactive" text AND the verb button ("Deactivate"/"Activate"). Top-level keeps a colored badge (justified UX); sub-menu is plain duplication. Pre-existing pattern divergence; UX call out of scope for this review.
- [x] [Review][Defer][LOW] Shared `isPending` blocks every toggle button with no per-row spinner — `web/src/routes/_app/admin/menus.tsx:125-135` and `menus.$menuId.tsx:491-501`. All toggle buttons disable during any in-flight mutation, with no per-row indicator (no spinner, no "Saving…", no aria-live). Explicitly documented as "correct UX" in Dev Notes (race-prevention via shared instance); polish work for a later UI pass.
- [x] [Review][Defer][LOW] Rapid double-click race on closure-captured `isActive` — `web/src/routes/_app/admin/menus.tsx:125-136`. Double-click before the `isPending` commit can fire two `mutate({ isActive: !menu.isActive })` calls reading the same closure value; if cache invalidates between clicks, the second call would set the value back. Theoretical with current pessimistic + shared-isPending design; per-row mutation tracking is the real fix but out of scope.
- [x] [Review][Defer][LOW] No idempotency / no-op test (PATCH same value still bumps `UpdatedAt`) — `MenuService.cs:355-373` unconditionally stamps `UpdatedAt`. The `Reorder` feature has a `DoesNotMutateUpdatedAtOnUnchangedRows` / `OnlyMutatesUpdatedAtOnChangedRows` symmetric pair locking the contract; the toggle path's "always stamp" intent is documented but not test-locked. Add `ToggleMenuActive_OnNoOp_StillBumpsUpdatedAt` in a future hardening pass.
- [x] [Review][Defer][LOW] Story body still asserts validator "DI scan" pattern that doesn't exist — `_bmad-output/implementation-artifacts/4-6-activate-and-deactivate-menu-items.md:27, 218`. Task 1 wording "Registered by DI scan (no manual registration needed)" contradicts the Dev Agent Record's confession (validators must be registered manually in `Program.cs`). Doc artifact, not code; risk is templating a future story off this one. Sweep with the next story-template cleanup pass.
- [x] [Review][Defer][LOW] No frontend test for `useToggleMenuActiveMutation` or `httpClient.patch` — story Task 7 explicitly scopes out frontend tests for the simple inline toggle ("inline toggle is too simple to test via smoke tests"). The `onSuccess` toast branching on `variables.isActive` and the new `patch` HTTP method would benefit from one smoke test; defer to a frontend-coverage backfill.
