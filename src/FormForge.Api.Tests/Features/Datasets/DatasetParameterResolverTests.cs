using FormForge.Api.Features.Datasets;

namespace FormForge.Api.Tests.Features.Datasets;

// Parameterized-query feature — pure unit coverage for placeholder extraction and binding.
// Calls DatasetParameterResolver directly (InternalsVisibleTo) — no Testcontainers, no DB.
public sealed class DatasetParameterResolverTests
{
    // ── ExtractPlaceholders ───────────────────────────────────────────────

    [Fact]
    public void ExtractPlaceholders_BareAndQuoted_ReturnsDistinctInOrder()
    {
        const string sql =
            "select s.name from students s where s.age > {_age} AND s.id='{_student_id}'";

        var names = DatasetParameterResolver.ExtractPlaceholders(sql);

        Assert.Equal(["_age", "_student_id"], names);
    }

    [Fact]
    public void ExtractPlaceholders_Repeated_DedupsKeepingFirstSeenOrder()
    {
        const string sql = "select * from t where a = {_x} or b = {_y} or c = {_x}";

        var names = DatasetParameterResolver.ExtractPlaceholders(sql);

        Assert.Equal(["_x", "_y"], names);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("select * from t where a = 1")]
    public void HasPlaceholders_NoTokens_ReturnsFalse(string? sql)
    {
        Assert.False(DatasetParameterResolver.HasPlaceholders(sql));
        Assert.Empty(DatasetParameterResolver.ExtractPlaceholders(sql));
    }

    // ── Resolve ───────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_QuotedPlaceholder_ReplacesWholeLiteralIncludingQuotes()
    {
        const string sql = "select * from t where id = '{_student_id}'";
        var values = new Dictionary<string, object?> { ["_student_id"] = "abc" };

        var result = DatasetParameterResolver.Resolve(sql, values);

        // The surrounding single quotes are consumed — the value is bound, not interpolated.
        Assert.Equal("select * from t where id = $1", result.Sql);
        Assert.Equal(["abc"], result.Parameters);
        Assert.Empty(result.MissingParameters);
    }

    [Fact]
    public void Resolve_MultipleDistinct_AssignsSequentialPositionalParams()
    {
        const string sql = "select * from t where age > {_age} and id = '{_student_id}'";
        var values = new Dictionary<string, object?>
        {
            ["_age"] = 23L,
            ["_student_id"] = "u1",
        };

        var result = DatasetParameterResolver.Resolve(sql, values);

        Assert.Equal("select * from t where age > $1 and id = $2", result.Sql);
        Assert.Equal([23L, "u1"], result.Parameters);
    }

    [Fact]
    public void Resolve_RepeatedPlaceholder_ReusesSameParamSlot()
    {
        const string sql = "select * from t where a = {_x} or b = {_x}";
        var values = new Dictionary<string, object?> { ["_x"] = 5L };

        var result = DatasetParameterResolver.Resolve(sql, values);

        Assert.Equal("select * from t where a = $1 or b = $1", result.Sql);
        Assert.Single(result.Parameters);
        Assert.Equal(5L, result.Parameters[0]);
    }

    [Fact]
    public void Resolve_MissingValue_ReportedInMissingParameters()
    {
        const string sql = "select * from t where a = {_a} and b = {_b}";
        var values = new Dictionary<string, object?> { ["_a"] = 1L };

        var result = DatasetParameterResolver.Resolve(sql, values);

        Assert.Equal(["_b"], result.MissingParameters);
    }

    // ── TryParseParameters ────────────────────────────────────────────────

    [Fact]
    public void TryParseParameters_MixedScalarTypes_ParsesEach()
    {
        var ok = DatasetParameterResolver.TryParseParameters(
            "{\"_age\":23,\"_student_id\":\"uuid\",\"_active\":true,\"_note\":null}",
            out var values, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(23L, values["_age"]);
        Assert.Equal("uuid", values["_student_id"]);
        Assert.Equal(true, values["_active"]);
        Assert.Null(values["_note"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParseParameters_Empty_ReturnsEmptyMap(string? json)
    {
        var ok = DatasetParameterResolver.TryParseParameters(json, out var values, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Empty(values);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"_x\":{\"nested\":1}}")]
    public void TryParseParameters_InvalidOrUnsupported_ReturnsFalseWithError(string json)
    {
        var ok = DatasetParameterResolver.TryParseParameters(json, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }
}
