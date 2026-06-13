# Story 2.11: Forgot Password Flow

Status: done

## Story

As an unauthenticated user who cannot access my account,
I want to request a password reset via email and follow the reset link to set a new password,
so that I can regain access without admin involvement.

## Acceptance Criteria

1. **AC-1 — Anti-enumeration: always HTTP 200**
   Given any user submits an email to `POST /api/auth/forgot-password`, when the endpoint processes the request, it always returns HTTP 200 with `"If that email is registered, a reset link has been sent."` regardless of whether the email is registered.

2. **AC-2 — Token generation and storage**
   Given the submitted email belongs to an active, registered user, when the endpoint processes the request, a 32-byte random token is generated via `RandomNumberGenerator.GetBytes(32)` and hex-encoded to a 64-char string; only the SHA-256 hash of that hex string is stored in `password_reset_tokens` with `expires_at = now() + 1 hour` and `used_at = null`.

3. **AC-3 — Reset email dispatched asynchronously**
   A reset-link email is dispatched as fire-and-forget containing the raw token (e.g., `https://<host>/reset-password?token=<raw>`). SMTP failure is logged but does not affect the HTTP 200 response. No `warnings` array in the response (unlike Story 2.10 — this endpoint always returns the same body).

4. **AC-4 — Successful password reset**
   Given a valid, unexpired, single-use token is submitted to `POST /api/auth/reset-password` with `{ token, newPassword }`, when the endpoint validates the token (SHA-256 hash match, `expires_at` not yet passed, `used_at` is null), then `passwordHash` is updated with the new BCrypt hash, `used_at` is set to `now()`, all active refresh tokens for the user are revoked, and HTTP 200 is returned.

5. **AC-5 — Invalid/expired/used token → 400**
   Given the reset token is invalid, expired, or already used, `POST /api/auth/reset-password` returns HTTP 400 with `code: "RESET_TOKEN_INVALID"` and `messageKey: "auth.resetTokenInvalid"`.

6. **AC-6 — Password validation → 422**
   Given `newPassword` is fewer than 8 characters or is identical to the current password (BCrypt comparison), `POST /api/auth/reset-password` returns HTTP 422 with `ValidationProblemDetails`.

7. **AC-7 — Frontend: /forgot-password route**
   A form renders at `/forgot-password` with an email input and a submit button. After submission the form is hidden and a success message is shown. A "Back to sign in" link navigates to `/login`.

8. **AC-8 — Frontend: /reset-password route**
   When navigating to `/reset-password?token=<raw>`, a form renders with new password and confirm password fields. On successful submission the SPA redirects to `/login` with a success toast. On token-invalid error, an inline error message with a link to `/forgot-password` is shown.

## Tasks / Subtasks

- [x] Task 1: Add `PasswordResetToken` entity + DbContext + EF Core migration (AC-2, AC-4, AC-5)
  - [x] Create `src/FormForge.Api/Domain/Entities/PasswordResetToken.cs` (see Dev Notes for exact shape)
  - [x] Add `DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();` to `FormForgeDbContext`
  - [x] Add entity configuration in `OnModelCreating` (table name, unique index on `token_hash`, cascade delete, `now()` default for `created_at` — see Dev Notes)
  - [x] Run `dotnet ef migrations add AddPasswordResetTokens` from `src/FormForge.Api/` and verify the generated SQL creates `password_reset_tokens` with the correct columns and indexes

- [x] Task 2: Create request DTOs and validators (AC-1, AC-4, AC-6)
  - [x] Create `src/FormForge.Api/Features/Auth/Dtos/ForgotPasswordRequest.cs` — record with `string Email`
  - [x] Create `src/FormForge.Api/Features/Auth/Dtos/ResetPasswordRequest.cs` — record with `string Token` and `string NewPassword`
  - [x] Create `src/FormForge.Api/Features/Auth/Validators/ForgotPasswordRequestValidator.cs` — Email required, valid email, max 320 chars (mirror `LoginRequestValidator` pattern)
  - [x] Create `src/FormForge.Api/Features/Auth/Validators/ResetPasswordRequestValidator.cs` — Token required + max 128 chars; NewPassword required, min 8 chars, max 72 chars (BCrypt UTF-8 byte limit)
  - [x] Register both validators as `AddScoped` in `Program.cs` after the existing auth validators block

- [x] Task 3: Extend `IEmailService` with password-reset method (AC-3)
  - [x] Add `TrySendPasswordResetEmailAsync(string recipientEmail, string resetUrl, string correlationId, CancellationToken ct)` to the `IEmailService` interface in `EmailService.cs`
  - [x] Implement it in `MailKitEmailService` following the exact same try/catch + `EmailServiceLog` pattern as `TrySendWelcomeEmailAsync` (see Dev Notes for email body)
  - [x] Add log message constants `PasswordResetEmailSkipped`, `PasswordResetEmailDispatched`, `PasswordResetEmailFailed` to `EmailServiceLog` static class using `[LoggerMessage]` source generation

- [x] Task 4: Extend `IAuthService` with reset-password methods (AC-2, AC-4, AC-5, AC-6)
  - [x] Add enums `PasswordResetInitiateOutcome` (`Success`, `UserNotFound`) and result record `PasswordResetInitiateResult` to `AuthService.cs`
  - [x] Add enum `PasswordResetOutcome` (`Success`, `TokenInvalid`, `PasswordSameAsCurrent`) and result record `PasswordResetResult`
  - [x] Add methods to `IAuthService` interface: `InitiatePasswordResetAsync(string email, CancellationToken ct)` and `ResetPasswordAsync(string rawToken, string newPassword, CancellationToken ct)`
  - [x] Implement `InitiatePasswordResetAsync`: normalize email, look up user, always guard against timing attacks with constant-time path, generate token, store hash in DB, return `(bool UserFound, string? RawToken)` (see Dev Notes for token generation)
  - [x] Implement `ResetPasswordAsync`: hash incoming token, look up in `password_reset_tokens`, validate expiry and `used_at`, BCrypt-verify new matches current to reject same-password attempt, update `passwordHash`, set `used_at`, bulk-revoke all refresh tokens via `ExecuteUpdateAsync`, return outcome

