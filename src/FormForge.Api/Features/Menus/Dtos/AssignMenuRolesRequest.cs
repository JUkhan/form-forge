namespace FormForge.Api.Features.Menus.Dtos;

// Nullable RoleIds: a missing "roleIds" key in the payload deserializes to null
// on this positional record, and the validator's NotNull rule then returns 422
// instead of the handler NRE-ing on request.RoleIds.Distinct(). Mirrors
// AssignRolesRequest in the Users feature.
internal sealed record AssignMenuRolesRequest(IReadOnlyList<Guid>? RoleIds);
