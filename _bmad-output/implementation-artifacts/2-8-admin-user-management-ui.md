# Story 2.8: Admin User Management UI

Status: done

## Story

As a Platform Admin,
I want to manage users from a dedicated settings page,
so that I have full control over who accesses the platform without touching the database.

## Acceptance Criteria

### AC-1 — Paginated user list at Admin > Users

**Given** I am logged in as a Platform Admin
**When** I navigate to Admin > Users
**Then** I see a paginated list of all users with name, email, status (Active/Inactive), and role count
**And** pagination follows the standard `?page=1&pageSize=25` convention (per AR-21)

### AC-2 — Create user form

**Given** the Create User form
**When** I submit `{ email, displayName, temporaryPassword }`
**Then** a user is created via `POST /api/admin/users` and I see the new user in the list
**And** the email field is rejected as duplicate with inline field error if the email already exists (HTTP 409 → `setError('email', ...)`)
**And** no email is sent — the admin shares the temporary password out-of-band

### AC-3 — Deactivate / Reactivate toggle

**Given** the user detail page
**When** I toggle Deactivate or Reactivate
**Then** the user's `isActive` flag flips via `PUT /api/admin/users/{id}/deactivate` or `PUT /api/admin/users/{id}/reactivate`
**And** all active refresh tokens for that user are revoked immediately when deactivating (per FR-1)
**And** the `UserDeactivated` domain event fires on deactivation, evicting the permission cache entry immediately (per AR-11 / AR-47)

### AC-4 — Self-deactivation prevention

**Given** I am the currently logged-in admin
**When** I attempt to deactivate myself
**Then** the action is blocked with an inline error (per FR-7 AC-5)
**And** the `PUT /api/admin/users/{id}/deactivate` endpoint enforces the same block server-side

### AC-5 — Role assignment multi-select on user detail

**Given** the role-assignment control on a user
**When** I open it
**Then** I see a multi-select listing all available roles; saving sends `PUT /api/admin/users/{id}/roles` (existing Story 2.5 endpoint — not reimplemented here)

### AC-6 — `isActive` guard in `usePermission` (closes deferred item from Story 2.7 review)

**Given** a deactivated user who still holds a valid JWT (within the 15-min TTL window)
**When** `usePermission` is evaluated for any resource/action
**Then** `usePermission` returns `false` for every check when `data.isActive === false`, short-circuiting before the platform-admin bypass
**And** `EffectivePermissions.IsActive` in `PermissionService.ComputePermissionsAsync` is now read from `users.is_active` (not hard-coded `true`) — closes the deferred item from Story 2.6 review

---

## Tasks / Subtasks

> TypeScript conventions remain in force: strict mode, `type` keyword for type-only imports, named exports, no `any`, arrow function components.
> C# conventions remain in force: nullable reference types, PascalCase types, _camelCase fields, Async suffix, no controllers.

### Backend

- [x] Task 1 — Extend `IUserService` + `UserService` with new operations (AC-1, AC-2, AC-3, AC-4)
  - [x] Define outcome enums and result records for each new operation (pattern: `RoleService.cs`)
  - [x] `GetUsersAsync(int page, int pageSize, CancellationToken ct)` → `PagedResult<UserListItem>`
  - [x] `GetUserAsync(Guid id, CancellationToken ct)` → `UserDetailResponse?` (includes role list)
  - [x] `CreateUserAsync(CreateUserRequest request, CancellationToken ct)` → `CreateUserResult`
    - Normalize email to lowercase, validate uniqueness (catch `23505` Postgres constraint on `uq_users_email`)
    - Hash password via `PasswordHasher.Hash()` (BCrypt work factor 12, already used in `AuthService`)
    - Set `IsActive = true`, `CreatedAt = DateTimeOffset.UtcNow`
  - [x] `UpdateUserAsync(Guid id, UpdateUserRequest request, CancellationToken ct)` → `UpdateUserResult`
    - Updates `DisplayName` and/or hashes + replaces password if `NewPassword` is provided
    - Sets `UpdatedAt = DateTimeOffset.UtcNow`
  - [x] `DeactivateUserAsync(Guid id, Guid currentUserId, CancellationToken ct)` → `DeactivateUserResult`
    - Block self-deactivation: if `id == currentUserId` return `DeactivateUserOutcome.SelfDeactivation`
    - Set `users.is_active = false`, `updated_at = now()`
    - Revoke all active refresh tokens: `db.RefreshTokens.Where(rt => rt.UserId == id && rt.RevokedAt == null)` → set `RevokedAt = DateTimeOffset.UtcNow` on each, `SaveChangesAsync`
    - Publish `UserDeactivated(id)` AFTER the DB transaction commits (same pattern as `UserRoleAssignmentChanged` in existing `AssignRolesAsync`)
  - [x] `ReactivateUserAsync(Guid id, CancellationToken ct)` → `ReactivateUserResult`
    - Set `users.is_active = true`, `updated_at = now()`
    - Does NOT re-mint refresh tokens (user must log in again)

- [x] Task 2 — Add DTOs in `src/FormForge.Api/Features/Users/Dtos/`
  - [x] `UserListItem(Guid Id, string Email, string DisplayName, bool IsActive, int RoleCount, DateTimeOffset CreatedAt)`
  - [x] `UserDetailResponse(Guid Id, string Email, string DisplayName, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, IReadOnlyList<UserRoleItem> Roles)`
  - [x] `UserRoleItem(Guid Id, string Name)`
  - [x] `CreateUserRequest(string? Email, string? DisplayName, string? TemporaryPassword)` (nullable for FluentValidation — validator enforces required)
  - [x] `UpdateUserRequest(string? DisplayName, string? NewPassword)` (all optional)

- [x] Task 3 — Add FluentValidation validators in `src/FormForge.Api/Features/Users/Validators/`
  - [x] `CreateUserRequestValidator`: Email required + valid format + max 320; DisplayName required + max 200; TemporaryPassword required + min 8
  - [x] `UpdateUserRequestValidator`: if DisplayName not null → max 200; if NewPassword not null → min 8; at least one field must be non-null