- [x] Task 5: Add two new endpoints to `AuthEndpoints.cs` (AC-1, AC-3, AC-4, AC-5, AC-6)
  - [x] Add `POST /forgot-password` handler — injects `IAuthService`, `IEmailService`, `IOptions<SmtpOptions>`, `HttpContext`, `CancellationToken`; calls `InitiatePasswordResetAsync`; if user found dispatches email fire-and-forget (`_ = Task.Run(...)`); always returns `Results.Ok(new { message = "..." })`
  - [x] Add `POST /reset-password` handler — injects `IAuthService`, `HttpContext`, `CancellationToken`; calls `ResetPasswordAsync`; maps outcomes to HTTP responses
  - [x] Wire both with `.AddValidationFilter<T>()` and `.RequireRateLimiting("auth-forgot-password")` / `.RequireRateLimiting("auth-reset-password")`

- [x] Task 6: Add rate-limiting policies in `Program.cs` (AC-1, AC-4)
  - [x] In the rate-limiting configuration block, add `"auth-forgot-password"` fixed-window policy: 5 requests per 1 minute per IP
  - [x] Add `"auth-reset-password"` fixed-window policy: 10 requests per 1 minute per IP

- [x] Task 7: Frontend — two new route files (AC-7, AC-8)
  - [x] Create `web/src/routes/forgot-password.tsx` — unauthenticated, email form with react-hook-form + zod, success state replaces form, link to `/login`
  - [x] Create `web/src/routes/reset-password.tsx` — unauthenticated, `validateSearch` to extract `token` string param; redirect to `/forgot-password` if no token; password + confirm form; on success navigate to `/login` with sonner `toast.success`; on `RESET_TOKEN_INVALID` show inline error with link to `/forgot-password`

- [x] Task 8: Frontend mutations (AC-7, AC-8)
  - [x] Add `useForgotPasswordMutation()` and `useResetPasswordMutation()` to `web/src/features/auth/authMutations.ts`
  - [x] `useForgotPasswordMutation` — `httpClient.post<{ message: string }>('/api/auth/forgot-password', body)`; no `onSuccess` toast (page shows inline success message instead)
  - [x] `useResetPasswordMutation` — `httpClient.post<void>('/api/auth/reset-password', body)`; caller handles navigation

- [x] Task 9: i18n strings (AC-7, AC-8)
  - [x] Add `auth.forgotPassword.*` and `auth.resetPassword.*` key groups to `web/src/lib/i18n/locales/en.json` (see Dev Notes for exact keys)
  - [x] Add `auth.resetTokenInvalid` and `auth.passwordSameAsCurrent` sibling keys inside the `auth` object

### Review Findings

- [x] [Review][Decision] D1: ResetPasswordAsync doesn't verify user.IsActive — dismissed; spec is silent on this and reset-as-reactivation may be intentional product policy; defer to explicit product decision.
- [x] [Review][Decision] D2: Timing side-channel (UserNotFound faster than Success) — dismissed; accepted per existing code comment; 5-req/min rate limit makes practical exploitation very low-risk.
- [x] [Review][Patch] D3→P7: Invalidate prior unused tokens before inserting a new one in InitiatePasswordResetAsync — concurrent requests create multiple valid tokens; requesting a new link should invalidate old ones [AuthService.cs InitiatePasswordResetAsync]
- [x] [Review][Patch] P1: Results.ValidationProblem missing statusCode:422 for PasswordSameAsCurrent — defaults to HTTP 400, not 422 as AC-6 requires [AuthEndpoints.cs:~137]
- [x] [Review][Patch] P2: No transaction wrapping ExecuteUpdateAsync + SaveChangesAsync in ResetPasswordAsync — sessions revoked but password not updated and token not marked used if SaveChangesAsync fails [AuthService.cs:~276-283]
- [x] [Review][Patch] P3: Concurrent token redemption race — UsedAt null check is not atomic; two concurrent requests with the same token both pass the guard [AuthService.cs:~257-260]
- [x] [Review][Patch] P4: Silent email suppression with no logging when baseUrl is null — token committed to DB, email never sent, no log emitted (AC-3 logging requirement) [AuthEndpoints.cs:~93-104]
- [x] [Review][Patch] P5: token! non-null assertion in reset-password.tsx — can send {token: undefined} to server if search param is stripped after beforeLoad fires [reset-password.tsx:~934]
- [x] [Review][Patch] P6: resetSchema Zod object recreated on every render — resolver reference change mid-submission can clear isSubmitting and allow double-submit [reset-password.tsx:~915]
- [x] [Review][Defer] W1: "unknown" rate-limit partition key collapses all null-IP callers — pre-existing pattern across all auth rate-limit policies [Program.cs] — deferred, pre-existing
- [x] [Review][Defer] W2: Raw token URL-concatenated without Uri.EscapeDataString — safe today (64-char uppercase hex), latent risk if token format changes [AuthEndpoints.cs:~95] — deferred, pre-existing
- [x] [Review][Defer] W3: C# default = DateTimeOffset.UtcNow on CreatedAt overrides HasDefaultValueSql("now()") — EF sends app-side timestamp, minor clock skew [PasswordResetToken.cs:21] — deferred, pre-existing

