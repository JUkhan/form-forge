# Story 2.14: TOTP MFA Verification on Login

Status: done

## Story

As a user with MFA enabled,
I am prompted for a TOTP code after submitting my password,
so that a second factor is required to obtain a session.

## Acceptance Criteria

1. **AC-1 — MFA gate on login**
   Given I POST `/api/auth/login` with valid credentials for an account where `mfa_enabled = true`, when the password check passes, then I receive HTTP 200 `{ mfaRequired: true, mfaSessionToken: "<opaque token>" }` — no access token or refresh token is issued and no refresh cookie is set. The `mfaSessionToken` is stored in `IMemoryCache` (key → `{ userId, issuedAt, failCount: 0 }`) with a 5-minute absolute TTL.

2. **AC-2 — Valid TOTP code issues JWT pair**
   Given I submit `POST /api/auth/mfa/verify` with `{ mfaSessionToken, code }` where `code` is a valid 6-digit TOTP (Otp.NET ±1 step tolerance), when the endpoint validates the session token and the TOTP code, then I receive `{ accessToken, refreshToken, expiresIn, user }` (same shape as the non-MFA login response), the `refresh_token` HttpOnly cookie is set, and the `mfaSessionToken` is evicted from the cache (single-use).

3. **AC-3 — Valid backup code issues JWT pair**
   Given I submit a valid 8-char alphanumeric backup code in place of a TOTP code to `POST /api/auth/mfa/verify`, when the endpoint finds a matching `mfa_backup_codes` row for this user where `used_at IS NULL` (via bcrypt verify), then the JWT pair is issued, the refresh cookie set, and `used_at` is stamped on that backup code row immediately.

4. **AC-4 — Invalid or expired session → 401**
   Given the `mfaSessionToken` has expired (> 5 min) or does not exist in cache, when I POST `/api/auth/mfa/verify`, then HTTP 401 with `code: "MFA_SESSION_INVALID"` and `messageKey: "auth.mfaSessionInvalid"` is returned.

5. **AC-5 — Wrong code → 401, session preserved until 5th failure**
   Given a valid `mfaSessionToken` exists and I submit a wrong TOTP or backup code, then HTTP 401 with `code: "MFA_CODE_INVALID"` and `messageKey: "auth.mfaCodeInvalid"` is returned. The `mfaSessionToken` remains in cache so the user can retry. On the 5th consecutive wrong code, the `mfaSessionToken` is evicted from cache, forcing restart from the password step.

6. **AC-6 — Already-used backup code → 401**
   Given a backup code has already been used (`used_at IS NOT NULL`), when submitted to `POST /api/auth/mfa/verify`, then HTTP 401 with `code: "MFA_CODE_INVALID"` is returned (same envelope as wrong code — no information leak).

7. **AC-7 — Frontend MFA challenge screen**
   Given the frontend receives `{ mfaRequired: true, mfaSessionToken }` from the login response, then the login page renders a "Two-factor authentication" screen with an autofocused 6-digit code input and a "Use a backup code instead" link. Clicking the link switches to a free-text backup code input (8 chars). On successful verify, the user is redirected to the original destination. On `MFA_SESSION_INVALID`, an error message directs the user to sign in again; on `MFA_CODE_INVALID`, an inline error allows retry without page reload.

## Tasks / Subtasks

- [x] Task 1: New DTOs and validator for `POST /api/auth/mfa/verify` (AC-1, AC-2, AC-3)
  - [x] Create `src/FormForge.Api/Features/Auth/Dtos/MfaRequiredResponse.cs`
  - [x] Create `src/FormForge.Api/Features/Auth/Dtos/MfaVerifyLoginRequest.cs`
  - [x] Create `src/FormForge.Api/Features/Auth/Validators/MfaVerifyLoginRequestValidator.cs`
  - [x] Register validator + `auth-mfa-verify` rate-limit policy in `Program.cs`

- [x] Task 2: Extend `IMfaService` / `MfaService` for login-time MFA (AC-1–AC-6)
  - [x] Add `string CreateMfaSession(Guid userId)` to `IMfaService` and implement in `MfaService`
  - [x] Add `Task<MfaLoginVerifyResult> VerifyMfaLoginAsync(string mfaSessionToken, string code, CancellationToken ct)` to `IMfaService` and implement in `MfaService`
  - [x] Add `MfaLoginVerifyOutcome` enum + `MfaLoginVerifyResult` record to `MfaService.cs`

- [x] Task 3: Modify `AuthService` to support the two-step MFA login flow (AC-1, AC-2)
  - [x] Add `MfaRequired` case to `AuthLoginOutcome` enum
  - [x] Add `MfaSessionToken? string` parameter to `AuthServiceResult`
  - [x] Inject `IMfaService mfaService` into `AuthService` constructor
  - [x] Add `Task<AuthServiceResult> CompleteMfaLoginAsync(Guid userId, CancellationToken ct)` to `IAuthService` and implement (extract private `IssueLoginTokensAsync` helper from `LoginAsync`)
  - [x] Modify `LoginAsync`: after `AccountInactive` guard, if `user.MfaEnabled` → call `mfaService.CreateMfaSession(user.Id)` and return `MfaRequired` outcome without issuing JWTs

- [x] Task 4: Add `POST /api/auth/mfa/verify` to `AuthEndpoints.cs` (AC-1–AC-6)
  - [x] Register endpoint in `MapAuthEndpoints`
  - [x] Implement `VerifyMfaLoginHandler` (orchestrates `mfaService.VerifyMfaLoginAsync` then `authService.CompleteMfaLoginAsync`)
  - [x] Add `AuthLoginOutcome.MfaRequired` branch to `LoginHandler` switch

- [x] Task 5: Frontend — `httpClient.ts`, `authMutations.ts`, `types.ts` (AC-7)
  - [x] Add `/api/auth/mfa/verify` to httpClient's no-retry exclusion list
  - [x] Add `MfaRequiredResponse` and `MfaLoginApiResponse` union type to `types.ts`
  - [x] Modify `useLoginMutation` to accept optional `onMfaRequired?: (token: string) => void` and branch on `mfaRequired: true` response
  - [x] Add `useMfaLoginVerifyMutation(redirectTo?: string)` hook in `authMutations.ts`

- [x] Task 6: Frontend — `login.tsx` MFA challenge screen (AC-7)
  - [x] Add state: `mfaSessionToken`, `useBackupCode`, `mfaCode`, `mfaError`
  - [x] Pass `onMfaRequired` to `useLoginMutation` to set `mfaSessionToken` state
  - [x] Render MFA challenge section (conditionally on `mfaSessionToken !== null`) with autofocused input, backup-code toggle, error display, and verify button
  - [x] Handle `MFA_SESSION_INVALID` (show session-expired message + "Back to sign in" reset) and `MFA_CODE_INVALID` (inline error, allow retry)

- [x] Task 7: i18n strings (AC-7)
  - [x] Add `auth.mfaSessionInvalid` key in `en.json`
  - [x] Add `auth.mfaChallenge.*` key group (see Dev Notes for exact keys)

- [x] Task 8: Integration tests in `AuthIntegrationTests.cs` (AC-1–AC-6)
  - [x] Add helper methods: `EnrolMfaAsync`, `ConfirmMfaEnrolAsync`, `StartMfaLoginAsync` to test class
  - [x] `Login_MfaEnabled_Returns200WithMfaRequired_NoJwtIssued`
  - [x] `Login_MfaEnabled_NoRefreshCookieSet`
  - [x] `MfaVerify_ValidTotpCode_Returns200WithTokensAndSetsCookie`
  - [x] `MfaVerify_ValidBackupCode_Returns200_SetsUsedAt`
  - [x] `MfaVerify_InvalidSession_Returns401_MfaSessionInvalid`
  - [x] `MfaVerify_5ConsecutiveWrongCodes_LastEvictsSession`
  - [x] `MfaVerify_AlreadyUsedBackupCode_Returns401`

## Dev Notes

### Architecture Reference (AR-56, AR-2.12)

The two-step login exchange is designed as follows (per architecture Decision 2.12):
1. `POST /api/auth/login` → HTTP 200 `{ mfaRequired: true, mfaSessionToken }` — no JWT issued.
2. `POST /api/auth/mfa/verify { mfaSessionToken, code }` → JWT pair issued on success.
- `mfaSessionToken`: cryptographically random opaque token (32-char hex), stored as IMemoryCache key mapping to `{ userId, issuedAt, failCount }`, 5-min absolute TTL, single-use (evicted on first successful verify).
- Backup codes accepted in place of TOTP; used backup codes stamped with `used_at` immediately (not deleted — audit trail).
- Enrolment guard: 5 consecutive wrong codes → session eviction (user must restart password step).

