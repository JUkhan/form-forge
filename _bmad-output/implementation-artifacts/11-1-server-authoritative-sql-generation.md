# Story 11.1: Server-Authoritative SQL Generation from builder_state

Status: done

## Story

As the system,
I re-derive the SQL SELECT statement from `builder_state` on the server before any VIEW DDL or preview execution,
So that the client cannot bypass Query Builder security constraints with a hand-crafted SQL string.

## Acceptance Criteria

1. Given PUT /api/datasets/{id} with `is_custom_query = false`, when the server processes the request, then `DatasetSqlGenerator.cs` accepts the `BuilderStateDto` JSON and runs the 10-step algorithm (per AR-66): (1) pre-flight validation — one left node, ≥1 column checked, all CASE/calculated aliases non-empty; (2) allowlist validation for all table names via `IDatasetAllowlist.IsAllowed`; (3) identifier safety via `SafeIdentifier.TryCreate` on all aliases; (4) FROM clause from left-designated table; (5) JOIN clauses per edge; (6) SELECT list (plain/aggregated/CASE/calculated); (7) GROUP BY auto-derived; (8) WHERE clause with safe-quoted literals in `ViewSql` and `$1,$2,...` parameterized form in `ParameterizedSql`; (9) ORDER BY in declared clause order; (10) SELECT-only validation via `SqlSelectEnforcer.Validate` on the assembled `ViewSql`.

2. Given the generator, when it produces SQL, then all table and column identifiers are double-quoted (`"table_name"."column_name"`) to handle reserved words (per FR-70 AC-3 / AR-66).

3. Given the generator processes filter values, when rendering `ViewSql` (for VIEW DDL), then values are rendered as properly-quoted PostgreSQL literals (string → `'value'` with `''` escaping; numeric → unquoted digits; boolean → `true`/`false` keyword) — never raw string-interpolated. When rendering `ParameterizedSql` (for Story 11.3 preview), values are collected into `Parameters[]` as `$1`, `$2`, … (per FR-70 AC-4 / NFR-17).

4. Given `builder_state` is incomplete (no left table, no columns selected, alias empty on a CASE/calculated column), when the generator runs, then it returns a non-empty `Errors` list → `DatasetService` returns `UpdateDatasetOutcome.BuilderStateInvalid` → endpoint emits HTTP 422 `{ code: "BUILDER_STATE_INVALID" }` before any DDL executes (per FR-70 AC-6 / AR-65).

5. Given a successful generator run, when the save transaction commits, then `custom_dataset.query` is set to `SqlGenerationResult.ViewSql` (the VIEW-compatible SQL with inlined literals) within the same `NpgsqlTransaction` as `builder_state` and the VIEW DDL, so both are always in sync (per FR-70 AC-7 / AR-66).

## Tasks / Subtasks

- [x] Task 1: Create `BuilderStateDto.cs` — C# record hierarchy mirroring the TypeScript `BuilderState` contract (AC: 1)
  - [x] Create `src/FormForge.Api/Features/Datasets/Dtos/BuilderStateDto.cs` (NEW file)
  - [x] Define all records in a single file with `using System.Text.Json.Serialization`:
    ```csharp
    internal sealed record BuilderStateDto(
        List<TableNodeDto> Nodes,
        List<JoinEdgeDto> Edges,
        FilterGroupDto Filters,
        List<OrderByClauseDto> OrderBy,
        List<CaseColumnDto> CaseColumns,
        List<CalculatedColumnDto> CalculatedColumns);

    internal sealed record TableNodeDto(
        string Id,
        TableNodeDataDto Data,
        NodePositionDto Position);

    internal sealed record NodePositionDto(double X, double Y);

    internal sealed record TableNodeDataDto(
        string TableName,
        string Side,           // "left" | "right"
        List<ColumnSelectionDto> Columns);

    internal sealed record ColumnSelectionDto(
        string ColumnName,
        string PgType,
        bool Checked,
        string Aggregate,      // "none" | "COUNT" | "SUM" | "AVG" | "MIN" | "MAX"
        string Alias);

    internal sealed record JoinEdgeDto(
        string Id,
        string Source,
        string Target,
        string SourceHandle,
        string TargetHandle,
        JoinEdgeDataDto Data);

    internal sealed record JoinEdgeDataDto(string JoinType); // "INNER" | "LEFT" | "RIGHT" | "FULL OUTER"

    // Discriminated union via kind field; deserialized by custom converter (Task 1 §5)
    internal abstract record FilterItemDto(string Kind);

    internal sealed record FilterGroupDto(
        string Id,
        string Kind,           // "group"
        string Combinator,     // "AND" | "OR"
        List<FilterItemDto> Items) : FilterItemDto(Kind);

    internal sealed record FilterConditionDto(
        string Id,
        string Kind,           // "condition"
        string TableName,
        string ColumnName,
        string Operator,       // "=" | "!=" | "<" | "<=" | ">" | ">=" | "IS NULL" | "IS NOT NULL" | "LIKE" | "ILIKE" | "IN" | "NOT IN" | "BETWEEN"
        JsonElement Value      // string | string[] | null — inspected at generation time
    ) : FilterItemDto(Kind);

    internal sealed record OrderByClauseDto(
        string TableName,
        string ColumnName,
        string Direction);     // "ASC" | "DESC"

    internal sealed record CaseColumnDto(
        string Id,
        string NodeId,
        string Alias,
        List<CaseWhenDto> Whens,
        string ElseValue);

    internal sealed record CaseWhenDto(
        string NodeId,
        string ColumnName,
        string Operator,
        string OperandValue,
        string ThenValue);

    internal sealed record CalculatedColumnDto(
        string Id,
        string NodeId,
        string Alias,
        string Expression);
    ```
  - [x] All records use PascalCase property names; add a static `JsonSerializerOptions BuilderStateJsonOptions` field on a helper class `BuilderStateSerializer` (new file not needed — add a static inner class or a top-level `internal static class BuilderStateSerializer` in the same file):
    ```csharp
    internal static class BuilderStateSerializer
    {
        internal static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        internal static BuilderStateDto? Deserialize(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize<BuilderStateDto>(json, Options); }
            catch { return null; }
        }
    }
    ```
  - [x] `FilterItemDto` discriminated union: the `items` array in `FilterGroupDto` contains a mix of `FilterGroupDto` and `FilterConditionDto`. Register a custom `JsonConverter<FilterItemDto>` that inspects the `kind` field:
    ```csharp
    internal sealed class FilterItemDtoConverter : JsonConverter<FilterItemDto>
    {
        public override FilterItemDto? Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions opts)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var kind = doc.RootElement.GetProperty("kind").GetString();
            var raw = doc.RootElement.GetRawText();
            return kind == "group"
                ? JsonSerializer.Deserialize<FilterGroupDto>(raw, opts)
                : JsonSerializer.Deserialize<FilterConditionDto>(raw, opts);
        }
        public override void Write(Utf8JsonWriter w, FilterItemDto v, JsonSerializerOptions opts)
            => JsonSerializer.Serialize(w, v, v.GetType(), opts);
    }
    ```
  - [x] Register `FilterItemDtoConverter` in `BuilderStateSerializer.Options.Converters`
  - [x] `FilterConditionDto.Value` is typed as `JsonElement` (not `object`) because `System.Text.Json` does not deserialize JSON `null | string | string[]` into a typed union cleanly; the generator reads `Value.ValueKind` to determine the type

