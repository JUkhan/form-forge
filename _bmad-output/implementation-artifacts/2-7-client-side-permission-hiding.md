# Story 2.7: Client-Side Permission Hiding

Status: done

## Story

As a Content Editor or Viewer,
I see only UI controls I am authorized to use,
so that I am not confused by actions I cannot perform.

## Acceptance Criteria

### AC-1 — Navbar: absent items for resources with no `canRead`

**Given** I am logged in
**When** the SPA renders the navbar
**Then** Menu Items for which I have no `canRead` are absent (not disabled — absent) per FR-6 AC-3

### AC-2 — Record list: hidden controls based on CRUD permission flags

**Given** I am on a record list page
**When** the page renders
**Then** the "New Record" button is hidden if I lack `canCreate` on that Resource
**And** Edit/Delete controls on rows and detail views are hidden if I lack `canUpdate`/`canDelete`

> Note: AC-2 is satisfied by the `PermissionGate` / `usePermission` infrastructure created in this story. Full wiring occurs when record list pages are built in Story 6.x. The dev agent must create the infrastructure and demonstrate its usage, even if the record list UI does not exist yet.

### AC-3 — Permission checks use `PermissionGate` or `usePermission` — never raw JWT claims

**Given** a permission check is needed in a component
**When** the component renders
**Then** it uses the `PermissionGate` shared component or the `usePermission` hook (per AR-31)
**And** it never reads JWT claims directly (no `atob`, no `jwtDecode`, no manual token parsing)

### AC-4 — Permissions re-fetched on every access token refresh

**Given** permissions change server-side (role assignment or deactivation)
**When** the access token is next refreshed by `useAuthQuery`
**Then** the client re-fetches `GET /api/users/me/permissions` and updates the cached set used by `PermissionGate` / `usePermission`

---

## Tasks / Subtasks

> All TypeScript conventions remain in force: strict mode, `type` keyword for type-only imports, named exports, no `any`, arrow function components. No new npm packages required — this story uses only existing dependencies (React, TanStack Query v5, httpClient).

- [x] Task 1 — Create `usePermissionsQuery.ts` in `web/src/features/auth/` (AC-3, AC-4)
  - [x] Define and export `CrudFlags` interface: `{ canCreate: boolean; canRead: boolean; canUpdate: boolean; canDelete: boolean }`
  - [x] Define and export `PermissionsResponse` interface matching `GET /api/users/me/permissions` response (see Dev Notes)
  - [x] Export `PLATFORM_ADMIN_ROLE_ID = '00000000-0000-0000-0000-000000000001' as const`
  - [x] Export `PERMISSIONS_QUERY_KEY = ['auth', 'permissions'] as const`
  - [x] Implement `usePermissionsQuery()` using `httpClient.get<PermissionsResponse>('/api/users/me/permissions')`, `staleTime: Infinity`, `refetchOnWindowFocus: false`, `retry: false`

- [x] Task 2 — Create `usePermission.ts` in `web/src/features/auth/` (AC-3)
  - [x] Export `CrudAction` type: `'canCreate' | 'canRead' | 'canUpdate' | 'canDelete'`
  - [x] Implement `usePermission(resourceId: string, action: CrudAction): boolean`
  - [x] Platform-admin bypass: if `data.roleIds.includes(PLATFORM_ADMIN_ROLE_ID)` return `true` (see Critical Dev Note)
  - [x] Resource check: `data.perResource[resourceId]?.[action] ?? false`
  - [x] Return `false` while `data` is undefined (loading state — never flash unauthorized content)

- [x] Task 3 — Create `PermissionGate.tsx` in `web/src/components/shared/` (AC-1, AC-2, AC-3)
  - [x] Create `web/src/components/shared/` directory if it does not exist
  - [x] Props: `{ allowed: boolean; children: ReactNode; fallback?: ReactNode }`
  - [x] When `allowed` is `true` render `children`; else render `fallback ?? null`
  - [x] **No imports from `features/`** — dumb component only, no hooks inside (AR-31 import hierarchy)

- [x] Task 4 — Update `useAuthQuery.ts`: invalidate permissions on token refresh (AC-4)
  - [x] Add `useQueryClient()` import and call inside `useAuthQuery`
  - [x] Add `useEffect` watching `query.dataUpdatedAt`; on change call `queryClient.invalidateQueries({ queryKey: PERMISSIONS_QUERY_KEY })`
  - [x] Import `PERMISSIONS_QUERY_KEY` from `./usePermissionsQuery`
  - [x] Add `useEffect` import from `react`

