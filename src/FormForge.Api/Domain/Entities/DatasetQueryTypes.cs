namespace FormForge.Api.Domain.Entities;

// Dataset Manager parameterized-query feature — the allowed values for
// custom_dataset.query_type (see CustomDataset.QueryType). Kept as string constants
// (not an enum) so the value round-trips through Dapper / JSON without a converter and
// matches the lowercase tokens persisted in the column.
internal static class DatasetQueryTypes
{
    // The generated SQL is materialized as a VIEW in the `datasets` schema (legacy default).
    internal const string View = "view";

    // The SQL is stored as a record only — no backing VIEW. May contain {_placeholder}
    // tokens (a parameterized query), which restricts it to the Dataset palette element.
    internal const string Query = "query";

    internal static bool IsValid(string? value) =>
        value is View or Query;

    // Normalizes free-form client input ("View", "QUERY", " query ") to a canonical token,
    // or null when it is not a recognized value. Case-insensitive, whitespace-tolerant.
    internal static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        if (string.Equals(trimmed, View, StringComparison.OrdinalIgnoreCase))
            return View;
        if (string.Equals(trimmed, Query, StringComparison.OrdinalIgnoreCase))
            return Query;
        return null;
    }
}
