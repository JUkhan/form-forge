using FormForge.Api.Features.Datasets;

namespace FormForge.Api.Tests.Features.Datasets;

// Story 8.8 (FR-60 / AR-61) — pure unit coverage for the SELECT-only enforcer
// (checkpoint a). Calls SqlSelectEnforcer.Validate directly (InternalsVisibleTo) —
// no Testcontainers, no WebApplicationFactory, no [Collection]. Fast and isolated.
public sealed class SqlSelectEnforcerTests
{
    // ── Valid: plain SELECT ────────────────────────────────────────────

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("SELECT id, name FROM users")]
    [InlineData("SELECT * FROM foo WHERE id = 1")]
    [InlineData("SELECT id FROM foo ORDER BY id DESC LIMIT 10")]
    [InlineData("SELECT count(*) FROM foo GROUP BY type")]
    [InlineData("SELECT a.id, b.name FROM a JOIN b ON a.id = b.a_id")]
    public void Validate_ValidSelectStatement_ReturnsIsValidTrue(string sql)
    {
        var result = SqlSelectEnforcer.Validate(sql);
        Assert.True(result.IsValid);
        Assert.Null(result.ErrorMessage);
    }

    [Theory]
    [InlineData("SELECT 1;")]
    [InlineData("SELECT id, name FROM users;")]
    public void Validate_TrailingSemicolon_ReturnsIsValidTrue(string sql)
    {
        var result = SqlSelectEnforcer.Validate(sql);
        Assert.True(result.IsValid);
        Assert.Null(result.ErrorMessage);
    }

    // ── Valid: CTEs ────────────────────────────────────────────────────

    [Theory]
    [InlineData("WITH cte AS (SELECT id FROM foo) SELECT id FROM cte")]
    [InlineData("WITH a AS (SELECT 1 AS n), b AS (SELECT 2 AS n) SELECT a.n, b.n FROM a, b")]
    public void Validate_CteSelectStatement_ReturnsIsValidTrue(string sql)
    {
        var result = SqlSelectEnforcer.Validate(sql);
        Assert.True(result.IsValid);
    }

    // ── Invalid: multi-statement ───────────────────────────────────────

    [Theory]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("SELECT id FROM foo; SELECT id FROM bar")]
    public void Validate_MultiStatement_ReturnsInvalid(string sql)
    {
        var result = SqlSelectEnforcer.Validate(sql);
        Assert.False(result.IsValid);
        Assert.NotNull(result.ErrorMessage);
    }

    // ── Invalid: SELECT INTO rejected ─────────────────────────────────

    [Theory]
    [InlineData("SELECT id INTO newtable FROM foo")]
    [InlineData("SELECT * INTO backup FROM users WHERE id = 1")]
    public void Validate_SelectInto_ReturnsInvalid(string sql)
    {
        var result = SqlSelectEnforcer.Validate(sql);
        Assert.False(result.IsValid);
        Assert.NotNull(result.ErrorMessage);
    }

    // ── Invalid: writable CTEs rejected ───────────────────────────────

    [Theory]
    [InlineData("WITH x AS (INSERT INTO foo VALUES (1) RETURNING id) SELECT id FROM x")]
    [InlineData("WITH x AS (UPDATE foo SET a = 1 RETURNING id) SELECT id FROM x")]
    [InlineData("WITH x AS (DELETE FROM foo WHERE id = 1 RETURNING id) SELECT id FROM x")]
    public void Validate_WritableCte_ReturnsInvalid(string sql)
    {
        var result = SqlSelectEnforcer.Validate(sql);
        Assert.False(result.IsValid);
        Assert.NotNull(result.ErrorMessage);
    }

    // ── Invalid: DML rejected ──────────────────────────────────────────

    [Theory]
    [InlineData("INSERT INTO foo (id) VALUES (1)")]
    [InlineData("UPDATE foo SET name = 'x' WHERE id = 1")]
    [InlineData("DELETE FROM foo WHERE id = 1")]
    public void Validate_DmlStatement_ReturnsInvalid(string sql)
    {
        var result = SqlSelectEnforcer.Validate(sql);
        Assert.False(result.IsValid);
        Assert.NotNull(result.ErrorMessage);
    }

    // ── Invalid: DDL rejected ──────────────────────────────────────────

    [Theory]
    [InlineData("CREATE TABLE foo (id INT)")]
    [InlineData("DROP TABLE foo")]
    [InlineData("ALTER TABLE foo ADD COLUMN bar INT")]
    [InlineData("TRUNCATE TABLE foo")]
    public void Validate_DdlStatement_ReturnsInvalid(string sql)
    {
        var result = SqlSelectEnforcer.Validate(sql);
        Assert.False(result.IsValid);
        Assert.NotNull(result.ErrorMessage);
    }

    // ── Invalid: parse failure ─────────────────────────────────────────

    [Theory]
    [InlineData("not valid sql $$$$")]
    [InlineData("SELECT FROM WHERE")]
    [InlineData("SELECT (((")]
    [InlineData(";")]
    public void Validate_UnparsableSql_ReturnsInvalid(string sql)
    {
        var result = SqlSelectEnforcer.Validate(sql);
        Assert.False(result.IsValid);
        Assert.NotNull(result.ErrorMessage);
    }

    // ── Invalid: empty / whitespace ────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Validate_EmptyOrWhiteSpace_ReturnsInvalid(string sql)
    {
        var result = SqlSelectEnforcer.Validate(sql);
        Assert.False(result.IsValid);
    }

    // ── Error message content ──────────────────────────────────────────

    [Fact]
    public void Validate_InsertStatement_ErrorMessageMentionsSelect()
    {
        var result = SqlSelectEnforcer.Validate("INSERT INTO foo VALUES (1)");
        Assert.Contains("SELECT", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_UnparsableSql_ErrorMessageMentionsParsed()
    {
        var result = SqlSelectEnforcer.Validate("$$$ garbage $$$");
        Assert.Contains("parsed", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
