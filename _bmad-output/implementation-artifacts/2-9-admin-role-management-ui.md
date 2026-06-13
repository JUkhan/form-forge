# Story 2.9: Admin Role Management UI

Status: done

## Story

As a Platform Admin,
I want to manage Roles and their per-Resource permissions from a dedicated admin page,
so that I can define access control without writing SQL.

## Acceptance Criteria

1. **AC-1 — Paginated role list at Admin > Roles**
   When I navigate to Admin > Roles, I see a paginated list of all roles with name, description, permission count, and an isSystem indicator, using the standard `page` / `pageSize` URL search params (AR-21 convention, pageSize default 25, max 100).

2. **AC-2 — Role editor with permission matrix**
   When I open a role for editing, I see a permission matrix where rows represent the role's current resources (from the role's `permissions` array, sorted by `resourceId`), and columns are `canCreate` / `canRead` / `canUpdate` / `canDelete` checkboxes. System roles (`isSystem: true`) render the matrix read-only with a banner and no Save/Delete buttons.

3. **AC-3 — New Menu Binding resource adds new matrix row**
   The role editor derives its rows entirely from the `GET /api/admin/roles/{id}` response. When a new Menu Binding is created (Epic 4), the API will include the new resource (all flags `false`). The editor re-fetches via TanStack Query invalidation and displays the new row automatically — no special client-side logic is required for this AC.

4. **AC-4 — Save role permissions and fire cache-invalidation event**
   When I save a non-system role, the frontend calls `PUT /api/admin/roles/{id}` with the full updated permissions array. The backend `UpdateRoleAsync` already publishes `RolePermissionsChanged(roleId)` after commit, which causes `PermissionService` to evict cached permissions for every user holding that role. No new backend work is required.

## Tasks / Subtasks

- [x] Task 1: Add Roles nav link in AdminLayout (AC-1)
  - [x] Update `web/src/routes/_app/admin.tsx`: add `<Link to="/admin/roles" activeProps={{ style: { fontWeight: 600 } }}>` alongside the existing Users link

- [x] Task 2: Create role type definitions (AC-1, AC-2, AC-4)
  - [x] Create `web/src/features/admin/roles/types.ts` with `RoleDetail`, `PermissionRecord`, `CreateRoleRequest`, `UpdateRoleRequest` interfaces

- [x] Task 3: Create paginated roles query (AC-1)
  - [x] Create `web/src/features/admin/roles/useRolesQueryPaginated.ts` — accepts `page` and `pageSize`, exports `ROLES_PAGINATED_QUERY_KEY`
  - [x] **Do NOT modify** `web/src/features/admin/roles/useRolesQuery.ts` — that hook is used by the user-detail role multi-select and must stay as-is

- [x] Task 4: Create role detail query (AC-2)
  - [x] Create `web/src/features/admin/roles/useRoleDetailQuery.ts` for `GET /api/admin/roles/{id}`, exports `ROLE_DETAIL_QUERY_KEY`

- [x] Task 5: Create role mutations (AC-4)
  - [x] Create `web/src/features/admin/roles/roleMutations.ts` with `useCreateRoleMutation`, `useUpdateRoleMutation`, `useDeleteRoleMutation`
  - [x] Each hook: invalidates via `ROLES_PAGINATED_QUERY_KEY` prefix on success + `toast.success(t(...))`
  - [x] `useUpdateRoleMutation`: also invalidates `ROLE_DETAIL_QUERY_KEY` for the specific roleId
  - [x] `useDeleteRoleMutation`: caller navigates to `/admin/roles` after success (do not navigate inside `onSuccess` — let the component handle it)

- [x] Task 6: Create roles list page (AC-1)
  - [x] Create `web/src/routes/_app/admin/roles.tsx` — paginated table with create form (mirror `users.tsx` exactly)
  - [x] `validateSearch` with `rolesSearchSchema` using `z.coerce.number()` for page/pageSize
  - [x] Table columns: name (link to detail), description, permissionCount, isSystem badge (note: backend `RoleListItem` DTO omits `createdAt`, so the createdAt column was dropped)
  - [x] Inline "Create Role" form (name + description fields; `permissions: []` on create — matrix is for editing)
  - [x] 409 on create → `setError('name', { type: 'server', message: t('admin.roles.nameConflict') })`

