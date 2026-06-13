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

// Dynamic-allowlist enhancement (2026-06-05) — when DatasetManager:AllowedTables is NOT
// configured, the catalog is discovered from the database: every public base table is
// exposed EXCEPT framework/internal tables (AR-57). This proves a (provisioned-style)
// user table appears automatically while internal tables never do. Counterpart to
// DatasetCatalogTests, which covers the optional config restrict-list path.
[Collection("DatasetIntegrationTests")]
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class DatasetCatalogDynamicTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public DatasetCatalogDynamicTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:formforge", _postgres.ConnectionString);
                builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
                // Intentionally NO DatasetManager:AllowedTables — exercise dynamic discovery.
            });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        await db.Database.MigrateAsync();

        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE custom_dataset, dataset_audit_log, " +
            "role_permissions, user_roles, roles, refresh_tokens, users " +
            "RESTART IDENTITY CASCADE;");

        // A non-system public base table stands in for a Designer-provisioned table.
        await db.Database.ExecuteSqlRawAsync(
            """
            DROP TABLE IF EXISTS public.dyn_probe CASCADE;
            CREATE TABLE public.dyn_probe (
                id   integer NOT NULL,
                name text    NOT NULL
            );
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
        if (_factory is not null)
        {
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
                await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS public.dyn_probe CASCADE;");
            }

            _client?.Dispose();
            await _factory.DisposeAsync();
        }
        else
        {
            _client?.Dispose();
        }
    }

    // ---------- Dynamic discovery: a non-system base table appears without config ----------

    [Fact]
    public async Task GetCatalog_WithNoConfig_DiscoversNonSystemTable()
    {
        var token = await LoginAsync();

        using var response = await GetAsync(token, "/api/datasets/catalog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var catalog = await response.Content.ReadFromJsonAsync<CatalogResponseDto>();
        Assert.NotNull(catalog);

        var probe = Assert.Single(catalog!.Tables, t => t.TableName == "dyn_probe");
        Assert.Equal(2, probe.ColumnCount);
    }

    // ---------- Framework/internal tables are never exposed (AR-57) ----------

    [Theory]
    [InlineData("custom_dataset")]
    [InlineData("role_permissions")]
    [InlineData("component_schema_versions")]
    [InlineData("__EFMigrationsHistory")]
    [InlineData("DataProtectionKeys")]
    public async Task GetCatalog_WithNoConfig_ExcludesSystemTable(string systemTable)
    {
        var token = await LoginAsync();

        using var response = await GetAsync(token, "/api/datasets/catalog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var catalog = await response.Content.ReadFromJsonAsync<CatalogResponseDto>();
        Assert.NotNull(catalog);

        Assert.DoesNotContain(catalog!.Tables, t => t.TableName == systemTable);
    }

    // ---------- users is exposed for joins, but only its safe columns ----------

    [Fact]
    public async Task GetTableColumns_UsersTable_ExposesOnlySafeColumns()
    {
        // `users` is intentionally NOT a system table, so it's discoverable as a join source
        // (a record's created_by/updated_by -> display name / email). But its sensitive
        // columns (password_hash, mfa_secret_protected, theme_preference, …) must never reach
        // the palette — only DatasetAllowlist.RestrictedColumns["users"] is surfaced, mirroring
        // the column-level DB grant to formforge_preview.
        var token = await LoginAsync();

        using var response = await GetAsync(token, "/api/datasets/catalog/users");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<TableColumnsResponseDto>();
        Assert.NotNull(result);
        Assert.Equal("users", result!.TableName);

        // Exactly the four allowlisted columns (the table physically has ten).
        Assert.Equal(4, result.Columns.Length);
        Assert.Contains(result.Columns, c => c.ColumnName == "id");
        Assert.Contains(result.Columns, c => c.ColumnName == "display_name");
        Assert.Contains(result.Columns, c => c.ColumnName == "email");
        Assert.Contains(result.Columns, c => c.ColumnName == "is_active");
        Assert.DoesNotContain(result.Columns, c => c.ColumnName == "password_hash");
        Assert.DoesNotContain(result.Columns, c => c.ColumnName == "mfa_secret_protected");
    }

    [Fact]
    public async Task GetCatalog_UsersTable_ReportsRestrictedColumnCount()
    {
        // The catalog list's ColumnCount for `users` reflects the exposed (allowlisted)
        // columns, not the physical total — consistent with GetTableColumns above.
        var token = await LoginAsync();

        using var response = await GetAsync(token, "/api/datasets/catalog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var catalog = await response.Content.ReadFromJsonAsync<CatalogResponseDto>();
        Assert.NotNull(catalog);

        var users = Assert.Single(catalog!.Tables, t => t.TableName == "users");
        Assert.Equal(4, users.ColumnCount);
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
