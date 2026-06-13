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

// Story 8.7 (FR-62 / AR-65) — end-to-end coverage of GET /api/datasets (paginated list,
// newest first, with creator display name) and GET /api/datasets/{id} (full DTO; 404 when
// missing). Both endpoints are auth-only. Mirrors the PostgresFixture +
// WebApplicationFactory pattern from DatasetDeleteTests.
[Collection("DatasetIntegrationTests")]
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class DatasetListGetTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public DatasetListGetTests(PostgresFixture postgres) => _postgres = postgres;

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
            "TRUNCATE TABLE custom_dataset, dataset_audit_log, " +
            "role_permissions, user_roles, roles, refresh_tokens, users " +
            "RESTART IDENTITY CASCADE;");

        // Drop any VIEWs left behind by previous runs (TRUNCATE does not drop VIEWs).
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

        await ReseedAdminRoleAsync(db);
        await SeedAdminUserAsync(db);

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

    // ---------- AC-1: list returns seeded datasets ----------

    [Fact]
    public async Task ListDatasets_ReturnsSeededDatasets_WithPagingAndCreatorName()
    {
        var token = await LoginAsync();
        await CreateAsync(token, new
        {
            datasetName = "list_one_ds",
            isCustomQuery = true,
            query = "SELECT 1 AS n",
        });
        await CreateAsync(token, new
        {
            datasetName = "list_two_ds",
            isCustomQuery = false,
            query = "SELECT 2 AS n",
        });

        using var response = await GetAsync(token, "/api/datasets?page=1&pageSize=25");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var paged = await response.Content.ReadFromJsonAsync<PagedResultDto>();
        Assert.NotNull(paged);
        Assert.Equal(2, paged!.Total);
        Assert.Equal(2, paged.Data.Length);
        Assert.Equal(1, paged.Page);
        Assert.Equal(25, paged.PageSize);
        Assert.Equal(1, paged.TotalPages); // 2 rows / pageSize 25 → 1 page (AC-1: totalPages)

        foreach (var item in paged.Data)
        {
            Assert.NotEqual(Guid.Empty, item.Id);
            Assert.False(string.IsNullOrWhiteSpace(item.DatasetName));
            Assert.NotEqual(default, item.CreatedAt);
            Assert.Null(item.UpdatedAt); // never updated → null (AC-1: updatedAt)
            Assert.Equal("Platform Admin", item.CreatedByName);
        }

        // ordered by created_at DESC — most recently created first.
        Assert.Equal("list_two_ds", paged.Data[0].DatasetName);
        Assert.Equal("list_one_ds", paged.Data[1].DatasetName);
    }

    // ---------- AC-1: empty list when no datasets ----------

    [Fact]
    public async Task ListDatasets_NoDatasets_ReturnsEmptyPage()
    {
        var token = await LoginAsync();

        using var response = await GetAsync(token, "/api/datasets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var paged = await response.Content.ReadFromJsonAsync<PagedResultDto>();
        Assert.NotNull(paged);
        Assert.Equal(0, paged!.Total);
        Assert.Empty(paged.Data);
        // AC-1: defaults applied when page/pageSize omitted from the query string.
        Assert.Equal(1, paged.Page);
        Assert.Equal(25, paged.PageSize);
    }

    // ---------- search: filters the list by dataset_name (case-insensitive) ----------

    [Fact]
    public async Task ListDatasets_Search_FiltersByNameCaseInsensitive()
    {
        var token = await LoginAsync();
        await CreateAsync(token, new { datasetName = "alpha_sales_ds", isCustomQuery = true, query = "SELECT 1 AS n" });
        await CreateAsync(token, new { datasetName = "beta_orders_ds", isCustomQuery = true, query = "SELECT 1 AS n" });

        using var response = await GetAsync(token, "/api/datasets?search=ALPHA");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var paged = await response.Content.ReadFromJsonAsync<PagedResultDto>();
        Assert.NotNull(paged);
        Assert.Equal(1, paged!.Total); // count honors the filter, not just the page slice
        var item = Assert.Single(paged.Data);
        Assert.Equal("alpha_sales_ds", item.DatasetName);
    }

    // ---------- sort: whitelisted single-column "field:dir" ----------

    [Fact]
    public async Task ListDatasets_SortByNameAsc_OrdersAlphabetically()
    {
        var token = await LoginAsync();
        // Create out of alphabetical order so the default (created_at DESC) differs from name asc.
        await CreateAsync(token, new { datasetName = "ds_charlie", isCustomQuery = true, query = "SELECT 1 AS n" });
        await CreateAsync(token, new { datasetName = "ds_alpha", isCustomQuery = true, query = "SELECT 1 AS n" });
        await CreateAsync(token, new { datasetName = "ds_bravo", isCustomQuery = true, query = "SELECT 1 AS n" });

        using var response = await GetAsync(token, "/api/datasets?sort=name:asc");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var paged = await response.Content.ReadFromJsonAsync<PagedResultDto>();
        Assert.NotNull(paged);
        Assert.Equal(3, paged!.Data.Length);
        Assert.Equal("ds_alpha", paged.Data[0].DatasetName);
        Assert.Equal("ds_bravo", paged.Data[1].DatasetName);
        Assert.Equal("ds_charlie", paged.Data[2].DatasetName);
    }

    [Fact]
    public async Task ListDatasets_UnknownSort_FallsBackToNewestFirst()
    {
        var token = await LoginAsync();
        await CreateAsync(token, new { datasetName = "fallback_one_ds", isCustomQuery = true, query = "SELECT 1 AS n" });
        await CreateAsync(token, new { datasetName = "fallback_two_ds", isCustomQuery = true, query = "SELECT 1 AS n" });

        // A bogus/unwhitelisted sort field must not error and must keep the legacy default order.
        using var response = await GetAsync(token, "/api/datasets?sort=evil;DROP:asc");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var paged = await response.Content.ReadFromJsonAsync<PagedResultDto>();
        Assert.NotNull(paged);
        Assert.Equal("fallback_two_ds", paged!.Data[0].DatasetName); // newest first
        Assert.Equal("fallback_one_ds", paged.Data[1].DatasetName);
    }

    // ---------- AC-2: get by id returns full DTO ----------

    [Fact]
    public async Task GetDataset_Existing_ReturnsFullDto()
    {
        var token = await LoginAsync();
        var created = await CreateAsync(token, new
        {
            datasetName = "get_one_ds",
            isCustomQuery = true,
            query = "SELECT 1 AS n",
        });

        using var response = await GetAsync(token, $"/api/datasets/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<DatasetFullDto>();
        Assert.NotNull(dto);
        Assert.Equal(created.Id, dto!.Id);
        Assert.Equal("get_one_ds", dto.DatasetName);
        Assert.True(dto.IsCustomQuery);
        Assert.Equal("SELECT 1 AS n", dto.Query);
        Assert.Null(dto.BuilderState); // custom-query dataset → no builder state (AC-2: builderState)
        Assert.Equal(1, dto.Version);
    }

    // ---------- AC-3: get by id not found → 404 ----------

    [Fact]
    public async Task GetDataset_NotFound_Returns404()
    {
        var token = await LoginAsync();

        using var response = await GetAsync(token, $"/api/datasets/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- AC-1 (AR-21): pageSize is clamped to a max of 100 ----------

    [Fact]
    public async Task ListDatasets_PageSizeAboveMax_ClampedTo100()
    {
        var token = await LoginAsync();

        using var response = await GetAsync(token, "/api/datasets?pageSize=500");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var paged = await response.Content.ReadFromJsonAsync<PagedResultDto>();
        Assert.NotNull(paged);
        // AR-21: pageSize is clamped server-side to 100; the response echoes the clamped value.
        Assert.Equal(100, paged!.PageSize);
    }

    // ---------- AC-1: creator deleted (created_by SET NULL) → createdByName null ----------

    [Fact]
    public async Task ListDatasets_CreatorRemoved_CreatedByNameIsNull()
    {
        var token = await LoginAsync();
        await CreateAsync(token, new
        {
            datasetName = "orphan_ds",
            isCustomQuery = true,
            query = "SELECT 1 AS n",
        });

        // Simulate the SET NULL FK outcome (creator's user row deleted) directly on the row,
        // so the LEFT JOIN yields a null display name without disturbing the auth token.
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            await db.Database.ExecuteSqlRawAsync("UPDATE custom_dataset SET created_by = NULL;");
        }

        using var response = await GetAsync(token, "/api/datasets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var paged = await response.Content.ReadFromJsonAsync<PagedResultDto>();
        Assert.NotNull(paged);
        var item = Assert.Single(paged!.Data);
        Assert.Null(item.CreatedByName);
    }

    // ---------- helpers ----------

    private async Task<DatasetResponseDto> CreateAsync(string token, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/datasets")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<DatasetResponseDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private async Task<HttpResponseMessage> GetAsync(string token, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private async Task<string> LoginAsync()
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email = "admin@example.com", password = "Password1!" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        return body!.AccessToken;
    }

    private static async Task ReseedAdminRoleAsync(FormForgeDbContext db)
    {
        var epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO roles (id, name, is_system, can_manage_datasets, created_at)" +
            " VALUES ({0}, 'platform-admin', true, true, {1}) ON CONFLICT (id) DO NOTHING",
            PlatformAdminRoleId, epoch);
    }

    private static async Task<Guid> SeedAdminUserAsync(FormForgeDbContext db)
    {
        var admin = new User
        {
            Email = "admin@example.com",
            DisplayName = "Platform Admin",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password1!", 12),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        db.UserRoles.Add(new UserRole
        {
            UserId = admin.Id,
            RoleId = PlatformAdminRoleId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        return admin.Id;
    }

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record PagedResultDto(
        DatasetSummaryItemDto[] Data, long Total, int Page, int PageSize, int TotalPages);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record DatasetSummaryItemDto(
        Guid Id, string DatasetName, bool IsCustomQuery,
        DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, string? CreatedByName);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record DatasetFullDto(
        Guid Id, string DatasetName, bool IsCustomQuery,
        string? Query, string? BuilderState, int Version,
        DateTimeOffset CreatedAt, Guid? CreatedBy);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record DatasetResponseDto(
        Guid Id,
        string DatasetName,
        bool IsCustomQuery,
        string? Query,
        string? BuilderState,
        int Version,
        DateTimeOffset CreatedAt,
        Guid? CreatedBy);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record LoginResponseDto(string AccessToken, string RefreshToken, int ExpiresIn);
}
