namespace FormForge.Api.Features.Menus.Dtos;

// Story 5.2 — PUT /api/admin/menus/{id}/binding payload. Both fields are nullable
// on the positional record so a missing/null JSON key deserialises to null and
// the validator's NotNull rules return 422 instead of the handler NRE-ing.
// Mirrors AssignMenuRolesRequest / ToggleMenuActiveRequest in this feature.
internal sealed record BindMenuDesignerRequest(string? DesignerId, int? Version);
