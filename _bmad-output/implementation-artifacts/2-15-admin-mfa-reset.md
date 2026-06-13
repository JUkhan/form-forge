# Story 2.15: Admin MFA Reset

Status: done

## Story

As a Platform Admin,
I want to disable MFA for any user account,
so that I can restore access for a user who has lost both their authenticator device and their backup codes.

## Acceptance Criteria

1. **AC-1 — DELETE endpoint clears MFA state**
   Given I am authenticated as a Platform Admin,
   When I `DELETE /api/admin/users/{userId}/mfa`,
   Then the user's `mfa_enabled` is set to `false`, `mfa_secret_protected` is cleared to `null`, all rows in `mfa_backup_codes` for that user are deleted, all active refresh tokens for the affected user are revoked, and HTTP 200 is returned.

2. **AC-2 — Authorization guard**
   Given the requester does not have the `platform-admin` role,
   When they call `DELETE /api/admin/users/{userId}/mfa`,
   Then HTTP 403 is returned (enforced by the existing `/api/admin/*` route-group guard — no per-handler code needed).

3. **AC-3 — User not found**
   Given no user exists with `{userId}`,
   When I call `DELETE /api/admin/users/{userId}/mfa`,
   Then HTTP 404 is returned.

4. **AC-4 — Idempotency**
   Given a user with `mfa_enabled = false` (MFA never enrolled or already reset),
   When I call `DELETE /api/admin/users/{userId}/mfa`,
   Then HTTP 200 is returned with no error (idempotent).

5. **AC-5 — "Reset MFA" button on user detail view**
   Given I open Admin > Users > {user} for a user with `mfa_enabled = true`,
   When the page renders,
   Then a "Reset MFA" button is visible.
   When I click it, a confirmation dialog appears that I must accept.
   When I accept, the `DELETE` is issued and on success the page refreshes to show MFA as no longer enabled.

6. **AC-6 — MFA disabled after reset**
   Given the reset succeeds,
   When the affected user next logs in,
   Then the login flow proceeds to JWT issuance without any TOTP challenge (`mfa_enabled = false` → `LoginAsync` skips the MFA gate).

## Tasks / Subtasks

- [x] Task 1: Backend — `UserDetailResponse` exposes `MfaEnabled` (AC-5)
  - [x] Add `bool MfaEnabled` to `UserDetailResponse` record in `Dtos/UserDetailResponse.cs`
  - [x] Add `u.MfaEnabled` to the projection in `UserService.GetUserAsync`

- [x] Task 2: Backend — `ResetUserMfaAsync` in `UserService` (AC-1, AC-3, AC-4)
  - [x] Add `AdminMfaResetOutcome` enum + `AdminMfaResetResult` record to `UserService.cs` (above `IUserService`)
  - [x] Add `Task<AdminMfaResetResult> ResetUserMfaAsync(Guid userId, CancellationToken ct)` to `IUserService`
  - [x] Implement `ResetUserMfaAsync` in `UserService`: load user (→ NotFound), transaction: clear fields + `ExecuteDeleteAsync` backup codes + `ExecuteUpdateAsync` refresh tokens

- [x] Task 3: Backend — `DELETE /{id:guid}/mfa` endpoint in `UserEndpoints` (AC-1, AC-2, AC-3)
  - [x] Register endpoint in `MapUserAdminEndpoints`
  - [x] Implement `ResetUserMfaHandler`

- [x] Task 4: Frontend — `UserDetail` type gains `mfaEnabled` (AC-5)
  - [x] Add `mfaEnabled: boolean` to `UserDetail` interface in `web/src/features/admin/users/types.ts`

- [x] Task 5: Frontend — `useResetMfaMutation` in `userMutations.ts` (AC-5)
  - [x] Add `useResetMfaMutation(userId: string)` to `web/src/features/admin/users/userMutations.ts`

- [x] Task 6: Frontend — "Reset MFA" button + confirmation dialog in `users.$userId.tsx` (AC-5)
  - [x] Add imports: `useResetMfaMutation` + `Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter, DialogDescription` from `@/components/ui/dialog`
  - [x] Add `resetMfaDialogOpen` + `resetMfaError` state
  - [x] Render "Reset MFA" button when `user.mfaEnabled === true` (in the header button row)
  - [x] Render confirmation dialog with cancel/confirm buttons
  - [x] Handle success (dialog closes, query invalidates) and error (inline error message)

- [x] Task 7: i18n — add keys to `admin.users` (AC-5)
  - [x] Add `resetMfaButton`, `resetMfaDialogTitle`, `resetMfaDialogBody`, `resetMfaDialogConfirm`, `resetMfaDialogCancel`, `resetMfaSuccess`, `resetMfaError` in `web/src/lib/i18n/locales/en.json`

- [x] Task 8: Integration tests in `UserAdminIntegrationTests.cs` (AC-1–AC-4, AC-6)
  - [x] `ResetMfa_Returns200AndClearsMfaState` (AC-1)
  - [x] `ResetMfa_RevokesAllRefreshTokens` (AC-1)
  - [x] `ResetMfa_NonAdmin_Returns403` (AC-2)
  - [x] `ResetMfa_UnknownUser_Returns404` (AC-3)
  - [x] `ResetMfa_UserWithMfaDisabled_IsIdempotent` (AC-4)

