using System.Collections;
using System.Text.RegularExpressions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using PgSqlParser;

namespace FormForge.Api.Features.Datasets;

// Story 11.1 (FR-70 / AR-64) — per-expression security gate for user-authored CASE
// operands/outputs and calculated-column expressions. The whole expression text is
// ultimately concatenated verbatim into the generated SQL, so this gate is the only thing
// standing between a hostile client and arbitrary SQL in the SELECT list.
//
// Hardened 2026-06-04 (code review P5): the original keyword/semicolon scan + wrap-parse
// did NOT stop sub-SELECT exfiltration (`(SELECT secret FROM users)`) or dangerous
// functions (`pg_read_file`, `current_setting`, `lo_export`, …) — neither is a leading
// keyword, and the step-10 SqlSelectEnforcer permits scalar subqueries / any table / any
// function. The layers are now:
//
//   Layer 1 — token scan: reject a statement terminator (semicolon) or any DML/DDL/
//             set-operation/SELECT keyword appearing ANYWHERE (word-boundary).
//   Layer 2 — wrap-parse: embed in `SELECT (<expr>) AS _x` (no FROM, so the only function
//             calls in the tree are the user's) and confirm it parses (pgsqlparser).
//   Layer 3 — parse-tree allowlist: walk the parsed tree and reject any SubLink (subquery)
//             or any FuncCall whose name is not in a curated set of safe scalar functions.
//
// pgsqlparser (namespace PgSqlParser) returns a Result<ParseResult?> from Parser.Parse; it
// does NOT throw on a parse failure. The parse tree is libpg_query protobuf, walked here via
// Google.Protobuf reflection (IMessage / MessageDescriptor).
internal sealed record ExpressionValidationResult(bool IsValid, string? ErrorMessage);

internal static partial class ExpressionSecurityValidator
{
    // Layer 1 — any of these as a whole word anywhere in the expression is rejected. SELECT/
    // UNION/INTERSECT/EXCEPT close the sub-SELECT vector early (and the `"x" || (SELECT …)`
    // CASE-passthrough payload); the DML/DDL tokens are belt-and-suspenders (they also fail
    // wrap-parse in an expression position).
    [GeneratedRegex(
        @"\b(DROP|INSERT|UPDATE|DELETE|CREATE|ALTER|TRUNCATE|MERGE|CALL|GRANT|REVOKE|SELECT|UNION|INTERSECT|EXCEPT)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex DangerousTokenPattern();

    // Layer 3 — curated allowlist of safe, side-effect-free scalar functions. Anything else
    // (pg_read_file, pg_ls_dir, lo_import/lo_export, current_setting, set_config, pg_sleep,
    // dblink, query_to_xml, aggregates, window funcs, …) is rejected. Built-in constructs
    // that parse to dedicated nodes rather than FuncCall (COALESCE→CoalesceExpr, NULLIF,
    // GREATEST/LEAST→MinMaxExpr, CAST→TypeCast, CASE→CaseExpr) are inherently safe and need
    // no entry here.
    private static readonly HashSet<string> AllowedFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        // math
        "abs", "ceil", "ceiling", "floor", "round", "trunc", "mod", "power", "pow", "sqrt",
        "cbrt", "sign", "exp", "ln", "log", "log10", "div", "gcd", "lcm", "width_bucket",
        // conditional / null
        "coalesce", "nullif", "greatest", "least", "num_nulls", "num_nonnulls",
        // string
        "length", "char_length", "character_length", "bit_length", "octet_length",
        "upper", "lower", "initcap", "trim", "ltrim", "rtrim", "btrim", "lpad", "rpad",
        "substr", "substring", "left", "right", "replace", "overlay", "concat", "concat_ws",
        "reverse", "repeat", "split_part", "strpos", "position", "translate", "starts_with",
        "to_char", "to_number", "to_hex", "md5", "ascii", "chr", "format",
        // date / time
        "date_part", "date_trunc", "extract", "age", "make_date", "make_time",
        "make_timestamp", "to_date", "to_timestamp",
    };

    // Parse-tree message types that are never allowed inside a calculated/CASE expression.
    private static readonly HashSet<string> ForbiddenNodeTypes =
        new(StringComparer.Ordinal) { "SubLink" };

    internal static ExpressionValidationResult Validate(string expression, string alias)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return new(false, $"Expression for alias '{alias}' cannot be empty.");

        var trimmed = expression.Trim();

        // Layer 1: statement terminator + dangerous-token scan.
        if (trimmed.Contains(';', StringComparison.Ordinal))
            return new(false, $"Expression for alias '{alias}' contains a semicolon.");

        if (DangerousTokenPattern().IsMatch(trimmed))
            return new(false, $"Expression for alias '{alias}' contains a disallowed keyword.");

        // Layer 2: wrap-parse. No FROM clause — the only function calls in the resulting
        // tree are the user's own, so the Layer-3 allowlist does not have to special-case
        // a synthetic table-valued function.
        var wrapped = $"SELECT ({expression}) AS _x";
        var parseResult = Parser.Parse(wrapped);
        if (!parseResult.IsSuccess || parseResult.Value is null)
            return new(false, $"Expression for alias '{alias}' could not be parsed.");

        // Layer 3: parse-tree allowlist (fail-closed — any traversal error rejects).
        try
        {
            if (!IsTreeAllowed(parseResult.Value, out var reason))
                return new(false, $"Expression for alias '{alias}' is not permitted: {reason}.");
        }
#pragma warning disable CA1031 // fail-closed: any reflection/traversal error → reject, never a 500
        catch (Exception)
#pragma warning restore CA1031
        {
            return new(false, $"Expression for alias '{alias}' could not be validated.");
        }

        return new(true, null);
    }

