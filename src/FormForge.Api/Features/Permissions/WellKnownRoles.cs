namespace FormForge.Api.Features.Permissions;

// Story 4.7 — shared canonical role GUIDs. PermissionService keeps its own private
// copy to preserve the historic encapsulation; other features (MenuService, etc.)
// reference these. Tests use the same literal "00000000-0000-0000-0000-000000000001".
internal static class WellKnownRoles
{
    public static readonly Guid PlatformAdminId = new("00000000-0000-0000-0000-000000000001");

    // Hidden cross-tenant developer role, seeded by the SeedPlatformDevRole migration.
    // Every tenant-facing Users/Roles/Menus surface filters this id (and its holders) out;
    // it can never be assigned through the self-service role/menu assignment endpoints.
    public static readonly Guid PlatformDevId = new("00000000-0000-0000-0000-000000000003");

    public const string PlatformDevName = "platform-dev";
}