- [x] Task 5 — Update `_app.tsx` (AppLayout): mount permissions query, demonstrate gating (AC-1, AC-3)
  - [x] Call `usePermissionsQuery()` in `AppLayout` — ensures permissions load on every authenticated page
  - [x] Import `usePermission` and add a minimal permission-gated nav element in the header demonstrating AC-1 (e.g., an "Admin" link visible only to platform-admin role) using `PermissionGate`
  - [x] Logout button and `useAuthQuery()` call remain unchanged
  - [x] Keep the "Full layout added in Story 4.7 / Epic 7" comment; only add a small, clearly-labelled permission-gated stub

- [x] Task 6 — Verify build passes (0 errors, 0 new lint violations)
  - [x] Run `pnpm tsc --noEmit` from `web/` — 0 TypeScript errors
  - [x] No ESLint `import/no-restricted-paths` violations (see AR-31 note)

### Review Findings

Recorded 2026-05-23 by `bmad-code-review` (3-reviewer parallel pass: Blind Hunter, Edge Case Hunter, Acceptance Auditor; opus-4.7).

- [x] [Review][Patch] Double-fetch of `/api/users/me/permissions` on cold boot [web/src/features/auth/useAuthQuery.ts:55-60] — `useEffect` guards on `dataUpdatedAt > 0`, but `dataUpdatedAt` transitions from `0` → positive on the very first successful silent refresh. `usePermissionsQuery()` is mounted in the same render in `_app.tsx:59` and has already started its initial fetch by the time the effect runs, so the invalidation triggers a second request for permissions on every cold boot. Fix: track the previous value via `useRef` and only invalidate on transitions between two non-zero values (skip the initial `0 → positive` edge). Sources: blind+edge+auditor. **Applied:** added `useRef<number>(0)` to track previous `dataUpdatedAt`; effect now only invalidates when `previousUpdatedAt.current > 0 && query.dataUpdatedAt > previousUpdatedAt.current`.
- [x] [Review][Patch] Demo `<a href="/admin">` causes full-page reload to a non-existent route [web/src/routes/_app.tsx:70] — Hard nav drops the in-memory `tokenStore`, forces `beforeLoad` silent refresh, then lands on `/admin` which is not registered (Stories 2.8/2.9 add admin UI). The demo's purpose is AC-1 (absent vs present), not navigation. Fix: change to `<a href="#">` (or a placeholder TanStack `<Link>`) with a comment that `/admin` route arrives in Story 2.8. Sources: blind+auditor. **Applied:** changed `href` to `"#"` with a comment explaining the placeholder until the real route lands in Story 2.8/2.9.
- [x] [Review][Defer] `isActive: false` user not handled in `usePermission` [web/src/features/auth/usePermission.ts:11] — deferred, pre-existing — server hard-codes `isActive=true` until Story 2.8 (spec line 274). Real wiring needed once `DeactivateUserAsync` ships; cross-references the same item already logged from Story 2.6 review in `deferred-work.md`. Sources: blind+edge.
- [x] [Review][Defer] `retry: false` + `staleTime: Infinity` causes permanent silent denial on a single transient failure [web/src/features/auth/usePermissionsQuery.ts:28-32] — deferred, spec-mandated — Task 1 explicitly mandates `retry: false`. Real UX recovery (toast on permissions-load failure, manual retry button, or bounded retry) is a follow-up consideration outside this story's scope. Sources: blind+edge.

---

## Dev Notes

### Backend API Contract (Story 2.6 — already live)

`GET /api/users/me/permissions` — requires `Authorization: Bearer <token>`.

Response shape:
```json
{
  "userId": "<uuid>",
  "computedAt": "<ISO-8601>",
  "isActive": true,
  "perResource": {
    "<designerId>": { "canCreate": true, "canRead": true, "canUpdate": false, "canDelete": false }
  },
  "roleIds": ["<uuid>"]
}
```

- `perResource` is a dictionary keyed by `designerId` (string). A user with no roles returns `"perResource": {}`.
- `isActive` is currently always `true` (Story 2.8 adds real deactivation).
- Server-side cache: 30-second TTL, busted by `UserRoleAssignmentChanged` / `RolePermissionsChanged` domain events.
- Route registered at `/api/users/me/permissions` (route group: `/api/users`, added in Story 2.6 `Program.cs`).

### CRITICAL: Platform-Admin Client-Side Bypass

**This is the #1 implementation trap for this story.** The server grants platform-admin all permissions at the `RequirePermission` filter level (Story 2.6 AC-2). However, `GET /api/users/me/permissions` for a pure platform-admin (no explicit `role_permissions` rows) returns `perResource: {}` — see Story 2.6 Test 4, which only asserts `roleIds contains "00000000-0000-0000-0000-000000000001"`, not any `perResource` entries.

