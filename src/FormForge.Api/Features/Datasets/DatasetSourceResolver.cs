using System.Data.Common;
using Dapper;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Domain.ValueTypes;
using Npgsql;

namespace FormForge.Api.Features.Datasets;

// Parameterized-query feature — resolves a dataset into a runtime SQL "source" that the
// row/chart/dropdown services can query uniformly, regardless of query type:
//   • "view"  → the source is the backing VIEW (datasets."<name>"); columns come from
//               information_schema; no WITH clause, no parameters.
//   • "query" → the source is a CTE (WITH _ds AS (<stored sql>)); the relation is referenced
//               as "_ds" exactly like a view name; columns come from a LIMIT-0 schema probe.
//               When the SQL has {_placeholder} tokens it is parameterized — restricted to
//               callers that pass allowParameterized=true (the Dataset palette), and its
//               placeholder values are resolved to @qp_* named parameters bound as `unknown`.
//
// Safety: the relation is either a validated DatasetName-quoted view or the constant "_ds"
// CTE alias; placeholder values are always bound parameters (never interpolated).
internal enum DatasetSourceOutcome { Ok, NotFound, Forbidden, MissingParameters, Invalid }

internal sealed record DatasetResolvedSource(
    DatasetName Name,
    string QueryType,
    bool IsParameterized,
    // FROM-clause relation: datasets."name" (view) or "_ds" (CTE alias).
    string Relation,
    // Leading WITH clause for non-recursive statements ("" for views, "WITH _ds AS (…) ").
    string WithClause,
    // The inner CTE definition without the WITH keyword ("" for views, "_ds AS (…)") — for
    // composing into a statement that already needs WITH RECURSIVE (tree descendants).
    string CteDefinition,
    // Placeholder parameters to bind (name without '@'); bound as `unknown`-typed at exec.
    IReadOnlyList<(string Name, object? Value)> NamedParameters,
    IReadOnlyList<string> ColumnOrder,
    IReadOnlyDictionary<string, string> ColumnTypes);

internal sealed record DatasetSourceResolution(
    DatasetSourceOutcome Outcome,
    DatasetResolvedSource? Source = null,
    string? Error = null,
    IReadOnlyList<string>? MissingParameters = null);

internal static class DatasetSourceResolver
{
    private const string CteName = "_ds";

    // requireParameterValues: when true (execution paths) a parameterized dataset missing a
    // placeholder value returns MissingParameters; when false (pure column discovery for the
    // inspector) the columns are still returned and the missing values are ignored.
    internal static async Task<DatasetSourceResolution> ResolveAsync(
        NpgsqlConnection conn, Guid datasetId, string? queryParametersJson,
        bool allowParameterized, int timeout, CancellationToken ct,
        bool requireParameterValues = true)
    {
        var row = await conn.QuerySingleOrDefaultAsync<DatasetRow>(new CommandDefinition(
            """
            SELECT dataset_name AS "DatasetName", query_type AS "QueryType", query AS "Query"
            FROM custom_dataset WHERE id = @id
            """,
            new { id = datasetId }, commandTimeout: timeout, cancellationToken: ct)).ConfigureAwait(false);
        return await BuildAsync(conn, row, queryParametersJson, allowParameterized,
            requireParameterValues, timeout, ct).ConfigureAwait(false);
    }

    internal static async Task<DatasetSourceResolution> ResolveByNameAsync(
        NpgsqlConnection conn, DatasetName name, string? queryParametersJson,
        bool allowParameterized, int timeout, CancellationToken ct,
        bool requireParameterValues = true)
    {
        ArgumentNullException.ThrowIfNull(name);
        var row = await conn.QuerySingleOrDefaultAsync<DatasetRow>(new CommandDefinition(
            """
            SELECT dataset_name AS "DatasetName", query_type AS "QueryType", query AS "Query"
            FROM custom_dataset WHERE dataset_name = @name
            """,
            new { name = name.Value }, commandTimeout: timeout, cancellationToken: ct)).ConfigureAwait(false);
        return await BuildAsync(conn, row, queryParametersJson, allowParameterized,
            requireParameterValues, timeout, ct).ConfigureAwait(false);
    }

