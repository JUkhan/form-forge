using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Features.Auth.Dtos;
using FormForge.Api.Features.Designer;
using FormForge.Api.Features.Tenancy;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FormForge.Api.Features.Auth;

internal enum AuthLoginOutcome
{
    Success,
    InvalidCredentials,
    AccountInactive,
    MfaRequired, // Story 2.14: MFA challenge required — no JWT issued at this step.
}

internal sealed record AuthServiceResult(
    AuthLoginOutcome Outcome,
    LoginResponse? Response = null,
    string? MfaSessionToken = null);

internal enum AuthRefreshOutcome
{
    Success,
    NotFound,
    Replayed,
    Expired,
    AccountInactive,
}

internal sealed record AuthRefreshResult(AuthRefreshOutcome Outcome, LoginResponse? Response = null);

internal enum AuthLogoutOutcome
{
    Revoked,
    NoOp,
}

internal sealed record AuthLogoutResult(AuthLogoutOutcome Outcome);

// Story 2.11 — password-reset initiation. UserNotFound is internal-only: the
// endpoint maps every outcome to the same HTTP 200 to prevent email enumeration.
internal enum PasswordResetInitiateOutcome
{
    Success,
    UserNotFound,
}

internal sealed record PasswordResetInitiateResult(PasswordResetInitiateOutcome Outcome, string? RawToken = null);

// Story 2.11 — password-reset completion outcomes.
internal enum PasswordResetOutcome
{
    Success,
    TokenInvalid,
    PasswordSameAsCurrent,
}

internal sealed record PasswordResetResult(PasswordResetOutcome Outcome);

// Story 2.12 — authenticated password change.
internal enum ChangePasswordOutcome
{
    Success,
    CurrentPasswordIncorrect,
    NewPasswordSameAsCurrent,
}

internal sealed record ChangePasswordResult(ChangePasswordOutcome Outcome);

internal interface IAuthService
{
    Task<AuthServiceResult> LoginAsync(string email, string password, CancellationToken ct);
    Task<AuthRefreshResult> RefreshAsync(string rawToken, CancellationToken ct);
    Task<AuthLogoutResult> LogoutAsync(string? rawToken, CancellationToken ct);
    Task<PasswordResetInitiateResult> InitiatePasswordResetAsync(string email, CancellationToken ct);
    Task<PasswordResetResult> ResetPasswordAsync(string rawToken, string newPassword, CancellationToken ct);
    Task<ChangePasswordResult> ChangePasswordAsync(
        Guid userId,
        string currentPassword,
        string newPassword,
        string? currentRefreshTokenRaw,
        CancellationToken ct);

