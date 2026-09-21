using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Infrastructure.Persistence;
using FormForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormForge.Api.Tests.Features.Users;

// Hidden platform-dev role: (1) it and its holder are invisible to / immutable by tenant
// admins on every Users, Roles and Menus surface, and (2) the /api/admin/* route groups are
// gated per role (platform-admin: users/roles/menus/audit; platform-dev: roles/menus/
// datasets/constraints/table-provisioning/designer library).
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
[SuppressMessage("Design", "CA1054",
    Justification = "Theory data passes relative URL strings straight to HttpRequestMessage.")]
public sealed class PlatformDevRoleIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");
    private static readonly Guid PlatformDevRoleId = new("00000000-0000-0000-0000-000000000003");

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    private Guid _viewerUserId;
    private Guid _devUserId;
    private Guid _menuId;

    public PlatformDevRoleIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

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
            "TRUNCATE TABLE menu_role_assignments, menus, role_permissions, user_roles, roles, "
            + "refresh_tokens, users, tenant_user_index, tenants, platform_admins RESTART IDENTITY CASCADE;");

        var seededAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        db.Roles.AddRange(
            new Role { Id = PlatformAdminRoleId, Name = "platform-admin", IsSystem = true, CreatedAt = seededAt },
            new Role { Id = ViewerRoleId, Name = "viewer", IsSystem = true, CreatedAt = seededAt },
            new Role
            {
                Id = PlatformDevRoleId,
                Name = "platform-dev",
                IsSystem = true,
                CanManageDatasets = true,
                CreatedAt = seededAt,
            });
        await db.SaveChangesAsync();

        var admin = NewUser("admin@example.com", "Platform Admin");
        var viewer = NewUser("viewer@example.com", "Viewer User");
        var dev = NewUser("dev@example.com", "Platform Developer");
        db.Users.AddRange(admin, viewer, dev);
        await db.SaveChangesAsync();
        db.UserRoles.AddRange(
            new UserRole { UserId = admin.Id, RoleId = PlatformAdminRoleId, CreatedAt = DateTimeOffset.UtcNow },
            new UserRole { UserId = dev.Id, RoleId = PlatformDevRoleId, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        _viewerUserId = viewer.Id;
        _devUserId = dev.Id;

        // A menu carrying a (should-never-exist) dev-role assignment plus a normal one.
        var menu = new Menu { Name = "Test Menu", Order = 1, IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
        db.Menus.Add(menu);
        await db.SaveChangesAsync();
        db.MenuRoleAssignments.AddRange(
            new MenuRoleAssignment { MenuId = menu.Id, RoleId = PlatformDevRoleId, CreatedAt = DateTimeOffset.UtcNow },
            new MenuRoleAssignment { MenuId = menu.Id, RoleId = ViewerRoleId, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        _menuId = menu.Id;

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    // ---------- hiding (tenant admin view) ----------

    [Fact]
    public async Task AdminListsUsers_ExcludesDevUserFromItemsAndTotals()
    {
        var body = await GetJsonAsync("admin@example.com", "/api/admin/users?page=1&pageSize=25");
        Assert.Equal(2, body.GetProperty("total").GetInt64());
        var emails = body.GetProperty("data").EnumerateArray()
            .Select(u => u.GetProperty("email").GetString()).ToList();
        Assert.DoesNotContain("dev@example.com", emails);
    }

    [Fact]
    public async Task AdminListsRoles_ExcludesPlatformDevRole()
    {
        var body = await GetJsonAsync("admin@example.com", "/api/admin/roles?page=1&pageSize=50");
        Assert.Equal(2, body.GetProperty("total").GetInt64());
        var names = body.GetProperty("data").EnumerateArray()
            .Select(r => r.GetProperty("name").GetString()).ToList();
        Assert.DoesNotContain("platform-dev", names);
    }

    [Fact]
    public async Task DevUserAlsoSeesFilteredRoleList()
    {
        var body = await GetJsonAsync("dev@example.com", "/api/admin/roles?page=1&pageSize=50");
        var names = body.GetProperty("data").EnumerateArray()
            .Select(r => r.GetProperty("name").GetString()).ToList();
        Assert.DoesNotContain("platform-dev", names);
    }

    [Fact]
    public async Task ActiveUsers_ExcludesDevUser()
    {
        var token = await LoginAsync("admin@example.com");
        using var request = Authed(HttpMethod.Get, "/api/users/active", token);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(
            body.EnumerateArray(), u => u.GetProperty("email").GetString() == "dev@example.com");
    }

    [Fact]
    public async Task AdminCannotReadOrMutateDevUserOrRole()
    {
        var token = await LoginAsync("admin@example.com");

        foreach (var (method, url, payload) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, $"/api/admin/users/{_devUserId}", null),
            (HttpMethod.Put, $"/api/admin/users/{_devUserId}", new { displayName = "x" }),
            (HttpMethod.Post, $"/api/admin/users/{_devUserId}/deactivate", null),
            (HttpMethod.Post, $"/api/admin/users/{_devUserId}/reactivate", null),
            (HttpMethod.Put, $"/api/admin/users/{_devUserId}/roles", new { roleIds = Array.Empty<Guid>() }),
            (HttpMethod.Get, $"/api/admin/roles/{PlatformDevRoleId}", null),
            (HttpMethod.Delete, $"/api/admin/roles/{PlatformDevRoleId}", null),
        })
        {
            using var request = Authed(method, url, token);
            if (payload is not null)
            {
                request.Content = JsonContent.Create(payload);
            }

            using var response = await _client!.SendAsync(request);
            Assert.True(
                response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"{method} {url} returned {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task AssigningPlatformDevRoleToUser_IsRejectedAsUnknownRole()
    {
        var token = await LoginAsync("admin@example.com");
        using var request = Authed(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}/roles", token);
        request.Content = JsonContent.Create(new { roleIds = new[] { PlatformDevRoleId } });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("ROLES_NOT_FOUND", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        Assert.False(await db.UserRoles.AnyAsync(ur => ur.UserId == _viewerUserId && ur.RoleId == PlatformDevRoleId));
    }

    [Fact]
    public async Task AssigningPlatformDevRoleToMenu_IsRejectedAsUnknownRole()
    {
        var token = await LoginAsync("admin@example.com");
        using var request = Authed(HttpMethod.Put, $"/api/admin/menus/{_menuId}/roles", token);
        request.Content = JsonContent.Create(new { roleIds = new[] { PlatformDevRoleId } });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("ROLES_NOT_FOUND", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MenuDetail_OmitsPlatformDevRoleIdFromRoleIds()
    {
        var body = await GetJsonAsync("admin@example.com", $"/api/admin/menus/{_menuId}");
        var roleIds = body.GetProperty("allowedRoleIds").EnumerateArray().Select(r => r.GetGuid()).ToList();
        Assert.Contains(ViewerRoleId, roleIds);
        Assert.DoesNotContain(PlatformDevRoleId, roleIds);
    }

    [Theory]
    [InlineData("platform-dev")]
    public async Task CreateAndRenameRole_ToPlatformDev_IsRejectedAsDuplicate(string name)
    {
        var token = await LoginAsync("admin@example.com");

        using var create = Authed(HttpMethod.Post, "/api/admin/roles", token);
        create.Content = JsonContent.Create(new { name, description = (string?)null, permissions = Array.Empty<object>() });
        using var createResponse = await _client!.SendAsync(create);
        Assert.Equal(HttpStatusCode.Conflict, createResponse.StatusCode);

        Guid customId;
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var role = new Role { Name = "custom-" + Guid.NewGuid().ToString("N")[..8], CreatedAt = DateTimeOffset.UtcNow };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            customId = role.Id;
        }

        using var update = Authed(HttpMethod.Put, $"/api/admin/roles/{customId}", token);
        update.Content = JsonContent.Create(new { name, description = (string?)null, permissions = Array.Empty<object>() });
        using var updateResponse = await _client!.SendAsync(update);
        Assert.Equal(HttpStatusCode.Conflict, updateResponse.StatusCode);
    }

    // ---------- per-role route gating ----------

    [Theory]
    [InlineData("/api/admin/data/some_designer/audit")]
    [InlineData("/api/admin/designers/some_designer/audit")]
    public async Task PlatformDev_IsForbiddenFromAuditRoutes(string url)
    {
        var token = await LoginAsync("dev@example.com");
        using var response = await _client!.SendAsync(Authed(HttpMethod.Get, url, token));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/admin/designers/some_designer/drift")]
    [InlineData("/api/admin/designers/some_designer/unique-constraints")]
    public async Task DevOnlyDesignerRoutes_ForbiddenToAdminAndViewer(string url)
    {
        foreach (var email in new[] { "admin@example.com", "viewer@example.com" })
        {
            var token = await LoginAsync(email);
            using var response = await _client!.SendAsync(Authed(HttpMethod.Get, url, token));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("/api/admin/data/some_designer/audit")]
    [InlineData("/api/admin/designers/some_designer/audit")]
    public async Task ViewerIsForbiddenFromAuditRoutes(string url)
    {
        var token = await LoginAsync("viewer@example.com");
        using var response = await _client!.SendAsync(Authed(HttpMethod.Get, url, token));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PlatformDev_CanCreateDesigner()
    {
        var token = await LoginAsync("dev@example.com");
        using var request = Authed(HttpMethod.Post, "/api/designers", token);
        request.Content = JsonContent.Create(new { designerId = "dev_made_designer", displayName = "Dev Made", mode = "CRUD" });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/admin/users")]
    [InlineData("/api/admin/roles")]
    [InlineData("/api/admin/menus")]
    public async Task PlatformAdmin_CanReachUsersRolesMenus(string url)
    {
        var token = await LoginAsync("admin@example.com");
        using var response = await _client!.SendAsync(Authed(HttpMethod.Get, url, token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/admin/table-provisioning")]
    [InlineData("/api/admin/designers/provisioned")]
    [InlineData("/api/admin/datasets/audit")]
    public async Task PlatformAdmin_IsForbiddenFromDevOnlyRoutes(string url)
    {
        var token = await LoginAsync("admin@example.com");
        using var response = await _client!.SendAsync(Authed(HttpMethod.Get, url, token));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/admin/roles")]
    [InlineData("/api/admin/menus")]
    [InlineData("/api/admin/table-provisioning")]
    [InlineData("/api/admin/designers/provisioned")]
    public async Task PlatformDev_CanReachDevAndSharedRoutes(string url)
    {
        var token = await LoginAsync("dev@example.com");
        using var response = await _client!.SendAsync(Authed(HttpMethod.Get, url, token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/admin/users")]
    [InlineData("/api/admin/users/00000000-0000-0000-0000-0000000000aa")]
    public async Task PlatformDev_IsForbiddenFromUsersRoutes(string url)
    {
        var token = await LoginAsync("dev@example.com");
        using var response = await _client!.SendAsync(Authed(HttpMethod.Get, url, token));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ViewerWithNeitherRole_IsForbiddenFromEveryAdminGroup()
    {
        var token = await LoginAsync("viewer@example.com");
        foreach (var url in new[] { "/api/admin/users", "/api/admin/roles", "/api/admin/menus", "/api/admin/table-provisioning" })
        {
            using var response = await _client!.SendAsync(Authed(HttpMethod.Get, url, token));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    // ---------- helpers ----------

    private static User NewUser(string email, string displayName) => new()
    {
        Email = email,
        DisplayName = displayName,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password1!", 12),
        IsActive = true,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static HttpRequestMessage Authed(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<JsonElement> GetJsonAsync(string email, string url)
    {
        var token = await LoginAsync(email);
        using var response = await _client!.SendAsync(Authed(HttpMethod.Get, url, token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string> LoginAsync(string email)
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login", new { email, password = "Password1!" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("accessToken").GetString()!;
    }
}
