namespace FormForge.Api.Features.Users.Dtos;

internal sealed record UserListItem(
    Guid Id,
    string Email,
    string DisplayName,
    bool IsActive,
    int RoleCount,
    DateTimeOffset CreatedAt);
