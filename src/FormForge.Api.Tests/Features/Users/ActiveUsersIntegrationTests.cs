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

namespace FormForge.Api.Tests.Features.Users;

[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class ActiveUsersIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public ActiveUsersIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

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
    public async Task GetActiveUsers_Unauthenticated_Returns401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/users/active");
        // No Authorization header
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetActiveUsers_Authenticated_ReturnsOnlyActiveUsers_SortedByDisplayName()
    {
        var token = await LoginAsync("zoe@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/users/active");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var users = await response.Content.ReadFromJsonAsync<List<ActiveUserDto>>();
        Assert.NotNull(users);

        // The inactive user is excluded; the two active users come back sorted by
        // display name ("Alice Active" before "Zoe Caller").
        Assert.Equal(["Alice Active", "Zoe Caller"], users!.Select(u => u.DisplayName));
        Assert.DoesNotContain(users, u => u.Email == "inactive@example.com");

        // Each row carries the minimal identity triple — id, email, display name.
        var alice = users.Single(u => u.Email == "alice@example.com");
        Assert.NotEqual(Guid.Empty, alice.Id);
        Assert.Equal("Alice Active", alice.DisplayName);
    }

    private async Task<string> LoginAsync(string email, string password)
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login", new { email, password });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        return body!.AccessToken;
    }

    private static async Task SeedTestUsersAsync(FormForgeDbContext db)
    {
        db.Users.Add(new User
        {
            Email = "zoe@example.com",
            DisplayName = "Zoe Caller",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password1!", 12),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.Users.Add(new User
        {
            Email = "alice@example.com",
            DisplayName = "Alice Active",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password1!", 12),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.Users.Add(new User
        {
            Email = "inactive@example.com",
            DisplayName = "Ivan Inactive",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password1!", 12),
            IsActive = false,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record LoginResponseDto(string AccessToken, string RefreshToken, int ExpiresIn);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record ActiveUserDto(Guid Id, string Email, string DisplayName);
}
