namespace FormForge.Api.Features.Roles.Dtos;

internal sealed record RoleListItem(
    Guid Id,
    string Name,
    string? Description,
    int PermissionCount,
    bool IsSystem);
