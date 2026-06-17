using System.Globalization;
using Dapper;
using FormForge.Api.Common;
using FormForge.Api.Domain.ValueTypes;
using FormForge.Api.Features.Datasets.Dtos;
using FormForge.Api.Features.DynamicCrud;
using FormForge.Api.Infrastructure.Persistence;

namespace FormForge.Api.Features.Datasets;

// Designer Dropdown "Dataset" source (FR — dataset-backed options). Serves a
// dataset's backing VIEW (datasets."<name>") two ways:
//   • GetColumnsAsync — the view's column names, for the inspector's Label/Value
//     field comboboxes.
//   • GetOptionsAsync — paginated {value,label} pairs, for the runtime dropdown.
//
// Both run through the PRIVILEGED DbConnectionFactory (the formforge role owns the
// datasets schema; the least-privileged formforge_preview role has SELECT only on
// `public`, so it cannot read dataset VIEWs). This mirrors the Designer-backed
// options handler, which also uses the privileged pool for an auth-only lookup.
//
// Injection safety: the view name is re-validated through DatasetName before being
// quoted, and label/value fields are accepted ONLY when they exactly match a column
// discovered from information_schema — never interpolated from raw user input.

internal enum DatasetOptionsOutcome { Success, NotFound, InvalidField }

internal sealed record DatasetOptionsResult(
    DatasetOptionsOutcome Outcome,
    PagedResult<DropdownOption>? Data = null,
    string? ErrorDetail = null);

internal interface IDatasetDropdownService
{
    // Returns the dataset's view columns, or null when no dataset row matches `id`.
    Task<DatasetColumnsDto?> GetColumnsAsync(Guid datasetId, CancellationToken ct);

    // Name-keyed columns lookup. The designer Dropdown stores the dataset's VIEW NAME
    // (optionsDatasetId) rather than its GUID, so the inspector resolves columns by
    // name. Returns null when no VIEW with that name exists in the datasets schema.
    Task<DatasetColumnsDto?> GetColumnsByNameAsync(DatasetName name, CancellationToken ct);

    // Returns paginated {value,label} options sourced from the dataset's view.
    // `valueEquals`, when non-null, restricts to rows whose value column equals it
    // exactly (used to resolve the label of a saved selection not on the first page).
    // `filters`, when non-null, cascades by restricting to rows whose target column
    // equals the supplied value (Designer Dropdown "Depends on" parity).
    Task<DatasetOptionsResult> GetOptionsAsync(
        Guid datasetId,
        string labelField,
        string valueField,
        string? search,
        string? valueEquals,
        IReadOnlyDictionary<string, string>? filters,
        int page,
        int pageSize,
        CancellationToken ct);

