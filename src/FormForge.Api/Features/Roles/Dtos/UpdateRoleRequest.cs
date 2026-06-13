namespace FormForge.Api.Features.Roles.Dtos;

internal sealed record UpdateRoleRequest(
    string Name,
    string? Description,
    IReadOnlyList<PermissionRecord> Permissions,
    bool? CanManageDatasets);