    // Story 2.14 — finalize a login after the MFA second factor is verified.
    Task<AuthServiceResult> CompleteMfaLoginAsync(Guid userId, CancellationToken ct);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed partial class AuthService(
    FormForgeDbContext db,
    IPasswordHasher passwordHasher,
    IJwtTokenService jwtTokenService,
    IOptions<JwtOptions> jwtOptions,
    AuthMetrics metrics,
    ILogger<AuthService> logger,
    IMfaService mfaService,
    IConfiguration configuration,
    ITenantLookupCache lookupCache) : IAuthService
{
    // Refresh-token lifetime is configurable via Jwt:RefreshTokenTtlDays (default 7).
    private int RefreshTokenTtlDays => jwtOptions.Value.RefreshTokenTtlDays;

    // Dummy hash used when no user record exists, to keep Verify() runtime constant
    // (~250 ms) and prevent timing-based user enumeration. Generated once per process
    // from a cryptographically random password so the literal can never accidentally
    // match a real credential; lazy so the BCrypt cost is paid only when the auth
    // path actually fires.
    private static readonly Lazy<string> _dummyPasswordHash = new(() =>
        BCrypt.Net.BCrypt.HashPassword(
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            workFactor: 12));

    public async Task<AuthServiceResult> LoginAsync(string email, string password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(password);

        // Normalize email for lookup (stored lowercase; see DbContext mapping).
        // Trim first so a stray trailing space from a mobile keyboard does not
        // miss the unique-index match and surface as INVALID_CREDENTIALS.
        var normalizedEmail = email.Trim().ToLowerInvariant();

        // Story 12.4 — platform_admins is checked before tenant_user_index (a platform
        // admin is never also a tenant user, so lookup order has no behavioral effect
        // beyond being deliberate — see this story's Design Notes). A match is always
        // terminal: right password issues an access-token-only response (no refresh-token
        // row is ever written for this tier — the access-token-only decision), wrong
        // password returns InvalidCredentials via the same constant-time BCrypt compare
        // used everywhere else (no _dummyPasswordHash needed here — a match guarantees a
        // real hash to compare against, same as the tenant-schema-user-found branch in
        // LoginAgainstTenantSchemaAsync below). No match falls through unchanged to the
        // tenant_user_index check, so that branch's own dummy-hash guard keeps the overall
        // per-request BCrypt budget uniform across every outcome.
        var platformAdmin = await db.PlatformAdmins
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserEmail == normalizedEmail, ct)
            .ConfigureAwait(false);

        if (platformAdmin is not null)
        {
            if (!passwordHasher.Verify(password, platformAdmin.PasswordHash))
            {
                return new AuthServiceResult(AuthLoginOutcome.InvalidCredentials);
            }

            var accessToken = jwtTokenService.CreateAccessTokenForPlatformAdmin(platformAdmin.Id, platformAdmin.UserEmail);
            var ttlSeconds = jwtOptions.Value.AccessTokenTtlMinutes * 60;

            var response = new LoginResponse(
                AccessToken: accessToken,
                RefreshToken: null,
                ExpiresIn: ttlSeconds,
                User: new AuthenticatedUser(
                    UserId: platformAdmin.Id,
                    Email: platformAdmin.UserEmail,
                    DisplayName: platformAdmin.UserEmail,
                    ThemePreference: null,
                    Roles: ["platform-super-admin"]));

            return new AuthServiceResult(AuthLoginOutcome.Success, response);
        }

        // Story 12.3 — tenant_user_index is checked next. A match means this email
        // belongs to a tenant-schema user, so the credential check must run against
        // that tenant's own `users` table instead of public.users. This is additive,
        // not a hard cutover: no match falls through unchanged to the pre-12.3 path
        // below, which is exactly what keeps the ~40 existing integration tests that
        // seed users directly into public.users passing untouched.
        var indexEntry = await db.TenantUserIndex
            .AsNoTracking()
            .Include(t => t.Tenant)
            .FirstOrDefaultAsync(t => t.Email == normalizedEmail, ct)
            .ConfigureAwait(false);

        if (indexEntry is not null)
        {
            return await LoginAgainstTenantSchemaAsync(indexEntry, password, ct).ConfigureAwait(false);
        }

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct)
            .ConfigureAwait(false);

        // Constant-time guard: always invoke Verify(), even when no user is found,
        // to prevent timing-based account enumeration (~250 ms BCrypt cost).
        var hashToVerify = user?.PasswordHash ?? _dummyPasswordHash.Value;
        var passwordValid = passwordHasher.Verify(password, hashToVerify);

        if (user is null || !passwordValid)
        {
            return new AuthServiceResult(AuthLoginOutcome.InvalidCredentials);
        }

        if (!user.IsActive)
        {
            return new AuthServiceResult(AuthLoginOutcome.AccountInactive);
        }

        // Story 2.14 — MFA gate. If enabled, issue an opaque session token instead
        // of JWTs; the caller must complete POST /api/auth/mfa/verify to obtain tokens.
        if (user.MfaEnabled)
        {
            var mfaSessionToken = mfaService.CreateMfaSession(user.Id);
            return new AuthServiceResult(AuthLoginOutcome.MfaRequired, MfaSessionToken: mfaSessionToken);
        }

