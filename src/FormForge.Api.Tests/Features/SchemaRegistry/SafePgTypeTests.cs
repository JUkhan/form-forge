using FormForge.Api.Features.SchemaRegistry;

namespace FormForge.Api.Tests.Features.SchemaRegistry;

// Story B — locks the allowlist canonicalizer (the DDL-safety linchpin) and the
// lenient runtime family/base classifier.
public sealed class SafePgTypeTests
{
    [Theory]
    [InlineData("text", "text")]
    [InlineData("integer", "integer")]
    [InlineData("bigint", "bigint")]
    [InlineData("smallint", "smallint")]
    [InlineData("boolean", "boolean")]
    [InlineData("uuid", "uuid")]
    [InlineData("date", "date")]
    [InlineData("time", "time")]
    [InlineData("timestamp", "timestamp")]
    [InlineData("timestamptz", "timestamptz")]
    [InlineData("real", "real")]
    [InlineData("double precision", "double precision")]
    [InlineData("jsonb", "jsonb")]
    public void TryCreate_NoParamTypes_CanonicalizesToSelf(string raw, string expected)
    {
        Assert.True(SafePgType.TryCreate(raw, out var result, out _));
        Assert.Equal(expected, result!.Value);
    }

    [Theory]
    [InlineData("numeric(10,2)", "numeric(10,2)")]
    [InlineData("NUMERIC(10,2)", "numeric(10,2)")]
    [InlineData("numeric( 10 , 2 )", "numeric(10,2)")]
    [InlineData("numeric(1,0)", "numeric(1,0)")]
    [InlineData("numeric(1000,1000)", "numeric(1000,1000)")]
    public void TryCreate_Numeric_Canonicalizes(string raw, string expected)
    {
        Assert.True(SafePgType.TryCreate(raw, out var result, out _));
        Assert.Equal(expected, result!.Value);
        Assert.Equal(PgTypeFamily.Numeric, result.Family);
    }

    [Theory]
    [InlineData("varchar(255)", "varchar(255)")]
    [InlineData("VARCHAR(255)", "varchar(255)")]
    [InlineData("char(10)", "char(10)")]
    [InlineData("  varchar ( 64 ) ", "varchar(64)")]
    public void TryCreate_LengthTypes_Canonicalize(string raw, string expected)
    {
        Assert.True(SafePgType.TryCreate(raw, out var result, out _));
        Assert.Equal(expected, result!.Value);
        Assert.Equal(PgTypeFamily.Text, result.Family);
    }

    [Theory]
    [InlineData("Text", "text")]
    [InlineData("  TIMESTAMPTZ  ", "timestamptz")]
    [InlineData("double  precision", "double precision")]
    public void TryCreate_NormalizesCaseAndWhitespace(string raw, string expected)
    {
        Assert.True(SafePgType.TryCreate(raw, out var result, out _));
        Assert.Equal(expected, result!.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("numeric")]            // params required
    [InlineData("varchar")]            // length required
    [InlineData("char")]
    [InlineData("numeric(0,0)")]       // precision < 1
    [InlineData("numeric(1001,0)")]    // precision > 1000
    [InlineData("numeric(5,6)")]       // scale > precision
    [InlineData("varchar(0)")]         // length < 1
    [InlineData("int")]                // not in allowlist (only "integer")
    [InlineData("serial")]             // not allowed
    [InlineData("money")]              // not allowed
    [InlineData("bogus")]
    [InlineData("text; DROP TABLE users")]  // injection attempt
    [InlineData("varchar(255)); DROP TABLE x;--")]
    public void TryCreate_InvalidOrUnsafe_Rejected(string? raw)
    {
        Assert.False(SafePgType.TryCreate(raw, out var result, out var error));
        Assert.Null(result);
        Assert.NotNull(error);
    }
}

// Story B — the lenient runtime classifier used on the read/write path. Must
// accept both new authored types and legacy uppercase constants.
public sealed class PgTypeInfoTests
{
    // expected is passed as a string (the enum name) because PgTypeFamily is
    // internal — a public xUnit test method may not expose it in its signature.
    [Theory]
    [InlineData("text", "Text")]
    [InlineData("TEXT", "Text")]
    [InlineData("varchar(255)", "Text")]
    [InlineData("char(10)", "Text")]
    [InlineData("numeric(10,2)", "Numeric")]
    [InlineData("NUMERIC", "Numeric")]
    [InlineData("integer", "Numeric")]
    [InlineData("bigint", "Numeric")]
    [InlineData("double precision", "Numeric")]
    [InlineData("real", "Numeric")]
    [InlineData("boolean", "Boolean")]
    [InlineData("BOOLEAN", "Boolean")]
    [InlineData("date", "Temporal")]
    [InlineData("time", "Temporal")]
    [InlineData("timestamp", "Temporal")]
    [InlineData("timestamptz", "Temporal")]
    [InlineData("TIMESTAMPTZ", "Temporal")]
    [InlineData("uuid", "Uuid")]
    [InlineData("UUID", "Uuid")]
    [InlineData("jsonb", "Json")]
    [InlineData("JSONB", "Json")]
    [InlineData("totally_unknown", "Text")]  // forward-compat default
    public void FamilyOf_ClassifiesAuthoredAndLegacy(string pgType, string expected)
        => Assert.Equal(expected, PgTypeInfo.FamilyOf(pgType).ToString());

    [Theory]
    [InlineData("numeric(10,2)", "numeric")]
    [InlineData("VARCHAR(255)", "varchar")]
    [InlineData("double precision", "double precision")]
    [InlineData("TIMESTAMPTZ", "timestamptz")]
    public void BaseOf_StripsParamsAndLowercases(string pgType, string expected)
        => Assert.Equal(expected, PgTypeInfo.BaseOf(pgType));

    [Theory]
    [InlineData("integer", true)]
    [InlineData("bigint", true)]
    [InlineData("smallint", true)]
    [InlineData("numeric", false)]
    [InlineData("real", false)]
    public void IsIntegerBase_Works(string baseType, bool expected)
        => Assert.Equal(expected, PgTypeInfo.IsIntegerBase(baseType));

    [Theory]
    [InlineData("real", true)]
    [InlineData("double precision", true)]
    [InlineData("numeric", false)]
    [InlineData("integer", false)]
    public void IsFloatBase_Works(string baseType, bool expected)
        => Assert.Equal(expected, PgTypeInfo.IsFloatBase(baseType));
}
