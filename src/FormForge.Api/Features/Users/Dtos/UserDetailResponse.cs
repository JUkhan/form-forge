namespace FormForge.Api.Features.Users.Dtos;

internal sealed record UserRoleItem(Guid Id, string Name);

internal sealed record UserDetailResponse(
    Guid Id,
    string Email,
    string DisplayName,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<UserRoleItem> Roles,
    bool MfaEnabled); // Story 2.15
