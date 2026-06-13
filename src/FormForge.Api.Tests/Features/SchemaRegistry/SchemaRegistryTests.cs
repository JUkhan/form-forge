using FormForge.Api.Features.SchemaRegistry;
using Microsoft.Extensions.Caching.Memory;

namespace FormForge.Api.Tests.Features.SchemaRegistry;

// Story 5.6 — InvalidateDesigner unit test. Exercises the per-designer cache
// eviction without needing a full integration host. The IMemoryCache is wired
// up directly; SchemaRegistry's _populatedVersions dictionary tracks which
// versions were Populated so InvalidateDesigner can purge every one.
public class SchemaRegistryTests
{
    [Fact]
    public void InvalidateDesigner_RemovesAllVersions_TryGetReturnsNull()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var registry = new global::FormForge.Api.Features.SchemaRegistry.SchemaRegistry(cache);

        // Populate two versions for designer "d1" and one for "d2".
        registry.Populate(new SchemaRegistryEntry("d1", 1, [], [], DateTimeOffset.UtcNow));
        registry.Populate(new SchemaRegistryEntry("d1", 2, [], [], DateTimeOffset.UtcNow));
        registry.Populate(new SchemaRegistryEntry("d2", 1, [], [], DateTimeOffset.UtcNow));

        // Sanity — all three entries are retrievable.
        Assert.NotNull(registry.TryGet("d1", 1));
        Assert.NotNull(registry.TryGet("d1", 2));
        Assert.NotNull(registry.TryGet("d2", 1));

        registry.InvalidateDesigner("d1");

        // Both d1 versions evicted; d2 untouched.
        Assert.Null(registry.TryGet("d1", 1));
        Assert.Null(registry.TryGet("d1", 2));
        Assert.NotNull(registry.TryGet("d2", 1));
    }
}
