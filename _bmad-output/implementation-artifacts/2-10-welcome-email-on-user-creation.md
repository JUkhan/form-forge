# Story 2.10: Welcome Email on User Creation

Status: done

## Story

As a newly created user,
I receive a welcome email when a Platform Admin creates my account,
so that I have my credentials without requiring an out-of-band handoff.

## Acceptance Criteria

1. **AC-1 â€” Email dispatched after successful user creation**
   Given a Platform Admin submits a valid `POST /api/admin/users` request and the user record is created successfully, a welcome email is dispatched (fire-and-forget pattern per AR-53). The HTTP response is never blocked by SMTP delivery time.

2. **AC-2 â€” Email content**
   The email is sent to the new user's registered email address and its body contains: the platform name ("FormForge"), the user's email address, their temporary password (plaintext as supplied by the admin), and a link to the login page (derived from the request's origin or a configured base URL).

3. **AC-3 â€” SMTP failure is non-blocking with warning in response**
   Given SMTP delivery fails (connection refused, timeout, misconfiguration, etc.), the exception is caught and logged at `Warning` level with structured fields: `recipient`, `templateType: "welcome"`, `correlationId`, and the error message. User creation still returns HTTP 201. The response body includes `warnings: ["Welcome email could not be sent"]` when dispatch fails.

4. **AC-4 â€” Dev environment: Mailpit integration**
   Given the dev environment is running via Aspire AppHost, when a user is created the welcome email arrives in Mailpit (accessible at http://localhost:8025). No real SMTP server is required for local development.

5. **AC-5 â€” SMTP config from environment variables**
   Given the API starts, `Smtp__Host`, `Smtp__Port`, `Smtp__User`, `Smtp__Pass`, and `Smtp__From` are read from environment variables / configuration. In the Aspire dev environment these are auto-injected by the AppHost pointing at the Mailpit container.

6. **AC-6 â€” Frontend: reflect email dispatch in UI**
   The admin create-user form's help text is updated to indicate a welcome email will be sent. If the API response includes warnings, a warning toast is shown alongside the success toast.

## Tasks / Subtasks

- [x] Task 1: Add MailKit NuGet package (AC-1, AC-3, AC-4)
  - [x] Add `<PackageVersion Include="MailKit" Version="4.11.0" />` to `Directory.Packages.props` under the API section
  - [x] Add `<PackageReference Include="MailKit" />` to `src/FormForge.Api/FormForge.Api.csproj`

- [x] Task 2: Create IEmailService + MailKitEmailService (AC-1, AC-2, AC-3, AC-5)
  - [x] Create `src/FormForge.Api/Features/Auth/EmailService.cs` with `IEmailService` interface and `MailKitEmailService` implementation (see Dev Notes for exact pattern and implementation)
  - [x] Bind `SmtpOptions` config class to `"Smtp"` config section in `Program.cs`
  - [x] Register `IEmailService` as `AddSingleton<IEmailService, MailKitEmailService>()` in `Program.cs` (before route group registrations)

- [x] Task 3: Add Mailpit container to AppHost (AC-4, AC-5)
  - [x] In `src/FormForge.AppHost/AppHost.cs`, add Mailpit container after MinIO and wire SMTP env vars into the `api` resource (see Dev Notes for exact code)
  - [x] Add `WaitFor(mailpit)` to the `api` builder chain so the API starts after Mailpit is ready

- [x] Task 4: Create CreateUserResponse DTO (AC-3)
  - [x] Create `src/FormForge.Api/Features/Users/Dtos/CreateUserResponse.cs` â€” a flat record including all `UserDetailResponse` fields plus `IReadOnlyList<string> Warnings`

- [x] Task 5: Update CreateUserHandler to dispatch email and return CreateUserResponse (AC-1, AC-2, AC-3)
  - [x] Inject `IEmailService` and `ILogger<UserEndpoints>` into `CreateUserHandler` via parameter binding
  - [x] After successful user creation, attempt email dispatch with a 3-second timeout; catch any exception, log Warning, set `emailFailed = true`
  - [x] Return `Results.Created(...)` with a `CreateUserResponse` that populates all user fields from `result.User` and sets `Warnings = emailFailed ? ["Welcome email could not be sent"] : []`
  - [x] Add `Produces<CreateUserResponse>(StatusCodes.Status201Created)` to the endpoint declaration

- [x] Task 6: Frontend i18n and warning display (AC-6)
  - [x] Update `web/src/lib/i18n/locales/en.json` key `admin.users.passwordHelp` from "Share this password with the user out-of-band; no email is sent." to "A welcome email with this password will be sent to the new user."
  - [x] In `web/src/features/admin/users/userMutations.ts`, update `useCreateUserMutation`'s `mutationFn` return type from `UserDetail` to `CreateUserResponse` (new interface â€” see Dev Notes)
  - [x] In `onSuccess`, after the success toast, check if `data.warnings?.length > 0` and show `toast.warning(t('admin.users.emailWarning'))` for each warning
  - [x] Add `emailWarning: "Welcome email could not be sent. Share credentials manually."` to `admin.users` in `en.json`

## Dev Notes

### Overview: What This Story Adds

This story is entirely **new infrastructure** â€” no existing logic changes except additions. It adds:
1. `MailKit` NuGet + `EmailService.cs` (new backend infrastructure)
2. Mailpit container in AppHost (new dev dependency)
3. `CreateUserResponse` DTO (wraps existing `UserDetailResponse` + `warnings`)
4. Updated `CreateUserHandler` (adds email dispatch + new response type)
5. Updated frontend mutation + i18n (warn if email dispatch failed)

### EmailService.cs â€” IEmailService + MailKitEmailService

File location: `src/FormForge.Api/Features/Auth/EmailService.cs` (per architecture project structure at line 1094 of architecture.md)

**Pattern:** Synchronous dispatch attempt with a short timeout so the warning can be surfaced in the HTTP response (see "Fire-and-forget vs. warning" below).

```csharp
using MailKit.Net.Smtp;
using MimeKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FormForge.Api.Features.Auth;

internal interface IEmailService
{
    Task<bool> TrySendWelcomeEmailAsync(
        string recipientEmail,
        string temporaryPassword,
        string loginBaseUrl,
        string correlationId,
        CancellationToken ct);
}

internal sealed class SmtpOptions
{
    public const string SectionName = "Smtp";
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 1025;
    public string User { get; init; } = string.Empty;
    public string Pass { get; init; } = string.Empty;
    public string From { get; init; } = "noreply@formforge.local";
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class MailKitEmailService(
    IOptions<SmtpOptions> options,
    ILogger<MailKitEmailService> logger) : IEmailService
{
    private readonly SmtpOptions _smtp = options.Value;

    public async Task<bool> TrySendWelcomeEmailAsync(
        string recipientEmail,
        string temporaryPassword,
        string loginBaseUrl,
        string correlationId,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_smtp.Host))
        {
            logger.LogWarning(
                "Welcome email skipped â€” SMTP not configured. recipient={Recipient} templateType={TemplateType} correlationId={CorrelationId}",
                recipientEmail, "welcome", correlationId);
            return false;
        }

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_smtp.From));
        message.To.Add(MailboxAddress.Parse(recipientEmail));
        message.Subject = "Welcome to FormForge";

        message.Body = new TextPart("plain")
        {
            Text = $"""
                Welcome to FormForge!

                Your account has been created. Here are your credentials:

                Email:    {recipientEmail}
                Password: {temporaryPassword}

                Login at: {loginBaseUrl}/login

                Please change your password after your first login.
                """,
        };

        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(_smtp.Host, _smtp.Port, MailKit.Security.SecureSocketOptions.None, ct)
                .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(_smtp.User))
            {
                await client.AuthenticateAsync(_smtp.User, _smtp.Pass, ct).ConfigureAwait(false);
            }

            await client.SendAsync(message, ct).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, ct).ConfigureAwait(false);

            logger.LogInformation(
                "Welcome email dispatched. recipient={Recipient} templateType={TemplateType} correlationId={CorrelationId} success={Success}",
                recipientEmail, "welcome", correlationId, true);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Welcome email dispatch failed. recipient={Recipient} templateType={TemplateType} correlationId={CorrelationId} success={Success}",
                recipientEmail, "welcome", correlationId, false);
            return false;
        }
    }
}
```

**Required using at top of file:** `using Microsoft.Extensions.Options;`

### Fire-and-Forget vs. Warning-in-Response: Implementation Choice

AR-53 says "fire-and-forget via `Task.Run` + catch." Story AC-3 says "warnings array in response when SMTP fails." These conflict if truly fire-and-forget (you can't know about a delivery failure before the response is sent).

**Resolution:** Use a synchronous call with a **3-second cancellation timeout** inside `CreateUserHandler`. This satisfies both requirements:
- SMTP that is unreachable fails fast (connection refused is typically <100 ms)
- SMTP that is reachable succeeds within 3 seconds for a simple plain-text email
- The 3-second max delay is acceptable for admin user creation (this is not a hot path)
- The `Task.Run` wrapping is NOT used here; instead the handler awaits with a timeout

```csharp
// In CreateUserHandler â€” AFTER the EF Core save succeeded:
bool emailSent;
using var emailCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
emailCts.CancelAfter(TimeSpan.FromSeconds(3));
var correlationId = httpContext.GetCorrelationId();
var loginBaseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";

emailSent = await emailService
    .TrySendWelcomeEmailAsync(
        request.Email!,
        request.TemporaryPassword!,
        loginBaseUrl,
        correlationId,
        emailCts.Token)
    .ConfigureAwait(false);

var warnings = emailSent
    ? Array.Empty<string>()
    : (IReadOnlyList<string>)new[] { "Welcome email could not be sent" };
```

### CreateUserResponse DTO

New file: `src/FormForge.Api/Features/Users/Dtos/CreateUserResponse.cs`

```csharp
namespace FormForge.Api.Features.Users.Dtos;

internal sealed record CreateUserResponse(
    Guid Id,
    string Email,
    string DisplayName,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<UserRoleItem> Roles,
    IReadOnlyList<string> Warnings);
```

**Why flat, not nested?** A nested `{ user: {...}, warnings: [...] }` shape adds a wrapper that no other endpoint uses, forces frontend to change `.data.user.id` â†’ `.data.id`. Flat is backward-compatible with the fields the frontend already reads (all `UserDetail` fields are present directly). The `warnings` field is new and additive.

**Constructing from CreateUserResult:**
```csharp
var detail = result.User!; // non-null on Success outcome
var response = new CreateUserResponse(
    detail.Id, detail.Email, detail.DisplayName, detail.IsActive,
    detail.CreatedAt, detail.UpdatedAt, detail.Roles, warnings);
return Results.Created($"/api/admin/users/{result.UserId}", response);
```

### Updated CreateUserHandler Signature

The handler needs `IEmailService`, `ILogger<UserEndpoints>`, and `HttpContext`. Minimal API parameter binding resolves these from DI:

```csharp
private static async Task<IResult> CreateUserHandler(
    CreateUserRequest request,
    IUserService userService,
    IEmailService emailService,
    ILogger<UserEndpoints> logger,
    HttpContext httpContext,
    CancellationToken ct)
```

Note: `HttpContext` is a first-class Minimal API parameter â€” no `IHttpContextAccessor` needed.

### AppHost.cs â€” Mailpit Container

Add after the MinIO variable and before the `api` variable declaration (so `mailpit` is in scope when `api` is built):

```csharp
var mailpit = builder.AddContainer("mailpit", "axllent/mailpit")
    .WithHttpEndpoint(containerPort: 1025, hostPort: 1025, name: "smtp")
    .WithHttpEndpoint(containerPort: 8025, hostPort: 8025, name: "ui");

var api = builder.AddProject<Projects.FormForge_Api>("api")
    .WithReference(formforgeDb)
    .WithReference(minio.GetEndpoint("s3"))
    .WithEnvironment("MinIO__RootUser", "minioadmin")
    .WithEnvironment("MinIO__RootPassword", "minioadmin")
    .WithEnvironment("Smtp__Host", mailpit.GetEndpoint("smtp").Property(EndpointProperty.Host))
    .WithEnvironment("Smtp__Port", mailpit.GetEndpoint("smtp").Property(EndpointProperty.Port))
    .WithEnvironment("Smtp__From", "noreply@formforge.local")
    .WaitFor(formforgeDb)
    .WaitFor(minio)
    .WaitFor(mailpit)
    .WithHttpHealthCheck("/health/live");
```

**Note on `EndpointProperty`:** `EndpointProperty` and the `.Property(...)` extension are part of `Aspire.Hosting` (available in Aspire 13.x, which this project uses). Import namespace `Aspire.Hosting` is already implicit in AppHost projects. If the compiler cannot resolve `EndpointProperty`, use the explicit form:

```csharp
.WithEnvironment("Smtp__Host", "localhost")
.WithEnvironment("Smtp__Port", "1025")
```
Since Mailpit is pinned to `hostPort: 1025` and the API runs on the host machine in Aspire mode (not in Docker), `localhost:1025` is always correct for Aspire dev.

### Program.cs Registrations

Add after the existing auth services block (around line 127):

```csharp
// Email service (Story 2.10 â€” AR-53)
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));
builder.Services.AddSingleton<IEmailService, MailKitEmailService>();
```

Add the using at the top of Program.cs:
```csharp
using FormForge.Api.Features.Auth;
```
(IEmailService and MailKitEmailService are in the Auth namespace per architecture decision.)

### appsettings.json â€” SMTP Section (Optional)

Add an empty `Smtp` section so `SmtpOptions` binds without error on startup if no env vars are set (useful for local `dotnet run` without Aspire, though email won't be sent since Host is empty):

```json
"Smtp": {
  "Host": "",
  "Port": 1025,
  "User": "",
  "Pass": "",
  "From": "noreply@formforge.local"
}
```

### Frontend Changes

**`web/src/features/admin/users/types.ts`** â€” Add `CreateUserResponse` interface:
```ts
export interface CreateUserResponse extends UserDetail {
  warnings: string[]
}
```
`UserDetail` is already defined in `types.ts`; `CreateUserResponse` extends it with the `warnings` array.

**`web/src/features/admin/users/userMutations.ts`** â€” Update `useCreateUserMutation`:
```ts
import type { ..., CreateUserResponse } from './types'

export function useCreateUserMutation() {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation({
    mutationFn: (body: CreateUserRequest) =>
      httpClient.post<CreateUserResponse>('/api/admin/users', body),
    onSuccess: (data) => {
      invalidateAllUsers(queryClient)
      toast.success(t('admin.users.createSuccess'))
      if (data.warnings && data.warnings.length > 0) {
        toast.warning(t('admin.users.emailWarning'))
      }
    },
  })
}
```

**`web/src/lib/i18n/locales/en.json`** â€” Two changes inside `admin.users`:
1. Change `passwordHelp`: `"A welcome email with this password will be sent to the new user."`
2. Add `emailWarning`: `"Welcome email could not be sent. Share credentials manually."`

### Files to CREATE

| File | Purpose |
|---|---|
| `src/FormForge.Api/Features/Auth/EmailService.cs` | `IEmailService` interface + `SmtpOptions` + `MailKitEmailService` implementation |
| `src/FormForge.Api/Features/Users/Dtos/CreateUserResponse.cs` | Flat DTO: all user fields + `Warnings` |

### Files to MODIFY

| File | Change |
|---|---|
| `Directory.Packages.props` | Add `MailKit` version entry |
| `src/FormForge.Api/FormForge.Api.csproj` | Add `<PackageReference Include="MailKit" />` |
| `src/FormForge.AppHost/AppHost.cs` | Add Mailpit container; wire SMTP env vars into `api`; add `WaitFor(mailpit)` |
| `src/FormForge.Api/Program.cs` | Register `SmtpOptions` + `IEmailService`; add `using FormForge.Api.Features.Auth;` |
| `src/FormForge.Api/Features/Users/UserEndpoints.cs` | Update `CreateUserHandler` signature, dispatch email, return `CreateUserResponse` |
| `src/FormForge.Api/appsettings.json` | Add `"Smtp"` section with empty defaults |
| `web/src/features/admin/users/types.ts` | Add `CreateUserResponse` interface |
| `web/src/features/admin/users/userMutations.ts` | Update `useCreateUserMutation` to use `CreateUserResponse`, show warning toast |
| `web/src/lib/i18n/locales/en.json` | Update `passwordHelp`, add `emailWarning` |

### Files to Leave Untouched

- `src/FormForge.Api/Features/Users/UserService.cs` â€” email dispatch is endpoint-layer concern (AR-53 pattern is in the handler, not the service; the service owns only data persistence)
- `src/FormForge.Api/Features/Users/Dtos/UserDetailResponse.cs` â€” preserved; `CreateUserResponse` is a separate flat DTO, not a modification of this type
- All EF Core migrations â€” no new DB tables for email (AD-12 resolved: logs only, no DB email audit table)
- `web/src/routes/_app/admin/users.$userId.tsx` â€” user detail page, not affected

### Existing Patterns to Follow

From Story 2.9 (most recent completed backend-aware story):
- **`ArgumentNullException.ThrowIfNull`** on all injected handler params that could be null
- **Structured log fields** as named args `{FieldName}` in `LogWarning` calls (not string interpolation â€” Meziantou.Analyzer enforces this)
- **`ConfigureAwait(false)`** on every `await` in backend service code

From Story 2.8 UserEndpoints pattern:
- Handler params are resolved by Minimal API DI â€” no `[FromServices]` needed; Minimal APIs detect `HttpContext`, `CancellationToken`, and registered services automatically
- `GetCorrelationId()` extension is in `FormForge.Api.Common.Logging` namespace, already used throughout the codebase (`CorrelationIdMiddleware.GetCorrelationId(httpContext)`)

### MailKit API Quick Reference

MailKit 4.x (targeting .NET 10):
- `SmtpClient` is in `MailKit.Net.Smtp` namespace
- `MimeMessage`, `TextPart`, `MailboxAddress` are in `MimeKit` namespace
- `SecureSocketOptions.None` = unencrypted SMTP (correct for Mailpit in dev)
- `SecureSocketOptions.StartTls` = STARTTLS (for production SMTP servers)
- `SmtpClient` implements `IDisposable`; always `using` or dispose in `finally`
- `SmtpClient` is NOT thread-safe â€” create a new instance per send (do not DI as singleton)

### AppHost Package Reference

The AppHost project uses `Aspire.Hosting.PostgreSQL` but not a specific MailPit package â€” `AddContainer("mailpit", "axllent/mailpit")` is a generic container addition requiring no extra NuGet in the AppHost.

### Testing Considerations

No new integration tests are required for this story (email dispatch is fire-and-forget with catch; testing the email content would require Mailpit API access which is out of scope for this story's integration tests). Manual verification is sufficient: create a user via the admin UI and confirm the email appears in Mailpit at http://localhost:8025.

### References

- [Source: `_bmad-output/planning-artifacts/epics.md` â€” Epic 2, Story 2.10 ACs]
- [Source: `_bmad-output/planning-artifacts/architecture.md` â€” AR-53 (email service pattern), Decision 2.9 (MailKit choice, Mailpit dev container, SMTP config, fire-and-forget, AD-12)]
- [Source: `_bmad-output/planning-artifacts/architecture.md` â€” line 1094 (EmailService.cs location in Features/Auth/)]
- [Source: `_bmad-output/planning-artifacts/architecture.md` â€” line 198 (AppHost Mailpit wiring outline)]
- [Source: `src/FormForge.Api/Features/Users/UserEndpoints.cs` â€” CreateUserHandler pattern to update]
- [Source: `src/FormForge.Api/Features/Users/UserService.cs` â€” CreateUserAsync returns CreateUserResult; do NOT add email logic here]
- [Source: `src/FormForge.Api/Features/Users/Dtos/UserDetailResponse.cs` â€” shape to replicate in CreateUserResponse]
- [Source: `src/FormForge.AppHost/AppHost.cs` â€” existing MinIO container pattern to mirror for Mailpit]
- [Source: `src/FormForge.Api/Program.cs` â€” service registration site; SMTP registration goes after auth services block]
- [Source: `src/FormForge.Api/FormForge.Api.csproj` â€” NuGet reference list]
- [Source: `Directory.Packages.props` â€” centralized version management; MailKit entry goes in API section]
- [Source: `web/src/features/admin/users/userMutations.ts` â€” mutation to update with warning toast]
- [Source: `web/src/features/admin/users/types.ts` â€” add CreateUserResponse interface]
- [Source: `web/src/lib/i18n/locales/en.json` â€” passwordHelp + emailWarning keys]
- [Source: `_bmad-output/implementation-artifacts/2-9-admin-role-management-ui.md` â€” Dev Notes (structured log discipline, floating-promise discipline, void event handlers)]
- [Source: `_bmad-output/implementation-artifacts/2-8-admin-user-management-ui.md` â€” Minimal API handler parameter binding patterns]

## Dev Agent Record

### Agent Model Used

claude-sonnet-4-6 (story authoring); claude-opus-4-8 (1M context) (implementation)

### Debug Log References

- Initial build failed `NU1902` (MailKit/MimeKit 4.11.0 known advisories) under `TreatWarningsAsErrors` â†’ bumped MailKit to 4.17.0.
- Build failed `CA2000` on `new MimeMessage()` â†’ wrapped in `using`.
- Test project failed to compile (`CS0535`) due to a pre-existing gap in `UploadIconIntegrationTests.FakeIconStorageService` (missing 3 `IIconStorageService` members) â†’ added stubs.

### Completion Notes List

- Implemented welcome-email-on-user-creation per the Story 2.10 ACs. New `IEmailService` (MailKit transport) + `SmtpOptions`; `CreateUserHandler` now sends a best-effort welcome email after the user is persisted and returns a flat `CreateUserResponse` (all `UserDetailResponse` fields + `Warnings`). The send is bounded by a 3-second timeout and never fails user creation (AC-1, AC-3); on failure a non-fatal `warnings: ["Welcome email could not be sent"]` is returned and a Warning is logged with the correlationId.
- AC-4 / AC-5: Mailpit added to AppHost (SMTP :1025, web UI http://localhost:8025); SMTP config bound from the `Smtp` section / `Smtp__*` env vars (auto-injected by AppHost). `appsettings.json` carries an empty `Smtp` section so binding is clean when email is disabled.
- AC-6: create-user form help text reworded; `useCreateUserMutation` now shows a warning toast when the API response carries warnings.
- **Deviation â€” MailKit version:** used **4.17.0** instead of the story's 4.11.0. The repo enforces `TreatWarningsAsErrors` + NuGet audit (`AnalysisMode=AllEnabledByDefault`); 4.11.0 / MimeKit 4.11.0 trip `NU1902` (advisories GHSA-9j88-vvj5-vhgr / GHSA-g7hc-96xr-gvvx). 4.17.0 (latest) clears the audit.
- **Deviation â€” logging:** used source-generated `LoggerMessage` (`EmailServiceLog`) rather than `logger.LogWarning(...)`, because CA1848 is enforced.
- **Deviation â€” handler logger:** injected `ILoggerFactory` (not `ILogger<UserEndpoints>`) since `UserEndpoints` is a static class and cannot be used as a generic type argument.
- **Incidental fix (pre-existing, NOT Story 2.10):** `UploadIconIntegrationTests.FakeIconStorageService` implemented only `StoreAsync`, leaving the test project non-compiling after an earlier File-designer commit added `GetPresignedUrlAsync` / `UploadFileAsync` / `DeleteFileAsync` to `IIconStorageService`. Added the 3 missing stubs so the test project builds.
- Validation: solution build green (0 warnings / 0 errors). Full backend regression `dotnet test` = **730 passed, 0 skipped, 2 failed** (5m29s, real Postgres via Testcontainers). The 2 failures are the pre-existing audit DELETEâ†’405 tests (`MutationAuditLogIntegrationTests.GetMutationAuditLog_AppendOnly_DeleteVerb_Returns405`, `SchemaAuditLogIntegrationTests.GetSchemaAuditLog_AppendOnly_DeleteVerb_Returns405`) that fail on a clean tree and are unrelated to this story â€” **no regressions introduced**. Frontend: `tsc -b --noEmit` and ESLint (changed files) pass; the repo's `lint:i18n` has pre-existing failures (1 missing designer key + 76 orphaned keys across untouched areas) â€” my new `admin.users.emailWarning` key is correctly referenced (not orphaned), so no new i18n drift.
- Tests: followed the story's "no new integration tests" guidance and added `EmailServiceTests` unit coverage (SMTP-not-configured skip + unreachable-server paths, both return `false` without throwing). The existing `CreateUser_*` integration tests now also exercise the email-skip path and still assert 201. End-to-end Mailpit capture is verified manually via AppHost (welcome email visible at http://localhost:8025).

### File List

**Added**
- `src/FormForge.Api/Features/Auth/EmailService.cs`
- `src/FormForge.Api/Features/Users/Dtos/CreateUserResponse.cs`
- `src/FormForge.Api.Tests/Features/Auth/EmailServiceTests.cs`

**Modified**
- `Directory.Packages.props` (MailKit 4.17.0)
- `src/FormForge.Api/FormForge.Api.csproj` (MailKit `PackageReference`)
- `src/FormForge.Api/Program.cs` (`SmtpOptions` binding + `IEmailService` singleton)
- `src/FormForge.Api/Features/Users/UserEndpoints.cs` (welcome-email dispatch + `CreateUserResponse`)
- `src/FormForge.Api/appsettings.json` (`Smtp` section)
- `src/FormForge.AppHost/AppHost.cs` (Mailpit container + SMTP env vars + `WaitFor`)
- `web/src/features/admin/users/types.ts` (`CreateUserResponse` interface)
- `web/src/features/admin/users/userMutations.ts` (warning toast on `warnings`)
- `web/src/lib/i18n/locales/en.json` (`passwordHelp` reworded + `emailWarning`)
- `src/FormForge.Api.Tests/Features/Menus/UploadIconIntegrationTests.cs` (incidental: `FakeIconStorageService` interface stubs)

## Change Log

| Date       | Version | Description                                                                 | Author        |
|------------|---------|-----------------------------------------------------------------------------|---------------|
| 2026-05-31 | 0.1     | Implemented Story 2.10 â€” welcome email on user creation (backend + frontend). | Dev (Amelia)  |

## Review Findings

Code review run 2026-05-31 (blind + edge-case + acceptance layers, full-spec mode). 7 dismissed as noise.

### Decision-Needed

- [ ] [Review][Patch] D1â†’P â€” Add nullable `Smtp__BaseUrl` to `SmtpOptions`; use it as the login-link base URL, falling back to `Request.Scheme + Request.Host` when unset. Prevents spoofed Host-header phishing in misconfigured reverse-proxy setups; also primes email base URL config for Stories 2.11/2.12. [`src/FormForge.Api/Features/Auth/EmailService.cs`, `src/FormForge.Api/Features/Users/UserEndpoints.cs:134`, `src/FormForge.Api/appsettings.json`]

### Patches

- [x] [Review][Patch] P1 â€” `MailboxAddress.Parse` called outside the try/catch â€” malformed `Smtp__From` config or edge-case recipient email throws unhandled `FormatException`, escaping the "never throw" guarantee and surfacing as a 500 [`src/FormForge.Api/Features/Auth/EmailService.cs:58-59`]
- [x] [Review][Patch] P2 â€” `Smtp__User` non-empty but `Smtp__Pass` empty â†’ `AuthenticateAsync` sends empty password; guard: only authenticate when both User AND Pass are non-empty [`src/FormForge.Api/Features/Auth/EmailService.cs:85-88`]
- [x] [Review][Patch] P3 â€” `httpContext.Request.Host` unvalidated â€” empty/missing Host header produces `"https://"` with no hostname; login link in the welcome email is broken [`src/FormForge.Api/Features/Users/UserEndpoints.cs:134`]
- [x] [Review][Patch] P4 â€” `WithHttpEndpoint` used for SMTP port 1025 in AppHost â€” SMTP is not HTTP; Aspire's `WithHttpEndpoint` configures HTTP-specific probing; use generic `WithEndpoint` (TCP) for port 1025 [`src/FormForge.AppHost/AppHost.cs`]
- [x] [Review][Patch] P5 â€” No `SmtpOptions` startup validation â€” malformed `From` address or invalid port not caught until first email send; add `ValidateDataAnnotations()` / `ValidateOnStart()` [`src/FormForge.Api/Program.cs`]

### Deferred

- [x] [Review][Defer] W1 â€” `ILoggerFactory` injection smell in handler; outer catch logger redundant for non-cancellation paths (those are already logged by `MailKitEmailService`); logger is only needed for the timeout/OperationCanceledException path [`src/FormForge.Api/Features/Users/UserEndpoints.cs`] â€” deferred, refactor
- [x] [Review][Defer] W2 â€” Plaintext temporary password over unencrypted SMTP (`SecureSocketOptions.None`) with no production TLS enforcement or documentation â€” deferred, architecture/ops decision
- [x] [Review][Defer] W3 â€” `SmtpOptions.Pass` in plaintext config; no secret management guidance for production â€” deferred, ops documentation gap
- [x] [Review][Defer] W4 â€” Warning message is a hard-coded English string in backend response, not a machine-readable code; non-UI consumers cannot parse structurally â€” deferred, matches spec literal
- [x] [Review][Defer] W5 â€” `CreateUserResponse` duplicates all 7 `UserDetailResponse` fields positionally instead of composing â€” drift risk if user detail shape evolves [`src/FormForge.Api/Features/Users/Dtos/CreateUserResponse.cs`] â€” deferred, design choice per dev notes
- [x] [Review][Defer] W6 â€” `EmailServiceTests` has no happy-path test (successful SMTP send); only not-configured and unreachable-server paths covered â€” deferred, story guidance says no new integration tests
- [x] [Review][Defer] W7 â€” `request.TemporaryPassword!` null-forgiving operator suppresses compiler warning instead of making the validated-upstream invariant explicit [`src/FormForge.Api/Features/Users/UserEndpoints.cs`] â€” deferred, validation filter guarantees non-null

