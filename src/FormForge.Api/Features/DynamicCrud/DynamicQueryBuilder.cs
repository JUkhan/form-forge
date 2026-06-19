using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using Dapper;
using FormForge.Api.Features.Designer;
using FormForge.Api.Features.SchemaRegistry;

namespace FormForge.Api.Features.DynamicCrud;

// Story 6.1 — pure SQL assembly for /api/data/{designerId} GET. No DI deps, no
// EF/Dapper imports beyond the DynamicParameters value type, so the unit tests
// drive the builder without a Postgres fixture. The caller (DynamicDataEndpoints)
// is responsible for validating identifiers and whitelisting client-supplied
// filter/sort keys before invoking the build methods.
internal static class DynamicQueryBuilder
{
    // AR-46 Option C — system column PG name (used inside SQL) and JSON name
    // (used in HTTP query strings via filter[...]). The builder rejects any
    // client key that is not present as either a system JSON name or a known
    // user fieldKey — see MapClientFilterKeyToPgName.
    private static readonly FrozenDictionary<string, string> SystemFilterJsonToPg =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "id",
            ["isDeleted"] = "is_deleted",
            ["createdAt"] = "created_at",
            ["createdBy"] = "created_by",
            ["updatedAt"] = "updated_at",
            ["updatedBy"] = "updated_by",
            ["cascadeEventId"] = "cascade_event_id",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    // AC-2 — sort whitelist always includes these four system PG columns.
    internal static readonly FrozenSet<string> SystemSortPgColumns =
        new[] { "id", "created_at", "updated_at", "is_deleted" }
            .ToFrozenSet(StringComparer.Ordinal);

    // AC-3 — filter whitelist always includes these five system PG columns.
    internal static readonly FrozenSet<string> SystemFilterPgColumns =
        new[] { "id", "created_at", "updated_at", "is_deleted", "created_by" }
            .ToFrozenSet(StringComparer.Ordinal);

    // SELECT column order — fixed so query output and tests are deterministic.
    // Mirrors the column order produced by DdlEmitter.CreateTableCoreAsync.
    private static readonly string[] SystemColumnsInSelectOrder =
    [
        "id", "created_at", "created_by", "updated_at", "updated_by", "is_deleted", "cascade_event_id",
    ];

    // Max number of sort pairs accepted in one request (AC-2).
    internal const int MaxSortPairs = 3;

    // Valid PostgreSQL boolean string literals (case-insensitive superset of C# bool.TryParse).
    private static readonly FrozenSet<string> PostgresBoolLiterals =
        new[] { "true", "false", "yes", "no", "on", "off", "1", "0" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Validates a raw filter value for typed system columns before it reaches the DB.
    // Returns false + a user-readable error when the value would cause a PostgreSQL
    // runtime type error (e.g. "foo" for a BOOLEAN column). User fieldKeys pass through
    // unchanged — PostgreSQL's implicit cast handles valid-but-untyped strings.
    internal static bool TryValidateFilterValue(string pgName, string rawValue, out string? error)
    {
        switch (pgName)
        {
            case "is_deleted":
                if (!PostgresBoolLiterals.Contains(rawValue))
                {
                    error = $"Value '{rawValue}' is not a valid boolean; expected true, false, yes, no, on, off, 1, or 0.";
                    return false;
                }
                break;
            case "id" or "created_by":
                if (!Guid.TryParse(rawValue, out _))
                {
                    error = $"Value '{rawValue}' is not a valid UUID.";
                    return false;
                }
                break;
            // Parent-FK columns (parent_<name>_id) resolve to UUID in
            // ResolveFilterPgType, so AppendFilterPredicate will Guid.Parse the
            // value. Validate it here for a clean 400 instead of an unhandled
            // FormatException 500 (e.g. a cascade dropdown sending a stale value).
            default:
                if (IsReservedParentFkColumn(pgName) && !Guid.TryParse(rawValue, out _))
                {
                    error = $"Value '{rawValue}' is not a valid UUID.";
                    return false;
                }
                break;
        }
        error = null;
        return true;
    }

    // Maps a client-supplied filter[] key to the underlying PG column name.
    // System camelCase keys (isDeleted, createdAt, ...) are translated; everything
    // else passes through verbatim (user fieldKeys are validated SafeIdentifier
    // values that are also the PG column names by construction).
    internal static string MapClientFilterKeyToPgName(string clientKey) =>
        SystemFilterJsonToPg.TryGetValue(clientKey, out var pg) ? pg : clientKey;

    internal sealed record SortParam(string Column, string Direction);

    internal sealed record ParseSortResult(
        bool IsSuccess,
        IReadOnlyList<SortParam> Sorts,
        string? ErrorMessage)
    {
        internal static ParseSortResult Success(IReadOnlyList<SortParam> sorts) =>
            new(true, sorts, null);

        internal static ParseSortResult Failure(string message) =>
            new(false, [], message);
    }

    // AC-2 — parses "col1:asc,col2:desc" into a normalized list. Returns Success
    // with an empty list when sortParam is null/whitespace (handler applies the
    // default ORDER BY in that case).
    internal static ParseSortResult ParseSort(string? sortParam, IReadOnlySet<string> allowedColumns)
    {
        ArgumentNullException.ThrowIfNull(allowedColumns);

        if (string.IsNullOrWhiteSpace(sortParam))
            return ParseSortResult.Success([]);

        var tokens = sortParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length > MaxSortPairs)
            return ParseSortResult.Failure(
                $"At most {MaxSortPairs} sort columns are allowed; received {tokens.Length}.");

        var sorts = new List<SortParam>(tokens.Length);
        foreach (var token in tokens)
        {
            var parts = token.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                return ParseSortResult.Failure(
                    $"Sort token '{token}' is not in 'column:direction' form.");

            var column = parts[0];
            var direction = parts[1].ToUpperInvariant();
            if (direction is not ("ASC" or "DESC"))
                return ParseSortResult.Failure(
                    $"Sort direction '{parts[1]}' is invalid; expected 'asc' or 'desc'.");

            if (!allowedColumns.Contains(column))
                return ParseSortResult.Failure(
                    $"Sort column '{column}' is not a known field on this resource.");

            sorts.Add(new SortParam(column, direction));
        }

        return ParseSortResult.Success(sorts);
    }

    // AC-1, AC-3, AC-5, AC-6 — assemble the paginated SELECT. The caller MUST have:
    //   * validated tableName via SafeIdentifier (passed in via the typed wrapper)
    //   * mapped filter keys to PG names via MapClientFilterKeyToPgName
    //   * whitelisted every key in `filters` against the allowed filter column set
    //   * passed `sorts` whose Column values came from ParseSort (already whitelisted)
    // The builder does NOT re-validate identifiers; everything reaching this method
    // is assumed safe for double-quoted interpolation.
    internal static (string Sql, DynamicParameters Parameters) BuildSelectQuery(
        SafeIdentifier tableName,
        IReadOnlyList<ColumnDefinition> userColumns,
        IReadOnlyList<SortParam> sorts,
        IReadOnlyDictionary<string, string> filters,
        int page,
        int pageSize,
        IReadOnlyList<DerivedColumn>? derivedColumns = null)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(userColumns);
        ArgumentNullException.ThrowIfNull(sorts);
        ArgumentNullException.ThrowIfNull(filters);

        var sb = new StringBuilder();
        sb.Append("SELECT ");
        AppendSelectColumns(sb, userColumns);
        AppendDerivedColumns(sb, tableName, derivedColumns);
        sb.Append(CultureInfo.InvariantCulture, $" FROM \"{tableName.Value}\"");

        var parameters = new DynamicParameters();
        AppendWhereClause(sb, parameters, filters, userColumns);
        AppendOrderByClause(sb, sorts);

