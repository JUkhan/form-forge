namespace FormForge.Api.Domain.Entities;

// Story 12.4 (FR-74 / architecture.md §7.4) — platform_admins is the account store for
// the platform-super-admin tier: the operator-level account that can provision tenants
// (Story 12.5), never a tenant user. Lives in `public`, alongside tenants (Story 12.1)
// and tenant_user_index (Story 12.3) — every tenant-scoped table lives in its own
// schema instead (Decision 7.7). No MFA columns (MFA is not gated on this login path)
// and no roles/permissions rows: the JWT issued for this account carries a single
// hardcoded "platform-super-admin" role claim, not an RBAC lookup.
internal sealed class PlatformAdmin
{
    public Guid Id { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
