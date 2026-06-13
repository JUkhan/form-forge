using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Features.Menus;
using FormForge.Api.Infrastructure.Persistence;
using FormForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormForge.Api.Tests.Features.Menus;

[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class UploadIconIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public UploadIconIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:formforge", _postgres.ConnectionString);
                builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
                builder.ConfigureServices(services =>
                {
                    // Replace real MinIO service with in-memory fake — no live bucket required.
                    var descriptor = services.Single(d => d.ServiceType == typeof(IIconStorageService));
                    services.Remove(descriptor);
                    services.AddSingleton<IIconStorageService, FakeIconStorageService>();
                });
            });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        await db.Database.MigrateAsync();

        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE menu_role_assignments, menus, role_permissions, user_roles, roles, refresh_tokens, users RESTART IDENTITY CASCADE;");

        await ReseedSystemRolesAsync(db);
        await SeedTestUsersAsync(db);

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

    [Fact]
    public async Task UploadIcon_Unauthenticated_Returns401()
    {
        using var content = BuildPngMultipart(64);

        using var response = await _client!.PostAsync(new Uri("/api/admin/menus/upload-icon", UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UploadIcon_AsNonAdmin_Returns403()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");
        using var content = BuildPngMultipart(64);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/menus/upload-icon")
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UploadIcon_ValidPng_Returns200WithObjectKey()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        using var content = BuildPngMultipart(64);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/menus/upload-icon")
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UploadIconResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("minio", body.Type);
        Assert.StartsWith("menus/icons/", body.ObjectKey, StringComparison.Ordinal);
        Assert.EndsWith(".png", body.ObjectKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadIcon_OversizedFile_Returns422UploadInvalid()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        // 2 MB + 1 byte
        using var content = BuildPngMultipart((2 * 1024 * 1024) + 1);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/menus/upload-icon")
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("UPLOAD_INVALID", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadIcon_WrongMimeType_Returns422UploadInvalid()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        using var content = BuildMultipart(new byte[] { 0x00, 0x01, 0x02 }, "file.txt", "text/plain");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/menus/upload-icon")
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("UPLOAD_INVALID", body, StringComparison.Ordinal);
    }

    // ---------- Helpers ----------

    private static MultipartFormDataContent BuildPngMultipart(int byteLength)
    {
        var bytes = new byte[byteLength];
        // Bytes don't have to be a real PNG — the endpoint validates ContentType, not magic numbers.
        return BuildMultipart(bytes, "icon.png", "image/png");
    }

    private static MultipartFormDataContent BuildMultipart(byte[] bytes, string filename, string contentType)
    {
        var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        multipart.Add(file, "file", filename);
        return multipart;
    }

    private async Task<string> LoginAsync(string email, string password)
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email, password });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        return body!.AccessToken;
    }

    private static async Task ReseedSystemRolesAsync(FormForgeDbContext db)
    {
        if (!await db.Roles.AnyAsync(r => r.Id == PlatformAdminRoleId))
        {
            db.Roles.Add(new Role
            {
                Id = PlatformAdminRoleId,
                Name = "platform-admin",
                IsSystem = true,
                CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            });
        }
        await db.SaveChangesAsync();
    }

    private static async Task SeedTestUsersAsync(FormForgeDbContext db)
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
        await db.SaveChangesAsync();
    }

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record LoginResponseDto(string AccessToken, string RefreshToken, int ExpiresIn);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record UploadIconResponseDto(string Type, string ObjectKey);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Registered via DI.")]
    private sealed class FakeIconStorageService : IIconStorageService
    {
        public Task<string> StoreAsync(Stream content, string extension, long length, CancellationToken ct) =>
            Task.FromResult($"menus/icons/{Guid.NewGuid():N}.{extension}");

        // Unused by the icon-upload tests but required by the IIconStorageService
        // contract (the File designer field added these). Pre-existing compile gap
        // unrelated to Story 2.10 — stubbed so the test project builds.
        public Task<string> GetPresignedUrlAsync(string objectKey, CancellationToken ct) =>
            Task.FromResult($"https://fake.local/{objectKey}");

        public Task UploadFileAsync(Stream content, string objectKey, string contentType, long length, CancellationToken ct) =>
            Task.CompletedTask;

        public Task DeleteFileAsync(string objectKey, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
