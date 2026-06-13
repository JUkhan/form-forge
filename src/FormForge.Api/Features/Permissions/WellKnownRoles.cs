namespace FormForge.Api.Features.Permissions;

// Story 4.7 — shared canonical role GUIDs. PermissionService keeps its own private
// copy to preserve the historic encapsulation; other features (MenuService, etc.)
// reference these. Tests use the same literal "00000000-0000-0000-0000-000000000001".
internal static class WellKnownRoles
{
    public static readonly Guid PlatformAdminId = new("00000000-0000-0000-0000-000000000001");
}