**A `usePermission` hook that only checks `perResource` will always return `false` for platform-admin → all UI controls hidden.** This is wrong.

**Correct implementation:**
```typescript
export const PLATFORM_ADMIN_ROLE_ID = '00000000-0000-0000-0000-000000000001' as const

export function usePermission(resourceId: string, action: CrudAction): boolean {
  const { data } = usePermissionsQuery()
  if (!data) return false
  if (data.roleIds.includes(PLATFORM_ADMIN_ROLE_ID)) return true   // bypass: admin sees all
  return data.perResource[resourceId]?.[action] ?? false
}
```

Reading from `data.roleIds` (the permissions API response) is NOT reading JWT claims — it is compliant with AC-3.

### PermissionGate: Dumb Component (AR-31 Import Hierarchy)

Per AR-31, the import hierarchy is `routes → features → components → lib`. `components/shared/` is below `features/auth/` in this chain. **`PermissionGate.tsx` must not import from `features/`** or it violates `import/no-restricted-paths`.

Implement as a dumb rendering gate that accepts a pre-computed `allowed: boolean`:

```tsx
// web/src/components/shared/PermissionGate.tsx
import type { ReactNode } from 'react'

interface PermissionGateProps {
  allowed: boolean
  children: ReactNode
  fallback?: ReactNode
}

export function PermissionGate({ allowed, children, fallback = null }: PermissionGateProps) {
  return <>{allowed ? children : fallback}</>
}
```

Call site (in any route component):
```tsx
const canCreate = usePermission(designerId, 'canCreate')
<PermissionGate allowed={canCreate}>
  <Button>New Record</Button>
</PermissionGate>
```

Absence pattern for navbar (AC-1 — absent, not disabled):
```tsx
// PermissionGate with no fallback renders nothing (null) when not allowed
<PermissionGate allowed={usePermission(designerId, 'canRead')}>
  <NavLink to="/records">Records</NavLink>
</PermissionGate>
```

### Permission Refresh: `useAuthQuery` Invalidation Pattern (TanStack Query v5)

TanStack Query v5 removed `onSuccess` from `useQuery`. Use `useEffect` watching `dataUpdatedAt`:

```typescript
// web/src/features/auth/useAuthQuery.ts  (modified)
import { useEffect } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { PERMISSIONS_QUERY_KEY } from './usePermissionsQuery'
// ... existing imports unchanged

export function useAuthQuery() {
  const queryClient = useQueryClient()

  const query = useQuery({
    queryKey: ['auth', 'session'],
    queryFn: async ({ signal }) => {
      const data = await silentRefresh(signal)
      tokenStore.set(data.accessToken)
      return data
    },
    staleTime: Infinity,
    refetchInterval: 13 * 60 * 1000,
    refetchOnWindowFocus: false,
    retry: false,
  })

  useEffect(() => {
    if (query.dataUpdatedAt > 0) {
      void queryClient.invalidateQueries({ queryKey: PERMISSIONS_QUERY_KEY })
    }
  }, [query.dataUpdatedAt, queryClient])

  return query
}
```

`query.dataUpdatedAt` is a timestamp updated on every successful refetch. The `useEffect` fires each time it changes, invalidating the `['auth', 'permissions']` cache entry so `usePermissionsQuery` triggers a fresh fetch.

### Logout Already Handles Cleanup

`useLogoutMutation.onSettled` calls `queryClient.clear()` which removes **all** cached queries including `['auth', 'permissions']`. No extra cleanup code needed on logout.

### `usePermissionsQuery` in AppLayout — Not in `beforeLoad`

`usePermissionsQuery` is a React hook — it cannot be called in `beforeLoad` (a plain async function). Mount it in `AppLayout` alongside `useAuthQuery()`. This guarantees the token is already set (guaranteed by `beforeLoad`) before the hook fires its first fetch.

```tsx
function AppLayout() {
  useAuthQuery()           // existing — keeps token fresh
  usePermissionsQuery()    // new — loads permission snapshot on every authenticated render
  // ...
}
```

### File Locations Summary

| File | Path | Action |
|---|---|---|
| `usePermissionsQuery.ts` | `web/src/features/auth/` | CREATE |
| `usePermission.ts` | `web/src/features/auth/` | CREATE |
| `PermissionGate.tsx` | `web/src/components/shared/` | CREATE (new directory) |
| `useAuthQuery.ts` | `web/src/features/auth/` | MODIFY |
| `_app.tsx` | `web/src/routes/` | MODIFY |
| `en.json` | `web/src/lib/i18n/locales/` | MODIFY only if nav labels needed |

