using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Infrastructure.Persistence;
using FormForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormForge.Api.Tests.Features.Datasets;

// End-to-end coverage of the Dataset-backed Dropdown source endpoints:
//   GET /api/datasets/{id}/columns  — the backing VIEW's column names (inspector).
//   GET /api/datasets/{id}/options  — paginated {value,label} pairs (runtime).
// Both are auth-only (a viewer without dataset-management can read them, mirroring
// the Designer-backed options endpoint). The dataset is created with a self-contained
// VALUES query so the test needs no allowlisted probe table.
[Collection("DatasetIntegrationTests")]
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class DatasetDropdownTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");

    // A dataset whose VIEW exposes emp_id (integer) + emp_name (text), three rows.
    private const string DatasetQuery =
        "SELECT * FROM (VALUES (1, 'Alice'), (2, 'Bob'), (3, 'Carol')) AS t(emp_id, emp_name)";

    // A dataset with a dept column to exercise the cascading "Depends on" filter:
    // two depts, two employees each.
    private const string CascadeDatasetQuery =
        "SELECT * FROM (VALUES " +
        "(1, 'Alice', 'sales'), (2, 'Bob', 'sales'), " +
        "(3, 'Carol', 'eng'), (4, 'Dave', 'eng')) AS t(emp_id, emp_name, dept)";

    private static readonly string[] ExpectedColumns = { "emp_id", "emp_name" };

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public DatasetDropdownTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:formforge", _postgres.ConnectionString);
                builder.UseSetting("ConnectionStrings:formforge_preview", _postgres.ConnectionString);
                builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
            });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        await db.Database.MigrateAsync();

        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE custom_dataset, dataset_audit_log, " +
            "role_permissions, user_roles, roles, refresh_tokens, users " +
            "RESTART IDENTITY CASCADE;");

        // Drop VIEWs left by previous runs (TRUNCATE does not drop VIEWs).
        await db.Database.ExecuteSqlRawAsync(
            """
            DO $$
            DECLARE v record;
            BEGIN
              FOR v IN SELECT table_name FROM information_schema.views WHERE table_schema = 'datasets'
              LOOP
                EXECUTE format('DROP VIEW IF EXISTS datasets.%I CASCADE', v.table_name);
              END LOOP;
            END $$;
            """);

        await ReseedRolesAsync(db);
        await SeedUsersAsync(db);

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            _client?.Dispose();
            await _factory.DisposeAsync();
        }
        else
        {
            _client?.Dispose();
        }
    }

    [Fact]
    public async Task Get_Columns_Returns_View_Columns()
    {
        var adminToken = await LoginAsync("admin@example.com");
        var datasetId = await CreateDatasetAsync(adminToken, "dropdown_ds_cols");

        using var response = await GetAsync(adminToken, $"/api/datasets/{datasetId}/columns");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ColumnsResponse>();
        Assert.NotNull(dto);
        Assert.Equal(datasetId, dto!.Id);
        Assert.Equal(ExpectedColumns, dto.Columns);
    }

    [Fact]
    public async Task Get_Options_Returns_Value_Label_Pairs()
    {
        var adminToken = await LoginAsync("admin@example.com");
        var datasetId = await CreateDatasetAsync(adminToken, "dropdown_ds_opts");

        using var response = await GetAsync(adminToken,
            $"/api/datasets/{datasetId}/options?labelField=emp_name&valueField=emp_id");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<OptionsPage>();
        Assert.NotNull(page);
        Assert.Equal(3, page!.Total);
        // Ordered by label, value.
        Assert.Collection(page.Data,
            o => { Assert.Equal("1", o.Value); Assert.Equal("Alice", o.Label); },
            o => { Assert.Equal("2", o.Value); Assert.Equal("Bob", o.Label); },
            o => { Assert.Equal("3", o.Value); Assert.Equal("Carol", o.Label); });
    }

    [Fact]
    public async Task Get_Options_Search_Filters_On_Label()
    {
        var adminToken = await LoginAsync("admin@example.com");
        var datasetId = await CreateDatasetAsync(adminToken, "dropdown_ds_search");

        using var response = await GetAsync(adminToken,
            $"/api/datasets/{datasetId}/options?labelField=emp_name&valueField=emp_id&search=ali");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<OptionsPage>();
        Assert.NotNull(page);
        var only = Assert.Single(page!.Data);
        Assert.Equal("Alice", only.Label);
    }

    [Fact]
    public async Task Get_Options_Value_Filter_Resolves_Single_Row()
    {
        var adminToken = await LoginAsync("admin@example.com");
        var datasetId = await CreateDatasetAsync(adminToken, "dropdown_ds_value");

        using var response = await GetAsync(adminToken,
            $"/api/datasets/{datasetId}/options?labelField=emp_name&valueField=emp_id&value=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<OptionsPage>();
        Assert.NotNull(page);
        var only = Assert.Single(page!.Data);
        Assert.Equal("2", only.Value);
        Assert.Equal("Bob", only.Label);
    }

    [Fact]
    public async Task Get_Options_Cascade_Filter_Narrows_To_Target_Column()
    {
        var adminToken = await LoginAsync("admin@example.com");
        var datasetId = await CreateDatasetAsync(adminToken, "dropdown_ds_cascade", CascadeDatasetQuery);

        using var response = await GetAsync(adminToken,
            $"/api/datasets/{datasetId}/options?labelField=emp_name&valueField=emp_id&filter[dept]=eng");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<OptionsPage>();
        Assert.NotNull(page);
        Assert.Equal(2, page!.Total);
        Assert.Collection(page.Data,
            o => Assert.Equal("Carol", o.Label),
            o => Assert.Equal("Dave", o.Label));
    }

    [Fact]
    public async Task Get_Options_Cascade_Filter_UnknownColumn_Returns422()
    {
        var adminToken = await LoginAsync("admin@example.com");
        var datasetId = await CreateDatasetAsync(adminToken, "dropdown_ds_cascade_bad", CascadeDatasetQuery);

        using var response = await GetAsync(adminToken,
            $"/api/datasets/{datasetId}/options?labelField=emp_name&valueField=emp_id&filter[nope]=x");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Get_Options_UnknownField_Returns422()
    {
        var adminToken = await LoginAsync("admin@example.com");
        var datasetId = await CreateDatasetAsync(adminToken, "dropdown_ds_badfield");

        using var response = await GetAsync(adminToken,
            $"/api/datasets/{datasetId}/options?labelField=does_not_exist&valueField=emp_id");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Get_Options_MissingFields_Returns422()
    {
        var adminToken = await LoginAsync("admin@example.com");
        var datasetId = await CreateDatasetAsync(adminToken, "dropdown_ds_missing");

        using var response = await GetAsync(adminToken,
            $"/api/datasets/{datasetId}/options?labelField=emp_name");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Get_Columns_UnknownDataset_Returns404()
    {
        var adminToken = await LoginAsync("admin@example.com");

        using var response = await GetAsync(adminToken, $"/api/datasets/{Guid.NewGuid()}/columns");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_Options_UnknownDataset_Returns404()
    {
        var adminToken = await LoginAsync("admin@example.com");

        using var response = await GetAsync(adminToken,
            $"/api/datasets/{Guid.NewGuid()}/options?labelField=emp_name&valueField=emp_id");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_Columns_And_Options_Are_AuthOnly_ViewerCanRead()
    {
        // Admin creates the dataset (create requires dataset-management); a viewer with
        // NO dataset-management permission must still be able to read columns + options.
        var adminToken = await LoginAsync("admin@example.com");
        var datasetId = await CreateDatasetAsync(adminToken, "dropdown_ds_viewer");

        var viewerToken = await LoginAsync("viewer@example.com");

        using var columns = await GetAsync(viewerToken, $"/api/datasets/{datasetId}/columns");
        Assert.Equal(HttpStatusCode.OK, columns.StatusCode);

        using var options = await GetAsync(viewerToken,
            $"/api/datasets/{datasetId}/options?labelField=emp_name&valueField=emp_id");
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
    }

    // ---------- helpers ----------

    private async Task<HttpResponseMessage> GetAsync(string token, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private async Task<Guid> CreateDatasetAsync(string token, string datasetName, string? query = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/datasets")
        {
            Content = JsonContent.Create(new
            {
                datasetName,
                isCustomQuery = true,
                query = query ?? DatasetQuery,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<CreatedDataset>();
        Assert.NotNull(dto);
        return dto!.Id;
    }

    private async Task<string> LoginAsync(string email)
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email, password = "Password1!" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        return body!.AccessToken;
    }

    private static async Task ReseedRolesAsync(FormForgeDbContext db)
    {
        var epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO roles (id, name, is_system, can_manage_datasets, created_at)" +
            " VALUES ({0}, 'platform-admin', true, true, {1}) ON CONFLICT (id) DO NOTHING",
            PlatformAdminRoleId, epoch);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO roles (id, name, is_system, can_manage_datasets, created_at)" +
            " VALUES ({0}, 'viewer', true, false, {1}) ON CONFLICT (id) DO NOTHING",
            ViewerRoleId, epoch);
    }

    private static async Task SeedUsersAsync(FormForgeDbContext db)
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
    private sealed record ColumnsResponse(Guid Id, string DatasetName, List<string> Columns);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record OptionsPage(List<OptionDto> Data, long Total, int Page, int PageSize, int TotalPages);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record OptionDto(string Value, string? Label);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record CreatedDataset(Guid Id, string DatasetName);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record LoginResponseDto(string AccessToken, string RefreshToken, int ExpiresIn);
}
