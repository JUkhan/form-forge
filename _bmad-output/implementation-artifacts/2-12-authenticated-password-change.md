# Story 2.12: Authenticated Password Change

Status: done

## Story

As an authenticated user,
I want to change my password from my account settings,
so that I can update my credentials without admin involvement.

## Acceptance Criteria

1. **AC-1 — Wrong current password → 401**
   Given I am authenticated and submit `PUT /api/users/me/password` with `{ currentPassword, newPassword }`, when the bcrypt comparison of `currentPassword` against my stored `passwordHash` fails, then HTTP 401 is returned with `code: "CURRENT_PASSWORD_INCORRECT"` and `messageKey: "auth.currentPasswordIncorrect"`.

2. **AC-2 — Weak or identical new password → 422**
   Given `newPassword` is fewer than 8 characters or matches `currentPassword` (bcrypt comparison), when I PUT `/api/users/me/password`, then HTTP 422 is returned with a descriptive `ValidationProblemDetails`.

3. **AC-3 — Successful password change**
   Given `currentPassword` is correct and `newPassword` passes validation, when the update commits, then `passwordHash` is updated to the bcrypt hash of `newPassword`, `UpdatedAt` is stamped, all active refresh tokens for my account **except the current session's** are revoked (via `ExecuteUpdateAsync`), and HTTP 200 is returned.

4. **AC-4 — Frontend: `/settings` route with Change Password section**
   Given I am authenticated and navigate to `/settings`, then a "Change Password" card is present with three fields: current password, new password, confirm new password. A submit button changes password via `PUT /api/users/me/password`.

5. **AC-5 — Frontend: success clears fields and toasts**
   Given the password change succeeds, when the response returns 200, then a sonner `toast.success` is shown and all three password fields are reset to empty.

6. **AC-6 — Frontend: inline error for wrong current password**
   Given the API returns 401 `CURRENT_PASSWORD_INCORRECT`, then an inline error is shown on the current password field (not a generic toast).

7. **AC-7 — Frontend: inline error for same-as-current**
   Given the API returns 422 with `fieldErrors.newPassword`, then the inline error is shown on the new password field.

8. **AC-8 — `/settings` accessible to all authenticated users**
   The `/settings` route is accessible to every authenticated user (not admin-only). Navigation to it is exposed for all users in the app header.

## Tasks / Subtasks

- [x] Task 1: Add `ChangePasswordRequest` DTO and validator (AC-1, AC-2)
  - [x] Create `src/FormForge.Api/Features/Users/Dtos/ChangePasswordRequest.cs` — record with `string CurrentPassword` and `string NewPassword`
  - [x] Create `src/FormForge.Api/Features/Users/Validators/ChangePasswordRequestValidator.cs` — CurrentPassword: NotEmpty + MaximumLength(72); NewPassword: NotEmpty + MinimumLength(8) + MaximumLength(72)
  - [x] Register both as `AddScoped` in `Program.cs` after the existing auth validators block

- [x] Task 2: Extend `IAuthService` with `ChangePasswordAsync` (AC-1, AC-2, AC-3)
  - [x] Add enum `ChangePasswordOutcome` (Success, CurrentPasswordIncorrect, NewPasswordSameAsCurrent) and result record `ChangePasswordResult` to `AuthService.cs` — follow the exact same enum+record pattern as `PasswordResetOutcome` / `PasswordResetResult`
  - [x] Add method signature to `IAuthService` interface: `Task<ChangePasswordResult> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, string? currentRefreshTokenRaw, CancellationToken ct)`
  - [x] Implement in `AuthService`: load user by `userId` (tracked, not AsNoTracking — we mutate it); bcrypt-verify `currentPassword`; bcrypt-verify `newPassword` equals current (same-as-current guard); update `PasswordHash` + `UpdatedAt`; bulk-revoke all active refresh tokens EXCEPT the current session's via `ExecuteUpdateAsync` with hash exclusion; `SaveChangesAsync`

- [x] Task 3: Add `PUT /me/password` endpoint to `MeEndpoints.cs` (AC-1, AC-2, AC-3)
  - [x] Add `MapPut("/me/password", ChangePasswordHandler)` with `.AddValidationFilter<ChangePasswordRequest>()` and `.RequireRateLimiting("user-change-password")`
  - [x] Implement `ChangePasswordHandler`: extract `userId` from `httpContext.User.FindFirst("userId")`; read `refresh_token` cookie from `httpContext.Request.Cookies["refresh_token"]`; call `authService.ChangePasswordAsync`; map `ChangePasswordOutcome` to HTTP responses (401 / 422 ValidationProblem / 200)

- [x] Task 4: Add rate-limit policy in `Program.cs` (AC-1)
  - [x] Add `"user-change-password"` sliding-window policy inside the existing `AddRateLimiter` block: 5 req/min per authenticated `userId` claim (mirroring the "admin" sliding window pattern)
  - [x] Register `ChangePasswordRequestValidator` as `AddScoped` (alongside existing auth validators)

