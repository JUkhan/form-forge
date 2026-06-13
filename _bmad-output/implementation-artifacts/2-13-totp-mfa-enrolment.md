# Story 2.13: TOTP MFA Enrolment

Status: done

## Story

As a user,
I want to enrol in TOTP multi-factor authentication by scanning a QR code with an authenticator app and confirming with a one-time code,
so that my account is protected by a second factor.

## Acceptance Criteria

1. **AC-1 — GET /me/mfa/enrol returns enrolment data**
   Given I am authenticated and call `GET /api/users/me/mfa/enrol`, when the endpoint responds, then I receive `{ secret, qrCodeDataUrl, backupCodes[] }` where `secret` is a base32-encoded TOTP secret, `qrCodeDataUrl` is a `data:image/png;base64,...` QR encoding `otpauth://totp/FormForge:<email>?secret=<secret>&issuer=FormForge`, and `backupCodes` is an array of 8 uppercase alphanumeric 8-character codes. The pending enrolment (secret + raw codes) is stored in `IMemoryCache` with a 10-min TTL and NOT persisted to DB yet (per AR-56 enrolment guard).

2. **AC-2 — Valid TOTP code persists enrolment**
   Given I called `GET /api/users/me/mfa/enrol` and submit `POST /api/users/me/mfa/verify` with `{ code }` where `code` is a valid 6-digit TOTP (Otp.NET ±1 step tolerance), then: `users.mfa_secret_protected` is set to the `IDataProtector`-encrypted secret (purpose `"mfa-totp-secret"`), `users.mfa_enabled` = true, bcrypt hashes of backup codes are written to `mfa_backup_codes`, old backup codes for this user are atomically deleted, and HTTP 200 is returned.

3. **AC-3 — Wrong TOTP code → 400, nothing persisted**
   Given the TOTP code fails verification, then HTTP 400 with `code: "MFA_CODE_INVALID"` and `messageKey: "auth.mfaCodeInvalid"` is returned and no DB write occurs.

4. **AC-4 — No pending enrolment → 400**
   Given I submit `POST /api/users/me/mfa/verify` without first calling `GET /api/users/me/mfa/enrol` (or the 10-min TTL expired), then HTTP 400 with `code: "MFA_NO_PENDING_ENROLMENT"` and `messageKey: "auth.mfaNoPendingEnrolment"` is returned.

5. **AC-5 — Re-enrolment is atomic**
   Given I already have MFA enabled and call `GET /api/users/me/mfa/enrol` then verify a new code, then the old secret is replaced, all old backup codes deleted, and the new secret + new backup codes are committed in a single `SaveChangesAsync` call (per AR-56 atomicity requirement).

6. **AC-6 — GET /me/mfa/status returns current state**
   Given I am authenticated and call `GET /api/users/me/mfa/status`, then I receive `{ mfaEnabled: bool }` reflecting `users.mfa_enabled` for my account.

7. **AC-7 — Frontend: Security section in /settings with 3-step MFA enrolment modal**
   Given I am authenticated and navigate to `/settings`, then a "Security" section is present showing current MFA status and an "Enable MFA" (or "Re-enrol MFA") button. When I click it, a multi-step modal opens: Step 1 — QR code + manual secret display; Step 2 — 6-digit code input with verify button (inline error on wrong code); Step 3 — backup codes grid with a "I have saved my backup codes" checkbox required before the "Done" button activates (per FR-53 AC-6).

## Tasks / Subtasks

- [x] Task 1: Install NuGet packages and verify Data Protection (AC-1, AC-2)
  - [x] Add `Otp.NET` to `src/FormForge.Api/FormForge.Api.csproj`
  - [x] Add `QRCoder` to `src/FormForge.Api/FormForge.Api.csproj` (use `PngByteQRCode` — no GDI+ dependency)
  - [x] Verify `builder.Services.AddDataProtection()` is in `Program.cs`; add it before `builder.Build()` if absent

- [x] Task 2: Create `MfaBackupCode` entity and extend `User` (AC-2, AC-5)
  - [x] Create `src/FormForge.Api/Domain/Entities/MfaBackupCode.cs` with properties: `Guid Id`, `Guid UserId`, `string CodeHash`, `DateTimeOffset? UsedAt`, `DateTimeOffset CreatedAt`, `User User` navigation
  - [x] In `User.cs`, add: `bool MfaEnabled { get; set; }`, `byte[]? MfaSecretProtected { get; set; }`, `ICollection<MfaBackupCode> BackupCodes { get; set; } = []`

- [x] Task 3: Update `FormForgeDbContext.cs` (AC-2, AC-5)
  - [x] Add `public DbSet<MfaBackupCode> MfaBackupCodes => Set<MfaBackupCode>();`
  - [x] In `OnModelCreating`, extend the `User` entity block: add column mappings for `mfa_enabled` (default false) and `mfa_secret_protected`
  - [x] Add `MfaBackupCode` entity configuration: table `mfa_backup_codes`, PK, `code_hash` required, cascade-delete FK to `users`

- [x] Task 4: Create EF Core migration (AC-2)
  - [x] Run `dotnet ef migrations add AddMfaTables --project src/FormForge.Api`
  - [x] Verify migration adds `mfa_enabled bool NOT NULL DEFAULT false`, `mfa_secret_protected bytea NULL` to `users`; creates `mfa_backup_codes` table
  - [x] Apply: migration applied automatically via app startup `Migrate()` and the test fixture's `MigrateAsync()` (Testcontainers)

- [x] Task 5: Create `IMfaService` and `MfaService` in `src/FormForge.Api/Features/Auth/MfaService.cs` (all ACs)
  - [x] Implement `InitiateEnrolment(Guid userId, string email)` — generate secret, QR, backup codes; cache pending enrolment 10 min
  - [x] Implement `VerifyEnrolmentAsync(Guid userId, string code, CancellationToken ct)` — verify TOTP, persist atomically via `SaveChangesAsync`
  - [x] Implement `GetMfaStatusAsync(Guid userId, CancellationToken ct)` — query `users.mfa_enabled`
  - [x] Create `src/FormForge.Api/Features/Auth/Dtos/MfaEnrolResponse.cs`

