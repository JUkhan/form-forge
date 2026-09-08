using FormForge.Api.Features.Tenancy;
using Npgsql;

namespace FormForge.Api.Infrastructure.Persistence;

// Story 5.3 — wraps raw NpgsqlConnection for Dapper DDL execution (Decision 1.6).
// NpgsqlConnection.CommandTimeout is read-only at runtime, so callers pass
// DdlCommandTimeoutSeconds via Dapper's commandTimeout parameter on each Execute.
// DDL can be slow on large tables, so the EF default (30 s) is too tight.
//
// Story 12.6 — reads the scoped ITenantContext at connection-open time (inline, not
// cached from construction) and sets `search_path` accordingly, exactly like
// TenantSchemaConnectionInterceptor does for the EF-managed connection. Scoped (not
// Singleton) so each request gets a factory backed by that request's own
// ITenantContext; Dapper callers already call CreateOpenConnectionAsync() fresh per
// use, so no interceptor plumbing is needed here.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class DbConnectionFactory(IConfiguration configuration, ITenantContext tenantContext)
{
    // Default per-command timeout for DDL paths. Passed through Dapper.
    public const int DdlCommandTimeoutSeconds = 60;

    private string ConnectionString =>
        configuration.GetConnectionString("formforge")
        ?? throw new InvalidOperationException("Connection string 'formforge' not configured.");

    public async Task<NpgsqlConnection> CreateOpenConnectionAsync(CancellationToken ct = default)
    {
        var schema = tenantContext.SchemaName ?? "public";
        var searchPath = schema == "public" ? "public" : $"{schema}, public";
        var csb = new NpgsqlConnectionStringBuilder(ConnectionString) { SearchPath = searchPath };
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
