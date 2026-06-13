using System.Globalization;
using System.Text;
using System.Text.Json;
using FormForge.Api.Features.Datasets.Dtos;
using FormForge.Api.Features.Designer;

namespace FormForge.Api.Features.Datasets;

// Story 11.1 (FR-70 / AR-66) — server-authoritative SQL generation. Re-derives the SQL
// SELECT from builder_state on every builder-mode save so the client cannot bypass Query
// Builder security with a hand-crafted query. Produces two SQL variants that share the
// SELECT/FROM/JOIN/GROUP BY/ORDER BY clauses and differ only in the WHERE clause:
//   ViewSql          — filter values inlined as safe PostgreSQL literals; for CREATE VIEW
//                       DDL and custom_dataset.query (VIEWs cannot use $1 placeholders).
//   ParameterizedSql — filter values replaced with $1,$2,…; Parameters holds the values
//                       in order. Consumed by Story 11.3's preview endpoint.
//
// All table/column identifiers are double-quoted (Q) — the primary SQL-injection defense
// for identifiers. Only user-provided aliases are additionally run through SafeIdentifier
// (catalog-sourced table/column names are already DB-controlled — see Dev Notes §5).
internal sealed record SqlGenerationResult
{
    public string? ViewSql { get; init; }          // for VIEW DDL + custom_dataset.query
    public string? ParameterizedSql { get; init; } // for Story 11.3 preview ($1, $2, …)
    public IReadOnlyList<object?> Parameters { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
    public bool HasErrors => Errors.Count > 0;
}

internal static class DatasetSqlGenerator
{
    // Aggregate keywords accepted from ColumnSelectionDto.Aggregate ("none"/"" → not aggregated).
    private static readonly HashSet<string> Aggregates =
        new(StringComparer.Ordinal) { "COUNT", "SUM", "AVG", "MIN", "MAX" };

    // Operator vocabulary mirrored from builderState.ts (FilterOperator / CaseOperator).
    // Anything outside this set is skipped defensively rather than emitted raw.
    private static readonly HashSet<string> AllowedOperators = new(StringComparer.Ordinal)
    {
        "=", "!=", "<", "<=", ">", ">=",
        "IS NULL", "IS NOT NULL", "LIKE", "ILIKE", "IN", "NOT IN", "BETWEEN",
    };

    // PostgreSQL numeric type names — values of these types are emitted unquoted when they
    // parse as a number (otherwise they fall through to the safe single-quoted path).
    private static readonly HashSet<string> NumericTypes = new(StringComparer.Ordinal)
    {
        "integer", "bigint", "smallint", "numeric", "decimal",
        "real", "double precision", "int4", "int8", "float4", "float8",
    };

    // Numeric literal parsing (code-review P3): sign, decimal point, and exponent only — NO
    // thousands separators, parentheses, currency, or surrounding whitespace. NumberStyles.Any
    // previously accepted "1,000" / "(5)" and the raw string was emitted unquoted, so
    // `IN (1,000)` parsed as `IN (1, 0)` — valid SQL, wrong results.
    private const NumberStyles NumericLiteralStyle =
        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

    internal static SqlGenerationResult Generate(BuilderStateDto state, IDatasetAllowlist allowlist)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(allowlist);

        // Defensive null-coalescing: a partial blob may omit a list (full normalization is
        // deferred to Story 11.2). Treat missing collections as empty so a malformed-but-
        // parseable state surfaces as a validation error, never a NullReferenceException/500.
        var nodes = state.Nodes ?? [];
        var edges = state.Edges ?? [];
        var caseColumns = state.CaseColumns ?? [];
        var calculatedColumns = state.CalculatedColumns ?? [];
        var orderBy = state.OrderBy ?? [];

