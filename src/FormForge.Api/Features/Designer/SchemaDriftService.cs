using System.Globalization;
using Dapper;
using FormForge.Api.Features.Designer.Dtos;
using FormForge.Api.Features.Provisioning;
using FormForge.Api.Features.SchemaRegistry;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FormForge.Api.Features.Designer;

// Story 5.6 — read the live PG schema for a provisioned designer table, diff it
// against the latest Published Designer version, and surface orphaned columns
// (present in DB, absent from current schema) so an admin can choose to drop
// them. System columns and FK columns are ALWAYS excluded — they're invariants
// of the provisioning pipeline, not user-authored fields, and dropping them
// would break the platform.
//
// Decision 1.6 (EF vs Dapper) applies: EF reads the Published version row;
// Dapper queries information_schema and executes the DROP DDL.
internal enum DropColumnOutcome
{
    Success,
    DesignerNotFound,
    ColumnNotFound,
    ColumnProtected,
    ColumnNotOrphaned,
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class SchemaDriftService(
    FormForgeDbContext db,
    DbConnectionFactory connectionFactory,
    DdlEmitter ddlEmitter,
    ISchemaRegistry schemaRegistry)
{
    // Returns null when the designerId itself is not a valid identifier — the
    // caller maps this to 404. Returns an empty list when the table doesn't
    // exist yet (a designer that has never been bound / provisioned).
    public async Task<SchemaDriftResponse?> GetDriftAsync(string designerId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(designerId);

        if (!SafeIdentifier.TryCreate(designerId, out var tableName, out _))
            return null;

        // Explicit try/finally rather than `await using var` — CA2007 requires
        // ConfigureAwait(false) on the implicit DisposeAsync, which the syntactic-
        // sugar form doesn't emit.
        var connection = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var tableExists = await TableExistsAsync(connection, tableName!.Value).ConfigureAwait(false);
            if (!tableExists)
                return new SchemaDriftResponse([]);

            // Pull (column_name, data_type) for the provisioned table.
            const string existingSql = """
                SELECT column_name AS ColumnName, data_type AS DataType
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = @tableName
                ORDER BY ordinal_position
                """;
            var existing = (await connection.QueryAsync<ExistingColumn>(
                existingSql,
                new { tableName = tableName.Value },
                commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds)
                .ConfigureAwait(false))
                .ToList();

            // Compute the target column set from the latest Published version. If no
            // Published version exists (all Draft/Archived), the target set is empty
            // and every user column becomes orphaned — intentional, per Dev Notes.
            var targetColumns = await GetTargetColumnNamesAsync(designerId, ct).ConfigureAwait(false);

            var orphans = new List<OrphanedColumnInfo>();
            foreach (var col in existing)
            {
                if (DdlEmitter.SystemColumnNames.Contains(col.ColumnName)) continue;
                if (IsFkColumn(col.ColumnName)) continue;
                if (targetColumns.Contains(col.ColumnName)) continue;

                // Column is orphaned — query the non-null row count. SafeIdentifier
                // re-validates the column name (defence-in-depth; info_schema names
                // are PG-enforced safe but treat them as untrusted in DDL/DML paths).
                if (!SafeIdentifier.TryCreate(col.ColumnName, out var safeCol, out _))
                    continue;

                var countSql = string.Create(CultureInfo.InvariantCulture,
                    $"SELECT COUNT(*) FROM {tableName.Value} WHERE {safeCol!.Value} IS NOT NULL");
                long nonNullCount;
                try
                {
                    nonNullCount = await connection.ExecuteScalarAsync<long>(
                        countSql,
                        commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds)
                        .ConfigureAwait(false);
                }
                catch (PostgresException ex) when (ex.SqlState == "42703")
                {
                    // Column was concurrently dropped between the information_schema fetch and
                    // this COUNT query — skip it rather than surfacing a 500 on GET /drift.
                    continue;
                }

                orphans.Add(new OrphanedColumnInfo(col.ColumnName, col.DataType, nonNullCount));
            }

            return new SchemaDriftResponse(orphans);
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<DropColumnOutcome> DropColumnAsync(
        string designerId,
        string columnName,
        Guid? actorId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(designerId);
        ArgumentNullException.ThrowIfNull(columnName);

        if (!SafeIdentifier.TryCreate(designerId, out var tableName, out _))
            return DropColumnOutcome.DesignerNotFound;

        // Hardcoded guards run BEFORE the SafeIdentifier column-name check because
        // PgReservedKeywords now includes the system-column names (id, created_at,
        // …), so SafeIdentifier.TryCreate("id", …) returns false and we'd otherwise
        // surface a misleading 404 ColumnNotFound for a clearly protected column.
        if (DdlEmitter.SystemColumnNames.Contains(columnName))
            return DropColumnOutcome.ColumnProtected;
        if (IsFkColumn(columnName))
            return DropColumnOutcome.ColumnProtected;

        if (!SafeIdentifier.TryCreate(columnName, out _, out _))
            return DropColumnOutcome.ColumnNotFound;

        // Verify the column actually exists on the live table — if not, 404 is
        // more informative than 422 (the caller asked us to drop something that
        // isn't there). Explicit try/finally for the same CA2007 reason as above.
        bool columnExists;
        var probe = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await TableExistsAsync(probe, tableName!.Value).ConfigureAwait(false))
                return DropColumnOutcome.ColumnNotFound;
            columnExists = await ColumnExistsAsync(probe, tableName.Value, columnName).ConfigureAwait(false);
        }
        finally
        {
            await probe.DisposeAsync().ConfigureAwait(false);
        }
        if (!columnExists) return DropColumnOutcome.ColumnNotFound;

        // Guard against active columns — fail closed if the column IS in the
        // current Published schema. The drift view filters these out so the
        // operator never sees them, but a hand-crafted DELETE request must be
        // rejected.
        var targetColumns = await GetTargetColumnNamesAsync(designerId, ct).ConfigureAwait(false);
        if (targetColumns.Contains(columnName))
            return DropColumnOutcome.ColumnNotOrphaned;

        await ddlEmitter.DropColumnAsync(designerId, columnName, actorId, ct).ConfigureAwait(false);
        // CancellationToken.None: the DDL already committed; the audit row must persist.
        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        // Clear all cached versions for this designer so the next Epic 6 read
        // doesn't see a stale entry that still references the dropped column.
        schemaRegistry.InvalidateDesigner(designerId);

        return DropColumnOutcome.Success;
    }

