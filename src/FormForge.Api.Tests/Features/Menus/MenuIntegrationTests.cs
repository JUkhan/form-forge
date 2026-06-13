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

namespace FormForge.Api.Tests.Features.Menus;

[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class MenuIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    // Story 4.7 — viewer (non-admin) seeded role so navbar role-filter tests have a
    // distinguishable non-admin role to assign menus to. Mirrors DesignerIntegrationTests:23.
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public MenuIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

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
            "TRUNCATE TABLE menu_role_assignments, menus, role_permissions, user_roles, roles, refresh_tokens, users RESTART IDENTITY CASCADE;");

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

    // ---------- GET /api/admin/menus ----------

    [Fact]
    public async Task GetMenus_Unauthenticated_Returns401()
    {
        using var response = await _client!.GetAsync(new Uri("/api/admin/menus", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetMenus_AsNonAdmin_Returns403()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/menus");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetMenus_AsPlatformAdmin_EmptyList_Returns200()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/menus?page=1&pageSize=25");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PagedResultDto<MenuListItemDto>>();
        Assert.NotNull(body);
        Assert.Equal(0, body.Total);
        Assert.Empty(body.Data);
    }

    [Fact]
    public async Task GetMenus_WithItems_ReturnsOrderedBySortOrderThenId()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        // Use known UUIDs to verify ORDER BY sort_order ASC, id ASC tie-breaking.
        // These UUIDs differ only in the last byte so they sort identically under both
        // .NET Guid.CompareTo and PostgreSQL uuid ordering — the test is not sensitive
        // to the difference between the two comparison schemes.
        var idA = new Guid("00000000-0000-0000-0000-000000000001");  // order=2, lower UUID → first among ties
        var idB = new Guid("00000000-0000-0000-0000-000000000002");  // order=1 → comes first by sort_order
        var idC = new Guid("00000000-0000-0000-0000-000000000003");  // order=2, higher UUID → last among ties

        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            db.Menus.AddRange(
                new Menu { Id = idA, Name = "First-at-2", Order = 2, IsActive = true, CreatedAt = DateTimeOffset.UtcNow },
                new Menu { Id = idB, Name = "Only-at-1", Order = 1, IsActive = true, CreatedAt = DateTimeOffset.UtcNow },
                new Menu { Id = idC, Name = "Second-at-2", Order = 2, IsActive = true, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/menus?page=1&pageSize=25");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PagedResultDto<MenuListItemDto>>();
        Assert.NotNull(body);
        Assert.Equal(3, body.Total);

        // Expected order: Only-at-1 (order=1), First-at-2 (order=2, idA < idC), Second-at-2 (order=2, idA < idC)
        Assert.Collection(body.Data,
            m => Assert.Equal(idB, m.Id),
            m => Assert.Equal(idA, m.Id),
            m => Assert.Equal(idC, m.Id));
    }

    [Fact]
    public async Task GetMenus_SortByNameDesc_OverridesManualOrder()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            db.Menus.AddRange(
                new Menu { Id = Guid.NewGuid(), Name = "Apple", Order = 5, IsActive = true, CreatedAt = DateTimeOffset.UtcNow },
                new Menu { Id = Guid.NewGuid(), Name = "Cherry", Order = 1, IsActive = true, CreatedAt = DateTimeOffset.UtcNow },
                new Menu { Id = Guid.NewGuid(), Name = "Banana", Order = 3, IsActive = true, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/admin/menus?page=1&pageSize=25&sort=name:desc");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PagedResultDto<MenuListItemDto>>();
        Assert.NotNull(body);
        // Name desc ignores the manual `order` axis entirely.
        Assert.Collection(body.Data,
            m => Assert.Equal("Cherry", m.Name),
            m => Assert.Equal("Banana", m.Name),
            m => Assert.Equal("Apple", m.Name));
    }

    [Fact]
    public async Task GetMenus_NoSort_StaysInManualOrder()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            db.Menus.AddRange(
                new Menu { Id = Guid.NewGuid(), Name = "Apple", Order = 5, IsActive = true, CreatedAt = DateTimeOffset.UtcNow },
                new Menu { Id = Guid.NewGuid(), Name = "Cherry", Order = 1, IsActive = true, CreatedAt = DateTimeOffset.UtcNow },
                new Menu { Id = Guid.NewGuid(), Name = "Banana", Order = 3, IsActive = true, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/menus?page=1&pageSize=25");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PagedResultDto<MenuListItemDto>>();
        Assert.NotNull(body);
        // Default (no sort) preserves the manual reorder axis: order 1, 3, 5.
        Assert.Collection(body.Data,
            m => Assert.Equal("Cherry", m.Name),
            m => Assert.Equal("Banana", m.Name),
            m => Assert.Equal("Apple", m.Name));
    }

    [Fact]
    public async Task GetMenus_SearchAndActiveFilter_NarrowResults()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            db.Menus.AddRange(
                new Menu { Id = Guid.NewGuid(), Name = "Reports Dashboard", Order = 1, IsActive = true, CreatedAt = DateTimeOffset.UtcNow },
                new Menu { Id = Guid.NewGuid(), Name = "Reports Archive", Order = 2, IsActive = false, CreatedAt = DateTimeOffset.UtcNow },
                new Menu { Id = Guid.NewGuid(), Name = "Settings", Order = 3, IsActive = true, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        // Search narrows to the two "Reports" rows; active filter drops the archived one.
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/admin/menus?page=1&pageSize=25&search=reports&active=active");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PagedResultDto<MenuListItemDto>>();
        Assert.NotNull(body);
        Assert.Equal(1, body.Total);
        Assert.Equal("Reports Dashboard", body.Data.Single().Name);
    }

    // ---------- POST /api/admin/menus ----------

    [Fact]
    public async Task CreateMenu_Unauthenticated_Returns401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/menus")
        {
            Content = JsonContent.Create(new { name = "Test", order = 0 }),
        };
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateMenu_AsNonAdmin_Returns403()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/menus")
        {
            Content = JsonContent.Create(new { name = "Test", order = 0 }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateMenu_ValidBody_Returns201WithDefaults()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/menus")
        {
            Content = JsonContent.Create(new { name = "Dashboard", order = 1 }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.StartsWith("/api/admin/menus/", response.Headers.Location!.ToString(), StringComparison.Ordinal);

        var body = await response.Content.ReadFromJsonAsync<MenuResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("Dashboard", body.Name);
        Assert.Equal(1, body.Order);
        Assert.True(body.IsActive);
        Assert.Null(body.ParentId);
        Assert.Empty(body.AllowedRoleIds);
    }

    [Fact]
    public async Task CreateMenu_WithIsActiveFalse_Returns201WithIsActiveFalse()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/menus")
        {
            Content = JsonContent.Create(new { name = "Hidden Menu", order = 1, isActive = false }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MenuResponseDto>();
        Assert.NotNull(body);
        Assert.False(body.IsActive);
    }

    [Fact]
    public async Task CreateMenu_MissingName_Returns422()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/menus")
        {
            Content = JsonContent.Create(new { name = string.Empty, order = 0 }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreateMenu_NameTooLong_Returns422()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var longName = new string('A', 201);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/menus")
        {
            Content = JsonContent.Create(new { name = longName, order = 0 }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreateMenu_NegativeOrder_Returns422()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/menus")
        {
            Content = JsonContent.Create(new { name = "Test", order = -1 }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ---------- POST /api/admin/menus (sub-menu creation, Story 4.2) ----------

    [Fact]
    public async Task CreateMenu_WithValidParent_Returns201WithParentIdSet()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var parentId = await CreateMenuViaApiAsync(token, "Top Level", 0);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/menus")
        {
            Content = JsonContent.Create(new { name = "Sub Menu", order = 0, parentId }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MenuResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("Sub Menu", body.Name);
        Assert.Equal(parentId, body.ParentId);

        // Round-trip: GET the new sub-menu and assert ParentId actually persisted.
        // Guards against a regression to hardcoded `ParentId = null` on entity construction.
        using var getReq = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/menus/{body.Id}");
        getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var getResp = await _client!.SendAsync(getReq);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var fetched = await getResp.Content.ReadFromJsonAsync<MenuResponseDto>();
        Assert.NotNull(fetched);
        Assert.Equal(parentId, fetched.ParentId);
    }

    [Fact]
    public async Task CreateMenu_WithUnknownParent_Returns422ParentNotFound()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var unknownParentId = Guid.NewGuid();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/menus")
        {
            Content = JsonContent.Create(new { name = "Orphan", order = 0, parentId = unknownParentId }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("MENU_PARENT_NOT_FOUND", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateMenu_WithSubMenuAsParent_Returns422MaxDepthExceeded()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        // Build the chain: top → sub. Attempting a 3rd level must be rejected.
        var topId = await CreateMenuViaApiAsync(token, "Top", 0);
        var subId = await CreateSubMenuViaApiAsync(token, "Sub", 0, topId);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/menus")
        {
            Content = JsonContent.Create(new { name = "Too Deep", order = 0, parentId = subId }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("MAX_MENU_DEPTH_EXCEEDED", body, StringComparison.Ordinal);
    }

    // ---------- GET /api/admin/menus?parentId={id} (children filter, Story 4.2 review patch) ----------

    [Fact]
    public async Task GetMenus_WithParentIdFilter_ReturnsOnlyChildrenOfThatParent()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var parentA = await CreateMenuViaApiAsync(token, "Parent A", 0);
        var parentB = await CreateMenuViaApiAsync(token, "Parent B", 1);
        await CreateSubMenuViaApiAsync(token, "A-child-1", 0, parentA);
        await CreateSubMenuViaApiAsync(token, "A-child-2", 1, parentA);
        await CreateSubMenuViaApiAsync(token, "B-child-1", 0, parentB);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/menus?parentId={parentA}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PagedResultDto<MenuListItemDto>>();
        Assert.NotNull(body);
        Assert.Equal(2, body.Total);
        Assert.All(body.Data, m => Assert.Equal(parentA, m.ParentId));
        Assert.Contains(body.Data, m => m.Name == "A-child-1");
        Assert.Contains(body.Data, m => m.Name == "A-child-2");
    }

    [Fact]
    public async Task GetMenus_WithParentIdFilterUnknown_ReturnsEmptyPagedResult()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateMenuViaApiAsync(token, "Some Top", 0);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/menus?parentId={Guid.NewGuid()}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PagedResultDto<MenuListItemDto>>();
        Assert.NotNull(body);
        Assert.Equal(0, body.Total);
        Assert.Empty(body.Data);
    }

    // ---------- POST /api/admin/menus (icon, Story 4.3) ----------

    [Fact]
    public async Task CreateMenu_WithValidLucideIcon_Returns201AndIconRoundTripsAsObject()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/menus")
        {
            Content = JsonContent.Create(new
            {
                name = "With Icon",
                order = 0,
                icon = new { type = "lucide", name = "Home" },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Assert icon is serialized as a JSON OBJECT, not as a JSON string. This guards
        // the latent double-serialization bug fixed in Story 4.3 (MenuResponse.Icon: string? → JsonElement?).
        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        var iconEl = doc.RootElement.GetProperty("icon");
        Assert.Equal(JsonValueKind.Object, iconEl.ValueKind);
        Assert.Equal("lucide", iconEl.GetProperty("type").GetString());
        Assert.Equal("Home", iconEl.GetProperty("name").GetString());

        // Round-trip through GET to confirm DB persistence + ToResponse() path produces
        // the same object shape (not just the inline CreateMenuAsync response path).
        var menuId = doc.RootElement.GetProperty("id").GetGuid();
        using var getReq = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/menus/{menuId}");
        getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var getResp = await _client!.SendAsync(getReq);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var getRaw = await getResp.Content.ReadAsStringAsync();
        using var getDoc = JsonDocument.Parse(getRaw);
        var getIconEl = getDoc.RootElement.GetProperty("icon");
        Assert.Equal(JsonValueKind.Object, getIconEl.ValueKind);
        Assert.Equal("Home", getIconEl.GetProperty("name").GetString());
    }

    [Fact]
    public async Task CreateMenu_WithInvalidLucideIconName_Returns422()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/menus")
        {
            Content = JsonContent.Create(new
            {
                name = "Bad Icon",
                order = 0,
                icon = new { type = "lucide", name = "NotARealLucideIconName_XYZ" },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreateMenu_WithValidMinioIcon_Returns201()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/menus")
        {
            Content = JsonContent.Create(new
            {
                name = "Minio Icon",
                order = 0,
                icon = new
                {
                    type = "minio",
                    objectKey = $"menus/icons/{Guid.NewGuid():N}.png",
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        var iconEl = doc.RootElement.GetProperty("icon");
        Assert.Equal(JsonValueKind.Object, iconEl.ValueKind);
        Assert.Equal("minio", iconEl.GetProperty("type").GetString());
        Assert.StartsWith("menus/icons/", iconEl.GetProperty("objectKey").GetString(), StringComparison.Ordinal);
    }

    // ---------- PUT /api/admin/menus/{id} ----------

    [Fact]
    public async Task UpdateMenu_ValidUpdate_Returns204()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "Original Name", 0);

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/menus/{menuId}")
        {
            Content = JsonContent.Create(new { name = "Updated Name", order = 5, isActive = false }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify via GET
        using var verifyReq = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/menus/{menuId}");
        verifyReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var verify = await _client!.SendAsync(verifyReq);
        var body = await verify.Content.ReadFromJsonAsync<MenuResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("Updated Name", body.Name);
        Assert.Equal(5, body.Order);
        Assert.False(body.IsActive);
    }

    [Fact]
    public async Task UpdateMenu_UnknownId_Returns404()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/menus/{Guid.NewGuid()}")
        {
            Content = JsonContent.Create(new { name = "Ghost", order = 0, isActive = true }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("MENU_NOT_FOUND", body, StringComparison.Ordinal);
    }

    // ---------- DELETE /api/admin/menus/{id} ----------

    [Fact]
    public async Task DeleteMenu_NoChildren_Returns204()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "Deletable", 0);

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/menus/{menuId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        Assert.False(await db.Menus.AnyAsync(m => m.Id == menuId));
    }

    [Fact]
    public async Task DeleteMenu_WithChildren_Returns409HasChildren()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var parentId = await CreateMenuViaApiAsync(token, "Parent Menu", 0);

        // Insert child directly via DbContext (Story 4.2 adds the POST parentId support)
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            db.Menus.Add(new Menu
            {
                Name = "Child Menu",
                Order = 0,
                IsActive = true,
                ParentId = parentId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/menus/{parentId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("MENU_HAS_CHILDREN", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteMenu_UnknownId_Returns404()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/menus/{Guid.NewGuid()}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("MENU_NOT_FOUND", body, StringComparison.Ordinal);
    }

    // ---------- GET /api/admin/menus/{id} ----------

    [Fact]
    public async Task GetMenu_UnknownId_Returns404()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/menus/{Guid.NewGuid()}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("MENU_NOT_FOUND", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetMenu_Unauthenticated_Returns401()
    {
        using var response = await _client!.GetAsync(new Uri($"/api/admin/menus/{Guid.NewGuid()}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetMenu_AsNonAdmin_Returns403()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/menus/{Guid.NewGuid()}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetMenu_KnownId_Returns200WithMenu()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "Findable", 3);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/menus/{menuId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MenuResponseDto>();
        Assert.NotNull(body);
        Assert.Equal(menuId, body.Id);
        Assert.Equal("Findable", body.Name);
        Assert.Equal(3, body.Order);
        Assert.True(body.IsActive);
        Assert.Null(body.ParentId);
        Assert.Empty(body.AllowedRoleIds);
    }

    // ---------- PUT /api/admin/menus/{id}/roles (Story 4.4) ----------

    [Fact]
    public async Task AssignMenuRoles_Unauthenticated_Returns401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/menus/{Guid.NewGuid()}/roles")
        {
            Content = JsonContent.Create(new { roleIds = Array.Empty<Guid>() }),
        };
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AssignMenuRoles_AsNonAdmin_Returns403()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/menus/{Guid.NewGuid()}/roles")
        {
            Content = JsonContent.Create(new { roleIds = Array.Empty<Guid>() }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AssignMenuRoles_ValidRoleIds_Returns204AndRoundTrips()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "Role Test", 0);

        using var assignRequest = new HttpRequestMessage(
            HttpMethod.Put, $"/api/admin/menus/{menuId}/roles")
        {
            Content = JsonContent.Create(new { roleIds = new[] { PlatformAdminRoleId } }),
        };
        assignRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var assignResponse = await _client!.SendAsync(assignRequest);
        Assert.Equal(HttpStatusCode.NoContent, assignResponse.StatusCode);

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/menus/{menuId}");
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var getResponse = await _client!.SendAsync(getRequest);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var body = await getResponse.Content.ReadFromJsonAsync<MenuResponseDto>();
        Assert.NotNull(body);
        Assert.Contains(PlatformAdminRoleId, body.AllowedRoleIds);
    }

    [Fact]
    public async Task AssignMenuRoles_EmptyList_ClearsAssignments()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "Clearable", 0);

        // Seed an assignment first so the clear has something to remove.
        using (var seedRequest = new HttpRequestMessage(
            HttpMethod.Put, $"/api/admin/menus/{menuId}/roles")
        {
            Content = JsonContent.Create(new { roleIds = new[] { PlatformAdminRoleId } }),
        })
        {
            seedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var seedResponse = await _client!.SendAsync(seedRequest);
            Assert.Equal(HttpStatusCode.NoContent, seedResponse.StatusCode);
        }

        using var clearRequest = new HttpRequestMessage(
            HttpMethod.Put, $"/api/admin/menus/{menuId}/roles")
        {
            Content = JsonContent.Create(new { roleIds = Array.Empty<Guid>() }),
        };
        clearRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var clearResponse = await _client!.SendAsync(clearRequest);
        Assert.Equal(HttpStatusCode.NoContent, clearResponse.StatusCode);

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/menus/{menuId}");
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var getResponse = await _client!.SendAsync(getRequest);
        var body = await getResponse.Content.ReadFromJsonAsync<MenuResponseDto>();
        Assert.NotNull(body);
        Assert.Empty(body.AllowedRoleIds);
    }

    [Fact]
    public async Task AssignMenuRoles_MenuNotFound_Returns404()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(
            HttpMethod.Put, $"/api/admin/menus/{Guid.NewGuid()}/roles")
        {
            Content = JsonContent.Create(new { roleIds = new[] { PlatformAdminRoleId } }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("MENU_NOT_FOUND", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssignMenuRoles_InvalidRoleId_Returns422WithRolesNotFound()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "Bad Roles", 0);
        var bogusRoleId = Guid.NewGuid();

        using var request = new HttpRequestMessage(
            HttpMethod.Put, $"/api/admin/menus/{menuId}/roles")
        {
            Content = JsonContent.Create(new { roleIds = new[] { bogusRoleId } }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        // Parse the problem-detail body so we assert the bogus Guid appears in
        // the `invalidIds` extension specifically — a substring match would pass
        // even if the Guid leaked into title/detail. (Story 4.4 bmad review P5.)
        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal("ROLES_NOT_FOUND", doc.RootElement.GetProperty("code").GetString());
        var invalidIds = doc.RootElement.GetProperty("invalidIds").EnumerateArray()
            .Select(el => el.GetGuid())
            .ToList();
        Assert.Contains(bogusRoleId, invalidIds);
    }

    [Fact]
    public async Task AssignMenuRoles_ReassignSameRole_PreservesCreatedAt()
    {
        // Delta-sync semantics: re-PUTting the same roleId set must leave the
        // existing menu_role_assignments row untouched so CreatedAt remains a
        // faithful "originally assigned at" timestamp. (Story 4.4 bmad review P7.)
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "Stable Roles", 0);

        using (var req1 = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/menus/{menuId}/roles")
        {
            Content = JsonContent.Create(new { roleIds = new[] { PlatformAdminRoleId } }),
        })
        {
            req1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp1 = await _client!.SendAsync(req1);
            Assert.Equal(HttpStatusCode.NoContent, resp1.StatusCode);
        }

        DateTimeOffset originalCreatedAt;
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var row = await db.MenuRoleAssignments.SingleAsync(x => x.MenuId == menuId);
            originalCreatedAt = row.CreatedAt;
        }

        // Wait long enough that a "fresh CreatedAt" would be visibly different
        // from the original (Postgres timestamptz has sub-microsecond precision
        // and DateTimeOffset.UtcNow ticks at 100ns resolution, so 50ms is ample).
        await Task.Delay(50);

        using (var req2 = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/menus/{menuId}/roles")
        {
            Content = JsonContent.Create(new { roleIds = new[] { PlatformAdminRoleId } }),
        })
        {
            req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp2 = await _client!.SendAsync(req2);
            Assert.Equal(HttpStatusCode.NoContent, resp2.StatusCode);
        }

        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var row = await db.MenuRoleAssignments.SingleAsync(x => x.MenuId == menuId);
            Assert.Equal(originalCreatedAt, row.CreatedAt);
        }
    }

    // ---------- PUT /api/admin/menus/reorder (Story 4.5) ----------

    [Fact]
    public async Task ReorderMenus_Unauthenticated_Returns401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/admin/menus/reorder")
        {
            Content = JsonContent.Create(new { items = Array.Empty<object>() }),
        };
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReorderMenus_AsNonAdmin_Returns403()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/admin/menus/reorder")
        {
            Content = JsonContent.Create(new { items = Array.Empty<object>() }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ReorderMenus_TopLevelScope_PersistsNewOrder()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var idA = await CreateMenuViaApiAsync(token, "A", 0);
        var idB = await CreateMenuViaApiAsync(token, "B", 1);
        var idC = await CreateMenuViaApiAsync(token, "C", 2);

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/admin/menus/reorder")
        {
            Content = JsonContent.Create(new
            {
                items = new[]
                {
                    new { id = idA, order = 2 },
                    new { id = idB, order = 0 },
                    new { id = idC, order = 1 },
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var getReq = new HttpRequestMessage(HttpMethod.Get, "/api/admin/menus?page=1&pageSize=25");
        getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var getResp = await _client!.SendAsync(getReq);
        var body = await getResp.Content.ReadFromJsonAsync<PagedResultDto<MenuListItemDto>>();
        Assert.NotNull(body);
        Assert.Equal(3, body.Total);
        Assert.Collection(body.Data,
            m => Assert.Equal(idB, m.Id),
            m => Assert.Equal(idC, m.Id),
            m => Assert.Equal(idA, m.Id));
    }

    [Fact]
    public async Task ReorderMenus_SubMenuScope_PersistsNewOrder()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var parentId = await CreateMenuViaApiAsync(token, "Parent", 0);
        var childA = await CreateSubMenuViaApiAsync(token, "Child A", 0, parentId);
        var childB = await CreateSubMenuViaApiAsync(token, "Child B", 1, parentId);
        var childC = await CreateSubMenuViaApiAsync(token, "Child C", 2, parentId);

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/admin/menus/reorder")
        {
            Content = JsonContent.Create(new
            {
                items = new[]
                {
                    new { id = childA, order = 2 },
                    new { id = childB, order = 0 },
                    new { id = childC, order = 1 },
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var getReq = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/menus?parentId={parentId}");
        getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var getResp = await _client!.SendAsync(getReq);
        var body = await getResp.Content.ReadFromJsonAsync<PagedResultDto<MenuListItemDto>>();
        Assert.NotNull(body);
        Assert.Equal(3, body.Total);
        Assert.Collection(body.Data,
            m => Assert.Equal(childB, m.Id),
            m => Assert.Equal(childC, m.Id),
            m => Assert.Equal(childA, m.Id));
    }

    [Fact]
    public async Task ReorderMenus_MixedScopes_Returns422()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var topId = await CreateMenuViaApiAsync(token, "Top", 0);
        var parentId = await CreateMenuViaApiAsync(token, "Parent", 1);
        var childId = await CreateSubMenuViaApiAsync(token, "Child", 0, parentId);

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/admin/menus/reorder")
        {
            Content = JsonContent.Create(new
            {
                items = new[]
                {
                    new { id = topId, order = 0 },
                    new { id = childId, order = 1 },
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal("REORDER_MIXED_SCOPES", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ReorderMenus_UnknownId_Returns422WithInvalidIds()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var realId = await CreateMenuViaApiAsync(token, "Real", 0);
        var bogusId = Guid.NewGuid();

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/admin/menus/reorder")
        {
            Content = JsonContent.Create(new
            {
                items = new[]
                {
                    new { id = realId, order = 0 },
                    new { id = bogusId, order = 1 },
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal("MENUS_NOT_FOUND", doc.RootElement.GetProperty("code").GetString());
        var invalidIds = doc.RootElement.GetProperty("invalidIds").EnumerateArray()
            .Select(el => el.GetGuid())
            .ToList();
        Assert.Contains(bogusId, invalidIds);
        Assert.DoesNotContain(realId, invalidIds);
    }

    [Fact]
    public async Task ReorderMenus_EmptyItems_Returns204NoOp()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "Untouched", 0);

        DateTimeOffset? originalUpdatedAt;
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var menu = await db.Menus.SingleAsync(m => m.Id == menuId);
            originalUpdatedAt = menu.UpdatedAt;
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/admin/menus/reorder")
        {
            Content = JsonContent.Create(new { items = Array.Empty<object>() }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var menu = await db.Menus.SingleAsync(m => m.Id == menuId);
            Assert.Equal(originalUpdatedAt, menu.UpdatedAt);
        }
    }

    [Fact]
    public async Task ReorderMenus_DuplicateIds_Returns422()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "Dup Test", 0);

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/admin/menus/reorder")
        {
            Content = JsonContent.Create(new
            {
                items = new[]
                {
                    new { id = menuId, order = 0 },
                    new { id = menuId, order = 1 },
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ReorderMenus_TooManyItems_Returns422()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var items = Enumerable.Range(0, 257)
            .Select(i => new { id = Guid.NewGuid(), order = i })
            .ToArray();

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/admin/menus/reorder")
        {
            Content = JsonContent.Create(new { items }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ReorderMenus_LiteralSegmentDoesNotCollideWithIdRoute()
    {
        // Sanity check: PUT /api/admin/menus/reorder routes to ReorderMenusHandler
        // (returns 204 for empty items), while PUT /api/admin/menus/{guid} still
        // routes to UpdateMenuHandler (404 for an unknown id). Guards against a
        // future route-ordering regression.
        var token = await LoginAsync("admin@example.com", "Password1!");

        using (var reorderReq = new HttpRequestMessage(HttpMethod.Put, "/api/admin/menus/reorder")
        {
            Content = JsonContent.Create(new { items = Array.Empty<object>() }),
        })
        {
            reorderReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var reorderResp = await _client!.SendAsync(reorderReq);
            Assert.Equal(HttpStatusCode.NoContent, reorderResp.StatusCode);
        }

        using (var updateReq = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/menus/{Guid.NewGuid()}")
        {
            Content = JsonContent.Create(new { name = "Ghost", order = 0, isActive = true }),
        })
        {
            updateReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var updateResp = await _client!.SendAsync(updateReq);
            Assert.Equal(HttpStatusCode.NotFound, updateResp.StatusCode);
        }
    }

    [Fact]
    public async Task ReorderMenus_DoesNotMutateUpdatedAtOnUnchangedRows()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var idA = await CreateMenuViaApiAsync(token, "A", 0);
        var idB = await CreateMenuViaApiAsync(token, "B", 1);
        var idC = await CreateMenuViaApiAsync(token, "C", 2);

        DateTimeOffset? beforeA, beforeB, beforeC;
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            beforeA = (await db.Menus.SingleAsync(m => m.Id == idA)).UpdatedAt;
            beforeB = (await db.Menus.SingleAsync(m => m.Id == idB)).UpdatedAt;
            beforeC = (await db.Menus.SingleAsync(m => m.Id == idC)).UpdatedAt;
        }

        // Re-PUT the same orders the rows already hold.
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/admin/menus/reorder")
        {
            Content = JsonContent.Create(new
            {
                items = new[]
                {
                    new { id = idA, order = 0 },
                    new { id = idB, order = 1 },
                    new { id = idC, order = 2 },
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            Assert.Equal(beforeA, (await db.Menus.SingleAsync(m => m.Id == idA)).UpdatedAt);
            Assert.Equal(beforeB, (await db.Menus.SingleAsync(m => m.Id == idB)).UpdatedAt);
            Assert.Equal(beforeC, (await db.Menus.SingleAsync(m => m.Id == idC)).UpdatedAt);
        }
    }

    [Fact]
    public async Task ReorderMenus_OnlyMutatesUpdatedAtOnChangedRows()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var idA = await CreateMenuViaApiAsync(token, "A", 0);
        var idB = await CreateMenuViaApiAsync(token, "B", 1);
        var idC = await CreateMenuViaApiAsync(token, "C", 2);

        DateTimeOffset? beforeA, beforeB, beforeC;
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            beforeA = (await db.Menus.SingleAsync(m => m.Id == idA)).UpdatedAt;
            beforeB = (await db.Menus.SingleAsync(m => m.Id == idB)).UpdatedAt;
            beforeC = (await db.Menus.SingleAsync(m => m.Id == idC)).UpdatedAt;
        }

        // Change only B's order; A and C keep their existing orders.
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/admin/menus/reorder")
        {
            Content = JsonContent.Create(new
            {
                items = new[]
                {
                    new { id = idA, order = 0 },
                    new { id = idB, order = 5 },
                    new { id = idC, order = 2 },
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            Assert.Equal(beforeA, (await db.Menus.SingleAsync(m => m.Id == idA)).UpdatedAt);
            Assert.Equal(beforeC, (await db.Menus.SingleAsync(m => m.Id == idC)).UpdatedAt);
            var afterB = (await db.Menus.SingleAsync(m => m.Id == idB)).UpdatedAt;
            Assert.NotNull(afterB);
            Assert.NotEqual(beforeB, afterB);
        }
    }

    // ---------- PATCH /api/admin/menus/{id}/active (Story 4.6) ----------

    [Fact]
    public async Task ToggleMenuActive_Unauthenticated_Returns401()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Patch, $"/api/admin/menus/{Guid.NewGuid()}/active")
        {
            Content = JsonContent.Create(new { isActive = false }),
        };
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ToggleMenuActive_AsNonAdmin_Returns403()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");

        using var request = new HttpRequestMessage(
            HttpMethod.Patch, $"/api/admin/menus/{Guid.NewGuid()}/active")
        {
            Content = JsonContent.Create(new { isActive = false }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ToggleMenuActive_ToInactive_Returns204AndPersists()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "Active by default", 0);

        using var patchRequest = new HttpRequestMessage(
            HttpMethod.Patch, $"/api/admin/menus/{menuId}/active")
        {
            Content = JsonContent.Create(new { isActive = false }),
        };
        patchRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var patchResponse = await _client!.SendAsync(patchRequest);
        Assert.Equal(HttpStatusCode.NoContent, patchResponse.StatusCode);

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/menus/{menuId}");
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var getResponse = await _client!.SendAsync(getRequest);
        var body = await getResponse.Content.ReadFromJsonAsync<MenuResponseDto>();
        Assert.NotNull(body);
        Assert.False(body.IsActive);
        Assert.NotNull(body.UpdatedAt);
    }

    [Fact]
    public async Task ToggleMenuActive_ToActive_Returns204AndPersists()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        // Seed directly with IsActive = false: CreateMenuViaApiAsync defaults to
        // true via the create endpoint, which would mask the activation case.
        var menuId = Guid.NewGuid();
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            db.Menus.Add(new Menu
            {
                Id = menuId,
                Name = "Initially inactive",
                Order = 0,
                IsActive = false,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var patchRequest = new HttpRequestMessage(
            HttpMethod.Patch, $"/api/admin/menus/{menuId}/active")
        {
            Content = JsonContent.Create(new { isActive = true }),
        };
        patchRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var patchResponse = await _client!.SendAsync(patchRequest);
        Assert.Equal(HttpStatusCode.NoContent, patchResponse.StatusCode);

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/menus/{menuId}");
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var getResponse = await _client!.SendAsync(getRequest);
        var body = await getResponse.Content.ReadFromJsonAsync<MenuResponseDto>();
        Assert.NotNull(body);
        Assert.True(body.IsActive);
        Assert.NotNull(body.UpdatedAt);
    }

    [Fact]
    public async Task ToggleMenuActive_NotFound_Returns404()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(
            HttpMethod.Patch, $"/api/admin/menus/{Guid.NewGuid()}/active")
        {
            Content = JsonContent.Create(new { isActive = false }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal("MENU_NOT_FOUND", doc.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"isActive\":null}")]
    public async Task ToggleMenuActive_MissingIsActive_Returns422(string body)
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(
            HttpMethod.Patch, $"/api/admin/menus/{Guid.NewGuid()}/active")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ---------- GET /api/menus (Story 4.7 — public navbar tree) ----------

    [Fact]
    public async Task GetNavMenus_Unauthenticated_Returns401()
    {
        using var response = await _client!.GetAsync(new Uri("/api/menus", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetNavMenus_AsPlatformAdmin_ReturnsAllActiveMenusFiltered()
    {
        // Platform-admin bypass: see every ACTIVE menu regardless of roleAssignments.
        // Inactive items are excluded for everyone (admin included) — AC-1.
        var token = await LoginAsync("admin@example.com", "Password1!");

        Guid idA, idB, idC;
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var a = new Menu { Name = "Active No-Roles", Order = 0, IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
            var b = new Menu { Name = "Active Viewer-Only", Order = 1, IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
            var c = new Menu { Name = "Inactive", Order = 2, IsActive = false, CreatedAt = DateTimeOffset.UtcNow };
            db.Menus.AddRange(a, b, c);
            await db.SaveChangesAsync();
            db.MenuRoleAssignments.Add(new MenuRoleAssignment { MenuId = b.Id, RoleId = ViewerRoleId, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            idA = a.Id; idB = b.Id; idC = c.Id;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/menus");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tree = await response.Content.ReadFromJsonAsync<List<NavMenuItemDto>>();
        Assert.NotNull(tree);
        Assert.Collection(tree,
            n => Assert.Equal(idA, n.Id),
            n => Assert.Equal(idB, n.Id));
        Assert.DoesNotContain(tree, n => n.Id == idC);
    }

    [Fact]
    public async Task GetNavMenus_AsViewer_ReturnsOnlyRoleMatchingMenus()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");

        Guid idA, idB, idC;
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var a = new Menu { Name = "Active No-Roles", Order = 0, IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
            var b = new Menu { Name = "Active Viewer-Assigned", Order = 1, IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
            var c = new Menu { Name = "Active Admin-Only", Order = 2, IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
            db.Menus.AddRange(a, b, c);
            await db.SaveChangesAsync();
            db.MenuRoleAssignments.AddRange(
                new MenuRoleAssignment { MenuId = b.Id, RoleId = ViewerRoleId, CreatedAt = DateTimeOffset.UtcNow },
                new MenuRoleAssignment { MenuId = c.Id, RoleId = PlatformAdminRoleId, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            idA = a.Id; idB = b.Id; idC = c.Id;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/menus");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tree = await response.Content.ReadFromJsonAsync<List<NavMenuItemDto>>();
        Assert.NotNull(tree);
        // A: no roles assigned → no intersection → hidden.
        // C: admin-only → viewer has no admin role → hidden.
        Assert.Single(tree);
        Assert.Equal(idB, tree[0].Id);
    }

    [Fact]
    public async Task GetNavMenus_SubMenuVisibleToUser_PromotesParentEvenWithoutDirectRole()
    {
        // Transitive visibility: a sub-menu the user can see causes its parent
        // to be rendered as a container, even when the parent has no direct
        // role assignment for the user. Replaces the previous "drop, do not
        // promote" rule (Story 4.7 AC-1 — softened after admins consistently
        // expected role-on-child to be sufficient).
        var token = await LoginAsync("viewer@example.com", "Password1!");

        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var parent = new Menu { Name = "Admin Parent", Order = 0, IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
            db.Menus.Add(parent);
            await db.SaveChangesAsync();
            var sub = new Menu { Name = "Viewer Sub", Order = 0, IsActive = true, ParentId = parent.Id, CreatedAt = DateTimeOffset.UtcNow };
            db.Menus.Add(sub);
            await db.SaveChangesAsync();
            db.MenuRoleAssignments.AddRange(
                new MenuRoleAssignment { MenuId = parent.Id, RoleId = PlatformAdminRoleId, CreatedAt = DateTimeOffset.UtcNow },
                new MenuRoleAssignment { MenuId = sub.Id, RoleId = ViewerRoleId, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/menus");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tree = await response.Content.ReadFromJsonAsync<List<NavMenuItemDto>>();
        Assert.NotNull(tree);
        var promotedParent = Assert.Single(tree, n => n.Name == "Admin Parent");
        var promotedChild = Assert.Single(promotedParent.Children, n => n.Name == "Viewer Sub");
        Assert.NotNull(promotedChild);
    }

    [Fact]
    public async Task GetNavMenus_SubMenusNestedUnderVisibleParent_AreReturnedAsTreeChildren()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");

        Guid parentId, s1Id, s2Id;
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var parent = new Menu { Name = "Viewer Parent", Order = 0, IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
            db.Menus.Add(parent);
            await db.SaveChangesAsync();
            var s1 = new Menu { Name = "Sub One", Order = 1, IsActive = true, ParentId = parent.Id, CreatedAt = DateTimeOffset.UtcNow };
            var s2 = new Menu { Name = "Sub Two", Order = 0, IsActive = true, ParentId = parent.Id, CreatedAt = DateTimeOffset.UtcNow };
            db.Menus.AddRange(s1, s2);
            await db.SaveChangesAsync();
            db.MenuRoleAssignments.AddRange(
                new MenuRoleAssignment { MenuId = parent.Id, RoleId = ViewerRoleId, CreatedAt = DateTimeOffset.UtcNow },
                new MenuRoleAssignment { MenuId = s1.Id, RoleId = ViewerRoleId, CreatedAt = DateTimeOffset.UtcNow },
                new MenuRoleAssignment { MenuId = s2.Id, RoleId = ViewerRoleId, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            parentId = parent.Id; s1Id = s1.Id; s2Id = s2.Id;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/menus");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tree = await response.Content.ReadFromJsonAsync<List<NavMenuItemDto>>();
        Assert.NotNull(tree);
        Assert.Single(tree);
        Assert.Equal(parentId, tree[0].Id);
        // Children ordered by (Order, Id) — s2 (order=0) before s1 (order=1).
        Assert.Collection(tree[0].Children,
            c => Assert.Equal(s2Id, c.Id),
            c => Assert.Equal(s1Id, c.Id));
    }

    [Fact]
    public async Task GetNavMenus_InactiveSubMenu_ExcludedFromChildren()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        Guid parentId;
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var parent = new Menu { Name = "Parent", Order = 0, IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
            db.Menus.Add(parent);
            await db.SaveChangesAsync();
            var inactiveSub = new Menu { Name = "Inactive Sub", Order = 0, IsActive = false, ParentId = parent.Id, CreatedAt = DateTimeOffset.UtcNow };
            db.Menus.Add(inactiveSub);
            await db.SaveChangesAsync();
            parentId = parent.Id;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/menus");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tree = await response.Content.ReadFromJsonAsync<List<NavMenuItemDto>>();
        Assert.NotNull(tree);
        Assert.Single(tree);
        Assert.Equal(parentId, tree[0].Id);
        Assert.Empty(tree[0].Children);
    }

    [Fact]
    public async Task GetNavMenus_TopLevelOrdering_ByOrderThenId()
    {
        // Use known UUIDs so the tie-break under ORDER BY id ASC is deterministic.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var idLowOrder10 = new Guid("00000000-0000-0000-0000-000000000010");
        var idHighOrder10 = new Guid("00000000-0000-0000-0000-000000000020");
        var idOrder5 = new Guid("00000000-0000-0000-0000-000000000030");

        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            db.Menus.AddRange(
                new Menu { Id = idLowOrder10, Name = "Ten-A", Order = 10, IsActive = true, CreatedAt = DateTimeOffset.UtcNow },
                new Menu { Id = idHighOrder10, Name = "Ten-B", Order = 10, IsActive = true, CreatedAt = DateTimeOffset.UtcNow },
                new Menu { Id = idOrder5, Name = "Five", Order = 5, IsActive = true, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/menus");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tree = await response.Content.ReadFromJsonAsync<List<NavMenuItemDto>>();
        Assert.NotNull(tree);
        Assert.Collection(tree,
            n => Assert.Equal(idOrder5, n.Id),
            n => Assert.Equal(idLowOrder10, n.Id),
            n => Assert.Equal(idHighOrder10, n.Id));
    }

    [Fact]
    public async Task GetNavMenus_CacheHit_ReturnsSameTreeAfterDirectDbMutation()
    {
        // Cache hit semantics: a direct DbContext mutation that bypasses the admin
        // API (no InvalidateAsync call) must NOT be visible to the same user's next
        // GET within the 5 s TTL — proves the response was served from the cache.
        var token = await LoginAsync("admin@example.com", "Password1!");
        Guid id;
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var m = new Menu { Name = "Cacheable", Order = 0, IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
            db.Menus.Add(m);
            await db.SaveChangesAsync();
            id = m.Id;
        }

        async Task<string> FetchNavBodyAsync()
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/menus");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _client!.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            return await resp.Content.ReadAsStringAsync();
        }

        var firstBody = await FetchNavBodyAsync();
        Assert.Contains("Cacheable", firstBody, StringComparison.Ordinal);

        // Mutate the row directly via DbContext — no admin endpoint, no cache invalidate.
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var row = await db.Menus.SingleAsync(m => m.Id == id);
            row.Name = "MutatedDirectly";
            await db.SaveChangesAsync();
        }

        var secondBody = await FetchNavBodyAsync();
        // Cache must still return the original name, NOT the direct DB mutation.
        Assert.Equal(firstBody, secondBody);
        Assert.DoesNotContain("MutatedDirectly", secondBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetNavMenus_CacheInvalidatedAfterMutation_ReflectsNewState()
    {
        // The admin API path calls cache.InvalidateAsync after every mutation — proves
        // the write-time bust is wired correctly. One representative path (POST) is enough;
        // the per-mutation invalidate call is uniform across all 6 (Task 3).
        var token = await LoginAsync("admin@example.com", "Password1!");
        var idA = await CreateMenuViaApiAsync(token, "Aaa-First", 0);

        async Task<List<NavMenuItemDto>> FetchTreeAsync()
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/menus");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _client!.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            return (await resp.Content.ReadFromJsonAsync<List<NavMenuItemDto>>())!;
        }

        var beforeTree = await FetchTreeAsync();
        Assert.Single(beforeTree);
        Assert.Equal(idA, beforeTree[0].Id);

        var idB = await CreateMenuViaApiAsync(token, "Bbb-Second", 1);

        var afterTree = await FetchTreeAsync();
        Assert.Equal(2, afterTree.Count);
        Assert.Contains(afterTree, n => n.Id == idA);
        Assert.Contains(afterTree, n => n.Id == idB);
    }

    // ---------- Helpers ----------

    // ---------- PUT /api/admin/menus/{id}/route-path (custom route alternative) ----------

    [Fact]
    public async Task SetRoutePath_Unauthenticated_Returns401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/menus/{Guid.NewGuid()}/route-path")
        {
            Content = JsonContent.Create(new { routePath = "/reports" }),
        };
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SetRoutePath_AsNonAdmin_Returns403()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/menus/{Guid.NewGuid()}/route-path")
        {
            Content = JsonContent.Create(new { routePath = "/reports" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SetRoutePath_UnknownId_Returns404()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/menus/{Guid.NewGuid()}/route-path")
        {
            Content = JsonContent.Create(new { routePath = "/reports" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/reports")]
    [InlineData("/admin/users")]
    [InlineData("https://example.com")]
    [InlineData("http://intranet/page?x=1")]
    public async Task SetRoutePath_ValidPath_Returns204AndRoundTrips(string routePath)
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "Route Target", 0);

        using var setRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/menus/{menuId}/route-path")
        {
            Content = JsonContent.Create(new { routePath }),
        };
        setRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var setResponse = await _client!.SendAsync(setRequest);
        Assert.Equal(HttpStatusCode.NoContent, setResponse.StatusCode);

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/menus/{menuId}");
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var getResponse = await _client!.SendAsync(getRequest);
        var body = await getResponse.Content.ReadFromJsonAsync<MenuResponseDto>();
        Assert.NotNull(body);
        Assert.Equal(routePath, body.RoutePath);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,abc")]
    [InlineData("reports")]
    [InlineData("//evil.example.com")]
    [InlineData("ftp://host/file")]
    public async Task SetRoutePath_InvalidPath_Returns422(string routePath)
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "Bad Route", 0);

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/menus/{menuId}/route-path")
        {
            Content = JsonContent.Create(new { routePath }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task SetRoutePath_Null_ClearsExistingPath()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "Clearable Route", 0);

        // Seed a route first.
        using (var seed = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/menus/{menuId}/route-path")
        {
            Content = JsonContent.Create(new { routePath = "/reports" }),
        })
        {
            seed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var seedResponse = await _client!.SendAsync(seed);
            Assert.Equal(HttpStatusCode.NoContent, seedResponse.StatusCode);
        }

        using (var clear = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/menus/{menuId}/route-path")
        {
            Content = JsonContent.Create(new { routePath = (string?)null }),
        })
        {
            clear.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var clearResponse = await _client!.SendAsync(clear);
            Assert.Equal(HttpStatusCode.NoContent, clearResponse.StatusCode);
        }

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/menus/{menuId}");
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var getResponse = await _client!.SendAsync(getRequest);
        var body = await getResponse.Content.ReadFromJsonAsync<MenuResponseDto>();
        Assert.NotNull(body);
        Assert.Null(body.RoutePath);
    }

    [Fact]
    public async Task SetRoutePath_ClearsExistingDesignerBinding()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "Bound Then Routed", 0);

        // Seed a Designer (FK target) and pin the binding directly on the menu row.
        // Setting it via the DB avoids kicking off the async provisioning pipeline —
        // we only care that SetRoutePath clears DesignerId/BoundVersion.
        const string designerId = "orders";
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            db.Set<ComponentSchema>().Add(new ComponentSchema
            {
                DesignerId = designerId,
                DisplayName = "Orders",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            var menu = await db.Set<Menu>().FirstAsync(m => m.Id == menuId);
            menu.DesignerId = designerId;
            menu.BoundVersion = 1;
            menu.ProvisioningStatus = "Success";
            await db.SaveChangesAsync();
        }

        using (var set = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/menus/{menuId}/route-path")
        {
            Content = JsonContent.Create(new { routePath = "/reports" }),
        })
        {
            set.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var setResponse = await _client!.SendAsync(set);
            Assert.Equal(HttpStatusCode.NoContent, setResponse.StatusCode);
        }

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/menus/{menuId}");
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var getResponse = await _client!.SendAsync(getRequest);
        var body = await getResponse.Content.ReadFromJsonAsync<MenuResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("/reports", body.RoutePath);
        Assert.Null(body.DesignerId);
        Assert.Null(body.BoundVersion);
        Assert.Null(body.ProvisioningStatus);
    }

    private async Task<string> LoginAsync(string email, string password)
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email, password });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        return body!.AccessToken;
    }

    private async Task<Guid> CreateMenuViaApiAsync(string token, string name, int order)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/menus")
        {
            Content = JsonContent.Create(new { name, order }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<MenuResponseDto>();
        return body!.Id;
    }

    private async Task<Guid> CreateSubMenuViaApiAsync(string token, string name, int order, Guid parentId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/menus")
        {
            Content = JsonContent.Create(new { name, order, parentId }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<MenuResponseDto>();
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
        // Story 4.7 — seed viewer system role so the navbar tests can pin a
        // non-admin role onto specific menus to verify the role-intersection filter.
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

        db.UserRoles.Add(new UserRole
        {
            UserId = admin.Id,
            RoleId = PlatformAdminRoleId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        // Story 4.7 — viewer is a non-admin authenticated user with ViewerRoleId so
        // the navbar tests can verify role-intersection visibility (an authenticated
        // user with no roles would never see any role-gated menu, which is a less
        // interesting test surface than a user with exactly one specific role).
        db.UserRoles.Add(new UserRole
        {
            UserId = viewer.Id,
            RoleId = ViewerRoleId,
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
    private sealed record MenuListItemDto(Guid Id, string Name, int Order, bool IsActive, Guid? ParentId, DateTimeOffset CreatedAt);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record MenuResponseDto(
        Guid Id,
        string Name,
        int Order,
        string? Icon,
        bool IsActive,
        Guid? ParentId,
        IReadOnlyList<Guid> AllowedRoleIds,
        DateTimeOffset CreatedAt,
        DateTimeOffset? UpdatedAt,
        string? DesignerId,
        int? BoundVersion,
        string? ProvisioningStatus,
        string? ProvisioningError,
        string? RoutePath);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record NavMenuItemDto(
        Guid Id,
        string Name,
        int Order,
        JsonElement? Icon,
        Guid? ParentId,
        string? DesignerId,
        IReadOnlyList<NavMenuItemDto> Children);
}
