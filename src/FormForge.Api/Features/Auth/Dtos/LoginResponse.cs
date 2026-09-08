namespace FormForge.Api.Features.Auth.Dtos;

internal sealed record AuthenticatedUser(
    Guid UserId,
    string Email,
    string DisplayName,
    string? ThemePreference,
    IReadOnlyList<string> Roles);

// Story 12.4 — RefreshToken is null for a platform-super-admin login (access-token-only
// decision: that tier's session is exactly AccessTokenTtlMinutes, no refresh-token row is
// ever written). Every other caller (legacy public.users, tenant-schema users, MFA
// completion, refresh rotation) continues to populate a non-null value.
internal sealed record LoginResponse(
    string AccessToken,
    string? RefreshToken,
    int ExpiresIn,
    AuthenticatedUser User);
