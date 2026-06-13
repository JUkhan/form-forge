using System.Collections.ObjectModel;
using Dapper;
using FormForge.Api.Features.Datasets.Dtos;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Memory;

namespace FormForge.Api.Features.Datasets;

internal interface IDatasetAllowlist
{
    Task<CatalogDto> GetCatalogAsync(CancellationToken ct);
    // One allowlisted table's columns, fetched lazily (null if not allowed / not found).
    Task<TableColumnsDto?> GetTableColumnsAsync(string tableName, CancellationToken ct);
    bool IsAllowed(string tableName);
}

// Story 9.1 (FR-63 / AR-62) + dynamic-allowlist enhancement (2026-06-05).
//
// The dataset catalog is now derived DYNAMICALLY from the database: every base table in
// the `public` schema is queryable in the Query Builder EXCEPT the excluded tables (the
// hardcoded SystemTables floor plus DatasetManager:ExcludedTables) (AR-57). This means
// tables provisioned by the Designer (Epic 5) appear automatically — no config edit +
// restart needed.
//
// `DatasetManager:AllowedTables` is an OPTIONAL restrict-list: when non-empty it further
// narrows the exposed set to exactly those names (still minus the excluded set), for
// operators who want to expose only a curated subset. When empty (the default) every
// non-excluded base table is exposed.
//
// `DatasetManager:ExcludedTables` is an OPTIONAL extra denylist, UNIONed onto the
// hardcoded SystemTables floor — config can ADD exclusions but never remove a built-in
// one, so an operator can hide more tables but never expose an internal table. Catalog
// results are cached for 5 minutes.
//
// IsAllowed (the synchronous security gate inside DatasetSqlGenerator) is a PURE POLICY
// check — "not a system table, and within the restrict-list if one is configured". It does
// NOT verify the table exists: a policy-allowed name that is not a real table simply fails
// later at CREATE VIEW / query execution (→ INVALID_QUERY), which is the correct layer for
// that error. Keeping it pure also keeps it synchronous and DB-free.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed partial class DatasetAllowlist : IDatasetAllowlist
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private const string CacheKey = "datasets.catalog";

    // Built-in framework + internal tables that must NEVER be exposed as dataset sources,
    // even though they live in the `public` schema (AR-57). This is the SECURITY FLOOR and
    // is intentionally hardcoded: operators can ADD to it via DatasetManager:ExcludedTables
    // (see _excludedTables) but can never remove an entry here. Superset of
    // DatasetName.PermanentDenylist (the 14 core internal tables, duplicated here because
    // that list is private to DatasetName) PLUS the remaining EF-mapped/framework tables
    // that the 14-name list omits (role_permissions, component_schema_versions) and the
    // two PascalCase framework tables (Data Protection keys, EF migrations history).
    // Ordinal comparison: "DataProtectionKeys"/"__EFMigrationsHistory" are case-sensitive
    // quoted identifiers in PostgreSQL.
    private static readonly HashSet<string> SystemTables = new(StringComparer.Ordinal)
    {
        // Core internal tables. NOTE: "users" is intentionally NOT excluded here (by
        // request) — it is exposed to the Query Builder, unlike DatasetName's name
        // validator which still blocks "users" as a dataset name.
        "roles", "user_roles", "menus", "menu_role_assignments",
        "component_schemas", "refresh_tokens", "password_reset_tokens",
        "mfa_backup_codes", "mfa_sessions", "schema_audit_log",
        "mutation_audit_log", "dataset_audit_log", "custom_dataset",
        // Remaining EF-mapped internal tables not in the 14-name list.
        "role_permissions", "component_schema_versions",
        // ASP.NET Core / EF Core framework tables (default PascalCase names).
        "DataProtectionKeys", "__EFMigrationsHistory",
    };

    // Per-table COLUMN allowlist. When a table appears here, ONLY these columns are exposed
    // in the catalog — the rest are hidden from the Query Builder palette. `users` is exposed
    // as a join source (a record's created_by/updated_by → display name / email) but its
    // sensitive columns (password_hash, MFA secrets, theme preference, timestamps, …) must
    // never surface. This is the UX/anti-leak layer; the DB-layer backstop is the column-level
    // GRANT to formforge_preview in migration 20260605164457_RestrictPreviewRoleUsersColumns —
    // the two lists MUST stay in sync (a column added here but not granted there would appear
    // in the palette yet 42501 at preview, and vice-versa). Ordinal; lowercase snake_case.
    private static readonly Dictionary<string, HashSet<string>> RestrictedColumns =
        new(StringComparer.Ordinal)
        {
            ["users"] = new(StringComparer.Ordinal) { "id", "display_name", "email", "is_active" },
        };

    // Effective exclusion set = built-in SystemTables UNION DatasetManager:ExcludedTables.
    // Config can only ADD exclusions (never remove a built-in one), so the security floor
    // holds. Used by both the catalog projection and the IsAllowed gate.
    private readonly HashSet<string> _excludedTables;
    private readonly ReadOnlyCollection<string> _configRestrictList;
    private readonly IMemoryCache _cache;
    private readonly DbConnectionFactory _db;
    private readonly ILogger<DatasetAllowlist> _logger;

    public DatasetAllowlist(
        IConfiguration configuration,
        IMemoryCache cache,
        DbConnectionFactory db,
        ILogger<DatasetAllowlist> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _cache = cache;
        _db = db;
        _logger = logger;

        // Operator-configured extra exclusions, unioned onto the hardcoded floor.
        var configExcluded = configuration
            .GetSection("DatasetManager:ExcludedTables")
            .Get<string[]>() ?? [];
        _excludedTables = new HashSet<string>(SystemTables, StringComparer.Ordinal);
        _excludedTables.UnionWith(configExcluded);

        var configured = configuration
            .GetSection("DatasetManager:AllowedTables")
            .Get<string[]>() ?? [];

        // Strip excluded tables from the restrict-list regardless of config (AR-57): if a
        // name is in both AllowedTables and the exclusion set, exclusion wins.
        _configRestrictList = configured
            .Where(t => !_excludedTables.Contains(t))
            .ToList()
            .AsReadOnly();

        if (_configRestrictList.Count < configured.Length && logger.IsEnabled(LogLevel.Warning))
        {
            var stripped = configured.Where(t => _excludedTables.Contains(t)).ToArray();
            // CA1873: the IsEnabled guard above already prevents the string.Join cost when
            // Warning logging is off, and this runs once at construction on misconfiguration.
#pragma warning disable CA1873
            LogStrippedInternalTables(logger, string.Join(", ", stripped));
#pragma warning restore CA1873
        }
    }

    // Pure policy gate (see class remarks): allowed iff not a system table and, when a
    // restrict-list is configured, present in it. No DB access, no existence check.
    public bool IsAllowed(string tableName) =>
        !_excludedTables.Contains(tableName)
        && (_configRestrictList.Count == 0 || _configRestrictList.Contains(tableName, StringComparer.Ordinal));

    public async Task<CatalogDto> GetCatalogAsync(CancellationToken ct)
    {
        return await _cache.GetOrCreateAsync(
            CacheKey,
            entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheTtl;
                return BuildCatalogAsync(ct);
            }).ConfigureAwait(false) ?? new CatalogDto([]);
    }

    private async Task<CatalogDto> BuildCatalogAsync(CancellationToken ct)
    {
        // Manual try/finally rather than `await using` because this project enforces
        // CA2007 and a bare `await using` flags it (mirrors DatasetService.GetByIdAsync).
        var conn = await _db.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            // Step 1 — discover every base table in the public schema, then drop the
            // framework/internal tables. The remainder is the dynamic allowlist (every
            // Designer-provisioned table appears automatically). VIEWs are excluded, so a
            // dataset's own backing view is never offered as a join source.
            const string tablesSql = """
                SELECT table_name
                FROM   information_schema.tables
                WHERE  table_schema = 'public'
                  AND  table_type   = 'BASE TABLE'
                """;

            var discovered = await conn.QueryAsync<string>(
                new CommandDefinition(tablesSql, cancellationToken: ct)).ConfigureAwait(false);

            IEnumerable<string> allowed = discovered.Where(t => !_excludedTables.Contains(t));

            // Optional operator restrict-list: narrow to exactly the configured names.
            if (_configRestrictList.Count > 0)
            {
                var restrict = _configRestrictList.ToHashSet(StringComparer.Ordinal);
                allowed = allowed.Where(restrict.Contains);
            }

            var allowedNames = allowed.ToHashSet(StringComparer.Ordinal);

            // Restrict-list entries that don't exist as a base table are simply absent
            // from the catalog; warn so a typo'd config name is diagnosable (AR-62).
            if (_configRestrictList.Count > 0)
            {
                foreach (var t in _configRestrictList.Where(t => !allowedNames.Contains(t)))
                {
                    LogTableNotFound(_logger, t);
                }
            }

            if (allowedNames.Count == 0)
                return new CatalogDto([]);

            // Step 2 — column COUNTS only (not the columns themselves), so the list stays
            // small with thousands of tables. Columns are fetched per-table on demand via
            // GetTableColumnsAsync. Npgsql resolves string[] → text[] so ANY(@allowlist)
            // binds safely; count(*) is cast to int for the DTO's ColumnCount.
            const string countsSql = """
                SELECT table_name AS "TableName", count(*)::int AS "ColumnCount"
                FROM   information_schema.columns
                WHERE  table_schema = 'public'
                  AND  table_name   = ANY(@allowlist)
                GROUP  BY table_name
                ORDER  BY table_name
                """;

            var tables = (await conn.QueryAsync<CatalogTableDto>(
                new CommandDefinition(countsSql,
                    parameters: new { allowlist = allowedNames.ToArray() },
                    cancellationToken: ct))
                .ConfigureAwait(false))
                // For column-restricted tables, report the exposed column count (not the raw
                // table total) so the palette stays consistent with GetTableColumnsAsync.
                .Select(t => RestrictedColumns.TryGetValue(t.TableName, out var cols)
                    ? t with { ColumnCount = cols.Count }
                    : t)
                .ToList();

            return new CatalogDto(tables);
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Lazily fetch one allowlisted table's columns (the palette loads names only; columns
    // are pulled when a table is dragged onto the canvas). Returns null when the table is
    // not allowed (system/excluded/not-in-restrict-list) or does not exist — IsAllowed is
    // checked FIRST so this never leaks columns of an internal table. @table is bound, not
    // interpolated, so the user-supplied name cannot inject SQL.
    public async Task<TableColumnsDto?> GetTableColumnsAsync(string tableName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        if (!IsAllowed(tableName))
            return null;

        var conn = await _db.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            const string sql = """
                SELECT column_name AS "ColumnName",
                       data_type   AS "PgType",
                       is_nullable AS "IsNullable"
                FROM   information_schema.columns
                WHERE  table_schema = 'public'
                  AND  table_name   = @table
                ORDER  BY ordinal_position
                """;

            var columns = (await conn.QueryAsync<CatalogColumnRow>(
                new CommandDefinition(sql, new { table = tableName }, cancellationToken: ct))
                .ConfigureAwait(false))
                .Select(r => new CatalogColumnDto(
                    r.ColumnName,
                    r.PgType,
                    string.Equals(r.IsNullable, "YES", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            // Apply the per-table column allowlist: for restricted tables (e.g. `users`),
            // expose only the sanctioned columns so sensitive ones never reach the palette.
            if (RestrictedColumns.TryGetValue(tableName, out var allowedColumns))
            {
                columns = columns
                    .Where(c => allowedColumns.Contains(c.ColumnName))
                    .ToList();
            }

            return columns.Count == 0 ? null : new TableColumnsDto(tableName, columns);
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Per-column projection of information_schema.columns for GetTableColumnsAsync.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by Dapper.")]
    private sealed record CatalogColumnRow(string ColumnName, string PgType, string IsNullable);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "DatasetManager:AllowedTables contains internal table names that were stripped: {Tables}")]
    private static partial void LogStrippedInternalTables(ILogger logger, string tables);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "DatasetManager:AllowedTables restrict-list entry '{Table}' was not found among the " +
                  "public base tables; it will be absent from the catalog.")]
    private static partial void LogTableNotFound(ILogger logger, string table);
}
