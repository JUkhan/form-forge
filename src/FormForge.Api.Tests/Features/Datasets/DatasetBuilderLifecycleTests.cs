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

// Story 11.4 (FR-73 / AR-59) — builder-mode PUT /api/datasets/{id} applies the same Epic 8
// transactional VIEW lifecycle as Custom Query mode: server-side SQL generation from
// builder_state runs before any DDL (AC-1), the row UPDATE + VIEW DDL commit/rollback
// atomically (AC-2/AC-3), and the audit log records the server-generated DDL prefixed with
// "-- Builder-generated" (AC-4). Mirrors the PostgresFixture + WebApplicationFactory pattern
// from DatasetBuilderModeTests (allowlist config / BuilderStateJson) and DatasetUpdateTests
// (OpenRawAsync / ViewExistsAsync / GetAuditEntriesAsync). Uses blc_-prefixed public tables to
// avoid collisions with builder_probe in the shared PostgresFixture; blc_ghost is allowlisted
// but intentionally never created in Postgres so a generated VIEW fails at CREATE (AC-3).
[Collection("DatasetIntegrationTests")]
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class DatasetBuilderLifecycleTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public DatasetBuilderLifecycleTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:formforge", _postgres.ConnectionString);
                builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
                // blc_probe is a real, allowlisted public table; blc_ghost is allowlisted but
                // intentionally NOT created so a generated VIEW fails at CREATE VIEW (AC-3).
                builder.UseSetting("DatasetManager:AllowedTables:0", "blc_probe");
                builder.UseSetting("DatasetManager:AllowedTables:1", "blc_ghost");
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

        // Seed blc_probe (real). Do NOT seed blc_ghost — it must be absent for the AC-3 test.
        await db.Database.ExecuteSqlRawAsync(
            """
            DROP TABLE IF EXISTS public.blc_probe CASCADE;
            CREATE TABLE public.blc_probe (
                id     integer NOT NULL,
                status text
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
                await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS public.blc_probe CASCADE;");
            }

            _client?.Dispose();
            await _factory.DisposeAsync();
        }
        else
        {
            _client?.Dispose();
        }
    }

    // ---------- Test 1: valid builder_state save → 200, VIEW exists with generated SQL (AC: 1, 2) ----------

    [Fact]
    public async Task Put_BuilderMode_Save_ViewExistsWithGeneratedSql()
    {
        var token = await LoginAsync();
        var created = await CreateBuilderDatasetAsync(token, "blc_save_ds");

        using var response = await PutAsync(token, created.Id, new
        {
            version = 1,
            isCustomQuery = false,
            builderState = BuilderStateJson("blc_probe", "id"),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<DatasetResponseDto>();
        Assert.NotNull(dto);
        Assert.False(dto!.IsCustomQuery);
        Assert.NotNull(dto.Query);
        Assert.Contains("FROM \"public\".\"blc_probe\"", dto.Query!, StringComparison.Ordinal);

        Assert.True(await ViewExistsAsync("blc_save_ds"));
        var def = await GetViewDefAsync("blc_save_ds");
        Assert.NotNull(def);
        Assert.Contains("blc_probe", def!, StringComparison.Ordinal);
    }

    // ---------- Test 2: rename applies the VIEW lifecycle → 200, old gone / new exists (AC: 2) ----------

    [Fact]
    public async Task Put_BuilderMode_Rename_ViewLifecycleApplies()
    {
        var token = await LoginAsync();
        var builderState = BuilderStateJson("blc_probe", "id");
        var created = await CreateBuilderDatasetWithStateAsync(token, "blc_rename_ds", builderState);

        Assert.True(await ViewExistsAsync("blc_rename_ds"));

        using var response = await PutAsync(token, created.Id, new
        {
            version = 1,
            datasetName = "blc_renamed_ds",
            isCustomQuery = false,
            builderState,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<DatasetResponseDto>();
        Assert.NotNull(dto);
        Assert.Equal("blc_renamed_ds", dto!.DatasetName);
        Assert.Equal(2, dto.Version);

        Assert.False(await ViewExistsAsync("blc_rename_ds"));
        Assert.True(await ViewExistsAsync("blc_renamed_ds"));
        var def = await GetViewDefAsync("blc_renamed_ds");
        Assert.NotNull(def);
        Assert.Contains("blc_probe", def!, StringComparison.Ordinal);
    }

    // ---------- Test 3: DDL failure rolls back → original VIEW + row intact (AC: 3) ----------

    [Fact]
    public async Task Put_BuilderMode_DdlFailure_OriginalViewIntact()
    {
        var token = await LoginAsync();
        var created = await CreateBuilderDatasetWithStateAsync(
            token, "blc_rollback_ds", BuilderStateJson("blc_probe", "id"));

        Assert.True(await ViewExistsAsync("blc_rollback_ds"));

        // blc_ghost passes the allowlist (generation succeeds) but does not exist in Postgres,
        // so CREATE VIEW fails with 42P01 → NpgsqlException → transaction rolls back.
        using var response = await PutAsync(token, created.Id, new
        {
            version = 1,
            isCustomQuery = false,
            builderState = BuilderStateJson("blc_ghost", "id"),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("INVALID_QUERY", doc.RootElement.GetProperty("code").GetString());

        // Row rolled back: still at version 1.
        await using var conn = await OpenRawAsync();
        await using var rowCmd = new NpgsqlCommand(
            "SELECT version FROM custom_dataset WHERE id = @id;", conn);
        rowCmd.Parameters.AddWithValue("id", created.Id);
        var version = (int)(await rowCmd.ExecuteScalarAsync())!;
        Assert.Equal(1, version);

        // Original VIEW survives and still references blc_probe.
        Assert.True(await ViewExistsAsync("blc_rollback_ds"));
        var def = await GetViewDefAsync("blc_rollback_ds");
        Assert.NotNull(def);
        Assert.Contains("blc_probe", def!, StringComparison.Ordinal);
    }

    // ---------- Test 4: audit DDL carries the "-- Builder-generated" marker (AC: 4) ----------

    [Fact]
    public async Task Put_BuilderMode_Save_AuditDdl_HasBuilderGeneratedMarker()
    {
        var token = await LoginAsync();
        var created = await CreateBuilderDatasetAsync(token, "blc_audit_ds");

        using (var response = await PutAsync(token, created.Id, new
        {
            version = 1,
            isCustomQuery = false,
            builderState = BuilderStateJson("blc_probe", "id"),
        }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var entry = Assert.Single(await GetAuditEntriesAsync("blc_audit_ds", "UPDATE"));
        Assert.True(entry.Succeeded);
        Assert.NotNull(entry.Ddl);
        Assert.StartsWith("-- Builder-generated\n", entry.Ddl!, StringComparison.Ordinal);
    }

    // ---------- Test 5: invalid builder_state → 422 BUILDER_STATE_INVALID, no DDL/audit (AC: 1) ----------

    [Fact]
    public async Task Put_BuilderMode_InvalidBuilderState_Returns422_NoDdl()
    {
        var token = await LoginAsync();
        var created = await CreateBuilderDatasetAsync(token, "blc_invalid_ds");

        using (var response = await PutAsync(token, created.Id, new
        {
            version = 1,
            isCustomQuery = false,
            // side = "right" → no left node → generation fails before any DDL.
            builderState = BuilderStateJson("blc_probe", "id", side: "right"),
        }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            Assert.Equal("BUILDER_STATE_INVALID", doc.RootElement.GetProperty("code").GetString());
        }

        // Generation failure returns before the transaction opens — no UPDATE audit row.
        Assert.Empty(await GetAuditEntriesAsync("blc_invalid_ds", "UPDATE"));
    }

    // ---------- Test 6: rename + builder_state change → ALTER RENAME then DROP+CREATE (AC: 2, 4) ----------

    // The simultaneous rename + query-change branch (DatasetService isRename && queryChanged):
    // a builder-mode PUT that renames the dataset AND changes builder_state must run
    // ALTER VIEW RENAME followed by DROP+CREATE, all in one transaction, and record the
    // composite DDL — prefixed with "-- Builder-generated" — to the audit log.
    [Fact]
    public async Task Put_BuilderMode_RenameAndQueryChange_ViewRedefinedUnderNewName()
    {
        var token = await LoginAsync();
        var created = await CreateBuilderDatasetWithStateAsync(
            token, "blc_renqc_ds", BuilderStateJson("blc_probe", "id"));

        Assert.True(await ViewExistsAsync("blc_renqc_ds"));
        var originalDef = await GetViewDefAsync("blc_renqc_ds");
        Assert.NotNull(originalDef);
        Assert.Contains("id", originalDef!, StringComparison.Ordinal);

        // New name AND a different builder_state (blc_probe.status) so the regenerated SQL
        // differs from the stored query → queryChanged == true → REPLACE fires on the rename.
        using var response = await PutAsync(token, created.Id, new
        {
            version = 1,
            datasetName = "blc_renqc_done",
            isCustomQuery = false,
            builderState = BuilderStateJson("blc_probe", "status", pgType: "text"),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<DatasetResponseDto>();
        Assert.NotNull(dto);
        Assert.Equal("blc_renqc_done", dto!.DatasetName);
        Assert.Equal(2, dto.Version);
        Assert.NotNull(dto.Query);
        Assert.Contains("status", dto.Query!, StringComparison.Ordinal);

        // Rename applied: old name gone, new name present with the REDEFINED body.
        Assert.False(await ViewExistsAsync("blc_renqc_ds"));
        Assert.True(await ViewExistsAsync("blc_renqc_done"));
        var newDef = await GetViewDefAsync("blc_renqc_done");
        Assert.NotNull(newDef);
        Assert.Contains("status", newDef!, StringComparison.Ordinal);

        // Audit DDL is the composite (ALTER RENAME + DROP/CREATE), prefixed and marked succeeded.
        var entry = Assert.Single(await GetAuditEntriesAsync("blc_renqc_done", "UPDATE"));
        Assert.True(entry.Succeeded);
        Assert.NotNull(entry.Ddl);
        Assert.StartsWith("-- Builder-generated\n", entry.Ddl!, StringComparison.Ordinal);
        Assert.Contains("ALTER VIEW", entry.Ddl!, StringComparison.Ordinal);
        Assert.Contains("CREATE VIEW datasets.\"blc_renqc_done\"", entry.Ddl!, StringComparison.Ordinal);
    }

    // ---------- helpers ----------

    // Minimal builder_state JSON: one node (left by default) with one checked column, no
    // joins/filters/order-by. Mirrors the helper in DatasetBuilderModeTests.
    private static string BuilderStateJson(
        string table, string column, string side = "left", bool isChecked = true,
        string pgType = "integer")
    {
        var state = new
        {
            nodes = new[]
            {
                new
                {
                    id = "n1",
                    type = "tableNode",
                    position = new { x = 0, y = 0 },
                    data = new
                    {
                        tableName = table,
                        side,
                        columns = new[]
                        {
                            new
                            {
                                columnName = column,
                                pgType,
                                @checked = isChecked,
                                aggregate = "none",
                                alias = "",
                            },
                        },
                    },
                },
            },
            edges = Array.Empty<object>(),
            filters = new { id = "root", kind = "group", combinator = "AND", items = Array.Empty<object>() },
            orderBy = Array.Empty<object>(),
            caseColumns = Array.Empty<object>(),
            calculatedColumns = Array.Empty<object>(),
        };
        return JsonSerializer.Serialize(state);
    }

    private async Task<DatasetResponseDto> CreateBuilderDatasetAsync(string token, string name)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/datasets")
        {
            Content = JsonContent.Create(new { datasetName = name, isCustomQuery = false }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<DatasetResponseDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private async Task<DatasetResponseDto> CreateBuilderDatasetWithStateAsync(
        string token, string name, string builderState)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/datasets")
        {
            Content = JsonContent.Create(new { datasetName = name, isCustomQuery = false, builderState }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<DatasetResponseDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private async Task<HttpResponseMessage> PutAsync(string token, Guid id, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/datasets/{id}")
        {
            Content = JsonContent.Create(payload),
        };
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

    private async Task<string?> GetViewDefAsync(string name)
    {
        await using var conn = await OpenRawAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT pg_get_viewdef(@rc::regclass, true);", conn);
        cmd.Parameters.AddWithValue("rc", $"datasets.{name}");
        return (string?)await cmd.ExecuteScalarAsync();
    }

    private async Task<List<AuditRow>> GetAuditEntriesAsync(string datasetName, string operation)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        return await db.DatasetAuditLog
            .Where(a => a.DatasetName == datasetName && a.Operation == operation)
            .Select(a => new AuditRow(a.Operation, a.Succeeded, a.Ddl))
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

    private sealed record AuditRow(string Operation, bool Succeeded, string? Ddl);

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