- [x] Task 5: Frontend — `/settings` route (AC-4, AC-5, AC-6, AC-7, AC-8)
  - [x] Create `web/src/routes/_app/settings.tsx` — auth-guarded by `_app` parent route, no additional `beforeLoad` needed
  - [x] Use `useForm` (react-hook-form + zodResolver), zod schema with `currentPassword`, `newPassword`, `confirmNewPassword`; refine checks `newPassword === confirmNewPassword`
  - [x] On success: `toast.success(t('settings.changePassword.successToast'))` then `reset()` all three fields
  - [x] On 401 `CURRENT_PASSWORD_INCORRECT`: `setError('currentPassword', { message: t('auth.currentPasswordIncorrect') })`
  - [x] On 422 with `fieldErrors.newPassword`: `setError('newPassword', { message: t('auth.passwordSameAsCurrent') })`
  - [x] On other errors: `setError('root', { message: t('errors.genericError') })`

- [x] Task 6: Frontend mutations (AC-4)
  - [x] Add `useChangePasswordMutation()` to `web/src/features/auth/authMutations.ts` — `httpClient.put<void>('/api/users/me/password', body)` where body is `{ currentPassword, newPassword }` (NOT confirmNewPassword)

- [x] Task 7: Frontend navigation — expose `/settings` link (AC-8)
  - [x] In `web/src/routes/_app.tsx`, add a settings link (e.g., `<Link to="/settings">`) visible to **all** authenticated users — NOT inside `PermissionGate`. Place it in the header alongside the logout button.

- [x] Task 8: i18n strings (AC-4, AC-5, AC-6, AC-7)
  - [x] Add `settings.*` key group and `auth.currentPasswordIncorrect` to `web/src/lib/i18n/locales/en.json` (see Dev Notes for exact keys)

## Dev Notes

### Overview: What This Story Adds

1. New DTO + validator for `{ currentPassword, newPassword }`
2. `IAuthService.ChangePasswordAsync` — bcrypt verify, update hash, partial refresh-token revocation
3. `PUT /api/users/me/password` in `MeEndpoints.cs` with rate limiting
4. New rate-limit policy `"user-change-password"` in `Program.cs`
5. `web/src/routes/_app/settings.tsx` — user settings page with Change Password card
6. `useChangePasswordMutation` in `authMutations.ts`
7. Navigation link to `/settings` in `_app.tsx` header (all authenticated users)
8. New i18n keys

**Key difference from Story 2.11:** `ResetPasswordAsync` revokes ALL refresh tokens for the user. `ChangePasswordAsync` revokes all **EXCEPT** the current session's — the user stays logged in after changing their password. The current session's refresh token hash is excluded via a `WHERE r.TokenHash != currentHash` predicate in `ExecuteUpdateAsync`.

---

### New DTO and Validator

**`ChangePasswordRequest.cs`:**
```csharp
namespace FormForge.Api.Features.Users.Dtos;

internal sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
```

**`ChangePasswordRequestValidator.cs`:** Mirror the `ResetPasswordRequestValidator` pattern:
```csharp
internal sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword)
            .NotEmpty()
            .MaximumLength(72); // BCrypt 72-byte ceiling — no MinimumLength (verifying existing password)

        RuleFor(x => x.NewPassword)
            .NotEmpty()
            .MinimumLength(8)
            .MaximumLength(72); // BCrypt UTF-8 byte limit
    }
}
```

**Register in `Program.cs`** (after the existing `ResetPasswordRequestValidator` line):
```csharp
builder.Services.AddScoped<IValidator<ChangePasswordRequest>, ChangePasswordRequestValidator>();
```

---

### Extending `IAuthService` and `AuthService`

**Add to `AuthService.cs`** (alongside the existing Story 2.11 enums/records — follow the same section pattern):
```csharp
// Story 2.12 — authenticated password change.
internal enum ChangePasswordOutcome
{
    Success,
    CurrentPasswordIncorrect,
    NewPasswordSameAsCurrent,
}

internal sealed record ChangePasswordResult(ChangePasswordOutcome Outcome);
```

**Add to `IAuthService` interface:**
```csharp
Task<ChangePasswordResult> ChangePasswordAsync(
    Guid userId,
    string currentPassword,
    string newPassword,
    string? currentRefreshTokenRaw,
    CancellationToken ct);
```

**Implementation in `AuthService`:**
```csharp
public async Task<ChangePasswordResult> ChangePasswordAsync(
    Guid userId,
    string currentPassword,
    string newPassword,
    string? currentRefreshTokenRaw,
    CancellationToken ct)
{
    ArgumentNullException.ThrowIfNull(currentPassword);
    ArgumentNullException.ThrowIfNull(newPassword);

    // Tracked (not AsNoTracking) — we mutate PasswordHash and UpdatedAt.
    var user = await db.Users
        .FirstOrDefaultAsync(u => u.Id == userId, ct)
        .ConfigureAwait(false);

    // Still-valid JWT references a deleted user (admin race) — treat as auth invalid.
    if (user is null)
        return new ChangePasswordResult(ChangePasswordOutcome.CurrentPasswordIncorrect);

    if (!passwordHasher.Verify(currentPassword, user.PasswordHash))
        return new ChangePasswordResult(ChangePasswordOutcome.CurrentPasswordIncorrect);

    // New password must differ from the current one.
    if (passwordHasher.Verify(newPassword, user.PasswordHash))
        return new ChangePasswordResult(ChangePasswordOutcome.NewPasswordSameAsCurrent);

    user.PasswordHash = passwordHasher.Hash(newPassword);
    user.UpdatedAt = DateTimeOffset.UtcNow;

    // Revoke all OTHER active refresh tokens — the current session's token is excluded
    // so the user remains logged in after changing their password.
    var currentHash = currentRefreshTokenRaw != null ? HashToken(currentRefreshTokenRaw) : null;

    await db.RefreshTokens
        .Where(r => r.UserId == userId
                 && r.RevokedAt == null
                 && (currentHash == null || r.TokenHash != currentHash))
        .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, DateTimeOffset.UtcNow), ct)
        .ConfigureAwait(false);

    await db.SaveChangesAsync(ct).ConfigureAwait(false);

    return new ChangePasswordResult(ChangePasswordOutcome.Success);
}
```

