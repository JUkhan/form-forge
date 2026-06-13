using FormForge.Api.Features.Designer;

namespace FormForge.Api.Tests.Features.Designer;

public sealed class SafeIdentifierTests
{
    [Theory]
    [InlineData("incident_report")]
    [InlineData("a")]
    [InlineData("_a")]
    [InlineData("a1")]
    [InlineData("a_b_c")]
    [InlineData("__leading_underscores")]
    // 63 chars, the PG NAMEDATALEN-1 boundary.
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void TryCreate_ValidIdentifiers_Succeed(string raw)
    {
        var ok = SafeIdentifier.TryCreate(raw, out var safe, out var err);

        Assert.True(ok);
        Assert.NotNull(safe);
        Assert.Equal(raw, safe!.Value);
        Assert.Null(err);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData(null, "empty")]
    [InlineData("Foo", "case")]
    [InlineData("1leading_digit", "leading-digit")]
    [InlineData("with-hyphen", "hyphen")]
    [InlineData("with space", "space")]
    [InlineData("trailing!", "punctuation")]
    // 64 chars — one over the PG NAMEDATALEN-1 boundary.
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "too-long")]
    public void TryCreate_InvalidIdentifiers_Fail(string? raw, string _)
    {
        var ok = SafeIdentifier.TryCreate(raw, out var safe, out var err);

        Assert.False(ok);
        Assert.Null(safe);
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Theory]
    [InlineData("select")]
    [InlineData("from")]
    [InlineData("where")]
    [InlineData("user")]
    [InlineData("role")]         // PG reserved keyword for CREATE ROLE statements
    [InlineData("oid")]          // PG system column on every table
    [InlineData("xmin")]         // PG MVCC system column
    [InlineData("ctid")]         // PG physical-tuple-id system column
    [InlineData("created_at")]   // collides with the system column on dynamic tables
    [InlineData("id")]
    [InlineData("pg_class")]     // PG-reserved namespace (any pg_* identifier)
    [InlineData("pg_anything")]
    [InlineData("pg_")]          // bare prefix
    public void TryCreate_ReservedKeywords_Fail(string raw)
    {
        var ok = SafeIdentifier.TryCreate(raw, out var safe, out var err);

        Assert.False(ok);
        Assert.Null(safe);
        Assert.Contains("reserved", err, StringComparison.OrdinalIgnoreCase);
    }

    // The 4-param overload exposes a SafeIdentifierError code so DesignerService
    // can route to distinct 422 envelopes (IDENTIFIER_INVALID vs
    // IDENTIFIER_RESERVED_KEYWORD per AC-1/AC-2). The 3-param overload above
    // discards the code, which is intentional for FieldKeyValidator (see Dev Notes).
    [Fact]
    public void TryCreate_InvalidPattern_SetsErrorCodeInvalidPattern()
    {
        var ok = SafeIdentifier.TryCreate("Has-Bad-Chars", out var safe, out var code, out var err);

        Assert.False(ok);
        Assert.Null(safe);
        Assert.Equal(SafeIdentifierError.InvalidPattern, code);
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Theory]
    [InlineData("select")]   // PG reserved keyword
    [InlineData("pg_toast")] // pg_* prefix — also routed through IsReserved
    public void TryCreate_ReservedKeyword_SetsErrorCodeReservedKeyword(string raw)
    {
        var ok = SafeIdentifier.TryCreate(raw, out var safe, out var code, out var err);

        Assert.False(ok);
        Assert.Null(safe);
        Assert.Equal(SafeIdentifierError.ReservedKeyword, code);
        Assert.Contains("reserved", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryCreate_ValidIdentifier_ErrorCodeIsNull()
    {
        var ok = SafeIdentifier.TryCreate("incident_report", out var safe, out var code, out var err);

        Assert.True(ok);
        Assert.NotNull(safe);
        Assert.Equal("incident_report", safe!.Value);
        Assert.Null(code);
        Assert.Null(err);
    }
}
