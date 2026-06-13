using System.Text.Json;
using FormForge.Api.Features.Datasets;
using FormForge.Api.Features.Datasets.Dtos;

namespace FormForge.Api.Tests.Features.Datasets;

// Story 11.1 (FR-70 / AR-66) — pure unit coverage for DatasetSqlGenerator. No DB, no
// WebApplicationFactory, no [Collection]: the generator is a static pure function over a
// BuilderStateDto + an IDatasetAllowlist stub. Mirrors the SqlSelectEnforcerTests pattern.
public sealed class DatasetSqlGeneratorTests
{
    // ── allowlist stubs (no Moq in the test project) ──────────────────────────────────

    private sealed class AllowAllStub : IDatasetAllowlist
    {
        public Task<CatalogDto> GetCatalogAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<TableColumnsDto?> GetTableColumnsAsync(string tableName, CancellationToken ct) => throw new NotImplementedException();
        public bool IsAllowed(string tableName) => true;
    }

    private sealed class DenyAllStub : IDatasetAllowlist
    {
        public Task<CatalogDto> GetCatalogAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<TableColumnsDto?> GetTableColumnsAsync(string tableName, CancellationToken ct) => throw new NotImplementedException();
        public bool IsAllowed(string tableName) => false;
    }

    // ── builder-state construction helpers ────────────────────────────────────────────

    private static ColumnSelectionDto Col(
        string name, string pgType = "integer", bool isChecked = true,
        string aggregate = "none", string alias = "")
        => new(name, pgType, isChecked, aggregate, alias);

    private static TableNodeDto Node(string id, string table, string side, params ColumnSelectionDto[] cols)
        => new(id, new TableNodeDataDto(table, side, [.. cols]), new NodePositionDto(0, 0));

    private static FilterGroupDto Group(string combinator, params FilterItemDto[] items)
        => new("root", "group", combinator, [.. items]);

    private static FilterConditionDto Cond(string table, string column, string op, JsonElement value)
        => new(Ulid.NewUlid().ToString(), "condition", table, column, op, value);

    private static BuilderStateDto State(
        List<TableNodeDto>? nodes = null,
        List<JoinEdgeDto>? edges = null,
        FilterGroupDto? filters = null,
        List<OrderByClauseDto>? orderBy = null,
        List<CaseColumnDto>? caseColumns = null,
        List<CalculatedColumnDto>? calculatedColumns = null)
        => new(
            nodes ?? [],
            edges ?? [],
            filters ?? new FilterGroupDto("root", "group", "AND", []),
            orderBy ?? [],
            caseColumns ?? [],
            calculatedColumns ?? []);