**`HashToken` is already a private static method in `AuthService`** — reuse it directly. Do NOT redefine it.

---

### New Endpoint in `MeEndpoints.cs`

Add inside `MapMePreferencesEndpoints`, alongside the existing `/me/preferences` mapping:

```csharp
group.MapPut("/me/password", ChangePasswordHandler)
     .AddValidationFilter<ChangePasswordRequest>()
     .RequireRateLimiting("user-change-password")
     .WithSummary("Change the authenticated user's own password")
     .Produces(StatusCodes.Status200OK)
     .Produces(StatusCodes.Status401Unauthorized)
     .Produces(StatusCodes.Status422UnprocessableEntity);
```

**Handler (add as a private static method in `MeEndpoints`):**
```csharp
private static async Task<IResult> ChangePasswordHandler(
    ChangePasswordRequest request,
    IAuthService authService,
    HttpContext httpContext,
    CancellationToken ct)
{
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(authService);
    ArgumentNullException.ThrowIfNull(httpContext);

    var userIdClaim = httpContext.User.FindFirst("userId")?.Value;
    if (!Guid.TryParse(userIdClaim, out var userId))
        return Results.Unauthorized();

    // Pass the current session's raw refresh token so AuthService can exclude it
    // from bulk revocation (the user stays logged in after the change).
    var currentRefreshToken = httpContext.Request.Cookies["refresh_token"];

    var result = await authService.ChangePasswordAsync(
        userId, request.CurrentPassword, request.NewPassword, currentRefreshToken, ct)
        .ConfigureAwait(false);

    return result.Outcome switch
    {
        ChangePasswordOutcome.CurrentPasswordIncorrect => Results.Problem(
            detail: "Current password is incorrect.",
            title: "Current password incorrect",
            statusCode: StatusCodes.Status401Unauthorized,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "CURRENT_PASSWORD_INCORRECT",
                ["messageKey"] = "auth.currentPasswordIncorrect",
                ["correlationId"] = httpContext.GetCorrelationId(),
            }),

        ChangePasswordOutcome.NewPasswordSameAsCurrent => Results.ValidationProblem(
            errors: new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["newPassword"] = ["New password must differ from your current password."],
            },
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = "auth.passwordSameAsCurrent",
                ["correlationId"] = httpContext.GetCorrelationId(),
            }),

        ChangePasswordOutcome.Success => Results.Ok(),

        _ => Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = "errors.genericError",
                ["correlationId"] = httpContext.GetCorrelationId(),
            }),
    };
}
```

**Required `using` additions to `MeEndpoints.cs`:**
- `using FormForge.Api.Common.Logging;` (for `GetCorrelationId()`) — check if already present; `UpdateMyPreferencesHandler` doesn't use it, so it may not be imported yet.
- `using FormForge.Api.Features.Auth;` (for `IAuthService`, `ChangePasswordOutcome`) — check if already present.
- `using FormForge.Api.Features.Users.Dtos;` — already present for `UpdateMyPreferencesRequest`.

---

### Rate Limiting in `Program.cs`

Add inside the existing `AddRateLimiter` block, after the Story 2.11 `"auth-reset-password"` policy:

```csharp
// Story 2.12 — PUT /api/users/me/password: 5 req/min per authenticated user.
// Sliding window per userId so brute-forcing currentPassword from a stolen JWT
// is rate-limited. Falls back to IP for any unauthenticated edge case.
options.AddPolicy("user-change-password", ctx =>
{
    var userId = ctx.HttpContext.User.FindFirst("userId")?.Value;
    return !string.IsNullOrEmpty(userId)
        ? RateLimitPartition.GetSlidingWindowLimiter(
            userId,
            _ => new SlidingWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                PermitLimit = 5,
                QueueLimit = 0,
            })
        : RateLimitPartition.GetSlidingWindowLimiter(
            ctx.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new SlidingWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                PermitLimit = 5,
                QueueLimit = 0,
            });
});
```

---

### ⚠️ httpClient 401-Retry Behavior — READ BEFORE IMPLEMENTING THE FRONTEND

The `httpClient.ts` automatically retries any 401 response once by refreshing the session (see `httpClient.ts` lines 93–104). This fires for `PUT /api/users/me/password` on a wrong `currentPassword`, because:
1. The user IS authenticated — so `refreshSession()` succeeds
2. The retry repeats the PUT and gets another 401 (`CURRENT_PASSWORD_INCORRECT`)
3. Since it is now a retry (`isRetry = true`), no further retry — the error object is populated from the `ProblemDetails` body
4. The `ApiError` thrown has `code: 'CURRENT_PASSWORD_INCORRECT'` — **correct**