---

### Task 1: New DTOs and Validator

**`src/FormForge.Api/Features/Auth/Dtos/MfaRequiredResponse.cs`** (new):
```csharp
namespace FormForge.Api.Features.Auth.Dtos;

internal sealed record MfaRequiredResponse(bool MfaRequired, string MfaSessionToken);
```

**`src/FormForge.Api/Features/Auth/Dtos/MfaVerifyLoginRequest.cs`** (new):
```csharp
namespace FormForge.Api.Features.Auth.Dtos;

internal sealed record MfaVerifyLoginRequest(string MfaSessionToken, string Code);
```

**`src/FormForge.Api/Features/Auth/Validators/MfaVerifyLoginRequestValidator.cs`** (new — folder already exists with `LoginRequestValidator`, `ForgotPasswordRequestValidator`, `ResetPasswordRequestValidator`):
```csharp
using System.Diagnostics.CodeAnalysis;
using FluentValidation;
using FormForge.Api.Features.Auth.Dtos;

namespace FormForge.Api.Features.Auth.Validators;

[SuppressMessage("Performance", "CA1812", Justification = "Instantiated via DI")]
internal sealed class MfaVerifyLoginRequestValidator : AbstractValidator<MfaVerifyLoginRequest>
{
    public MfaVerifyLoginRequestValidator()
    {
        RuleFor(x => x.MfaSessionToken)
            .NotEmpty()
            .MaximumLength(64);

        RuleFor(x => x.Code)
            .NotEmpty()
            .MaximumLength(128)
            .Matches(@"^[a-zA-Z0-9]+$").WithMessage("Code must be alphanumeric.");
    }
}
```

**Rate limit policy in `Program.cs`** — add after the `auth-reset-password` block (~line 344), before `user-change-password`:
```csharp
// Story 2.14 — mfa-verify: 10 req/min/IP. Mirrors the login rate limiter; the
// 5-consecutive-wrong-codes session eviction provides an additional application-layer guard.
options.AddPolicy("auth-mfa-verify", ctx =>
    RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
        }));
```

**`Program.cs` DI registration** — add after the `IMfaService` line (~line 174):
```csharp
builder.Services.AddScoped<IValidator<MfaVerifyLoginRequest>, MfaVerifyLoginRequestValidator>();
```

---

### Task 2: Extend `MfaService.cs`

Add the following to `MfaService.cs` **after the existing `PendingEnrolment` record and before `InitiateEnrolment`**:

```csharp
// ── Login-time MFA session ────────────────────────────────────────
internal enum MfaLoginVerifyOutcome { Success, InvalidCode, SessionInvalid }
internal sealed record MfaLoginVerifyResult(MfaLoginVerifyOutcome Outcome, Guid? UserId = null);
```

(These can live at the top of the file alongside `MfaVerifyEnrolmentOutcome` — same pattern.)

Add to `IMfaService` interface (already defined in `MfaService.cs`):
```csharp
string CreateMfaSession(Guid userId);
Task<MfaLoginVerifyResult> VerifyMfaLoginAsync(string mfaSessionToken, string code, CancellationToken ct);
```

**`CreateMfaSession` implementation** (add to `MfaService` class):
```csharp
private const string MfaSessionPrefix = "mfa:session:";
private static string SessionKey(string token) => $"{MfaSessionPrefix}{token}";
private const int MaxLoginFailAttempts = 5;

private sealed record MfaLoginSession(Guid UserId, DateTimeOffset IssuedAt, int FailCount = 0);

public string CreateMfaSession(Guid userId)
{
    // 32-char hex — same pattern as PasswordResetToken generation (AR-2.10)
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    cache.Set(
        SessionKey(token),
        new MfaLoginSession(userId, DateTimeOffset.UtcNow),
        TimeSpan.FromMinutes(5));
    return token;
}
```

**`VerifyMfaLoginAsync` implementation** (add to `MfaService` class):
```csharp
public async Task<MfaLoginVerifyResult> VerifyMfaLoginAsync(
    string mfaSessionToken, string code, CancellationToken ct)
{
    ArgumentNullException.ThrowIfNull(mfaSessionToken);
    ArgumentNullException.ThrowIfNull(code);

    if (!cache.TryGetValue(SessionKey(mfaSessionToken), out MfaLoginSession? session) || session is null)
        return new MfaLoginVerifyResult(MfaLoginVerifyOutcome.SessionInvalid);

    // Load user with backup codes (tracked — backup code UsedAt is mutated on success)
    var user = await db.Users
        .Include(u => u.BackupCodes)
        .FirstOrDefaultAsync(u => u.Id == session.UserId, ct)
        .ConfigureAwait(false);

    if (user is null || !user.MfaEnabled || user.MfaSecretProtected is null)
    {
        cache.Remove(SessionKey(mfaSessionToken));
        return new MfaLoginVerifyResult(MfaLoginVerifyOutcome.SessionInvalid);
    }

    var verified = false;

    // TOTP path: 6 decimal digits
    if (code.Length == 6 && code.All(char.IsAsciiDigit))
    {
        var base32Secret = Encoding.UTF8.GetString(_protector.Unprotect(user.MfaSecretProtected));
        var totp = new Totp(Base32Encoding.ToBytes(base32Secret));
        verified = totp.VerifyTotp(code, out _, new VerificationWindow(1, 1));
    }
    else
    {
        // Backup-code path: 8 alphanumeric chars — normalize input to uppercase so users
        // who type lowercase still match the stored uppercase codes.
        var normalizedCode = code.Trim().ToUpperInvariant();
        var matchingCode = user.BackupCodes
            .FirstOrDefault(c => c.UsedAt is null && passwordHasher.Verify(normalizedCode, c.CodeHash));

        if (matchingCode is not null)
        {
            matchingCode.UsedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            verified = true;
        }
    }

    if (verified)
    {
        cache.Remove(SessionKey(mfaSessionToken));
        return new MfaLoginVerifyResult(MfaLoginVerifyOutcome.Success, user.Id);
    }

    // Track consecutive failures. On MaxLoginFailAttempts, evict → force password re-entry.
    var newFailCount = session.FailCount + 1;
    if (newFailCount >= MaxLoginFailAttempts)
    {
        cache.Remove(SessionKey(mfaSessionToken));
    }
    else
    {
        cache.Set(
            SessionKey(mfaSessionToken),
            session with { FailCount = newFailCount },
            TimeSpan.FromMinutes(5));
    }

    return new MfaLoginVerifyResult(MfaLoginVerifyOutcome.InvalidCode);
}
```

**Required `using` for `MfaService.cs`** — already present: `System.Security.Cryptography`, `OtpNet`, `Microsoft.Extensions.Caching.Memory`. Verify `System.Linq` and `System.Text` are present (both should be via global usings).

---

### Task 3: Modify `AuthService.cs`

**Add `MfaRequired` to `AuthLoginOutcome` enum** (after `AccountInactive`):
```csharp
internal enum AuthLoginOutcome
{
    Success,
    InvalidCredentials,
    AccountInactive,
    MfaRequired, // Story 2.14: MFA challenge required — no JWT issued at this step
}
```

**Extend `AuthServiceResult`** (add optional `MfaSessionToken` property):
```csharp
internal sealed record AuthServiceResult(
    AuthLoginOutcome Outcome,
    LoginResponse? Response = null,
    string? MfaSessionToken = null);
```

**Add `IMfaService mfaService` to `AuthService` constructor** (append to existing primary constructor — `AuthService` is `internal sealed partial class` with primary constructor syntax):
```csharp
internal sealed partial class AuthService(
    FormForgeDbContext db,
    IPasswordHasher passwordHasher,
    IJwtTokenService jwtTokenService,
    IOptions<JwtOptions> jwtOptions,
    AuthMetrics metrics,
    ILogger<AuthService> logger,
    IMfaService mfaService) : IAuthService  // mfaService is new; append to existing list
```

**Add `CompleteMfaLoginAsync` to `IAuthService` interface**:
```csharp
Task<AuthServiceResult> CompleteMfaLoginAsync(Guid userId, CancellationToken ct);
```

**Refactor `LoginAsync`** — extract the "issue tokens" tail into a private method, then call it from both `LoginAsync` and `CompleteMfaLoginAsync`:

