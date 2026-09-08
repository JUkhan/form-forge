using Microsoft.Extensions.Caching.Memory;

namespace FormForge.Api.Features.Tenancy;

// Story 12.3 (FR-74 / architecture.md §7.3) — cached (SchemaName, Status) snapshot
// keyed by tenantId, so TenantContextMiddleware doesn't hit `tenants` on every
// authenticated request. Mirrors SchemaRegistry's IMemoryCache-wrapper shape
// (Features/SchemaRegistry/SchemaRegistry.cs): the cache is populate-on-miss only —
// it never queries the database itself, that's the middleware's job. 5-minute
// sliding TTL per architecture.md §7.3 (shorter than SchemaRegistry's 1-hour TTL:
// a suspended tenant should stop authenticating within a bounded, short window).
internal sealed record TenantLookupEntry(string SchemaName, string Status);

internal interface ITenantLookupCache
{
    TenantLookupEntry? TryGet(Guid tenantId);

    void Set(Guid tenantId, TenantLookupEntry entry);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class TenantLookupCache(IMemoryCache cache) : ITenantLookupCache
{
    private static string CacheKey(Guid tenantId) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"tenant:{tenantId}");

    // AbsoluteExpirationRelativeToNow alongside the sliding window: without it, a
    // continuously-accessed entry (any tenant with steady request traffic) would never
    // expire, defeating the "bounded, short window" a suspended tenant is supposed to
    // stop authenticating within.
    private static readonly MemoryCacheEntryOptions EntryOptions = new()
    {
        SlidingExpiration = TimeSpan.FromMinutes(5),
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
    };

    public TenantLookupEntry? TryGet(Guid tenantId) =>
        cache.TryGetValue(CacheKey(tenantId), out TenantLookupEntry? entry) ? entry : null;

    public void Set(Guid tenantId, TenantLookupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cache.Set(CacheKey(tenantId), entry, EntryOptions);
    }
}
