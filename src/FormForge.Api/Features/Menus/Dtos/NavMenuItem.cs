using System.Text.Json;

namespace FormForge.Api.Features.Menus.Dtos;

// Story 4.7 — public navbar tree node. Distinct from MenuListItem (admin paginated
// flat) and MenuResponse (admin single-item with allowedRoleIds + isActive). Sub-menus
// nest inline under their parent; leaves have Children: []. Icon is JsonElement? for the
// same reason MenuResponse.Icon is — Menu.Icon is stored as a JSON string in the DB.
// DesignerId surfaces the menu→designer binding (Story 5.2) so the public
// navbar can route a click on a bound menu to /data/{designerId}. null for
// section headers and unbound menus — the SPA renders these as plain
// non-link containers (the disclosure chevron still expands sub-menus).
// RoutePath is the custom-route alternative to DesignerId (mutually exclusive): when
// set the SPA links the menu item straight to that path (internal "/…" or external
// "https://…") instead of /data/{designerId}. null when the menu uses a binding or is
// an unbound section header.
internal sealed record NavMenuItem(
    Guid Id,
    string Name,
    int Order,
    JsonElement? Icon,
    Guid? ParentId,
    string? DesignerId,
    string? RoutePath,
    IReadOnlyList<NavMenuItem> Children);