**Practical impact:** wrong current password causes one extra POST to `/api/auth/refresh` before the error surfaces. Tolerable in v1. The final `ApiError.code` IS `'CURRENT_PASSWORD_INCORRECT'`, so frontend error handling works correctly.

**Frontend error handling pattern:**
```ts
} catch (err) {
  if (err instanceof ApiError && err.code === 'CURRENT_PASSWORD_INCORRECT') {
    setError('currentPassword', { message: t('auth.currentPasswordIncorrect') })
    return
  }
  if (err instanceof ApiError && err.status === 422 && err.fieldErrors?.newPassword) {
    setError('newPassword', { message: t('auth.passwordSameAsCurrent') })
    return
  }
  setError('root', { message: t('errors.genericError') })
}
```

---

### Frontend Mutation: `authMutations.ts`

Add to `web/src/features/auth/authMutations.ts`:

```ts
// Story 2.12 — authenticated user changes their own password.
// Caller handles success toast and field reset.
export function useChangePasswordMutation() {
  return useMutation({
    mutationFn: (body: { currentPassword: string; newPassword: string }) =>
      httpClient.put<void>('/api/users/me/password', body),
  })
}
```

**Note:** `httpClient.put<void>` — server returns `Results.Ok()` with no body (empty 200). `httpClient` resolves empty 2xx bodies to `undefined`, consistent with `useResetPasswordMutation`.

---

### Frontend Route: `settings.tsx`

**File:** `web/src/routes/_app/settings.tsx`

```tsx
// eslint-disable-next-line react-refresh/only-export-components
import { createFileRoute } from '@tanstack/react-router'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { useTranslation } from 'react-i18next'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { useChangePasswordMutation } from '@/features/auth/authMutations'
import { ApiError } from '@/lib/api/apiError'

export const Route = createFileRoute('/_app/settings')({
  component: SettingsPage,
})

const changePasswordSchema = z
  .object({
    currentPassword: z.string().min(1),
    newPassword: z.string().min(8).max(72),
    confirmNewPassword: z.string(),
  })
  .refine((d) => d.newPassword === d.confirmNewPassword, {
    // Note: can't use t() here (outside hook); the refine message is a static string
    // — the component renders the translated version by comparing message to a sentinel.
    message: 'PASSWORDS_MISMATCH_SENTINEL',
    path: ['confirmNewPassword'],
  })

type ChangePasswordFormValues = z.infer<typeof changePasswordSchema>

const MISMATCH_SENTINEL = 'PASSWORDS_MISMATCH_SENTINEL' as const

// eslint-disable-next-line react-refresh/only-export-components
function SettingsPage() {
  const { t } = useTranslation()
  const mutation = useChangePasswordMutation()
  const {
    register,
    handleSubmit,
    formState: { errors, isSubmitting },
    reset,
    setError,
  } = useForm<ChangePasswordFormValues>({
    resolver: zodResolver(changePasswordSchema),
    mode: 'onChange',
    defaultValues: { currentPassword: '', newPassword: '', confirmNewPassword: '' },
  })

  const submit = async (values: ChangePasswordFormValues) => {
    try {
      await mutation.mutateAsync({
        currentPassword: values.currentPassword,
        newPassword: values.newPassword,
      })
      toast.success(t('settings.changePassword.successToast'))
      reset()
    } catch (err) {
      if (err instanceof ApiError && err.code === 'CURRENT_PASSWORD_INCORRECT') {
        setError('currentPassword', { message: t('auth.currentPasswordIncorrect') })
        return
      }
      if (err instanceof ApiError && err.status === 422 && err.fieldErrors?.newPassword) {
        setError('newPassword', { message: t('auth.passwordSameAsCurrent') })
        return
      }
      setError('root', { message: t('errors.genericError') })
    }
  }

  return (
    <div className="space-y-6">
      <h1 className="text-2xl font-bold tracking-tight">{t('settings.title')}</h1>

      <form
        onSubmit={(e) => { void handleSubmit(submit)(e) }}
        className="space-y-4 rounded-xl border border-border bg-card p-6 shadow-sm"
      >
        <h2 className="text-lg font-semibold text-foreground">
          {t('settings.changePassword.sectionTitle')}
        </h2>

        <div className="space-y-1.5">
          <Label htmlFor="current-password">
            {t('settings.changePassword.currentPasswordLabel')}
          </Label>
          <Input
            id="current-password"
            type="password"
            autoComplete="current-password"
            {...register('currentPassword')}
          />
          {errors.currentPassword && (
            <p role="alert" className="text-xs text-destructive">
              {errors.currentPassword.message}
            </p>
          )}
        </div>

        <div className="space-y-1.5">
          <Label htmlFor="new-password">
            {t('settings.changePassword.newPasswordLabel')}
          </Label>
          <Input
            id="new-password"
            type="password"
            autoComplete="new-password"
            {...register('newPassword')}
          />
          {errors.newPassword && (
            <p role="alert" className="text-xs text-destructive">
              {errors.newPassword.message}
            </p>
          )}
        </div>

        <div className="space-y-1.5">
          <Label htmlFor="confirm-new-password">
            {t('settings.changePassword.confirmNewPasswordLabel')}
          </Label>
          <Input
            id="confirm-new-password"
            type="password"
            autoComplete="new-password"
            {...register('confirmNewPassword')}
          />
          {errors.confirmNewPassword && (
            <p role="alert" className="text-xs text-destructive">
              {errors.confirmNewPassword.message === MISMATCH_SENTINEL
                ? t('settings.changePassword.passwordMismatch')
                : errors.confirmNewPassword.message}
            </p>
          )}
        </div>

        {errors.root && (
          <div
            role="alert"
            className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive"
          >
            {errors.root.message}
          </div>
        )}

        <Button type="submit" disabled={isSubmitting}>
          {isSubmitting
            ? t('settings.changePassword.submitting')
            : t('settings.changePassword.submitButton')}
        </Button>
      </form>
    </div>
  )
}
```

