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

// Story 6.1 — end-to-end coverage for GET /api/data/{designerId}. Mirrors the
// ProvisioningIntegrationTests shape so the helpers (login, create+publish
// designer, bind, poll-until-success) read identically. Direct Npgsql is used
// for row inserts because Story 6.1 does not own a POST handler — those land
// in Story 6.3.
[Collection("DynamicCrudTests")]
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class DynamicCrudIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public DynamicCrudIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

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
    public async Task ListRecords_ProvisionedTable_EmptyData_Returns200WithPagedResultEnvelope()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "list_empty");

        using var response = await GetRecordsAsync(token, "list_empty", "?page=1&pageSize=25");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(0, root.GetProperty("total").GetInt64());
        Assert.Equal(1, root.GetProperty("page").GetInt32());
        Assert.Equal(25, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(0, root.GetProperty("totalPages").GetInt32());
        Assert.Equal(0, root.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task ListRecords_JsonResponse_SystemColumnsAreCamelCase_UserColumnsAreVerbatim()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "json_shape");
        await InsertRowAsync("json_shape", "hello", isDeleted: false);

        using var response = await GetRecordsAsync(token, "json_shape", "?page=1&pageSize=25");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var first = doc.RootElement.GetProperty("data")[0];
        // Option C hybrid (AR-46): system columns renamed to camelCase, user fieldKeys verbatim.
        Assert.True(first.TryGetProperty("id", out _));
        Assert.True(first.TryGetProperty("createdAt", out _));
        Assert.True(first.TryGetProperty("isDeleted", out _));
        Assert.True(first.TryGetProperty("title", out var titleProp));
        Assert.Equal("hello", titleProp.GetString());
        Assert.False(first.TryGetProperty("is_deleted", out _),
            "is_deleted should NOT appear (mapped to isDeleted)");
    }

    // ---------- AC-2 ----------

    [Fact]
    public async Task ListRecords_SortByUserColumnDesc_ReturnsCorrectOrder()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "list_sort");
        await InsertRowAsync("list_sort", "alpha", isDeleted: false);
        await InsertRowAsync("list_sort", "bravo", isDeleted: false);

        using var response = await GetRecordsAsync(token, "list_sort", "?sort=title:desc");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(2, data.GetArrayLength());
        Assert.Equal("bravo", data[0].GetProperty("title").GetString());
        Assert.Equal("alpha", data[1].GetProperty("title").GetString());
    }

    [Fact]
    public async Task ListRecords_UnknownSortColumn_Returns422WithValidationFailed()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "list_bad_sort");

        using var response = await GetRecordsAsync(token, "list_bad_sort", "?sort=nope:asc");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("VALIDATION_FAILED", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("errors.validationFailed",
            doc.RootElement.GetProperty("messageKey").GetString());
    }

    // ---------- AC-3 ----------

    [Fact]
    public async Task ListRecords_FilterByUserColumn_ReturnsOnlyMatching()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "list_filter");
        await InsertRowAsync("list_filter", "foo", isDeleted: false);
        await InsertRowAsync("list_filter", "bar", isDeleted: false);

        using var response = await GetRecordsAsync(token, "list_filter", "?filter[title]=foo");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt64());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal("foo", data[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task ListRecords_FilterByIsDeletedSystemKey_ReturnsOnlyLiveRecords()
    {
        // AC-3 + AC-5: filter[isDeleted]=false maps to is_deleted PG column.
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "list_softdel_filter");
        await InsertRowAsync("list_softdel_filter", "alive", isDeleted: false);
        await InsertRowAsync("list_softdel_filter", "tombstone", isDeleted: true);

        using var response = await GetRecordsAsync(
            token, "list_softdel_filter", "?filter[isDeleted]=false");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt64());
        Assert.Equal("alive",
            doc.RootElement.GetProperty("data")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task ListRecords_UnknownFilterKey_Returns422()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "list_bad_filter");

        using var response = await GetRecordsAsync(token, "list_bad_filter", "?filter[nope]=x");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("VALIDATION_FAILED", doc.RootElement.GetProperty("code").GetString());
    }

    // ---------- Story 7-followup: Export ----------

    [Theory]
    [InlineData("csv", "text/csv", "csv")]
    [InlineData("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx")]
    [InlineData("pdf", "application/pdf", "pdf")]
    public async Task ExportRecords_Format_ReturnsCorrectContentTypeAndDispositionAndBytes(
        string format, string expectedContentType, string expectedExt)
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "export_basic");
        await InsertRowAsync("export_basic", "alpha", isDeleted: false);
        await InsertRowAsync("export_basic", "bravo", isDeleted: false);

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/data/export_basic/export?format={format}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(response.Content.Headers.ContentDisposition);
        var fileName = response.Content.Headers.ContentDisposition!.FileName ?? response.Content.Headers.ContentDisposition.FileNameStar;
        Assert.NotNull(fileName);
        Assert.EndsWith($".{expectedExt}", fileName!.Trim('"'), StringComparison.Ordinal);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 50);
    }

    [Fact]
    public async Task ExportRecords_HonoursFilter_OnlyMatchingRowsAppearInCsv()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "export_filter");
        await InsertRowAsync("export_filter", "alpha", isDeleted: false);
        await InsertRowAsync("export_filter", "bravo", isDeleted: false);

        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/data/export_filter/export?format=csv&filter[title]=alp");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("alpha", text, StringComparison.Ordinal);
        Assert.DoesNotContain("bravo", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportRecords_MissingFormat_Returns422()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "export_noformat");

        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/data/export_noformat/export");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ExportRecords_UnknownFormat_Returns422()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "export_badformat");

        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/data/export_badformat/export?format=docx");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ExportRecords_NonAdminWithReadButNoExport_Returns403()
    {
        // Story 7-followup — the export endpoint now requires canExport, not
        // canRead. A non-admin who can read but lacks canExport is denied.
        var adminToken = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(adminToken, "export_perm_denied");
        await GrantViewerReadAsync("export_perm_denied", canExport: false);

        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/data/export_perm_denied/export?format=csv");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ExportRecords_NonAdminWithExport_Returns200()
    {
        var adminToken = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(adminToken, "export_perm_granted");
        await InsertRowAsync("export_perm_granted", "alpha", isDeleted: false);
        await GrantViewerReadAsync("export_perm_granted", canExport: true);

        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/data/export_perm_granted/export?format=csv");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("alpha", text, StringComparison.Ordinal);
    }

    // ---------- AC-4 ----------

    [Fact]
    public async Task ListRecords_UnprovisionedDesigner_Returns404WithTableNotProvisioned()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var response = await GetRecordsAsync(token, "never_bound", "?page=1&pageSize=25");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("TABLE_NOT_PROVISIONED", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("errors.tableNotProvisioned",
            doc.RootElement.GetProperty("messageKey").GetString());
    }

    // ---------- AC-5 ----------

    [Fact]
    public async Task ListRecords_DefaultExcludesSoftDeletedRecords()
    {
        // Story 7-followup — soft-deleted rows used to leak into the default
        // response; they now require an explicit opt-in via includeDeleted=true
        // (or filter[isDeleted]=true). The 'Show deleted' toggle in the UI is
        // what flips includeDeleted.
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "list_softdel");
        await InsertRowAsync("list_softdel", "alive", isDeleted: false);
        await InsertRowAsync("list_softdel", "tombstone", isDeleted: true);

        using var response = await GetRecordsAsync(token, "list_softdel", "?page=1&pageSize=25");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt64());
        Assert.Equal(1, doc.RootElement.GetProperty("data").GetArrayLength());
        Assert.Equal("alive",
            doc.RootElement.GetProperty("data")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task ListRecords_AdminIncludeDeletedTrue_AlsoReturnsSoftDeletedRecords()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "list_softdel_inc");
        await InsertRowAsync("list_softdel_inc", "alive", isDeleted: false);
        await InsertRowAsync("list_softdel_inc", "tombstone", isDeleted: true);

        using var response = await GetRecordsAsync(
            token, "list_softdel_inc", "?page=1&pageSize=25&includeDeleted=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt64());
        Assert.Equal(2, doc.RootElement.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task ListRecords_NonAdminWithRead_IncludeDeletedTrue_StillExcludesDeleted()
    {
        // Server-side enforcement: includeDeleted is platform-admin-only. A
        // non-admin who holds canRead and hand-crafts includeDeleted=true must
        // still receive live-only rows — the handler forces is_deleted=false.
        var adminToken = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(adminToken, "list_nonadmin_del");
        await InsertRowAsync("list_nonadmin_del", "alive", isDeleted: false);
        await InsertRowAsync("list_nonadmin_del", "tombstone", isDeleted: true);
        await GrantViewerReadAsync("list_nonadmin_del");

        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");
        using var response = await GetRecordsAsync(
            viewerToken, "list_nonadmin_del", "?page=1&pageSize=25&includeDeleted=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt64());
        Assert.Equal("alive",
            doc.RootElement.GetProperty("data")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task ExportRecords_NonAdminWithExport_IncludeDeletedTrue_StillExcludesDeletedFromCsv()
    {
        // The non-admin needs canExport to reach the endpoint at all (Story
        // 7-followup gate); the assertion then proves includeDeleted is still
        // ignored for them — they get live-only rows.
        var adminToken = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(adminToken, "export_nonadmin_del");
        await InsertRowAsync("export_nonadmin_del", "alive", isDeleted: false);
        await InsertRowAsync("export_nonadmin_del", "tombstone", isDeleted: true);
        await GrantViewerReadAsync("export_nonadmin_del", canExport: true);

        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/data/export_nonadmin_del/export?format=csv&includeDeleted=true");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("alive", text, StringComparison.Ordinal);
        Assert.DoesNotContain("tombstone", text, StringComparison.Ordinal);
    }

    // ---------- AC-7 ----------

    [Fact]
    public async Task ListRecords_AsViewerWithoutCanRead_Returns403()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await SetupProvisionedDesignerWithTitleAsync(token, "list_forbidden");

        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");
        using var response = await GetRecordsAsync(viewerToken, "list_forbidden", "?page=1&pageSize=25");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- Helpers ----------

    // Provisions a dynamic table with one user column "title" (TEXT). Returns the
    // bound version so callers that need to validate the version can use it.
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

    // Inserts one row using the same Dapper-on-NpgsqlConnection pattern the
    // production handler uses. The table name is a test-controlled SafeIdentifier
    // value, so we double-quote in-place; no user input enters the SQL string.
#pragma warning disable CA2100 // SQL injection — tableName is a hardcoded test value
    private async Task InsertRowAsync(string tableName, string title, bool isDeleted)
    {
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                $"INSERT INTO \"{tableName}\" (id, created_at, updated_at, is_deleted, title) " +
                "VALUES (@id, NOW(), NOW(), @isDeleted, @title)",
                new { id = Guid.NewGuid(), isDeleted, title });
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }
#pragma warning restore CA2100

    private async Task<HttpResponseMessage> GetRecordsAsync(string token, string designerId, string queryString)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/data/{designerId}{queryString}");
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

    // Grants the seeded (non-admin) viewer role canRead (and optionally canExport)
    // on one designerId by inserting a role_permissions row directly. Used to
    // prove (a) a non-admin with read still can't see soft-deleted rows and
    // (b) export requires the separate canExport flag.
    private async Task GrantViewerReadAsync(string designerId, bool canExport = false)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        db.RolePermissions.Add(new RolePermission
        {
            Id = Guid.NewGuid(),
            RoleId = ViewerRoleId,
            ResourceId = designerId,
            CanRead = true,
            CanExport = canExport,
        });
        await db.SaveChangesAsync();
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