    private async Task<HashSet<string>> GetTargetColumnNamesAsync(
        string designerId, CancellationToken ct)
    {
        var latestPublished = await db.ComponentSchemaVersions
            .AsNoTracking()
            .Where(v => v.DesignerId == designerId && v.Status == "Published")
            .OrderByDescending(v => v.Version)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (latestPublished is null)
            return new HashSet<string>(StringComparer.Ordinal);

        var (columns, _) = RootElementParser.ParseFull(latestPublished.RootElement);
        return columns.Select(c => c.ColumnName).ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<bool> TableExistsAsync(
        Npgsql.NpgsqlConnection connection, string tableName)
    {
        const string sql = """
            SELECT COUNT(1) > 0
            FROM information_schema.tables
            WHERE table_schema = 'public'
              AND table_name = @tableName
            """;
        return await connection.ExecuteScalarAsync<bool>(
            sql,
            new { tableName },
            commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds)
            .ConfigureAwait(false);
    }

    private static async Task<bool> ColumnExistsAsync(
        Npgsql.NpgsqlConnection connection, string tableName, string columnName)
    {
        const string sql = """
            SELECT COUNT(1) > 0
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = @tableName
              AND column_name = @columnName
            """;
        return await connection.ExecuteScalarAsync<bool>(
            sql,
            new { tableName, columnName },
            commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds)
            .ConfigureAwait(false);
    }

    // Story 5.5 FK column shape: parent_{parentTableName}_id. We refuse to drop
    // any column matching that envelope; user fieldKeys cannot collide because
    // SafeIdentifier allows [a-z_][a-z0-9_]* but combined with the parent_/_id
    // bracketing this is a reserved shape for the provisioning pipeline.
    private static bool IsFkColumn(string columnName) =>
        columnName.StartsWith("parent_", StringComparison.Ordinal) &&
        columnName.EndsWith("_id", StringComparison.Ordinal);

    private sealed record ExistingColumn(string ColumnName, string DataType);
}
