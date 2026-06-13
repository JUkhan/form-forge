# Story 4.2: Create Sub-menu Items

Status: done

## Story

As a Platform Admin,
I want to nest a Menu Item one level under a parent,
So that I can group related data modules under a section heading.

## Acceptance Criteria

**AC-1 — Create sub-menu under a top-level parent**
Given an existing top-level Menu Item
When I POST `/api/admin/menus` with `{ name, order, parentId: <topLevelId> }`
Then a sub-menu item is created under that parent
And the response is HTTP 201 with the created `MenuResponse` (parentId populated)

**AC-2 — 3rd-level nesting rejected**
Given a sub-menu item (its own `parentId` is non-null)
When I attempt to POST another item with `parentId` pointing to that sub-menu
Then the response is HTTP 422 with `code: "MAX_MENU_DEPTH_EXCEEDED"`

**AC-3 — Unknown parent rejected**
Given I POST with a `parentId` that does not correspond to any existing Menu Item
When the server processes the request
Then the response is HTTP 422 with `code: "MENU_PARENT_NOT_FOUND"`

**AC-4 — Sub-menus visible on parent detail page**
Given a top-level Menu Item
When an Admin views its detail page
Then a "Sub-menu Items" section lists all immediate children (name, order, isActive)
And an inline form lets the Admin add a new sub-menu item (name + order; parentId is implicit)

**AC-5 — Sub-menu detail page shows parent link**
Given a sub-menu item
When an Admin views its detail page
Then a "← View Parent Menu" link navigates to the parent's detail page

## Tasks / Subtasks

- [x] Task 1: Backend — extend `CreateMenuRequest` DTO (AC-1, AC-2, AC-3)
  - [x] Update `src/FormForge.Api/Features/Menus/Dtos/CreateMenuRequest.cs`
    - Added `Guid? ParentId = null` as optional positional parameter on the existing `record` (preserves the established record idiom in this file; backward-compatible with all existing call sites; JSON deserializes by property name)

- [x] Task 2: Backend — depth validation in `MenuService` (AC-1, AC-2, AC-3)
  - [x] Update `src/FormForge.Api/Features/Menus/MenuService.cs`
    - Added `ParentNotFound` and `MaxDepthExceeded` to the `CreateMenuOutcome` enum
    - In `CreateMenuAsync`: pre-construction guard loads parent via `AsNoTracking()` when `request.ParentId.HasValue`; returns `ParentNotFound` if parent missing, `MaxDepthExceeded` if `parent.ParentId.HasValue` (enforcing 2-level cap)
    - Replaced hardcoded `ParentId = null` with `ParentId = request.ParentId` on the new `Menu` entity

- [x] Task 3: Backend — map new outcomes in endpoint (AC-2, AC-3)
  - [x] Update `src/FormForge.Api/Features/Menus/MenuAdminEndpoints.cs`
    - Added `ParentNotFound` → 422 `MENU_PARENT_NOT_FOUND` and `MaxDepthExceeded` → 422 `MAX_MENU_DEPTH_EXCEEDED` arms in `CreateMenuHandler` switch (using `StringComparer.Ordinal` dictionary, matching the existing 404/409 patterns)

- [x] Task 4: Frontend — extend `CreateMenuRequest` type (AC-1)
  - [x] Update `web/src/features/menu/types.ts`
    - Added `parentId?: string | null` to the `CreateMenuRequest` interface

- [x] Task 5: Frontend — sub-menus section on detail page (AC-4, AC-5)
  - [x] Update `web/src/routes/_app/admin/menus.$menuId.tsx`
    - Top-level branch (`menu.parentId === null`): renders new `<SubMenusSection parentId={menuId} />` after the delete button. SubMenusSection uses `useMenusAdminQuery(1, 100)`, filters `.data.data` by `parentId === parentId`, renders a Name/Order/Active table (each name linking to the child detail page), shows `t('admin.menus.noSubMenus')` empty state, and provides an inline create form (name + order, local state) calling `useCreateMenuMutation` with `{ name, order, parentId }`. Inline error handler maps 422 `MENU_PARENT_NOT_FOUND` and 422 `MAX_MENU_DEPTH_EXCEEDED` to their respective i18n keys; other errors fall back to `saveError`.
    - Sub-menu branch (`menu.parentId !== null`): renders `← View Parent Menu` Link in the page header (next to "Back to Menus"). No SubMenusSection — enforces the 2-level UI cap.

