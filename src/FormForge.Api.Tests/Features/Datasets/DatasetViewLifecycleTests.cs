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

// Story 8.4 (FR-58 / AR-59) — end-to-end coverage of POST /api/datasets: the
// custom_dataset row + backing PostgreSQL VIEW are created atomically, the audit
// log records success/failure, and the error envelopes (409 DATASET_NAME_CONFLICT,
// 422 INVALID_QUERY) match the contract. Mirrors the PostgresFixture +
// WebApplicationFactory pattern from DatasetNameValidationTests / DatasetPermissionTests.
[Collection("DatasetIntegrationTests")]
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class DatasetViewLifecycleTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;
    private Guid _adminUserId;

    public DatasetViewLifecycleTests(PostgresFixture postgres) => _postgres = postgres;

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

        // Truncate dataset tables too, so prior test classes don't leak datasets/VIEWs.
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE custom_dataset, dataset_audit_log, " +
            "role_permissions, user_roles, roles, refresh_tokens, users " +
            "RESTART IDENTITY CASCADE;");

        // Drop any VIEWs left behind by previous runs of this class (TRUNCATE on
        // custom_dataset does not drop the schema-qualified VIEWs).
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

    // ---------- AC-1: create with query → 201, body correct, VIEW exists ----------

    [Fact]
    public async Task PostDataset_WithQuery_Returns201AndCreatesView()
    {
        var token = await LoginAsync();
        using var response = await PostAsync(token, new
        {
            datasetName = "lifecycle_test",
            isCustomQuery = true,
            query = "SELECT 1 AS n",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<DatasetResponseDto>();
        Assert.NotNull(dto);
        Assert.NotEqual(Guid.Empty, dto!.Id);
        Assert.Equal("lifecycle_test", dto.DatasetName);
        Assert.True(dto.IsCustomQuery);
        Assert.Equal("SELECT 1 AS n", dto.Query);
        Assert.Null(dto.BuilderState);
        Assert.Equal(1, dto.Version);
        Assert.NotEqual(default, dto.CreatedAt);
        Assert.Equal(_adminUserId, dto.CreatedBy);

        Assert.Equal($"/api/datasets/{dto.Id}", response.Headers.Location?.ToString());

        await using var conn = await OpenRawAsync();
        await using var viewCheck = new NpgsqlCommand(
            "SELECT COUNT(*) FROM information_schema.views " +
            "WHERE table_schema = 'datasets' AND table_name = 'lifecycle_test';", conn);
        Assert.Equal(1L, (long)(await viewCheck.ExecuteScalarAsync())!);
    }

    // ---------- AC-2: create without query → 201, query null, placeholder VIEW ----------

    [Fact]
    public async Task PostDataset_NoQuery_Returns201WithPlaceholderView()
    {
        var token = await LoginAsync();
        using var response = await PostAsync(token, new
        {
            datasetName = "builder_placeholder",
            isCustomQuery = false,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<DatasetResponseDto>();
        Assert.NotNull(dto);
        Assert.Null(dto!.Query);
        Assert.False(dto.IsCustomQuery);
        Assert.Equal(1, dto.Version);
        Assert.NotEqual(default, dto.CreatedAt);
        Assert.Equal(_adminUserId, dto.CreatedBy);

        await using var conn = await OpenRawAsync();
        await using var defCmd = new NpgsqlCommand(
            "SELECT pg_get_viewdef('datasets.builder_placeholder'::regclass, true);", conn);
        var def = (string?)await defCmd.ExecuteScalarAsync();
        Assert.NotNull(def);
        Assert.Contains("placeholder", def, StringComparison.Ordinal);
    }

    // ---------- AC-3: duplicate name → 409 DATASET_NAME_CONFLICT ----------

    [Fact]
    public async Task PostDataset_DuplicateName_Returns409Conflict()
    {
        var token = await LoginAsync();

        using (var first = await PostAsync(token, new { datasetName = "conflict_ds", isCustomQuery = true }))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        }

        using var second = await PostAsync(token, new { datasetName = "conflict_ds", isCustomQuery = true });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await second.Content.ReadAsStreamAsync());
        Assert.Equal("DATASET_NAME_CONFLICT", doc.RootElement.GetProperty("code").GetString());

        // Only one row exists for the name — the conflicting POST did not insert.
        await using var conn = await OpenRawAsync();
        await using var countCmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM custom_dataset WHERE dataset_name = 'conflict_ds';", conn);
        Assert.Equal(1L, (long)(await countCmd.ExecuteScalarAsync())!);
    }

    // ---------- AC-4: invalid SQL → 422 INVALID_QUERY, row rolled back ----------

    [Fact]
    public async Task PostDataset_InvalidSql_Returns422AndRollsBack()
    {
        var token = await LoginAsync();
        using var response = await PostAsync(token, new
        {
            datasetName = "bad_sql_ds",
            isCustomQuery = true,
            query = "NOT VALID SQL XYZ",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("INVALID_QUERY", doc.RootElement.GetProperty("code").GetString());
        Assert.True(doc.RootElement.TryGetProperty("detail", out var detail));
        Assert.False(string.IsNullOrWhiteSpace(detail.GetString()));

        await using var conn = await OpenRawAsync();
        await using var rowCmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM custom_dataset WHERE dataset_name = 'bad_sql_ds';", conn);
        Assert.Equal(0L, (long)(await rowCmd.ExecuteScalarAsync())!);

        await using var viewCmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM information_schema.views " +
            "WHERE table_schema = 'datasets' AND table_name = 'bad_sql_ds';", conn);
        Assert.Equal(0L, (long)(await viewCmd.ExecuteScalarAsync())!);
    }

    // ---------- AC-5: audit log success entry ----------

    [Fact]
    public async Task PostDataset_Success_WritesSucceededAuditRow()
    {
        var token = await LoginAsync();
        var knownCorrelationId = Ulid.NewUlid().ToString();
        using (var response = await PostAsync(token, new
        {
            datasetName = "audit_success_ds",
            isCustomQuery = true,
            query = "SELECT 1 AS n",
        }, knownCorrelationId))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        var entries = await GetAuditEntriesAsync("audit_success_ds");
        var entry = Assert.Single(entries);
        Assert.Equal("CREATE", entry.Operation);
        Assert.True(entry.Succeeded);
        Assert.Equal("audit_success_ds", entry.DatasetName);
        Assert.StartsWith(
            "CREATE VIEW datasets.\"audit_success_ds\"", entry.Ddl, StringComparison.Ordinal);
        Assert.Equal(_adminUserId, entry.ActorId);
        Assert.Equal(knownCorrelationId, entry.CorrelationId);
    }

    // ---------- AC-6: audit log failure entry ----------

    [Fact]
    public async Task PostDataset_DdlFailure_WritesFailedAuditRow()
    {
        // Story 8.8 (AR-61): syntactic garbage is now rejected by SqlSelectEnforcer
        // BEFORE any DDL/audit (AC-5), so this test uses a syntactically-valid SELECT
        // that passes the enforcer but fails at CREATE VIEW (the referenced table does
        // not exist → Postgres 42P01) to still exercise the DDL-failure → failed-audit-row
        // path of Story 8.4 (AC-6).
        var token = await LoginAsync();
        using (var response = await PostAsync(token, new
        {
            datasetName = "datasetname_failure",
            isCustomQuery = true,
            query = "SELECT * FROM nonexistent_table_xyz",
        }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }

        var entries = await GetAuditEntriesAsync("datasetname_failure");
        var entry = Assert.Single(entries);
        Assert.Equal("CREATE", entry.Operation);
        Assert.False(entry.Succeeded);
        Assert.Equal("datasetname_failure", entry.DatasetName);
        Assert.Contains(
            "CREATE VIEW datasets.\"datasetname_failure\"", entry.Ddl, StringComparison.Ordinal);
        Assert.Equal(_adminUserId, entry.ActorId);
    }

    // ---------- AC-6 note: no audit row for a name conflict (no DDL attempted) ----------

    [Fact]
    public async Task PostDataset_NameConflict_WritesNoExtraAuditRow()
    {
        var token = await LoginAsync();

        using (var first = await PostAsync(token, new { datasetName = "no_audit_conflict", isCustomQuery = true }))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        }

        using (var second = await PostAsync(token, new { datasetName = "no_audit_conflict", isCustomQuery = true }))
        {
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        }

        var entries = await GetAuditEntriesAsync("no_audit_conflict");
        Assert.Single(entries); // only the successful CREATE, not the conflict attempt
    }

    // ---------- Story 8.8 (AR-61) AC-2 / AC-4 / AC-5: enforcer rejects non-SELECT ----------

    [Fact]
    public async Task PostDataset_CustomQueryNonSelect_RejectedByEnforcer_WritesNoAuditRow()
    {
        // A parseable but non-SELECT statement (INSERT) is rejected at checkpoint (a),
        // before the connection/transaction opens. The server enforces independently of
        // the client (AC-4): 422 INVALID_QUERY, and NO dataset_audit_log row (AC-5).
        var token = await LoginAsync();
        using (var response = await PostAsync(token, new
        {
            datasetName = "enforcer_reject_ds",
            isCustomQuery = true,
            query = "INSERT INTO foo (id) VALUES (1)",
        }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            Assert.Equal("INVALID_QUERY", doc.RootElement.GetProperty("code").GetString());
        }

        Assert.Empty(await GetAuditEntriesAsync("enforcer_reject_ds"));
    }

    // ---------- helpers ----------

    private async Task<HttpResponseMessage> PostAsync(string token, object payload, string? correlationId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/datasets")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (correlationId is not null)
            request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
        return await _client!.SendAsync(request);
    }

    private async Task<NpgsqlConnection> OpenRawAsync()
    {
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private async Task<List<AuditRow>> GetAuditEntriesAsync(string datasetName)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        return await db.DatasetAuditLog
            .Where(a => a.DatasetName == datasetName)
            .Select(a => new AuditRow(a.Operation, a.Succeeded, a.Ddl, a.ActorId, a.DatasetName, a.CorrelationId))
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

    private sealed record AuditRow(string Operation, bool Succeeded, string? Ddl, Guid? ActorId, string DatasetName, string? CorrelationId);

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