## Dev Notes

### Architecture Reference (AR-56, Decision 2.12)

From `_bmad-output/planning-artifacts/architecture.md` — Decision 2.12:
> **Admin reset:** `DELETE /api/admin/users/{userId}/mfa` — clears `mfa_enabled`, `mfa_secret_protected`, and all backup codes.

Per the epics AC (AC-1): also revoke all active refresh tokens so the user cannot continue a live session without MFA.

---

### Task 1: `UserDetailResponse` — Add `MfaEnabled`

**`src/FormForge.Api/Features/Users/Dtos/UserDetailResponse.cs`** (UPDATE — add `MfaEnabled` as last field):
```csharp
namespace FormForge.Api.Features.Users.Dtos;

internal sealed record UserRoleItem(Guid Id, string Name);

internal sealed record UserDetailResponse(
    Guid Id,
    string Email,
    string DisplayName,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<UserRoleItem> Roles,
    bool MfaEnabled); // Story 2.15
```

**`UserService.GetUserAsync` projection** (UPDATE — add `MfaEnabled: u.MfaEnabled` as the last property in the `new UserDetailResponse(...)` call):
```csharp
return await db.Users
    .Where(u => u.Id == id)
    .Select(u => new UserDetailResponse(
        u.Id,
        u.Email,
        u.DisplayName,
        u.IsActive,
        u.CreatedAt,
        u.UpdatedAt,
        u.UserRoles
            .OrderBy(ur => ur.Role.Name)
            .Select(ur => new UserRoleItem(ur.RoleId, ur.Role.Name))
            .ToList(),
        u.MfaEnabled)) // Story 2.15
    .FirstOrDefaultAsync(ct)
    .ConfigureAwait(false);
```

**WARNING:** `UserDetailResponse` is a positional record — the new `MfaEnabled` parameter must be the last positional argument, and the call sites (including `CreateUserAsync` which constructs it inline) must pass a value. Update `CreateUserAsync`'s inline construction:
```csharp
// In CreateUserAsync, the UserDetailResponse construction — append MfaEnabled: false
var detail = new UserDetailResponse(
    user.Id,
    user.Email,
    user.DisplayName,
    user.IsActive,
    user.CreatedAt,
    user.UpdatedAt,
    [],
    false); // MfaEnabled — new user never has MFA at creation time
```

---

### Task 2: `UserService.cs` — `ResetUserMfaAsync`

**Add new outcome/result before `IUserService`** (after `ReactivateUserResult`, before `interface IUserService`):
```csharp
internal enum AdminMfaResetOutcome { Success, NotFound }

internal sealed record AdminMfaResetResult(AdminMfaResetOutcome Outcome);
```

**Add to `IUserService` interface** (after `ReactivateUserAsync`):
```csharp
Task<AdminMfaResetResult> ResetUserMfaAsync(Guid userId, CancellationToken ct);
```

**Implementation** (add to `UserService` class, after `ReactivateUserAsync`):
```csharp
public async Task<AdminMfaResetResult> ResetUserMfaAsync(Guid userId, CancellationToken ct)
{
    var user = await db.Users
        .FirstOrDefaultAsync(u => u.Id == userId, ct)
        .ConfigureAwait(false);

    if (user is null)
        return new AdminMfaResetResult(AdminMfaResetOutcome.NotFound);

    var now = DateTimeOffset.UtcNow;

    // Wrap all mutations in a transaction so a partial failure cannot leave
    // the user row cleared while backup codes or refresh tokens remain live.
    var txn = await db.Database
        .BeginTransactionAsync(ct)
        .ConfigureAwait(false);
    try
    {
        // Idempotent: always clear — already-false fields are no-ops in SaveChangesAsync.
        user.MfaEnabled = false;
        user.MfaSecretProtected = null;
        user.UpdatedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Bulk delete all backup codes for this user (no audit trail needed — codes are
        // cryptographic material, not business records; the user row's UpdatedAt records
        // the reset timestamp). ExecuteDeleteAsync issues a single DELETE WHERE user_id=X.
        await db.MfaBackupCodes
            .Where(c => c.UserId == userId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        // Revoke all active refresh tokens so the user must re-authenticate.
        // Mirrors DeactivateUserAsync's token sweep (Story 2.8). Use RevokedAt not
        // ExecuteDeleteAsync — preserves audit history for compliance.
        await db.RefreshTokens
            .Where(rt => rt.UserId == userId && rt.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(rt => rt.RevokedAt, now), ct)
            .ConfigureAwait(false);

        await txn.CommitAsync(ct).ConfigureAwait(false);
    }
    finally
    {
        await txn.DisposeAsync().ConfigureAwait(false);
    }

    return new AdminMfaResetResult(AdminMfaResetOutcome.Success);
}
```

