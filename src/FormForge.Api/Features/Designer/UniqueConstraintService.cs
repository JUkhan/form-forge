using System.Globalization;
using System.Text;
using Dapper;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Features.Designer.Dtos;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FormForge.Api.Features.Designer;

// Admin UNIQUE-constraint management for provisioned CRUD tables.
//
// A CRUD-mode Designer bound to a menu gets a physical table named after its
// designerId (see DdlEmitter). This service lets an admin add or drop UNIQUE
// constraints on the user-authored columns of those tables.
//
// Decision 1.6 (EF vs Dapper) applies, mirroring SchemaDriftService: EF reads the
// menu/designer rows for the picker and appends the audit row; Dapper queries
// information_schema / pg_constraint and executes the ALTER TABLE … DDL. Every
// identifier interpolated into DDL is a SafeIdentifier value or an information_-
// schema-sourced name re-validated through SafeIdentifier — raw strings never
// reach a DDL string.

internal enum AddUniqueConstraintOutcome
{
    Success,
    DesignerNotFound,    // designerId invalid OR no provisioned table
    NoColumns,           // empty / null column list
    TooManyColumns,      // exceeds the composite-key cap
    DuplicateColumns,    // the same column listed twice in one request
    InvalidColumn,       // a column name is not a safe identifier
    ColumnNotFound,      // a column does not exist on the table (or is system/FK)
    AlreadyExists,       // a UNIQUE constraint already covers this exact column set
    DuplicateValues,     // existing rows violate the requested uniqueness (PG 23505)
}

internal sealed record AddUniqueConstraintResult(
    AddUniqueConstraintOutcome Outcome,
    UniqueConstraintInfo? Created = null,
    string? OffendingColumn = null);