- [x] Task 4 — Add endpoints in `UserEndpoints.MapUserAdminEndpoints()` (AC-1, AC-2, AC-3, AC-4)
  - [x] `GET /` — `GetUsersHandler`: delegates to `GetUsersAsync(page, pageSize, ct)` → `Results.Ok(PagedResult<UserListItem>)`
  - [x] `GET /{id:guid}` — `GetUserHandler`: 404 if null
  - [x] `POST /` — `CreateUserHandler` with `AddValidationFilter<CreateUserRequest>()`: 201 Created + Location header on success, 409 on duplicate email, 422 on validation failure
  - [x] `PUT /{id:guid}` — `UpdateUserHandler` with `AddValidationFilter<UpdateUserRequest>()`: 204 on success, 404 if not found
  - [x] `PUT /{id:guid}/deactivate` — `DeactivateUserHandler`: reads `currentUserId` from JWT claims (`HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value`), 204 on success, 404 if not found, 409 on self-deactivation
  - [x] `PUT /{id:guid}/reactivate` — `ReactivateUserHandler`: 204 on success, 404 if not found

- [x] Task 5 — Fix `PermissionService.ComputePermissionsAsync` to read `users.is_active` (AC-6)
  - [x] In `ComputePermissionsAsync`, add: `var isActive = await db.Users.Where(u => u.Id == userId).Select(u => u.IsActive).FirstOrDefaultAsync(ct)` — return with `IsActive: isActive` instead of `IsActive: true`
  - [x] No migration needed — `users.is_active` column already exists

- [x] Task 6 — Add integration tests in `src/FormForge.Api.Tests/Features/Users/`
  - [x] `UserAdminIntegrationTests.cs`: happy-path create user + list + get + deactivate/reactivate cycle
  - [x] Duplicate email → 409; self-deactivation → 409; non-admin caller → 403
  - [x] Follow the Testcontainers pattern from `RoleIntegrationTests.cs` and `UserRoleIntegrationTests.cs`

### Frontend

- [x] Task 7 — Create `web/src/features/admin/users/` feature folder (AC-1, AC-2, AC-3, AC-5)
  - [x] `types.ts` — `UserListItem`, `UserDetail`, `UserRoleItem` interfaces matching backend DTOs
  - [x] `useUsersQuery.ts` — `useUsersQuery(page: number, pageSize: number)` using `httpClient.get<PagedResult<UserListItem>>('/api/admin/users?page=...&pageSize=...')`, key: `['admin', 'users', page, pageSize]`
  - [x] `useUserDetailQuery.ts` — `useUserDetailQuery(userId: string)` → key: `['admin', 'users', userId]`
  - [x] `userMutations.ts`:
    - `useCreateUserMutation()` → POST `/api/admin/users`; on success: `queryClient.invalidateQueries({ queryKey: ['admin', 'users'] })`
    - `useUpdateUserMutation()` → PUT `/api/admin/users/${id}`; on success: invalidate detail + list
    - `useDeactivateUserMutation()` → PUT `/api/admin/users/${id}/deactivate`; on success: invalidate detail + list; also `queryClient.invalidateQueries({ queryKey: ['auth', 'permissions'] })` to immediately pick up isActive change in the SPA if the deactivated user is the current user (edge case — normally guarded server-side via 409 self-deactivation)
    - `useReactivateUserMutation()` → PUT `/api/admin/users/${id}/reactivate`; on success: invalidate detail + list
    - `useAssignUserRolesMutation()` → PUT `/api/admin/users/${id}/roles` (existing Story 2.5 endpoint); on success: invalidate detail + list

- [x] Task 8 — Create admin area route layout with `beforeLoad` guard (AC-1)
  - [x] Create `web/src/routes/_app/admin.tsx` — layout component for all admin sub-routes
  - [x] `beforeLoad` guard: read `queryClient.getQueryData(PERMISSIONS_QUERY_KEY)` to check platform-admin membership; if not platform-admin (or data not yet loaded), redirect to `/`
  - [x] Admin layout component: minimal shell with breadcrumb and sub-nav links (Users, Roles) — full polish in Epic 7

- [x] Task 9 — Create Users list page at `web/src/routes/_app/admin/users.tsx` (AC-1, AC-2)
  - [x] Render `useUsersQuery(page, pageSize)` in a table: columns Name, Email, Status, Role Count
  - [x] "Create User" button opens a dialog/form — react-hook-form + Zod schema for `{ email, displayName, temporaryPassword }`
  - [x] On 409 from `useCreateUserMutation`: `setError('email', { type: 'server', message: t('admin.users.emailConflict') })`
  - [x] Status badge: green "Active" / red "Inactive" based on `isActive`
  - [x] Pagination controls updating URL search params via TanStack Router `validateSearch`
  - [x] Row click navigates to `/admin/users/$userId`

- [x] Task 10 — Create User detail page at `web/src/routes/_app/admin/users.$userId.tsx` (AC-2, AC-3, AC-4, AC-5)
  - [x] Renders `useUserDetailQuery(userId)` — shows email, displayName, isActive, createdAt, roles
  - [x] Edit form (react-hook-form): update displayName and/or password
  - [x] Deactivate/Reactivate button: calls the appropriate mutation; shows inline error if self-deactivation (AC-4 — 409 response)
  - [x] Role multi-select: lists all roles from `GET /api/admin/roles` (reuse existing role list endpoint from Story 2.4); submits via `useAssignUserRolesMutation()`
  - [x] Self-deactivation block: the server returns 409 with `code: "SELF_DEACTIVATION"` → surface as an inline error on the button, not a toast

- [x] Task 11 — Fix `usePermission.ts` to short-circuit on `isActive === false` (AC-6)
  - [x] Add `isActive` check BEFORE the platform-admin bypass: `if (data.isActive === false) return false`
  - [x] This ensures a deactivated platform-admin sees no UI controls for the remaining JWT TTL window

