using FormForge.Api.Common.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FormForge.Api.Tests.Common.Logging;

public sealed class SqlFingerprintTests
{
    [Fact]
    public void Fingerprint_StripsSingleQuotedLiterals()
    {
        var result = SqlFingerprint.Fingerprint("SELECT * FROM x WHERE name = 'bob'");
        Assert.Equal("SELECT * FROM x WHERE name = ?", result);
    }

    [Fact]
    public void Fingerprint_StripsInlineNumbers()
    {
        var result = SqlFingerprint.Fingerprint("SELECT id FROM x WHERE age > 18");
        Assert.Equal("SELECT id FROM x WHERE age > ?", result);
    }

    [Fact]
    public void Fingerprint_StripsNegativeNumbers()
    {
        var result = SqlFingerprint.Fingerprint("SELECT id FROM x WHERE balance < -100");
        Assert.Equal("SELECT id FROM x WHERE balance < ?", result);
    }

    [Fact]
    public void Fingerprint_StripsHexadecimalLiterals()
    {
        var result = SqlFingerprint.Fingerprint("SELECT id FROM x WHERE flags = 0x1F");
        Assert.Equal("SELECT id FROM x WHERE flags = ?", result);
    }

    [Fact]
    public void Fingerprint_StripsScientificNotation()
    {
        var result = SqlFingerprint.Fingerprint("SELECT id FROM x WHERE price < 1.5e10");
        Assert.Equal("SELECT id FROM x WHERE price < ?", result);
    }

    [Fact]
    public void Fingerprint_PreservesNamedPlaceholders()
    {
        const string Sql = "INSERT INTO x (id, name) VALUES (@p0, @p1)";
        var result = SqlFingerprint.Fingerprint(Sql);
        Assert.Equal(Sql, result);
    }

    [Fact]
    public void Fingerprint_PreservesIdentifiersContainingDigits()
    {
        const string Sql = "SELECT col_1, col2 FROM tbl_3 WHERE col_1 = @v";
        var result = SqlFingerprint.Fingerprint(Sql);
        Assert.Equal(Sql, result);
    }

    [Fact]
    public void Fingerprint_StripsEscapedQuotesAsSingleLiteral()
    {
        var result = SqlFingerprint.Fingerprint("SELECT * FROM x WHERE q = 'it''s ok'");
        Assert.Equal("SELECT * FROM x WHERE q = ?", result);
    }

    [Fact]
    public void Fingerprint_NullThrows()
    {
        Assert.Throws<ArgumentNullException>(() => SqlFingerprint.Fingerprint(null!));
    }

    [Fact]
    public void BeginDdlScope_EmitsOperationKindDdl()
    {
        var logger = new CapturingLogger();

        using (SqlFingerprint.BeginDdlScope(logger, "designer-1", "create_table", "CREATE TABLE x"))
        {
            Assert.NotNull(logger.LastScope);
            Assert.Equal("ddl", logger.LastScope!["operationKind"]);
            Assert.Equal("designer-1", logger.LastScope["designerId"]);
            Assert.Equal("create_table", logger.LastScope["operation"]);
            Assert.Equal("CREATE TABLE x", logger.LastScope["sqlFingerprint"]);
        }
    }

    [Fact]
    public void BeginCrudScope_EmitsOperationKindCrud()
    {
        var logger = new CapturingLogger();

        using (SqlFingerprint.BeginCrudScope(logger, "designer-1", "insert", "INSERT INTO x VALUES (?)"))
        {
            Assert.NotNull(logger.LastScope);
            Assert.Equal("crud", logger.LastScope!["operationKind"]);
        }
    }

    [Fact]
    public void BeginDdlScope_AllowsNullableReturnFromLoggerWithoutNre()
    {
        // Verifies the signature change (IDisposable? instead of force-bang'd IDisposable) does not
        // crash callers when ILogger.BeginScope legally returns null. NullLogger always returns
        // NullScope (non-null), so this test exercises the signature, not the runtime path.
        var scope = SqlFingerprint.BeginDdlScope(NullLogger.Instance, "d", "op", "fp");
        scope?.Dispose();
    }

    private sealed class CapturingLogger : ILogger
    {
        public Dictionary<string, object>? LastScope { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            if (state is IEnumerable<KeyValuePair<string, object>> kvps)
            {
                LastScope = kvps.ToDictionary(k => k.Key, v => v.Value, StringComparer.Ordinal);
            }
            return EmptyDisposable.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }

        private sealed class EmptyDisposable : IDisposable
        {
            public static readonly EmptyDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