    // Recursively walk every protobuf message in the parsed tree. Reject a forbidden node
    // type (subquery) or a function call outside the allowlist.
    private static bool IsTreeAllowed(IMessage message, out string? reason)
    {
        reason = null;

        var typeName = message.Descriptor.Name;
        if (ForbiddenNodeTypes.Contains(typeName))
        {
            reason = "subqueries are not allowed";
            return false;
        }

        if (typeName == "FuncCall")
        {
            var fn = ExtractFunctionName(message);
            if (fn is null || !AllowedFunctions.Contains(fn))
            {
                reason = $"function '{fn ?? "unknown"}' is not allowed";
                return false;
            }
        }

        foreach (var field in message.Descriptor.Fields.InFieldNumberOrder())
        {
            var value = field.Accessor.GetValue(message);
            switch (value)
            {
                case IMessage child:
                    if (!IsTreeAllowed(child, out reason))
                        return false;
                    break;
                case IEnumerable items when value is not string:
                    foreach (var item in items)
                    {
                        if (item is IMessage childItem && !IsTreeAllowed(childItem, out reason))
                            return false;
                    }
                    break;
            }
        }

        return true;
    }

    // FuncCall.funcname is a list of String nodes; the unqualified function name is the last
    // element. Read via proto field names ("funcname" → Node "string" → String "sval") to
    // avoid C#-keyword property-name mangling.
    private static string? ExtractFunctionName(IMessage funcCall)
    {
        if (funcCall.Descriptor.FindFieldByName("funcname")?.Accessor.GetValue(funcCall)
            is not IEnumerable parts)
        {
            return null;
        }

        string? last = null;
        foreach (var part in parts)
        {
            if (part is not IMessage node)
                continue;
            if (node.Descriptor.FindFieldByName("string")?.Accessor.GetValue(node) is IMessage strNode
                && strNode.Descriptor.FindFieldByName("sval")?.Accessor.GetValue(strNode) is string sval
                && !string.IsNullOrEmpty(sval))
            {
                last = sval;
            }
        }

        return last;
    }
}