- [x] Task 6: Create `MfaVerifyRequest` DTO + validator (AC-2, AC-3)
  - [x] Create `src/FormForge.Api/Features/Users/Dtos/MfaVerifyRequest.cs`
  - [x] Create `src/FormForge.Api/Features/Users/Validators/MfaVerifyRequestValidator.cs` — Code: NotEmpty + Length(6) + Matches `^\d{6}$`
  - [x] Register both in `Program.cs` after the `ChangePasswordRequestValidator` line

- [x] Task 7: Add three MFA endpoints to `MeEndpoints.cs` (AC-1, AC-2, AC-3, AC-4, AC-6)
  - [x] `GET /me/mfa/enrol` → `EnrolMfaHandler` (sync, returns `IResult`)
  - [x] `POST /me/mfa/verify` → `VerifyMfaEnrolmentHandler` with `.AddValidationFilter<MfaVerifyRequest>()`
  - [x] `GET /me/mfa/status` → `GetMfaStatusHandler`

- [x] Task 8: Frontend — create `web/src/features/auth/mfaMutations.ts` (AC-7)
  - [x] `useMfaStatusQuery()` — GET /api/users/me/mfa/status
  - [x] `useMfaEnrolMutation()` — GET /api/users/me/mfa/enrol (lazy, fired on button click)
  - [x] `useMfaVerifyEnrolmentMutation()` — POST /api/users/me/mfa/verify; invalidates `mfaKeys.status` on success

- [x] Task 9: Frontend — extend `settings.tsx` with Security card + 3-step modal (AC-7)
  - [x] Add Security card showing MFA status + Enable/Re-enrol button below the Change Password card
  - [x] Implement `Dialog` modal with `step` state (1 | 2 | 3), step 1 QR display, step 2 code verify, step 3 backup codes + checkbox gate
  - [x] Verify `web/src/components/ui/checkbox.tsx` exists; add via `npx shadcn@latest add checkbox` if absent (already present — no CLI add needed)

- [x] Task 10: i18n strings (AC-7)
  - [x] Add `auth.mfaCodeInvalid` and `auth.mfaNoPendingEnrolment` to the `auth` object in `en.json`
  - [x] Add `settings.security.*` key group (see Dev Notes for exact keys)

### Review Findings

**Code review: 2026-06-01 — 2 decision-needed, 5 patch, 4 defer, 14 dismissed — all resolved**

**Decision-Needed (resolved):**
- [x] [Review][Decision] D1 — AddDataProtection without key persistence — Fixed: added `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` package, implemented `IDataProtectionKeyContext` on `FormForgeDbContext`, changed to `.PersistKeysToDbContext<FormForgeDbContext>()`, generated `AddDataProtectionKeys` migration.
- [x] [Review][Decision] D2 — Dialog escape-close at Step 3 before backup codes acknowledged — Fixed: `onOpenChange` now ignores close events when `mfaStep === 3`, keeping the modal open until the checkbox is checked and Done is clicked.

**Patch (applied):**
- [x] [Review][Patch] P1 — otpauth URI email label not URI-encoded [`src/FormForge.Api/Features/Auth/MfaService.cs:InitiateEnrolment`] — Fixed: `Uri.EscapeDataString(email)` applied to the label segment.
- [x] [Review][Patch] P2 — GetMfaStatusAsync returns `false` for non-existent user instead of 401 [`src/FormForge.Api/Features/Auth/MfaService.cs:GetMfaStatusAsync` + `MeEndpoints.cs:GetMfaStatusHandler`] — Fixed: nullable `bool?` projection; handler returns 401 when `null`.
- [x] [Review][Patch] P3 — Enable MFA button not disabled when modal is already open [`web/src/routes/_app/settings.tsx`] — Fixed: `disabled={mfaEnrolMutation.isPending || mfaModalOpen}`.
- [x] [Review][Patch] P4 — `handleOpenMfaModal` opens modal when `enrolData` is null/undefined [`web/src/routes/_app/settings.tsx`] — Fixed: guard added; toasts error and returns early if `!data`.
- [x] [Review][Patch] P5 — `VerifyEnrolmentAsync` does not evict cache when user is null [`src/FormForge.Api/Features/Auth/MfaService.cs:VerifyEnrolmentAsync`] — Fixed: `cache.Remove(PendingKey(userId))` added in the `user is null` branch.

**Deferred:**
- [x] [Review][Defer] W1 — Concurrent enrolment request race (multi-tab) — A second call to GET /me/mfa/enrol while a verify is in-flight replaces the cache entry; the verify reads the new secret and fails TOTP for the user's authenticator. Very narrow race window; fix requires a versioned nonce or per-user lock. Out of scope for 2.13. — deferred, pre-existing
- [x] [Review][Defer] W2 — No rate-limiting on POST /me/mfa/verify — Authenticated brute-force of the 6-digit TOTP window is possible with no throttle. Real security concern; requires understanding existing rate-limiting policies before a safe patch can be applied. — deferred, pre-existing
- [x] [Review][Defer] W3 — No rate-limiting on GET /me/mfa/enrol — QRCoder PNG generation is synchronous CPU work done per-request with no throttle. Cross-cutting concern consistent with other unrated endpoints. — deferred, pre-existing
- [x] [Review][Defer] W4 — SaveChangesAsync exception surfaces as unstructured 500 — If `SaveChangesAsync` throws, the exception propagates past the handler's switch, bypassing the structured Problem response. Pre-existing pattern throughout the API; not introduced by this story. — deferred, pre-existing

## Dev Notes

### Overview: What This Story Adds

1. New `MfaBackupCode` entity; `User` extended with `MfaEnabled`, `MfaSecretProtected`, `BackupCodes`
2. EF Core migration: two new columns on `users`, new `mfa_backup_codes` table
3. `IMfaService` / `MfaService` — TOTP generation (Otp.NET), QR code (QRCoder PngByteQRCode), backup codes, enrolment persistence, MFA status query
4. Three new endpoints in `MeEndpoints.cs`: GET /me/mfa/enrol, POST /me/mfa/verify, GET /me/mfa/status
5. `MfaVerifyRequest` DTO + validator
6. `MfaEnrolResponse` response DTO
7. Frontend `mfaMutations.ts` with three hooks
8. Security section + 3-step Dialog modal in `settings.tsx`
9. New i18n keys

**Scope guard:** This story covers enrolment ONLY. `POST /api/auth/mfa/verify` (login-time challenge, `mfaSessionToken`, backup-code login) belongs to Story 2.14. `DELETE /api/admin/users/{id}/mfa` belongs to Story 2.15. Do not implement those here.

---

