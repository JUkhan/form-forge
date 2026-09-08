using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Features.Tenancy;
using FormForge.Api.Infrastructure.Persistence;
using FormForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FormForge.Api.Tests.Features.Auth;

// Story 12.6 (Decision) — covers AuthService's "{tenantId}.{secret}" refresh-token
// cookie format end-to-end: a valid tenant prefix resolves and mutates that tenant's
// own refresh_tokens row (closing Story 12.3's accepted gap — see
// TenantAwareLoginIntegrationTests for the full happy-path assertions on the rotated
// token/JWT shape), while a malformed prefix, an unknown tenant, a non-Active tenant,
// or a legacy pre-migration cookie (no "." at all) all collapse to the exact same
// REFRESH_TOKEN_INVALID / 401 envelope as an ordinary unmatched token — never a
// distinguishable error. Real Testcontainers Postgres throughout, same "no mocks"
// posture as TenantAwareLoginIntegrationTests.
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class AuthServiceRefreshTenantTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public AuthServiceRefreshTenantTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:formforge", _postgres.ConnectionString);
                builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
            });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        await db.Database.MigrateAsync();

        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE tenant_user_index, tenants, refresh_tokens, users, platform_admins RESTART IDENTITY CASCADE;");

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    // Mirrors TenantAwareLoginIntegrationTests.ProvisionTenantAsync — real tenant row +
    // real provisioned schema, marked Active.
    private async Task<Tenant> ProvisionTenantAsync(string schemaName)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var provisioningSvc = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();

        var tenant = new Tenant { Name = $"Tenant {schemaName}", SchemaName = schemaName, Status = "Provisioning" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        await provisioningSvc.ProvisionSchemaAsync(tenant, CancellationToken.None);

        tenant.Status = "Active";
        await db.SaveChangesAsync();

        return tenant;
    }

    private async Task SeedTenantUserAsync(string schemaName, Guid tenantId, string email, string password)
    {
        var csb = new NpgsqlConnectionStringBuilder(_postgres.ConnectionString) { SearchPath = schemaName };
        await using var tenantConnection = new NpgsqlConnection(csb.ConnectionString);
        var options = new DbContextOptionsBuilder<FormForgeDbContext>().UseNpgsql(tenantConnection).Options;
        await using (var tenantDb = new FormForgeDbContext(options))
        {
            tenantDb.Users.Add(new User
            {
                Email = email,
                DisplayName = "Tenant Admin",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, 12),
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await tenantDb.SaveChangesAsync();
        }

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        db.TenantUserIndex.Add(new TenantUserIndexEntry { Email = email, TenantId = tenantId });
        await db.SaveChangesAsync();
    }

    private async Task<HttpResponseMessage> RefreshWithCookieAsync(string cookieValue)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        request.Headers.Add("Cookie", $"refresh_token={cookieValue}");
        return await _client!.SendAsync(request);
    }

    private static async Task AssertRefreshTokenInvalidAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("REFRESH_TOKEN_INVALID", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_MalformedTenantIdPrefix_ReturnsRefreshTokenInvalid()
    {
        using var response = await RefreshWithCookieAsync("not-a-guid.some-secret-value");
        await AssertRefreshTokenInvalidAsync(response);
    }

    [Fact]
    public async Task Refresh_UnknownTenantId_ReturnsRefreshTokenInvalid()
    {
        using var response = await RefreshWithCookieAsync($"{Guid.NewGuid()}.some-secret-value");
        await AssertRefreshTokenInvalidAsync(response);
    }

    [Fact]
    public async Task Refresh_NonActiveTenant_ReturnsRefreshTokenInvalid()
    {
        var tenant = await ProvisionTenantAsync("tenant_refresh_suspended");
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var tracked = await db.Tenants.FirstAsync(t => t.Id == tenant.Id);
            tracked.Status = "Suspended";
            await db.SaveChangesAsync();
        }

        using var response = await RefreshWithCookieAsync($"{tenant.Id}.some-secret-value");
        await AssertRefreshTokenInvalidAsync(response);
    }

    [Fact]
    public async Task Refresh_LegacyPreMigrationCookie_NoDotAtAll_ReturnsRefreshTokenInvalid()
    {
        using var response = await RefreshWithCookieAsync("bare-secret-with-no-tenant-prefix-at-all");
        await AssertRefreshTokenInvalidAsync(response);
    }

    [Fact]
    public async Task Refresh_EmptySecretAfterValidTenantPrefix_ReturnsRefreshTokenInvalid()
    {
        // A well-formed tenant prefix but a secret that matches no row must fail exactly
        // like any other unmatched token — never a distinguishable error, and never a
        // crash on a degenerate ("{tenantId}.") cookie value.
        var tenant = await ProvisionTenantAsync("tenant_refresh_empty_secret");
        using var response = await RefreshWithCookieAsync($"{tenant.Id}.");
        await AssertRefreshTokenInvalidAsync(response);
    }

    [Fact]
    public async Task RefreshAndLogout_TenantUser_Succeed_AgainstTenantsOwnSchema()
    {
        var tenant = await ProvisionTenantAsync("tenant_refresh_happy");
        await SeedTenantUserAsync(tenant.SchemaName, tenant.Id, "admin@tenant-refresh-happy.example", "Password1!");

        using var loginResponse = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email = "admin@tenant-refresh-happy.example", password = "Password1!" });
        loginResponse.EnsureSuccessStatusCode();
        var loginBody = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotNull(loginBody);
        Assert.StartsWith($"{tenant.Id}.", loginBody!.RefreshToken, StringComparison.Ordinal);

        using var refreshResponse = await RefreshWithCookieAsync(loginBody.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
        var refreshBody = await refreshResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotNull(refreshBody);
        Assert.StartsWith($"{tenant.Id}.", refreshBody!.RefreshToken, StringComparison.Ordinal);

        // The old (pre-rotation) token must now be Replayed, not Success — proves rotation
        // actually happened against the tenant's own refresh_tokens row, not a no-op.
        using var replayResponse = await RefreshWithCookieAsync(loginBody.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, replayResponse.StatusCode);

        using var logoutResponse = await LogoutWithCookieAsync(refreshBody.RefreshToken);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);
    }

    private async Task<HttpResponseMessage> LogoutWithCookieAsync(string cookieValue)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Add("Cookie", $"refresh_token={cookieValue}");
        return await _client!.SendAsync(request);
    }

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record LoginResponseDto(string AccessToken, string RefreshToken, int ExpiresIn);
}
