using Dapper;
using FormForge.Api.Domain.ValueTypes;
using FormForge.Api.Infrastructure.Persistence;
using Npgsql;

namespace FormForge.Api.Features.Datasets;

// Story 8.4 — CREATE VIEW. Story 8.5 adds CREATE OR REPLACE / ALTER RENAME;
// Story 8.6 adds DROP VIEW IF EXISTS.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed partial class DatasetViewManager(ILogger<DatasetViewManager> logger)
{
    // Builds the schema-qualified CREATE VIEW DDL string.
    // dataset_name is validated (DatasetName value type guarantees safe identifier).
    // Double-quotes the view name for SQL standards compliance.
    internal static string BuildCreateViewDdl(DatasetName name, string effectiveQuery) =>
        $"""CREATE VIEW datasets."{name.Value}" AS {effectiveQuery}""";

    // Executes CREATE VIEW inside the caller-supplied transaction.
    // Throws NpgsqlException on PostgreSQL errors — caller is responsible for rollback.
    internal async Task CreateAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        DatasetName name,
        string effectiveQuery,
        CancellationToken ct)
    {
        var ddl = BuildCreateViewDdl(name, effectiveQuery);
        await conn.ExecuteAsync(new CommandDefinition(
            ddl,
            transaction: tx,
            commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds,
            cancellationToken: ct)).ConfigureAwait(false);

        LogViewCreated(logger, name.Value);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created VIEW datasets.\"{Name}\"")]
    private static partial void LogViewCreated(ILogger logger, string name);

    // Story 8.5 — redefine a same-name VIEW. Story 8.10 fix: use DROP + CREATE
    // rather than CREATE OR REPLACE VIEW. CREATE OR REPLACE cannot drop, rename, or
    // reorder a view's existing output columns — Postgres raises 42P16 "cannot drop
    // columns from view" — so editing a dataset to a narrower or differently-shaped
    // column set (e.g. "SELECT *" → "SELECT email, display_name") failed. Both
    // statements run in the caller's transaction, so the redefinition stays atomic
    // and supports an arbitrary new column set. IF EXISTS keeps it safe if the view
    // is somehow already gone. (No CASCADE: a dependent object surfaces a clear error
    // rather than being silently dropped — dataset views have no dependents today.)
    internal static string BuildReplaceViewDdl(DatasetName name, string effectiveQuery) =>
        $"DROP VIEW IF EXISTS datasets.\"{name.Value}\";\n" +
        $"CREATE VIEW datasets.\"{name.Value}\" AS {effectiveQuery}";

    // Story 8.5 — ALTER VIEW ... RENAME TO ... DDL. Both names are validated
    // identifiers (oldName comes from the DB row, newName from a DatasetName).
    internal static string BuildRenameViewDdl(string oldName, string newName) =>
        $"ALTER VIEW datasets.\"{oldName}\" RENAME TO \"{newName}\"";

    // Executes the DROP + CREATE VIEW redefinition inside the caller-supplied transaction
    // (both statements in one command). Throws NpgsqlException on PostgreSQL errors —
    // caller is responsible for rollback.
    internal async Task ReplaceAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        DatasetName name,
        string effectiveQuery,
        CancellationToken ct)
    {
        var ddl = BuildReplaceViewDdl(name, effectiveQuery);
        await conn.ExecuteAsync(new CommandDefinition(
            ddl,
            transaction: tx,
            commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds,
            cancellationToken: ct)).ConfigureAwait(false);

        LogViewReplaced(logger, name.Value);
    }

    // Executes ALTER VIEW ... RENAME inside the caller-supplied transaction.
    // Throws NpgsqlException on PostgreSQL errors — caller is responsible for rollback.
    internal async Task RenameAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string oldName,
        string newName,
        CancellationToken ct)
    {
        var ddl = BuildRenameViewDdl(oldName, newName);
        await conn.ExecuteAsync(new CommandDefinition(
            ddl,
            transaction: tx,
            commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds,
            cancellationToken: ct)).ConfigureAwait(false);

        LogViewRenamed(logger, oldName, newName);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Replaced VIEW datasets.\"{Name}\"")]
    private static partial void LogViewReplaced(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Renamed VIEW datasets.\"{OldName}\" → \"{NewName}\"")]
    private static partial void LogViewRenamed(ILogger logger, string oldName, string newName);

    // Story 8.6 — DROP VIEW IF EXISTS DDL. Uses IF EXISTS for safety (idempotent).
    // datasetName comes from the DB row (already a validated identifier), mirroring
    // BuildRenameViewDdl's plain-string oldName parameter.
    internal static string BuildDropViewDdl(string datasetName) =>
        $"DROP VIEW IF EXISTS datasets.\"{datasetName}\"";

    // Executes DROP VIEW IF EXISTS inside the caller-supplied transaction.
    // Throws NpgsqlException on PostgreSQL errors — caller is responsible for rollback.
    internal async Task DropAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string datasetName,
        CancellationToken ct)
    {
        var ddl = BuildDropViewDdl(datasetName);
        await conn.ExecuteAsync(new CommandDefinition(
            ddl,
            transaction: tx,
            commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds,
            cancellationToken: ct)).ConfigureAwait(false);

        LogViewDropped(logger, datasetName);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Dropped VIEW datasets.\"{Name}\"")]
    private static partial void LogViewDropped(ILogger logger, string name);
}
