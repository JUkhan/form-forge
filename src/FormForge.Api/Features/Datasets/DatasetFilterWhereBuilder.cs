using System.Globalization;
using System.Text.Json;
using Dapper;
using FormForge.Api.Features.Datasets.Dtos;

namespace FormForge.Api.Features.Datasets;

// Builds a parameterized WHERE clause for a DatasetComponent runtime filter applied to a
// single dataset VIEW. The single-table analogue of DatasetSqlGenerator's parameterized
// rendering: same operator/value vocabulary, but unqualified `"col" op @p` (no joins).
//
// Safety: every referenced column must be a member of the dataset VIEW's discovered
// columns (rejected → caller returns 422); operators are checked against an allowlist;
// every value is a Dapper parameter (never interpolated). Values are coerced to a CLR
// type from the column's information_schema data_type so typed comparisons (numeric/date)
// work; LIKE/ILIKE cast the column to text.
internal static class DatasetFilterWhereBuilder
{
    private static readonly HashSet<string> AllowedOperators = new(StringComparer.Ordinal)
    {
        "=", "!=", "<", "<=", ">", ">=",
        "IS NULL", "IS NOT NULL", "LIKE", "ILIKE", "IN", "NOT IN", "BETWEEN",
    };

    internal sealed record Result(string Where, bool IsValid, string? Error)
    {
        internal static Result Ok(string where) => new(where, true, null);
        internal static Result Invalid(string error) => new(string.Empty, false, error);
    }

    // `columnTypes`: columnName → information_schema data_type. Appends @p0, @p1, … to
    // `parameters`. Returns Where == "" when the tree yields no predicates.
    //
    // `authFilter` (optional) is a server-resolved scoping predicate: when set, an
    // equality `"<Column>" = <Value>` is AND-ed onto the rest of the WHERE clause. The
    // column is validated against `columnTypes` (unknown ⇒ Invalid); the value (the
    // requesting user's id) is bound as a typed parameter — Guid for a uuid column, the
    // raw string otherwise. The caller MUST supply the value from the authenticated
    // identity, never from the request body.
    public static Result Build(
        FilterGroupDto? group,
        IReadOnlyDictionary<string, string> columnTypes,
        DynamicParameters parameters,
        (string Column, Guid Value)? authFilter = null)
    {
        ArgumentNullException.ThrowIfNull(columnTypes);
        ArgumentNullException.ThrowIfNull(parameters);

        var counter = new ParamCounter();

        string? groupSql = null;
        if (group is not null)
        {
            var (sql, error) = RenderGroup(group, columnTypes, parameters, counter);
            if (error is not null)
                return Result.Invalid(error);
            groupSql = sql;
        }

        var clauses = new List<string>(2);
        if (!string.IsNullOrEmpty(groupSql))
            clauses.Add(groupSql!);

        if (authFilter is { } af)
        {
            if (!columnTypes.TryGetValue(af.Column, out var authType))
                return Result.Invalid($"Unknown auth filter column '{af.Column}'.");
            // uuid columns bind a Guid; everything else binds the canonical string form
            // (PostgreSQL casts a text parameter against a text/varchar column directly).
            object authValue = authType == "uuid"
                ? af.Value
                : af.Value.ToString();
            var name = $"p{counter.Next++}";
            parameters.Add(name, authValue);
            clauses.Add($"{QuoteIdentifier(af.Column)} = @{name}");
        }

        if (clauses.Count == 0)
            return Result.Ok(string.Empty);
        return Result.Ok(" WHERE " + string.Join(" AND ", clauses));
    }

    private sealed class ParamCounter { public int Next; }

    private static (string? Sql, string? Error) RenderGroup(
        FilterGroupDto group,
        IReadOnlyDictionary<string, string> columnTypes,
        DynamicParameters parameters,
        ParamCounter counter)
    {
        if (group.Items is null || group.Items.Count == 0)
            return (null, null);

        var parts = new List<string>();
        foreach (var item in group.Items)
        {
            string? rendered;
            string? error;
            switch (item)
            {
                case FilterGroupDto sub:
                    (rendered, error) = RenderGroup(sub, columnTypes, parameters, counter);
                    break;
                case FilterConditionDto cond:
                    (rendered, error) = RenderCondition(cond, columnTypes, parameters, counter);
                    break;
                default:
                    rendered = null;
                    error = null;
                    break;
            }
            if (error is not null)
                return (null, error);
            if (!string.IsNullOrEmpty(rendered))
                parts.Add(rendered);
        }

        if (parts.Count == 0)
            return (null, null);

        var combinator = group.Combinator == "OR" ? " OR " : " AND ";
        var joined = string.Join(combinator, parts);
        return (parts.Count > 1 ? $"({joined})" : joined, null);
    }

