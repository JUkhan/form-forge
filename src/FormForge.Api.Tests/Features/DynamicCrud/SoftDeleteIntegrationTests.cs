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

// Story 6.5 — end-to-end coverage for DELETE /api/data/{designerId}/{id}. Shares
// the [Collection("DynamicCrudTests")] grouping with the list / get / create / update
// suites so the five classes serialize against the same PostgreSQL container.
[Collection("DynamicCrudTests")]
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class SoftDeleteIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public SoftDeleteIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

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
    public async Task SoftDelete_ValidRecord_Returns200WithIsDeletedTrue()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "del_happy");

        var recordId = await CreateRecordAndGetIdAsync(token, "del_happy", new { title = "to soft-delete" });
        // Tiny delay so updatedAt is provably later than createdAt.
        await Task.Delay(50);

        using var response = await DeleteRecordAsync(token, "del_happy", recordId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(recordId, root.GetProperty("id").GetGuid());
        Assert.True(root.GetProperty("isDeleted").GetBoolean());
        var createdAt = root.GetProperty("createdAt").GetDateTimeOffset();
        var updatedAt = root.GetProperty("updatedAt").GetDateTimeOffset();
        Assert.True(updatedAt > createdAt, "updatedAt must be newer than createdAt after DELETE.");

        var actorUuid = GetUserIdFromToken(token);
        Assert.True(root.TryGetProperty("updatedBy", out var updatedByProp));
        Assert.Equal(actorUuid, updatedByProp.GetGuid());

        // createdBy must survive the overlay and equal the actor who created the record.
        Assert.True(root.TryGetProperty("createdBy", out var createdByProp));
        Assert.Equal(actorUuid, createdByProp.GetGuid());

        // Dev Notes §4 — cascadeEventId must be present AND JSON null (not absent
        // and not a uuid) for an individual no-children delete.
        Assert.True(root.TryGetProperty("cascadeEventId", out var cascadeEventIdProp));
        Assert.Equal(JsonValueKind.Null, cascadeEventIdProp.ValueKind);
    }

    // ---------- AC-4 ----------

    [Fact]
    public async Task SoftDelete_RecordNotFound_Returns404()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "del_notfound");

        using var response = await DeleteRecordAsync(token, "del_notfound", Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("NOT_FOUND", doc.RootElement.GetProperty("code").GetString());
    }

    // ---------- AC-5 ----------

    [Fact]
    public async Task SoftDelete_UnprovisionedDesigner_Returns404TableNotProvisioned()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var response = await DeleteRecordAsync(token, "never_bound_delete", Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("TABLE_NOT_PROVISIONED", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("errors.tableNotProvisioned",
            doc.RootElement.GetProperty("messageKey").GetString());
    }

    // ---------- AC-6 ----------

    [Fact]
    public async Task SoftDelete_NoCanDelete_Returns403()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "del_forbidden");
        var recordId = await CreateRecordAndGetIdAsync(token, "del_forbidden", new { title = "a" });

        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");
        using var response = await DeleteRecordAsync(viewerToken, "del_forbidden", recordId);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("FORBIDDEN", doc.RootElement.GetProperty("code").GetString());
    }

    // ---------- AC-7 ----------

    [Fact]
    public async Task SoftDelete_AlreadyDeletedRecord_Returns422RecordAlreadyDeleted()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "del_already");
        var recordId = await CreateRecordAndGetIdAsync(token, "del_already", new { title = "marked" });

        // Soft-delete the row directly so we can test the 422 second-delete path.
        var setupConn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await setupConn.OpenAsync();
            await setupConn.ExecuteAsync(
                "UPDATE \"del_already\" SET is_deleted = true WHERE id = @id",
                new { id = recordId });
        }
        finally
        {
            await setupConn.DisposeAsync();
        }

        using var response = await DeleteRecordAsync(token, "del_already", recordId);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("RECORD_ALREADY_DELETED", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("errors.recordAlreadyDeleted",
            doc.RootElement.GetProperty("messageKey").GetString());
    }

    // ---------- AC-3 ----------

    [Fact]
    public async Task SoftDelete_AuditLogRow_AppendedWithSoftDeleteOperation()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "del_audit");
        var recordId = await CreateRecordAndGetIdAsync(token, "del_audit", new { title = "audit me" });

        var before = DateTimeOffset.UtcNow.AddSeconds(-2);
        using var response = await DeleteRecordAsync(token, "del_audit", recordId);
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
                "SELECT record_id, designer_id, operation, actor_id, timestamp, new_values, previous_values, correlation_id FROM mutation_audit_log WHERE record_id = @recordId AND operation = 'SOFT_DELETE'",
                new { recordId });
            Assert.Equal(recordId, row.record_id);
            Assert.Equal("del_audit", row.designer_id);
            Assert.Equal("SOFT_DELETE", row.operation);
            Assert.Equal(actorUuid, row.actor_id);
            Assert.InRange(row.timestamp, before, after);
            // AC-3 — both JSON columns must be NULL for soft-delete (no field diff).
            Assert.Null(row.new_values);
            Assert.Null(row.previous_values);
            Assert.False(string.IsNullOrEmpty(row.correlation_id));
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    // ---------- AC-1 extended: soft-deleted record is preserved in the table ----------

    [Fact]
    public async Task SoftDelete_RecordDoesNotExistInDbAfterSoftDelete()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "del_preserved");
        var recordId = await CreateRecordAndGetIdAsync(token, "del_preserved", new { title = "stays" });

        using var del = await DeleteRecordAsync(token, "del_preserved", recordId);
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        // GET the same record — the row is preserved (not hard-deleted) so the
        // single-record handler still returns 200 with isDeleted: true.
        using var getResp = await GetRecordAsync(token, "del_preserved", recordId);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        using var getDoc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync());
        Assert.True(getDoc.RootElement.GetProperty("isDeleted").GetBoolean());

        // Direct SELECT via Npgsql confirms the row still exists with is_deleted = true.
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            var isDeleted = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT is_deleted FROM \"del_preserved\" WHERE id = @id",
                new { id = recordId });
            Assert.True(isDeleted);
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    // ---------- AC-1 extended: list with isDeleted filter ----------

    [Fact]
    public async Task SoftDelete_DeletedRecordIsReturnedByList_WithIsDeletedTrue()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "del_list");
        var recordId = await CreateRecordAndGetIdAsync(token, "del_list", new { title = "list me" });

        using var del = await DeleteRecordAsync(token, "del_list", recordId);
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        using var listResp = await ListRecordsAsync(token,
            "del_list?filter[isDeleted]=true");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var items = listDoc.RootElement.GetProperty("data");
        // The deleted record is present in the filtered list.
        Assert.Equal(1, items.GetArrayLength());
        var item = items[0];
        Assert.Equal(recordId, item.GetProperty("id").GetGuid());
        Assert.True(item.GetProperty("isDeleted").GetBoolean());
    }

    // ---------- AR-46 Option C: camelCase system columns ----------

    [Fact]
    public async Task SoftDelete_SystemColumnsInResponse_AreCamelCase()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "del_camel");
        var recordId = await CreateRecordAndGetIdAsync(token, "del_camel", new { title = "x" });

        using var response = await DeleteRecordAsync(token, "del_camel", recordId);
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

        // Absent (raw snake_case must NOT appear).
        Assert.False(root.TryGetProperty("is_deleted", out _));
        Assert.False(root.TryGetProperty("created_by", out _));
        Assert.False(root.TryGetProperty("updated_by", out _));
        Assert.False(root.TryGetProperty("cascade_event_id", out _));
    }

    // ---------- AC-2 ----------

    [Fact]
    public async Task SoftDelete_CascadeToRepeaterChildren_DeletesChildRowsAtomically()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        const string parentDesignerId = "del_casc_parent";
        const string childDesignerId  = "del_casc_child";

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

        // Insert parent row and two child rows directly via Npgsql.
        var parentId = await InsertParentRowAsync(parentDesignerId, "parent row");
        var fkCol    = $"parent_{parentDesignerId[..Math.Min(parentDesignerId.Length, 53)]}_id";
        var childId1 = await InsertChildRowAsync(childDesignerId, fkCol, parentId, "child row 1");
        var childId2 = await InsertChildRowAsync(childDesignerId, fkCol, parentId, "child row 2");

        // DELETE the parent
        using var response = await DeleteRecordAsync(token, parentDesignerId, parentId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.True(root.GetProperty("isDeleted").GetBoolean());

        // cascadeEventId must be a non-empty UUID (not null) when children were cascade-deleted.
        Assert.True(root.TryGetProperty("cascadeEventId", out var cascadeEventIdProp));
        Assert.Equal(JsonValueKind.String, cascadeEventIdProp.ValueKind);
        var cascadeEventId = cascadeEventIdProp.GetGuid();
        Assert.NotEqual(Guid.Empty, cascadeEventId);

        // Verify via direct Npgsql: parent + both child rows have is_deleted=true
        // and share the same cascade_event_id (atomicity / same transaction).
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();

            var parentRow = await conn.QuerySingleAsync<(bool is_deleted, Guid? cascade_event_id)>(
                $"SELECT is_deleted, cascade_event_id FROM \"{parentDesignerId}\" WHERE id = @id",
                new { id = parentId });
            Assert.True(parentRow.is_deleted);
            Assert.Equal(cascadeEventId, parentRow.cascade_event_id);

            var child1Row = await conn.QuerySingleAsync<(bool is_deleted, Guid? cascade_event_id)>(
                $"SELECT is_deleted, cascade_event_id FROM \"{childDesignerId}\" WHERE id = @id",
                new { id = childId1 });
            Assert.True(child1Row.is_deleted);
            Assert.Equal(cascadeEventId, child1Row.cascade_event_id);

            var child2Row = await conn.QuerySingleAsync<(bool is_deleted, Guid? cascade_event_id)>(
                $"SELECT is_deleted, cascade_event_id FROM \"{childDesignerId}\" WHERE id = @id",
                new { id = childId2 });
            Assert.True(child2Row.is_deleted);
            Assert.Equal(cascadeEventId, child2Row.cascade_event_id);
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task SoftDelete_CascadeOnSelfReferencingTree_DeletesAllDescendants()
    {
        // A component whose Repeater references ITSELF is an adjacency-list tree:
        // one table with a parent_<table>_id self-FK. Deleting a node must cascade
        // through the self-edge to EVERY depth — a regression guard against the
        // designer-id cycle guard halting the cascade after a single level.
        var token = await LoginAsync("admin@example.com", "Password1!");

        const string designerId = "del_tree_node";

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

        // Build a 3-level tree in the one table: root → child → grandchild.
        var fkCol = $"parent_{designerId}_id";
        var rootId  = await InsertTreeNodeAsync(designerId, fkCol, null, "root");
        var childId = await InsertTreeNodeAsync(designerId, fkCol, rootId, "child");
        var grandId = await InsertTreeNodeAsync(designerId, fkCol, childId, "grandchild");

        using var response = await DeleteRecordAsync(token, designerId, rootId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rootEl = doc.RootElement;
        Assert.True(rootEl.GetProperty("isDeleted").GetBoolean());
        var cascadeEventId = rootEl.GetProperty("cascadeEventId").GetGuid();
        Assert.NotEqual(Guid.Empty, cascadeEventId);

        // Every node — root, child AND grandchild — soft-deleted under one cascade event.
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            foreach (var nodeId in new[] { rootId, childId, grandId })
            {
                var row = await conn.QuerySingleAsync<(bool is_deleted, Guid? cascade_event_id)>(
                    $"SELECT is_deleted, cascade_event_id FROM \"{designerId}\" WHERE id = @id",
                    new { id = nodeId });
                Assert.True(row.is_deleted, $"node {nodeId} should be soft-deleted");
                Assert.Equal(cascadeEventId, row.cascade_event_id);
            }
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task SoftDelete_CascadeOnTreeViewSelfRef_DeletesAllDescendants()
    {
        // A TreeView in a HOST designer turns a SEPARATE node-template table into a
        // self-referencing tree (parent_<node>_id). Crucially the node table's OWN
        // RootElement does NOT reference itself — so the cascade graph has no self-edge
        // from parsing. The delete handler must detect the physical self-FK column and
        // inject the self-edge data-driven, or the cascade would orphan every descendant.
        var token = await LoginAsync("admin@example.com", "Password1!");

        const string nodeDesignerId = "tv_del_node";
        const string hostDesignerId = "tv_del_host";

        // Node template: a plain CRUD designer (no self-reference in its own RootElement).
        var nodeRoot = new
        {
            id = "root", type = "Stack", properties = new { }, children = new object[]
            {
                new { id = "nf1", type = "TextInput",
                      properties = new { fieldKey = "node_name" }, children = Array.Empty<object>() },
            },
        };
        var nodeVersion = await CreateAndPublishDesignerWithFieldsAsync(token, nodeDesignerId, nodeRoot);

        // Bind the node template to its own menu → provisions its table AND grants the
        // admin CRUD on it (needed for the DELETE below). No self-FK yet at this point.
        var nodeMenuId = await CreateMenuViaApiAsync(token, $"menu_{nodeDesignerId}", 0);
        using (var bind = await PutBindingAsync(token, nodeMenuId, nodeDesignerId, nodeVersion))
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, nodeMenuId, TimeSpan.FromSeconds(10)));

        // Host designer carrying a TreeView referencing the node template. Binding +
        // provisioning the host is what adds the self-FK parent_tv_del_node_id to the node table.
        var hostRoot = new
        {
            id = "root", type = "Stack", properties = new { }, children = new object[]
            {
                new { id = "tv1", type = "TreeView",
                      properties = new { fieldKey = "selected_nodes", rowDesignerId = nodeDesignerId },
                      children = Array.Empty<object>() },
            },
        };
        var hostVersion = await CreateAndPublishDesignerWithFieldsAsync(token, hostDesignerId, hostRoot);
        var hostMenuId = await CreateMenuViaApiAsync(token, $"menu_{hostDesignerId}", 1);
        using (var bind = await PutBindingAsync(token, hostMenuId, hostDesignerId, hostVersion))
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, hostMenuId, TimeSpan.FromSeconds(10)));

        // 3-level tree in the node table via the self-FK the host provisioning created.
        var fkCol = $"parent_{nodeDesignerId}_id";
        var rootId  = await InsertTreeNodeAsync(nodeDesignerId, fkCol, null, "root");
        var childId = await InsertTreeNodeAsync(nodeDesignerId, fkCol, rootId, "child");
        var grandId = await InsertTreeNodeAsync(nodeDesignerId, fkCol, childId, "grandchild");

        using var response = await DeleteRecordAsync(token, nodeDesignerId, rootId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("isDeleted").GetBoolean());
        var cascadeEventId = doc.RootElement.GetProperty("cascadeEventId").GetGuid();
        Assert.NotEqual(Guid.Empty, cascadeEventId);

        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            foreach (var nodeId in new[] { rootId, childId, grandId })
            {
                var row = await conn.QuerySingleAsync<(bool is_deleted, Guid? cascade_event_id)>(
                    $"SELECT is_deleted, cascade_event_id FROM \"{nodeDesignerId}\" WHERE id = @id",
                    new { id = nodeId });
                Assert.True(row.is_deleted, $"node {nodeId} should be soft-deleted");
                Assert.Equal(cascadeEventId, row.cascade_event_id);
            }
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task TreeList_OnProvisionedDesignerWithoutHost_SelfHealsSelfFk_Returns200()
    {
        // Reproduces the "no self-reference column" 422: a plain provisioned designer that
        // no TreeView host has referenced yet. GET /tree must self-heal by adding the
        // self-FK on demand and return 200 (empty tree), not 422.
        var token = await LoginAsync("admin@example.com", "Password1!");

        const string designerId = "lone_tree";
        var root = new
        {
            id = "root", type = "Stack", properties = new { }, children = new object[]
            {
                new { id = "f1", type = "TextInput",
                      properties = new { fieldKey = "node_name" }, children = Array.Empty<object>() },
            },
        };
        var version = await CreateAndPublishDesignerWithFieldsAsync(token, designerId, root);
        var menuId = await CreateMenuViaApiAsync(token, $"menu_{designerId}", 0);
        using (var bind = await PutBindingAsync(token, menuId, designerId, version))
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        // No self-FK yet — confirm, then hit the endpoint.
        Assert.False(await ColumnExistsAsync(designerId, $"parent_{designerId}_id"));

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/data/{designerId}/tree?pageSize=2");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The self-FK was created on demand.
        Assert.True(await ColumnExistsAsync(designerId, $"parent_{designerId}_id"));
    }

    [Fact]
    public async Task TreeDescendants_ReturnsAllDescendantIds_ExcludingTheParent()
    {
        // "All select" cascade source: GET /tree/descendants returns every descendant id
        // of a node (recursively), so checking a node can select its whole subtree.
        var token = await LoginAsync("admin@example.com", "Password1!");
        const string nodeDesignerId = "tv_desc_node";

        var root = new
        {
            id = "root", type = "Stack", properties = new { }, children = new object[]
            {
                new { id = "f1", type = "TextInput",
                      properties = new { fieldKey = "node_name" }, children = Array.Empty<object>() },
            },
        };
        var version = await CreateAndPublishDesignerWithFieldsAsync(token, nodeDesignerId, root);
        var menuId = await CreateMenuViaApiAsync(token, $"menu_{nodeDesignerId}", 0);
        using (var bind = await PutBindingAsync(token, menuId, nodeDesignerId, version))
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        // First /tree call self-heals the self-FK so InsertTreeNodeAsync can use it.
        using (var warm = new HttpRequestMessage(HttpMethod.Get, $"/api/data/{nodeDesignerId}/tree"))
        {
            warm.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var warmResp = await _client!.SendAsync(warm);
            Assert.Equal(HttpStatusCode.OK, warmResp.StatusCode);
        }

        var fkCol = $"parent_{nodeDesignerId}_id";
        var rootId = await InsertTreeNodeAsync(nodeDesignerId, fkCol, null, "root");
        var childId = await InsertTreeNodeAsync(nodeDesignerId, fkCol, rootId, "child");
        var grandId = await InsertTreeNodeAsync(nodeDesignerId, fkCol, childId, "grandchild");
        var child2Id = await InsertTreeNodeAsync(nodeDesignerId, fkCol, rootId, "child2");

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/data/{nodeDesignerId}/tree/descendants?parentId={rootId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = doc.RootElement.GetProperty("ids").EnumerateArray()
            .Select(e => Guid.Parse(e.GetString()!)).ToHashSet();

        Assert.Equal(3, ids.Count);
        Assert.Contains(childId, ids);
        Assert.Contains(grandId, ids);
        Assert.Contains(child2Id, ids);
        Assert.DoesNotContain(rootId, ids);   // the parent itself is excluded
    }

    private async Task<bool> ColumnExistsAsync(string tableName, string columnName)
    {
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            return await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM information_schema.columns " +
                "WHERE table_schema = 'public' AND table_name = @t AND column_name = @c)",
                new { t = tableName, c = columnName });
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    // ---------- Helpers ----------

    private async Task<HttpResponseMessage> DeleteRecordAsync(string token, string designerId, Guid id)
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

    private async Task<HttpResponseMessage> ListRecordsAsync(string token, string designerIdWithQuery)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/data/{designerIdWithQuery}");
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

    // Inserts a node into a self-referencing tree table. parentId = null seeds a root
    // node (the self-FK column is nullable); a non-null parentId links to another row.
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
