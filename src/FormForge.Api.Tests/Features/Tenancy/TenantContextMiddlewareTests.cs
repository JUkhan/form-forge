using System.Security.Claims;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Features.Tenancy;
using FormForge.Api.Infrastructure.Persistence;
using FormForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FormForge.Api.Tests.Features.Tenancy;

// Story 12.3 — exercises TenantContextMiddleware directly (convention-based middleware,
// invoked with its DI-resolved parameters supplied explicitly), against a real
// Testcontainers Postgres for the `tenants` lookup — same "real DB, no mocks" posture
// as TenantProvisioningServiceTests. Covers every row of the story's I/O matrix that
// concerns the middleware: valid+Active, missing/non-Active (401), and no-claim
// passthrough.
public sealed class TenantContextMiddlewareTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private DbContextOptions<FormForgeDbContext>? _dbOptions;

    public TenantContextMiddlewareTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _dbOptions = new DbContextOptionsBuilder<FormForgeDbContext>()
            .UseNpgsql(_postgres.ConnectionString)
            .Options;

        await using var db = new FormForgeDbContext(_dbOptions);
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE tenants RESTART IDENTITY CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private FormForgeDbContext CreateDb() => new(_dbOptions!);

    private static DefaultHttpContext CreateHttpContext(Guid? tenantId)
    {
        var ctx = new DefaultHttpContext();
        if (tenantId is not null)
        {
            var identity = new ClaimsIdentity([new Claim("tenantId", tenantId.Value.ToString())], "TestAuth");
            ctx.User = new ClaimsPrincipal(identity);
        }
        return ctx;
    }

    // --- Valid claim, Active tenant -----------------------------------------------

    [Fact]
    public async Task InvokeAsync_ValidActiveTenantClaim_SetsTenantContextAndCallsNext()
    {
        await using var seedDb = CreateDb();
        var tenant = new Tenant { Name = "Acme", SchemaName = "tenant_mw_active", Status = "Active" };
        seedDb.Tenants.Add(tenant);
        await seedDb.SaveChangesAsync();

        var nextCalled = false;
        var middleware = new TenantContextMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            NullLogger<TenantContextMiddleware>.Instance);
        var tenantContext = new TenantContext();
        var httpContext = CreateHttpContext(tenant.Id);

        await using var requestDb = CreateDb();
        using var memCache = new MemoryCache(new MemoryCacheOptions());
        await middleware.InvokeAsync(httpContext, requestDb, new TenantLookupCache(memCache), tenantContext);

        Assert.True(nextCalled, "next() must be called for a valid, Active tenant claim.");
        Assert.Equal(tenant.Id, tenantContext.TenantId);
        Assert.Equal("tenant_mw_active", tenantContext.SchemaName);
        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
    }

    // --- Claim references a missing or non-Active tenant → 401 --------------------

    [Theory]
    [InlineData("Suspended")]
    [InlineData("Provisioning")]
    public async Task InvokeAsync_NonActiveTenant_Returns401_DoesNotCallNext(string status)
    {
        ArgumentNullException.ThrowIfNull(status);

        await using var seedDb = CreateDb();
        var tenant = new Tenant
        {
            Name = "Acme",
            SchemaName = $"tenant_mw_{status.ToLowerInvariant()}",
            Status = status,
        };
        seedDb.Tenants.Add(tenant);
        await seedDb.SaveChangesAsync();

        var nextCalled = false;
        var middleware = new TenantContextMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            NullLogger<TenantContextMiddleware>.Instance);
        var tenantContext = new TenantContext();
        var httpContext = CreateHttpContext(tenant.Id);

        await using var requestDb = CreateDb();
        using var memCache = new MemoryCache(new MemoryCacheOptions());
        await middleware.InvokeAsync(httpContext, requestDb, new TenantLookupCache(memCache), tenantContext);

        Assert.False(nextCalled, "next() must not be called when the tenant is not Active.");
        Assert.Equal(StatusCodes.Status401Unauthorized, httpContext.Response.StatusCode);
        Assert.Null(tenantContext.TenantId);
    }

    [Fact]
    public async Task InvokeAsync_MissingTenant_Returns401_DoesNotCallNext()
    {
        var nextCalled = false;
        var middleware = new TenantContextMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            NullLogger<TenantContextMiddleware>.Instance);
        var tenantContext = new TenantContext();
        var httpContext = CreateHttpContext(Guid.NewGuid()); // no such tenant row exists

        await using var requestDb = CreateDb();
        using var memCache = new MemoryCache(new MemoryCacheOptions());
        await middleware.InvokeAsync(httpContext, requestDb, new TenantLookupCache(memCache), tenantContext);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, httpContext.Response.StatusCode);
        Assert.Null(tenantContext.TenantId);
    }

    [Fact]
    public async Task InvokeAsync_MalformedTenantIdClaim_Returns401_DoesNotCallNext()
    {
        var nextCalled = false;
        var middleware = new TenantContextMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            NullLogger<TenantContextMiddleware>.Instance);
        var tenantContext = new TenantContext();
        var httpContext = new DefaultHttpContext();
        var identity = new ClaimsIdentity([new Claim("tenantId", "not-a-guid")], "TestAuth");
        httpContext.User = new ClaimsPrincipal(identity);

        await using var requestDb = CreateDb();
        using var memCache = new MemoryCache(new MemoryCacheOptions());
        await middleware.InvokeAsync(httpContext, requestDb, new TenantLookupCache(memCache), tenantContext);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, httpContext.Response.StatusCode);
    }

    // --- No tenantId claim → pass through untouched --------------------------------

    [Fact]
    public async Task InvokeAsync_NoTenantIdClaim_PassesThroughUntouched()
    {
        var nextCalled = false;
        var middleware = new TenantContextMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            NullLogger<TenantContextMiddleware>.Instance);
        var tenantContext = new TenantContext();
        var httpContext = new DefaultHttpContext(); // anonymous — no claims at all

        await using var requestDb = CreateDb();
        using var memCache = new MemoryCache(new MemoryCacheOptions());
        await middleware.InvokeAsync(httpContext, requestDb, new TenantLookupCache(memCache), tenantContext);

        Assert.True(nextCalled);
        Assert.Null(tenantContext.TenantId);
        Assert.Null(tenantContext.SchemaName);
        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
    }

    // --- Cache behavior --------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_ValidTenant_CachesLookup_SecondRequestSkipsDatabase()
    {
        await using var seedDb = CreateDb();
        var tenant = new Tenant { Name = "Acme", SchemaName = "tenant_mw_cache", Status = "Active" };
        seedDb.Tenants.Add(tenant);
        await seedDb.SaveChangesAsync();

        using var memCache = new MemoryCache(new MemoryCacheOptions());
        var cache = new TenantLookupCache(memCache);
        var middleware = new TenantContextMiddleware(
            _ => Task.CompletedTask,
            NullLogger<TenantContextMiddleware>.Instance);

        await using (var requestDb1 = CreateDb())
        {
            var tc1 = new TenantContext();
            await middleware.InvokeAsync(CreateHttpContext(tenant.Id), requestDb1, cache, tc1);
            Assert.Equal(tenant.SchemaName, tc1.SchemaName);
        }

        // Remove the underlying row entirely. If the second call actually hit the
        // database it would 401 (missing tenant); succeeding instead proves the cache
        // populated on the first call was actually consulted.
        await using (var deleteDb = CreateDb())
        {
            await deleteDb.Tenants.Where(t => t.Id == tenant.Id).ExecuteDeleteAsync();
        }

        await using (var requestDb2 = CreateDb())
        {
            var tc2 = new TenantContext();
            var httpContext2 = CreateHttpContext(tenant.Id);
            await middleware.InvokeAsync(httpContext2, requestDb2, cache, tc2);

            Assert.Equal(tenant.SchemaName, tc2.SchemaName);
            Assert.Equal(StatusCodes.Status200OK, httpContext2.Response.StatusCode);
        }
    }
}
