using FormForge.Api.Features.Datasets;
using FormForge.Api.Features.Designer;
using FormForge.Api.Features.DynamicCrud;
using FormForge.Api.Features.Menus;
using FormForge.Api.Features.Provisioning;
using FormForge.Api.Features.Users;

namespace FormForge.Api.Features.Roles;

// Top-level admin dispatcher. Future stories (2.8 user CRUD, 2.9 role-management UI)
// add MapXxxEndpoints() calls here rather than introducing parallel top-level
// /api/admin route groups in Program.cs.
internal static class AdminEndpoints
{
    internal static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapGroup("/roles").WithTags("Admin — Roles").MapRoleEndpoints();
        group.MapGroup("/users").WithTags("Admin — Users").MapUserAdminEndpoints();
        group.MapGroup("/menus").WithTags("Admin — Menus").MapMenuAdminEndpoints();
        // Story 5.6 — admin drift view: inspect + drop orphaned columns on provisioned tables.
        group.MapGroup("/designers").WithTags("Admin — Designers").MapDesignerAdminEndpoints();
        // "Table Provisioned" tab — provision a CRUD designer's table without a menu binding.
        group.MapGroup("/table-provisioning").WithTags("Admin — Table Provisioning").MapTableProvisioningEndpoints();
        // Story 6.8 — mutation audit log for provisioned dynamic tables.
        group.MapGroup("/data").WithTags("Admin — Data").MapDataAdminEndpoints();
        // Story 8.9 — dataset audit log (platform-admin only, inherits group policy).
        group.MapGroup("/datasets").WithTags("Admin — Datasets").MapDatasetAdminEndpoints();
        return group;
    }
}
