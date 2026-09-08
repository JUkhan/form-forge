namespace FormForge.Api.Features.Tenancy;

// Story 12.3 (FR-74 / architecture.md §7.3) — per-request resolved tenant, set by
// TenantContextMiddleware once it has validated the JWT's `tenantId` claim against
// the Active tenant lookup. Both members are null for a request with no tenant claim
// (anonymous, or a legacy token issued before this story) — callers must treat that
// as "no tenant", not as an error; only the middleware itself 401s on a bad claim.
//
// Scoped to the request. No consumer yet schema-qualifies a query against this
// (that's Story 12.6) — this story only resolves and exposes it.
internal interface ITenantContext
{
    Guid? TenantId { get; }
    string? SchemaName { get; }

    // Called exactly once per request by TenantContextMiddleware after a successful
    // resolution. Not part of the public "read" surface conceptually, but there is no
    // separate writer interface in this codebase's DI style (mirrors how other
    // request-scoped accessors are implemented) — Story 12.6 consumers only ever call
    // the getters above.
    void Set(Guid tenantId, string schemaName);
}
