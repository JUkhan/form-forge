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

// Story 11.3 (FR-72 / AR-63) — end-to-end coverage of POST /api/datasets/preview:
// custom-query SELECT execution with LIMIT 10 (AC-2/AC-4), DDL rejection (AC-6),
// builder-mode SQL generation + execution (AC-3), invalid builder_state (AC-3/422),
// read-only semantics (AC-7), and the dataset-management permission gate (AC-8).
//
// The preview pool connection string (`formforge_preview`) is pointed at the same
// Testcontainers DB as `formforge` (the container superuser): the migration creates the
// least-privileged role but granting it SELECT on tables seeded AFTER migration is out of
// scope here — the privilege isolation itself is covered by DatasetMigrationTests. These
// tests exercise the endpoint/service behaviour, not the role's GRANT matrix. The startup
// ALTER-ROLE password step is skipped because no preview password is configured.
[Collection("DatasetIntegrationTests")]
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class DatasetPreviewTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public DatasetPreviewTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:formforge", _postgres.ConnectionString);
                // Story 11.3 — the preview pool. Pointed at the same test DB (superuser) so
                // the LIMIT-10 execution path runs; least-privilege is verified elsewhere.
                builder.UseSetting("ConnectionStrings:formforge_preview", _postgres.ConnectionString);
                builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
                builder.UseSetting("DatasetManager:AllowedTables:0", "preview_probe");
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

        // A real, allowlisted public table the builder-mode generated SELECT reads from.
        await db.Database.ExecuteSqlRawAsync(
            """
            DROP TABLE IF EXISTS public.preview_probe CASCADE;
            CREATE TABLE public.preview_probe (
                id     integer NOT NULL,
                status text
            );
            INSERT INTO public.preview_probe (id, status) VALUES (1, 'open'), (2, 'closed');
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
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
                await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS public.preview_probe CASCADE;");
            }

            _client?.Dispose();
            await _factory.DisposeAsync();
        }
        else
        {
            _client?.Dispose();
        }
    }

    // ---------- Test 1: custom query → 200, columns + rows ----------

    [Fact]
    public async Task Post_Preview_CustomQuery_Returns200()
    {
        var token = await LoginAsync("admin@example.com");

        using var response = await PostPreviewAsync(token, new
        {
            isCustomQuery = true,
            query = "SELECT 1 AS n",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<PreviewResponse>();
        Assert.NotNull(result);
        Assert.Equal("n", Assert.Single(result!.Columns));
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0][0].GetInt32());
    }

    // ---------- Test 2: DDL query → 422 DATASET_QUERY_INVALID ----------

    [Fact]
    public async Task Post_Preview_CustomQuery_DdlQuery_Returns422()
    {
        var token = await LoginAsync("admin@example.com");

        using var response = await PostPreviewAsync(token, new
        {
            isCustomQuery = true,
            query = "DROP TABLE custom_dataset",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("DATASET_QUERY_INVALID", doc.RootElement.GetProperty("code").GetString());
    }

    // ---------- Test 3: builder mode valid state → 200, columns present ----------

    [Fact]
    public async Task Post_Preview_BuilderMode_ValidState_Returns200()
    {
        var token = await LoginAsync("admin@example.com");

        using var response = await PostPreviewAsync(token, new
        {
            isCustomQuery = false,
            builderState = BuilderStateJson("preview_probe", "id"),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<PreviewResponse>();
        Assert.NotNull(result);
        Assert.NotEmpty(result!.Columns);
    }

    // ---------- Test 4: builder mode no left node → 422 BUILDER_STATE_INVALID ----------

    [Fact]
    public async Task Post_Preview_BuilderMode_NoLeftNode_Returns422()
    {
        var token = await LoginAsync("admin@example.com");

        using var response = await PostPreviewAsync(token, new
        {
            isCustomQuery = false,
            builderState = BuilderStateJson("preview_probe", "id", side: "right"),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("BUILDER_STATE_INVALID", doc.RootElement.GetProperty("code").GetString());
    }

    // ---------- Test 5: preview is read-only (no dataset row created/modified) ----------

    [Fact]
    public async Task Post_Preview_IsReadOnly()
    {
        var token = await LoginAsync("admin@example.com");

        // Seed at least one dataset row so the count is meaningful.
        using (var create = new HttpRequestMessage(HttpMethod.Post, "/api/datasets")
        {
            Content = JsonContent.Create(new { datasetName = "preview_readonly_ds", isCustomQuery = true }),
        })
        {
            create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var createResponse = await _client!.SendAsync(create);
            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        }

        var before = await CountCustomDatasetAsync();

        using var response = await PostPreviewAsync(token, new
        {
            isCustomQuery = true,
            query = "SELECT 1 AS n",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var after = await CountCustomDatasetAsync();
        Assert.Equal(before, after);
    }

    // ---------- Test 7: genuine Postgres execution error → 422 DATASET_QUERY_INVALID ----------
    // AC-6: a query that passes SELECT-only validation but fails at execution (e.g. missing column)
    // must return 422 with the Postgres error message and no stack trace.

    [Fact]
    public async Task Post_Preview_CustomQuery_PostgresRuntimeError_Returns422WithMessage()
    {
        var token = await LoginAsync("admin@example.com");

        using var response = await PostPreviewAsync(token, new
        {
            isCustomQuery = true,
            query = "SELECT nonexistent_column_xyzzy FROM preview_probe",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("DATASET_QUERY_INVALID", doc.RootElement.GetProperty("code").GetString());
        // detail must contain the Postgres message, not a stack trace
        var detail = doc.RootElement.GetProperty("detail").GetString();
        Assert.NotNull(detail);
        Assert.DoesNotContain("System.", detail, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Test 6: no dataset-management permission → 403 ----------

    [Fact]
    public async Task Post_Preview_NoPermission_Returns403()
    {
        var token = await LoginAsync("viewer@example.com");

        using var response = await PostPreviewAsync(token, new
        {
            isCustomQuery = true,
            query = "SELECT 1 AS n",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- helpers ----------

    private async Task<HttpResponseMessage> PostPreviewAsync(string token, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/datasets/preview")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private async Task<long> CountCustomDatasetAsync()
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        return await db.Database
            .SqlQueryRaw<long>("SELECT COUNT(*)::bigint AS \"Value\" FROM custom_dataset")
            .FirstAsync();
    }

    private static string BuilderStateJson(
        string table, string column, string side = "left", bool isChecked = true)
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
                                pgType = "integer",
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
    private sealed record PreviewResponse(List<string> Columns, List<List<JsonElement>> Rows);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record LoginResponseDto(string AccessToken, string RefreshToken, int ExpiresIn);
}