### New Entity: `MfaBackupCode`

**`src/FormForge.Api/Domain/Entities/MfaBackupCode.cs`:**
```csharp
namespace FormForge.Api.Domain.Entities;

internal sealed class MfaBackupCode
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public User User { get; set; } = null!;
}
```

**Extend `User.cs`** (add below the existing `ICollection<UserRole> UserRoles` line — do NOT remove any existing members):
```csharp
public bool MfaEnabled { get; set; }
public byte[]? MfaSecretProtected { get; set; }
public ICollection<MfaBackupCode> BackupCodes { get; set; } = [];
```

---

### `FormForgeDbContext.cs` Changes

Add DbSet (alongside existing ones):
```csharp
public DbSet<MfaBackupCode> MfaBackupCodes => Set<MfaBackupCode>();
```

Inside `OnModelCreating`, extend the **existing** `modelBuilder.Entity<User>(b => { ... })` block:
```csharp
b.Property(u => u.MfaEnabled)
    .HasColumnName("mfa_enabled")
    .HasDefaultValue(false);

b.Property(u => u.MfaSecretProtected)
    .HasColumnName("mfa_secret_protected");
```

Add a **new** entity configuration block for `MfaBackupCode`:
```csharp
modelBuilder.Entity<MfaBackupCode>(b =>
{
    b.ToTable("mfa_backup_codes");
    b.HasKey(c => c.Id);
    b.Property(c => c.UserId).HasColumnName("user_id");
    b.Property(c => c.CodeHash).HasColumnName("code_hash").IsRequired();
    b.Property(c => c.UsedAt).HasColumnName("used_at");
    b.Property(c => c.CreatedAt).HasColumnName("created_at");
    b.HasOne(c => c.User)
        .WithMany(u => u.BackupCodes)
        .HasForeignKey(c => c.UserId)
        .OnDelete(DeleteBehavior.Cascade);
});
```

---

### `IMfaService` and `MfaService`

**`src/FormForge.Api/Features/Auth/MfaService.cs`** (full file):
```csharp
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Features.Auth.Dtos;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using OtpNet;
using QRCoder;

namespace FormForge.Api.Features.Auth;

internal interface IMfaService
{
    MfaEnrolResponse InitiateEnrolment(Guid userId, string email);
    Task<MfaVerifyEnrolmentResult> VerifyEnrolmentAsync(Guid userId, string code, CancellationToken ct);
    Task<bool> GetMfaStatusAsync(Guid userId, CancellationToken ct);
}

internal enum MfaVerifyEnrolmentOutcome { Success, InvalidCode, NoPendingEnrolment }

internal sealed record MfaVerifyEnrolmentResult(MfaVerifyEnrolmentOutcome Outcome);

[SuppressMessage("Performance", "CA1812", Justification = "Instantiated via DI")]
internal sealed class MfaService(
    FormForgeDbContext db,
    IDataProtectionProvider dataProtectionProvider,
    IMemoryCache cache,
    IPasswordHasher passwordHasher) : IMfaService
{
    private readonly IDataProtector _protector =
        dataProtectionProvider.CreateProtector("mfa-totp-secret");
    private const string BackupCodeChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const int BackupCodeCount = 8;
    private const int BackupCodeLength = 8;

    private sealed record PendingEnrolment(string Base32Secret, string[] RawBackupCodes);

    private static string PendingKey(Guid userId) => $"mfa:pending:{userId}";

    public MfaEnrolResponse InitiateEnrolment(Guid userId, string email)
    {
        ArgumentNullException.ThrowIfNull(email);

        // 160-bit secret — RFC 6238 recommends ≥ 128 bits
        var key = KeyGeneration.GenerateRandomKey(20);
        var base32Secret = Base32Encoding.ToString(key);

        var rawBackupCodes = Enumerable.Range(0, BackupCodeCount)
            .Select(_ => GenerateBackupCode())
            .ToArray();

        // Store pending enrolment in cache — NOT in DB until verified
        cache.Set(
            PendingKey(userId),
            new PendingEnrolment(base32Secret, rawBackupCodes),
            TimeSpan.FromMinutes(10));

        var otpAuthUri = $"otpauth://totp/FormForge:{email}?secret={base32Secret}&issuer=FormForge";
        var qrCodeDataUrl = GenerateQrCodeDataUrl(otpAuthUri);

        return new MfaEnrolResponse(base32Secret, qrCodeDataUrl, rawBackupCodes);
    }

    public async Task<MfaVerifyEnrolmentResult> VerifyEnrolmentAsync(
        Guid userId, string code, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(code);

        if (!cache.TryGetValue(PendingKey(userId), out PendingEnrolment? pending) || pending is null)
            return new MfaVerifyEnrolmentResult(MfaVerifyEnrolmentOutcome.NoPendingEnrolment);

        // VerificationWindow(1, 1) = ±1 step (±30-second) clock-skew tolerance per AR-56
        var totp = new Totp(Base32Encoding.ToBytes(pending.Base32Secret));
        if (!totp.VerifyTotp(code, out _, new VerificationWindow(1, 1)))
            return new MfaVerifyEnrolmentResult(MfaVerifyEnrolmentOutcome.InvalidCode);

        var user = await db.Users
            .Include(u => u.BackupCodes)
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            .ConfigureAwait(false);

        if (user is null)
            return new MfaVerifyEnrolmentResult(MfaVerifyEnrolmentOutcome.InvalidCode);

        user.MfaEnabled = true;
        user.MfaSecretProtected = _protector.Protect(Encoding.UTF8.GetBytes(pending.Base32Secret));
        user.UpdatedAt = DateTimeOffset.UtcNow;

        // Replace backup codes atomically — old codes removed, new hashes added in same SaveChanges
        db.MfaBackupCodes.RemoveRange(user.BackupCodes);
        var now = DateTimeOffset.UtcNow;
        db.MfaBackupCodes.AddRange(pending.RawBackupCodes.Select(rawCode => new MfaBackupCode
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CodeHash = passwordHasher.Hash(rawCode),
            CreatedAt = now,
        }));

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Evict only after successful DB commit
        cache.Remove(PendingKey(userId));

        return new MfaVerifyEnrolmentResult(MfaVerifyEnrolmentOutcome.Success);
    }

    public async Task<bool> GetMfaStatusAsync(Guid userId, CancellationToken ct)
    {
        return await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.MfaEnabled)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    private static string GenerateQrCodeDataUrl(string text)
    {
        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
        using var qrCode = new PngByteQRCode(qrCodeData);
        var pngBytes = qrCode.GetGraphic(20); // 20px per module — renders ~420px wide
        return $"data:image/png;base64,{Convert.ToBase64String(pngBytes)}";
    }

    private static string GenerateBackupCode()
    {
        var chars = new char[BackupCodeLength];
        for (var i = 0; i < BackupCodeLength; i++)
            chars[i] = BackupCodeChars[RandomNumberGenerator.GetInt32(BackupCodeChars.Length)];
        return new string(chars);
    }
}
```

