using FormForge.Api.Features.Designer;

namespace FormForge.Api.Features.Tenancy;

// Story 12.6 — shared helper for resolving the tenant's own `{schema}_datasets`
// namespace (Story 12.7's naming) from ITenantContext, falling back to the legacy
// global `datasets` schema when no tenant is resolved. Re-validates the schema name
// via SafeIdentifier before it is ever interpolated into DDL/SQL — defense-in-depth,
// mirroring TenantOnboardingService's own re-validation before it interpolates
// Tenant.SchemaName into a connection string / DDL.
internal static class TenantDatasetSchemaResolver
{
    internal static string Resolve(ITenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(tenantContext);

        var schemaName = tenantContext.SchemaName;
        if (schemaName is null)
            return "datasets";

        if (!SafeIdentifier.TryCreate(schemaName, out var safeSchemaName, out var error))
            throw new InvalidOperationException(
                $"Tenant schema_name '{schemaName}' is invalid: {error}");

        var datasetsSchemaName = $"{safeSchemaName!.Value}_datasets";

        // SafeIdentifier caps schemaName at 63 chars on its own, but appending
        // "_datasets" can push a valid schemaName past Postgres's 63-byte identifier
        // limit — Postgres silently truncates rather than erroring, which risks two
        // long-named tenants colliding on the same truncated dataset schema. Re-validate
        // the combined identifier (same check TenantOnboardingService performs before
        // ever creating this schema) and fail loudly instead.
        if (!SafeIdentifier.TryCreate(datasetsSchemaName, out _, out var datasetsError))
            throw new InvalidOperationException(
                $"Tenant schema_name '{schemaName}' is too long to derive a dataset namespace " +
                $"('{datasetsSchemaName}'): {datasetsError}");

        return datasetsSchemaName;
    }
}
