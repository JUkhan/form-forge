namespace FormForge.Api.Features.Auth.Dtos;

internal sealed record AuthenticatedUser(
    Guid UserId,
    string Email,
    string DisplayName,
    string? ThemePreference,
    IReadOnlyList<string> Roles);

internal sealed record LoginResponse(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn,
    AuthenticatedUser User);
