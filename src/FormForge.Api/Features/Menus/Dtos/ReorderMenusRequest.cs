namespace FormForge.Api.Features.Menus.Dtos;

// Nullable Items: a missing "items" key in the payload deserializes to null on
// this positional record, and the validator's NotNull rule then returns 422
// instead of the handler NRE-ing on request.Items!.Count. Mirrors
// AssignMenuRolesRequest (Story 4.4).
//
// Envelope shape ({ items: [...] }) instead of a top-level JSON array because
// (a) FluentValidation in this codebase targets a single named DTO class per
// filter, (b) every other admin mutation uses an envelope, and (c) it leaves
// room to add cause/version/optimistic fields later without a breaking change.
internal sealed record ReorderMenusRequest(IReadOnlyList<ReorderMenuItem>? Items);

internal sealed record ReorderMenuItem(Guid Id, int Order);