- [x] Task 6: Frontend — i18n strings (AC-2, AC-3, AC-4, AC-5)
  - [x] Update `web/src/lib/i18n/locales/en.json`
    - Added 8 keys to `admin.menus`: `subMenusTitle`, `noSubMenus`, `addSubMenuButton`, `addingSubMenuButton`, `viewParent`, `parentNotFound`, `maxDepthExceeded`, `createSubMenuSuccess`

- [x] Task 7: Integration tests (AC-1, AC-2, AC-3)
  - [x] Update `src/FormForge.Api.Tests/Features/Menus/MenuIntegrationTests.cs`
    - Added 3 new tests + 1 helper (`CreateSubMenuViaApiAsync`):
      - `CreateMenu_WithValidParent_Returns201WithParentIdSet`
      - `CreateMenu_WithUnknownParent_Returns422ParentNotFound`
      - `CreateMenu_WithSubMenuAsParent_Returns422MaxDepthExceeded`

### Review Findings (2026-05-24)

- [x] [Review][Patch] Add backend `?parentId=` filter + paginated children query in `SubMenusSection` (resolves former [Decision] on the 100-child cap) — extend `GET /api/admin/menus` with an optional `parentId` query param (validated as Guid, applied as `Where(m => m.ParentId == parentId)`), add an `useMenuChildrenQuery(parentId, page, pageSize)` hook, replace the client-side filter in `SubMenusSection` with that hook + local `page` state + Next/Prev controls. Mirror the existing list-endpoint contract (PagedResult, OrderBy Order + ThenBy Id). Decision 2026-05-24: pagination UI alone is cosmetic without the backend filter; the user opted for the full fix. [`src/FormForge.Api/Features/Menus/MenuAdminEndpoints.cs` GET handler + `src/FormForge.Api/Features/Menus/MenuService.cs` GetMenusAsync + `web/src/features/admin/menus/` new useMenuChildrenQuery + `web/src/routes/_app/admin/menus.$menuId.tsx` SubMenusSection]
- [x] [Review][Patch] TOCTOU on parent delete during `CreateMenuAsync` [`src/FormForge.Api/Features/Menus/MenuService.cs:90`] — parent is loaded `AsNoTracking`, then `SaveChangesAsync` inserts a child; if the parent is deleted in between, `fk_menus_parent ON DELETE RESTRICT` raises sqlstate 23503 and surfaces as unmapped 500. Mirror Story 4.1's `DeleteMenuAsync` fix: wrap `SaveChangesAsync` in `try/catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23503" })` → return `CreateMenuOutcome.ParentNotFound`.
- [x] [Review][Patch] `Number(e.target.value)` returns `NaN` for non-numeric `subOrder` input [`web/src/routes/_app/admin/menus.$menuId.tsx` SubMenusSection order onChange] — `NaN` serializes as `null`, hits server, falls through to generic `saveError` toast. Guard with `Number.isFinite` (e.g., `const n = Number(e.target.value); setSubOrder(Number.isFinite(n) ? n : 0)`).
- [x] [Review][Patch] Dead `createSubMenuSuccess` i18n key [`web/src/lib/i18n/locales/en.json` admin.menus block] — added to en.json but never referenced in code. `useCreateMenuMutation.onSuccess` already toasts `admin.menus.createSuccess` for every create. Remove the unused key, or replace the generic toast with a sub-menu-specific one when `parentId` was passed.
- [x] [Review][Patch] `isTopLevel` strict `=== null` check rejects `undefined` `parentId` [`web/src/routes/_app/admin/menus.$menuId.tsx:108`] — TypeScript declares `string | null` but a stale TanStack cache or future API drift could yield `undefined`, which would hide both the SubMenusSection AND the "View Parent" link, orphaning the menu in the UI. Switch to `!menu.parentId` to defend against the tri-state.
- [x] [Review][Patch] AC-1 test asserts POST response only — no GET round-trip to verify `parentId` actually persists [`src/FormForge.Api.Tests/Features/Menus/MenuIntegrationTests.cs` CreateMenu_WithValidParent_Returns201WithParentIdSet] — a regression that restores `ParentId = null` on entity construction (Story 4.1's prior behavior) would still pass since the response is built from the mutated in-memory entity. Add a follow-up `GET /api/admin/menus/{id}` and assert `body.ParentId == parentId`.

- [x] [Review][Defer] `UpdateMenuAsync` ignores `ParentId` — no re-parent path, no test asserts the invariant [`src/FormForge.Api/Features/Menus/MenuService.cs:99-122`] — explicitly spec-deferred to Story 4.5 (drag-and-drop reorder); deferred, pre-existing
- [x] [Review][Defer] Concurrent admin promotes parent to sub-menu between check and insert → permits 3-level chain — relies on re-parent path that doesn't exist; relevant once Story 4.5 lands; deferred, pre-existing
- [x] [Review][Defer] Sub-menus section client-side filter over the full admin list [`web/src/routes/_app/admin/menus.$menuId.tsx` SubMenusSection] — spec Dev Notes accept `pageSize=100` as over-provisioned; backend `?parentId=` filter endpoint is future work; deferred, pre-existing
- [x] [Review][Defer] `viewParent` "← View Parent Menu" hardcodes the directional arrow in the i18n string [`web/src/lib/i18n/locales/en.json` viewParent] — RTL-flip concern is broader than this story; project is English-only currently; deferred, pre-existing
- [x] [Review][Defer] No integration test verifies `IMenuCache.InvalidateAsync` is called after sub-menu create — `NoOpMenuCache` is a stub; will matter when Story 5.2 wires a real cache; deferred, pre-existing
- [x] [Review][Defer] `isActiveLabel` reused as both column header and truthy cell value [`web/src/routes/_app/admin/menus.$menuId.tsx:265,278`] — same coupling exists in Story 4.1's `menus.tsx:67,102`; pre-existing pattern, not introduced by this story; deferred, pre-existing

## Dev Notes

### No Migration Needed

The `menus` table already has the `parent_id` column, the `fk_menus_parent ON DELETE RESTRICT` constraint, and the `idx_menus_parent_id` index — all created by the Story 4.1 migration (`20260524054931_CreateMenusAndMenuRoleAssignments`). Story 4.2 only enables the application layer to populate that column.

### Backend: Updated `CreateMenuRequest.cs`

```csharp
// src/FormForge.Api/Features/Menus/Dtos/CreateMenuRequest.cs
internal sealed class CreateMenuRequest
{
    public string Name { get; set; } = string.Empty;
    public int Order { get; set; }
    public System.Text.Json.JsonElement? Icon { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid? ParentId { get; set; }   // null = top-level (Story 4.1 default); non-null = sub-menu
}
```

No validator changes are needed. `ParentId` is an optional GUID; its existence and depth constraints require DB access and are enforced in the service.

### Backend: Updated `CreateMenuOutcome` Enum

```csharp
internal enum CreateMenuOutcome { Success, NameConflict, ParentNotFound, MaxDepthExceeded }
```

### Backend: `CreateMenuAsync` Depth-Validation Block

Insert this block **before** the `new Menu { ... }` construction:

```csharp
if (request.ParentId.HasValue)
{
    var parent = await db.Menus
        .AsNoTracking()
        .FirstOrDefaultAsync(m => m.Id == request.ParentId.Value, ct)
        .ConfigureAwait(false);
    if (parent is null)
        return new CreateMenuResult(CreateMenuOutcome.ParentNotFound);
    if (parent.ParentId.HasValue)
        return new CreateMenuResult(CreateMenuOutcome.MaxDepthExceeded);
}
```

Also update the `Menu` entity construction to use `ParentId = request.ParentId` instead of the hardcoded `null` from Story 4.1:

```csharp
var menu = new Menu
{
    Name = request.Name.Trim(),
    Order = request.Order,
    Icon = request.Icon is { ValueKind: System.Text.Json.JsonValueKind.Null } ? null : request.Icon?.ToString(),
    IsActive = request.IsActive,
    ParentId = request.ParentId,   // was: ParentId = null (Story 4.1)
    CreatedAt = DateTimeOffset.UtcNow,
};
```

### Backend: `CreateMenuHandler` New Arms

Follow the exact `Results.Problem(...)` pattern from the existing 404 and 409 arms in `MenuAdminEndpoints.cs`:

```csharp
CreateMenuOutcome.ParentNotFound => Results.Problem(
    title: "Parent menu not found",
    statusCode: 422,
    extensions: new Dictionary<string, object?> { ["code"] = "MENU_PARENT_NOT_FOUND", ["messageKey"] = "menus.parentNotFound" }),
CreateMenuOutcome.MaxDepthExceeded => Results.Problem(
    title: "Maximum menu depth exceeded",
    statusCode: 422,
    extensions: new Dictionary<string, object?> { ["code"] = "MAX_MENU_DEPTH_EXCEEDED", ["messageKey"] = "menus.maxDepthExceeded" }),
```

### Frontend: Updated `CreateMenuRequest` Type

```typescript
// web/src/features/menu/types.ts — add to existing CreateMenuRequest interface
export interface CreateMenuRequest {
  name: string
  order: number
  icon?: MenuIcon | null
  isActive?: boolean
  parentId?: string | null   // Story 4.2: null or omitted = top-level
}
```

### Frontend: Sub-menus Section on Detail Page

The `MenuDetailContent` component in `menus.$menuId.tsx` receives a `menu` prop that already includes `parentId: string | null`. Use this to branch:

**For top-level items (`menu.parentId === null`):**

Load the full admin menu list and filter client-side. A `pageSize` of 100 is intentionally over-provisioned for a navigation menu structure:

```typescript
const allMenusQuery = useMenusAdminQuery(1, 100)
const childMenus = allMenusQuery.data?.items.filter(m => m.parentId === menuId) ?? []
```

Render a "Sub-menu Items" section **after** the existing edit form and delete button. The inline create form needs its own local form state (separate from the edit form):

```typescript
const [subName, setSubName] = useState('')
const [subOrder, setSubOrder] = useState(0)
const [subCreateError, setSubCreateError] = useState<string | null>(null)
const createMutation = useCreateMenuMutation()
```

On submit:
```typescript
await createMutation.mutateAsync({ name: subName.trim(), order: subOrder, parentId: menuId })
setSubName('')
setSubOrder(0)
```

Handle errors by inspecting `ApiError.code`:
- `'MENU_PARENT_NOT_FOUND'` → `t('admin.menus.parentNotFound')` (edge case: stale menuId)
- `'MAX_MENU_DEPTH_EXCEEDED'` → `t('admin.menus.maxDepthExceeded')`

**For sub-menu items (`menu.parentId !== null`):**

Render near the page `<header>`:
```tsx
<Link to={`/admin/menus/${menu.parentId}`}>{t('admin.menus.viewParent')}</Link>
```

No sub-menus section is rendered for sub-menu items (enforcing the 2-level hierarchy at the UI layer).

### i18n Keys

Add the following keys to the `admin.menus` block in `web/src/lib/i18n/locales/en.json`:

```json
"subMenusTitle": "Sub-menu Items",
"noSubMenus": "No sub-menu items yet.",
"addSubMenuButton": "Add Sub-menu Item",
"addingSubMenuButton": "Adding…",
"viewParent": "← View Parent Menu",
"parentNotFound": "Parent menu item no longer exists",
"maxDepthExceeded": "Menu items can only be nested one level deep",
"createSubMenuSuccess": "Sub-menu item created"
```

### Integration Test Scenarios

Add to `src/FormForge.Api.Tests/Features/Menus/MenuIntegrationTests.cs`.

Auth tests for POST (`401`, `403`) are already covered in Story 4.1 — do **not** duplicate them.

**Required new scenarios:**

```
POST /api/admin/menus (sub-menu creation):
- Admin, { name, order, parentId: <id of an existing top-level item> }
    → 201; response body has parentId = <that id>
- Admin, { name, order, parentId: <random unknown GUID> }
    → 422, code "MENU_PARENT_NOT_FOUND"
- Admin, { name, order, parentId: <id of an existing sub-menu item> }
    → 422, code "MAX_MENU_DEPTH_EXCEEDED"
```

For the third scenario, seed the chain:
1. Create a top-level item via API → `topId`
2. Create a sub-menu under `topId` via API → `subId`
3. Attempt to POST with `parentId = subId` → expect 422 `MAX_MENU_DEPTH_EXCEEDED`

### Backend Test Count Baseline

Before Story 4.2: 254 backend tests (251 from Story 4.1 base + 3 new from code review patches).
After Story 4.2: expect +3 new integration tests (~257 total).

### What This Story Does NOT Implement

- Moving a sub-menu to a different parent — `UpdateMenuRequest` does not accept `parentId`; reparenting is deferred to Story 4.5 (reorder/drag-and-drop)
- Filtering `GET /api/admin/menus` by `parentId` query param — the admin list returns all menus flat; Story 4.7's public endpoint renders the tree
- Reordering sub-menu items via drag-and-drop → Story 4.5
- Role assignment on sub-menus → Story 4.4
- Permission-filtered navbar rendering → Story 4.7

### Key Architecture References

- [Source: epics.md § Story 4.2] 2-level hierarchy only: top-level → sub-menu. Any deeper nesting returns 422.
- [Source: architecture.md § 3.5] Same `/api/admin` group auth (RequirePlatformAdmin) — no new route group needed
- [Source: 4-1-create-and-manage-top-level-menu-items.md § Dev Notes] `parent_id` column + `fk_menus_parent ON DELETE RESTRICT` FK already exist; `ON DELETE RESTRICT` means deleting a top-level item with sub-menu children still returns 409 `MENU_HAS_CHILDREN` (Story 4.1 AC-2 unchanged)

## Dev Agent Record

### Agent Model Used

claude-opus-4-7[1m] (Claude Code, Opus 4.7 with 1M context)

### Debug Log References

None — no halts or rework. One mid-edit typo (extra `}` in `MenuService.cs` line 98) introduced and immediately fixed before any test run.

### Completion Notes List

- Kept `CreateMenuRequest` as a `record` instead of converting to a `class` (the Dev Notes showed a class-style example, but the existing file uses positional record syntax, and adding an optional `Guid? ParentId = null` positional parameter is the minimal backward-compatible change that preserves the established idiom — JSON deserialization still matches by property name, and all existing call-sites compile unchanged).
- Backend tests: 257 / 257 pass (254 prior baseline + 3 new sub-menu tests, matching the expected count from the story's Dev Notes).
- Frontend tests: 78 / 78 pass (no test count change — no UI tests were specified in the story).
- TypeScript: clean (`tsc --noEmit` exit 0).
- Lint: 28 baseline errors; 0 new errors introduced. The 2 errors on `menus.$menuId.tsx` are pre-existing `react-refresh/only-export-components` matching the established route-file pattern (Story 4.1 noted "+4 react-refresh/only-export-components matching existing route-file pattern" in its commit log).
- Production build: clean. `menus._menuId` chunk grew from earlier size to 6.64 kB (gzip 1.96 kB) to absorb the SubMenusSection component.
- AC-4 inline create form uses local component state (separate `subName` / `subOrder` `useState` hooks) rather than `react-hook-form` — kept simple because the form is two fields, has no async validation needs, and follows the pattern of inline error mapping the rest of the page uses.
- AC-5 parent link is rendered in the page header (next to the existing "Back to Menus" link), not as a standalone section, so visual hierarchy mirrors the breadcrumb pattern.

### File List

**Backend**:
- `src/FormForge.Api/Features/Menus/Dtos/CreateMenuRequest.cs` (modified) — added `Guid? ParentId = null` positional parameter
- `src/FormForge.Api/Features/Menus/MenuService.cs` (modified) — added `ParentNotFound` + `MaxDepthExceeded` outcomes, depth-validation guard block in `CreateMenuAsync`, `ParentId = request.ParentId` on entity construction
- `src/FormForge.Api/Features/Menus/MenuAdminEndpoints.cs` (modified) — added 422 Problem arms for new outcomes
- `src/FormForge.Api.Tests/Features/Menus/MenuIntegrationTests.cs` (modified) — added 3 new sub-menu tests + `CreateSubMenuViaApiAsync` helper

**Frontend**:
- `web/src/features/menu/types.ts` (modified) — added `parentId?: string | null` to `CreateMenuRequest`
- `web/src/lib/i18n/locales/en.json` (modified) — added 8 new keys under `admin.menus`
- `web/src/routes/_app/admin/menus.$menuId.tsx` (modified) — added parent link in header for sub-menus; added `SubMenusSection` component (table + inline create form + 422 error mapping) rendered only for top-level items; new imports for `useMenusAdminQuery`, `useCreateMenuMutation`, `MenuListItem` type

**Sprint tracking**:
- `_bmad-output/implementation-artifacts/sprint-status.yaml` (modified) — story 4.2 status: ready-for-dev → in-progress → review

## Change Log

- 2026-05-24 — Story 4.2 implementation: Backend (DTO + MenuService depth-validation + endpoint arms for 422 `MENU_PARENT_NOT_FOUND` / `MAX_MENU_DEPTH_EXCEEDED`); Frontend (CreateMenuRequest type + 8 i18n keys + SubMenusSection on top-level detail pages with inline create form + parent link in header for sub-menus). 257/257 backend tests, 78/78 frontend tests, TypeScript clean, lint baseline preserved, production build clean.
