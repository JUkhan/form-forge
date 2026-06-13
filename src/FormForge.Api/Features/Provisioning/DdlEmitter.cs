using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Features.Designer;
using FormForge.Api.Features.SchemaRegistry;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FormForge.Api.Features.Provisioning;

// Story 5.3 — emits CREATE TABLE (new binding) or ALTER TABLE ... ADD COLUMN
// (idempotent re-run after Story 5.8 recovery, or a retry against the same
// version). Uses Dapper + raw NpgsqlConnection for DDL per Decision 1.6
// (EF owns static schema; Dapper owns dynamic-schema DDL).
//
// The FormForgeDbContext is used ONLY for:
//   1. Reading ComponentSchemaVersion.RootElement (the schema source of truth)
//   2. Appending a SchemaAuditLogEntry — NOT saved here. The caller
//      (ProvisioningBackgroundService.ProcessJobAsync) calls SaveChangesAsync
//      in its finally block so the menu-status update and audit-log row
//      commit together in one EF transaction.
//
// Dual-write hazard (accepted for v1): the Dapper DDL transaction commits
// independently of the EF SaveChanges. If the EF commit fails after the DDL
// succeeds, the table exists but provisioning_status stays Pending. Story 5.8's
// recovery scanner picks up such rows on next startup via the filtered index.
//
// Story 5.5 — restructured to own ONE transaction at EmitAsync level so the
// parent CREATE/ALTER, every recursive child CREATE/ALTER, and the FK column +
// index commit (or roll back) atomically per FR-27 AC-5. The previous self-
// contained transactions inside CreateTableAsync/AddMissingColumnsAsync were
// renamed to *Core methods that receive a transaction and execute DDL only.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed partial class DdlEmitter(
    FormForgeDbContext db,
    DbConnectionFactory connectionFactory,
    ISchemaRegistry schemaRegistry,
    ILogger<DdlEmitter> logger)
{
    // Story 5.4 — system columns created by CreateTableAsync. Excluded from
    // orphaned-column tracking because they are not user-authored fieldKeys
    // and never appear in the target column set computed from RootElement.
    // Story 5.6 — promoted to `internal` so SchemaDriftService can reference the
    // same constant directly (resolves the Story 5.4 deferred item about
    // SystemColumnNames having no compile-time link to CreateTableAsync SQL).
    internal static readonly HashSet<string> SystemColumnNames = new(StringComparer.Ordinal)
    {
        "id", "created_at", "created_by", "updated_at", "updated_by", "is_deleted", "cascade_event_id",
    };

    // Story 5.4 — result of an ALTER planning pass. ColumnsAdded is the array of
    // newly added column names (empty for an idempotent no-op). ExistingUserColumns
    // and OrphanedColumns drive the columns_diff JSON payload on the audit row.
    private sealed record AlterResult(
        string[] ColumnsAdded,
        string[] ExistingUserColumns,
        string[] OrphanedColumns);

    public async Task EmitAsync(ProvisioningJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        // 1. Load the designer version to get the RootElement.
        var version = await db.ComponentSchemaVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                v => v.DesignerId == job.DesignerId && v.Version == job.Version,
                ct)
            .ConfigureAwait(false);

        if (version is null)
            throw new InvalidOperationException(
                $"Version {job.Version} of designer '{job.DesignerId}' not found in component_schema_versions.");

        // 2. Re-validate the identifier as a defence-in-depth measure.
        // DesignerId was validated at bind-time but the job record carries a plain string.
        if (!SafeIdentifier.TryCreate(job.DesignerId, out var tableName, out _))
            throw new InvalidOperationException(
                $"DesignerId '{job.DesignerId}' is not a safe PostgreSQL identifier.");

        // 3. Parse the RootElement to extract column definitions (+ derived table
        // columns for the record-list SELECT — Story B-1-4-followup).
        var (columns, childRepeaterIds, derivedColumns) =
            RootElementParser.ParseWithDerived(version.RootElement);

        // 4. Run DDL in ONE Dapper transaction spanning parent + all child tables
        // (Story 5.5 / FR-27 AC-5). Use try/finally rather than `await using var` so
        // the DisposeAsync call is ConfigureAwait(false)-compatible (CA2007).
        // NpgsqlConnection implements IAsyncDisposable; await DisposeAsync explicitly
        // to release the pool connection promptly.
        string correlationId = Ulid.NewUlid().ToString();
        string[] columnsAdded;
        bool tableExists;
        AlterResult? alterResult = null;   // Story 5.4 — diff payload for the audit row

        var connection = await connectionFactory
            .CreateOpenConnectionAsync(ct)
            .ConfigureAwait(false);
        NpgsqlTransaction? tx = null;
        try
        {
            // Existence check happens BEFORE the transaction (same as before — for
            // audit-log routing). Inside the transaction, CREATE TABLE IF NOT EXISTS
            // handles the actual idempotency guard, so a TOCTOU between this read
            // and the DDL is harmless.
            tableExists = await TableExistsAsync(connection, tableName!.Value, ct)
                .ConfigureAwait(false);

            tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            if (!tableExists)
            {
                columnsAdded = await CreateTableCoreAsync(connection, tx, tableName, columns)
                    .ConfigureAwait(false);
                LogCreatedTable(logger, tableName.Value, columnsAdded.Length);
            }
            else
            {
                // Idempotent fallthrough + version evolution (AC-1/AC-2): add any missing
                // columns, never drop orphaned ones. AlterResult carries the diff for the
                // audit log payload computed below.
                alterResult = await AddMissingColumnsCoreAsync(connection, tx, tableName, columns)
                    .ConfigureAwait(false);
                columnsAdded = alterResult.ColumnsAdded;
                LogAlteredTable(logger, tableName.Value, columnsAdded.Length);
                if (alterResult.OrphanedColumns.Length > 0
                    && logger.IsEnabled(LogLevel.Information))
                {
                    // The audit-log JSON payload (built below) always captures the full
                    // orphaned-columns array regardless of log level — this log line is a
                    // human-readable summary, guarded by IsEnabled so string.Join is
                    // skipped when Information logging is disabled.
#pragma warning disable CA1873 // analyzer doesn't recognize the IsEnabled guard on a generated LoggerMessage helper
                    LogOrphanedColumns(
                        logger,
                        tableName.Value,
                        alterResult.OrphanedColumns.Length,
                        job.Version,
                        string.Join(", ", alterResult.OrphanedColumns));
#pragma warning restore CA1873
                }
            }

            // Story 5.5 — provision child tables in the SAME transaction (AC-1/2/3/5).
            // provisionedIds seeds the root designerId so a back-edge to the root inside
            // a descendant tree is detected here as a defence-in-depth check (the bind-
            // time CycleDetector is the primary guard; this fallback only fires if a
            // designer mutated between bind and dispatch).
            // P1 (code review): pass tableName.Value (SafeIdentifier) so EnsureParentFk*
            // uses the validated identifier — not the raw job.DesignerId string — in
            // the REFERENCES clause and FK column name.
            if (childRepeaterIds.Count > 0)
            {
                var provisionedIds = new HashSet<string>(StringComparer.Ordinal) { job.DesignerId };
                await ProvisionChildTablesAsync(
                    connection, tx, tableName!.Value, childRepeaterIds, provisionedIds, ct)
                    .ConfigureAwait(false);
            }

            // TreeView — a self-referencing tree over a node-template table. Unlike a
            // Repeater (which makes the referenced table a CHILD of THIS designer via
            // parent_<thisDesigner>_id), a TreeView makes the referenced table reference
            // ITSELF: parent_<refTable>_id REFERENCES <refTable>(id) ON DELETE CASCADE.
            // Provisioned in the same transaction so the host-designer bind atomically
            // ensures the self-FK exists on the selected node-template table.
            var treeViewIds = RootElementParser.CollectTreeViewSelfRefIds(version.RootElement);
            if (treeViewIds.Count > 0)
                await ProvisionTreeViewSelfRefsAsync(connection, tx, treeViewIds, ct)
                    .ConfigureAwait(false);

            // CancellationToken.None on commit: once we've reached this line, host
            // shutdown must not roll back successful DDL. The catch path uses
            // CancellationToken.None on rollback for the same reason.
            await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            if (tx is not null)
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (tx is not null)
                await tx.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        // Story 5.4 — build the diff JSON for ALTER rows. CREATE rows persist null so the
        // audit-log view can distinguish "no diff because new table" from "empty diff".
        string? columnsDiffJson = tableExists && alterResult is not null
            ? JsonSerializer.Serialize(new
            {
                existingUserColumns = alterResult.ExistingUserColumns,
                addedColumns        = alterResult.ColumnsAdded,
                orphanedColumns     = alterResult.OrphanedColumns,
            })
            : null;

        // 5. Append audit log row first (before Populate, which is in-memory but could
        //    theoretically throw). Adding to the DbContext change tracker is purely
        //    in-memory and cannot fail. The BackgroundService's finally-block
        //    SaveChangesAsync commits this entry together with the menu status update.
        db.SchemaAuditLog.Add(new SchemaAuditLogEntry
        {
            DesignerId = job.DesignerId,
            // Story 5.4 — was job.Version on the ALTER branch (wrong: that's toVersion).
            // job.FromVersion is the previous BoundVersion captured by MenuService
            // before the overwrite. null on a Retry or a first-time bind.
            FromVersion = tableExists ? job.FromVersion : null,
            ToVersion = job.Version,
            DdlOperation = tableExists ? "ALTER" : "CREATE",
            ColumnsAdded = columnsAdded,
            ColumnsDiff = columnsDiffJson,   // Story 5.4
            CorrelationId = correlationId,
            ActorId = job.ActorId,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        // 6. Populate schema registry for future CRUD use (AR-7).
        // IMemoryCache.Set is unlikely to throw, but wrap defensively: a failure here
        // must not cause the DDL-succeeded provisioning to be recorded as Error.
        try
        {
            schemaRegistry.Populate(new SchemaRegistryEntry(
                job.DesignerId,
                job.Version,
                columns,
                childRepeaterIds,
                DateTimeOffset.UtcNow)
            {
                DerivedColumns = derivedColumns,
            });
        }
#pragma warning disable CA1031 // defensive catch — Populate failure must not shadow a successful DDL emit
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRegistryPopulateFailed(logger, ex, job.DesignerId, job.Version);
        }
    }

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection, string tableName, CancellationToken ct)
    {
        _ = ct;
        const string sql = """
            SELECT COUNT(1) > 0
            FROM information_schema.tables
            WHERE table_schema = 'public'
              AND table_name = @tableName
            """;
        return await connection
            .ExecuteScalarAsync<bool>(
                sql,
                new { tableName },
                commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds)
            .ConfigureAwait(false);
    }

    // Story 5.5 — Core variant of CreateTableAsync. The caller (EmitAsync or
    // ProvisionChildTablesAsync) owns the NpgsqlTransaction. No transaction
    // lifecycle here — just emit the CREATE TABLE statement on the supplied tx.
    private static async Task<string[]> CreateTableCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        SafeIdentifier tableName,
        IReadOnlyList<ColumnDefinition> columns)
    {
        var userColumnsSql = BuildUserColumnsSql(columns);
        // System columns are hardcoded strings — no user input interpolated.
        // User column names come only from SafeIdentifier-validated fieldKeys.
        var sql = string.Create(CultureInfo.InvariantCulture, $"""
            CREATE TABLE IF NOT EXISTS {tableName.Value} (
                id              UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
                created_at      TIMESTAMPTZ  DEFAULT now(),
                created_by      UUID         REFERENCES users(id),
                updated_at      TIMESTAMPTZ,
                updated_by      UUID         REFERENCES users(id),
                is_deleted      BOOLEAN      DEFAULT false,
                cascade_event_id UUID        NULL{(userColumnsSql.Length > 0 ? "," : "")}
            {userColumnsSql}
            )
            """);

        await connection.ExecuteAsync(
            sql,
            transaction: tx,
            commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds)
            .ConfigureAwait(false);

        // Grant the sandboxed Dataset preview role read access to the freshly
        // provisioned table. The dataset-foundation migration's one-time
        // `GRANT SELECT ON ALL TABLES` only covered tables existing at that point;
        // Designer-provisioned tables created later are invisible to
        // formforge_preview without this, so dataset preview / Query Builder over
        // them fails with "42501: permission denied for table ...". tableName.Value
        // is a SafeIdentifier (bare, validated) — interpolated like the CREATE above.
        // Guarded so provisioning still succeeds where the preview role was never
        // created (e.g. a DB whose migration user lacked CREATEROLE).
        var grantSql = string.Create(CultureInfo.InvariantCulture, $"""
            DO $$
            BEGIN
              IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'formforge_preview') THEN
                GRANT SELECT ON {tableName.Value} TO formforge_preview;
              END IF;
            END
            $$;
            """);
        await connection.ExecuteAsync(
            grantSql,
            transaction: tx,
            commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds)
            .ConfigureAwait(false);

        return [.. columns.Select(c => c.ColumnName)];
    }

    // Story 5.5 — Core variant of AddMissingColumnsAsync. The caller owns the
    // NpgsqlTransaction. Reads information_schema.columns to compute the diff,
    // then ALTERs missing columns inside the supplied tx (no commit/rollback here).
    private static async Task<AlterResult> AddMissingColumnsCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        SafeIdentifier tableName,
        IReadOnlyList<ColumnDefinition> columns)
    {
        // Fetch existing columns from information_schema. This read is performed
        // against the same connection/transaction so it sees any in-flight DDL
        // performed earlier in the transaction (PostgreSQL DDL is transactional).
        const string existingSql = """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = @tableName
            """;
        var existing = (await connection
            .QueryAsync<string>(
                existingSql,
                new { tableName = tableName.Value },
                transaction: tx,
                commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds)
            .ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        // Story 5.4 — compute the full diff for the audit log.
        // existingUserColumns = live columns minus the hardcoded system columns.
        // orphanedColumns = user columns no longer present in the target schema.
        var targetColumnNames = columns.Select(c => c.ColumnName).ToHashSet(StringComparer.Ordinal);
        var existingUserColumns = existing.Where(c => !SystemColumnNames.Contains(c)).ToArray();
        var orphanedColumns = existingUserColumns.Where(c => !targetColumnNames.Contains(c)).ToArray();

        var toAdd = columns.Where(c => !existing.Contains(c.ColumnName)).ToList();
        if (toAdd.Count == 0)
        {
            // No-op ALTER still returns the diff so the audit row records
            // existingUserColumns + orphanedColumns even when nothing was added.
            return new AlterResult([], existingUserColumns, orphanedColumns);
        }

        foreach (var col in toAdd)
        {
            // col.ColumnName is a validated SafeIdentifier value (re-validated in
            // RootElementParser); col.PgType comes from the hardcoded mapper.
            var alterSql = string.Create(CultureInfo.InvariantCulture,
                $"ALTER TABLE {tableName.Value} ADD COLUMN {col.ColumnName} {col.PgType} NULL");
            await connection.ExecuteAsync(
                alterSql,
                transaction: tx,
                commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds)
                .ConfigureAwait(false);
        }

        return new AlterResult(
            [.. toAdd.Select(c => c.ColumnName)],
            existingUserColumns,
            orphanedColumns);
    }

    // Story 5.5 — recursive child table provisioner. For each Repeater rowDesignerId
    // collected from the parent's RootElement, provision the child table (CREATE
    // IF NOT EXISTS or ALTER), add the parent FK column, create the FK index, and
    // recurse into the child's own Repeater children — all inside the parent's
    // transaction so failure anywhere rolls back the entire parent+children DDL.
    //
    // P1 (code review): parentTableName is always a SafeIdentifier.Value so it can be
    // safely interpolated into the REFERENCES clause without bypassing the DDL-safety
    // invariant that raw strings never enter DDL.
    //
    // P2 (code review): provisionedIds guards table provisioning (CREATE/ALTER) and
    // recursion (once per child node). FK column + index are added outside that guard
    // because they are per parent→child edge — a shared child referenced by two
    // parents needs a separate FK column for each.
    //
    // Not static — uses db (load child versions) and logger inherited from the instance.
    private async Task ProvisionChildTablesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        string parentTableName,
        IReadOnlyList<string> childRepeaterIds,
        HashSet<string> provisionedIds,
        CancellationToken ct)
    {
        foreach (var childId in childRepeaterIds)
        {
            if (!SafeIdentifier.TryCreate(childId, out var childTableName, out _)) continue;

            bool firstEncounter = provisionedIds.Add(childId);

            if (firstEncounter)
            {
                // Load the child designer's latest Published version. AsNoTracking — we
                // only read RootElement; never mutate the schema row from the provisioner.
                var childSchemaVersion = await db.ComponentSchemaVersions
                    .AsNoTracking()
                    .Where(v => v.DesignerId == childId && v.Status == "Published")
                    .OrderByDescending(v => v.Version)
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);

                if (childSchemaVersion is null) continue;   // unpublished — no table, skip FK too

                var (childColumns, grandchildIds) = RootElementParser.ParseFull(childSchemaVersion.RootElement);

                // Provision the child table (CREATE or ALTER) within the parent transaction.
                bool childTableExists = await TableExistsAsync(connection, childTableName!.Value, ct)
                    .ConfigureAwait(false);
                if (!childTableExists)
                    await CreateTableCoreAsync(connection, tx, childTableName, childColumns)
                        .ConfigureAwait(false);
                else
                    await AddMissingColumnsCoreAsync(connection, tx, childTableName, childColumns)
                        .ConfigureAwait(false);

                // Recurse for grandchildren (AC-5 — multi-level nesting).
                // P1: pass childTableName.Value (SafeIdentifier) not raw childId.
                if (grandchildIds.Count > 0)
                    await ProvisionChildTablesAsync(
                        connection, tx, childTableName!.Value, grandchildIds, provisionedIds, ct)
                        .ConfigureAwait(false);
            }
            else
            {
                // P2: Child was already provisioned in this run (fan-in/diamond graph).
                // Still need to add the FK for THIS parent→child edge. Guard with a
                // TableExistsAsync check in case the first encounter skipped the child
                // as unpublished (table was never provisioned).
                bool childTableExists = await TableExistsAsync(connection, childTableName!.Value, ct)
                    .ConfigureAwait(false);
                if (!childTableExists) continue;
            }

            // P2: FK column and index are per parent→child edge — always run even for
            // a child that was already provisioned in an earlier iteration.
            // Both use IF NOT EXISTS and are idempotent on re-runs.
            // P1: parentTableName is SafeIdentifier.Value — safe for DDL interpolation.
            await EnsureParentFkColumnAsync(connection, tx, parentTableName, childTableName!.Value)
                .ConfigureAwait(false);
            await EnsureParentFkIndexAsync(connection, tx, parentTableName, childTableName!.Value)
                .ConfigureAwait(false);
            LogProvisionedChildTable(logger, childTableName!.Value, parentTableName);
        }
    }

    // TreeView self-FK provisioner. For each node-template designerId referenced by a
    // TreeView in the host designer, ensure the referenced table exists (CREATE from its
    // latest Published version if missing) and carries a self-FK parent_<table>_id
    // REFERENCES <table>(id) ON DELETE CASCADE plus its index — the adjacency-list tree.
    // Both EnsureParentFk* are idempotent (IF NOT EXISTS), so re-binding the host is a
    // no-op. Publish-order note: when the node-template designer has no Published version
    // yet, its table cannot be provisioned here — re-bind the host after publishing it.
    private async Task ProvisionTreeViewSelfRefsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        IReadOnlyList<string> treeViewIds,
        CancellationToken ct)
    {
        // De-dupe so two TreeViews referencing the same table emit the self-FK once.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var refId in treeViewIds)
        {
            if (!seen.Add(refId)) continue;
            if (!SafeIdentifier.TryCreate(refId, out var refTable, out _)) continue;

            var exists = await TableExistsAsync(connection, refTable!.Value, ct).ConfigureAwait(false);
            if (!exists)
            {
                var refVersion = await db.ComponentSchemaVersions
                    .AsNoTracking()
                    .Where(v => v.DesignerId == refId && v.Status == "Published")
                    .OrderByDescending(v => v.Version)
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);
                if (refVersion is null) continue;   // unpublished — cannot provision; skip self-FK

                var (refColumns, _) = RootElementParser.ParseFull(refVersion.RootElement);
                await CreateTableCoreAsync(connection, tx, refTable, refColumns).ConfigureAwait(false);
            }

            // Self-edge: parent table == child table == the referenced node-template table.
            await EnsureParentFkColumnAsync(connection, tx, refTable!.Value, refTable!.Value)
                .ConfigureAwait(false);
            await EnsureParentFkIndexAsync(connection, tx, refTable!.Value, refTable!.Value)
                .ConfigureAwait(false);
            LogProvisionedChildTable(logger, refTable!.Value, refTable!.Value);
        }
    }

    // Self-heal entry point for TreeView tree endpoints. A node-template table only gets
    // its self-FK (parent_<table>_id) when a host designer CONTAINING the TreeView is
    // provisioned (see ProvisionTreeViewSelfRefsAsync) — so a table can be a valid tree
    // target while still missing the column (publish order, or testing the endpoint before
    // the host is bound). The tree endpoints call this to add it on demand. Idempotent
    // (ADD COLUMN / CREATE INDEX IF NOT EXISTS). Returns false (no-op) when the designerId
    // is not a safe identifier or its table does not exist yet — the caller then surfaces
    // the original "not provisioned" / validation error.
    public async Task<bool> EnsureSelfReferenceColumnAsync(string designerId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(designerId);
        if (!SafeIdentifier.TryCreate(designerId, out var tableName, out _)) return false;

        var connection = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        NpgsqlTransaction? tx = null;
        try
        {
            if (!await TableExistsAsync(connection, tableName!.Value, ct).ConfigureAwait(false))
                return false;

            tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            // Self-edge: parent table == child table == this table.
            await EnsureParentFkColumnAsync(connection, tx, tableName!.Value, tableName!.Value)
                .ConfigureAwait(false);
            await EnsureParentFkIndexAsync(connection, tx, tableName!.Value, tableName!.Value)
                .ConfigureAwait(false);
            await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            LogProvisionedChildTable(logger, tableName!.Value, tableName!.Value);
            return true;
        }
        catch
        {
            if (tx is not null) await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task EnsureParentFkColumnAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        string parentTableName,     // P1: SafeIdentifier.Value — safe for DDL interpolation
        string childTableName)
    {
        var fkColName = BuildFkColumnName(parentTableName);
        var sql = string.Create(CultureInfo.InvariantCulture,
            $"ALTER TABLE {childTableName} ADD COLUMN IF NOT EXISTS {fkColName} UUID REFERENCES {parentTableName}(id) ON DELETE CASCADE");
        await connection.ExecuteAsync(
            sql,
            transaction: tx,
            commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds)
            .ConfigureAwait(false);
    }

    private static async Task EnsureParentFkIndexAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        string parentTableName,     // P1: SafeIdentifier.Value — safe for DDL interpolation
        string childTableName)
    {
        var fkColName = BuildFkColumnName(parentTableName);
        // Index name is bounded to 63 chars (PG limit) — see BuildIndexName below.
        var indexName = BuildIndexName(childTableName);
        var sql = string.Create(CultureInfo.InvariantCulture,
            $"CREATE INDEX IF NOT EXISTS {indexName} ON {childTableName}({fkColName})");
        await connection.ExecuteAsync(
            sql,
            transaction: tx,
            commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds)
            .ConfigureAwait(false);
    }

    // parent_{parentTableName}_id — capped so the identifier stays within PG's
    // 63-char limit. "parent_" = 7 chars, "_id" = 3 chars → parentTableName may be
    // at most 53 chars. SafeIdentifier already enforces ≤63 chars on designerId so
    // truncation only triggers when parentTableName is 54+ chars. Documented as a
    // known v1 limitation: two long parent IDs differing only past char 53 would
    // collide on the FK column name.
    private static string BuildFkColumnName(string parentTableName)
    {
        const int MaxParent = 63 - 7 - 3; // = 53
        var p = parentTableName.Length <= MaxParent ? parentTableName : parentTableName[..MaxParent];
        return $"parent_{p}_id";
    }

    // idx_{childTableName}_parent — capped to 63 chars. "idx_" = 4, "_parent" = 7
    // → childTableName may be at most 52 chars.
    private static string BuildIndexName(string childTableName)
    {
        const int MaxChild = 63 - 4 - 7; // = 52
        var c = childTableName.Length <= MaxChild ? childTableName : childTableName[..MaxChild];
        return $"idx_{c}_parent";
    }

    private static string BuildUserColumnsSql(IReadOnlyList<ColumnDefinition> columns)
    {
        if (columns.Count == 0) return string.Empty;
        // Each column name is a SafeIdentifier-validated fieldKey — safe to interpolate.
        var sb = new StringBuilder();
        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            sb.Append(CultureInfo.InvariantCulture, $"    {col.ColumnName,-30} {col.PgType,-15} NULL");
            if (i < columns.Count - 1) sb.Append(',');
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // Story 5.6 — drop a single orphaned column. Caller (SchemaDriftService) has
    // already established that the column is orphaned (not system, not FK, not in
    // current Published schema). This method owns the Dapper transaction lifecycle
    // and appends a DROP audit row to db.SchemaAuditLog. The caller is responsible
    // for db.SaveChangesAsync — same EF/Dapper dual-write pattern as EmitAsync.
    public async Task DropColumnAsync(
        string designerId,
        string columnName,
        Guid? actorId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(designerId);
        ArgumentNullException.ThrowIfNull(columnName);

        // Defence-in-depth re-validation. The HTTP route's actorId-extracting handler
        // already constrains designerId via path-parameter binding and the service
        // re-validates via SafeIdentifier, but raw strings must never enter DDL.
        if (!SafeIdentifier.TryCreate(designerId, out var tableName, out _))
            throw new InvalidOperationException(
                $"DesignerId '{designerId}' is not a safe PostgreSQL identifier.");
        if (!SafeIdentifier.TryCreate(columnName, out var colName, out _))
            throw new InvalidOperationException(
                $"ColumnName '{columnName}' is not a safe PostgreSQL identifier.");

        string correlationId = Ulid.NewUlid().ToString();

        var connection = await connectionFactory
            .CreateOpenConnectionAsync(ct)
            .ConfigureAwait(false);
        NpgsqlTransaction? tx = null;
        try
        {
            tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            // tableName.Value and colName.Value are SafeIdentifier-validated — safe
            // for direct interpolation. No user-supplied text reaches the DDL string.
            var sql = string.Create(CultureInfo.InvariantCulture,
                $"ALTER TABLE {tableName!.Value} DROP COLUMN {colName!.Value}");
            await connection.ExecuteAsync(
                sql,
                transaction: tx,
                commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds)
                .ConfigureAwait(false);

            // CancellationToken.None on commit/rollback for the same reason as EmitAsync:
            // once we're at this line, host shutdown must not leave the table half-dropped.
            await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            if (tx is not null)
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (tx is not null)
                await tx.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        // Append DROP audit row. ToVersion = 0 sentinel: DROP is not version-bound.
        // The caller (SchemaDriftService.DropColumnAsync) calls SaveChangesAsync.
        db.SchemaAuditLog.Add(new SchemaAuditLogEntry
        {
            DesignerId = designerId,
            FromVersion = null,
            ToVersion = 0,
            DdlOperation = "DROP",
            ColumnsAdded = null,
            ColumnsDropped = [colName.Value],
            CorrelationId = correlationId,
            ActorId = actorId,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        LogDroppedColumn(logger, tableName.Value, colName.Value);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Dropped column '{ColumnName}' from table '{TableName}'")]
    private static partial void LogDroppedColumn(ILogger logger, string tableName, string columnName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Created table '{TableName}' with {ColumnCount} user-defined column(s)")]
    private static partial void LogCreatedTable(ILogger logger, string tableName, int columnCount);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Altered table '{TableName}' — added {ColumnCount} missing column(s)")]
    private static partial void LogAlteredTable(ILogger logger, string tableName, int columnCount);

    // Story 5.4 — orphaned columns are kept (FR-25 AC-1) but logged at Information so
    // the operator can see when a designer evolution drops fields without losing data.
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Table '{TableName}' has {Count} orphaned column(s) absent from designer v{Version}: {Columns}")]
    private static partial void LogOrphanedColumns(
        ILogger logger, string tableName, int count, int version, string columns);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Schema registry populate failed for DesignerId '{DesignerId}' v{Version} — entry will be rebuilt on next provision")]
    private static partial void LogRegistryPopulateFailed(ILogger logger, Exception ex, string designerId, int version);

    // Story 5.5 — emitted after each child CREATE/ALTER + FK column + FK index complete.
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Provisioned child table '{ChildTableName}' for parent '{ParentTableName}'")]
    private static partial void LogProvisionedChildTable(
        ILogger logger, string childTableName, string parentTableName);
}
