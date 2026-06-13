using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Infrastructure.Persistence;
using FormForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FormForge.Api.Tests.Features.Permissions;

[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class PermissionsIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    private Guid _adminUserId;
    private Guid _viewerUserId;

    public PermissionsIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:formforge", _postgres.ConnectionString);
                builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");

                // /health auth test (AC-4) requires HTTP 200. MinIO isn't running
                // in the test environment, so strip the MinIO registration and
                // leave only the postgres check (testcontainer is up). The
                // /health auth tests only care about 401/403/200 status, not
                // which dependency was checked.
                builder.ConfigureTestServices(services =>
                {
                    services.Configure<HealthCheckServiceOptions>(options =>
                    {
                        var minio = options.Registrations
                            .FirstOrDefault(r => string.Equals(r.Name, "minio", StringComparison.Ordinal));
                        if (minio is not null)
                        {
                            options.Registrations.Remove(minio);
                        }
                    });
                });
            });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        await db.Database.MigrateAsync();

        // TRUNCATE order (FK chain): role_permissions → user_roles → roles → refresh_tokens → users.
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE role_permissions, user_roles, roles, refresh_tokens, users RESTART IDENTITY CASCADE;");

        await ReseedSystemRolesAsync(db);
        (_adminUserId, _viewerUserId) = await SeedTestUsersAsync(db);

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

    // ---------- AC-1: GET /api/users/me/permissions ----------

    [Fact]
    public async Task GetMyPermissions_Unauthenticated_Returns401()
    {
        using var response = await _client!.GetAsync(new Uri("/api/users/me/permissions", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetMyPermissions_NoRoles_ReturnsEmptyPerResource()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/users/me/permissions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PermissionsResponseDto>();
        Assert.NotNull(body);
        Assert.Equal(_viewerUserId, body.UserId);
        Assert.True(body.IsActive);
        Assert.Empty(body.PerResource);
        Assert.Empty(body.RoleIds);
    }

    [Fact]
    public async Task GetMyPermissions_ViewerWithResourcePermission_ReturnsCorrectFlags()
    {
        // Seed: viewer role gets canRead on "test-form".
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            db.RolePermissions.Add(new RolePermission
            {
                RoleId = ViewerRoleId,
                ResourceId = "test-form",
                CanCreate = false,
                CanRead = true,
                CanUpdate = false,
                CanDelete = false,
            });
            await db.SaveChangesAsync();
        }

        // Promote viewer to the viewer role via the admin endpoint (also exercises
        // UserRoleAssignmentChanged → cache bust on the freshly-seeded user).
        var adminToken = await LoginAsync("admin@example.com", "Password1!");
        await AssignRolesAsync(adminToken, _viewerUserId, new[] { ViewerRoleId });

        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/users/me/permissions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PermissionsResponseDto>();
        Assert.NotNull(body);
        Assert.True(body.PerResource.ContainsKey("test-form"));
        Assert.True(body.PerResource["test-form"].CanRead);
        Assert.False(body.PerResource["test-form"].CanCreate);
        Assert.False(body.PerResource["test-form"].CanUpdate);
        Assert.False(body.PerResource["test-form"].CanDelete);
        Assert.Contains(ViewerRoleId, body.RoleIds);
    }

    // ---------- AC-2: Platform-admin role indicator ----------

    [Fact]
    public async Task GetMyPermissions_PlatformAdmin_Returns200WithPlatformAdminRoleId()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/users/me/permissions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PermissionsResponseDto>();
        Assert.NotNull(body);
        Assert.Equal(_adminUserId, body.UserId);
        Assert.Contains(PlatformAdminRoleId, body.RoleIds);
    }

    // ---------- AC-5: Cache busted on role assignment ----------

    [Fact]
    public async Task GetMyPermissions_CacheBustedAfterRoleAssignment()
    {
        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");

        // First call: viewer has no roles, perResource is empty.
        using (var initialRequest = new HttpRequestMessage(HttpMethod.Get, "/api/users/me/permissions"))
        {
            initialRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
            using var initialResponse = await _client!.SendAsync(initialRequest);
            Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
            var initial = await initialResponse.Content.ReadFromJsonAsync<PermissionsResponseDto>();
            Assert.NotNull(initial);
            Assert.Empty(initial.PerResource);
        }

        // Mutate: create a role with canRead on "resource-x" and assign it to viewer.
        // PUT /api/admin/users/{id}/roles publishes UserRoleAssignmentChanged →
        // PermissionService.OnUserRoleAssignmentChanged removes the cache entry.
        Guid newRoleId;
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var role = new Role
            {
                Name = "cache-bust-role",
                IsSystem = false,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            role.Permissions.Add(new RolePermission
            {
                ResourceId = "resource-x",
                CanRead = true,
            });
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            newRoleId = role.Id;
        }

        var adminToken = await LoginAsync("admin@example.com", "Password1!");
        await AssignRolesAsync(adminToken, _viewerUserId, new[] { newRoleId });

        // Second call: cache was busted, fresh compute returns "resource-x".
        using var followupRequest = new HttpRequestMessage(HttpMethod.Get, "/api/users/me/permissions");
        followupRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
        using var followupResponse = await _client!.SendAsync(followupRequest);

        Assert.Equal(HttpStatusCode.OK, followupResponse.StatusCode);
        var followup = await followupResponse.Content.ReadFromJsonAsync<PermissionsResponseDto>();
        Assert.NotNull(followup);
        Assert.True(followup.PerResource.ContainsKey("resource-x"));
        Assert.True(followup.PerResource["resource-x"].CanRead);
    }

    // ---------- AC-6: RolePermissionsChanged bust ----------

    [Fact]
    public async Task GetMyPermissions_CacheBustedAfterRolePermissionsUpdated()
    {
        // Bmad review: AC-6 demands that PUT /api/admin/roles/{id} (which updates
        // role_permissions) busts cached permissions of every user holding that
        // role. Without this, role-permission edits would be invisible to the
        // SPA for up to 30 s (cache TTL). The cache-bust test above covers
        // UserRoleAssignmentChanged; this covers RolePermissionsChanged.

        // Seed: create a non-system role with canRead on "resource_y" and
        // assign it to the viewer.
        Guid roleId;
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var role = new Role
            {
                Name = "role-bust-target",
                IsSystem = false,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            role.Permissions.Add(new RolePermission
            {
                ResourceId = "resource_y",
                CanRead = true,
            });
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            roleId = role.Id;

            db.UserRoles.Add(new UserRole
            {
                UserId = _viewerUserId,
                RoleId = roleId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");

        // Warm cache: viewer sees canRead on resource_y, no canCreate.
        using (var initialRequest = new HttpRequestMessage(HttpMethod.Get, "/api/users/me/permissions"))
        {
            initialRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
            using var initialResponse = await _client!.SendAsync(initialRequest);
            Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
            var initial = await initialResponse.Content.ReadFromJsonAsync<PermissionsResponseDto>();
            Assert.NotNull(initial);
            Assert.True(initial.PerResource.ContainsKey("resource_y"));
            Assert.True(initial.PerResource["resource_y"].CanRead);
            Assert.False(initial.PerResource["resource_y"].CanCreate);
        }

        // Mutate: admin PUTs the role with a new permission set that adds
        // canCreate. RoleService.UpdateRoleAsync publishes RolePermissionsChanged
        // → PermissionService.OnRolePermissionsChanged removes viewer from cache.
        var adminToken = await LoginAsync("admin@example.com", "Password1!");
        using (var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/roles/{roleId}")
        {
            Content = JsonContent.Create(new
            {
                name = "role-bust-target",
                description = (string?)null,
                permissions = new[]
                {
                    new { resourceId = "resource_y", canCreate = true, canRead = true, canUpdate = false, canDelete = false },
                },
            }),
        })
        {
            updateRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            using var updateResponse = await _client!.SendAsync(updateRequest);
            Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);
        }

        // Re-read: cache was busted, viewer now sees canCreate=true.
        using var followupRequest = new HttpRequestMessage(HttpMethod.Get, "/api/users/me/permissions");
        followupRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
        using var followupResponse = await _client!.SendAsync(followupRequest);

        Assert.Equal(HttpStatusCode.OK, followupResponse.StatusCode);
        var followup = await followupResponse.Content.ReadFromJsonAsync<PermissionsResponseDto>();
        Assert.NotNull(followup);
        Assert.True(followup.PerResource.ContainsKey("resource_y"));
        Assert.True(followup.PerResource["resource_y"].CanCreate,
            "RolePermissionsChanged failed to bust cache: viewer still sees canCreate=false 30s after admin's role update");
    }

    // ---------- AC-4: /health requires platform-admin ----------

    [Fact]
    public async Task Health_Unauthenticated_Returns401()
    {
        using var response = await _client!.GetAsync(new Uri("/health", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_AsViewer_Returns403()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Health_AsPlatformAdmin_Returns200()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Story 2.6 review patch #6: replace the structural assertions that
        // HealthCheckEndpointsTests.HealthEndpoint_Returns503_WhenDependenciesUnavailable
        // used to make on the /health JSON. InitializeAsync stripped the MinIO check,
        // so only the "postgres" registration must round-trip through HealthCheckJsonWriter.
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("status", out _),
            "Expected 'status' key in /health JSON");
        Assert.True(doc.RootElement.TryGetProperty("checks", out var checks),
            "Expected 'checks' key in /health JSON");
        Assert.True(checks.TryGetProperty("postgres", out _),
            "Expected 'postgres' entry in /health checks");
    }

    // ---------- Helpers ----------

    private async Task<string> LoginAsync(string email, string password)
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email, password });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        return body!.AccessToken;
    }

    private async Task AssignRolesAsync(string adminToken, Guid userId, IReadOnlyList<Guid> roleIds)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{userId}/roles")
        {
            Content = JsonContent.Create(new { roleIds }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var response = await _client!.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static async Task ReseedSystemRolesAsync(FormForgeDbContext db)
    {
        if (!await db.Roles.AnyAsync(r => r.Id == PlatformAdminRoleId))
        {
            db.Roles.Add(new Role
            {
                Id = PlatformAdminRoleId,
                Name = "platform-admin",
                IsSystem = true,
                CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            });
        }

        if (!await db.Roles.AnyAsync(r => r.Id == ViewerRoleId))
        {
            db.Roles.Add(new Role
            {
                Id = ViewerRoleId,
                Name = "viewer",
                IsSystem = true,
                CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task<(Guid AdminId, Guid ViewerId)> SeedTestUsersAsync(FormForgeDbContext db)
    {
        var admin = new User
        {
            Email = "admin@example.com",
            DisplayName = "Platform Admin",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password1!", 12),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var viewer = new User
        {
            Email = "viewer@example.com",
            DisplayName = "Viewer User",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password1!", 12),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.AddRange(admin, viewer);
        await db.SaveChangesAsync();

        db.UserRoles.Add(new UserRole
        {
            UserId = admin.Id,
            RoleId = PlatformAdminRoleId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        return (admin.Id, viewer.Id);
    }

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record LoginResponseDto(string AccessToken, string RefreshToken, int ExpiresIn);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record CrudFlagsResponseDto(bool CanCreate, bool CanRead, bool CanUpdate, bool CanDelete);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record PermissionsResponseDto(
        Guid UserId,
        DateTimeOffset ComputedAt,
        bool IsActive,
        IReadOnlyDictionary<string, CrudFlagsResponseDto> PerResource,
        IReadOnlyList<Guid> RoleIds);
}
