using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Infrastructure.Persistence;
using FormForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FormForge.Api.Tests.Features.DynamicCrud;

// End-to-end coverage for DELETE /api/data/{designerId}/{id}/hard-delete. Permanent
// purge of a soft-deleted record AND its descendant subtree (Repeater children,
// TreeView nodes), walked in application code so it works without DB FK constraints.
// Shares the [Collection("DynamicCrudTests")] grouping so it serializes against the
// same Postgres container as the other DynamicCrud suites. Helpers mirror
// SoftDeleteIntegrationTests.
[Collection("DynamicCrudTests")]
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class HardDeleteIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public HardDeleteIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

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
            "TRUNCATE TABLE menu_role_assignments, menus, component_schema_versions, component_schemas, role_permissions, user_roles, roles, refresh_tokens, users, schema_audit_log, mutation_audit_log RESTART IDENTITY CASCADE;");

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
                          'mutation_audit_log',
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
            await _factory.DisposeAsync();
    }

    // ---------- Happy path: soft-deleted record is permanently removed ----------

    [Fact]
    public async Task HardDelete_SoftDeletedRecord_Returns200AndRowIsGone()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "hd_happy");
        var recordId = await CreateRecordAndGetIdAsync(token, "hd_happy", new { title = "purge me" });

        // Soft-delete first (the two-step trash → empty-trash contract).
        using (var soft = await SoftDeleteRecordAsync(token, "hd_happy", recordId))
            Assert.Equal(HttpStatusCode.OK, soft.StatusCode);

        using var response = await HardDeleteRecordAsync(token, "hd_happy", recordId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(recordId, doc.RootElement.GetProperty("id").GetGuid());

        // GET now 404s — the row is physically gone.
        using var getResp = await GetRecordAsync(token, "hd_happy", recordId);
        Assert.Equal(HttpStatusCode.NotFound, getResp.StatusCode);

        // Direct SELECT confirms zero rows remain.
        Assert.Equal(0, await RowCountByIdAsync("hd_happy", recordId));
    }

    // ---------- Not soft-deleted first → 422 ----------

    [Fact]
    public async Task HardDelete_LiveRecord_Returns422RecordNotSoftDeleted()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "hd_live");
        var recordId = await CreateRecordAndGetIdAsync(token, "hd_live", new { title = "still live" });

        using var response = await HardDeleteRecordAsync(token, "hd_live", recordId);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("RECORD_NOT_SOFT_DELETED", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("errors.recordNotSoftDeleted",
            doc.RootElement.GetProperty("messageKey").GetString());

        // The record must still exist (the failed hard delete is a no-op).
        Assert.Equal(1, await RowCountByIdAsync("hd_live", recordId));
    }

    // ---------- Record not found → 404 ----------

    [Fact]
    public async Task HardDelete_RecordNotFound_Returns404()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "hd_notfound");

        using var response = await HardDeleteRecordAsync(token, "hd_notfound", Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("NOT_FOUND", doc.RootElement.GetProperty("code").GetString());
    }

    // ---------- Unprovisioned designer → 404 ----------

    [Fact]
    public async Task HardDelete_UnprovisionedDesigner_Returns404TableNotProvisioned()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var response = await HardDeleteRecordAsync(token, "never_bound_hd", Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("TABLE_NOT_PROVISIONED", doc.RootElement.GetProperty("code").GetString());
    }

    // ---------- Non-admin cannot hard delete → 403 ----------

    [Fact]
    public async Task HardDelete_NonAdmin_Returns403()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "hd_forbidden");
        var recordId = await CreateRecordAndGetIdAsync(token, "hd_forbidden", new { title = "a" });
        using (var soft = await SoftDeleteRecordAsync(token, "hd_forbidden", recordId))
            Assert.Equal(HttpStatusCode.OK, soft.StatusCode);

        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");
        using var response = await HardDeleteRecordAsync(viewerToken, "hd_forbidden", recordId);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("FORBIDDEN", doc.RootElement.GetProperty("code").GetString());

        // The record is untouched by the rejected request.
        Assert.Equal(1, await RowCountByIdAsync("hd_forbidden", recordId));
    }

    // ---------- Audit row appended ----------

    [Fact]
    public async Task HardDelete_AuditLogRow_AppendedWithHardDeleteOperation()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "hd_audit");
        var recordId = await CreateRecordAndGetIdAsync(token, "hd_audit", new { title = "audit me" });
        using (var soft = await SoftDeleteRecordAsync(token, "hd_audit", recordId))
            Assert.Equal(HttpStatusCode.OK, soft.StatusCode);

        var before = DateTimeOffset.UtcNow.AddSeconds(-2);
        using var response = await HardDeleteRecordAsync(token, "hd_audit", recordId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = DateTimeOffset.UtcNow.AddSeconds(2);

        var actorUuid = GetUserIdFromToken(token);

        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            var row = await conn.QuerySingleOrDefaultAsync<(
                Guid record_id,
                string designer_id,
                string operation,
                Guid? actor_id,
                DateTimeOffset timestamp,
                string? new_values,
                string? previous_values,
                string? correlation_id)>(
                "SELECT record_id, designer_id, operation, actor_id, timestamp, new_values, previous_values, correlation_id FROM mutation_audit_log WHERE record_id = @recordId AND operation = 'HARD_DELETE'",
                new { recordId });
            Assert.Equal(recordId, row.record_id);
            Assert.Equal("hd_audit", row.designer_id);
            Assert.Equal("HARD_DELETE", row.operation);
            Assert.Equal(actorUuid, row.actor_id);
            Assert.InRange(row.timestamp, before, after);
            Assert.Null(row.new_values);
            Assert.Null(row.previous_values);
            Assert.False(string.IsNullOrEmpty(row.correlation_id));
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    // ---------- Cascade: Repeater children purged ----------

    [Fact]
    public async Task HardDelete_CascadeToRepeaterChildren_RemovesAllRows()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        const string parentDesignerId = "hd_casc_parent";
        const string childDesignerId = "hd_casc_child";

        var childRoot = new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = new object[]
            {
                new { id = "f1", type = "TextInput",
                      properties = new { fieldKey = "note" }, children = Array.Empty<object>() },
            },
        };
        await CreateAndPublishDesignerWithFieldsAsync(token, childDesignerId, childRoot);

        var parentRoot = new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = new object[]
            {
                new { id = "f1", type = "TextInput",
                      properties = new { fieldKey = "title" }, children = Array.Empty<object>() },
                new { id = "r1", type = "Repeater",
                      properties = new { rowDesignerId = childDesignerId, fieldKey = "line_items" }, children = Array.Empty<object>() },
            },
        };
        var parentVersion = await CreateAndPublishDesignerWithFieldsAsync(token, parentDesignerId, parentRoot);

        var menuId = await CreateMenuViaApiAsync(token, $"menu_{parentDesignerId}", 0);
        using (var bind = await PutBindingAsync(token, menuId, parentDesignerId, parentVersion))
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        var parentId = await InsertParentRowAsync(parentDesignerId, "parent row");
        var fkCol = $"parent_{parentDesignerId[..Math.Min(parentDesignerId.Length, 53)]}_id";
        var childId1 = await InsertChildRowAsync(childDesignerId, fkCol, parentId, "child row 1");
        var childId2 = await InsertChildRowAsync(childDesignerId, fkCol, parentId, "child row 2");

        // Soft-delete the parent (cascade-soft-deletes the children), then hard-delete.
        using (var soft = await SoftDeleteRecordAsync(token, parentDesignerId, parentId))
            Assert.Equal(HttpStatusCode.OK, soft.StatusCode);

        using var response = await HardDeleteRecordAsync(token, parentDesignerId, parentId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Parent AND both child rows are physically gone.
        Assert.Equal(0, await RowCountByIdAsync(parentDesignerId, parentId));
        Assert.Equal(0, await RowCountByIdAsync(childDesignerId, childId1));
        Assert.Equal(0, await RowCountByIdAsync(childDesignerId, childId2));
    }

    // ---------- Cascade: self-referencing tree purged to every depth ----------

    [Fact]
    public async Task HardDelete_CascadeOnSelfReferencingTree_RemovesAllDescendants()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        const string designerId = "hd_tree_node";

        var root = new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = new object[]
            {
                new { id = "f1", type = "TextInput",
                      properties = new { fieldKey = "node_name" }, children = Array.Empty<object>() },
                new { id = "r1", type = "Repeater",
                      properties = new { rowDesignerId = designerId, fieldKey = "child_nodes" }, children = Array.Empty<object>() },
            },
        };
        var version = await CreateAndPublishDesignerWithFieldsAsync(token, designerId, root);

        var menuId = await CreateMenuViaApiAsync(token, $"menu_{designerId}", 0);
        using (var bind = await PutBindingAsync(token, menuId, designerId, version))
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        var fkCol = $"parent_{designerId}_id";
        var rootId = await InsertTreeNodeAsync(designerId, fkCol, null, "root");
        var childId = await InsertTreeNodeAsync(designerId, fkCol, rootId, "child");
        var grandId = await InsertTreeNodeAsync(designerId, fkCol, childId, "grandchild");

        using (var soft = await SoftDeleteRecordAsync(token, designerId, rootId))
            Assert.Equal(HttpStatusCode.OK, soft.StatusCode);

        using var response = await HardDeleteRecordAsync(token, designerId, rootId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Every node — root, child AND grandchild — is physically gone.
        foreach (var nodeId in new[] { rootId, childId, grandId })
            Assert.Equal(0, await RowCountByIdAsync(designerId, nodeId));
    }

    // ---------- Helpers ----------

    private async Task<int> RowCountByIdAsync(string tableName, Guid id)
    {
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
#pragma warning disable CA2100
            return await conn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM \"{tableName}\" WHERE id = @id", new { id });
