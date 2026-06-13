using Npgsql;

namespace FormForge.Api.Infrastructure.Persistence;

// Story 5.3 — wraps raw NpgsqlConnection for Dapper DDL execution (Decision 1.6).
// NpgsqlConnection.CommandTimeout is read-only at runtime, so callers pass
// DdlCommandTimeoutSeconds via Dapper's commandTimeout parameter on each Execute.
// DDL can be slow on large tables, so the EF default (30 s) is too tight.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class DbConnectionFactory(IConfiguration configuration)
{
    // Default per-command timeout for DDL paths. Passed through Dapper.
    public const int DdlCommandTimeoutSeconds = 60;

    private string ConnectionString =>
        configuration.GetConnectionString("formforge")
        ?? throw new InvalidOperationException("Connection string 'formforge' not configured.");

    public async Task<NpgsqlConnection> CreateOpenConnectionAsync(CancellationToken ct = default)
    {
        var connection = new NpgsqlConnection(ConnectionString);
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