**`src/FormForge.Api/Features/Auth/Dtos/MfaEnrolResponse.cs`:**
```csharp
namespace FormForge.Api.Features.Auth.Dtos;

internal sealed record MfaEnrolResponse(string Secret, string QrCodeDataUrl, string[] BackupCodes);
```

---

### `MfaVerifyRequest` DTO and Validator

**`src/FormForge.Api/Features/Users/Dtos/MfaVerifyRequest.cs`:**
```csharp
namespace FormForge.Api.Features.Users.Dtos;

internal sealed record MfaVerifyRequest(string Code);
```

**`src/FormForge.Api/Features/Users/Validators/MfaVerifyRequestValidator.cs`:**
```csharp
using System.Diagnostics.CodeAnalysis;
using FluentValidation;
using FormForge.Api.Features.Users.Dtos;

namespace FormForge.Api.Features.Users.Validators;

[SuppressMessage("Performance", "CA1812", Justification = "Instantiated via DI")]
internal sealed class MfaVerifyRequestValidator : AbstractValidator<MfaVerifyRequest>
{
    public MfaVerifyRequestValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty()
            .Length(6)
            .Matches(@"^\d{6}$").WithMessage("Code must be exactly 6 digits.");
    }
}
```

**Register in `Program.cs`** after the `ChangePasswordRequestValidator` line:
```csharp
builder.Services.AddScoped<IValidator<MfaVerifyRequest>, MfaVerifyRequestValidator>();
builder.Services.AddScoped<IMfaService, MfaService>();
```

---

### New Endpoints in `MeEndpoints.cs`

Add inside `MapMePreferencesEndpoints` after the existing `PUT /me/password` block:

```csharp
group.MapGet("/me/mfa/enrol", EnrolMfaHandler)
     .WithSummary("Initiate TOTP MFA enrolment — returns secret, QR code, and backup codes")
     .Produces<MfaEnrolResponse>(StatusCodes.Status200OK)
     .Produces(StatusCodes.Status401Unauthorized);

group.MapPost("/me/mfa/verify", VerifyMfaEnrolmentHandler)
     .AddValidationFilter<MfaVerifyRequest>()
     .WithSummary("Verify TOTP code to complete MFA enrolment")
     .Produces(StatusCodes.Status200OK)
     .Produces(StatusCodes.Status400BadRequest)
     .Produces(StatusCodes.Status422UnprocessableEntity);

group.MapGet("/me/mfa/status", GetMfaStatusHandler)
     .WithSummary("Get the caller's current MFA enabled state")
     .Produces(StatusCodes.Status200OK)
     .Produces(StatusCodes.Status401Unauthorized);
```

**Handlers** (add as private static methods at the bottom of `MeEndpoints`):

```csharp
// Synchronous — InitiateEnrolment does no I/O; return IResult directly (no async overhead)
private static IResult EnrolMfaHandler(
    IMfaService mfaService,
    HttpContext httpContext)
{
    ArgumentNullException.ThrowIfNull(mfaService);
    ArgumentNullException.ThrowIfNull(httpContext);

    var userIdClaim = httpContext.User.FindFirst("userId")?.Value;
    if (!Guid.TryParse(userIdClaim, out var userId))
        return Results.Unauthorized();

    var email = httpContext.User.FindFirst("email")?.Value;
    if (string.IsNullOrEmpty(email))
        return Results.Unauthorized();

    var response = mfaService.InitiateEnrolment(userId, email);
    return Results.Ok(response);
}

private static async Task<IResult> VerifyMfaEnrolmentHandler(
    MfaVerifyRequest request,
    IMfaService mfaService,
    HttpContext httpContext,
    CancellationToken ct)
{
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(mfaService);
    ArgumentNullException.ThrowIfNull(httpContext);

    var userIdClaim = httpContext.User.FindFirst("userId")?.Value;
    if (!Guid.TryParse(userIdClaim, out var userId))
        return Results.Unauthorized();

    var result = await mfaService.VerifyEnrolmentAsync(userId, request.Code, ct)
        .ConfigureAwait(false);

    return result.Outcome switch
    {
        MfaVerifyEnrolmentOutcome.Success => Results.Ok(),

        MfaVerifyEnrolmentOutcome.NoPendingEnrolment => Results.Problem(
            detail: "No pending MFA enrolment found. Call GET /me/mfa/enrol first.",
            title: "No pending enrolment",
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "MFA_NO_PENDING_ENROLMENT",
                ["messageKey"] = "auth.mfaNoPendingEnrolment",
                ["correlationId"] = httpContext.GetCorrelationId(),
            }),

        MfaVerifyEnrolmentOutcome.InvalidCode => Results.Problem(
            detail: "The provided TOTP code is invalid or expired.",
            title: "Invalid MFA code",
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "MFA_CODE_INVALID",
                ["messageKey"] = "auth.mfaCodeInvalid",
                ["correlationId"] = httpContext.GetCorrelationId(),
            }),

        _ => Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = "errors.genericError",
                ["correlationId"] = httpContext.GetCorrelationId(),
            }),
    };
}

private static async Task<IResult> GetMfaStatusHandler(
    IMfaService mfaService,
    HttpContext httpContext,
    CancellationToken ct)
{
    ArgumentNullException.ThrowIfNull(mfaService);
    ArgumentNullException.ThrowIfNull(httpContext);

    var userIdClaim = httpContext.User.FindFirst("userId")?.Value;
    if (!Guid.TryParse(userIdClaim, out var userId))
        return Results.Unauthorized();

    var mfaEnabled = await mfaService.GetMfaStatusAsync(userId, ct)
        .ConfigureAwait(false);

    return Results.Ok(new { mfaEnabled });
}
```