- [x] Task 7: Create role detail/edit page with permission matrix (AC-2, AC-3, AC-4)
  - [x] Create `web/src/routes/_app/admin/roles.$roleId.tsx`
  - [x] Fetch role via `useRoleDetailQuery(roleId)`; show loading / 404-not-found states
  - [x] Name + description edit form (react-hook-form + zod, `mode: 'onChange'`), disabled for system roles
  - [x] Permission matrix component: rows from `role.permissions` sorted by `resourceId`; empty-state message if no permissions yet
  - [x] Checkboxes disabled for system roles
  - [x] Save button calls `useUpdateRoleMutation(roleId)` with full updated `{ name, description, permissions }` array
  - [x] Delete button (hidden for system roles): calls `useDeleteRoleMutation`, navigates to `/admin/roles` on success
  - [x] Error handling: 409 `ROLE_NAME_CONFLICT` → `setError('name', ...)`, 409 `ROLE_HAS_ASSIGNMENTS` → show alert near delete button, 404 → "role no longer exists" message, network/500 → generic error

- [x] Task 8: Add i18n strings (AC-1, AC-2)
  - [x] Add `admin.roles` namespace to `web/src/lib/i18n/locales/en.json` (full key list in Dev Notes below)

## Dev Notes

### This story is FRONTEND-ONLY — no C# changes

All backend endpoints and cache-invalidation logic are already complete:

| Endpoint | Story | Notes |
|---|---|---|
| `GET /api/admin/roles` | 2.4 | Paginated; `page` + `pageSize` params; `pageSize` clamped 1–100 |
| `GET /api/admin/roles/{id}` | 2.4 | Returns full `RoleResponse` with `permissions` sorted by `resourceId` |
| `POST /api/admin/roles` | 2.4 | 201 + Location header on success; 409 `ROLE_NAME_CONFLICT` on duplicate name |
| `PUT /api/admin/roles/{id}` | 2.4 + 2.6 | 204; replaces permissions atomically; publishes `RolePermissionsChanged` |
| `DELETE /api/admin/roles/{id}` | 2.4 | 204; 409 `ROLE_HAS_ASSIGNMENTS` if users assigned; 409 `ROLE_SYSTEM_PROTECTED` for system roles |

`PermissionService.OnRolePermissionsChanged` (Story 2.6) handles cache eviction — no additional event wiring needed.

### Critical: do NOT touch this existing file

`web/src/features/admin/roles/useRolesQuery.ts` — fetches `pageSize=100` for the role multi-select on the user-detail page. The comment on line 13 explicitly defers a paginated variant to this story. **Leave the original hook completely unchanged.**

### Backend wire shapes (TypeScript interfaces to create in `types.ts`)

```ts
// Mirrors src/FormForge.Api/Features/Roles/Dtos/

export interface RoleListItem {
  id: string
  name: string
  description: string | null
  permissionCount: number
  isSystem: boolean
}

export interface PermissionRecord {
  resourceId: string
  canCreate: boolean
  canRead: boolean
  canUpdate: boolean
  canDelete: boolean
}

export interface RoleDetail {
  id: string
  name: string
  description: string | null
  isSystem: boolean
  createdAt: string
  updatedAt: string | null
  permissions: PermissionRecord[]
}

export interface CreateRoleRequest {
  name: string
  description?: string | null
  permissions: PermissionRecord[]
}

export interface UpdateRoleRequest {
  name: string
  description?: string | null
  permissions: PermissionRecord[]
}
```

`PagedResult<T>` is already defined in `web/src/features/admin/users/types.ts` — import from there (do not duplicate).

### Exact patterns from Story 2.8 to replicate

