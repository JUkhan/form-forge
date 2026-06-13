namespace FormForge.Api.Domain.Entities;

// Story 2.11 (AR-54) — single-use, time-boxed token backing the self-service
// password-reset flow. Only the SHA-256 hash of the raw token is stored; the
// raw 64-char hex token is emailed to the user and never persisted. A row is
// consumed by stamping UsedAt, so a token can be redeemed at most once.
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
