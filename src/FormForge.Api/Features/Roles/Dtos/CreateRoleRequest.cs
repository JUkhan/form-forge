namespace FormForge.Api.Features.Roles.Dtos;

internal sealed record CreateRoleRequest(
    string Name,
    string? Description,
    IReadOnlyList<PermissionRecord> Permissions);
