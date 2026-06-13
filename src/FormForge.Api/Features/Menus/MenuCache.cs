using FormForge.Api.Features.Menus.Dtos;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace FormForge.Api.Features.Menus;

internal interface IMenuCache
{
    Task<IReadOnlyList<NavMenuItem>?> TryGetAsync(Guid userId, CancellationToken ct);
    Task SetAsync(Guid userId, IReadOnlyList<NavMenuItem> tree, CancellationToken ct);
    Task InvalidateAsync(CancellationToken ct = default);
}

// Story 4.7 — real 5 s TTL navbar cache. Per-user keys; total eviction on mutation
// via a single shared CancellationTokenSource swap (O(1) bust, no secondary index).
//
// Singleton lifetime is required: the _generation token must be shared across
// requests so that an InvalidateAsync on one request evicts entries written on
// any other. IMemoryCache itself is thread-safe (registered in Program.cs).
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class MenuCache(IMemoryCache cache) : IMenuCache, IDisposable
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    // Swapped atomically on Invalidate; every cached entry is registered with the
    // current generation's token, so cancelling it evicts the entry the next time
    // IMemoryCache.TryGetValue checks tokens.
    private CancellationTokenSource _generation = new();

    private static string CacheKey(Guid userId) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"menus:{userId:N}");

    public Task<IReadOnlyList<NavMenuItem>?> TryGetAsync(Guid userId, CancellationToken ct)
    {
        if (cache.TryGetValue<IReadOnlyList<NavMenuItem>>(CacheKey(userId), out var hit) && hit is not null)
        {
            return Task.FromResult<IReadOnlyList<NavMenuItem>?>(hit);
        }
        return Task.FromResult<IReadOnlyList<NavMenuItem>?>(null);
    }

    public Task SetAsync(Guid userId, IReadOnlyList<NavMenuItem> tree, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tree);

        // Snapshot the current generation token so a concurrent Invalidate that
        // swaps the source between this read and the entry creation still busts
        // this entry (the snapshotted token is the one that gets cancelled).
        var token = _generation.Token;

        using var entry = cache.CreateEntry(CacheKey(userId));
        entry.Value = tree;
        entry.AbsoluteExpirationRelativeToNow = Ttl;
        entry.AddExpirationToken(new CancellationChangeToken(token));
        return Task.CompletedTask;
    }

    public async Task InvalidateAsync(CancellationToken ct = default)
    {
        // Atomic swap: cancel the current generation so every entry tied to that
        // token is evicted, then install a fresh source for future writes.
        var old = Interlocked.Exchange(ref _generation, new CancellationTokenSource());
        await old.CancelAsync().ConfigureAwait(false);
        old.Dispose();
    }

    public void Dispose()
    {
        // Singleton lifetime; only runs on app shutdown. Cancel cooperatively to
        // ensure no in-flight TryGet observes a half-disposed token.
        _generation.Cancel();
        _generation.Dispose();
    }
}