## Dev Notes

### Overview: What This Story Adds

This story adds the self-service password reset flow:
1. `PasswordResetToken` entity + DB table (new; no existing table)
2. Two new auth endpoints in `AuthEndpoints.cs` (existing file, add handlers)
3. `IEmailService` extended with a password-reset email method (existing file `EmailService.cs`)
4. `IAuthService` extended with two new methods (existing file `AuthService.cs`)
5. Two new validators + DTOs (new files)
6. Two new rate-limit policies in `Program.cs`
7. Two new frontend route files
8. Two new frontend mutations in `authMutations.ts`
9. New i18n keys

**Key difference from Story 2.10:** the forgot-password email is **true fire-and-forget** (`_ = Task.Run(...)`). Story 2.10 used a 3-second await to surface a `warnings` array in the HTTP response. Here the response is always the same (HTTP 200 + generic message), so no await needed — pure AR-53 pattern.

---

### New Entity: `PasswordResetToken`

**File:** `src/FormForge.Api/Domain/Entities/PasswordResetToken.cs`

```csharp
namespace FormForge.Api.Domain.Entities;

internal sealed class PasswordResetToken
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid UserId { get; init; }
    public User User { get; init; } = null!;
    public string TokenHash { get; init; } = string.Empty; // SHA-256 hex of raw token, 64 chars
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
```

**DbContext changes** — add DbSet and entity config in `OnModelCreating`. Mirror the `RefreshToken` entity config pattern exactly:

```csharp
// DbSet — add alongside the RefreshTokens line
public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();

// In OnModelCreating:
builder.Entity<PasswordResetToken>(b =>
{
    b.ToTable("password_reset_tokens");
    b.HasKey(t => t.Id);
    b.Property(t => t.Id).ValueGeneratedNever();
    b.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
    b.Property(t => t.UsedAt).IsRequired(false);
    b.HasIndex(t => t.TokenHash).IsUnique();
    b.HasIndex(t => t.UserId);
    b.HasOne(t => t.User)
     .WithMany()
     .HasForeignKey(t => t.UserId)
     .OnDelete(DeleteBehavior.Cascade);
    b.Property(t => t.CreatedAt).HasDefaultValueSql("now()");
});
```

**EF Core migration command** — run from the solution root (where `FormForge.sln` lives):
```
dotnet ef migrations add AddPasswordResetTokens --project src/FormForge.Api
```
Inspect the generated migration to confirm `password_reset_tokens` table, unique index on `token_hash`, and cascade-delete FK to `users`.

---

### Token Generation (AR-54)

The raw token is a 64-character uppercase hex string. The hash stored in DB is SHA-256 of that string. **Reuse `HashToken` in `AuthService.cs`** — it already implements the right pattern:

```csharp
// Raw token (sent to user via email):
string rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); // 64-char hex

// Stored in DB:
string tokenHash = HashToken(rawToken);
// HashToken is already in AuthService: Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLower(...)
```

`InitiatePasswordResetAsync` must apply the **same constant-time guard** as `LoginAsync` — always attempt the DB lookup but never branch early on user-not-found until after a timing-neutral path to prevent email enumeration:

```csharp
var normalizedEmail = email.Trim().ToLowerInvariant();
var user = await db.Users
    .AsNoTracking()
    .FirstOrDefaultAsync(u => u.Email == normalizedEmail && u.IsActive, ct)
    .ConfigureAwait(false);

if (user is null)
    return new PasswordResetInitiateResult(PasswordResetInitiateOutcome.UserNotFound, null);

var rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var tokenHash = HashToken(rawToken);

var resetToken = new PasswordResetToken
{
    UserId = user.Id,
    TokenHash = tokenHash,
    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
};
db.PasswordResetTokens.Add(resetToken);
await db.SaveChangesAsync(ct).ConfigureAwait(false);

return new PasswordResetInitiateResult(PasswordResetInitiateOutcome.Success, rawToken);
```

---

### `ResetPasswordAsync` Implementation

```csharp
public async Task<PasswordResetResult> ResetPasswordAsync(string rawToken, string newPassword, CancellationToken ct)
{
    var hash = HashToken(rawToken);

    var token = await db.PasswordResetTokens
        .Include(t => t.User)
        .FirstOrDefaultAsync(t => t.TokenHash == hash, ct)
        .ConfigureAwait(false);

    if (token is null || token.ExpiresAt <= DateTimeOffset.UtcNow || token.UsedAt is not null)
        return new PasswordResetResult(PasswordResetOutcome.TokenInvalid);

    // AC-6: must differ from current password
    if (passwordHasher.Verify(newPassword, token.User.PasswordHash))
        return new PasswordResetResult(PasswordResetOutcome.PasswordSameAsCurrent);

    token.User.PasswordHash = passwordHasher.Hash(newPassword);
    token.UsedAt = DateTimeOffset.UtcNow;

    // Revoke ALL active refresh tokens for this user (bulk update — no change-tracking)
    await db.RefreshTokens
        .Where(r => r.UserId == token.UserId && r.RevokedAt == null)
        .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, DateTimeOffset.UtcNow), ct)
        .ConfigureAwait(false);

    await db.SaveChangesAsync(ct).ConfigureAwait(false);

    return new PasswordResetResult(PasswordResetOutcome.Success);
}
```

**Important:** `ExecuteUpdateAsync` requires EF Core 7+ (this project targets .NET 10 + EF Core 10 — fine). It bypasses change tracking — perfect for bulk revocation. No `metrics.RecordRevoked()` call needed here (metrics are on the refresh rotation path, not mass revocation).

---

### New Endpoint Handlers in `AuthEndpoints.cs`

