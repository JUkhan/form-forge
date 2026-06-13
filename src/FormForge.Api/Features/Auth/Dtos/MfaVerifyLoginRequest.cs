namespace FormForge.Api.Features.Auth.Dtos;

// Story 2.14 — request body for POST /api/auth/mfa/verify. Code is either a
// 6-digit TOTP or an 8-char alphanumeric backup code.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Instantiated via System.Text.Json deserialization / DI.")]
internal sealed record MfaVerifyLoginRequest(string MfaSessionToken, string Code);
