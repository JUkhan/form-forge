namespace FormForge.Api.Domain.Entities;

// Story 12.1 (FR-74 / architecture.md Decision 7.1) — tenants is the one table that
// stays in the `public` schema, alongside platform_admins (Story 12.4) and
// tenant_user_index (Story 12.3). Every other table (users, roles, menus,
// component_schemas, runtime-provisioned tables, the datasets VIEW namespace) is
// provisioned into each tenant's own schema instead (Decision 7.7).
internal sealed class Tenant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SchemaName { get; set; } = string.Empty;
    public string Status { get; set; } = "Provisioning";
    public DateTimeOffset CreatedAt { get; set; }

    // No FK yet: platform_admins (Story 12.4) does not exist until a later story in
    // this epic. Story 12.4 adds the constraint once the referenced table is real.
    public Guid? CreatedBy { get; set; }
}
