using Npgsql;

namespace FormForge.Api.Infrastructure.Persistence;

// Story 11.3 (FR-72 / AR-63) — mirrors DbConnectionFactory but reads the
// `formforge_preview` connection string (least-privileged role + `Maximum Pool Size=5`
// set on the connection string itself, so Npgsql gives this string its own bounded pool).
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class PreviewConnectionFactory : IPreviewConnectionFactory
{
    // Cached at construction so IConfiguration is not re-read on every call.
    // Null when "formforge_preview" is absent from configuration; the error is
    // raised on first use (not at construction) so unauthorised requests still
    // reach the permission filter and return 403 before any connection is opened.
    private readonly string? _connectionString;

    public PreviewConnectionFactory(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("formforge_preview");
    }

    private string ConnectionString => _connectionString
        ?? throw new InvalidOperationException(
            "Connection string 'formforge_preview' not configured.");

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
