using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FormForge.Api.Features.Datasets;

// Parameterized-query feature — resolves {_placeholder} tokens in a stored "query"-type
// dataset's SQL into bound positional parameters ($1, $2, …) so values are never string-
// interpolated into the query text. Two placeholder forms are recognized:
//   {_age}            — bare; substituted by the bound parameter directly  (… > $1)
//   '{_student_id}'   — quoted; the WHOLE quoted literal (quotes included) is replaced by
//                       the bound parameter (… = $2), so the value is bound, not injected.
// A placeholder repeated in the SQL reuses the same $N (one parameter, multiple references).
internal static partial class DatasetParameterResolver
{
    // Placeholder name: a leading underscore then word chars (matches the spec's {_param_name}).
    // The quoted alternative is tried first so '{_x}' consumes its surrounding quotes.
    [GeneratedRegex(@"'\{(?<name>_\w+)\}'|\{(?<name>_\w+)\}",
        RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    // Distinct placeholder names in first-seen order. Used to gate preview (prompt the user)
    // and to validate the designer has mapped every placeholder to a filter.
    internal static IReadOnlyList<string> ExtractPlaceholders(string? sql)
    {
        if (string.IsNullOrEmpty(sql))
            return [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (Match m in PlaceholderRegex().Matches(sql))
        {
            var name = m.Groups["name"].Value;
            if (seen.Add(name))
                names.Add(name);
        }
        return names;
    }

    internal static bool HasPlaceholders(string? sql) =>
        !string.IsNullOrEmpty(sql) && PlaceholderRegex().IsMatch(sql);

    // Substitute every placeholder with a positional $N parameter, binding each name's value
    // from `values`. Distinct names get distinct, 1-based parameter slots (reused on repeat).
    // Placeholders with no entry in `values` are listed in MissingParameters — the caller must
    // reject the request before executing rather than binding a silent NULL.
    internal static ParameterResolution Resolve(
        string? sql, IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (string.IsNullOrEmpty(sql))
            return new ParameterResolution(sql ?? string.Empty, [], [], []);

        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var parameters = new List<object?>();
        var placeholders = new List<string>();
        var missing = new List<string>();

        var rewritten = PlaceholderRegex().Replace(sql, match =>
        {
            var name = match.Groups["name"].Value;
            if (!indexByName.TryGetValue(name, out var index))
            {
                index = parameters.Count + 1; // $N is 1-based
                indexByName[name] = index;
                placeholders.Add(name);
                if (values.TryGetValue(name, out var value))
                {
                    parameters.Add(value);
                }
                else
                {
                    parameters.Add(null); // never executed — caller aborts on MissingParameters
                    missing.Add(name);
                }
            }
            return "$" + index.ToString(CultureInfo.InvariantCulture);
        });

        return new ParameterResolution(rewritten, parameters, placeholders, missing);
    }

    // Runtime variant of Resolve: substitutes each placeholder with a NAMED parameter
    // (@qp_<name>) instead of a positional $N. Named binding is required at runtime because
    // the placeholder params must coexist in one command with DatasetFilterWhereBuilder's
    // @p0/@p1 filter params — PostgreSQL/Npgsql forbid mixing positional and named in a
    // single statement. The parameter name drops the placeholder's leading underscore-prefix
    // duplication (e.g. "_age" → "qp_age"). Values are bound as `unknown`-typed at the call
    // site so they coerce like literals (see ParameterResolver callers).
    internal static NamedParameterResolution ResolveNamed(
        string? sql, IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (string.IsNullOrEmpty(sql))
            return new NamedParameterResolution(sql ?? string.Empty, [], [], []);

        var paramByName = new Dictionary<string, string>(StringComparer.Ordinal);
        var parameters = new List<(string Name, object? Value)>();
        var placeholders = new List<string>();
        var missing = new List<string>();

        var rewritten = PlaceholderRegex().Replace(sql, match =>
        {
            var name = match.Groups["name"].Value;
            if (!paramByName.TryGetValue(name, out var paramName))
            {
                // "_age" → "qp_age" (the name already starts with '_').
                paramName = "qp" + name;
                paramByName[name] = paramName;
                placeholders.Add(name);
                if (values.TryGetValue(name, out var value))
                    parameters.Add((paramName, value));
                else
                {
                    parameters.Add((paramName, null));
                    missing.Add(name);
                }
            }
            return "@" + paramName;
        });

        return new NamedParameterResolution(rewritten, parameters, placeholders, missing);
    }

    // Substitute every placeholder with a literal NULL so the SQL parses and plans for a
    // schema-only probe (column discovery) regardless of the (unknown) parameter values. A
    // bare NULL coerces to the comparison column's type, whereas a typed param might not.
    internal static string BuildSchemaProbe(string? sql) =>
        string.IsNullOrEmpty(sql) ? sql ?? string.Empty : PlaceholderRegex().Replace(sql, "NULL");

    // Parse the queryParameters JSON object ({"_age":23,"_student_id":"uuid"}) into a CLR
    // map Npgsql can bind. Numbers map to long/decimal/double, strings/bools pass through,
    // null → null. Nested objects/arrays are rejected (no sensible scalar binding). Returns
    // false with an error message on malformed JSON or an unsupported value type.
    internal static bool TryParseParameters(
        string? json,
        out Dictionary<string, object?> values,
        out string? error)
    {
        values = new Dictionary<string, object?>(StringComparer.Ordinal);
        error = null;

        if (string.IsNullOrWhiteSpace(json))
            return true; // no parameters supplied — valid (caller checks MissingParameters)

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            error = $"queryParameters is not valid JSON: {ex.Message}";
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "queryParameters must be a JSON object mapping placeholder names to values.";
            return false;
        }

        foreach (var prop in root.EnumerateObject())
        {
            object? value;
            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.String:
                    value = prop.Value.GetString();
                    break;
                case JsonValueKind.Number:
                    value = prop.Value.TryGetInt64(out var l) ? l
                        : prop.Value.TryGetDecimal(out var dec) ? dec
                        : prop.Value.GetDouble();
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    value = prop.Value.GetBoolean();
                    break;
                case JsonValueKind.Null:
                    value = null;
                    break;
                default:
                    error = $"Parameter '{prop.Name}' has an unsupported value type "
                            + $"({prop.Value.ValueKind}); only strings, numbers, booleans and null are allowed.";
                    return false;
            }
            values[prop.Name] = value;
        }

        return true;
    }
}

// Result of substituting placeholders: the rewritten SQL (with $N), the ordered parameter
// values (index 0 → $1), the distinct placeholder names, and any names that had no value.
internal sealed record ParameterResolution(
    string Sql,
    IReadOnlyList<object?> Parameters,
    IReadOnlyList<string> Placeholders,
    IReadOnlyList<string> MissingParameters);

// Named-parameter variant of ParameterResolution (see ResolveNamed). Parameters carries the
// (parameterName, value) pairs — the name is without the leading '@'.
internal sealed record NamedParameterResolution(
    string Sql,
    IReadOnlyList<(string Name, object? Value)> Parameters,
    IReadOnlyList<string> Placeholders,
    IReadOnlyList<string> MissingParameters);
