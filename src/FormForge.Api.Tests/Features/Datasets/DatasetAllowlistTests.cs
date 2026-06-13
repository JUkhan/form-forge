using FormForge.Api.Features.Datasets;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FormForge.Api.Tests.Features.Datasets;

// Pure unit coverage for DatasetAllowlist.IsAllowed — the synchronous policy gate. No DB:
// IsAllowed only reads the exclusion set + optional restrict-list built from configuration
// at construction (DbConnectionFactory is constructed but never opens a connection here).
public sealed class DatasetAllowlistTests : IDisposable
{
    private readonly List<MemoryCache> _caches = [];

    public void Dispose()
    {
        foreach (var c in _caches) c.Dispose();
    }

    private DatasetAllowlist Build(params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s =>
                new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        var cache = new MemoryCache(new MemoryCacheOptions());
        _caches.Add(cache);
        return new DatasetAllowlist(
            config,
            cache,
            new DbConnectionFactory(config),
            NullLogger<DatasetAllowlist>.Instance);
    }

    [Fact]
    public void NonSystemTable_IsAllowed_ByDefault()
    {
        var allowlist = Build();
        Assert.True(allowlist.IsAllowed("villages"));
    }

    [Theory]
    [InlineData("custom_dataset")]
    [InlineData("role_permissions")]
    [InlineData("DataProtectionKeys")]
    [InlineData("__EFMigrationsHistory")]
    public void BuiltInSystemTable_IsNeverAllowed(string systemTable)
    {
        var allowlist = Build();
        Assert.False(allowlist.IsAllowed(systemTable));
    }

    [Fact]
    public void ExcludedTables_HidesTheConfiguredTable()
    {
        var allowlist = Build(("DatasetManager:ExcludedTables:0", "villages"));
        Assert.False(allowlist.IsAllowed("villages"));
        Assert.True(allowlist.IsAllowed("houses"));
    }

    [Fact]
    public void ExcludedTables_CannotExpose_ABuiltInSystemTable()
    {
        // Even if an operator tries to "allow" a system table, the hardcoded floor wins.
        var allowlist = Build(("DatasetManager:AllowedTables:0", "password_reset_tokens"));
        Assert.False(allowlist.IsAllowed("password_reset_tokens"));
    }

    [Fact]
    public void AllowedTables_RestrictsToTheConfiguredSubset()
    {
        var allowlist = Build(("DatasetManager:AllowedTables:0", "villages"));
        Assert.True(allowlist.IsAllowed("villages"));
        Assert.False(allowlist.IsAllowed("houses"));
    }
}