**No new `using` needed** — `UserService.cs` already has `Microsoft.EntityFrameworkCore`, `FormForge.Api.Domain.Entities`, and `FormForge.Api.Infrastructure.Persistence`. The `ExecuteDeleteAsync` extension is part of `Microsoft.EntityFrameworkCore` (EF Core 7+).

---

### Task 3: `UserEndpoints.cs` — New Endpoint

**Add to `MapUserAdminEndpoints`** (after the `AssignRolesHandler` registration, before `return group;`):
```csharp
group.MapDelete("/{id:guid}/mfa", ResetUserMfaHandler)
     .WithSummary("Admin: disable MFA, revoke all sessions, and delete all backup codes for a user")
     .Produces(StatusCodes.Status200OK)
     .Produces(StatusCodes.Status404NotFound);
```

**`ResetUserMfaHandler`** (add as private static method, alongside the other handlers):
```csharp
private static async Task<IResult> ResetUserMfaHandler(
    Guid id,
    IUserService userService,
    CancellationToken ct)
{
    ArgumentNullException.ThrowIfNull(userService);

    var result = await userService.ResetUserMfaAsync(id, ct).ConfigureAwait(false);
    return result.Outcome switch
    {
        AdminMfaResetOutcome.NotFound => UserNotFoundProblem(),
        AdminMfaResetOutcome.Success => Results.Ok(),
        _ => Results.Problem(
            detail: "Something went wrong. Please try again.",
            statusCode: StatusCodes.Status500InternalServerError,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = "errors.genericError",
            }),
    };
}
```

**AC-2 authorization note:** The `/api/admin/*` route group in `Program.cs` requires the `platform-admin` role at the group level (from Story 2.6). No per-handler `RequireAuthorization` needed — the 403 is already enforced.

**HTTP 200 (not 204):** The epic AC explicitly states "HTTP 200 is returned". Use `Results.Ok()` (200 with empty body) not `Results.NoContent()` (204). This differs from the deactivate/reactivate pattern (those return 204) — follow the spec.

**No rate limiting needed** — this is a low-frequency admin operation; no rate-limit policy analogous to `auth-*` patterns is required.

---

### Task 4: Frontend — `types.ts`

**`web/src/features/admin/users/types.ts`** (UPDATE — add `mfaEnabled` to `UserDetail`):
```typescript
export interface UserDetail {
  id: string
  email: string
  displayName: string
  isActive: boolean
  createdAt: string
  updatedAt: string | null
  roles: UserRoleItem[]
  mfaEnabled: boolean  // Story 2.15
}
```

`CreateUserResponse extends UserDetail` — it inherits `mfaEnabled` automatically. No other type changes needed.

---

### Task 5: Frontend — `userMutations.ts`

**Add `useResetMfaMutation`** (after `useAssignUserRolesMutation`, at the bottom of the file):
```typescript
export function useResetMfaMutation(userId: string) {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation({
    mutationFn: () => httpClient.delete<void>(`/api/admin/users/${userId}/mfa`),
    onSuccess: () => {
      invalidateAllUsers(queryClient)
      toast.success(t('admin.users.resetMfaSuccess'))
    },
  })
}
```

No new imports needed — `useMutation`, `useQueryClient`, `toast`, `useTranslation`, `httpClient`, and `invalidateAllUsers` are all already present.

---

### Task 6: Frontend — `users.$userId.tsx`

**New imports to ADD** (merge with existing imports at the top):
```typescript
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { useResetMfaMutation } from '../../../features/admin/users/userMutations'
```

**Check existing imports** — `useDeactivateUserMutation`, `useReactivateUserMutation` are already imported from `userMutations`. Add `useResetMfaMutation` to the existing named import from that file.

**New state + mutation** (add inside `UserDetailPage`, after existing mutation declarations):
```typescript
const resetMfaMutation = useResetMfaMutation(userId)
const [resetMfaDialogOpen, setResetMfaDialogOpen] = useState(false)
const [resetMfaError, setResetMfaError] = useState<string | null>(null)
```

**`onResetMfa` handler** (add after `onReactivate`):
```typescript
const onResetMfa = async () => {
  setResetMfaError(null)
  try {
    await resetMfaMutation.mutateAsync()
    setResetMfaDialogOpen(false)
  } catch {
    setResetMfaError(t('admin.users.resetMfaError'))
  }
}
```

**"Reset MFA" button** — add to the header button row (the `<div className="flex items-center gap-2">` that already contains Deactivate/Reactivate). Add this **before** the existing active/inactive toggle button:
```tsx
{user.mfaEnabled && (
  <Button
    type="button"
    variant="outline"
    size="sm"
    onClick={() => setResetMfaDialogOpen(true)}
  >
    {t('admin.users.resetMfaButton')}
  </Button>
)}
```

