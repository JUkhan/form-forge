namespace FormForge.Api.Features.Roles.Dtos;

internal sealed record PermissionRecord(
    string ResourceId,
    bool CanCreate,
    bool CanRead,
    bool CanUpdate,
    bool CanDelete,
    bool CanExport);
