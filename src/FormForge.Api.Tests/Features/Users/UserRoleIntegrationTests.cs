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

namespace FormForge.Api.Tests.Features.Users;

[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class UserRoleIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    private Guid _viewerUserId;

    public UserRoleIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

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

        // user_roles must be wiped before users/roles because of the FK chain;
        // CASCADE handles any orphan rows defensively. Same TRUNCATE pattern as
        // RoleIntegrationTests.
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE role_permissions, user_roles, roles, refresh_tokens, users RESTART IDENTITY CASCADE;");

        await ReseedSystemRolesAsync(db);
        (_, _viewerUserId) = await SeedTestUsersAsync(db);

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

    // ---------- Auth guard tests (AC-5) ----------

    [Fact]
    public async Task AssignRoles_Unauthenticated_Returns401()
    {
        using var response = await _client!.PutAsJsonAsync(
            new Uri($"/api/admin/users/{_viewerUserId}/roles", UriKind.Relative),
            new { roleIds = Array.Empty<Guid>() });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AssignRoles_AsNonAdmin_Returns403()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}/roles")
        {
            Content = JsonContent.Create(new { roleIds = Array.Empty<Guid>() }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- 404 test (AC-2) ----------

    [Fact]
    public async Task AssignRoles_UserNotFound_Returns404()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{Guid.NewGuid()}/roles")
        {
            Content = JsonContent.Create(new { roleIds = Array.Empty<Guid>() }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("USER_NOT_FOUND", body, StringComparison.Ordinal);
    }

    // ---------- Validation tests (AC-3, AC-4) ----------

    [Fact]
    public async Task AssignRoles_NullRoleIds_Returns422()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        // Omit roleIds entirely — positional record yields null.
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}/roles")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task AssignRoles_DuplicateRoleIds_Returns422()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}/roles")
        {
            Content = JsonContent.Create(new { roleIds = new[] { ViewerRoleId, ViewerRoleId } }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Duplicate roleId values are not allowed.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssignRoles_UnknownRoleId_Returns422WithInvalidRoleIds()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var fakeRoleId = Guid.NewGuid();

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}/roles")
        {
            Content = JsonContent.Create(new { roleIds = new[] { ViewerRoleId, fakeRoleId } }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ROLES_NOT_FOUND", body, StringComparison.Ordinal);
        Assert.Contains(fakeRoleId.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssignRoles_GuidEmptyInRoleIds_Returns422()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}/roles")
        {
            Content = JsonContent.Create(new { roleIds = new[] { Guid.Empty } }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Guid.Empty", body, StringComparison.Ordinal);
    }

    // ---------- Happy-path tests (AC-1, AC-6) ----------

    [Fact]
    public async Task AssignRoles_ValidAssignment_Returns204()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}/roles")
        {
            Content = JsonContent.Create(new { roleIds = new[] { ViewerRoleId } }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var assignment = await db.UserRoles.FirstOrDefaultAsync(ur => ur.UserId == _viewerUserId && ur.RoleId == ViewerRoleId);
        Assert.NotNull(assignment);
    }

    [Fact]
    public async Task AssignRoles_EmptyRoleIds_Returns204AndClearsRoles()
    {
        // Pre-condition: viewer user has a role to clear.
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            db.UserRoles.Add(new UserRole { UserId = _viewerUserId, RoleId = ViewerRoleId, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}/roles")
        {
            Content = JsonContent.Create(new { roleIds = Array.Empty<Guid>() }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var verifyScope = _factory!.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        Assert.False(await verifyDb.UserRoles.AnyAsync(ur => ur.UserId == _viewerUserId));
    }

    [Fact]
    public async Task AssignRoles_JwtReflectsNewRoles_AfterLoginPostAssignment()
    {
        // Negative control: viewer has no platform-admin role on initial login,
        // so /api/admin/roles must return 403. Without this baseline, a broken
        // RequirePlatformAdmin policy would let the post-assignment 200 pass
        // green. (Story 2.5 review.)
        var preToken = await LoginAsync("viewer@example.com", "Password1!");
        using var preRequest = new HttpRequestMessage(HttpMethod.Get, "/api/admin/roles");
        preRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", preToken);
        using var preResponse = await _client!.SendAsync(preRequest);
        Assert.Equal(HttpStatusCode.Forbidden, preResponse.StatusCode);

        // Promote viewer to platform-admin via the new endpoint.
        var adminToken = await LoginAsync("admin@example.com", "Password1!");

        using var assignRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}/roles")
        {
            Content = JsonContent.Create(new { roleIds = new[] { PlatformAdminRoleId } }),
        };
        assignRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var assignResponse = await _client!.SendAsync(assignRequest);
        Assert.Equal(HttpStatusCode.NoContent, assignResponse.StatusCode);

        // Fresh login for viewer — JWT should now contain platform-admin.
        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");

        // Functional proof: new JWT grants access to admin-gated endpoint.
        using var adminRequest = new HttpRequestMessage(HttpMethod.Get, "/api/admin/roles");
        adminRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
        using var adminResponse = await _client!.SendAsync(adminRequest);

        Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
    }

    [Fact]
    public async Task AssignRoles_RoleReplacement_IsAtomic()
    {
        // Start: viewer has viewer role.
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            db.UserRoles.Add(new UserRole { UserId = _viewerUserId, RoleId = ViewerRoleId, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}/roles")
        {
            Content = JsonContent.Create(new { roleIds = new[] { PlatformAdminRoleId } }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify: only platform-admin row remains; the prior viewer row is gone.
        using var verifyScope = _factory!.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var userRoles = await verifyDb.UserRoles.Where(ur => ur.UserId == _viewerUserId).ToListAsync();
        Assert.Single(userRoles);
        Assert.Equal(PlatformAdminRoleId, userRoles[0].RoleId);
    }

    [Fact]
    public async Task AssignRoles_ConcurrentDemotionsOfDistinctAdmins_LeaveAtLeastOneAdmin()
    {
        // Story 2.6 review: without a SERIALIZABLE transaction around the
        // last-admin guard, two concurrent PUT /api/admin/users/{A|B}/roles
        // requests demoting distinct admins each pass otherAdminExists=true
        // against READ COMMITTED snapshots and both commit — zero admins.
        // Promote viewer to platform-admin so we have two admins to demote.
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            db.UserRoles.Add(new UserRole
            {
                UserId = _viewerUserId,
                RoleId = PlatformAdminRoleId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        Guid admin1Id;
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            admin1Id = await db.Users
                .Where(u => u.Email == "admin@example.com")
                .Select(u => u.Id)
                .SingleAsync();
        }

        var adminToken = await LoginAsync("admin@example.com", "Password1!");

        // Fire both demotions in parallel. A correct implementation must abort
        // (40001 → Conflict) or reject (LastAdminLockout) at least one.
        async Task<HttpResponseMessage> DemoteAsync(Guid targetId)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{targetId}/roles")
            {
                Content = JsonContent.Create(new { roleIds = Array.Empty<Guid>() }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            return await _client!.SendAsync(request);
        }

        var responses = await Task.WhenAll(DemoteAsync(admin1Id), DemoteAsync(_viewerUserId));
        try
        {
            // Invariant: regardless of how the race resolves, at least one
            // platform-admin must survive.
            using var verifyScope = _factory!.Services.CreateScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var remaining = await verifyDb.UserRoles
                .CountAsync(ur => ur.RoleId == PlatformAdminRoleId);
            Assert.True(remaining >= 1,
                $"Zero platform-admins after concurrent demotions; race window is open. Responses: " +
                string.Join(", ", responses.Select(r => (int)r.StatusCode)));
        }
        finally
        {
            foreach (var r in responses)
            {
                r.Dispose();
            }
        }
    }

    [Fact]
    public async Task AssignRoles_LastPlatformAdmin_CannotDemoteSelf_Returns422()
    {
        // Find the only platform-admin user (seeded as admin@example.com).
        Guid adminUserId;
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            adminUserId = await db.UserRoles
                .Where(ur => ur.RoleId == PlatformAdminRoleId)
                .Select(ur => ur.UserId)
                .SingleAsync();
        }

        var token = await LoginAsync("admin@example.com", "Password1!");

        // Attempt to clear all roles — would leave zero platform-admins.
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{adminUserId}/roles")
        {
            Content = JsonContent.Create(new { roleIds = Array.Empty<Guid>() }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("LAST_ADMIN_LOCKOUT", body, StringComparison.Ordinal);

        // Verify the assignment was not touched.
        using var verifyScope = _factory!.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        Assert.True(await verifyDb.UserRoles.AnyAsync(ur => ur.UserId == adminUserId && ur.RoleId == PlatformAdminRoleId));
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

        // admin gets platform-admin; viewer starts with NO roles (tests assign via API).
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
}
