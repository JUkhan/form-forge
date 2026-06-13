using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Infrastructure.Persistence;
using FormForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;

namespace FormForge.Api.Tests.Features.Auth;

[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class AuthIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public AuthIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

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

        // The Postgres container is shared via IClassFixture across test methods. Truncate
        // before each test so prior runs don't pollute row counts / unique-constraint state.
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE refresh_tokens, users RESTART IDENTITY CASCADE;");

        await SeedTestUserAsync(db);

        // Disable automatic cookie management so manual Cookie headers are not
        // overridden by the container (critical for replay-detection tests).
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
    public async Task Login_ValidCredentials_Returns200WithTokens()
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email = "test@example.com", password = "Password1!" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrEmpty(body.AccessToken));
        Assert.False(string.IsNullOrEmpty(body.RefreshToken));
        Assert.Equal(900, body.ExpiresIn);
    }

    [Fact]
    public async Task Login_ValidCredentials_SetsRefreshTokenCookie()
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email = "test@example.com", password = "Password1!" });

        Assert.True(response.Headers.Contains("Set-Cookie"));
        var setCookie = response.Headers.GetValues("Set-Cookie").First();
        Assert.Contains("refresh_token=", setCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/auth", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401WithInvalidCredentialsCode()
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email = "test@example.com", password = "WrongPassword!" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("INVALID_CREDENTIALS", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ACCOUNT_INACTIVE", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_UnknownEmail_Returns401_SameShapeAsWrongPassword()
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email = "nobody@nowhere.invalid", password = "anything" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("INVALID_CREDENTIALS", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_InactiveUser_Returns403WithAccountInactiveCode()
    {
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            if (!await db.Users.AnyAsync(u => u.Email == "inactive@example.com"))
            {
                db.Users.Add(new User
                {
                    Email = "inactive@example.com",
                    DisplayName = "Inactive",
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password1!", 12),
                    IsActive = false,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync();
            }
        }

        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email = "inactive@example.com", password = "Password1!" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ACCOUNT_INACTIVE", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_ValidationError_Returns422()
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email = "not-an-email", password = string.Empty });

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, (int)response.StatusCode);
    }

    [Fact]
    public async Task Login_EmptyBody_Returns400NotUnhandled500()
    {
        using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var response = await _client!.PostAsync(new Uri("/api/auth/login", UriKind.Relative), content);

        // ValidationFilter now returns 400 (rather than letting the handler throw
        // NRE → 500) when the JSON body fails to bind to LoginRequest.
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest
            || response.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"Expected 400 or 422, got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task Login_TrimsWhitespaceInEmailBeforeLookup()
    {
        // A stray trailing space from a mobile keyboard must not surface as INVALID_CREDENTIALS.
        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email = " test@example.com ", password = "Password1!" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_AccessToken_IsValidJwt()
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email = "test@example.com", password = "Password1!" });

        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotNull(body);
        var parts = body.AccessToken.Split('.');
        Assert.Equal(3, parts.Length);
    }

    [Fact]
    public async Task Response_CarriesSecurityHeaders()
    {
        using var response = await _client!.GetAsync(new Uri("/", UriKind.Relative));

        Assert.True(response.Headers.Contains("X-Frame-Options")
            || response.Content.Headers.Contains("X-Frame-Options"));
    }

    [Fact]
    public async Task Refresh_ValidCookie_Returns200WithNewTokens()
    {
        var (_, rawToken) = await LoginAndGetTokensAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        request.Headers.Add("Cookie", $"refresh_token={rawToken}");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrEmpty(body.AccessToken));
        Assert.False(string.IsNullOrEmpty(body.RefreshToken));
    }

    [Fact]
    public async Task Refresh_ValidCookie_IssuesNewRefreshToken()
    {
        var (_, rawToken) = await LoginAndGetTokensAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        request.Headers.Add("Cookie", $"refresh_token={rawToken}");
        using var response = await _client!.SendAsync(request);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotEqual(rawToken, body!.RefreshToken);
    }

    [Fact]
    public async Task Refresh_MissingCookie_Returns401WithRefreshTokenInvalidCode()
    {
        using var response = await _client!.PostAsync(
            new Uri("/api/auth/refresh", UriKind.Relative), content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        // AC-2: missing-cookie shares the same envelope as other invalid cases.
        Assert.Contains("REFRESH_TOKEN_INVALID", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_ExpiredToken_Returns401WithRefreshTokenInvalidCode()
    {
        var (_, rawToken) = await LoginAndGetTokensAsync();

        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var token = await db.RefreshTokens.FirstAsync(r => r.RevokedAt == null);
            token.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        request.Headers.Add("Cookie", $"refresh_token={rawToken}");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("REFRESH_TOKEN_INVALID", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_ReplayedToken_Returns401WithRefreshTokenInvalidCode()
    {
        var (_, rawToken) = await LoginAndGetTokensAsync();

        // First call — succeeds, revokes rawToken
        using var firstRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        firstRequest.Headers.Add("Cookie", $"refresh_token={rawToken}");
        using var firstResponse = await _client!.SendAsync(firstRequest);
        firstResponse.EnsureSuccessStatusCode();

        // Second call with the same (now-revoked) token — AC-2 mandates the same
        // public envelope as any other invalid case. Replay differentiation lives
        // only in the server's Warning log + replayed metric counter.
        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        replayRequest.Headers.Add("Cookie", $"refresh_token={rawToken}");
        using var replayResponse = await _client!.SendAsync(replayRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, replayResponse.StatusCode);
        var body = await replayResponse.Content.ReadAsStringAsync();
        Assert.Contains("REFRESH_TOKEN_INVALID", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logout_ValidCookie_Returns204AndRevokesToken()
    {
        var (_, rawToken) = await LoginAndGetTokensAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Add("Cookie", $"refresh_token={rawToken}");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var tokenHash = HashTokenForTest(rawToken);
        var row = await db.RefreshTokens.AsNoTracking().FirstAsync(r => r.TokenHash == tokenHash);
        Assert.NotNull(row.RevokedAt);
    }

    [Fact]
    public async Task Logout_ValidCookie_ClearsRefreshCookie()
    {
        var (_, rawToken) = await LoginAndGetTokensAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Add("Cookie", $"refresh_token={rawToken}");
        using var response = await _client!.SendAsync(request);

        Assert.True(response.Headers.Contains("Set-Cookie"));
        var setCookie = response.Headers.GetValues("Set-Cookie").First();
        Assert.Contains("refresh_token=", setCookie, StringComparison.Ordinal);
        Assert.Contains("path=/api/auth", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            setCookie.Contains("max-age=0", StringComparison.OrdinalIgnoreCase)
            || setCookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase),
            $"Cookie did not signal immediate expiry: {setCookie}");
    }

    [Fact]
    public async Task Logout_NoCookie_Returns204_NotChallenge()
    {
        using var response = await _client!.PostAsync(new Uri("/api/auth/logout", UriKind.Relative), content: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(string.Empty, body);

        // AC-3: response still emits cookie-clearing Set-Cookie on the no-cookie NoOp branch
        // (defense in depth — a stale browser-jar cookie the server did not see still gets overwritten).
        Assert.True(response.Headers.Contains("Set-Cookie"));
        var setCookie = response.Headers.GetValues("Set-Cookie").First();
        Assert.Contains("refresh_token=", setCookie, StringComparison.Ordinal);
        Assert.True(
            setCookie.Contains("max-age=0", StringComparison.OrdinalIgnoreCase)
            || setCookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase),
            $"Cookie did not signal immediate expiry: {setCookie}");
    }

    [Fact]
    public async Task Logout_UnknownToken_Returns204()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Add("Cookie", "refresh_token=this-token-does-not-exist-in-the-database");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // AC-3: cookie-clearing Set-Cookie is unconditional, including the unknown-token NoOp branch.
        Assert.True(response.Headers.Contains("Set-Cookie"));
        var setCookie = response.Headers.GetValues("Set-Cookie").First();
        Assert.Contains("refresh_token=", setCookie, StringComparison.Ordinal);
        Assert.True(
            setCookie.Contains("max-age=0", StringComparison.OrdinalIgnoreCase)
            || setCookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase),
            $"Cookie did not signal immediate expiry: {setCookie}");
    }

    [Fact]
    public async Task Logout_AlreadyRevokedToken_Returns204_NoSecondRevoke()
    {
        var (_, rawToken) = await LoginAndGetTokensAsync();

        using var first = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        first.Headers.Add("Cookie", $"refresh_token={rawToken}");
        using var firstResponse = await _client!.SendAsync(first);
        Assert.Equal(HttpStatusCode.NoContent, firstResponse.StatusCode);

        DateTimeOffset? firstRevokedAt;
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var hash = HashTokenForTest(rawToken);
            firstRevokedAt = (await db.RefreshTokens.AsNoTracking().FirstAsync(r => r.TokenHash == hash)).RevokedAt;
        }
        Assert.NotNull(firstRevokedAt);

        using var second = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        second.Headers.Add("Cookie", $"refresh_token={rawToken}");
        using var secondResponse = await _client!.SendAsync(second);
        Assert.Equal(HttpStatusCode.NoContent, secondResponse.StatusCode);

        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var hash = HashTokenForTest(rawToken);
            var row = await db.RefreshTokens.AsNoTracking().FirstAsync(r => r.TokenHash == hash);
            Assert.Equal(firstRevokedAt, row.RevokedAt);
        }
    }

    [Fact]
    public async Task Logout_ThenRefresh_WithSameToken_Returns401Invalid()
    {
        var (_, rawToken) = await LoginAndGetTokensAsync();

        using var logoutReq = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logoutReq.Headers.Add("Cookie", $"refresh_token={rawToken}");
        using var logoutResp = await _client!.SendAsync(logoutReq);
        Assert.Equal(HttpStatusCode.NoContent, logoutResp.StatusCode);

        using var refreshReq = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        refreshReq.Headers.Add("Cookie", $"refresh_token={rawToken}");
        using var refreshResp = await _client!.SendAsync(refreshReq);

        Assert.Equal(HttpStatusCode.Unauthorized, refreshResp.StatusCode);
        var body = await refreshResp.Content.ReadAsStringAsync();
        Assert.Contains("REFRESH_TOKEN_INVALID", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logout_DoesNotRequireBearerToken()
    {
        var (_, rawToken) = await LoginAndGetTokensAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Add("Cookie", $"refresh_token={rawToken}");
        // Deliberately no Authorization header.
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<(string AccessToken, string RawRefreshToken)> LoginAndGetTokensAsync()
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email = "test@example.com", password = "Password1!" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        return (body!.AccessToken, body.RefreshToken);
    }

    private static string HashTokenForTest(string raw) =>
        Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(raw)))
            .ToLowerInvariant();

    private static async Task SeedTestUserAsync(FormForgeDbContext db)
    {
        if (!await db.Users.AnyAsync(u => u.Email == "test@example.com"))
        {
            db.Users.Add(new User
            {
                Email = "test@example.com",
                DisplayName = "Test User",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password1!", 12),
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Login_Returns_UserProfile_WithThemePreference()
    {
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var u = await db.Users.FirstAsync(u => u.Email == "test@example.com");
            u.ThemePreference = "solarized";
            await db.SaveChangesAsync();
        }

        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email = "test@example.com", password = "Password1!" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var user = body.GetProperty("user");
        Assert.Equal("test@example.com", user.GetProperty("email").GetString());
        Assert.Equal("solarized", user.GetProperty("themePreference").GetString());
    }

    [Fact]
    public async Task Refresh_Returns_UserProfile_WithThemePreference()
    {
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var u = await db.Users.FirstAsync(u => u.Email == "test@example.com");
            u.ThemePreference = "slate-dark";
            await db.SaveChangesAsync();
        }

        var (_, rawToken) = await LoginAndGetTokensAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        request.Headers.Add("Cookie", $"refresh_token={rawToken}");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var user = body.GetProperty("user");
        Assert.Equal("test@example.com", user.GetProperty("email").GetString());
        Assert.Equal("slate-dark", user.GetProperty("themePreference").GetString());
    }

    // ---- Story 2.14: TOTP MFA verification on login ----

    [Fact]
    public async Task Login_MfaEnabled_Returns200WithMfaRequired_NoJwtIssued()
    {
        var mfaStart = await SetupMfaAndStartLoginAsync();

        Assert.True(mfaStart.MfaRequired);
        Assert.False(string.IsNullOrEmpty(mfaStart.MfaSessionToken));
    }

    [Fact]
    public async Task Login_MfaEnabled_NoRefreshCookieSet()
    {
        var (accessToken, _) = await LoginAndGetTokensAsync();
        var enrol = await EnrolMfaAsync(accessToken);
        await ConfirmMfaEnrolAsync(accessToken, new Totp(Base32Encoding.ToBytes(enrol.Secret)).ComputeTotp());

        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email = "test@example.com", password = "Password1!" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // No refresh cookie at the password step — tokens are issued only after MFA verify.
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            Assert.DoesNotContain(cookies, c => c.Contains("refresh_token=", StringComparison.Ordinal));
        }

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("accessToken", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MfaVerify_ValidTotpCode_Returns200WithTokensAndSetsCookie()
    {
        var (accessToken, _) = await LoginAndGetTokensAsync();
        var enrol = await EnrolMfaAsync(accessToken);
        await ConfirmMfaEnrolAsync(accessToken, new Totp(Base32Encoding.ToBytes(enrol.Secret)).ComputeTotp());

        var mfaStart = await StartMfaLoginAsync("test@example.com", "Password1!");

        var code = new Totp(Base32Encoding.ToBytes(enrol.Secret)).ComputeTotp();
        using var response = await _client!.PostAsJsonAsync("/api/auth/mfa/verify",
            new { mfaSessionToken = mfaStart.MfaSessionToken, code });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrEmpty(body.AccessToken));
        Assert.False(string.IsNullOrEmpty(body.RefreshToken));

        Assert.True(response.Headers.Contains("Set-Cookie"));
        var setCookie = response.Headers.GetValues("Set-Cookie").First();
        Assert.Contains("refresh_token=", setCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MfaVerify_ValidBackupCode_Returns200_SetsUsedAt()
    {
        var (accessToken, _) = await LoginAndGetTokensAsync();
        var enrol = await EnrolMfaAsync(accessToken);
        await ConfirmMfaEnrolAsync(accessToken, new Totp(Base32Encoding.ToBytes(enrol.Secret)).ComputeTotp());

        var mfaStart = await StartMfaLoginAsync("test@example.com", "Password1!");

        var backupCode = enrol.BackupCodes[0];
        using var response = await _client!.PostAsJsonAsync("/api/auth/mfa/verify",
            new { mfaSessionToken = mfaStart.MfaSessionToken, code = backupCode });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.False(string.IsNullOrEmpty(body!.AccessToken));

        // Exactly one backup-code row is now stamped used (audit trail — not deleted).
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == "test@example.com");
        var usedCount = await db.MfaBackupCodes.CountAsync(c => c.UserId == user.Id && c.UsedAt != null);
        Assert.Equal(1, usedCount);
    }

    [Fact]
    public async Task MfaVerify_InvalidSession_Returns401_MfaSessionInvalid()
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/mfa/verify",
            new { mfaSessionToken = "deadbeefdeadbeefdeadbeefdeadbeef", code = "123456" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("MFA_SESSION_INVALID", problem.GetProperty("code").GetString());
        Assert.Equal("auth.mfaSessionInvalid", problem.GetProperty("messageKey").GetString());
    }

    [Fact]
    public async Task MfaVerify_5ConsecutiveWrongCodes_LastEvictsSession()
    {
        var (accessToken, _) = await LoginAndGetTokensAsync();
        var enrol = await EnrolMfaAsync(accessToken);
        await ConfirmMfaEnrolAsync(accessToken, new Totp(Base32Encoding.ToBytes(enrol.Secret)).ComputeTotp());

        var mfaStart = await StartMfaLoginAsync("test@example.com", "Password1!");

        var validNow = new Totp(Base32Encoding.ToBytes(enrol.Secret)).ComputeTotp();
        var wrongCode = validNow == "000000" ? "111111" : "000000";

        // First four wrong codes — the session is preserved so the user can retry.
        for (var i = 0; i < 4; i++)
        {
            using var wrong = await _client!.PostAsJsonAsync("/api/auth/mfa/verify",
                new { mfaSessionToken = mfaStart.MfaSessionToken, code = wrongCode });
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
            var p = await wrong.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.Equal("MFA_CODE_INVALID", p.GetProperty("code").GetString());
        }

        // Fifth wrong code — still MFA_CODE_INVALID, but the session is now evicted.
        using (var fifth = await _client!.PostAsJsonAsync("/api/auth/mfa/verify",
            new { mfaSessionToken = mfaStart.MfaSessionToken, code = wrongCode }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, fifth.StatusCode);
            var p = await fifth.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.Equal("MFA_CODE_INVALID", p.GetProperty("code").GetString());
        }

        // A subsequent correct code fails because the session was evicted on the 5th failure.
        var freshValid = new Totp(Base32Encoding.ToBytes(enrol.Secret)).ComputeTotp();
        using var afterEvict = await _client!.PostAsJsonAsync("/api/auth/mfa/verify",
            new { mfaSessionToken = mfaStart.MfaSessionToken, code = freshValid });
        Assert.Equal(HttpStatusCode.Unauthorized, afterEvict.StatusCode);
        var pe = await afterEvict.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("MFA_SESSION_INVALID", pe.GetProperty("code").GetString());
    }

    [Fact]
    public async Task MfaVerify_AlreadyUsedBackupCode_Returns401()
    {
        var (accessToken, _) = await LoginAndGetTokensAsync();
        var enrol = await EnrolMfaAsync(accessToken);
        await ConfirmMfaEnrolAsync(accessToken, new Totp(Base32Encoding.ToBytes(enrol.Secret)).ComputeTotp());

        var backupCode = enrol.BackupCodes[0];

        // First use — succeeds and stamps used_at.
        var firstStart = await StartMfaLoginAsync("test@example.com", "Password1!");
        using (var first = await _client!.PostAsJsonAsync("/api/auth/mfa/verify",
            new { mfaSessionToken = firstStart.MfaSessionToken, code = backupCode }))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        // Second login, same backup code — already used → MFA_CODE_INVALID (no info leak).
        var secondStart = await StartMfaLoginAsync("test@example.com", "Password1!");
        using var second = await _client!.PostAsJsonAsync("/api/auth/mfa/verify",
            new { mfaSessionToken = secondStart.MfaSessionToken, code = backupCode });

        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        var problem = await second.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("MFA_CODE_INVALID", problem.GetProperty("code").GetString());
    }

    // ---- Story 2.14 review hardening ----

    [Fact]
    public async Task MfaVerify_MalformedCode_Returns422_BeforeService()
    {
        // A 7-character numeric code matches neither the 6-digit TOTP shape nor the
        // 8-char backup-code shape, so the validator rejects it (422) before the
        // bcrypt-amplifying backup-code branch ever runs. No session is needed.
        using var response = await _client!.PostAsJsonAsync("/api/auth/mfa/verify",
            new { mfaSessionToken = "deadbeefdeadbeefdeadbeefdeadbeef", code = "1234567" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task MfaVerify_UserDeletedBeforeVerify_Returns401_NotServerError()
    {
        var (accessToken, _) = await LoginAndGetTokensAsync();
        var enrol = await EnrolMfaAsync(accessToken);
        await ConfirmMfaEnrolAsync(accessToken, new Totp(Base32Encoding.ToBytes(enrol.Secret)).ComputeTotp());
        var mfaStart = await StartMfaLoginAsync("test@example.com", "Password1!");

        // Delete the user after the MFA session is minted (admin race). The deleted-user
        // path must surface as 401, never a generic 500. (This hits VerifyMfaLogin's own
        // user-null guard; the same 401 contract covers the narrower CompleteMfaLoginAsync
        // race, which is not deterministically reachable from a black-box test.)
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            await db.Users.Where(u => u.Email == "test@example.com").ExecuteDeleteAsync();
        }

        var code = new Totp(Base32Encoding.ToBytes(enrol.Secret)).ComputeTotp();
        using var response = await _client!.PostAsJsonAsync("/api/auth/mfa/verify",
            new { mfaSessionToken = mfaStart.MfaSessionToken, code });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("MFA_SESSION_INVALID", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task MfaVerify_ConcurrentSameToken_IssuesTokensOnce()
    {
        var (accessToken, _) = await LoginAndGetTokensAsync();
        var enrol = await EnrolMfaAsync(accessToken);
        await ConfirmMfaEnrolAsync(accessToken, new Totp(Base32Encoding.ToBytes(enrol.Secret)).ComputeTotp());
        var mfaStart = await StartMfaLoginAsync("test@example.com", "Password1!");

        var code = new Totp(Base32Encoding.ToBytes(enrol.Secret)).ComputeTotp();

        // Two verifies for the SAME single-use session, racing the same valid TOTP. The
        // per-token gate must serialize them so exactly one mints a JWT pair; the loser
        // sees the already-consumed session as invalid (401).
        var statuses = await Task.WhenAll(
            VerifyMfaStatusAsync(mfaStart.MfaSessionToken, code),
            VerifyMfaStatusAsync(mfaStart.MfaSessionToken, code));

        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Contains(HttpStatusCode.Unauthorized, statuses);
    }

    [Fact]
    public async Task MfaVerify_ConcurrentSameBackupCode_StampsOnce()
    {
        var (accessToken, _) = await LoginAndGetTokensAsync();
        var enrol = await EnrolMfaAsync(accessToken);
        await ConfirmMfaEnrolAsync(accessToken, new Totp(Base32Encoding.ToBytes(enrol.Secret)).ComputeTotp());

        // Two independent sessions (different tokens) racing the SAME backup code. The
        // per-token gate does not serialize different tokens, so only the DB-level atomic
        // stamp (WHERE used_at IS NULL) stops a single-use code authenticating twice.
        var startA = await StartMfaLoginAsync("test@example.com", "Password1!");
        var startB = await StartMfaLoginAsync("test@example.com", "Password1!");
        var backupCode = enrol.BackupCodes[0];

        var statuses = await Task.WhenAll(
            VerifyMfaStatusAsync(startA.MfaSessionToken, backupCode),
            VerifyMfaStatusAsync(startB.MfaSessionToken, backupCode));

        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Contains(HttpStatusCode.Unauthorized, statuses);

        // Exactly one backup-code row is stamped used — the concurrent double-spend lost.
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == "test@example.com");
        var usedCount = await db.MfaBackupCodes.CountAsync(c => c.UserId == user.Id && c.UsedAt != null);
        Assert.Equal(1, usedCount);
    }

    // ---- Story 2.14 helpers ----

    private async Task<MfaLoginStartDto> SetupMfaAndStartLoginAsync()
    {
        var (accessToken, _) = await LoginAndGetTokensAsync();
        var enrol = await EnrolMfaAsync(accessToken);
        await ConfirmMfaEnrolAsync(accessToken, new Totp(Base32Encoding.ToBytes(enrol.Secret)).ComputeTotp());
        return await StartMfaLoginAsync("test@example.com", "Password1!");
    }

    private async Task<MfaEnrolDto> EnrolMfaAsync(string bearerToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/users/me/mfa/enrol");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        using var response = await _client!.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<MfaEnrolDto>();
        return body!;
    }

    private async Task ConfirmMfaEnrolAsync(string bearerToken, string code)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/users/me/mfa/verify")
        {
            Content = JsonContent.Create(new { code }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        using var response = await _client!.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private async Task<MfaLoginStartDto> StartMfaLoginAsync(string email, string password)
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MfaLoginStartDto>();
        return body!;
    }

    // Posts a single mfa/verify and returns just the status code. Disposes the response
    // internally so concurrent callers don't pass HttpResponseMessage-producing tasks
    // into Task.WhenAll (which trips a buggy disposable-analyzer).
    private async Task<HttpStatusCode> VerifyMfaStatusAsync(string mfaSessionToken, string code)
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/mfa/verify",
            new { mfaSessionToken, code });
        return response.StatusCode;
    }

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record LoginResponseDto(string AccessToken, string RefreshToken, int ExpiresIn);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record MfaEnrolDto(string Secret, string QrCodeDataUrl, string[] BackupCodes);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record MfaLoginStartDto(bool MfaRequired, string MfaSessionToken);
}
