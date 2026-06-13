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

namespace FormForge.Api.Tests.Features.Datasets;

// Story 8.2 (FR-56 / AR-58) — end-to-end coverage for dataset-management permission
// wiring: EffectivePermissions.CanManageDatasets union computation (AC-1/AC-2),
// /api/datasets write-endpoint 403 enforcement (AC-2), read-endpoint auth-only access
// (AC-3), and the canManageDatasets field on /api/users/me/permissions (AC-5).
//
// The harness truncates and re-seeds roles, so the platform-admin role is created with
// CanManageDatasets = true to faithfully mirror the Story 8.1 migration seed.
[Collection("DatasetIntegrationTests")]
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class DatasetPermissionTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    private Guid _adminUserId;
    private Guid _viewerUserId;

    public DatasetPermissionTests(PostgresFixture postgres) => _postgres = postgres;

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

    // ---------- AC-1 / AC-2: EffectivePermissions.CanManageDatasets union computation ----------

    [Fact]
    public async Task Permissions_PlatformAdmin_CanManageDatasetsTrue()
    {
        // AC-1: the seeded platform-admin role grants can_manage_datasets.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var body = await GetMyPermissionsAsync(token);
        Assert.True(body.CanManageDatasets);
    }

    [Fact]
    public async Task Permissions_ViewerNoDatasetRole_CanManageDatasetsFalse()
    {
        // AC-2: a user with no can_manage_datasets role resolves to false.
        var token = await LoginAsync("viewer@example.com", "Password1!");
        var body = await GetMyPermissionsAsync(token);
        Assert.False(body.CanManageDatasets);
    }

    [Fact]
    public async Task Permissions_CustomRoleWithDatasetPermission_CanManageDatasetsTrue()
    {
        var roleId = await CreateCustomRoleAsync("ds-manager", canManageDatasets: true);
        var adminToken = await LoginAsync("admin@example.com", "Password1!");
        await AssignRolesAsync(adminToken, _viewerUserId, new[] { roleId });

        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");
        var body = await GetMyPermissionsAsync(viewerToken);
        Assert.True(body.CanManageDatasets);
    }

    [Fact]
    public async Task Permissions_CustomRoleWithoutDatasetPermission_CanManageDatasetsFalse()
    {
        var roleId = await CreateCustomRoleAsync("ds-plain", canManageDatasets: false);
        var adminToken = await LoginAsync("admin@example.com", "Password1!");
        await AssignRolesAsync(adminToken, _viewerUserId, new[] { roleId });

        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");
        var body = await GetMyPermissionsAsync(viewerToken);
        Assert.False(body.CanManageDatasets);
    }

    [Fact]
    public async Task Permissions_UnionOfDatasetAndNonDatasetRoles_CanManageDatasetsTrue()
    {
        // AC-2: union across roles — any role granting the capability grants it.
        var datasetRole = await CreateCustomRoleAsync("ds-union-yes", canManageDatasets: true);
        var plainRole = await CreateCustomRoleAsync("ds-union-no", canManageDatasets: false);
        var adminToken = await LoginAsync("admin@example.com", "Password1!");
        await AssignRolesAsync(adminToken, _viewerUserId, new[] { datasetRole, plainRole });

        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");
        var body = await GetMyPermissionsAsync(viewerToken);
        Assert.True(body.CanManageDatasets);
    }

    // ---------- AC-2: Write endpoints return 403 for users without permission ----------

    [Fact]
    public async Task PostDatasets_Viewer_Returns403WithDatasetManagementAction()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");
        using var response = await SendAsync(HttpMethod.Post, "/api/datasets", token, new { });
        await AssertForbiddenDatasetManagementAsync(response);
    }

    [Fact]
    public async Task PutDataset_Viewer_Returns403()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");
        using var response = await SendAsync(
            HttpMethod.Put, $"/api/datasets/{Guid.NewGuid()}", token, new { });
        await AssertForbiddenDatasetManagementAsync(response);
    }

    [Fact]
    public async Task DeleteDataset_Viewer_Returns403()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");
        using var response = await SendAsync(
            HttpMethod.Delete, $"/api/datasets/{Guid.NewGuid()}", token, payload: null);
        await AssertForbiddenDatasetManagementAsync(response);
    }

    [Fact]
    public async Task PostDatasetsPreview_Viewer_Returns403()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");
        using var response = await SendAsync(HttpMethod.Post, "/api/datasets/preview", token, new { });
        await AssertForbiddenDatasetManagementAsync(response);
    }

    // ---------- AC-2: Write endpoints return non-4xx for authorised users ----------

    [Fact]
    public async Task PostDatasets_Admin_Returns201NotForbidden()
    {
        // The key assertion is that an authorised user is NOT blocked by the permission
        // filter (no 401/403). Story 8.4 implemented the create handler, so a VALID name
        // now returns 201 Created (the empty-body / invalid-name paths are covered by
        // DatasetNameValidationTests). This class does not truncate custom_dataset, so a
        // unique probe name guarantees the row is fresh and the POST returns 201, not 409.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var probeName = $"admin_probe_{Guid.NewGuid():N}";
        using var response = await SendAsync(
            HttpMethod.Post, "/api/datasets", token,
            new { datasetName = probeName, isCustomQuery = true });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---------- AC-3: Read endpoints require only auth ----------

    [Fact]
    public async Task GetDatasets_Unauthenticated_Returns401()
    {
        using var response = await _client!.GetAsync(new Uri("/api/datasets", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetDatasets_Viewer_Returns200()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");
        using var response = await SendAsync(HttpMethod.Get, "/api/datasets", token, payload: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetDataset_Viewer_Returns404NotForbidden()
    {
        // The stub handler returns 404; the key assertion is that a read does NOT
        // require dataset-management (no 401/403 for an authenticated viewer).
        var token = await LoginAsync("viewer@example.com", "Password1!");
        using var response = await SendAsync(
            HttpMethod.Get, $"/api/datasets/{Guid.NewGuid()}", token, payload: null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- AC-4: PUT /api/admin/roles/{id} persists canManageDatasets ----------

    [Fact]
    public async Task UpdateRole_SetsCanManageDatasets_PersistsAndComputes()
    {
        var adminToken = await LoginAsync("admin@example.com", "Password1!");

        // Create a custom role via the API (CreateRoleRequest has no dataset field,
        // so it starts false), then PUT it with canManageDatasets = true.
        var roleId = await CreateRoleViaApiAsync(adminToken, "ds-editor");

        using (var put = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/roles/{roleId}")
        {
            Content = JsonContent.Create(new
            {
                name = "ds-editor",
                description = (string?)null,
                permissions = Array.Empty<object>(),
                canManageDatasets = true,
            }),
        })
        {
            put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            using var putResponse = await _client!.SendAsync(put);
            Assert.Equal(HttpStatusCode.NoContent, putResponse.StatusCode);
        }

        // GET round-trips the persisted value (RoleResponse.CanManageDatasets).
        using (var get = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/roles/{roleId}"))
        {
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            using var getResponse = await _client!.SendAsync(get);
            Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
            var role = await getResponse.Content.ReadFromJsonAsync<RoleResponseDto>();
            Assert.NotNull(role);
            Assert.True(role!.CanManageDatasets);
        }

        // Assigning the role flows the persisted value into computed permissions.
        await AssignRolesAsync(adminToken, _viewerUserId, new[] { roleId });
        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");
        var body = await GetMyPermissionsAsync(viewerToken);
        Assert.True(body.CanManageDatasets);
    }

    // ---------- AC-5: /api/users/me/permissions includes canManageDatasets ----------

    [Fact]
    public async Task GetMyPermissions_Admin_HasCanManageDatasetsTrue()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var body = await GetMyPermissionsAsync(token);
        Assert.True(body.CanManageDatasets);
    }

    [Fact]
    public async Task GetMyPermissions_Viewer_HasCanManageDatasetsFalse()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");
        var body = await GetMyPermissionsAsync(token);
        Assert.False(body.CanManageDatasets);
    }

    [Fact]
    public async Task GetMyPermissions_AfterGrantingDatasetRole_FlipsToTrue()
    {
        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");

        // Warm the cache: viewer starts without the capability.
        var before = await GetMyPermissionsAsync(viewerToken);
        Assert.False(before.CanManageDatasets);

        // Grant a dataset-management role via the admin endpoint, which publishes
        // UserRoleAssignmentChanged → PermissionService busts the viewer's cache.
        var roleId = await CreateCustomRoleAsync("ds-granted", canManageDatasets: true);
        var adminToken = await LoginAsync("admin@example.com", "Password1!");
        await AssignRolesAsync(adminToken, _viewerUserId, new[] { roleId });

        var after = await GetMyPermissionsAsync(viewerToken);
        Assert.True(after.CanManageDatasets);
    }

    // ---------- Helpers ----------

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string uri, string token, object? payload)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private async Task<PermissionsResponseDto> GetMyPermissionsAsync(string token)
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/users/me/permissions", token, payload: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PermissionsResponseDto>();
        Assert.NotNull(body);
        return body!;
    }

    private static async Task AssertForbiddenDatasetManagementAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        // ProblemDetails extension members are flattened to the JSON root via
        // [JsonExtensionData] (see CreateRecordIntegrationTests), not nested under "extensions".
        Assert.Equal("FORBIDDEN", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("dataset-management", doc.RootElement.GetProperty("action").GetString());
        Assert.False(doc.RootElement.TryGetProperty("resource", out _),
            "Platform-level FORBIDDEN envelope must not include 'resource' key.");
    }

    private async Task<Guid> CreateCustomRoleAsync(string name, bool canManageDatasets)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var role = new Role
        {
            Name = name,
            IsSystem = false,
            CanManageDatasets = canManageDatasets,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return role.Id;
    }

    private async Task<Guid> CreateRoleViaApiAsync(string adminToken, string name)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/roles")
        {
            Content = JsonContent.Create(new
            {
                name,
                description = (string?)null,
                permissions = Array.Empty<object>(),
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var response = await _client!.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<RoleResponseDto>();
        return body!.Id;
    }

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
        // ON CONFLICT DO NOTHING makes the insert atomic — the previous check-then-insert
        // pattern races when parallel xUnit collections share the same PostgresFixture DB.
        var epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO roles (id, name, is_system, can_manage_datasets, created_at)" +
            " VALUES ({0}, 'platform-admin', true, true, {1}) ON CONFLICT (id) DO NOTHING",
            PlatformAdminRoleId, epoch);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO roles (id, name, is_system, can_manage_datasets, created_at)" +
            " VALUES ({0}, 'viewer', true, false, {1}) ON CONFLICT (id) DO NOTHING",
            ViewerRoleId, epoch);
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
    private sealed record PermissionsResponseDto(Guid UserId, bool IsActive, bool CanManageDatasets);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record RoleResponseDto(Guid Id, string Name, bool CanManageDatasets);
}
