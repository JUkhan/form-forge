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

// Story 8.5 (FR-58 / FR-59 / AR-59 / AR-60) — end-to-end coverage of PUT
// /api/datasets/{id}: the custom_dataset row + backing VIEW are updated atomically
// (CREATE OR REPLACE / ALTER RENAME), optimistic concurrency returns 409 on version
// mismatch, name uniqueness returns 409, VIEW DDL failure returns 422 and rolls back,
// and the audit log records the success/failure with previous/new values. Mirrors the
// PostgresFixture + WebApplicationFactory pattern from DatasetViewLifecycleTests.
[Collection("DatasetIntegrationTests")]
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class DatasetUpdateTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;
    private Guid _adminUserId;

    public DatasetUpdateTests(PostgresFixture postgres) => _postgres = postgres;

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

    // ---------- AC-1: version mismatch → 409 DATASET_CONCURRENCY_CONFLICT ----------

    [Fact]
    public async Task PutDataset_VersionMismatch_Returns409Concurrency()
    {
        var token = await LoginAsync();
        var created = await CreateAsync(token, new
        {
            datasetName = "concurrency_test",
            isCustomQuery = true,
            query = "SELECT 1 AS n",
        });

        using var response = await PutAsync(token, created.Id, new
        {
            version = 99,
            query = "SELECT 1 AS n",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("DATASET_CONCURRENCY_CONFLICT", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("currentVersion").GetInt32());

        Assert.Equal(1, await GetVersionAsync(created.Id));
    }

    // ---------- AC-2: edit same name + query change → 200, VIEW redefined ----------

    [Fact]
    public async Task PutDataset_SameNameQueryChange_Returns200AndUpdatesView()
    {
        var token = await LoginAsync();
        var created = await CreateAsync(token, new
        {
            datasetName = "edit_same_name",
            isCustomQuery = true,
            query = "SELECT 1 AS n",
        });

        // A same-name edit that changes only the query value. (Since the Story 8.10
        // DROP + CREATE fix, the output column set may also change freely — see
        // PutDataset_NarrowsColumnSet_Returns200.)
        using var response = await PutAsync(token, created.Id, new
        {
            version = 1,
            datasetName = "edit_same_name",
            query = "SELECT 2 AS n",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<DatasetResponseDto>();
        Assert.NotNull(dto);
        Assert.Equal("edit_same_name", dto!.DatasetName);
        Assert.Equal("SELECT 2 AS n", dto.Query);
        Assert.Equal(2, dto.Version);

        await using var conn = await OpenRawAsync();
        await using var defCmd = new NpgsqlCommand(
            "SELECT pg_get_viewdef('datasets.edit_same_name'::regclass, true);", conn);
        var def = (string?)await defCmd.ExecuteScalarAsync();
        Assert.NotNull(def);
        Assert.Contains("2", def, StringComparison.Ordinal);

        await using var rowCmd = new NpgsqlCommand(
            "SELECT version, updated_at, updated_by FROM custom_dataset WHERE id = @id;", conn);
        rowCmd.Parameters.AddWithValue("id", created.Id);
        await using var reader = await rowCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.False(await reader.IsDBNullAsync(1));
        Assert.Equal(_adminUserId, reader.GetGuid(2));
    }

    // ---------- Story 8.10 regression: edit may drop/reorder/rename columns ----------

    // Reproduces the reported bug: a dataset created with a wide column set, then
    // edited to a narrower one, failed with 42P16 "cannot drop columns from view"
    // because the redefinition used CREATE OR REPLACE VIEW. The DROP + CREATE fix lets
    // the output column set change freely on edit.
    [Fact]
    public async Task PutDataset_NarrowsColumnSet_Returns200()
    {
        var token = await LoginAsync();
        var created = await CreateAsync(token, new
        {
            datasetName = "col_shrink_ds",
            isCustomQuery = true,
            query = "SELECT 1 AS a, 2 AS b, 3 AS c",
        });

        using var response = await PutAsync(token, created.Id, new
        {
            version = 1,
            query = "SELECT 1 AS a",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<DatasetResponseDto>();
        Assert.NotNull(dto);
        Assert.Equal("SELECT 1 AS a", dto!.Query);
        Assert.Equal(2, dto.Version);

        // The redefined view must expose exactly one column (a) — the dropped b/c
        // would have triggered 42P16 under CREATE OR REPLACE.
        await using var conn = await OpenRawAsync();
        await using var colCmd = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.columns " +
            "WHERE table_schema = 'datasets' AND table_name = 'col_shrink_ds';", conn);
        var colCount = (long)(await colCmd.ExecuteScalarAsync())!;
        Assert.Equal(1, colCount);
    }

    // ---------- AC-3: rename only → 200, old VIEW gone, new VIEW exists ----------

    [Fact]
    public async Task PutDataset_RenameOnly_Returns200AndRenamesView()
    {
        var token = await LoginAsync();
        var created = await CreateAsync(token, new
        {
            datasetName = "original_name",
            isCustomQuery = true,
            query = "SELECT 1 AS n",
        });

        using var response = await PutAsync(token, created.Id, new
        {
            version = 1,
            datasetName = "renamed_name",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<DatasetResponseDto>();
        Assert.NotNull(dto);
        Assert.Equal("renamed_name", dto!.DatasetName);
        Assert.Equal(2, dto.Version);

        Assert.False(await ViewExistsAsync("original_name"));
        Assert.True(await ViewExistsAsync("renamed_name"));

        // AC-3: view body must be unchanged after a rename-only (no REPLACE issued).
        await using var conn = await OpenRawAsync();
        await using var defCmd = new NpgsqlCommand(
            "SELECT pg_get_viewdef('datasets.renamed_name'::regclass, true);", conn);
        var def = (string?)await defCmd.ExecuteScalarAsync();
        Assert.NotNull(def);
        Assert.Contains("1 AS n", def, StringComparison.Ordinal);
    }

    // ---------- AC-4: rename + query change → 200, new name + new definition ----------

    [Fact]
    public async Task PutDataset_RenameAndQueryChange_Returns200AndRedefinesUnderNewName()
    {
        var token = await LoginAsync();
        var created = await CreateAsync(token, new
        {
            datasetName = "before_rename",
            isCustomQuery = true,
            query = "SELECT 1 AS z",
        });

        // Rename (ALTER VIEW) then redefine (DROP + CREATE) in one tx.
        using var response = await PutAsync(token, created.Id, new
        {
            version = 1,
            datasetName = "after_rename",
            query = "SELECT 99 AS z",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<DatasetResponseDto>();
        Assert.NotNull(dto);
        Assert.Equal("after_rename", dto!.DatasetName);
        Assert.Equal("SELECT 99 AS z", dto.Query);
        Assert.Equal(2, dto.Version);

        Assert.False(await ViewExistsAsync("before_rename"));
        Assert.True(await ViewExistsAsync("after_rename"));

        await using var conn = await OpenRawAsync();
        await using var defCmd = new NpgsqlCommand(
            "SELECT pg_get_viewdef('datasets.after_rename'::regclass, true);", conn);
        var def = (string?)await defCmd.ExecuteScalarAsync();
        Assert.NotNull(def);
        Assert.Contains("99", def, StringComparison.Ordinal);
    }

    // ---------- AC-5: null query → placeholder VIEW, NULL stored in row ----------

    [Fact]
    public async Task PutDataset_NullQuery_UsesPlaceholderViewAndStoresNullInRow()
    {
        var token = await LoginAsync();
        var created = await CreateAsync(token, new
        {
            datasetName = "null_query_test",
            isCustomQuery = true,
            query = "SELECT 1 AS n",
        });

        // Sending query: null means "keep existing" per AC-6, so to exercise the
        // null/placeholder path we create a builder-mode dataset (query already null).
        var builderDs = await CreateAsync(token, new
        {
            datasetName = "builder_placeholder_test",
            isCustomQuery = false,
        });

        // PUT with null query (omitted field) on a dataset whose query IS null.
        using var response = await PutAsync(token, builderDs.Id, new
        {
            version = 1,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<DatasetResponseDto>();
        Assert.NotNull(dto);
        Assert.Null(dto!.Query);
        Assert.Equal(2, dto.Version);

        // VIEW must use the placeholder definition (AC-5).
        await using var conn = await OpenRawAsync();
        await using var defCmd = new NpgsqlCommand(
            "SELECT pg_get_viewdef('datasets.builder_placeholder_test'::regclass, true);", conn);
        var def = (string?)await defCmd.ExecuteScalarAsync();
        Assert.NotNull(def);
        Assert.Contains("placeholder", def, StringComparison.OrdinalIgnoreCase);

        // Row query column must remain NULL (AC-5).
        await using var rowCmd = new NpgsqlCommand(
            "SELECT query FROM custom_dataset WHERE id = @id;", conn);
        rowCmd.Parameters.AddWithValue("id", builderDs.Id);
        var storedQuery = await rowCmd.ExecuteScalarAsync();
        Assert.True(storedQuery is null or DBNull);
    }

    // ---------- AC-6: partial update (only query, no name) → name preserved ----------

    [Fact]
    public async Task PutDataset_PartialUpdate_PreservesExistingName()
    {
        var token = await LoginAsync();
        var created = await CreateAsync(token, new
        {
            datasetName = "partial_test",
            isCustomQuery = true,
            query = "SELECT 1 AS q",
        });

        // Only the query value changes; the column name "q" is kept (CREATE OR REPLACE
        // limitation), and dataset_name is omitted so it must be preserved.
        using var response = await PutAsync(token, created.Id, new
        {
            version = 1,
            query = "SELECT 7 AS q",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<DatasetResponseDto>();
        Assert.NotNull(dto);
        Assert.Equal("partial_test", dto!.DatasetName);
        Assert.Equal("SELECT 7 AS q", dto.Query);
        Assert.Equal(2, dto.Version);

        Assert.True(await ViewExistsAsync("partial_test"));
        await using var conn = await OpenRawAsync();
        await using var defCmd = new NpgsqlCommand(
            "SELECT pg_get_viewdef('datasets.partial_test'::regclass, true);", conn);
        var def = (string?)await defCmd.ExecuteScalarAsync();
        Assert.NotNull(def);
        Assert.Contains("7", def, StringComparison.Ordinal);
    }

    // ---------- AC-7: duplicate name → 409 DATASET_NAME_CONFLICT, row unchanged ----------

    [Fact]
    public async Task PutDataset_DuplicateName_Returns409AndLeavesRowUnchanged()
    {
        var token = await LoginAsync();
        await CreateAsync(token, new { datasetName = "alpha_ds", isCustomQuery = true, query = "SELECT 1 AS n" });
        var beta = await CreateAsync(token, new { datasetName = "beta_ds", isCustomQuery = true, query = "SELECT 1 AS n" });

        using var response = await PutAsync(token, beta.Id, new
        {
            version = 1,
            datasetName = "alpha_ds",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("DATASET_NAME_CONFLICT", doc.RootElement.GetProperty("code").GetString());

        await using var conn = await OpenRawAsync();
        await using var rowCmd = new NpgsqlCommand(
            "SELECT dataset_name, version FROM custom_dataset WHERE id = @id;", conn);
        rowCmd.Parameters.AddWithValue("id", beta.Id);
        await using var reader = await rowCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("beta_ds", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));

        Assert.True(await ViewExistsAsync("beta_ds"));

        // AC-7: no audit log entry must be written on a name conflict.
        var auditEntries = await GetAuditEntriesAsync("beta_ds", "UPDATE");
        Assert.Empty(auditEntries);
    }

    // ---------- AC-8: invalid SQL → 422 INVALID_QUERY, row rolled back ----------

    [Fact]
    public async Task PutDataset_InvalidSql_Returns422AndRollsBack()
    {
        var token = await LoginAsync();
        var created = await CreateAsync(token, new
        {
            datasetName = "rollback_test",
            isCustomQuery = true,
            query = "SELECT 1 AS n",
        });

        using var response = await PutAsync(token, created.Id, new
        {
            version = 1,
            query = "ABSOLUTELY NOT VALID SQL !!! @@@",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("INVALID_QUERY", doc.RootElement.GetProperty("code").GetString());
        Assert.True(doc.RootElement.TryGetProperty("detail", out var detail));
        Assert.False(string.IsNullOrWhiteSpace(detail.GetString()));

        // Row left intact at the prior state (no partial update).
        await using var conn = await OpenRawAsync();
        await using var rowCmd = new NpgsqlCommand(
            "SELECT version, query FROM custom_dataset WHERE id = @id;", conn);
        rowCmd.Parameters.AddWithValue("id", created.Id);
        await using var reader = await rowCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("SELECT 1 AS n", reader.GetString(1));
        await reader.DisposeAsync();

        await using var defCmd = new NpgsqlCommand(
            "SELECT pg_get_viewdef('datasets.rollback_test'::regclass, true);", conn);
        var def = (string?)await defCmd.ExecuteScalarAsync();
        Assert.NotNull(def);
        Assert.Contains("1 AS n", def, StringComparison.Ordinal);
    }

    // ---------- AC-9: not found → 404 ----------

    [Fact]
    public async Task PutDataset_NotFound_Returns404()
    {
        var token = await LoginAsync();
        using var response = await PutAsync(token, Guid.NewGuid(), new { version = 1 });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- AC-10: audit log success entry ----------

    [Fact]
    public async Task PutDataset_Success_WritesSucceededAuditRow()
    {
        var token = await LoginAsync();
        var created = await CreateAsync(token, new
        {
            datasetName = "audit_update_ds",
            isCustomQuery = true,
            query = "SELECT 1 AS n",
        });

        var knownCorrelationId = Ulid.NewUlid().ToString();
        using (var response = await PutAsync(token, created.Id, new
        {
            version = 1,
            query = "SELECT 42 AS n",
        }, knownCorrelationId))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var entry = Assert.Single(await GetAuditEntriesAsync("audit_update_ds", "UPDATE"));
        Assert.True(entry.Succeeded);
        Assert.Equal("UPDATE", entry.Operation);
        // Story 8.10 fix: redefinition is now DROP + CREATE (not CREATE OR REPLACE).
        Assert.Contains("DROP VIEW IF EXISTS datasets.\"audit_update_ds\"", entry.Ddl, StringComparison.Ordinal);
        Assert.Contains("CREATE VIEW datasets.\"audit_update_ds\"", entry.Ddl, StringComparison.Ordinal);
        Assert.Equal(_adminUserId, entry.ActorId);
        Assert.Equal(knownCorrelationId, entry.CorrelationId);
        Assert.Equal("audit_update_ds", entry.DatasetName);
        Assert.True(entry.Timestamp > DateTimeOffset.UtcNow.AddMinutes(-1));

        // previous/new values are jsonb — parse rather than substring-match (jsonb text
        // output normalizes whitespace and key order).
        Assert.NotNull(entry.PreviousValues);
        using var prev = JsonDocument.Parse(entry.PreviousValues!);
        Assert.Equal(1, prev.RootElement.GetProperty("version").GetInt32());
        Assert.NotNull(entry.NewValues);
        using var next = JsonDocument.Parse(entry.NewValues!);
        Assert.Equal(2, next.RootElement.GetProperty("version").GetInt32());
    }

    // ---------- AC-11: audit log failure entry ----------

    [Fact]
    public async Task PutDataset_DdlFailure_WritesFailedAuditRow()
    {
        var token = await LoginAsync();
        var created = await CreateAsync(token, new
        {
            datasetName = "audit_fail_ds",
            isCustomQuery = true,
            query = "SELECT 1 AS n",
        });

        // Story 8.8 (AR-61): syntactic garbage is now rejected by SqlSelectEnforcer
        // before the transaction/audit (AC-5). Use a syntactically-valid SELECT that
        // fails at CREATE VIEW (referenced table does not exist → 42P01) so this test
        // still exercises the DDL-failure → failed-audit-row path (Story 8.5 AC-11).
        using (var response = await PutAsync(token, created.Id, new
        {
            version = 1,
            query = "SELECT * FROM nonexistent_table_xyz",
        }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }

        var entry = Assert.Single(await GetAuditEntriesAsync("audit_fail_ds", "UPDATE"));
        Assert.False(entry.Succeeded);
        Assert.Equal("UPDATE", entry.Operation);
        Assert.Contains(
            "CREATE VIEW datasets.\"audit_fail_ds\"", entry.Ddl, StringComparison.Ordinal);
        Assert.Equal(_adminUserId, entry.ActorId);
    }

    // ---------- Story 8.8 (AR-61) AC-2 / AC-4 / AC-5: enforcer rejects non-SELECT ----------

    [Fact]
    public async Task PutDataset_CustomQueryNonSelect_RejectedByEnforcer_WritesNoAuditRow()
    {
        // A parseable but non-SELECT UPDATE-query change is rejected at checkpoint (a),
        // before the transaction opens: 422 INVALID_QUERY (AC-4), no UPDATE audit row,
        // and the row is left untouched at version 1 (AC-5).
        var token = await LoginAsync();
        var created = await CreateAsync(token, new
        {
            datasetName = "enforcer_reject_upd",
            isCustomQuery = true,
            query = "SELECT 1 AS n",
        });

        using (var response = await PutAsync(token, created.Id, new
        {
            version = 1,
            query = "DELETE FROM foo WHERE id = 1",
        }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            Assert.Equal("INVALID_QUERY", doc.RootElement.GetProperty("code").GetString());
        }

        Assert.Empty(await GetAuditEntriesAsync("enforcer_reject_upd", "UPDATE"));

        // Row left intact at the prior state (no partial update).
        await using var conn = await OpenRawAsync();
        await using var rowCmd = new NpgsqlCommand(
            "SELECT version, query FROM custom_dataset WHERE id = @id;", conn);
        rowCmd.Parameters.AddWithValue("id", created.Id);
        await using var reader = await rowCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("SELECT 1 AS n", reader.GetString(1));
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

    private async Task<HttpResponseMessage> PutAsync(
        string token, Guid id, object payload, string? correlationId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/datasets/{id}")
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

    private async Task<bool> ViewExistsAsync(string name)
    {
        await using var conn = await OpenRawAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM information_schema.views " +
            "WHERE table_schema = 'datasets' AND table_name = @name;", conn);
        cmd.Parameters.AddWithValue("name", name);
        return (long)(await cmd.ExecuteScalarAsync())! == 1L;
    }

    private async Task<int> GetVersionAsync(Guid id)
    {
        await using var conn = await OpenRawAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT version FROM custom_dataset WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", id);
        return (int)(await cmd.ExecuteScalarAsync())!;
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