1. Add the private helper (after `HashToken`):
```csharp
private async Task<AuthServiceResult> IssueLoginTokensAsync(User user, CancellationToken ct)
{
    var roleNames = await db.UserRoles
        .Where(ur => ur.UserId == user.Id)
        .Select(ur => ur.Role.Name)
        .ToArrayAsync(ct)
        .ConfigureAwait(false);

    var accessToken = jwtTokenService.CreateAccessToken(user, roleNames);
    var (rawToken, tokenHash) = GenerateRefreshToken();

    var refreshToken = new RefreshToken
    {
        UserId = user.Id,
        TokenHash = tokenHash,
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(RefreshTokenTtlDays),
        CreatedAt = DateTimeOffset.UtcNow,
    };
    db.RefreshTokens.Add(refreshToken);
    await db.SaveChangesAsync(ct).ConfigureAwait(false);

    var ttlSeconds = jwtOptions.Value.AccessTokenTtlMinutes * 60;
    var response = new LoginResponse(
        AccessToken: accessToken,
        RefreshToken: rawToken,
        ExpiresIn: ttlSeconds,
        User: new AuthenticatedUser(
            UserId: user.Id,
            Email: user.Email,
            DisplayName: user.DisplayName,
            ThemePreference: user.ThemePreference,
            Roles: roleNames));

    return new AuthServiceResult(AuthLoginOutcome.Success, response);
}
```

2. Shorten `LoginAsync` — replace the role/token/refresh block from `var roleNames = ...` through `return new AuthServiceResult(AuthLoginOutcome.Success, response);` with:
```csharp
// MFA gate — if enabled, issue a session token instead of JWTs (AR-56)
if (user.MfaEnabled)
{
    var mfaSessionToken = mfaService.CreateMfaSession(user.Id);
    return new AuthServiceResult(AuthLoginOutcome.MfaRequired, MfaSessionToken: mfaSessionToken);
}

return await IssueLoginTokensAsync(user, ct).ConfigureAwait(false);
```

3. Implement `CompleteMfaLoginAsync`:
```csharp
public async Task<AuthServiceResult> CompleteMfaLoginAsync(Guid userId, CancellationToken ct)
{
    // User validity already confirmed by MFA verification; AsNoTracking for performance.
    var user = await db.Users
        .AsNoTracking()
        .FirstOrDefaultAsync(u => u.Id == userId, ct)
        .ConfigureAwait(false);

    // Guard against race where admin deletes user between MFA session creation and verify.
    if (user is null)
        return new AuthServiceResult(AuthLoginOutcome.InvalidCredentials);

    return await IssueLoginTokensAsync(user, ct).ConfigureAwait(false);
}
```

**Check `AuthService.cs` `using` directives** — `IMfaService` is in `FormForge.Api.Features.Auth` (same namespace); no new using needed.

---

### Task 4: `AuthEndpoints.cs` Changes

**Endpoint registration in `MapAuthEndpoints`** (add after `/reset-password`):
```csharp
// Story 2.14 — TOTP login-time MFA challenge. Open auth group (no RequireAuth).
group.MapPost("/mfa/verify", VerifyMfaLoginHandler)
     .AddValidationFilter<MfaVerifyLoginRequest>()
     .RequireRateLimiting("auth-mfa-verify")
     .WithSummary("Complete MFA-gated login — verify TOTP or backup code, receive JWT pair")
     .Produces<LoginResponse>(StatusCodes.Status200OK)
     .Produces(StatusCodes.Status401Unauthorized)
     .Produces(StatusCodes.Status422UnprocessableEntity);
```

**Update `LoginHandler` switch** — add the `MfaRequired` arm before `Success`:
```csharp
AuthLoginOutcome.MfaRequired => Results.Ok(new MfaRequiredResponse(true, result.MfaSessionToken!)),
AuthLoginOutcome.Success => SetRefreshCookieAndReturn(httpContext, env, result.Response!),
```

**`VerifyMfaLoginHandler`** (add as private static method in `AuthEndpoints`):
```csharp
private static async Task<IResult> VerifyMfaLoginHandler(
    MfaVerifyLoginRequest request,
    IMfaService mfaService,
    IAuthService authService,
    HttpContext httpContext,
    IHostEnvironment env,
    CancellationToken ct)
{
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(mfaService);
    ArgumentNullException.ThrowIfNull(authService);
    ArgumentNullException.ThrowIfNull(httpContext);
    ArgumentNullException.ThrowIfNull(env);

    var mfaResult = await mfaService.VerifyMfaLoginAsync(request.MfaSessionToken, request.Code, ct)
        .ConfigureAwait(false);

    if (mfaResult.Outcome == MfaLoginVerifyOutcome.SessionInvalid)
    {
        return Results.Problem(
            detail: "MFA session is invalid or has expired. Please sign in again.",
            title: "MFA session invalid",
            statusCode: StatusCodes.Status401Unauthorized,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "MFA_SESSION_INVALID",
                ["messageKey"] = "auth.mfaSessionInvalid",
                ["correlationId"] = httpContext.GetCorrelationId(),
            });
    }

    if (mfaResult.Outcome == MfaLoginVerifyOutcome.InvalidCode)
    {
        return Results.Problem(
            detail: "The provided code is incorrect.",
            title: "Invalid MFA code",
            statusCode: StatusCodes.Status401Unauthorized,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "MFA_CODE_INVALID",
                ["messageKey"] = "auth.mfaCodeInvalid",
                ["correlationId"] = httpContext.GetCorrelationId(),
            });
    }

    // MfaLoginVerifyOutcome.Success — issue JWT pair (same flow as non-MFA login)
    var loginResult = await authService.CompleteMfaLoginAsync(mfaResult.UserId!.Value, ct)
        .ConfigureAwait(false);

    return loginResult.Outcome == AuthLoginOutcome.Success
        ? SetRefreshCookieAndReturn(httpContext, env, loginResult.Response!)
        : Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = "errors.genericError",
                ["correlationId"] = httpContext.GetCorrelationId(),
            });
}
```

**New `using` for `AuthEndpoints.cs`** — check before adding (may already be present):
- `using FormForge.Api.Features.Auth.Dtos;` — already present (has `LoginRequest`, `LoginResponse`, etc.)

`MfaLoginVerifyOutcome` and `IMfaService` are in namespace `FormForge.Api.Features.Auth` (same as `AuthEndpoints`) — no extra using needed.

---

### Task 5: Frontend — `httpClient.ts`, `types.ts`, `authMutations.ts`

**`web/src/features/auth/httpClient.ts`** — add `/api/auth/mfa/verify` to the no-retry exclusion guard. The guard currently reads:
```typescript
path !== '/api/auth/login' &&
path !== '/api/auth/refresh' &&
```
Change to:
```typescript
path !== '/api/auth/login' &&
path !== '/api/auth/refresh' &&
path !== '/api/auth/mfa/verify' &&
```
**Why**: A 401 from `/api/auth/mfa/verify` means authentication failed (wrong code or session expired), NOT that the caller's access token expired. The refresh-and-retry loop must not fire here.

**`web/src/features/auth/types.ts`** — add the MFA-required response shapes (keep `RefreshResponse` unchanged):
```typescript
export interface MfaRequiredResponse {
  mfaRequired: true
  mfaSessionToken: string
}

// Union returned by POST /api/auth/login — either full tokens or MFA challenge
export type LoginApiResponse = RefreshResponse | MfaRequiredResponse
```

**`web/src/features/auth/authMutations.ts`** — modify `useLoginMutation` and add `useMfaLoginVerifyMutation`:

