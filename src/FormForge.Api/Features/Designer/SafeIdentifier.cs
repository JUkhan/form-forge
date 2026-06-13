using System.Text.RegularExpressions;

namespace FormForge.Api.Features.Designer;

// Distinguishes the two failure classes so DesignerService can return separate
// 422 envelopes per AC-2 (IDENTIFIER_INVALID vs IDENTIFIER_RESERVED_KEYWORD).
// FieldKeyValidator still uses the 3-param overload and collapses both classes
// under FIELD_KEY_INVALID — see Dev Notes for why that asymmetry is intentional.
internal enum SafeIdentifierError
{
    InvalidPattern,
    ReservedKeyword,
}

// Validated PostgreSQL-safe identifier (designerId, fieldKey). Re-validates on
// construction so raw strings are never interpolated into SQL by Stories 5.x.
// Sealed + private ctor means the only way to obtain a SafeIdentifier is via
// TryCreate; downstream DDL/CRUD code MUST take SafeIdentifier (not string)
// to make string interpolation of raw user input into SQL physically impossible.
internal sealed partial class SafeIdentifier
{
    [GeneratedRegex(@"^[a-z_][a-z0-9_]{0,62}$", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex ValidPattern();

    public string Value { get; }

    private SafeIdentifier(string value) => Value = value;

    public static bool TryCreate(string? raw, out SafeIdentifier? result, out string? error)
        => TryCreate(raw, out result, out _, out error);

    public static bool TryCreate(
        string? raw,
        out SafeIdentifier? result,
        out SafeIdentifierError? errorCode,
        out string? error)
    {
        result = null;
        errorCode = null;
        error = null;

        if (string.IsNullOrEmpty(raw))
        {
            errorCode = SafeIdentifierError.InvalidPattern;
            error = "Identifier must not be empty.";
            return false;
        }

        if (!ValidPattern().IsMatch(raw))
        {
            errorCode = SafeIdentifierError.InvalidPattern;
            error = $"'{raw}' is not a valid identifier. Use lowercase letters (a-z), digits (0-9), and underscores (_). Must start with a letter or underscore. Maximum 63 characters.";
            return false;
        }

        if (PgReservedKeywords.IsReserved(raw))
        {
            errorCode = SafeIdentifierError.ReservedKeyword;
            error = $"'{raw}' is a reserved PostgreSQL keyword and cannot be used as an identifier.";
            return false;
        }

        result = new SafeIdentifier(raw);
        return true;
    }

    public override string ToString() => Value;
}