- [x] Task 2: Create `ExpressionSecurityValidator.cs` — 3-layer check per AR-64 (AC: 1, 4)
  - [x] Create `src/FormForge.Api/Features/Datasets/ExpressionSecurityValidator.cs` (NEW file)
  - [x] Static class; `Validate(string expression, string alias)` returns `ExpressionValidationResult`:
    ```csharp
    internal sealed record ExpressionValidationResult(bool IsValid, string? ErrorMessage);
    internal static class ExpressionSecurityValidator
    {
        // Layer 1 keywords (trimmed, case-insensitive start OR unquoted semicolon)
        private static readonly string[] DangerousKeywords =
            ["DROP", "INSERT", "UPDATE", "DELETE", "CREATE", "ALTER", "TRUNCATE", "MERGE", "CALL"];

        internal static ExpressionValidationResult Validate(string expression, string alias)
        {
            if (string.IsNullOrWhiteSpace(expression))
                return new(false, $"Expression for alias '{alias}' cannot be empty.");

            // Layer 1: keyword scan
            var trimmed = expression.Trim();
            foreach (var kw in DangerousKeywords)
            {
                if (trimmed.StartsWith(kw, StringComparison.OrdinalIgnoreCase))
                    return new(false, $"Expression for alias '{alias}' contains a disallowed keyword.");
            }
            if (trimmed.Contains(';'))
                return new(false, $"Expression for alias '{alias}' contains a semicolon.");

            // Layer 2: wrap-parse — embed in a dummy SELECT and try to parse
            var wrapped = $"SELECT ({expression}) AS _x FROM generate_series(1,1) _t";
            var parseResult = PgSqlParser.Parser.Parse(wrapped);
            if (!parseResult.IsSuccess || parseResult.Value is null)
                return new(false, $"Expression for alias '{alias}' could not be parsed.");

            return new(true, null);
        }
    }
    ```
  - [x] Note: Layer 3 (final SELECT-only validation) is performed by `SqlSelectEnforcer.Validate` on the fully assembled SQL at step 10 of the generator — `ExpressionSecurityValidator` covers layers 1 and 2 per-expression only
  - [x] Import `using PgSqlParser;` at the top (same package as `SqlSelectEnforcer.cs`)

