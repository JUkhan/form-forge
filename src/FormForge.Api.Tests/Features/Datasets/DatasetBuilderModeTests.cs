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

// Story 11.1 (FR-70 / AR-66 / AR-65) — end-to-end coverage of builder-mode PUT
// /api/datasets/{id}: the server re-derives the SQL SELECT from builder_state and persists
// it as custom_dataset.query in the same transaction as the VIEW DDL (AC-1/AC-5), and an
// invalid builder_state returns 422 BUILDER_STATE_INVALID before any DDL (AC-4). The
// allowlist is configured via DatasetManager:AllowedTables (UseSetting) and a real public
// table (builder_probe) is seeded so the generated VIEW can be created. Mirrors the
// PostgresFixture + WebApplicationFactory pattern from DatasetUpdateTests / DatasetCatalogTests.
[Collection("DatasetIntegrationTests")]
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class DatasetBuilderModeTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public DatasetBuilderModeTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:formforge", _postgres.ConnectionString);
                builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
                // builder_probe is a real, allowlisted public table the generated VIEW reads from.
                builder.UseSetting("DatasetManager:AllowedTables:0", "builder_probe");
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

        // Seed a real public table the generated SELECT/VIEW can reference.
        await db.Database.ExecuteSqlRawAsync(
            """
            DROP TABLE IF EXISTS public.builder_probe CASCADE;
            CREATE TABLE public.builder_probe (
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
                await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS public.builder_probe CASCADE;");
            }

            _client?.Dispose();
            await _factory.DisposeAsync();
        }
        else
        {
            _client?.Dispose();
        }
    }

    // ---------- Test 1: valid builder_state → 200, server-generated query persisted ----------

    [Fact]
    public async Task Put_BuilderMode_ValidState_Returns200_And_QueryIsGenerated()
    {
        var token = await LoginAsync();
        var created = await CreateBuilderDatasetAsync(token, "builder_valid_ds");

        using var response = await PutAsync(token, created.Id, new
        {
            version = 1,
            isCustomQuery = false,
            builderState = BuilderStateJson("builder_probe", "id"),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<DatasetResponseDto>();
        Assert.NotNull(dto);
        Assert.False(dto!.IsCustomQuery);
        Assert.NotNull(dto.Query);
        Assert.Contains("SELECT", dto.Query!, StringComparison.Ordinal);
        Assert.Contains("FROM \"public\".\"builder_probe\"", dto.Query!, StringComparison.Ordinal);

        // The persisted query (GET) must match the server-generated SQL.
        using var getResponse = await GetAsync(token, $"/api/datasets/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<DatasetResponseDto>();
        Assert.NotNull(fetched);
        Assert.Equal(dto.Query, fetched!.Query);
    }

    // ---------- Test 2: no left node → 422 BUILDER_STATE_INVALID ----------

    [Fact]
    public async Task Put_BuilderMode_NoLeftNode_Returns422_BuilderStateInvalid()
    {
        var token = await LoginAsync();
        var created = await CreateBuilderDatasetAsync(token, "builder_noleft_ds");

        using var response = await PutAsync(token, created.Id, new
        {
            version = 1,
            isCustomQuery = false,
            builderState = BuilderStateJson("builder_probe", "id", side: "right"),
        });

        await AssertBuilderStateInvalidAsync(response);
    }

    // ---------- Test 3: no columns selected → 422 BUILDER_STATE_INVALID ----------

    [Fact]
    public async Task Put_BuilderMode_NoColumnsSelected_Returns422_BuilderStateInvalid()
    {
        var token = await LoginAsync();
        var created = await CreateBuilderDatasetAsync(token, "builder_nocols_ds");

        using var response = await PutAsync(token, created.Id, new
        {
            version = 1,
            isCustomQuery = false,
            builderState = BuilderStateJson("builder_probe", "id", isChecked: false),
        });

        await AssertBuilderStateInvalidAsync(response);
    }

    // ---------- Test 4: non-allowlisted table → 422 BUILDER_STATE_INVALID ----------

    [Fact]
    public async Task Put_BuilderMode_NonAllowlistedTable_Returns422_BuilderStateInvalid()
    {
        var token = await LoginAsync();
        var created = await CreateBuilderDatasetAsync(token, "builder_denied_ds");

        using var response = await PutAsync(token, created.Id, new
        {
            version = 1,
            isCustomQuery = false,
            builderState = BuilderStateJson("internal_secret_table", "id"),
        });

        await AssertBuilderStateInvalidAsync(response);
    }

    // ---------- Test 5: builder-mode create with builder_state → 201, query + state persisted ----------

    [Fact]
    public async Task Post_BuilderMode_WithBuilderState_Returns201_QueryAndStatePersisted()
    {
        var token = await LoginAsync();
        var builderState = BuilderStateJson("builder_probe", "id");

        using var response = await PostAsync(token, new
        {
            datasetName = "builder_create_with_state",
            isCustomQuery = false,
            builderState,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<DatasetResponseDto>();
        Assert.NotNull(dto);
        Assert.False(dto!.IsCustomQuery);
        Assert.NotNull(dto.Query);
        Assert.Contains("SELECT", dto.Query!, StringComparison.Ordinal);
        Assert.Contains("FROM \"public\".\"builder_probe\"", dto.Query!, StringComparison.Ordinal);
        // The POST response echoes the raw builder_state passed through (not re-read from jsonb).
        Assert.Equal(builderState, dto.BuilderState);

        // GET must confirm both the server-generated query and the builder_state persisted.
        using var getResponse = await GetAsync(token, $"/api/datasets/{dto.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<DatasetResponseDto>();
        Assert.NotNull(fetched);
        Assert.Equal(dto.Query, fetched!.Query);
        Assert.NotNull(fetched.BuilderState);
        Assert.Contains("builder_probe", fetched.BuilderState!, StringComparison.Ordinal);
    }

    // ---------- Test 6: builder-mode create with no builder_state → 201, placeholder VIEW ----------

    [Fact]
    public async Task Post_BuilderMode_NullBuilderState_Returns201_PlaceholderView()
    {
        var token = await LoginAsync();

        using var response = await PostAsync(token, new
        {
            datasetName = "builder_create_no_state",
            isCustomQuery = false,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<DatasetResponseDto>();
        Assert.NotNull(dto);
        // Backward compat — no builder_state means no generator run: query stays null (placeholder
        // VIEW), builder_state stays null. (Mirrors Story 8.4 AC-2.)
        Assert.Null(dto!.Query);
        Assert.Null(dto.BuilderState);
    }

    // ---------- Test 7: builder-mode create with invalid builder_state → 422 ----------

    [Fact]
    public async Task Post_BuilderMode_InvalidBuilderState_Returns422()
    {
        var token = await LoginAsync();

        using var response = await PostAsync(token, new
        {
            datasetName = "builder_create_invalid",
            isCustomQuery = false,
            builderState = BuilderStateJson("builder_probe", "id", side: "right"),
        });

        await AssertBuilderStateInvalidAsync(response);
    }

    // ---------- helpers ----------

    private async Task<HttpResponseMessage> PostAsync(string token, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/datasets")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private static async Task AssertBuilderStateInvalidAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("BUILDER_STATE_INVALID", doc.RootElement.GetProperty("code").GetString());
    }

    // Minimal builder_state JSON: one node (left by default) with one checked column, no
    // joins/filters/order-by. Returned as the raw JSON string the client sends in
    // UpdateDatasetRequest.BuilderState.
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

    private async Task<HttpResponseMessage> PutAsync(string token, Guid id, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/datasets/{id}")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
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