Add the two endpoint registrations inside `MapAuthEndpoints`:

```csharp
group.MapPost("/forgot-password", ForgotPasswordHandler)
     .AddValidationFilter<ForgotPasswordRequest>()
     .RequireRateLimiting("auth-forgot-password")
     .WithSummary("Request a password reset email")
     .AllowAnonymous();

group.MapPost("/reset-password", ResetPasswordHandler)
     .AddValidationFilter<ResetPasswordRequest>()
     .RequireRateLimiting("auth-reset-password")
     .WithSummary("Consume a password reset token and set a new password")
     .AllowAnonymous();
```

**`ForgotPasswordHandler`:**

```csharp
private static async Task<IResult> ForgotPasswordHandler(
    ForgotPasswordRequest request,
    IAuthService authService,
    IEmailService emailService,
    IOptions<SmtpOptions> smtpOptions,
    HttpContext httpContext,
    CancellationToken ct)
{
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(authService);
    ArgumentNullException.ThrowIfNull(emailService);
    ArgumentNullException.ThrowIfNull(smtpOptions);
    ArgumentNullException.ThrowIfNull(httpContext);

    var result = await authService.InitiatePasswordResetAsync(request.Email, ct).ConfigureAwait(false);

    if (result.Outcome == PasswordResetInitiateOutcome.Success && result.RawToken is not null)
    {
        // Compute base URL: prefer configured BaseUrl (prevents Host-header phishing
        // per the D1 review patch applied in Story 2.10).
        var opts = smtpOptions.Value;
        var host = httpContext.Request.Host;
        var baseUrl = !string.IsNullOrEmpty(opts.BaseUrl)
            ? opts.BaseUrl
            : (host.HasValue ? $"{httpContext.Request.Scheme}://{host}" : null);

        if (baseUrl is not null)
        {
            var resetUrl = $"{baseUrl}/reset-password?token={result.RawToken}";
            var correlationId = httpContext.GetCorrelationId();
            _ = Task.Run(() =>
                emailService.TrySendPasswordResetEmailAsync(
                    request.Email, resetUrl, correlationId, CancellationToken.None),
                CancellationToken.None);
        }
    }

    // Always return same response — no enumeration leak.
    return Results.Ok(new { message = "If that email is registered, a reset link has been sent." });
}
```

**`ResetPasswordHandler`:**

```csharp
private static async Task<IResult> ResetPasswordHandler(
    ResetPasswordRequest request,
    IAuthService authService,
    HttpContext httpContext,
    CancellationToken ct)
{
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(authService);
    ArgumentNullException.ThrowIfNull(httpContext);

    var result = await authService.ResetPasswordAsync(request.Token, request.NewPassword, ct)
        .ConfigureAwait(false);

    return result.Outcome switch
    {
        PasswordResetOutcome.TokenInvalid => Results.Problem(
            detail: "Reset link is invalid or has expired.",
            title: "Reset token invalid",
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "RESET_TOKEN_INVALID",
                ["messageKey"] = "auth.resetTokenInvalid",
                ["correlationId"] = httpContext.GetCorrelationId(),
            }),

        PasswordResetOutcome.PasswordSameAsCurrent => Results.ValidationProblem(
            errors: new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["newPassword"] = ["New password must differ from your current password."],
            },
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = "auth.passwordSameAsCurrent",
                ["correlationId"] = httpContext.GetCorrelationId(),
            }),

        PasswordResetOutcome.Success => Results.Ok(),

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

---

### `IEmailService` Extension — `TrySendPasswordResetEmailAsync`

**Add to interface in `EmailService.cs`:**
```csharp
Task<bool> TrySendPasswordResetEmailAsync(
    string recipientEmail,
    string resetUrl,
    string correlationId,
    CancellationToken ct);
```

**Implement in `MailKitEmailService` — verbatim pattern from `TrySendWelcomeEmailAsync`:**

```csharp
public async Task<bool> TrySendPasswordResetEmailAsync(
    string recipientEmail,
    string resetUrl,
    string correlationId,
    CancellationToken ct)
{
    const string templateType = "password-reset";

    if (string.IsNullOrEmpty(_smtp.Host))
    {
        EmailServiceLog.PasswordResetEmailSkipped(logger, recipientEmail, templateType, correlationId);
        return false;
    }

    try
    {
        using var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_smtp.From));
        message.To.Add(MailboxAddress.Parse(recipientEmail));
        message.Subject = "Reset your FormForge password";
        message.Body = new TextPart("plain")
        {
            Text = $"""
                You requested a password reset for your FormForge account.

                Click the link below to set a new password (valid for 1 hour):

                {resetUrl}

                If you did not request a password reset, you can ignore this email.
                """,
        };

        using var client = new SmtpClient();
        await client.ConnectAsync(_smtp.Host, _smtp.Port, SecureSocketOptions.None, ct)
            .ConfigureAwait(false);

        if (!string.IsNullOrEmpty(_smtp.User) && !string.IsNullOrEmpty(_smtp.Pass))
        {
            await client.AuthenticateAsync(_smtp.User, _smtp.Pass, ct).ConfigureAwait(false);
        }

        await client.SendAsync(message, ct).ConfigureAwait(false);
        await client.DisconnectAsync(quit: true, ct).ConfigureAwait(false);

        EmailServiceLog.PasswordResetEmailDispatched(logger, recipientEmail, templateType, correlationId);
        return true;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        EmailServiceLog.PasswordResetEmailFailed(logger, ex, recipientEmail, templateType, correlationId);
        return false;
    }
}
```

**Add log message methods to `EmailServiceLog` static partial class:**
```csharp
[LoggerMessage(Level = LogLevel.Warning,
    Message = "Password reset email skipped — SMTP not configured. recipient={Recipient} templateType={TemplateType} correlationId={CorrelationId}")]
