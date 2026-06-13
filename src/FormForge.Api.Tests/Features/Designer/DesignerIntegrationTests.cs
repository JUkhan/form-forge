using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Infrastructure.EventBus;
using FormForge.Api.Infrastructure.Persistence;
using FormForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FormForge.Api.Tests.Features.Designer;

[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class DesignerIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");

    private readonly PostgresFixture _postgres;
    private readonly RecordingDomainEventBus _eventBus = new();
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public DesignerIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:formforge", _postgres.ConnectionString);
                builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");

                // Replace IDomainEventBus with a recording test double so 3.7
                // tests can assert SchemaPublished firing without a real subscriber.
                // The real InProcessEventBus is a singleton; the test double also
                // registers as singleton to keep handler-isolation semantics.
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IDomainEventBus>();
                    services.AddSingleton<IDomainEventBus>(_eventBus);
                });
            });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        await db.Database.MigrateAsync();

        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE component_schema_versions, component_schemas, role_permissions, user_roles, roles, refresh_tokens, users RESTART IDENTITY CASCADE;");

        await ReseedSystemRolesAsync(db);
        await SeedTestUsersAsync(db);

        _eventBus.Reset();

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

    // ---------- POST /api/designers ----------

    [Fact]
    public async Task CreateDesigner_Unauthenticated_Returns401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers")
        {
            Content = JsonContent.Create(new { designerId = "incident_report", displayName = "Incident", mode = "CRUD" }),
        };
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateDesigner_AsNonAdmin_Returns403()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers")
        {
            Content = JsonContent.Create(new { designerId = "incident_report", displayName = "Incident", mode = "CRUD" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateDesigner_HappyPath_Returns201_WithLocationAndV1Draft()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers")
        {
            Content = JsonContent.Create(new { designerId = "incident_report", displayName = "Incident Report", mode = "CRUD" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal("/api/designers/incident_report", response.Headers.Location!.ToString());

        var body = await response.Content.ReadFromJsonAsync<DesignerResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("incident_report", body.DesignerId);
        Assert.Equal("Incident Report", body.DisplayName);
        Assert.Equal("CRUD", body.Mode);
        Assert.Equal(1, body.LatestVersion);
        Assert.Equal("Draft", body.Status);
        Assert.Null(body.RootElement);
        Assert.Null(body.UpdatedAt);
    }

    [Theory]
    [InlineData("Incident_Report")]            // uppercase
    [InlineData("1leading_digit")]             // leading digit
    [InlineData("incident-report")]            // hyphen
    [InlineData("with space")]                 // space
    [InlineData("")]                           // empty
    public async Task CreateDesigner_InvalidDesignerId_Returns422_IdentifierInvalid(string designerId)
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers")
        {
            Content = JsonContent.Create(new { designerId, displayName = "Display", mode = "CRUD" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        // AC-1: regex/length/empty failures produce IDENTIFIER_INVALID.
        // Reserved-keyword failures produce IDENTIFIER_RESERVED_KEYWORD (AC-2,
        // Story 5.1) — none of the InlineData inputs above are reserved keywords,
        // so this [Theory] correctly asserts IDENTIFIER_INVALID for all of them.
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("IDENTIFIER_INVALID", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateDesigner_ReservedPgKeyword_Returns422_IdentifierReservedKeyword()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        // Reserved PG keyword passes the structural regex (lowercase, fits length)
        // so FluentValidation returns 200. SafeIdentifier inside the service catches
        // it and the endpoint translates that to 422 IDENTIFIER_RESERVED_KEYWORD
        // (distinct from IDENTIFIER_INVALID per AC-2 — Story 5.1).
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers")
        {
            Content = JsonContent.Create(new { designerId = "select", displayName = "Display", mode = "CRUD" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("IDENTIFIER_RESERVED_KEYWORD", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateDesigner_InvalidPattern_Returns422_IdentifierInvalid()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        // Locks in the AC-1 vs AC-2 distinction at the integration level:
        // a regex-failing id ("Has-Uppercase") must surface IDENTIFIER_INVALID,
        // never IDENTIFIER_RESERVED_KEYWORD. Pairs with the renamed reserved-keyword
        // test above so both error codes are test-locked end-to-end.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers")
        {
            Content = JsonContent.Create(new { designerId = "Has-Uppercase", displayName = "Display", mode = "CRUD" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("IDENTIFIER_INVALID", body, StringComparison.Ordinal);
        Assert.DoesNotContain("IDENTIFIER_RESERVED_KEYWORD", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateDesigner_Duplicate_Returns409_DesignerExists()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        await CreateDesignerViaApiAsync(token, "duplicate_id", "First");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers")
        {
            Content = JsonContent.Create(new { designerId = "duplicate_id", displayName = "Second", mode = "CRUD" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("DESIGNER_EXISTS", body, StringComparison.Ordinal);
    }

    // FR-54 AC-1 — mode is required on creation.
    [Fact]
    public async Task CreateDesigner_MissingMode_Returns422()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers")
        {
            // No `mode` field — FluentValidation rejects with 422 before the service.
            Content = JsonContent.Create(new { designerId = "no_mode", displayName = "No Mode" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreateDesigner_InvalidMode_Returns422()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers")
        {
            Content = JsonContent.Create(new { designerId = "bad_mode", displayName = "Bad Mode", mode = "READ" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // FR-54 AC-2 — mode is persisted and round-trips on the create response.
    [Fact]
    public async Task CreateDesigner_ViewMode_Returns201_WithModeView()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers")
        {
            Content = JsonContent.Create(new { designerId = "view_only", displayName = "View Only", mode = "VIEW" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DesignerResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("VIEW", body!.Mode);
    }

    // FR-54 AC-5 — a Repeater referencing a VIEW-mode designer is rejected on Save.
    [Fact]
    public async Task SaveVersion_ViewModeReference_Returns422_ViewReferenceRejected()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        // A VIEW-mode designer to reference, and a CRUD host designer to author into.
        await CreateDesignerViaApiAsync(token, "view_src", "View Source", "VIEW");
        await CreateDesignerViaApiAsync(token, "crud_host", "Crud Host", "CRUD");

        var root = new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = new object[]
            {
                new
                {
                    id = "rep",
                    type = "Repeater",
                    properties = new { rowDesignerId = "view_src", fieldKey = "line_items" },
                    children = Array.Empty<object>(),
                },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers/crud_host/versions")
        {
            Content = JsonContent.Create(new { rootElement = root }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("VIEW_REFERENCE_REJECTED", body, StringComparison.Ordinal);
    }

    // ---------- GET /api/designers (list) ----------

    [Fact]
    public async Task ListDesigners_IncludesModeOnEachItem()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "crud_item", "Crud Item", "CRUD");
        await CreateDesignerViaApiAsync(token, "view_item", "View Item", "VIEW");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/designers?page=1&pageSize=25&sort=designerId:asc");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PagedResultDto<DesignerListItemDto>>();
        Assert.NotNull(body);
        Assert.Equal("CRUD", body!.Data.Single(d => d.DesignerId == "crud_item").Mode);
        Assert.Equal("VIEW", body.Data.Single(d => d.DesignerId == "view_item").Mode);
    }

    [Fact]
    public async Task ListDesigners_Empty_Returns200_EmptyPage()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/designers?page=1&pageSize=25");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PagedResultDto<DesignerListItemDto>>();
        Assert.NotNull(body);
        Assert.Empty(body.Data);
        Assert.Equal(0, body.Total);
    }

    [Fact]
    public async Task ListDesigners_WithItems_Returns200_OrderedByDesignerId()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "b_designer", "Beta");
        await CreateDesignerViaApiAsync(token, "a_designer", "Alpha");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/designers?page=1&pageSize=25");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PagedResultDto<DesignerListItemDto>>();
        Assert.NotNull(body);
        Assert.Equal(2, body.Total);
        Assert.Equal(1, body.TotalPages);
        Assert.Collection(body.Data,
            d =>
            {
                Assert.Equal("a_designer", d.DesignerId);
                Assert.Equal(1, d.LatestVersion);
                Assert.Equal("Draft", d.Status);
                // AC-1: CreatorDisplayName is hydrated via a LEFT JOIN through
                // the ComponentSchema → User FK navigation. The seeded admin
                // user's display name must round-trip through the list endpoint.
                Assert.Equal("Platform Admin", d.CreatorDisplayName);
            },
            d =>
            {
                Assert.Equal("b_designer", d.DesignerId);
                Assert.Equal("Platform Admin", d.CreatorDisplayName);
            });
    }

    [Fact]
    public async Task ListDesigners_SortByDisplayNameDesc_OrdersDescending()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "d_alpha", "Alpha");
        await CreateDesignerViaApiAsync(token, "d_charlie", "Charlie");
        await CreateDesignerViaApiAsync(token, "d_bravo", "Bravo");

        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/designers?page=1&pageSize=25&sort=displayName:desc");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PagedResultDto<DesignerListItemDto>>();
        Assert.NotNull(body);
        string[] expectedOrder = ["Charlie", "Bravo", "Alpha"];
        Assert.Equal(expectedOrder, body.Data.Select(d => d.DisplayName));
    }

    [Fact]
    public async Task ListDesigners_SearchTerm_FiltersByIdOrDisplayName()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "incident_report", "Incident Report");
        await CreateDesignerViaApiAsync(token, "expense_claim", "Expense Claim");

        // Matches the displayName of the first but neither identifier of the second.
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/designers?page=1&pageSize=25&search=incident");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PagedResultDto<DesignerListItemDto>>();
        Assert.NotNull(body);
        Assert.Equal(1, body.Total);
        Assert.Equal("incident_report", body.Data.Single().DesignerId);
    }

    [Fact]
    public async Task ListDesigners_StatusFilter_ReturnsOnlyMatchingLatestStatus()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "pub_one", "Pub One");
        await CreateDesignerViaApiAsync(token, "draft_one", "Draft One");

        // Publish pub_one's v1 so its latest status is Published; draft_one stays Draft.
        using (var pub = await PutVersionStatusAsync(token, "pub_one", 1, "Published"))
        {
            Assert.Equal(HttpStatusCode.OK, pub.StatusCode);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/designers?page=1&pageSize=25&status=Published");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PagedResultDto<DesignerListItemDto>>();
        Assert.NotNull(body);
        Assert.Equal(1, body.Total);
        Assert.Equal("pub_one", body.Data.Single().DesignerId);
        Assert.Equal("Published", body.Data.Single().Status);
    }

    [Fact]
    public async Task ListDesigners_AsNonAdmin_Returns200()
    {
        // GET is open to any authenticated user (admin-only POST). Confirm so the
        // Epic 6 data-entry renderer can fetch schemas without platform-admin.
        var adminToken = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(adminToken, "open_to_viewer", "Open");

        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/designers");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------- GET /api/designers/{designerId} ----------

    [Fact]
    public async Task GetDesigner_Existing_Returns200_LatestVersionDraftNullRoot()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "get_me", "Get Me");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/designers/get_me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DesignerResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("get_me", body.DesignerId);
        Assert.Equal(1, body.LatestVersion);
        Assert.Equal("Draft", body.Status);
        Assert.Null(body.RootElement);
    }

    [Fact]
    public async Task GetDesigner_Missing_Returns404_DesignerNotFound()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/designers/no_such_designer");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("DESIGNER_NOT_FOUND", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDesigner_AsNonAdmin_Returns200_LatestVersion()
    {
        // GET /{designerId} must be open to any authenticated user so the Epic 6
        // data-entry renderer (and viewer-role users) can fetch schemas.
        var adminToken = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(adminToken, "viewer_get", "Viewer Get");

        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/designers/viewer_get");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DesignerResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("viewer_get", body.DesignerId);
        Assert.Equal(1, body.LatestVersion);
    }

    // ---------- GET /api/designers/{designerId}/versions/{version} ----------

    [Fact]
    public async Task GetDesignerVersion_Existing_Returns200_WithRootElement()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "ver_me", "Versioned");

        // Seed a rootElement directly via DbContext — Story 3.6 will add the PUT
        // endpoint that does this through the API. For 3.2 we just need to verify
        // the GET serializes a non-null root as a JSON object (not a string).
        const string root = """{"id":"root","type":"stack","properties":{},"children":[]}""";
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var v1 = await db.ComponentSchemaVersions
                .FirstAsync(v => v.DesignerId == "ver_me" && v.Version == 1);
            v1.RootElement = root;
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/designers/ver_me/versions/1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var rootEl = body.GetProperty("rootElement");
        Assert.Equal(JsonValueKind.Object, rootEl.ValueKind);
        Assert.Equal("root", rootEl.GetProperty("id").GetString());
        Assert.Equal("stack", rootEl.GetProperty("type").GetString());
    }

    [Fact]
    public async Task GetDesignerVersion_UnknownDesigner_Returns404()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/designers/no_such/versions/1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetDesignerVersion_UnknownVersion_Returns404()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "single_v", "One");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/designers/single_v/versions/99");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("DESIGNER_NOT_FOUND", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDesignerVersion_AsNonAdmin_Returns200_WithRootElement()
    {
        // GET /{designerId}/versions/{version} must be open to any authenticated
        // user — DynamicComponent renderer (Epic 6) reads the pinned version on
        // behalf of viewer-role data-entry users.
        var adminToken = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(adminToken, "viewer_ver", "Viewer Version");

        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/designers/viewer_ver/versions/1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DesignerResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("viewer_ver", body.DesignerId);
        Assert.Equal(1, body.LatestVersion);
        Assert.Equal("Draft", body.Status);
    }

    // ---------- POST /api/designers/{designerId}/versions (Story 3.6) ----------

    [Fact]
    public async Task SaveVersion_Unauthenticated_Returns401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers/any/versions")
        {
            Content = JsonContent.Create(new { rootElement = MinimalStackRoot() }),
        };
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SaveVersion_AsNonAdmin_Returns403()
    {
        var adminToken = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(adminToken, "save_v_403", "Save 403");

        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers/save_v_403/versions")
        {
            Content = JsonContent.Create(new { rootElement = MinimalStackRoot() }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SaveVersion_UnknownDesigner_Returns404_DesignerNotFound()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers/no_such_id/versions")
        {
            Content = JsonContent.Create(new { rootElement = MinimalStackRoot() }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("DESIGNER_NOT_FOUND", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveVersion_HappyPath_Returns201_NewV2DraftAndV1Unchanged()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "save_v_happy", "Happy");

        var root = new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = new object[]
            {
                new
                {
                    id = "f1",
                    type = "Text Input",
                    properties = new { fieldKey = "full_name", label = "Full name" },
                    children = Array.Empty<object>(),
                },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers/save_v_happy/versions")
        {
            Content = JsonContent.Create(new { rootElement = root }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal("/api/designers/save_v_happy/versions/2", response.Headers.Location!.ToString());

        var body = await response.Content.ReadFromJsonAsync<DesignerResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("save_v_happy", body.DesignerId);
        Assert.Equal(2, body.LatestVersion);
        Assert.Equal("Draft", body.Status);
        Assert.NotNull(body.RootElement);
        Assert.NotNull(body.UpdatedAt);

        // AC-4: round-trip — re-fetching v2 returns identical rootElement
        using var getV2 = new HttpRequestMessage(HttpMethod.Get, "/api/designers/save_v_happy/versions/2");
        getV2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var getV2Resp = await _client!.SendAsync(getV2);
        Assert.Equal(HttpStatusCode.OK, getV2Resp.StatusCode);
        var getBody = await getV2Resp.Content.ReadFromJsonAsync<JsonElement>();
        var rootEl = getBody.GetProperty("rootElement");
        Assert.Equal("root", rootEl.GetProperty("id").GetString());
        Assert.Equal("Stack", rootEl.GetProperty("type").GetString());
        Assert.Equal("Text Input", rootEl.GetProperty("children")[0].GetProperty("type").GetString());
        Assert.Equal("full_name", rootEl.GetProperty("children")[0].GetProperty("properties").GetProperty("fieldKey").GetString());

        // Previous version (v1) must remain unmutated.
        using var getV1 = new HttpRequestMessage(HttpMethod.Get, "/api/designers/save_v_happy/versions/1");
        getV1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var getV1Resp = await _client!.SendAsync(getV1);
        Assert.Equal(HttpStatusCode.OK, getV1Resp.StatusCode);
        var v1Body = await getV1Resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, v1Body.GetProperty("rootElement").ValueKind);
        Assert.Equal(1, v1Body.GetProperty("latestVersion").GetInt32());
        Assert.Equal("Draft", v1Body.GetProperty("status").GetString());
    }

    // ---------- PUT /api/designers/{id}/versions/{version} (in-place update) ----------

    [Fact]
    public async Task UpdateVersion_HappyPath_Returns200_UpdatesInPlaceAndRenames()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "upd_v_happy", "Original");

        var root = new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = new object[]
            {
                new
                {
                    id = "f1",
                    type = "Text Input",
                    properties = new { fieldKey = "full_name", label = "Full name" },
                    children = Array.Empty<object>(),
                },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/designers/upd_v_happy/versions/1")
        {
            Content = JsonContent.Create(new { rootElement = root, displayName = "Renamed Form" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DesignerResponseDto>();
        Assert.NotNull(body);
        // Rename persisted on the schema row…
        Assert.Equal("Renamed Form", body.DisplayName);
        // …and the SAME version was overwritten — no v2 was spawned.
        Assert.Equal(1, body.LatestVersion);
        Assert.NotNull(body.RootElement);

        // GET latest still reports v1 (in-place), with the new name + content.
        using var getLatest = new HttpRequestMessage(HttpMethod.Get, "/api/designers/upd_v_happy");
        getLatest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var getResp = await _client!.SendAsync(getLatest);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var getBody = await getResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, getBody.GetProperty("latestVersion").GetInt32());
        Assert.Equal("Renamed Form", getBody.GetProperty("displayName").GetString());
        Assert.Equal(
            "full_name",
            getBody.GetProperty("rootElement").GetProperty("children")[0]
                .GetProperty("properties").GetProperty("fieldKey").GetString());
        // Exactly one version exists — the update did not create a new snapshot.
        Assert.Equal(1, getBody.GetProperty("versions").GetArrayLength());
    }

    [Fact]
    public async Task UpdateVersion_VersionNotFound_Returns404()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "upd_v_nover", "NoVer");

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/designers/upd_v_nover/versions/99")
        {
            Content = JsonContent.Create(new { rootElement = MinimalStackRoot(), displayName = "Whatever" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("VERSION_NOT_FOUND", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateVersion_MissingFieldKey_Returns422_FieldKeyInvalid()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "upd_v_missing", "Missing");

        var root = new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = new object[]
            {
                new
                {
                    id = "leaf",
                    type = "Text Input",
                    properties = new { label = "Label only — no fieldKey" },
                    children = Array.Empty<object>(),
                },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/designers/upd_v_missing/versions/1")
        {
            Content = JsonContent.Create(new { rootElement = root, displayName = "Missing" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("FIELD_KEY_INVALID", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateVersion_AsNonAdmin_Returns403()
    {
        var adminToken = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(adminToken, "upd_v_forbidden", "Forbidden");
        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/designers/upd_v_forbidden/versions/1")
        {
            Content = JsonContent.Create(new { rootElement = MinimalStackRoot(), displayName = "Nope" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SaveVersion_MissingFieldKey_Returns422_FieldKeyInvalid()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "save_v_missing", "Missing");

        var root = new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = new object[]
            {
                new
                {
                    id = "leaf",
                    type = "Text Input",
                    properties = new { label = "Label only — no fieldKey" },
                    children = Array.Empty<object>(),
                },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers/save_v_missing/versions")
        {
            Content = JsonContent.Create(new { rootElement = root }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("FIELD_KEY_INVALID", body, StringComparison.Ordinal);
        Assert.Contains("FIELD_KEY_MISSING", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveVersion_RegexInvalidFieldKey_Returns422_FieldKeyInvalid()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "save_v_regex", "Regex");

        var root = new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = new object[]
            {
                new
                {
                    id = "leaf",
                    type = "Text Input",
                    properties = new { fieldKey = "My Field" },
                    children = Array.Empty<object>(),
                },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers/save_v_regex/versions")
        {
            Content = JsonContent.Create(new { rootElement = root }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("FIELD_KEY_INVALID", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveVersion_ReservedPgKeywordFieldKey_Returns422_FieldKeyInvalid()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "save_v_reserved", "Reserved");

        // "select" passes the structural regex but SafeIdentifier rejects it
        // as a PostgreSQL reserved keyword.
        var root = new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = new object[]
            {
                new
                {
                    id = "leaf",
                    type = "Text Input",
                    properties = new { fieldKey = "select" },
                    children = Array.Empty<object>(),
                },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers/save_v_reserved/versions")
        {
            Content = JsonContent.Create(new { rootElement = root }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("FIELD_KEY_INVALID", body, StringComparison.Ordinal);
        Assert.Contains("reserved", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveVersion_FieldKeyCollision_Returns422_FieldKeyCollision()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "save_v_collision", "Collision");

        var root = new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = new object[]
            {
                new
                {
                    id = "a",
                    type = "Text Input",
                    properties = new { fieldKey = "shared" },
                    children = Array.Empty<object>(),
                },
                new
                {
                    id = "b",
                    type = "Number Input",
                    properties = new { fieldKey = "shared" },
                    children = Array.Empty<object>(),
                },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers/save_v_collision/versions")
        {
            Content = JsonContent.Create(new { rootElement = root }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("FIELD_KEY_COLLISION", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveVersion_RepeaterFieldNoFieldKey_Returns201()
    {
        // Neither the Repeater CONTAINER (maps to a child table) NOR a Repeater
        // Field requires a fieldKey: a Repeater Field is a tabular display column
        // that references a row-form field by `fieldName`, so it has no fieldKey
        // of its own. This pins that contract at the API boundary so it can't
        // silently drift if the InputBearingTypes set is edited.
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "save_v_rep", "RepeaterField");

        var root = new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = new object[]
            {
                new
                {
                    id = "rep",
                    type = "Repeater",
                    properties = new { rowDesignerId = "some_row_form", fieldKey = "line_items" },
                    children = new object[]
                    {
                        new
                        {
                            id = "rf",
                            type = "Repeater Field",
                            properties = new { fieldName = "full_name" },
                            children = Array.Empty<object>(),
                        },
                    },
                },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers/save_v_rep/versions")
        {
            Content = JsonContent.Create(new { rootElement = root }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task SaveVersion_NonInputBearingTypesNoFieldKey_Returns201()
    {
        // Structural containers (Stack, Row, Tabs) and non-data leaves (Label,
        // Button) must NOT be checked for fieldKey. (Repeater is excluded here —
        // it IS input-bearing now: its fieldKey names the one-to-many relation.)
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "save_v_nib", "NonInputBearing");

        var root = new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = new object[]
            {
                new { id = "row1",   type = "Row",      properties = new { }, children = Array.Empty<object>() },
                new { id = "tabs1",  type = "Tabs",     properties = new { }, children = Array.Empty<object>() },
                new { id = "lbl1",   type = "Label",    properties = new { }, children = Array.Empty<object>() },
                new { id = "btn1",   type = "Button",   properties = new { }, children = Array.Empty<object>() },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers/save_v_nib/versions")
        {
            Content = JsonContent.Create(new { rootElement = root }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---------- POST /api/designers/{designerId}/duplicate (Story 3.8) ----------

    [Fact]
    public async Task DuplicateDesigner_Unauthenticated_Returns401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers/anything/duplicate");
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DuplicateDesigner_AsNonAdmin_Returns403()
    {
        var adminToken = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(adminToken, "dup_403", "Dup 403");

        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers/dup_403/duplicate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DuplicateDesigner_UnknownDesigner_Returns404_DesignerNotFound()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers/no_such_designer/duplicate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("DESIGNER_NOT_FOUND", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateDesigner_HappyPath_Returns201_NewCopyWithSourceRootElement()
    {
        // AC-2: a duplicate yields a new ComponentSchema at "{id}_copy" with
        // displayName "Copy of {original}", status Draft, latestVersion 1, and
        // its rootElement matches the source's latest version.
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "dup_src", "Source Schema");

        // Save a v2 on the source so we can verify the duplicate inherits the
        // LATEST version's rootElement (not v1's null).
        var root = new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = new object[]
            {
                new
                {
                    id = "f1",
                    type = "Text Input",
                    properties = new { fieldKey = "full_name", label = "Full name" },
                    children = Array.Empty<object>(),
                },
            },
        };
        await CreateVersionViaApiAsync(token, "dup_src", root);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers/dup_src/duplicate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal("/api/designers/dup_src_copy", response.Headers.Location!.ToString());

        var body = await response.Content.ReadFromJsonAsync<DesignerResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("dup_src_copy", body.DesignerId);
        Assert.Equal("Copy of Source Schema", body.DisplayName);
        Assert.Equal("Draft", body.Status);
        Assert.Equal(1, body.LatestVersion);

        // Verify the copy's v1 rootElement matches the source's latest (v2).
        using var getCopy = new HttpRequestMessage(HttpMethod.Get, "/api/designers/dup_src_copy/versions/1");
        getCopy.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var getCopyResp = await _client!.SendAsync(getCopy);
        Assert.Equal(HttpStatusCode.OK, getCopyResp.StatusCode);
        var copyBody = await getCopyResp.Content.ReadFromJsonAsync<JsonElement>();
        var rootEl = copyBody.GetProperty("rootElement");
        Assert.Equal(JsonValueKind.Object, rootEl.ValueKind);
        Assert.Equal("Text Input", rootEl.GetProperty("children")[0].GetProperty("type").GetString());
        Assert.Equal("full_name", rootEl.GetProperty("children")[0].GetProperty("properties").GetProperty("fieldKey").GetString());

        // Second duplicate of the same source must take {id}_copy2.
        using var second = new HttpRequestMessage(HttpMethod.Post, "/api/designers/dup_src/duplicate");
        second.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var secondResp = await _client!.SendAsync(second);
        Assert.Equal(HttpStatusCode.Created, secondResp.StatusCode);
        var secondBody = await secondResp.Content.ReadFromJsonAsync<DesignerResponseDto>();
        Assert.Equal("dup_src_copy2", secondBody!.DesignerId);
    }

    [Fact]
    public async Task DuplicateDesigner_SourceIdTooLong_Returns422_DuplicateIdTooLong()
    {
        // Source designerId with length ≥59 makes even the shortest candidate
        // ("{id}_copy") exceed the 63-char column cap. Pre-patch, the backend
        // returned the misleading DUPLICATE_CONFLICT ("Too many copies…");
        // now it returns 422 DUPLICATE_ID_TOO_LONG so the SPA can show an
        // accurate message.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var longId = new string('a', 59); // 59 chars + "_copy" = 64 > 63
        await CreateDesignerViaApiAsync(token, longId, "Long Id Source");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/designers/{longId}/duplicate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("DUPLICATE_ID_TOO_LONG", body, StringComparison.Ordinal);
        Assert.Contains("designers.duplicateIdTooLong", body, StringComparison.Ordinal);
    }

    // ---------- PUT /api/designers/{designerId}/versions/{version}/status (Story 3.7) ----------

    [Fact]
    public async Task PublishVersion_DraftVersion_Returns200WithPublishedStatus()
    {
        // AC-1 happy path: Draft v1 → Published.
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "pub_draft", "Pub Draft");

        using var response = await PutVersionStatusAsync(token, "pub_draft", 1, "Published");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<DesignerResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("Published", body.Status);
        Assert.Equal(1, body.LatestVersion);
        Assert.NotNull(body.PublishedAt);

        // AC-1 / AR-7: SchemaPublished fires after a successful publish. Pins
        // the contract Epic 5's schema-registry cache-eviction subscriber depends on.
        var events = _eventBus.Published.OfType<SchemaPublished>().ToList();
        Assert.Single(events);
        Assert.Equal("pub_draft", events[0].DesignerId);
        Assert.Equal(1, events[0].Version);
    }

    [Fact]
    public async Task PublishVersion_ExistingPublishedPresent_BothRemainPublished()
    {
        // A designer may have MULTIPLE Published versions at once. Publishing v2
        // must NOT demote the previously-Published v1 — both stay Published.
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "multi_pub", "Multi Publish");

        // Publish v1.
        using (var v1Pub = await PutVersionStatusAsync(token, "multi_pub", 1, "Published"))
        {
            Assert.Equal(HttpStatusCode.OK, v1Pub.StatusCode);
        }

        // Save v2 (auto-Draft per Story 3.6) and publish it.
        var v2 = await CreateVersionViaApiAsync(token, "multi_pub", MinimalStackRoot());
        Assert.Equal(2, v2);

        using (var v2Pub = await PutVersionStatusAsync(token, "multi_pub", 2, "Published"))
        {
            Assert.Equal(HttpStatusCode.OK, v2Pub.StatusCode);
        }

        // Both v1 and v2 remain Published with publishedAt set — no auto-demote.
        using var getV1 = new HttpRequestMessage(HttpMethod.Get, "/api/designers/multi_pub/versions/1");
        getV1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var getV1Resp = await _client!.SendAsync(getV1);
        var v1Body = await getV1Resp.Content.ReadFromJsonAsync<DesignerResponseDto>();
        Assert.Equal("Published", v1Body!.Status);
        Assert.NotNull(v1Body.PublishedAt);

        using var getV2 = new HttpRequestMessage(HttpMethod.Get, "/api/designers/multi_pub/versions/2");
        getV2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var getV2Resp = await _client!.SendAsync(getV2);
        var v2Body = await getV2Resp.Content.ReadFromJsonAsync<DesignerResponseDto>();
        Assert.Equal("Published", v2Body!.Status);
        Assert.NotNull(v2Body.PublishedAt);

        // SchemaPublished fires once per successful publish (v1, v2).
        var events = _eventBus.Published.OfType<SchemaPublished>().ToList();
        Assert.Equal(2, events.Count);
        Assert.Equal(1, events[0].Version);
        Assert.Equal(2, events[1].Version);
        Assert.All(events, e => Assert.Equal("multi_pub", e.DesignerId));
    }

    [Fact]
    public async Task ArchiveVersion_DraftVersion_Returns200WithArchivedStatus()
    {
        // AC-2 second precondition: Draft → Archived is valid. (The other AC-2
        // test covers Published → Archived; this one covers the Draft path so
        // the AC-2 "Published OR Draft" precondition is fully exercised.)
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "arc_draft", "Archive Draft");

        // v1 starts as Draft (per Story 3.2 CreateAsync); no Publish here.
        using var arcResp = await PutVersionStatusAsync(token, "arc_draft", 1, "Archived");
        Assert.Equal(HttpStatusCode.OK, arcResp.StatusCode);
        var body = await arcResp.Content.ReadFromJsonAsync<DesignerResponseDto>();
        Assert.Equal("Archived", body!.Status);
        // Archiving a Draft never publishes — PublishedAt stays null.
        Assert.Null(body.PublishedAt);

        // AC-1 / AR-7: SchemaPublished must NOT fire on an Archive.
        Assert.Empty(_eventBus.Published.OfType<SchemaPublished>());
    }

    [Fact]
    public async Task ArchiveVersion_PublishedVersion_Returns200WithArchivedStatus()
    {
        // AC-2: Published → Archived.
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "arc_pub", "Archive Published");

        using (var pub = await PutVersionStatusAsync(token, "arc_pub", 1, "Published"))
        {
            Assert.Equal(HttpStatusCode.OK, pub.StatusCode);
        }

        using var arcResp = await PutVersionStatusAsync(token, "arc_pub", 1, "Archived");
        Assert.Equal(HttpStatusCode.OK, arcResp.StatusCode);
        var body = await arcResp.Content.ReadFromJsonAsync<DesignerResponseDto>();
        Assert.Equal("Archived", body!.Status);
    }

    [Fact]
    public async Task PublishVersion_ArchivedVersion_Succeeds()
    {
        // AC-3: Archived → Published is a valid transition. Publish v1, archive it
        // explicitly, then re-publish it.
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "republish", "Republish");

        await PutVersionStatusAsync(token, "republish", 1, "Published");
        await PutVersionStatusAsync(token, "republish", 1, "Archived");

        // v1 is now Archived. Re-publish it.
        using var rePub = await PutVersionStatusAsync(token, "republish", 1, "Published");
        Assert.Equal(HttpStatusCode.OK, rePub.StatusCode);
        var body = await rePub.Content.ReadFromJsonAsync<DesignerResponseDto>();
        Assert.Equal("Published", body!.Status);
        Assert.NotNull(body.PublishedAt);
    }

    [Fact]
    public async Task PublishVersion_AlreadyPublished_Returns200NoWrite()
    {
        // AC-4: same-status no-op returns 200 with current state — no DB write
        // and no SchemaPublished event fires.
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "noop_pub", "NoOp");

        // First publish — sets schema.UpdatedAt and fires SchemaPublished once.
        await PutVersionStatusAsync(token, "noop_pub", 1, "Published");
        var firstEventCount = _eventBus.Published.OfType<SchemaPublished>().Count();
        Assert.Equal(1, firstEventCount);

        // Snapshot schema.UpdatedAt before the same-status no-op call.
        DateTimeOffset? updatedAtBefore;
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            updatedAtBefore = await db.ComponentSchemas
                .AsNoTracking()
                .Where(s => s.DesignerId == "noop_pub")
                .Select(s => s.UpdatedAt)
                .SingleAsync();
        }

        using var second = await PutVersionStatusAsync(token, "noop_pub", 1, "Published");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<DesignerResponseDto>();
        Assert.Equal("Published", body!.Status);

        // AC-4 "no DB write": schema.UpdatedAt must be unchanged after the no-op.
        DateTimeOffset? updatedAtAfter;
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            updatedAtAfter = await db.ComponentSchemas
                .AsNoTracking()
                .Where(s => s.DesignerId == "noop_pub")
                .Select(s => s.UpdatedAt)
                .SingleAsync();
        }
        Assert.Equal(updatedAtBefore, updatedAtAfter);

        // AC-4 "no event fired": SchemaPublished count must not grow.
        Assert.Equal(firstEventCount, _eventBus.Published.OfType<SchemaPublished>().Count());
    }

    [Fact]
    public async Task UpdateVersionStatus_DraftTarget_Returns422_StatusInvalid()
    {
        // AC-5: Draft is a recognized but invalid TARGET status. The validator
        // recognizes it so the request reaches the handler; the handler returns
        // STATUS_INVALID (distinct from VALIDATION_FAILED for unknown values).
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "draft_target", "Draft Target");

        using var response = await PutVersionStatusAsync(token, "draft_target", 1, "Draft");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("STATUS_INVALID", body.GetProperty("code").GetString());
        Assert.Equal("designers.statusInvalid", body.GetProperty("messageKey").GetString());
    }

    [Fact]
    public async Task UpdateVersionStatus_InvalidStatus_Returns422_ValidationFailed()
    {
        // AC-5: unknown status values are rejected by the FluentValidation filter
        // with the standard ValidationProblemDetails envelope (the `errors`
        // dictionary names the rejected field). Distinct from the
        // recognized-but-invalid "Draft" path which returns STATUS_INVALID.
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "bad_status", "Bad Status");

        using var response = await PutVersionStatusAsync(token, "bad_status", 1, "foo");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var errors = body.GetProperty("errors");
        Assert.Equal(JsonValueKind.Object, errors.ValueKind);
        Assert.True(errors.TryGetProperty("Status", out _),
            "Validation envelope should name the 'Status' field as the rejected property.");
    }

    [Fact]
    public async Task UpdateVersionStatus_VersionNotFound_Returns404()
    {
        // AC-6: the designer exists but the version number does not.
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "ver_404", "Ver 404");

        using var response = await PutVersionStatusAsync(token, "ver_404", 99, "Published");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("VERSION_NOT_FOUND", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateVersionStatus_DesignerNotFound_Returns404()
    {
        // AC-7: the designer itself does not exist.
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var response = await PutVersionStatusAsync(token, "no_such_designer", 1, "Published");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("DESIGNER_NOT_FOUND", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateVersionStatus_AsViewer_Returns403()
    {
        // AC-8: non-platform-admin users are blocked at the authorization filter.
        var adminToken = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(adminToken, "viewer_403", "Viewer 403");

        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");
        using var response = await PutVersionStatusAsync(viewerToken, "viewer_403", 1, "Published");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UpdateVersionStatus_Unauthenticated_Returns401()
    {
        // AC-8 sibling: requests without a bearer token must be rejected by the
        // authentication pipeline before the platform-admin filter runs. Pins
        // the auth-pipeline contract so a future routing change can't silently
        // open the endpoint to anonymous callers.
        using var response = await PutVersionStatusAsync(token: "", "no_token", 1, "Published");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------- Helpers ----------

    private async Task<string> LoginAsync(string email, string password)
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email, password });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        return body!.AccessToken;
    }

    private static object MinimalStackRoot() => new
    {
        id = "root",
        type = "Stack",
        properties = new { },
        children = Array.Empty<object>(),
    };

    private async Task CreateDesignerViaApiAsync(
        string token, string designerId, string displayName, string mode = "CRUD")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers")
        {
            Content = JsonContent.Create(new { designerId, displayName, mode }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private async Task<int> CreateVersionViaApiAsync(string token, string designerId, object rootElement)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/designers/{designerId}/versions")
        {
            Content = JsonContent.Create(new { rootElement }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<DesignerResponseDto>();
        return body!.LatestVersion;
    }

    // Returned HttpResponseMessage is the caller's to dispose — Story 3.7 tests
    // assert on body/headers after the call.
    private async Task<HttpResponseMessage> PutVersionStatusAsync(
        string token, string designerId, int version, string status)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/designers/{designerId}/versions/{version}/status")
        {
            Content = JsonContent.Create(new { status }),
        };
        if (token.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return await _client!.SendAsync(request);
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
    private sealed record DesignerListItemDto(
        string DesignerId,
        string DisplayName,
        string Mode,
        string Status,
        int LatestVersion,
        DateTimeOffset CreatedAt,
        DateTimeOffset? UpdatedAt,
        string? CreatorDisplayName);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record DesignerResponseDto(
        string DesignerId,
        string DisplayName,
        string Mode,
        string Status,
        int LatestVersion,
        JsonElement? RootElement,
        DateTimeOffset CreatedAt,
        DateTimeOffset? UpdatedAt,
        DateTimeOffset? PublishedAt);

    // Test double for IDomainEventBus. Records every Published event for
    // assertion in Story 3.7's AC-1 / AC-4 / AR-7 tests. Subscribe is a no-op
    // because 3.7 has no real subscribers — the production InProcessEventBus
    // tolerates missing handlers identically.
    private sealed class RecordingDomainEventBus : IDomainEventBus
    {
        private readonly object _gate = new();
        private readonly List<object> _published = new();

        public IReadOnlyList<object> Published
        {
            get
            {
                lock (_gate)
                {
                    return _published.ToArray();
                }
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _published.Clear();
            }
        }

        public void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class
        {
            ArgumentNullException.ThrowIfNull(handler);
        }

        public void Publish<TEvent>(TEvent @event) where TEvent : class
        {
            ArgumentNullException.ThrowIfNull(@event);
            lock (_gate)
            {
                _published.Add(@event);
            }
        }
    }
}
