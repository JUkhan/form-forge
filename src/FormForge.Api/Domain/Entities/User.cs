namespace FormForge.Api.Domain.Entities;

internal sealed class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;       // stored lowercase-normalized
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty; // BCrypt hash
    public bool IsActive { get; set; } = true;
    public string? ThemePreference { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
    public ICollection<UserRole> UserRoles { get; set; } = [];

    // Story 2.13 — TOTP MFA enrolment. MfaSecretProtected holds the IDataProtector-
    // encrypted base32 secret (purpose "mfa-totp-secret"); never the raw secret.
    public bool MfaEnabled { get; set; }
    public byte[]? MfaSecretProtected { get; set; }
    public ICollection<MfaBackupCode> BackupCodes { get; set; } = [];
}