- [x] Task 3: Create `DatasetSqlGenerator.cs` — 10-step algorithm (AC: 1–5)
  - [x] Create `src/FormForge.Api/Features/Datasets/DatasetSqlGenerator.cs` (NEW file)
  - [x] Static class with the following result type and main entry point:
    ```csharp
    internal sealed record SqlGenerationResult
    {
        public string? ViewSql { get; init; }          // for VIEW DDL + custom_dataset.query
        public string? ParameterizedSql { get; init; } // for Story 11.3 preview ($1, $2, ...)
        public IReadOnlyList<object?> Parameters { get; init; } = [];
        public IReadOnlyList<string> Errors { get; init; } = [];
        public bool HasErrors => Errors.Count > 0;
    }

    internal static class DatasetSqlGenerator
    {
        internal static SqlGenerationResult Generate(
            BuilderStateDto state, IDatasetAllowlist allowlist) { ... }
    }
    ```
  - [x] **Step 1 — Pre-flight validation** (returns errors without touching DB):
    - `state.Nodes.Count(n => n.Data.Side == "left") != 1` → add error "Exactly one table must be designated as the left (FROM) table."
    - `state.Nodes.Count == 0 || !state.Nodes.Any(n => n.Data.Columns.Any(c => c.Checked))` → add error "At least one column must be selected."
    - `state.CaseColumns.Any(c => string.IsNullOrWhiteSpace(c.Alias))` → add error "All CASE columns must have a non-empty alias."
    - `state.CalculatedColumns.Any(c => string.IsNullOrWhiteSpace(c.Alias))` → add error "All calculated columns must have a non-empty alias."
    - Return errors if any
  - [x] **Step 2 — Allowlist validation**:
    - For each node, `if (!allowlist.IsAllowed(node.Data.TableName))` → add error `"Table '{tableName}' is not in the allowlist."`
    - Return with code `TABLE_NOT_ALLOWLISTED` if any errors (see endpoint wiring in Task 5)
    - Note: store separately from step 1 errors since the endpoint maps them to a distinct error code (see Task 5)
  - [x] **Step 3 — Identifier safety** via `SafeIdentifier.TryCreate`:
    - Validate every non-empty column `alias` from checked columns, CASE columns, and calculated columns
    - `SafeIdentifier.TryCreate(alias, out _, out var err)` failure → add error including the alias and `err`
    - Note: table/column names from the allowlist + catalog do NOT need SafeIdentifier validation (they are already DB-controlled identifiers; SafeIdentifier would reject valid PG names like `created_at` with underscores). Only user-provided aliases go through SafeIdentifier.
    - The SafeIdentifier import path: `using FormForge.Api.Domain.ValueTypes;`
  - [x] **Step 4 — Build FROM clause**: find left node (`n.Data.Side == "left"`), emit `FROM "public"."tableName"`. Self-join alias (same table twice): assign each node a numeric alias `t0`, `t1`, etc. and use those in all clauses. For v1, assume no self-joins (defer self-join aliasing; story spec does not require it in this release).
  - [x] **Step 5 — Build JOIN clauses**: for each `JoinEdgeDto` in `state.Edges`, look up source/target node table names:
    ```
    {joinType} JOIN "public"."{targetTable}" ON "{sourceTable}"."{edge.SourceHandle}" = "{targetTable}"."{edge.TargetHandle}"
    ```
    JoinType mapping: `"INNER"` → `INNER`, `"LEFT"` → `LEFT OUTER`, `"RIGHT"` → `RIGHT OUTER`, `"FULL OUTER"` → `FULL OUTER` (emit with `JOIN` keyword).
  - [x] **Step 6 — Build SELECT list**: iterate checked columns in node order:
    - Plain checked (aggregate == "none" or empty, alias empty): `"tableName"."colName" AS "tableName_colName"` (default alias = `{table}_{column}`)
    - Plain checked (alias non-empty): `"tableName"."colName" AS "alias"`
    - Aggregated (aggregate != "none"): `AGG("tableName"."colName") AS "alias_or_default"` (default alias = `{aggFunction}_{table}_{column}`)
    - CASE columns (from `state.CaseColumns`): `CASE WHEN ... THEN ... [ELSE ...] END AS "alias"` — rendered by `RenderCaseColumn(col, state.Nodes)`
    - Calculated columns (from `state.CalculatedColumns`): call `ExpressionSecurityValidator.Validate(col.Expression, col.Alias)` first; failure → add error; success → `({expression}) AS "alias"`
    - Apply `ExpressionSecurityValidator` to CASE column WHEN arm `operandValue` and `thenValue` / `elseValue` if non-empty
  - [x] **Step 7 — Build GROUP BY**: `if (state.Nodes.Any(n => n.Data.Columns.Any(c => c.Checked && c.Aggregate != "none" && c.Aggregate != "")))` → collect all checked non-aggregated columns → `GROUP BY "t"."col", ...`
  - [x] **Step 8 — Build WHERE clause** (two variants):
    - `BuildWhereView(state.Filters)` → returns SQL fragment with inlined safe literals
    - `BuildWhereParameterized(state.Filters, ref paramList)` → returns SQL fragment with `$1, $2, ...`; appends values to `paramList`
    - Skip WHERE if `state.Filters.Items.Count == 0`
    - `RenderFilterGroup` / `RenderFilterCondition` helper methods (recursive)
    - `RenderLiteral(JsonElement value, string pgType)` for safe inlining:
      ```csharp
      private static string RenderLiteral(JsonElement value, string pgType)
      {
          if (value.ValueKind == JsonValueKind.Null) return "NULL";
          var str = value.ToString();
          // Numeric types: emit as-is if it parses as a number (prevents quoting injection)
          if (pgType is "integer" or "bigint" or "smallint" or "numeric" or "decimal"
              or "real" or "double precision" or "int4" or "int8" or "float4" or "float8")
          {
              if (decimal.TryParse(str, out _)) return str;
          }
          if (pgType is "boolean" or "bool")
          {
              var lower = str.ToLowerInvariant();
              if (lower is "true" or "t") return "true";
              if (lower is "false" or "f") return "false";
          }
          // Default: single-quoted string with '' escaping
          return $"'{str.Replace("'", "''")}'";
      }
      ```
    - For `IN`/`NOT IN` with `value.ValueKind == JsonValueKind.Array`: iterate array elements; for ViewSql emit `IN (lit1, lit2, ...)`, for parameterized emit `IN ($n, $n+1, ...)`
    - For `BETWEEN`: two-element array; ViewSql `BETWEEN lit1 AND lit2`; parameterized `BETWEEN $n AND $n+1`
    - For `IS NULL`/`IS NOT NULL`: emit operator with no value (no literal, no parameter)
    - Skipped/empty condition (tableName or columnName blank): skip silently (defensive; generator AC-4 should have caught it in pre-flight only if it's a degenerate state not already caught; in practice, the pre-flight only catches missing aliases; the generator should skip blank-tableName conditions in WHERE to avoid SQL errors)
    - Find pgType for a condition: `state.Nodes.FirstOrDefault(n => n.Data.TableName == cond.TableName)?.Data.Columns.FirstOrDefault(c => c.ColumnName == cond.ColumnName)?.PgType ?? "text"`
  - [x] **Step 9 — Build ORDER BY**: if `state.OrderBy.Count > 0`:
    - Emit `ORDER BY "tableName"."colName" ASC|DESC, ...` in declared index order
    - Skip any `OrderByClauseDto` with blank `TableName` or `ColumnName` (degenerate state; generator is defensive)
  - [x] **Step 10 — SELECT-only validation**: assemble the full `ViewSql` string; call `SqlSelectEnforcer.Validate(viewSql)` — failure → return `INVALID_QUERY` error
  - [x] **Return `SqlGenerationResult`** with:
    - `ViewSql` = fully assembled SQL with inlined literals
    - `ParameterizedSql` = fully assembled SQL with `$1, $2, ...` placeholders
    - `Parameters` = collected parameter values (in order)
  - [x] `RenderCaseColumn(CaseColumnDto col, List<TableNodeDto> nodes)` helper: builds `CASE WHEN "tn"."cn" {op} {literal_or_null} THEN {thenLiteral} ... [ELSE {elseLiteral}] END`. For IS NULL/IS NOT NULL: `"tn"."cn" IS NULL`. THEN/ELSE values: if the string looks like a double-quoted identifier (`"..."`) render as-is; otherwise single-quote as a literal. `elseValue` empty string → omit ELSE clause.
  - [x] All double-quoting of identifiers: use a private helper `Q(string identifier)` = `$"\"{identifier}\""` — never trust that any identifier passed to Q is free of quote characters (though SafeIdentifier validation upstream should prevent this; double-quote-escaping inside identifiers would use `""` but is not needed given SafeIdentifier ensures no `"` in names).

- [x] Task 4: Wire generator into `DatasetService.UpdateAsync` (AC: 1, 4, 5)
  - [x] Add `IDatasetAllowlist _allowlist` to `DatasetService` primary constructor (after `viewManager`):
    ```csharp
    internal sealed partial class DatasetService(
        FormForgeDbContext db,
        DbConnectionFactory connectionFactory,
        DatasetViewManager viewManager,
        IDatasetAllowlist allowlist,
        ILogger<DatasetService> logger) : IDatasetService
    ```
  - [x] Add `BuilderStateInvalid` to `UpdateDatasetOutcome` enum (in `DatasetService.cs` at the top):
    ```csharp
    internal enum UpdateDatasetOutcome
        { Success, NotFound, ConcurrencyConflict, NameConflict, InvalidQuery, BuilderStateInvalid }
    ```
  - [x] Update `UpdateDatasetResult` to add `ErrorCode` field for distinguishing `BUILDER_STATE_INVALID` vs `TABLE_NOT_ALLOWLISTED`:
    - No change needed — `ErrorDetail` (existing string) carries the message; the new `BuilderStateInvalid` outcome is enough for the endpoint to choose the 422 code
  - [x] In `UpdateAsync`, after computing `effectiveIsCustomQuery`, `effectiveNewQuery`, `effectiveBuilderState`, and `effectiveViewQuery` (Step D of the existing code), add the builder-mode SQL generation block **before** checkpoint (a) (which is already guarded by `if (effectiveIsCustomQuery)`, so won't fire for builder mode):
    ```csharp
    // Story 11.1 — checkpoint (b): for builder mode, derive SQL from builder_state.
    // Runs after the version/concurrency checks (step C) so invalid state returns
    // 422 without consuming a connection attempt beyond the initial SELECT.
    if (!effectiveIsCustomQuery && !string.IsNullOrWhiteSpace(effectiveBuilderState))
    {
        var bsDto = BuilderStateSerializer.Deserialize(effectiveBuilderState);
        if (bsDto is null)
            return new UpdateDatasetResult(UpdateDatasetOutcome.BuilderStateInvalid,
                ErrorDetail: "builder_state could not be parsed.");

        var genResult = DatasetSqlGenerator.Generate(bsDto, _allowlist);
        if (genResult.HasErrors)
            return new UpdateDatasetResult(UpdateDatasetOutcome.BuilderStateInvalid,
                ErrorDetail: string.Join("; ", genResult.Errors));

        // Override the effective query with the server-generated SQL.
        // This overwrites any query the client may have provided for builder mode.
        effectiveNewQuery = genResult.ViewSql;
    }
    ```
  - [x] The block must be inserted **after** the `effectiveIsCustomQuery` / `effectiveNewQuery` assignments (Step D in existing code) and **before** the checkpoint (a) `if (effectiveIsCustomQuery && ...)` block. This ordering ensures: (1) version/concurrency is checked first (cheaper DB round-trip before expensive generation), (2) generated SQL flows into `effectiveViewQuery` computation that follows, (3) existing custom-query checkpoint (a) is untouched.
  - [x] After inserting the block, the existing `effectiveViewQuery` calculation already references `effectiveNewQuery`:
    ```csharp
    var effectiveViewQuery = string.IsNullOrWhiteSpace(effectiveNewQuery)
        ? "SELECT 1 AS placeholder" : effectiveNewQuery;
    ```
    No change needed — the generated SQL flows through automatically.
  - [x] **CRITICAL**: `effectiveNewQuery` is declared as `var` (implicitly `string?`). The generator output `ViewSql` is `string?`. Assigning `effectiveNewQuery = genResult.ViewSql` may require making `effectiveNewQuery` a `string?` variable (it already is, based on the `?:` pattern in existing code). Verify the type is compatible; if `effectiveNewQuery` is `string?`, the assignment is fine.
  - [x] The existing `DatasetService.CreateAsync` is **NOT modified** in Story 11.1 — builder-mode creates remain out of scope (the `CreateDatasetRequest` has no `BuilderState` field; that's Story 11.2's territory).

- [x] Task 5: Update `DatasetEndpoints.cs` — add `BuilderStateInvalid` outcome mapping (AC: 4)
  - [x] In the `MapPut("/{id:guid}")` handler, add the new outcome to the `result.Outcome switch`:
    ```csharp
    UpdateDatasetOutcome.BuilderStateInvalid => Results.Problem(
        detail: result.ErrorDetail,
        title: "Builder state invalid",
        statusCode: StatusCodes.Status422UnprocessableEntity,
        extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = "BUILDER_STATE_INVALID",
            ["messageKey"] = "datasets.builderStateInvalid",
        }),
    ```
  - [x] Add this case **before** the `_ =>` wildcard fallback, after the existing `InvalidQuery` case
  - [x] Also add an i18n key to `web/src/lib/i18n/locales/en.json`:
    - Under `datasets` object, add: `"builderStateInvalid": "The query builder configuration is invalid. Please review your column selections and filter conditions."`

- [x] Task 6: Unit tests — `DatasetSqlGeneratorTests.cs` (AC: 1–5)
  - [x] Create `src/FormForge.Api.Tests/Features/Datasets/DatasetSqlGeneratorTests.cs` (NEW file — no DB, no `[Collection]`, no `PostgresFixture`)
  - [x] Uses `Moq` or a stub `IDatasetAllowlist` that allows all tables. Create a private helper:
    ```csharp
    private static IDatasetAllowlist AllowAll()
    {
        var mock = new Mock<IDatasetAllowlist>();
        mock.Setup(a => a.IsAllowed(It.IsAny<string>())).Returns(true);
        return mock.Object;
    }
    ```
  - [x] Check if `Moq` is already in the test project's `.csproj`; if not, use a hand-written stub:
    ```csharp
    private sealed class AllowAllStub : IDatasetAllowlist
    {
        public Task<CatalogDto> GetCatalogAsync(CancellationToken ct) => throw new NotImplementedException();
        public bool IsAllowed(string tableName) => true;
    }
    ```
  - [x] **Pre-flight validation tests** (no DB):
    - `Generate_NoLeftNode_ReturnsError`: builder state with 2 right nodes, 1 checked column → HasErrors true, Errors contains "left"
    - `Generate_NoColumnsChecked_ReturnsError`: 1 left node, all columns unchecked → HasErrors true
    - `Generate_CaseColumnEmptyAlias_ReturnsError`: 1 left node, 1 checked column, 1 CaseColumn with empty alias
    - `Generate_CalculatedColumnEmptyAlias_ReturnsError`: similar for CalculatedColumn
  - [x] **Allowlist validation test**:
    - `Generate_TableNotAllowlisted_ReturnsError`: stub returns false for one table → HasErrors true
  - [x] **Happy-path SELECT generation tests**:
    - `Generate_SingleTable_OneColumn_ProducesCorrectSql`: left node `orders` with column `id` (pgType `integer`, checked, aggregate `none`, alias ``) → ViewSql contains `SELECT "orders"."id" AS "orders_id"`, `FROM "public"."orders"`, no WHERE, no ORDER BY
    - `Generate_SingleTable_WithAlias_UsesAlias`: alias `order_id` → `AS "order_id"`
    - `Generate_Aggregate_SumWithAlias_ProducesAggSql`: aggregate `SUM`, alias `total_amount` → `SUM("orders"."amount") AS "total_amount"`, GROUP BY clause present with non-aggregated cols
    - `Generate_Aggregate_AutoGroupBy_ExcludesAggregatedCol`: 2 checked cols, 1 aggregated → GROUP BY has only the non-aggregated col
    - `Generate_InnerJoin_ProducesJoinClause`: 2 nodes (left + right), 1 edge (INNER JOIN) → `INNER JOIN "public"."items" ON "orders"."id" = "items"."order_id"`
    - `Generate_FullOuterJoin_EmitsFULLOUTERKeyword`
    - `Generate_OrderBy_SingleClauseAsc_ProducesOrderBy`: `ORDER BY "orders"."id" ASC`
    - `Generate_OrderBy_MultipleClausesPreserveOrder`: 2 clauses → ORDER BY in declared order
    - `Generate_OrderBy_EmptyList_NoOrderByClause`
  - [x] **WHERE clause tests**:
    - `Generate_WhereEquals_StringColumn_ViewSqlInlinesQuotedLiteral`: condition `=` on text col `status`, value `'active'` → ViewSql contains `"orders"."status" = 'active'`
    - `Generate_WhereEquals_IntColumn_ViewSqlInlinesUnquotedNumber`: int col, value `42` → `"orders"."id" = 42`
    - `Generate_WhereIn_ProducesInClause`: IN, string[], values `['a','b']` → ViewSql `IN ('a', 'b')`
    - `Generate_WhereBetween_ProducesBetweenClause`: BETWEEN, `['10','20']` → `BETWEEN 10 AND 20` (numeric)
    - `Generate_WhereIsNull_NoLiteral`: IS NULL → `"col" IS NULL` (no literal)
    - `Generate_ParameterizedSql_UsesPlaceholders`: same filter → ParameterizedSql contains `= $1`, Parameters[0] == value
    - `Generate_WhereNestedGroups_CorrectlyParenthesized`: nested AND/OR → `((A AND B) OR C)`
    - `Generate_WhereEmptyFilters_NoWhereClause`: filters with empty items → no WHERE in output
  - [x] **Calculated column / CASE tests**:
    - `Generate_CalculatedColumn_ValidExpression_InSelectList`: expression `price * 1.1`, alias `adjusted_price` → `(price * 1.1) AS "adjusted_price"` in SELECT
    - `Generate_CalculatedColumn_DangerousExpression_ReturnsError`: expression `DROP TABLE users` → HasErrors
    - `Generate_CaseColumn_SingleWhen_ProducesCaseExpression`: 1 WHEN arm, 1 THEN → valid CASE WHEN ... THEN ... END

- [x] Task 7: Unit tests — `ExpressionSecurityValidatorTests.cs` (AC: 1, 4)
  - [x] Create `src/FormForge.Api.Tests/Features/Datasets/ExpressionSecurityValidatorTests.cs` (NEW file)
  - [x] Valid expressions: `price * 1.1`, `COALESCE(name, 'unknown')`, `LENGTH(description)`, `a + b - c`
  - [x] Invalid — keyword start: `DROP TABLE users`, `DELETE FROM foo`, `INSERT INTO bar VALUES (1)`, `CREATE TABLE x (id int)`
  - [x] Invalid — contains semicolon: `price; DROP TABLE users`
  - [x] Invalid — unparseable: `(unclosed paren`, `price +`
  - [x] Each test calls `ExpressionSecurityValidator.Validate(expr, "test_alias")` and asserts `IsValid` / error message

- [x] Task 8: Integration test — `DatasetBuilderModeTests.cs` (AC: 1, 4, 5)
  - [x] Create `src/FormForge.Api.Tests/Features/Datasets/DatasetBuilderModeTests.cs` (NEW file)
  - [x] `[Collection("DatasetIntegrationTests")]` + `IClassFixture<PostgresFixture>` + `IAsyncLifetime` — same setup pattern as `DatasetUpdateTests.cs`
  - [x] `InitializeAsync`: same pattern as DatasetUpdateTests — `db.Database.MigrateAsync()`, TRUNCATE, drop views, seed admin user + role
  - [x] Helper method `CreateBuilderDataset(client, name)` → POST /api/datasets with `is_custom_query: false, query: null` → return `DatasetDto`
  - [x] Helper `MakeBuilderState(tableName, columnName)` → creates a minimal valid `BuilderStateDto` JSON with one left node, one checked column, no filters or ORDER BY
  - [x] **Test 1: `Put_BuilderMode_ValidState_Returns200_And_QueryIsGenerated`**:
    - Pre: create dataset (custom query mode, version 1)
    - PUT with `is_custom_query: false`, `builder_state: <valid builder state with allowlisted table + 1 checked column>`, `version: 1`
    - Assert: 200, response `isCustomQuery == false`, response `query` contains `SELECT` and `FROM "public"."..."`
    - Assert: GET /api/datasets/{id} returns the persisted `query` value matching the expected SQL
    - **Note**: this test requires that the table referenced in builder_state is actually in the `DatasetManager:AllowedTables` configuration for the test fixture. Use a table that exists in the test DB after migrations — e.g. `users` is in the denylist, but `component_schemas` might be allowlisted. Check the test app's configuration or use `"WithWebHostBuilder"` to set `"DatasetManager:AllowedTables:0": "some_test_table"` and create that table in the test DB, OR better: use the catalog endpoint to find an allowlisted table dynamically. Alternatively, mock `IDatasetAllowlist` in the test factory. See Dev Notes §8 for the recommended approach.
  - [x] **Test 2: `Put_BuilderMode_NoLeftNode_Returns422_BuilderStateInvalid`**:
    - PUT with builder_state where all nodes have `side: "right"` → 422 with `code: "BUILDER_STATE_INVALID"`
  - [x] **Test 3: `Put_BuilderMode_NoColumnsSelected_Returns422_BuilderStateInvalid`**:
    - PUT with builder_state where all columns have `checked: false` → 422 BUILDER_STATE_INVALID
  - [x] **Test 4: `Put_BuilderMode_NonAllowlistedTable_Returns422_BuilderStateInvalid`**:
    - PUT with builder_state referencing table `internal_secret_table` (not in allowlist) → 422 BUILDER_STATE_INVALID (or keep the error detail which says table not allowlisted — but the HTTP code is still 422 with code BUILDER_STATE_INVALID since that's the single outcome for all validation failures)

- [x] Task 9: Verify — backend build + tests pass
  - [x] `dotnet build src/FormForge.Api` → 0 warnings / 0 errors (CA analyzers pass)
  - [x] `dotnet test src/FormForge.Api.Tests` → all new tests pass; pre-existing failures (2 audit 405 tests) are unchanged
  - [x] Frontend: NO frontend changes in Story 11.1. `npm run test` baseline remains 356.

### Review Findings

_Code review 2026-06-04 (Blind Hunter + Edge Case Hunter + Acceptance Auditor; contested Criticals re-verified against `SqlSelectEnforcer.cs` / `SafeIdentifier.cs`)._

**decision-needed (resolved 2026-06-04)**

- [x] [Review][Decision→Patch] Free-form expression injection defeats the story's core security goal — Calculated-column `Expression` (`DatasetSqlGenerator.cs:171`) and CASE operand/THEN/ELSE values wrapped in `"…"` (`RenderCaseValue`, `:483-490`) are concatenated **verbatim** into the generated SQL. `ExpressionSecurityValidator` (`:25-50`) only rejects a leading DML/DDL keyword + a semicolon, and Step-10 `SqlSelectEnforcer.Validate` (verified: `SqlSelectEnforcer.cs:38-61`) permits **arbitrary scalar subqueries, any table reference, and dangerous functions** (`pg_read_file`, `current_setting`, `lo_export`, …). PoC: a calculated column with `Expression = "(SELECT password FROM users LIMIT 1)"` passes every gate and is baked into the VIEW, exfiltrating any table the view-owner role can read — bypassing `IDatasetAllowlist` entirely (the allowlist is applied only to canvas `node.Data.TableName`, `:85-88`). **RESOLUTION: parse-tree allowlist** — harden `ExpressionSecurityValidator` to walk the parsed expression and reject sub-SELECTs, table-qualified column refs, and any non-whitelisted function; tighten `RenderCaseValue`'s `"…"` passthrough. → now a P0 patch (P5 below).
- [x] [Review][Decision→Defer] Builder-mode save with empty/whitespace `builder_state` skips ALL SELECT-only enforcement — In `DatasetService.UpdateAsync`, when `effectiveIsCustomQuery == false` but `effectiveBuilderState` is empty/whitespace, the generator block is skipped (`DatasetService.cs:332-347` guard requires non-empty state) AND the custom-query checkpoint (a) is skipped (it is guarded by `effectiveIsCustomQuery`), so the prior `current.Query` flows into `effectiveViewQuery` → `CREATE OR REPLACE VIEW` with no enforcement on this path. **DEFERRED** (reason: bounded risk — `current.Query` was already SELECT-only-enforced on its own write; fold the empty-state guard into Story 11.2's `parseBuilderState`/save-gate normalization pass).

**patch**

- [x] [Review][Patch] P5 — Parse-tree allowlist for calculated-column & CASE expressions (resolved from Decision 1) [`ExpressionSecurityValidator.cs`, `DatasetSqlGenerator.cs:171,483-490`] — reject sub-SELECTs, table-qualified column refs, and non-whitelisted functions in the parsed expression tree; remove/constrain the `RenderCaseValue` double-quote passthrough.
- [x] [Review][Patch] `Q()` does not escape embedded double-quotes → identifier-injection [`DatasetSqlGenerator.cs:276`] — `Q(id) => $"\"{id}\""` never doubles an internal `"`. Column names, JOIN handles (`SourceHandle`/`TargetHandle`), ORDER BY table/column, and CASE WHEN column are client-supplied and **not** run through `SafeIdentifier` (only user aliases are, `:95-99`). An embedded `"` breaks out of the quoted identifier. Fix: `id.Replace("\"", "\"\"")` inside `Q`.
- [x] [Review][Patch] Attacker JSON omitting `data`/`columns`/`whens`/`items` → NullReferenceException → HTTP 500 (not 422) [`DatasetSqlGenerator.cs:69,72`] — `BuilderStateSerializer.Deserialize` only catches during deserialization; a node with null `Data` (System.Text.Json leaves non-nullable ref props null when the JSON property is absent) NREs in `Generate`. Fix: null-guard `Data`/`Columns`/`Whens`/group `Items` and surface as a `BuilderStateInvalid` error.
- [x] [Review][Patch] `RenderLiteral` emits the raw numeric string → `IN (1,000)` silent wrong results [`DatasetSqlGenerator.cs:502-513`] — `decimal.TryParse(str, NumberStyles.Any, …)` accepts `1,000` / `(5)` / `1e3` / ` 5 ` but the method returns the **original** `str` unquoted; `… IN (1,000)` parses (verified) as two integers `1, 0` and passes Step 10 → VIEW silently filters wrong. Fix: emit the parsed value and tighten `NumberStyles` (drop `AllowThousands`/`AllowParentheses`/`AllowCurrencySymbol`/leading-white).
- [x] [Review][Patch] ViewSql vs ParameterizedSql numeric type divergence [`DatasetSqlGenerator.cs:527-534`] — `JsonElementToClr` keys only on `JsonValueKind`, ignoring `pgType`; a numeric value sent as a JSON string `"42"` (the canonical contract shape) is bound as a **text** parameter while `RenderLiteral` emits it unquoted. The two SQL variants are not equivalent — Story 11.3's preview may coerce/err differently than the persisted VIEW. Fix: align `JsonElementToClr` typing with `RenderLiteral`'s pgType logic.

**defer** (logged to `deferred-work.md`)

- [x] [Review][Defer] GROUP BY omits CASE/calculated columns → PG "must appear in GROUP BY" error when mixed with aggregates (fails safe — VIEW-creation error, not corruption) [`DatasetSqlGenerator.cs:177-190`] — deferred
- [x] [Review][Defer] `JoinEdgeDataDto` drops `sourceColumn`/`targetColumn`; JOIN built from edge-level handles — divergence from `builderState.ts` contract (Decision 6.11) [`Dtos/BuilderStateDto.cs:49`] — deferred
- [x] [Review][Defer] ORDER BY / CASE WHEN columns not validated against canvas nodes or allowlist — bounded to allowlisted tables & quoted, but broader than the UI surface [`DatasetSqlGenerator.cs:200-206,428`] — deferred
- [x] [Review][Defer] Empty `IN ()` and zero-WHEN `CASE END` produce the opaque message "Generated SQL is not a valid SELECT" instead of a meaningful error (handled by Step 10, ungraceful) [`DatasetSqlGenerator.cs:322-327,414-478`] — deferred
- [x] [Review][Defer] Deeply nested filter tree — `FilterItemDtoConverter` re-serializes each node (O(n²)) and recursion is bounded only by S.T.Json `MaxDepth` (→422); minor CPU DoS [`Dtos/BuilderStateDto.cs:101-113`] — deferred
- [x] [Review][Defer] `''`-escaping in `RenderLiteral`/`RenderCaseValue` assumes `standard_conforming_strings = on` (PostgreSQL default) — hardening note only [`DatasetSqlGenerator.cs:524,489`] — deferred
- [x] [Review][Defer] CASE THEN/ELSE/operand literal strings beginning with a DML/DDL keyword (e.g. "Drop-off") are over-rejected by `ExpressionSecurityValidator` [`DatasetSqlGenerator.cs:445,457,470`] — deferred (extends the existing 11-1 implementation defer)
- [x] [Review][Defer] AC-5 "same transaction / VIEW stays in sync" is only indirectly asserted — no test inspects the actual VIEW definition (`pg_views`) or proves a VIEW-DDL failure rolls back the `query` write; many edge cases untested (numeric separator, null `Data`→500, empty `IN`, `NOT IN`, `LIKE`/`ILIKE`, boolean `t`/`f`, scalar-to-IN, array-to-`=`, multi-table GROUP BY) [`DatasetBuilderModeTests.cs`, `DatasetSqlGeneratorTests.cs`] — deferred

## Dev Notes

### 1. Architecture Context — What Story 11.1 Adds

Story 11.1 is the first backend-only story in Epic 11. It adds three new C# classes and wires one of them into the existing `DatasetService.UpdateAsync` flow. The frontend is NOT changed in this story — the canvas already emits `builder_state` JSON via `onChange`, and the parent (dataset management UI from Story 8.10) already sends it in `UpdateDatasetRequest.BuilderState`. The backend simply now processes it instead of passing it through.

**No frontend changes. No new API endpoints. No database migrations.**

### 2. What Already Exists — Do NOT Recreate

- `SqlSelectEnforcer.cs` — already validates SELECT-only; `DatasetSqlGenerator` calls it at step 10. Do NOT duplicate this logic.
- `SafeIdentifier.cs` — `TryCreate(string? raw, out SafeIdentifier? result, out string? error)`. Import path: `using FormForge.Api.Domain.ValueTypes;`. Used only for user-provided aliases (column, CASE, calculated), NOT for catalog-sourced table/column names.
- `DatasetService.UpdateAsync` — already handles the full transactional lifecycle. The generator output (`ViewSql`) just needs to flow into `effectiveNewQuery` before the existing flow. The checkpoint (a) block is already guarded by `if (effectiveIsCustomQuery)` so it won't fire for builder mode. The existing UPDATE SQL already writes `query = @query` and `builder_state = @builderState::jsonb`.
- `IDatasetAllowlist.IsAllowed(string tableName)` — synchronous, no I/O, reads from an in-memory set. Call directly in the generator. The `_allowlist` field added to `DatasetService` is the same singleton already registered for the catalog endpoint.
- `pgsqlparser` NuGet package — already in `FormForge.Api.csproj`. Namespace: `PgSqlParser`, class: `Parser`. `Parser.Parse(sql)` returns `Result<ParseResult?>` with `.IsSuccess` / `.Value` / `.Error` — does NOT throw on parse failure.
- `DatasetDto` — already has `BuilderState: string?`. The generated SQL goes into `Query`, not `BuilderState`.
- `UpdateDatasetRequest` — already has `BuilderState: string?`. The frontend sends the raw JSON in this field.
- Deferred items covered by this story (see `deferred-work.md`): ExpressionSecurityValidator.cs, BuilderStateDto.cs, SQL generation of `(expr) AS "alias"`, `$1,$2,...` parameterization plan.

### 3. `effectiveNewQuery` Mutability Issue

In the existing `DatasetService.UpdateAsync` (line ~305), the variables are declared with `var`:
```csharp
var effectiveNewQuery = request.Query is not null ? request.Query : current.Query;
```

`string?` is assignable. The block in Task 4 reassigns `effectiveNewQuery = genResult.ViewSql;`. This is safe — the `var` infers `string?` and `ViewSql` is also `string?`. However, if the compiler infers `string` (non-nullable) due to the ternary, you may need to declare it as `string? effectiveNewQuery`. Verify with `dotnet build`.

### 4. `FilterItemDto` Discriminated Union — JSON Deserialization Challenge

The `items` array in a `FilterGroupDto` can contain either a `FilterGroupDto` or a `FilterConditionDto`. `System.Text.Json` cannot handle this polymorphism without a custom converter. The `FilterItemDtoConverter` in Task 1 uses `JsonDocument.ParseValue` to peek at the `kind` field before dispatching. This converter must be registered in `BuilderStateSerializer.Options.Converters` BEFORE deserializing.

**Critical**: `BuilderStateSerializer.Options` is a static readonly field initialized once. `Converters` is mutable before the first use; do NOT add the converter after deserialization calls have been made. Initialize it fully in the field initializer:
```csharp
internal static readonly JsonSerializerOptions Options = new()
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    Converters = { new FilterItemDtoConverter() },
};
```

### 5. SafeIdentifier Does NOT Validate Catalog Column Names

`SafeIdentifier` uses pattern `^[a-z_][a-z0-9_]{0,62}$`. PostgreSQL column names from `information_schema` are already lowercase in this project (the Designer enforces lowercase identifiers via FR-9 / Decision 1.1). However, calling `SafeIdentifier.TryCreate(columnName, ...)` on catalog-sourced column names would reject names like `updated_at` that start with `_`... wait, actually `_` IS allowed by the pattern (`^[a-z_]...`). And `updated_at` starts with `u` — it passes.

The concern is more about runtime-provisioned column names: since all column names in provisioned tables went through SafeIdentifier validation at designer save time, they all already satisfy the pattern. So SafeIdentifier validation of column names would pass. However, calling TryCreate on table/column names is unnecessary overhead and could cause unexpected failures for edge-case names. **Only validate user-provided aliases with SafeIdentifier** (step 3), not table/column names.

Double-quoting of all identifiers in the SQL output (step 2 of the generator) is the actual SQL injection defense for identifiers. Double-quoting handles even unusual names that might fail SafeIdentifier.

### 6. Two SQL Output Variants: ViewSql vs ParameterizedSql

`DatasetSqlGenerator.Generate()` produces two SQL strings:

- **`ViewSql`**: WHERE filter values are rendered as safe inlined literals. Used for `CREATE VIEW` / `CREATE OR REPLACE VIEW` DDL and stored in `custom_dataset.query`. PostgreSQL VIEWs do not support `$1, $2` parameter placeholders — they require static SQL text.
- **`ParameterizedSql`**: WHERE filter values are replaced with `$1, $2, ...` placeholders. The `Parameters` array holds the corresponding values. Used by Story 11.3's preview endpoint where Npgsql binds parameters to the prepared statement.

Story 11.1 uses only `ViewSql`. Story 11.3 will read both `ParameterizedSql` and `Parameters` from the generator.

**Both strings share identical SELECT/FROM/JOIN/ORDER BY clauses**. Only the WHERE clause differs in how values are represented. Implement `BuildWhereView()` and `BuildWhereParameterized()` as separate recursive methods sharing the filter-tree traversal logic.

### 7. `RenderLiteral` Safety Properties

`RenderLiteral(JsonElement value, string pgType)` is the core injection-prevention function for `ViewSql`. It must satisfy:
- Strings: always wrapped in `'...'` with internal `'` doubled to `''`. Example: `O'Brien` → `'O''Brien'`. No escape sequences, no `E'...'` prefix needed (PostgreSQL standard_conforming_strings defaults to on).
- Numerics: only emitted unquoted if `decimal.TryParse(str, out _)` succeeds — this prevents any non-numeric string from being emitted raw.
- Boolean: only `true` or `false` keyword — never raw user string.
- Fallback: any unrecognized pgType falls through to the string quoting branch (safe by default).
- `NULL` keyword: emitted only when `value.ValueKind == JsonValueKind.Null`.
- NEVER use string interpolation to embed an unvalidated user value: `$"...{str}..."` is only used after quoting (`$"'{str.Replace("'", "''")}'"` is safe because the Replace has already escaped all `'`).

### 8. Integration Test — Allowlist Configuration for Tests

The integration test for PUT builder mode requires the `builder_state` to reference a table that is actually in the allowlist. The allowlist is configured via `DatasetManager:AllowedTables` in `appsettings.json`. The test factory uses `WithWebHostBuilder` to inject settings.

**Recommended approach**: in `DatasetBuilderModeTests.cs`, add a `DatasetManager:AllowedTables:0` setting pointing to a real table that exists after migrations. Candidate tables: check `DatasetCatalogTests.cs` for what tables it expects from the allowlist — if that test already seeds `DatasetManager:AllowedTables`, use the same table name in `DatasetBuilderModeTests`.

If no real allowlisted table is suitable for testing (e.g., all real tables are in the denylist), use a test-specific approach: either (a) set up a test table via raw DDL in `InitializeAsync`, or (b) override `IDatasetAllowlist` in the factory by adding `.ConfigureServices(s => s.AddSingleton<IDatasetAllowlist>(new AllAllowlistStub()))` to `WithWebHostBuilder`. Option (b) is simpler for this story.

See `DatasetCatalogTests.cs` for the existing test pattern with allowlisted tables.

### 9. DatasetService Constructor — Compilation Order

Adding `IDatasetAllowlist allowlist` to the primary constructor of `DatasetService` will cause a compile error if no `IDatasetAllowlist` is registered in the test DI container. The integration tests for Dataset stories (`DatasetUpdateTests`, `DatasetViewLifecycleTests`, etc.) use `WebApplicationFactory<Program>` which runs the real `Program.cs` DI registration. Since `IDatasetAllowlist` is already registered in `Program.cs` (it was added in Story 9.1), the existing integration tests will NOT break.

Unit tests that directly instantiate `DatasetService` (if any) would need updating — but no current tests do this (all dataset tests use the HTTP client via `WebApplicationFactory`). So adding the constructor parameter is safe.

### 10. Deferred Items to Carry Forward in `deferred-work.md`

After implementation, add these entries to `deferred-work.md` under a new `## Deferred from: implementation of 11-1-server-authoritative-sql-generation` section:

- **Self-join table aliasing in DatasetSqlGenerator** — When the same table appears twice as two separate canvas nodes (self-join), the generator needs per-node numeric aliases (`"orders" t0`, `"orders" t1`) in FROM, JOIN, SELECT, WHERE, and ORDER BY clauses. Currently the generator assumes each table appears only once. Story 11.2 or a follow-on story should add this when self-join canvas scenarios are tested. [`DatasetSqlGenerator.cs`]
- **Filter condition with blank tableName/columnName skipped silently** — `FilterConditionDto` with empty `tableName` or `columnName` is skipped in WHERE generation rather than returning a validation error. Story 11.2 should add a pre-flight validation gate: "All filter conditions must reference a table and column." [`DatasetSqlGenerator.cs`]
- **`ViewSql` used for `custom_dataset.query` — preview path not yet wired** — `SqlGenerationResult.ParameterizedSql` and `Parameters[]` are generated but not yet consumed. Story 11.3 wires these into POST /api/datasets/preview. [`DatasetEndpoints.cs`]
- **CreateAsync not wired for builder-mode creates** — `DatasetService.CreateAsync` still treats `is_custom_query = false` as a placeholder-only create (no builder_state parsing). Story 11.2 should add `BuilderState` to `CreateDatasetRequest` and run the generator in `CreateAsync`. [`DatasetService.cs`, `Dtos/CreateDatasetRequest.cs`]
- **`parseBuilderState` normalization deferred to Story 11.2** — `BuilderStateSerializer.Deserialize` returns `null` on invalid JSON (canvas gets `EMPTY_BUILDER_STATE`), but partial blobs missing `orderBy`, `caseColumns`, or `calculatedColumns` will fail deserialization with an exception in the C# layer before this point. Story 11.2 should add field-by-field normalization in `parseBuilderState` (frontend) and defensive null-coalescing in `BuilderStateSerializer` (backend). [`builderState.ts`, `BuilderStateDto.cs`]

### 11. Current Test Baseline and Expected New Tests

- Backend: All existing tests pass (pre-existing 2 audit 405 failures documented in `deferred-work.md` are excluded). New tests add:
  - `DatasetSqlGeneratorTests.cs`: ~18 unit tests
  - `ExpressionSecurityValidatorTests.cs`: ~8 unit tests
  - `DatasetBuilderModeTests.cs`: ~4 integration tests
- Frontend: 356 tests; Story 11.1 has no frontend changes → 356 tests remain.
- `dotnet build` with `<TreatWarningsAsErrors>true` — ensure all new `internal` types have CA1812 suppression if registered via DI (only `DatasetService` constructor changes; the new classes are not DI-registered, just statically called).

### 12. `@xyflow/react` v12 Typing Rules

These do NOT apply to Story 11.1. This is a backend-only story. No React Flow components are created or modified.

### Project Structure Notes

**New files:**
- `src/FormForge.Api/Features/Datasets/Dtos/BuilderStateDto.cs` — C# mirror of `builderState.ts`; same folder as other DTO files
- `src/FormForge.Api/Features/Datasets/DatasetSqlGenerator.cs` — pure static class; same folder as `SqlSelectEnforcer.cs`
- `src/FormForge.Api/Features/Datasets/ExpressionSecurityValidator.cs` — pure static class; same folder as `SqlSelectEnforcer.cs`
- `src/FormForge.Api.Tests/Features/Datasets/DatasetSqlGeneratorTests.cs` — unit tests, no DB
- `src/FormForge.Api.Tests/Features/Datasets/ExpressionSecurityValidatorTests.cs` — unit tests, no DB
- `src/FormForge.Api.Tests/Features/Datasets/DatasetBuilderModeTests.cs` — integration tests

**Modified files:**
- `src/FormForge.Api/Features/Datasets/DatasetService.cs` — add `IDatasetAllowlist` to constructor; add `BuilderStateInvalid` to `UpdateDatasetOutcome`; add builder-mode generation block in `UpdateAsync`
- `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs` — add `BuilderStateInvalid` case in PUT handler
- `web/src/lib/i18n/locales/en.json` — add `datasets.builderStateInvalid` key

### References

- Architecture: `_bmad-output/planning-artifacts/architecture.md`, Section 6.10 (AR-66 DatasetSqlGenerator), Section 6.11 (AR-67 builder_state contract), Section 6.8 (AR-64 ExpressionSecurityValidator), Section 6.9 (AR-65 error codes), Section 6.3 (AR-59 view lifecycle)
- TypeScript BuilderState contract: `web/src/features/datasets/types/builderState.ts` — authoritative cross-layer contract (Decision 6.11); C# must mirror exactly
- Existing enforcer pattern: `src/FormForge.Api/Features/Datasets/SqlSelectEnforcer.cs` — pgsqlparser usage pattern
- SafeIdentifier API: `src/FormForge.Api/Features/Designer/SafeIdentifier.cs` — `TryCreate(string?, out SafeIdentifier?, out string?)` signature
- DatasetService: `src/FormForge.Api/Features/Datasets/DatasetService.cs` — UpdateAsync implementation (full source); insert generator block after Step D, before checkpoint (a)
- DatasetEndpoints: `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs` — PUT handler switch statement
- AllowlistContract: `src/FormForge.Api/Features/Datasets/DatasetAllowlist.cs` — `IDatasetAllowlist.IsAllowed(string tableName)` is synchronous
- Existing test patterns: `src/FormForge.Api.Tests/Features/Datasets/DatasetUpdateTests.cs` — WebApplicationFactory + PostgresFixture + collection pattern
- `SqlSelectEnforcerTests.cs` — unit test pattern (no DB, no collection)
- Deferred work items owned by this story: `_bmad-output/implementation-artifacts/deferred-work.md` lines 39, 64–66, 72–73 (ExpressionSecurityValidator, BuilderStateDto, SQL generation)
- Memory note: pgsqlparser package substitution — see project memory `project_pgquery_package_substitution.md`; use `pgsqlparser` NuGet / `PgSqlParser.Parser.Parse()`

## Dev Agent Record

### Agent Model Used

claude-opus-4-8[1m] (Opus 4.8, 1M context)

### Debug Log References

- Initial API build hit one analyzer error (CA1859) on `RenderCaseColumn`'s `IReadOnlyDictionary` parameter; changed to the concrete `Dictionary<string, TableNodeDto>` it is always called with. Rebuild: 0 warnings / 0 errors.
- A stale `FormForge.Api.exe` dev instance (PID 24304) held a lock on the output DLL and blocked the rebuild; stopped it and rebuilt cleanly. (Relaunch the app to pick up the new build.)
- Story's stated `SafeIdentifier` import path (`FormForge.Api.Domain.ValueTypes`) is wrong — the class lives in `FormForge.Api.Features.Designer`; used the correct namespace.

### Completion Notes List

- Implemented three new backend classes per AR-66/AR-64/AR-67: `BuilderStateDto` (C# mirror of `builderState.ts`, with a polymorphic `FilterItemDtoConverter` for the condition/group union), `ExpressionSecurityValidator` (layers 1–2; layer 3 is the generator's step-10 `SqlSelectEnforcer` call), and `DatasetSqlGenerator` (the 10-step algorithm producing both `ViewSql` (inlined safe literals) and `ParameterizedSql` + `Parameters[]` for Story 11.3).
- Wired the generator into `DatasetService.UpdateAsync` as checkpoint (b): builder-mode saves re-derive the SQL server-side and override `effectiveNewQuery` **before** `effectiveViewQuery` is computed, so the row's `query` and the backing VIEW stay in sync within the same transaction (AC-5). Added `UpdateDatasetOutcome.BuilderStateInvalid` + endpoint 422 `BUILDER_STATE_INVALID` mapping and the `datasets.builderStateInvalid` i18n key.
- All identifiers are double-quoted; only user-provided aliases are run through `SafeIdentifier`. `RenderLiteral` is the injection-prevention core for `ViewSql` (string `''`-escaping, unquoted-only-if-numeric, boolean keyword, NULL only for JSON null).
- Tests: 26 generator unit tests + 8 expression-validator unit tests (47 xUnit cases total incl. theories) all pass; 4 builder-mode integration tests (valid→200 + persisted query; no-left/no-columns/non-allowlisted→422) all pass. Backend build is 0 warnings / 0 errors.
- Deferred items (self-join aliasing, blank-condition pre-flight gate, preview-path wiring, builder-mode CreateAsync, builder_state normalization) carried forward per Dev Notes §10.

### File List

**New:**
- `src/FormForge.Api/Features/Datasets/Dtos/BuilderStateDto.cs`
- `src/FormForge.Api/Features/Datasets/ExpressionSecurityValidator.cs`
- `src/FormForge.Api/Features/Datasets/DatasetSqlGenerator.cs`
- `src/FormForge.Api.Tests/Features/Datasets/DatasetSqlGeneratorTests.cs`
- `src/FormForge.Api.Tests/Features/Datasets/ExpressionSecurityValidatorTests.cs`
- `src/FormForge.Api.Tests/Features/Datasets/DatasetBuilderModeTests.cs`

**Modified:**
- `src/FormForge.Api/Features/Datasets/DatasetService.cs` — `IDatasetAllowlist` ctor param; `BuilderStateInvalid` outcome; builder-mode generation block in `UpdateAsync`
- `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs` — `BuilderStateInvalid` → 422 case in the PUT handler
- `web/src/lib/i18n/locales/en.json` — added `datasets.builderStateInvalid` key (both `datasets` objects)

## Change Log

| Date       | Change                                                                 |
|------------|------------------------------------------------------------------------|
| 2026-06-04 | Story 11.1 implemented: server-authoritative SQL generation from `builder_state` (`DatasetSqlGenerator`, `ExpressionSecurityValidator`, `BuilderStateDto`); wired into `DatasetService.UpdateAsync` with `BUILDER_STATE_INVALID` (422) handling; 30 new tests added. |