        var offset = (long)(page - 1) * pageSize;
        parameters.Add("p_limit", pageSize);
        parameters.Add("p_offset", offset);
        sb.Append(" LIMIT @p_limit OFFSET @p_offset");

        return (sb.ToString(), parameters);
    }

    // Story 7-followup — assembles a full-result SELECT for export. Same
    // WHERE/ORDER BY shape as BuildSelectQuery (so filter + sort honour the
    // user's UI state) but with no LIMIT/OFFSET — the caller streams the
    // entire result set to a file. Caller is responsible for capping the
    // total row count via BuildCountQuery before invoking this, so a hostile
    // or accidental "export every row of a 50M-row table" gets a 422 long
    // before this SQL runs.
    internal static (string Sql, DynamicParameters Parameters) BuildExportQuery(
        SafeIdentifier tableName,
        IReadOnlyList<ColumnDefinition> userColumns,
        IReadOnlyList<SortParam> sorts,
        IReadOnlyDictionary<string, string> filters,
        IReadOnlyList<DerivedColumn>? derivedColumns = null)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(userColumns);
        ArgumentNullException.ThrowIfNull(sorts);
        ArgumentNullException.ThrowIfNull(filters);

        var sb = new StringBuilder();
        sb.Append("SELECT ");
        AppendSelectColumns(sb, userColumns);
        AppendDerivedColumns(sb, tableName, derivedColumns);
        sb.Append(CultureInfo.InvariantCulture, $" FROM \"{tableName.Value}\"");

        var parameters = new DynamicParameters();
        AppendWhereClause(sb, parameters, filters, userColumns);
        AppendOrderByClause(sb, sorts);

        return (sb.ToString(), parameters);
    }

    // AC-1, AC-3, AC-6 — COUNT query for `total`. Uses the same WHERE clause as
    // the SELECT so the count matches the paginated rows.
    internal static (string Sql, DynamicParameters Parameters) BuildCountQuery(
        SafeIdentifier tableName,
        IReadOnlyList<ColumnDefinition> userColumns,
        IReadOnlyDictionary<string, string> filters)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(userColumns);
        ArgumentNullException.ThrowIfNull(filters);

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"SELECT COUNT(*) FROM \"{tableName.Value}\"");

        var parameters = new DynamicParameters();
        AppendWhereClause(sb, parameters, filters, userColumns);

        return (sb.ToString(), parameters);
    }

    // Designer-backed dropdown options: SELECT DISTINCT of ONLY the value + label
    // columns (no other record data is exposed). `filters` carries the cascading
    // predicates (+ the implicit is_deleted=false). `search`, when present, does a
    // substring match against the label column (cast to text so any column type
    // works). Ordered by label then value for a stable, human-friendly list.
    // valueColumn/labelColumn are pre-validated against the registry column set
    // (or the "id" system column) by the caller, so quoting them is injection-safe.
    internal static (string Sql, DynamicParameters Parameters) BuildOptionsQuery(
        SafeIdentifier tableName,
        string valueColumn,
        string labelColumn,
        IReadOnlyList<ColumnDefinition> userColumns,
        IReadOnlyDictionary<string, string> filters,
        string? search,
        int page,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentException.ThrowIfNullOrEmpty(valueColumn);
        ArgumentException.ThrowIfNullOrEmpty(labelColumn);
        ArgumentNullException.ThrowIfNull(userColumns);
        ArgumentNullException.ThrowIfNull(filters);

        var parameters = new DynamicParameters();
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"SELECT DISTINCT \"{valueColumn}\" AS value, \"{labelColumn}\" AS label FROM \"{tableName.Value}\"");
        AppendWhereClause(sb, parameters, filters, userColumns);
        AppendOptionsSearch(sb, parameters, labelColumn, search, filters.Count > 0);
        sb.Append(" ORDER BY label, value");

        var offset = (long)(page - 1) * pageSize;
        parameters.Add("p_limit", pageSize);
        parameters.Add("p_offset", offset);
        sb.Append(" LIMIT @p_limit OFFSET @p_offset");

        return (sb.ToString(), parameters);
    }

    internal static (string Sql, DynamicParameters Parameters) BuildOptionsCountQuery(
        SafeIdentifier tableName,
        string valueColumn,
        string labelColumn,
        IReadOnlyList<ColumnDefinition> userColumns,
        IReadOnlyDictionary<string, string> filters,
        string? search)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentException.ThrowIfNullOrEmpty(valueColumn);
        ArgumentException.ThrowIfNullOrEmpty(labelColumn);
        ArgumentNullException.ThrowIfNull(userColumns);
        ArgumentNullException.ThrowIfNull(filters);

        var parameters = new DynamicParameters();
        var inner = new StringBuilder();
        inner.Append(CultureInfo.InvariantCulture,
            $"SELECT DISTINCT \"{valueColumn}\", \"{labelColumn}\" FROM \"{tableName.Value}\"");
        AppendWhereClause(inner, parameters, filters, userColumns);
        AppendOptionsSearch(inner, parameters, labelColumn, search, filters.Count > 0);

        return ($"SELECT COUNT(*) FROM ({inner}) AS sub", parameters);
    }

    // Appends the label substring predicate (if `search` is non-empty), joining
    // with AND/WHERE depending on whether a WHERE clause already exists.
    private static void AppendOptionsSearch(
        StringBuilder sb, DynamicParameters parameters, string labelColumn, string? search, bool whereExists)
    {
        if (string.IsNullOrWhiteSpace(search)) return;
        sb.Append(whereExists ? " AND " : " WHERE ");
        sb.Append(CultureInfo.InvariantCulture, $"\"{labelColumn}\"::text ILIKE @p_search");
        parameters.Add("p_search", "%" + EscapeLikeWildcards(search.Trim()) + "%");
    }

    // Story 6.2 — single-row SELECT for GET /api/data/{designerId}/{id}.
    // Same column list as BuildSelectQuery (system + user, in CREATE TABLE order)
    // but no ORDER BY / LIMIT / OFFSET. id parameter is typed so PG sees UUID, not text.
    internal static (string Sql, DynamicParameters Parameters) BuildGetByIdQuery(
        SafeIdentifier tableName,
        IReadOnlyList<ColumnDefinition> userColumns,
        Guid id)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(userColumns);

        var sb = new StringBuilder();
        sb.Append("SELECT ");
        AppendSelectColumns(sb, userColumns);
        sb.Append(CultureInfo.InvariantCulture, $" FROM \"{tableName.Value}\" WHERE \"id\" = @p_id");

        var parameters = new DynamicParameters();
        parameters.Add("p_id", id);
        return (sb.ToString(), parameters);
    }

    // Story 6.2 — child-rows SELECT for ?include=children. Filters by the parent FK column
    // (e.g. parent_my_form_id) which is itself a validated PG identifier produced by
    // BuildFkColumnName below. Same column list pattern as BuildGetByIdQuery; no ORDER BY.
    internal static (string Sql, DynamicParameters Parameters) BuildGetChildrenQuery(
        SafeIdentifier childTableName,
        IReadOnlyList<ColumnDefinition> childColumns,
        string fkColumnName,
        Guid parentId)
    {
        ArgumentNullException.ThrowIfNull(childTableName);
        ArgumentNullException.ThrowIfNull(childColumns);
        ArgumentException.ThrowIfNullOrEmpty(fkColumnName);

        var sb = new StringBuilder();
        sb.Append("SELECT ");
        AppendSelectColumns(sb, childColumns);
        // Exclude soft-deleted child rows — the parent's `children` collection is
        // the live set the data-entry form edits. Without this guard, soft-deleted
        // rows resurface in the form and a save would re-submit them.
        sb.Append(CultureInfo.InvariantCulture,
            $" FROM \"{childTableName.Value}\" WHERE \"{fkColumnName}\" = @p_parent_id AND \"is_deleted\" = false");

        var parameters = new DynamicParameters();
        parameters.Add("p_parent_id", parentId);
        return (sb.ToString(), parameters);
    }

    // TreeView — one level of a self-referencing adjacency-list tree. Returns the rows
    // whose parent FK (parent_<table>_id) equals parentId (or IS NULL for root nodes),
    // plus a derived "_has_children" boolean (live direct children exist) so the UI can
    // render an expand affordance without an extra round-trip. Optional `search` matches
    // ANY user column (cast to text) with a case-insensitive substring — the fields the
    // author placed in the node template. Caller fetches `limit` = pageSize + 1 to detect
    // a next page (drops the extra row). fkColumnName is produced by BuildFkColumnName.
    internal static (string Sql, DynamicParameters Parameters) BuildTreeLevelQuery(
        SafeIdentifier tableName,
        IReadOnlyList<ColumnDefinition> userColumns,
        string fkColumnName,
        Guid? parentId,
        string? search,
        int limit,
        long offset,
        // Auth filter: when both set, scope to rows the user owns (ownerColumn = ownerId).
        // ownerColumn is a caller-validated user fieldKey (SafeIdentifier), safe to quote.
        string? ownerColumn = null,
        Guid? ownerId = null)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(userColumns);
        ArgumentException.ThrowIfNullOrEmpty(fkColumnName);

        var sb = new StringBuilder();
        sb.Append("SELECT ");
        AppendSelectColumns(sb, userColumns);
        // EXISTS subquery → does this node have any live direct children?
        sb.Append(CultureInfo.InvariantCulture,
            $", EXISTS(SELECT 1 FROM \"{tableName.Value}\" c WHERE c.\"{fkColumnName}\" = " +
            $"\"{tableName.Value}\".\"id\" AND c.\"is_deleted\" = false) AS \"_has_children\"");
        sb.Append(CultureInfo.InvariantCulture, $" FROM \"{tableName.Value}\"");

        var parameters = new DynamicParameters();
        sb.Append(" WHERE \"is_deleted\" = false");
        if (parentId.HasValue)
        {
            sb.Append(CultureInfo.InvariantCulture, $" AND \"{fkColumnName}\" = @p_parent_id");
            parameters.Add("p_parent_id", parentId.Value);
        }
        else
        {
            sb.Append(CultureInfo.InvariantCulture, $" AND \"{fkColumnName}\" IS NULL");
        }

        if (!string.IsNullOrEmpty(ownerColumn) && ownerId.HasValue)
        {
            sb.Append(CultureInfo.InvariantCulture, $" AND \"{ownerColumn}\" = @p_owner_id");
            parameters.Add("p_owner_id", ownerId.Value);
        }

        AppendTreeSearch(sb, parameters, userColumns, search);

        // created_at ASC = stable insertion order within a level.
        sb.Append(" ORDER BY \"created_at\" ASC");
        parameters.Add("p_limit", limit);
        parameters.Add("p_offset", offset);
        sb.Append(" LIMIT @p_limit OFFSET @p_offset");

        return (sb.ToString(), parameters);
    }

    // TreeView "All select" — every (live) descendant id of a node, via a recursive CTE
    // over the self-FK. UNION (not UNION ALL) dedups by id so a corrupt cyclic parent
    // chain still terminates. Excludes the parent itself; the caller adds it. fkColumnName
    // is produced by BuildFkColumnName.
    internal static (string Sql, DynamicParameters Parameters) BuildTreeDescendantIdsQuery(
        SafeIdentifier tableName,
        string fkColumnName,
        Guid parentId,
        // Auth filter: when both set, only the user's own descendants are returned.
        string? ownerColumn = null,
        Guid? ownerId = null)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentException.ThrowIfNullOrEmpty(fkColumnName);

        var t = tableName.Value;
        var scoped = !string.IsNullOrEmpty(ownerColumn) && ownerId.HasValue;
        var anchorOwner = scoped ? $" AND \"{ownerColumn}\" = @p_owner_id" : string.Empty;
        var recurseOwner = scoped ? $" AND c.\"{ownerColumn}\" = @p_owner_id" : string.Empty;
        var sql = string.Create(CultureInfo.InvariantCulture, $"""
            WITH RECURSIVE descendants AS (
                SELECT "id" FROM "{t}" WHERE "{fkColumnName}" = @p_parent_id AND "is_deleted" = false{anchorOwner}
                UNION
                SELECT c."id" FROM "{t}" c
                  JOIN descendants d ON c."{fkColumnName}" = d."id"
                WHERE c."is_deleted" = false{recurseOwner}
            )
            SELECT "id" FROM descendants
            """);

        var parameters = new DynamicParameters();
        parameters.Add("p_parent_id", parentId);
        if (scoped) parameters.Add("p_owner_id", ownerId!.Value);
        return (sql, parameters);
    }

    // TreeView ancestor reveal — given a set of (selected) node ids, returns the ids of
    // ALL their ancestors by walking UP the self-FK, EXCLUDING the seed ids themselves.
    // Lets the UI mark collapsed ancestors that contain a selection (and auto-expand the
    // path to one). fkColumnName is produced by BuildFkColumnName.
    internal static (string Sql, DynamicParameters Parameters) BuildTreeAncestorIdsQuery(
        SafeIdentifier tableName,
        string fkColumnName,
        IReadOnlyList<Guid> selectedIds,
        // Auth filter: when both set, only the user's own ancestors are walked.
        string? ownerColumn = null,
        Guid? ownerId = null)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentException.ThrowIfNullOrEmpty(fkColumnName);
        ArgumentNullException.ThrowIfNull(selectedIds);

        var t = tableName.Value;
        var scoped = !string.IsNullOrEmpty(ownerColumn) && ownerId.HasValue;
        var anchorOwner = scoped ? $" AND \"{ownerColumn}\" = @p_owner_id" : string.Empty;
        var recurseOwner = scoped ? $" AND p.\"{ownerColumn}\" = @p_owner_id" : string.Empty;
        var sql = string.Create(CultureInfo.InvariantCulture, $"""
            WITH RECURSIVE ancestors AS (
                SELECT "id", "{fkColumnName}" AS parent_id FROM "{t}"
                  WHERE "id" = ANY(@p_ids) AND "is_deleted" = false{anchorOwner}
                UNION
                SELECT p."id", p."{fkColumnName}" AS parent_id FROM "{t}" p
                  JOIN ancestors a ON p."id" = a.parent_id
                WHERE p."is_deleted" = false{recurseOwner}
            )
            SELECT "id" FROM ancestors WHERE NOT ("id" = ANY(@p_ids))
            """);

        var parameters = new DynamicParameters();
        parameters.Add("p_ids", selectedIds.ToArray());
        if (scoped) parameters.Add("p_owner_id", ownerId!.Value);
        return (sql, parameters);
    }

    // TreeView "entire tree search" — recursive ancestor-path search over the provisioned
    // (designer) table. Finds the FIRST node matching `searchConditionSql` (a parameterized
    // predicate built by DatasetFilterWhereBuilder, WITHOUT a leading WHERE), then walks UP the
    // self-FK collecting that node's ancestor chain with a `level` counter (0 at the matched
    // node, incrementing toward the root). Rows come back root-first (ORDER BY level DESC) so the
    // UI can reveal/expand the path top-down. Each row carries `_has_children` exactly like
    // BuildTreeLevelQuery so the matched path renders identically to a lazily-loaded level.
    //
    // `searchParameters` MUST already hold the @pN parameters referenced by searchConditionSql
    // (the caller seeds them via DatasetFilterWhereBuilder); the owner scoping parameter
    // (@p_owner_id) is added here when scoped, then the same instance is returned. fkColumnName
    // is produced by BuildFkColumnName. Caller guarantees searchConditionSql is non-empty (an
    // empty predicate would make search_target pick an arbitrary row).
    internal static (string Sql, DynamicParameters Parameters) BuildTreeSearchPathQuery(
        SafeIdentifier tableName,
        IReadOnlyList<ColumnDefinition> userColumns,
        string fkColumnName,
        string searchConditionSql,
        DynamicParameters searchParameters,
        // Auth filter: when both set, the matched node AND every ancestor must be owned by the
        // user (ownerColumn = ownerId) — you can't reveal a path through nodes you don't own.
        string? ownerColumn = null,
        Guid? ownerId = null)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(userColumns);
        ArgumentException.ThrowIfNullOrEmpty(fkColumnName);
        ArgumentException.ThrowIfNullOrWhiteSpace(searchConditionSql);
        ArgumentNullException.ThrowIfNull(searchParameters);

        var t = tableName.Value;
        var scoped = !string.IsNullOrEmpty(ownerColumn) && ownerId.HasValue;
        var targetOwner = scoped ? $" AND \"{ownerColumn}\" = @p_owner_id" : string.Empty;
        var anchorOwner = scoped ? $" AND \"{ownerColumn}\" = @p_owner_id" : string.Empty;
        var recurseOwner = scoped ? $" AND l.\"{ownerColumn}\" = @p_owner_id" : string.Empty;

        var anchorCols = new StringBuilder();
        AppendTreeSearchColumns(anchorCols, userColumns, fkColumnName, alias: null);
        var recurseCols = new StringBuilder();
        AppendTreeSearchColumns(recurseCols, userColumns, fkColumnName, alias: "l");

        var sql = string.Create(CultureInfo.InvariantCulture, $"""
            WITH RECURSIVE search_target AS (
                SELECT "id" FROM "{t}"
                WHERE ({searchConditionSql}) AND "is_deleted" = false{targetOwner}
                LIMIT 1
            ),
            ancestors AS (
                SELECT {anchorCols}, 0 AS level
                FROM "{t}"
                WHERE "id" = (SELECT "id" FROM search_target) AND "is_deleted" = false{anchorOwner}
                UNION ALL
                SELECT {recurseCols}, a.level + 1
                FROM "{t}" l
                INNER JOIN ancestors a ON l."id" = a."{fkColumnName}"
                WHERE l."is_deleted" = false{recurseOwner}
            )
            SELECT a.*, EXISTS(SELECT 1 FROM "{t}" c WHERE c."{fkColumnName}" = a."id" AND c."is_deleted" = false) AS "_has_children"
            FROM ancestors a
            ORDER BY a."level" DESC
            """);

        if (scoped) searchParameters.Add("p_owner_id", ownerId!.Value);
        return (sql, searchParameters);
    }

    // Emits the entire-tree-search projection column list: system columns (in select order) +
    // user columns + the self-FK column, each optionally prefixed with a table alias. Mirrors
    // AppendSelectColumns' system+user ordering, but additionally projects the self-FK — the
    // recursive member joins the parent row on a."<fk>", so the column must travel in the CTE.
    private static void AppendTreeSearchColumns(
        StringBuilder sb, IReadOnlyList<ColumnDefinition> userColumns, string fkColumnName, string? alias)
    {
        var prefix = string.IsNullOrEmpty(alias) ? string.Empty : alias + ".";
        var first = true;
        foreach (var sys in SystemColumnsInSelectOrder)
        {
            if (!first) sb.Append(", ");
            sb.Append(CultureInfo.InvariantCulture, $"{prefix}\"{sys}\"");
            first = false;
        }
        foreach (var col in userColumns)
        {
            sb.Append(", ");
            sb.Append(CultureInfo.InvariantCulture, $"{prefix}\"{col.ColumnName}\"");
        }
        sb.Append(", ");
        sb.Append(CultureInfo.InvariantCulture, $"{prefix}\"{fkColumnName}\"");
    }

    // Appends an OR of substring (ILIKE) predicates across every user column (cast to
    // text so numeric/temporal/uuid columns are searchable too), joined with AND onto the
    // existing WHERE. No-op when search is blank or the node template has no user columns.
    private static void AppendTreeSearch(
        StringBuilder sb, DynamicParameters parameters,
        IReadOnlyList<ColumnDefinition> userColumns, string? search)
    {
        if (string.IsNullOrWhiteSpace(search) || userColumns.Count == 0) return;
        sb.Append(" AND (");
        for (int i = 0; i < userColumns.Count; i++)
        {
            if (i > 0) sb.Append(" OR ");
            sb.Append(CultureInfo.InvariantCulture, $"\"{userColumns[i].ColumnName}\"::text ILIKE @p_search");
        }
        sb.Append(')');
        parameters.Add("p_search", "%" + EscapeLikeWildcards(search.Trim()) + "%");
    }

    // Story 6.3 — parameterized INSERT for POST /api/data/{designerId}. System
    // columns are always set server-side. User columns are included only when
    // present in coercedPayload (absent columns → PG NULL, all are nullable per
    // FR-24 AC-3). Returns the new record ID and the insert timestamp so the
    // caller can build the response and audit row without a re-SELECT.
    internal static (string Sql, DynamicParameters Parameters, Guid NewRecordId, DateTimeOffset InsertedAt)
        BuildInsertQuery(
            SafeIdentifier tableName,
            IReadOnlyList<ColumnDefinition> userColumns,
            IReadOnlyDictionary<string, object?> coercedPayload,
            Guid? actorId)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(userColumns);
        ArgumentNullException.ThrowIfNull(coercedPayload);

        var newId      = Guid.NewGuid();
        var insertedAt = DateTimeOffset.UtcNow;

        // Identify which user columns appear in the payload (order follows the
        // ColumnDefinition list for determinism).
        var presentUserCols = new List<ColumnDefinition>(userColumns.Count);
        foreach (var col in userColumns)
        {
            if (coercedPayload.ContainsKey(col.ColumnName))
                presentUserCols.Add(col);
        }

        // --- columns list ---
        var colSb = new StringBuilder(256);
        colSb.Append("\"id\", \"created_at\", \"created_by\", \"updated_at\", \"updated_by\", \"is_deleted\", \"cascade_event_id\"");
        foreach (var col in presentUserCols)
        {
            colSb.Append(CultureInfo.InvariantCulture, $", \"{col.ColumnName}\"");
        }

        // --- values list (cascade_event_id is SQL NULL literal, not a parameter —
        // sidesteps Dapper's PG uuid null-parameter edge case).
        var valSb = new StringBuilder(256);
        valSb.Append("@p_id, @p_created_at, @p_created_by, @p_updated_at, @p_updated_by, false, NULL");
        foreach (var col in presentUserCols)
        {
            valSb.Append(CultureInfo.InvariantCulture, $", {ValuePlaceholder(col)}");
        }

        var sql = $"INSERT INTO \"{tableName.Value}\" ({colSb}) VALUES ({valSb})";

        var parameters = new DynamicParameters();
        parameters.Add("p_id",          newId);
        parameters.Add("p_created_at",  insertedAt);
        parameters.Add("p_created_by",  actorId);
        parameters.Add("p_updated_at",  insertedAt);
        parameters.Add("p_updated_by",  actorId);

        foreach (var col in presentUserCols)
        {
            parameters.Add($"f_{col.ColumnName}", coercedPayload[col.ColumnName]);
        }

        return (sql, parameters, newId, insertedAt);
    }

    // Story 6.4 — parameterized UPDATE for PUT /api/data/{designerId}/{id}. Only
    // user columns present in coercedPayload appear in the SET clause (partial
    // update). updated_at and updated_by are always set server-side. The caller
    // SELECT-then-checks is_deleted before calling this method; the WHERE clause
    // also guards is_deleted=false so a concurrent soft-delete between SELECT and
    // UPDATE is a no-op (0 rows affected) rather than silently overwriting a deleted
    // row. Caller must check the affected-row count and surface 422 RECORD_DELETED
    // if 0. Returns the server-generated updatedAt so the caller can build the
    // response and audit row without a re-SELECT.
    internal static (string Sql, DynamicParameters Parameters, DateTimeOffset UpdatedAt)
        BuildUpdateQuery(
            SafeIdentifier tableName,
            IReadOnlyList<ColumnDefinition> userColumns,
            IReadOnlyDictionary<string, object?> coercedPayload,
            Guid id,
            Guid? actorId)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(userColumns);
        ArgumentNullException.ThrowIfNull(coercedPayload);

        var updatedAt = DateTimeOffset.UtcNow;

        // Order follows ColumnDefinition list for determinism (same as BuildInsertQuery).
        var presentUserCols = new List<ColumnDefinition>(userColumns.Count);
        foreach (var col in userColumns)
        {
            if (coercedPayload.ContainsKey(col.ColumnName))
                presentUserCols.Add(col);
        }

        var setSb = new StringBuilder(256);
        setSb.Append("\"updated_at\" = @p_updated_at, \"updated_by\" = @p_updated_by");
        foreach (var col in presentUserCols)
        {
            setSb.Append(CultureInfo.InvariantCulture, $", \"{col.ColumnName}\" = {ValuePlaceholder(col)}");
        }

        var sql = $"UPDATE \"{tableName.Value}\" SET {setSb} WHERE \"id\" = @p_id AND \"is_deleted\" = false";

        var parameters = new DynamicParameters();
        parameters.Add("p_updated_at", updatedAt);
        parameters.Add("p_updated_by", actorId);
        parameters.Add("p_id",         id);

        foreach (var col in presentUserCols)
        {
            parameters.Add($"f_{col.ColumnName}", coercedPayload[col.ColumnName]);
        }

        return (sql, parameters, updatedAt);
    }

    // Story 6.5 — parameterized UPDATE for DELETE /api/data/{designerId}/{id}. Sets
    // is_deleted=true, updated_at, updated_by, and cascade_event_id (null for
    // individual deletes, UUID for cascade). The WHERE clause includes
    // AND "is_deleted" = false so a concurrent double-delete returns 0 rows affected
    // instead of silently succeeding. The caller must check rowsAffected and surface
    // 422 RECORD_ALREADY_DELETED if 0. Returns updatedAt so the caller can build the
    // response and audit row without a re-SELECT.
    internal static (string Sql, DynamicParameters Parameters, DateTimeOffset UpdatedAt)
        BuildSoftDeleteByIdQuery(
            SafeIdentifier tableName,
            Guid id,
            Guid? actorId,
            Guid? cascadeEventId)
    {
        ArgumentNullException.ThrowIfNull(tableName);

        var updatedAt = DateTimeOffset.UtcNow;

        // cascade_event_id uses a SQL NULL literal (not a parameter) when there are
        // no children — same pattern as BuildInsertQuery's cascade_event_id = NULL
        // to avoid Dapper's Npgsql UUID null-parameter edge case.
        var cascadePart = cascadeEventId.HasValue
            ? ", \"cascade_event_id\" = @p_cascade_event_id"
            : ", \"cascade_event_id\" = NULL";

        var sql = $"UPDATE \"{tableName.Value}\" SET \"is_deleted\" = true, " +
                  $"\"updated_at\" = @p_updated_at, \"updated_by\" = @p_updated_by{cascadePart} " +
                  $"WHERE \"id\" = @p_id AND \"is_deleted\" = false";

        var parameters = new DynamicParameters();
        parameters.Add("p_updated_at", updatedAt);
        parameters.Add("p_updated_by", actorId);
        parameters.Add("p_id",         id);
        if (cascadeEventId.HasValue)
            parameters.Add("p_cascade_event_id", cascadeEventId.Value);

        return (sql, parameters, updatedAt);
    }

    // Story 6.5 — SELECT id of all non-deleted children of a parent row. Used
    // during the cascade walk to collect child IDs before recursing to their own
    // children. fkColumnName is produced by BuildFkColumnName and is already a
    // validated identifier.
    internal static (string Sql, DynamicParameters Parameters) BuildSelectChildIdsByFkQuery(
        SafeIdentifier childTableName,
        string fkColumnName,
        Guid parentId)
    {
        ArgumentNullException.ThrowIfNull(childTableName);
        ArgumentException.ThrowIfNullOrEmpty(fkColumnName);

        var sql = $"SELECT \"id\" FROM \"{childTableName.Value}\" " +
                  $"WHERE \"{fkColumnName}\" = @p_parent_id AND \"is_deleted\" = false";

        var parameters = new DynamicParameters();
        parameters.Add("p_parent_id", parentId);
        return (sql, parameters);
    }

    // Story 6.5 — UPDATE all non-deleted children of a parent row within the cascade
    // transaction. fkColumnName is produced by BuildFkColumnName. updatedAt is
    // passed in (not generated here) so parent and all descendants share one timestamp
    // snapshot. cascadeEventId is always non-null for cascade calls.
    internal static (string Sql, DynamicParameters Parameters) BuildCascadeChildSoftDeleteQuery(
        SafeIdentifier childTableName,
        string fkColumnName,
        Guid parentId,
        Guid? actorId,
        Guid cascadeEventId,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(childTableName);
        ArgumentException.ThrowIfNullOrEmpty(fkColumnName);

        var sql = $"UPDATE \"{childTableName.Value}\" SET " +
                  $"\"is_deleted\" = true, \"updated_at\" = @p_updated_at, " +
                  $"\"updated_by\" = @p_updated_by, \"cascade_event_id\" = @p_cascade_event_id " +
                  $"WHERE \"{fkColumnName}\" = @p_parent_id AND \"is_deleted\" = false";

        var parameters = new DynamicParameters();
        parameters.Add("p_updated_at",         updatedAt);
        parameters.Add("p_updated_by",         actorId);
        parameters.Add("p_cascade_event_id",   cascadeEventId);
        parameters.Add("p_parent_id",          parentId);
        return (sql, parameters);
    }

    // Story 6.6 — parameterized UPDATE for PUT /api/data/{designerId}/{id}/restore.
    // Sets is_deleted=false, updated_at, updated_by; cascade_event_id is unconditionally
    // cleared to NULL (individual restore clears unconditionally per Decision 1.3).
    // WHERE is_deleted=true so a concurrent restore between SELECT and UPDATE returns
    // 0 rows affected → caller surfaces 422 RECORD_NOT_DELETED. Returns updatedAt for
    // response + audit assembly without a re-SELECT.
    internal static (string Sql, DynamicParameters Parameters, DateTimeOffset UpdatedAt)
        BuildRestoreByIdQuery(
            SafeIdentifier tableName,
            Guid id,
            Guid? actorId)
    {
        ArgumentNullException.ThrowIfNull(tableName);

        var updatedAt = DateTimeOffset.UtcNow;

        var sql = $"UPDATE \"{tableName.Value}\" SET " +
                  $"\"is_deleted\" = false, \"updated_at\" = @p_updated_at, " +
                  $"\"updated_by\" = @p_updated_by, \"cascade_event_id\" = NULL " +
                  $"WHERE \"id\" = @p_id AND \"is_deleted\" = true";

        var parameters = new DynamicParameters();
        parameters.Add("p_updated_at", updatedAt);
        parameters.Add("p_updated_by", actorId);
        parameters.Add("p_id",         id);

        return (sql, parameters, updatedAt);
    }

    // Story 6.6 — UPDATE all rows in a child table that were cascade-soft-deleted
    // as part of a specific cascade event. Matches by cascade_event_id (not by FK)
    // because all rows sharing the cascade event UUID are the correct set to restore,
    // regardless of parent_id — the UUID is unique per cascade event. cascade_event_id
    // is cleared to NULL on restore (Decision 1.3). updatedAt is passed in so all
    // restored rows share one server-generated timestamp.
    internal static (string Sql, DynamicParameters Parameters) BuildRestoreCascadeChildrenQuery(
        SafeIdentifier childTableName,
        Guid parentCascadeEventId,
        Guid? actorId,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(childTableName);

        var sql = $"UPDATE \"{childTableName.Value}\" SET " +
                  $"\"is_deleted\" = false, \"updated_at\" = @p_updated_at, " +
                  $"\"updated_by\" = @p_updated_by, \"cascade_event_id\" = NULL " +
                  $"WHERE \"cascade_event_id\" = @p_cascade_event_id AND \"is_deleted\" = true";

        var parameters = new DynamicParameters();
        parameters.Add("p_updated_at",       updatedAt);
        parameters.Add("p_updated_by",       actorId);
        parameters.Add("p_cascade_event_id", parentCascadeEventId);
        return (sql, parameters);
    }

    // Hard-delete — SELECT id of ALL children of a parent row (deleted or not),
    // regardless of is_deleted. Unlike BuildSelectChildIdsByFkQuery (which filters
    // is_deleted = false for the soft-delete cascade), a hard delete purges the whole
    // subtree, so already-soft-deleted descendants must be collected too. fkColumnName
    // is produced by BuildFkColumnName and is already a validated identifier.
    internal static (string Sql, DynamicParameters Parameters) BuildSelectAllChildIdsByFkQuery(
        SafeIdentifier childTableName,
        string fkColumnName,
        Guid parentId)
    {
        ArgumentNullException.ThrowIfNull(childTableName);
        ArgumentException.ThrowIfNullOrEmpty(fkColumnName);

        var sql = $"SELECT \"id\" FROM \"{childTableName.Value}\" " +
                  $"WHERE \"{fkColumnName}\" = @p_parent_id";

        var parameters = new DynamicParameters();
        parameters.Add("p_parent_id", parentId);
        return (sql, parameters);
    }

    // Hard-delete — physically DELETE every child row of a parent (deleted or not)
    // within the cascade transaction. fkColumnName is produced by BuildFkColumnName.
    // No is_deleted filter: a hard delete removes the entire subtree. Used during the
    // bottom-up cascade walk so children are gone before the parent row is deleted,
    // which keeps the operation valid even when a real FK constraint exists WITHOUT
    // ON DELETE CASCADE.
    internal static (string Sql, DynamicParameters Parameters) BuildHardDeleteChildrenByFkQuery(
        SafeIdentifier childTableName,
        string fkColumnName,
        Guid parentId)
    {
        ArgumentNullException.ThrowIfNull(childTableName);
        ArgumentException.ThrowIfNullOrEmpty(fkColumnName);

        var sql = $"DELETE FROM \"{childTableName.Value}\" " +
                  $"WHERE \"{fkColumnName}\" = @p_parent_id";

        var parameters = new DynamicParameters();
        parameters.Add("p_parent_id", parentId);
        return (sql, parameters);
    }

    // Hard-delete — physically DELETE a single row by id. The caller checks the
    // returned rows-affected: 0 means the row was raced away between SELECT and DELETE
    // (caller maps to 404). Runs last in the cascade transaction, after all descendants
    // have been removed.
    internal static (string Sql, DynamicParameters Parameters) BuildHardDeleteByIdQuery(
        SafeIdentifier tableName,
        Guid id)
    {
        ArgumentNullException.ThrowIfNull(tableName);

        var sql = $"DELETE FROM \"{tableName.Value}\" WHERE \"id\" = @p_id";

        var parameters = new DynamicParameters();
        parameters.Add("p_id", id);
        return (sql, parameters);
    }

    // Story 6.7 — INSERT for child records in the Repeater nested-write flow.
    // Identical structure to BuildInsertQuery but inserts the FK column that links
    // the child to its parent. fkColumnName is produced by BuildFkColumnName(parentDesignerId).
    // The FK column is NOT in childUserColumns (it is a schema-relationship column created
    // by DdlEmitter during provisioning, not a user fieldKey), so it is injected explicitly.
    // cascade_event_id = NULL literal (same pattern as BuildInsertQuery — avoids Dapper
    // Npgsql UUID null-parameter edge case).
    internal static (string Sql, DynamicParameters Parameters, Guid NewRecordId, DateTimeOffset InsertedAt)
        BuildChildInsertQuery(
            SafeIdentifier childTableName,
            IReadOnlyList<ColumnDefinition> childUserColumns,
            IReadOnlyDictionary<string, object?> coercedPayload,
            string fkColumnName,
            Guid parentId,
            Guid? actorId)
    {
        ArgumentNullException.ThrowIfNull(childTableName);
        ArgumentNullException.ThrowIfNull(childUserColumns);
        ArgumentNullException.ThrowIfNull(coercedPayload);
        ArgumentException.ThrowIfNullOrEmpty(fkColumnName);

        var newId      = Guid.NewGuid();
        var insertedAt = DateTimeOffset.UtcNow;

        var presentUserCols = new List<ColumnDefinition>(childUserColumns.Count);
        foreach (var col in childUserColumns)
        {
            if (coercedPayload.ContainsKey(col.ColumnName))
                presentUserCols.Add(col);
        }

        var colSb = new StringBuilder(256);
        colSb.Append("\"id\", \"created_at\", \"created_by\", \"updated_at\", \"updated_by\", \"is_deleted\", \"cascade_event_id\"");
        colSb.Append(CultureInfo.InvariantCulture, $", \"{fkColumnName}\"");
        foreach (var col in presentUserCols)
            colSb.Append(CultureInfo.InvariantCulture, $", \"{col.ColumnName}\"");

        var valSb = new StringBuilder(256);
        valSb.Append("@p_id, @p_created_at, @p_created_by, @p_updated_at, @p_updated_by, false, NULL");
        valSb.Append(", @p_fk_parent_id");
        foreach (var col in presentUserCols)
            valSb.Append(CultureInfo.InvariantCulture, $", {ValuePlaceholder(col)}");

        var sql = $"INSERT INTO \"{childTableName.Value}\" ({colSb}) VALUES ({valSb})";

        var parameters = new DynamicParameters();
        parameters.Add("p_id",          newId);
        parameters.Add("p_created_at",  insertedAt);
        parameters.Add("p_created_by",  actorId);
        parameters.Add("p_updated_at",  insertedAt);
        parameters.Add("p_updated_by",  actorId);
        parameters.Add("p_fk_parent_id", parentId);

        foreach (var col in presentUserCols)
            parameters.Add($"f_{col.ColumnName}", coercedPayload[col.ColumnName]);

        return (sql, parameters, newId, insertedAt);
    }

    // Must stay in sync with DdlEmitter.BuildFkColumnName (Story 5.5). Cap at 53 so
    // "parent_" (7) + name (≤53) + "_id" (3) ≤ 63 chars (PostgreSQL identifier limit).
    // BuildFkColumnName_LongName_CapsAt63TotalChars is the regression guard.
    internal static string BuildFkColumnName(string parentTableName)
    {
        ArgumentException.ThrowIfNullOrEmpty(parentTableName);
        var cap = Math.Min(parentTableName.Length, 53);
        return $"parent_{parentTableName[..cap]}_id";
    }

    // True when a user fieldKey matches the auto-provisioned parent-FK column
    // naming convention (parent_<parentDesignerId>_id — see BuildFkColumnName).
    // DdlEmitter creates these as UUID and they are system-owned: only the Repeater
    // nested-write coordinator sets them (always = the real parentId). A designer
    // who declares a fieldKey with this exact shape collides with that UUID column;
    // the validator coerces the field as its own (TEXT) type, so binding the value
    // into an INSERT/UPDATE yields PG 42804 "uuid but expression is of type text".
    // Callers strip such keys so the system-owned value always wins.
    internal static bool IsReservedParentFkColumn(string columnName)
    {
        ArgumentNullException.ThrowIfNull(columnName);
        // parent_<name>_id with a non-empty <name> — "parent__id" (len 10) is excluded.
        return columnName.Length > "parent__id".Length
            && columnName.StartsWith("parent_", StringComparison.Ordinal)
            && columnName.EndsWith("_id", StringComparison.Ordinal);
    }

    // Re-coerces parent-FK payload values from TEXT to Guid so they bind as UUID.
    // A field whose fieldKey matches the FK naming convention (e.g. a designer-backed
    // dropdown that picks the parent record) targets a UUID column, but the payload
    // validator coerces it as the field's declared type (TEXT) — its value arrives as
    // a string. Binding a string into a UUID column yields PG 42804, so we parse each
    // such value to Guid here. Null / empty / whitespace become a SQL NULL (cleared
    // selection). Returns the same instance when nothing matches so the common path
    // allocates nothing. Returns false + the offending column when a non-empty value
    // is not a valid UUID, so the caller can surface a 422 instead of a PG 22P02 500.
    internal static bool TryCoerceReservedParentFkColumns(
        IReadOnlyDictionary<string, object?> coercedValues,
        out IReadOnlyDictionary<string, object?> result,
        out string? invalidColumn)
    {
        ArgumentNullException.ThrowIfNull(coercedValues);
        invalidColumn = null;

        var hasReserved = false;
        foreach (var key in coercedValues.Keys)
        {
            if (IsReservedParentFkColumn(key)) { hasReserved = true; break; }
        }
        if (!hasReserved) { result = coercedValues; return true; }

        var converted = new Dictionary<string, object?>(coercedValues.Count, StringComparer.Ordinal);
        foreach (var (key, value) in coercedValues)
        {
            if (!IsReservedParentFkColumn(key) || value is Guid)
            {
                converted[key] = value;
                continue;
            }

            // value originates from the TEXT-coercing validator → string or null.
            if (value is null || (value is string s0 && string.IsNullOrWhiteSpace(s0)))
            {
                converted[key] = null;
                continue;
            }

            if (value is string s && Guid.TryParse(s, CultureInfo.InvariantCulture, out var fk))
            {
                converted[key] = fk;
                continue;
            }

            invalidColumn = key;
            result = coercedValues;
            return false;
        }

        result = converted;
        return true;
    }

    internal static void AppendSelectColumns(StringBuilder sb, IReadOnlyList<ColumnDefinition> userColumns)
    {
        // System columns first, in CREATE TABLE order for determinism.
        var first = true;
        foreach (var sys in SystemColumnsInSelectOrder)
        {
            if (!first) sb.Append(", ");
            sb.Append(CultureInfo.InvariantCulture, $"\"{sys}\"");
            first = false;
        }
        // Then user columns. Order follows the ColumnDefinition list (which is the
        // order RootElementParser produced — i.e. document order from the Designer).
        foreach (var col in userColumns)
        {
            sb.Append(", ");
            sb.Append(CultureInfo.InvariantCulture, $"\"{col.ColumnName}\"");
        }
    }

    // Story B-1-4-followup — appends correlated scalar subqueries for derived table
    // columns after the real columns. Each subquery resolves a value from a referenced
    // designer's table at SELECT time and is aliased to DerivedColumn.ResultAlias, which
    // the frontend recomputes via the same rule to read it back off the row.
    //
    // All interpolated identifiers (ReferencedDesignerId, LabelColumn, LocalFkColumn,
    // ResultAlias, parent-FK) originate from SafeIdentifier-validated schema values
    // (re-validated in RootElementParser), consistent with this builder's "caller
    // pre-validates identifiers" contract. The referenced designer must be provisioned
    // and expose LabelColumn — the same precondition the /options endpoint requires.
    internal static void AppendDerivedColumns(
        StringBuilder sb, SafeIdentifier tableName, IReadOnlyList<DerivedColumn>? derivedColumns)
    {
        if (derivedColumns is null || derivedColumns.Count == 0) return;

        foreach (var d in derivedColumns)
        {
            sb.Append(", ");
            switch (d.Kind)
            {
                case DerivedColumn.DropdownLabelKind:
                    // Pull the human label for the row this Dropdown's UUID points to.
                    sb.Append(CultureInfo.InvariantCulture,
                        $"(SELECT \"{d.LabelColumn}\" FROM \"{d.ReferencedDesignerId}\" " +
                        $"WHERE \"id\" = \"{tableName.Value}\".\"{d.LocalFkColumn}\" " +
                        $"AND \"is_deleted\" = false LIMIT 1) AS \"{d.ResultAlias}\"");
                    break;
                case DerivedColumn.DatasetDropdownLabelKind:
                    // Pull the human label from the dataset's backing VIEW
                    // (datasets."<name>") joined on the dropdown's valueField. The VIEW is
                    // an arbitrary query result, so there is no "is_deleted" column to
                    // filter. Compare via ::text (mirrors DatasetDropdownService's value
                    // match) so a value-column type mismatch with the stored FK can't error.
                    sb.Append(CultureInfo.InvariantCulture,
                        $"(SELECT \"{d.LabelColumn}\" FROM datasets.\"{d.ReferencedDesignerId}\" " +
                        $"WHERE \"{d.ValueColumn}\"::text = \"{tableName.Value}\".\"{d.LocalFkColumn}\"::text " +
                        $"LIMIT 1) AS \"{d.ResultAlias}\"");
                    break;
                case DerivedColumn.RepeaterCountKind:
                    // Count the live child rows that reference this parent row.
                    var parentFk = BuildFkColumnName(tableName.Value);
                    sb.Append(CultureInfo.InvariantCulture,
                        $"(SELECT COUNT(*) FROM \"{d.ReferencedDesignerId}\" " +
                        $"WHERE \"{parentFk}\" = \"{tableName.Value}\".\"id\" " +
                        $"AND \"is_deleted\" = false) AS \"{d.ResultAlias}\"");
                    break;
            }
        }
    }

    private static void AppendWhereClause(
        StringBuilder sb,
        DynamicParameters parameters,
        IReadOnlyDictionary<string, string> filters,
        IReadOnlyList<ColumnDefinition> userColumns)
    {
        if (filters.Count == 0) return;
        sb.Append(" WHERE ");

        // Build a per-request lookup so we can dispatch each filter on the
        // column's actual PG type. System columns are resolved by name; user
        // columns are looked up here.
        var userColPgType = new Dictionary<string, string>(userColumns.Count, StringComparer.Ordinal);
        foreach (var c in userColumns)
            userColPgType[c.ColumnName] = c.PgType;

        var first = true;
        var idx = 0;
        foreach (var (pgName, rawValue) in filters)
        {
            if (!first) sb.Append(" AND ");
            var paramName = string.Create(CultureInfo.InvariantCulture, $"filter_{idx}");
            AppendFilterPredicate(sb, parameters, pgName, rawValue, paramName, ResolveFilterPgType(pgName, userColPgType));
            first = false;
            idx++;
        }
    }

    // Resolves the PG type for a whitelisted filter column. System columns are
    // hardcoded (their PG types never vary by table); user columns are looked
    // up in the schema-registry-derived dictionary.
    private static string ResolveFilterPgType(
        string pgName, Dictionary<string, string> userColPgType) => pgName switch
    {
        "is_deleted" => "BOOLEAN",
        "id" or "created_by" or "updated_by" or "cascade_event_id" => "UUID",
        "created_at" or "updated_at" => "TIMESTAMPTZ",
        // Parent-FK columns (parent_<name>_id) are physically UUID even though a
        // colliding designer field (e.g. a designer-backed dropdown that picks the
        // parent record) types them TEXT in the registry. Without this override,
        // AppendFilterPredicate would emit "<uuid col> ILIKE @p" → PG 42883
        // "operator does not exist: uuid ~~* text". Symmetric with the write path's
        // TryCoerceReservedParentFkColumns, which binds these values as Guid.
        _ when IsReservedParentFkColumn(pgName) => "UUID",
        _ => userColPgType.TryGetValue(pgName, out var t) ? t : "TEXT",
    };

    // Emits one predicate into the WHERE clause and binds its parameter with the
    // right CLR type. Postgres does not implicit-cast text → numeric / timestamptz
    // / boolean, so a parameter passed as text against a typed column produces
    // 42883 "operator does not exist: <type> = text". For each PG type we either
    // (a) bind a typed parameter (BOOLEAN, UUID) or (b) cast inside SQL (NUMERIC,
    // TIMESTAMPTZ, JSONB). TEXT columns use ILIKE substring match so the UI's
    // partial-typing feedback finds rows on every keystroke.
    private static void AppendFilterPredicate(
        StringBuilder sb,
        DynamicParameters parameters,
        string pgName,
        string rawValue,
        string paramName,
        string pgType)
    {
        switch (pgType)
        {
            case "TEXT":
                sb.Append(CultureInfo.InvariantCulture, $"\"{pgName}\" ILIKE @{paramName}");
                parameters.Add(paramName, "%" + EscapeLikeWildcards(rawValue) + "%");
                break;
            case "NUMERIC":
                // Server-side cast: client value arrives as text; PG cannot
                // implicit-cast text→numeric for the = operator.
                sb.Append(CultureInfo.InvariantCulture, $"\"{pgName}\" = @{paramName}::numeric");
                parameters.Add(paramName, rawValue);
                break;
            case "BOOLEAN":
                sb.Append(CultureInfo.InvariantCulture, $"\"{pgName}\" = @{paramName}");
                parameters.Add(paramName, ParseBoolLiteral(rawValue));
                break;
            case "TIMESTAMPTZ":
                // Equality on a timestamp is almost never what the user wants
                // (they pick a date but rows have microsecond precision). Match
                // the calendar date in UTC instead — same semantics as a daily
                // bucket.
                sb.Append(CultureInfo.InvariantCulture, $"(\"{pgName}\" AT TIME ZONE 'UTC')::date = @{paramName}::date");
                parameters.Add(paramName, rawValue);
                break;
            case "UUID":
                sb.Append(CultureInfo.InvariantCulture, $"\"{pgName}\" = @{paramName}");
                parameters.Add(paramName, Guid.Parse(rawValue, CultureInfo.InvariantCulture));
                break;
            case "JSONB":
            default:
                // Unknown future column types are TEXT-cast and substring-matched.
                // JSONB equality is brittle (key ordering, whitespace) so we
                // never use = directly here.
                sb.Append(CultureInfo.InvariantCulture, $"\"{pgName}\"::text ILIKE @{paramName}");
                parameters.Add(paramName, "%" + EscapeLikeWildcards(rawValue) + "%");
                break;
        }
    }

    // % _ \ are LIKE wildcards / the escape char in PostgreSQL. User-supplied
    // text must not be interpreted as a pattern, so we escape them. We rely on
    // PG's default escape character (backslash) — no `ESCAPE` clause needed.
    private static string EscapeLikeWildcards(string input) => input
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    // Story B — value placeholder for an INSERT/UPDATE. jsonb columns receive the
    // raw JSON text from the payload validator; bind with an explicit ::jsonb cast
    // so PG parses the text (a bare text parameter into a jsonb column yields PG
    // 42804). All other families bind their coerced CLR type directly — Npgsql maps
    // them to the column type without a cast.
    private static string ValuePlaceholder(ColumnDefinition col) =>
        PgTypeInfo.FamilyOf(col.PgType) == PgTypeFamily.Json
            ? string.Create(CultureInfo.InvariantCulture, $"@f_{col.ColumnName}::jsonb")
            : string.Create(CultureInfo.InvariantCulture, $"@f_{col.ColumnName}");

    private static bool ParseBoolLiteral(string raw) => raw switch
    {
        "true" or "True" or "TRUE" or "yes" or "Yes" or "YES" or "on" or "On" or "ON" or "1" => true,
        "false" or "False" or "FALSE" or "no" or "No" or "NO" or "off" or "Off" or "OFF" or "0" => false,
        _ => bool.Parse(raw), // Defensive — caller validates against PostgresBoolLiterals first.
    };

    private static void AppendOrderByClause(StringBuilder sb, IReadOnlyList<SortParam> sorts)
    {
        if (sorts.Count == 0)
        {
            // Default sort = created_at DESC for stable pagination (AC-1 implicit).
            sb.Append(" ORDER BY \"created_at\" DESC");
            return;
        }
        sb.Append(" ORDER BY ");
        for (int i = 0; i < sorts.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(CultureInfo.InvariantCulture, $"\"{sorts[i].Column}\" {sorts[i].Direction}");
        }
    }

    // Validates a raw filter value against a user column's PG type before it
    // reaches the DB. Equality / cast errors at the SQL layer surface as opaque
    // 500s (42883 / 22P02); validating here gives the client a precise 400
    // describing which field's value was malformed.
    internal static bool TryValidateUserFilterValue(
        string pgType, string rawValue, out string? error)
    {
        // Story B — validate by behaviour family (mirrors AppendFilterPredicate).
        var baseType = PgTypeInfo.BaseOf(pgType);
        switch (PgTypeInfo.FamilyOf(pgType))
        {
            case PgTypeFamily.Numeric:
                if (PgTypeInfo.IsIntegerBase(baseType))
                {
                    if (!long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    {
                        error = $"Value '{rawValue}' is not a valid whole number.";
                        return false;
                    }
                }
                else if (!decimal.TryParse(rawValue, NumberStyles.Number, CultureInfo.InvariantCulture, out _)
                      && !double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    error = $"Value '{rawValue}' is not a valid number.";
                    return false;
                }
                break;
            case PgTypeFamily.Boolean:
                if (!PostgresBoolLiterals.Contains(rawValue))
                {
                    error = $"Value '{rawValue}' is not a valid boolean; expected true, false, yes, no, on, off, 1, or 0.";
                    return false;
                }
                break;
            case PgTypeFamily.Temporal:
                if (!IsValidTemporalFilter(baseType, rawValue))
                {
                    error = $"Value '{rawValue}' is not a valid {baseType}.";
                    return false;
                }
                break;
            case PgTypeFamily.Uuid:
                // UUID columns bind a parsed Guid in AppendFilterPredicate, which
                // would throw (→ 500) on a malformed value; reject it as a 400 here.
                if (!Guid.TryParse(rawValue, CultureInfo.InvariantCulture, out _))
                {
                    error = $"Value '{rawValue}' is not a valid UUID.";
                    return false;
                }
                break;
            // Text and Json accept arbitrary strings — both end up in an ILIKE
            // substring match where any character is valid.
        }
        error = null;
        return true;
    }

    // Story B — temporal filter value validation. date/time parse strictly; the
    // timestamp family accepts a calendar date OR a full timestamp because the
    // predicate compares ::date either way.
    private static bool IsValidTemporalFilter(string baseType, string rawValue) => baseType switch
    {
        "date" => DateOnly.TryParse(rawValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "time" => TimeOnly.TryParse(rawValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        _ => DateOnly.TryParse(rawValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
          || DateTime.TryParse(rawValue, CultureInfo.InvariantCulture,
                 DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _),
    };
}