**Confirmation dialog** — add just before the closing `</div>` of `UserDetailPage`'s outer `<div className="space-y-6">` (or at the very end before the final `return` closes). The Dialog component renders via a portal so placement in the JSX tree does not affect layout:
```tsx
<Dialog open={resetMfaDialogOpen} onOpenChange={setResetMfaDialogOpen}>
  <DialogContent className="max-w-md">
    <DialogHeader>
      <DialogTitle>{t('admin.users.resetMfaDialogTitle')}</DialogTitle>
      <DialogDescription>{t('admin.users.resetMfaDialogBody')}</DialogDescription>
    </DialogHeader>
    {resetMfaError && (
      <p role="alert" className="text-xs text-destructive">{resetMfaError}</p>
    )}
    <DialogFooter>
      <Button
        type="button"
        variant="outline"
        onClick={() => {
          setResetMfaDialogOpen(false)
          setResetMfaError(null)
        }}
        disabled={resetMfaMutation.isPending}
      >
        {t('admin.users.resetMfaDialogCancel')}
      </Button>
      <Button
        type="button"
        variant="destructive"
        onClick={() => { void onResetMfa() }}
        disabled={resetMfaMutation.isPending}
      >
        {resetMfaMutation.isPending
          ? t('admin.users.loading')
          : t('admin.users.resetMfaDialogConfirm')}
      </Button>
    </DialogFooter>
  </DialogContent>
</Dialog>
```

**`useState` import** — already present (`import { useEffect, useState } from 'react'`). No change needed.

---

### Task 7: i18n Strings

**`web/src/lib/i18n/locales/en.json`** — inside the `"admin"` → `"users"` object, add after `"roleAssignError"`:
```json
"resetMfaButton": "Reset MFA",
"resetMfaDialogTitle": "Reset MFA",
"resetMfaDialogBody": "This will disable two-factor authentication, revoke all active sessions, and delete all backup codes for this user. They will need to re-enrol to use MFA again.",
"resetMfaDialogConfirm": "Yes, reset MFA",
"resetMfaDialogCancel": "Cancel",
"resetMfaSuccess": "MFA has been reset. The user will be signed out of all sessions.",
"resetMfaError": "Could not reset MFA. Please try again."
```

---

### Task 8: Integration Tests

**Add to `UserAdminIntegrationTests.cs`** — new private MFA setup helpers (add alongside existing `LoginAsync` helpers):

```csharp
// MFA setup helpers — mirrors AuthIntegrationTests pattern (Story 2.13/2.14)
// OtpNet is already referenced in FormForge.Api.Tests.csproj (added in Story 2.13).
// Add `using OtpNet;` at the top if not already present; check existing usings first.

private async Task<MfaEnrolResponseDto> EnrolMfaAsync(string bearerToken)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, "/api/users/me/mfa/enrol");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
    using var response = await _client!.SendAsync(request);
    response.EnsureSuccessStatusCode();
    return (await response.Content.ReadFromJsonAsync<MfaEnrolResponseDto>())!;
}

private async Task ConfirmMfaEnrolAsync(string bearerToken, string code)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/users/me/mfa/verify")
    {
        Content = JsonContent.Create(new { code }),
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
    using var response = await _client!.SendAsync(request);
    response.EnsureSuccessStatusCode();
}

[SuppressMessage("Performance", "CA1812", Justification = "Instantiated by System.Text.Json.")]
private sealed record MfaEnrolResponseDto(string Secret, string QrCodeDataUrl, string[] BackupCodes);
```

**5 new test methods:**

```csharp
// ---------- Story 2.15: Admin MFA Reset ----------

[Fact]
public async Task ResetMfa_Returns200AndClearsMfaState()
{
    // Setup: enrol MFA for the viewer user
    var viewerToken = await LoginAsync("viewer@example.com", "Password1!");
    var enrol = await EnrolMfaAsync(viewerToken);
    var confirmCode = new Totp(Base32Encoding.ToBytes(enrol.Secret)).ComputeTotp();
    await ConfirmMfaEnrolAsync(viewerToken, confirmCode);

    // Admin resets MFA
    var adminToken = await LoginAsync("admin@example.com", "Password1!");
    using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/users/{_viewerUserId}/mfa");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
    using var response = await _client!.SendAsync(request);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

    // Verify DB state: mfa_enabled=false, mfa_secret=null, backup codes deleted
    using var scope = _factory!.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
    var viewer = await db.Users.FirstAsync(u => u.Id == _viewerUserId);
    Assert.False(viewer.MfaEnabled);
    Assert.Null(viewer.MfaSecretProtected);
    var backupCodeCount = await db.MfaBackupCodes.CountAsync(c => c.UserId == _viewerUserId);
    Assert.Equal(0, backupCodeCount);
}

[Fact]
public async Task ResetMfa_RevokesAllRefreshTokens()
{
    // Setup: enrol MFA, then get a refresh token by logging in again after enrolment
    var viewerToken = await LoginAsync("viewer@example.com", "Password1!");
    var enrol = await EnrolMfaAsync(viewerToken);
    var confirmCode = new Totp(Base32Encoding.ToBytes(enrol.Secret)).ComputeTotp();
    await ConfirmMfaEnrolAsync(viewerToken, confirmCode);
    // The initial login issued a refresh token; enrolment does not issue a new one.

    // Admin resets MFA
    var adminToken = await LoginAsync("admin@example.com", "Password1!");
    using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/users/{_viewerUserId}/mfa");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
    using var response = await _client!.SendAsync(request);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

    // Verify no active refresh tokens remain for the viewer
    using var scope = _factory!.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
    var liveTokens = await db.RefreshTokens
        .CountAsync(rt => rt.UserId == _viewerUserId && rt.RevokedAt == null);
    Assert.Equal(0, liveTokens);
}

[Fact]
public async Task ResetMfa_NonAdmin_Returns403()
{
    var viewerToken = await LoginAsync("viewer@example.com", "Password1!");
    using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/users/{_viewerUserId}/mfa");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
    using var response = await _client!.SendAsync(request);
    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
}

[Fact]
public async Task ResetMfa_UnknownUser_Returns404()
{
    var adminToken = await LoginAsync("admin@example.com", "Password1!");
    using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/users/{Guid.NewGuid()}/mfa");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
    using var response = await _client!.SendAsync(request);
    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
}

[Fact]
public async Task ResetMfa_UserWithMfaDisabled_IsIdempotent()
{
    // Viewer has MFA disabled by default (no enrolment)
    var adminToken = await LoginAsync("admin@example.com", "Password1!");
    using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/users/{_viewerUserId}/mfa");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
    using var response = await _client!.SendAsync(request);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
}
```