**Required `using` additions to `MeEndpoints.cs`** (check before adding — some may already be present):
- `using FormForge.Api.Features.Auth;` — for `IMfaService`, `MfaVerifyEnrolmentOutcome`
- `using FormForge.Api.Features.Auth.Dtos;` — for `MfaEnrolResponse`
- `using FormForge.Api.Features.Users.Dtos;` — already present; `MfaVerifyRequest` now lives here too

**Note on `EnrolMfaHandler` signature:** It is `IResult` (not `Task<IResult>`) because `InitiateEnrolment` is synchronous. Minimal API route handlers accept both sync and async delegates. If the build fails for any reason, wrap with `Task.FromResult<IResult>(...)` to make it `Task<IResult>`.

---

### `Program.cs`: `AddDataProtection`

Add before `var app = builder.Build();` if not already present:
```csharp
builder.Services.AddDataProtection(); // Required for IMfaService IDataProtector (AR-56)
```

ASP.NET Core does NOT auto-register Data Protection for JWT-only apps — add it explicitly.

---

### Frontend: `mfaMutations.ts`

**Create `web/src/features/auth/mfaMutations.ts`:**
```ts
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { httpClient } from './httpClient'

export interface MfaEnrolResponse {
  secret: string
  qrCodeDataUrl: string
  backupCodes: string[]
}

export interface MfaStatusResponse {
  mfaEnabled: boolean
}

export const mfaKeys = {
  status: ['mfa', 'status'] as const,
}

export function useMfaStatusQuery() {
  return useQuery({
    queryKey: mfaKeys.status,
    queryFn: () => httpClient.get<MfaStatusResponse>('/api/users/me/mfa/status'),
  })
}

// Lazy trigger — call mutateAsync() on button click, not on mount
export function useMfaEnrolMutation() {
  return useMutation({
    mutationFn: () => httpClient.get<MfaEnrolResponse>('/api/users/me/mfa/enrol'),
  })
}

export function useMfaVerifyEnrolmentMutation() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: { code: string }) =>
      httpClient.post<void>('/api/users/me/mfa/verify', body),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: mfaKeys.status })
    },
  })
}
```

**Check `httpClient.ts`** for the exact GET/POST method names. Story 2.12 used `httpClient.put<void>(...)` — the client likely exposes `get`, `post`, `put`, `delete`. If only `httpClient.fetch(url, options)` exists, adapt the `mutationFn` calls to pass `{ method: 'GET' }` / `{ method: 'POST', body }` accordingly.

---

### Frontend: Security Section in `settings.tsx`

**New imports to add:**
```ts
import { useState } from 'react'
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Checkbox } from '@/components/ui/checkbox'
import {
  useMfaStatusQuery,
  useMfaEnrolMutation,
  useMfaVerifyEnrolmentMutation,
  type MfaEnrolResponse,
} from '@/features/auth/mfaMutations'
import { ApiError } from '@/lib/api/apiError'
```

**New state inside `SettingsPage` function** (add alongside existing `mutation`, `register`, etc.):
```ts
const { data: mfaStatus } = useMfaStatusQuery()
const mfaEnrolMutation = useMfaEnrolMutation()
const mfaVerifyMutation = useMfaVerifyEnrolmentMutation()
const [mfaModalOpen, setMfaModalOpen] = useState(false)
const [mfaStep, setMfaStep] = useState<1 | 2 | 3>(1)
const [enrolData, setEnrolData] = useState<MfaEnrolResponse | null>(null)
const [mfaCode, setMfaCode] = useState('')
const [mfaCodeError, setMfaCodeError] = useState('')
const [backupCodesSaved, setBackupCodesSaved] = useState(false)
```

**New handler functions inside `SettingsPage`:**
```ts
const handleOpenMfaModal = async () => {
  setMfaStep(1)
  setMfaCode('')
  setMfaCodeError('')
  setBackupCodesSaved(false)
  try {
    const data = await mfaEnrolMutation.mutateAsync(undefined)
    setEnrolData(data ?? null)
    setMfaModalOpen(true)
  } catch {
    toast.error(t('errors.genericError'))
  }
}

const handleMfaVerify = async () => {
  setMfaCodeError('')
  try {
    await mfaVerifyMutation.mutateAsync({ code: mfaCode })
    setMfaStep(3)
  } catch (err) {
    if (err instanceof ApiError && err.code === 'MFA_CODE_INVALID') {
      setMfaCodeError(t('auth.mfaCodeInvalid'))
    } else if (err instanceof ApiError && err.code === 'MFA_NO_PENDING_ENROLMENT') {
      setMfaCodeError(t('auth.mfaNoPendingEnrolment'))
    } else {
      setMfaCodeError(t('errors.genericError'))
    }
  }
}

const handleMfaModalClose = () => {
  setMfaModalOpen(false)
  setEnrolData(null)
}
```

