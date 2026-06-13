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
using OtpNet;

namespace FormForge.Api.Tests.Features.Users;

[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class MeIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public MeIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

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

        await ReseedSystemRoleAsync(db);
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
    public async Task PutMyPreferences_ValidTheme_Returns204AndPersists()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/users/me/preferences")
        {
            Content = JsonContent.Create(new { themePreference = "slate-dark" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == "viewer@example.com");
        Assert.Equal("slate-dark", user.ThemePreference);
    }

    [Fact]
    public async Task PutMyPreferences_Unauthenticated_Returns401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/users/me/preferences")
        {
            Content = JsonContent.Create(new { themePreference = "solarized" }),
        };
        // No Authorization header
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PutMyPreferences_UnknownTheme_Returns422()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/users/me/preferences")
        {
            Content = JsonContent.Create(new { themePreference = "hacker-green" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task PutMyPreferences_NullTheme_Returns204AndClearsPreference()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");

        // First set to a theme
        using (var setReq = new HttpRequestMessage(HttpMethod.Put, "/api/users/me/preferences")
        {
            Content = JsonContent.Create(new { themePreference = "solarized" }),
        })
        {
            setReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var setResp = await _client!.SendAsync(setReq);
            Assert.Equal(HttpStatusCode.NoContent, setResp.StatusCode);
        }

        // Then clear it
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/users/me/preferences")
        {
            Content = JsonContent.Create(new { themePreference = (string?)null }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == "viewer@example.com");
        Assert.Null(user.ThemePreference);
    }

    [Fact]
    public async Task ChangePassword_WrongCurrentPassword_Returns401WithCode()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/users/me/password")
        {
            Content = JsonContent.Create(new { currentPassword = "WrongPassword1!", newPassword = "BrandNew1!" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CURRENT_PASSWORD_INCORRECT", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ChangePassword_NewPasswordTooShort_Returns422()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/users/me/password")
        {
            Content = JsonContent.Create(new { currentPassword = "Password1!", newPassword = "short" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_SameAsCurrent_Returns422()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/users/me/password")
        {
            Content = JsonContent.Create(new { currentPassword = "Password1!", newPassword = "Password1!" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("auth.passwordSameAsCurrent", problem.GetProperty("messageKey").GetString());
    }

    [Fact]
    public async Task ChangePassword_Unauthenticated_Returns401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/users/me/password")
        {
            Content = JsonContent.Create(new { currentPassword = "Password1!", newPassword = "BrandNew1!" }),
        };
        // No Authorization header
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_Valid_Returns200UpdatesHashAndAllowsLoginWithNewPassword()
    {
        var (token, _) = await LoginFullAsync("viewer@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/users/me/password")
        {
            Content = JsonContent.Create(new { currentPassword = "Password1!", newPassword = "BrandNew1!" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The stored hash now verifies the new password and rejects the old one.
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == "viewer@example.com");
            Assert.True(BCrypt.Net.BCrypt.Verify("BrandNew1!", user.PasswordHash));
            Assert.False(BCrypt.Net.BCrypt.Verify("Password1!", user.PasswordHash));
            Assert.NotNull(user.UpdatedAt);
        }

        // The new password works on a fresh login; the old one no longer does.
        using var goodLogin = await _client!.PostAsJsonAsync(
            "/api/auth/login", new { email = "viewer@example.com", password = "BrandNew1!" });
        Assert.Equal(HttpStatusCode.OK, goodLogin.StatusCode);

        using var staleLogin = await _client!.PostAsJsonAsync(
            "/api/auth/login", new { email = "viewer@example.com", password = "Password1!" });
        Assert.Equal(HttpStatusCode.Unauthorized, staleLogin.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_RevokesOtherSessionsButKeepsCurrent()
    {
        // Two concurrent sessions → two active refresh tokens.
        var (_, rawRefreshA) = await LoginFullAsync("viewer@example.com", "Password1!");
        var (tokenB, _) = await LoginFullAsync("viewer@example.com", "Password1!");

        // Change password from session B, presenting session A's refresh token as
        // the "current session" cookie — A must survive, B (and any other) revoked.
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/users/me/password")
        {
            Content = JsonContent.Create(new { currentPassword = "Password1!", newPassword = "BrandNew1!" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);
        request.Headers.Add("Cookie", $"refresh_token={rawRefreshA}");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == "viewer@example.com");
        var activeCount = await db.RefreshTokens
            .CountAsync(r => r.UserId == user.Id && r.RevokedAt == null);

        // Exactly the excluded current session (A) remains active.
        Assert.Equal(1, activeCount);
    }

    // ---- Story 2.13: TOTP MFA enrolment ----

    [Fact]
    public async Task GetMfaStatus_Unauthenticated_Returns401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/users/me/mfa/status");
        // No Authorization header
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetMfaStatus_AuthenticatedUser_DefaultsToFalse()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/users/me/mfa/status");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("mfaEnabled").GetBoolean());
    }

    [Fact]
    public async Task EnrolMfa_Unauthenticated_Returns401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/users/me/mfa/enrol");
        // No Authorization header
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EnrolMfa_Authenticated_ReturnsSecretQrAndEightBackupCodes()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");

        var enrol = await EnrolAsync(token);

        Assert.False(string.IsNullOrWhiteSpace(enrol.Secret));
        Assert.StartsWith("data:image/png;base64,", enrol.QrCodeDataUrl, StringComparison.Ordinal);
        Assert.Equal(8, enrol.BackupCodes.Length);
        Assert.All(enrol.BackupCodes, code =>
        {
            Assert.Equal(8, code.Length);
            Assert.Matches("^[A-Z0-9]{8}$", code);
        });

        // Enrolment alone must NOT persist anything — mfa stays disabled until verify.
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == "viewer@example.com");
        Assert.False(user.MfaEnabled);
        Assert.Null(user.MfaSecretProtected);
        Assert.Equal(0, await db.MfaBackupCodes.CountAsync(c => c.UserId == user.Id));
    }

    [Fact]
    public async Task VerifyEnrolment_NoPriorEnrol_Returns400_NoPendingEnrolment()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/users/me/mfa/verify")
        {
            Content = JsonContent.Create(new { code = "123456" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MFA_NO_PENDING_ENROLMENT", problem.GetProperty("code").GetString());
        Assert.Equal("auth.mfaNoPendingEnrolment", problem.GetProperty("messageKey").GetString());
    }

    [Fact]
    public async Task VerifyEnrolment_InvalidCode_Returns400_MfaCodeInvalid()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");
        await EnrolAsync(token);

        // "000000" is overwhelmingly unlikely to be the current TOTP; even if it were,
        // the assertion would flake at ~1/10^6 — acceptable for an integration test.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/users/me/mfa/verify")
        {
            Content = JsonContent.Create(new { code = "000000" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MFA_CODE_INVALID", problem.GetProperty("code").GetString());
        Assert.Equal("auth.mfaCodeInvalid", problem.GetProperty("messageKey").GetString());

        // Nothing persisted on a wrong code.
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == "viewer@example.com");
        Assert.False(user.MfaEnabled);
        Assert.Null(user.MfaSecretProtected);
    }

    [Fact]
    public async Task VerifyEnrolment_ValidCode_Returns200_SetsMfaEnabled_WritesBackupCodes()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");
        var enrol = await EnrolAsync(token);

        var code = new Totp(Base32Encoding.ToBytes(enrol.Secret)).ComputeTotp();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/users/me/mfa/verify")
        {
            Content = JsonContent.Create(new { code }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == "viewer@example.com");
        Assert.True(user.MfaEnabled);
        Assert.NotNull(user.MfaSecretProtected);
        Assert.NotNull(user.UpdatedAt);

        var codes = await db.MfaBackupCodes.Where(c => c.UserId == user.Id).ToListAsync();
        Assert.Equal(8, codes.Count);
        // Backup codes are bcrypt-hashed at rest — never the raw value.
        Assert.All(codes, c => Assert.DoesNotContain(c.CodeHash, enrol.BackupCodes));

        // The status endpoint now reflects the enabled state.
        using var statusReq = new HttpRequestMessage(HttpMethod.Get, "/api/users/me/mfa/status");
        statusReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var statusResp = await _client!.SendAsync(statusReq);
        var statusBody = await statusResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(statusBody.GetProperty("mfaEnabled").GetBoolean());
    }

    [Fact]
    public async Task VerifyEnrolment_ReEnrolment_ReplacesOldBackupCodesAtomically()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");

        // First enrolment.
        var firstEnrol = await EnrolAsync(token);
        await VerifyAsync(token, new Totp(Base32Encoding.ToBytes(firstEnrol.Secret)).ComputeTotp());

        Guid[] firstCodeIds;
        byte[]? firstSecret;
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == "viewer@example.com");
            firstSecret = user.MfaSecretProtected;
            firstCodeIds = await db.MfaBackupCodes
                .Where(c => c.UserId == user.Id)
                .Select(c => c.Id)
                .ToArrayAsync();
        }
        Assert.Equal(8, firstCodeIds.Length);

        // Re-enrolment with a fresh secret.
        var secondEnrol = await EnrolAsync(token);
        Assert.NotEqual(firstEnrol.Secret, secondEnrol.Secret);
        await VerifyAsync(token, new Totp(Base32Encoding.ToBytes(secondEnrol.Secret)).ComputeTotp());

        using var scope2 = _factory!.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var user2 = await db2.Users.FirstAsync(u => u.Email == "viewer@example.com");

        // Secret rotated.
        Assert.NotNull(user2.MfaSecretProtected);
        Assert.False((firstSecret ?? []).AsSpan().SequenceEqual(user2.MfaSecretProtected));

        // Exactly 8 backup codes, and NONE of the original rows survive (old set deleted).
        var secondCodeIds = await db2.MfaBackupCodes
            .Where(c => c.UserId == user2.Id)
            .Select(c => c.Id)
            .ToArrayAsync();
        Assert.Equal(8, secondCodeIds.Length);
        Assert.Empty(secondCodeIds.Intersect(firstCodeIds));
    }

    private async Task<MfaEnrolResponseDto> EnrolAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/users/me/mfa/enrol");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<MfaEnrolResponseDto>();
        return body!;
    }

    private async Task VerifyAsync(string token, string code)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/users/me/mfa/verify")
        {
            Content = JsonContent.Create(new { code }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private async Task<string> LoginAsync(string email, string password)
    {
        var (accessToken, _) = await LoginFullAsync(email, password);
        return accessToken;
    }

    private async Task<(string AccessToken, string RefreshToken)> LoginFullAsync(string email, string password)
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login", new { email, password });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        return (body!.AccessToken, body.RefreshToken);
    }

    private static async Task ReseedSystemRoleAsync(FormForgeDbContext db)
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
            await db.SaveChangesAsync();
        }
    }

    private static async Task SeedTestUsersAsync(FormForgeDbContext db)
    {
        if (!await db.Users.AnyAsync(u => u.Email == "viewer@example.com"))
        {
            db.Users.Add(new User
            {
                Email = "viewer@example.com",
                DisplayName = "Viewer User",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password1!", 12),
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
    }

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record LoginResponseDto(string AccessToken, string RefreshToken, int ExpiresIn);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record MfaEnrolResponseDto(string Secret, string QrCodeDataUrl, string[] BackupCodes);
}
