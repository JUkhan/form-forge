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

// Story 6.3 — end-to-end coverage for POST /api/data/{designerId}. Shares the
// [Collection("DynamicCrudTests")] grouping with the list / get suites so the
// three classes serialize against the same PostgreSQL container instead of
// fighting over schema state.
[Collection("DynamicCrudTests")]
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class CreateRecordIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public CreateRecordIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

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
    public async Task CreateRecord_ValidPayload_Returns201WithCreatedRecord()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "create_happy");

        using var response = await PostRecordAsync(token, "create_happy", new { title = "My Record" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        // System columns are camelCase (AR-46 Option C).
        Assert.True(root.TryGetProperty("id", out var idProp));
        Assert.NotEqual(Guid.Empty, idProp.GetGuid());
        Assert.True(root.TryGetProperty("createdAt", out _));
        Assert.True(root.TryGetProperty("updatedAt", out _));
        Assert.False(root.GetProperty("isDeleted").GetBoolean());
        // AR-46 Option C — every system column must serialise as camelCase.
        Assert.True(root.TryGetProperty("createdBy", out var createdByProp));
        Assert.NotEqual(Guid.Empty, createdByProp.GetGuid());
        Assert.True(root.TryGetProperty("updatedBy", out var updatedByProp));
        Assert.Equal(createdByProp.GetGuid(), updatedByProp.GetGuid());
        Assert.True(root.TryGetProperty("cascadeEventId", out var cascadeEventIdProp));
        Assert.Equal(JsonValueKind.Null, cascadeEventIdProp.ValueKind);
        // User fieldKey is verbatim.
        Assert.Equal("My Record", root.GetProperty("title").GetString());
        // Raw snake_case system column names must NOT appear (renamed to camelCase).
        Assert.False(root.TryGetProperty("is_deleted", out _),
            "is_deleted should NOT appear (mapped to isDeleted per AR-46 Option C)");
        Assert.False(root.TryGetProperty("created_by", out _));
        Assert.False(root.TryGetProperty("updated_by", out _));
        Assert.False(root.TryGetProperty("cascade_event_id", out _));
    }

    // AC-1 final bullet — system columns in the client payload are silently ignored;
    // server-generated values always win regardless of what the client supplies.
    [Fact]
    public async Task CreateRecord_SystemColumnsInPayload_AreIgnored()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "create_sys_ignore");

        var fakeId = Guid.NewGuid();
        var fakeCreatedAt = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

        using var response = await PostRecordAsync(token, "create_sys_ignore", new
        {
            id = fakeId,
            created_at = fakeCreatedAt,
            is_deleted = true,
            title = "server wins",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        // Server-generated id must differ from the client-supplied id.
        Assert.NotEqual(fakeId, root.GetProperty("id").GetGuid());
        // createdAt must be in the request window, not the year 2000.
        var createdAt = root.GetProperty("createdAt").GetDateTimeOffset();
        Assert.True(createdAt > new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "Server must overwrite client-supplied created_at.");
        // isDeleted is always false on create regardless of client input.
        Assert.False(root.GetProperty("isDeleted").GetBoolean());
        // User field still applied.
        Assert.Equal("server wins", root.GetProperty("title").GetString());
    }

    // ---------- AC-2 ----------

    [Fact]
    public async Task CreateRecord_UnknownFieldInPayload_Returns201IgnoresUnknownField()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "create_unknown");

        using var response = await PostRecordAsync(token, "create_unknown",
            new { title = "ok", ghost_field = "ignored" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", doc.RootElement.GetProperty("title").GetString());
        Assert.False(doc.RootElement.TryGetProperty("ghost_field", out _),
            "Unknown fields must be silently dropped (AC-2).");
    }

    // ---------- AC-3 ----------

    [Fact]
    public async Task CreateRecord_TypeMismatch_Returns422WithFieldError()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithNumericAsync(token, "create_typemismatch");

        // price column is NUMERIC; send a non-numeric string → Layer 2 validation rejects.
        using var response = await PostRecordAsync(token, "create_typemismatch",
            new { price = "not-a-number" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("errors", out var errors),
            "422 ValidationProblemDetails must include an errors dict.");
        Assert.True(errors.TryGetProperty("price", out var priceErrors));
        Assert.True(priceErrors.GetArrayLength() > 0,
            "Field 'price' must have at least one error message.");
    }

    // ---------- AC-4 ----------

    [Fact]
    public async Task CreateRecord_AuditLogRow_Appended()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "create_audit");

        var before = DateTimeOffset.UtcNow.AddSeconds(-2);
        using var response = await PostRecordAsync(token, "create_audit", new { title = "audit me" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var after = DateTimeOffset.UtcNow.AddSeconds(2);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var recordId = doc.RootElement.GetProperty("id").GetGuid();
        var responseCreatedBy = doc.RootElement.GetProperty("createdBy").GetGuid();

        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            var row = await conn.QuerySingleOrDefaultAsync<(
                string designer_id,
                string operation,
                Guid? actor_id,
                DateTimeOffset timestamp,
                string? new_values,
                string? previous_values,
                string? correlation_id)>(
                "SELECT designer_id, operation, actor_id, timestamp, new_values, previous_values, correlation_id FROM mutation_audit_log WHERE record_id = @recordId",
                new { recordId });
            Assert.Equal("create_audit", row.designer_id);
            Assert.Equal("CREATE", row.operation);
            // AC-4: actor_id matches the authenticated user who created the record.
            Assert.Equal(responseCreatedBy, row.actor_id);
            // AC-4: timestamp falls within the request window.
            Assert.InRange(row.timestamp, before, after);
            // AC-4: new_values contains a JSON snapshot of the inserted user columns.
            Assert.False(string.IsNullOrEmpty(row.new_values));
            Assert.Contains("audit me", row.new_values, StringComparison.Ordinal);
            // previous_values is null for CREATE (entity comment / Dev Notes).
            Assert.Null(row.previous_values);
            // AC-4: correlation_id from the LogContext middleware is persisted.
            Assert.False(string.IsNullOrEmpty(row.correlation_id));
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    // ---------- AC-5 ----------

    [Fact]
    public async Task CreateRecord_UnprovisionedDesigner_Returns404TableNotProvisioned()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var response = await PostRecordAsync(token, "never_bound_create", new { title = "x" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("TABLE_NOT_PROVISIONED", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("errors.tableNotProvisioned",
            doc.RootElement.GetProperty("messageKey").GetString());
    }

    // ---------- AC-6 ----------

    [Fact]
    public async Task CreateRecord_NoCanCreate_Returns403()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "create_forbidden");

        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");
        using var response = await PostRecordAsync(viewerToken, "create_forbidden", new { title = "denied" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("FORBIDDEN", doc.RootElement.GetProperty("code").GetString());
    }

    // ---------- Empty payload (AC-1 / AC-2 combination) ----------

    [Fact]
    public async Task CreateRecord_EmptyPayload_Returns201WithNullUserFields()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "create_empty");

        using var response = await PostRecordAsync(token, "create_empty", new { });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("isDeleted").GetBoolean());
        // User field is included as null when omitted from payload.
        Assert.True(doc.RootElement.TryGetProperty("title", out var titleProp));
        Assert.Equal(JsonValueKind.Null, titleProp.ValueKind);
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