- [x] Task 12 — Update `_app.tsx` Admin link to proper TanStack `<Link>` (AC-1)
  - [x] Replace `<a href="#">` with `<Link to="/admin/users">` from `@tanstack/react-router`
  - [x] Keep the `PermissionGate` wrapper (already in place from Story 2.7)

- [x] Task 13 — Update `en.json` with admin user keys
  - [x] Add under `admin.users.*`: `title`, `createButton`, `emailLabel`, `displayNameLabel`, `passwordLabel`, `statusActive`, `statusInactive`, `roleCountLabel`, `confirmDeactivate`, `confirmReactivate`, `emailConflict`, `selfDeactivationError`, `createSuccess`, `deactivateSuccess`, `reactivateSuccess`

- [x] Task 14 — Verify build passes (0 errors, 0 new lint violations)
  - [x] `pnpm tsc --noEmit` from `web/` — 0 TypeScript errors
  - [x] `dotnet build` from `src/` — 0 warnings (TreatWarningsAsErrors)
  - [x] `dotnet test` — all existing 125+ tests pass plus new tests green

### Review Findings

_Code review run on 2026-05-23 (bmad-code-review, 3-reviewer parallel pass: Blind Hunter, Edge Case Hunter, Acceptance Auditor). All 6 acceptance criteria satisfied. 3 decision-needed resolved (1 → defer, 1 → dismiss as intentional, 1 → new patch), 16 patches applied (P1 reclassified as dismiss after verifying InProcessEventBus already isolates subscriber exceptions per Story 2.6 review patch #8), 8 deferred, 12 dismissed. 4 new integration tests added; 152/152 backend tests green; TS 0 errors; vite build successful._

#### Decision-Needed (resolved)

- [x] [Review][Decision → Defer] **Password complexity floor** — Validators currently enforce only `MinimumLength(8)`. Decision: keep min-8 for v1. **Defer reason:** "Story 2.8 v1 issues temporary passwords shared out-of-band by an admin; complexity floor will be raised when end-user self-service password change is introduced." [`CreateUserRequestValidator.cs:17`, `UpdateUserRequestValidator.cs:22`]
- [x] [Review][Decision → Dismiss] **UpdateUser on a deactivated user** — Decision: intentional. Admins can mutate a deactivated user (displayName / password) to support the "reset password, then reactivate" flow. No 409 block; a short documentation comment added to `UpdateUserAsync` notes the design choice. [`UserService.cs:~286-313`]
- [x] [Review][Decision → Patch] **Dead success-toast i18n keys** — Decision: wire `sonner` + toasts now. New patch P17 below covers installation, `<Toaster />` mount, and wiring all five existing keys (`createSuccess` / `updateSuccess` / `deactivateSuccess` / `reactivateSuccess` / `rolesAssignedSuccess`). [`web/src/lib/i18n/locales/en.json:51-55`]

#### Patches

- [x] [Review][P1 → Dismiss] ~~Wrap `bus.Publish(new UserDeactivated(id))` in try/catch + log~~ — Reclassified after verifying `InProcessEventBus.Publish` already isolates each subscriber via try/catch + structured logging (Story 2.6 review patch #8). Subscriber failures cannot bubble back to the publisher; no extra wrap needed. The wider concern (stale cache when handler throws) is already deferred from Story 2.6 review (see `deferred-work.md`).
- [x] [Review][P2 ✅] Idempotent already-inactive deactivate now publishes `UserDeactivated` on every call so a stale cache entry can be busted by retry [`UserService.cs:~318-372`]
- [x] [Review][P3 ✅] Idempotent re-deactivation now always sweeps active refresh tokens — handles the race where a token was minted between two deactivate calls [`UserService.cs:~344-352`]
- [x] [Review][P4 ✅] Added `UserReactivated` domain event + subscriber in `PermissionService.OnUserReactivated`. `ReactivateUserAsync` publishes on every call so the cached `IsActive=false` entry is evicted promptly [`IDomainEventBus.cs:17`, `PermissionService.cs:~229-235`, `UserService.cs:~389-396`]
- [x] [Review][P5 ✅] Refresh-token sweep switched to `ExecuteUpdateAsync` — bounded memory regardless of active-session count; both Deactivate and password-change paths use this [`UserService.cs:~351, 332`]
- [x] [Review][P6 ✅] `UpdateUserAsync` now revokes every active refresh token when `NewPassword` is set, wrapping the password hash update + token sweep in an explicit transaction [`UserService.cs:~309-340`]
- [x] [Review][P7 ✅] `CreateUserAsync` now also catches bare `PostgresException` 23505 on `uq_users_email` for the race window — mirrors the dual-catch already in `AssignRolesAsync` [`UserService.cs:~277-289`]
- [x] [Review][P8 ✅] Both validators now reject whitespace-only `DisplayName` via `.Must(s => !string.IsNullOrWhiteSpace(s))` paired with the existing length rules [`CreateUserRequestValidator.cs:17-26`, `UpdateUserRequestValidator.cs:19-25`]
- [x] [Review][P9 ✅] Both validators now reject whitespace-only `TemporaryPassword` / `NewPassword` — 8 spaces no longer becomes a valid password [`CreateUserRequestValidator.cs:28-34`, `UpdateUserRequestValidator.cs:27-32`]
- [x] [Review][P10 ✅] `UpdateUserAsync` now tracks a `changed` flag and skips the `UpdatedAt` bump (and the SaveChanges round-trip entirely) when no semantic change occurred [`UserService.cs:~301-353`]
- [x] [Review][P11 ✅] `/admin` `beforeLoad` switched to `ensureQueryData(PERMISSIONS_QUERY_KEY)` so a cold-boot non-admin is redirected to `/` before any admin API call fires — eliminates the admin-chrome flash and the leaked 403 [`web/src/routes/_app/admin.tsx:~9-29`]
- [x] [Review][P12 ✅] Zod refine emits a constant i18n KEY (`UPDATE_REQUIRED_KEY`) and the form translates it via `t('admin.users.updateRequired')` before rendering — the literal key string is never displayed [`users.$userId.tsx:~22-37, 218-225`, `en.json:68`]
- [x] [Review][P13 ✅] `UpdateUserForm.submit` now catches mutation rejections and surfaces them via `setError('root', ...)` with distinct messages for 404 (`userNotFoundError`) and generic save failure (`saveError`) — RHF no longer swallows the error [`users.$userId.tsx:~178-194`]
- [x] [Review][P14 ✅] `RoleAssignment.save` is now `async` with try/catch; assignment errors surface inline via `assignError` state with distinct messaging for 404 vs generic failure [`users.$userId.tsx:~270-285, 318-322`]
- [x] [Review][P15 ✅] `onReactivate` / `onDeactivate` now distinguish 404 from network / server error — the admin gets a "user no longer exists, refresh the list" message instead of a generic error when the row was concurrently deleted [`users.$userId.tsx:~64-95`]
- [x] [Review][P16 ✅] Removed the misleading `PERMISSIONS_QUERY_KEY` invalidation from `useDeactivateUserMutation.onSuccess` — the self-deactivation path returns 409 server-side so the invalidation never fired for the case the comment promised [`userMutations.ts`]
- [x] [Review][P17 ✅] (from D3) Installed `sonner` 2.0.5 as a runtime dependency, mounted `<Toaster richColors position="bottom-right" />` in `__root.tsx`, and wired success toasts in all five user mutations using the existing i18n keys (`createSuccess`, `updateSuccess`, `deactivateSuccess`, `reactivateSuccess`, `rolesAssignedSuccess`) [`package.json`, `__root.tsx`, `userMutations.ts`]

##### New integration tests covering the backend patches

- `CreateUser_WhitespaceDisplayName_Returns422` — covers P8.
- `CreateUser_WhitespacePassword_Returns422` — covers P9.
- `UpdateUser_NewPassword_RevokesExistingRefreshTokens` — covers P6.
- `UpdateUser_SameDisplayName_DoesNotBumpUpdatedAt` — covers P10.

#### Deferred

- [x] [Review][Defer] `UsersPage` performs no client-side GUID validation for the `$userId` route param — malformed URL fires an unnecessary network round-trip, server returns 404 [`web/src/routes/_app/admin/users.$userId.tsx:~24-27`] — deferred, minor UX
- [x] [Review][Defer] Create-user form maps only 409 to a field error — 422 / other 4xx collapses to a root error rather than parsing ProblemDetails `code` / `messageKey` [`web/src/routes/_app/admin/users.tsx:~169-175`] — deferred, FluentValidation already client-side covers the common cases
- [x] [Review][Defer] `PermissionService.ComputePermissionsAsync` cannot distinguish "deactivated" from "hard-deleted" — `FirstOrDefaultAsync<bool>` returns default `false` either way [`src/FormForge.Api/Features/Permissions/PermissionService.cs:~127-134`] — deferred, hard-delete is not in current scope
- [x] [Review][Defer] `GetUsersAsync` page-beyond-last returns 200 with empty data instead of 400 [`src/FormForge.Api/Features/Users/UserService.cs:~189-205`] — deferred, admin-only and harmless
- [x] [Review][Defer] `temporaryPassword` is sent in HTTP body and may be logged by request-body logging middleware [`src/FormForge.Api/Features/Users/UserEndpoints.cs:~817`] — deferred, needs investigation of Serilog / correlation logging config
- [x] [Review][Defer] Integration test `GetMyPermissions_AfterDeactivation_ReturnsIsActiveFalse` could be flaky on slow CI — relies on event-bus cache-bust completing synchronously [`src/FormForge.Api.Tests/Features/Users/UserAdminIntegrationTests.cs:~470-518`] — deferred, test stability not a code defect
- [x] [Review][Defer] Admin can change any user's password without step-up auth, audit log entry, or notification to the target user [`src/FormForge.Api/Features/Users/UserService.cs:~1133-1136`] — deferred, production-hardening follow-up
- [x] [Review][Defer] (from D1) **Raise password complexity floor above min 8** — Validators currently enforce only `MinimumLength(8)`; OWASP ASVS L1 baseline is min 12 + class diversity [`CreateUserRequestValidator.cs:17`, `UpdateUserRequestValidator.cs:22`] — deferred, v1 scope: admin-bootstrapped passwords are rotated OOB; revisit when end-user self-service password change is introduced.

---

## Dev Notes

### Backend: Patterns to Follow Exactly

**Outcome enum + Result record pattern** (mirrors `AssignRolesOutcome` / `AssignRolesResult` in `UserService.cs`):
```csharp
internal enum CreateUserOutcome { Success, DuplicateEmail }

internal sealed record CreateUserResult(
    CreateUserOutcome Outcome,
    Guid? UserId = null,
    UserDetailResponse? User = null);
```

**Endpoint handler pattern** (mirrors `RoleEndpoints.cs`):
```csharp
group.MapPost("/", CreateUserHandler)
     .AddValidationFilter<CreateUserRequest>()
     .WithSummary("Create a new user account")
     .Produces<UserDetailResponse>(StatusCodes.Status201Created)
     .Produces(StatusCodes.Status409Conflict)
     .Produces(StatusCodes.Status422UnprocessableEntity);
```

**Error Problem responses** (same extension pattern as `RoleEndpoints.cs`):
```csharp
private static IResult SelfDeactivationProblem() =>
    Results.Problem(
        title: "Cannot deactivate your own account",
        statusCode: StatusCodes.Status409Conflict,
        extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = "SELF_DEACTIVATION",
            ["messageKey"] = "users.selfDeactivation",
        });
```

**Reading current user from JWT in endpoint handler:**
```csharp
private static async Task<IResult> DeactivateUserHandler(
    Guid id,
    IUserService userService,
    HttpContext httpContext,
    CancellationToken ct)
{
    var currentUserIdStr = httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (!Guid.TryParse(currentUserIdStr, out var currentUserId))
        return Results.Problem(statusCode: StatusCodes.Status401Unauthorized);
    // ... delegate to userService.DeactivateUserAsync(id, currentUserId, ct)
}
```

**`DeactivateUserAsync` implementation notes:**
- Revoke refresh tokens with `RevokedAt = DateTimeOffset.UtcNow` before `SaveChangesAsync`. Do NOT use `ExecuteDeleteAsync` (hard delete) — keep tokens in the DB for audit.
- Publish `UserDeactivated(id)` ONLY after the DB commit (not inside the transaction) — same discipline as `bus.Publish(new UserRoleAssignmentChanged(...))` in `UserService.AssignRolesAsync`.
- The `UserDeactivated` event handler in `PermissionService.OnUserDeactivated` already exists and correctly increments `_userVersions[userId]` + calls `_cache.Remove(CacheKey(userId))`.

**`PermissionService.ComputePermissionsAsync` fix:**
```csharp
// Replace: IsActive: true,
// With:
var isActive = await db.Users
    .Where(u => u.Id == userId)
    .Select(u => u.IsActive)
    .FirstOrDefaultAsync(ct)
    .ConfigureAwait(false);
// ...
return new EffectivePermissions(
    UserId: userId,
    ComputedAt: DateTimeOffset.UtcNow,
    IsActive: isActive,   // <-- was hard-coded true
    PerResource: result,
    RoleIds: new HashSet<Guid>(roleIds));
```
This adds one additional DB round-trip per cache miss. Acceptable since this runs only on cold-start per 30 s TTL. If the userId is not found (deleted user with a valid JWT), `FirstOrDefaultAsync` returns `false` (default for bool) → correctly returns inactive.

**Password hasher:** Already exists at `src/FormForge.Api/Features/Auth/PasswordHasher.cs`. Inject `IPasswordHasher` (or the concrete type if registered without interface) the same way `AuthService` does.

**No new EF Core migration needed.** All required columns already exist:
- `users`: `id`, `email`, `display_name`, `password_hash`, `is_active`, `created_at`, `updated_at`
- `refresh_tokens`: `id`, `user_id`, `token_hash`, `expires_at`, `created_at`, `revoked_at`

### Backend: `UserDeactivated` Domain Event

The event is already declared (used by `PermissionService`) and subscribed. Confirm the declaration in `src/FormForge.Api/Infrastructure/EventBus/`:
```csharp
public sealed record UserDeactivated(Guid UserId);
```
The handler in `PermissionService.OnUserDeactivated` bumps `_userVersions[userId]` and evicts the cache key — no changes needed there.

### Frontend: `usePermission.ts` Fix (AC-6)

Current implementation (Story 2.7):
```typescript
export function usePermission(resourceId: string, action: CrudAction): boolean {
  const { data } = usePermissionsQuery()
  if (!data) return false
  if (data.roleIds.includes(PLATFORM_ADMIN_ROLE_ID)) return true   // bypass
  return data.perResource[resourceId]?.[action] ?? false
}
```

**Fix — add `isActive` guard BEFORE the platform-admin bypass:**
```typescript
export function usePermission(resourceId: string, action: CrudAction): boolean {
  const { data } = usePermissionsQuery()
  if (!data) return false
  if (data.isActive === false) return false   // deactivated user — deny all, even admin
  if (data.roleIds.includes(PLATFORM_ADMIN_ROLE_ID)) return true
  return data.perResource[resourceId]?.[action] ?? false
}
```
This closes the deferred item from the Story 2.7 code review. The server also enforces deactivation via the 30 s TTL on the permission cache + the access token's 15 min TTL, so the client-side guard is belt-and-suspenders.

### Frontend: Admin Route Layout + `beforeLoad` Guard

TanStack Router file-based routing — admin area nests under `_app`:

```
web/src/routes/_app/
├── index.tsx         (existing home page)
├── admin.tsx         (NEW — admin layout, beforeLoad guard)
└── admin/
    ├── users.tsx     (NEW — user list + create)
    └── users.$userId.tsx  (NEW — user detail + edit)
```

**`admin.tsx` `beforeLoad` guard** (Architecture minor-gap #7 from epics Story 7.5):
```typescript
import { createFileRoute, redirect, Outlet } from '@tanstack/react-router'
import { PERMISSIONS_QUERY_KEY, PLATFORM_ADMIN_ROLE_ID } from '../../features/auth/usePermissionsQuery'
import type { PermissionsResponse } from '../../features/auth/usePermissionsQuery'

export const Route = createFileRoute('/_app/admin')({
  beforeLoad: ({ context }) => {
    // By the time admin beforeLoad runs, _app beforeLoad has already run (ensured auth).
    // Permissions may or may not be cached yet — use getQueryData (sync, no refetch).
    const data = context.queryClient.getQueryData<PermissionsResponse>(PERMISSIONS_QUERY_KEY)
    // If permissions not yet fetched (cold boot race), allow through — the component
    // renders usePermission() which returns false and shows nothing until permissions load.
    // For a hard guard, throw redirect only when we have data AND the user is not admin.
    if (data && !data.roleIds.includes(PLATFORM_ADMIN_ROLE_ID)) {
      throw redirect({ to: '/' })
    }
  },
  component: AdminLayout,
})

function AdminLayout() {
  return (
    <div>
      {/* Admin sub-nav — full polish in Epic 7 */}
      <Outlet />
    </div>
  )
}
```

**Note:** The `context.queryClient` is available because `__root.tsx` already injects `QueryClient` into the TanStack Router context (inspect `web/src/routes/__root.tsx` for the `RouterContext` interface).

### Frontend: TanStack Query Keys (AR-48)

Follow tuple convention `['scope', 'entity', ...params]`:
- `['admin', 'users']` — user list (prefix for list invalidation)
- `['admin', 'users', page, pageSize]` — paginated user list
- `['admin', 'users', userId]` — single user detail
- `['admin', 'roles']` — role list (for multi-select; reuse existing roles endpoint)

Invalidation pattern on mutations: `queryClient.invalidateQueries({ queryKey: ['admin', 'users'] })` busts all user-scoped queries (both list and detail) via prefix match.

### Frontend: Role Multi-Select for User Detail

For the role assignment on the user detail page, fetch available roles from the existing `GET /api/admin/roles` endpoint (Story 2.4). Use key `['admin', 'roles']`. The response is `PagedResult<RoleListItem>`; for the multi-select, fetch with `pageSize=100` to get all roles (acceptable — role count is small in v1).

### Frontend: Form Pattern (AR-34, AR-4.9)

Admin forms use `react-hook-form` + `zod` resolver with `mode: 'onChange'` (per AR-4.9 for admin forms with interdependencies):
```typescript
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'

const createUserSchema = z.object({
  email: z.string().email().max(320),
  displayName: z.string().min(1).max(200),
  temporaryPassword: z.string().min(8),
})

type CreateUserFormValues = z.infer<typeof createUserSchema>

// In component:
const { register, handleSubmit, setError, formState: { errors } } = useForm<CreateUserFormValues>({
  resolver: zodResolver(createUserSchema),
  mode: 'onChange',
})

// On 409 Conflict from mutation:
mutation.mutate(values, {
  onError: (err) => {
    if (err instanceof ApiError && err.status === 409) {
      setError('email', { type: 'server', message: t('admin.users.emailConflict') })
    }
  }
})
```

### Frontend: `sonner` Toast Setup

If `sonner` is not yet installed, add it before implementing success/error toasts:
```bash
npx shadcn@latest add sonner
```
Then import `toast` from `sonner` in mutations for success confirmations. `<Toaster />` is mounted in `__root.tsx` (add it if not already there).

### File Locations Summary

| File | Path | Action |
|---|---|---|
| `UserService.cs` | `src/FormForge.Api/Features/Users/` | MODIFY — add 5 new methods |
| `UserEndpoints.cs` | `src/FormForge.Api/Features/Users/` | MODIFY — add 5 new endpoints |
| `UserListItem.cs` | `src/FormForge.Api/Features/Users/Dtos/` | CREATE |
| `UserDetailResponse.cs` | `src/FormForge.Api/Features/Users/Dtos/` | CREATE |
| `CreateUserRequest.cs` | `src/FormForge.Api/Features/Users/Dtos/` | CREATE |
| `UpdateUserRequest.cs` | `src/FormForge.Api/Features/Users/Dtos/` | CREATE |
| `CreateUserRequestValidator.cs` | `src/FormForge.Api/Features/Users/Validators/` | CREATE |
| `UpdateUserRequestValidator.cs` | `src/FormForge.Api/Features/Users/Validators/` | CREATE |
| `UserAdminIntegrationTests.cs` | `src/FormForge.Api.Tests/Features/Users/` | CREATE |
| `PermissionService.cs` | `src/FormForge.Api/Features/Permissions/` | MODIFY — `IsActive: isActive` |
| `types.ts` | `web/src/features/admin/users/` | CREATE |
| `useUsersQuery.ts` | `web/src/features/admin/users/` | CREATE |
| `useUserDetailQuery.ts` | `web/src/features/admin/users/` | CREATE |
| `userMutations.ts` | `web/src/features/admin/users/` | CREATE |
| `admin.tsx` | `web/src/routes/_app/` | CREATE — admin layout + guard |
| `users.tsx` | `web/src/routes/_app/admin/` | CREATE — user list + create |
| `users.$userId.tsx` | `web/src/routes/_app/admin/` | CREATE — user detail |
| `usePermission.ts` | `web/src/features/auth/` | MODIFY — isActive guard |
| `_app.tsx` | `web/src/routes/` | MODIFY — Admin `<Link>` |
| `en.json` | `web/src/lib/i18n/locales/` | MODIFY — admin.users.* keys |

### Previous Story Intelligence (Story 2.7)

- `usePermissionsQuery`, `usePermission`, `PermissionGate` are live.
- `PermissionsResponse` shape: `{ userId, computedAt, isActive, perResource: Record<string, CrudFlags>, roleIds: string[] }` — `isActive` exists but was always `true`. Story 2.8 makes it meaningful.
- `useAuthQuery` invalidates `PERMISSIONS_QUERY_KEY` on every token refresh — no new wiring needed for permission re-fetching after deactivation.
- The Admin link in `_app.tsx` is currently `<a href="#">` — Story 2.8 wires it to the real route.
- `PLATFORM_ADMIN_ROLE_ID = '00000000-0000-0000-0000-000000000001'` — already exported from `usePermissionsQuery.ts`.

### Deferred Items Closed by This Story

1. ✅ **`isActive: false` not handled in `usePermission`** (`web/src/features/auth/usePermission.ts:11`, deferred from Story 2.7 code review) — Task 11 adds the `isActive` guard.
2. ✅ **`EffectivePermissions.IsActive` hard-coded `true`** (`src/FormForge.Api/Features/Permissions/PermissionService.cs::ComputePermissionsAsync`, deferred from Story 2.6 code review) — Task 5 reads from `users.is_active`.

### Deferred Items That Remain Deferred After This Story

- `_userVersions` ConcurrentDictionary grows unbounded (`PermissionService.cs:30,189,204,211`) — future permissions-cache hardening pass.
- `usePermissionsQuery` permanent silent denial on transient failure (`usePermissionsQuery.ts:28-32`) — SPA-hardening follow-up story.

### Testing Standards

Backend integration tests (pattern: `UserRoleIntegrationTests.cs`):
- `IClassFixture<WebApplicationFactory<Program>>` + `Testcontainers.PostgreSQL`
- `MigrateAsync()` in `InitializeAsync`; TRUNCATE users/refresh_tokens/roles/user_roles in `InitializeAsync` (must not forget roles table for seeded roles)
- Do NOT truncate seeded system roles (`platform-admin`, `viewer`) — only truncate `user_roles` and user-created data
- Seed a platform-admin user in each test class for authentication
- Test isolation: one test class per feature area, TRUNCATE in InitializeAsync resets state

Frontend: No Vitest tests required for this story (per established precedent in Stories 2.6 and 2.7 — frontend test harness deferred to a future story). Validation is build-time TypeScript + behavioral demonstration in the running UI.

### Git Intelligence (Recent Commits)

```
f33ee88 Story 2.7 code review — apply 2 patches from 3-reviewer parallel pass
ef2351e Story 2.7 — Client-Side Permission Hiding: usePermissionsQuery + usePermission + PermissionGate
f5224c8 Story 2.6 bmad-code-review — apply 5 patches from 3-reviewer parallel pass
5772f20 Story 2.6 code review — apply 7 patches from extra-high recall pass
36640e2 Story 2.6 — Effective permission computation + server-side endpoint authorization
```

Current branch: `story-1-4-followup` — merge or branch from `main` before starting this story.

### References

- [Source: epics.md §Story 2.8] — AC-1 through AC-5 exact specification
- [Source: epics.md §FR-1] — Deactivation revokes refresh tokens immediately
- [Source: epics.md §FR-7 AC-5] — Self-deactivation blocked
- [Source: architecture.md §AR-11] — `UserDeactivated` event evicts permission cache
- [Source: architecture.md §AR-22] — Route group filter chain: auth → rate limit → permission → validation → handler
- [Source: architecture.md §AR-47] — `UserDeactivated` domain event, record type, minimal payload
- [Source: architecture.md §4.6] — Admin routes at `web/src/routes/_app/admin/`
- [Source: architecture.md §4.9] — Admin forms use `mode: 'onChange'`
- [Source: architecture.md §AR-48] — TanStack Query key tuple convention
- [Source: deferred-work.md §2.7] — `isActive: false` guard in `usePermission`; permanent denial on transient failure
- [Source: deferred-work.md §2.6] — `EffectivePermissions.IsActive` hard-coded `true`
- [Source: src/FormForge.Api/Features/Users/UserService.cs] — `AssignRolesAsync` pattern; `UserDeactivated` event; `PlatformAdminRoleId`
- [Source: src/FormForge.Api/Features/Roles/RoleEndpoints.cs] — endpoint registration pattern with `AddValidationFilter`, `Produces`, private helper methods
- [Source: src/FormForge.Api/Features/Permissions/PermissionService.cs] — `ComputePermissionsAsync`, `OnUserDeactivated`, `CacheKey` method
- [Source: src/FormForge.Api/Features/Users/UserEndpoints.cs] — existing `MapUserAdminEndpoints`, comment about future stories extending this class
- [Source: web/src/features/auth/usePermission.ts] — current implementation to modify
- [Source: web/src/routes/_app.tsx] — Admin link placeholder + `usePermissionsQuery()` / `usePermission()` usage pattern
- [Source: web/src/features/auth/httpClient.ts] — `httpClient.get/post/put`, `ApiError`
- [Source: web/src/routes/login.tsx] — form pattern (react-hook-form + zod + setError on API error)

---

## Dev Agent Record

### Agent Model Used

Claude Opus 4.7 (`claude-opus-4-7[1m]`)

### Debug Log References

- Backend integration tests: 21 new tests added (all green) — `dotnet test --filter "FullyQualifiedName~UserAdminIntegrationTests"` reports 21/21 pass in ~36 s.
- Full test suite: 148 tests pass (127 pre-existing + 21 new), 0 failures, 0 skipped.
- Frontend `npx tsc --noEmit`: 0 errors.
- Frontend `vite build && tsc -b --noEmit`: succeeds; emits `users-*.js`, `users._userId-*.js`, `admin-*.js` chunks.
- `dotnet build` (full solution): 0 warnings, 0 errors (TreatWarningsAsErrors enabled).
- `eslint .`: pre-existing `react-refresh/only-export-components` errors persist on every route file (inherent to TanStack Router file-based routing: `Route` is a non-component export co-located with the component). The new route files match this established codebase convention. No new violation **kinds** introduced.

### Completion Notes List

- **AC-1** satisfied: `/api/admin/users` returns `PagedResult<UserListItem>` (id, email, displayName, isActive, roleCount, createdAt) with the standard `?page=1&pageSize=25` convention. UI table renders these columns at `/admin/users`. Pagination state lives in URL search params via TanStack Router `validateSearch`.
- **AC-2** satisfied: Create-user dialog at `/admin/users` validates with Zod (email + displayName + min-8 password); 409 duplicate-email response is surfaced as an inline field error via `setError('email', ...)`. No email is sent — server returns the user record and the admin shares the password out-of-band.
- **AC-3** satisfied: `PUT /api/admin/users/{id}/deactivate` flips `IsActive=false`, revokes all active refresh tokens via `RevokedAt = UtcNow` (preserves audit trail), then publishes `UserDeactivated(id)` *after* the commit. `PermissionService.OnUserDeactivated` evicts the permission cache. Reactivate is the symmetric inverse but does NOT re-mint refresh tokens.
- **AC-4** satisfied: `DeactivateUserAsync` returns `SelfDeactivation` before any DB work when `id == currentUserId`; endpoint maps this to 409 with `code: SELF_DEACTIVATION` and `messageKey: users.selfDeactivation`. Client surfaces the message as an inline error on the deactivate button (not a toast). Verified by `DeactivateUser_Self_Returns409SelfDeactivation`.
- **AC-5** satisfied: User detail page renders a checkbox-based multi-select of roles (`useRolesQuery` fetches `/api/admin/roles?page=1&pageSize=100`) and submits via `PUT /api/admin/users/{id}/roles` using the existing Story 2.5 endpoint — no reimplementation here. Component remounts on role-set change via React `key` to keep the draft state in sync with server state without `setState`-in-effect.
- **AC-6 (closes deferred items)**:
  - Server: `PermissionService.ComputePermissionsAsync` now reads `users.is_active` from the DB instead of hard-coding `true`. One extra round-trip per cache miss (acceptable given 30 s TTL). Verified by `GetMyPermissions_AfterDeactivation_ReturnsIsActiveFalse`.
  - Client: `usePermission` returns `false` for every check when `data.isActive === false`, short-circuiting *before* the platform-admin bypass — so a deactivated admin holding a still-valid JWT sees no UI controls for the remaining TTL window.

#### Deviations from spec

- **JWT claim name for `currentUserId`**: the story spec example uses `ClaimTypes.NameIdentifier`, but the project uniformly issues `"userId"` as a custom claim (`JwtTokenService`) and `Program.cs` sets `MapInboundClaims = false` + `NameClaimType = "userId"`. `PermissionsEndpoints` and `RouteGroupExtensions` both read `"userId"` directly. The `DeactivateUserHandler` follows the project convention (`httpContext.User.FindFirst("userId")?.Value`) rather than the spec example, ensuring claim resolution is consistent across the codebase.
- **Roles query lives in a new sibling folder** `web/src/features/admin/roles/` (just `useRolesQuery`) rather than buried inside `admin/users/`. Cleaner for Story 2.9 which will own the full role-management UI.

#### Items intentionally left out of scope (remain deferred)

- `_userVersions` unbounded growth in `PermissionService` — explicitly listed as remaining-deferred in story Dev Notes.
- `usePermissionsQuery` permanent silent denial on transient failure — explicitly listed as remaining-deferred.

### File List

#### Backend — Created

- `src/FormForge.Api/Features/Users/Dtos/UserListItem.cs`
- `src/FormForge.Api/Features/Users/Dtos/UserDetailResponse.cs`
- `src/FormForge.Api/Features/Users/Dtos/CreateUserRequest.cs`
- `src/FormForge.Api/Features/Users/Dtos/UpdateUserRequest.cs`
- `src/FormForge.Api/Features/Users/Validators/CreateUserRequestValidator.cs`
- `src/FormForge.Api/Features/Users/Validators/UpdateUserRequestValidator.cs`
- `src/FormForge.Api.Tests/Features/Users/UserAdminIntegrationTests.cs`

#### Backend — Modified

- `src/FormForge.Api/Features/Users/UserService.cs` — added 5 new methods + outcome enums/result records; added `IPasswordHasher` to primary constructor.
- `src/FormForge.Api/Features/Users/UserEndpoints.cs` — added 6 new endpoint mappings (`GET /`, `GET /{id}`, `POST /`, `PUT /{id}`, `PUT /{id}/deactivate`, `PUT /{id}/reactivate`); kept the existing `PUT /{id}/roles` from Story 2.5; introduced `UserNotFoundProblem`, `UserEmailConflictProblem`, `SelfDeactivationProblem` private helpers.
- `src/FormForge.Api/Features/Permissions/PermissionService.cs` — `ComputePermissionsAsync` now reads `users.is_active`.
- `src/FormForge.Api/Program.cs` — registered `CreateUserRequestValidator` + `UpdateUserRequestValidator` in DI.

#### Frontend — Created

- `web/src/features/admin/users/types.ts`
- `web/src/features/admin/users/useUsersQuery.ts`
- `web/src/features/admin/users/useUserDetailQuery.ts`
- `web/src/features/admin/users/userMutations.ts`
- `web/src/features/admin/roles/useRolesQuery.ts`
- `web/src/routes/_app/admin.tsx` — admin layout + `beforeLoad` platform-admin guard.
- `web/src/routes/_app/admin/users.tsx` — user list + create dialog.
- `web/src/routes/_app/admin/users.$userId.tsx` — user detail + edit form + deactivate/reactivate + role multi-select.

#### Frontend — Modified

- `web/src/features/auth/usePermission.ts` — added `isActive === false` short-circuit before the platform-admin bypass.
- `web/src/routes/_app.tsx` — Admin link converted from `<a href="#">` to TanStack `<Link to="/admin/users">`.
- `web/src/lib/i18n/locales/en.json` — added `admin.users.*` keys.
- `web/src/routeTree.gen.ts` — auto-regenerated by TanStack Router plugin during `vite build`.

#### Sprint tracking — Modified

- `_bmad-output/implementation-artifacts/sprint-status.yaml` — story status transitions: `ready-for-dev → in-progress → review`.

## Change Log

| Date | Author | Change |
|---|---|---|
| 2026-05-23 | Amelia (dev agent) | Story 2.8 implementation complete — admin user CRUD endpoints + UI; deferred items from Story 2.6 (server `IsActive`) and Story 2.7 (client `isActive` guard) closed. 21 new integration tests; 148/148 backend tests pass. |
| 2026-05-23 | bmad-code-review | 3-reviewer parallel pass (Blind Hunter / Edge Case Hunter / Acceptance Auditor); 16 patches applied (1 false-positive dismissed, 1 spec-decision deferred, 1 design call documented). Backend: P2–P10 + D2 doc — refactored `DeactivateUserAsync` to be idempotent-with-event + always-sweep-tokens via `ExecuteUpdateAsync` in an explicit transaction; `UpdateUserAsync` now revokes refresh tokens on password change and skips no-op `UpdatedAt` bumps; added `UserReactivated` domain event + `PermissionService.OnUserReactivated` cache bust; validators reject whitespace-only inputs; `CreateUserAsync` adds bare `PostgresException` defensive catch. Frontend: P11–P17 — `/admin` `beforeLoad` switched to `ensureQueryData` (no admin-chrome leak); `UpdateUserForm` / `RoleAssignment` / `onDeactivate` / `onReactivate` now distinguish 404 vs generic error and surface mutation failures via i18n keys; Zod refine emits an i18n key translated at render; removed misleading `PERMISSIONS_QUERY_KEY` invalidation; installed `sonner` + `<Toaster />` and wired all five success toasts to the existing `admin.users.*Success` keys. 4 new integration tests; 152/152 backend tests green; TS 0 errors; vite build OK. Story → `done`. |
