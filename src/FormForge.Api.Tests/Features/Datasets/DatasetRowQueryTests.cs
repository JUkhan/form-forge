using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Infrastructure.Persistence;
using FormForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormForge.Api.Tests.Features.Datasets;

// End-to-end coverage of the DatasetComponent runtime data-view endpoints:
//   POST /api/datasets/{id}/rows         — paginated/filtered/sorted rows.
//   POST /api/datasets/{id}/rows/export  — same result set as CSV/XLSX/PDF.
// Both are auth-only. The dataset is a self-contained VALUES query so the test needs
// no allowlisted probe table.
[Collection("DatasetIntegrationTests")]
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class DatasetRowQueryTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");

    // VIEW: emp_id (integer), emp_name (text), dept (text), salary (integer). Five rows.
    private const string DatasetQuery =
        "SELECT * FROM (VALUES " +
        "(1, 'Alice', 'Eng', 100), (2, 'Bob', 'Eng', 90), (3, 'Carol', 'Sales', 80), " +
        "(4, 'Dave', 'Sales', 70), (5, 'Eve', 'Ops', 60)) AS t(emp_id, emp_name, dept, salary)";

    private static readonly string[] AllColumns = { "emp_id", "emp_name", "dept", "salary" };
    private static readonly string[] FilteredNames = { "Alice", "Bob", "Carol" };
    private static readonly string[] TagInValues = { "x", "y" };
    private static readonly string[] SalaryNotInValues = { "90", "80" };
    private static readonly string[] SalaryBetweenValues = { "70", "90" };

    // A dataset with a nullable `tag` column for exercising every operator (incl. IS NULL).
    // emp_id, emp_name, salary, tag:
    //   1 Alice 100 x | 2 Bob 90 y | 3 Carol 80 NULL | 4 Dave 70 x | 5 Eve 60 NULL
    private const string OperatorQuery =
        "SELECT * FROM (VALUES " +
        "(1, 'Alice', 100, 'x'), (2, 'Bob', 90, 'y'), (3, 'Carol', 80, NULL), " +
        "(4, 'Dave', 70, 'x'), (5, 'Eve', 60, NULL)) AS t(emp_id, emp_name, salary, tag)";

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public DatasetRowQueryTests(PostgresFixture postgres) => _postgres = postgres;

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

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
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
    public async Task Post_Rows_ReturnsPaginatedRows()
    {
        var token = await LoginAsync("admin@example.com");
        var id = await CreateDatasetAsync(token, "rows_basic");

        using var response = await PostRowsAsync(token, id, new
        {
            sort = new[] { new { column = "emp_id", direction = "asc" } },
            page = 1,
            pageSize = 2,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<RowsPage>();
        Assert.NotNull(page);
        Assert.Equal(5, page!.Total);
        Assert.Equal(2, page.Data.Count);
        Assert.Equal(AllColumns, page.Columns);
        Assert.Equal(1, page.Data[0]["emp_id"].GetInt32());
        Assert.Equal(2, page.Data[1]["emp_id"].GetInt32());
    }

    [Fact]
    public async Task Post_Rows_FilterTree_GreaterThan_And_In()
    {
        var token = await LoginAsync("admin@example.com");
        var id = await CreateDatasetAsync(token, "rows_filter");

        // (salary >= 80) AND (dept IN ('Eng','Sales'))  → Alice(100), Bob(90), Carol(80)
        var filters = new
        {
            id = "root",
            kind = "group",
            combinator = "AND",
            items = new object[]
            {
                new { id = "c1", kind = "condition", tableName = "", columnName = "salary", @operator = ">=", value = "80" },
                new { id = "c2", kind = "condition", tableName = "", columnName = "dept", @operator = "IN", value = new[] { "Eng", "Sales" } },
            },
        };

        using var response = await PostRowsAsync(token, id, new
        {
            filters,
            sort = new[] { new { column = "emp_id", direction = "asc" } },
            page = 1,
            pageSize = 25,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<RowsPage>();
        Assert.NotNull(page);
        Assert.Equal(3, page!.Total);
        Assert.Equal(FilteredNames,
            page.Data.Select(r => r["emp_name"].GetString()).ToArray());
    }

    [Fact]
    public async Task Post_Rows_AllOperators_FilterCorrectly()
    {
        var token = await LoginAsync("admin@example.com");

        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/datasets")
        {
            Content = JsonContent.Create(new { datasetName = "rows_operators", isCustomQuery = true, query = OperatorQuery }),
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var createResp = await _client!.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var id = (await createResp.Content.ReadFromJsonAsync<CreatedDataset>())!.Id;

        // Run a single-condition filter and return the matching emp_names, comma-joined in
        // emp_id order — concise enough to assert each operator inline.
        async Task<string> NamesCsv(string column, string op, object? value)
        {
            var filters = new
            {
                id = "root",
                kind = "group",
                combinator = "AND",
                items = new object[]
                {
                    new { id = "c", kind = "condition", tableName = "", columnName = column, @operator = op, value },
                },
            };
            using var resp = await PostRowsAsync(token, id, new
            {
                filters,
                sort = new[] { new { column = "emp_id", direction = "asc" } },
                page = 1,
                pageSize = 25,
            });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var page = await resp.Content.ReadFromJsonAsync<RowsPage>();
            return string.Join(",", page!.Data.Select(r => r["emp_name"].GetString()));
        }

        // Comparison operators (numeric column, value coerced to the column type).
        Assert.Equal("Bob", await NamesCsv("salary", "=", "90"));
        Assert.Equal("Alice,Carol,Dave,Eve", await NamesCsv("salary", "!=", "90"));
        Assert.Equal("Dave,Eve", await NamesCsv("salary", "<", "80"));
        Assert.Equal("Carol,Dave,Eve", await NamesCsv("salary", "<=", "80"));
        Assert.Equal("Alice,Bob", await NamesCsv("salary", ">", "80"));
        Assert.Equal("Alice,Bob,Carol", await NamesCsv("salary", ">=", "80"));

        // Null operators (value ignored).
        Assert.Equal("Carol,Eve", await NamesCsv("tag", "IS NULL", null));
        Assert.Equal("Alice,Bob,Dave", await NamesCsv("tag", "IS NOT NULL", null));

        // LIKE is case-sensitive contains; ILIKE is case-insensitive contains.
        Assert.Equal("Carol,Dave", await NamesCsv("emp_name", "LIKE", "a"));
        Assert.Equal("Alice,Carol,Dave", await NamesCsv("emp_name", "ILIKE", "A"));

        // Set + range operators (arrays).
        Assert.Equal("Alice,Bob,Dave", await NamesCsv("tag", "IN", TagInValues));
        Assert.Equal("Alice,Dave,Eve", await NamesCsv("salary", "NOT IN", SalaryNotInValues));
        Assert.Equal("Bob,Carol,Dave", await NamesCsv("salary", "BETWEEN", SalaryBetweenValues));
    }

    [Fact]
    public async Task Post_Rows_UnknownFilterColumn_Returns422()
    {
        var token = await LoginAsync("admin@example.com");
        var id = await CreateDatasetAsync(token, "rows_badcol");

        var filters = new
        {
            id = "root",
            kind = "group",
            combinator = "AND",
            items = new object[]
            {
                new { id = "c1", kind = "condition", tableName = "", columnName = "does_not_exist", @operator = "=", value = "x" },
            },
        };

        using var response = await PostRowsAsync(token, id, new { filters, page = 1, pageSize = 25 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Post_Chart_CountByCategory()
    {
        var token = await LoginAsync("admin@example.com");
        var id = await CreateDatasetAsync(token, "chart_count");

        using var response = await PostChartAsync(token, id, new
        {
            categoryColumn = "dept",
            aggregate = "count",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await response.Content.ReadFromJsonAsync<ChartData>();
        Assert.NotNull(data);
        var byCat = data!.Points.ToDictionary(p => p.Category, p => p.Value);
        Assert.Equal(2d, byCat["Eng"]);
        Assert.Equal(2d, byCat["Sales"]);
        Assert.Equal(1d, byCat["Ops"]);
    }

    [Fact]
    public async Task Post_Chart_SumByCategory()
    {
        var token = await LoginAsync("admin@example.com");
        var id = await CreateDatasetAsync(token, "chart_sum");

        using var response = await PostChartAsync(token, id, new
        {
            categoryColumn = "dept",
            valueColumn = "salary",
            aggregate = "sum",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await response.Content.ReadFromJsonAsync<ChartData>();
        Assert.NotNull(data);
        var byCat = data!.Points.ToDictionary(p => p.Category, p => p.Value);
        Assert.Equal(190d, byCat["Eng"]);   // 100 + 90
        Assert.Equal(150d, byCat["Sales"]); // 80 + 70
        Assert.Equal(60d, byCat["Ops"]);
        // Largest categories first.
        Assert.Equal("Eng", data.Points[0].Category);
    }

    [Fact]
    public async Task Post_Chart_SumOnTextColumn_Returns422()
    {
        var token = await LoginAsync("admin@example.com");
        var id = await CreateDatasetAsync(token, "chart_badsum");

        using var response = await PostChartAsync(token, id, new
        {
            categoryColumn = "dept",
            valueColumn = "emp_name", // text → SUM invalid
            aggregate = "sum",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Post_Chart_UnknownCategory_Returns422()
    {
        var token = await LoginAsync("admin@example.com");
        var id = await CreateDatasetAsync(token, "chart_badcat");

        using var response = await PostChartAsync(token, id, new
        {
            categoryColumn = "does_not_exist",
            aggregate = "count",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Post_Rows_UnknownDataset_Returns404()
    {
        var token = await LoginAsync("admin@example.com");
        using var response = await PostRowsAsync(token, Guid.NewGuid(), new { page = 1, pageSize = 25 });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_Rows_IsAuthOnly_ViewerCanRead()
    {
        var adminToken = await LoginAsync("admin@example.com");
        var id = await CreateDatasetAsync(adminToken, "rows_viewer");

        var viewerToken = await LoginAsync("viewer@example.com");
        using var response = await PostRowsAsync(viewerToken, id, new { page = 1, pageSize = 25 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Post_RowsExport_Csv_ReturnsFile()
    {
        var token = await LoginAsync("admin@example.com");
        var id = await CreateDatasetAsync(token, "rows_export");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/datasets/{id}/rows/export?format=csv")
        {
            Content = JsonContent.Create(new
            {
                sort = new[] { new { column = "emp_id", direction = "asc" } },
                columns = new[]
                {
                    new { column = "emp_name", header = "Name" },
                    new { column = "salary", header = "Pay" },
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        var csv = await response.Content.ReadAsStringAsync();
        Assert.Contains("Name", csv, StringComparison.Ordinal);
        Assert.Contains("Pay", csv, StringComparison.Ordinal);
        Assert.Contains("Alice", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_RowsExport_MissingFormat_Returns422()
    {
        var token = await LoginAsync("admin@example.com");
        var id = await CreateDatasetAsync(token, "rows_export_badformat");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/datasets/{id}/rows/export")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ---------- query-type datasets (no backing VIEW) ----------

    [Fact]
    public async Task Post_Rows_QueryTypeDataset_HasNoView_AndReturnsRows()
    {
        var token = await LoginAsync("admin@example.com");
        var id = await CreateQueryDatasetAsync(token, "rows_query_type", DatasetQuery);

        // A "query"-type dataset is stored as a record only — no backing VIEW is created.
        Assert.Equal(0, await CountDatasetViewsAsync("rows_query_type"));

        using var response = await PostRowsAsync(token, id, new
        {
            filters = new
            {
                id = "root",
                kind = "group",
                combinator = "AND",
                items = new object[]
                {
                    new { id = "c1", kind = "condition", tableName = "", columnName = "salary", @operator = ">=", value = "80" },
                },
            },
            sort = new[] { new { column = "emp_id", direction = "asc" } },
            page = 1,
            pageSize = 25,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<RowsPage>();
        Assert.NotNull(page);
        Assert.Equal(3, page!.Total); // Alice(100), Bob(90), Carol(80) — the CTE source filters live.
        Assert.Equal(AllColumns, page.Columns);
    }

    [Fact]
    public async Task Post_Rows_ParameterizedDataset_ResolvesParametersAndFilters()
    {
        var token = await LoginAsync("admin@example.com");
        // Integer placeholder ({_min}) + quoted text placeholder ('{_name}') — both bound as
        // `unknown` so they coerce to integer / text like inline literals would.
        const string q =
            "SELECT emp_id, emp_name FROM (VALUES " +
            "(1,'Alice',100),(2,'Bob',90),(3,'Carol',80)) AS t(emp_id, emp_name, salary) " +
            "WHERE salary >= {_min} AND emp_name = '{_name}'";
        var id = await CreateQueryDatasetAsync(token, "rows_parameterized", q);

        using var response = await PostRowsAsync(token, id, new
        {
            queryParameters = "{\"_min\":80,\"_name\":\"Carol\"}",
            sort = new[] { new { column = "emp_id", direction = "asc" } },
            page = 1,
            pageSize = 25,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<RowsPage>();
        Assert.NotNull(page);
        var row = Assert.Single(page!.Data);
        Assert.Equal("Carol", row["emp_name"].GetString());
    }

    [Fact]
    public async Task Post_Rows_ParameterizedDataset_MissingParameters_Returns422()
    {
        var token = await LoginAsync("admin@example.com");
        const string q =
            "SELECT emp_id FROM (VALUES (1,100),(2,90)) AS t(emp_id, salary) WHERE salary >= {_min}";
        var id = await CreateQueryDatasetAsync(token, "rows_param_missing", q);

        // No queryParameters supplied → the placeholder can't be resolved → 422.
        using var response = await PostRowsAsync(token, id, new { page = 1, pageSize = 25 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ---------- helpers ----------

    private async Task<HttpResponseMessage> PostRowsAsync(string token, Guid id, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/datasets/{id}/rows")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PostChartAsync(string token, Guid id, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/datasets/{id}/chart")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private async Task<Guid> CreateDatasetAsync(string token, string datasetName)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/datasets")
        {
            Content = JsonContent.Create(new { datasetName, isCustomQuery = true, query = DatasetQuery }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<CreatedDataset>();
        Assert.NotNull(dto);
        return dto!.Id;
    }

    private async Task<Guid> CreateQueryDatasetAsync(string token, string datasetName, string query)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/datasets")
        {
            Content = JsonContent.Create(new
            {
                datasetName,
                isCustomQuery = true,
                queryType = "query",
                query,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<CreatedDataset>();
        Assert.NotNull(dto);
        return dto!.Id;
    }

    private async Task<int> CountDatasetViewsAsync(string name)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        return await db.Database
            .SqlQueryRaw<int>(
                "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.views " +
                "WHERE table_schema = 'datasets' AND table_name = {0}", name)
            .FirstAsync();
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

        db.UserRoles.Add(new UserRole { UserId = admin.Id, RoleId = PlatformAdminRoleId, CreatedAt = DateTimeOffset.UtcNow });
        db.UserRoles.Add(new UserRole { UserId = viewer.Id, RoleId = ViewerRoleId, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record RowsPage(
        List<Dictionary<string, JsonElement>> Data,
        long Total,
        int Page,
        int PageSize,
        int TotalPages,
        List<string> Columns);

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record ChartData(string CategoryColumn, string? ValueColumn, string Aggregate, List<ChartPoint> Points);

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record ChartPoint(string Category, double Value);

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record CreatedDataset(Guid Id, string DatasetName);

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record LoginResponseDto(string AccessToken, string RefreshToken, int ExpiresIn);
}