**Key pattern from Story 2.11 debug log:** `react-refresh/only-export-components` fires on any component function in a route file. Add `// eslint-disable-next-line react-refresh/only-export-components` above BOTH the exported `Route` const AND the component function (or add a single disable at the top of the file). Match the pattern used in `forgot-password.tsx` and `reset-password.tsx`.

**Password mismatch sentinel pattern:** Zod `refine` runs outside React hooks (no access to `t()`), so use a sentinel string constant for the mismatch message. The component detects it and renders the translated string. This is the same workaround as `UPDATE_REQUIRED_KEY` in `admin/users.$userId.tsx`.

---

### Frontend Navigation: `_app.tsx`

In `web/src/routes/_app.tsx`, add a Settings icon link visible to ALL authenticated users (not inside `<PermissionGate>`). Place it in the header alongside the theme selector and the admin gear (before the logout button):

```tsx
// Add import at the top:
import { Link } from '@tanstack/react-router'
import { Settings as SettingsIcon } from 'lucide-react'
// (Note: Settings import already exists in _app.tsx — check before adding)
```

Add a new `<Tooltip>` block for `/settings` (NOT inside `PermissionGate`, visible to everyone):

```tsx
<Tooltip>
  <TooltipTrigger asChild>
    <Button asChild variant="ghost" size="icon-sm">
      <Link to="/settings" aria-label={t('settings.title')}>
        <SettingsIcon className="h-4 w-4" />
      </Link>
    </Button>
  </TooltipTrigger>
  <TooltipContent>{t('settings.title')}</TooltipContent>
</Tooltip>
```

**Important:** Check the existing `_app.tsx` imports — `Settings as SettingsIcon` and `Link` are already imported. The admin gear icon ALSO uses `SettingsIcon`. The new `/settings` link and the existing admin `/admin/users` link both use the same icon — this is fine since one is visible to all users and one is admin-only (inside `PermissionGate`). Consider using a different icon (e.g., `KeyRound` from lucide-react) for the `/settings` link to avoid the two identical gear icons in the header for admins. Either choice is acceptable.

---

### i18n Keys

**Add to `web/src/lib/i18n/locales/en.json`:**

Inside the `"auth"` object (alongside the existing `"passwordSameAsCurrent"` key):
```json
"currentPasswordIncorrect": "Current password is incorrect."
```

Add a new top-level `"settings"` object:
```json
"settings": {
  "title": "Account Settings",
  "changePassword": {
    "sectionTitle": "Change Password",
    "currentPasswordLabel": "Current password",
    "newPasswordLabel": "New password",
    "confirmNewPasswordLabel": "Confirm new password",
    "submitButton": "Change Password",
    "submitting": "Saving…",
    "successToast": "Password changed successfully.",
    "passwordMismatch": "Passwords do not match."
  }
}
```

---

### Files to CREATE

| File | Purpose |
|---|---|
| `src/FormForge.Api/Features/Users/Dtos/ChangePasswordRequest.cs` | Request DTO |
| `src/FormForge.Api/Features/Users/Validators/ChangePasswordRequestValidator.cs` | FluentValidation validator |
| `web/src/routes/_app/settings.tsx` | Authenticated user settings route |

### Files to MODIFY

| File | Change |
|---|---|
| `src/FormForge.Api/Features/Auth/AuthService.cs` | Add `ChangePasswordOutcome` enum + `ChangePasswordResult` record + `IAuthService` method signature + implementation |
| `src/FormForge.Api/Features/Users/MeEndpoints.cs` | Add `MapPut("/me/password", ...)` registration + `ChangePasswordHandler` method |
| `src/FormForge.Api/Program.cs` | Add `"user-change-password"` rate-limit policy; register `ChangePasswordRequestValidator` |
| `web/src/features/auth/authMutations.ts` | Add `useChangePasswordMutation` |
| `web/src/routes/_app.tsx` | Add `/settings` link in header (all users, not inside PermissionGate) |
| `web/src/lib/i18n/locales/en.json` | Add `auth.currentPasswordIncorrect` and `settings.*` keys |

### Files to Leave Untouched