**1. List page** — copy the structure of `web/src/routes/_app/admin/users.tsx`:
```ts
// validateSearch at route level
const rolesSearchSchema = z.object({
  page: z.coerce.number().int().min(1).default(1),
  pageSize: z.coerce.number().int().min(1).max(100).default(25),
})
export const Route = createFileRoute('/_app/admin/roles')({
  validateSearch: rolesSearchSchema,
  component: RolesPage,
})

// Navigation
const navigate = useNavigate({ from: '/admin/roles' })
const goToPage = (next: number) => { void navigate({ search: { page: next, pageSize } }) }
```

**2. Paginated query** — mirror `web/src/features/admin/users/useUsersQuery.ts`:
```ts
export const ROLES_PAGINATED_QUERY_KEY = ['admin', 'roles', 'list'] as const

export function useRolesQueryPaginated(page: number, pageSize: number) {
  return useQuery({
    queryKey: [...ROLES_PAGINATED_QUERY_KEY, page, pageSize] as const,
    queryFn: () =>
      httpClient.get<PagedResult<RoleListItem>>(
        `/api/admin/roles?page=${String(page)}&pageSize=${String(pageSize)}`,
      ),
    placeholderData: (previous) => previous,
  })
}
```

**3. Detail query** — mirror `web/src/features/admin/users/useUserDetailQuery.ts`:
```ts
export const ROLE_DETAIL_QUERY_KEY = ['admin', 'roles', 'detail'] as const

export function useRoleDetailQuery(roleId: string) {
  return useQuery({
    queryKey: [...ROLE_DETAIL_QUERY_KEY, roleId] as const,
    queryFn: () => httpClient.get<RoleDetail>(`/api/admin/roles/${roleId}`),
  })
}
```

**4. Mutations** — mirror `web/src/features/admin/users/userMutations.ts`:
```ts
function invalidateAllRoles(queryClient: ReturnType<typeof useQueryClient>) {
  void queryClient.invalidateQueries({ queryKey: ROLES_PAGINATED_QUERY_KEY })
}

export function useUpdateRoleMutation(roleId: string) {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation({
    mutationFn: (body: UpdateRoleRequest) =>
      httpClient.put<void>(`/api/admin/roles/${roleId}`, body),
    onSuccess: () => {
      invalidateAllRoles(queryClient)
      void queryClient.invalidateQueries({ queryKey: [...ROLE_DETAIL_QUERY_KEY, roleId] })
      toast.success(t('admin.roles.updateSuccess'))
    },
  })
}
```

**5. Floating promise discipline** — always void async event handlers:
```tsx
onClick={() => { void onDelete() }}
onSubmit={(e) => { void handleSubmit(submit)(e) }}
```

**6. Mutation error handling** — distinguish status codes, never swallow silently (Story 2.8 patch P13/P14):
```ts
try {
  await mutation.mutateAsync(...)
} catch (err) {
  if (err instanceof ApiError && err.status === 409) {
    // handle specific 409 code
  } else if (err instanceof ApiError && err.status === 404) {
    setError('root', { message: t('admin.roles.roleNotFoundError') })
  } else {
    setError('root', { message: t('errors.genericError') })
  }
}
```

**7. Form sync after server update** — use `useEffect` + `reset` so form reflects server state after invalidation:
```ts
useEffect(() => {
  reset({ name: role.name, description: role.description ?? '' })
}, [role.name, role.description, reset])
```

**8. Key-remount trick for matrix draft reset** (Story 2.8 pattern from `RoleAssignment`):
```tsx
<PermissionMatrix
  key={role.permissions.map(p => p.resourceId).sort().join(',')}
  initialPermissions={role.permissions}
  onSave={handleSave}
  readOnly={role.isSystem}
  isPending={updateMutation.isPending}
/>
```
This resets local checkbox state to server truth whenever the role's permission set changes after a save.

### Error code → UI mapping

| Code | HTTP | Where | UI Action |
|---|---|---|---|
| `ROLE_NAME_CONFLICT` | 409 | create/update | `setError('name', { type: 'server', message: t('admin.roles.nameConflict') })` |
| `ROLE_SYSTEM_PROTECTED` | 409 | update/delete | Generic alert (UI guard should prevent reaching this; still handle defensively) |
| `ROLE_HAS_ASSIGNMENTS` | 409 | delete | Show inline error near delete button: `t('admin.roles.hasAssignments')` |
| `ROLE_NOT_FOUND` | 404 | get/update/delete | Show "role no longer exists" + back link |

