using FormForge.Api.Features.Designer;
using FormForge.Api.Features.DynamicCrud;
using FormForge.Api.Features.SchemaRegistry;

namespace FormForge.Api.Tests.Features.DynamicCrud;

public class DynamicQueryBuilderTests
{
    private static SafeIdentifier MakeTable(string name)
    {
        Assert.True(SafeIdentifier.TryCreate(name, out var id, out _));
        return id!;
    }

    private static HashSet<string> AllowedSortCols(params string[] userCols)
    {
        var set = new HashSet<string>(DynamicQueryBuilder.SystemSortPgColumns, StringComparer.Ordinal);
        foreach (var c in userCols) set.Add(c);
        return set;
    }

    [Fact]
    public void ParseSort_SingleAscColumn_ReturnsParsed()
    {
        var result = DynamicQueryBuilder.ParseSort("title:asc", AllowedSortCols("title"));
        Assert.True(result.IsSuccess);
        var sort = Assert.Single(result.Sorts);
        Assert.Equal("title", sort.Column);
        Assert.Equal("ASC", sort.Direction);
    }

    [Fact]
    public void ParseSort_ThreeColumns_AllValid_ReturnsParsed()
    {
        var result = DynamicQueryBuilder.ParseSort(
            "created_at:desc,title:asc,id:DESC",
            AllowedSortCols("title"));
        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Sorts.Count);
        Assert.Equal(("created_at", "DESC"), (result.Sorts[0].Column, result.Sorts[0].Direction));
        Assert.Equal(("title", "ASC"), (result.Sorts[1].Column, result.Sorts[1].Direction));
        Assert.Equal(("id", "DESC"), (result.Sorts[2].Column, result.Sorts[2].Direction));
    }

    [Fact]
    public void ParseSort_FourColumns_ReturnsError()
    {
        var result = DynamicQueryBuilder.ParseSort(
            "created_at:desc,title:asc,id:asc,updated_at:desc",
            AllowedSortCols("title"));
        Assert.False(result.IsSuccess);
        Assert.Contains("3 sort", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseSort_UnknownColumn_ReturnsError()
    {
        var result = DynamicQueryBuilder.ParseSort("nope:asc", AllowedSortCols("title"));
        Assert.False(result.IsSuccess);
        Assert.Contains("nope", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseSort_InvalidDirection_ReturnsError()
    {
        var result = DynamicQueryBuilder.ParseSort("title:sideways", AllowedSortCols("title"));
        Assert.False(result.IsSuccess);
        Assert.Contains("sideways", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseSort_Null_ReturnsEmptyList()
    {
        var result = DynamicQueryBuilder.ParseSort(null, AllowedSortCols("title"));
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Sorts);
    }

    [Fact]
    public void ParseSort_Whitespace_ReturnsEmptyList()
    {
        var result = DynamicQueryBuilder.ParseSort("   ", AllowedSortCols("title"));
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Sorts);
    }

    [Fact]
    public void ParseSort_MissingDirection_ReturnsError()
    {
        var result = DynamicQueryBuilder.ParseSort("title", AllowedSortCols("title"));
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void MapClientFilterKeyToPgName_SystemKeys_AreMapped()
    {
        Assert.Equal("is_deleted", DynamicQueryBuilder.MapClientFilterKeyToPgName("isDeleted"));
        Assert.Equal("created_at", DynamicQueryBuilder.MapClientFilterKeyToPgName("createdAt"));
        Assert.Equal("created_by", DynamicQueryBuilder.MapClientFilterKeyToPgName("createdBy"));
        Assert.Equal("id", DynamicQueryBuilder.MapClientFilterKeyToPgName("id"));
    }

    [Fact]
    public void MapClientFilterKeyToPgName_UserFieldKey_PassesThrough()
    {
        Assert.Equal("title", DynamicQueryBuilder.MapClientFilterKeyToPgName("title"));
        Assert.Equal("report_title", DynamicQueryBuilder.MapClientFilterKeyToPgName("report_title"));
    }

    [Fact]
    public void BuildSelectQuery_NoFilterNoSort_GeneratesDefaultOrderByCreatedAtDesc()
    {
        var (sql, parameters) = DynamicQueryBuilder.BuildSelectQuery(
            MakeTable("records_t"),
            [new ColumnDefinition("title", "TEXT", "TextInput", false)],
            [],
            new Dictionary<string, string>(StringComparer.Ordinal),
            page: 1, pageSize: 25);

        Assert.Contains("FROM \"records_t\"", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(" WHERE ", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY \"created_at\" DESC", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT @p_limit OFFSET @p_offset", sql, StringComparison.Ordinal);

        var paramNames = parameters.ParameterNames.ToHashSet(StringComparer.Ordinal);
        Assert.Contains("p_limit", paramNames);
        Assert.Contains("p_offset", paramNames);
    }

    [Fact]
    public void BuildSelectQuery_WithSortAndTextFilter_GeneratesIlikeSubstringMatch()
    {
        var (sql, parameters) = DynamicQueryBuilder.BuildSelectQuery(
            MakeTable("orders"),
            [new ColumnDefinition("title", "TEXT", "TextInput", false)],
            [new DynamicQueryBuilder.SortParam("created_at", "DESC"),
             new DynamicQueryBuilder.SortParam("title", "ASC")],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["title"] = "foo",
            },
            page: 2, pageSize: 10);

        Assert.Contains("FROM \"orders\"", sql, StringComparison.Ordinal);
        // TEXT columns get ILIKE substring matching — PG has no implicit cast
        // from text to other types via =, but more importantly the UI is doing
        // partial-typing search and = was producing zero results.
        Assert.Contains("WHERE \"title\" ILIKE @filter_0", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY \"created_at\" DESC, \"title\" ASC", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT @p_limit OFFSET @p_offset", sql, StringComparison.Ordinal);

        var paramNames = parameters.ParameterNames.ToHashSet(StringComparer.Ordinal);
        Assert.Contains("filter_0", paramNames);
        Assert.Contains("p_limit", paramNames);
        Assert.Contains("p_offset", paramNames);

        Assert.Equal(10L, parameters.Get<long>("p_offset"));
        Assert.Equal(10, parameters.Get<int>("p_limit"));
        Assert.Equal("%foo%", parameters.Get<string>("filter_0"));
    }

    [Fact]
    public void BuildSelectQuery_NumericFilter_CastsParameterServerSide()
    {
        var (sql, parameters) = DynamicQueryBuilder.BuildSelectQuery(
            MakeTable("orders"),
            [new ColumnDefinition("price", "NUMERIC", "NumberInput", false)],
            [],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["price"] = "42.5",
            },
            page: 1, pageSize: 25);

        // Postgres does not implicit-cast text → numeric for the = operator
        // (42883). Explicit ::numeric cast is what makes the comparison work.
        Assert.Contains("WHERE \"price\" = @filter_0::numeric", sql, StringComparison.Ordinal);
        Assert.Equal("42.5", parameters.Get<string>("filter_0"));
    }

    [Fact]
    public void BuildSelectQuery_BooleanUserFilter_BindsBoolParameter()
    {
        var (_, parameters) = DynamicQueryBuilder.BuildSelectQuery(
            MakeTable("records_t"),
            [new ColumnDefinition("is_active", "BOOLEAN", "Checkbox", false)],
            [],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["is_active"] = "true",
            },
            page: 1, pageSize: 25);

        Assert.True(parameters.Get<bool>("filter_0"));
    }

    [Fact]
    public void BuildSelectQuery_TimestamptzFilter_MatchesByCalendarDate()
    {
        var (sql, parameters) = DynamicQueryBuilder.BuildSelectQuery(
            MakeTable("records_t"),
            [new ColumnDefinition("due_at", "TIMESTAMPTZ", "DateTimePicker", false)],
            [],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["due_at"] = "2026-05-28",
            },
            page: 1, pageSize: 25);

        // Equality on a TIMESTAMPTZ is never what the UI date-picker wants —
        // we bucket to UTC date so picking 2026-05-28 finds any row from that
        // day regardless of microsecond / timezone.
        Assert.Contains("(\"due_at\" AT TIME ZONE 'UTC')::date = @filter_0::date", sql, StringComparison.Ordinal);
        Assert.Equal("2026-05-28", parameters.Get<string>("filter_0"));
    }

    [Fact]
    public void BuildSelectQuery_TextFilter_EscapesLikeWildcards()
    {
        var (_, parameters) = DynamicQueryBuilder.BuildSelectQuery(
            MakeTable("orders"),
            [new ColumnDefinition("title", "TEXT", "TextInput", false)],
            [],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // User-supplied % _ \ must not be interpreted as LIKE wildcards.
                ["title"] = @"50% off_\done",
            },
            page: 1, pageSize: 25);

        Assert.Equal(@"%50\% off\_\\done%", parameters.Get<string>("filter_0"));
    }

    [Fact]
    public void BuildSelectQuery_UnknownColumn_FallsBackToTextIlike()
    {
        // pgName not in userColumns → defaults to TEXT (defence-in-depth; the
        // endpoint's whitelist should normally prevent this branch).
        var (sql, parameters) = DynamicQueryBuilder.BuildSelectQuery(
            MakeTable("orders"),
            [],
            [],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ghost"] = "x",
            },
            page: 1, pageSize: 25);

        Assert.Contains("\"ghost\" ILIKE @filter_0", sql, StringComparison.Ordinal);
        Assert.Equal("%x%", parameters.Get<string>("filter_0"));
    }

    [Fact]
    public void BuildSelectQuery_ParentFkFilter_FromTextTypedDropdown_ComparesAsUuid()
    {
        // A designer-backed dropdown that picks the parent record declares a
        // fieldKey like parent_villages_id and the registry types it TEXT
        // (Dropdown -> TEXT). The physical column is UUID (the auto-provisioned
        // parent FK). Filtering must compare as UUID (= @p, Guid param) — NOT
        // "uuid ILIKE @p", which is PG 42883 "operator does not exist: uuid ~~* text".
        var fk = Guid.NewGuid();
        var (sql, parameters) = DynamicQueryBuilder.BuildSelectQuery(
            MakeTable("houses"),
            [new ColumnDefinition("parent_villages_id", "TEXT", "Dropdown", false)],
            [],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["parent_villages_id"] = fk.ToString(),
            },
            page: 1, pageSize: 25);

        Assert.Contains("WHERE \"parent_villages_id\" = @filter_0", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ILIKE", sql, StringComparison.Ordinal);
        Assert.Equal(fk, parameters.Get<Guid>("filter_0"));
    }

    [Fact]
    public void BuildOptionsQuery_ParentFkCascadeFilter_ComparesAsUuid()
    {
        // The reported bug: GET /api/data/houses/options?filter[parent_villages_id]=<uuid>
        // (cascading dropdown) hit "uuid ~~* text". The options query routes through
        // the same WHERE builder, so the UUID comparison must hold here too.
        var fk = Guid.NewGuid();
        var (sql, parameters) = DynamicQueryBuilder.BuildOptionsQuery(
            MakeTable("houses"),
            "id",
            "title",
            [new ColumnDefinition("parent_villages_id", "TEXT", "Dropdown", false)],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["parent_villages_id"] = fk.ToString(),
            },
            search: null,
            page: 1, pageSize: 50);

        Assert.Contains("\"parent_villages_id\" = @filter_0", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ILIKE", sql, StringComparison.Ordinal);
        Assert.Equal(fk, parameters.Get<Guid>("filter_0"));
    }

    [Theory]
    [InlineData("parent_villages_id")]
    [InlineData("parent_a_id")]
    public void TryValidateFilterValue_ParentFkColumn_RejectsMalformedUuid(string pgName)
    {
        // Parent-FK filters resolve to UUID and are Guid.Parse'd in the builder, so a
        // malformed value must be caught here (clean 400) rather than throwing a 500.
        Assert.False(DynamicQueryBuilder.TryValidateFilterValue(pgName, "not-a-uuid", out var error));
        Assert.NotNull(error);

        Assert.True(DynamicQueryBuilder.TryValidateFilterValue(
            pgName, Guid.NewGuid().ToString(), out var ok));
        Assert.Null(ok);
    }

    [Fact]
    public void BuildSelectQuery_SystemAndUserColumns_AreDoubleQuotedInSelectList()
    {
        var (sql, _) = DynamicQueryBuilder.BuildSelectQuery(
            MakeTable("records_t"),
            [new ColumnDefinition("title", "TEXT", "TextInput", false),
             new ColumnDefinition("age", "NUMERIC", "NumberInput", false)],
            [],
            new Dictionary<string, string>(StringComparer.Ordinal),
            page: 1, pageSize: 25);

        // System columns
        Assert.Contains("\"id\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"created_at\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"is_deleted\"", sql, StringComparison.Ordinal);
        // User columns
        Assert.Contains("\"title\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"age\"", sql, StringComparison.Ordinal);
    }

    // ---- Story B-1-4-followup: derived (subquery-backed) table columns ----

    [Fact]
    public void BuildSelectQuery_DesignerDropdownDerivedColumn_EmitsCorrelatedLabelSubquery()
    {
        var (sql, _) = DynamicQueryBuilder.BuildSelectQuery(
            MakeTable("orders"),
            [new ColumnDefinition("factory", "UUID", "Dropdown", false)],
            [],
            new Dictionary<string, string>(StringComparer.Ordinal),
            page: 1, pageSize: 25,
            derivedColumns: [new DerivedColumn(
                DerivedColumn.DropdownLabelKind, "factory_name", "factory", "name", "factory")]);

        Assert.Contains(
            "(SELECT \"name\" FROM \"factory\" WHERE \"id\" = \"orders\".\"factory\" " +
            "AND \"is_deleted\" = false LIMIT 1) AS \"factory_name\"",
            sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_DatasetDropdownDerivedColumn_EmitsCorrelatedViewLabelSubquery()
    {
        var (sql, _) = DynamicQueryBuilder.BuildSelectQuery(
            MakeTable("orders"),
            [new ColumnDefinition("user_id", "uuid", "Dropdown", false)],
            [],
            new Dictionary<string, string>(StringComparer.Ordinal),
            page: 1, pageSize: 25,
            derivedColumns: [new DerivedColumn(
                DerivedColumn.DatasetDropdownLabelKind, "app_users_users_display_name",
                "app_users", "users_display_name", "user_id", "users_id")]);

        // Reads from the datasets schema VIEW, joins on valueField (not "id"), compares
        // via ::text, and has no is_deleted filter (a VIEW has no such column).
        Assert.Contains(
            "(SELECT \"users_display_name\" FROM datasets.\"app_users\" " +
            "WHERE \"users_id\"::text = \"orders\".\"user_id\"::text " +
            "LIMIT 1) AS \"app_users_users_display_name\"",
            sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_RepeaterDerivedColumn_EmitsCorrelatedCountSubquery()
    {
        var (sql, _) = DynamicQueryBuilder.BuildSelectQuery(
            MakeTable("orders"),
            [],
            [],
            new Dictionary<string, string>(StringComparer.Ordinal),
            page: 1, pageSize: 25,
            derivedColumns: [new DerivedColumn(
                DerivedColumn.RepeaterCountKind, "line_item_row_count", "line_item", null, null)]);

        Assert.Contains(
            "(SELECT COUNT(*) FROM \"line_item\" WHERE \"parent_orders_id\" = \"orders\".\"id\" " +
            "AND \"is_deleted\" = false) AS \"line_item_row_count\"",
            sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_NoDerivedColumns_OmitsSubqueries()
    {
        var (sql, _) = DynamicQueryBuilder.BuildSelectQuery(
            MakeTable("orders"),
            [new ColumnDefinition("title", "TEXT", "TextInput", false)],
            [],
            new Dictionary<string, string>(StringComparer.Ordinal),
            page: 1, pageSize: 25);

        Assert.DoesNotContain("(SELECT", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildExportQuery_WithDerivedColumns_IncludesSubqueriesAndNoLimit()
    {
        var (sql, _) = DynamicQueryBuilder.BuildExportQuery(
            MakeTable("orders"),
            [],
            [],
            new Dictionary<string, string>(StringComparer.Ordinal),
            derivedColumns: [new DerivedColumn(
                DerivedColumn.RepeaterCountKind, "line_item_row_count", "line_item", null, null)]);

        Assert.Contains("AS \"line_item_row_count\"", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("LIMIT", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_IsDeletedFilter_CoercesToBoolean()
    {
        var (_, parameters) = DynamicQueryBuilder.BuildSelectQuery(
            MakeTable("records_t"),
            [],
            [],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["is_deleted"] = "false",
            },
            page: 1, pageSize: 25);

        Assert.False(parameters.Get<bool>("filter_0"));
    }

    [Fact]
    public void BuildCountQuery_WithFilter_HasNoLimitOffsetOrOrderBy()
    {
        var (sql, parameters) = DynamicQueryBuilder.BuildCountQuery(
            MakeTable("records_t"),
            [new ColumnDefinition("title", "TEXT", "TextInput", false)],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["title"] = "foo",
            });

        Assert.Contains("SELECT COUNT(*) FROM \"records_t\"", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE \"title\" ILIKE @filter_0", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDER BY", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("LIMIT", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("OFFSET", sql, StringComparison.Ordinal);
        Assert.Equal("%foo%", parameters.Get<string>("filter_0"));
    }

    [Fact]
    public void BuildCountQuery_NoFilter_ProducesUnconstrainedCount()
    {
        var (sql, _) = DynamicQueryBuilder.BuildCountQuery(
            MakeTable("records_t"),
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Equal("SELECT COUNT(*) FROM \"records_t\"", sql);
    }

    [Theory]
    [InlineData("NUMERIC", "abc", false)]
    [InlineData("NUMERIC", "12.34", true)]
    [InlineData("BOOLEAN", "yes", true)]
    [InlineData("BOOLEAN", "maybe", false)]
    [InlineData("TIMESTAMPTZ", "2026-05-28", true)]
    [InlineData("TIMESTAMPTZ", "not-a-date", false)]
    [InlineData("TEXT", "anything", true)]
    public void TryValidateUserFilterValue_Cases(string pgType, string rawValue, bool expected)
    {
        var ok = DynamicQueryBuilder.TryValidateUserFilterValue(pgType, rawValue, out var error);
        Assert.Equal(expected, ok);
        if (expected)
            Assert.Null(error);
        else
            Assert.NotNull(error);
    }

    // ---------- Story 7-followup: Export ----------

    [Fact]
    public void BuildExportQuery_PreservesWhereAndOrderBy_WithoutLimitOrOffset()
    {
        var (sql, parameters) = DynamicQueryBuilder.BuildExportQuery(
            MakeTable("orders"),
            [new ColumnDefinition("title", "TEXT", "TextInput", false)],
            [new DynamicQueryBuilder.SortParam("title", "ASC")],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["title"] = "foo",
            });

        Assert.Contains("FROM \"orders\"", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE \"title\" ILIKE @filter_0", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY \"title\" ASC", sql, StringComparison.Ordinal);
        // Export queries are NOT paginated — the caller bounds the result via
        // BuildCountQuery + a cap, then streams the rest.
        Assert.DoesNotContain("LIMIT", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("OFFSET", sql, StringComparison.Ordinal);
        Assert.Equal("%foo%", parameters.Get<string>("filter_0"));
    }

    [Fact]
    public void BuildExportQuery_NoFilterNoSort_AppliesDefaultOrderByCreatedAtDesc()
    {
        var (sql, _) = DynamicQueryBuilder.BuildExportQuery(
            MakeTable("orders"),
            [new ColumnDefinition("title", "TEXT", "TextInput", false)],
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.DoesNotContain(" WHERE ", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY \"created_at\" DESC", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("LIMIT", sql, StringComparison.Ordinal);
    }

    // ---------- Story 6.2 ----------

    [Fact]
    public void BuildGetByIdQuery_GeneratesSelectWhereId()
    {
        var (sql, _) = DynamicQueryBuilder.BuildGetByIdQuery(
            MakeTable("records_t"),
            [new ColumnDefinition("title", "TEXT", "TextInput", false)],
            Guid.NewGuid());

        Assert.Contains("FROM \"records_t\"", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE \"id\" = @p_id", sql, StringComparison.Ordinal);
        // System columns are included verbatim alongside the user column.
        Assert.Contains("\"id\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"created_at\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"is_deleted\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"title\"", sql, StringComparison.Ordinal);
        // Single-row read — no pagination / ordering applies.
        Assert.DoesNotContain("ORDER BY", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("LIMIT", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("OFFSET", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGetByIdQuery_ParameterContainsId()
    {
        var id = Guid.NewGuid();
        var (_, parameters) = DynamicQueryBuilder.BuildGetByIdQuery(
            MakeTable("records_t"),
            [],
            id);

        Assert.Equal(id, parameters.Get<Guid>("p_id"));
    }

    [Fact]
    public void BuildGetChildrenQuery_GeneratesWhereOnFkColumn()
    {
        var parentId = Guid.NewGuid();
        var (sql, parameters) = DynamicQueryBuilder.BuildGetChildrenQuery(
            MakeTable("child_notes_t"),
            [new ColumnDefinition("note", "TEXT", "TextInput", false)],
            fkColumnName: "parent_my_table_id",
            parentId);

        Assert.Contains("FROM \"child_notes_t\"", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE \"parent_my_table_id\" = @p_parent_id", sql, StringComparison.Ordinal);
        Assert.Contains("\"note\"", sql, StringComparison.Ordinal);
        Assert.Equal(parentId, parameters.Get<Guid>("p_parent_id"));
    }

    [Fact]
    public void BuildFkColumnName_ShortName_ReturnsFull()
    {
        // "my_table" (8 chars) → "parent_my_table_id" (18 chars).
        Assert.Equal("parent_my_table_id", DynamicQueryBuilder.BuildFkColumnName("my_table"));
    }

    [Fact]
    public void BuildFkColumnName_LongName_CapsAt63TotalChars()
    {
        // 60-char name exceeds the 53-char cap — result truncates name to 53 chars,
        // total length stays at PostgreSQL's 63-char identifier limit (7 + 53 + 3).
        var longName = new string('a', 60);
        var result = DynamicQueryBuilder.BuildFkColumnName(longName);
        Assert.Equal(63, result.Length);
        Assert.Equal("parent_" + new string('a', 53) + "_id", result);
    }

    // ---------- Reserved parent-FK column (parent_<designerId>_id) ----------

    [Theory]
    [InlineData("parent_villages_id", true)]
    [InlineData("parent_my_table_id", true)]
    [InlineData("parent_a_id", true)]            // single-char designerId
    [InlineData("parent__id", false)]            // empty designerId — not a real FK
    [InlineData("parent_id", false)]             // no inner name segment
    [InlineData("villages_id", false)]           // missing "parent_" prefix
    [InlineData("parent_villages", false)]       // missing "_id" suffix
    [InlineData("parents_id", false)]            // "parents_" is not the "parent_" prefix
    [InlineData("title", false)]
    public void IsReservedParentFkColumn_MatchesOnlyTheFkNamingConvention(string columnName, bool expected)
    {
        Assert.Equal(expected, DynamicQueryBuilder.IsReservedParentFkColumn(columnName));
    }

    [Fact]
    public void IsReservedParentFkColumn_MatchesBuildFkColumnNameOutput()
    {
        // The predicate must accept whatever BuildFkColumnName emits — including the
        // truncated long-name form — so the two can never drift apart.
        Assert.True(DynamicQueryBuilder.IsReservedParentFkColumn(
            DynamicQueryBuilder.BuildFkColumnName("my_table")));
        Assert.True(DynamicQueryBuilder.IsReservedParentFkColumn(
            DynamicQueryBuilder.BuildFkColumnName(new string('a', 60))));
    }

    [Fact]
    public void TryCoerceReservedParentFkColumns_ParsesFkStringToGuid_KeepsTheRest()
    {
        var fk = Guid.NewGuid();
        var input = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["title"] = "house 1",
            ["parent_villages_id"] = fk.ToString(), // coerced as TEXT by the validator
        };

        var ok = DynamicQueryBuilder.TryCoerceReservedParentFkColumns(
            input, out var result, out var invalidColumn);

        Assert.True(ok);
        Assert.Null(invalidColumn);
        // The FK value now binds as a real Guid (not text) so PG accepts the UUID column.
        Assert.IsType<Guid>(result["parent_villages_id"]);
        Assert.Equal(fk, (Guid)result["parent_villages_id"]!);
        Assert.Equal("house 1", result["title"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryCoerceReservedParentFkColumns_NullOrEmptyFk_BecomesSqlNull(string? raw)
    {
        var input = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["parent_villages_id"] = raw, // cleared dropdown selection
        };

        var ok = DynamicQueryBuilder.TryCoerceReservedParentFkColumns(
            input, out var result, out var invalidColumn);

        Assert.True(ok);
        Assert.Null(invalidColumn);
        Assert.True(result.ContainsKey("parent_villages_id"));
        Assert.Null(result["parent_villages_id"]);
    }

    [Fact]
    public void TryCoerceReservedParentFkColumns_MalformedFk_FailsWithColumnName()
    {
        var input = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["parent_villages_id"] = "not-a-uuid",
        };

        var ok = DynamicQueryBuilder.TryCoerceReservedParentFkColumns(
            input, out var result, out var invalidColumn);

        Assert.False(ok);
        Assert.Equal("parent_villages_id", invalidColumn);
        Assert.Same(input, result);
    }

    [Fact]
    public void TryCoerceReservedParentFkColumns_NoFkColumn_ReturnsSameInstance()
    {
        // Common path allocates nothing — the original dictionary is returned as-is.
        var input = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["title"] = "house 1",
            ["village_name"] = "Springfield",
        };

        var ok = DynamicQueryBuilder.TryCoerceReservedParentFkColumns(
            input, out var result, out var invalidColumn);

        Assert.True(ok);
        Assert.Null(invalidColumn);
        Assert.Same(input, result);
    }

    // ---------- Story 6.3 ----------

    [Fact]
    public void BuildInsertQuery_NoPayload_GeneratesInsertWithSystemColumnsOnly()
    {
        var (sql, parameters, _, _) = DynamicQueryBuilder.BuildInsertQuery(
            MakeTable("records_t"),
            [new ColumnDefinition("title", "TEXT", "TextInput", false)],
            new Dictionary<string, object?>(StringComparer.Ordinal),
            actorId: Guid.NewGuid());

        Assert.Contains("INSERT INTO \"records_t\"", sql, StringComparison.Ordinal);
        // System columns appear in the column list.
        Assert.Contains("\"id\", \"created_at\", \"created_by\", \"updated_at\", \"updated_by\", \"is_deleted\", \"cascade_event_id\"",
            sql, StringComparison.Ordinal);
        // is_deleted defaults to literal false; cascade_event_id is literal NULL.
        Assert.Contains("false, NULL", sql, StringComparison.Ordinal);
        // No user column ⇒ no @f_title parameter.
        var paramNames = parameters.ParameterNames.ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("f_title", paramNames);
        // Empty payload means VALUES list ends right after the cascade_event_id NULL literal.
        Assert.EndsWith("false, NULL)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInsertQuery_WithOneUserColumn_IncludesColumnAndParameter()
    {
        var (sql, parameters, _, _) = DynamicQueryBuilder.BuildInsertQuery(
            MakeTable("records_t"),
            [new ColumnDefinition("title", "TEXT", "TextInput", false)],
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["title"] = "hello",
            },
            actorId: null);

        Assert.Contains(", \"title\")", sql, StringComparison.Ordinal);
        Assert.Contains(", @f_title)", sql, StringComparison.Ordinal);
        Assert.Equal("hello", parameters.Get<string>("f_title"));
    }

    [Fact]
    public void BuildInsertQuery_SystemColumnsInPayloadAreIgnored()
    {
        // Payload mimics what the validator produces — system column names are
        // NEVER added to CoercedValues (the validator iterates over user
        // ColumnDefinitions only). Verify the builder does not emit @f_id /
        // @f_created_at even if hostile callers pass them in.
        var (_, parameters, _, _) = DynamicQueryBuilder.BuildInsertQuery(
            MakeTable("records_t"),
            [new ColumnDefinition("title", "TEXT", "TextInput", false)],
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["title"] = "ok",
            },
            actorId: null);

        var paramNames = parameters.ParameterNames.ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("f_id", paramNames);
        Assert.DoesNotContain("f_created_at", paramNames);
        Assert.DoesNotContain("f_is_deleted", paramNames);
        Assert.Contains("f_title", paramNames);
    }

    [Fact]
    public void BuildInsertQuery_ReturnsNewGuidAndTimestamp()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var (_, _, newId, insertedAt) = DynamicQueryBuilder.BuildInsertQuery(
            MakeTable("records_t"),
            [],
            new Dictionary<string, object?>(StringComparer.Ordinal),
            actorId: null);
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        Assert.NotEqual(Guid.Empty, newId);
        Assert.InRange(insertedAt, before, after);
    }

    [Fact]
    public void BuildInsertQuery_ActorIdNullable_IncludesNullParameter()
    {
        var (_, parameters, _, _) = DynamicQueryBuilder.BuildInsertQuery(
            MakeTable("records_t"),
            [],
            new Dictionary<string, object?>(StringComparer.Ordinal),
            actorId: null);

        Assert.Null(parameters.Get<Guid?>("p_created_by"));
        Assert.Null(parameters.Get<Guid?>("p_updated_by"));
    }

    // ---------- Story 6.4 ----------

    [Fact]
    public void BuildUpdateQuery_EmptyPayload_SetsOnlyUpdatedAtAndUpdatedBy()
    {
        var (sql, parameters, _) = DynamicQueryBuilder.BuildUpdateQuery(
            MakeTable("records_t"),
            [new ColumnDefinition("title", "TEXT", "TextInput", false)],
            new Dictionary<string, object?>(StringComparer.Ordinal),
            id: Guid.NewGuid(),
            actorId: Guid.NewGuid());

        Assert.Contains("UPDATE \"records_t\" SET", sql, StringComparison.Ordinal);
        Assert.Contains("\"updated_at\" = @p_updated_at", sql, StringComparison.Ordinal);
        Assert.Contains("\"updated_by\" = @p_updated_by", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE \"id\" = @p_id", sql, StringComparison.Ordinal);

        var paramNames = parameters.ParameterNames.ToHashSet(StringComparer.Ordinal);
        Assert.Contains("p_updated_at", paramNames);
        Assert.Contains("p_updated_by", paramNames);
        Assert.Contains("p_id", paramNames);
        // No user column ⇒ no @f_ parameters.
        Assert.DoesNotContain(paramNames, n => n.StartsWith("f_", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildUpdateQuery_WithOneUserColumn_IncludesColumnInSetClause()
    {
        var (sql, parameters, _) = DynamicQueryBuilder.BuildUpdateQuery(
            MakeTable("records_t"),
            [new ColumnDefinition("title", "TEXT", "TextInput", false)],
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["title"] = "hello",
            },
            id: Guid.NewGuid(),
            actorId: null);

        Assert.Contains(", \"title\" = @f_title", sql, StringComparison.Ordinal);
        Assert.Equal("hello", parameters.Get<string>("f_title"));
    }

    [Fact]
    public void BuildUpdateQuery_MultipleUserColumns_AllInSetClause()
    {
        // Order in the SET clause follows the ColumnDefinition list, not the
        // payload dict's iteration order.
        var (sql, parameters, _) = DynamicQueryBuilder.BuildUpdateQuery(
            MakeTable("records_t"),
            [
                new ColumnDefinition("title", "TEXT", "TextInput", false),
                new ColumnDefinition("price", "NUMERIC", "NumberInput", false),
            ],
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["price"] = 9.99m,
                ["title"] = "hi",
            },
            id: Guid.NewGuid(),
            actorId: null);

        var titleIdx = sql.IndexOf("\"title\"", StringComparison.Ordinal);
        var priceIdx = sql.IndexOf("\"price\"", StringComparison.Ordinal);
        Assert.True(titleIdx > 0);
        Assert.True(priceIdx > titleIdx,
            "ColumnDefinition order (title before price) must drive the SET clause order.");
        Assert.Equal("hi", parameters.Get<string>("f_title"));
        Assert.Equal(9.99m, parameters.Get<decimal>("f_price"));
    }

    [Fact]
    public void BuildUpdateQuery_SystemColumnsInPayloadNotInSetClause()
    {
        // Pass system column names directly in coercedPayload alongside a user column.
        // The builder filters by userColumns (ColumnDefinition list), so system keys
        // present in the dict must never appear in the SET clause or parameter list.
        var (sql, parameters, _) = DynamicQueryBuilder.BuildUpdateQuery(
            MakeTable("records_t"),
            [new ColumnDefinition("title", "TEXT", "TextInput", false)],
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["title"]            = "ok",
                ["id"]               = Guid.NewGuid(),
                ["created_at"]       = DateTimeOffset.UtcNow,
                ["created_by"]       = Guid.NewGuid(),
                ["is_deleted"]       = false,
                ["cascade_event_id"] = (object?)null,
            },
            id: Guid.NewGuid(),
            actorId: null);

        var paramNames = parameters.ParameterNames.ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("f_id", paramNames);
        Assert.DoesNotContain("f_created_at", paramNames);
        Assert.DoesNotContain("f_created_by", paramNames);
        Assert.DoesNotContain("f_is_deleted", paramNames);
        Assert.DoesNotContain("f_cascade_event_id", paramNames);
        // updated_at / updated_by are server-side (no @f_ prefix).
        Assert.DoesNotContain("f_updated_at", paramNames);
        Assert.DoesNotContain("f_updated_by", paramNames);
        Assert.Contains("f_title", paramNames);
        Assert.DoesNotContain(", \"id\" =", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUpdateQuery_ReturnsUpdatedAtTimestamp()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var (_, _, updatedAt) = DynamicQueryBuilder.BuildUpdateQuery(
            MakeTable("records_t"),
            [],
            new Dictionary<string, object?>(StringComparer.Ordinal),
            id: Guid.NewGuid(),
            actorId: null);
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        Assert.NotEqual(default, updatedAt);
        Assert.InRange(updatedAt, before, after);
    }

    // ---------- Story 6.5 ----------

    [Fact]
    public void BuildSoftDeleteByIdQuery_NoCascadeEventId_SetsCascadeEventIdToNull()
    {
        var (sql, parameters, _) = DynamicQueryBuilder.BuildSoftDeleteByIdQuery(
            MakeTable("records_t"),
            id: Guid.NewGuid(),
            actorId: Guid.NewGuid(),
            cascadeEventId: null);

        // SQL NULL literal (not a parameter), same pattern as BuildInsertQuery.
        Assert.Contains("\"cascade_event_id\" = NULL", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@p_cascade_event_id", sql, StringComparison.Ordinal);

        var paramNames = parameters.ParameterNames.ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("p_cascade_event_id", paramNames);
    }

    [Fact]
    public void BuildSoftDeleteByIdQuery_WithCascadeEventId_IncludesParam()
    {
        var cascadeId = Guid.NewGuid();
        var (sql, parameters, _) = DynamicQueryBuilder.BuildSoftDeleteByIdQuery(
            MakeTable("records_t"),
            id: Guid.NewGuid(),
            actorId: null,
            cascadeEventId: cascadeId);

        Assert.Contains("\"cascade_event_id\" = @p_cascade_event_id", sql, StringComparison.Ordinal);
        Assert.Equal(cascadeId, parameters.Get<Guid>("p_cascade_event_id"));
    }

    [Fact]
    public void BuildSoftDeleteByIdQuery_WhereClauseIncludesIsDeletedFalse()
    {
        var recordId = Guid.NewGuid();
        var (sql, parameters, _) = DynamicQueryBuilder.BuildSoftDeleteByIdQuery(
            MakeTable("records_t"),
            id: recordId,
            actorId: null,
            cascadeEventId: null);

        Assert.Contains("WHERE \"id\" = @p_id AND \"is_deleted\" = false", sql, StringComparison.Ordinal);
        // The SET clause flips is_deleted to true; the WHERE clause guards against
        // double-delete.
        Assert.Contains("SET \"is_deleted\" = true", sql, StringComparison.Ordinal);
        Assert.Equal(recordId, parameters.Get<Guid>("p_id"));
    }

    [Fact]
    public void BuildSelectChildIdsByFkQuery_BuiltCorrectly()
    {
        var parentId = Guid.NewGuid();
        var (sql, parameters) = DynamicQueryBuilder.BuildSelectChildIdsByFkQuery(
            MakeTable("child_notes_t"),
            fkColumnName: "parent_my_table_id",
            parentId);

        Assert.Contains("SELECT \"id\" FROM \"child_notes_t\"", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE \"parent_my_table_id\" = @p_parent_id AND \"is_deleted\" = false",
            sql, StringComparison.Ordinal);
        Assert.Equal(parentId, parameters.Get<Guid>("p_parent_id"));
    }

    [Fact]
    public void BuildCascadeChildSoftDeleteQuery_SetsAllSystemColumns()
    {
        var cascadeId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var ts = DateTimeOffset.UtcNow;

        var (sql, parameters) = DynamicQueryBuilder.BuildCascadeChildSoftDeleteQuery(
            MakeTable("child_notes_t"),
            fkColumnName: "parent_my_table_id",
            parentId: parentId,
            actorId: actor,
            cascadeEventId: cascadeId,
            updatedAt: ts);

        Assert.Contains("UPDATE \"child_notes_t\" SET", sql, StringComparison.Ordinal);
        Assert.Contains("\"is_deleted\" = true", sql, StringComparison.Ordinal);
        Assert.Contains("\"updated_at\" = @p_updated_at", sql, StringComparison.Ordinal);
        Assert.Contains("\"updated_by\" = @p_updated_by", sql, StringComparison.Ordinal);
        Assert.Contains("\"cascade_event_id\" = @p_cascade_event_id", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE \"parent_my_table_id\" = @p_parent_id AND \"is_deleted\" = false",
            sql, StringComparison.Ordinal);

        Assert.Equal(ts,        parameters.Get<DateTimeOffset>("p_updated_at"));
        Assert.Equal(actor,     parameters.Get<Guid>("p_updated_by"));
        Assert.Equal(cascadeId, parameters.Get<Guid>("p_cascade_event_id"));
        Assert.Equal(parentId,  parameters.Get<Guid>("p_parent_id"));
    }

    // ---------- Story 6.6 ----------

    [Fact]
    public void BuildRestoreByIdQuery_SetsIsDeletedFalse_ClearsSystemColumns()
    {
        var recordId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var (sql, parameters, _) = DynamicQueryBuilder.BuildRestoreByIdQuery(
            MakeTable("records_t"),
            id: recordId,
            actorId: actor);

        // SET clause flips is_deleted to false (the inverse of soft-delete).
        Assert.Contains("UPDATE \"records_t\" SET", sql, StringComparison.Ordinal);
        Assert.Contains("\"is_deleted\" = false", sql, StringComparison.Ordinal);
        Assert.Contains("\"updated_at\" = @p_updated_at", sql, StringComparison.Ordinal);
        Assert.Contains("\"updated_by\" = @p_updated_by", sql, StringComparison.Ordinal);
        // cascade_event_id is unconditionally cleared on restore (Decision 1.3) —
        // SQL NULL literal (not a parameter), same pattern as BuildSoftDeleteByIdQuery.
        Assert.Contains("\"cascade_event_id\" = NULL", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@p_cascade_event_id", sql, StringComparison.Ordinal);
        // WHERE is_deleted = true so a concurrent restore between SELECT and UPDATE
        // returns 0 rows affected → caller surfaces 422 RECORD_NOT_DELETED.
        Assert.Contains("WHERE \"id\" = @p_id AND \"is_deleted\" = true",
            sql, StringComparison.Ordinal);

        var paramNames = parameters.ParameterNames.ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("p_cascade_event_id", paramNames);
        Assert.Equal(recordId, parameters.Get<Guid>("p_id"));
        Assert.Equal(actor,    parameters.Get<Guid>("p_updated_by"));
    }

    [Fact]
    public void BuildRestoreByIdQuery_ReturnsUpdatedAtTimestamp()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var (_, _, updatedAt) = DynamicQueryBuilder.BuildRestoreByIdQuery(
            MakeTable("records_t"),
            id: Guid.NewGuid(),
            actorId: null);
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        Assert.NotEqual(default, updatedAt);
        Assert.InRange(updatedAt, before, after);
    }

    // ---------- Story 6.7 ----------

    [Fact]
    public void BuildChildInsertQuery_IncludesFkColumn_WithCorrectParam()
    {
        var parentId  = Guid.NewGuid();
        var actorId   = Guid.NewGuid();
        var columns   = new List<ColumnDefinition>
        {
            new("note", "TEXT", "TextInput", false),
        };
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal) { ["note"] = "hello" };

        var (sql, parameters, newId, _) = DynamicQueryBuilder.BuildChildInsertQuery(
            MakeTable("child_tbl"),
            columns,
            payload,
            fkColumnName: "parent_parent_tbl_id",
            parentId: parentId,
            actorId: actorId);

        Assert.Contains("INSERT INTO \"child_tbl\"", sql, StringComparison.Ordinal);
        Assert.Contains(
            "\"id\", \"created_at\", \"created_by\", \"updated_at\", \"updated_by\", \"is_deleted\", \"cascade_event_id\"",
            sql, StringComparison.Ordinal);
        // FK column appears immediately after cascade_event_id in the column list.
        Assert.Contains("\"cascade_event_id\", \"parent_parent_tbl_id\"", sql, StringComparison.Ordinal);
        // VALUES list: cascade_event_id is the SQL NULL literal followed by the FK param.
        Assert.Contains("false, NULL, @p_fk_parent_id", sql, StringComparison.Ordinal);
        // User column + parameter present.
        Assert.Contains("\"note\"",   sql, StringComparison.Ordinal);
        Assert.Contains("@f_note",    sql, StringComparison.Ordinal);

        Assert.Equal(parentId,  parameters.Get<Guid>("p_fk_parent_id"));
        Assert.Equal("hello",   parameters.Get<string>("f_note"));
        Assert.NotEqual(Guid.Empty, newId);
    }

    [Fact]
    public void BuildChildInsertQuery_EmptyPayload_OnlySystemAndFkColumns()
    {
        var parentId = Guid.NewGuid();
        var (sql, parameters, newId, _) = DynamicQueryBuilder.BuildChildInsertQuery(
            MakeTable("child_empty"),
            childUserColumns: new List<ColumnDefinition>(),
            coercedPayload: new Dictionary<string, object?>(StringComparer.Ordinal),
            fkColumnName: "parent_x_id",
            parentId: parentId,
            actorId: null);

        // Only system + FK columns — no user column params.
        Assert.DoesNotContain("@f_", sql, StringComparison.Ordinal);
        Assert.Contains("@p_fk_parent_id", sql, StringComparison.Ordinal);
        // VALUES list ends exactly after the FK parameter when the payload is empty.
        Assert.EndsWith("@p_fk_parent_id)", sql, StringComparison.Ordinal);
        Assert.Equal(parentId, parameters.Get<Guid>("p_fk_parent_id"));
        Assert.Null(parameters.Get<Guid?>("p_created_by"));
        Assert.NotEqual(Guid.Empty, newId);
    }

    [Fact]
    public void BuildRestoreCascadeChildrenQuery_MatchesCascadeEventId()
    {
        var cascadeId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var ts = DateTimeOffset.UtcNow;

        var (sql, parameters) = DynamicQueryBuilder.BuildRestoreCascadeChildrenQuery(
            MakeTable("child_notes_t"),
            parentCascadeEventId: cascadeId,
            actorId: actor,
            updatedAt: ts);

        Assert.Contains("UPDATE \"child_notes_t\" SET", sql, StringComparison.Ordinal);
        Assert.Contains("\"is_deleted\" = false", sql, StringComparison.Ordinal);
        Assert.Contains("\"updated_at\" = @p_updated_at", sql, StringComparison.Ordinal);
        Assert.Contains("\"updated_by\" = @p_updated_by", sql, StringComparison.Ordinal);
        // cascade_event_id is cleared on restore (Decision 1.3).
        Assert.Contains("\"cascade_event_id\" = NULL", sql, StringComparison.Ordinal);
        // WHERE matches by cascade_event_id (not by FK), and excludes rows that
        // were not soft-deleted (defensive — should never match in practice).
        Assert.Contains(
            "WHERE \"cascade_event_id\" = @p_cascade_event_id AND \"is_deleted\" = true",
            sql, StringComparison.Ordinal);

        Assert.Equal(ts,        parameters.Get<DateTimeOffset>("p_updated_at"));
        Assert.Equal(actor,     parameters.Get<Guid>("p_updated_by"));
        Assert.Equal(cascadeId, parameters.Get<Guid>("p_cascade_event_id"));
    }

    // ---- TreeView: BuildTreeLevelQuery ---------------------------------------

    [Fact]
    public void BuildTreeLevelQuery_RootLevel_FiltersParentIsNull_AndEmitsHasChildrenFlag()
    {
        var (sql, parameters) = DynamicQueryBuilder.BuildTreeLevelQuery(
            MakeTable("org_unit"),
            [new ColumnDefinition("name", "TEXT", "TextInput", false)],
            fkColumnName: "parent_org_unit_id",
            parentId: null,
            search: null,
            limit: 26,
            offset: 0);

        Assert.Contains("FROM \"org_unit\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"is_deleted\" = false", sql, StringComparison.Ordinal);
        // Root nodes have a NULL self-FK.
        Assert.Contains("\"parent_org_unit_id\" IS NULL", sql, StringComparison.Ordinal);
        // Derived per-row flag drives the expand affordance.
        Assert.Contains("AS \"_has_children\"", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY \"created_at\" ASC", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT @p_limit OFFSET @p_offset", sql, StringComparison.Ordinal);

        var paramNames = parameters.ParameterNames.ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("p_parent_id", paramNames);   // null parent → no bound param
        Assert.Equal(26, parameters.Get<int>("p_limit"));
        Assert.Equal(0L, parameters.Get<long>("p_offset"));
    }

    [Fact]
    public void BuildTreeLevelQuery_WithParent_FiltersByFk()
    {
        var parent = Guid.NewGuid();
        var (sql, parameters) = DynamicQueryBuilder.BuildTreeLevelQuery(
            MakeTable("org_unit"),
            [new ColumnDefinition("name", "TEXT", "TextInput", false)],
            fkColumnName: "parent_org_unit_id",
            parentId: parent,
            search: null,
            limit: 11,
            offset: 10);

        Assert.Contains("\"parent_org_unit_id\" = @p_parent_id", sql, StringComparison.Ordinal);
        Assert.Equal(parent, parameters.Get<Guid>("p_parent_id"));
        Assert.Equal(10L, parameters.Get<long>("p_offset"));
    }

    [Fact]
    public void BuildTreeLevelQuery_WithSearch_BuildsOrIlikeAcrossUserColumns()
    {
        var (sql, parameters) = DynamicQueryBuilder.BuildTreeLevelQuery(
            MakeTable("org_unit"),
            [
                new ColumnDefinition("name", "TEXT", "TextInput", false),
                new ColumnDefinition("code", "TEXT", "TextInput", false),
            ],
            fkColumnName: "parent_org_unit_id",
            parentId: null,
            search: "foo",
            limit: 26,
            offset: 0);

        Assert.Contains(
            "(\"name\"::text ILIKE @p_search OR \"code\"::text ILIKE @p_search)",
            sql, StringComparison.Ordinal);
        Assert.Equal("%foo%", parameters.Get<string>("p_search"));
    }

    [Fact]
    public void BuildTreeDescendantIdsQuery_RecursesViaSelfFk_FiltersLiveRows()
    {
        var parent = Guid.NewGuid();
        var (sql, parameters) = DynamicQueryBuilder.BuildTreeDescendantIdsQuery(
            MakeTable("org_unit"), "parent_org_unit_id", parent);

        Assert.Contains("WITH RECURSIVE descendants AS", sql, StringComparison.Ordinal);
        // Anchor: direct children of the parent.
        Assert.Contains("\"parent_org_unit_id\" = @p_parent_id", sql, StringComparison.Ordinal);
        // Recursive term joins children back onto the working set via the self-FK.
        Assert.Contains("JOIN descendants d ON c.\"parent_org_unit_id\" = d.\"id\"", sql, StringComparison.Ordinal);
        // UNION (not UNION ALL) dedups by id so cyclic/corrupt chains still terminate.
        Assert.Contains("UNION\n", sql.Replace("\r", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("UNION ALL", sql, StringComparison.Ordinal);
        // Live rows only, at both levels.
        Assert.Equal(2, CountOccurrences(sql, "\"is_deleted\" = false"));
        Assert.Equal(parent, parameters.Get<Guid>("p_parent_id"));
    }

    [Fact]
    public void BuildTreeLevelQuery_WithOwnerFilter_ScopesToOwner()
    {
        var owner = Guid.NewGuid();
        var (sql, parameters) = DynamicQueryBuilder.BuildTreeLevelQuery(
            MakeTable("org_unit"),
            [new ColumnDefinition("name", "TEXT", "TextInput", false)],
            fkColumnName: "parent_org_unit_id",
            parentId: null,
            search: null,
            limit: 26,
            offset: 0,
            ownerColumn: "owner_id",
            ownerId: owner);

        Assert.Contains("\"owner_id\" = @p_owner_id", sql, StringComparison.Ordinal);
        Assert.Equal(owner, parameters.Get<Guid>("p_owner_id"));
    }

    [Fact]
    public void BuildTreeLevelQuery_WithoutOwnerFilter_HasNoOwnerPredicate()
    {
        var (sql, parameters) = DynamicQueryBuilder.BuildTreeLevelQuery(
            MakeTable("org_unit"),
            [new ColumnDefinition("name", "TEXT", "TextInput", false)],
            "parent_org_unit_id", null, null, 26, 0);

        Assert.DoesNotContain("@p_owner_id", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("p_owner_id", parameters.ParameterNames);
    }

    [Fact]
    public void BuildTreeDescendantIdsQuery_WithOwnerFilter_ScopesBothCteTerms()
    {
        var owner = Guid.NewGuid();
        var (sql, parameters) = DynamicQueryBuilder.BuildTreeDescendantIdsQuery(
            MakeTable("org_unit"), "parent_org_unit_id", Guid.NewGuid(),
            ownerColumn: "owner_id", ownerId: owner);

        // Anchor term filters the parent's direct children by owner…
        Assert.Contains("\"owner_id\" = @p_owner_id", sql, StringComparison.Ordinal);
        // …and the recursive term filters joined children by owner too.
        Assert.Contains("c.\"owner_id\" = @p_owner_id", sql, StringComparison.Ordinal);
        Assert.Equal(owner, parameters.Get<Guid>("p_owner_id"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) != -1) { count++; i += needle.Length; }
        return count;
    }
}
