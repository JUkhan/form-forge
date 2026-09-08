using System.Diagnostics.CodeAnalysis;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Infrastructure.Persistence;
using FormForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormForge.Api.Tests.Features.Auth;

// Story 12.4 — end-to-end coverage for the platform-super-admin login path: credential
// check against public.platform_admins (never public.users or tenant_user_index),
// the resulting JWT's claim shape (roles=["platform-super-admin"], no tenantId), the
// access-token-only decision (RefreshToken: null, no refresh_token cookie), and the
// group-level DenyPlatformSuperAdmin() deny on /api/data/* and /api/datasets/*
// (including the deliberately auth-only "/options" and dataset-list routes) plus the
// pre-existing /api/admin/* denial (no "platform-admin" role claim ever issued here).
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class PlatformAdminLoginIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string AdminEmail = "platform.super@formforge.test";
    private const string AdminPassword = "SuperSecret1!";

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public PlatformAdminLoginIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

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

        // Clear the WebApplicationFactory-startup bootstrap row (admin@formforge.local)
        // too, so this suite controls its own known platform-admin credentials.
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE platform_admins, refresh_tokens, users RESTART IDENTITY CASCADE;");

        db.PlatformAdmins.Add(new PlatformAdmin
        {
            UserEmail = AdminEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(AdminPassword, 12),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

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

    private async Task<string> LoginAsPlatformAdminAsync()
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email = AdminEmail, password = AdminPassword });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        return body!.AccessToken;
    }

    [Fact]
    public async Task Login_ValidCredentials_Returns200_JwtCarriesPlatformSuperAdminRole_NoTenantId()
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email = AdminEmail, password = AdminPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotNull(body);
        Assert.Null(body!.RefreshToken);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(body.AccessToken);
        var roles = jwt.Claims.Where(c => c.Type == "roles").Select(c => c.Value).ToList();
        Assert.Equal(["platform-super-admin"], roles);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "tenantId");
    }

    [Fact]
    public async Task Login_ValidCredentials_DoesNotSetRefreshTokenCookie()
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email = AdminEmail, password = AdminPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            Assert.DoesNotContain(cookies, c => c.Contains("refresh_token=", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401InvalidCredentials()
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email = AdminEmail, password = "WrongPassword!" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("INVALID_CREDENTIALS", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlatformSuperAdmin_CallsDynamicDataOptionsRoute_Returns403Forbidden()
    {
        var token = await LoginAsPlatformAdminAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/data/some-designer/options");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("FORBIDDEN", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlatformSuperAdmin_CallsDynamicDataListRoute_Returns403Forbidden()
    {
        // AC-2 covers "any /api/data/* route" — including the permission-checked list
        // endpoint, not just the auth-only /options route: the group-level deny must
        // reject the token before RequirePermission's per-endpoint check even runs.
        var token = await LoginAsPlatformAdminAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/data/some-designer");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("FORBIDDEN", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlatformSuperAdmin_CallsDatasetsListRoute_Returns403Forbidden()
    {
        var token = await LoginAsPlatformAdminAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/datasets");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("FORBIDDEN", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlatformSuperAdmin_CallsAdminRoute_Returns403_AlreadyDeniedByExistingPolicy()
    {
        // No new code path — the "platform-admin" policy on /api/admin/* requires a
        // "platform-admin" role claim, which a platform-super-admin token never carries.
        var token = await LoginAsPlatformAdminAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/users");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record LoginResponseDto(string AccessToken, string? RefreshToken, int ExpiresIn);
}