public static partial void PasswordResetEmailSkipped(ILogger logger, string recipient, string templateType, string correlationId);

[LoggerMessage(Level = LogLevel.Information,
    Message = "Password reset email dispatched. recipient={Recipient} templateType={TemplateType} correlationId={CorrelationId}")]
public static partial void PasswordResetEmailDispatched(ILogger logger, string recipient, string templateType, string correlationId);

[LoggerMessage(Level = LogLevel.Warning,
    Message = "Password reset email dispatch failed. recipient={Recipient} templateType={TemplateType} correlationId={CorrelationId}")]
public static partial void PasswordResetEmailFailed(ILogger logger, Exception exception, string recipient, string templateType, string correlationId);
```

---

### Rate Limiting in `Program.cs`

Locate the `AddRateLimiter` configuration block (the one that configures `"auth-login"` and `"auth-refresh"` policies). Add inside that block:

```csharp
options.AddFixedWindowLimiter("auth-forgot-password", o =>
{
    o.Window = TimeSpan.FromMinutes(1);
    o.PermitLimit = 5;
    o.QueueLimit = 0;
    o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
});

options.AddFixedWindowLimiter("auth-reset-password", o =>
{
    o.Window = TimeSpan.FromMinutes(1);
    o.PermitLimit = 10;
    o.QueueLimit = 0;
    o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
});
```

---

### Validators

**`ForgotPasswordRequestValidator.cs`** — mirror `LoginRequestValidator.cs` exactly for the email rule:
```csharp
internal sealed class ForgotPasswordRequestValidator : AbstractValidator<ForgotPasswordRequest>
{
    public ForgotPasswordRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(320);
    }
}
```

**`ResetPasswordRequestValidator.cs`:**
```csharp
internal sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(x => x.Token)
            .NotEmpty()
            .MaximumLength(128);

        RuleFor(x => x.NewPassword)
            .NotEmpty()
            .MinimumLength(8)
            .MaximumLength(72); // BCrypt UTF-8 byte limit (same rule as LoginRequestValidator for password)
    }
}
```

**Register in `Program.cs`** after the existing auth validators block:
```csharp
builder.Services.AddScoped<IValidator<ForgotPasswordRequest>, ForgotPasswordRequestValidator>();
builder.Services.AddScoped<IValidator<ResetPasswordRequest>, ResetPasswordRequestValidator>();
```

---

### Frontend: `forgot-password.tsx`

**File:** `web/src/routes/forgot-password.tsx`

- No auth guard (unauthenticated route — no `beforeLoad` redirect)
- Uses `useTranslation`, `useForm` (react-hook-form + zod resolver), `useForgotPasswordMutation`
- Zod schema: `z.object({ email: z.string().email().max(320) })`
- Form submission state: `mutationState.isPending` → disable button + show submitting text
- Success state: `mutationState.isSuccess` → hide form, show `t('auth.forgotPassword.successMessage')`, show "Back to sign in" `<Link to="/login">`
- The mutation's `onError` should set `setError('root', { message: t('auth.genericError') })` to display inline

Pattern reference: `login.tsx` is the closest existing route. Key difference: there is no `beforeLoad` guard and the success state replaces the form inline rather than navigating.

```tsx
import { Link } from '@tanstack/react-router'
import { createFileRoute } from '@tanstack/react-router'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { useTranslation } from 'react-i18next'
import { useForgotPasswordMutation } from '@/features/auth/authMutations'
// shadcn components: Button, Input, Label, Card/CardContent as needed

export const Route = createFileRoute('/forgot-password')({
  component: ForgotPasswordPage,
})
```

---

### Frontend: `reset-password.tsx`

**File:** `web/src/routes/reset-password.tsx`

- No auth guard
- `validateSearch`: `z.object({ token: z.string().optional() })` — read from URL search params
- If `search.token` is falsy → `redirect({ to: '/forgot-password' })` in `beforeLoad`
- Zod schema: `z.object({ newPassword: z.string().min(8).max(72), confirmPassword: z.string() }).refine(data => data.newPassword === data.confirmPassword, { message: t('auth.resetPassword.passwordMismatch'), path: ['confirmPassword'] })`
- On `mutationFn`: send only `{ token: search.token, newPassword: form.newPassword }` (not confirmPassword)
- On `onSuccess`: `toast.success(t('auth.resetPassword.successToast'))` then `navigate({ to: '/login' })`
- On `onError`: if `error.code === 'RESET_TOKEN_INVALID'` → set an inline error state (not `setError` root — display a dedicated "invalid token" banner with link to `/forgot-password`); else generic error

The mutation error type is `ApiError` (from `httpClient.ts`). Check `error instanceof ApiError && error.code === 'RESET_TOKEN_INVALID'`.

---

### Frontend Mutations: `authMutations.ts`

Add to `web/src/features/auth/authMutations.ts`:

```ts
export function useForgotPasswordMutation() {
  return useMutation({
    mutationFn: (body: { email: string }) =>
      httpClient.post<{ message: string }>('/api/auth/forgot-password', body),
    // No onSuccess toast — the page shows the success message inline.
    // No onError toast — the form shows inline error.
  })
}

