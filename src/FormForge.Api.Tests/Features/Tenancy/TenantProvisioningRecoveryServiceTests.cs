using System.Diagnostics.CodeAnalysis;
using System.Net;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Features.Tenancy;
using FormForge.Api.Infrastructure.Persistence;
using FormForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FormForge.Api.Tests.Features.Tenancy;

// Story 12.7 (FR-75 AC-3) — exercises TenantProvisioningRecoveryService's startup scan
// against a real Testcontainers Postgres. The hosted-service registration in Program.cs is
// removed for these tests (ConfigureServices below) so each test controls exactly when the
// scan runs relative to seeding — the real registration would otherwise race the scan
// against the WebApplicationFactory's own host-startup timing. Each test then constructs
// and drives its own instance directly via IHostedService's StartAsync/ExecuteTask, mirroring
// the only reliable way to await a BackgroundService's ExecuteAsync to completion in tests.
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class TenantProvisioningRecoveryServiceTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;

    public TenantProvisioningRecoveryServiceTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:formforge", _postgres.ConnectionString);
                builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
                builder.ConfigureServices(services =>
                {
                    var recovery = services.FirstOrDefault(
                        d => d.ImplementationType == typeof(TenantProvisioningRecoveryService));
                    if (recovery is not null)
                    {
                        services.Remove(recovery);
                    }
                });
            });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE tenants RESTART IDENTITY CASCADE;");
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecuteAsync_TenantStuckAtProvisioning_LogsWarningAndNeverMutatesOrRunsDdl()
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var scopeFactory = scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>();

        var stuck = new Tenant { Name = "Stuck Co", SchemaName = "tenant_recovery_stuck", Status = "Provisioning" };
        var active = new Tenant { Name = "Fine Co", SchemaName = "tenant_recovery_fine", Status = "Active" };
        db.Tenants.AddRange(stuck, active);
        await db.SaveChangesAsync();

        var fakeLogger = new ListLogger<TenantProvisioningRecoveryService>();
        var service = new TenantProvisioningRecoveryService(scopeFactory, fakeLogger);

        await RunToCompletionAsync(service);

        // Logged at Warning with TenantId and SchemaName for the stuck tenant only.
        var warnings = fakeLogger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        var warning = Assert.Single(warnings);
        Assert.Contains(stuck.Id.ToString(), warning.Message, StringComparison.Ordinal);
        Assert.Contains(stuck.SchemaName, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(active.Id.ToString(), warning.Message, StringComparison.Ordinal);

        // No status mutation, no DDL: the stuck tenant is still Provisioning and its
        // schema was never created.
        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        Assert.Equal("Provisioning", (await verifyDb.Tenants.SingleAsync(t => t.Id == stuck.Id)).Status);
        Assert.Equal("Active", (await verifyDb.Tenants.SingleAsync(t => t.Id == active.Id)).Status);
    }

    [Fact]
    public async Task ExecuteAsync_NoTenantsStuck_LogsNoWarnings()
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var scopeFactory = scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>();

        db.Tenants.Add(new Tenant { Name = "Fine Co", SchemaName = "tenant_recovery_clean", Status = "Active" });
        await db.SaveChangesAsync();

        var fakeLogger = new ListLogger<TenantProvisioningRecoveryService>();
        var service = new TenantProvisioningRecoveryService(scopeFactory, fakeLogger);

        await RunToCompletionAsync(service);

        Assert.DoesNotContain(fakeLogger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Recovery_RealHostedServiceRegistration_TenantStuckAtProvisioning_HostStartsAndServesRequests()
    {
        // Unlike every test above (which removes the real AddHostedService<TenantProvisioningRecoveryService>()
        // registration to drive a manually-constructed instance), this test leaves Program.cs's actual
        // wiring intact end-to-end — mirroring the precedent ProvisioningRecoveryIntegrationTests sets for
        // the sibling ProvisioningRecoveryService (specifically Recovery_ScanFails_HostStartsAndServesRequests).
        // The tenant is seeded via _factory (hosted service removed there) so seeding never races the real
        // scan; a second, fully-wired factory then starts with the real registration active, and the test
        // confirms the app still comes up and serves requests with a tenant stuck at 'Provisioning'.
        Guid tenantId;
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var tenant = new Tenant
            {
                Name = "Real Wiring Co",
                SchemaName = "tenant_recovery_real_wiring",
                Status = "Provisioning",
            };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
            tenantId = tenant.Id;
        }

        var realFactory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:formforge", _postgres.ConnectionString);
                builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
                // No ConfigureServices override here — the real AddHostedService<TenantProvisioningRecoveryService>()
                // registration from Program.cs runs its startup scan against the tenant seeded above.
            });
        try
        {
            using var client = realFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Flag-only: the real scan must not have mutated the stuck tenant's status.
            using var verifyScope = realFactory.Services.CreateScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var reloaded = await verifyDb.Tenants.SingleAsync(t => t.Id == tenantId);
            Assert.Equal("Provisioning", reloaded.Status);
        }
        finally
        {
            await realFactory.DisposeAsync();
        }
    }

    // BackgroundService.StartAsync only awaits ExecuteAsync up to its first genuine
    // await point (by design, so a long-running loop doesn't block host startup) — it does
    // NOT wait for the scan to finish. ExecuteTask is the documented way to await full
    // completion from a test.
    private static async Task RunToCompletionAsync(TenantProvisioningRecoveryService service)
    {
        await service.StartAsync(CancellationToken.None);
        if (service.ExecuteTask is not null)
        {
            await service.ExecuteTask;
        }

        await service.StopAsync(CancellationToken.None);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
