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
using Npgsql;

namespace FormForge.Api.Tests.Features.Datasets;

// Story 8.6 (FR-58 / AR-59) — end-to-end coverage of DELETE /api/datasets/{id}: the
// custom_dataset row + backing VIEW are removed atomically within one NpgsqlTransaction
// (DELETE row + DROP VIEW IF EXISTS), a missing id returns 404, and the audit log records
// the success with the deleted row's previous_values. Mirrors the PostgresFixture +
// WebApplicationFactory pattern from DatasetUpdateTests.
[Collection("DatasetIntegrationTests")]
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class DatasetDeleteTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;
    private Guid _adminUserId;

    public DatasetDeleteTests(PostgresFixture postgres) => _postgres = postgres;

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
        _adminUserId = await SeedAdminUserAsync(db);

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

    // ---------- AC-1: delete existing dataset → 204, row gone, VIEW gone ----------

    [Fact]
    public async Task DeleteDataset_Existing_Returns204AndRemovesRowAndView()
    {
        var token = await LoginAsync();
        var created = await CreateAsync(token, new
        {
            datasetName = "delete_me_ds",
            isCustomQuery = true,
            query = "SELECT 1 AS n",
        });

        // The backing VIEW must exist before the delete.
        Assert.True(await ViewExistsAsync("delete_me_ds"));

        using var response = await DeleteAsync(token, created.Id);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Row is gone.
        Assert.Equal(0, await RowCountAsync(created.Id));

        // VIEW is gone.
        Assert.False(await ViewExistsAsync("delete_me_ds"));
    }

    // ---------- AC-2: delete non-existent id → 404 ----------

    [Fact]
    public async Task DeleteDataset_NotFound_Returns404()
    {
        var token = await LoginAsync();

        // Seed an unrelated dataset so we can assert the table is otherwise untouched.
        await CreateAsync(token, new
        {
            datasetName = "untouched_ds",
            isCustomQuery = true,
            query = "SELECT 1 AS n",
        });

        using var response = await DeleteAsync(token, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // No rows were affected: the unrelated dataset is still present.
        Assert.Equal(1, await TotalRowCountAsync());
        Assert.True(await ViewExistsAsync("untouched_ds"));
    }

    // ---------- AC-3: audit log on success ----------

    [Fact]
    public async Task DeleteDataset_Success_WritesSucceededAuditRow()
    {
        var token = await LoginAsync();
        var created = await CreateAsync(token, new
        {
            datasetName = "audit_delete_ds",
            isCustomQuery = true,
            query = "SELECT 1 AS n",
        });

        using (var response = await DeleteAsync(token, created.Id))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var entry = Assert.Single(await GetAuditEntriesAsync("audit_delete_ds", "DELETE"));
        Assert.True(entry.Succeeded);
        Assert.Equal("DELETE", entry.Operation);
        Assert.Contains(
            "DROP VIEW IF EXISTS datasets.\"audit_delete_ds\"", entry.Ddl, StringComparison.Ordinal);
        Assert.Equal(_adminUserId, entry.ActorId);
        Assert.Equal("audit_delete_ds", entry.DatasetName);
        Assert.True(entry.Timestamp > DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.NotEqual(default, entry.Timestamp);

        // previous_values captures the deleted row snapshot; new_values is null (gone).
        Assert.NotNull(entry.PreviousValues);
        using var prev = JsonDocument.Parse(entry.PreviousValues!);
        Assert.Equal("audit_delete_ds", prev.RootElement.GetProperty("dataset_name").GetString());
        Assert.Equal(1, prev.RootElement.GetProperty("version").GetInt32());
        Assert.Null(entry.NewValues);
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

    private async Task<HttpResponseMessage> DeleteAsync(string token, Guid id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/datasets/{id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private async Task<NpgsqlConnection> OpenRawAsync()
    {
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private async Task<bool> ViewExistsAsync(string name)
    {
        await using var conn = await OpenRawAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM information_schema.views " +
            "WHERE table_schema = 'datasets' AND table_name = @name;", conn);
        cmd.Parameters.AddWithValue("name", name);
        return (long)(await cmd.ExecuteScalarAsync())! == 1L;
    }

    private async Task<int> RowCountAsync(Guid id)
    {
        await using var conn = await OpenRawAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM custom_dataset WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", id);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<int> TotalRowCountAsync()
    {
        await using var conn = await OpenRawAsync();
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM custom_dataset;", conn);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<List<AuditRow>> GetAuditEntriesAsync(string datasetName, string operation)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        return await db.DatasetAuditLog
            .Where(a => a.DatasetName == datasetName && a.Operation == operation)
            .Select(a => new AuditRow(
                a.Operation, a.Succeeded, a.Ddl, a.ActorId, a.DatasetName,
                a.CorrelationId, a.PreviousValues, a.NewValues, a.Timestamp))
            .ToListAsync();
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

    private sealed record AuditRow(
        string Operation, bool Succeeded, string? Ddl, Guid? ActorId, string DatasetName,
        string? CorrelationId, string? PreviousValues, string? NewValues,
        DateTimeOffset Timestamp);

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
