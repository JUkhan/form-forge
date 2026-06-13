using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace FormForge.Api.Features.SchemaRegistry;

// Story 5.3 — IMemoryCache-backed singleton. 1-hour sliding TTL bounds memory
// growth for v1; a future story can swap to SizeLimit + Size=1 for true LRU
// eviction once the live designer count grows past a single-digit ceiling.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class SchemaRegistry(IMemoryCache cache) : ISchemaRegistry
{
    private static string CacheKey(string designerId, int version) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"schema:{designerId}:{version}");

    private static readonly MemoryCacheEntryOptions EntryOptions = new()
    {
        SlidingExpiration = TimeSpan.FromHours(1),
    };

    // Story 5.6 — tracks which versions have been Populated per designer so
    // InvalidateDesigner can remove every cached entry without iterating the
    // entire IMemoryCache. ConcurrentBag<int> permits duplicate entries (a
    // version re-provisioned twice adds its number twice); since
    // cache.Remove on a non-existent key is a no-op, correctness is unaffected.
    private readonly ConcurrentDictionary<string, ConcurrentBag<int>> _populatedVersions =
        new(StringComparer.Ordinal);

    public void Populate(SchemaRegistryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cache.Set(CacheKey(entry.DesignerId, entry.Version), entry, EntryOptions);
        _populatedVersions.GetOrAdd(entry.DesignerId, _ => []).Add(entry.Version);
    }

    public SchemaRegistryEntry? TryGet(string designerId, int version) =>
        cache.TryGetValue(CacheKey(designerId, version), out SchemaRegistryEntry? entry)
            ? entry
            : null;

    public void InvalidateDesigner(string designerId)
    {
        if (_populatedVersions.TryRemove(designerId, out var versions))
        {
            foreach (var v in versions)
                cache.Remove(CacheKey(designerId, v));
        }
    }
}
