using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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

// Story 6.6 — end-to-end coverage for PUT /api/data/{designerId}/{id}/restore.
// Shares the [Collection("DynamicCrudTests")] grouping with the list / get / create
// / update / soft-delete suites so all classes serialize against the same Postgres
// container. Helpers mirror SoftDeleteIntegrationTests verbatim per Story 6.6 spec.
[Collection("DynamicCrudTests")]
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class RestoreIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public RestoreIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

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
    public async Task Restore_SoftDeletedRecord_Returns200WithIsDeletedFalse()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "restore_happy");
        var recordId = await CreateRecordAndGetIdAsync(token, "restore_happy", new { title = "to restore" });

        // Soft-delete via the API so cascade_event_id is left NULL (no children).
        using (var del = await DeleteRecordAsync(token, "restore_happy", recordId))
            Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var before = DateTimeOffset.UtcNow.AddSeconds(-2);
        using var response = await RestoreRecordAsync(token, "restore_happy", recordId);
        var after = DateTimeOffset.UtcNow.AddSeconds(2);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(recordId, root.GetProperty("id").GetGuid());
        Assert.False(root.GetProperty("isDeleted").GetBoolean());

        var actorUuid = GetUserIdFromToken(token);
        Assert.True(root.TryGetProperty("updatedBy", out var updatedByProp));
        Assert.Equal(actorUuid, updatedByProp.GetGuid());

        // AC-1 — updatedAt is the server-generated restore timestamp.
        Assert.True(root.TryGetProperty("updatedAt", out var updatedAtProp));
        Assert.InRange(updatedAtProp.GetDateTimeOffset(), before, after);

        // AC-1 — cascadeEventId is cleared on restore.
        Assert.True(root.TryGetProperty("cascadeEventId", out var cascadeEventIdProp));
        Assert.Equal(JsonValueKind.Null, cascadeEventIdProp.ValueKind);

        // Verify the underlying row changed in the DB, not just the response overlay.
        var dbConn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await dbConn.OpenAsync();
            var row = await dbConn.QuerySingleAsync<(bool is_deleted, Guid? cascade_event_id, DateTimeOffset updated_at)>(
                "SELECT is_deleted, cascade_event_id, updated_at FROM \"restore_happy\" WHERE id = @id",
                new { id = recordId });
            Assert.False(row.is_deleted);
            Assert.Null(row.cascade_event_id);
            Assert.InRange(row.updated_at, before, after);
        }
        finally
        {
            await dbConn.DisposeAsync();
        }
    }

    // ---------- AC-5 ----------

    [Fact]
    public async Task Restore_RecordNotFound_Returns404()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "restore_notfound");

        var missingId = Guid.NewGuid();
        using var response = await RestoreRecordAsync(token, "restore_notfound", missingId);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("NOT_FOUND", doc.RootElement.GetProperty("code").GetString());

        // No audit row may be written for a 404 — early-return is before the audit INSERT.
        var auditConn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await auditConn.OpenAsync();
            var auditCount = await auditConn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM mutation_audit_log WHERE record_id = @id",
                new { id = missingId });
            Assert.Equal(0, auditCount);
        }
        finally
        {
            await auditConn.DisposeAsync();
        }
    }

    // ---------- AC-6 ----------

    [Fact]
    public async Task Restore_RecordNotDeleted_Returns422()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "restore_active");
        var recordId = await CreateRecordAndGetIdAsync(token, "restore_active", new { title = "still active" });

        using var response = await RestoreRecordAsync(token, "restore_active", recordId);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("RECORD_NOT_DELETED", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("errors.recordNotDeleted",
            doc.RootElement.GetProperty("messageKey").GetString());

        // No RESTORE audit row may be written when the handler short-circuits with 422.
        var auditConn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await auditConn.OpenAsync();
            var auditCount = await auditConn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM mutation_audit_log WHERE record_id = @id AND operation = 'RESTORE'",
                new { id = recordId });
            Assert.Equal(0, auditCount);
        }
        finally
        {
            await auditConn.DisposeAsync();
        }
    }

    // ---------- AC-7 ----------

    [Fact]
    public async Task Restore_UnprovisionedDesigner_Returns404TableNotProvisioned()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var response = await RestoreRecordAsync(token, "never_bound_restore", Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("TABLE_NOT_PROVISIONED", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("errors.tableNotProvisioned",
            doc.RootElement.GetProperty("messageKey").GetString());
    }

    // ---------- AC-8 ----------

    [Fact]
    public async Task Restore_NoCanUpdate_Returns403()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "restore_forbidden");
        var recordId = await CreateRecordAndGetIdAsync(token, "restore_forbidden", new { title = "a" });
        using (var del = await DeleteRecordAsync(token, "restore_forbidden", recordId))
            Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");
        using var response = await RestoreRecordAsync(viewerToken, "restore_forbidden", recordId);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("FORBIDDEN", doc.RootElement.GetProperty("code").GetString());
    }

    // ---------- AC-4 ----------

    [Fact]
    public async Task Restore_AuditLogRow_AppendedWithRestoreOperation()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "restore_audit");
        var recordId = await CreateRecordAndGetIdAsync(token, "restore_audit", new { title = "audit me" });
        using (var del = await DeleteRecordAsync(token, "restore_audit", recordId))
            Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var before = DateTimeOffset.UtcNow.AddSeconds(-2);
        using var response = await RestoreRecordAsync(token, "restore_audit", recordId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = DateTimeOffset.UtcNow.AddSeconds(2);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var responseUpdatedAt = doc.RootElement.GetProperty("updatedAt").GetDateTimeOffset();

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
                "SELECT record_id, designer_id, operation, actor_id, timestamp, new_values, previous_values, correlation_id FROM mutation_audit_log WHERE record_id = @recordId AND operation = 'RESTORE'",
                new { recordId });
            Assert.Equal(recordId, row.record_id);
            Assert.Equal("restore_audit", row.designer_id);
            Assert.Equal("RESTORE", row.operation);
            Assert.Equal(actorUuid, row.actor_id);
            Assert.InRange(row.timestamp, before, after);
            // AC-4 — audit timestamp matches the updatedAt used in the UPDATE.
            // Both fields receive the same `ts` variable inside RestoreRecordHandler;
            // PostgreSQL TIMESTAMPTZ stores at microsecond precision (.NET ticks are
            // 100ns), so allow a 1µs tolerance to absorb the round-trip truncation.
            Assert.True(Math.Abs((responseUpdatedAt - row.timestamp).Ticks) <= 10,
                $"audit timestamp {row.timestamp:O} differs from response updatedAt {responseUpdatedAt:O} by more than 1µs");
            // AC-4 — both JSON columns must be NULL for restore (no field diff).
            Assert.Null(row.new_values);
            Assert.Null(row.previous_values);
            Assert.False(string.IsNullOrEmpty(row.correlation_id));
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    // ---------- AC-1 + AR-46 Option C ----------

    [Fact]
    public async Task Restore_SystemColumnsInResponse_AreCamelCase()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "restore_camel");
        var recordId = await CreateRecordAndGetIdAsync(token, "restore_camel", new { title = "x" });
        using (var del = await DeleteRecordAsync(token, "restore_camel", recordId))
            Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        using var response = await RestoreRecordAsync(token, "restore_camel", recordId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        // Present (camelCase).
        Assert.True(root.TryGetProperty("id", out _));
        Assert.True(root.TryGetProperty("createdAt", out _));
        Assert.True(root.TryGetProperty("updatedAt", out _));
        Assert.True(root.TryGetProperty("createdBy", out _));
        Assert.True(root.TryGetProperty("updatedBy", out _));
        Assert.True(root.TryGetProperty("isDeleted", out _));
        Assert.True(root.TryGetProperty("cascadeEventId", out _));
        // isDeleted is now false; cascadeEventId is JSON null (cleared on restore).
        Assert.False(root.GetProperty("isDeleted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("cascadeEventId").ValueKind);

        // Absent (raw snake_case must NOT appear).
        Assert.False(root.TryGetProperty("is_deleted", out _));
        Assert.False(root.TryGetProperty("created_by", out _));
        Assert.False(root.TryGetProperty("updated_by", out _));
        Assert.False(root.TryGetProperty("cascade_event_id", out _));
    }

    // ---------- AC-2 + AC-3 ----------

    [Fact]
    public async Task Restore_CascadeDelete_ThenRestore_OnlyRestoresMatchingChildren()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        const string parentDesignerId = "restore_casc_parent";
        const string childDesignerId  = "restore_casc_child";

        // Child schema: one TextInput (note)
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

        // Parent schema: one TextInput (title) + one Repeater → child
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

        // Bind + provision parent (child table auto-provisioned by Story 5.5)
        var menuId = await CreateMenuViaApiAsync(token, $"menu_{parentDesignerId}", 0);
        using (var bind = await PutBindingAsync(token, menuId, parentDesignerId, parentVersion))
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        var status = await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10));
        Assert.Equal("Success", status);

        // Insert parent row + 2 child rows + 1 unrelated child row (no FK to this parent).
        var parentId = await InsertParentRowAsync(parentDesignerId, "parent row");
        var fkCol    = $"parent_{parentDesignerId[..Math.Min(parentDesignerId.Length, 53)]}_id";
        var childId1 = await InsertChildRowAsync(childDesignerId, fkCol, parentId, "child row 1");
        var childId2 = await InsertChildRowAsync(childDesignerId, fkCol, parentId, "child row 2");

        // DELETE parent → parent + both children share one cascade_event_id.
        using (var del = await DeleteRecordAsync(token, parentDesignerId, parentId))
            Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        // AC-3 setup: directly soft-delete a THIRD child (different/null cascade_event_id)
        // using NULL — it must NOT be restored when the parent's cascade event fires.
        var childId3 = await InsertChildRowAsync(childDesignerId, fkCol, parentId, "child row 3");
        // AC-3 setup: a FOURTH child with a DIFFERENT non-null cascade_event_id
        // (simulating a row soft-deleted by an unrelated cascade event). It must
        // also NOT be restored — exercises the non-null-but-mismatched UUID path
        // distinctly from the NULL path of childId3.
        var childId4 = await InsertChildRowAsync(childDesignerId, fkCol, parentId, "child row 4");
        var unrelatedCascadeId = Guid.NewGuid();
        var setupConn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await setupConn.OpenAsync();
            await setupConn.ExecuteAsync(
                $"UPDATE \"{childDesignerId}\" SET is_deleted = true, cascade_event_id = NULL WHERE id = @id",
                new { id = childId3 });
            await setupConn.ExecuteAsync(
                $"UPDATE \"{childDesignerId}\" SET is_deleted = true, cascade_event_id = @cascadeId WHERE id = @id",
                new { id = childId4, cascadeId = unrelatedCascadeId });
        }
        finally
        {
            await setupConn.DisposeAsync();
        }

        // RESTORE the parent
        using var response = await RestoreRecordAsync(token, parentDesignerId, parentId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.False(root.GetProperty("isDeleted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("cascadeEventId").ValueKind);

        // Direct Npgsql verification: parent + childId1 + childId2 restored;
        // childId3 (different cascade_event_id) still deleted.
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();

            var parentRow = await conn.QuerySingleAsync<(bool is_deleted, Guid? cascade_event_id)>(
                $"SELECT is_deleted, cascade_event_id FROM \"{parentDesignerId}\" WHERE id = @id",
                new { id = parentId });
            Assert.False(parentRow.is_deleted);
            Assert.Null(parentRow.cascade_event_id);

            var child1Row = await conn.QuerySingleAsync<(bool is_deleted, Guid? cascade_event_id)>(
                $"SELECT is_deleted, cascade_event_id FROM \"{childDesignerId}\" WHERE id = @id",
                new { id = childId1 });
            Assert.False(child1Row.is_deleted);
            Assert.Null(child1Row.cascade_event_id);

            var child2Row = await conn.QuerySingleAsync<(bool is_deleted, Guid? cascade_event_id)>(
                $"SELECT is_deleted, cascade_event_id FROM \"{childDesignerId}\" WHERE id = @id",
                new { id = childId2 });
            Assert.False(child2Row.is_deleted);
            Assert.Null(child2Row.cascade_event_id);

            // AC-3 — child with NULL cascade_event_id is NOT restored.
            var child3Row = await conn.QuerySingleAsync<(bool is_deleted, Guid? cascade_event_id)>(
                $"SELECT is_deleted, cascade_event_id FROM \"{childDesignerId}\" WHERE id = @id",
                new { id = childId3 });
            Assert.True(child3Row.is_deleted);
            Assert.Null(child3Row.cascade_event_id);

            // AC-3 — child with a DIFFERENT non-null cascade_event_id is also NOT
            // restored; its prior cascade_event_id is preserved (not cleared).
            var child4Row = await conn.QuerySingleAsync<(bool is_deleted, Guid? cascade_event_id)>(
                $"SELECT is_deleted, cascade_event_id FROM \"{childDesignerId}\" WHERE id = @id",
                new { id = childId4 });
            Assert.True(child4Row.is_deleted);
            Assert.Equal(unrelatedCascadeId, child4Row.cascade_event_id);
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    // ---------- Helpers (mirrored from SoftDeleteIntegrationTests) ----------

    private async Task<HttpResponseMessage> RestoreRecordAsync(string token, string designerId, Guid id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"/api/data/{Uri.EscapeDataString(designerId)}/{id}/restore");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent("", Encoding.UTF8, "application/json");
        return await _client!.SendAsync(request);
    }

    private async Task<HttpResponseMessage> DeleteRecordAsync(string token, string designerId, Guid id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete,
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
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
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
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
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