**`using` additions for `UserAdminIntegrationTests.cs`:**
```csharp
using OtpNet; // already in FormForge.Api.Tests.csproj from Story 2.13
using System.Net.Http.Headers; // check if already present; add if not
```

**Test baseline:** 757 passed / 2 failed (pre-existing audit DELETE→405 tests). New tests target 757 + 5 = 762 passed.

---

### Anti-Pattern Prevention

**DO NOT** use `ExecuteDeleteAsync` for refresh tokens — use `ExecuteUpdateAsync` to stamp `RevokedAt`. The audit trail of revoked tokens must survive; only backup codes (cryptographic material) are hard-deleted.

**DO NOT** use `Results.NoContent()` (204) — the epic AC explicitly says HTTP 200. Use `Results.Ok()`.

**DO NOT** add a rate-limit policy or `AddValidationFilter` to the MFA reset endpoint — no request body, no rate-limiting concern on an admin-only route.

**DO NOT** skip the transaction** — clearing the user row + deleting backup codes + revoking refresh tokens are three separate DB operations. A partial failure without a transaction could leave backup codes live while `mfa_enabled = false`, or tokens live while the secret is cleared.

**DO NOT** add `[Authorize(Roles = "platform-admin")]` to `ResetUserMfaHandler` — the route group `MapAdminEndpoints` already has a group-level `RequireAuthorization("platform-admin")` policy applied in `Program.cs`. Adding it again on the handler creates a confusing double guard.

**DO NOT** try to add `DialogClose` to the cancel button — use the `onClick` handler pattern (sets `setResetMfaDialogOpen(false)`) consistent with `settings.tsx`. `DialogClose` as a wrapper adds unnecessary radix event complexity.

**DO NOT** add `mfaEnabled` to `UserListItem` — the list view does not display MFA status. Only `UserDetailResponse` (and its frontend `UserDetail` type) needs the field.

**DO NOT** reinvent the MFA setup helpers in tests — the `EnrolMfaAsync` / `ConfirmMfaEnrolAsync` pattern is the same used in `AuthIntegrationTests.cs`. Copy the helpers rather than re-calling through the service layer.

**DO NOT** forget to update `CreateUserAsync`'s inline `UserDetailResponse` construction — the positional record gains a new `bool MfaEnabled` parameter, so the call with 7 args will fail to compile. Pass `false` (new users have no MFA).

**DO NOT** clear `user.UpdatedAt` — stamp it with `DateTimeOffset.UtcNow`. This is required so the admin user list shows "last updated" reflecting the reset operation.

---

### Previous Story Learnings (2.13, 2.14)

- **`ConfigureAwait(false)` on every `await` in C#** — the build analyzer (`CA2007`) enforces this. Every new `await` in `UserService.cs` and `UserEndpoints.cs` needs `.ConfigureAwait(false)`.
- **`ArgumentNullException.ThrowIfNull`** — required on `IUserService userService` in `ResetUserMfaHandler`.
- **Transaction `DisposeAsync` in `finally`** — the pattern used by `DeactivateUserAsync` and `AssignRolesAsync` (explicit `try/finally`, not `await using`) is required to avoid CA2007 on the implicit `DisposeAsync`.
- **`ExecuteDeleteAsync` / `ExecuteUpdateAsync` for bulk mutations** — used in `UpdateUserAsync` (token revoke) and `MfaService.VerifyMfaLoginAsync` (backup code stamp). Same EF Core 7+ API — no new library needed.
- **`void` prefix pattern for async event handlers in TSX** — `onClick={() => { void onResetMfa() }}` matches project style.
- **Toast on success, not on open/confirm** — the toast fires in `onSuccess` of `useMutation`, not in the click handler. Mirrors `useDeactivateUserMutation`.
- **Pre-existing test failures**: 2 DELETE→405 audit tests + 1 i18n-lint failure — ignore. Do not investigate.
- **`[SuppressMessage("Performance", "CA1812")]`** — required on new private sealed record DTOs used only in test deserialization (`MfaEnrolResponseDto`).
- **Primary constructor syntax** — `UserService` uses primary constructor. The `db` dependency is already declared via the constructor parameter. No new field declarations needed.

