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

// Story 8.3 (FR-57 / AR-57) — end-to-end coverage of the inline dataset_name
// validation wired into the POST/PUT stub endpoints. Each rejected name must come
// back as HTTP 422 with a root `code: "INVALID_DATASET_NAME"`; a valid name falls
// through to the 501 stub (handler arrives in Story 8.4/8.5). Mirrors the
// PostgresFixture + WebApplicationFactory pattern from DatasetPermissionTests.
[Collection("DatasetIntegrationTests")]
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class DatasetNameValidationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public DatasetNameValidationTests(PostgresFixture postgres) => _postgres = postgres;

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
            "TRUNCATE TABLE role_permissions, user_roles, roles, refresh_tokens, users RESTART IDENTITY CASCADE;");

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

    // ---------- POST /api/datasets — invalid names rejected with 422 ----------

    [Fact]
    public async Task PostDataset_InvalidPattern_Returns422InvalidDatasetName()
    {
        var token = await LoginAsync();
        using var response = await PostAsync(token, new { datasetName = "InvalidName", isCustomQuery = true });
        await AssertInvalidDatasetNameAsync(response);
    }

    [Fact]
    public async Task PostDataset_TooLong_Returns422InvalidDatasetName()
    {
        var token = await LoginAsync();
        var tooLong = "a" + new string('b', 63); // 64 chars
        using var response = await PostAsync(token, new { datasetName = tooLong, isCustomQuery = true });
        await AssertInvalidDatasetNameAsync(response);
    }

    [Fact]
    public async Task PostDataset_ReservedKeyword_Returns422InvalidDatasetName()
    {
        var token = await LoginAsync();
        using var response = await PostAsync(token, new { datasetName = "select", isCustomQuery = true });
        await AssertInvalidDatasetNameAsync(response);
    }

    [Fact]
    public async Task PostDataset_DenylistedUsers_Returns422InvalidDatasetName()
    {
        var token = await LoginAsync();
        using var response = await PostAsync(token, new { datasetName = "users", isCustomQuery = true });
        await AssertInvalidDatasetNameAsync(response);
    }

    [Fact]
    public async Task PostDataset_DenylistedCustomDataset_Returns422InvalidDatasetName()
    {
        var token = await LoginAsync();
        using var response = await PostAsync(token, new { datasetName = "custom_dataset", isCustomQuery = true });
        await AssertInvalidDatasetNameAsync(response);
    }

    [Fact]
    public async Task PostDataset_ValidName_Returns201NotInvalid()
    {
        // A valid name passes inline validation and reaches the create handler (Story 8.4),
        // proving the inline check does NOT block legitimate names. A unique name is used
        // because this class does not truncate custom_dataset (avoids a 409 on re-run).
        var token = await LoginAsync();
        var name = $"valid_probe_{Guid.NewGuid():N}";
        using var response = await PostAsync(token, new { datasetName = name, isCustomQuery = true });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---------- PUT /api/datasets/{id} — optional rename validated ----------

    [Fact]
    public async Task PutDataset_InvalidName_Returns422InvalidDatasetName()
    {
        var token = await LoginAsync();
        using var response = await PutAsync(token, Guid.NewGuid(), new { datasetName = "BAD_NAME", version = 1 });
        await AssertInvalidDatasetNameAsync(response);
    }

    [Fact]
    public async Task PutDataset_NullName_SkipsValidationReachesHandler()
    {
        // Omitting dataset_name means "keep existing" — name validation is skipped and the
        // request reaches the real handler (Story 8.5). With a non-existent id the handler's
        // initial fetch returns 404 (NOT the 422 INVALID_DATASET_NAME envelope), proving the
        // null name did not trip validation.
        var token = await LoginAsync();
        using var response = await PutAsync(token, Guid.NewGuid(), new { version = 1 });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- helpers ----------

    private async Task<HttpResponseMessage> PostAsync(string token, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/datasets")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
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

    private static async Task AssertInvalidDatasetNameAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        // Results.Problem extensions are flattened to the JSON root via [JsonExtensionData].
        Assert.Equal("INVALID_DATASET_NAME", doc.RootElement.GetProperty("code").GetString());
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
    private sealed record LoginResponseDto(string AccessToken, string RefreshToken, int ExpiresIn);
}
