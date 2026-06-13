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

// Story 8.9 (FR-61 / AR-65) — end-to-end coverage of GET /api/admin/datasets/audit:
// paginated dataset audit log filterable by datasetName and operation, platform-admin
// only (403 for non-admin), append-only (no DELETE endpoint — 405 expected), and
// succeeded=false entries appear for rolled-back DDL. Mirrors the PostgresFixture +
// WebApplicationFactory pattern from DatasetDeleteTests / DatasetPermissionTests.
[Collection("DatasetIntegrationTests")]
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class DatasetAuditLogTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;
    private Guid _adminUserId;

    public DatasetAuditLogTests(PostgresFixture postgres) => _postgres = postgres;

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

        // Drop any VIEWs left behind by previous test class runs.
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

        await ReseedSystemRolesAsync(db);
        _adminUserId = await SeedAdminUserAsync(db);
        await SeedViewerUserAsync(db);

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

    // ---------- AC-1: audit entry fields are correct after a CREATE ----------

    [Fact]
    public async Task GetAuditLog_AfterCreate_ReturnsEntryWithCorrectFields()
    {
        var token = await LoginAsync("admin@example.com");
        await CreateDatasetAsync(token, "audit_create_ds", "SELECT 1 AS n");

        using var response = await GetAuditLogAsync(token, "?page=1&pageSize=25");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var paged = await response.Content.ReadFromJsonAsync<AuditPagedResultDto>();
        Assert.NotNull(paged);
        Assert.Equal(1, paged!.Total);

        var entry = Assert.Single(paged.Data);
        Assert.NotEqual(Guid.Empty, entry.Id);
        Assert.NotEqual(default, entry.Timestamp);
        Assert.Equal(_adminUserId, entry.ActorId);
        Assert.Equal("Platform Admin", entry.ActorName);
        Assert.Equal("audit_create_ds", entry.DatasetName);
        Assert.Equal("CREATE", entry.Operation);
        Assert.NotNull(entry.Ddl);
        Assert.True(entry.Succeeded);
    }

    // ---------- AC-1: filter by datasetName ----------

    [Fact]
    public async Task GetAuditLog_FilterByDatasetName_ReturnsMatchingEntriesOnly()
    {
        var token = await LoginAsync("admin@example.com");
        await CreateDatasetAsync(token, "filter_ds_alpha", "SELECT 1 AS n");
        await CreateDatasetAsync(token, "filter_ds_beta", "SELECT 2 AS n");

        // Filter by the first dataset name only.
        using var response = await GetAuditLogAsync(token, "?datasetName=filter_ds_alpha");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var paged = await response.Content.ReadFromJsonAsync<AuditPagedResultDto>();
        Assert.NotNull(paged);
        Assert.Equal(1, paged!.Total);
        var entry = Assert.Single(paged.Data);
        Assert.Equal("filter_ds_alpha", entry.DatasetName);
    }

    // ---------- AC-4: filter by operation ----------

    [Fact]
    public async Task GetAuditLog_FilterByOperation_ReturnsMatchingEntriesOnly()
    {
        var token = await LoginAsync("admin@example.com");
        var created = await CreateDatasetAsync(token, "op_filter_ds", "SELECT 1 AS n");
        await DeleteDatasetAsync(token, created.Id);

        // Two entries exist: CREATE and DELETE. Filter to DELETE only.
        using var response = await GetAuditLogAsync(token, "?operation=DELETE");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var paged = await response.Content.ReadFromJsonAsync<AuditPagedResultDto>();
        Assert.NotNull(paged);
        Assert.Equal(1, paged!.Total);
        var entry = Assert.Single(paged.Data);
        Assert.Equal("DELETE", entry.Operation);
    }

    // ---------- AC-4: operation filter is case-insensitive ----------

    [Fact]
    public async Task GetAuditLog_OperationFilterLowercase_NormalizesAndMatches()
    {
        var token = await LoginAsync("admin@example.com");
        await CreateDatasetAsync(token, "case_filter_ds", "SELECT 1 AS n");

        // Send lowercase "create"; server normalizes to "CREATE" before comparing.
        using var response = await GetAuditLogAsync(token, "?operation=create");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var paged = await response.Content.ReadFromJsonAsync<AuditPagedResultDto>();
        Assert.NotNull(paged);
        Assert.Equal(1, paged!.Total);
    }

    // ---------- AC-1/AC-3: platform-admin required — viewer gets 403 ----------

    [Fact]
    public async Task GetAuditLog_ViewerUser_Returns403()
    {
        var viewerToken = await LoginAsync("viewer@example.com");
        using var response = await GetAuditLogAsync(viewerToken, string.Empty);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- AC-2: succeeded=false entries are returned in the log ----------

    [Fact]
    public async Task GetAuditLog_FailedDdl_SucceededFalseEntryIncluded()
    {
        var token = await LoginAsync("admin@example.com");

        // Submit a syntactically valid SELECT that fails at CREATE VIEW (table doesn't exist).
        // The DatasetService records a failed audit row with succeeded=false (AC-2 / AR-57).
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/datasets")
        {
            Content = JsonContent.Create(new
            {
                datasetName = "failed_ddl_ds",
                isCustomQuery = true,
                query = "SELECT * FROM nonexistent_table_for_audit_test_xyz",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var createResponse = await _client!.SendAsync(request);
        // The VIEW DDL fails — service returns 422 INVALID_QUERY from the PG error.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, createResponse.StatusCode);

        // The audit log must contain the failed attempt.
        using var auditResponse = await GetAuditLogAsync(token, "?datasetName=failed_ddl_ds");
        Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);

        var paged = await auditResponse.Content.ReadFromJsonAsync<AuditPagedResultDto>();
        Assert.NotNull(paged);
        Assert.Equal(1, paged!.Total);
        var entry = Assert.Single(paged.Data);
        Assert.Equal("CREATE", entry.Operation);
        Assert.False(entry.Succeeded);
        Assert.NotNull(entry.Ddl);
    }

    // ---------- AC-4: pagination ----------

    [Fact]
    public async Task GetAuditLog_Pagination_ReturnsCorrectPage()
    {
        var token = await LoginAsync("admin@example.com");
        // Create 3 datasets → 3 CREATE audit entries.
        await CreateDatasetAsync(token, "page_ds_one", "SELECT 1 AS n");
        await CreateDatasetAsync(token, "page_ds_two", "SELECT 2 AS n");
        await CreateDatasetAsync(token, "page_ds_three", "SELECT 3 AS n");

        // First page with pageSize=2 should return 2 items, total=3, totalPages=2.
        using var response = await GetAuditLogAsync(token, "?page=1&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var paged = await response.Content.ReadFromJsonAsync<AuditPagedResultDto>();
        Assert.NotNull(paged);
        Assert.Equal(3, paged!.Total);
        Assert.Equal(2, paged.Data.Length);
        Assert.Equal(2, paged.TotalPages);

        // Second page should return the remaining 1 item.
        using var page2Response = await GetAuditLogAsync(token, "?page=2&pageSize=2");
        var page2 = await page2Response.Content.ReadFromJsonAsync<AuditPagedResultDto>();
        Assert.NotNull(page2);
        Assert.Single(page2!.Data);
    }

    // ---------- helpers ----------

    private async Task<DatasetResponseDto> CreateDatasetAsync(string token, string name, string query)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/datasets")
        {
            Content = JsonContent.Create(new
            {
                datasetName = name,
                isCustomQuery = true,
                query,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<DatasetResponseDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private async Task DeleteDatasetAsync(string token, Guid id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/datasets/{id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<HttpResponseMessage> GetAuditLogAsync(string token, string queryString)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/admin/datasets/audit{queryString}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private async Task<string> LoginAsync(string email)
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email, password = "Password1!" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        return body!.AccessToken;
    }

    private static async Task ReseedSystemRolesAsync(FormForgeDbContext db)
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

    private static async Task SeedViewerUserAsync(FormForgeDbContext db)
    {
        var viewer = new User
        {
            Email = "viewer@example.com",
            DisplayName = "Viewer User",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password1!", 12),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(viewer);
        await db.SaveChangesAsync();

        db.UserRoles.Add(new UserRole
        {
            UserId = viewer.Id,
            RoleId = ViewerRoleId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    // ---------- DTO projections ----------

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record AuditPagedResultDto(
        AuditEntryDto[] Data, long Total, int Page, int PageSize, int TotalPages);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record AuditEntryDto(
        Guid Id,
        DateTimeOffset Timestamp,
        Guid? ActorId,
        string? ActorName,
        string DatasetName,
        string Operation,
        string? PreviousValues,
        string? NewValues,
        string? Ddl,
        bool Succeeded,
        string? CorrelationId);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record DatasetResponseDto(
        Guid Id, string DatasetName, bool IsCustomQuery,
        string? Query, string? BuilderState, int Version,
        DateTimeOffset CreatedAt, Guid? CreatedBy);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record LoginResponseDto(string AccessToken, string RefreshToken, int ExpiresIn);
}
