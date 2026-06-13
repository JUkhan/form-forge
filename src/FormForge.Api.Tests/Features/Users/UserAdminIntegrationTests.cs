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
using OtpNet;

namespace FormForge.Api.Tests.Features.Users;

[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class UserAdminIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    private Guid _adminUserId;
    private Guid _viewerUserId;

    public UserAdminIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

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

        await ReseedSystemRolesAsync(db);
        (_adminUserId, _viewerUserId) = await SeedTestUsersAsync(db);

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

    // ---------- AC-1: list ----------

    [Fact]
    public async Task GetUsers_AsPlatformAdmin_Returns200WithPagedList()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/users?page=1&pageSize=25");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PagedResultDto<UserListItemDto>>();
        Assert.NotNull(body);
        Assert.Equal(2, body.Total);
        Assert.Equal(1, body.Page);
        Assert.Equal(25, body.PageSize);
        // Ordered by email ascending: admin@example.com, viewer@example.com
        Assert.Collection(body.Data,
            u => Assert.Equal("admin@example.com", u.Email),
            u => Assert.Equal("viewer@example.com", u.Email));
        // The admin user has the platform-admin role assigned in SeedTestUsersAsync.
        Assert.Equal(1, body.Data[0].RoleCount);
        Assert.True(body.Data[0].IsActive);
    }

    [Fact]
    public async Task GetUsers_SortByEmailDesc_OrdersDescending()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/admin/users?page=1&pageSize=25&sort=email:desc");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PagedResultDto<UserListItemDto>>();
        Assert.NotNull(body);
        Assert.Collection(body.Data,
            u => Assert.Equal("viewer@example.com", u.Email),
            u => Assert.Equal("admin@example.com", u.Email));
    }

    [Fact]
    public async Task GetUsers_Search_FiltersByEmailOrDisplayName()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/admin/users?page=1&pageSize=25&search=viewer");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PagedResultDto<UserListItemDto>>();
        Assert.NotNull(body);
        Assert.Equal(1, body.Total);
        Assert.Equal("viewer@example.com", body.Data.Single().Email);
    }

    [Fact]
    public async Task GetUsers_StatusFilter_NarrowsByActiveFlag()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        // Both seeded users are active, so active → 2 and inactive → 0.
        using var activeReq = new HttpRequestMessage(
            HttpMethod.Get, "/api/admin/users?page=1&pageSize=25&status=active");
        activeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var activeResp = await _client!.SendAsync(activeReq);
        var activeBody = await activeResp.Content.ReadFromJsonAsync<PagedResultDto<UserListItemDto>>();
        Assert.NotNull(activeBody);
        Assert.Equal(2, activeBody.Total);

        using var inactiveReq = new HttpRequestMessage(
            HttpMethod.Get, "/api/admin/users?page=1&pageSize=25&status=inactive");
        inactiveReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var inactiveResp = await _client!.SendAsync(inactiveReq);
        var inactiveBody = await inactiveResp.Content.ReadFromJsonAsync<PagedResultDto<UserListItemDto>>();
        Assert.NotNull(inactiveBody);
        Assert.Equal(0, inactiveBody.Total);
    }

    [Fact]
    public async Task GetUsers_Unauthenticated_Returns401()
    {
        using var response = await _client!.GetAsync(new Uri("/api/admin/users", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetUsers_AsNonAdmin_Returns403()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/users");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- AC-1: get by id ----------

    [Fact]
    public async Task GetUser_KnownId_Returns200WithRoles()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/users/{_adminUserId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UserDetailResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("admin@example.com", body.Email);
        Assert.True(body.IsActive);
        Assert.Single(body.Roles);
        Assert.Equal("platform-admin", body.Roles[0].Name);
    }

    [Fact]
    public async Task GetUser_UnknownId_Returns404()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/users/{Guid.NewGuid()}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("USER_NOT_FOUND", body, StringComparison.Ordinal);
    }

    // ---------- AC-2: create ----------

    [Fact]
    public async Task CreateUser_ValidBody_Returns201WithLocation()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users")
        {
            Content = JsonContent.Create(new
            {
                email = "newuser@example.com",
                displayName = "New User",
                temporaryPassword = "TempPass123!",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.StartsWith("/api/admin/users/", response.Headers.Location!.ToString(), StringComparison.Ordinal);

        var body = await response.Content.ReadFromJsonAsync<UserDetailResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("newuser@example.com", body.Email);
        Assert.Equal("New User", body.DisplayName);
        Assert.True(body.IsActive);
        Assert.Empty(body.Roles);

        // Verify the password actually works (BCrypt hashed correctly).
        var newToken = await LoginAsync("newuser@example.com", "TempPass123!");
        Assert.NotEmpty(newToken);
    }

    [Fact]
    public async Task CreateUser_EmailNormalizedToLowercase()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users")
        {
            Content = JsonContent.Create(new
            {
                email = "  MIXED@Example.COM  ",
                displayName = "Mixed Case",
                temporaryPassword = "TempPass123!",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UserDetailResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("mixed@example.com", body.Email);
    }

    [Fact]
    public async Task CreateUser_DuplicateEmail_Returns409()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users")
        {
            Content = JsonContent.Create(new
            {
                email = "viewer@example.com",
                displayName = "Duplicate",
                temporaryPassword = "TempPass123!",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("USER_EMAIL_CONFLICT", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateUser_InvalidEmail_Returns422()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users")
        {
            Content = JsonContent.Create(new
            {
                email = "not-an-email",
                displayName = "Bad",
                temporaryPassword = "TempPass123!",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreateUser_ShortPassword_Returns422()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users")
        {
            Content = JsonContent.Create(new
            {
                email = "shortpw@example.com",
                displayName = "Short PW",
                temporaryPassword = "abc",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreateUser_WhitespaceDisplayName_Returns422()
    {
        // Story 2.8 review P8: ".NotEmpty()" alone accepts whitespace-only strings,
        // which the handler then trims to empty before insert. Validator must reject.
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users")
        {
            Content = JsonContent.Create(new
            {
                email = "wsname@example.com",
                displayName = "   ",
                temporaryPassword = "TempPass123!",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreateUser_WhitespacePassword_Returns422()
    {
        // Story 2.8 review P9: "MinimumLength(8)" alone accepts 8 spaces as a
        // valid password. Validator must reject whitespace-only.
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users")
        {
            Content = JsonContent.Create(new
            {
                email = "wspw@example.com",
                displayName = "Whitespace PW",
                temporaryPassword = "        ",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreateUser_AsNonAdmin_Returns403()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users")
        {
            Content = JsonContent.Create(new
            {
                email = "shouldnotbe@example.com",
                displayName = "Forbidden",
                temporaryPassword = "TempPass123!",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- Update ----------

    [Fact]
    public async Task UpdateUser_ChangeDisplayName_Returns204()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}")
        {
            Content = JsonContent.Create(new { displayName = "Renamed Viewer" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var updated = await db.Users.FirstAsync(u => u.Id == _viewerUserId);
        Assert.Equal("Renamed Viewer", updated.DisplayName);
        Assert.NotNull(updated.UpdatedAt);
    }

    [Fact]
    public async Task UpdateUser_NewPassword_TakesEffect()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}")
        {
            Content = JsonContent.Create(new { newPassword = "BrandNew123!" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Old password no longer works; new password does.
        var newToken = await LoginAsync("viewer@example.com", "BrandNew123!");
        Assert.NotEmpty(newToken);
    }

    [Fact]
    public async Task UpdateUser_EmptyBody_Returns422()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task UpdateUser_UnknownId_Returns404()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{Guid.NewGuid()}")
        {
            Content = JsonContent.Create(new { displayName = "Ghost" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateUser_NewPassword_RevokesExistingRefreshTokens()
    {
        // Story 2.8 review P6: a password rotation must invalidate every existing
        // refresh token — a leaked refresh credential from before the reset would
        // otherwise keep minting access tokens.
        var adminToken = await LoginAsync("admin@example.com", "Password1!");

        // Viewer logs in to mint a refresh token.
        var viewerLogin = await LoginRawAsync("viewer@example.com", "Password1!");
        Assert.NotEmpty(viewerLogin.RefreshToken);

        using (var updateReq = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}")
        {
            Content = JsonContent.Create(new { newPassword = "BrandNew123!" }),
        })
        {
            updateReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            using var updateResp = await _client!.SendAsync(updateReq);
            Assert.Equal(HttpStatusCode.NoContent, updateResp.StatusCode);
        }

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var liveTokens = await db.RefreshTokens
            .CountAsync(rt => rt.UserId == _viewerUserId && rt.RevokedAt == null);
        Assert.Equal(0, liveTokens);
    }

    [Fact]
    public async Task UpdateUser_SameDisplayName_DoesNotBumpUpdatedAt()
    {
        // Story 2.8 review P10: resubmitting the same displayName must NOT bump
        // UpdatedAt — audit trail must reflect real changes only.
        var token = await LoginAsync("admin@example.com", "Password1!");

        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var u = await db.Users.FirstAsync(u => u.Id == _viewerUserId);
            Assert.Null(u.UpdatedAt);
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}")
        {
            Content = JsonContent.Create(new { displayName = "Viewer User" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var verifyScope = _factory!.Services.CreateScope();
        var db2 = verifyScope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var after = await db2.Users.FirstAsync(u => u.Id == _viewerUserId);
        Assert.Null(after.UpdatedAt);
    }

    // ---------- AC-3: deactivate/reactivate cycle ----------

    [Fact]
    public async Task DeactivateUser_Returns204AndRevokesRefreshTokens()
    {
        var adminToken = await LoginAsync("admin@example.com", "Password1!");

        // Viewer logs in to mint a refresh token whose revocation we'll verify.
        var viewerLogin = await LoginRawAsync("viewer@example.com", "Password1!");
        Assert.NotEmpty(viewerLogin.RefreshToken);

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}/deactivate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var user = await db.Users.FirstAsync(u => u.Id == _viewerUserId);
        Assert.False(user.IsActive);
        Assert.NotNull(user.UpdatedAt);

        // All previously active refresh tokens for the deactivated user are revoked.
        var liveTokens = await db.RefreshTokens.CountAsync(rt => rt.UserId == _viewerUserId && rt.RevokedAt == null);
        Assert.Equal(0, liveTokens);

        // Login is blocked after deactivation (Auth flow rejects on IsActive=false).
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email = "viewer@example.com", password = "Password1!" }),
        };
        using var loginResponse = await _client!.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.Forbidden, loginResponse.StatusCode);
    }

    [Fact]
    public async Task ReactivateUser_Returns204AndAllowsLoginAgain()
    {
        var adminToken = await LoginAsync("admin@example.com", "Password1!");

        // First deactivate.
        using (var deactivateReq = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}/deactivate"))
        {
            deactivateReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            using var deactivateResp = await _client!.SendAsync(deactivateReq);
            Assert.Equal(HttpStatusCode.NoContent, deactivateResp.StatusCode);
        }

        // Now reactivate.
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}/reactivate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Login works again — the user must re-authenticate (refresh tokens were revoked).
        var newToken = await LoginAsync("viewer@example.com", "Password1!");
        Assert.NotEmpty(newToken);
    }

    [Fact]
    public async Task DeactivateUser_UnknownId_Returns404()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{Guid.NewGuid()}/deactivate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReactivateUser_UnknownId_Returns404()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{Guid.NewGuid()}/reactivate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- AC-4: self-deactivation ----------

    [Fact]
    public async Task DeactivateUser_Self_Returns409SelfDeactivation()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_adminUserId}/deactivate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("SELF_DEACTIVATION", body, StringComparison.Ordinal);

        // The admin user is still active and not touched.
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var admin = await db.Users.FirstAsync(u => u.Id == _adminUserId);
        Assert.True(admin.IsActive);
    }

    // ---------- AC-6: PermissionService reads users.is_active ----------

    [Fact]
    public async Task GetMyPermissions_AfterDeactivation_ReturnsIsActiveFalse()
    {
        // Promote viewer to platform-admin so it can hit /me/permissions and read
        // its own state. (The endpoint requires auth only; non-admin works too —
        // we use admin here so the JWT carries a role.)
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            db.UserRoles.Add(new UserRole
            {
                UserId = _viewerUserId,
                RoleId = ViewerRoleId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");

        // Initial state — IsActive=true.
        using (var initialReq = new HttpRequestMessage(HttpMethod.Get, "/api/users/me/permissions"))
        {
            initialReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
            using var initialResp = await _client!.SendAsync(initialReq);
            Assert.Equal(HttpStatusCode.OK, initialResp.StatusCode);
            var body = await initialResp.Content.ReadFromJsonAsync<PermissionsResponseDto>();
            Assert.NotNull(body);
            Assert.True(body.IsActive);
        }

        // Admin deactivates the viewer; UserDeactivated event busts the cache.
        var adminToken = await LoginAsync("admin@example.com", "Password1!");
        using (var deactivateReq = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}/deactivate"))
        {
            deactivateReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            using var deactivateResp = await _client!.SendAsync(deactivateReq);
            Assert.Equal(HttpStatusCode.NoContent, deactivateResp.StatusCode);
        }

        // The viewer's existing JWT is still valid (15-min TTL). Now /me/permissions
        // must reflect IsActive=false because PermissionService re-reads users.is_active.
        using var finalReq = new HttpRequestMessage(HttpMethod.Get, "/api/users/me/permissions");
        finalReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
        using var finalResp = await _client!.SendAsync(finalReq);
        Assert.Equal(HttpStatusCode.OK, finalResp.StatusCode);
        var finalBody = await finalResp.Content.ReadFromJsonAsync<PermissionsResponseDto>();
        Assert.NotNull(finalBody);
        Assert.False(finalBody.IsActive);
    }

    // ---------- Story 2.15: Admin MFA Reset ----------

    [Fact]
    public async Task ResetMfa_Returns200AndClearsMfaState()
    {
        // Setup: enrol MFA for the viewer user
        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");
        var enrol = await EnrolMfaAsync(viewerToken);
        var confirmCode = new Totp(Base32Encoding.ToBytes(enrol.Secret)).ComputeTotp();
        await ConfirmMfaEnrolAsync(viewerToken, confirmCode);

        // Admin resets MFA
        var adminToken = await LoginAsync("admin@example.com", "Password1!");
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/users/{_viewerUserId}/mfa");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify DB state: mfa_enabled=false, mfa_secret=null, backup codes deleted
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var viewer = await db.Users.FirstAsync(u => u.Id == _viewerUserId);
        Assert.False(viewer.MfaEnabled);
        Assert.Null(viewer.MfaSecretProtected);
        var backupCodeCount = await db.MfaBackupCodes.CountAsync(c => c.UserId == _viewerUserId);
        Assert.Equal(0, backupCodeCount);
    }

    [Fact]
    public async Task ResetMfa_RevokesAllRefreshTokens()
    {
        // Setup: enrol MFA, then get a refresh token by logging in again after enrolment
        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");
        var enrol = await EnrolMfaAsync(viewerToken);
        var confirmCode = new Totp(Base32Encoding.ToBytes(enrol.Secret)).ComputeTotp();
        await ConfirmMfaEnrolAsync(viewerToken, confirmCode);
        // The initial login issued a refresh token; enrolment does not issue a new one.

        // Admin resets MFA
        var adminToken = await LoginAsync("admin@example.com", "Password1!");
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/users/{_viewerUserId}/mfa");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify no active refresh tokens remain for the viewer
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var liveTokens = await db.RefreshTokens
            .CountAsync(rt => rt.UserId == _viewerUserId && rt.RevokedAt == null);
        Assert.Equal(0, liveTokens);

        // ...and the rows still EXIST (stamped, not hard-deleted) — pins the audit-trail
        // constraint so a regression from ExecuteUpdateAsync(RevokedAt) to ExecuteDeleteAsync
        // would fail this test.
        var totalTokens = await db.RefreshTokens.CountAsync(rt => rt.UserId == _viewerUserId);
        Assert.True(totalTokens > 0);
    }

    [Fact]
    public async Task ResetMfa_NonAdmin_Returns403()
    {
        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/users/{_viewerUserId}/mfa");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ResetMfa_UnknownUser_Returns404()
    {
        var adminToken = await LoginAsync("admin@example.com", "Password1!");
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/users/{Guid.NewGuid()}/mfa");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ResetMfa_UserWithMfaDisabled_IsIdempotent()
    {
        // Viewer has MFA disabled by default (no enrolment)
        var adminToken = await LoginAsync("admin@example.com", "Password1!");
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/users/{_viewerUserId}/mfa");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------- Helpers ----------

    // MFA setup helpers — mirrors AuthIntegrationTests pattern (Story 2.13/2.14).
    private async Task<MfaEnrolResponseDto> EnrolMfaAsync(string bearerToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/users/me/mfa/enrol");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        using var response = await _client!.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MfaEnrolResponseDto>())!;
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

    private async Task<string> LoginAsync(string email, string password)
    {
        var login = await LoginRawAsync(email, password);
        return login.AccessToken;
    }

    private async Task<LoginResponseDto> LoginRawAsync(string email, string password)
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email, password });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        return body!;
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
        if (!await db.Roles.AnyAsync(r => r.Id == ViewerRoleId))
        {
            db.Roles.Add(new Role
            {
                Id = ViewerRoleId,
                Name = "viewer",
                IsSystem = true,
                CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            });
        }
        await db.SaveChangesAsync();
    }

    private static async Task<(Guid AdminId, Guid ViewerId)> SeedTestUsersAsync(FormForgeDbContext db)
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

        return (admin.Id, viewer.Id);
    }

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record LoginResponseDto(string AccessToken, string RefreshToken, int ExpiresIn);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record MfaEnrolResponseDto(string Secret, string QrCodeDataUrl, string[] BackupCodes);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record PagedResultDto<T>(IReadOnlyList<T> Data, long Total, int Page, int PageSize, int TotalPages);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record UserListItemDto(
        Guid Id,
        string Email,
        string DisplayName,
        bool IsActive,
        int RoleCount,
        DateTimeOffset CreatedAt);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record UserRoleItemDto(Guid Id, string Name);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record UserDetailResponseDto(
        Guid Id,
        string Email,
        string DisplayName,
        bool IsActive,
        DateTimeOffset CreatedAt,
        DateTimeOffset? UpdatedAt,
        IReadOnlyList<UserRoleItemDto> Roles);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record PermissionsResponseDto(
        Guid UserId,
        DateTimeOffset ComputedAt,
        bool IsActive,
        IReadOnlyDictionary<string, CrudFlagsResponseDto> PerResource,
        IReadOnlyList<Guid> RoleIds);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record CrudFlagsResponseDto(bool CanCreate, bool CanRead, bool CanUpdate, bool CanDelete);
}
