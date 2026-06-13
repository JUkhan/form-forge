using FormForge.Api.Features.Menus;
using FormForge.Api.Features.Menus.Dtos;
using Microsoft.Extensions.Caching.Memory;

namespace FormForge.Api.Tests.Features.Menus;

// Story 4.7 — pure unit tests for the MenuCache primitive. No PostgresFixture
// because the cache has no DB dependency. Validates the contract that
// MenuService relies on:
//   * Set then TryGet returns the same instance.
//   * Different user keys are isolated.
//   * InvalidateAsync evicts every cached entry across all users.
public sealed class MenuCacheTests
{
    private static IReadOnlyList<NavMenuItem> SampleTree(string name) =>
    [
        new NavMenuItem(Guid.NewGuid(), name, 0, Icon: null, ParentId: null, DesignerId: null, RoutePath: null, Children: []),
    ];

    [Fact]
    public async Task Set_then_TryGet_ReturnsSameInstance()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var cache = new MenuCache(memory);
        var userId = Guid.NewGuid();
        var tree = SampleTree("Set-then-Get");

        await cache.SetAsync(userId, tree, CancellationToken.None);
        var hit = await cache.TryGetAsync(userId, CancellationToken.None);

        Assert.NotNull(hit);
        Assert.Same(tree, hit);
    }

    [Fact]
    public async Task Set_DifferentUsers_AreIsolated()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var cache = new MenuCache(memory);
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var treeA = SampleTree("user-A");

        await cache.SetAsync(userA, treeA, CancellationToken.None);

        var hitA = await cache.TryGetAsync(userA, CancellationToken.None);
        var hitB = await cache.TryGetAsync(userB, CancellationToken.None);
        Assert.Same(treeA, hitA);
        Assert.Null(hitB);
    }

    [Fact]
    public async Task Invalidate_EvictsEntriesForAllUsers()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var cache = new MenuCache(memory);
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var userC = Guid.NewGuid();

        await cache.SetAsync(userA, SampleTree("A"), CancellationToken.None);
        await cache.SetAsync(userB, SampleTree("B"), CancellationToken.None);
        await cache.SetAsync(userC, SampleTree("C"), CancellationToken.None);

        await cache.InvalidateAsync();

        Assert.Null(await cache.TryGetAsync(userA, CancellationToken.None));
        Assert.Null(await cache.TryGetAsync(userB, CancellationToken.None));
        Assert.Null(await cache.TryGetAsync(userC, CancellationToken.None));
    }

    [Fact]
    public async Task SetAfterInvalidate_IsRetainedByTheNewGeneration()
    {
        // The post-invalidate generation must not also be cancelled — otherwise the
        // very first write after Invalidate would be evicted instantly.
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var cache = new MenuCache(memory);
        var userId = Guid.NewGuid();

        await cache.SetAsync(userId, SampleTree("pre-bust"), CancellationToken.None);
        await cache.InvalidateAsync();

        var fresh = SampleTree("post-bust");
        await cache.SetAsync(userId, fresh, CancellationToken.None);
        var hit = await cache.TryGetAsync(userId, CancellationToken.None);

        Assert.Same(fresh, hit);
    }
}
