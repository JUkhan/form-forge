using FormForge.Api.Features.Datasets;

namespace FormForge.Api.Tests.Features.Datasets;

// Story 11.1 (FR-70 / AR-64) — pure unit coverage for the per-expression security gate
// used by calculated columns and CASE operands/outputs. Layers 1 (keyword/semicolon) and
// 2 (wrap-parse) only; the final SELECT-only check runs on the assembled SQL elsewhere.
public sealed class ExpressionSecurityValidatorTests
{
    [Theory]
    [InlineData("price * 1.1")]
    [InlineData("COALESCE(name, 'unknown')")]
    [InlineData("LENGTH(description)")]
    [InlineData("a + b - c")]
    [InlineData("ROUND(amount, 2)")]
    public void Validate_ValidExpression_ReturnsIsValidTrue(string expression)
    {
        var result = ExpressionSecurityValidator.Validate(expression, "test_alias");
        Assert.True(result.IsValid);
        Assert.Null(result.ErrorMessage);
    }

    [Theory]
    [InlineData("DROP TABLE users")]
    [InlineData("DELETE FROM foo")]
    [InlineData("INSERT INTO bar VALUES (1)")]
    [InlineData("CREATE TABLE x (id int)")]
    [InlineData("UPDATE foo SET a = 1")]
    [InlineData("TRUNCATE TABLE foo")]
    [InlineData("ALTER TABLE foo ADD COLUMN bar int")]
    public void Validate_DangerousKeyword_ReturnsInvalid(string expression)
    {
        var result = ExpressionSecurityValidator.Validate(expression, "test_alias");
        Assert.False(result.IsValid);
        Assert.NotNull(result.ErrorMessage);
    }

    [Theory]
    [InlineData("price; DROP TABLE users")]
    [InlineData("1; SELECT 1")]
    public void Validate_ContainsSemicolon_ReturnsInvalid(string expression)
    {
        var result = ExpressionSecurityValidator.Validate(expression, "test_alias");
        Assert.False(result.IsValid);
        Assert.Contains("semicolon", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("(unclosed paren")]
    [InlineData("price +")]
    [InlineData("* / +")]
    public void Validate_Unparseable_ReturnsInvalid(string expression)
    {
        var result = ExpressionSecurityValidator.Validate(expression, "test_alias");
        Assert.False(result.IsValid);
        Assert.NotNull(result.ErrorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyOrWhitespace_ReturnsInvalid(string expression)
    {
        var result = ExpressionSecurityValidator.Validate(expression, "test_alias");
        Assert.False(result.IsValid);
        Assert.Contains("empty", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_ErrorMessage_IncludesAlias()
    {
        var result = ExpressionSecurityValidator.Validate("DROP TABLE x", "my_alias");
        Assert.Contains("my_alias", result.ErrorMessage!, StringComparison.Ordinal);
    }

    // Code-review P5 — the keyword/semicolon scan + step-10 SELECT-only enforcement did NOT
    // stop sub-SELECT exfiltration or dangerous functions; the parse-tree allowlist must.
    [Theory]
    [InlineData("(SELECT secret FROM users LIMIT 1)")]            // scalar subquery
    [InlineData("(SELECT password FROM users)")]
    [InlineData("\"x\" || (SELECT secret FROM users) || \"y\"")] // CASE double-quote-passthrough payload
    [InlineData("1 UNION SELECT 1")]                              // set operation
    public void Validate_SubqueryOrSetOp_ReturnsInvalid(string expression)
    {
        var result = ExpressionSecurityValidator.Validate(expression, "test_alias");
        Assert.False(result.IsValid);
        Assert.NotNull(result.ErrorMessage);
    }

    [Theory]
    [InlineData("pg_read_file('/etc/passwd')")]
    [InlineData("current_setting('is_superuser')")]
    [InlineData("pg_sleep(10)")]
    [InlineData("lo_export(1, '/tmp/x')")]
    [InlineData("query_to_xml('SELECT 1', true, false, '')")]
    public void Validate_NonAllowlistedFunction_ReturnsInvalid(string expression)
    {
        var result = ExpressionSecurityValidator.Validate(expression, "test_alias");
        Assert.False(result.IsValid);
        Assert.NotNull(result.ErrorMessage);
    }

    [Theory]
    [InlineData("UPPER(name)")]
    [InlineData("COALESCE(a, b, 0)")]
    [InlineData("ROUND(amount * 1.1, 2)")]
    [InlineData("GREATEST(a, b)")]
    [InlineData("CASE WHEN a > 0 THEN 'p' ELSE 'n' END")]
    public void Validate_AllowlistedFunctionsAndConstructs_RemainValid(string expression)
    {
        var result = ExpressionSecurityValidator.Validate(expression, "test_alias");
        Assert.True(result.IsValid);
    }
}
