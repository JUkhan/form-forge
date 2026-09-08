namespace FormForge.Api.Domain.Entities;

// Story 12.3 (FR-74 / architecture.md §7.3 Decision) — public-schema email->tenant
// routing table. This is the one piece of user-identifying data that must stay
// global now that `users` lives per-tenant: it stores no credentials, only the
// pointer LoginAsync needs to find the right tenant schema before running the
// credential check. Populated by TenantOnboardingService's first-user seed
// (Story 12.7); maintained by every future user-creation call site in later stories.
internal sealed class TenantUserIndexEntry
{
    public string Email { get; set; } = string.Empty; // stored lowercase-normalized; PK
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
}