internal enum DropUniqueConstraintOutcome
{
    Success,
    DesignerNotFound,
    ConstraintNotFound,   // no constraint with that name on the table
    NotUniqueConstraint,  // a constraint exists but it is not a UNIQUE constraint
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed partial class UniqueConstraintService(
    FormForgeDbContext db,
    DbConnectionFactory connectionFactory,
    ILogger<UniqueConstraintService> logger)
{
    // A composite UNIQUE constraint spanning more than this many columns is almost
    // certainly a mistake; cap it so the UI and DDL stay sane.
    private const int MaxColumns = 8;

    // Columns hidden from the constraint picker: the `id` primary key (already
    // unique, so a UNIQUE constraint on it is redundant) plus the audit / soft-delete
    // / cascade bookkeeping columns the provisioning layer manages. These never make
    // sense as user-chosen UNIQUE columns. Everything else physically present on the
    // table (including any parent_*_id FK columns) is offered.
    //
    // NOTE: deliberately NOT DdlEmitter.SystemColumnNames — that set is shared with
    // the schema-drift orphaned-column logic that must keep its own semantics.
    private static readonly HashSet<string> NonConstrainableColumns = new(StringComparer.Ordinal)
    {
        "id", "created_at", "created_by", "updated_at", "updated_by", "is_deleted", "cascade_event_id",
    };

    // ---- Picker -----------------------------------------------------------

    // Every CRUD-mode Designer whose physical table actually exists — i.e. is
    // provisioned. This mirrors the Table Provisioning tab
    // (TableProvisioningService.ListAsync), which derives "provisioned" from the
    // physical table's existence rather than from a successful menu binding. A
    // designer can be provisioned without any menu (provisioned directly from the
    // Table Provisioning tab); such menu-less tables must still appear here.
    //
    // Menu names / bound version are optional decoration for the picker label, not
    // a gate: we LEFT-join them in but never require a menu to exist.
    public async Task<ProvisionedDesignersResponse> ListProvisionedDesignersAsync(CancellationToken ct)
    {
        // All CRUD designers (VIEW-mode components never provision a table).
        var crudDesigners = await db.ComponentSchemas.AsNoTracking()
            .Where(c => c.Mode == "CRUD")
            .Select(c => new { c.DesignerId, c.DisplayName })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Menu bindings whose provisioning succeeded — decoration only. Keyed by
        // designerId so a menu-less designer simply has no entry.
        var menuRows = await db.Menus.AsNoTracking()
            .Where(m => m.DesignerId != null && m.ProvisioningStatus == "Success")
            .Select(m => new { DesignerId = m.DesignerId!, m.BoundVersion, MenuName = m.Name })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var menusByDesigner = menuRows
            .GroupBy(r => r.DesignerId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // Which physical tables actually exist (table name == designerId).
        var existingTables = await GetExistingTableNamesAsync(ct).ConfigureAwait(false);

        var designers = crudDesigners
            .Where(d => existingTables.Contains(d.DesignerId))
            .Select(d =>
            {
                menusByDesigner.TryGetValue(d.DesignerId, out var menus);
                return new ProvisionedDesignerItem(
                    d.DesignerId,
                    d.DisplayName,
                    menus?.Max(r => r.BoundVersion),
                    menus?
                        .Select(r => r.MenuName)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                        ?? []);
            })
            .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ProvisionedDesignersResponse(designers);
    }

    // Names of every base table in the public schema. Designer tables are named
    // after their designerId, so membership == "table is provisioned". Mirrors
    // TableProvisioningService.GetExistingTableNamesAsync.
    private async Task<HashSet<string>> GetExistingTableNamesAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'public' AND table_type = 'BASE TABLE'
            """;

        var connection = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var names = await connection
                .QueryAsync<string>(new CommandDefinition(sql, cancellationToken: ct))
                .ConfigureAwait(false);
            return new HashSet<string>(names, StringComparer.Ordinal);
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---- Read columns + constraints --------------------------------------

    // Returns null when the designerId is not a safe identifier or no provisioned
    // table exists for it — the caller maps both to 404.
    public async Task<UniqueConstraintsResponse?> GetConstraintsAsync(string designerId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(designerId);

        if (!SafeIdentifier.TryCreate(designerId, out var tableName, out _))
            return null;

        var connection = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await TableExistsAsync(connection, tableName!.Value).ConfigureAwait(false))
                return null;

            var columns = await GetUserColumnsAsync(connection, tableName.Value).ConfigureAwait(false);
            var constraints = await GetUniqueConstraintsAsync(connection, tableName.Value).ConfigureAwait(false);

            return new UniqueConstraintsResponse(
                designerId,
                tableName.Value,
                columns.Select(c => new ConstrainableColumn(c.ColumnName, c.DataType)).ToList(),
                constraints);
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---- Add --------------------------------------------------------------

    public async Task<AddUniqueConstraintResult> AddAsync(
        string designerId,
        IReadOnlyList<string>? requestColumns,
        Guid? actorId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(designerId);

        if (!SafeIdentifier.TryCreate(designerId, out var tableName, out _))
            return new AddUniqueConstraintResult(AddUniqueConstraintOutcome.DesignerNotFound);

        var columns = (requestColumns ?? [])
            .Select(c => c?.Trim() ?? string.Empty)
            .Where(c => c.Length > 0)
            .ToList();

        if (columns.Count == 0)
            return new AddUniqueConstraintResult(AddUniqueConstraintOutcome.NoColumns);
        if (columns.Count > MaxColumns)
            return new AddUniqueConstraintResult(AddUniqueConstraintOutcome.TooManyColumns);
        if (columns.Distinct(StringComparer.Ordinal).Count() != columns.Count)
            return new AddUniqueConstraintResult(AddUniqueConstraintOutcome.DuplicateColumns);

        // Every requested column must be a safe identifier (defence-in-depth before
        // it enters the DDL string).
        foreach (var col in columns)
        {
            if (!SafeIdentifier.TryCreate(col, out _, out _))
                return new AddUniqueConstraintResult(
                    AddUniqueConstraintOutcome.InvalidColumn, OffendingColumn: col);
        }

        var connection = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        NpgsqlTransaction? tx = null;
        UniqueConstraintInfo created;
        try
        {
            if (!await TableExistsAsync(connection, tableName!.Value).ConfigureAwait(false))
                return new AddUniqueConstraintResult(AddUniqueConstraintOutcome.DesignerNotFound);

            // Constrainable user columns on the live table — rejects system/FK columns
            // and anything not physically present.
            var userColumns = (await GetUserColumnsAsync(connection, tableName.Value).ConfigureAwait(false))
                .Select(c => c.ColumnName)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var col in columns)
            {
                if (!userColumns.Contains(col))
                    return new AddUniqueConstraintResult(
                        AddUniqueConstraintOutcome.ColumnNotFound, OffendingColumn: col);
            }

            // Reject a constraint that duplicates an existing one (same column SET —
            // UNIQUE(a,b) and UNIQUE(b,a) enforce the same rule).
            var existing = await GetUniqueConstraintsAsync(connection, tableName.Value).ConfigureAwait(false);
            var requestedSet = columns.OrderBy(c => c, StringComparer.Ordinal).ToList();
            foreach (var ex in existing)
            {
                var exSet = ex.Columns.OrderBy(c => c, StringComparer.Ordinal).ToList();
                if (exSet.SequenceEqual(requestedSet, StringComparer.Ordinal))
                    return new AddUniqueConstraintResult(AddUniqueConstraintOutcome.AlreadyExists);
            }

            var constraintName = BuildConstraintName(
                tableName.Value, columns, existing.Select(e => e.Name));

            // tableName.Value, constraintName and every column are SafeIdentifier-safe
            // (validated above) — safe to interpolate. No user text reaches the DDL.
            var columnList = string.Join(", ", columns);
            var sql = string.Create(CultureInfo.InvariantCulture,
                $"ALTER TABLE {tableName.Value} ADD CONSTRAINT {constraintName} UNIQUE ({columnList})");

            tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                await connection.ExecuteAsync(
                    sql,
                    transaction: tx,
                    commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds)
                    .ConfigureAwait(false);
                await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // Existing rows already contain duplicate values for the chosen
                // column(s); PostgreSQL refuses to build the constraint. Roll back
                // and surface a friendly 422 rather than a 500.
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new AddUniqueConstraintResult(AddUniqueConstraintOutcome.DuplicateValues);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DuplicateObject
                                            || ex.SqlState == PostgresErrorCodes.DuplicateTable)
            {
                // Constraint name already taken (race with a concurrent add). Treat as
                // already-exists rather than a 500.
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new AddUniqueConstraintResult(AddUniqueConstraintOutcome.AlreadyExists);
            }

            created = new UniqueConstraintInfo(constraintName, columns);
            LogAddedConstraint(logger, constraintName, tableName.Value, columnList);
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

        // Audit row. The schema audit table has no constraint-name column, so reuse
        // ColumnsAdded for the constrained columns and Notes for the constraint name.
        // ToVersion = 0 sentinel: constraint changes are not version-bound (same as DROP).
        db.SchemaAuditLog.Add(new SchemaAuditLogEntry
        {
            DesignerId = designerId,
            FromVersion = null,
            ToVersion = 0,
            DdlOperation = "ADD_UQ",
            ColumnsAdded = [.. created.Columns],
            CorrelationId = Ulid.NewUlid().ToString(),
            ActorId = actorId,
            CreatedAt = DateTimeOffset.UtcNow,
            Notes = created.Name,
        });
        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        return new AddUniqueConstraintResult(AddUniqueConstraintOutcome.Success, created);
    }

    // ---- Drop -------------------------------------------------------------

    public async Task<DropUniqueConstraintOutcome> DropAsync(
        string designerId,
        string constraintName,
        Guid? actorId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(designerId);
        ArgumentNullException.ThrowIfNull(constraintName);

        if (!SafeIdentifier.TryCreate(designerId, out var tableName, out _))
            return DropUniqueConstraintOutcome.DesignerNotFound;
        if (!SafeIdentifier.TryCreate(constraintName, out var safeName, out _))
            return DropUniqueConstraintOutcome.ConstraintNotFound;

        string[] droppedColumns;
        var connection = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        NpgsqlTransaction? tx = null;
        try
        {
            if (!await TableExistsAsync(connection, tableName!.Value).ConfigureAwait(false))
                return DropUniqueConstraintOutcome.DesignerNotFound;

            // Resolve the constraint by name and assert it is a UNIQUE constraint on
            // THIS table. This blocks dropping the primary key, FK constraints, or a
            // constraint belonging to another table via a hand-crafted request.
            var match = (await GetUniqueConstraintsAsync(connection, tableName.Value).ConfigureAwait(false))
                .FirstOrDefault(c => string.Equals(c.Name, safeName!.Value, StringComparison.Ordinal));
            if (match is null)
            {
                // Distinguish "no such constraint" from "exists but not UNIQUE" for a
                // clearer error.
                var anyContype = await ConstraintExistsAsync(connection, tableName.Value, safeName!.Value)
                    .ConfigureAwait(false);
                return anyContype
                    ? DropUniqueConstraintOutcome.NotUniqueConstraint
                    : DropUniqueConstraintOutcome.ConstraintNotFound;
            }
            droppedColumns = [.. match.Columns];

            var sql = string.Create(CultureInfo.InvariantCulture,
                $"ALTER TABLE {tableName.Value} DROP CONSTRAINT {safeName!.Value}");

            tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await connection.ExecuteAsync(
                sql,
                transaction: tx,
                commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds)
                .ConfigureAwait(false);
            await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);

            LogDroppedConstraint(logger, safeName.Value, tableName.Value);
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

        db.SchemaAuditLog.Add(new SchemaAuditLogEntry
        {
            DesignerId = designerId,
            FromVersion = null,
            ToVersion = 0,
            DdlOperation = "DROP_UQ",
            ColumnsDropped = droppedColumns,
            CorrelationId = Ulid.NewUlid().ToString(),
            ActorId = actorId,
            CreatedAt = DateTimeOffset.UtcNow,
            Notes = safeName.Value,
        });
        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        return DropUniqueConstraintOutcome.Success;
    }

    // ---- Helpers ----------------------------------------------------------

    // uq_{table}_{col1}_{col2}… truncated to PostgreSQL's 63-char identifier limit.
    // If the (possibly truncated) base collides with an existing constraint name,
    // a numeric suffix is appended — also kept within 63 chars.
    private static string BuildConstraintName(
        string tableName, IReadOnlyList<string> columns, IEnumerable<string> existingNames)
    {
        const int MaxLen = 63;
        var taken = new HashSet<string>(existingNames, StringComparer.Ordinal);

        var sb = new StringBuilder("uq_").Append(tableName);
        foreach (var col in columns)
            sb.Append('_').Append(col);
        var baseName = sb.ToString();
        if (baseName.Length > MaxLen) baseName = baseName[..MaxLen];

        if (!taken.Contains(baseName)) return baseName;

        for (var i = 2; i < 1000; i++)
        {
            var suffix = "_" + i.ToString(CultureInfo.InvariantCulture);
            var trimmed = baseName.Length + suffix.Length > MaxLen
                ? baseName[..(MaxLen - suffix.Length)]
                : baseName;
            var candidate = trimmed + suffix;
            if (!taken.Contains(candidate)) return candidate;
        }

        // Practically unreachable — 998 same-named constraints on one table.
        return baseName;
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string tableName)
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

    // Constrainable columns: every physical column except those in
    // NonConstrainableColumns (the `id` primary key and audit / soft-delete / cascade
    // bookkeeping columns). parent_*_id FK columns are intentionally included.
    private static async Task<List<UserColumn>> GetUserColumnsAsync(
        NpgsqlConnection connection, string tableName)
    {
        const string sql = """
            SELECT column_name AS ColumnName, data_type AS DataType
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = @tableName
            ORDER BY ordinal_position
            """;
        var all = await connection.QueryAsync<UserColumn>(
            sql,
            new { tableName },
            commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds)
            .ConfigureAwait(false);

        return all
            .Where(c => !NonConstrainableColumns.Contains(c.ColumnName))
            .ToList();
    }

    // All UNIQUE constraints on the table with their ordered column lists. Uses
    // pg_constraint (contype = 'u'); columns are resolved via pg_attribute and
    // ordered by their position in conkey.
    private static async Task<List<UniqueConstraintInfo>> GetUniqueConstraintsAsync(
        NpgsqlConnection connection, string tableName)
    {
        const string sql = """
            SELECT con.conname AS Name, att.attname AS ColumnName, u.ord AS Ord
            FROM pg_constraint con
            JOIN pg_class rel ON rel.oid = con.conrelid
            JOIN pg_namespace nsp ON nsp.oid = rel.relnamespace
            CROSS JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS u(attnum, ord)
            JOIN pg_attribute att ON att.attrelid = rel.oid AND att.attnum = u.attnum
            WHERE nsp.nspname = 'public'
              AND rel.relname = @tableName
              AND con.contype = 'u'
            ORDER BY con.conname, u.ord
            """;
        var rows = await connection.QueryAsync<ConstraintColumnRow>(
            sql,
            new { tableName },
            commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds)
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => r.Name, StringComparer.Ordinal)
            .Select(g => new UniqueConstraintInfo(
                g.Key,
                g.OrderBy(r => r.Ord).Select(r => r.ColumnName).ToList()))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
    }

    // True if a constraint with this name exists on the table regardless of type
    // (used to distinguish "not found" from "found but not UNIQUE").
    private static async Task<bool> ConstraintExistsAsync(
        NpgsqlConnection connection, string tableName, string constraintName)
    {
        const string sql = """
            SELECT COUNT(1) > 0
            FROM pg_constraint con
            JOIN pg_class rel ON rel.oid = con.conrelid
            JOIN pg_namespace nsp ON nsp.oid = rel.relnamespace
            WHERE nsp.nspname = 'public'
              AND rel.relname = @tableName
              AND con.conname = @constraintName
            """;
        return await connection.ExecuteScalarAsync<bool>(
            sql,
            new { tableName, constraintName },
            commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds)
            .ConfigureAwait(false);
    }

    private sealed record UserColumn(string ColumnName, string DataType);

    private sealed record ConstraintColumnRow(string Name, string ColumnName, long Ord);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Added UNIQUE constraint '{ConstraintName}' on '{TableName}' ({Columns})")]
    private static partial void LogAddedConstraint(
        ILogger logger, string constraintName, string tableName, string columns);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Dropped UNIQUE constraint '{ConstraintName}' from '{TableName}'")]
    private static partial void LogDroppedConstraint(
        ILogger logger, string constraintName, string tableName);
}
