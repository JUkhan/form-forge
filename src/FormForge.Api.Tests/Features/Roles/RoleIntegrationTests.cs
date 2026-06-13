using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Infrastructure.Persistence;
using FormForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormForge.Api.Tests.Features.Roles;

[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class RoleIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public RoleIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

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

        // Truncate all role + auth tables. user_roles must be wiped before users/roles
        // because of the FK chain. RESTART IDENTITY isn't needed (we use UUIDs) but
        // CASCADE handles any orphan rows defensively.
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE role_permissions, user_roles, roles, refresh_tokens, users RESTART IDENTITY CASCADE;");

        // TRUNCATE wiped the migration-seeded system roles — re-insert them so they
        // exist for the test (matches the production startup migrate path).
        await ReseedSystemRolesAsync(db);
        await SeedTestUsersAsync(db);

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

    // ---------- GET /api/admin/roles ----------

    [Fact]
    public async Task GetRoles_Unauthenticated_Returns401()
    {
        using var response = await _client!.GetAsync(new Uri("/api/admin/roles", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetRoles_AsNonAdmin_Returns403()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/roles");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetRoles_AsPlatformAdmin_Returns200WithSystemRoles()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/roles?page=1&pageSize=25");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PagedResultDto<RoleListItemDto>>();
        Assert.NotNull(body);
        Assert.Equal(2, body.Total);
        Assert.Equal(1, body.Page);
        Assert.Equal(25, body.PageSize);
        Assert.Equal(1, body.TotalPages);
        // Ordered by name ascending: "platform-admin", "viewer".
        Assert.Collection(body.Data,
            r => Assert.Equal("platform-admin", r.Name),
            r => Assert.Equal("viewer", r.Name));
        Assert.All(body.Data, r => Assert.True(r.IsSystem));
    }

    [Fact]
    public async Task GetRoles_SortByNameDesc_OrdersDescending()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/admin/roles?page=1&pageSize=25&sort=name:desc");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PagedResultDto<RoleListItemDto>>();
        Assert.NotNull(body);
        Assert.Collection(body.Data,
            r => Assert.Equal("viewer", r.Name),
            r => Assert.Equal("platform-admin", r.Name));
    }

    [Fact]
    public async Task GetRoles_Search_FiltersByName()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/admin/roles?page=1&pageSize=25&search=admin");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PagedResultDto<RoleListItemDto>>();
        Assert.NotNull(body);
        Assert.Equal(1, body.Total);
        Assert.Equal("platform-admin", body.Data.Single().Name);
    }

    [Fact]
    public async Task GetRoles_SystemFilter_CustomReturnsOnlyNonSystem()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateRoleViaApiAsync(token, "editor", "Editor role", Array.Empty<PermissionRecordDto>());

        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/admin/roles?page=1&pageSize=25&system=custom");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PagedResultDto<RoleListItemDto>>();
        Assert.NotNull(body);
        Assert.Equal(1, body.Total);
        Assert.Equal("editor", body.Data.Single().Name);
        Assert.False(body.Data.Single().IsSystem);
    }

    // ---------- GET /api/admin/roles/{id} ----------

    [Fact]
    public async Task GetRole_KnownId_Returns200WithPermissions()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var roleId = await CreateRoleViaApiAsync(token, "editor", "Editor role",
            new[] { new PermissionRecordDto("incident_report", true, true, true, false, CanExport: true) });

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/roles/{roleId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RoleResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("editor", body.Name);
        Assert.Single(body.Permissions);
        Assert.Equal("incident_report", body.Permissions[0].ResourceId);
        Assert.True(body.Permissions[0].CanCreate);
        Assert.False(body.Permissions[0].CanDelete);
        // Story 7-followup — CanExport round-trips through create → persist → GET.
        Assert.True(body.Permissions[0].CanExport);
    }

    [Fact]
    public async Task GetRole_UnknownId_Returns404()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/roles/{Guid.NewGuid()}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ROLE_NOT_FOUND", body, StringComparison.Ordinal);
    }

    // ---------- POST /api/admin/roles ----------

    [Fact]
    public async Task CreateRole_ValidBody_Returns201WithLocation()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/roles")
        {
            Content = JsonContent.Create(new
            {
                name = "content-editor",
                description = "Can create and update records",
                permissions = new[]
                {
                    new { resourceId = "incident_report", canCreate = true, canRead = true, canUpdate = true, canDelete = false },
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.StartsWith("/api/admin/roles/", response.Headers.Location!.ToString(), StringComparison.Ordinal);

        var body = await response.Content.ReadFromJsonAsync<RoleResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("content-editor", body.Name);
        Assert.False(body.IsSystem);
        Assert.Single(body.Permissions);
    }

    [Fact]
    public async Task CreateRole_DuplicateName_Returns409NameConflict()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        _ = await CreateRoleViaApiAsync(token, "duplicate-name", null, Array.Empty<PermissionRecordDto>());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/roles")
        {
            Content = JsonContent.Create(new
            {
                name = "duplicate-name",
                description = (string?)null,
                permissions = Array.Empty<PermissionRecordDto>(),
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ROLE_NAME_CONFLICT", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateRole_EmptyName_Returns422ValidationProblem()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/roles")
        {
            Content = JsonContent.Create(new
            {
                name = string.Empty,
                description = (string?)null,
                permissions = Array.Empty<PermissionRecordDto>(),
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ---------- PUT /api/admin/roles/{id} ----------

    [Fact]
    public async Task UpdateRole_ValidBody_Returns204()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var roleId = await CreateRoleViaApiAsync(token, "putable", "old description",
            new[] { new PermissionRecordDto("alpha", false, true, false, false) });

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/roles/{roleId}")
        {
            Content = JsonContent.Create(new
            {
                name = "putable-renamed",
                description = "new description",
                permissions = new[]
                {
                    new { resourceId = "beta", canCreate = true, canRead = true, canUpdate = true, canDelete = true },
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var verifyReq = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/roles/{roleId}");
        verifyReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var verify = await _client!.SendAsync(verifyReq);
        var body = await verify.Content.ReadFromJsonAsync<RoleResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("putable-renamed", body.Name);
        Assert.Equal("new description", body.Description);
        // Permissions replaced atomically: only the new "beta" row should remain.
        Assert.Single(body.Permissions);
        Assert.Equal("beta", body.Permissions[0].ResourceId);
        Assert.True(body.Permissions[0].CanDelete);
    }

    [Fact]
    public async Task UpdateRole_DuplicateName_Returns409()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        _ = await CreateRoleViaApiAsync(token, "alpha-role", null, Array.Empty<PermissionRecordDto>());
        var betaId = await CreateRoleViaApiAsync(token, "beta-role", null, Array.Empty<PermissionRecordDto>());

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/roles/{betaId}")
        {
            Content = JsonContent.Create(new
            {
                name = "alpha-role",
                description = (string?)null,
                permissions = Array.Empty<PermissionRecordDto>(),
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ROLE_NAME_CONFLICT", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateRole_SystemRole_Returns409SystemProtected()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/roles/{PlatformAdminRoleId}")
        {
            Content = JsonContent.Create(new
            {
                name = "renamed-admin",
                description = "should fail",
                permissions = Array.Empty<PermissionRecordDto>(),
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ROLE_SYSTEM_PROTECTED", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateRole_UnknownId_Returns404()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/roles/{Guid.NewGuid()}")
        {
            Content = JsonContent.Create(new
            {
                name = "ghost",
                description = (string?)null,
                permissions = Array.Empty<PermissionRecordDto>(),
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ROLE_NOT_FOUND", body, StringComparison.Ordinal);
    }

    // ---------- DELETE /api/admin/roles/{id} ----------

    [Fact]
    public async Task DeleteRole_NoAssignments_Returns204()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var roleId = await CreateRoleViaApiAsync(token, "deletable", null, Array.Empty<PermissionRecordDto>());

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/roles/{roleId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        Assert.False(await db.Roles.AnyAsync(r => r.Id == roleId));
    }

    [Fact]
    public async Task DeleteRole_WithAssignments_Returns409HasAssignments()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var roleId = await CreateRoleViaApiAsync(token, "assigned-role", null, Array.Empty<PermissionRecordDto>());

        // Story 2.5's user-role-assignment endpoint doesn't exist yet, so insert
        // the assignment directly via DbContext.
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var viewerUser = await db.Users.FirstAsync(u => u.Email == "viewer@example.com");
            db.UserRoles.Add(new UserRole
            {
                UserId = viewerUser.Id,
                RoleId = roleId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/roles/{roleId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ROLE_HAS_ASSIGNMENTS", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteRole_SystemRole_Returns409SystemProtected()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/roles/{ViewerRoleId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ROLE_SYSTEM_PROTECTED", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteRole_UnknownId_Returns404()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/roles/{Guid.NewGuid()}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ROLE_NOT_FOUND", body, StringComparison.Ordinal);
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

    private async Task<Guid> CreateRoleViaApiAsync(string token, string name, string? description, IReadOnlyList<PermissionRecordDto> permissions)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/roles")
        {
            Content = JsonContent.Create(new { name, description, permissions }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<RoleResponseDto>();
        return body!.Id;
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

    private static async Task SeedTestUsersAsync(FormForgeDbContext db)
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

        // admin user gets platform-admin role; viewer user is intentionally NOT
        // assigned to the viewer system role (Story 2.6 makes the viewer policy
        // matter for endpoint-level read access — for 2.4's 403 test we just need
        // *any* user without platform-admin).
        db.UserRoles.Add(new UserRole
        {
            UserId = admin.Id,
            RoleId = PlatformAdminRoleId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record LoginResponseDto(string AccessToken, string RefreshToken, int ExpiresIn);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record PagedResultDto<T>(IReadOnlyList<T> Data, long Total, int Page, int PageSize, int TotalPages);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record RoleListItemDto(Guid Id, string Name, string? Description, int PermissionCount, bool IsSystem);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record RoleResponseDto(
        Guid Id,
        string Name,
        string? Description,
        bool IsSystem,
        DateTimeOffset CreatedAt,
        DateTimeOffset? UpdatedAt,
        IReadOnlyList<PermissionRecordDto> Permissions);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record PermissionRecordDto(
        string ResourceId, bool CanCreate, bool CanRead, bool CanUpdate, bool CanDelete, bool CanExport = false);
}