        // ── Step 0 — structural validation (code-review P2) ─────────────────────────
        // System.Text.Json leaves a non-nullable reference property null when the JSON
        // omits it, so a hand-crafted blob missing `data`/`columns` (or an edge missing
        // `data`) would dereference-null in the steps below → HTTP 500. Catch it here and
        // surface as a clean BUILDER_STATE_INVALID (422) instead.
        var structural = new List<string>();
        foreach (var node in nodes)
        {
            if (node is null) { structural.Add("A table node is missing."); continue; }
            if (node.Data is null)
                structural.Add("Each table node must include a data object.");
            else if (node.Data.Columns is null)
                structural.Add($"Table node '{node.Data.TableName}' is missing its columns list.");
        }
        foreach (var edge in edges)
        {
            if (edge is null)
                structural.Add("A join edge is missing.");
            else if (edge.Data is null)
                structural.Add("Each join edge must include a data object.");
        }
        if (structural.Count > 0)
            return new SqlGenerationResult { Errors = structural };

        // ── Step 1 — pre-flight validation ──────────────────────────────────────────
        var errors = new List<string>();

        if (nodes.Count(n => n.Data.Side == "left") != 1)
            errors.Add("Exactly one table must be designated as the left (FROM) table.");

        if (nodes.Count == 0 || !nodes.Any(n => n.Data.Columns.Any(c => c.Checked)))
            errors.Add("At least one column must be selected.");

        if (caseColumns.Any(c => string.IsNullOrWhiteSpace(c.Alias)))
            errors.Add("All CASE columns must have a non-empty alias.");

        if (calculatedColumns.Any(c => string.IsNullOrWhiteSpace(c.Alias)))
            errors.Add("All calculated columns must have a non-empty alias.");

        // Story 11.2 (AC-4): every filter condition must reference a table and column.
        // A blank tableName/columnName is silently skipped during WHERE rendering
        // (RenderConditionView returns null), so without this gate a half-authored
        // condition would vanish from the generated SQL instead of failing the save.
        if (HasBlankFilterCondition(state.Filters))
            errors.Add("All filter conditions must reference a table and column.");

        if (errors.Count > 0)
            return new SqlGenerationResult { Errors = errors };

        // ── Step 2 — allowlist validation ───────────────────────────────────────────
        foreach (var node in nodes)
        {
            if (!allowlist.IsAllowed(node.Data.TableName))
                errors.Add($"Table '{node.Data.TableName}' is not in the allowlist.");
        }

        if (errors.Count > 0)
            return new SqlGenerationResult { Errors = errors };

        // ── Step 3 — identifier safety (user-provided aliases only) ─────────────────
        foreach (var alias in CollectUserAliases(nodes, caseColumns, calculatedColumns))
        {
            if (!SafeIdentifier.TryCreate(alias, out _, out var err))
                errors.Add($"Alias '{alias}' is invalid: {err}");
        }

        if (errors.Count > 0)
            return new SqlGenerationResult { Errors = errors };

        // ── Step 4 — FROM clause (left-designated table) ────────────────────────────
        var leftNode = nodes.First(n => n.Data.Side == "left");
        var from = $"FROM \"public\".{Q(leftNode.Data.TableName)}";

