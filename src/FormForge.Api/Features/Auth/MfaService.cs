using System.Collections.Concurrent;
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
    Task<bool?> GetMfaStatusAsync(Guid userId, CancellationToken ct);

    // Story 2.14 — login-time MFA challenge.
    string CreateMfaSession(Guid userId);
    Task<MfaLoginVerifyResult> VerifyMfaLoginAsync(string mfaSessionToken, string code, CancellationToken ct);
}

internal enum MfaVerifyEnrolmentOutcome { Success, InvalidCode, NoPendingEnrolment }

internal sealed record MfaVerifyEnrolmentResult(MfaVerifyEnrolmentOutcome Outcome);

// Story 2.14 — login-time MFA verification outcomes.
internal enum MfaLoginVerifyOutcome { Success, InvalidCode, SessionInvalid }

internal sealed record MfaLoginVerifyResult(MfaLoginVerifyOutcome Outcome, Guid? UserId = null);

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

    // Story 2.14 — login-time MFA session (post-password, pre-JWT).
    private const string MfaSessionPrefix = "mfa:session:";
    private const int MaxLoginFailAttempts = 5;
    private static readonly TimeSpan MfaSessionLifetime = TimeSpan.FromMinutes(5);
    private static string SessionKey(string token) => $"{MfaSessionPrefix}{token}";

    private sealed record MfaLoginSession(Guid UserId, DateTimeOffset IssuedAt, int FailCount = 0);

    // Story 2.14 (review) — per-session-token async lock. Serializes verification of a
    // single token so the read→verify→consume sequence is atomic: two concurrent requests
    // bearing the same token + same valid TOTP cannot both pass and both mint a JWT pair
    // from a single-use session. (Backup codes are additionally guarded at the DB layer;
    // the TOTP path has no DB row to gate on, so this lock is its only protection.)
    // Static so it is shared across the scoped service's per-request instances.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SessionGates =
        new(StringComparer.Ordinal);

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

        var otpAuthUri = $"otpauth://totp/FormForge:{Uri.EscapeDataString(email)}?secret={base32Secret}&issuer=FormForge";
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
        {
            cache.Remove(PendingKey(userId));
            return new MfaVerifyEnrolmentResult(MfaVerifyEnrolmentOutcome.InvalidCode);
        }

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

    public async Task<bool?> GetMfaStatusAsync(Guid userId, CancellationToken ct)
    {
        return await db.Users
            .Where(u => u.Id == userId)
            .Select(u => (bool?)u.MfaEnabled)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    // Story 2.14 — issue an opaque single-use session token after the password
    // step succeeds. 32-char hex, 5-minute absolute TTL, stored in IMemoryCache.
    public string CreateMfaSession(Guid userId)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        cache.Set(
            SessionKey(token),
            new MfaLoginSession(userId, DateTimeOffset.UtcNow),
            SessionCacheOptions(MfaSessionLifetime));
        return token;
    }

    // Build the cache-entry options for an MFA login session. The post-eviction callback
    // removes the per-token serialization gate when the session leaves the cache —
    // including when it expires naturally (user abandons MFA before completing it), the
    // one path the finally-block cleanup in VerifyMfaLoginAsync cannot reach. Both
    // CreateMfaSession and the wrong-code re-Set MUST go through this: the bare TimeSpan
    // overload of cache.Set drops the callback, orphaning the gate for abandoned sessions.
    private static MemoryCacheEntryOptions SessionCacheOptions(TimeSpan ttl)
    {
        var options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl,
        };
        options.RegisterPostEvictionCallback(static (key, _, _, _) =>
        {
            var tok = ((string)key)[MfaSessionPrefix.Length..];
            SessionGates.TryRemove(tok, out _);
        });
        return options;
    }

    // Story 2.14 — verify a TOTP or backup code against an active MFA session.
    // Single-use on success; preserves the session on wrong codes until the 5th
    // consecutive failure, then evicts to force a restart from the password step.
    //
    // The per-token gate serializes concurrent verifies of the same session so the
    // read→verify→consume cycle is atomic (see SessionGates).
    public async Task<MfaLoginVerifyResult> VerifyMfaLoginAsync(
        string mfaSessionToken, string code, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mfaSessionToken);
        ArgumentNullException.ThrowIfNull(code);

        var gate = SessionGates.GetOrAdd(mfaSessionToken, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await VerifyMfaLoginCoreAsync(mfaSessionToken, code, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
            // Drop the gate once the session is gone (consumed or evicted) to bound memory.
            // Not disposed: a racing caller may already hold this same instance from GetOrAdd,
            // and we never touch AvailableWaitHandle, so the GC reclaims it safely. Removing it
            // is safe because any racing request now sees no session → SessionInvalid anyway.
            if (!cache.TryGetValue(SessionKey(mfaSessionToken), out _))
            {
                SessionGates.TryRemove(mfaSessionToken, out _);
            }
        }
    }

    private async Task<MfaLoginVerifyResult> VerifyMfaLoginCoreAsync(
        string mfaSessionToken, string code, CancellationToken ct)
    {
        if (!cache.TryGetValue(SessionKey(mfaSessionToken), out MfaLoginSession? session) || session is null)
            return new MfaLoginVerifyResult(MfaLoginVerifyOutcome.SessionInvalid);

        // The code shape decides which factor we verify, and that decides what we load:
        // the backup-code branch needs the codes eagerly, but the common TOTP path never
        // touches them — so skip the join unless we're verifying a backup code.
        var isTotpShape = code.Length == 6 && code.All(char.IsAsciiDigit);

        // AsNoTracking — a matched backup code is stamped via ExecuteUpdateAsync (a direct
        // UPDATE, not through the change tracker), and the TOTP path only reads the user.
        IQueryable<User> usersQuery = db.Users.AsNoTracking();
        if (!isTotpShape)
            usersQuery = usersQuery.Include(u => u.BackupCodes);

        var user = await usersQuery
            .FirstOrDefaultAsync(u => u.Id == session.UserId, ct)
            .ConfigureAwait(false);

        if (user is null || !user.MfaEnabled || user.MfaSecretProtected is null)
        {
            cache.Remove(SessionKey(mfaSessionToken));
            return new MfaLoginVerifyResult(MfaLoginVerifyOutcome.SessionInvalid);
        }

        var verified = false;

        // TOTP path: exactly 6 ASCII digits. Do NOT normalize — TOTP codes are pure digits.
        if (isTotpShape)
        {
            var base32Secret = Encoding.UTF8.GetString(_protector.Unprotect(user.MfaSecretProtected));
            var totp = new Totp(Base32Encoding.ToBytes(base32Secret));
            verified = totp.VerifyTotp(code, out _, new VerificationWindow(1, 1));
        }
        else
        {
            // Backup-code path: 8 alphanumeric chars. Normalize to uppercase so a
            // lowercase-typed code still matches the stored uppercase code's hash.
            var normalizedCode = code.Trim().ToUpperInvariant();
            var matchingCode = user.BackupCodes
                .FirstOrDefault(c => c.UsedAt is null && passwordHasher.Verify(normalizedCode, c.CodeHash));

            if (matchingCode is not null)
            {
                // Atomic single-use stamp: WHERE used_at IS NULL ensures only one concurrent
                // request consumes this code — 0 rows means another request stamped it first.
                // Mirrors AuthService.ResetPasswordAsync's single-use guard. The per-token gate
                // protects the same-session case; this guards the same-code-across-sessions case.
                var stamped = await db.MfaBackupCodes
                    .Where(c => c.Id == matchingCode.Id && c.UsedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, DateTimeOffset.UtcNow), ct)
                    .ConfigureAwait(false);

                verified = stamped > 0;
            }
        }

        if (verified)
        {
            cache.Remove(SessionKey(mfaSessionToken));
            return new MfaLoginVerifyResult(MfaLoginVerifyOutcome.Success, user.Id);
        }

        // Track consecutive failures. On the 5th, evict → force password re-entry.
        var newFailCount = session.FailCount + 1;
        if (newFailCount >= MaxLoginFailAttempts)
        {
            cache.Remove(SessionKey(mfaSessionToken));
        }
        else
        {
            // Re-Set with the *remaining* lifetime computed from IssuedAt, not a fresh 5 min.
            // A wrong guess must not slide the absolute cap — otherwise a client failing once
            // every <5 min could keep the post-password/pre-JWT session alive indefinitely.
            var remaining = session.IssuedAt + MfaSessionLifetime - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                cache.Remove(SessionKey(mfaSessionToken));
            }
            else
            {
                cache.Set(
                    SessionKey(mfaSessionToken),
                    session with { FailCount = newFailCount },
                    SessionCacheOptions(remaining));
            }
        }

        return new MfaLoginVerifyResult(MfaLoginVerifyOutcome.InvalidCode);
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