---

### Files to CREATE

None — all changes are modifications to existing files.

### Files to MODIFY

| File | Change |
|---|---|
| `src/FormForge.Api/Features/Users/Dtos/UserDetailResponse.cs` | Add `bool MfaEnabled` as last positional parameter |
| `src/FormForge.Api/Features/Users/UserService.cs` | Add `AdminMfaResetOutcome` enum + `AdminMfaResetResult` record + `IUserService` method + `ResetUserMfaAsync` implementation + update `GetUserAsync` projection + update `CreateUserAsync` inline construction |
| `src/FormForge.Api/Features/Users/UserEndpoints.cs` | Register `DELETE /{id:guid}/mfa` endpoint; add `ResetUserMfaHandler` |
| `web/src/features/admin/users/types.ts` | Add `mfaEnabled: boolean` to `UserDetail` |
| `web/src/features/admin/users/userMutations.ts` | Add `useResetMfaMutation` |
| `web/src/routes/_app/admin/users.$userId.tsx` | Add Dialog imports, mutation, state, "Reset MFA" button, confirmation dialog |
| `web/src/lib/i18n/locales/en.json` | 7 new keys in `admin.users` |
| `src/FormForge.Api.Tests/Features/Users/UserAdminIntegrationTests.cs` | MFA helpers + 5 new tests |

### Files to Leave Untouched

- `src/FormForge.Api/Features/Auth/MfaService.cs` — no MFA service changes needed; reset is a direct DB operation owned by `UserService`
- `src/FormForge.Api/Program.cs` — no new DI registrations (no new services/validators)
- `src/FormForge.Api/Features/Roles/AdminEndpoints.cs` — route group unchanged
- `web/src/routes/_app/admin/users.tsx` (list page) — MFA status is a detail-only concept
- `web/src/features/admin/users/useUsersQuery.ts` / `useUserDetailQuery.ts` — queries unchanged; React Query cache invalidation via `invalidateAllUsers` handles refetch
- All migrations — no DB schema changes; the `mfa_backup_codes` and `users` tables are already set up by the Stories 2.13 migrations
- All Designer, Menu, CRUD, Provisioning, Audit files — unrelated

---

### References

- [Source: `_bmad-output/planning-artifacts/epics.md` — Story 2.15 AC, lines 863–889]
- [Source: `_bmad-output/planning-artifacts/architecture.md` — Decision 2.12 line 459: admin reset spec]
- [Source: `src/FormForge.Api/Features/Users/UserService.cs` — `DeactivateUserAsync` transaction pattern + `ExecuteUpdateAsync` for token revoke; `GetUserAsync` projection to extend]
- [Source: `src/FormForge.Api/Features/Users/UserEndpoints.cs` — existing handler patterns, `UserNotFoundProblem()` helper, `MapUserAdminEndpoints` registration style]
- [Source: `src/FormForge.Api/Features/Users/Dtos/UserDetailResponse.cs` — current record shape to extend]
- [Source: `web/src/routes/_app/admin/users.$userId.tsx` — full component structure, state patterns, button placement]
- [Source: `web/src/features/admin/users/userMutations.ts` — mutation shape, `invalidateAllUsers` helper, toast pattern]
- [Source: `web/src/features/auth/httpClient.ts` — `httpClient.delete<T>` is already defined]
- [Source: `web/src/components/ui/dialog.tsx` — exports: `Dialog`, `DialogContent`, `DialogHeader`, `DialogTitle`, `DialogFooter`, `DialogDescription`, `DialogClose`]
- [Source: `web/src/routes/_app/settings.tsx` — Dialog open/onOpenChange pattern to mirror]
- [Source: `web/src/lib/i18n/locales/en.json` — `admin.users` object at line 141; `roleAssignError` is the last key before the closing brace]
- [Source: `src/FormForge.Api.Tests/Features/Auth/AuthIntegrationTests.cs` — `EnrolMfaAsync`/`ConfirmMfaEnrolAsync` helper pattern]
- [Source: `src/FormForge.Api.Tests/Features/Users/UserAdminIntegrationTests.cs` — test class structure, seeding pattern, `LoginAsync` helper]

## Dev Agent Record

### Agent Model Used

claude-sonnet-4-6 (story authoring); claude-opus-4-8 (implementation)

### Debug Log References

None — implementation followed the Dev Notes guidance directly with no blockers.

### Completion Notes List