        // ── Step 5 — JOIN clauses ───────────────────────────────────────────────────
        // Build defensively (code-review P2): duplicate or null node ids in a hand-crafted
        // blob would throw from ToDictionary (→500). Last-wins on duplicates; null ids skipped.
        var nodesById = new Dictionary<string, TableNodeDto>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node.Id is not null)
                nodesById[node.Id] = node;
        }

        var joins = new List<string>();
        foreach (var edge in edges)
        {
            if (edge.Source is null || edge.Target is null
                || !nodesById.TryGetValue(edge.Source, out var sourceNode)
                || !nodesById.TryGetValue(edge.Target, out var targetNode))
            {
                continue; // edge references a missing/blank node — skip defensively
            }

            var sourceTable = sourceNode.Data.TableName;
            var targetTable = targetNode.Data.TableName;
            joins.Add(
                $"{JoinKeyword(edge.Data.JoinType)} JOIN \"public\".{Q(targetTable)} " +
                $"ON {Q(sourceTable)}.{Q(edge.SourceHandle)} = {Q(targetTable)}.{Q(edge.TargetHandle)}");
        }

        // ── Step 6 — SELECT list ────────────────────────────────────────────────────
        var selectItems = new List<string>();

        foreach (var node in nodes)
        {
            var table = node.Data.TableName;
            foreach (var col in node.Data.Columns.Where(c => c.Checked))
            {
                var qualified = $"{Q(table)}.{Q(col.ColumnName)}";
                if (IsAggregate(col.Aggregate))
                {
                    var alias = string.IsNullOrWhiteSpace(col.Alias)
                        ? $"{col.Aggregate.ToLowerInvariant()}_{table}_{col.ColumnName}"
                        : col.Alias;
                    selectItems.Add($"{col.Aggregate}({qualified}) AS {Q(alias)}");
                }
                else
                {
                    var alias = string.IsNullOrWhiteSpace(col.Alias)
                        ? $"{table}_{col.ColumnName}"
                        : col.Alias;
                    selectItems.Add($"{qualified} AS {Q(alias)}");
                }
            }
        }

        foreach (var cc in caseColumns)
        {
            var (caseSql, caseErrors) = RenderCaseColumn(cc, nodesById);
            if (caseErrors.Count > 0)
            {
                errors.AddRange(caseErrors);
                continue;
            }
            selectItems.Add($"{caseSql} AS {Q(cc.Alias)}");
        }

        foreach (var calc in calculatedColumns)
        {
            var validation = ExpressionSecurityValidator.Validate(calc.Expression, calc.Alias);
            if (!validation.IsValid)
            {
                errors.Add(validation.ErrorMessage!);
                continue;
            }
            selectItems.Add($"({calc.Expression}) AS {Q(calc.Alias)}");
        }

        if (errors.Count > 0)
            return new SqlGenerationResult { Errors = errors };

        // ── Step 7 — GROUP BY (auto-derived when any aggregate is present) ──────────
        string? groupBy = null;
        var hasAggregate = nodes.Any(n => n.Data.Columns.Any(c => c.Checked && IsAggregate(c.Aggregate)));
        if (hasAggregate)
        {
            var groupItems = new List<string>();
            foreach (var node in nodes)
            {
                foreach (var col in node.Data.Columns.Where(c => c.Checked && !IsAggregate(c.Aggregate)))
                    groupItems.Add($"{Q(node.Data.TableName)}.{Q(col.ColumnName)}");
            }
            if (groupItems.Count > 0)
                groupBy = "GROUP BY " + string.Join(", ", groupItems);
        }

        // ── Step 8 — WHERE clause (inlined for ViewSql, parameterized for preview) ──
        var whereView = state.Filters is null ? null : BuildWhereView(state.Filters, nodes);
        var parameters = new List<object?>();
        var whereParam = state.Filters is null ? null : BuildWhereParameterized(state.Filters, nodes, parameters);

        // ── Step 9 — ORDER BY (declared clause order) ───────────────────────────────
        string? orderByClause = null;
        var orderItems = new List<string>();
        foreach (var ob in orderBy)
        {
            if (string.IsNullOrWhiteSpace(ob.TableName) || string.IsNullOrWhiteSpace(ob.ColumnName))
                continue; // degenerate clause — skip defensively
            var dir = ob.Direction == "DESC" ? "DESC" : "ASC";
            orderItems.Add($"{Q(ob.TableName)}.{Q(ob.ColumnName)} {dir}");
        }
        if (orderItems.Count > 0)
            orderByClause = "ORDER BY " + string.Join(", ", orderItems);

        // ── Assemble both SQL variants ──────────────────────────────────────────────
        var viewSql = Assemble(selectItems, from, joins, whereView, groupBy, orderByClause);
        var parameterizedSql = Assemble(selectItems, from, joins, whereParam, groupBy, orderByClause);

        // ── Step 10 — SELECT-only validation on the assembled ViewSql ───────────────
        var enforcement = SqlSelectEnforcer.Validate(viewSql);
        if (!enforcement.IsValid)
            return new SqlGenerationResult { Errors = [enforcement.ErrorMessage ?? "Generated SQL is not a valid SELECT."] };

        return new SqlGenerationResult
        {
            ViewSql = viewSql,
            ParameterizedSql = parameterizedSql,
            Parameters = parameters,
        };
    }

    // ── Assembly + helpers ──────────────────────────────────────────────────────────

    private static string Assemble(
        List<string> selectItems, string from, List<string> joins,
        string? where, string? groupBy, string? orderBy)
    {
        var sb = new StringBuilder();
        sb.Append("SELECT ").Append(string.Join(", ", selectItems));
        sb.Append('\n').Append(from);
        foreach (var join in joins)
            sb.Append('\n').Append(join);
        if (!string.IsNullOrEmpty(where))
            sb.Append('\n').Append("WHERE ").Append(where);
        if (!string.IsNullOrEmpty(groupBy))
            sb.Append('\n').Append(groupBy);
        if (!string.IsNullOrEmpty(orderBy))
            sb.Append('\n').Append(orderBy);
        return sb.ToString();
    }

    private static IEnumerable<string> CollectUserAliases(
        List<TableNodeDto> nodes,
        List<CaseColumnDto> caseColumns,
        List<CalculatedColumnDto> calculatedColumns)
    {
        foreach (var node in nodes)
        {
            foreach (var col in node.Data.Columns.Where(c => c.Checked && !string.IsNullOrWhiteSpace(c.Alias)))
                yield return col.Alias;
        }
        foreach (var cc in caseColumns.Where(c => !string.IsNullOrWhiteSpace(c.Alias)))
            yield return cc.Alias;
        foreach (var calc in calculatedColumns.Where(c => !string.IsNullOrWhiteSpace(c.Alias)))
            yield return calc.Alias;
    }

    private static bool IsAggregate(string? aggregate) =>
        aggregate is not null && Aggregates.Contains(aggregate);

    private static string JoinKeyword(string joinType) => joinType switch
    {
        "LEFT" => "LEFT OUTER",
        "RIGHT" => "RIGHT OUTER",
        "FULL OUTER" => "FULL OUTER",
        _ => "INNER",
    };

    // Double-quote an identifier (reserved-word safe). Internal quotes are doubled ("")
    // so a client-supplied column name / join handle / ORDER BY ref that is NOT routed
    // through SafeIdentifier (only user aliases are) cannot break out of the quoted
    // identifier (code-review P1). A null identifier collapses to an empty quoted token,
    // which fails the step-10 SELECT-only validation rather than throwing.
    private static string Q(string identifier) =>
        $"\"{(identifier ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    // Story 11.2 (AC-4): recurse the filter tree and report whether any condition has a
    // blank table or column name. A null group (deserialization left Filters unset) is
    // treated as "no blank condition" — there is simply no WHERE clause to validate.
    private static bool HasBlankFilterCondition(FilterGroupDto? group)
    {
        if (group is null) return false;
        foreach (var item in group.Items ?? [])
        {
            switch (item)
            {
                case FilterConditionDto cond when
                    string.IsNullOrWhiteSpace(cond.TableName) ||
                    string.IsNullOrWhiteSpace(cond.ColumnName):
                    return true;
                case FilterGroupDto subGroup when HasBlankFilterCondition(subGroup):
                    return true;
            }
        }
        return false;
    }

    // ── WHERE: ViewSql variant (inlined literals) ────────────────────────────────────

    private static string? BuildWhereView(FilterGroupDto group, List<TableNodeDto> nodes)
    {
        if (group.Items is null || group.Items.Count == 0)
            return null;

        var parts = new List<string>();
        foreach (var item in group.Items)
        {
            string? rendered = item switch
            {
                FilterGroupDto sub => BuildWhereView(sub, nodes),
                FilterConditionDto cond => RenderConditionView(cond, nodes),
                _ => null,
            };
            if (!string.IsNullOrEmpty(rendered))
                parts.Add(rendered);
        }

        if (parts.Count == 0)
            return null;

        var combinator = group.Combinator == "OR" ? " OR " : " AND ";
        var joined = string.Join(combinator, parts);
        return parts.Count > 1 ? $"({joined})" : joined;
    }

    private static string? RenderConditionView(FilterConditionDto cond, List<TableNodeDto> nodes)
    {
        if (string.IsNullOrWhiteSpace(cond.TableName) || string.IsNullOrWhiteSpace(cond.ColumnName))
            return null;
        if (!AllowedOperators.Contains(cond.Operator))
            return null;

        var qualified = $"{Q(cond.TableName)}.{Q(cond.ColumnName)}";
        var pgType = LookupPgType(nodes, cond.TableName, cond.ColumnName);

        switch (cond.Operator)
        {
            case "IS NULL":
                return $"{qualified} IS NULL";
            case "IS NOT NULL":
                return $"{qualified} IS NOT NULL";
            case "IN":
            case "NOT IN":
                if (cond.Value.ValueKind != JsonValueKind.Array)
                    return null;
                var inLits = cond.Value.EnumerateArray().Select(e => RenderLiteral(e, pgType));
                return $"{qualified} {cond.Operator} ({string.Join(", ", inLits)})";
            case "BETWEEN":
                if (cond.Value.ValueKind != JsonValueKind.Array)
                    return null;
                var betweenArr = cond.Value.EnumerateArray().ToList();
                if (betweenArr.Count < 2)
                    return null;
                return $"{qualified} BETWEEN {RenderLiteral(betweenArr[0], pgType)} AND {RenderLiteral(betweenArr[1], pgType)}";
            default:
                return $"{qualified} {cond.Operator} {RenderLiteral(cond.Value, pgType)}";
        }
    }

    // ── WHERE: ParameterizedSql variant ($1, $2, …) ──────────────────────────────────

    private static string? BuildWhereParameterized(
        FilterGroupDto group, List<TableNodeDto> nodes, List<object?> parameters)
    {
        if (group.Items is null || group.Items.Count == 0)
            return null;

        var parts = new List<string>();
        foreach (var item in group.Items)
        {
            string? rendered = item switch
            {
                FilterGroupDto sub => BuildWhereParameterized(sub, nodes, parameters),
                FilterConditionDto cond => RenderConditionParameterized(cond, nodes, parameters),
                _ => null,
            };
            if (!string.IsNullOrEmpty(rendered))
                parts.Add(rendered);
        }

        if (parts.Count == 0)
            return null;

        var combinator = group.Combinator == "OR" ? " OR " : " AND ";
        var joined = string.Join(combinator, parts);
        return parts.Count > 1 ? $"({joined})" : joined;
    }

    private static string? RenderConditionParameterized(
        FilterConditionDto cond, List<TableNodeDto> nodes, List<object?> parameters)
    {
        if (string.IsNullOrWhiteSpace(cond.TableName) || string.IsNullOrWhiteSpace(cond.ColumnName))
            return null;
        if (!AllowedOperators.Contains(cond.Operator))
            return null;

        var qualified = $"{Q(cond.TableName)}.{Q(cond.ColumnName)}";
        var pgType = LookupPgType(nodes, cond.TableName, cond.ColumnName);

        switch (cond.Operator)
        {
            case "IS NULL":
                return $"{qualified} IS NULL";
            case "IS NOT NULL":
                return $"{qualified} IS NOT NULL";
            case "IN":
            case "NOT IN":
                if (cond.Value.ValueKind != JsonValueKind.Array)
                    return null;
                var placeholders = new List<string>();
                foreach (var e in cond.Value.EnumerateArray())
                {
                    parameters.Add(JsonElementToClr(e, pgType));
                    placeholders.Add($"${parameters.Count}");
                }
                return $"{qualified} {cond.Operator} ({string.Join(", ", placeholders)})";
            case "BETWEEN":
                if (cond.Value.ValueKind != JsonValueKind.Array)
                    return null;
                var arr = cond.Value.EnumerateArray().ToList();
                if (arr.Count < 2)
                    return null;
                parameters.Add(JsonElementToClr(arr[0], pgType));
                var lo = $"${parameters.Count}";
                parameters.Add(JsonElementToClr(arr[1], pgType));
                var hi = $"${parameters.Count}";
                return $"{qualified} BETWEEN {lo} AND {hi}";
            default:
                parameters.Add(JsonElementToClr(cond.Value, pgType));
                return $"{qualified} {cond.Operator} ${parameters.Count}";
        }
    }

    // ── CASE column rendering ────────────────────────────────────────────────────────

    private static (string Sql, List<string> Errors) RenderCaseColumn(
        CaseColumnDto col, Dictionary<string, TableNodeDto> nodesById)
    {
        var localErrors = new List<string>();
        var sb = new StringBuilder("CASE");

        foreach (var when in col.Whens ?? [])
        {
            if (when.NodeId is null || !nodesById.TryGetValue(when.NodeId, out var node))
            {
                localErrors.Add($"CASE column '{col.Alias}' references an unknown table node.");
                continue;
            }

            var qualified = $"{Q(node.Data.TableName)}.{Q(when.ColumnName)}";

            if (!AllowedOperators.Contains(when.Operator))
            {
                localErrors.Add($"CASE column '{col.Alias}' uses an unsupported operator.");
                continue;
            }

            string condition;
            if (when.Operator is "IS NULL" or "IS NOT NULL")
            {
                condition = $"{qualified} {when.Operator}";
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(when.OperandValue))
                {
                    var opCheck = ExpressionSecurityValidator.Validate(when.OperandValue, col.Alias);
                    if (!opCheck.IsValid)
                    {
                        localErrors.Add(opCheck.ErrorMessage!);
                        continue;
                    }
                }
                condition = $"{qualified} {when.Operator} {RenderCaseValue(when.OperandValue)}";
            }

            if (!string.IsNullOrWhiteSpace(when.ThenValue))
            {
                var thenCheck = ExpressionSecurityValidator.Validate(when.ThenValue, col.Alias);
                if (!thenCheck.IsValid)
                {
                    localErrors.Add(thenCheck.ErrorMessage!);
                    continue;
                }
            }

            sb.Append(" WHEN ").Append(condition).Append(" THEN ").Append(RenderCaseValue(when.ThenValue));
        }

        if (!string.IsNullOrWhiteSpace(col.ElseValue))
        {
            var elseCheck = ExpressionSecurityValidator.Validate(col.ElseValue, col.Alias);
            if (!elseCheck.IsValid)
                localErrors.Add(elseCheck.ErrorMessage!);
            else
                sb.Append(" ELSE ").Append(RenderCaseValue(col.ElseValue));
        }

        sb.Append(" END");
        return (sb.ToString(), localErrors);
    }

    // A double-quoted token is treated as a column/identifier reference and emitted as-is;
    // anything else is a string literal with '' escaping (the safe default).
    private static string RenderCaseValue(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "''";
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            return raw;
        return $"'{raw.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    // ── Literal + type helpers ───────────────────────────────────────────────────────

    private static string LookupPgType(List<TableNodeDto> nodes, string tableName, string columnName) =>
        nodes.FirstOrDefault(n => n.Data.TableName == tableName)?.Data.Columns
            .FirstOrDefault(c => c.ColumnName == columnName)?.PgType ?? "text";

    // The core injection-prevention function for ViewSql (Dev Notes §7): strings are always
    // single-quoted with '' escaping; numerics are emitted unquoted only when they parse as a
    // number; booleans only as the true/false keyword; NULL only for a JSON null. Any
    // unrecognized type falls through to the safe quoted-string branch.
    private static string RenderLiteral(JsonElement value, string pgType)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return "NULL";

        var str = value.ToString();

        if (NumericTypes.Contains(pgType)
            && decimal.TryParse(str, NumericLiteralStyle, CultureInfo.InvariantCulture, out var num))
        {
            // Emit the PARSED value, not the raw string (code-review P3) — see NumericLiteralStyle.
            return num.ToString(CultureInfo.InvariantCulture);
        }

        if (pgType is "boolean" or "bool")
        {
            var lower = str.ToLowerInvariant();
            if (lower is "true" or "t")
                return "true";
            if (lower is "false" or "f")
                return "false";
        }

        return $"'{str.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    // Coerce a JSON value to the CLR type implied by the column's pgType (code-review P4) so
    // the parameterized variant binds the SAME type the inlined ViewSql would emit. The
    // canonical contract sends every value as a JSON string, so a numeric/boolean column must
    // have the string coerced here rather than bound as text.
    private static object? JsonElementToClr(JsonElement e, string pgType)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                return e.TryGetInt64(out var l) ? l : e.GetDecimal();
            default:
                var s = e.ToString();
                if (NumericTypes.Contains(pgType)
                    && decimal.TryParse(s, NumericLiteralStyle, CultureInfo.InvariantCulture, out var num))
                    return num;
                if (pgType is "boolean" or "bool")
                {
                    var lower = s.ToLowerInvariant();
                    if (lower is "true" or "t") return true;
                    if (lower is "false" or "f") return false;
                }
                return s;
        }
    }
}
