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

// Story 6.7 — end-to-end coverage for nested-write POST and PUT
// /api/data/{parentDesignerId}[/{id}] with optional `children` payload. Shares
// the [Collection("DynamicCrudTests")] grouping with the list / get / create /
// update / softDelete / restore suites so the seven classes serialize against
// the same PostgreSQL container.
[Collection("DynamicCrudTests")]
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class RepeaterWriteIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public RepeaterWriteIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

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

    // ---------- AC-1, AC-16 ----------

    [Fact]
    public async Task Post_WithChildren_Returns201AndInsertsChildAtomically()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        const string parentDesignerId = "rw_post_parent";
        const string childDesignerId  = "rw_post_child";
        await SetupParentChildDesignersAsync(token, parentDesignerId, childDesignerId);

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["title"] = "parent record",
            ["children"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [childDesignerId] = new[]
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["note"] = "child1" },
                },
            },
        };

        using var response = await PostRecordAsync(token, parentDesignerId, payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var parentId = root.GetProperty("id").GetGuid();
        Assert.True(root.TryGetProperty("createdAt", out _));
        Assert.True(root.TryGetProperty("updatedAt", out _));
        // AR-46 Option C — system columns camelCase, snake_case absent.
        Assert.False(root.TryGetProperty("is_deleted", out _));

        // Direct Npgsql SELECT — child row exists with FK and is_deleted=false.
        var fkCol = $"parent_{parentDesignerId[..Math.Min(parentDesignerId.Length, 53)]}_id";
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            var childRow = await conn.QuerySingleOrDefaultAsync<(string note, Guid fk, bool is_deleted)>(
                $"SELECT note, \"{fkCol}\" AS fk, is_deleted FROM \"{childDesignerId}\" WHERE \"{fkCol}\" = @parentId",
                new { parentId });
            Assert.Equal("child1", childRow.note);
            Assert.Equal(parentId, childRow.fk);
            Assert.False(childRow.is_deleted);
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    // ---------- AC-2 ----------

    [Fact]
    public async Task Post_ChildrenAbsent_Returns201_BackwardCompatible()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        const string parentDesignerId = "rw_post_nokids";
        const string childDesignerId  = "rw_post_nokids_child";
        await SetupParentChildDesignersAsync(token, parentDesignerId, childDesignerId);

        // POST without `children` key — single-INSERT path, no transaction.
        using var response = await PostRecordAsync(token, parentDesignerId, new { title = "no kids" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var parentId = doc.RootElement.GetProperty("id").GetGuid();

        var fkCol = $"parent_{parentDesignerId[..Math.Min(parentDesignerId.Length, 53)]}_id";
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            var childCount = await conn.ExecuteScalarAsync<long>(
                $"SELECT COUNT(*) FROM \"{childDesignerId}\" WHERE \"{fkCol}\" = @parentId",
                new { parentId });
            Assert.Equal(0, childCount);
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    // ---------- AC-3 ----------

    [Fact]
    public async Task Post_MultipleChildren_AllInsertedWithParentFk()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        const string parentDesignerId = "rw_post_multi";
        const string childDesignerId  = "rw_post_multi_child";
        await SetupParentChildDesignersAsync(token, parentDesignerId, childDesignerId);

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["title"] = "multi",
            ["children"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [childDesignerId] = new[]
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["note"] = "c1" },
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["note"] = "c2" },
                },
            },
        };

        using var response = await PostRecordAsync(token, parentDesignerId, payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var parentId = doc.RootElement.GetProperty("id").GetGuid();

        var fkCol = $"parent_{parentDesignerId[..Math.Min(parentDesignerId.Length, 53)]}_id";
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            var notes = (await conn.QueryAsync<string>(
                $"SELECT note FROM \"{childDesignerId}\" WHERE \"{fkCol}\" = @parentId ORDER BY created_at",
                new { parentId })).ToList();
            Assert.Equal(2, notes.Count);
            Assert.Contains("c1", notes);
            Assert.Contains("c2", notes);
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    // ---------- AC-4, AC-7 ----------

    [Fact]
    public async Task Put_WithChildren_UpdateExistingChild_UpdatesAtomically()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        const string parentDesignerId = "rw_put_upd_p";
        const string childDesignerId  = "rw_put_upd_c";
        await SetupParentChildDesignersAsync(token, parentDesignerId, childDesignerId);

        // Create parent + 1 child via POST with children.
        var createPayload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["title"] = "initial",
            ["children"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [childDesignerId] = new[]
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["note"] = "initial note" },
                },
            },
        };
        using var createResp = await PostRecordAsync(token, parentDesignerId, createPayload);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        using var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var parentId = createDoc.RootElement.GetProperty("id").GetGuid();

        // Look up the child id via direct SELECT.
        var fkCol = $"parent_{parentDesignerId[..Math.Min(parentDesignerId.Length, 53)]}_id";
        Guid childId;
        var lookupConn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await lookupConn.OpenAsync();
            childId = await lookupConn.QuerySingleAsync<Guid>(
                $"SELECT id FROM \"{childDesignerId}\" WHERE \"{fkCol}\" = @parentId",
                new { parentId });
        }
        finally
        {
            await lookupConn.DisposeAsync();
        }

        // Now PUT to update parent title + the existing child's note.
        var putPayload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["title"] = "updated",
            ["children"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [childDesignerId] = new[]
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["id"]   = childId.ToString(),
                        ["note"] = "updated note",
                    },
                },
            },
        };

        using var putResp = await PutRecordAsync(token, parentDesignerId, parentId, putPayload);
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        var verifyConn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await verifyConn.OpenAsync();
            var childRow = await verifyConn.QuerySingleAsync<(string note, bool is_deleted)>(
                $"SELECT note, is_deleted FROM \"{childDesignerId}\" WHERE id = @childId",
                new { childId });
            Assert.Equal("updated note", childRow.note);
            Assert.False(childRow.is_deleted);
        }
        finally
        {
            await verifyConn.DisposeAsync();
        }
    }

    // ---------- Regression: child fieldKey collides with the reserved parent FK ----------
    //
    // A child row-form field whose fieldKey equals the auto-provisioned parent FK
    // column (parent_<parentDesignerId>_id) used to throw PG 42804 "column ... is of
    // type uuid but expression is of type text" on PUT: the validator coerced the
    // colliding field as TEXT and BuildUpdateQuery bound it into the SET clause
    // against the UUID FK column. The FK is system-owned (always = parentId), so the
    // coordinator now strips it from each child's coerced values. The UUID column only
    // arises via schema evolution — the FK is provisioned before the colliding field
    // is added to a later child version — so the setup mirrors that history.
    [Fact]
    public async Task Put_WithChildren_ChildFieldKeyCollidesWithParentFk_StripsFkAndSucceeds()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        const string parentDesignerId = "rw_fkcollide_p";
        const string childDesignerId  = "rw_fkcollide_c";
        var fkCol = $"parent_{parentDesignerId[..Math.Min(parentDesignerId.Length, 53)]}_id";

        // Phase 1 — provision with the child carrying ONLY `note`. The parent's
        // Repeater provisioning adds `fkCol` to the child table as UUID.
        await SetupParentChildDesignersAsync(token, parentDesignerId, childDesignerId);

        // Phase 2 — publish a NEW child version that adds a field whose fieldKey
        // collides with the existing UUID FK column. ADD COLUMN IF NOT EXISTS is a
        // no-op, so the DB column stays UUID while the schema now treats it as a TEXT
        // user column. The write path loads this latest published child version.
        var collidingChildRoot = new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = new object[]
            {
                new { id = "f1", type = "TextInput",
                      properties = new { fieldKey = "note" }, children = Array.Empty<object>() },
                new { id = "f2", type = "TextInput",
                      properties = new { fieldKey = fkCol }, children = Array.Empty<object>() },
            },
        };
        await PublishAdditionalVersionAsync(token, childDesignerId, collidingChildRoot);

        // Create parent + 1 child.
        var createPayload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["title"] = "initial",
            ["children"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [childDesignerId] = new[]
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["note"] = "initial note" },
                },
            },
        };
        using var createResp = await PostRecordAsync(token, parentDesignerId, createPayload);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        using var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var parentId = createDoc.RootElement.GetProperty("id").GetGuid();

        Guid childId;
        var lookupConn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await lookupConn.OpenAsync();
            childId = await lookupConn.QuerySingleAsync<Guid>(
                $"SELECT id FROM \"{childDesignerId}\" WHERE \"{fkCol}\" = @parentId",
                new { parentId });
        }
        finally
        {
            await lookupConn.DisposeAsync();
        }

        // PUT round-trips the FK value back as a string (as the SPA does after loading
        // the child via ?include=children). Pre-fix this threw 42804 → 500.
        var putPayload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["title"] = "updated",
            ["children"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [childDesignerId] = new[]
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["id"]   = childId.ToString(),
                        ["note"] = "updated note",
                        [fkCol]  = parentId.ToString(),
                    },
                },
            },
        };

        using var putResp = await PutRecordAsync(token, parentDesignerId, parentId, putPayload);
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        // Note updated; the system-owned FK is untouched (still the original parent).
        var verifyConn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await verifyConn.OpenAsync();
            var row = await verifyConn.QuerySingleAsync<(string note, Guid fk, bool is_deleted)>(
                $"SELECT note, \"{fkCol}\" AS fk, is_deleted FROM \"{childDesignerId}\" WHERE id = @childId",
                new { childId });
            Assert.Equal("updated note", row.note);
            Assert.Equal(parentId, row.fk);
            Assert.False(row.is_deleted);
        }
        finally
        {
            await verifyConn.DisposeAsync();
        }
    }

    // ---------- Regression: PARENT-path fieldKey targets the parent FK column ----------
    //
    // A designer that is itself a Repeater row (so DdlEmitter provisioned a
    // parent_<parentDesignerId>_id UUID FK on its table) may ALSO be edited standalone
    // via its own data page. If its form declares a field whose fieldKey equals that FK
    // column (e.g. a designer-backed dropdown that picks the parent record), POST/PUT
    // /api/data/{child} used to throw PG 42804 "column ... is of type uuid but
    // expression is of type text": the parent handlers (CreateRecordHandler /
    // UpdateRecordHandler) bound the TEXT-coerced dropdown value into the UUID column.
    // The handlers now re-coerce the value to a Guid so the parent link PERSISTS as a
    // real UUID (the value references a live parent row — the FK constraint enforces it).
    [Fact]
    public async Task Post_StandaloneChild_FieldKeyTargetsParentFk_PersistsFkAsUuid()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        const string parentDesignerId = "rw_pfk_p";
        const string childDesignerId  = "rw_pfk_c";
        var fkCol = $"parent_{parentDesignerId[..Math.Min(parentDesignerId.Length, 53)]}_id";

        // Provision the child as a Repeater row of the parent → the child table gains
        // the parent_<parent>_id UUID FK column. SetupParentChildDesignersAsync also
        // binds the parent to its own menu, so we can create parent rows standalone.
        await SetupParentChildDesignersAsync(token, parentDesignerId, childDesignerId);

        // Publish a NEW child version that adds a field whose fieldKey equals the UUID FK
        // column (ADD COLUMN IF NOT EXISTS is a no-op → the column stays UUID while the
        // schema now treats it as a user field), then bind the child to its OWN menu so
        // it can be created/edited standalone.
        var collidingChildRoot = new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = new object[]
            {
                new { id = "f1", type = "TextInput",
                      properties = new { fieldKey = "note" }, children = Array.Empty<object>() },
                new { id = "f2", type = "TextInput",
                      properties = new { fieldKey = fkCol }, children = Array.Empty<object>() },
            },
        };
        var childVersion = await PublishAdditionalVersionAsync(token, childDesignerId, collidingChildRoot);

        var childMenuId = await CreateMenuViaApiAsync(token, $"menu_{childDesignerId}", 1);
        using (var bind = await PutBindingAsync(token, childMenuId, childDesignerId, childVersion))
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        var childStatus = await PollUntilTerminalAsync(token, childMenuId, TimeSpan.FromSeconds(10));
        Assert.Equal("Success", childStatus);

        // Create two real parent rows to link to (the FK REFERENCES the parent table).
        var villageA = await CreateParentAndGetIdAsync(token, parentDesignerId, "Village A");
        var villageB = await CreateParentAndGetIdAsync(token, parentDesignerId, "Village B");

        // POST the child standalone with the FK field set to a real parent id (as a
        // designer-backed dropdown would submit). Pre-fix this threw 42804 → 500.
        var createPayload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["note"] = "standalone house",
            [fkCol]  = villageA.ToString(),
        };
        using var createResp = await PostRecordAsync(token, childDesignerId, createPayload);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        using var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var childId = createDoc.RootElement.GetProperty("id").GetGuid();
        // Response echoes the persisted FK as a real UUID value.
        Assert.Equal(villageA, createDoc.RootElement.GetProperty(fkCol).GetGuid());

        await AssertChildFkAsync(childDesignerId, fkCol, childId, "standalone house", villageA);

        // PUT re-points the parent link to a different village — must persist, not 42804.
        var putPayload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["note"] = "renamed house",
            [fkCol]  = villageB.ToString(),
        };
        using var putResp = await PutRecordAsync(token, childDesignerId, childId, putPayload);
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        await AssertChildFkAsync(childDesignerId, fkCol, childId, "renamed house", villageB);

        // A malformed (non-UUID) FK value is a 422, not a 500.
        var badPayload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["note"] = "house",
            [fkCol]  = "not-a-uuid",
        };
        using var badResp = await PutRecordAsync(token, childDesignerId, childId, badPayload);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badResp.StatusCode);
    }

    private async Task<Guid> CreateParentAndGetIdAsync(string token, string parentDesignerId, string title)
    {
        using var resp = await PostRecordAsync(token, parentDesignerId, new { title });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private async Task AssertChildFkAsync(
        string childDesignerId, string fkCol, Guid childId, string expectedNote, Guid expectedFk)
    {
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            var row = await conn.QuerySingleAsync<(string note, Guid fk)>(
                $"SELECT note, \"{fkCol}\" AS fk FROM \"{childDesignerId}\" WHERE id = @childId",
                new { childId });
            Assert.Equal(expectedNote, row.note);
            Assert.Equal(expectedFk, row.fk);
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    // ---------- AC-5 ----------

    [Fact]
    public async Task Put_WithChildren_InsertNewChild_InsertsAtomically()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        const string parentDesignerId = "rw_put_ins_p";
        const string childDesignerId  = "rw_put_ins_c";
        await SetupParentChildDesignersAsync(token, parentDesignerId, childDesignerId);

        // Create parent with no children.
        using var createResp = await PostRecordAsync(token, parentDesignerId, new { title = "parent" });
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        using var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var parentId = createDoc.RootElement.GetProperty("id").GetGuid();

        // PUT with a new child (no id).
        var putPayload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["children"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [childDesignerId] = new[]
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["note"] = "brand new" },
                },
            },
        };
        using var putResp = await PutRecordAsync(token, parentDesignerId, parentId, putPayload);
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        var fkCol = $"parent_{parentDesignerId[..Math.Min(parentDesignerId.Length, 53)]}_id";
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            var note = await conn.QuerySingleAsync<string>(
                $"SELECT note FROM \"{childDesignerId}\" WHERE \"{fkCol}\" = @parentId",
                new { parentId });
            Assert.Equal("brand new", note);
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    // ---------- AC-6 ----------

    [Fact]
    public async Task Put_WithChildren_OmittedChild_SoftDeleted()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        const string parentDesignerId = "rw_put_omit_p";
        const string childDesignerId  = "rw_put_omit_c";
        await SetupParentChildDesignersAsync(token, parentDesignerId, childDesignerId);

        // Create parent + 2 children directly via DB.
        var parentId = await InsertParentRowAsync(parentDesignerId, "parent");
        var fkCol = $"parent_{parentDesignerId[..Math.Min(parentDesignerId.Length, 53)]}_id";
        var childId1 = await InsertChildRowAsync(childDesignerId, fkCol, parentId, "child 1");
        var childId2 = await InsertChildRowAsync(childDesignerId, fkCol, parentId, "child 2");

        // PUT with only childId1 in the payload — childId2 must be soft-deleted.
        var putPayload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["children"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [childDesignerId] = new[]
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["id"]   = childId1.ToString(),
                        ["note"] = "child 1 kept",
                    },
                },
            },
        };
        using var putResp = await PutRecordAsync(token, parentDesignerId, parentId, putPayload);
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            var c1 = await conn.QuerySingleAsync<(bool is_deleted, Guid? cascade_event_id)>(
                $"SELECT is_deleted, cascade_event_id FROM \"{childDesignerId}\" WHERE id = @id",
                new { id = childId1 });
            Assert.False(c1.is_deleted);

            var c2 = await conn.QuerySingleAsync<(bool is_deleted, Guid? cascade_event_id)>(
                $"SELECT is_deleted, cascade_event_id FROM \"{childDesignerId}\" WHERE id = @id",
                new { id = childId2 });
            Assert.True(c2.is_deleted);
            // AC-6: cascade_event_id MUST be NULL (individual delete, not cascade).
            Assert.Null(c2.cascade_event_id);
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    // ---------- AC-13 ----------

    [Fact]
    public async Task Put_WithChildren_AuditLog_AllOperationsRecorded()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        const string parentDesignerId = "rw_audit_p";
        const string childDesignerId  = "rw_audit_c";
        await SetupParentChildDesignersAsync(token, parentDesignerId, childDesignerId);

        var actorId = GetUserIdFromToken(token);

        // Create parent + 2 children via DB (so we can omit one and update another).
        var parentId = await InsertParentRowAsync(parentDesignerId, "parent");
        var fkCol = $"parent_{parentDesignerId[..Math.Min(parentDesignerId.Length, 53)]}_id";
        var childId1 = await InsertChildRowAsync(childDesignerId, fkCol, parentId, "to update");
        var childId2 = await InsertChildRowAsync(childDesignerId, fkCol, parentId, "to delete");

        // PUT: update childId1, omit childId2 (→ SOFT_DELETE), insert a new one.
        var putPayload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["title"] = "audit parent",
            ["children"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [childDesignerId] = new[]
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["id"]   = childId1.ToString(),
                        ["note"] = "updated",
                    },
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["note"] = "newly inserted",
                    },
                },
            },
        };
        using var putResp = await PutRecordAsync(token, parentDesignerId, parentId, putPayload);
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            // Audit rows: 1 UPDATE (parent), 1 UPDATE (childId1), 1 SOFT_DELETE (childId2),
            // 1 CREATE (new child). Exactly 4 rows total for this PUT.
            // Table is TRUNCATEd in InitializeAsync so all rows belong to this test.
            var rows = (await conn.QueryAsync<(Guid record_id, string designer_id, string operation, Guid? actor_id, string? correlation_id)>(
                "SELECT record_id, designer_id, operation, actor_id, correlation_id FROM mutation_audit_log WHERE designer_id IN (@p, @c)",
                new { p = parentDesignerId, c = childDesignerId })).ToList();

            Assert.Equal(4, rows.Count);

            // Parent UPDATE row.
            Assert.Contains(rows, r => r.record_id == parentId && r.operation == "UPDATE");
            // Child UPDATE row.
            Assert.Contains(rows, r => r.record_id == childId1 && r.operation == "UPDATE");
            // Child SOFT_DELETE row.
            Assert.Contains(rows, r => r.record_id == childId2 && r.operation == "SOFT_DELETE");
            // Child CREATE row (record_id is the new child id — we don't know it, but the count guards the total).
            Assert.Contains(rows, r => r.designer_id == childDesignerId && r.operation == "CREATE");

            // All rows share the same actor_id and a non-empty correlation_id.
            foreach (var r in rows)
            {
                Assert.Equal(actorId, r.actor_id);
                Assert.False(string.IsNullOrEmpty(r.correlation_id));
            }
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    // ---------- AC-8 ----------

    [Fact]
    public async Task Put_WithoutChildren_BackwardCompatible_UpdatesParentOnly()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        const string parentDesignerId = "rw_ac8_p";
        const string childDesignerId  = "rw_ac8_c";
        await SetupParentChildDesignersAsync(token, parentDesignerId, childDesignerId);

        // Create parent record with no children.
        using var createResp = await PostRecordAsync(token, parentDesignerId, new { title = "original" });
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        using var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var parentId = createDoc.RootElement.GetProperty("id").GetGuid();

        // PUT without a `children` key — must hit the no-transaction path (AC-8).
        using var putResp = await PutRecordAsync(token, parentDesignerId, parentId, new { title = "updated" });
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        // Verify parent was updated and no child rows were created.
        using var putDoc = JsonDocument.Parse(await putResp.Content.ReadAsStringAsync());
        Assert.Equal("updated", putDoc.RootElement.GetProperty("title").GetString());

        var fkCol = $"parent_{parentDesignerId[..Math.Min(parentDesignerId.Length, 53)]}_id";
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            var childCount = await conn.ExecuteScalarAsync<long>(
                $"SELECT COUNT(*) FROM \"{childDesignerId}\" WHERE \"{fkCol}\" = @parentId",
                new { parentId });
            Assert.Equal(0, childCount);
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    // ---------- AC-9 ----------

    [Fact]
    public async Task Post_ChildValidationFails_Returns422_NothingPersisted()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        const string parentDesignerId = "rw_val_p";
        const string childDesignerId  = "rw_val_c";
        // Child schema uses a NUMERIC field so we can submit an invalid value.
        await SetupParentChildDesignersWithNumericChildAsync(token, parentDesignerId, childDesignerId);

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["title"] = "should not persist",
            ["children"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [childDesignerId] = new[]
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["count"] = "not-a-number" },
                },
            },
        };

        using var response = await PostRecordAsync(token, parentDesignerId, payload);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", doc.RootElement.GetProperty("code").GetString());

        // Verify nothing was persisted in either table.
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            var parentCount = await conn.ExecuteScalarAsync<long>(
                $"SELECT COUNT(*) FROM \"{parentDesignerId}\"");
            Assert.Equal(0, parentCount);
            var childCount = await conn.ExecuteScalarAsync<long>(
                $"SELECT COUNT(*) FROM \"{childDesignerId}\"");
            Assert.Equal(0, childCount);
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    // ---------- AC-10 ----------

    [Fact]
    public async Task Post_UnknownChildDesigner_Returns422ValidationFailed()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        const string parentDesignerId = "rw_ac10_p";
        const string childDesignerId  = "rw_ac10_c";
        await SetupParentChildDesignersAsync(token, parentDesignerId, childDesignerId);

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["title"] = "x",
            ["children"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["not_a_repeater_child"] = new[]
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["note"] = "x" },
                },
            },
        };

        using var response = await PostRecordAsync(token, parentDesignerId, payload);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", doc.RootElement.GetProperty("code").GetString());
    }

    // ---------- AC-12 ----------

    [Fact]
    public async Task Put_UnknownChildId_Returns404ChildNotFound()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        const string parentDesignerId = "rw_ac12_p";
        const string childDesignerId  = "rw_ac12_c";
        await SetupParentChildDesignersAsync(token, parentDesignerId, childDesignerId);
        var parentId = await InsertParentRowAsync(parentDesignerId, "parent");

        var putPayload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["children"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [childDesignerId] = new[]
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["id"]   = Guid.NewGuid().ToString(),
                        ["note"] = "ghost",
                    },
                },
            },
        };
        using var putResp = await PutRecordAsync(token, parentDesignerId, parentId, putPayload);
        Assert.Equal(HttpStatusCode.NotFound, putResp.StatusCode);
        using var doc = JsonDocument.Parse(await putResp.Content.ReadAsStringAsync());
        Assert.Equal("CHILD_NOT_FOUND", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("errors.childNotFound", doc.RootElement.GetProperty("messageKey").GetString());
    }

    // ---------- Helpers ----------

    private async Task<HttpResponseMessage> PostRecordAsync(string token, string designerId, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/data/{designerId}")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PutRecordAsync(
        string token, string designerId, Guid id, object payload)
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

    private async Task SetupParentChildDesignersAsync(
        string token, string parentDesignerId, string childDesignerId)
    {
        // Child schema: TextInput note.
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

        // Parent schema: TextInput title + Repeater rowDesignerId=childDesignerId.
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
        var status = await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10));
        Assert.Equal("Success", status);
    }

    private async Task SetupParentChildDesignersWithNumericChildAsync(
        string token, string parentDesignerId, string childDesignerId)
    {
        // Child schema: NumberInput count (NUMERIC).
        var childRoot = new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = new object[]
            {
                new { id = "f1", type = "NumberInput",
                      properties = new { fieldKey = "count" }, children = Array.Empty<object>() },
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
        var status = await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10));
        Assert.Equal("Success", status);
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

    // POSTs a new version to an EXISTING designer and publishes it. Multiple
    // published versions are allowed; the write path resolves the latest one.
    private async Task<int> PublishAdditionalVersionAsync<TRoot>(
        string token, string designerId, TRoot rootElement)
    {
        ArgumentNullException.ThrowIfNull(rootElement);

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

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var version = doc.RootElement.GetProperty("latestVersion").GetInt32();
        await PutVersionStatusAsync(token, designerId, version, "Published");
        return version;
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