        return await IssueLoginTokensAsync(db, user, tenantId: null, ct).ConfigureAwait(false);
    }

    // Story 12.3 — the tenant-matched half of LoginAsync. Opens a dedicated,
    // schema-scoped FormForgeDbContext (SearchPath = the tenant's schema) using the
    // same pattern as Story 12.2's TenantProvisioningService/TenantOnboardingService,
    // since HasDefaultSchema can't vary per call. Re-validates schema_name via
    // SafeIdentifier before it's interpolated into the connection string's SearchPath
    // — same defense-in-depth posture as every other dynamic-schema call site.
    //
    // Deliberately does not gate on User.MfaEnabled: CompleteMfaLoginAsync (the
    // verify-completion half of the MFA flow) has no schema awareness yet and always
    // queries public.users, so wiring a tenant user into an MFA session it could never
    // complete would be worse than skipping the gate. Newly onboarded tenant admins
    // (Story 12.7) are seeded with MfaEnabled = false, so this has no observable effect
    // in this story's scope; extending MFA to tenant schemas is left to a later story.
    private async Task<AuthServiceResult> LoginAgainstTenantSchemaAsync(
        TenantUserIndexEntry indexEntry, string password, CancellationToken ct)
    {
        if (!SafeIdentifier.TryCreate(indexEntry.Tenant.SchemaName, out var safeSchemaName, out _))
        {
            // A corrupted schema_name must never be interpolated into a connection
            // string. Still pay the constant-time BCrypt cost so this branch can't be
            // distinguished from a normal invalid-credentials response by timing.
            passwordHasher.Verify(password, _dummyPasswordHash.Value);
            return new AuthServiceResult(AuthLoginOutcome.InvalidCredentials);
        }

        // Defense-in-depth, same posture as TenantContextMiddleware's later per-request
        // check: a Suspended/Provisioning tenant must never authenticate, even though its
        // schema still exists and its admin's credentials are still valid there. Same
        // constant-time-BCrypt treatment as the SafeIdentifier failure branch above so
        // this can't be distinguished from a normal invalid-credentials response by timing.
        if (!string.Equals(indexEntry.Tenant.Status, "Active", StringComparison.Ordinal))
        {
            passwordHasher.Verify(password, _dummyPasswordHash.Value);
            return new AuthServiceResult(AuthLoginOutcome.InvalidCredentials);
        }

        var baseConnectionString = configuration.GetConnectionString("formforge")
            ?? throw new InvalidOperationException("Connection string 'formforge' not configured.");

        var csb = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = safeSchemaName!.Value };
        var tenantConnection = new NpgsqlConnection(csb.ConnectionString);
        try
        {
            var options = new DbContextOptionsBuilder<FormForgeDbContext>().UseNpgsql(tenantConnection).Options;
            var tenantDb = new FormForgeDbContext(options);
            try
            {
                var user = await tenantDb.Users
                    .AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Email == indexEntry.Email, ct)
                    .ConfigureAwait(false);

                var hashToVerify = user?.PasswordHash ?? _dummyPasswordHash.Value;
                var passwordValid = passwordHasher.Verify(password, hashToVerify);

                if (user is null || !passwordValid)
                {
                    return new AuthServiceResult(AuthLoginOutcome.InvalidCredentials);
                }

                if (!user.IsActive)
                {
                    return new AuthServiceResult(AuthLoginOutcome.AccountInactive);
                }

                return await IssueLoginTokensAsync(tenantDb, user, indexEntry.TenantId, ct).ConfigureAwait(false);
            }
            finally
            {
                await tenantDb.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await tenantConnection.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Story 2.14 — issue the JWT + refresh-token pair. Extracted from LoginAsync so
    // the MFA-verify completion path (CompleteMfaLoginAsync) shares the exact logic.
    // Story 12.3 — takes the FormForgeDbContext to write the refresh token against
    // (the injected public-schema `db` for the legacy path, or a tenant-schema-scoped
    // instance for a tenant-matched login) plus the optional tenantId to embed in the
    // JWT claim.
    // Story 12.6 (Decision) — the refresh-token cookie value is now
    // "{tenantId}.{secret}" (tenantId "" for the legacy/platform-tenant-less path, so
    // the cookie value is ".{secret}"); the secret half is the exact same random value
    // hashed into RefreshTokens.TokenHash as before. This is what lets RefreshAsync/
    // LogoutAsync resolve which schema's refresh_tokens table to query directly from
    // the cookie, closing Story 12.3's accepted gap.
    private async Task<AuthServiceResult> IssueLoginTokensAsync(
        FormForgeDbContext context, User user, Guid? tenantId, CancellationToken ct)
    {
        var roleNames = await context.UserRoles
            .Where(ur => ur.UserId == user.Id)
            .Select(ur => ur.Role.Name)
            .ToArrayAsync(ct)
            .ConfigureAwait(false);

        var accessToken = jwtTokenService.CreateAccessToken(user, roleNames, tenantId);
        var (secret, tokenHash) = GenerateRefreshToken();

        var refreshToken = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = tokenHash,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(RefreshTokenTtlDays),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        context.RefreshTokens.Add(refreshToken);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);

        var ttlSeconds = jwtOptions.Value.AccessTokenTtlMinutes * 60;

        var response = new LoginResponse(
            AccessToken: accessToken,
            RefreshToken: BuildRefreshCookieValue(tenantId, secret),
            ExpiresIn: ttlSeconds,
            User: new AuthenticatedUser(
                UserId: user.Id,
                Email: user.Email,
                DisplayName: user.DisplayName,
                ThemePreference: user.ThemePreference,
                Roles: roleNames));

        return new AuthServiceResult(AuthLoginOutcome.Success, response);
    }

    // Story 2.14 — finalize an MFA-gated login after the second factor is verified.
    public async Task<AuthServiceResult> CompleteMfaLoginAsync(Guid userId, CancellationToken ct)
    {
        // Validity already confirmed by MFA verification; AsNoTracking for performance.
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            .ConfigureAwait(false);

        // Guard against a race where an admin deletes the user between session
        // creation and verify.
        if (user is null)
            return new AuthServiceResult(AuthLoginOutcome.InvalidCredentials);

        // Story 12.3 — CompleteMfaLoginAsync is only reachable from the legacy
        // public.users path today (see LoginAgainstTenantSchemaAsync's comment on why
        // tenant users never get an MFA session in this story), so this always uses
        // the injected public-schema `db` with no tenantId claim.
        return await IssueLoginTokensAsync(db, user, tenantId: null, ct).ConfigureAwait(false);
    }

    public async Task<AuthRefreshResult> RefreshAsync(string rawToken, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rawToken);

        var resolution = await ResolveRefreshCookieAsync(rawToken, ct).ConfigureAwait(false);
        if (!resolution.IsValid)
        {
            // Malformed prefix, unknown/non-Active tenant, or a legacy pre-migration
            // cookie (no "." at all) — all collapse to the same NotFound envelope as
            // an ordinary unmatched token (Story 12.6 I/O matrix).
            return new AuthRefreshResult(AuthRefreshOutcome.NotFound);
        }

        var effectiveDb = resolution.Scoped?.Db ?? db;
        try
        {
            var hash = HashToken(resolution.Secret);

            // Single round-trip: load token + user together.
            var token = await effectiveDb.RefreshTokens
                .Include(r => r.User)
                .FirstOrDefaultAsync(r => r.TokenHash == hash, ct)
                .ConfigureAwait(false);

            if (token is null)
            {
                return new AuthRefreshResult(AuthRefreshOutcome.NotFound);
            }

            if (token.RevokedAt is not null)
            {
                // Possible refresh-token theft / out-of-order client retry. Spec AC-2:
                // log Warning + record metric + return same envelope as generic-invalid.
                // No PII in the log — only the hash prefix and opaque UserId.
                RefreshTokenReplayDetected(logger, hash[..8], token.UserId);
                metrics.RecordReplayed();
                return new AuthRefreshResult(AuthRefreshOutcome.Replayed);
            }

            if (token.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                return new AuthRefreshResult(AuthRefreshOutcome.Expired);
            }

            if (!token.User.IsActive)
            {
                // Spec required: revoke immediately so the token cannot be reused if the
                // account is later reactivated. Guarded by the same ConcurrencyCheck as
                // the rotation path so we don't double-revoke under a race.
                token.RevokedAt = DateTimeOffset.UtcNow;
                try
                {
                    await effectiveDb.SaveChangesAsync(ct).ConfigureAwait(false);
                    metrics.RecordRevoked();
                }
                catch (DbUpdateConcurrencyException)
                {
                    // Another caller just revoked this row — treat as Replayed (same
                    // public envelope; differentiation lives only in this catch).
                    RefreshTokenConcurrencyConflict(logger, token.UserId);
                    metrics.RecordReplayed();
                    return new AuthRefreshResult(AuthRefreshOutcome.Replayed);
                }
                return new AuthRefreshResult(AuthRefreshOutcome.AccountInactive);
            }

            // Rotation: revoke old, issue new — atomic, plus optimistic concurrency
            // on RevokedAt so a parallel rotation of the same token loses cleanly.
            token.RevokedAt = DateTimeOffset.UtcNow;

            var (newSecret, newHash) = GenerateRefreshToken();
            var newToken = new RefreshToken
            {
                UserId = token.UserId,
                TokenHash = newHash,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(RefreshTokenTtlDays),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            effectiveDb.RefreshTokens.Add(newToken);

            try
            {
                await effectiveDb.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                // A concurrent rotation revoked this token first. The new row we tried
                // to insert was rolled back by the same SaveChanges. Surface as Replayed
                // so the client falls back to the login flow rather than retrying.
                RefreshTokenConcurrencyConflict(logger, token.UserId);
                metrics.RecordReplayed();
                return new AuthRefreshResult(AuthRefreshOutcome.Replayed);
            }

            metrics.RecordRevoked();
            metrics.RecordIssued();

            var roleNames = await effectiveDb.UserRoles
                .Where(ur => ur.UserId == token.UserId)
                .Select(ur => ur.Role.Name)
                .ToArrayAsync(ct)
                .ConfigureAwait(false);
            var accessToken = jwtTokenService.CreateAccessToken(token.User, roleNames, resolution.TenantId);
            var ttlSeconds = jwtOptions.Value.AccessTokenTtlMinutes * 60;

            var response = new LoginResponse(
                AccessToken: accessToken,
                RefreshToken: BuildRefreshCookieValue(resolution.TenantId, newSecret),
                ExpiresIn: ttlSeconds,
                User: new AuthenticatedUser(
                    UserId: token.User.Id,
                    Email: token.User.Email,
                    DisplayName: token.User.DisplayName,
                    ThemePreference: token.User.ThemePreference,
                    Roles: roleNames));

            return new AuthRefreshResult(AuthRefreshOutcome.Success, response);
        }
        finally
        {
            if (resolution.Scoped is not null)
            {
                await resolution.Scoped.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task<AuthLogoutResult> LogoutAsync(string? rawToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rawToken))
        {
            return new AuthLogoutResult(AuthLogoutOutcome.NoOp);
        }

        var resolution = await ResolveRefreshCookieAsync(rawToken, ct).ConfigureAwait(false);
        if (!resolution.IsValid)
        {
            // Same safe-no-op posture as an ordinary unmatched token — logout never
            // surfaces an error to the client (Story 12.6 I/O matrix).
            return new AuthLogoutResult(AuthLogoutOutcome.NoOp);
        }

        var effectiveDb = resolution.Scoped?.Db ?? db;
        try
        {
            var hash = HashToken(resolution.Secret);

            var token = await effectiveDb.RefreshTokens
                .FirstOrDefaultAsync(r => r.TokenHash == hash, ct)
                .ConfigureAwait(false);

            if (token is null)
            {
                return new AuthLogoutResult(AuthLogoutOutcome.NoOp);
            }

            if (token.RevokedAt is not null)
            {
                return new AuthLogoutResult(AuthLogoutOutcome.NoOp);
            }

            token.RevokedAt = DateTimeOffset.UtcNow;

            try
            {
                await effectiveDb.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                RefreshTokenConcurrencyConflict(logger, token.UserId);
                return new AuthLogoutResult(AuthLogoutOutcome.NoOp);
            }

            metrics.RecordRevoked();
            return new AuthLogoutResult(AuthLogoutOutcome.Revoked);
        }
        finally
        {
            if (resolution.Scoped is not null)
            {
                await resolution.Scoped.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // Story 12.6 (Decision) — parses the "{tenantId}.{secret}" refresh-token cookie
    // format and, for a non-empty tenantId prefix, resolves + validates that tenant
    // (same exists/Active check TenantContextMiddleware performs, backed by the same
    // ITenantLookupCache instance so the two stay consistent) and opens a schema-scoped
    // FormForgeDbContext to query that tenant's own refresh_tokens table — mirroring
    // LoginAgainstTenantSchemaAsync's connection-per-call pattern, since HasDefaultSchema
    // can't vary per call. IsValid is false for: no "." at all (a legacy pre-migration
    // cookie), an unparsable tenantId, an unknown tenant, a non-Active tenant, or a
    // corrupted schema_name — every one of these must be indistinguishable from an
    // ordinary unmatched token to the caller.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
        Justification = "Ownership of tenantConnection/tenantDb transfers to the caller via " +
            "RefreshCookieResolution.Scoped; RefreshAsync/LogoutAsync dispose both from their " +
            "finally block via TenantScopedDbContext.DisposeAsync.")]
    private async Task<RefreshCookieResolution> ResolveRefreshCookieAsync(string rawToken, CancellationToken ct)
    {
        var dotIndex = rawToken.IndexOf('.', StringComparison.Ordinal);
        if (dotIndex < 0)
        {
            return RefreshCookieResolution.Invalid;
        }

        var tenantIdPrefix = rawToken[..dotIndex];
        var secret = rawToken[(dotIndex + 1)..];

        if (tenantIdPrefix.Length == 0)
        {
            return new RefreshCookieResolution(IsValid: true, TenantId: null, Secret: secret, Scoped: null);
        }

        if (!Guid.TryParse(tenantIdPrefix, out var tenantId))
        {
            return RefreshCookieResolution.Invalid;
        }

        var entry = lookupCache.TryGet(tenantId);
        if (entry is null)
        {
            var tenant = await db.Tenants
                .AsNoTracking()
                .Where(t => t.Id == tenantId)
                .Select(t => new { t.SchemaName, t.Status })
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (tenant is null)
            {
                return RefreshCookieResolution.Invalid;
            }

            entry = new TenantLookupEntry(tenant.SchemaName, tenant.Status);
            lookupCache.Set(tenantId, entry);
        }

        if (!string.Equals(entry.Status, "Active", StringComparison.Ordinal))
        {
            return RefreshCookieResolution.Invalid;
        }

        if (!SafeIdentifier.TryCreate(entry.SchemaName, out var safeSchemaName, out _))
        {
            return RefreshCookieResolution.Invalid;
        }

        var baseConnectionString = configuration.GetConnectionString("formforge")
            ?? throw new InvalidOperationException("Connection string 'formforge' not configured.");
        var csb = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = safeSchemaName!.Value };
        var tenantConnection = new NpgsqlConnection(csb.ConnectionString);
        var options = new DbContextOptionsBuilder<FormForgeDbContext>().UseNpgsql(tenantConnection).Options;
        var tenantDb = new FormForgeDbContext(options);

        return new RefreshCookieResolution(
            IsValid: true, TenantId: tenantId, Secret: secret,
            Scoped: new TenantScopedDbContext(tenantConnection, tenantDb));
    }

    // Bundles a tenant-scoped connection + FormForgeDbContext so RefreshAsync/
    // LogoutAsync can dispose both, in the right order, from one finally block —
    // mirrors LoginAgainstTenantSchemaAsync's nested try/finally without duplicating it.
    private sealed class TenantScopedDbContext(NpgsqlConnection connection, FormForgeDbContext db) : IAsyncDisposable
    {
        public FormForgeDbContext Db { get; } = db;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed record RefreshCookieResolution(
        bool IsValid, Guid? TenantId, string Secret, TenantScopedDbContext? Scoped)
    {
        internal static readonly RefreshCookieResolution Invalid = new(false, null, string.Empty, null);
    }

    // Story 12.6 (Decision) — composes the refresh-token cookie value. tenantId is
    // null for the legacy/platform-tenant-less path, producing ".{secret}" (a leading
    // dot, never a bare secret) so every current-format cookie is unambiguously
    // splittable on the first '.'.
    private static string BuildRefreshCookieValue(Guid? tenantId, string secret) =>
        string.Create(CultureInfo.InvariantCulture, $"{tenantId}.{secret}");

    public async Task<PasswordResetInitiateResult> InitiatePasswordResetAsync(string email, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(email);

        // Same normalization as LoginAsync so a stray trailing space still matches
        // the unique-index lookup.
        var normalizedEmail = email.Trim().ToLowerInvariant();

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == normalizedEmail && u.IsActive, ct)
            .ConfigureAwait(false);

        // No row → caller still returns HTTP 200 (anti-enumeration). We simply skip
        // token creation and email dispatch; there is no secret-dependent branch on
        // the wire, so the absence of a constant-time BCrypt guard here is fine —
        // unlike login, this path never compares a password.
        if (user is null)
        {
            return new PasswordResetInitiateResult(PasswordResetInitiateOutcome.UserNotFound);
        }

        // Invalidate any prior unused tokens — requesting a new link cancels outstanding
        // tokens so old emails in the inbox stop working (one active token per user).
        await db.PasswordResetTokens
            .Where(t => t.UserId == user.Id && t.UsedAt == null)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        // 64-char uppercase hex raw token; only its SHA-256 hash is persisted.
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
    }

    public async Task<PasswordResetResult> ResetPasswordAsync(string rawToken, string newPassword, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rawToken);
        ArgumentNullException.ThrowIfNull(newPassword);

        var hash = HashToken(rawToken);

        // Load token + owning user in one round-trip; tracked so we can mutate both.
        var token = await db.PasswordResetTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct)
            .ConfigureAwait(false);

        // Invalid, expired, or already consumed → indistinguishable on the wire.
        if (token is null || token.ExpiresAt <= DateTimeOffset.UtcNow || token.UsedAt is not null)
        {
            return new PasswordResetResult(PasswordResetOutcome.TokenInvalid);
        }

        // AC-6: the new password must differ from the current one.
        if (passwordHasher.Verify(newPassword, token.User.PasswordHash))
        {
            return new PasswordResetResult(PasswordResetOutcome.PasswordSameAsCurrent);
        }

        // All three writes are in one explicit transaction so a failure cannot leave
        // sessions revoked with the password unchanged or the token un-stamped.
        // try/finally rather than await using to satisfy CA2007 on the disposal await.
        var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            // Atomic single-use stamp: WHERE used_at IS NULL ensures only one concurrent
            // request succeeds — 0 rows means another request consumed this token first.
            var stamped = await db.PasswordResetTokens
                .Where(t => t.Id == token.Id && t.UsedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, DateTimeOffset.UtcNow), ct)
                .ConfigureAwait(false);

            if (stamped == 0)
            {
                return new PasswordResetResult(PasswordResetOutcome.TokenInvalid);
            }

            var newPasswordHash = passwordHasher.Hash(newPassword);
            await db.Users
                .Where(u => u.Id == token.UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, newPasswordHash), ct)
                .ConfigureAwait(false);

            await db.RefreshTokens
                .Where(r => r.UserId == token.UserId && r.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, DateTimeOffset.UtcNow), ct)
                .ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return new PasswordResetResult(PasswordResetOutcome.Success);
        }
        finally
        {
            await tx.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<ChangePasswordResult> ChangePasswordAsync(
        Guid userId,
        string currentPassword,
        string newPassword,
        string? currentRefreshTokenRaw,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(currentPassword);
        ArgumentNullException.ThrowIfNull(newPassword);

        // Tracked (not AsNoTracking) — we mutate PasswordHash and UpdatedAt below,
        // so change tracking must be active for SaveChangesAsync to flush them.
        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            .ConfigureAwait(false);

        // Still-valid JWT references a deleted user (admin race) — treat as auth
        // invalid (same wire shape as a wrong current password).
        if (user is null)
        {
            return new ChangePasswordResult(ChangePasswordOutcome.CurrentPasswordIncorrect);
        }

        // Deactivated users retain valid JWTs until expiry but must not be allowed
        // to mutate their credentials (LoginAsync and RefreshAsync both gate on IsActive).
        if (!user.IsActive)
        {
            return new ChangePasswordResult(ChangePasswordOutcome.CurrentPasswordIncorrect);
        }

        if (!passwordHasher.Verify(currentPassword, user.PasswordHash))
        {
            return new ChangePasswordResult(ChangePasswordOutcome.CurrentPasswordIncorrect);
        }

        // AC-2: the new password must differ from the current one.
        if (passwordHasher.Verify(newPassword, user.PasswordHash))
        {
            return new ChangePasswordResult(ChangePasswordOutcome.NewPasswordSameAsCurrent);
        }

        user.PasswordHash = passwordHasher.Hash(newPassword);
        user.UpdatedAt = DateTimeOffset.UtcNow;

        // Revoke other active refresh tokens. If the caller supplied a refresh-token
        // cookie we can identify and preserve the current session; otherwise (no cookie,
        // API client, cookie cleared) we revoke all — same behaviour as ResetPasswordAsync.
        // Story 12.6 — currentRefreshTokenRaw is now the composed "{tenantId}.{secret}"
        // cookie value; RefreshTokens.TokenHash only ever hashes the secret half, so it
        // must be extracted the same way ResolveRefreshCookieAsync does before hashing,
        // or "preserve the current session" would never match any row and silently
        // degrade to "revoke all" for every caller.
        var currentSecret = ExtractRefreshTokenSecret(currentRefreshTokenRaw);
        var currentHash = currentSecret != null ? HashToken(currentSecret) : null;

        if (currentHash != null)
        {
            await db.RefreshTokens
                .Where(r => r.UserId == userId && r.RevokedAt == null && r.TokenHash != currentHash)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, DateTimeOffset.UtcNow), ct)
                .ConfigureAwait(false);
        }
        else
        {
            // No cookie present — cannot identify current session; revoke all.
            await db.RefreshTokens
                .Where(r => r.UserId == userId && r.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, DateTimeOffset.UtcNow), ct)
                .ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new ChangePasswordResult(ChangePasswordOutcome.Success);
    }

    // Story 12.6 — splits the composed "{tenantId}.{secret}" cookie value on its first
    // '.' and returns the secret half (what TokenHash actually hashes). A value with no
    // '.' at all (a legacy pre-migration cookie) is returned unchanged — see the call
    // site in ChangePasswordAsync for why that degrades safely rather than needing to
    // be rejected here. internal (not private) so AuthIntegrationTests can call it
    // directly instead of reimplementing the same split.
    internal static string? ExtractRefreshTokenSecret(string? rawCookieValue)
    {
        if (rawCookieValue is null)
        {
            return null;
        }

        var dotIndex = rawCookieValue.IndexOf('.', StringComparison.Ordinal);
        return dotIndex < 0 ? rawCookieValue : rawCookieValue[(dotIndex + 1)..];
    }

    private static (string RawToken, string TokenHash) GenerateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);

        // Base64URL (no padding) — the opaque token returned to the client.
        var raw = Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        return (raw, HashToken(raw));
    }

    private static string HashToken(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
            .ToLower(CultureInfo.InvariantCulture);

    [LoggerMessage(
        EventId = 2200,
        Level = LogLevel.Warning,
        Message = "Refresh token replay detected — possible theft. TokenHashPrefix={TokenHashPrefix} UserId={UserId}")]
    private static partial void RefreshTokenReplayDetected(ILogger logger, string tokenHashPrefix, Guid userId);

    [LoggerMessage(
        EventId = 2201,
        Level = LogLevel.Warning,
        Message = "Refresh token concurrency conflict — concurrent rotation lost. UserId={UserId}")]
    private static partial void RefreshTokenConcurrencyConflict(ILogger logger, Guid userId);
}
