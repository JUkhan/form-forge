using System.Globalization;
using System.Text.Json;
using Dapper;
using FormForge.Api.Domain.ValueTypes;
using FormForge.Api.Features.Datasets.Dtos;
using FormForge.Api.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace FormForge.Api.Features.Datasets;

// Serves a dataset's backing VIEW rows to the DatasetComponent runtime data-view:
//   • GetRowsAsync   — paginated, optionally filtered + sorted rows (the table).
//   • GetExportAsync — the full filtered/sorted result set as {headers, rows} for the
//     CSV/XLSX/PDF writers (capped at MaxExportRows, count-first).
//
// Like DatasetDropdownService it reads datasets."<name>" through the PRIVILEGED pool
// (the least-privileged preview role lacks SELECT on the datasets schema), re-validates
// the stored name through DatasetName before quoting, and validates every referenced
// column against information_schema. Filters go through DatasetFilterWhereBuilder.

internal enum DatasetRowsOutcome { Success, NotFound, InvalidRequest }

internal sealed record DatasetRowsPage(
    IReadOnlyList<IDictionary<string, object?>> Data,
    long Total,
    int Page,
    int PageSize,
    int TotalPages,
    IReadOnlyList<string> Columns);

internal sealed record DatasetRowsResult(
    DatasetRowsOutcome Outcome,
    DatasetRowsPage? Page = null,
    string? ErrorDetail = null);

internal enum DatasetExportOutcome { Success, NotFound, InvalidRequest, TooManyRows }

internal sealed record DatasetExportData(
    string DatasetName,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IDictionary<string, object?>> Rows);

internal sealed record DatasetExportResult(
    DatasetExportOutcome Outcome,
    DatasetExportData? Data = null,
    string? ErrorDetail = null);

internal enum DatasetChartOutcome { Success, NotFound, InvalidRequest }

internal sealed record DatasetChartPoint(string Category, double Value);

internal sealed record DatasetChartData(
    string CategoryColumn,
    string? ValueColumn,
    string Aggregate,
    IReadOnlyList<DatasetChartPoint> Points);

internal sealed record DatasetChartResult(
    DatasetChartOutcome Outcome,
    DatasetChartData? Data = null,
    string? ErrorDetail = null);

// One level of a dataset-backed tree: the direct children of ParentId (roots when null/blank),
// matched by ParentColumn == KeyColumn of the parent. KeyColumn/ParentColumn name the dataset
// VIEW columns that act as the node id and the self-reference. Search substring-matches every
// column. AuthFilterColumn scopes to the requesting user (value is the server-resolved id).
internal sealed record DatasetTreeLevelRequest(
    string KeyColumn,
    string ParentColumn,
    string? ParentId = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 25,
    string? AuthFilterColumn = null,
    string? QueryParameters = null);

internal sealed record DatasetTreeLevelPage(
    IReadOnlyList<IDictionary<string, object?>> Data,
    bool HasNextPage,
    int Page,
    int PageSize);

internal sealed record DatasetTreeLevelResult(
    DatasetRowsOutcome Outcome,
    DatasetTreeLevelPage? Page = null,
    string? ErrorDetail = null);

internal sealed record DatasetTreeDescendantsResult(
    DatasetRowsOutcome Outcome,
    IReadOnlyList<string>? Ids = null,
    string? ErrorDetail = null);

internal sealed record DatasetTreeAncestorsResult(
    DatasetRowsOutcome Outcome,
    IReadOnlyList<string>? Ids = null,
    string? ErrorDetail = null);

internal interface IDatasetRowQueryService
{
    // authUserId (optional) is the requesting user's id; combined with the request's
    // AuthFilterColumn it scopes the result set to the user's own rows. Defaulted so
    // existing callers/tests that don't auth-scope keep compiling.
    Task<DatasetRowsResult> GetRowsAsync(
        Guid datasetId, DatasetRowsRequest request, CancellationToken ct, Guid? authUserId = null);

    Task<DatasetExportResult> GetExportAsync(
        Guid datasetId, DatasetExportRequest request, CancellationToken ct, Guid? authUserId = null);

    Task<DatasetChartResult> GetChartAsync(
        Guid datasetId, DatasetChartRequest request, CancellationToken ct, Guid? authUserId = null);

