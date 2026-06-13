using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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

// Story 6.2 — end-to-end coverage for GET /api/data/{designerId}/{id}. Mirrors
// DynamicCrudIntegrationTests shape (login → provision → insert via Npgsql → GET)
// with one extra path: ?include=children for Repeater-driven child tables.
// Same [Collection] as the list-records suite so the two test classes do not
// run in parallel against overlapping container state.
[Collection("DynamicCrudTests")]
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class GetRecordIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public GetRecordIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

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
    public async Task GetRecord_ExistingRecord_Returns200WithCorrectFields()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "get_existing");
        var id = await InsertRowAsync("get_existing", "hello", isDeleted: false);

        using var response = await GetRecordAsync(token, "get_existing", id, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        // AR-46 Option C: system columns camelCase, user fieldKeys verbatim.
        Assert.Equal(id, root.GetProperty("id").GetGuid());
        Assert.False(root.GetProperty("isDeleted").GetBoolean());
        Assert.Equal("hello", root.GetProperty("title").GetString());
        Assert.False(root.TryGetProperty("is_deleted", out _),
            "is_deleted should NOT appear (mapped to isDeleted)");
    }

    // ---------- AC-2 ----------

    [Fact]
    public async Task GetRecord_RecordNotFound_Returns404WithNotFound()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "get_missing");

        using var response = await GetRecordAsync(token, "get_missing", Guid.NewGuid(), null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("NOT_FOUND", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("errors.notFound",
            doc.RootElement.GetProperty("messageKey").GetString());
    }

    // ---------- AC-3 ----------

    [Fact]
    public async Task GetRecord_SoftDeletedRecord_Returns200WithIsDeletedTrue()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "get_softdel");
        var id = await InsertRowAsync("get_softdel", "tombstone", isDeleted: true);

        using var response = await GetRecordAsync(token, "get_softdel", id, null);
        // FR-30 AC-3 — soft-deleted ≠ missing; row is returned with isDeleted: true.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("isDeleted").GetBoolean());
        Assert.Equal("tombstone", doc.RootElement.GetProperty("title").GetString());
    }

    // ---------- AC-4 ----------

    [Fact]
    public async Task GetRecord_WithChildren_ReturnsChildRows()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        const string parentDesignerId = "parent_rec_62";
        const string childDesignerId = "child_notes_62";

        // Child must be Published BEFORE the parent is bound (DdlEmitter provisions
        // children as part of parent provisioning). Child schema has one user column.
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

        // Parent schema declares a Repeater pointing at the child + one TextInput.
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

        var parentId = await InsertRowAsync(parentDesignerId, "parent-title", isDeleted: false);

        // FK column name must match DdlEmitter.BuildFkColumnName exactly.
        var fkCol = $"parent_{parentDesignerId[..Math.Min(parentDesignerId.Length, 53)]}_id";
        await InsertChildRowAsync(childDesignerId, fkCol, parentId, "child 1");
        await InsertChildRowAsync(childDesignerId, fkCol, parentId, "child 2");

        using var response = await GetRecordAsync(token, parentDesignerId, parentId, "children");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("parent-title", root.GetProperty("title").GetString());

        Assert.True(root.TryGetProperty("children", out var children));
        Assert.True(children.TryGetProperty(childDesignerId, out var childArr));
        Assert.Equal(2, childArr.GetArrayLength());

        // Child records use the same AR-46 Option C hybrid serialization:
        // system columns camelCase, user fieldKeys verbatim, raw PG names absent.
        var noteValues = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < childArr.GetArrayLength(); i++)
        {
            var child = childArr[i];
            Assert.True(child.TryGetProperty("id", out _));
            Assert.True(child.TryGetProperty("isDeleted", out _));
            Assert.False(child.TryGetProperty("is_deleted", out _),
                "is_deleted should NOT appear on child records (mapped to isDeleted per AR-46 Option C)");
            noteValues.Add(child.GetProperty("note").GetString() ?? string.Empty);
        }
        Assert.Contains("child 1", noteValues);
        Assert.Contains("child 2", noteValues);
    }

    [Fact]
    public async Task GetRecord_WithChildren_ExcludesSoftDeletedChildRows()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        const string parentDesignerId = "parent_rec_62d";
        const string childDesignerId = "child_notes_62d";

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

        var parentId = await InsertRowAsync(parentDesignerId, "parent-title", isDeleted: false);
        var fkCol = $"parent_{parentDesignerId[..Math.Min(parentDesignerId.Length, 53)]}_id";
        await InsertChildRowAsync(childDesignerId, fkCol, parentId, "live child");
        await InsertChildRowAsync(childDesignerId, fkCol, parentId, "deleted child", isDeleted: true);

        using var response = await GetRecordAsync(token, parentDesignerId, parentId, "children");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("children", out var children));
        Assert.True(children.TryGetProperty(childDesignerId, out var childArr));
        // Only the live child is returned — the soft-deleted one is filtered out.
        Assert.Equal(1, childArr.GetArrayLength());
        Assert.Equal("live child", childArr[0].GetProperty("note").GetString());
    }

    // ---------- AC-5 ----------

    // AC-5 scenario A: table WITH Repeater children, client does NOT pass ?include=children.
    [Fact]
    public async Task GetRecord_WithRepeaterChildren_WithoutInclude_OmitsChildrenKey()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        const string parentDesignerId = "ac5_parent_62";
        const string childDesignerId = "ac5_child_62";

        var childRoot = new
        {
            id = "root", type = "Stack", properties = new { },
            children = new object[]
            {
                new { id = "f1", type = "TextInput",
                      properties = new { fieldKey = "note" }, children = Array.Empty<object>() },
            },
        };
        await CreateAndPublishDesignerWithFieldsAsync(token, childDesignerId, childRoot);

        var parentRoot = new
        {
            id = "root", type = "Stack", properties = new { },
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

        var id = await InsertRowAsync(parentDesignerId, "ac5-title", isDeleted: false);

        // include param deliberately omitted — children must NOT appear even though Repeater exists.
        using var response = await GetRecordAsync(token, parentDesignerId, id, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.TryGetProperty("children", out _),
            "Response must not include a 'children' key when ?include=children is not requested, even if Repeater children exist.");
    }

    // AC-5 / Dev Notes §9: table WITHOUT any Repeater, client passes ?include=children.
    // The childSchemaMap.Count == 0 guard must suppress the children key.
    [Fact]
    public async Task GetRecord_IncludeChildren_NoRepeaters_OmitsChildrenKey()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "get_include_no_repeater");
        var id = await InsertRowAsync("get_include_no_repeater", "alone", isDeleted: false);

        // Pass ?include=children on a schema that has no Repeater children.
        using var response = await GetRecordAsync(token, "get_include_no_repeater", id, "children");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.TryGetProperty("children", out _),
            "Response must not include a 'children' key when the schema has no Repeater children.");
    }

    [Fact]
    public async Task ListOptions_ReturnsValueLabelPairs_SearchFilterAndSoftDeleteHonored()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        const string designerId = "opt_lookup";
        var root = new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = new object[]
            {
                new { id = "f1", type = "TextInput", properties = new { fieldKey = "name" }, children = Array.Empty<object>() },
                new { id = "f2", type = "TextInput", properties = new { fieldKey = "category" }, children = Array.Empty<object>() },
            },
        };
        var version = await CreateAndPublishDesignerWithFieldsAsync(token, designerId, root);

        var menuId = await CreateMenuViaApiAsync(token, $"menu_{designerId}", 0);
        using (var bind = await PutBindingAsync(token, menuId, designerId, version))
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        await InsertOptionRowAsync(designerId, "Apple", "fruit", isDeleted: false);
        await InsertOptionRowAsync(designerId, "Banana", "fruit", isDeleted: false);
        await InsertOptionRowAsync(designerId, "Carrot", "veg", isDeleted: false);
        await InsertOptionRowAsync(designerId, "Deleted", "fruit", isDeleted: true);

        // All live rows — soft-deleted "Deleted" must be excluded.
        using (var resp = await GetOptionsAsync(token, designerId, $"version={version}&labelField=name&valueField=name"))
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal(3, doc.RootElement.GetProperty("total").GetInt32());
            var labels = doc.RootElement.GetProperty("data").EnumerateArray()
                .Select(e => e.GetProperty("label").GetString())
                .ToHashSet(StringComparer.Ordinal);
            Assert.Contains("Apple", labels);
            Assert.Contains("Banana", labels);
            Assert.Contains("Carrot", labels);
            Assert.DoesNotContain("Deleted", labels);
        }

        // Search matches the label column (substring, case-insensitive).
        using (var resp = await GetOptionsAsync(token, designerId, $"version={version}&labelField=name&valueField=name&search=app"))
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt32());
            Assert.Equal("Apple", doc.RootElement.GetProperty("data")[0].GetProperty("label").GetString());
        }

        // Cascading filter narrows by a target column.
        using (var resp = await GetOptionsAsync(token, designerId, $"version={version}&labelField=name&valueField=name&filter[category]=veg"))
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt32());
            Assert.Equal("Carrot", doc.RootElement.GetProperty("data")[0].GetProperty("label").GetString());
        }
    }

    [Fact]
    public async Task ListOptions_MissingLabelOrValueField_Returns422()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "opt_missing_fields");

        using var resp = await GetOptionsAsync(token, "opt_missing_fields", "version=1&labelField=title");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    // ---------- AC-6 ----------

    [Fact]
    public async Task GetRecord_UnprovisionedDesigner_Returns404TableNotProvisioned()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var response = await GetRecordAsync(token, "never_bound_get", Guid.NewGuid(), null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("TABLE_NOT_PROVISIONED", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("errors.tableNotProvisioned",
            doc.RootElement.GetProperty("messageKey").GetString());
    }

    // ---------- AC-8 ----------

    [Fact]
    public async Task GetRecord_NoCanRead_Returns403()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "get_forbidden");
        var id = await InsertRowAsync("get_forbidden", "secret", isDeleted: false);

        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");
        using var response = await GetRecordAsync(viewerToken, "get_forbidden", id, null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // AC-8 requires code: "FORBIDDEN" in the response body.
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("FORBIDDEN", doc.RootElement.GetProperty("code").GetString());
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

    // tableName is a test-controlled SafeIdentifier value, so double-quote in-place;
    // no user input enters the SQL string.
#pragma warning disable CA2100
    private async Task<Guid> InsertRowAsync(string tableName, string title, bool isDeleted)
    {
        var id = Guid.NewGuid();
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                $"INSERT INTO \"{tableName}\" (id, created_at, updated_at, is_deleted, title) " +
                "VALUES (@id, NOW(), NOW(), @isDeleted, @title)",
                new { id, isDeleted, title });
        }
        finally
        {
            await conn.DisposeAsync();
        }
        return id;
    }

    private async Task InsertOptionRowAsync(
        string tableName, string name, string category, bool isDeleted)
    {
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                $"INSERT INTO \"{tableName}\" (id, created_at, updated_at, is_deleted, name, category) " +
                "VALUES (@id, NOW(), NOW(), @isDeleted, @name, @category)",
                new { id = Guid.NewGuid(), isDeleted, name, category });
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    private async Task InsertChildRowAsync(
        string childTableName, string fkColumn, Guid parentId, string note, bool isDeleted = false)
    {
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                string.Create(CultureInfo.InvariantCulture,
                    $"INSERT INTO \"{childTableName}\" (id, created_at, updated_at, is_deleted, note, \"{fkColumn}\") " +
                    $"VALUES (@id, NOW(), NOW(), @isDeleted, @note, @parentId)"),
                new { id = Guid.NewGuid(), note, parentId, isDeleted });
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }
#pragma warning restore CA2100

    private async Task<HttpResponseMessage> GetRecordAsync(
        string token, string designerId, Guid id, string? include)
    {
        var qs = include is null ? string.Empty : $"?include={include}";
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/data/{designerId}/{id}{qs}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private async Task<HttpResponseMessage> GetOptionsAsync(string token, string designerId, string query)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/data/{designerId}/options?{query}");
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
