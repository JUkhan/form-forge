namespace FormForge.Api.Features.Users.Dtos;

// Nullable RoleIds: positional record with an omitted "roleIds" key deserializes to
// null. The validator's NotNull() rule then yields a structured 422, instead of
// the handler throwing NRE on `request.RoleIds.Distinct()`.
internal sealed record AssignRolesRequest(IReadOnlyList<Guid>? RoleIds);