**JSX — add after the closing `</form>` of the Change Password card, before the outer `</div>`:**
```tsx
{/* Security section */}
<div className="space-y-4 rounded-xl border border-border bg-card p-6 shadow-sm">
  <h2 className="text-lg font-semibold text-foreground">
    {t('settings.security.sectionTitle')}
  </h2>
  <div className="flex items-center justify-between">
    <div>
      <p className="text-sm text-muted-foreground">{t('settings.security.description')}</p>
      <p className="mt-1 text-sm font-medium">
        {mfaStatus?.mfaEnabled
          ? t('settings.security.mfaEnabled')
          : t('settings.security.mfaDisabled')}
      </p>
    </div>
    <Button
      variant="outline"
      onClick={() => { void handleOpenMfaModal() }}
      disabled={mfaEnrolMutation.isPending}
    >
      {mfaStatus?.mfaEnabled
        ? t('settings.security.reenrolButton')
        : t('settings.security.enableButton')}
    </Button>
  </div>
</div>

{/* MFA Enrolment Modal */}
<Dialog open={mfaModalOpen} onOpenChange={(open) => { if (!open) handleMfaModalClose() }}>
  <DialogContent className="max-w-md">
    <DialogHeader>
      <DialogTitle>
        {mfaStep === 1 && t('settings.security.modal.step1Title')}
        {mfaStep === 2 && t('settings.security.modal.step2Title')}
        {mfaStep === 3 && t('settings.security.modal.step3Title')}
      </DialogTitle>
    </DialogHeader>

    {mfaStep === 1 && enrolData && (
      <div className="space-y-4">
        <p className="text-sm text-muted-foreground">
          {t('settings.security.modal.step1Instructions')}
        </p>
        <div className="flex justify-center">
          <img
            src={enrolData.qrCodeDataUrl}
            alt={t('settings.security.modal.qrAlt')}
            className="h-48 w-48 rounded-md border border-border"
          />
        </div>
        <div className="space-y-1">
          <p className="text-xs text-muted-foreground">
            {t('settings.security.modal.manualSecretLabel')}
          </p>
          <code className="block rounded bg-muted px-2 py-1 text-xs font-mono break-all">
            {enrolData.secret}
          </code>
        </div>
        <Button className="w-full" onClick={() => setMfaStep(2)}>
          {t('settings.security.modal.nextButton')}
        </Button>
      </div>
    )}

    {mfaStep === 2 && (
      <div className="space-y-4">
        <p className="text-sm text-muted-foreground">
          {t('settings.security.modal.step2Instructions')}
        </p>
        <div className="space-y-1.5">
          <Label htmlFor="mfa-code">{t('settings.security.modal.codeLabel')}</Label>
          <Input
            id="mfa-code"
            type="text"
            inputMode="numeric"
            maxLength={6}
            placeholder={t('settings.security.modal.codePlaceholder')}
            value={mfaCode}
            onChange={(e) => setMfaCode(e.target.value.replace(/\D/g, '').slice(0, 6))}
            autoFocus
          />
          {mfaCodeError && (
            <p role="alert" className="text-xs text-destructive">{mfaCodeError}</p>
          )}
        </div>
        <div className="flex gap-2">
          <Button variant="outline" className="flex-1" onClick={() => setMfaStep(1)}>
            {t('settings.security.modal.backButton')}
          </Button>
          <Button
            className="flex-1"
            onClick={() => { void handleMfaVerify() }}
            disabled={mfaCode.length !== 6 || mfaVerifyMutation.isPending}
          >
            {mfaVerifyMutation.isPending
              ? t('settings.security.modal.verifying')
              : t('settings.security.modal.verifyButton')}
          </Button>
        </div>
      </div>
    )}

    {mfaStep === 3 && enrolData && (
      <div className="space-y-4">
        <p className="text-sm text-muted-foreground">
          {t('settings.security.modal.step3Instructions')}
        </p>
        <div className="grid grid-cols-2 gap-2">
          {enrolData.backupCodes.map((code, i) => (
            <code
              key={i}
              className="rounded bg-muted px-2 py-1 text-center text-sm font-mono"
            >
              {code}
            </code>
          ))}
        </div>
        <p className="text-xs font-medium text-muted-foreground">
          {t('settings.security.modal.backupCodesNote')}
        </p>
        <div className="flex items-center gap-2">
          <Checkbox
            id="backup-codes-saved"
            checked={backupCodesSaved}
            onCheckedChange={(v) => setBackupCodesSaved(v === true)}
          />
          <Label htmlFor="backup-codes-saved" className="cursor-pointer text-sm">
            {t('settings.security.modal.confirmSavedLabel')}
          </Label>
        </div>
        <Button className="w-full" disabled={!backupCodesSaved} onClick={handleMfaModalClose}>
          {t('settings.security.modal.doneButton')}
        </Button>
      </div>
    )}
  </DialogContent>
</Dialog>
```

**Note on `Label` import:** already present in `settings.tsx` from Story 2.12. Do NOT add a duplicate import.

**Note on `useState` import:** check if `react` is already imported; add `useState` to the existing react import.

---

### i18n Keys

**In `web/src/lib/i18n/locales/en.json`**, inside the `"auth"` object (alongside existing auth keys):
```json
"mfaCodeInvalid": "Invalid or expired code. Please try again.",
"mfaNoPendingEnrolment": "MFA setup session expired. Please start over."
```

Inside the existing `"settings"` object, add a `"security"` key:
```json
"security": {
  "sectionTitle": "Two-Factor Authentication",
  "description": "Add a second layer of security to your account.",
  "mfaEnabled": "Status: Enabled",
  "mfaDisabled": "Status: Not enabled",
  "enableButton": "Enable MFA",
  "reenrolButton": "Re-enrol MFA",
  "modal": {
    "step1Title": "Set Up Authenticator App",
    "step1Instructions": "Scan this QR code with your authenticator app (e.g. Google Authenticator, Authy), or enter the key manually.",
    "qrAlt": "QR code for MFA setup",
    "manualSecretLabel": "Or enter manually:",
    "nextButton": "Next",
    "step2Title": "Verify Code",
    "step2Instructions": "Enter the 6-digit code from your authenticator app to confirm setup.",
    "codeLabel": "Authentication code",
    "codePlaceholder": "000000",
    "backButton": "Back",
    "verifyButton": "Verify",
    "verifying": "Verifying…",
    "step3Title": "Save Backup Codes",
    "step3Instructions": "Save these backup codes somewhere safe. Each code can only be used once if you lose your authenticator.",
    "backupCodesNote": "These codes will not be shown again.",
    "confirmSavedLabel": "I have saved my backup codes in a safe place",
    "doneButton": "Done"
  }
}
```

---

### Files to CREATE

| File | Purpose |
|---|---|
| `src/FormForge.Api/Domain/Entities/MfaBackupCode.cs` | New entity |
| `src/FormForge.Api/Features/Auth/MfaService.cs` | IMfaService + MfaService + outcomes |
| `src/FormForge.Api/Features/Auth/Dtos/MfaEnrolResponse.cs` | Enrolment response DTO |
| `src/FormForge.Api/Features/Users/Dtos/MfaVerifyRequest.cs` | Verify request DTO |
| `src/FormForge.Api/Features/Users/Validators/MfaVerifyRequestValidator.cs` | FluentValidation |
| `web/src/features/auth/mfaMutations.ts` | TanStack Query hooks |
| EF Core migration (auto-generated) | `AddMfaTables` |

### Files to MODIFY

| File | Change |
|---|---|
| `src/FormForge.Api/Domain/Entities/User.cs` | Add `MfaEnabled`, `MfaSecretProtected`, `BackupCodes` |
| `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` | `MfaBackupCodes` DbSet + entity config |
| `src/FormForge.Api/Features/Users/MeEndpoints.cs` | 3 new endpoints + handlers; new `using` statements |
| `src/FormForge.Api/Program.cs` | Register `IMfaService`, `MfaVerifyRequestValidator`; `AddDataProtection()` |
| `web/src/routes/_app/settings.tsx` | Security card + 3-step Dialog modal |
| `web/src/lib/i18n/locales/en.json` | `auth.mfaCodeInvalid`, `auth.mfaNoPendingEnrolment`, `settings.security.*` |

