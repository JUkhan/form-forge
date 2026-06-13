using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Features.Auth.Dtos;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    IMfaService mfaService) : IAuthService
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

        return await IssueLoginTokensAsync(user, ct).ConfigureAwait(false);
    }

    // Story 2.14 — issue the JWT + refresh-token pair. Extracted from LoginAsync so
    // the MFA-verify completion path (CompleteMfaLoginAsync) shares the exact logic.
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

        return await IssueLoginTokensAsync(user, ct).ConfigureAwait(false);
    }

    public async Task<AuthRefreshResult> RefreshAsync(string rawToken, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rawToken);

        var hash = HashToken(rawToken);

        // Single round-trip: load token + user together.
        var token = await db.RefreshTokens
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
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
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

        var (newRaw, newHash) = GenerateRefreshToken();
        var newToken = new RefreshToken
        {
            UserId = token.UserId,
            TokenHash = newHash,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(RefreshTokenTtlDays),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.RefreshTokens.Add(newToken);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
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

        var roleNames = await db.UserRoles
            .Where(ur => ur.UserId == token.UserId)
            .Select(ur => ur.Role.Name)
            .ToArrayAsync(ct)
            .ConfigureAwait(false);
        var accessToken = jwtTokenService.CreateAccessToken(token.User, roleNames);
        var ttlSeconds = jwtOptions.Value.AccessTokenTtlMinutes * 60;

        var response = new LoginResponse(
            AccessToken: accessToken,
            RefreshToken: newRaw,
            ExpiresIn: ttlSeconds,
            User: new AuthenticatedUser(
                UserId: token.User.Id,
                Email: token.User.Email,
                DisplayName: token.User.DisplayName,
                ThemePreference: token.User.ThemePreference,
                Roles: roleNames));

        return new AuthRefreshResult(AuthRefreshOutcome.Success, response);
    }

    public async Task<AuthLogoutResult> LogoutAsync(string? rawToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rawToken))
        {
            return new AuthLogoutResult(AuthLogoutOutcome.NoOp);
        }

        var hash = HashToken(rawToken);

        var token = await db.RefreshTokens
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
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            RefreshTokenConcurrencyConflict(logger, token.UserId);
            return new AuthLogoutResult(AuthLogoutOutcome.NoOp);
        }

        metrics.RecordRevoked();
        return new AuthLogoutResult(AuthLogoutOutcome.Revoked);
    }

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
        var currentHash = currentRefreshTokenRaw != null ? HashToken(currentRefreshTokenRaw) : null;

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
