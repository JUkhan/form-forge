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

// Story 9.1 (FR-63 / AR-62) — end-to-end coverage of GET /api/datasets/catalog.
// The allowlist is configured via DatasetManager:AllowedTables (UseSetting). The test
// seeds two real public-schema tables (one allowlisted, one not) and lists a denylisted
// internal name in config to prove it is stripped (AR-57). Mirrors the PostgresFixture +
// WebApplicationFactory pattern from DatasetListGetTests.
[Collection("DatasetIntegrationTests")]
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class DatasetCatalogTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public DatasetCatalogTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:formforge", _postgres.ConnectionString);
                builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
                // AC-1: catalog_probe is a real, allowlisted public table.
                // AC-6 (defense-in-depth): custom_dataset is denylisted → stripped (AR-57).
                builder.UseSetting("DatasetManager:AllowedTables:0", "catalog_probe");
                builder.UseSetting("DatasetManager:AllowedTables:1", "custom_dataset");
            });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        await db.Database.MigrateAsync();

        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE custom_dataset, dataset_audit_log, " +
            "role_permissions, user_roles, roles, refresh_tokens, users " +
            "RESTART IDENTITY CASCADE;");

        // Seed two public-schema probe tables. catalog_probe is allowlisted (AC-1);
        // catalog_secret exists but is NOT allowlisted, proving AC-6.
        await db.Database.ExecuteSqlRawAsync(
            """
            DROP TABLE IF EXISTS public.catalog_probe CASCADE;
            DROP TABLE IF EXISTS public.catalog_secret CASCADE;
            CREATE TABLE public.catalog_probe (
                id   integer NOT NULL,
                name text    NOT NULL,
                note text
            );
            CREATE TABLE public.catalog_secret (
                id integer NOT NULL
            );
            """);

        await ReseedAdminRoleAsync(db);
        await SeedAdminUserAsync(db);
        await SeedViewerRoleAsync(db);
        await SeedViewerUserAsync(db);

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
    }

    public async Task DisposeAsync()
    {
        // Drop the probe tables so they don't leak into other dataset integration tests.
        if (_factory is not null)
        {
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
                await db.Database.ExecuteSqlRawAsync(
                    "DROP TABLE IF EXISTS public.catalog_probe CASCADE;" +
                    "DROP TABLE IF EXISTS public.catalog_secret CASCADE;");
            }

            _client?.Dispose();
            await _factory.DisposeAsync();
        }
        else
        {
            _client?.Dispose();
        }
    }

    // ---------- AC-1: catalog returns the allowlisted table with its columns ----------

    [Fact]
    public async Task GetCatalog_ReturnsAllowlistedTable_WithColumnCount()
    {
        var token = await LoginAsync();

        using var response = await GetAsync(token, "/api/datasets/catalog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var catalog = await response.Content.ReadFromJsonAsync<CatalogResponseDto>();
        Assert.NotNull(catalog);

        // The list carries names + column counts only (columns are fetched lazily).
        var probe = Assert.Single(catalog!.Tables, t => t.TableName == "catalog_probe");
        Assert.Equal(3, probe.ColumnCount);
    }

    // ---------- Lazy per-table columns: GET /catalog/{table} ----------

    [Fact]
    public async Task GetTableColumns_ReturnsColumnsAndTypes_ForAllowlistedTable()
    {
        var token = await LoginAsync();

        using var response = await GetAsync(token, "/api/datasets/catalog/catalog_probe");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<TableColumnsResponseDto>();
        Assert.NotNull(result);
        Assert.Equal("catalog_probe", result!.TableName);

        // Columns ordered by ordinal_position: id, name, note.
        Assert.Equal(3, result.Columns.Length);

        var id = result.Columns[0];
        Assert.Equal("id", id.ColumnName);
        Assert.Equal("integer", id.PgType);
        Assert.False(id.IsNullable);

        var name = result.Columns[1];
        Assert.Equal("name", name.ColumnName);
        Assert.Equal("text", name.PgType);
        Assert.False(name.IsNullable);

        var note = result.Columns[2];
        Assert.Equal("note", note.ColumnName);
        Assert.Equal("text", note.PgType);
        Assert.True(note.IsNullable);
    }

    [Fact]
    public async Task GetTableColumns_NotAllowlistedTable_Returns404()
    {
        var token = await LoginAsync();
        // catalog_secret exists but is not allowlisted → must not leak its columns.
        using var response = await GetAsync(token, "/api/datasets/catalog/catalog_secret");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetTableColumns_DenylistedInternalTable_Returns404()
    {
        var token = await LoginAsync();
        // custom_dataset is on the permanent denylist → never exposed, even by name.
        using var response = await GetAsync(token, "/api/datasets/catalog/custom_dataset");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- AC-6: non-allowlisted public table never appears ----------

    [Fact]
    public async Task GetCatalog_OmitsTableNotInAllowlist()
    {
        var token = await LoginAsync();

        using var response = await GetAsync(token, "/api/datasets/catalog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var catalog = await response.Content.ReadFromJsonAsync<CatalogResponseDto>();
        Assert.NotNull(catalog);

        // catalog_secret exists in public but was never allowlisted → must be absent.
        Assert.DoesNotContain(catalog!.Tables, t => t.TableName == "catalog_secret");
    }

    // ---------- AC-6 (AR-57): denylisted internal names are stripped from the allowlist ----------

    [Fact]
    public async Task GetCatalog_StripsDenylistedInternalTable()
    {
        var token = await LoginAsync();

        using var response = await GetAsync(token, "/api/datasets/catalog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var catalog = await response.Content.ReadFromJsonAsync<CatalogResponseDto>();
        Assert.NotNull(catalog);

        // custom_dataset was listed in config but is on the permanent denylist → stripped.
        Assert.DoesNotContain(catalog!.Tables, t => t.TableName == "custom_dataset");
    }

    // ---------- AC-1: endpoint requires authentication ----------

    [Fact]
    public async Task GetCatalog_WithoutToken_Returns401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/datasets/catalog");
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------- AC-1: endpoint requires dataset-management permission ----------

    [Fact]
    public async Task GetCatalog_AuthenticatedWithoutDatasetManagement_Returns403()
    {
        var token = await LoginAsync("viewer@example.com");

        using var response = await GetAsync(token, "/api/datasets/catalog");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- helpers ----------

    private async Task<HttpResponseMessage> GetAsync(string token, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private async Task<string> LoginAsync(string email = "admin@example.com", string password = "Password1!")
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email, password });
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

    private static async Task SeedViewerRoleAsync(FormForgeDbContext db)
    {
        var epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO roles (id, name, is_system, can_manage_datasets, created_at)" +
            " VALUES ({0}, 'viewer', false, false, {1}) ON CONFLICT (id) DO NOTHING",
            ViewerRoleId, epoch);
    }

    private static async Task SeedViewerUserAsync(FormForgeDbContext db)
    {
        var viewer = new User
        {
            Email = "viewer@example.com",
            DisplayName = "Viewer",
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

    private static async Task SeedAdminUserAsync(FormForgeDbContext db)
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
    }

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record CatalogResponseDto(CatalogTableItemDto[] Tables);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record CatalogTableItemDto(string TableName, int ColumnCount);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record TableColumnsResponseDto(string TableName, CatalogColumnItemDto[] Columns);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record CatalogColumnItemDto(string ColumnName, string PgType, bool IsNullable);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record LoginResponseDto(string AccessToken, string RefreshToken, int ExpiresIn);
}