    // Name-keyed options lookup (see GetColumnsByNameAsync for why the Dropdown is
    // name-keyed). Returns NotFound when no VIEW with that name exists.
    Task<DatasetOptionsResult> GetOptionsByNameAsync(
        DatasetName name,
        string labelField,
        string valueField,
        string? search,
        string? valueEquals,
        IReadOnlyDictionary<string, string>? filters,
        int page,
        int pageSize,
        CancellationToken ct);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class DatasetDropdownService(DbConnectionFactory connectionFactory)
    : IDatasetDropdownService
{
    private const int CommandTimeoutSeconds = 5;

    public async Task<DatasetColumnsDto?> GetColumnsAsync(Guid datasetId, CancellationToken ct)
    {
        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            // Column discovery for the inspector: parameterized datasets ARE allowed here
            // (the DatasetComponent inspector needs their output columns to map filters), and
            // values are not required since columns come from a schema probe.
            var resolved = await DatasetSourceResolver.ResolveAsync(
                conn, datasetId, queryParametersJson: null, allowParameterized: true,
                CommandTimeoutSeconds, ct, requireParameterValues: false).ConfigureAwait(false);
            if (resolved.Outcome != DatasetSourceOutcome.Ok)
                return null;
            var src = resolved.Source!;
            return new DatasetColumnsDto(datasetId, src.Name.Value, [.. src.ColumnOrder]);
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<DatasetColumnsDto?> GetColumnsByNameAsync(DatasetName name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);

        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var resolved = await DatasetSourceResolver.ResolveByNameAsync(
                conn, name, queryParametersJson: null, allowParameterized: true,
                CommandTimeoutSeconds, ct, requireParameterValues: false).ConfigureAwait(false);
            if (resolved.Outcome != DatasetSourceOutcome.Ok)
                return null;
            var src = resolved.Source!;
            return new DatasetColumnsDto(Guid.Empty, src.Name.Value, [.. src.ColumnOrder]);
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<DatasetOptionsResult> GetOptionsAsync(
        Guid datasetId,
        string labelField,
        string valueField,
        string? search,
        string? valueEquals,
        IReadOnlyDictionary<string, string>? filters,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(labelField);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueField);

        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 50;

        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            // Dropdown options reject parameterized queries (palette-only): allowParameterized=false.
            var resolved = await DatasetSourceResolver.ResolveAsync(
                conn, datasetId, queryParametersJson: null, allowParameterized: false,
                CommandTimeoutSeconds, ct).ConfigureAwait(false);
            var mapped = MapNonOk(resolved);
            if (mapped is not null)
                return mapped;

            return await BuildOptionsAsync(
                conn, resolved.Source!, labelField, valueField, search, valueEquals, filters, page, pageSize, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Maps a non-Ok source resolution to the options result (null when Ok). NotFound → 404;
    // Forbidden (a parameterized query used as a dropdown source) / Invalid → 422 InvalidField.
    private static DatasetOptionsResult? MapNonOk(DatasetSourceResolution resolved) =>
        resolved.Outcome switch
        {
            DatasetSourceOutcome.Ok => null,
            DatasetSourceOutcome.NotFound => new DatasetOptionsResult(DatasetOptionsOutcome.NotFound),
            _ => new DatasetOptionsResult(DatasetOptionsOutcome.InvalidField, ErrorDetail: resolved.Error),
        };

    public async Task<DatasetOptionsResult> GetOptionsByNameAsync(
        DatasetName name,
        string labelField,
        string valueField,
        string? search,
        string? valueEquals,
        IReadOnlyDictionary<string, string>? filters,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(labelField);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueField);

        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 50;

        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var resolved = await DatasetSourceResolver.ResolveByNameAsync(
                conn, name, queryParametersJson: null, allowParameterized: false,
                CommandTimeoutSeconds, ct).ConfigureAwait(false);
            var mapped = MapNonOk(resolved);
            if (mapped is not null)
                return mapped;

            return await BuildOptionsAsync(
                conn, resolved.Source!, labelField, valueField, search, valueEquals, filters, page, pageSize, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Shared options query against a resolved dataset source (VIEW or query-type CTE).
    private static async Task<DatasetOptionsResult> BuildOptionsAsync(
        Npgsql.NpgsqlConnection conn,
        DatasetResolvedSource src,
        string labelField,
        string valueField,
        string? search,
        string? valueEquals,
        IReadOnlyDictionary<string, string>? filters,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        {
            var allowed = new HashSet<string>(src.ColumnOrder, StringComparer.Ordinal);
            if (!allowed.Contains(labelField))
                return new DatasetOptionsResult(DatasetOptionsOutcome.InvalidField,
                    ErrorDetail: $"Unknown label field '{labelField}'.");
            if (!allowed.Contains(valueField))
                return new DatasetOptionsResult(DatasetOptionsOutcome.InvalidField,
                    ErrorDetail: $"Unknown value field '{valueField}'.");

            // Both identifiers are exact members of the discovered column set, so
            // quoting them (with doubled-quote escaping) is injection-safe. The relation is
            // either the backing VIEW or the "_ds" CTE (with src.WithClause prepended).
            var valueId = QuoteIdentifier(valueField);
            var labelId = QuoteIdentifier(labelField);
            var viewRef = src.Relation;

            var parameters = new DynamicParameters();
            var predicates = new List<string>();
            if (!string.IsNullOrWhiteSpace(search))
            {
                parameters.Add("p_search", "%" + EscapeLikeWildcards(search.Trim()) + "%");
                predicates.Add($"{labelId}::text ILIKE @p_search");
            }
            if (valueEquals is not null)
            {
                parameters.Add("p_value", valueEquals);
                predicates.Add($"{valueId}::text = @p_value");
            }

            // Cascading "Depends on" filters: each target column must equal the value
            // resolved from the form's local field. Every key is validated against the
            // discovered column set before its quoted identifier is interpolated, so
            // the only injection surface stays parameter-bound (the compared values).
            if (filters is not null)
            {
                var filterIndex = 0;
                foreach (var (column, value) in filters)
                {
                    if (!allowed.Contains(column))
                        return new DatasetOptionsResult(DatasetOptionsOutcome.InvalidField,
                            ErrorDetail: $"Unknown filter field '{column}'.");
                    var pname = $"p_filter_{filterIndex}";
                    parameters.Add(pname, value);
                    predicates.Add($"{QuoteIdentifier(column)}::text = @{pname}");
                    filterIndex++;
                }
            }

            // All interpolated parts are SQL identifiers (strings) — no culture
            // sensitivity, so plain interpolation is correct and the only injection
            // surface (the identifiers) is already validated + quote-escaped above.
            var where = predicates.Count > 0 ? " WHERE " + string.Join(" AND ", predicates) : string.Empty;

            var countSql = src.WithClause +
                $"SELECT COUNT(*) FROM (SELECT DISTINCT {valueId}, {labelId} FROM {viewRef}{where}) AS sub";
            var total = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                countSql, parameters, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))
                .ConfigureAwait(false);

            parameters.Add("p_limit", pageSize);
            parameters.Add("p_offset", (long)(page - 1) * pageSize);
            var selectSql = src.WithClause +
                $"SELECT DISTINCT {valueId} AS value, {labelId} AS label FROM {viewRef}{where} " +
                "ORDER BY label, value LIMIT @p_limit OFFSET @p_offset";

            var rawRows = await conn.QueryAsync(new CommandDefinition(
                selectSql, parameters, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))
                .ConfigureAwait(false);

            var options = rawRows
                .Cast<IDictionary<string, object>>()
                .Select(row => new DropdownOption(
                    row.TryGetValue("value", out var v) && v is not null
                        ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty
                        : string.Empty,
                    row.TryGetValue("label", out var l) && l is not null
                        ? Convert.ToString(l, CultureInfo.InvariantCulture)
                        : null))
                .Where(o => o.Value.Length > 0)
                .ToList();

            return new DatasetOptionsResult(DatasetOptionsOutcome.Success,
                new PagedResult<DropdownOption>(options, total, page, pageSize));
        }
    }

    // Double-quote a SQL identifier, escaping any embedded double-quote per the SQL
    // standard. Safe because every identifier passed here is either a DatasetName
    // (validated) or an exact match against a discovered column name.
    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    // % _ \ are LIKE wildcards / the escape char in PostgreSQL; escape them so
    // user search text is matched literally (PG's default backslash escape).
    private static string EscapeLikeWildcards(string input) => input
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