#pragma warning restore CA2100
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    private async Task<HttpResponseMessage> HardDeleteRecordAsync(string token, string designerId, Guid id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/data/{Uri.EscapeDataString(designerId)}/{id}/hard-delete");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SoftDeleteRecordAsync(string token, string designerId, Guid id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/data/{Uri.EscapeDataString(designerId)}/{id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private async Task<HttpResponseMessage> GetRecordAsync(string token, string designerId, Guid id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/data/{Uri.EscapeDataString(designerId)}/{id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private async Task<int> SetupProvisionedDesignerWithTitleAsync(string token, string designerId)
    {
        var menuId = await CreateMenuViaApiAsync(token, $"menu_{designerId}", 0);
        var rootElement = new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = new object[]
            {
                new { id = "f1", type = "TextInput",
                      properties = new { fieldKey = "title" }, children = Array.Empty<object>() },
            },
        };
        var version = await CreateAndPublishDesignerWithFieldsAsync(token, designerId, rootElement);

        using (var bind = await PutBindingAsync(token, menuId, designerId, version))
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);

        var status = await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10));
        Assert.Equal("Success", status);
        return version;
    }

    private async Task<HttpResponseMessage> PostRecordAsync(string token, string designerId, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/data/{designerId}")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private async Task<Guid> CreateRecordAndGetIdAsync(string token, string designerId, object payload)
    {
        using var response = await PostRecordAsync(token, designerId, payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(WebJsonOptions);
        return json.GetProperty("id").GetGuid();
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

    private async Task<int> CreateAndPublishDesignerWithFieldsAsync<TRoot>(
        string token, string designerId, TRoot rootElement)
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

    private async Task<HttpResponseMessage> PutBindingAsync(
        string token, Guid menuId, string designerId, int version)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put, $"/api/admin/menus/{menuId}/binding")
        {
            Content = JsonContent.Create(new { designerId, version }),
        };
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

#pragma warning disable CA2100
    private async Task<Guid> InsertParentRowAsync(string tableName, string title)
    {
        var id = Guid.NewGuid();
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                $"INSERT INTO \"{tableName}\" (id, created_at, updated_at, is_deleted, title) " +
                "VALUES (@id, NOW(), NOW(), false, @title)",
                new { id, title });
        }
        finally
        {
            await conn.DisposeAsync();
        }
        return id;
    }

    private async Task<Guid> InsertChildRowAsync(string childTableName, string fkColumn, Guid parentId, string note)
    {
        var id = Guid.NewGuid();
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                $"INSERT INTO \"{childTableName}\" (id, created_at, updated_at, is_deleted, note, \"{fkColumn}\") " +
                $"VALUES (@id, NOW(), NOW(), false, @note, @parentId)",
                new { id, note, parentId });
        }
        finally
        {
            await conn.DisposeAsync();
        }
        return id;
    }

    private async Task<Guid> InsertTreeNodeAsync(string tableName, string fkColumn, Guid? parentId, string nodeName)
    {
        var id = Guid.NewGuid();
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                $"INSERT INTO \"{tableName}\" (id, created_at, updated_at, is_deleted, node_name, \"{fkColumn}\") " +
                "VALUES (@id, NOW(), NOW(), false, @nodeName, @parentId)",
                new { id, nodeName, parentId });
        }
        finally
        {
            await conn.DisposeAsync();
        }
        return id;
    }
#pragma warning restore CA2100

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

        db.UserRoles.Add(new UserRole { UserId = admin.Id, RoleId = PlatformAdminRoleId, CreatedAt = DateTimeOffset.UtcNow });
        db.UserRoles.Add(new UserRole { UserId = viewer.Id, RoleId = ViewerRoleId, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    private static Guid GetUserIdFromToken(string token)
    {
        var payload = token.Split('.')[1];
        var base64 = payload.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("userId").GetGuid();
    }

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record LoginResponseDto(string AccessToken, string RefreshToken, int ExpiresIn);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record MenuResponseDto(
        Guid Id, string Name, int Order, string? Icon, bool IsActive,
        Guid? ParentId, IReadOnlyList<Guid> AllowedRoleIds,
        DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt,
        string? DesignerId, int? BoundVersion,
        string? ProvisioningStatus, string? ProvisioningError);
}
