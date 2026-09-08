using System.Diagnostics.CodeAnalysis;
using System.IdentityModel.Tokens.Jwt;
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

// Story 12.3 — covers the LoginAsync extension: tenant_user_index match runs the
// schema-scoped credential check and stamps the JWT with tenantId; no match falls back
// to the pre-existing public.users check unchanged (proved by
// Login_EmailNotInTenantUserIndex_FallsBackToLegacyPublicUsersCheck coexisting with a
// real tenant row). Story 12.6 closed the refresh/logout accepted gap (the
// "{tenantId}.{secret}" cookie Decision) — Refresh_TenantUserToken_... and
// Logout_TenantUserToken_... below now exercise the end-to-end success path instead of
// the old NotFound/safe-no-op gap.
// Real Testcontainers Postgres throughout (via ITenantProvisioningService for the schema
// + full migration replay), matching TenantProvisioningServiceTests' "no mocks" posture.
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class TenantAwareLoginIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public TenantAwareLoginIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

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

        // tenant_user_index cascades from tenants (FK); refresh_tokens cascades from
        // users (FK) — CASCADE also sweeps any leftover rows in dependent tables not
        // listed explicitly, same pattern as AuthIntegrationTests.
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

    // Creates a real tenant row + real provisioned schema (Story 12.2's full static
    // migration replay), then marks it Active — mirrors what Story 12.7's onboarding
    // does, minus the parts (dataset namespace, grants, welcome email) irrelevant to
    // exercising LoginAsync.
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

    // Seeds a user directly into the tenant's own schema (SearchPath-scoped
    // FormForgeDbContext, same pattern as TenantOnboardingService) plus the
    // tenant_user_index routing row in public — the two things LoginAsync depends on.
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

    [Fact]
    public async Task Login_EmailInTenantUserIndex_Returns200_AndJwtCarriesTenantId()
    {
        var tenant = await ProvisionTenantAsync("tenant_login_happy");
        await SeedTenantUserAsync(tenant.SchemaName, tenant.Id, "admin@tenant-happy.example", "Password1!");

        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email = "admin@tenant-happy.example", password = "Password1!" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotNull(body);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(body!.AccessToken);
        var tenantIdClaim = jwt.Claims.FirstOrDefault(c => c.Type == "tenantId");
        Assert.NotNull(tenantIdClaim);
        Assert.Equal(tenant.Id.ToString(), tenantIdClaim!.Value);
    }

    [Fact]
    public async Task Login_TenantUser_WrongPassword_Returns401InvalidCredentials()
    {
        var tenant = await ProvisionTenantAsync("tenant_login_wrongpw");
        await SeedTenantUserAsync(tenant.SchemaName, tenant.Id, "admin@tenant-wrongpw.example", "Password1!");

        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email = "admin@tenant-wrongpw.example", password = "WrongPassword!" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("INVALID_CREDENTIALS", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_TenantUser_InactiveAccount_Returns403AccountInactive()
    {
        var tenant = await ProvisionTenantAsync("tenant_login_inactive");
        await SeedTenantUserAsync(tenant.SchemaName, tenant.Id, "admin@tenant-inactive.example", "Password1!");

        var csb = new NpgsqlConnectionStringBuilder(_postgres.ConnectionString) { SearchPath = tenant.SchemaName };
        await using (var tenantConnection = new NpgsqlConnection(csb.ConnectionString))
        {
            var options = new DbContextOptionsBuilder<FormForgeDbContext>().UseNpgsql(tenantConnection).Options;
            await using var tenantDb = new FormForgeDbContext(options);
            var user = await tenantDb.Users.FirstAsync(u => u.Email == "admin@tenant-inactive.example");
            user.IsActive = false;
            await tenantDb.SaveChangesAsync();
        }

        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email = "admin@tenant-inactive.example", password = "Password1!" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ACCOUNT_INACTIVE", body, StringComparison.Ordinal);
    }

    // Review fix — LoginAgainstTenantSchemaAsync must reject a Suspended/Provisioning
    // tenant the same way TenantContextMiddleware's later defense-in-depth check does;
    // otherwise a suspended tenant's admin could still log in and mint a valid JWT.
    [Fact]
    public async Task Login_TenantUser_SuspendedTenant_Returns401InvalidCredentials()
    {
        var tenant = await ProvisionTenantAsync("tenant_login_suspended");
        await SeedTenantUserAsync(tenant.SchemaName, tenant.Id, "admin@tenant-suspended.example", "Password1!");

        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var trackedTenant = await db.Tenants.FirstAsync(t => t.Id == tenant.Id);
            trackedTenant.Status = "Suspended";
            await db.SaveChangesAsync();
        }

        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email = "admin@tenant-suspended.example", password = "Password1!" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("INVALID_CREDENTIALS", body, StringComparison.Ordinal);
    }

    // Review fix — covers LoginAgainstTenantSchemaAsync's SafeIdentifier.TryCreate
    // failure branch: a tenant_user_index row pointing at a tenant whose schema_name is
    // corrupted/invalid must never be interpolated into a connection string, and must
    // fail the same way as ordinary invalid credentials. No real schema is provisioned
    // for this tenant — the failure must happen before any DB connection is attempted.
    [Fact]
    public async Task Login_TenantUserIndex_CorruptedSchemaName_Returns401InvalidCredentials()
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();

        var tenant = new Tenant { Name = "Corrupted", SchemaName = "Invalid-Schema-Name", Status = "Active" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        db.TenantUserIndex.Add(new TenantUserIndexEntry
        {
            Email = "admin@corrupted-schema.example",
            TenantId = tenant.Id,
        });
        await db.SaveChangesAsync();

        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email = "admin@corrupted-schema.example", password = "Password1!" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("INVALID_CREDENTIALS", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_EmailNotInTenantUserIndex_FallsBackToLegacyPublicUsersCheck()
    {
        // A real tenant row exists in the same DB — proves the index-miss branch is
        // unaffected by tenant rows existing elsewhere, and that the legacy public.users
        // path issues a claim-less JWT (no tenantId).
        await ProvisionTenantAsync("tenant_login_coexist");

        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            db.Users.Add(new User
            {
                Email = "legacy@example.com",
                DisplayName = "Legacy User",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password1!", 12),
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email = "legacy@example.com", password = "Password1!" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotNull(body);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(body!.AccessToken);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "tenantId");
    }

    // Story 12.6 — closes the Story 12.3 accepted gap: the refresh-token cookie now
    // encodes "{tenantId}.{secret}" (Decision), so RefreshAsync can resolve the tenant's
    // own schema and find the row IssueLoginTokensAsync wrote there. Refresh now
    // succeeds end-to-end for a tenant user, and the rotated access token still carries
    // the tenantId claim.
    [Fact]
    public async Task Refresh_TenantUserToken_Returns200_RotatesTokens_AndJwtCarriesTenantId()
    {
        var tenant = await ProvisionTenantAsync("tenant_login_refresh");
        await SeedTenantUserAsync(tenant.SchemaName, tenant.Id, "admin@tenant-refresh.example", "Password1!");

        using var loginResponse = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email = "admin@tenant-refresh.example", password = "Password1!" });
        loginResponse.EnsureSuccessStatusCode();
        var loginBody = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotNull(loginBody);
        Assert.StartsWith($"{tenant.Id}.", loginBody!.RefreshToken, StringComparison.Ordinal);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        request.Headers.Add("Cookie", $"refresh_token={loginBody.RefreshToken}");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotNull(body);
        Assert.StartsWith($"{tenant.Id}.", body!.RefreshToken, StringComparison.Ordinal);
        Assert.NotEqual(loginBody.RefreshToken, body.RefreshToken);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(body.AccessToken);
        var tenantIdClaim = jwt.Claims.FirstOrDefault(c => c.Type == "tenantId");
        Assert.NotNull(tenantIdClaim);
        Assert.Equal(tenant.Id.ToString(), tenantIdClaim!.Value);
    }

    // Story 12.6 — same closed gap applies to LogoutAsync: the tenant-schema token is
    // now actually resolved and revoked server-side, not just safely ignored.
    [Fact]
    public async Task Logout_TenantUserToken_Returns204_AndRevokesTokenInTenantSchema()
    {
        var tenant = await ProvisionTenantAsync("tenant_login_logout");
        await SeedTenantUserAsync(tenant.SchemaName, tenant.Id, "admin@tenant-logout.example", "Password1!");

        using var loginResponse = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email = "admin@tenant-logout.example", password = "Password1!" });
        loginResponse.EnsureSuccessStatusCode();
        var loginBody = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotNull(loginBody);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Add("Cookie", $"refresh_token={loginBody!.RefreshToken}");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var csb = new NpgsqlConnectionStringBuilder(_postgres.ConnectionString) { SearchPath = tenant.SchemaName };
        await using var tenantConnection = new NpgsqlConnection(csb.ConnectionString);
        var options = new DbContextOptionsBuilder<FormForgeDbContext>().UseNpgsql(tenantConnection).Options;
        await using var tenantDb = new FormForgeDbContext(options);
        var token = await tenantDb.RefreshTokens.AsNoTracking().SingleAsync();
        Assert.NotNull(token.RevokedAt);
    }

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record LoginResponseDto(string AccessToken, string RefreshToken, int ExpiresIn);
}
