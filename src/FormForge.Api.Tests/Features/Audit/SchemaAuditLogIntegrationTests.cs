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
using Npgsql;

namespace FormForge.Api.Tests.Features.Audit;

// Story 5.7 — exercises GET /api/admin/designers/{designerId}/audit end-to-end:
// authenticates as the platform admin, drives the provisioning pipeline through
// CREATE/ALTER/DROP, then asserts the response shape, pagination, ordering,
// actor-name resolution, sentinels (toVersion=0, fromVersion=null), 404 for
// invalid designerIds, and 405 for DELETE (AC-2 append-only).
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class SchemaAuditLogIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public SchemaAuditLogIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

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
            "TRUNCATE TABLE menu_role_assignments, menus, component_schema_versions, component_schemas, role_permissions, user_roles, roles, refresh_tokens, users, schema_audit_log RESTART IDENTITY CASCADE;");

        // Drop any dynamically-provisioned tables from previous test runs.
        await db.Database.ExecuteSqlRawAsync("""
            DO $$
            DECLARE r RECORD;
            BEGIN
                FOR r IN
                    SELECT tablename FROM pg_tables
                    WHERE schemaname = 'public'
                      AND tablename NOT IN (
                          'users', 'refresh_tokens', 'roles', 'role_permissions', 'user_roles',
                          'component_schemas', 'component_schema_versions',
                          'menus', 'menu_role_assignments',
                          'schema_audit_log',
                          '__EFMigrationsHistory'
                      )
                LOOP
                    EXECUTE 'DROP TABLE IF EXISTS ' || quote_ident(r.tablename) || ' CASCADE';
                END LOOP;
            END $$;
            """);

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

    [Fact]
    public async Task GetSchemaAuditLog_NoEntries_Returns200WithEmptyData()
    {
        // AC-1: a valid designerId that has never been provisioned returns an empty
        // page (data: [], total: 0) — NOT a 404. The designer doesn't even need to
        // exist as a row in component_schemas; only the syntactic identifier check matters.
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var response = await GetAuditAsync(token, "never_provisioned", page: 1, pageSize: 25);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AuditPagedResultDto>(WebJsonOptions);
        Assert.NotNull(body);
        Assert.Empty(body!.Data);
        Assert.Equal(0, body.Total);
        Assert.Equal(1, body.Page);
        Assert.Equal(25, body.PageSize);
        Assert.Equal(0, body.TotalPages);
    }

    [Fact]
    public async Task GetSchemaAuditLog_AfterCreate_ReturnsCreateEntry()
    {
        // AC-1: one CREATE row after a fresh bind — fromVersion=null, ddlOperation="CREATE",
        // columnsAdded non-empty.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "AuditCreateMenu", 0);

        var root = new
        {
            id = "root", type = "Stack", properties = new { },
            children = new object[]
            {
                new { id = "f1", type = "TextInput",
                      properties = new { fieldKey = "title" }, children = Array.Empty<object>() },
            },
        };
        var v = await CreateAndPublishDesignerWithFieldsAsync(token, "audit_create", root);
        using (var bind = await PutBindingAsync(token, menuId, "audit_create", v))
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        using var response = await GetAuditAsync(token, "audit_create", page: 1, pageSize: 25);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AuditPagedResultDto>(WebJsonOptions);
        Assert.NotNull(body);
        var entry = Assert.Single(body!.Data);
        Assert.Equal("CREATE", entry.DdlOperation);
        Assert.Null(entry.FromVersion);
        Assert.True(entry.ToVersion >= 1);
        Assert.NotNull(entry.ColumnsAdded);
        Assert.Contains("title", entry.ColumnsAdded!);
    }

    [Fact]
    public async Task GetSchemaAuditLog_MultipleEntries_ReturnedNewestFirst()
    {
        // AC-1 ordering: CREATE then ALTER → data[0] is the ALTER (newer), data[1] is the CREATE.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "AuditOrderMenu", 0);

        var rootV1 = new
        {
            id = "root", type = "Stack", properties = new { },
            children = new object[]
            {
                new { id = "f1", type = "TextInput",
                      properties = new { fieldKey = "first" }, children = Array.Empty<object>() },
            },
        };
        var v1 = await CreateAndPublishDesignerWithFieldsAsync(token, "audit_order", rootV1);
        using (var bind1 = await PutBindingAsync(token, menuId, "audit_order", v1))
            Assert.Equal(HttpStatusCode.Accepted, bind1.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        var rootV2 = new
        {
            id = "root", type = "Stack", properties = new { },
            children = new object[]
            {
                new { id = "f1", type = "TextInput",
                      properties = new { fieldKey = "first" }, children = Array.Empty<object>() },
                new { id = "f2", type = "TextInput",
                      properties = new { fieldKey = "second" }, children = Array.Empty<object>() },
            },
        };
        await PostVersionWithRootAsync(token, "audit_order", rootV2);
        await PutVersionStatusAsync(token, "audit_order", 3, "Published");
        using (var bind2 = await PutBindingAsync(token, menuId, "audit_order", 3))
            Assert.Equal(HttpStatusCode.Accepted, bind2.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        using var response = await GetAuditAsync(token, "audit_order", page: 1, pageSize: 25);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AuditPagedResultDto>(WebJsonOptions);
        Assert.NotNull(body);
        Assert.Equal(2, body!.Data.Count);
        Assert.Equal("ALTER", body.Data[0].DdlOperation);   // newest first
        Assert.Equal("CREATE", body.Data[1].DdlOperation);
    }

    [Fact]
    public async Task GetSchemaAuditLog_Paginated_SecondPage()
    {
        // AC-1 pagination: with two audit rows and pageSize=1, page=2 returns exactly the
        // older one and reports page=2 / total=2 / totalPages=2.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "AuditPageMenu", 0);

        var rootV1 = new
        {
            id = "root", type = "Stack", properties = new { },
            children = new object[]
            {
                new { id = "f1", type = "TextInput",
                      properties = new { fieldKey = "alpha" }, children = Array.Empty<object>() },
            },
        };
        var v1 = await CreateAndPublishDesignerWithFieldsAsync(token, "audit_page", rootV1);
        using (var bind1 = await PutBindingAsync(token, menuId, "audit_page", v1))
            Assert.Equal(HttpStatusCode.Accepted, bind1.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        var rootV2 = new
        {
            id = "root", type = "Stack", properties = new { },
            children = new object[]
            {
                new { id = "f1", type = "TextInput",
                      properties = new { fieldKey = "alpha" }, children = Array.Empty<object>() },
                new { id = "f2", type = "TextInput",
                      properties = new { fieldKey = "beta" }, children = Array.Empty<object>() },
            },
        };
        await PostVersionWithRootAsync(token, "audit_page", rootV2);
        await PutVersionStatusAsync(token, "audit_page", 3, "Published");
        using (var bind2 = await PutBindingAsync(token, menuId, "audit_page", 3))
            Assert.Equal(HttpStatusCode.Accepted, bind2.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        using var response = await GetAuditAsync(token, "audit_page", page: 2, pageSize: 1);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AuditPagedResultDto>(WebJsonOptions);
        Assert.NotNull(body);
        Assert.Single(body!.Data);
        Assert.Equal(2, body.Total);
        Assert.Equal(2, body.Page);
        Assert.Equal(1, body.PageSize);
        Assert.Equal(2, body.TotalPages);
        // Page 2 with pageSize 1 ordered DESC → the CREATE (oldest).
        Assert.Equal("CREATE", body.Data[0].DdlOperation);
    }

    [Fact]
    public async Task GetSchemaAuditLog_ActorName_ResolvedFromUsers()
    {
        // AC-1 actor resolution: actorName is the admin's DisplayName; actorId is non-null.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "AuditActorMenu", 0);
        await CreateAndPublishDesignerAsync(token, "audit_actor");

        using (var bind = await PutBindingAsync(token, menuId, "audit_actor", 1))
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        using var response = await GetAuditAsync(token, "audit_actor", page: 1, pageSize: 25);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AuditPagedResultDto>(WebJsonOptions);
        Assert.NotNull(body);
        var entry = Assert.Single(body!.Data);
        Assert.NotNull(entry.ActorId);
        Assert.Equal("Platform Admin", entry.ActorName);   // seeded DisplayName
    }

    [Fact]
    public async Task GetSchemaAuditLog_DropRow_ToVersionIsZeroSentinel()
    {
        // AC-1 sentinel: a DROP row has toVersion = 0 (never normalized) and
        // columnsDropped contains the dropped column name.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "AuditDropMenu", 0);

        var rootV1 = new
        {
            id = "root", type = "Stack", properties = new { },
            children = new object[]
            {
                new { id = "f1", type = "TextInput",
                      properties = new { fieldKey = "keep_col" }, children = Array.Empty<object>() },
                new { id = "f2", type = "TextInput",
                      properties = new { fieldKey = "to_drop" }, children = Array.Empty<object>() },
            },
        };
        var v1 = await CreateAndPublishDesignerWithFieldsAsync(token, "audit_drop", rootV1);
        using (var bind1 = await PutBindingAsync(token, menuId, "audit_drop", v1))
            Assert.Equal(HttpStatusCode.Accepted, bind1.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        // v3 removes to_drop → it becomes orphaned in the DB.
        var rootV2 = new
        {
            id = "root", type = "Stack", properties = new { },
            children = new object[]
            {
                new { id = "f1", type = "TextInput",
                      properties = new { fieldKey = "keep_col" }, children = Array.Empty<object>() },
            },
        };
        await PostVersionWithRootAsync(token, "audit_drop", rootV2);
        await PutVersionStatusAsync(token, "audit_drop", 3, "Published");
        using (var bind2 = await PutBindingAsync(token, menuId, "audit_drop", 3))
            Assert.Equal(HttpStatusCode.Accepted, bind2.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        // Now drop the orphaned column via the Story 5.6 endpoint.
        using (var dropResp = await DeleteColumnAsync(token, "audit_drop", "to_drop"))
            Assert.Equal(HttpStatusCode.NoContent, dropResp.StatusCode);

        using var response = await GetAuditAsync(token, "audit_drop", page: 1, pageSize: 25);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AuditPagedResultDto>(WebJsonOptions);
        Assert.NotNull(body);
        var dropEntry = body!.Data.FirstOrDefault(e => e.DdlOperation == "DROP");
        Assert.NotNull(dropEntry);
        Assert.Equal(0, dropEntry!.ToVersion);                    // sentinel preserved as-is
        Assert.NotNull(dropEntry.ColumnsDropped);
        Assert.Contains("to_drop", dropEntry.ColumnsDropped);
    }

    [Fact]
    public async Task GetSchemaAuditLog_InvalidDesignerId_Returns404()
    {
        // AC-1: a designerId that fails SafeIdentifier.TryCreate (uppercase, hyphen)
        // returns 404 with code DESIGNER_NOT_FOUND.
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var response = await GetAuditAsync(token, "INVALID-ID", page: 1, pageSize: 25);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("DESIGNER_NOT_FOUND", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetSchemaAuditLog_AppendOnly_DeleteVerb_Returns405()
    {
        // AC-2: no DELETE handler is mapped on /audit, so ASP.NET returns 405.
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            "/api/admin/designers/audit_delete_attempt/audit");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    // ---------- Helpers (copied / adapted from ProvisioningIntegrationTests) ----------

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

    private async Task CreateDesignerViaApiAsync(string token, string designerId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers")
        {
            Content = JsonContent.Create(new { designerId, displayName = designerId, mode = "CRUD" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private async Task PostVersionWithRootAsync<TRoot>(string token, string designerId, TRoot rootElement)
    {
        ArgumentNullException.ThrowIfNull(rootElement);
        var rootJson = JsonSerializer.Serialize(rootElement, WebJsonOptions);
        var bodyJson = $"{{\"rootElement\":{rootJson}}}";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/designers/{designerId}/versions")
        {
            Content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private async Task PutVersionStatusAsync(string token, string designerId, int version, string status)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/designers/{designerId}/versions/{version}/status")
        {
            Content = JsonContent.Create(new { status }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private async Task CreateAndPublishDesignerAsync(string token, string designerId)
    {
        await CreateDesignerViaApiAsync(token, designerId);
        await PutVersionStatusAsync(token, designerId, 1, "Published");
    }

    private async Task<int> CreateAndPublishDesignerWithFieldsAsync<TRoot>(
        string token,
        string designerId,
        TRoot rootElement)
    {
        ArgumentNullException.ThrowIfNull(rootElement);
        await CreateDesignerViaApiAsync(token, designerId);

        var rootJson = JsonSerializer.Serialize(rootElement, WebJsonOptions);
        var bodyJson = $"{{\"rootElement\":{rootJson}}}";
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/designers/{designerId}/versions")
        {
            Content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        response.EnsureSuccessStatusCode();

        const int publishedVersion = 2;
        await PutVersionStatusAsync(token, designerId, publishedVersion, "Published");
        return publishedVersion;
    }

    private async Task<HttpResponseMessage> PutBindingAsync(string token, Guid menuId, string designerId, int version)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/admin/menus/{menuId}/binding")
        {
            Content = JsonContent.Create(new { designerId, version }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private async Task<HttpResponseMessage> DeleteColumnAsync(string token, string designerId, string columnName)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/admin/designers/{designerId}/columns/{columnName}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private async Task<HttpResponseMessage> GetAuditAsync(string token, string designerId, int page, int pageSize)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/admin/designers/{designerId}/audit?page={page}&pageSize={pageSize}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private async Task<MenuResponseDto?> GetMenuAsync(string token, Guid menuId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/menus/{menuId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MenuResponseDto>();
    }

    private async Task<string?> PollUntilTerminalAsync(string token, Guid menuId, TimeSpan deadline)
    {
        var stop = DateTimeOffset.UtcNow.Add(deadline);
        string? status;
        do
        {
            var menu = await GetMenuAsync(token, menuId);
            status = menu?.ProvisioningStatus;
            if (status is not null and not "Pending") return status;
            await Task.Delay(200);
        } while (DateTimeOffset.UtcNow < stop);
        return status;
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

        db.UserRoles.Add(new UserRole
        {
            UserId = admin.Id,
            RoleId = PlatformAdminRoleId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
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
        string? ProvisioningError);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record AuditPagedResultDto(
        IReadOnlyList<SchemaAuditEntryDto> Data,
        long Total,
        int Page,
        int PageSize,
        int TotalPages);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record SchemaAuditEntryDto(
        Guid Id,
        DateTimeOffset Timestamp,
        Guid? ActorId,
        string? ActorName,
        string DesignerId,
        int? FromVersion,
        int ToVersion,
        string DdlOperation,
        string[]? ColumnsAdded,
        string[]? ColumnsDropped,
        string? ColumnsDiff,
        string? CorrelationId,
        string? Notes);
}
