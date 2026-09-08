using FormForge.Api.Domain.Entities;

namespace FormForge.Api.Features.Tenancy;

// Story 12.7 (FR-75 / Decision 7.7) — the second, independently callable step that
// finishes what Story 12.2's ITenantProvisioningService deliberately stopped short of.
// Precondition: the given Tenant's schema already exists and has the full static
// migration set replayed into it (Story 12.2 complete) — this service never re-runs
// CREATE SCHEMA or the migration replay. Narrow contract: caller supplies the first
// user's identity directly (no server-side password generation exists in this
// codebase), same as CreateUserRequest requires today. No HTTP surface — Story 12.5
// consumes both this service and Story 12.2's later.
internal interface ITenantOnboardingService
{
    Task OnboardTenantAsync(
        Tenant tenant,
        string adminEmail,
        string adminDisplayName,
        string adminTemporaryPassword,
        CancellationToken ct);
}