- `src/FormForge.Api/Features/Auth/AuthEndpoints.cs` — this change goes in `MeEndpoints.cs` (users group), not the auth group
- `src/FormForge.Api/Features/Auth/EmailService.cs` — no email sent for password change
- `src/FormForge.Api/Features/Auth/PasswordHasher.cs` — reused as-is; `Verify` and `Hash` already exist
- `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` — no new entity, no migration needed
- `src/FormForge.Api/Features/Permissions/PermissionsEndpoints.cs` — no changes; `MapUserSelfEndpoints` is already mapped in Program.cs alongside `MapMePreferencesEndpoints`
- All Designer, Menu, CRUD, and Provisioning files — completely unrelated

---

### Anti-Pattern Prevention

**DO NOT** add the `PUT /me/password` endpoint to `AuthEndpoints.cs`. This endpoint requires authentication (JWT userId claim), belongs in the `/api/users` route group (already mapped in Program.cs as `app.MapGroup("/api/users")`), and follows the pattern of `MeEndpoints.MapMePreferencesEndpoints`.

**DO NOT** revoke ALL refresh tokens — only revoke those where `r.TokenHash != currentHash`. The spec explicitly says "except the current session's". The current session's raw token is read from the `refresh_token` HttpOnly cookie in the handler.

**DO NOT** use `db.RefreshTokens.RemoveRange(...)` for revocation — use `ExecuteUpdateAsync` (bulk UPDATE without change tracking), exactly as in `ResetPasswordAsync`.

**DO NOT** call `HashToken` on the raw token outside of `AuthService`. The handler passes the raw cookie value to `authService.ChangePasswordAsync`; hashing happens inside the service (where `HashToken` is private static).

**DO NOT** add `AsNoTracking()` to the user load in `ChangePasswordAsync`. We mutate `user.PasswordHash` and `user.UpdatedAt` — change tracking must be active so `SaveChangesAsync` picks them up.

**DO NOT** send a confirmation email on password change — no email is specified in AR-55 or FR-52 for this flow. Only the forgot-password flow sends email.

**DO NOT** issue a new access token or refresh token on success — the existing access token (valid up to 15 min) and the preserved refresh token remain valid. No cookie manipulation needed in the response.

**DO NOT** log the old or new password — `AuthService` already enforces this (no logging of password fields). Use `[LoggerMessage]` if you add any logs; `CA1848` is enforced via `TreatWarningsAsErrors`.

**DO NOT** add `[SuppressMessage("Performance", "CA1812")]` to `ChangePasswordRequestValidator` — it IS needed because it is registered via DI with no public constructor used at the call site. Follow the pattern from `ForgotPasswordRequestValidator` and `ResetPasswordRequestValidator`.

**DO NOT** add a `confirmNewPassword` field to the DTO or API — it is a frontend-only validation. Only `{ currentPassword, newPassword }` is sent to `PUT /api/users/me/password`. The validator does NOT check `confirmNewPassword`.

**DO NOT** create a new `IChangePasswordService` — extend `IAuthService` exactly as specified (same pattern as the reset-password methods added in Story 2.11).

---

### Previous Story Learnings (2.11)

