using System.Text.RegularExpressions;
using FormForge.Api.Features.Designer;

namespace FormForge.Api.Domain.ValueTypes;

// Distinguishes the three failure classes so callers can branch (e.g. surface a
// more specific message). Mirrors SafeIdentifierError but adds Denylist for the
// permanent internal-table guard that SafeIdentifier doesn't have (AR-57).
internal enum DatasetNameError
{
    InvalidPattern,
    ReservedKeyword,
    Denylist,
}

// Validated PostgreSQL-safe identifier used as a Dataset VIEW name. Mirrors
// SafeIdentifier's sealed + private-ctor pattern: the only way to obtain a
// DatasetName is via TryCreate, so downstream VIEW DDL (Stories 8.4/8.5/8.6)
// MUST accept a DatasetName (not a raw string), making unsafe string
// interpolation of user input into SQL physically impossible (AC-6 / FR-57 AC-6).
//
// Unlike SafeIdentifier (which bounds length in the regex), DatasetName uses an
// unbounded pattern with a SEPARATE length check performed BEFORE the regex —
// this avoids running the regex against adversarially long input.
internal sealed partial class DatasetName
{
    [GeneratedRegex(@"^[a-z_][a-z0-9_]*$", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex ValidPattern();

    // 14 permanent internal table names (AR-57) blocked regardless of regex /
    // reserved-keyword status. Note `roles` is here because PgReservedKeywords
    // only reserves the singular `role`.
    private static readonly HashSet<string> PermanentDenylist = new(StringComparer.Ordinal)
    {
        "users", "roles", "user_roles", "menus", "menu_role_assignments",
        "component_schemas", "refresh_tokens", "password_reset_tokens",
        "mfa_backup_codes", "mfa_sessions", "schema_audit_log",
        "mutation_audit_log", "dataset_audit_log", "custom_dataset",
    };

    public string Value { get; }

    private DatasetName(string value) => Value = value;

    // 2-param overload: caller only needs success/failure + error message.
    public static bool TryCreate(string? raw, out DatasetName? result, out string? error)
        => TryCreate(raw, out result, out _, out error);

    // 4-param overload: exposes the error-class enum for callers needing to branch.
    public static bool TryCreate(
        string? raw,
        out DatasetName? result,
        out DatasetNameError? errorCode,
        out string? error)
    {
        result = null;
        errorCode = null;
        error = null;

        if (string.IsNullOrEmpty(raw))
        {
            errorCode = DatasetNameError.InvalidPattern;
            error = "Dataset name must not be empty.";
            return false;
        }

        // Length check FIRST so the regex never runs against a 10000-char input.
        if (raw.Length > 63 || !ValidPattern().IsMatch(raw))
        {
            errorCode = DatasetNameError.InvalidPattern;
            error = $"'{raw}' is not a valid dataset name. Use lowercase letters (a-z), digits (0-9), " +
                    "and underscores (_). Must start with a letter or underscore. Maximum 63 characters.";
            return false;
        }

        if (PgReservedKeywords.IsReserved(raw))
        {
            errorCode = DatasetNameError.ReservedKeyword;
            error = $"'{raw}' is a reserved PostgreSQL keyword and cannot be used as a dataset name.";
            return false;
        }

        if (PermanentDenylist.Contains(raw))
        {
            errorCode = DatasetNameError.Denylist;
            error = $"'{raw}' is a reserved internal table name and cannot be used as a dataset name.";
            return false;
        }

        result = new DatasetName(raw);
        return true;
    }

    public override string ToString() => Value;
}