    private static async Task<DatasetSourceResolution> BuildAsync(
        NpgsqlConnection conn, DatasetRow? row, string? queryParametersJson,
        bool allowParameterized, bool requireParameterValues, int timeout, CancellationToken ct)
    {
        if (row is null || !DatasetName.TryCreate(row.DatasetName, out var name, out _))
            return new DatasetSourceResolution(DatasetSourceOutcome.NotFound);

        // ── view-type: read from the backing VIEW, columns from information_schema ──
        if (row.QueryType != DatasetQueryTypes.Query)
        {
            var viewCols = await GetViewColumnsAsync(conn, name!, timeout, ct).ConfigureAwait(false);
            if (viewCols.Order.Count == 0)
                return new DatasetSourceResolution(DatasetSourceOutcome.NotFound);
            return Ok(new DatasetResolvedSource(
                name!, DatasetQueryTypes.View, IsParameterized: false,
                Relation: $"datasets.{QuoteIdentifier(name!.Value)}",
                WithClause: string.Empty, CteDefinition: string.Empty,
                NamedParameters: [], viewCols.Order, viewCols.Types));
        }

        // ── query-type: source is a CTE over the stored SQL ──
        var query = row.Query ?? string.Empty;
        var isParameterized = DatasetParameterResolver.HasPlaceholders(query);
        if (isParameterized && !allowParameterized)
            return new DatasetSourceResolution(DatasetSourceOutcome.Forbidden,
                Error: "This dataset is a parameterized query and can only be used by the "
                       + "Dataset palette element.");

        // Discover columns from a placeholder-free schema probe (values irrelevant for shape).
        var probeSql = DatasetParameterResolver.BuildSchemaProbe(query);
        ColumnSet probeCols;
        try
        {
            probeCols = await ProbeColumnsAsync(conn, probeSql, timeout, ct).ConfigureAwait(false);
        }
        catch (PostgresException ex)
        {
            // A stored query-type dataset is SELECT-validated at save, so this is rare
            // (e.g. an underlying table was dropped). Surface as a clear config error.
            return new DatasetSourceResolution(DatasetSourceOutcome.Invalid, Error: ex.MessageText);
        }
        if (probeCols.Order.Count == 0)
            return new DatasetSourceResolution(DatasetSourceOutcome.Invalid,
                Error: "The dataset query produced no columns.");

        // Resolve the executable body + placeholder parameters.
        var body = query;
        IReadOnlyList<(string Name, object? Value)> namedParams = [];
        if (isParameterized)
        {
            if (!DatasetParameterResolver.TryParseParameters(
                    queryParametersJson, out var values, out var parseError))
                return new DatasetSourceResolution(DatasetSourceOutcome.Invalid, Error: parseError);

            var resolved = DatasetParameterResolver.ResolveNamed(query, values);
            if (requireParameterValues && resolved.MissingParameters.Count > 0)
                return new DatasetSourceResolution(DatasetSourceOutcome.MissingParameters,
                    Error: "Missing values for parameter(s): "
                           + string.Join(", ", resolved.MissingParameters),
                    MissingParameters: resolved.MissingParameters);

            body = resolved.Sql;
            namedParams = resolved.Parameters;
        }

        var cteDefinition = $"{CteName} AS ({body})";
        return Ok(new DatasetResolvedSource(
            name!, DatasetQueryTypes.Query, isParameterized,
            Relation: CteName,
            WithClause: $"WITH {cteDefinition} ",
            CteDefinition: cteDefinition,
            NamedParameters: namedParams, probeCols.Order, probeCols.Types));
    }

    private static DatasetSourceResolution Ok(DatasetResolvedSource source) =>
        new(DatasetSourceOutcome.Ok, source);

    // The dataset's column order + name→information_schema-style data_type map, discovered by
    // running the CTE-wrapped query with LIMIT 0 and reading the result reader's schema. The
    // DbColumn.DataTypeName values ("integer", "uuid", "timestamp with time zone", …) match
    // the information_schema.data_type strings DatasetFilterWhereBuilder expects.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
        Justification = "probeSql is the stored dataset query (SELECT-validated at save) with "
            + "placeholders replaced by NULL literals; no request input is concatenated.")]
    private static async Task<ColumnSet> ProbeColumnsAsync(
        NpgsqlConnection conn, string probeSql, int timeout, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"WITH {CteName} AS ({probeSql}) SELECT * FROM {CteName} LIMIT 0";
        cmd.CommandTimeout = timeout;
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var schema = await reader.GetColumnSchemaAsync(ct).ConfigureAwait(false);

        var order = new List<string>(schema.Count);
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var col in schema)
        {
            order.Add(col.ColumnName);
            types[col.ColumnName] = col.DataTypeName ?? "text";
        }
        return new ColumnSet(order, types);
    }

    private static async Task<ColumnSet> GetViewColumnsAsync(
        NpgsqlConnection conn, DatasetName name, int timeout, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<(string ColumnName, string DataType)>(new CommandDefinition(
            """
            SELECT column_name AS "ColumnName", data_type AS "DataType"
            FROM information_schema.columns
            WHERE table_schema = 'datasets' AND table_name = @name
            ORDER BY ordinal_position
            """,
            new { name = name.Value }, commandTimeout: timeout, cancellationToken: ct)).ConfigureAwait(false);

        var order = new List<string>();
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (col, type) in rows)
        {
            order.Add(col);
            types[col] = type;
        }
        return new ColumnSet(order, types);
    }

    private sealed record ColumnSet(IReadOnlyList<string> Order, IReadOnlyDictionary<string, string> Types);

    private sealed record DatasetRow(string DatasetName, string QueryType, string? Query);

    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