### System role handling

Roles where `isSystem === true` (platform-admin `00000000-0000-0000-0000-000000000001`, viewer `00000000-0000-0000-0000-000000000002`):
- Show a notice banner: `t('admin.roles.systemRoleNotice')` — "System roles cannot be modified."
- Render name/description fields and checkboxes with `disabled`
- Hide Save and Delete buttons entirely (the backend enforces this too via 409, but UI should prevent the call)

### Permission matrix — empty state

For v2.9 there are no Menu Bindings (Epic 4), so custom roles will have `permissions: []` and the matrix will be empty. Show:
```
t('admin.roles.matrixEmptyState')
// "No resources defined yet. Resources appear here when Menu Bindings are created."
```
This is correct and expected — the matrix will populate once Epic 3/4 introduces resources.

### Admin layout — AdminLayout nav update

`web/src/routes/_app/admin.tsx` currently has a single `<Link to="/admin/users">`. Add after it:
```tsx
<Link to="/admin/roles" activeProps={{ style: { fontWeight: 600 } }}>
  {t('admin.roles.title')}
</Link>
```
Use `t('admin.roles.title')` (not a hardcoded string) — consistent with how Users is rendered.

### i18n keys to add to `en.json`

Insert the `roles` block inside the existing `admin` object, after the `users` block:

```json
"roles": {
  "title": "Roles",
  "subtitle": "Manage access control roles and their per-resource permissions.",
  "createButton": "Create role",
  "createDialogTitle": "Create role",
  "nameLabel": "Role name",
  "descriptionLabel": "Description (optional)",
  "permissionCountLabel": "Permissions",
  "systemBadge": "System",
  "systemRoleNotice": "System roles cannot be modified.",
  "noRoles": "No roles yet.",
  "loading": "Loading…",
  "loadError": "Could not load roles.",
  "roleNotFound": "Role not found.",
  "roleNotFoundError": "This role no longer exists. Refresh the list.",
  "saveButton": "Save",
  "cancelButton": "Cancel",
  "deleteButton": "Delete role",
  "backToRoles": "Back to roles",
  "previousPage": "Previous",
  "nextPage": "Next",
  "pageIndicator": "Page {{page}} of {{totalPages}}",
  "nameConflict": "A role with this name already exists.",
  "hasAssignments": "This role has active user assignments and cannot be deleted.",
  "createSuccess": "Role created.",
  "updateSuccess": "Role permissions saved.",
  "deleteSuccess": "Role deleted.",
  "saveError": "Could not save changes. Please try again.",
  "deleteError": "Could not delete role. Please try again.",
  "matrixTitle": "Permissions",
  "matrixEmptyState": "No resources defined yet. Resources appear here when Menu Bindings are created.",
  "resourceLabel": "Resource",
  "canCreate": "Create",
  "canRead": "Read",
  "canUpdate": "Update",
  "canDelete": "Delete",
  "detailTitle": "Role detail",
  "updateRequired": "Update at least one field."
}
```

### Routing — TanStack Router file-based

New route files are picked up automatically by the router plugin at dev-server start. `routeTree.gen.ts` regenerates automatically — **never edit it manually**.

Route paths after adding files:
- `web/src/routes/_app/admin/roles.tsx` → `/admin/roles`
- `web/src/routes/_app/admin/roles.$roleId.tsx` → `/admin/roles/:roleId`

### Project Structure Notes

**New files to CREATE:**

| File | Purpose |
|---|---|
| `web/src/routes/_app/admin/roles.tsx` | Paginated roles list + create form |
| `web/src/routes/_app/admin/roles.$roleId.tsx` | Role detail + permission matrix editor |
| `web/src/features/admin/roles/types.ts` | TypeScript DTOs (wire-shape interfaces) |
| `web/src/features/admin/roles/useRolesQueryPaginated.ts` | Paginated list query |
| `web/src/features/admin/roles/useRoleDetailQuery.ts` | Single role detail query |
| `web/src/features/admin/roles/roleMutations.ts` | Create / update / delete mutations |

