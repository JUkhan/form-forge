namespace FormForge.Api.Features.Permissions;

internal readonly record struct CrudFlags(bool CanCreate, bool CanRead, bool CanUpdate, bool CanDelete, bool CanExport);

internal sealed record EffectivePermissions(
    Guid UserId,
    DateTimeOffset ComputedAt,
    bool IsActive,
    IReadOnlyDictionary<string, CrudFlags> PerResource,
    IReadOnlySet<Guid> RoleIds,
    bool CanManageDatasets);
