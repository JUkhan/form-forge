using FormForge.Api.Common.Endpoints;
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
        // Per-tab role gating, enforced server-side (the parent /api/admin group only
        // requires authentication):
        //   platform-admin : Users, Roles, Menus, Audit Logs
        //   platform-dev   : Roles, Menus, Datasets, Constraints, Table Provisioning, Library
        group.MapGroup("/roles").RequireAuthorization(AuthPolicies.PlatformAdminOrDev)
             .WithTags("Admin — Roles").MapRoleEndpoints();
        group.MapGroup("/users").RequireAuthorization(AuthPolicies.PlatformAdmin)
             .WithTags("Admin — Users").MapUserAdminEndpoints();
        group.MapGroup("/menus").RequireAuthorization(AuthPolicies.PlatformAdminOrDev)
             .WithTags("Admin — Menus").MapMenuAdminEndpoints();
        // Story 5.6 — admin drift view: inspect + drop orphaned columns on provisioned tables.
        // Policies are applied per endpoint inside (schema audit is shared, the rest is dev-only).
        group.MapGroup("/designers").RequireAuthorization(AuthPolicies.PlatformAdminOrDev)
             .WithTags("Admin — Designers").MapDesignerAdminEndpoints();
        // "Table Provisioned" tab — provision a CRUD designer's table without a menu binding.
        group.MapGroup("/table-provisioning").RequireAuthorization(AuthPolicies.PlatformDev)
             .WithTags("Admin — Table Provisioning").MapTableProvisioningEndpoints();
        // Story 6.8 — mutation audit log for provisioned dynamic tables (Audit Logs tab).
        group.MapGroup("/data").RequireAuthorization(AuthPolicies.PlatformAdmin)
             .WithTags("Admin — Data").MapDataAdminEndpoints();
        // Story 8.9 — dataset audit log (Datasets tab).
        group.MapGroup("/datasets").RequireAuthorization(AuthPolicies.PlatformDev)
             .WithTags("Admin — Datasets").MapDatasetAdminEndpoints();
        return group;
    }
}
