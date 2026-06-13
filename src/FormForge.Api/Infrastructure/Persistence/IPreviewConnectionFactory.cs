using Npgsql;

namespace FormForge.Api.Infrastructure.Persistence;

// Story 11.3 (FR-72 / AR-63) — opens connections from the dedicated, least-privileged
// `formforge_preview` pool used exclusively for read-only query previews. Separate from
// DbConnectionFactory (the privileged `formforge` pool) so preview execution runs as a
// principal that can only SELECT public tables (GRANT/REVOKE set by the dataset migration).
internal interface IPreviewConnectionFactory
{
    Task<NpgsqlConnection> CreateOpenConnectionAsync(CancellationToken ct = default);
}