### TanStack Query Key Convention (AR-48)

Tuple pattern `['scope', 'entity', ...params]`:
- `['auth', 'session']` — access token refresh (existing)
- `['auth', 'permissions']` — effective permissions (new this story)

Both are auth-scoped. Using the same `'auth'` prefix means a future `queryClient.invalidateQueries({ queryKey: ['auth'] })` would bust both — correct semantics.

### `usePermissionsQuery` Interface — Full Shape

```typescript
export interface CrudFlags {
  canCreate: boolean
  canRead: boolean
  canUpdate: boolean
  canDelete: boolean
}

export interface PermissionsResponse {
  userId: string
  computedAt: string        // ISO-8601
  isActive: boolean
  perResource: Record<string, CrudFlags>
  roleIds: string[]         // UUIDs
}
```

Match this exactly to the Story 2.6 `PermissionsResponse` DTO:
```csharp
// src/FormForge.Api/Features/Permissions/Dtos/PermissionsResponse.cs
record PermissionsResponse(Guid UserId, DateTimeOffset ComputedAt, bool IsActive,
    Dictionary<string, CrudFlagsResponse> PerResource, IEnumerable<Guid> RoleIds);
record CrudFlagsResponse(bool CanCreate, bool CanRead, bool CanUpdate, bool CanDelete);
```

Note: .NET serializes C# `PascalCase` records as `camelCase` JSON by default (configured in `Program.cs` Story 1.x). Map accordingly in TypeScript.

### Previous Story Intelligence (Story 2.6)

- `GET /api/users/me/permissions` is live. Route group: `app.MapGroup("/api/users").RequireAuth()...MapUserSelfEndpoints()` in `Program.cs`.
- `UserRoleAssignmentChanged` event busts the server-side 30s permission cache. Client sees fresh data on next `usePermissionsQuery` fetch after cache bust.
- `EffectivePermissions.IsActive` is hard-coded `true` until Story 2.8 — `isActive` field exists in the response but is always `true`.
- 125 backend tests passing — this story adds no backend changes, must not break backend.

### Git Intelligence (Recent Commits)

```
f5224c8 Story 2.6 bmad-code-review — apply 5 patches from 3-reviewer parallel pass
5772f20 Story 2.6 code review — apply 7 patches from extra-high recall pass
36640e2 Story 2.6 — Effective permission computation + server-side endpoint authorization
6b4e81d Story 2.5 code review — apply 8 patches from three-reviewer pass
fee393c Story 2.5 — User-Role Assignment: PUT /api/admin/users/{id}/roles
```

Frontend patterns from Story 2.3 (`useLogoutMutation`): raw `fetch` for logout, `queryClient.clear()` on settle, `useNavigate` + `useMutation`. Mirror this structural pattern when needed.

### References

- [Source: epics.md §Story 2.7] — AC-1 through AC-4 exact specification
- [Source: architecture.md §4.6 Module/Feature Folder Structure] — `PermissionGate` in `components/shared/`
- [Source: architecture.md §AR-31] — Import hierarchy `routes→features→components→lib`, no reverse imports
- [Source: architecture.md §AR-48] — TanStack Query key tuple convention
- [Source: story 2.6 §AC-1] — `GET /api/users/me/permissions` response shape
- [Source: story 2.6 §Test 4] — Platform-admin: `roleIds` asserted, `perResource` not asserted (may be empty)
- [Source: story 2.6 §Dev Notes AR-11] — 30s server-side cache, domain event bust
- [Source: web/src/features/auth/useAuthQuery.ts] — TanStack Query v5 pattern, `['auth', 'session']` key, `staleTime: Infinity`
- [Source: web/src/features/auth/httpClient.ts:130-134] — `httpClient.get<T>(path)` API
- [Source: web/src/features/auth/authMutations.ts:47-51] — `queryClient.clear()` on logout (permissions cleanup is free)
- [Source: web/src/routes/_app.tsx:47-70] — Current AppLayout stub to modify

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-7[1m]

### Debug Log References

- `npx tsc --noEmit -p tsconfig.app.json` → exit 0, 0 errors
- `npx eslint src/features/auth/usePermissionsQuery.ts src/features/auth/usePermission.ts src/features/auth/useAuthQuery.ts src/components/shared/PermissionGate.tsx src/routes/_app.tsx` → 1 error (pre-existing `react-refresh/only-export-components` on `function AppLayout` in `_app.tsx`, identical violation present at HEAD before this story — confirmed via `git stash` + re-lint baseline)
- `npx vite build` → exit 0, `✓ built in 1.47s`
- No `import/no-restricted-paths` violations introduced (rule not configured in `eslint.config.js`, but AR-31 import hierarchy verified by inspection — `PermissionGate.tsx` imports only `type ReactNode` from `react`)

