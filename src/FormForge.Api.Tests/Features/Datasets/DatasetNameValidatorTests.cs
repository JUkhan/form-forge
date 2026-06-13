using System.Reflection;
using FormForge.Api.Domain.ValueTypes;

namespace FormForge.Api.Tests.Features.Datasets;

// Story 8.3 (FR-57 / AR-57) — unit coverage for the DatasetName value type. Each
// validation layer (pattern/length, reserved keyword, permanent denylist) is
// exercised in isolation, plus the sealed+private-ctor structural guarantee (AC-6)
// and the 2-param / 4-param overload parity.
public sealed class DatasetNameValidatorTests
{
    // ---------- AC-1: valid names ----------

    [Theory]
    [InlineData("my_dataset")]
    [InlineData("_private")]
    [InlineData("report_2024")]
    [InlineData("a")]
    public void TryCreate_ValidName_ReturnsTrue(string raw)
    {
        var ok = DatasetName.TryCreate(raw, out var result, out var errorCode, out var error);

        Assert.True(ok);
        Assert.NotNull(result);
        Assert.Equal(raw, result!.Value);
        Assert.Null(errorCode);
        Assert.Null(error);
    }

    [Fact]
    public void TryCreate_MaxLength63_ReturnsTrue()
    {
        var raw = "a" + new string('b', 62); // exactly 63 chars

        var ok = DatasetName.TryCreate(raw, out var result, out _, out _);

        Assert.True(ok);
        Assert.Equal(63, result!.Value.Length);
    }

    // ---------- AC-1: invalid pattern / length ----------

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("MyDataset")]   // uppercase
    [InlineData("123abc")]      // starts with a digit
    [InlineData("my-dataset")]  // hyphen
    [InlineData("a b")]         // space
    public void TryCreate_InvalidPattern_ReturnsFalseWithInvalidPatternCode(string raw)
    {
        var ok = DatasetName.TryCreate(raw, out var result, out var errorCode, out var error);

        Assert.False(ok);
        Assert.Null(result);
        Assert.Equal(DatasetNameError.InvalidPattern, errorCode);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryCreate_64Chars_ReturnsFalseWithInvalidPatternCode()
    {
        var raw = "a" + new string('b', 63); // 64 chars — exceeds the 63-char limit

        var ok = DatasetName.TryCreate(raw, out var result, out var errorCode, out _);

        Assert.False(ok);
        Assert.Null(result);
        Assert.Equal(DatasetNameError.InvalidPattern, errorCode);
    }

    // ---------- AC-2: reserved PostgreSQL keywords ----------

    [Theory]
    [InlineData("select")]
    [InlineData("from")]
    [InlineData("table")]
    [InlineData("group")]
    [InlineData("user")]
    [InlineData("role")]
    [InlineData("pg_foo")] // pg_ prefix is reserved wholesale by PgReservedKeywords
    public void TryCreate_ReservedKeyword_ReturnsFalseWithReservedKeywordCode(string raw)
    {
        var ok = DatasetName.TryCreate(raw, out var result, out var errorCode, out var error);

        Assert.False(ok);
        Assert.Null(result);
        Assert.Equal(DatasetNameError.ReservedKeyword, errorCode);
        Assert.NotNull(error);
    }

    // ---------- AC-3: permanent denylist ----------

    [Theory]
    [InlineData("users")]
    [InlineData("roles")]
    [InlineData("menus")]
    [InlineData("custom_dataset")]
    [InlineData("dataset_audit_log")]
    [InlineData("refresh_tokens")]
    public void TryCreate_Denylisted_ReturnsFalseWithDenylistCode(string raw)
    {
        var ok = DatasetName.TryCreate(raw, out var result, out var errorCode, out var error);

        Assert.False(ok);
        Assert.Null(result);
        Assert.Equal(DatasetNameError.Denylist, errorCode);
        Assert.NotNull(error);
    }

    // ---------- AC-6: structural guarantee (sealed + private ctor) ----------

    [Fact]
    public void DatasetName_ConstructorIsPrivate()
    {
        var ctors = typeof(DatasetName).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.All(ctors, c => Assert.True(c.IsPrivate));
        // No public ctor exists — the only path to a DatasetName is TryCreate.
        Assert.Empty(typeof(DatasetName).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
    }

    [Fact]
    public void DatasetName_IsSealed()
    {
        Assert.True(typeof(DatasetName).IsSealed);
    }

    // ---------- 2-param overload mirrors the 4-param overload ----------

    [Theory]
    [InlineData("my_dataset")]   // valid
    [InlineData("MyDataset")]    // invalid pattern
    [InlineData("select")]       // reserved keyword
    [InlineData("users")]        // denylist
    public void TryCreate_TwoParamOverload_MatchesFourParamOverload(string raw)
    {
        var ok4 = DatasetName.TryCreate(raw, out var result4, out _, out var error4);
        var ok2 = DatasetName.TryCreate(raw, out var result2, out var error2);

        Assert.Equal(ok4, ok2);
        Assert.Equal(result4?.Value, result2?.Value);
        Assert.Equal(error4, error2);
    }
}
