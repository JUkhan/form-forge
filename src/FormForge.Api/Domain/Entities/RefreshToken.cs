using System.ComponentModel.DataAnnotations;

namespace FormForge.Api.Domain.Entities;

internal sealed class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string TokenHash { get; set; } = string.Empty; // SHA-256 hex of the opaque token
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // ConcurrencyCheck closes the rotation race: EF Core appends the original
    // RevokedAt value to UPDATE WHERE clauses, so two parallel rotations of
    // the same token cannot both succeed — the loser sees DbUpdateConcurrencyException.
    [ConcurrencyCheck]
    public DateTimeOffset? RevokedAt { get; set; }
}
