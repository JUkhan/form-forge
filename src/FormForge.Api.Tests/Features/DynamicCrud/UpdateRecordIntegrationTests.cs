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

// Story 6.4 — end-to-end coverage for PUT /api/data/{designerId}/{id}. Shares the
// [Collection("DynamicCrudTests")] grouping with the list / get / create suites so
// the four classes serialize against the same PostgreSQL container.
[Collection("DynamicCrudTests")]
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class UpdateRecordIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public UpdateRecordIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

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

    // ---------- AC-1 ----------

    [Fact]
    public async Task UpdateRecord_ValidPartialPayload_Returns200WithUpdatedRecord()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "upd_happy");

        var recordId = await CreateRecordAndGetIdAsync(token, "upd_happy", new { title = "Original" });
        // Tiny delay so updatedAt is provably later than createdAt; matches Story 6.2.
        await Task.Delay(50);

        using var response = await PutRecordAsync(token, "upd_happy", recordId, new { title = "Updated" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        // System columns camelCase per AR-46 Option C.
        Assert.Equal(recordId, root.GetProperty("id").GetGuid());
        Assert.Equal("Updated", root.GetProperty("title").GetString());
        Assert.False(root.GetProperty("isDeleted").GetBoolean());
        var createdAt = root.GetProperty("createdAt").GetDateTimeOffset();
        var updatedAt = root.GetProperty("updatedAt").GetDateTimeOffset();
        Assert.True(updatedAt > createdAt, "updatedAt must be newer than createdAt after PUT.");
        // updatedBy must equal the JWT actor UUID, not just any non-empty Guid.
        var actorUuid = GetUserIdFromToken(token);
        Assert.True(root.TryGetProperty("createdBy", out var createdByProp));
        Assert.NotEqual(Guid.Empty, createdByProp.GetGuid());
        Assert.True(root.TryGetProperty("updatedBy", out var updatedByProp));
        Assert.Equal(actorUuid, updatedByProp.GetGuid());
        // cascadeEventId must be present camelCase; cascade_event_id must be absent.
        Assert.True(root.TryGetProperty("cascadeEventId", out _));
        Assert.False(root.TryGetProperty("cascade_event_id", out _));
        // Raw snake_case must NOT appear.
        Assert.False(root.TryGetProperty("is_deleted", out _));
        Assert.False(root.TryGetProperty("created_by", out _));
        Assert.False(root.TryGetProperty("updated_by", out _));
    }

    // ---------- AC-2 ----------

    [Fact]
    public async Task UpdateRecord_SoftDeletedRecord_Returns422RecordDeleted()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "upd_deleted");

        var recordId = await CreateRecordAndGetIdAsync(token, "upd_deleted", new { title = "to delete" });

        // Soft-delete the row directly — Story 6.5 is not yet implemented.
        var setupConn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await setupConn.OpenAsync();
            await setupConn.ExecuteAsync(
                "UPDATE \"upd_deleted\" SET is_deleted = true WHERE id = @id",
                new { id = recordId });
        }
        finally
        {
            await setupConn.DisposeAsync();
        }

        using var response = await PutRecordAsync(token, "upd_deleted", recordId,
            new { title = "nope" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("RECORD_DELETED", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("errors.recordDeleted",
            doc.RootElement.GetProperty("messageKey").GetString());
    }

    // ---------- AC-3 ----------

    [Fact]
    public async Task UpdateRecord_AuditLogRow_AppendedWithPreviousAndNewValues()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "upd_audit");

        var recordId = await CreateRecordAndGetIdAsync(token, "upd_audit", new { title = "Old" });

        var before = DateTimeOffset.UtcNow.AddSeconds(-2);
        using var response = await PutRecordAsync(token, "upd_audit", recordId,
            new { title = "New" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = DateTimeOffset.UtcNow.AddSeconds(2);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var responseUpdatedBy = doc.RootElement.GetProperty("updatedBy").GetGuid();

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
                "SELECT record_id, designer_id, operation, actor_id, timestamp, new_values, previous_values, correlation_id FROM mutation_audit_log WHERE record_id = @recordId AND operation = 'UPDATE'",
                new { recordId });
            Assert.Equal(recordId, row.record_id);
            Assert.Equal("upd_audit", row.designer_id);
            Assert.Equal("UPDATE", row.operation);
            Assert.Equal(responseUpdatedBy, row.actor_id);
            Assert.InRange(row.timestamp, before, after);
            // AC-3 — deserialise JSON blobs and assert on concrete key-value pairs.
            Assert.False(string.IsNullOrEmpty(row.new_values));
            using var newDoc = JsonDocument.Parse(row.new_values!);
            Assert.Equal("New", newDoc.RootElement.GetProperty("title").GetString());
            Assert.False(string.IsNullOrEmpty(row.previous_values));
            using var prevDoc = JsonDocument.Parse(row.previous_values!);
            Assert.Equal("Old", prevDoc.RootElement.GetProperty("title").GetString());
            Assert.False(string.IsNullOrEmpty(row.correlation_id));
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    // ---------- AC-4 ----------

    [Fact]
    public async Task UpdateRecord_RecordNotFound_Returns404()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "upd_notfound");

        using var response = await PutRecordAsync(token, "upd_notfound", Guid.NewGuid(),
            new { title = "ghost" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("NOT_FOUND", doc.RootElement.GetProperty("code").GetString());
    }

    // ---------- AC-5 ----------

    [Fact]
    public async Task UpdateRecord_UnprovisionedDesigner_Returns404TableNotProvisioned()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var response = await PutRecordAsync(token, "never_bound_update", Guid.NewGuid(),
            new { title = "x" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("TABLE_NOT_PROVISIONED", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("errors.tableNotProvisioned",
            doc.RootElement.GetProperty("messageKey").GetString());
    }

    // ---------- AC-6 ----------

    [Fact]
    public async Task UpdateRecord_NoCanUpdate_Returns403()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "upd_forbidden");
        var recordId = await CreateRecordAndGetIdAsync(token, "upd_forbidden", new { title = "a" });

        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");
        using var response = await PutRecordAsync(viewerToken, "upd_forbidden", recordId,
            new { title = "denied" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("FORBIDDEN", doc.RootElement.GetProperty("code").GetString());
    }

    // ---------- AC-7 ----------

    [Fact]
    public async Task UpdateRecord_TypeMismatch_Returns422WithFieldError()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithNumericAsync(token, "upd_typemismatch");

        // We need a record to update — but a type-mismatch payload should be
        // rejected BEFORE the SELECT, so the record's existence is incidental.
        var recordId = await CreateRecordAndGetIdAsync(token, "upd_typemismatch", new { price = 1m });

        using var response = await PutRecordAsync(token, "upd_typemismatch", recordId,
            new { price = "not-a-number" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("errors", out var errors));
        Assert.True(errors.TryGetProperty("price", out var priceErrors));
        Assert.True(priceErrors.GetArrayLength() > 0);
    }

    // ---------- AC-8 ----------

    [Fact]
    public async Task UpdateRecord_UnknownFieldInPayload_Returns200IgnoresUnknownField()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "upd_unknown");
        var recordId = await CreateRecordAndGetIdAsync(token, "upd_unknown", new { title = "before" });

        using var response = await PutRecordAsync(token, "upd_unknown", recordId,
            new { title = "ok", ghost_field = "ignored" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", doc.RootElement.GetProperty("title").GetString());
        Assert.False(doc.RootElement.TryGetProperty("ghost_field", out _),
            "Unknown fields must be silently dropped (AC-8).");
    }

    // ---------- AC-1 edge: empty payload ----------

    [Fact]
    public async Task UpdateRecord_EmptyPayload_OnlyUpdatesTimestamps()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "upd_empty");
        var recordId = await CreateRecordAndGetIdAsync(token, "upd_empty", new { title = "stay" });
        await Task.Delay(50);

        using var response = await PutRecordAsync(token, "upd_empty", recordId, new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var createdAt = root.GetProperty("createdAt").GetDateTimeOffset();
        var updatedAt = root.GetProperty("updatedAt").GetDateTimeOffset();
        Assert.True(updatedAt > createdAt, "Empty payload should still bump updatedAt.");
        // Title retains its prior value.
        Assert.Equal("stay", root.GetProperty("title").GetString());
    }

    // ---------- AC-3 edge: empty-payload audit row ----------

    [Fact]
    public async Task UpdateRecord_EmptyPayload_AuditLogRow_BothValuesNull()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "upd_empty_audit");
        var recordId = await CreateRecordAndGetIdAsync(token, "upd_empty_audit", new { title = "x" });

        using var response = await PutRecordAsync(token, "upd_empty_audit", recordId, new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            var row = await conn.QuerySingleOrDefaultAsync<(string? new_values, string? previous_values)>(
                "SELECT new_values, previous_values FROM mutation_audit_log WHERE record_id = @recordId AND operation = 'UPDATE'",
                new { recordId });
            // Empty payload → both audit JSON columns must be NULL, not "{}".
            Assert.Null(row.new_values);
            Assert.Null(row.previous_values);
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    // ---------- AC-1: system columns in payload are ignored ----------

    [Fact]
    public async Task UpdateRecord_SystemColumnsInPayload_AreIgnored()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "upd_sys_ignore");
        var recordId = await CreateRecordAndGetIdAsync(token, "upd_sys_ignore", new { title = "before" });

        var fakeId = Guid.NewGuid();
        var fakeCreatedAt = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

        using var response = await PutRecordAsync(token, "upd_sys_ignore", recordId, new
        {
            id = fakeId,
            created_at = fakeCreatedAt,
            is_deleted = true,
            title = "after",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        // Server-side id wins over the fake id in the payload.
        Assert.Equal(recordId, root.GetProperty("id").GetGuid());
        Assert.NotEqual(fakeId, root.GetProperty("id").GetGuid());
        // createdAt unchanged — server never overwrites it on UPDATE.
        var createdAt = root.GetProperty("createdAt").GetDateTimeOffset();
        Assert.True(createdAt > new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "Server must ignore client-supplied created_at on UPDATE.");
        // Soft-delete flag from the payload is ignored.
        Assert.False(root.GetProperty("isDeleted").GetBoolean());
        // User field still applied.
        Assert.Equal("after", root.GetProperty("title").GetString());
    }

    // ---------- Helpers ----------

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

    private async Task<int> SetupProvisionedDesignerWithNumericAsync(string token, string designerId)
    {
        var menuId = await CreateMenuViaApiAsync(token, $"menu_{designerId}", 0);
        var rootElement = new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = new object[]
            {
                new { id = "f1", type = "NumberInput",
                      properties = new { fieldKey = "price" }, children = Array.Empty<object>() },
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

    private async Task<HttpResponseMessage> PutRecordAsync(string token, string designerId, Guid id, object payload)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/data/{Uri.EscapeDataString(designerId)}/{id}")
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