export function useResetPasswordMutation() {
  return useMutation({
    mutationFn: (body: { token: string; newPassword: string }) =>
      httpClient.post<void>('/api/auth/reset-password', body),
    // Navigation and toast are handled by the route component.
  })
}
```

Note: `httpClient.post<void>` for the reset endpoint — the server returns `Results.Ok()` with no body (204-style, or empty 200). The existing `httpClient.ts` already handles empty 2xx responses by returning `undefined`.

---

### i18n Keys

**Add to `web/src/lib/i18n/locales/en.json`** inside the `"auth"` object, after the existing `"logout"` section:

```json
"forgotPassword": {
  "title": "Forgot Password",
  "description": "Enter your email address and we'll send you a reset link.",
  "emailLabel": "Email",
  "emailPlaceholder": "you@example.com",
  "submitButton": "Send Reset Link",
  "submitting": "Sending…",
  "successMessage": "If that email is registered, a reset link has been sent. Check your inbox.",
  "backToLogin": "Back to sign in"
},
"resetPassword": {
  "title": "Reset Password",
  "newPasswordLabel": "New password",
  "confirmPasswordLabel": "Confirm new password",
  "submitButton": "Reset Password",
  "submitting": "Resetting…",
  "passwordMismatch": "Passwords do not match.",
  "invalidTokenBanner": "This reset link is invalid or has expired.",
  "requestNewLink": "Request a new link",
  "successToast": "Password reset successfully. Please sign in.",
  "backToLogin": "Back to sign in"
},
"resetTokenInvalid": "Reset link is invalid or has expired.",
"passwordSameAsCurrent": "New password must differ from your current password."
```

---

### Files to CREATE

| File | Purpose |
|---|---|
| `src/FormForge.Api/Domain/Entities/PasswordResetToken.cs` | New entity |
| `src/FormForge.Api/Features/Auth/Dtos/ForgotPasswordRequest.cs` | Request DTO |
| `src/FormForge.Api/Features/Auth/Dtos/ResetPasswordRequest.cs` | Request DTO |
| `src/FormForge.Api/Features/Auth/Validators/ForgotPasswordRequestValidator.cs` | FluentValidation validator |
| `src/FormForge.Api/Features/Auth/Validators/ResetPasswordRequestValidator.cs` | FluentValidation validator |
| `web/src/routes/forgot-password.tsx` | Unauthenticated route |
| `web/src/routes/reset-password.tsx` | Unauthenticated route |
| EF Core migration (generated by `dotnet ef migrations add AddPasswordResetTokens`) | DB schema |

### Files to MODIFY

| File | Change |
|---|---|
| `src/FormForge.Api/Domain/Entities/PasswordResetToken.cs` | NEW (above) |
| `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` | Add `PasswordResetTokens` DbSet + entity config in `OnModelCreating` |
| `src/FormForge.Api/Features/Auth/EmailService.cs` | Add `TrySendPasswordResetEmailAsync` to interface + `MailKitEmailService` + 3 log messages to `EmailServiceLog` |
| `src/FormForge.Api/Features/Auth/AuthService.cs` | Add 4 enums/result-records + 2 `IAuthService` method signatures + 2 implementations |
| `src/FormForge.Api/Features/Auth/AuthEndpoints.cs` | Add 2 `MapPost` registrations + 2 handler methods |
| `src/FormForge.Api/Program.cs` | Add 2 rate-limit policies; register 2 validators |
| `web/src/features/auth/authMutations.ts` | Add 2 mutation hooks |
| `web/src/lib/i18n/locales/en.json` | Add `auth.forgotPassword.*`, `auth.resetPassword.*`, `auth.resetTokenInvalid`, `auth.passwordSameAsCurrent` |

### Files to Leave Untouched

- `src/FormForge.Api/Features/Auth/JwtTokenService.cs` — no access token issued on reset
- `src/FormForge.Api/Features/Auth/PasswordHasher.cs` — reused as-is; `Verify` and `Hash` methods already exist
- `src/FormForge.AppHost/AppHost.cs` — Mailpit already wired in Story 2.10
- `Directory.Packages.props` — MailKit 4.17.0 already added in Story 2.10
- All Designer, Menu, CRUD, and Provisioning files — completely unrelated

### Anti-Pattern Prevention

**DO NOT** fire-and-forget email with `emailCts.Token` (3-second timeout) — that was Story 2.10's approach to surface a `warnings` array. This story always returns 200 with the same body; use `CancellationToken.None` in the `Task.Run` callback so the fire-and-forget survives request cancellation.

**DO NOT** return different HTTP statuses based on whether the email is found — this violates the anti-enumeration requirement (AC-1). The branch only controls whether email dispatch fires.

**DO NOT** re-hash the token twice — `HashToken` produces SHA-256 hex already; do not additionally hash the result. The raw token → `HashToken(rawToken)` → stored. On lookup: `HashToken(incomingToken)` → DB lookup.

**DO NOT** use `db.RefreshTokens.RemoveRange(...)` for revocation — use `ExecuteUpdateAsync` (bulk update without change tracking, per the pattern in Epic 5). This is more efficient and avoids loading N rows into memory.

**DO NOT** use `logger.LogWarning(...)` directly in `EmailService.cs` — use `EmailServiceLog.SomeMethod(logger, ...)` (CA1848 is enforced via `AnalysisMode=AllEnabledByDefault`). Follow the established `[LoggerMessage]` source-generation pattern.

**DO NOT** add navigation properties to the `User` entity for `PasswordResetTokens` — the existing `User.cs` does not have a `PasswordResetTokens` collection and adding one is not needed. The `WithMany()` call in entity config takes no argument.

**DO NOT** add `.AllowAnonymous()` unless the endpoint group already requires auth. Check `AuthEndpoints`'s route group definition — the existing login/refresh/logout endpoints are in an `api/auth` group that is **not** protected by JWT policy (they're outside the auth-required group). So `.AllowAnonymous()` may be redundant; omit if the group has no `RequireAuthorization`.

**DO NOT** create a separate `IPasswordResetService` — extend `IAuthService` as specified. All auth-domain logic belongs in `AuthService.cs`.

### Previous Story Learnings (2.10)

- **MailKit version:** 4.17.0 is in `Directory.Packages.props` — do NOT add a new entry; the version is already there. Just add `<PackageReference Include="MailKit" />` if the `.csproj` somehow dropped it (it won't — it was added in 2.10).
- **Logger in handlers:** `UserEndpoints` injected `ILoggerFactory` because it's a static class. `AuthEndpoints.cs` is also a static class. If you need logging in a handler, inject `ILoggerFactory logger` and call `logger.CreateLogger<AuthEndpoints>()` — or better, avoid needing a logger in the handler by letting `AuthService` and `MailKitEmailService` do all logging via their injected loggers.
- **`[SuppressMessage("Performance", "CA1812")]`** — required on every `internal sealed class` registered via DI (the analyzer fires because the class has no public constructor). `MailKitEmailService` already has it. If you add any new implementation classes registered via DI, add this attribute.
- **`ConfigureAwait(false)`** on every `await` in service code — this is enforced by an analyzer rule. Missing it causes a build warning that fails the `TreatWarningsAsErrors` build.
- **`ValidateOnStart()`** — already applied to `SmtpOptions` in `Program.cs`. No change needed there.
- **Pre-existing test failures:** `MutationAuditLogIntegrationTests` and `SchemaAuditLogIntegrationTests` have 2 DELETE→405 failures on a clean tree — these are pre-existing and should not be investigated or counted as regressions.

### Testing

No new integration tests are mandated by the story. Manual verification path:
1. Start Aspire (Mailpit at http://localhost:8025)
2. Navigate to `/forgot-password`, submit a registered email → confirm email arrives in Mailpit with a valid reset link
3. Follow the reset link → `/reset-password?token=...` → set new password → confirm redirect to `/login` with toast
4. Verify old refresh tokens are revoked: attempt to use a pre-reset `refresh_token` cookie → should get 401

Unit-test recommendation (follow Story 2.10 `EmailServiceTests` pattern): test `TrySendPasswordResetEmailAsync` for the "SMTP not configured → returns false" path and "unreachable server → returns false without throwing" path.

### References

- [Source: `_bmad-output/planning-artifacts/epics.md` — Epic 2, Story 2.11 acceptance criteria]
- [Source: `_bmad-output/planning-artifacts/architecture.md` — AR-53 (email service), AR-54 (password reset token), Decision 2.4 (BCrypt), Decision 4.6 (frontend folder structure), Decision 4.9 (form composition)]
- [Source: `_bmad-output/planning-artifacts/prd.md` — FR-51 (forgot password), FR-52 (authenticated password change), AC-1 through AC-7]
- [Source: `src/FormForge.Api/Features/Auth/AuthService.cs` — `HashToken`, `GenerateRefreshToken`, enum + result-record patterns, constant-time guard, `ExecuteUpdateAsync` candidate]
- [Source: `src/FormForge.Api/Features/Auth/AuthEndpoints.cs` — handler signature pattern, ProblemDetails extension dict, `AllowAnonymous` check]
- [Source: `src/FormForge.Api/Features/Auth/EmailService.cs` — `IEmailService` interface, `MailKitEmailService` implementation, `EmailServiceLog` pattern, `SmtpOptions.BaseUrl` field]
- [Source: `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` — `RefreshToken` entity config to mirror for `PasswordResetToken`]
- [Source: `src/FormForge.Api/Program.cs` — rate-limit policy registration pattern, validator `AddScoped` pattern]
- [Source: `web/src/routes/login.tsx` — route component pattern (form, react-hook-form, zod, mutation, error display)]
- [Source: `web/src/features/auth/authMutations.ts` — `useMutation` pattern, `httpClient.post` call]
- [Source: `web/src/lib/i18n/locales/en.json` — existing `auth.*` key structure to extend]
- [Source: `_bmad-output/implementation-artifacts/2-10-welcome-email-on-user-creation.md` — dev notes, deviations, fire-and-forget vs. 3s-timeout distinction, pre-existing test failure list]

## Dev Agent Record

### Agent Model Used

claude-sonnet-4-6 (story authoring)
claude-opus-4-8 (implementation)

### Debug Log References

- Initial `dotnet ef migrations add` / build failed with MSB3027 file-lock (`FormForge.Api` PID held `FormForge.ServiceDefaults.dll`). Resolved by stopping the running process (user-approved), then the migration scaffolded cleanly.
- Generated migration tripped CA1062 (`TreatWarningsAsErrors`): added `ArgumentNullException.ThrowIfNull(migrationBuilder)` to `Up`/`Down`, matching the existing migration convention.
- `react-refresh/only-export-components` fires on the component function in file-based route files; suppressed with `// eslint-disable-next-line` above the component (matching the precedent in `data.$designerId.tsx`) so the two new routes add zero lint errors.