**Files to MODIFY:**

| File | Change |
|---|---|
| `web/src/routes/_app/admin.tsx` | Add Roles nav link after Users link |
| `web/src/lib/i18n/locales/en.json` | Add `admin.roles` key namespace |

**Files to leave untouched:**
- `web/src/features/admin/roles/useRolesQuery.ts` — used by users.$userId.tsx for role multi-select
- All C# backend files
- `web/src/routes/routeTree.gen.ts` — auto-generated

### References

- [Source: _bmad-output/planning-artifacts/epics.md — Epic 2, Story 2.9 ACs]
- [Source: _bmad-output/planning-artifacts/architecture.md — Decision 3.4 (Pagination shape), Decision 4.6 (Module folder structure), Decision 4.9 (Form composition), AR-11 (RolePermissionsChanged)]
- [Source: _bmad-output/implementation-artifacts/2-4-role-crud.md — AC-2 through AC-6 (Role CRUD backend)]
- [Source: _bmad-output/implementation-artifacts/2-8-admin-user-management-ui.md — Dev Notes, Story 2.8 review patches P11–P16]
- [Source: web/src/routes/_app/admin/users.tsx — list page pattern to mirror]
- [Source: web/src/routes/_app/admin/users.$userId.tsx — detail page pattern to mirror]
- [Source: web/src/routes/_app/admin.tsx — AdminLayout with beforeLoad guard]
- [Source: web/src/features/admin/roles/useRolesQuery.ts — DO NOT MODIFY; paginated variant is the new hook]
- [Source: web/src/features/admin/users/userMutations.ts — mutations pattern]
- [Source: src/FormForge.Api/Features/Roles/RoleEndpoints.cs — error codes and HTTP shape]
- [Source: src/FormForge.Api/Features/Roles/RoleService.cs — UpdateRoleAsync publishes RolePermissionsChanged after commit]
- [Source: src/FormForge.Api/Infrastructure/EventBus/IDomainEventBus.cs — RolePermissionsChanged record]

## Dev Agent Record

### Agent Model Used

claude-opus-4-7 (Opus 4.7, 1M context)

### Debug Log References