1. Add import for new types:
```typescript
import type { RefreshResponse, LoginApiResponse } from './types'
```
(Replace `import type { RefreshResponse } from './types'` if that's the current import — check the actual import line.)

2. Modify `useLoginMutation` signature to accept optional `onMfaRequired` callback:
```typescript
export function useLoginMutation(redirectTo?: string, onMfaRequired?: (token: string) => void) {
  const navigate = useNavigate()

  return useMutation({
    mutationFn: (credentials: LoginRequest) =>
      httpClient.post<LoginApiResponse>('/api/auth/login', credentials),
    onSuccess: (data) => {
      // MFA challenge — delegate to caller; do NOT set token or navigate
      if ('mfaRequired' in data && data.mfaRequired) {
        onMfaRequired?.(data.mfaSessionToken)
        return
      }
      const response = data as RefreshResponse
      tokenStore.set(response.accessToken)
      sessionCache.set(response)
      const serverTheme = response.user.themePreference
      if (
        serverTheme !== null &&
        (THEMES as readonly string[]).includes(serverTheme) &&
        localStorage.getItem('ff-theme') !== serverTheme
      ) {
        applyTheme(serverTheme as Theme)
      }
      void navigate({ to: (redirectTo ?? '/') as '/', replace: true })
    },
  })
}
```

3. Add `useMfaLoginVerifyMutation` (after `useLogoutMutation`):
```typescript
// Story 2.14 — complete MFA-gated login. Accepts mfaSessionToken + code (TOTP or backup).
export function useMfaLoginVerifyMutation(redirectTo?: string) {
  const navigate = useNavigate()

  return useMutation({
    mutationFn: (body: { mfaSessionToken: string; code: string }) =>
      httpClient.post<RefreshResponse>('/api/auth/mfa/verify', body),
    onSuccess: (data) => {
      tokenStore.set(data.accessToken)
      sessionCache.set(data)
      const serverTheme = data.user.themePreference
      if (
        serverTheme !== null &&
        (THEMES as readonly string[]).includes(serverTheme) &&
        localStorage.getItem('ff-theme') !== serverTheme
      ) {
        applyTheme(serverTheme as Theme)
      }
      void navigate({ to: (redirectTo ?? '/') as '/', replace: true })
    },
  })
}
```

---

### Task 6: Frontend — `login.tsx` MFA Challenge Screen

Add to the top of the file (after existing imports):
```typescript
import { useState } from 'react'
```
(Check whether `useState` is already imported from 'react' — if not, add it to the existing `import ... from 'react'` line or add separately.)

Add the new hook import:
```typescript
import { useLoginMutation, useMfaLoginVerifyMutation } from '../features/auth/authMutations'
```
(Replace the existing `useLoginMutation` import line.)

**New state in `LoginPage`** (add after existing `loginMutation` declaration):
```typescript
const [mfaSessionToken, setMfaSessionToken] = useState<string | null>(null)
const [useBackupCode, setUseBackupCode] = useState(false)
const [mfaCode, setMfaCode] = useState('')
const [mfaError, setMfaError] = useState('')
const loginMutation = useLoginMutation(redirectTo, (token) => setMfaSessionToken(token))
const mfaVerifyMutation = useMfaLoginVerifyMutation(redirectTo)
```

**MFA submit handler** (add inside `LoginPage`, after `onSubmit`):
```typescript
const handleMfaVerify = async () => {
  setMfaError('')
  try {
    await mfaVerifyMutation.mutateAsync({ mfaSessionToken: mfaSessionToken!, code: mfaCode })
  } catch (err) {
    if (err instanceof ApiError && err.code === 'MFA_SESSION_INVALID') {
      // Session expired — reset to password screen
      setMfaSessionToken(null)
      setMfaError(t('auth.mfaChallenge.sessionExpired'))
    } else if (err instanceof ApiError && err.code === 'MFA_CODE_INVALID') {
      setMfaError(t('auth.mfaCodeInvalid'))
    } else {
      setMfaError(t('errors.genericError'))
    }
  }
}
```

**MFA challenge JSX** — render when `mfaSessionToken !== null`. Replace the outer `return (...)` with a conditional:

```tsx
if (mfaSessionToken) {
  return (
    <div className="flex min-h-screen items-center justify-center bg-gradient-to-br from-background to-muted px-4 py-12">
      <div className="w-full max-w-md">
        <div className="mb-6 flex flex-col items-center">
          <div className="mb-3 flex h-12 w-12 items-center justify-center rounded-xl bg-primary text-primary-foreground shadow-sm">
            <LogIn className="h-6 w-6" />
          </div>
          <h1 className="text-2xl font-bold tracking-tight text-foreground">
            {t('auth.mfaChallenge.title')}
          </h1>
        </div>

        <div className="space-y-4 rounded-xl border border-border bg-card p-6 shadow-sm">
          <div className="space-y-1.5">
            <Label htmlFor="mfa-code">
              {useBackupCode ? t('auth.mfaChallenge.backupCodeLabel') : t('auth.mfaChallenge.codeLabel')}
            </Label>
            <Input
              id="mfa-code"
              type="text"
              inputMode={useBackupCode ? undefined : 'numeric'}
              maxLength={useBackupCode ? 8 : 6}
              placeholder={useBackupCode ? t('auth.mfaChallenge.backupCodePlaceholder') : t('auth.mfaChallenge.codePlaceholder')}
              value={mfaCode}
              onChange={(e) => {
                const val = useBackupCode
                  ? e.target.value.toUpperCase().replace(/[^A-Z0-9]/g, '').slice(0, 8)
                  : e.target.value.replace(/\D/g, '').slice(0, 6)
                setMfaCode(val)
              }}
              autoFocus
            />
            {mfaError && (
              <p role="alert" className="text-xs text-destructive">{mfaError}</p>
            )}
          </div>

          <Button
            className="w-full"
            disabled={
              mfaVerifyMutation.isPending ||
              (useBackupCode ? mfaCode.length !== 8 : mfaCode.length !== 6)
            }
            onClick={() => { void handleMfaVerify() }}
          >
            {mfaVerifyMutation.isPending ? t('auth.mfaChallenge.submitting') : t('auth.mfaChallenge.submitButton')}
          </Button>

          <div className="flex flex-col items-center gap-2 text-sm">
            <button
              type="button"
              className="text-primary hover:underline"
              onClick={() => {
                setUseBackupCode((prev) => !prev)
                setMfaCode('')
                setMfaError('')
              }}
            >
              {useBackupCode ? t('auth.mfaChallenge.useTotpCode') : t('auth.mfaChallenge.useBackupCode')}
            </button>
            <button
              type="button"
              className="text-muted-foreground hover:underline"
              onClick={() => {
                setMfaSessionToken(null)
                setMfaCode('')
                setMfaError('')
                setUseBackupCode(false)
              }}
            >
              {t('auth.mfaChallenge.backToSignIn')}
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}

// Existing login form JSX follows here unchanged
return (
  <div className="flex min-h-screen ...">
    ...
  </div>
)
```

**Note**: The `Label`, `Button`, `Input`, `LogIn` imports are already present in `login.tsx`. Do NOT add duplicate imports.

---

### Task 7: i18n Strings

**In `web/src/lib/i18n/locales/en.json`**, inside the `"auth"` object (add after `"mfaNoPendingEnrolment"` line ~51):
```json
"mfaSessionInvalid": "Your two-factor session has expired. Please sign in again.",
"mfaChallenge": {
  "title": "Two-factor authentication",
  "codeLabel": "Authentication code",
  "codePlaceholder": "000000",
  "backupCodeLabel": "Backup code",
  "backupCodePlaceholder": "ABCD1234",
  "useBackupCode": "Use a backup code instead",
  "useTotpCode": "Use authenticator code instead",
  "submitButton": "Verify",
  "submitting": "Verifying…",
  "sessionExpired": "Session expired. Please sign in again.",
  "backToSignIn": "Back to sign in"
}
```

**Note**: `auth.mfaCodeInvalid` already exists from Story 2.13 (`"Invalid or expired code. Please try again."`). Reuse it in `handleMfaVerify` — no duplication.

---

### Task 8: Integration Tests

**Add to `AuthIntegrationTests.cs`** — new private helper methods for MFA test setup. Add these alongside the existing `LoginAsync`/`LoginFullAsync`/`SeedTestUserAsync` helpers:

```csharp
// Requires Otp.NET reference — already added in Story 2.13 to FormForge.Api.Tests.csproj.
// Add using OtpNet; at the top of the file if not already present.

private async Task<MfaEnrolDto> EnrolMfaAsync(string bearerToken)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, "/api/users/me/mfa/enrol");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
    using var response = await _client!.SendAsync(request);
    response.EnsureSuccessStatusCode();
    var body = await response.Content.ReadFromJsonAsync<MfaEnrolDto>();
    return body!;
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

private async Task<MfaLoginStartDto> StartMfaLoginAsync(string email, string password)
{
    using var response = await _client!.PostAsJsonAsync("/api/auth/login", new { email, password });
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var body = await response.Content.ReadFromJsonAsync<MfaLoginStartDto>();
    return body!;
}

[SuppressMessage("Performance", "CA1812", Justification = "Instantiated by System.Text.Json.")]
private sealed record MfaEnrolDto(string Secret, string QrCodeDataUrl, string[] BackupCodes);

[SuppressMessage("Performance", "CA1812", Justification = "Instantiated by System.Text.Json.")]
private sealed record MfaLoginStartDto(bool MfaRequired, string MfaSessionToken);
```

**Test setup pattern** (used in each MFA test):
```csharp
// 1. Login to get a JWT (MFA not yet enabled)
var (accessToken, _) = await LoginFullAsync("test@example.com", "Password1!");
// 2. Enrol MFA
var enrol = await EnrolMfaAsync(accessToken);
// 3. Confirm enrolment with a valid TOTP
var confirmCode = new Totp(Base32Encoding.ToBytes(enrol.Secret)).ComputeTotp();
await ConfirmMfaEnrolAsync(accessToken, confirmCode);
// 4. Login again — should now return MFA challenge
var mfaStart = await StartMfaLoginAsync("test@example.com", "Password1!");
Assert.True(mfaStart.MfaRequired);
Assert.False(string.IsNullOrEmpty(mfaStart.MfaSessionToken));
```

**IMPORTANT**: Each test must enrol MFA from scratch because `InitializeAsync` truncates all users and re-seeds a fresh user without MFA. This means each test that needs MFA performs the 4-step setup above before testing the actual AC.

**Test list and key assertions**:

- `Login_MfaEnabled_Returns200WithMfaRequired_NoJwtIssued` — response body is `{ mfaRequired: true, mfaSessionToken }`, no `accessToken` field.
- `Login_MfaEnabled_NoRefreshCookieSet` — after MFA login start, check `response.Headers` for `Set-Cookie` absence (or cookie with empty value/past expiry).
- `MfaVerify_ValidTotpCode_Returns200WithTokensAndSetsCookie` — setup MFA, compute fresh TOTP, call `POST /api/auth/mfa/verify`, assert 200, assert `accessToken` in body, assert `Set-Cookie: refresh_token=...` header present.
- `MfaVerify_ValidBackupCode_Returns200_SetsUsedAt` — use one of `enrol.BackupCodes` as the code, assert 200, then query DB to confirm `mfa_backup_codes.used_at IS NOT NULL` for that code's hash.
- `MfaVerify_InvalidSession_Returns401_MfaSessionInvalid` — call with a non-existent/random `mfaSessionToken`, assert 401 and `code == "MFA_SESSION_INVALID"`.
- `MfaVerify_5ConsecutiveWrongCodes_LastEvictsSession` — submit 4 wrong codes (assert 401 `MFA_CODE_INVALID` each time), then submit a 5th wrong code (same assertion), then submit a correct code — expect 401 `MFA_SESSION_INVALID` (session evicted).
- `MfaVerify_AlreadyUsedBackupCode_Returns401` — use a backup code successfully once, then submit the same backup code again → 401 `MFA_CODE_INVALID`.

**Test baseline**: 746 passed, 2 pre-existing failures (audit 405 tests). New tests must pass; target 746 + N new tests passed.

**`using` additions for `AuthIntegrationTests.cs`**:
```csharp
using OtpNet; // already added to FormForge.Api.Tests.csproj in Story 2.13
using System.Net.Http.Headers;
```
(Check existing `using` block — `System.Net.Http.Headers` may already be present.)

---

### Anti-Pattern Prevention

**DO NOT** issue a JWT or set the refresh cookie when `LoginAsync` returns `MfaRequired` — no tokens at the password step.

**DO NOT** evict the session on wrong code until the 5th consecutive failure — the user must be able to retry with the next 30-second TOTP window without rescanning the QR.

**DO NOT** delete a backup code row when it is used — stamp `used_at` only. The row is an audit trail.

**DO NOT** bypass the `mfaSessionToken` lookup if `userId` is somehow derivable from context — the token IS the proof that the password step was passed.

**DO NOT** add `.ConfigureAwait(false)` to the `.mutateAsync()` call in frontend code — that's a C# pattern; TypeScript `async/await` does not use it.

**DO NOT** reuse `auth.mfaNoPendingEnrolment` for `MFA_SESSION_INVALID` — they are semantically different error codes. Use the new `auth.mfaSessionInvalid` key.

**DO NOT** treat the `MfaVerifyLoginHandler` as the enrolment verify endpoint (`POST /api/users/me/mfa/verify` in `MeEndpoints`) — they are different routes with different logic:
- `/api/auth/mfa/verify` = login-time challenge (Story 2.14, uses `mfaSessionToken`)
- `/api/users/me/mfa/verify` = enrolment confirmation (Story 2.13, uses pending enrolment cache)

**DO NOT** add rate-limit attribute `RequireRateLimiting` to `GET /api/users/me/mfa/enrol` or `POST /api/users/me/mfa/verify` (enrolment endpoints) — those are deferred (W2/W3 from 2.13 review). Only the new `POST /api/auth/mfa/verify` (login endpoint) gets `auth-mfa-verify`.

**DO NOT** check `string.All(char.IsDigit)` for TOTP validation — use `char.IsAsciiDigit` (the modern overload) to avoid non-ASCII Unicode digits that bcrypt/Otp.NET would reject.

**DO NOT** call `Include(u => u.BackupCodes)` without tracked load in `VerifyMfaLoginAsync` — backup code's `UsedAt` is mutated; tracked load is required.

**DO NOT** normalize the TOTP code (no `.Trim()` or `.ToUpperInvariant()`) — TOTP codes are pure digits; normalizing them would be a no-op for the happy path and could inadvertently produce a matching hash for a malformed input. Only backup codes get `.Trim().ToUpperInvariant()`.

**DO NOT** add `async Task<IResult>` to `VerifyMfaLoginHandler` if you forget to `await` — the compiler will warn. Keep all handlers `async Task<IResult>` (both service calls are awaited).

---

### Previous Story Learnings (2.13)

- **`ConfigureAwait(false)` on every `await` in C#** — the build analyzer enforces this; check every new `await` in `AuthService.cs`, `MfaService.cs`, `AuthEndpoints.cs`.
- **`[SuppressMessage("Performance", "CA1812")]`** — required on all `internal sealed class` types registered via DI (`MfaVerifyLoginRequestValidator`).
- **`ArgumentNullException.ThrowIfNull`** — required on all handler and service method parameters.
- **`AuthService` is `internal sealed partial class`** — the `partial` keyword is there for `[LoggerMessage]` source generators. Do NOT remove `partial`.
- **No `AddValidationFilter` on GET endpoints** — `GET /api/auth/mfa/verify` does not exist; only `POST /api/auth/mfa/verify` gets the filter.
- **Pre-existing test failures**: 2 DELETE→405 audit tests + 1 i18n-lint failure — ignore.
- **Primary constructor syntax** for `AuthService` — the injected parameters are declared in the class header `AuthService(...)`. When adding `IMfaService mfaService`, append it to the existing parameter list; do NOT add a separate field.
- **`LoginResponse` shape** — must remain unchanged for non-MFA logins; `MfaRequiredResponse` is a separate type for the MFA gate response.
- **`void` prefix pattern for async event handlers in TSX** — `onClick={() => { void handleMfaVerify() }}` matches the project style.

---

### Files to CREATE

| File | Purpose |
|---|---|
| `src/FormForge.Api/Features/Auth/Dtos/MfaRequiredResponse.cs` | Response DTO for MFA-gated login |
| `src/FormForge.Api/Features/Auth/Dtos/MfaVerifyLoginRequest.cs` | Request DTO for `POST /api/auth/mfa/verify` |
| `src/FormForge.Api/Features/Auth/Validators/MfaVerifyLoginRequestValidator.cs` | FluentValidation |

### Files to MODIFY

| File | Change |
|---|---|
| `src/FormForge.Api/Features/Auth/AuthService.cs` | Add `MfaRequired` outcome, `MfaSessionToken` in result, `IMfaService` injection, `CompleteMfaLoginAsync`, refactor `LoginAsync` |
| `src/FormForge.Api/Features/Auth/MfaService.cs` | Add `MfaLoginVerifyOutcome`, `MfaLoginVerifyResult`, `CreateMfaSession`, `VerifyMfaLoginAsync` |
| `src/FormForge.Api/Features/Auth/AuthEndpoints.cs` | Add `POST /mfa/verify` endpoint + handler; update `LoginHandler` switch |
| `src/FormForge.Api/Program.cs` | Register `auth-mfa-verify` rate policy; register `MfaVerifyLoginRequestValidator` |
| `web/src/features/auth/httpClient.ts` | Add `/api/auth/mfa/verify` to no-retry exclusion list |
| `web/src/features/auth/types.ts` | Add `MfaRequiredResponse` and `LoginApiResponse` union |
| `web/src/features/auth/authMutations.ts` | Modify `useLoginMutation`; add `useMfaLoginVerifyMutation` |
| `web/src/routes/login.tsx` | MFA challenge screen (conditional render on mfaSessionToken state) |
| `web/src/lib/i18n/locales/en.json` | `auth.mfaSessionInvalid`, `auth.mfaChallenge.*` keys |
| `src/FormForge.Api.Tests/Features/Auth/AuthIntegrationTests.cs` | MFA test helpers + 7 new test methods |

### Files to Leave Untouched

- `src/FormForge.Api/Features/Users/MeEndpoints.cs` — enrolment endpoints (2.13) unchanged
- `src/FormForge.Api/Features/Auth/AuthEndpoints.cs`'s `ForgotPasswordHandler`, `ResetPasswordHandler`, `RefreshHandler`, `LogoutHandler` — untouched
- `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` — no schema changes needed
- All migrations — no DB schema change required for 2.14 (`mfa_backup_codes.used_at` already exists from 2.13)
- `web/src/routes/_app/settings.tsx` — MFA enrolment UI (2.13) unchanged
- All Designer, Menu, CRUD, Provisioning files — unrelated

---

### References

- [Source: `_bmad-output/planning-artifacts/epics.md` — Story 2.14 full AC (lines listing `mfaRequired`, `mfaSessionToken`, 5-failure eviction)]
- [Source: `_bmad-output/planning-artifacts/architecture.md` — Decision 2.12 TOTP MFA (two-step login exchange, IMemoryCache session, backup code stamping)]
- [Source: `src/FormForge.Api/Features/Auth/AuthService.cs` — existing `LoginAsync` pattern to extend; `IssueLoginTokensAsync` private helper to extract]
- [Source: `src/FormForge.Api/Features/Auth/AuthEndpoints.cs` — `LoginHandler`/`SetRefreshCookieAndReturn` pattern; endpoint registration style]
- [Source: `src/FormForge.Api/Features/Auth/MfaService.cs` — existing `PendingEnrolment` cache pattern and `_protector` usage to mirror for `MfaLoginSession`]
- [Source: `src/FormForge.Api/Features/Auth/Validators/` — folder exists with `LoginRequestValidator`, validator namespace/pattern to follow]
- [Source: `src/FormForge.Api/Program.cs` — rate limiter block at ~line 299; DI registrations at ~line 151]
- [Source: `web/src/features/auth/httpClient.ts` — no-retry guard at `path !== '/api/auth/login'`; extend with `/api/auth/mfa/verify`]
- [Source: `web/src/features/auth/authMutations.ts` — `useLoginMutation` full implementation to modify]
- [Source: `web/src/routes/login.tsx` — existing login form structure to extend with MFA challenge conditional]
- [Source: `web/src/features/auth/types.ts` — `RefreshResponse` shape; add `MfaRequiredResponse`]
- [Source: `web/src/lib/i18n/locales/en.json` — `auth.mfaCodeInvalid` line ~50; `auth` object to extend]
- [Source: `_bmad-output/implementation-artifacts/2-13-totp-mfa-enrolment.md` — anti-pattern list, `ConfigureAwait(false)` reminder, SuppressMessage pattern]

## Dev Agent Record

### Agent Model Used

claude-sonnet-4-6 (story authoring)
claude-opus-4-8 (implementation)

### Debug Log References

- **Pre-existing build break fixed (out of story scope, required to build):** the Story 2.13
  migration `Migrations/20260531233219_AddDataProtectionKeys.cs` was missing the
  `// <auto-generated />` header that every other migration carries. Without it, Roslyn treats
  the file as hand-written code and CA1062 (validate `migrationBuilder` non-null) fails the
  build as an error. Confirmed pre-existing by stashing all Story 2.14 changes and building
  HEAD — the same 2 CA1062 errors occur there. Fix: added the `// <auto-generated />` header
  (the same analyzer opt-out every sibling migration uses). No DDL change.
- **Self-introduced TS error caught and fixed during validation:** the first cut of
  `useLoginMutation` accessed `data.accessToken` after the `mfaRequired` guard without
  narrowing the `LoginApiResponse` union; `tsc` failed. Fixed by restoring the
  `const response = data as RefreshResponse` cast specified in the Dev Notes. `tsc` now exit 0.

### Completion Notes List

- Implemented the two-step MFA login exchange end to end (AC-1 … AC-7).
- Backend: new `MfaRequiredResponse` / `MfaVerifyLoginRequest` DTOs + validator; `IMfaService`
  gained `CreateMfaSession` (opaque 32-char hex token, IMemoryCache, 5-min absolute TTL) and
  `VerifyMfaLoginAsync` (TOTP ±1 step; backup-code bcrypt verify with `used_at` stamping;
  single-use eviction on success; 5-consecutive-failure eviction); `AuthService` gained the
  `MfaRequired` outcome, `MfaSessionToken`, `IMfaService` injection, an extracted
  `IssueLoginTokensAsync` helper, and `CompleteMfaLoginAsync`; `LoginHandler` gained the
  `MfaRequired` arm; new `POST /api/auth/mfa/verify` endpoint (`auth-mfa-verify`, 10/min/IP).
- Frontend: `httpClient` excludes `/api/auth/mfa/verify` from the 401-refresh-retry loop;
  `useLoginMutation` branches on `mfaRequired`; new `useMfaLoginVerifyMutation`; `login.tsx`
  renders the MFA challenge screen (autofocused 6-digit input, backup-code toggle, inline
  error retry, session-expired reset) with new `auth.mfaChallenge.*` + `auth.mfaSessionInvalid`
  i18n keys. `auth.mfaCodeInvalid` reused from Story 2.13.
- **Validation (confirmed via process exit codes + targeted output inspection):**
  - `dotnet build` FormForge.Api — succeeded, 0 warnings / 0 errors.
  - `dotnet build` FormForge.Api.Tests — succeeded, 0 warnings / 0 errors.
  - `dotnet test` full suite (Docker/Testcontainers PostgreSQL) — **753 passed / 2 failed /
    755 total**. All 7 new `AuthIntegrationTests` MFA tests passed. The 2 failures are the
    pre-existing, documented audit DELETE→405 tests
    (`SchemaAuditLogIntegrationTests.GetSchemaAuditLog_AppendOnly_DeleteVerb_Returns405`,
    `MutationAuditLogIntegrationTests.GetMutationAuditLog_AppendOnly_DeleteVerb_Returns405`) —
    red on a clean tree, unrelated to this story.
  - Frontend `tsc -b --noEmit` — exit 0.
  - `eslint` (changed files) — only finding is the pre-existing
    `react-refresh/only-export-components` on `login.tsx`'s `Route`+component pattern, identical
    at HEAD with changes stashed (line shifted only by the one added import). Not introduced here.
  - `vitest run` — **242 passed / 1 failed**. The single failure is the pre-existing
    `i18n-lint.test.ts` missing key `designer.inspector.placeholders.label`. The new
    `auth.mfaChallenge.*` keys are referenced and not flagged; `auth.mfaSessionInvalid` appears
    only as an "orphaned" warning (not a failure), consistent with the other backend
    `messageKey` entries (`auth.invalidCredentials`, `auth.accountInactive`, etc.).

### Completion Notes List (Review Resolution — 2026-06-01)

- Addressed all 6 open review items. ✅ Resolved review finding [Patch]: atomic backup-code single-use (`ExecuteUpdateAsync` WHERE `used_at IS NULL`). ✅ Resolved [Patch]: absolute MFA-session TTL (remaining-window re-Set; `IssuedAt` now live). ✅ Resolved [Patch]: deleted-user race → 401 `MFA_SESSION_INVALID` + logged Warning (was generic 500). ✅ Resolved [Patch]: validator narrowed to `^(\d{6}|[a-zA-Z0-9]{8})$`. ✅ Resolved [Decision-1]: per-token `SemaphoreSlim` gate makes session consume atomic (concurrent same-token double-issue eliminated); cross-session TOTP replay accepted as residual risk per user. ✅ Resolved [Decision-2]: per-user brute-force lockout accepted & documented (rely on 10/min/IP limiter).
- No new files created in this pass; all edits are to files already in the File List. No frontend changes (every finding was backend).
- Validation: `dotnet build` API + Tests — 0 warnings / 0 errors. Full `dotnet test` — **757 passed / 2 failed / 759 total**; the 2 failures are the documented pre-existing audit `DELETE→405` tests (`MutationAuditLogIntegrationTests`/`SchemaAuditLogIntegrationTests`), red on a clean tree. 4 new MFA review-hardening tests all green: `MfaVerify_MalformedCode_Returns422_BeforeService`, `MfaVerify_UserDeletedBeforeVerify_Returns401_NotServerError`, `MfaVerify_ConcurrentSameToken_IssuesTokensOnce`, `MfaVerify_ConcurrentSameBackupCode_StampsOnce`.

### File List

**Created**
- `src/FormForge.Api/Features/Auth/Dtos/MfaRequiredResponse.cs`
- `src/FormForge.Api/Features/Auth/Dtos/MfaVerifyLoginRequest.cs`
- `src/FormForge.Api/Features/Auth/Validators/MfaVerifyLoginRequestValidator.cs`

**Modified**
- `src/FormForge.Api/Features/Auth/MfaService.cs`
- `src/FormForge.Api/Features/Auth/AuthService.cs`
- `src/FormForge.Api/Features/Auth/AuthEndpoints.cs`
- `src/FormForge.Api/Program.cs`
- `src/FormForge.Api.Tests/Features/Auth/AuthIntegrationTests.cs`
- `web/src/features/auth/httpClient.ts`
- `web/src/features/auth/types.ts`
- `web/src/features/auth/authMutations.ts`
- `web/src/routes/login.tsx`
- `web/src/lib/i18n/locales/en.json`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260531233219_AddDataProtectionKeys.cs` (pre-existing build-break fix — see Debug Log)

### Change Log

| Date | Change |
|---|---|
| 2026-06-01 | Implemented Story 2.14 (TOTP MFA verification on login) — Tasks 1–8: backend two-step MFA exchange, frontend challenge screen, i18n keys, and 7 integration tests. |
| 2026-06-01 | Fixed pre-existing build break: added missing `// <auto-generated />` header to the Story 2.13 `AddDataProtectionKeys` migration (CA1062). |
| 2026-06-01 | Ran full .NET suite (753 passed / 2 pre-existing audit-405 failures; 7 new MFA tests green) + frontend tsc (clean) / eslint (1 pre-existing) / vitest (242 passed / 1 pre-existing). Story → review. |
| 2026-06-01 | Applied code-review patches (4 fixes) + Decision-1 atomic consume; Decision-2 accepted & documented. Backend-only: atomic backup-code stamp, absolute MFA-session TTL, deleted-user race → 401 + log, validator narrowed, per-token verify gate. Added 4 integration tests. Full .NET suite 757 passed / 2 pre-existing audit-405 failures. No frontend changes. |

## Review Findings

_Adversarial code review 2026-06-01 (Blind Hunter + Edge Case Hunter + Acceptance Auditor). All 7 ACs and all 12 anti-patterns verified SATISFIED by the Acceptance Auditor. The items below are defense-in-depth/correctness gaps the spec did not mandate._

### Decision Needed

- [x] [Review][Decision] **MFA session consume + TOTP code reuse are not atomic / not deduped** — `VerifyMfaLoginAsync` only `cache.Remove`s the session *after* a successful verify, and the read→verify→remove sequence is non-atomic. Two concurrent verify requests carrying the same `mfaSessionToken` + same valid TOTP can both pass and both issue a JWT pair. Separately, a captured valid TOTP can be replayed against a *different* freshly-minted session within the ±1-step (~90s) window because no last-used TOTP counter is persisted per user (RFC 6238 §5.2). Hardening the cross-session replay needs a design call (persist last-used step on the user row vs. accept the risk). `[src/FormForge.Api/Features/Auth/MfaService.cs — VerifyMfaLoginAsync]`
- [x] [Review][Decision] **Per-session 5-failure cap is bypassable; no per-user brute-force lockout** — The `MaxLoginFailAttempts = 5` eviction is per-session-token only. An attacker who already holds the password can call `/api/auth/login` repeatedly to mint fresh sessions (each `FailCount = 0`), so the real online-guess budget against a 6-digit TOTP is bounded only by the `auth-mfa-verify` 10/min/IP limiter, not by the 5-failure guard. AC-5 (per-session eviction) IS met; per-user lockout was never specified — adding it is a product decision. `[src/FormForge.Api/Features/Auth/MfaService.cs]`

### Patch

- [x] [Review][Patch] **Backup-code single-use is a non-atomic check-then-act (concurrent double-spend)** `[src/FormForge.Api/Features/Auth/MfaService.cs — VerifyMfaLoginAsync, backup-code branch]` — Match is `FirstOrDefault(c => c.UsedAt is null && Verify(...))` then `matchingCode.UsedAt = now; SaveChangesAsync()` with no atomic guard. Two concurrent requests with the same valid backup code both see `UsedAt == null` and both succeed → one single-use code authenticates two sessions. The codebase already has the correct pattern in `AuthService.ResetPasswordAsync` (`ExecuteUpdateAsync` with `WHERE UsedAt == null` + rowcount check). Mirror it: after the in-memory bcrypt match, stamp via `ExecuteUpdateAsync(... WHERE Id == matchingCode.Id && UsedAt == null ...)` and treat rowcount 0 as already-used. The sequential single-use test passes; it does not cover the concurrent window.
- [x] [Review][Patch] **"5-minute absolute TTL" is actually a sliding TTL; `IssuedAt` is dead** `[src/FormForge.Api/Features/Auth/MfaService.cs]` — Each wrong guess re-`Set`s the session with a fresh `TimeSpan.FromMinutes(5)`, and `MfaLoginSession.IssuedAt` is recorded but never read, so a client failing once every <5 min keeps the post-password/pre-JWT session alive far past the documented hard cap. Either enforce the absolute cap (check `UtcNow - IssuedAt >= 5min` at entry, or compute the remaining TTL from `IssuedAt` on re-Set) or drop the misleading comment + dead field.
- [x] [Review][Patch] **Deleted-user race returns HTTP 500 instead of 401 and isn't logged** `[src/FormForge.Api/Features/Auth/AuthEndpoints.cs — VerifyMfaLoginHandler; AuthService.cs — CompleteMfaLoginAsync]` — If the user is deleted between session creation and verify, `CompleteMfaLoginAsync` returns `InvalidCredentials`, which the handler's non-Success arm maps to a generic 500 with no log line — an expected race surfaced as a server fault, polluting error dashboards. Map this to 401 (e.g. `MFA_SESSION_INVALID`) and log. (Note: the backup code is already stamped `used_at` before this point, so on this race the code is burned for a now-deleted user — acceptable given the user is gone, but resolved by the same handling.)
- [x] [Review][Patch] **`mfa-verify` validator accepts up to 128 chars → bcrypt amplification on the backup path** `[src/FormForge.Api/Features/Auth/Validators/MfaVerifyLoginRequestValidator.cs]` — Any non-6-digit input routes to the backup branch and is bcrypt-verified against every unused backup code (~8–10 × ~250ms). The validator's `MaximumLength(128)` + `^[a-zA-Z0-9]+$` does not narrow `Code` to the two legitimate shapes (exactly 6 digits OR exactly 8 alphanumerics). Tighten the rule to those shapes to cap the work-per-request. (Deviates from the spec's prescribed `MaximumLength(128)`, but reduces a mild DoS-amplification vector with no AC impact.)

