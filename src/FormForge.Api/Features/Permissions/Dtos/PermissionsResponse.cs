namespace FormForge.Api.Features.Permissions.Dtos;

internal sealed record CrudFlagsResponse(bool CanCreate, bool CanRead, bool CanUpdate, bool CanDelete, bool CanExport);

internal sealed record PermissionsResponse(
    Guid UserId,
    DateTimeOffset ComputedAt,
    bool IsActive,
    IReadOnlyDictionary<string, CrudFlagsResponse> PerResource,
    IReadOnlySet<Guid> RoleIds,
    bool CanManageDatasets);
