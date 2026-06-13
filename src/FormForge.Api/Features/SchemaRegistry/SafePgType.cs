using System.Globalization;
using System.Text.RegularExpressions;

namespace FormForge.Api.Features.SchemaRegistry;

// Story B — behaviour family for a PostgreSQL column type. Collapses the ~16
// authorable types into the handful of value-handling strategies the dynamic
// read/write path actually needs (payload coercion, filter predicate, filter
// value validation). Distinguishing integer vs float vs decimal, or date vs
// timestamptz, still needs the base token — use PgTypeInfo.BaseOf for that.
internal enum PgTypeFamily
{
    Text,
    Numeric,
    Boolean,
    Temporal,
    Uuid,
    Json,
}

// Story B — lenient runtime classifier for whatever string lives in
// ColumnDefinition.PgType. It must accept BOTH the new canonical authored types
// ("text", "varchar(255)", "numeric(10,2)", "integer", …) AND the legacy
// hardcoded constants emitted by ComponentTypeMapper ("TEXT", "NUMERIC",
// "BOOLEAN", "TIMESTAMPTZ", "UUID", "JSONB") so already-provisioned tables keep
// working. Unknown types fall back to the Text family (ILIKE substring match) —
// the same defensive default the query builder used before this change.
//
// This is deliberately MORE permissive than SafePgType.TryCreate: classification
// never rejects, whereas authoring is strictly allowlisted.
internal static partial class PgTypeInfo
{
    // Lowercased base token with any "(...)" arguments and surrounding whitespace
    // stripped, internal whitespace collapsed (so "double  precision" → "double
    // precision"). Returns "" for null/blank.
    public static string BaseOf(string? pgType)
    {
        if (string.IsNullOrWhiteSpace(pgType)) return string.Empty;
        var s = pgType.Trim().ToLowerInvariant();
        var paren = s.IndexOf('(', StringComparison.Ordinal);
        if (paren >= 0) s = s[..paren].Trim();
        // Collapse runs of internal whitespace to a single space.
        return CollapseWhitespace().Replace(s, " ");
    }

    public static PgTypeFamily FamilyOf(string? pgType) => BaseOf(pgType) switch
    {
        "text" or "varchar" or "character varying" or "char" or "character" or "bpchar"
            => PgTypeFamily.Text,
        "numeric" or "decimal"
            or "integer" or "int" or "int4" or "bigint" or "int8" or "smallint" or "int2"
            or "real" or "float4" or "double precision" or "float8" or "money"
            => PgTypeFamily.Numeric,
        "boolean" or "bool"
            => PgTypeFamily.Boolean,
        "date" or "time" or "timestamp" or "timestamptz"
            or "timestamp with time zone" or "timestamp without time zone"
            or "time with time zone" or "time without time zone"
            => PgTypeFamily.Temporal,
        "uuid"
            => PgTypeFamily.Uuid,
        "jsonb" or "json"
            => PgTypeFamily.Json,
        _ => PgTypeFamily.Text,   // forward-compat fallback (substring match)
    };

    // Integer-valued numeric bases — payload/filter values must be whole numbers.
    public static bool IsIntegerBase(string baseType) => baseType is
        "integer" or "int" or "int4" or "bigint" or "int8" or "smallint" or "int2";

    // Floating-point bases — coerced to double (vs decimal for exact numeric).
    public static bool IsFloatBase(string baseType) => baseType is
        "real" or "float4" or "double precision" or "float8";

