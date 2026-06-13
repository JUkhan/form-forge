namespace FormForge.Api.Features.Menus.Dtos;

// Nullable IsActive: a missing "isActive" key in the payload deserializes to null
// on this positional record, and the validator's NotNull rule then returns 422
// instead of silently treating absence as false. Mirrors
// AssignMenuRolesRequest's nullable-envelope pattern.
internal sealed record ToggleMenuActiveRequest(bool? IsActive);
