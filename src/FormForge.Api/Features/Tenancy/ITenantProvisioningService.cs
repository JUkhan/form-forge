using FormForge.Api.Domain.Entities;

namespace FormForge.Api.Features.Tenancy;

// Story 12.2 (FR-74 / Decision 7.7) — turns a validated Tenant row (Story 12.1) into
// a real, isolated PostgreSQL schema containing the full static-schema migration set.
// Narrow contract for this story's scope only: no seeding, no status transition, no
// HTTP surface. Errors (invalid/colliding schema_name, migration failure) surface as
// exceptions — there is no Result wrapper because the caller (Story 12.5) has not been
// built yet to define what a friendly envelope should look like.
internal interface ITenantProvisioningService
{
    Task ProvisionSchemaAsync(Tenant tenant, CancellationToken ct);
}