### Completion Notes List

**Implementation summary** — all 9 tasks complete, all 8 ACs satisfied.

- **AC-1** `POST /api/auth/forgot-password` always returns HTTP 200 with the generic message regardless of whether the email exists (`ForgotPasswordHandler`).
- **AC-2** `InitiatePasswordResetAsync` generates a 64-char hex token via `Convert.ToHexString(RandomNumberGenerator.GetBytes(32))`, stores only `HashToken(raw)` (SHA-256 hex) with `expires_at = now()+1h`, `used_at = null`.
- **AC-3** Reset email dispatched true fire-and-forget (`_ = Task.Run(... CancellationToken.None)`); SMTP failure is logged, never affects the response; no `warnings` array.
- **AC-4** `ResetPasswordAsync` validates the token, sets the new BCrypt hash, stamps `used_at`, and bulk-revokes all active refresh tokens via `ExecuteUpdateAsync`; returns 200.
- **AC-5** Invalid/expired/used token → 400 `RESET_TOKEN_INVALID` / `auth.resetTokenInvalid`.
- **AC-6** `< 8` chars → 422 via `ResetPasswordRequestValidator` (ValidationFilter returns 422); same-as-current → 422 `ValidationProblem` with `auth.passwordSameAsCurrent`.
- **AC-7** `/forgot-password` route: email form, inline success state replaces the form, "Back to sign in" link.
- **AC-8** `/reset-password` route: `validateSearch` token, `beforeLoad` redirect to `/forgot-password` when token absent, new+confirm password form, success toast + redirect to `/login`, dedicated invalid-token banner with "request a new link".