### Dismissed (5, recorded for traceability)

- TOTP/backup branch normalization inconsistency (`Trim()` only in backup branch) — unreachable: the validator's `^[a-zA-Z0-9]+$` rejects whitespace before the service sees it; branch-by-length is correct since backup codes are always 8 chars.
- Frontend leaves stale code in the box / no attempt-budget awareness on `MFA_CODE_INVALID` — UX preference, not a defect; the next submit on an evicted session correctly resets to the password screen.
- `useMfaLoginVerifyMutation` casts to `RefreshResponse` without union-narrowing — `/api/auth/mfa/verify` only ever returns the full `LoginResponse` (includes `user`); the narrowing on `/login` exists solely because that route returns two shapes.
- Empty-string `mfaSessionToken` silent dead-end — backend always emits a 32-hex token (`result.MfaSessionToken!`); requires a serialization regression to trigger.
- `CompleteMfaLoginAsync` `AsNoTracking` + `IssueLoginTokensAsync` tracking mismatch — false positive; the refresh token is persisted via explicit `db.RefreshTokens.Add`, not the tracked graph (confirmed).

### Review Resolution (2026-06-01)

User decisions: apply all 4 Patches; Decision 1 → atomic consume only (no migration); Decision 2 → accept & document.

- **[Decision-1] Atomic consume (implemented).** Added a static per-session-token `SemaphoreSlim` gate (`SessionGates`) in `MfaService` that serializes verification of a given `mfaSessionToken`, so the read→verify→consume cycle is atomic — concurrent same-token + same-valid-TOTP requests can no longer both issue a JWT pair (the loser sees the consumed session as `SessionInvalid`). The gate is dropped from the dictionary once the session is gone (consumed/evicted) to bound memory; it is deliberately not `Dispose()`d (a racing caller may hold the same instance and we never touch `AvailableWaitHandle`, so GC reclaims it safely). **Cross-session TOTP replay** within the ±1-step (~90 s) window is an *accepted residual risk* per the user's choice — it requires both the password and a live code captured inside the window; blocking it needs a per-user last-used-step column (a DB migration), explicitly deferred.
- **[Decision-2] Per-user brute-force lockout — accepted & documented.** No code change. The per-session 5-failure eviction (AC-5) stands; online-guessing against a 6-digit TOTP remains bounded by the `auth-mfa-verify` 10/min/IP limiter. Per-user lockout was never specified and would add an account-lockout DoS surface; it is recorded here as a deferred product decision.
- **[Patch-1] Atomic backup-code single-use.** Replaced the check-then-act (`FirstOrDefault(... UsedAt is null ...)` → mutate → `SaveChanges`) with an in-memory bcrypt match followed by `ExecuteUpdateAsync(... WHERE Id == matchingCode.Id && UsedAt == null ...)`; `verified = stamped > 0`. Mirrors `AuthService.ResetPasswordAsync`. Two concurrent sessions racing one backup code now stamp it exactly once (the DB row lock serialises; the loser gets 0 rows → `InvalidCode`). User load switched to `AsNoTracking()` since nothing is mutated through the tracker anymore.
- **[Patch-2] Absolute MFA-session TTL.** Introduced `MfaSessionLifetime = 5 min`; on a wrong-code re-`Set` the remaining TTL is now computed from `IssuedAt` (`IssuedAt + lifetime - UtcNow`) instead of a fresh 5 min, so failed guesses can no longer slide the cap. `IssuedAt` is now live; if the remaining window is ≤0 the session is evicted.
- **[Patch-3] Deleted-user race → 401 + log.** `VerifyMfaLoginHandler` now maps a non-`Success` `CompleteMfaLoginAsync` outcome to **401 `MFA_SESSION_INVALID`** (not a generic 500) and logs `AuthEndpointsLog.MfaCompleteUserMissing` (Warning, includes UserId + correlationId). The `GetCorrelationId()` call is hoisted to a local to satisfy CA1873.
- **[Patch-4] Validator narrowing.** `MfaVerifyLoginRequestValidator.Code` regex tightened from `^[a-zA-Z0-9]+$` + `MaximumLength(128)` to `^(\d{6}|[a-zA-Z0-9]{8})$` — only the two legitimate shapes pass, capping the bcrypt work per request. Malformed input now returns 422 before the service runs.