    // Detached (document-independent) JsonElement from a JSON snippet.
    private static JsonElement JE(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static SqlGenerationResult Gen(BuilderStateDto state) =>
        DatasetSqlGenerator.Generate(state, new AllowAllStub());

    // ── Step 1: pre-flight validation ────────────────────────────────────────────────

    [Fact]
    public void Generate_NoLeftNode_ReturnsError()
    {
        var state = State(nodes:
        [
            Node("n1", "orders", "right", Col("id")),
            Node("n2", "items", "right", Col("id")),
        ]);

        var result = Gen(state);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, e => e.Contains("left", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_NoColumnsChecked_ReturnsError()
    {
        var state = State(nodes: [Node("n1", "orders", "left", Col("id", isChecked: false))]);

        var result = Gen(state);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, e => e.Contains("column", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_CaseColumnEmptyAlias_ReturnsError()
    {
        var state = State(
            nodes: [Node("n1", "orders", "left", Col("id"))],
            caseColumns:
            [
                new CaseColumnDto("c1", "n1", "  ",
                    [new CaseWhenDto("n1", "id", "=", "1", "x")], ""),
            ]);

        var result = Gen(state);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, e => e.Contains("CASE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_CalculatedColumnEmptyAlias_ReturnsError()
    {
        var state = State(
            nodes: [Node("n1", "orders", "left", Col("id"))],
            calculatedColumns: [new CalculatedColumnDto("calc1", "n1", "", "1 + 1")]);

        var result = Gen(state);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, e => e.Contains("calculated", StringComparison.OrdinalIgnoreCase));
    }

    // ── Step 2: allowlist validation ─────────────────────────────────────────────────

    [Fact]
    public void Generate_TableNotAllowlisted_ReturnsError()
    {
        var state = State(nodes: [Node("n1", "secret_table", "left", Col("id"))]);

        var result = DatasetSqlGenerator.Generate(state, new DenyAllStub());

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, e => e.Contains("allowlist", StringComparison.OrdinalIgnoreCase));
    }

    // ── Step 3: identifier safety on user aliases ────────────────────────────────────

    [Fact]
    public void Generate_InvalidAlias_ReturnsError()
    {
        // Aliases with uppercase / spaces fail SafeIdentifier's lowercase pattern.
        var state = State(nodes: [Node("n1", "orders", "left", Col("id", alias: "Bad Alias"))]);

        var result = Gen(state);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, e => e.Contains("alias", StringComparison.OrdinalIgnoreCase));
    }

    // ── Step 4/6: single-table SELECT ────────────────────────────────────────────────

    [Fact]
    public void Generate_SingleTable_OneColumn_ProducesCorrectSql()
    {
        var state = State(nodes: [Node("n1", "orders", "left", Col("id"))]);

        var result = Gen(state);

        Assert.False(result.HasErrors);
        Assert.Contains("SELECT \"orders\".\"id\" AS \"orders_id\"", result.ViewSql, StringComparison.Ordinal);
        Assert.Contains("FROM \"public\".\"orders\"", result.ViewSql, StringComparison.Ordinal);
        Assert.DoesNotContain("WHERE", result.ViewSql!, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDER BY", result.ViewSql!, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_SingleTable_WithAlias_UsesAlias()
    {
        var state = State(nodes: [Node("n1", "orders", "left", Col("id", alias: "order_id"))]);

        var result = Gen(state);

        Assert.False(result.HasErrors);
        Assert.Contains("AS \"order_id\"", result.ViewSql, StringComparison.Ordinal);
    }

    // ── Step 6/7: aggregates + auto GROUP BY ─────────────────────────────────────────

    [Fact]
    public void Generate_Aggregate_SumWithAlias_ProducesAggSql()
    {
        var state = State(nodes:
        [
            Node("n1", "orders", "left",
                Col("id"),
                Col("amount", pgType: "numeric", aggregate: "SUM", alias: "total_amount")),
        ]);

        var result = Gen(state);

        Assert.False(result.HasErrors);
        Assert.Contains("SUM(\"orders\".\"amount\") AS \"total_amount\"", result.ViewSql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY", result.ViewSql, StringComparison.Ordinal);
        Assert.Contains("\"orders\".\"id\"", result.ViewSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_Aggregate_AutoGroupBy_ExcludesAggregatedCol()
    {
        var state = State(nodes:
        [
            Node("n1", "orders", "left",
                Col("region", pgType: "text"),
                Col("amount", pgType: "numeric", aggregate: "SUM", alias: "total_amount")),
        ]);

        var result = Gen(state);

        Assert.False(result.HasErrors);
        var groupByLine = result.ViewSql!.Split('\n').Single(l => l.StartsWith("GROUP BY", StringComparison.Ordinal));
        Assert.Contains("\"orders\".\"region\"", groupByLine, StringComparison.Ordinal);
        Assert.DoesNotContain("amount", groupByLine, StringComparison.Ordinal);
    }

    // ── Step 5: joins ────────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_InnerJoin_ProducesJoinClause()
    {
        var state = State(
            nodes:
            [
                Node("n1", "orders", "left", Col("id")),
                Node("n2", "items", "right", Col("order_id")),
            ],
            edges:
            [
                new JoinEdgeDto("e1", "n1", "n2", "id", "order_id", new JoinEdgeDataDto("INNER")),
            ]);

        var result = Gen(state);

        Assert.False(result.HasErrors);
        Assert.Contains(
            "INNER JOIN \"public\".\"items\" ON \"orders\".\"id\" = \"items\".\"order_id\"",
            result.ViewSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_FullOuterJoin_EmitsFullOuterKeyword()
    {
        var state = State(
            nodes:
            [
                Node("n1", "orders", "left", Col("id")),
                Node("n2", "items", "right", Col("order_id")),
            ],
            edges:
            [
                new JoinEdgeDto("e1", "n1", "n2", "id", "order_id", new JoinEdgeDataDto("FULL OUTER")),
            ]);

        var result = Gen(state);

        Assert.False(result.HasErrors);
        Assert.Contains("FULL OUTER JOIN", result.ViewSql, StringComparison.Ordinal);
    }

    // ── Step 9: ORDER BY ─────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_OrderBy_SingleClauseAsc_ProducesOrderBy()
    {
        var state = State(
            nodes: [Node("n1", "orders", "left", Col("id"))],
            orderBy: [new OrderByClauseDto("orders", "id", "ASC")]);

        var result = Gen(state);

        Assert.False(result.HasErrors);
        Assert.Contains("ORDER BY \"orders\".\"id\" ASC", result.ViewSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_OrderBy_MultipleClausesPreserveOrder()
    {
        var state = State(
            nodes: [Node("n1", "orders", "left", Col("id"), Col("created_at", pgType: "text"))],
            orderBy:
            [
                new OrderByClauseDto("orders", "created_at", "DESC"),
                new OrderByClauseDto("orders", "id", "ASC"),
            ]);

        var result = Gen(state);

        Assert.False(result.HasErrors);
        Assert.Contains(
            "ORDER BY \"orders\".\"created_at\" DESC, \"orders\".\"id\" ASC",
            result.ViewSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_OrderBy_EmptyList_NoOrderByClause()
    {
        var state = State(nodes: [Node("n1", "orders", "left", Col("id"))]);

        var result = Gen(state);

        Assert.DoesNotContain("ORDER BY", result.ViewSql!, StringComparison.Ordinal);
    }

    // ── Step 8: WHERE (ViewSql inlined literals) ─────────────────────────────────────

    [Fact]
    public void Generate_WhereEquals_StringColumn_ViewSqlInlinesQuotedLiteral()
    {
        var state = State(
            nodes: [Node("n1", "orders", "left", Col("status", pgType: "text"))],
            filters: Group("AND", Cond("orders", "status", "=", JE("\"active\""))));

        var result = Gen(state);

        Assert.False(result.HasErrors);
        Assert.Contains("\"orders\".\"status\" = 'active'", result.ViewSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_WhereEquals_IntColumn_ViewSqlInlinesUnquotedNumber()
    {
        var state = State(
            nodes: [Node("n1", "orders", "left", Col("id", pgType: "integer"))],
            filters: Group("AND", Cond("orders", "id", "=", JE("\"42\""))));

        var result = Gen(state);

        Assert.False(result.HasErrors);
        Assert.Contains("\"orders\".\"id\" = 42", result.ViewSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_WhereStringWithApostrophe_IsEscaped()
    {
        var state = State(
            nodes: [Node("n1", "orders", "left", Col("name", pgType: "text"))],
            filters: Group("AND", Cond("orders", "name", "=", JE("\"O'Brien\""))));

        var result = Gen(state);

        Assert.False(result.HasErrors);
        Assert.Contains("= 'O''Brien'", result.ViewSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_WhereIn_ProducesInClause()
    {
        var state = State(
            nodes: [Node("n1", "orders", "left", Col("category", pgType: "text"))],
            filters: Group("AND", Cond("orders", "category", "IN", JE("[\"a\",\"b\"]"))));

        var result = Gen(state);

        Assert.False(result.HasErrors);
        Assert.Contains("\"orders\".\"category\" IN ('a', 'b')", result.ViewSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_WhereBetween_ProducesBetweenClause()
    {
        var state = State(
            nodes: [Node("n1", "orders", "left", Col("amount", pgType: "numeric"))],
            filters: Group("AND", Cond("orders", "amount", "BETWEEN", JE("[\"10\",\"20\"]"))));

        var result = Gen(state);

        Assert.False(result.HasErrors);
        Assert.Contains("\"orders\".\"amount\" BETWEEN 10 AND 20", result.ViewSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_WhereIsNull_NoLiteral()
    {
        var state = State(
            nodes: [Node("n1", "orders", "left", Col("status", pgType: "text"))],
            filters: Group("AND", Cond("orders", "status", "IS NULL", JE("null"))));

        var result = Gen(state);

        Assert.False(result.HasErrors);
        Assert.Contains("\"orders\".\"status\" IS NULL", result.ViewSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_ParameterizedSql_UsesPlaceholders()
    {
        var state = State(
            nodes: [Node("n1", "orders", "left", Col("status", pgType: "text"))],
            filters: Group("AND", Cond("orders", "status", "=", JE("\"active\""))));

        var result = Gen(state);

        Assert.False(result.HasErrors);
        Assert.Contains("\"orders\".\"status\" = $1", result.ParameterizedSql, StringComparison.Ordinal);
        Assert.Single(result.Parameters);
        Assert.Equal("active", result.Parameters[0]);
    }

    [Fact]
    public void Generate_WhereNestedGroups_CorrectlyParenthesized()
    {
        var state = State(
            nodes: [Node("n1", "orders", "left", Col("n", pgType: "integer"))],
            filters: Group("OR",
                Group("AND",
                    Cond("orders", "n", "=", JE("\"1\"")),
                    Cond("orders", "n", "=", JE("\"2\""))),
                Cond("orders", "n", "=", JE("\"3\""))));

        var result = Gen(state);

        Assert.False(result.HasErrors);
        Assert.Contains(
            "((\"orders\".\"n\" = 1 AND \"orders\".\"n\" = 2) OR \"orders\".\"n\" = 3)",
            result.ViewSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_WhereEmptyFilters_NoWhereClause()
    {
        var state = State(nodes: [Node("n1", "orders", "left", Col("id"))]);

        var result = Gen(state);

        Assert.False(result.HasErrors);
        Assert.DoesNotContain("WHERE", result.ViewSql!, StringComparison.Ordinal);
    }

    // ── Story 11.2 (AC-4): blank filter-condition pre-flight gate ─────────────────────

    [Fact]
    public void Generate_FilterCondition_BlankTableName_ReturnsError()
    {
        var state = State(
            nodes: [Node("n1", "orders", "left", Col("status", pgType: "text"))],
            filters: Group("AND", Cond("", "status", "=", JE("\"active\""))));

        var result = Gen(state);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, e => e.Contains("table and column", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_FilterCondition_BlankColumnName_ReturnsError()
    {
        var state = State(
            nodes: [Node("n1", "orders", "left", Col("status", pgType: "text"))],
            filters: Group("AND", Cond("orders", "", "=", JE("\"active\""))));

        var result = Gen(state);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, e => e.Contains("table and column", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_FilterCondition_WhitespaceName_ReturnsError()
    {
        var state = State(
            nodes: [Node("n1", "orders", "left", Col("status", pgType: "text"))],
            filters: Group("AND", Cond("   ", "  ", "=", JE("\"active\""))));

        var result = Gen(state);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, e => e.Contains("table and column", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_FilterCondition_ValidNames_NoError()
    {
        // Regression guard: a fully-specified condition must NOT trip the blank-condition gate.
        var state = State(
            nodes: [Node("n1", "orders", "left", Col("status", pgType: "text"))],
            filters: Group("AND", Cond("orders", "status", "=", JE("\"active\""))));

        var result = Gen(state);

        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Generate_NestedFilterGroup_BlankCondition_ReturnsError()
    {
        // The gate must recurse into sub-groups (HasBlankFilterCondition recursion).
        var state = State(
            nodes: [Node("n1", "orders", "left", Col("status", pgType: "text"))],
            filters: Group("AND",
                Group("OR", Cond("", "status", "=", JE("\"active\"")))));

        var result = Gen(state);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, e => e.Contains("table and column", StringComparison.OrdinalIgnoreCase));
    }

    // ── Step 6: calculated + CASE columns ────────────────────────────────────────────

    [Fact]
    public void Generate_CalculatedColumn_ValidExpression_InSelectList()
    {
        var state = State(
            nodes: [Node("n1", "orders", "left", Col("id"))],
            calculatedColumns: [new CalculatedColumnDto("calc1", "n1", "adjusted_price", "price * 1.1")]);

        var result = Gen(state);

        Assert.False(result.HasErrors);
        Assert.Contains("(price * 1.1) AS \"adjusted_price\"", result.ViewSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_CalculatedColumn_DangerousExpression_ReturnsError()
    {
        var state = State(
            nodes: [Node("n1", "orders", "left", Col("id"))],
            calculatedColumns: [new CalculatedColumnDto("calc1", "n1", "evil", "DROP TABLE users")]);

        var result = Gen(state);

        Assert.True(result.HasErrors);
    }

    [Fact]
    public void Generate_CaseColumn_SingleWhen_ProducesCaseExpression()
    {
        var state = State(
            nodes: [Node("n1", "orders", "left", Col("status", pgType: "text"))],
            caseColumns:
            [
                new CaseColumnDto("c1", "n1", "status_label",
                    [new CaseWhenDto("n1", "status", "=", "active", "Active")],
                    "Inactive"),
            ]);

        var result = Gen(state);

        Assert.False(result.HasErrors);
        Assert.Contains("CASE WHEN \"orders\".\"status\" = 'active' THEN 'Active'", result.ViewSql, StringComparison.Ordinal);
        Assert.Contains("ELSE 'Inactive' END AS \"status_label\"", result.ViewSql, StringComparison.Ordinal);
    }

    // ── Code-review regression: injection + correctness hardening ────────────────────

    [Fact]
    public void Generate_CalculatedColumn_SubqueryInjection_ReturnsError()
    {
        // P5: a calculated column that smuggles a sub-SELECT must be rejected — it would
        // otherwise exfiltrate any table the VIEW owner can read, bypassing the allowlist.
        var state = State(
            nodes: [Node("n1", "orders", "left", Col("id"))],
            calculatedColumns:
            [
                new CalculatedColumnDto("calc1", "n1", "leak", "(SELECT password FROM users LIMIT 1)"),
            ]);

        var result = Gen(state);

        Assert.True(result.HasErrors);
    }

    [Fact]
    public void Generate_CalculatedColumn_DangerousFunction_ReturnsError()
    {
        var state = State(
            nodes: [Node("n1", "orders", "left", Col("id"))],
            calculatedColumns:
            [
                new CalculatedColumnDto("calc1", "n1", "leak", "pg_read_file('/etc/passwd')"),
            ]);

        var result = Gen(state);

        Assert.True(result.HasErrors);
    }

    [Fact]
    public void Generate_CaseColumn_SubqueryViaDoubleQuotePassthrough_ReturnsError()
    {
        // P5: the RenderCaseValue "…" passthrough was an injection vector; the expression
        // validator now gates THEN/ELSE/operand values, so a subquery payload is rejected.
        var state = State(
            nodes: [Node("n1", "orders", "left", Col("status", pgType: "text"))],
            caseColumns:
            [
                new CaseColumnDto("c1", "n1", "leak",
                    [new CaseWhenDto("n1", "status", "=", "active",
                        "\"x\" || (SELECT secret FROM users) || \"y\"")],
                    ""),
            ]);

        var result = Gen(state);

        Assert.True(result.HasErrors);
    }

    [Fact]
    public void Generate_NumericThousandsSeparator_DoesNotProduceMultiValueList()
    {
        // P3: "1,000" must NOT be emitted unquoted (it parsed as `IN (1, 0)` — wrong rows).
        var state = State(
            nodes: [Node("n1", "orders", "left", Col("amount", pgType: "integer"))],
            filters: Group("AND", Cond("orders", "amount", "IN", JE("[\"1,000\"]"))));

        var result = Gen(state);

        Assert.False(result.HasErrors);
        Assert.DoesNotContain("IN (1,000)", result.ViewSql!, StringComparison.Ordinal);
        Assert.Contains("'1,000'", result.ViewSql!, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_ColumnNameWithEmbeddedQuote_IsEscapedNotInjected()
    {
        // P1: a client-supplied column name containing a double-quote must be doubled ("")
        // so it cannot break out of the quoted identifier.
        var state = State(nodes: [Node("n1", "orders", "left", Col("ev\"il", pgType: "text"))]);

        var result = Gen(state);

        Assert.False(result.HasErrors);
        Assert.Contains("\"ev\"\"il\"", result.ViewSql!, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_NodeMissingData_ReturnsErrorNotException()
    {
        // P2: a hand-crafted blob omitting `data` must surface as a clean validation error,
        // not a NullReferenceException (HTTP 500).
        var state = State(nodes: [new TableNodeDto("n1", null!, new NodePositionDto(0, 0))]);

        var result = Gen(state);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, e => e.Contains("data", StringComparison.OrdinalIgnoreCase));
    }
}
