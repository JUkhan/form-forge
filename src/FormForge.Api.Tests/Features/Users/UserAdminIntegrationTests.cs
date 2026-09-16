using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Features.Tenancy;
using FormForge.Api.Infrastructure.Persistence;
using FormForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using OtpNet;

namespace FormForge.Api.Tests.Features.Users;

[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class UserAdminIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");

    // Shared by every tenant admin seeded via ProvisionTenantWithAdminAsync.
    private const string TenantAdminPassword = "Password1!";

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

        // tenants / tenant_user_index / platform_admins are swept too now that the
        // create-user path asserts on real public.tenant_user_index rows: a leftover index
        // row from a previous test method would make the "email already routed elsewhere"
        // pre-check fire on an unrelated create. tenant_user_index cascades from tenants
        // (FK), but both are listed explicitly so the intent survives a schema change.
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE role_permissions, user_roles, roles, refresh_tokens, users, "
            + "tenant_user_index, tenants, platform_admins RESTART IDENTITY CASCADE;");

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

    // ---------- architecture.md Decision 7.3: public.tenant_user_index on create ----------

    // The bug this covers: a tenant admin created a user, the row landed in the tenant's
    // own `users` table, but nothing was written to public.tenant_user_index — so
    // LoginAsync could not route that email to a tenant and fell through to the legacy
    // public.users check, returning 401 forever. Asserts the real index row (queried, not
    // mocked), that both tables carry the SAME normalized email, and that the user row
    // landed in the tenant's schema rather than public.
    [Fact]
    public async Task CreateUser_AsTenantAdmin_WritesTenantUserIndexRow()
    {
        var tenant = await ProvisionTenantWithAdminAsync(
            "tenant_useradmin_index", "admin@tenant-index.example");
        var token = await LoginAsync("admin@tenant-index.example", TenantAdminPassword);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users")
        {
            Content = JsonContent.Create(new
            {
                email = "  NewTenant@Example.COM  ",
                displayName = "New Tenant User",
                temporaryPassword = "TempPass123!",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();

        // Same Trim().ToLowerInvariant() normalization as the user row.
        var entry = await db.TenantUserIndex
            .AsNoTracking()
            .SingleAsync(t => t.Email == "newtenant@example.com");
        Assert.Equal(tenant.Id, entry.TenantId);

        // The user row is in the tenant's own schema...
        Assert.Equal(1, await TenantSchemaUserCountAsync(tenant.SchemaName, "newtenant@example.com"));

        // ...and nowhere in public.users (this scope has no tenant, so `db` is on public).
        Assert.False(await db.Users.AsNoTracking()
            .AnyAsync(u => u.Email == "newtenant@example.com"));
    }

    // Matrix row "Email used by another tenant": the index PK is global by design, so an
    // address already routed to a different tenant must come back as the same generic 409
    // — never a 500, and never a message that reveals the other tenant.
    [Fact]
    public async Task CreateUser_AsTenantAdmin_EmailOwnedByAnotherTenant_Returns409AndWritesNothing()
    {
        var tenant = await ProvisionTenantWithAdminAsync(
            "tenant_useradmin_cross", "admin@tenant-cross.example");

        // The other tenant needs no provisioned schema — only a row the index FK can point
        // at, since this create never gets far enough to touch that tenant's data.
        Guid otherTenantId;
        using (var seedScope = _factory!.Services.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var otherTenant = new Tenant
            {
                Name = "Other Tenant",
                SchemaName = "tenant_useradmin_cross_other",
                Status = "Active",
            };
            seedDb.Tenants.Add(otherTenant);
            await seedDb.SaveChangesAsync();
            otherTenantId = otherTenant.Id;

            seedDb.TenantUserIndex.Add(new TenantUserIndexEntry
            {
                Email = "taken@other.example",
                TenantId = otherTenantId,
            });
            await seedDb.SaveChangesAsync();
        }

        var token = await LoginAsync("admin@tenant-cross.example", TenantAdminPassword);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users")
        {
            Content = JsonContent.Create(new
            {
                email = "taken@other.example",
                displayName = "Cross Tenant",
                temporaryPassword = "TempPass123!",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        // Exactly the generic conflict envelope UserEndpoints.UserEmailConflictProblem emits
        // for a same-tenant duplicate — nothing that distinguishes "taken here" from "taken by
        // another tenant".
        await AssertGenericEmailConflictAsync(response);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();

        // The other tenant's routing row is untouched — not repointed, not duplicated.
        var entry = await db.TenantUserIndex
            .AsNoTracking()
            .SingleAsync(t => t.Email == "taken@other.example");
        Assert.Equal(otherTenantId, entry.TenantId);

        // No user row was written into the acting tenant's schema.
        Assert.Equal(0, await TenantSchemaUserCountAsync(tenant.SchemaName, "taken@other.example"));
    }

    // Matrix row "Email belongs to a platform admin": LoginAsync checks public.platform_admins
    // first and treats a match as terminal, so creating a tenant user on that address would
    // produce an account that can never log in. Mirrors TenantOnboardingService's guard.
    [Fact]
    public async Task CreateUser_AsTenantAdmin_EmailBelongsToPlatformAdmin_Returns409AndWritesNothing()
    {
        var tenant = await ProvisionTenantWithAdminAsync(
            "tenant_useradmin_padmin", "admin@tenant-padmin.example");

        using (var seedScope = _factory!.Services.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            seedDb.PlatformAdmins.Add(new PlatformAdmin
            {
                UserEmail = "super@formforge.test",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("SuperSecret1!", 12),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await seedDb.SaveChangesAsync();
        }

        var token = await LoginAsync("admin@tenant-padmin.example", TenantAdminPassword);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users")
        {
            Content = JsonContent.Create(new
            {
                email = "Super@FormForge.test",
                displayName = "Collides With Super Admin",
                temporaryPassword = "TempPass123!",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        // Same generic envelope — the tenant admin must not learn that the address belongs to
        // a platform-super-admin.
        await AssertGenericEmailConflictAsync(response);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        Assert.False(await db.TenantUserIndex.AsNoTracking()
            .AnyAsync(t => t.Email == "super@formforge.test"));

        Assert.Equal(0, await TenantSchemaUserCountAsync(tenant.SchemaName, "super@formforge.test"));
    }

    // Matrix row "Email already in this tenant": the pre-existing uq_users_email pre-check
    // still wins, and crucially no index row is left behind for the rejected address.
    [Fact]
    public async Task CreateUser_AsTenantAdmin_DuplicateWithinSameTenant_Returns409AndWritesNoIndexRow()
    {
        await ProvisionTenantWithAdminAsync("tenant_useradmin_dup", "admin@tenant-dup.example");
        var token = await LoginAsync("admin@tenant-dup.example", TenantAdminPassword);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users")
        {
            Content = JsonContent.Create(new
            {
                // The tenant admin itself already occupies this address in the tenant schema.
                email = "admin@tenant-dup.example",
                displayName = "Duplicate",
                temporaryPassword = "TempPass123!",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // Exactly one index row — the admin's own, written when the tenant was seeded.
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        Assert.Equal(1, await db.TenantUserIndex.AsNoTracking()
            .CountAsync(t => t.Email == "admin@tenant-dup.example"));
    }

    // Regression guard for the shadowing hazard: the EF `users` pre-check runs under the
    // tenant's search_path, so it cannot see a legacy public.users row. LoginAsync reads
    // tenant_user_index BEFORE public.users, so writing an index row for an address a legacy
    // account already owns would silently hijack that account's login. Must be rejected.
    [Fact]
    public async Task CreateUser_AsTenantAdmin_EmailOwnedByLegacyPublicUser_Returns409AndWritesNothing()
    {
        var tenant = await ProvisionTenantWithAdminAsync(
            "tenant_useradmin_legacy", "admin@tenant-legacy.example");

        // viewer@example.com is seeded into public.users by SeedTestUsersAsync and is
        // invisible to any EF query made under the tenant's search_path.
        var token = await LoginAsync("admin@tenant-legacy.example", TenantAdminPassword);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users")
        {
            Content = JsonContent.Create(new
            {
                email = "viewer@example.com",
                displayName = "Shadows A Legacy Account",
                temporaryPassword = "TempPass123!",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertGenericEmailConflictAsync(response);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();

        // No routing row, so the legacy owner still resolves through public.users at login...
        Assert.False(await db.TenantUserIndex.AsNoTracking()
            .AnyAsync(t => t.Email == "viewer@example.com"));

        // ...and no shadow user row landed in the tenant's schema.
        Assert.Equal(0, await TenantSchemaUserCountAsync(tenant.SchemaName, "viewer@example.com"));

        // The legacy account can still log in — the point of the guard.
        var legacyToken = await LoginAsync("viewer@example.com", "Password1!");
        Assert.NotEmpty(legacyToken);
    }

    // Deterministic coverage for the widened PK_tenant_user_index catch filter. The
    // pre-checks cannot be relied on to exercise it — they short-circuit first — so a
    // test-only SavingChanges interceptor commits the conflicting index row on a separate
    // connection in the race window, after the pre-checks have passed and before the batch
    // reaches the database. Narrowing IsEmailUniqueViolation back to uq_users_email only
    // turns this into a 500; splitting the single SaveChangesAsync leaves an orphan user row.
    [Fact]
    public async Task CreateUser_AsTenantAdmin_IndexRowRaceLostAtInsert_Returns409AndRollsBackUserRow()
    {
        const string RacedEmail = "raced@example.com";
        var tenant = await ProvisionTenantWithAdminAsync(
            "tenant_useradmin_race", "admin@tenant-race.example");

        Guid otherTenantId;
        using (var seedScope = _factory!.Services.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var otherTenant = new Tenant
            {
                Name = "Race Winner",
                SchemaName = "tenant_useradmin_race_winner",
                Status = "Active",
            };
            seedDb.Tenants.Add(otherTenant);
            await seedDb.SaveChangesAsync();
            otherTenantId = otherTenant.Id;
        }

        var raceInterceptor = new RaceTheIndexRowInterceptor(
            _postgres.ConnectionString, RacedEmail, otherTenantId);

        // AddDbContext registers DbContextOptions<T> with TryAdd, so the app's own
        // registration has to be removed before this one can attach the extra interceptor;
        // the production TenantSchemaConnectionInterceptor is re-attached verbatim so the
        // request still resolves to the tenant's schema.
        using var racingFactory = _factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<FormForgeDbContext>>();
                services.AddDbContext<FormForgeDbContext>((sp, options) =>
                    options.UseNpgsql(_postgres.ConnectionString)
                        .AddInterceptors(
                            sp.GetRequiredService<TenantSchemaConnectionInterceptor>(),
                            raceInterceptor));
            }));
        using var racingClient = racingFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });

        using (var loginResponse = await racingClient.PostAsJsonAsync("/api/auth/login",
            new { email = "admin@tenant-race.example", password = TenantAdminPassword }))
        {
            loginResponse.EnsureSuccessStatusCode();
            var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();

            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users")
            {
                Content = JsonContent.Create(new
                {
                    email = RacedEmail,
                    displayName = "Lost The Race",
                    temporaryPassword = "TempPass123!",
                }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
            using var response = await racingClient.SendAsync(request);

            // The interceptor must actually have fired, or this test proves nothing.
            Assert.True(raceInterceptor.Fired);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            await AssertGenericEmailConflictAsync(response);
        }

        // The index row is the racer's, and the losing request left no user row behind.
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var entry = await db.TenantUserIndex.AsNoTracking().SingleAsync(t => t.Email == RacedEmail);
        Assert.Equal(otherTenantId, entry.TenantId);
        Assert.Equal(0, await TenantSchemaUserCountAsync(tenant.SchemaName, RacedEmail));
    }

    // Matrix row "No tenant in context": a legacy token carries no tenantId claim, so
    // TenantContext stays unset and the index must not be touched at all — today's
    // behavior for every pre-tenancy deployment stays exactly as it was.
    [Fact]
    public async Task CreateUser_AsLegacyPlatformAdmin_WritesNoTenantUserIndexRow()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users")
        {
            Content = JsonContent.Create(new
            {
                email = "legacy-created@example.com",
                displayName = "Legacy Created",
                temporaryPassword = "TempPass123!",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        Assert.True(await db.Users.AsNoTracking().AnyAsync(u => u.Email == "legacy-created@example.com"));
        Assert.False(await db.TenantUserIndex.AsNoTracking()
            .AnyAsync(t => t.Email == "legacy-created@example.com"));
    }

    // Matrix row "Concurrent duplicate POSTs". Deliberately raced across TWO tenants,
    // because that is the case the widened catch filter exists for: each tenant's `users`
    // insert succeeds in its own schema (uq_users_email is per-schema), so the loser can
    // only fail on the globally-unique PK_tenant_user_index. Both requests clear the
    // AnyAsync pre-check before either commits, so the loser must be translated into the
    // documented 409 rather than surfacing as a 500 — and its user row must roll back with
    // the routing row, never left behind unroutable.
    [Fact]
    public async Task CreateUser_ConcurrentPostsFromTwoTenants_SameEmail_OneCreatedOneConflict()
    {
        var tenantA = await ProvisionTenantWithAdminAsync(
            "tenant_useradmin_race_a", "admin@tenant-race-a.example");
        var tenantB = await ProvisionTenantWithAdminAsync(
            "tenant_useradmin_race_b", "admin@tenant-race-b.example");

        var tokenA = await LoginAsync("admin@tenant-race-a.example", TenantAdminPassword);
        var tokenB = await LoginAsync("admin@tenant-race-b.example", TenantAdminPassword);

        async Task<HttpStatusCode> PostAsync(string bearerToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users")
            {
                Content = JsonContent.Create(new
                {
                    email = "racer@contested.example",
                    displayName = "Racer",
                    temporaryPassword = "TempPass123!",
                }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            using var response = await _client!.SendAsync(request);
            return response.StatusCode;
        }

        var statuses = await Task.WhenAll(PostAsync(tokenA), PostAsync(tokenB));

        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Conflict));

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();

        // Exactly one routing row survives, owned by whichever tenant won.
        var entry = await db.TenantUserIndex
            .AsNoTracking()
            .SingleAsync(t => t.Email == "racer@contested.example");
        var winner = entry.TenantId == tenantA.Id ? tenantA : tenantB;
        var loser = entry.TenantId == tenantA.Id ? tenantB : tenantA;

        // The winner kept its user row; the loser's whole SaveChanges rolled back, so it has
        // no orphaned user sitting in its schema with nothing routing to it.
        Assert.Equal(1, await TenantSchemaUserCountAsync(winner.SchemaName, "racer@contested.example"));
        Assert.Equal(0, await TenantSchemaUserCountAsync(loser.SchemaName, "racer@contested.example"));
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

    // Every 409 from the create path — same-tenant duplicate, another tenant's email, a
    // platform-super-admin's email, a legacy public.users email, or the lost insert race —
    // must return the one generic envelope from UserEndpoints.UserEmailConflictProblem.
    // Asserting the exact code/messageKey/detail is what pins the confidentiality boundary:
    // any future branch that tries to explain WHY the email is taken fails here.
    private static async Task AssertGenericEmailConflictAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsDto>();
        Assert.NotNull(problem);
        Assert.Equal("USER_EMAIL_CONFLICT", problem!.Code);
        Assert.Equal("users.emailConflict", problem.MessageKey);
        Assert.Equal("A user with this email already exists.", problem.Detail);
        Assert.Equal("User email already exists", problem.Title);
    }

    // Commits a conflicting public.tenant_user_index row on its own connection at the exact
    // moment CreateUserAsync flushes — i.e. after its pre-checks passed. Registered only by
    // the race test's own factory, and gated on the email so no other SaveChanges is touched.
    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated directly by the race test.")]
    private sealed class RaceTheIndexRowInterceptor(
        string connectionString, string email, Guid otherTenantId) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(eventData);

            var isTargetInsert = !Fired
                && eventData.Context is not null
                && eventData.Context.ChangeTracker.Entries<User>()
                    .Any(e => e.State == EntityState.Added
                        && string.Equals(e.Entity.Email, email, StringComparison.Ordinal));

            if (isTargetInsert)
            {
                Fired = true;
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync(cancellationToken);
                await using var cmd = new NpgsqlCommand(
                    "INSERT INTO public.tenant_user_index (email, tenant_id) VALUES (@email, @tenantId)",
                    conn);
                cmd.Parameters.AddWithValue("email", email);
                cmd.Parameters.AddWithValue("tenantId", otherTenantId);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    // Provisions a real tenant schema (Story 12.2's full static migration replay) and seeds
    // a tenant admin into it, with the tenant-admin role row and the public routing row —
    // the three things a tenant-authenticated POST /api/admin/users needs: a login that
    // resolves through tenant_user_index, a "platform-admin" role claim for the
    // /api/admin group policy, and an Active tenant for TenantContextMiddleware to resolve.
    // Deliberately mirrors TenantAwareLoginIntegrationTests' seeding rather than calling
    // ITenantOnboardingService, which additionally creates a datasets namespace and sends
    // a welcome email — neither is relevant here.
    private async Task<Tenant> ProvisionTenantWithAdminAsync(string schemaName, string adminEmail)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var provisioningSvc = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();

        var tenant = new Tenant
        {
            Name = $"Tenant {schemaName}",
            SchemaName = schemaName,
            Status = "Provisioning",
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        await provisioningSvc.ProvisionSchemaAsync(tenant, CancellationToken.None);

        var csb = new NpgsqlConnectionStringBuilder(_postgres.ConnectionString) { SearchPath = schemaName };
        await using (var tenantConnection = new NpgsqlConnection(csb.ConnectionString))
        {
            var options = new DbContextOptionsBuilder<FormForgeDbContext>().UseNpgsql(tenantConnection).Options;
            await using var tenantDb = new FormForgeDbContext(options);

            var admin = new User
            {
                Email = adminEmail,
                DisplayName = "Tenant Admin",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(TenantAdminPassword, 12),
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            tenantDb.Users.Add(admin);
            // Navigation (not UserId): User.Id is store-generated, so only fixup can
            // resolve the FK within a single SaveChanges. Same as TenantOnboardingService.
            // The role row itself already exists in the tenant schema — the static
            // migration set seeds id 0001 / name "platform-admin" into every schema.
            tenantDb.UserRoles.Add(new UserRole
            {
                User = admin,
                RoleId = PlatformAdminRoleId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await tenantDb.SaveChangesAsync();
        }

        db.TenantUserIndex.Add(new TenantUserIndexEntry { Email = adminEmail, TenantId = tenant.Id });
        tenant.Status = "Active";
        await db.SaveChangesAsync();

        return tenant;
    }

    // Counts rows in the tenant's OWN `users` table over a SearchPath-scoped connection, so
    // an assertion can distinguish "written into the tenant schema" from "written into
    // public" — and, on the rollback paths, prove that exactly zero rows survived.
    private async Task<int> TenantSchemaUserCountAsync(string schemaName, string email)
    {
        var csb = new NpgsqlConnectionStringBuilder(_postgres.ConnectionString) { SearchPath = schemaName };
        await using var tenantConnection = new NpgsqlConnection(csb.ConnectionString);
        var options = new DbContextOptionsBuilder<FormForgeDbContext>().UseNpgsql(tenantConnection).Options;
        await using var tenantDb = new FormForgeDbContext(options);
        return await tenantDb.Users.AsNoTracking().CountAsync(u => u.Email == email);
    }

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
    private sealed record ProblemDetailsDto(string Title, string Detail, string Code, string MessageKey);

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