    [GeneratedRegex(@"\s+", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex CollapseWhitespace();
}

// Story B — validated, DDL-safe PostgreSQL column type authored per-field in the
// Designer ("pgType" property). Modeled on SafeIdentifier: sealed + private ctor
// so the only way to obtain one is TryCreate, and the canonical Value is
// RECONSTRUCTED from parsed primitives (base token from a fixed allowlist, params
// re-emitted from validated integers) — so nothing the user typed is ever
// interpolated verbatim into CREATE TABLE / ALTER TABLE. This is the linchpin
// that lets DdlEmitter keep treating ColumnDefinition.PgType as trusted now that
// it originates from user JSON instead of the hardcoded ComponentTypeMapper.
internal sealed partial class SafePgType
{
    // PostgreSQL maximum length for char/varchar (1 GB / 1 byte chars).
    private const int MaxStringLength = 10_485_760;
    // PostgreSQL numeric precision is 1..1000; scale is 0..precision.
    private const int MaxNumericPrecision = 1000;

    // Types that take no parameters. Canonical form == the token itself.
    private static readonly HashSet<string> NoParamTypes = new(StringComparer.Ordinal)
    {
        "text",
        "integer", "bigint", "smallint",
        "boolean",
        "uuid",
        "date", "time", "timestamp", "timestamptz",
        "double precision", "real",
        "jsonb",
    };

    public string Value { get; }            // canonical, DDL-safe, e.g. "numeric(10,2)"
    public PgTypeFamily Family { get; }

    private SafePgType(string value, PgTypeFamily family)
    {
        Value = value;
        Family = family;
    }

    public static bool TryCreate(string? raw, out SafePgType? result, out string? error)
    {
        result = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "PgType must not be empty.";
            return false;
        }

        // Normalize: lowercase + collapse whitespace so "VARCHAR ( 255 )" and
        // "double  precision" canonicalize consistently before pattern matching.
        var s = CollapseWhitespace().Replace(raw.Trim().ToLowerInvariant(), " ");
        s = s.Replace(" (", "(", StringComparison.Ordinal)
             .Replace("( ", "(", StringComparison.Ordinal)
             .Replace(" )", ")", StringComparison.Ordinal)
             .Replace(", ", ",", StringComparison.Ordinal)
             .Replace(" ,", ",", StringComparison.Ordinal);

        // numeric(p,s) — both precision and scale required by the authoring UI.
        var numericMatch = NumericPattern().Match(s);
        if (numericMatch.Success)
        {
            if (!int.TryParse(numericMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var p)
                || !int.TryParse(numericMatch.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var scale))
            {
                error = $"'{raw}' has an out-of-range numeric precision or scale.";
                return false;
            }
            if (p < 1 || p > MaxNumericPrecision)
            {
                error = $"numeric precision must be between 1 and {MaxNumericPrecision}.";
                return false;
            }
            if (scale < 0 || scale > p)
            {
                error = "numeric scale must be between 0 and the precision.";
                return false;
            }
            result = new SafePgType(
                string.Create(CultureInfo.InvariantCulture, $"numeric({p},{scale})"),
                PgTypeFamily.Numeric);
            return true;
        }

        // varchar(n) / char(n) — length required.
        var lengthMatch = LengthPattern().Match(s);
        if (lengthMatch.Success)
        {
            var baseType = lengthMatch.Groups[1].Value;
            if (!int.TryParse(lengthMatch.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                error = $"'{raw}' has an out-of-range length.";
                return false;
            }
            if (n < 1 || n > MaxStringLength)
            {
                error = $"{baseType} length must be between 1 and {MaxStringLength}.";
                return false;
            }
            result = new SafePgType(
                string.Create(CultureInfo.InvariantCulture, $"{baseType}({n})"),
                PgTypeFamily.Text);
            return true;
        }

        // No-parameter types.
        if (NoParamTypes.Contains(s))
        {
            result = new SafePgType(s, PgTypeInfo.FamilyOf(s));
            return true;
        }

        error = $"'{raw}' is not a supported PostgreSQL type.";
        return false;
    }

    [GeneratedRegex(@"^numeric\((\d{1,5}),(\d{1,5})\)$", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex NumericPattern();

    [GeneratedRegex(@"^(varchar|char)\((\d{1,9})\)$", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex LengthPattern();

    [GeneratedRegex(@"\s+", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex CollapseWhitespace();
}