- **`ConfigureAwait(false)` on every `await`** — enforced by analyzer; missing it causes a build warning that fails `TreatWarningsAsErrors`.
- **`[SuppressMessage("Performance", "CA1812")]`** — required on every `internal sealed class` registered via DI. Add it to `ChangePasswordRequestValidator` (and `ChangePasswordRequest` if it has a nested implementation class — but a record DTO has no DI registration, so the attribute is only needed on the validator).
- **`ArgumentNullException.ThrowIfNull`** — add to every handler parameter and every service method parameter. Follow the exact same pattern as `ResetPasswordHandler` and `ResetPasswordAsync`.
- **`react-refresh/only-export-components` lint rule** — suppress above the exported `Route` const in the new route file, matching `forgot-password.tsx` / `reset-password.tsx`.
- **Pre-existing test failures:** `MutationAuditLogIntegrationTests` and `SchemaAuditLogIntegrationTests` have 2 DELETE→405 failures on a clean tree — ignore. Frontend: `i18n-lint.test.ts` fails with 1 pre-existing key (`designer.inspector.placeholders.label`) — ignore.
- **No EF Core migration needed** — no new tables or columns. `password_reset_tokens` was added in Story 2.11; `users` already has all needed columns.
- **`AuthService` is `internal sealed partial class`** — it must remain `partial` for the `[LoggerMessage]` source-generation to compile. Do not remove the `partial` keyword.
- **`ExecuteUpdateAsync` order matters** — call it BEFORE `SaveChangesAsync`. The `ExecuteUpdateAsync` is a direct DB UPDATE that bypasses change tracking; `SaveChangesAsync` then flushes the tracked user entity changes (PasswordHash, UpdatedAt). Both must succeed for the operation to be consistent (they use the same `FormForgeDbContext` and are in the same implicit transaction if wrapped, but they're issued as separate SQL statements here — this is acceptable as the architecture established this pattern in `ResetPasswordAsync`).

### Testing

No new integration tests mandated. Manual verification path:
1. Log in as any user → navigate to `/settings`
2. Submit wrong current password → inline error on the current password field appears
3. Submit current password + new password that's fewer than 8 characters → 422 inline error on new password
4. Submit current password + new password identical to current → 422 "must differ" inline error
5. Submit correct current password + valid new password → toast success; fields cleared; log in with new password to confirm

Unit-test recommendation (follow `EmailServiceTests` pattern for isolated service logic): test `ChangePasswordAsync` for:
- User not found → `CurrentPasswordIncorrect`
- Wrong `currentPassword` → `CurrentPasswordIncorrect`
- Same `newPassword` as current → `NewPasswordSameAsCurrent`
- Success path + partial refresh-token revocation (mock `ExecuteUpdateAsync`)

### References

- [Source: `_bmad-output/planning-artifacts/epics.md` — Epic 2 overview, Story 2.12, FR-52, AR-55]
- [Source: `_bmad-output/planning-artifacts/architecture.md` — Decision 2.11, Decision 2.4 (BCrypt), Decision 2.6 (rate limiting), Decision 4.6 (feature folder structure), Decision 4.9 (form composition)]
- [Source: `src/FormForge.Api/Features/Auth/AuthService.cs` — `HashToken`, `PasswordResetOutcome`/`PasswordResetResult` pattern, `ResetPasswordAsync` implementation, `ExecuteUpdateAsync` bulk-revoke pattern]
- [Source: `src/FormForge.Api/Features/Auth/AuthEndpoints.cs` — ProblemDetails extension dict pattern, handler signature pattern]
- [Source: `src/FormForge.Api/Features/Users/MeEndpoints.cs` — current file to extend with new endpoint; `UpdateMyPreferencesHandler` pattern for `userId` claim extraction]
- [Source: `src/FormForge.Api/Features/Users/UserEndpoints.cs` — admin-facing user update for comparison; confirm `MeEndpoints` is the right place (not `UserEndpoints`)]
- [Source: `src/FormForge.Api/Program.cs` — `AddRateLimiter` block (lines ~287–386), "admin" sliding-window policy pattern to mirror, validator registration pattern, `/api/users` route group mapping (line ~572)]
- [Source: `src/FormForge.Api/Features/Permissions/PermissionsEndpoints.cs` — `MapUserSelfEndpoints` already lives in `/api/users` group alongside `MapMePreferencesEndpoints`]
- [Source: `web/src/features/auth/authMutations.ts` — `useResetPasswordMutation` pattern for PUT void mutation]
- [Source: `web/src/features/auth/httpClient.ts` — lines 93–104: 401 retry behavior that will fire on wrong currentPassword; `httpClient.put` available]
- [Source: `web/src/routes/_app.tsx` — header layout, existing imports, `PermissionGate` and `SettingsIcon` usage; where to insert the new `/settings` nav link]
- [Source: `web/src/routes/_app/admin/users.$userId.tsx` — `UPDATE_REQUIRED_KEY` sentinel pattern for Zod refine outside React hooks; `UpdateUserForm` for form structure reference]
- [Source: `web/src/routes/forgot-password.tsx` and `reset-password.tsx` — eslint-disable comment pattern for route files, mutation error handling shape]
- [Source: `web/src/lib/i18n/locales/en.json` — existing `auth.*` key structure; `settings.*` top-level key to add]
- [Source: `_bmad-output/implementation-artifacts/2-11-forgot-password-flow.md` — dev notes, completion notes, anti-patterns, prior story learnings]

## Dev Agent Record

### Agent Model Used

claude-sonnet-4-6 (story authoring); claude-opus-4-8 (implementation)

### Debug Log References

- **Rate-limit policy `HttpContext` shape (build error → fixed):** The Dev Notes snippet used `ctx.HttpContext.User…` inside `options.AddPolicy("user-change-password", ctx => …)`. In this codebase the `AddPolicy` callback receives the `HttpContext` **directly** (the existing `"admin"` policy uses `httpContext.User.FindFirst(...)`), so `ctx.HttpContext` does not compile (CS1061). Corrected to `ctx.User.FindFirst("userId")` and `ctx.Connection.RemoteIpAddress`.
- **Same-as-current status 400 → 422 (test failure → fixed):** `Results.ValidationProblem(...)` defaults to **400 Bad Request**, but AC-2 mandates **422** and the frontend (AC-7) keys its inline `newPassword` error on `err.status === 422`. Added `statusCode: StatusCodes.Status422UnprocessableEntity` to the `NewPasswordSameAsCurrent` arm. (Note: Story 2.11's reset-password handler returns 400 here; this story deliberately returns 422 per its own AC.) Integration test `ChangePassword_SameAsCurrent_Returns422` confirms.

### Completion Notes List

- **All 8 ACs implemented and verified.** Backend: new `ChangePasswordRequest` DTO + validator, `IAuthService.ChangePasswordAsync` (tracked user load, bcrypt verify current, same-as-current guard, hash + `UpdatedAt` update, partial refresh-token revocation excluding the current session via `ExecuteUpdateAsync` with a `TokenHash != currentHash` predicate), `PUT /api/users/me/password` in `MeEndpoints.cs`, and a dedicated `"user-change-password"` sliding-window rate-limit policy (5/min/user).
- **Partial refresh-token revocation verified** by integration test `ChangePassword_RevokesOtherSessionsButKeepsCurrent`: with two active sessions and session A's raw token presented via the `refresh_token` cookie, exactly one active token (A) remains after the change — the user stays logged in.
- **Frontend:** `/settings` route under `_app` (auth-guarded by parent, accessible to ALL authenticated users — AC-8), Change Password card with three fields, zod mismatch-sentinel pattern (refine runs outside React hooks), success toast + field reset, inline 401/422 error mapping, generic root error fallback. `useChangePasswordMutation` sends only `{ currentPassword, newPassword }`. Header nav link added to `_app.tsx` outside `PermissionGate`, using `KeyRound` to read distinctly from the admin gear (`SettingsIcon`).
- **Validation results:**
  - Backend build: 0 warnings / 0 errors (clean under `TreatWarningsAsErrors` + CA analyzers).
  - Backend tests: **738 passed, 2 failed**. The 2 failures are the documented pre-existing audit `DELETE→405` tests (`MutationAuditLogIntegrationTests`, `SchemaAuditLogIntegrationTests`) — unrelated to this story. 6 new `MeIntegrationTests` all pass.
  - Frontend tests: **242 passed, 1 failed** — the failure is the documented pre-existing `i18n-lint.test.ts` (`designer.inspector.placeholders.label`). No new failures.
  - Frontend typecheck (`tsc -b --noEmit`): exit 0 (confirms `<Link to="/settings">` resolves; route tree regenerated by the TanStack plugin).
  - Frontend eslint: changed files add **0** new errors. `settings.tsx` and `authMutations.ts` are clean; `_app.tsx`'s lone `react-refresh/only-export-components` error is pre-existing baseline (verified absent eslint-disable in HEAD; same rule fails `login.tsx`/`index.tsx`/`designer.library.tsx`, which I did not touch).
  - i18n-check: all new keys (`settings.*`, `auth.currentPasswordIncorrect`) resolve (neither missing nor orphaned); the only missing key remains the pre-existing `designer.inspector.placeholders.label`.
- **No EF Core migration needed** — no schema change. `users.UpdatedAt` and `refresh_tokens` already exist.

### File List

**Created:**
- `src/FormForge.Api/Features/Users/Dtos/ChangePasswordRequest.cs`
- `src/FormForge.Api/Features/Users/Validators/ChangePasswordRequestValidator.cs`
- `web/src/routes/_app/settings.tsx`

**Modified:**
- `src/FormForge.Api/Features/Auth/AuthService.cs` — `ChangePasswordOutcome` enum, `ChangePasswordResult` record, `IAuthService.ChangePasswordAsync` signature + implementation
- `src/FormForge.Api/Features/Users/MeEndpoints.cs` — `PUT /me/password` registration + `ChangePasswordHandler`; added `using FormForge.Api.Common.Logging;` and `using FormForge.Api.Features.Auth;`
- `src/FormForge.Api/Program.cs` — `"user-change-password"` rate-limit policy; `ChangePasswordRequestValidator` DI registration
- `web/src/features/auth/authMutations.ts` — `useChangePasswordMutation`
- `web/src/routes/_app.tsx` — `/settings` header nav link (all users, outside `PermissionGate`); `KeyRound` import
- `web/src/lib/i18n/locales/en.json` — `auth.currentPasswordIncorrect` + top-level `settings.*` keys
- `src/FormForge.Api.Tests/Features/Users/MeIntegrationTests.cs` — 6 new integration tests + `LoginFullAsync` helper; `using System.Text.Json;`
- `web/src/routeTree.gen.ts` — regenerated by the TanStack Router plugin to include `/settings`

### Review Findings

- [x] [Review][Decision→Patch] Null `refresh_token` cookie silently revokes ALL sessions — refactored predicate to explicit `if/else`; null-cookie path explicitly revokes all (same as `ResetPasswordAsync`); non-null path revokes all except the identified session [src/FormForge.Api/Features/Auth/AuthService.cs]
- [x] [Review][Patch] `ChangePasswordAsync` does not check `user.IsActive` — added `IsActive` guard after the user-null check; deactivated users with valid JWTs now get `CurrentPasswordIncorrect` [src/FormForge.Api/Features/Auth/AuthService.cs]
- [x] [Review][Patch] `ValidationProblem` for `NewPasswordSameAsCurrent` has no `code` extension key — added `["code"] = "PASSWORD_SAME_AS_CURRENT"` to the 422 extensions [src/FormForge.Api/Features/Users/MeEndpoints.cs]
- [x] [Review][Defer] `ExecuteUpdateAsync` + `SaveChangesAsync` not in explicit transaction — if `SaveChangesAsync` fails, tokens are revoked but password unchanged; spec explicitly accepts this pattern ("same as ResetPasswordAsync, acceptable") [src/FormForge.Api/Features/Auth/AuthService.cs] — deferred, pre-existing architecture
- [x] [Review][Defer] 401 from wrong-current-password triggers `httpClient` retry, consuming 2 rate-limit slots per attempt (only ~2–3 visible tries/min, not 5) — spec dev notes explicitly say "Tolerable in v1" [web/src/lib/api/httpClient.ts] — deferred, documented known trade-off

## Change Log

| Date | Change |
|---|---|
| 2026-05-31 | Implemented Story 2.12 — authenticated self password change (`PUT /api/users/me/password`), `/settings` page, partial refresh-token revocation, rate limiting, i18n, and 6 integration tests. Status → review. |
| 2026-06-01 | Code review completed. 1 decision-needed, 2 patches, 2 deferred. Status → in-progress. |
