namespace FormForge.Api.Features.Auth.Dtos;

// Story 2.11 — body for POST /api/auth/reset-password. Token is the raw 64-char
// hex value delivered by email; NewPassword is the replacement plaintext.
internal sealed record ResetPasswordRequest(string Token, string NewPassword);