### Review Findings — Pass 2 (2026-06-01)

_Adversarial code review pass 2 (Blind Hunter + Edge Case Hunter + Acceptance Auditor) against the post-patch implementation including uncommitted changes. All 7 ACs confirmed SATISFIED. 13 findings dismissed as false positives or pre-existing/accepted risks. 4 patches and 6 defers follow._

#### Patch

- [x] [Review][Patch] **Validator regex `\d{6}` accepts Unicode decimal digits — input misrouted to backup-code bcrypt path** `[src/FormForge.Api/Features/Auth/Validators/MfaVerifyLoginRequestValidator.cs]` — .NET `\d` matches all Unicode decimal digit categories (Arabic-Indic, fullwidth, etc.), not just ASCII 0–9. A 6-character string of Unicode digits (e.g. `٦٧٨٩٠١`) passes the validator's `^(\d{6}|[a-zA-Z0-9]{8})$` rule, but `code.All(char.IsAsciiDigit)` in `VerifyMfaLoginCoreAsync` returns false, routing the input to the backup-code branch where bcrypt verify fails harmlessly. No credential bypass, but the validator contract is violated. Fix: change the TOTP alternative to `[0-9]{6}` (ASCII digits only): `^([0-9]{6}|[a-zA-Z0-9]{8})$`.
- [x] [Review][Patch] **MFA_SESSION_INVALID error message is silently lost when returning to password screen** `[web/src/routes/login.tsx — handleMfaVerify]` — When `MFA_SESSION_INVALID` fires, `setMfaSessionToken(null)` and `setMfaError(t('auth.mfaChallenge.sessionExpired'))` are batched in the same React update. Setting `mfaSessionToken` to null unmounts the MFA challenge screen (the `if (mfaSessionToken)` block), so `mfaError` is set but has no element to render it — the user is silently returned to the password form with no explanation. Fix: add a separate `loginScreenError` state (or reuse `mfaError` as a general login-page error) that persists after the challenge screen tears down and is displayed on the password form.
- [x] [Review][Patch] **MFA challenge has no `<form>` wrapper — Enter key does not submit the code** `[web/src/routes/login.tsx — MFA challenge JSX]` — The challenge screen renders a plain `<div>` with an `onClick` handler on the Button. Pressing Enter in the autofocused `<Input id="mfa-code">` fires no submit handler, breaking both keyboard-only users and the standard UX expectation for a code-entry form. Fix: wrap the challenge content in `<form onSubmit={(e) => { e.preventDefault(); void handleMfaVerify() }}>` and change the Button `type` to `"submit"`.
- [x] [Review][Patch] **`SessionGates` memory leak: entries for naturally-expired sessions are never removed** `[src/FormForge.Api/Features/Auth/MfaService.cs — CreateMfaSession / SessionGates]` — `SessionGates.TryRemove` is only called inside `VerifyMfaLoginAsync`'s `finally` block. If a session expires via `IMemoryCache` TTL (user closes browser before completing MFA), no eviction callback fires on `SessionGates`, so the `SemaphoreSlim` entry accumulates unbounded. Fix: register a `PostEvictionCallback` when creating the cache entry so the gate is cleaned up on natural expiry: use `cache.CreateEntry(SessionKey(token))` with `entry.RegisterPostEvictionCallback((key, _, _, _) => { SessionGates.TryRemove(((string)key)[MfaSessionPrefix.Length..], out _); })`.