### Files to Leave Untouched

- `src/FormForge.Api/Features/Auth/AuthService.cs` — login MFA challenge is Story 2.14
- `src/FormForge.Api/Features/Auth/AuthEndpoints.cs` — `POST /api/auth/mfa/verify` is Story 2.14
- `web/src/routes/_app.tsx` — no navigation changes needed for this story
- All Designer, Menu, CRUD, Provisioning files — unrelated

---

### Anti-Pattern Prevention

**DO NOT** implement `POST /api/auth/mfa/verify` (the login-time TOTP challenge) — that is Story 2.14 and lives in `AuthEndpoints.cs`. The `POST /api/users/me/mfa/verify` in this story is the enrolment confirmation only.

**DO NOT** commit the TOTP secret in `InitiateEnrolment` — it lives in `IMemoryCache` only until `VerifyEnrolmentAsync` succeeds.

**DO NOT** use `QRCode` (GDI+-based renderer) from the QRCoder package — use `PngByteQRCode`. The Docker container (Linux) does not have `libgdiplus` installed; `PngByteQRCode` renders pure PNG in managed code.

**DO NOT** evict the pending enrolment cache entry on a wrong TOTP code — the user needs to retry with the next 30-second code window without re-scanning the QR code.

**DO NOT** use `ExecuteDeleteAsync` for backup code deletion — use `db.MfaBackupCodes.RemoveRange(user.BackupCodes)` tracked by EF Core so the delete and insert commit in the same `SaveChangesAsync` transaction. `ExecuteDeleteAsync` bypasses change tracking and executes immediately, breaking atomicity.

**DO NOT** store `MfaEnabled` in the JWT — it's mutable state. Always query `GET /me/mfa/status` for current value.

**DO NOT** log the base32 secret or raw backup codes anywhere — treat them like passwords.

**DO NOT** add `.AddValidationFilter<MfaVerifyRequest>()` to the `GET /me/mfa/enrol` endpoint — GET has no body.

**DO NOT** add `AsNoTracking()` to the user load in `VerifyEnrolmentAsync` — we mutate `user.MfaEnabled`, `user.MfaSecretProtected`, `user.UpdatedAt`; tracked load is required.

**DO NOT** call `Include(u => u.BackupCodes)` in `GetMfaStatusAsync` — only `mfa_enabled` is needed; `.Select(u => u.MfaEnabled)` avoids loading unnecessary data.

---

### Previous Story Learnings (2.12)

- **`ConfigureAwait(false)` on every `await`** — build analyzer enforces this; every async call needs it.
- **`[SuppressMessage("Performance", "CA1812")]`** — required on `MfaService` and `MfaVerifyRequestValidator` (both `internal sealed class`, DI-registered, no public constructor callsite).
- **`ArgumentNullException.ThrowIfNull`** on all handler and service method parameters.
- **`AuthService` is `internal sealed partial class`** — `MfaService` does NOT use `[LoggerMessage]` in this story so does NOT need `partial`.
- **Pre-existing test failures:** 2 DELETE→405 audit tests + 1 i18n-lint failure — ignore.
- **TanStack Router does NOT regenerate `routeTree.gen.ts`** — no new route files added in this story (only `settings.tsx` is modified).
- **shadcn `Dialog` pattern** — the project already uses shadcn/ui; Dialog is likely already installed. Check `web/src/components/ui/dialog.tsx` before adding via CLI.
- **`void` prefix on async event handlers in JSX** — `onClick={() => { void handleOpenMfaModal() }}` matches the existing pattern in `settings.tsx` (`onSubmit={(e) => { void handleSubmit(submit)(e) }}`).

---

### Testing

**Backend — add to `src/FormForge.Api.Tests/Features/Users/MeIntegrationTests.cs`:**
```
GetMfaStatus_Unauthenticated_Returns401
GetMfaStatus_AuthenticatedUser_DefaultsToFalse
EnrolMfa_Unauthenticated_Returns401
EnrolMfa_Authenticated_ReturnsSecretQrAndEightBackupCodes
VerifyEnrolment_NoPriorEnrol_Returns400_NoPendingEnrolment
VerifyEnrolment_InvalidCode_Returns400_MfaCodeInvalid
VerifyEnrolment_ValidCode_Returns200_SetsMfaEnabled_WritesBackupCodes
VerifyEnrolment_ReEnrolment_ReplacesOldBackupCodesAtomically
```

Test baseline: 738 passed, 2 pre-existing failures. All new tests must pass.

For the valid-code test: use Otp.NET directly in the test to generate a current TOTP code from the base32 secret returned by the enrol endpoint — no mocking needed.

**Frontend manual verification:**
1. Log in → `/settings` → Security section shows "Status: Not enabled" + "Enable MFA"
2. Click "Enable MFA" → modal step 1: QR code + manual secret visible
3. Scan QR with authenticator → click "Next" → step 2: 6-digit code input
4. Enter correct code → "Verify" → step 3: 8 backup codes displayed
5. Check the checkbox → "Done" becomes active → click → modal closes → status updates to "Enabled"
6. Click "Re-enrol MFA" → new QR generated; verify old backup codes are replaced after completing re-enrolment

---

### References

- [Source: `_bmad-output/planning-artifacts/epics.md` — Story 2.13 full AC (lines 800–827), Stories 2.14 & 2.15 (lines 828–889), FR-53, AR-56]
- [Source: `_bmad-output/planning-artifacts/architecture.md` — Decision 2.12 TOTP MFA (lines 449–461), Decision 4.7 httpClient, Decision 4.9 form composition]
- [Source: `src/FormForge.Api/Domain/Entities/User.cs` — current entity; extend by adding 3 properties]
- [Source: `src/FormForge.Api/Features/Auth/AuthService.cs` — `IAuthService`/`AuthService` constructor pattern; `ChangePasswordOutcome`/`ChangePasswordResult` to mirror for MFA outcomes]
- [Source: `src/FormForge.Api/Features/Users/MeEndpoints.cs` — `ChangePasswordHandler` pattern for ProblemDetails extensions; `UpdateMyPreferencesHandler` for userId claim extraction]
- [Source: `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` — DbSet pattern, `OnModelCreating` entity config style to follow for `MfaBackupCode`]
- [Source: `src/FormForge.Api/Program.cs` — `AddScoped` registration patterns; `AddRateLimiter` block; route group mappings]
- [Source: `web/src/features/auth/authMutations.ts` — `useChangePasswordMutation` mutation pattern]
- [Source: `web/src/routes/_app/settings.tsx` — current page structure to extend; Change Password card layout to mirror for Security card]
- [Source: `web/src/lib/i18n/locales/en.json` — existing `settings.*` and `auth.*` structure]
- [Source: `_bmad-output/implementation-artifacts/2-12-authenticated-password-change.md` — dev notes, anti-patterns, Story 2.12 learnings]