- **Task 1**: Added `bool MfaEnabled` as the last positional parameter of `UserDetailResponse`; updated the `GetUserAsync` projection (`u.MfaEnabled`) and the inline `CreateUserAsync` construction (`false`, since new users never have MFA). The positional record's call sites compile cleanly.
- **Task 2**: Added `AdminMfaResetOutcome` enum + `AdminMfaResetResult` record above `IUserService`, the interface method, and `ResetUserMfaAsync`. Implementation loads the user (→ NotFound when absent), then within a transaction clears `MfaEnabled`/`MfaSecretProtected`, stamps `UpdatedAt`, hard-deletes backup codes via `ExecuteDeleteAsync`, and revokes active refresh tokens via `ExecuteUpdateAsync` (RevokedAt — preserves audit trail). Idempotent on already-disabled MFA. `ConfigureAwait(false)` on every await; explicit `try/finally` transaction disposal per CA2007.
- **Task 3**: Registered `DELETE /{id:guid}/mfa` in `MapUserAdminEndpoints` and added `ResetUserMfaHandler` returning `Results.Ok()` (HTTP 200 per epic AC, not 204) on success and `UserNotFoundProblem()` (404) when not found. AC-2 403 enforced by the existing `/api/admin/*` group guard — no per-handler authorization added.
- **Task 4**: Added `mfaEnabled: boolean` to the `UserDetail` interface; `CreateUserResponse extends UserDetail` inherits it.
- **Task 5**: Added `useResetMfaMutation(userId)` calling `httpClient.delete`, invalidating user queries and toasting `admin.users.resetMfaSuccess` on success. No new imports required.
- **Task 6**: Added Dialog imports + `useResetMfaMutation`, `resetMfaDialogOpen`/`resetMfaError` state, an `onResetMfa` handler, a conditional "Reset MFA" button (rendered only when `user.mfaEnabled`), and a confirmation dialog with cancel/destructive-confirm buttons. Followed the `onClick`-based close pattern (no `DialogClose` wrapper) per Dev Notes anti-pattern guidance.
- **Task 7**: Added the 7 `resetMfa*` keys to `admin.users` in `en.json`. i18n-check confirms they are used (not orphaned) and the only missing key remains the pre-existing `designer.inspector.placeholders.label`.
- **Task 8**: Added `EnrolMfaAsync`/`ConfirmMfaEnrolAsync` helpers + `MfaEnrolResponseDto`, `using OtpNet;`, and the 5 new tests. All 33 `UserAdminIntegrationTests` pass (28 existing + 5 new).