    // One lazy, paginated level of a dataset-backed TreeView (roots or a parent's children),
    // each row carrying a derived `_has_children` flag.
    Task<DatasetTreeLevelResult> GetTreeLevelAsync(
        Guid datasetId, DatasetTreeLevelRequest request, CancellationToken ct, Guid? authUserId = null);

    // The key-column values of every descendant of ParentId (recursive), for cascade select.
    Task<DatasetTreeDescendantsResult> GetTreeDescendantsAsync(
        Guid datasetId, string keyColumn, string parentColumn, string parentId,
        string? authFilterColumn, CancellationToken ct, Guid? authUserId = null);

    // The key-column values of every ancestor of the given node ids (recursive, walks up),
    // for revealing/marking the path to a selection seeded when editing a record.
    Task<DatasetTreeAncestorsResult> GetTreeAncestorsAsync(
        Guid datasetId, string keyColumn, string parentColumn, IReadOnlyList<string> ids,
        string? authFilterColumn, CancellationToken ct, Guid? authUserId = null);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class DatasetRowQueryService(DbConnectionFactory connectionFactory)
    : IDatasetRowQueryService
{
    private const int CommandTimeoutSeconds = 5;
    private const int ExportCommandTimeoutSeconds = 30;
    private const int MaxExportRows = 100_000;

    public async Task<DatasetRowsResult> GetRowsAsync(
        Guid datasetId, DatasetRowsRequest request, CancellationToken ct, Guid? authUserId = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize is < 1 or > 100 ? 25 : request.PageSize;

        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var resolved = await DatasetSourceResolver.ResolveAsync(
                conn, datasetId, request.QueryParameters, allowParameterized: true,
                CommandTimeoutSeconds, ct).ConfigureAwait(false);
            if (resolved.Outcome == DatasetSourceOutcome.NotFound)
                return new DatasetRowsResult(DatasetRowsOutcome.NotFound);
            if (resolved.Outcome != DatasetSourceOutcome.Ok)
                return new DatasetRowsResult(DatasetRowsOutcome.InvalidRequest, ErrorDetail: resolved.Error);
            var src = resolved.Source!;
            var columnTypes = src.ColumnTypes;

            // Column projection: requested allow/order list (validated) or all columns.
            var (selectCols, colError) = ResolveSelectColumns(request.Columns, src.ColumnOrder, columnTypes);
            if (colError is not null)
                return new DatasetRowsResult(DatasetRowsOutcome.InvalidRequest, ErrorDetail: colError);

            var parameters = new DynamicParameters();
            var where = DatasetFilterWhereBuilder.Build(
                request.Filters, columnTypes, parameters,
                ResolveAuthFilter(request.AuthFilterColumn, authUserId));
            if (!where.IsValid)
                return new DatasetRowsResult(DatasetRowsOutcome.InvalidRequest, ErrorDetail: where.Error);

            var (orderBy, sortError) = BuildOrderBy(request.Sort, columnTypes);
            if (sortError is not null)
                return new DatasetRowsResult(DatasetRowsOutcome.InvalidRequest, ErrorDetail: sortError);

            var countSql = src.WithClause + $"SELECT COUNT(*) FROM {src.Relation}{where.Where}";
            var total = await ScalarLongAsync(conn, countSql, parameters, src, CommandTimeoutSeconds, ct)
                .ConfigureAwait(false);

            parameters.Add("p_limit", pageSize);
            parameters.Add("p_offset", (long)(page - 1) * pageSize);
            var selectList = string.Join(", ", selectCols.Select(QuoteIdentifier));
            var selectSql = src.WithClause +
                $"SELECT {selectList} FROM {src.Relation}{where.Where}{orderBy} LIMIT @p_limit OFFSET @p_offset";

            var rows = await QueryRowsAsync(conn, selectSql, parameters, src, CommandTimeoutSeconds, ct)
                .ConfigureAwait(false);

            var totalPages = pageSize == 0 ? 0 : (int)((total + pageSize - 1) / pageSize);
            return new DatasetRowsResult(DatasetRowsOutcome.Success,
                new DatasetRowsPage(rows, total, page, pageSize, totalPages, selectCols));
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<DatasetExportResult> GetExportAsync(
        Guid datasetId, DatasetExportRequest request, CancellationToken ct, Guid? authUserId = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var resolved = await DatasetSourceResolver.ResolveAsync(
                conn, datasetId, request.QueryParameters, allowParameterized: true,
                ExportCommandTimeoutSeconds, ct).ConfigureAwait(false);
            if (resolved.Outcome == DatasetSourceOutcome.NotFound)
                return new DatasetExportResult(DatasetExportOutcome.NotFound);
            if (resolved.Outcome != DatasetSourceOutcome.Ok)
                return new DatasetExportResult(DatasetExportOutcome.InvalidRequest, ErrorDetail: resolved.Error);
            var src = resolved.Source!;
            var columnTypes = src.ColumnTypes;
            var name = src.Name;

            // Resolve columns + headers. Absent ⇒ all columns (ordinal order), header = column name.
            List<string> selectCols;
            List<string> headers;
            if (request.Columns is { Count: > 0 })
            {
                selectCols = [];
                headers = [];
                foreach (var c in request.Columns)
                {
                    if (string.IsNullOrWhiteSpace(c.Column) || !columnTypes.ContainsKey(c.Column))
                        return new DatasetExportResult(DatasetExportOutcome.InvalidRequest,
                            ErrorDetail: $"Unknown export column '{c.Column}'.");
                    selectCols.Add(c.Column);
                    headers.Add(string.IsNullOrWhiteSpace(c.Header) ? c.Column : c.Header!);
                }
            }
            else
            {
                selectCols = [.. src.ColumnOrder];
                headers = [.. src.ColumnOrder];
            }

            var parameters = new DynamicParameters();
            var where = DatasetFilterWhereBuilder.Build(
                request.Filters, columnTypes, parameters,
                ResolveAuthFilter(request.AuthFilterColumn, authUserId));
            if (!where.IsValid)
                return new DatasetExportResult(DatasetExportOutcome.InvalidRequest, ErrorDetail: where.Error);

            var (orderBy, sortError) = BuildOrderBy(request.Sort, columnTypes);
            if (sortError is not null)
                return new DatasetExportResult(DatasetExportOutcome.InvalidRequest, ErrorDetail: sortError);

            // Count-first so an "export everything" can't OOM the process.
            var countSql = src.WithClause + $"SELECT COUNT(*) FROM {src.Relation}{where.Where}";
            var total = await ScalarLongAsync(conn, countSql, parameters, src, ExportCommandTimeoutSeconds, ct)
                .ConfigureAwait(false);
            if (total > MaxExportRows)
                return new DatasetExportResult(DatasetExportOutcome.TooManyRows,
                    ErrorDetail: $"Result set ({total} rows) exceeds the export limit of {MaxExportRows}.");

            var selectList = string.Join(", ", selectCols.Select(QuoteIdentifier));
            var selectSql = src.WithClause + $"SELECT {selectList} FROM {src.Relation}{where.Where}{orderBy}";
            var rows = await QueryRowsAsync(conn, selectSql, parameters, src, ExportCommandTimeoutSeconds, ct)
                .ConfigureAwait(false);

            // The export writers key each cell by the column-name list they're given (which here is
            // the header labels), so re-key the source-column rows to the headers in column order.
            var rekeyed = new List<IDictionary<string, object?>>(rows.Count);
            foreach (var row in rows)
            {
                var d = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < selectCols.Count; i++)
                    d[headers[i]] = row.TryGetValue(selectCols[i], out var v) ? v : null;
                rekeyed.Add(d);
            }

            return new DatasetExportResult(DatasetExportOutcome.Success,
                new DatasetExportData(name.Value, headers, rekeyed));
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Aggregate name → COUNT(*) / SUM("col") / … . Mapped (never interpolated raw) so the
    // request's aggregate string can't reach SQL directly.
    private static readonly HashSet<string> AggregatesNeedingValue =
        new(StringComparer.Ordinal) { "sum", "avg", "min", "max" };
    private const int MaxChartCategories = 50;

    public async Task<DatasetChartResult> GetChartAsync(
        Guid datasetId, DatasetChartRequest request, CancellationToken ct, Guid? authUserId = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var aggregate = (request.Aggregate ?? "").Trim().ToLowerInvariant();
        var needsValue = AggregatesNeedingValue.Contains(aggregate);
        if (aggregate != "count" && !needsValue)
            return new DatasetChartResult(DatasetChartOutcome.InvalidRequest,
                ErrorDetail: $"Unsupported aggregate '{request.Aggregate}'.");

        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var resolved = await DatasetSourceResolver.ResolveAsync(
                conn, datasetId, request.QueryParameters, allowParameterized: true,
                CommandTimeoutSeconds, ct).ConfigureAwait(false);
            if (resolved.Outcome == DatasetSourceOutcome.NotFound)
                return new DatasetChartResult(DatasetChartOutcome.NotFound);
            if (resolved.Outcome != DatasetSourceOutcome.Ok)
                return new DatasetChartResult(DatasetChartOutcome.InvalidRequest, ErrorDetail: resolved.Error);
            var src = resolved.Source!;
            var columnTypes = src.ColumnTypes;

            if (string.IsNullOrWhiteSpace(request.CategoryColumn) || !columnTypes.ContainsKey(request.CategoryColumn))
                return new DatasetChartResult(DatasetChartOutcome.InvalidRequest,
                    ErrorDetail: $"Unknown category column '{request.CategoryColumn}'.");
            if (needsValue && (string.IsNullOrWhiteSpace(request.ValueColumn) || !columnTypes.ContainsKey(request.ValueColumn!)))
                return new DatasetChartResult(DatasetChartOutcome.InvalidRequest,
                    ErrorDetail: $"Unknown value column '{request.ValueColumn}'.");

            var catQ = QuoteIdentifier(request.CategoryColumn);
            var aggExpr = aggregate switch
            {
                "count" => "COUNT(*)",
                "sum" => $"SUM({QuoteIdentifier(request.ValueColumn!)})",
                "avg" => $"AVG({QuoteIdentifier(request.ValueColumn!)})",
                "min" => $"MIN({QuoteIdentifier(request.ValueColumn!)})",
                "max" => $"MAX({QuoteIdentifier(request.ValueColumn!)})",
                _ => "COUNT(*)",
            };

            var parameters = new DynamicParameters();
            var where = DatasetFilterWhereBuilder.Build(
                request.Filters, columnTypes, parameters,
                ResolveAuthFilter(request.AuthFilterColumn, authUserId));
            if (!where.IsValid)
                return new DatasetChartResult(DatasetChartOutcome.InvalidRequest, ErrorDetail: where.Error);

            parameters.Add("p_chart_limit", MaxChartCategories);
            // Cast the aggregate to double so any numeric type binds uniformly; cast the
            // category to text for a stable label. Largest categories first, then by label.
            var sql = src.WithClause +
                $"SELECT {catQ}::text AS category, CAST({aggExpr} AS double precision) AS value " +
                $"FROM {src.Relation}{where.Where} GROUP BY {catQ} " +
                "ORDER BY value DESC NULLS LAST, category LIMIT @p_chart_limit";

            try
            {
                var raw = await QueryRowsAsync(conn, sql, parameters, src, CommandTimeoutSeconds, ct)
                    .ConfigureAwait(false);
                var points = raw
                    .Select(r => new DatasetChartPoint(
                        r.TryGetValue("category", out var c) && c is not null
                            ? Convert.ToString(c, CultureInfo.InvariantCulture) ?? string.Empty
                            : string.Empty,
                        r.TryGetValue("value", out var v) && v is double d ? d : 0d))
                    .ToList();
                return new DatasetChartResult(DatasetChartOutcome.Success,
                    new DatasetChartData(request.CategoryColumn, request.ValueColumn, aggregate, points));
            }
            catch (Npgsql.PostgresException ex)
            {
                // e.g. SUM/AVG on a non-numeric value column — surface as a config error (422).
                return new DatasetChartResult(DatasetChartOutcome.InvalidRequest, ErrorDetail: ex.MessageText);
            }
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<DatasetTreeLevelResult> GetTreeLevelAsync(
        Guid datasetId, DatasetTreeLevelRequest request, CancellationToken ct, Guid? authUserId = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize is < 1 or > 100 ? 25 : request.PageSize;

        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var resolved = await DatasetSourceResolver.ResolveAsync(
                conn, datasetId, request.QueryParameters, allowParameterized: true,
                CommandTimeoutSeconds, ct).ConfigureAwait(false);
            if (resolved.Outcome == DatasetSourceOutcome.NotFound)
                return new DatasetTreeLevelResult(DatasetRowsOutcome.NotFound);
            if (resolved.Outcome != DatasetSourceOutcome.Ok)
                return new DatasetTreeLevelResult(DatasetRowsOutcome.InvalidRequest, ErrorDetail: resolved.Error);
            var src = resolved.Source!;
            var columnTypes = src.ColumnTypes;
            if (!columnTypes.ContainsKey(request.KeyColumn))
                return new DatasetTreeLevelResult(DatasetRowsOutcome.InvalidRequest,
                    ErrorDetail: $"Unknown key column '{request.KeyColumn}'.");
            if (!columnTypes.ContainsKey(request.ParentColumn))
                return new DatasetTreeLevelResult(DatasetRowsOutcome.InvalidRequest,
                    ErrorDetail: $"Unknown parent column '{request.ParentColumn}'.");

            var authFilter = ResolveAuthFilter(request.AuthFilterColumn, authUserId);
            if (authFilter is { } afCheck && !columnTypes.ContainsKey(afCheck.Column))
                return new DatasetTreeLevelResult(DatasetRowsOutcome.InvalidRequest,
                    ErrorDetail: $"Unknown auth filter column '{afCheck.Column}'.");

            var viewRef = src.Relation;
            var parameters = new DynamicParameters();

            // Level WHERE = parent predicate (+ optional search OR across all columns) +
            // server-resolved auth scope. Built as one FilterGroupDto so DatasetFilterWhereBuilder
            // handles value coercion/parameterisation; its predicates are unqualified and resolve
            // to the outer "t" relation (the EXISTS subquery below has its own alias "c").
            var group = BuildTreeLevelFilterGroup(request, src.ColumnOrder);
            var where = DatasetFilterWhereBuilder.Build(group, columnTypes, parameters, authFilter);
            if (!where.IsValid)
                return new DatasetTreeLevelResult(DatasetRowsOutcome.InvalidRequest, ErrorDetail: where.Error);

            // _has_children: correlated EXISTS over the same VIEW linking child.parent = t.key,
            // honoring the auth scope so a node whose only children are hidden reads as a leaf.
            var hasChildren =
                $"EXISTS (SELECT 1 FROM {viewRef} c WHERE c.{QuoteIdentifier(request.ParentColumn)} = t.{QuoteIdentifier(request.KeyColumn)}";
            if (authFilter is { } af)
            {
                object hcAuthValue = columnTypes.TryGetValue(af.Column, out var at) && at == "uuid"
                    ? af.Value
                    : af.Value.ToString();
                parameters.Add("p_hc_auth", hcAuthValue);
                hasChildren += $" AND c.{QuoteIdentifier(af.Column)} = @p_hc_auth";
            }
            hasChildren += ") AS _has_children";

            // Stable ordering for pagination — the key column (the VIEW may have no created_at).
            var orderBy = $" ORDER BY t.{QuoteIdentifier(request.KeyColumn)} ASC";

            parameters.Add("p_limit", pageSize + 1);
            parameters.Add("p_offset", (long)(page - 1) * pageSize);

            var selectList = string.Join(", ", src.ColumnOrder.Select(c => $"t.{QuoteIdentifier(c)}"));
            var sql = src.WithClause +
                $"SELECT {selectList}, {hasChildren} FROM {viewRef} t{where.Where}{orderBy} " +
                "LIMIT @p_limit OFFSET @p_offset";

            var rows = await QueryRowsAsync(conn, sql, parameters, src, CommandTimeoutSeconds, ct).ConfigureAwait(false);
            var hasNextPage = rows.Count > pageSize;
            if (hasNextPage) rows.RemoveAt(rows.Count - 1);

            return new DatasetTreeLevelResult(DatasetRowsOutcome.Success,
                new DatasetTreeLevelPage(rows, hasNextPage, page, pageSize));
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<DatasetTreeDescendantsResult> GetTreeDescendantsAsync(
        Guid datasetId, string keyColumn, string parentColumn, string parentId,
        string? authFilterColumn, CancellationToken ct, Guid? authUserId = null)
    {
        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            // Tree descendants does not carry runtime placeholder values, so a parameterized
            // dataset resolves to MissingParameters → InvalidRequest here (TreeView on a
            // parameterized query is unsupported); placeholder-free query + view types work.
            var resolved = await DatasetSourceResolver.ResolveAsync(
                conn, datasetId, queryParametersJson: null, allowParameterized: true,
                CommandTimeoutSeconds, ct).ConfigureAwait(false);
            if (resolved.Outcome == DatasetSourceOutcome.NotFound)
                return new DatasetTreeDescendantsResult(DatasetRowsOutcome.NotFound);
            if (resolved.Outcome != DatasetSourceOutcome.Ok)
                return new DatasetTreeDescendantsResult(DatasetRowsOutcome.InvalidRequest, ErrorDetail: resolved.Error);
            var src = resolved.Source!;
            var columnTypes = src.ColumnTypes;
            if (!columnTypes.ContainsKey(keyColumn))
                return new DatasetTreeDescendantsResult(DatasetRowsOutcome.InvalidRequest,
                    ErrorDetail: $"Unknown key column '{keyColumn}'.");
            if (!columnTypes.ContainsKey(parentColumn))
                return new DatasetTreeDescendantsResult(DatasetRowsOutcome.InvalidRequest,
                    ErrorDetail: $"Unknown parent column '{parentColumn}'.");

            var authFilter = ResolveAuthFilter(authFilterColumn, authUserId);
            if (authFilter is { } afc && !columnTypes.ContainsKey(afc.Column))
                return new DatasetTreeDescendantsResult(DatasetRowsOutcome.InvalidRequest,
                    ErrorDetail: $"Unknown auth filter column '{afc.Column}'.");

            var viewRef = src.Relation;
            var kc = QuoteIdentifier(keyColumn);
            var pc = QuoteIdentifier(parentColumn);

            var parameters = new DynamicParameters();
            // Coerce the anchor parent id to the parent column's type (uuid -> Guid).
            object parentParam = columnTypes.TryGetValue(parentColumn, out var pcType) && pcType == "uuid"
                ? (Guid.TryParse(parentId, out var pg) ? pg : (object)parentId)
                : parentId;
            parameters.Add("p_parent", parentParam);

            var authAnchor = string.Empty;
            var authRec = string.Empty;
            if (authFilter is { } af)
            {
                object authVal = columnTypes.TryGetValue(af.Column, out var at) && at == "uuid"
                    ? af.Value
                    : af.Value.ToString();
                parameters.Add("p_auth", authVal);
                var ac = QuoteIdentifier(af.Column);
                authAnchor = $" AND {ac} = @p_auth";
                authRec = $" AND c.{ac} = @p_auth";
            }

            // UNION (not UNION ALL) dedups in case the dataset is not a strict tree. For a
            // query-type source the dataset's own CTE is composed into the same WITH RECURSIVE
            // list (RECURSIVE may precede a non-recursive CTE — _ds — without issue).
            var sql =
                "WITH RECURSIVE " +
                (src.CteDefinition.Length > 0 ? src.CteDefinition + ", " : string.Empty) +
                "d AS (" +
                $"SELECT {kc} AS k FROM {viewRef} WHERE {pc} = @p_parent{authAnchor} " +
                $"UNION SELECT c.{kc} FROM {viewRef} c JOIN d ON c.{pc} = d.k{authRec}" +
                ") SELECT k FROM d";

            var raw = await QueryRowsAsync(conn, sql, parameters, src, CommandTimeoutSeconds, ct)
                .ConfigureAwait(false);
            var ids = raw
                .Select(r => r.TryGetValue("k", out var v) && v is not null
                    ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty
                    : string.Empty)
                .Where(s => s.Length > 0)
                .ToList();
            return new DatasetTreeDescendantsResult(DatasetRowsOutcome.Success, ids);
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<DatasetTreeAncestorsResult> GetTreeAncestorsAsync(
        Guid datasetId, string keyColumn, string parentColumn, IReadOnlyList<string> ids,
        string? authFilterColumn, CancellationToken ct, Guid? authUserId = null)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
            return new DatasetTreeAncestorsResult(DatasetRowsOutcome.Success, Array.Empty<string>());

        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            // Mirrors GetTreeDescendantsAsync: a parameterized dataset is unsupported for tree
            // reveal (no runtime placeholders), placeholder-free query + view types work.
            var resolved = await DatasetSourceResolver.ResolveAsync(
                conn, datasetId, queryParametersJson: null, allowParameterized: true,
                CommandTimeoutSeconds, ct).ConfigureAwait(false);
            if (resolved.Outcome == DatasetSourceOutcome.NotFound)
                return new DatasetTreeAncestorsResult(DatasetRowsOutcome.NotFound);
            if (resolved.Outcome != DatasetSourceOutcome.Ok)
                return new DatasetTreeAncestorsResult(DatasetRowsOutcome.InvalidRequest, ErrorDetail: resolved.Error);
            var src = resolved.Source!;
            var columnTypes = src.ColumnTypes;
            if (!columnTypes.ContainsKey(keyColumn))
                return new DatasetTreeAncestorsResult(DatasetRowsOutcome.InvalidRequest,
                    ErrorDetail: $"Unknown key column '{keyColumn}'.");
            if (!columnTypes.ContainsKey(parentColumn))
                return new DatasetTreeAncestorsResult(DatasetRowsOutcome.InvalidRequest,
                    ErrorDetail: $"Unknown parent column '{parentColumn}'.");

            var authFilter = ResolveAuthFilter(authFilterColumn, authUserId);
            if (authFilter is { } afc && !columnTypes.ContainsKey(afc.Column))
                return new DatasetTreeAncestorsResult(DatasetRowsOutcome.InvalidRequest,
                    ErrorDetail: $"Unknown auth filter column '{afc.Column}'.");

            var viewRef = src.Relation;
            var kc = QuoteIdentifier(keyColumn);
            var pc = QuoteIdentifier(parentColumn);

            var parameters = new DynamicParameters();
            // Compare on ::text so the seed-id array works regardless of the key column type
            // (uuid / int / text). Ids round-trip to/from the client as strings already.
            parameters.Add("p_ids", ids.ToArray());

            var authAnchor = string.Empty;
            var authRec = string.Empty;
            if (authFilter is { } af)
            {
                object authVal = columnTypes.TryGetValue(af.Column, out var at) && at == "uuid"
                    ? af.Value
                    : af.Value.ToString();
                parameters.Add("p_auth", authVal);
                var ac = QuoteIdentifier(af.Column);
                authAnchor = $" AND {ac} = @p_auth";
                authRec = $" AND c.{ac} = @p_auth";
            }

            // Anchor on the seed nodes, then recurse to each row whose key matches a
            // collected parent. UNION dedups shared ancestors / non-strict trees.
            var sql =
                "WITH RECURSIVE " +
                (src.CteDefinition.Length > 0 ? src.CteDefinition + ", " : string.Empty) +
                "a AS (" +
                $"SELECT {kc} AS k, {pc} AS p FROM {viewRef} WHERE {kc}::text = ANY(@p_ids){authAnchor} " +
                $"UNION SELECT c.{kc}, c.{pc} FROM {viewRef} c JOIN a ON c.{kc} = a.p{authRec}" +
                ") SELECT k FROM a WHERE NOT (k::text = ANY(@p_ids))";

            var raw = await QueryRowsAsync(conn, sql, parameters, src, CommandTimeoutSeconds, ct)
                .ConfigureAwait(false);
            var resultIds = raw
                .Select(r => r.TryGetValue("k", out var v) && v is not null
                    ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty
                    : string.Empty)
                .Where(s => s.Length > 0)
                .ToList();
            return new DatasetTreeAncestorsResult(DatasetRowsOutcome.Success, resultIds);
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Composes the level WHERE tree: the parent predicate (roots = parent IS NULL) plus an
    // optional OR-group of ILIKE conditions across every column for `search`.
    private static FilterGroupDto BuildTreeLevelFilterGroup(
        DatasetTreeLevelRequest request, IReadOnlyList<string> allColumns)
    {
        var items = new List<FilterItemDto>();

        if (string.IsNullOrWhiteSpace(request.ParentId))
        {
            items.Add(new FilterConditionDto(
                "parent", "condition", string.Empty, request.ParentColumn, "IS NULL",
                JsonSerializer.SerializeToElement((string?)null)));
        }
        else
        {
            items.Add(new FilterConditionDto(
                "parent", "condition", string.Empty, request.ParentColumn, "=",
                JsonSerializer.SerializeToElement(request.ParentId.Trim())));
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            var searchItems = allColumns
                .Select(c => (FilterItemDto)new FilterConditionDto(
                    $"s_{c}", "condition", string.Empty, c, "ILIKE",
                    JsonSerializer.SerializeToElement(term)))
                .ToList();
            if (searchItems.Count > 0)
                items.Add(new FilterGroupDto("search", "group", "OR", searchItems));
        }

        return new FilterGroupDto("root", "group", "AND", items);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    // Forms the auth-scope predicate for DatasetFilterWhereBuilder: present only when the
    // component declares an AuthFilterColumn AND the request carries an authenticated user
    // id. The value is the server-resolved user id — never anything from the request body.
    private static (string Column, Guid Value)? ResolveAuthFilter(string? authFilterColumn, Guid? authUserId) =>
        !string.IsNullOrWhiteSpace(authFilterColumn) && authUserId.HasValue
            ? (authFilterColumn!.Trim(), authUserId.Value)
            : null;

    private static (List<string> Columns, string? Error) ResolveSelectColumns(
        List<string>? requested, IReadOnlyList<string> order, IReadOnlyDictionary<string, string> types)
    {
        if (requested is not { Count: > 0 })
            return ([.. order], null);
        var selected = new List<string>();
        foreach (var c in requested)
        {
            if (string.IsNullOrWhiteSpace(c) || !types.ContainsKey(c))
                return ([], $"Unknown column '{c}'.");
            selected.Add(c);
        }
        return (selected, null);
    }

    private static (string OrderBy, string? Error) BuildOrderBy(
        List<DatasetSortDto>? sort, IReadOnlyDictionary<string, string> columnTypes)
    {
        if (sort is not { Count: > 0 })
            return (string.Empty, null);
        var parts = new List<string>();
        foreach (var s in sort)
        {
            if (string.IsNullOrWhiteSpace(s.Column) || !columnTypes.ContainsKey(s.Column))
                return (string.Empty, $"Unknown sort column '{s.Column}'.");
            var dir = string.Equals(s.Direction, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
            parts.Add($"{QuoteIdentifier(s.Column)} {dir}");
        }
        return parts.Count == 0 ? (string.Empty, null) : (" ORDER BY " + string.Join(", ", parts), null);
    }

    // Executes a SELECT against a resolved dataset source, binding the filter parameters
    // (typed, via AddWithValue) and the source's {_placeholder} parameters (as `unknown`-typed
    // so they coerce like literals). Returns the rows as ordinal-keyed dictionaries. Used for
    // every read path so view and query (CTE) sources execute through one binding routine.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
        Justification = "SQL is built from validated identifiers + the SELECT-validated dataset "
            + "query; all values (filters + placeholders) are bound parameters, never concatenated.")]
    private static async Task<List<IDictionary<string, object?>>> QueryRowsAsync(
        NpgsqlConnection conn, string sql, DynamicParameters parameters,
        DatasetResolvedSource src, int timeout, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = timeout;
        BindParameters(cmd, parameters, src);

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<IDictionary<string, object?>>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var v = reader.GetValue(i);
                dict[reader.GetName(i)] = v is DBNull ? null : v;
            }
            result.Add(dict);
        }
        return result;
    }

    // COUNT(*)-style scalar with the same dual parameter binding as QueryRowsAsync.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
        Justification = "SQL is built from validated identifiers + the SELECT-validated dataset "
            + "query; all values (filters + placeholders) are bound parameters, never concatenated.")]
    private static async Task<long> ScalarLongAsync(
        NpgsqlConnection conn, string sql, DynamicParameters parameters,
        DatasetResolvedSource src, int timeout, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = timeout;
        BindParameters(cmd, parameters, src);
        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return scalar is null or DBNull ? 0L : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    // Binds the Dapper filter parameters (already typed by DatasetFilterWhereBuilder.Coerce)
    // and the source's placeholder parameters. Placeholder values are bound as PostgreSQL
    // `unknown`-typed (string form) so they coerce contextually like an inline literal —
    // a plain text param would not (e.g. uuid = text has no operator).
    private static void BindParameters(
        NpgsqlCommand cmd, DynamicParameters? filterParams, DatasetResolvedSource src)
    {
        if (filterParams is not null)
        {
            foreach (var pn in filterParams.ParameterNames)
                cmd.Parameters.AddWithValue(pn, filterParams.Get<object>(pn) ?? DBNull.Value);
        }

        foreach (var (name, value) in src.NamedParameters)
        {
            cmd.Parameters.Add(new NpgsqlParameter
            {
                ParameterName = name,
                NpgsqlDbType = NpgsqlDbType.Unknown,
                Value = value is null
                    ? DBNull.Value
                    : Convert.ToString(value, CultureInfo.InvariantCulture) ?? (object)DBNull.Value,
            });
        }
    }

    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
