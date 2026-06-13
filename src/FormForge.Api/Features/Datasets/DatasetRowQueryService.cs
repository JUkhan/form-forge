using System.Globalization;
using Dapper;
using FormForge.Api.Domain.ValueTypes;
using FormForge.Api.Features.Datasets.Dtos;
using FormForge.Api.Infrastructure.Persistence;

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
            var name = await ResolveDatasetNameAsync(conn, datasetId, ct).ConfigureAwait(false);
            if (name is null)
                return new DatasetRowsResult(DatasetRowsOutcome.NotFound);

            var cols = await GetViewColumnsAsync(conn, name, ct).ConfigureAwait(false);
            var columnTypes = cols.Types;
            var viewRef = $"datasets.{QuoteIdentifier(name.Value)}";

            // Column projection: requested allow/order list (validated) or all columns.
            var (selectCols, colError) = ResolveSelectColumns(request.Columns, cols);
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

            var countSql = $"SELECT COUNT(*) FROM {viewRef}{where.Where}";
            var total = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                countSql, parameters, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))
                .ConfigureAwait(false);

            parameters.Add("p_limit", pageSize);
            parameters.Add("p_offset", (long)(page - 1) * pageSize);
            var selectList = string.Join(", ", selectCols.Select(QuoteIdentifier));
            var selectSql =
                $"SELECT {selectList} FROM {viewRef}{where.Where}{orderBy} LIMIT @p_limit OFFSET @p_offset";

            var rows = await QueryRowsAsync(conn, selectSql, parameters, CommandTimeoutSeconds, ct)
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
            var name = await ResolveDatasetNameAsync(conn, datasetId, ct).ConfigureAwait(false);
            if (name is null)
                return new DatasetExportResult(DatasetExportOutcome.NotFound);

            var cols = await GetViewColumnsAsync(conn, name, ct).ConfigureAwait(false);
            var columnTypes = cols.Types;
            var viewRef = $"datasets.{QuoteIdentifier(name.Value)}";

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
                selectCols = [.. cols.Order];
                headers = [.. cols.Order];
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
            var countSql = $"SELECT COUNT(*) FROM {viewRef}{where.Where}";
            var total = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                countSql, parameters, commandTimeout: ExportCommandTimeoutSeconds, cancellationToken: ct))
                .ConfigureAwait(false);
            if (total > MaxExportRows)
                return new DatasetExportResult(DatasetExportOutcome.TooManyRows,
                    ErrorDetail: $"Result set ({total} rows) exceeds the export limit of {MaxExportRows}.");

            var selectList = string.Join(", ", selectCols.Select(QuoteIdentifier));
            var selectSql = $"SELECT {selectList} FROM {viewRef}{where.Where}{orderBy}";
            var rows = await QueryRowsAsync(conn, selectSql, parameters, ExportCommandTimeoutSeconds, ct)
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
            var name = await ResolveDatasetNameAsync(conn, datasetId, ct).ConfigureAwait(false);
            if (name is null)
                return new DatasetChartResult(DatasetChartOutcome.NotFound);

            var cols = await GetViewColumnsAsync(conn, name, ct).ConfigureAwait(false);
            var columnTypes = cols.Types;

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
            var viewRef = $"datasets.{QuoteIdentifier(name.Value)}";
            // Cast the aggregate to double so any numeric type binds uniformly; cast the
            // category to text for a stable label. Largest categories first, then by label.
            var sql =
                $"SELECT {catQ}::text AS category, CAST({aggExpr} AS double precision) AS value " +
                $"FROM {viewRef}{where.Where} GROUP BY {catQ} " +
                "ORDER BY value DESC NULLS LAST, category LIMIT @p_chart_limit";

            try
            {
                var raw = await conn.QueryAsync(new CommandDefinition(
                    sql, parameters, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))
                    .ConfigureAwait(false);
                var points = raw
                    .Cast<IDictionary<string, object>>()
                    .Select(r => new DatasetChartPoint(
                        r.TryGetValue("category", out var c) && c is not null and not DBNull
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

    // ── helpers ──────────────────────────────────────────────────────────────────────

    // Forms the auth-scope predicate for DatasetFilterWhereBuilder: present only when the
    // component declares an AuthFilterColumn AND the request carries an authenticated user
    // id. The value is the server-resolved user id — never anything from the request body.
    private static (string Column, Guid Value)? ResolveAuthFilter(string? authFilterColumn, Guid? authUserId) =>
        !string.IsNullOrWhiteSpace(authFilterColumn) && authUserId.HasValue
            ? (authFilterColumn!.Trim(), authUserId.Value)
            : null;

    private static (List<string> Columns, string? Error) ResolveSelectColumns(
        List<string>? requested, ColumnSet cols)
    {
        if (requested is not { Count: > 0 })
            return ([.. cols.Order], null);
        var selected = new List<string>();
        foreach (var c in requested)
        {
            if (string.IsNullOrWhiteSpace(c) || !cols.Types.ContainsKey(c))
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

    private static async Task<List<IDictionary<string, object?>>> QueryRowsAsync(
        Npgsql.NpgsqlConnection conn, string sql, DynamicParameters parameters, int timeout, CancellationToken ct)
    {
        var raw = await conn.QueryAsync(new CommandDefinition(
            sql, parameters, commandTimeout: timeout, cancellationToken: ct)).ConfigureAwait(false);
        var result = new List<IDictionary<string, object?>>();
        foreach (var row in raw.Cast<IDictionary<string, object>>())
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var kv in row)
                dict[kv.Key] = kv.Value is DBNull ? null : kv.Value;
            result.Add(dict);
        }
        return result;
    }

    private static async Task<DatasetName?> ResolveDatasetNameAsync(
        Npgsql.NpgsqlConnection conn, Guid id, CancellationToken ct)
    {
        var rawName = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT dataset_name FROM custom_dataset WHERE id = @id",
            new { id }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))
            .ConfigureAwait(false);
        if (rawName is null)
            return null;
        return DatasetName.TryCreate(rawName, out var name, out _) ? name : null;
    }

    // The dataset VIEW's columns: an ordinal-ordered name list (for the default projection)
    // plus a name → information_schema data_type map (for filter value coercion + validation).
    private sealed record ColumnSet(IReadOnlyList<string> Order, IReadOnlyDictionary<string, string> Types);

    private static async Task<ColumnSet> GetViewColumnsAsync(
        Npgsql.NpgsqlConnection conn, DatasetName name, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<(string ColumnName, string DataType)>(new CommandDefinition(
            """
            SELECT column_name AS "ColumnName", data_type AS "DataType"
            FROM information_schema.columns
            WHERE table_schema = 'datasets' AND table_name = @name
            ORDER BY ordinal_position
            """,
            new { name = name.Value }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))
            .ConfigureAwait(false);

        var order = new List<string>();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (col, type) in rows)
        {
            order.Add(col);
            map[col] = type;
        }
        return new ColumnSet(order, map);
    }

    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