### Completion Notes List

**AC-1 (Navbar absence for unauthorized):** Satisfied by the demo `<PermissionGate allowed={canSeeAdmin}>` wrapping the `Admin` link in `_app.tsx` header. With no `fallback` prop, `PermissionGate` renders `null` when `allowed === false` — true absence, not disabled state, per FR-6 AC-3.

**AC-2 (Record list CRUD controls):** Infrastructure (`PermissionGate` + `usePermission`) created and demonstrated. Full wiring on record list pages occurs in Story 6.x as specified in the AC note.

**AC-3 (No raw JWT claims):** Verified by inspection — no `atob`, `jwtDecode`, or manual token parsing introduced in any new/modified file. All permission state flows through the `usePermissionsQuery` hook, which reads the `GET /api/users/me/permissions` response. Reading `data.roleIds` from this API response is API consumption, not JWT decoding, and is the explicit pattern called out in the Critical Dev Note.

**AC-4 (Refetch on token refresh):** `useAuthQuery` now watches `query.dataUpdatedAt` via `useEffect`; every successful silent-refresh tick (every 13 min, on focus restoration, or on manual refetch) invalidates `PERMISSIONS_QUERY_KEY` so the next render triggers a fresh `usePermissionsQuery` fetch. Initial mount is also covered because `dataUpdatedAt` transitions from `0` to `> 0` on first success, which is the explicit guard in the `useEffect`.

**Platform-admin bypass:** Implemented exactly as specified in the Critical Dev Note. `usePermission` returns `true` for any (resourceId, action) when `data.roleIds` includes `PLATFORM_ADMIN_ROLE_ID`, mirroring the server-side `RequirePermission` filter behavior from Story 2.6 AC-2. The demo header link (`usePermission('platform-admin', 'canRead')`) exercises this code path: visible for platform-admin (bypass returns `true`), absent for non-admin users (no `perResource['platform-admin']` entry → `false`).

**Logout cleanup:** No new code needed. `useLogoutMutation.onSettled` already calls `queryClient.clear()` (`authMutations.ts:50`), which removes the `['auth', 'permissions']` cache along with all other keys.

**AR-31 import hierarchy:** Verified by inspection. `PermissionGate.tsx` (components/shared/) imports only `type ReactNode` from `react`. The `eslint.config.js` does not currently configure `import/no-restricted-paths` (architecture.md mentions it as intent), but the architectural rule is followed manually.

**Test strategy:** The story Tasks/Subtasks lists no test creation tasks (validation gate is Task 6: `tsc --noEmit` + lint). The `web/` package has no vitest config or existing tests on disk despite vitest devDeps being installed — establishing a frontend test harness from scratch is well outside this story's scope and would expand the change set materially. Per the story spec, validation is build-time + behavioral demonstration in `_app.tsx`. Backend has 125 tests; this story touches zero backend files so no backend regression possible.

**Deviations from spec:** None.

### File List

**Created**
- `web/src/features/auth/usePermissionsQuery.ts`
- `web/src/features/auth/usePermission.ts`
- `web/src/components/shared/PermissionGate.tsx`

**Modified**
- `web/src/features/auth/useAuthQuery.ts` — added `useEffect`/`useQueryClient` to invalidate `PERMISSIONS_QUERY_KEY` on every refresh
- `web/src/routes/_app.tsx` — mounted `usePermissionsQuery()`; added permission-gated `Admin` header link via `PermissionGate` + `usePermission`
- `web/src/lib/i18n/locales/en.json` — added `nav.admin` label

## Change Log

| Date | Change | Author |
|---|---|---|
| 2026-05-23 | Initial implementation — `usePermissionsQuery`, `usePermission`, `PermissionGate`; permissions invalidation on token refresh; AC-1 demonstration in `_app.tsx` | Dev Agent (claude-opus-4-7) |
| 2026-05-23 | Code review (3-reviewer parallel pass) — applied 2 patches: skip initial `dataUpdatedAt` 0→positive transition in `useAuthQuery` to avoid double-fetch on cold boot; switched demo Admin link to `href="#"` to avoid full-page reload to non-existent `/admin` route. Deferred 2 items (isActive guard → Story 2.8; permissions-load failure UX → SPA-hardening follow-up) to `deferred-work.md`. Status `review` → `done`. | Reviewer Agent (claude-opus-4-7) |