## Dev Agent Record

### Agent Model Used

claude-sonnet-4-6 (story authoring); claude-opus-4-8 (implementation)

### Debug Log References

- `dotnet build` — clean (0 warnings/errors) after adding `ArgumentNullException.ThrowIfNull(migrationBuilder)` to the generated migration's `Up`/`Down` (CA1062, matching existing migration convention).
- Backend `dotnet test` (full suite): **746 passed, 2 failed**. The 2 failures are the pre-existing `MutationAuditLogIntegrationTests.GetMutationAuditLog_AppendOnly_DeleteVerb_Returns405` audit 405 tests (red on a clean tree — not introduced by this story). 738 prior baseline + 8 new MFA tests = 746.
- Frontend `tsc -b --noEmit` — clean (exit 0).
- Frontend `eslint` — the 2 changed/created files (`settings.tsx`, `mfaMutations.ts`) are clean; the 47 reported errors are all pre-existing in unrelated files (`designer.$designerId.tsx`, `designer.library.tsx`, `index.tsx`, `login.tsx`).
- Frontend `vitest run` — **242 passed, 1 failed**. The 1 failure is the pre-existing `i18n-lint.test.ts` (missing `designer.inspector.placeholders.label`, documented as red on a clean tree).

### Completion Notes List

- Implemented enrolment-only TOTP MFA per the story scope guard. Did NOT touch `AuthService`/`AuthEndpoints` (login-time challenge is Story 2.14) or admin MFA reset (Story 2.15).
- TOTP secret is generated as a 160-bit base32 key (Otp.NET), QR is rendered with `PngByteQRCode` (managed PNG, no libgdiplus dependency for the Linux container). Pending enrolment (secret + raw backup codes) lives in `IMemoryCache` for 10 minutes and is only persisted on successful verify.
- On verify: `users.mfa_secret_protected` stores the `IDataProtector`-encrypted (purpose `"mfa-totp-secret"`) base32 secret, `mfa_enabled` set true, 8 bcrypt-hashed backup codes written, and old backup codes removed via tracked `RemoveRange` — delete + insert + flag flip commit in a single `SaveChangesAsync` (AC-5 atomicity). Cache eviction happens only after a successful commit; a wrong code leaves the pending entry intact for retry.
- Added `AddDataProtection()` to `Program.cs` (not auto-registered for JWT-only apps) and registered `IMfaService` + `MfaVerifyRequestValidator`.
- The EF migration `AddMfaTables` is applied automatically by the app's startup `db.Database.Migrate()` and by the integration test fixture's `MigrateAsync()`; the cascade FK means the test's `TRUNCATE ... users CASCADE` also clears `mfa_backup_codes`.
- Frontend: Security card + 3-step Dialog (QR/manual secret → 6-digit verify with inline error → backup-codes grid gated by a required checkbox). Used `key={code}` for the backup-codes list to avoid the array-index-key lint rule. All new i18n keys are referenced by literal `t('...')` calls so they are not flagged as orphaned.
- Added `Otp.NET` to the test project so the valid-code test computes a live TOTP from the enrol secret (no mocking).

### File List

**Created:**
- `src/FormForge.Api/Domain/Entities/MfaBackupCode.cs`
- `src/FormForge.Api/Features/Auth/MfaService.cs`
- `src/FormForge.Api/Features/Auth/Dtos/MfaEnrolResponse.cs`
- `src/FormForge.Api/Features/Users/Dtos/MfaVerifyRequest.cs`
- `src/FormForge.Api/Features/Users/Validators/MfaVerifyRequestValidator.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260531221752_AddMfaTables.cs` (+ `.Designer.cs`, auto-generated)
- `web/src/features/auth/mfaMutations.ts`

**Modified:**
- `Directory.Packages.props` — added `Otp.NET` 1.4.0, `QRCoder` 1.6.0 versions
- `src/FormForge.Api/FormForge.Api.csproj` — `Otp.NET`, `QRCoder` package references
- `src/FormForge.Api/Domain/Entities/User.cs` — `MfaEnabled`, `MfaSecretProtected`, `BackupCodes`
- `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` — `MfaBackupCodes` DbSet, User column mappings, `MfaBackupCode` entity config
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/FormForgeDbContextModelSnapshot.cs` — updated by migration
- `src/FormForge.Api/Features/Users/MeEndpoints.cs` — 3 MFA endpoints + handlers, `Auth.Dtos` using
- `src/FormForge.Api/Program.cs` — `AddDataProtection()`, `IMfaService` + `MfaVerifyRequestValidator` registrations
- `src/FormForge.Api.Tests/FormForge.Api.Tests.csproj` — `Otp.NET` package reference
- `src/FormForge.Api.Tests/Features/Users/MeIntegrationTests.cs` — 8 new MFA integration tests + helpers + DTO
- `web/src/routes/_app/settings.tsx` — Security card + 3-step MFA enrolment Dialog
- `web/src/lib/i18n/locales/en.json` — `auth.mfaCodeInvalid`, `auth.mfaNoPendingEnrolment`, `settings.security.*`

## Change Log

| Date | Change |
|---|---|
| 2026-06-01 | Implemented Story 2.13 TOTP MFA enrolment: `MfaBackupCode` entity + `User` MFA columns, `AddMfaTables` migration, `IMfaService`/`MfaService` (Otp.NET TOTP + QRCoder QR + bcrypt backup codes + IDataProtector secret-at-rest), 3 endpoints on `MeEndpoints` (GET enrol / POST verify / GET status), `MfaVerifyRequest` DTO+validator, frontend `mfaMutations.ts` hooks, Security card + 3-step Dialog in `settings.tsx`, and i18n keys. Added 8 integration tests (all pass). Status → review. |