#### Defer

- [x] [Review][Defer] **Cross-session TOTP replay within ±90 s window** `[src/FormForge.Api/Features/Auth/MfaService.cs]` — deferred, pre-existing. Accepted residual risk per Decision-1 in prior review pass; fixing requires a per-user last-used-step column and a DB migration. The per-token `SemaphoreSlim` gate prevents same-session double-issue; cross-session replay requires both knowledge of the password and interception of a live TOTP code.
- [x] [Review][Defer] **Per-user brute-force lockout bypass via fresh session minting** `[src/FormForge.Api/Features/Auth/MfaService.cs]` — deferred, pre-existing. Accepted per Decision-2 in prior review pass; per-user lockout was never specified and adds an account-lockout DoS surface. Rate-limited to 10/min/IP.
- [x] [Review][Defer] **MFA session token delivered in JSON response body — accessible to XSS** `[src/FormForge.Api/Features/Auth/AuthEndpoints.cs, web/src/features/auth/authMutations.ts]` — deferred, architectural decision. Spec-defined response shape `{ mfaRequired: true, mfaSessionToken }` is a JSON body. Token is short-lived (5 min) and single-use. Hardening requires a server-set HttpOnly cookie for the session token, a non-trivial protocol change.
- [x] [Review][Defer] **Timing oracle: empty or exhausted backup codes take ~0 ms (no bcrypt work)** `[src/FormForge.Api/Features/Auth/MfaService.cs — VerifyMfaLoginCoreAsync, backup-code branch]` — deferred. When `user.BackupCodes` is empty or all codes have `UsedAt != null`, `FirstOrDefault` returns null and no bcrypt calls are made, producing a measurably faster response. Observing this timing difference leaks that no unused backup codes remain. Exploitation requires sub-millisecond timing precision. Mitigate in a future hardening pass with a constant-time dummy bcrypt call when no candidates are found.
- [x] [Review][Defer] **`mfaResult.UserId!.Value` null-suppression is fragile to future outcome variants** `[src/FormForge.Api/Features/Auth/AuthEndpoints.cs — VerifyMfaLoginHandler]` — deferred. Safe today: all non-Success outcomes are guarded by early returns before the dereference. If a future `MfaLoginVerifyOutcome` variant has a null `UserId` and no early-return guard, this becomes a `NullReferenceException`. Consider changing `UserId` to a non-nullable `Guid` on the `Success` variant via a discriminated-union pattern.
- [x] [Review][Defer] **Backup-code toggle button lacks `aria-pressed` / accessible state** `[web/src/routes/login.tsx — MFA challenge JSX]` — deferred. The `<button type="button">` toggles between "Use a backup code instead" and "Use authenticator code instead" but carries no `aria-pressed` or `aria-describedby` state to communicate the active mode to screen readers. Accessibility polish; address in a dedicated a11y pass.
