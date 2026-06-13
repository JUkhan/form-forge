using System.Text.RegularExpressions;

namespace FormForge.Api.Common.Logging;

// Produces a redacted "fingerprint" of a parameterized SQL string for safe logging at Information level.
//
// Architecture contract (FR-46 AC-3, AR-50): DDL/CRUD log entries must carry a sqlFingerprint field with
// placeholders only -- never parameter values. Call sites in Epic 5 (Provisioning) and Epic 6 (Dynamic CRUD)
// already parameterize via Dapper/EF Core, so the input is expected to contain only @name / $n placeholders.
// This helper is defense-in-depth: if a future caller accidentally inlines a literal, the regex pass strips it.
internal static partial class SqlFingerprint
{
    [GeneratedRegex(@"'(?:[^']|'')*'", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SingleQuotedLiteral();

    // Matches hexadecimal literals (0x… / 0X…) before the plain numeric pass so the leading 0
    // is not consumed in isolation.
    [GeneratedRegex(@"(?<![A-Za-z0-9_])0[xX][0-9A-Fa-f]+(?![A-Za-z0-9_])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex HexLiteral();

    // Matches integer/decimal/scientific-notation literals with an optional leading sign. Examples
    // stripped: 18, 3.14, -100, +0.5, 1e10, 1.5E-3. Identifiers containing digits (col_1, tbl_3)
    // are protected by the (?<![A-Za-z0-9_]) lookbehind. The leading sign is consumed only when not
    // adjacent to a letter/digit/underscore so subtraction operators between identifiers (a - b) are
    // not misread as a signed literal.
    [GeneratedRegex(@"(?<![A-Za-z0-9_])[+-]?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?(?![A-Za-z0-9_])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex NumericLiteral();

    public static string Fingerprint(string parameterizedSql)
    {
        ArgumentNullException.ThrowIfNull(parameterizedSql);

        var stripped = SingleQuotedLiteral().Replace(parameterizedSql, "?");
        stripped = HexLiteral().Replace(stripped, "?");
        stripped = NumericLiteral().Replace(stripped, "?");
        return stripped;
    }

    public static IDisposable? BeginDdlScope(ILogger logger, string designerId, string operation, string sqlFingerprint)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(designerId);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(sqlFingerprint);

        return logger.BeginScope(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["operationKind"] = "ddl",
            ["designerId"] = designerId,
            ["operation"] = operation,
            ["sqlFingerprint"] = sqlFingerprint,
        });
    }

    public static IDisposable? BeginCrudScope(ILogger logger, string designerId, string operation, string sqlFingerprint)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(designerId);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(sqlFingerprint);

        return logger.BeginScope(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["operationKind"] = "crud",
            ["designerId"] = designerId,
            ["operation"] = operation,
            ["sqlFingerprint"] = sqlFingerprint,
        });
    }
}
