using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Infrastructure.Persistence;
using FormForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FormForge.Api.Tests.Features.Tenancy;

// Story 12.5 — end-to-end coverage for the new platform-super-admin-only
// /api/admin/tenants group: list + create, the group-level RequirePlatformSuperAdmin()
// gate, the "no row created" guarantee for an invalid/colliding schema_name, and the
// endpoint's own catch-block Decision (mid-flow provisioning/onboarding failure sets
// Tenant.Status = "Error" and returns 500). Real Testcontainers Postgres throughout —
// no mocks — same posture as TenantOnboardingServiceTests/TenantProvisioningServiceTests.
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class TenantEndpointsIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    // Story 2.6's deterministic system-role id, seeded into public.roles by the static
    // migration set (CreateRolesRolePermissionsAndUserRoles) — same constant
    // RoleIntegrationTests uses. This test class never truncates roles/user_roles, so
    // the seeded row survives from the InitializeAsync migrate step untouched.
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");

    private const string SuperAdminEmail = "tenants.super@formforge.test";
    private const string SuperAdminPassword = "SuperSecret1!";
    private const string PlainUserEmail = "plain.user@example.com";
    private const string PlainUserPassword = "Password1!";
    private const string TenantAdminRoleUserEmail = "tenant.admin.role@example.com";
    private const string TenantAdminRoleUserPassword = "Password1!";

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public TenantEndpointsIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

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
            "TRUNCATE TABLE tenants, platform_admins, tenant_user_index, refresh_tokens, users RESTART IDENTITY CASCADE;");

        db.PlatformAdmins.Add(new PlatformAdmin
        {
            UserEmail = SuperAdminEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(SuperAdminPassword, 12),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.Users.Add(new User
        {
            Email = PlainUserEmail,
            DisplayName = "Plain User",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(PlainUserPassword, 12),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var tenantAdminRoleUser = new User
        {
            Email = TenantAdminRoleUserEmail,
            DisplayName = "Tenant Admin Role User",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(TenantAdminRoleUserPassword, 12),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(tenantAdminRoleUser);
        await db.SaveChangesAsync();

        // "platform-admin" (tenant-admin tier, Story 2.6) — distinct from
        // "platform-super-admin" (Story 12.4). RequirePlatformSuperAdmin() must deny
        // this role too, not just a roleless user.
        db.UserRoles.Add(new UserRole
        {
            UserId = tenantAdminRoleUser.Id,
            RoleId = PlatformAdminRoleId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

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

    // ---------- GET /api/admin/tenants — auth gate ----------

    [Fact]
    public async Task GetTenants_Unauthenticated_Returns401()
    {
        using var response = await _client!.GetAsync(new Uri("/api/admin/tenants", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetTenants_AsNonSuperAdmin_Returns403()
    {
        var token = await LoginAsync(PlainUserEmail, PlainUserPassword);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/tenants");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetTenants_AsPlatformAdminRole_Returns403()
    {
        // "platform-admin" (tenant-admin tier) is a different role from
        // "platform-super-admin" — RequirePlatformSuperAdmin() must reject it too.
        var token = await LoginAsync(TenantAdminRoleUserEmail, TenantAdminRoleUserPassword);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/tenants");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- POST /api/admin/tenants — auth gate ----------

    [Fact]
    public async Task CreateTenant_Unauthenticated_Returns401()
    {
        using var response = await _client!.PostAsJsonAsync("/api/admin/tenants",
            new { name = "Acme", schemaName = "tenant_acme_unauth" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateTenant_AsNonSuperAdmin_Returns403()
    {
        var token = await LoginAsync(PlainUserEmail, PlainUserPassword);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants")
        {
            Content = JsonContent.Create(new { name = "Acme", schemaName = "tenant_acme_forbidden" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateTenant_AsPlatformAdminRole_Returns403()
    {
        var token = await LoginAsync(TenantAdminRoleUserEmail, TenantAdminRoleUserPassword);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants")
        {
            Content = JsonContent.Create(new { name = "Acme", schemaName = "tenant_acme_role_forbidden" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- GET /api/admin/tenants — happy path ----------

    [Fact]
    public async Task GetTenants_AsPlatformSuperAdmin_Returns200WithTenantFields()
    {
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            db.Tenants.Add(new Tenant
            {
                Name = "Seeded Tenant",
                SchemaName = "tenant_seeded_list",
                Status = "Active",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var token = await LoginAsync(SuperAdminEmail, SuperAdminPassword);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/tenants?page=1&pageSize=25");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PagedResultDto<TenantDto>>();
        Assert.NotNull(body);
        Assert.Equal(1, body.Total);
        var tenant = body.Data.Single();
        Assert.Equal("Seeded Tenant", tenant.Name);
        Assert.Equal("tenant_seeded_list", tenant.SchemaName);
        Assert.Equal("Active", tenant.Status);
    }

    // ---------- POST /api/admin/tenants — create outcomes ----------

    [Fact]
    public async Task CreateTenant_ValidInput_ProvisionsAndOnboardsSynchronously_Returns201Active()
    {
        var token = await LoginAsync(SuperAdminEmail, SuperAdminPassword);
        const string schemaName = "tenant_e2e_create_ok";

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants")
        {
            Content = JsonContent.Create(new { name = "End To End Co", schemaName }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var body = await response.Content.ReadFromJsonAsync<CreateTenantResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("End To End Co", body.Tenant.Name);
        Assert.Equal(schemaName, body.Tenant.SchemaName);
        // Create-tenant is synchronous (Intent): by the time the response comes back,
        // ProvisionSchemaAsync + OnboardTenantAsync have both already completed, so the
        // row's status is already resolved to its terminal state — no separate poll is
        // needed to observe it here (the frontend's bounded poll exists for UX/defense
        // in depth, not because the backend is async).
        Assert.Equal("Active", body.Tenant.Status);
        Assert.False(string.IsNullOrWhiteSpace(body.TemporaryPassword));
        Assert.True(body.TemporaryPassword.Length >= 8);

        // The row is visible (and Active) on the very next GET — matrix's "poll after
        // create" row, trivially satisfied by the synchronous design.
        using var listRequest = new HttpRequestMessage(HttpMethod.Get, "/api/admin/tenants");
        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var listResponse = await _client!.SendAsync(listRequest);
        var list = await listResponse.Content.ReadFromJsonAsync<PagedResultDto<TenantDto>>();
        Assert.Contains(list!.Data, t => t.SchemaName == schemaName && t.Status == "Active");
    }

    [Fact]
    public async Task CreateTenant_SchemaNameContainsUnderscore_DerivedAdminEmailIsLoginable()
    {
        // SafeIdentifier allows '_' in schemaName, but '_' is not a valid domain-label
        // character: browsers' native <input type="email"> validation (WHATWG) rejects an
        // email like "admin@test_1.tenant.local", blocking the derived admin from ever
        // signing in via the login form. This guards TenantEndpoints.ToEmailDomainLabel's
        // '_' -> '-' mapping end-to-end: create with an underscored schema name, then
        // actually log in as the derived admin.
        var token = await LoginAsync(SuperAdminEmail, SuperAdminPassword);
        const string schemaName = "tenant_underscore_1";

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants")
        {
            Content = JsonContent.Create(new { name = "Underscore Co", schemaName }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateTenantResponseDto>();
        Assert.NotNull(body);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var tenantId = await db.Tenants.AsNoTracking()
            .Where(t => t.SchemaName == schemaName)
            .Select(t => t.Id)
            .SingleAsync();
        var indexEntry = await db.Set<TenantUserIndexEntry>().AsNoTracking()
            .SingleAsync(e => e.TenantId == tenantId);
        var domainPart = indexEntry.Email.Split('@')[1];
        Assert.DoesNotContain('_', domainPart);
        Assert.Equal("admin@tenant-underscore-1.tenant.local", indexEntry.Email);

        // The whole point: the derived admin can actually log in with this email.
        var adminToken = await LoginAsync(indexEntry.Email, body!.TemporaryPassword);
        Assert.False(string.IsNullOrWhiteSpace(adminToken));
    }

    [Fact]
    public async Task CreateTenant_EmptyName_Returns422ValidationProblem()
    {
        var token = await LoginAsync(SuperAdminEmail, SuperAdminPassword);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants")
        {
            Content = JsonContent.Create(new { name = string.Empty, schemaName = "tenant_empty_name" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreateTenant_InvalidSchemaNameFormat_Returns400AndCreatesNoRow()
    {
        var token = await LoginAsync(SuperAdminEmail, SuperAdminPassword);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants")
        {
            // Uppercase + hyphen fails SafeIdentifier's ^[a-z_][a-z0-9_]{0,62}$ pattern.
            Content = JsonContent.Create(new { name = "Bad Schema Co", schemaName = "Invalid-Schema-Name" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("TENANT_SCHEMA_NAME_INVALID", body, StringComparison.Ordinal);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        Assert.False(await db.Tenants.AnyAsync(t => t.Name == "Bad Schema Co"));
    }

    [Fact]
    public async Task CreateTenant_ReservedPgKeyword_Returns400AndCreatesNoRow()
    {
        var token = await LoginAsync(SuperAdminEmail, SuperAdminPassword);

        // "select" passes SafeIdentifier's structural regex (lowercase, fits length)
        // but is a genuine PostgreSQL reserved keyword — mirrors
        // DesignerIntegrationTests.CreateDesigner_ReservedPgKeyword_Returns422_IdentifierReservedKeyword's
        // pattern. Distinct code path from the "public"-style system-schema check
        // (TenantProvisioningService.IsReservedSchemaName): this one is caught by
        // SafeIdentifier.TryCreate itself (SafeIdentifierError.ReservedKeyword).
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants")
        {
            Content = JsonContent.Create(new { name = "Reserved Keyword Co", schemaName = "select" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("TENANT_SCHEMA_NAME_RESERVED", body, StringComparison.Ordinal);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        Assert.False(await db.Tenants.AnyAsync(t => t.Name == "Reserved Keyword Co"));
    }

    [Fact]
    public async Task CreateTenant_SchemaNameCollision_Returns409AndCreatesNoSecondRow()
    {
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            db.Tenants.Add(new Tenant
            {
                Name = "Existing Tenant",
                SchemaName = "tenant_collision_seed",
                Status = "Active",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var token = await LoginAsync(SuperAdminEmail, SuperAdminPassword);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants")
        {
            Content = JsonContent.Create(new { name = "Colliding Tenant", schemaName = "tenant_collision_seed" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("TENANT_SCHEMA_NAME_CONFLICT", body, StringComparison.Ordinal);

        using var verifyScope = _factory!.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        Assert.Equal(1, await verifyDb.Tenants.CountAsync(t => t.SchemaName == "tenant_collision_seed"));
    }

    [Fact]
    public async Task CreateTenant_ProvisioningFailsMidFlow_SetsStatusErrorAndReturns500()
    {
        var token = await LoginAsync(SuperAdminEmail, SuperAdminPassword);
        const string schemaName = "tenant_midflow_fail";

        // Pre-create the tenant's dataset-namespace schema so ProvisionSchemaAsync
        // itself succeeds (CREATE SCHEMA "tenant_midflow_fail" is untouched) but
        // OnboardTenantAsync's own "CREATE SCHEMA {schemaName}_datasets" collides and
        // throws — same deterministic fault-injection TenantOnboardingServiceTests'
        // OnboardTenantAsync_DdlStepFails_... test uses. This only throws AFTER the
        // row is already inserted at status="Provisioning", making it a genuine
        // mid-flow failure that exercises the endpoint's own catch block (Decision) —
        // distinct from the pre-insert SafeIdentifier / IsReservedSchemaName checks.
        await using (var admin = new NpgsqlConnection(_postgres.ConnectionString))
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync($"""CREATE SCHEMA "{schemaName}_datasets" """);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants")
        {
            Content = JsonContent.Create(new { name = "Mid Flow Fail Co", schemaName }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("TENANT_PROVISIONING_FAILED", body, StringComparison.Ordinal);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var row = await db.Tenants.AsNoTracking().SingleAsync(t => t.SchemaName == schemaName);
        Assert.Equal("Error", row.Status);
    }

    // ---------- Helpers ----------

    private async Task<string> LoginAsync(string email, string password)
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login", new { email, password });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        return body!.AccessToken;
    }

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record LoginResponseDto(string AccessToken, string? RefreshToken, int ExpiresIn);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record PagedResultDto<T>(IReadOnlyList<T> Data, long Total, int Page, int PageSize, int TotalPages);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record TenantDto(Guid Id, string Name, string SchemaName, string Status, DateTimeOffset CreatedAt);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record CreateTenantResponseDto(TenantDto Tenant, string TemporaryPassword);
}
