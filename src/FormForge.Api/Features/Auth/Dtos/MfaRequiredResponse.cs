namespace FormForge.Api.Features.Auth.Dtos;

// Story 2.14 — returned by POST /api/auth/login when the account has MFA enabled.
// Carries no JWT: the caller must complete POST /api/auth/mfa/verify with the
// opaque session token to obtain tokens.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Instantiated via System.Text.Json serialization / DI.")]
internal sealed record MfaRequiredResponse(bool MfaRequired, string MfaSessionToken);