    private static (string? Sql, string? Error) RenderCondition(
        FilterConditionDto cond,
        IReadOnlyDictionary<string, string> columnTypes,
        DynamicParameters parameters,
        ParamCounter counter)
    {
        if (string.IsNullOrWhiteSpace(cond.ColumnName))
            return (null, null); // empty/unbound condition — skip silently
        if (!columnTypes.TryGetValue(cond.ColumnName, out var dataType))
            return (null, $"Unknown filter column '{cond.ColumnName}'.");
        if (!AllowedOperators.Contains(cond.Operator))
            return (null, $"Unsupported filter operator '{cond.Operator}'.");

        var col = QuoteIdentifier(cond.ColumnName);

        switch (cond.Operator)
        {
            case "IS NULL":
                return ($"{col} IS NULL", null);
            case "IS NOT NULL":
                return ($"{col} IS NOT NULL", null);
            case "IN":
            case "NOT IN":
            {
                if (cond.Value.ValueKind != JsonValueKind.Array)
                    return (null, null); // no values yet — skip
                var placeholders = new List<string>();
                foreach (var e in cond.Value.EnumerateArray())
                    placeholders.Add(AddParam(parameters, counter, Coerce(e, dataType)));
                if (placeholders.Count == 0)
                    return (null, null);
                return ($"{col} {cond.Operator} ({string.Join(", ", placeholders)})", null);
            }
            case "BETWEEN":
            {
                if (cond.Value.ValueKind != JsonValueKind.Array)
                    return (null, null);
                var arr = cond.Value.EnumerateArray().ToList();
                if (arr.Count < 2)
                    return (null, null);
                var lo = AddParam(parameters, counter, Coerce(arr[0], dataType));
                var hi = AddParam(parameters, counter, Coerce(arr[1], dataType));
                return ($"{col} BETWEEN {lo} AND {hi}", null);
            }
            case "LIKE":
            case "ILIKE":
            {
                if (IsEmptyScalar(cond.Value))
                    return (null, null);
                // A bound filter input carries a plain term, so wrap it as a contains-match
                // (%term%) — the user expects partial matching, not an exact match. LIKE
                // metacharacters in their input are escaped so they're matched literally
                // (mirrors the dropdown-options search). Cast the column to text so the
                // match works for any column type.
                var term = "%" + EscapeLikeWildcards(ScalarString(cond.Value)) + "%";
                var p = AddParam(parameters, counter, term);
                return ($"{col}::text {cond.Operator} {p}", null);
            }
            default:
            {
                if (IsEmptyScalar(cond.Value))
                    return (null, null);
                var p = AddParam(parameters, counter, Coerce(cond.Value, dataType));
                return ($"{col} {cond.Operator} {p}", null);
            }
        }
    }

    private static string AddParam(DynamicParameters parameters, ParamCounter counter, object? value)
    {
        var name = $"p{counter.Next++}";
        parameters.Add(name, value);
        return "@" + name;
    }

    private static bool IsEmptyScalar(JsonElement v) =>
        v.ValueKind == JsonValueKind.Null ||
        (v.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(v.GetString()));

    private static string ScalarString(JsonElement v) =>
        v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : v.GetRawText();

    // Coerce a JSON scalar to a CLR type matching the column's PostgreSQL data_type so
    // Npgsql binds a typed parameter (text comparison against a numeric column otherwise
    // throws). Unparseable values fall back to the raw string (PG will surface a clear error).
    private static object? Coerce(JsonElement v, string dataType)
    {
        if (v.ValueKind == JsonValueKind.Null)
            return null;
        var s = v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : v.GetRawText();

        switch (dataType)
        {
            case "smallint":
            case "integer":
            case "bigint":
                return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
                    ? l : s;
            case "numeric":
            case "real":
            case "double precision":
                return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
                    ? d : s;
            case "boolean":
                return bool.TryParse(s, out var b) ? b : s;
            case "date":
            case "timestamp without time zone":
            case "timestamp with time zone":
                return DateTime.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var dt) ? dt : s;
            case "uuid":
                return Guid.TryParse(s, out var g) ? g : s;
            default:
                return s; // text, character varying, jsonb, etc.
        }
    }

    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    // % _ \ are LIKE wildcards / the escape char in PostgreSQL; escape them so the user's
    // search term is matched literally inside the %…% contains pattern (PG default backslash escape).
    private static string EscapeLikeWildcards(string input) => input
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
