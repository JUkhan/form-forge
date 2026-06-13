namespace FormForge.Api.Features.Auth;

internal sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string SigningKey { get; set; } = string.Empty; // mandatory; env var in prod, user-secrets in dev
    public string Issuer { get; set; } = "FormForge";
    public string Audience { get; set; } = "FormForge";
    public int AccessTokenTtlMinutes { get; set; } = 15;
    public int RefreshTokenTtlDays { get; set; } = 7;
}