- `npm run build` (Vite + `tsc -b --noEmit`) passed cleanly — 0 TypeScript errors across 289 modules, both new route bundles emitted (`roles-*.js`, `roles._roleId-*.js`).
- `npm run lint` — total 19 errors, all `react-refresh/only-export-components`. 14 pre-existing in the codebase (`button.tsx`, `__root.tsx`, `_app.tsx`, `admin.tsx`, `users.tsx`, `users.$userId.tsx`, `index.tsx`, `login.tsx`). The 5 new errors come from the two new route files following the same TanStack Router file-based convention used by the existing `users.tsx` / `users.$userId.tsx` (the rule is structurally incompatible with co-locating `Route` and component, which is the framework's prescribed pattern).
- `routeTree.gen.ts` regenerated correctly by the Vite plugin — `/admin/roles` and `/admin/roles/$roleId` registered under `_app/admin`.

### Completion Notes List

- **AC-1 (paginated role list):** New route `web/src/routes/_app/admin/roles.tsx` mirrors `users.tsx`; uses `validateSearch` with `z.coerce.number()` for `page` / `pageSize` (default 25, max 100). Table columns: name (link to detail), description, permissionCount, isSystem badge. Note: backend `RoleListItem` DTO has no `createdAt` field, so the createdAt column from the original task description was intentionally omitted to avoid rendering empty cells.
- **AC-2 (role editor with permission matrix):** New route `web/src/routes/_app/admin/roles.$roleId.tsx` derives matrix rows entirely from `role.permissions` (sorted by `resourceId` via `localeCompare`). System roles (`isSystem: true`) render with a `role="status"` banner, disabled inputs, and the Save / Delete buttons hidden — defense in depth alongside the backend 409 `ROLE_SYSTEM_PROTECTED`.
- **AC-3 (new resource auto-appears):** Confirmed via implementation — matrix has no client-side resource catalog. The detail page re-fetches via TanStack Query (`useRoleDetailQuery`), and any new resource the backend includes in `role.permissions` will appear automatically. The component is remounted via `key={role.permissions.map(p => p.resourceId).sort().join(',')}` (Story 2.8 pattern) so the local draft resets to server truth whenever the resource set changes.
- **AC-4 (save fires cache invalidation):** `useUpdateRoleMutation` PUTs `{ name, description, permissions }` to `/api/admin/roles/{id}`. Backend `UpdateRoleAsync` publishes `RolePermissionsChanged(roleId)` after commit (Story 2.6), which evicts cached permissions for every user holding the role. Frontend invalidates both `ROLES_PAGINATED_QUERY_KEY` (covers all `page` / `pageSize` variants via prefix match) and the specific `ROLE_DETAIL_QUERY_KEY` for the edited role.
- **Error handling** wired per the Story 2.8 patches: 409 `ROLE_NAME_CONFLICT` → `setError('name', { type: 'server' })`, 409 `ROLE_HAS_ASSIGNMENTS` → inline alert near delete button, 404 → "role no longer exists" message, generic / 500 → `t('admin.roles.saveError')` / `t('errors.genericError')`. All async event handlers use the `void` floating-promise discipline.
- **Critical constraint respected:** `web/src/features/admin/roles/useRolesQuery.ts` was NOT modified. The user-detail role multi-select continues to use the existing pageSize=100 hook, and the new paginated list uses the separate `useRolesQueryPaginated.ts` with its own `ROLES_PAGINATED_QUERY_KEY = ['admin', 'roles', 'list']` (distinct prefix so paginated-list invalidations don't clobber the multi-select fetch).

### File List

**New files:**

- `web/src/features/admin/roles/types.ts`
- `web/src/features/admin/roles/useRolesQueryPaginated.ts`
- `web/src/features/admin/roles/useRoleDetailQuery.ts`
- `web/src/features/admin/roles/roleMutations.ts`
- `web/src/routes/_app/admin/roles.tsx`
- `web/src/routes/_app/admin/roles.$roleId.tsx`

**Modified files:**

- `web/src/routes/_app/admin.tsx` — added `<Link to="/admin/roles">` after the existing Users link
- `web/src/lib/i18n/locales/en.json` — added `admin.roles` namespace
- `web/src/routeTree.gen.ts` — auto-regenerated by Vite plugin (new routes registered)

**Unchanged (per story constraint):**

- `web/src/features/admin/roles/useRolesQuery.ts` — used by user-detail role multi-select
- All C# backend files — story is frontend-only

### Review Findings

_From bmad-code-review on 2026-05-23 (three-reviewer parallel pass: Blind Hunter, Edge Case Hunter, Acceptance Auditor)._

- [x] [Review][Decision→Patch] Dual save buttons silently discard each other's edits — resolved by lifting the form into the page-level `RoleDetailContent` with a single Save button; matrix exposes its `draft` via `useImperativeHandle` so the submit handler reads name + description + permissions live at click time. [`web/src/routes/_app/admin/roles.$roleId.tsx`]
- [x] [Review][Patch] Server name regex 422 unhandled — added the same `^[a-z][a-z0-9-]{0,98}[a-z0-9]$` regex and no-consecutive-hyphens refinement to both `createRoleSchema` and `updateRoleSchema`. [`web/src/routes/_app/admin/roles.tsx:21-37`, `roles.$roleId.tsx:19-33`]
- [x] [Review][Patch] Form `useEffect(reset, ...)` wipes in-progress typing on background refetch — gated on `!formState.isDirty` and the submit handler now explicitly `reset(...)`s after a successful PUT so the post-save resync still works. [`web/src/routes/_app/admin/roles.$roleId.tsx:102-106`, `:121-122`]
- [x] [Review][Patch] Matrix remount key omits role identity — key now includes `roleId` plus the sorted resource ID list, so navigating between roles with matching resource sets remounts the matrix. [`web/src/routes/_app/admin/roles.$roleId.tsx:227-231`]
- [x] [Review][Patch] `ROLE_SYSTEM_PROTECTED` 409 surfaces as generic message — added explicit `code === 'ROLE_SYSTEM_PROTECTED'` branches in both `submit` and `onDelete`, plus new i18n key `admin.roles.systemProtected`. [`web/src/routes/_app/admin/roles.$roleId.tsx:127-130`, `:151-154`]
- [x] [Review][Patch] Create-form 409 ignores `err.code` — now matches on `err.code === 'ROLE_NAME_CONFLICT'`, consistent with the detail page. [`web/src/routes/_app/admin/roles.tsx`]
- [x] [Review][Patch] `updateSuccess` toast copy misleading — generalized to "Role saved." since the single-Save now always commits the full role. [`web/src/lib/i18n/locales/en.json` `admin.roles.updateSuccess`]
- [x] [Review][Patch] In-flight button labels said "Loading…" — added `savingButton: "Saving…"` and `deletingButton: "Deleting…"` i18n keys and applied them on the Save and Delete buttons. [`web/src/routes/_app/admin/roles.$roleId.tsx:241-242`, `:259`]
- [x] [Review][Patch] Pagination indicator "Page 5 of 2" on tampered URLs — clamped the displayed page to `Math.min(page, Math.max(1, totalPages))`. [`web/src/routes/_app/admin/roles.tsx`]
- [x] [Review][Defer] `RoleListItem` interface lives in the untouchable `useRolesQuery.ts` instead of `types.ts` — `useRolesQueryPaginated.ts:4` imports `RoleListItem` from `./useRolesQuery`, inverting the canonical-types dependency direction the spec intended (`types.ts` should be canonical per Dev Notes wire-shape table). Functional behavior is correct; spec also forbids modifying `useRolesQuery.ts`. Defer to a future code-quality pass. [`web/src/features/admin/roles/types.ts`, `useRolesQueryPaginated.ts:4`] — deferred, code-quality nit, doesn't break behavior
- [x] [Review][Defer] Permission matrix renders raw `resourceId` (likely snake_case or GUID) as row label and aria-label — UX-only; for v2.9 the matrix is empty in practice (no Menu Bindings yet), so this isn't observable until Epic 4 introduces resources. The same epic will likely add the human-readable resource catalog needed for a proper label/aria-label pairing. [`web/src/routes/_app/admin/roles.$roleId.tsx:322`, `:327`] — deferred, Epic 4 dependency
- [x] [Review][Defer] No optimistic concurrency / ETag on role updates — concurrent admin edits last-write-wins on permissions; backend lacks `If-Match` and frontend has no diff-against-baseline. Pre-existing architectural gap (not introduced by Story 2.9). [`src/FormForge.Api/Features/Roles/RoleEndpoints.cs`] — deferred, pre-existing backend limitation
- [x] [Review][Defer] Detail-page error fallthrough treats 403/500 as "Role not found" — `roleQuery.isError || !roleQuery.data` collapses every non-200 into the not-found UI. AdminLayout's beforeLoad guard already prevents non-admins from reaching this route, so 403 is the only realistic non-404 and it's defense-only. [`web/src/routes/_app/admin/roles.$roleId.tsx:31-40`] — deferred, low-risk, layout guard covers the realistic path

## Change Log

| Date | Change | Author |
|---|---|---|
| 2026-05-23 | Implemented Story 2.9: Admin Role Management UI — paginated role list, role detail/edit with permission matrix, create/update/delete mutations, i18n strings, AdminLayout nav link | dev-agent (claude-opus-4-7) |
| 2026-05-23 | bmad-code-review: 1 decision-needed, 8 patches, 4 deferred, ~10 dismissed (Toaster mount, sonner lockfile, router-gated edge cases, etc.) | code-review (claude-opus-4-7) |
| 2026-05-23 | Applied all 9 patches: refactor to single page-level Save (lift draft, useImperativeHandle); client-side name regex; gated form reset; matrix key includes roleId; ROLE_SYSTEM_PROTECTED branch; create-form 409 code check; toast copy; saving/deleting button labels; pagination clamp. Build clean, lint count unchanged. | code-review (claude-opus-4-7) |