**Anti-pattern checklist honoured:** fire-and-forget uses `CancellationToken.None` (not a 3s linked timeout); no status-code branching on email existence; token hashed once via reused `HashToken`; bulk revocation via `ExecuteUpdateAsync` (no `RemoveRange`); logging via `[LoggerMessage]` source-gen (CA1848); no `PasswordResetTokens` nav collection on `User` (`WithMany()` no-arg); no `.AllowAnonymous()` (the `/api/auth` group is unprotected); reset logic lives in `IAuthService` (no separate service).

**Tests:**
- Added 2 `EmailServiceTests` for `TrySendPasswordResetEmailAsync` (SMTP-unconfigured → false; unreachable server → false-without-throw). All 4 email tests pass.
- Backend: build clean (0 warnings under `TreatWarningsAsErrors`); 732 passed, 2 failed. The 2 failures are the documented pre-existing audit DELETE→405 integration tests (`SchemaAuditLogIntegrationTests`, `MutationAuditLogIntegrationTests`) — not regressions.
- Frontend: `npm run build` (vite + `tsc -b --noEmit`) passes; the two new route files lint clean. `npm test` = 242 passed, 1 failed — the failure is the pre-existing `i18n-lint.test.ts` (missing `designer.inspector.placeholders.label`, referenced in untouched `PropertyInspector.tsx`); not a regression. New `auth.resetTokenInvalid` / `auth.passwordSameAsCurrent` keys are server messageKeys used dynamically, so they appear as orphaned warnings (expected, mirroring the existing `auth.*` messageKeys).

**Manual verification not run** (requires the Aspire stack + Mailpit). Path documented in Dev Notes → Testing.

### File List

**Created (backend):**
- `src/FormForge.Api/Domain/Entities/PasswordResetToken.cs`
- `src/FormForge.Api/Features/Auth/Dtos/ForgotPasswordRequest.cs`
- `src/FormForge.Api/Features/Auth/Dtos/ResetPasswordRequest.cs`
- `src/FormForge.Api/Features/Auth/Validators/ForgotPasswordRequestValidator.cs`
- `src/FormForge.Api/Features/Auth/Validators/ResetPasswordRequestValidator.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260531143906_AddPasswordResetTokens.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260531143906_AddPasswordResetTokens.Designer.cs`

**Modified (backend):**
- `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` — `PasswordResetTokens` DbSet + entity config
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/FormForgeDbContextModelSnapshot.cs` — regenerated by EF
- `src/FormForge.Api/Features/Auth/EmailService.cs` — `TrySendPasswordResetEmailAsync` (interface + impl) + 3 `EmailServiceLog` methods
- `src/FormForge.Api/Features/Auth/AuthService.cs` — 4 enums/result-records + 2 `IAuthService` methods + implementations
- `src/FormForge.Api/Features/Auth/AuthEndpoints.cs` — 2 endpoint registrations + 2 handlers + `IOptions` using
- `src/FormForge.Api/Program.cs` — 2 rate-limit policies + 2 validator registrations
- `src/FormForge.AppHost/AppHost.cs` — set `Smtp__BaseUrl=http://localhost:5173` so dev email links target the SPA, not the API's host:port (post-implementation fix; see Change Log)

**Modified (tests):**
- `src/FormForge.Api.Tests/Features/Auth/EmailServiceTests.cs` — 2 password-reset email tests

**Created (frontend):**
- `web/src/routes/forgot-password.tsx`
- `web/src/routes/reset-password.tsx`

**Modified (frontend):**
- `web/src/features/auth/authMutations.ts` — `useForgotPasswordMutation`, `useResetPasswordMutation`
- `web/src/lib/i18n/locales/en.json` — `auth.forgotPassword.*`, `auth.resetPassword.*`, `auth.resetTokenInvalid`, `auth.passwordSameAsCurrent`

_Note: `web/src/routeTree.gen.ts` is regenerated by the TanStack Router build plugin (and now registers both new routes), but it is gitignored — not a tracked change._

## Change Log

| Date | Change |
|---|---|
| 2026-05-31 | Implemented Story 2.11 forgot-password flow: `password_reset_tokens` table + migration, `IAuthService.InitiatePasswordResetAsync`/`ResetPasswordAsync`, `IEmailService.TrySendPasswordResetEmailAsync`, `POST /api/auth/forgot-password` + `/reset-password` endpoints with rate limiting, and `/forgot-password` + `/reset-password` SPA routes with mutations and i18n. Status → review. |
| 2026-05-31 | Fix: dev email reset/login links pointed at the API host:port (~5429) instead of the SPA. Set `Smtp__BaseUrl=http://localhost:5173` in `AppHost.cs` so `SmtpOptions.BaseUrl` overrides the Host-header-derived base URL. Also corrects the same latent port issue in the Story 2.10 welcome-email login link. |