**Validation results:**
- Backend build: 0 warnings / 0 errors.
- `UserAdminIntegrationTests`: 33 passed / 0 failed.
- Full backend suite: **762 passed / 2 failed** — the 2 failures are the documented pre-existing audit `DELETE→405` tests (`MutationAuditLogIntegrationTests`), unrelated to this story. Matches the story's predicted baseline (757 + 5 = 762).
- Frontend `tsc -b --noEmit`: passes.
- Frontend unit tests (`vitest`): 242 passed / 1 failed — the documented pre-existing `i18n-lint.test.ts` failure (`designer.inspector.placeholders.label`).
- ESLint on modified web files: only the 4 pre-existing `react-refresh/only-export-components` errors (the file's existing nested components); no new errors introduced.

### File List

- `src/FormForge.Api/Features/Users/Dtos/UserDetailResponse.cs` (modified)
- `src/FormForge.Api/Features/Users/UserService.cs` (modified)
- `src/FormForge.Api/Features/Users/UserEndpoints.cs` (modified)
- `web/src/features/admin/users/types.ts` (modified)
- `web/src/features/admin/users/userMutations.ts` (modified)
- `web/src/routes/_app/admin/users.$userId.tsx` (modified)
- `web/src/lib/i18n/locales/en.json` (modified)
- `src/FormForge.Api.Tests/Features/Users/UserAdminIntegrationTests.cs` (modified)

### Change Log

| Date | Change |
|---|---|
| 2026-06-01 | Story 2.15 created — ready for dev |
| 2026-06-01 | Story 2.15 implemented — admin MFA reset endpoint, service, UI button + dialog, i18n, and 5 integration tests. All 8 tasks complete; status → review. |

## Review Findings

Adversarial code review (Blind Hunter + Edge Case Hunter + Acceptance Auditor) of commit `afeae65`, 2026-06-01. Acceptance Auditor confirmed **full AC-1…AC-6 compliance** and all named anti-pattern constraints satisfied. 0 decision-needed, 2 patch, 1 deferred, 11 dismissed as noise/false-positive.

### Patch (actionable)

- [x] [Review][Patch] `onResetMfa` does not distinguish 404 — generic error + dialog left open on a deleted user [web/src/routes/_app/admin/users.$userId.tsx:115] — FIXED: added `ApiError && err.status === 404` branch surfacing `userNotFoundError`, mirroring the sibling handlers. — Sibling handlers `onDeactivate` (line 90) and `onReactivate` (line 107) branch on `err instanceof ApiError && err.status === 404` and surface `admin.users.userNotFoundError`. `onResetMfa` has a bare `catch {}` that always shows the generic `resetMfaError`, leaving the dialog open and inviting an infinite retry against a user that no longer exists. `ApiError` is already imported (line 29). Fix: add a 404 branch that sets the user-not-found message. Severity: Low.
- [x] [Review][Patch] `ResetMfa_RevokesAllRefreshTokens` does not pin the "stamp, don't delete" constraint [src/FormForge.Api.Tests/Features/Users/UserAdminIntegrationTests.cs:705] — FIXED: added an assertion that the user's total refresh-token row count is `> 0` after reset, so a regression to `ExecuteDeleteAsync` would fail. — The test asserts `liveTokens == 0` (RevokedAt != null) but never asserts the token rows still exist, so it would not catch a regression from `ExecuteUpdateAsync(RevokedAt)` to `ExecuteDeleteAsync`. Production code is correct (`RevokeActiveRefreshTokensAsync` stamps `RevokedAt`); add an assertion that the user's total refresh-token row count is unchanged (> 0) to guard the audit-trail-survival constraint. Severity: Low (optional, test hardening).

### Deferred

- [x] [Review][Defer] MFA login session gate can be orphaned mid-session by the post-eviction callback firing on `EvictionReason.Replaced` [src/FormForge.Api/Features/Auth/MfaService.cs:174] — deferred, pre-existing (Story 2.14 session machinery, not introduced by 2.15). On every wrong-code attempt `VerifyMfaLoginCoreAsync` re-`Set`s the session entry; MemoryCache fires the *old* entry's post-eviction callback with reason `Replaced`, which runs `SessionGates.TryRemove(token)` and drops the per-token serialization semaphore while the session is still alive. A subsequent pair of concurrent verifies bearing a valid TOTP can then acquire two different semaphores (the gate is recreated via `GetOrAdd`), defeating the single-use serialization the gate exists to provide and permitting a narrow TOTP double-mint window. Pre-existing for the first-failure case in both old/new code; the bundled 2.14 change widens it to every failure by routing the re-`Set` through `SessionCacheOptions`. Suggested fix in a follow-up: have the post-eviction callback ignore `EvictionReason.Replaced` (a replaced entry means the session is still live, so the gate must survive). Severity: Medium, exploitability very low (requires a prior failed attempt + precisely-timed concurrent valid submissions).

### Dismissed (false positives / handled / in-spec)

- Null deref on `user.MfaSecretProtected` in the TOTP branch (Blind, High) — **false positive**; guarded at `MfaService.cs:235` (`user is null || !user.MfaEnabled || user.MfaSecretProtected is null` → `SessionInvalid`) before the TOTP branch.
- TOTP-shaped 6-digit backup code never validates (Blind, Medium) — **false positive**; backup codes are 8 chars from `[A-Z0-9]` (`BackupCodeLength = 8`), disjoint from the `length == 6` TOTP shape.
- Non-positive `remaining` throws `ArgumentOutOfRangeException` on fail-count re-`Set` (Blind, Medium) — **false positive**; clamped at `MfaService.cs:291` (`if (remaining <= TimeSpan.Zero) cache.Remove(...)`).
- Transaction does not enlist bulk `Execute*Async` / atomicity not guaranteed (Blind, Medium) — handled; EF enlists `ExecuteDeleteAsync`/`ExecuteUpdateAsync` in the ambient `BeginTransactionAsync`; identical proven pattern to `DeactivateUserAsync`.
- Concurrent resets surface as 500, no SERIALIZABLE/40001 handling (Edge, Low) — consistent with the closest sibling `DeactivateUserAsync` (same default isolation, no conflict handling); `AssignRolesAsync` only needs SERIALIZABLE for read-modify-write role reconciliation, not blind idempotent clears.
- No domain event published, unlike `bus.Publish` in sibling mutations (Blind+Edge, Medium) — **false alarm**; those events exist solely to bust the per-user permission cache in `PermissionService`. MFA reset does not change roles/permissions, so no event is required. There is no admin-action audit log anywhere for siblings to be inconsistent with; spec scoped audit to `UpdatedAt`.
- No audit log on security-critical admin action (Blind, Medium) — consistent with all sibling admin operations (none write an audit record); spec explicitly records the reset via `user.UpdatedAt`.
- Self-reset permitted (no `currentUserId` self-guard like `DeactivateUserAsync`) (Blind+Edge) — in-spec; AC-1 says "disable MFA for **any** user account," and self-reset is a legitimate recovery path (re-enrolment is available). Unlike self-deactivation, it cannot lock the admin out.
- Access JWT survives until TTL; toast "signed out of all sessions" overstates (Edge, Medium) — in-spec; AC-1 requires only refresh-token revocation, which is done; access tokens are short-lived by design; matches the password-rotation/deactivation pattern.
- Idempotent reset still revokes sessions for an MFA-disabled user (Blind, Medium) — in-spec (AC-4: 200, no error); UI-gated behind `user.mfaEnabled`; "secure the account" is a defensible side effect.
- Endpoint advertises only 200/404 but can return 403/500 (Blind, Low) — 403 (group guard) and 500 are implicit and consistent with project OpenAPI convention.
- `MfaSessionInvalidProblem` collapses SessionInvalid + user-deleted-after-2FA into one 401 (Blind, Low) — intentional per Story 2.14 design and the in-code comment; `MfaCompleteUserMissing` is still logged distinctly.
