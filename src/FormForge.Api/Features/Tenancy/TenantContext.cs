namespace FormForge.Api.Features.Tenancy;

// Story 12.3 — scoped (per-request) implementation of ITenantContext. Deliberately
// dumb: it holds whatever TenantContextMiddleware sets and nothing else. Registered
// Scoped in Program.cs so a new instance backs every request, matching the lifetime
// of the FormForgeDbContext consumers will eventually schema-qualify against (Story 12.6).
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }
    public string? SchemaName { get; private set; }

    public void Set(Guid tenantId, string schemaName)
    {
        ArgumentException.ThrowIfNullOrEmpty(schemaName);
        TenantId = tenantId;
        SchemaName = schemaName;
    }
}
