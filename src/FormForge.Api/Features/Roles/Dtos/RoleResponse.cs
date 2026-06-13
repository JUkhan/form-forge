namespace FormForge.Api.Features.Roles.Dtos;

internal sealed record RoleResponse(
    Guid Id,
    string Name,
    string? Description,
    bool IsSystem,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<PermissionRecord> Permissions,
    bool CanManageDatasets);
