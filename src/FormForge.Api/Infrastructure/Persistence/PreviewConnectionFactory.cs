using FormForge.Api.Features.Tenancy;
using Npgsql;

namespace FormForge.Api.Infrastructure.Persistence;

// Story 11.3 (FR-72 / AR-63) — mirrors DbConnectionFactory but reads the
// `formforge_preview` connection string (least-privileged role + `Maximum Pool Size=5`
// set on the connection string itself, so Npgsql gives this string its own bounded pool).
//
// Story 12.6 — targets the tenant's own `{schemaName}_datasets` namespace (Story 12.7's
// naming) via search_path, falling back to the legacy global `datasets` schema only when
// no tenant is resolved (ITenantContext.SchemaName is null). Scoped (not Singleton) so
// each request reads its own ITenantContext at connection-open time.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class PreviewConnectionFactory : IPreviewConnectionFactory
{
    // Cached at construction so IConfiguration is not re-read on every call.
    // Null when "formforge_preview" is absent from configuration; the error is
    // raised on first use (not at construction) so unauthorised requests still
    // reach the permission filter and return 403 before any connection is opened.
    private readonly string? _connectionString;
    private readonly ITenantContext _tenantContext;

    public PreviewConnectionFactory(IConfiguration configuration, ITenantContext tenantContext)
    {
        _connectionString = configuration.GetConnectionString("formforge_preview");
        _tenantContext = tenantContext;
    }

    private string ConnectionString => _connectionString
        ?? throw new InvalidOperationException(
            "Connection string 'formforge_preview' not configured.");

    public async Task<NpgsqlConnection> CreateOpenConnectionAsync(CancellationToken ct = default)
    {
        var datasetsSchema = TenantDatasetSchemaResolver.Resolve(_tenantContext);
        var csb = new NpgsqlConnectionStringBuilder(ConnectionString) { SearchPath = $"{datasetsSchema}, public" };
        var connection = new NpgsqlConnection(csb.ConnectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
