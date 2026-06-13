using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Features.Provisioning;
using FormForge.Api.Infrastructure.Persistence;
using FormForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FormForge.Api.Tests.Features.Provisioning;

// Story 5.8 — exercises ProvisioningRecoveryService end-to-end. Each test manages
// its own WebApplicationFactory (sometimes two) to simulate a process restart
// without crashing the test host. InitializeAsync runs a throwaway setup factory
// (with the BackgroundService AND RecoveryService both removed) purely to handle
// migrations + TRUNCATE + seed; the setup factory is disposed before the test
// body runs, so we never carry a class-level _factory/_client field as
// ProvisioningIntegrationTests does. All HTTP helpers are static and take
// HttpClient as the first parameter for the same reason.
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory and HttpClient instances are disposed inside each test via using statements.")]
public sealed class ProvisioningRecoveryIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");

    private readonly PostgresFixture _postgres;

    public ProvisioningRecoveryIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        // Throwaway factory used only to migrate + TRUNCATE + seed. Hosted services
        // removed so no provisioning runs during setup, which would race with the
        // truncate. Disposed before the test body runs.
        var setupFactory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:formforge", _postgres.ConnectionString);
                builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
                builder.ConfigureServices(RemoveProvisioningHostedServices);
            });

        try
        {
            using var scope = setupFactory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            await db.Database.MigrateAsync();

            // FK order: menus → component_schemas via fk_menus_bound_designer SetNull.
            // Truncate menus first; schema_audit_log is independent.
            await db.Database.ExecuteSqlRawAsync(
                "TRUNCATE TABLE menu_role_assignments, menus, component_schema_versions, component_schemas, role_permissions, user_roles, roles, refresh_tokens, users, schema_audit_log RESTART IDENTITY CASCADE;");

            // Drop any dynamically-provisioned tables left from previous test runs.
            // List must stay in sync with the static EF schema.
            await db.Database.ExecuteSqlRawAsync("""
                DO $$
                DECLARE r RECORD;
                BEGIN
                    FOR r IN
                        SELECT tablename FROM pg_tables
                        WHERE schemaname = 'public'
                          AND tablename NOT IN (
                              'users', 'refresh_tokens', 'roles', 'role_permissions', 'user_roles',
                              'component_schemas', 'component_schema_versions',
                              'menus', 'menu_role_assignments',
                              'schema_audit_log',
                              '__EFMigrationsHistory'
                          )
                    LOOP
                        EXECUTE 'DROP TABLE IF EXISTS ' || quote_ident(r.tablename) || ' CASCADE';
                    END LOOP;
                END $$;
                """);

            await ReseedSystemRolesAsync(db);
            await SeedTestUsersAsync(db);
        }
        finally
        {
            await setupFactory.DisposeAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------- Tests ----------

    [Fact]
    public async Task Recovery_PendingMenuAfterRestart_CompletesToSuccess()
    {
        // AC-3: two-factory restart simulation. Factory1 binds with no consumer →
        // menu row committed as Pending, channel buffer dies with factory1. Factory2
        // starts with full services → recovery scans → consumer drains → Success.

        Guid menuId;
        // Factory 1 — bind to Pending, no consumer to drain it.
        var factory1 = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:formforge", _postgres.ConnectionString);
                builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
                builder.ConfigureServices(RemoveProvisioningHostedServices);
            });
        try
        {
            using var client1 = factory1.CreateClient(new WebApplicationFactoryClientOptions
            {
                HandleCookies = false,
            });
            var token1 = await LoginAsync(client1, "admin@example.com", "Password1!");
            menuId = await CreateMenuViaApiAsync(client1, token1, "RecoveryMenu1", 0);
            await CreateAndPublishDesignerAsync(client1, token1, "recovery_designer_1");

            using (var bind = await PutBindingAsync(client1, token1, menuId, "recovery_designer_1", 1))
                Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);

            // Without a consumer, status should still be Pending. Read DB directly
            // because the menu API surfaces the same value but going to PG removes
            // any doubt about caching/ordering.
            Assert.Equal("Pending", await GetProvisioningStatusFromPgAsync(menuId));
        }
        finally
        {
            await factory1.DisposeAsync();
        }

        // Factory 2 — full services. Recovery scans → re-enqueues → consumer drains.
        var factory2 = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:formforge", _postgres.ConnectionString);
                builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
            });
        try
        {
            using var client2 = factory2.CreateClient(new WebApplicationFactoryClientOptions
            {
                HandleCookies = false,
            });
            var token2 = await LoginAsync(client2, "admin@example.com", "Password1!");

            var status = await PollUntilTerminalAsync(client2, token2, menuId, TimeSpan.FromSeconds(15));
            Assert.Equal("Success", status);

            // Table was actually provisioned by the recovered job.
            Assert.True(await TableExistsInPostgresAsync("recovery_designer_1"));
        }
        finally
        {
            await factory2.DisposeAsync();
        }
    }

    [Fact]
    public async Task Recovery_NoPendingMenus_StartsClean()
    {
        // Recovery scans an empty pending set, exits cleanly, and the normal bind
        // flow still works. Guards against the recovery service interfering with
        // ordinary startup when there is nothing to recover.

        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:formforge", _postgres.ConnectionString);
                builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
            });
        try
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                HandleCookies = false,
            });
            var token = await LoginAsync(client, "admin@example.com", "Password1!");
            var menuId = await CreateMenuViaApiAsync(client, token, "CleanStartMenu", 0);
            await CreateAndPublishDesignerAsync(client, token, "clean_start_designer");

            using (var bind = await PutBindingAsync(client, token, menuId, "clean_start_designer", 1))
                Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);

            var status = await PollUntilTerminalAsync(client, token, menuId, TimeSpan.FromSeconds(10));
            Assert.Equal("Success", status);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task Recovery_PendingMenuIdempotent_TableAlreadyExists()
    {
        // AC-2 idempotency / Dapper-EF dual-write hazard mitigation. Factory1 runs
        // a full bind to Success (table created, menu committed). Direct PG UPDATE
        // flips provisioning_status back to Pending (simulating the hazard: table
        // altered, but the EF status update lost). Factory2's recovery scanner
        // re-enqueues → CREATE TABLE IF NOT EXISTS is a no-op → AddMissingColumns
        // finds 0 missing → idempotent no-op ALTER → status flips back to Success.

        Guid menuId;
        var factory1 = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:formforge", _postgres.ConnectionString);
                builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
            });
        try
        {
            using var client1 = factory1.CreateClient(new WebApplicationFactoryClientOptions
            {
                HandleCookies = false,
            });
            var token1 = await LoginAsync(client1, "admin@example.com", "Password1!");
            menuId = await CreateMenuViaApiAsync(client1, token1, "IdempotentMenu", 0);
            await CreateAndPublishDesignerAsync(client1, token1, "idempotent_recovery");

            using (var bind = await PutBindingAsync(client1, token1, menuId, "idempotent_recovery", 1))
                Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
            Assert.Equal("Success", await PollUntilTerminalAsync(client1, token1, menuId, TimeSpan.FromSeconds(10)));
            Assert.True(await TableExistsInPostgresAsync("idempotent_recovery"));
        }
        finally
        {
            await factory1.DisposeAsync();
        }

        // Capture column count BEFORE forcing Pending so we can assert the table
        // shape did not drift after the idempotent recovery pass.
        var colsBefore = await GetTableColumnsAsync("idempotent_recovery");

        // Simulate the Dapper-EF dual-write hazard: DDL committed, status update lost.
        await ForceProvisioningStatusAsync(menuId, "Pending");
        Assert.Equal("Pending", await GetProvisioningStatusFromPgAsync(menuId));

        var factory2 = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:formforge", _postgres.ConnectionString);
                builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
            });
        try
        {
            using var client2 = factory2.CreateClient(new WebApplicationFactoryClientOptions
            {
                HandleCookies = false,
            });
            var token2 = await LoginAsync(client2, "admin@example.com", "Password1!");

            var status = await PollUntilTerminalAsync(client2, token2, menuId, TimeSpan.FromSeconds(15));
            Assert.Equal("Success", status);

            // Table still exists with the same column set — no schema drift introduced
            // by the recovered idempotent re-run.
            var colsAfter = await GetTableColumnsAsync("idempotent_recovery");
            Assert.Equal(colsBefore.Count, colsAfter.Count);
            var beforeNames = colsBefore.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var afterNames = colsAfter.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(beforeNames, afterNames);
        }
        finally
        {
            await factory2.DisposeAsync();
        }
    }

    [Fact]
    public async Task Recovery_ScanFails_HostStartsAndServesRequests()
    {
        // Regression guard for the catch-all in ProvisioningRecoveryService.ExecuteAsync
        // (deviation documented in Dev Agent Record). When the DB is unreachable at startup,
        // ExecuteAsync throws; without the catch-all the exception propagates to the host
        // (BackgroundServiceExceptionBehavior = StopHost) and the TestServer is disposed —
        // exactly the failure mode observed in HealthCheckEndpointsTests before the fix.
        //
        // Pattern mirrors FormForgeApiFactory in CorrelationIdMiddlewareTests: point the
        // connection string at ignored.invalid so Migrate() and the recovery scan both fail
        // fast (Timeout=1). Program.cs wraps Migrate() in a try/catch; ExecuteAsync wraps
        // the scan in a try/catch. Both must hold for the app to stay up.
        //
        // If the catch block in ExecuteAsync were removed, this test would fail with
        // ObjectDisposedException on the HTTP call (TestServer disposed by the crashed host).

        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting(
                    "ConnectionStrings:formforge",
                    "Host=ignored.invalid;Port=1;Database=ignored;Username=u;Password=p;Timeout=1;CommandTimeout=1");
                builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
            });
        try
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            // /health/live is anonymous, has no DB dependency (only the "live" self-tag
            // check), and is mapped via FormForge.ServiceDefaults.Extensions in every
            // environment. It returns 200 if the host process is up — exactly the
            // assertion this test cares about.
            //
            // (Previously hit GET / which returned 200 from a placeholder MapGet("/").
            // Story 7.3 replaced that with MapFallback(IndexHtmlRewriter.HandleAsync)
            // for SPA routing, which 404s when wwwroot/index.html doesn't exist —
            // i.e. in the test environment where the SPA isn't built.)
            using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // ---------- Helpers (static, take HttpClient as first param) ----------

    private static void RemoveProvisioningHostedServices(IServiceCollection services)
    {
        var bg = services.FirstOrDefault(d => d.ImplementationType == typeof(ProvisioningBackgroundService));
        if (bg is not null) services.Remove(bg);
        var rec = services.FirstOrDefault(d => d.ImplementationType == typeof(ProvisioningRecoveryService));
        if (rec is not null) services.Remove(rec);
    }

    private static async Task<string> LoginAsync(HttpClient client, string email, string password)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login",
            new { email, password });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        return body!.AccessToken;
    }

    private static async Task<Guid> CreateMenuViaApiAsync(HttpClient client, string token, string name, int order)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/menus")
        {
            Content = JsonContent.Create(new { name, order }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<MenuResponseDto>();
        return body!.Id;
    }

    private static async Task CreateDesignerViaApiAsync(HttpClient client, string token, string designerId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers")
        {
            Content = JsonContent.Create(new { designerId, displayName = designerId, mode = "CRUD" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static async Task PutVersionStatusAsync(HttpClient client, string token, string designerId, int version, string status)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/designers/{designerId}/versions/{version}/status")
        {
            Content = JsonContent.Create(new { status }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static async Task CreateAndPublishDesignerAsync(HttpClient client, string token, string designerId)
    {
        await CreateDesignerViaApiAsync(client, token, designerId);
        await PutVersionStatusAsync(client, token, designerId, 1, "Published");
    }

    private static async Task<HttpResponseMessage> PutBindingAsync(HttpClient client, string token, Guid menuId, string designerId, int version)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/admin/menus/{menuId}/binding")
        {
            Content = JsonContent.Create(new { designerId, version }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<MenuResponseDto?> GetMenuAsync(HttpClient client, string token, Guid menuId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/menus/{menuId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MenuResponseDto>();
    }

    // Polls every 200 ms until status leaves "Pending"; throws TimeoutException on deadline.
    private static async Task<string> PollUntilTerminalAsync(HttpClient client, string token, Guid menuId, TimeSpan deadline)
    {
        var stop = DateTimeOffset.UtcNow.Add(deadline);
        string? status;
        do
        {
            var menu = await GetMenuAsync(client, token, menuId);
            status = menu?.ProvisioningStatus;
            if (status is not null and not "Pending") return status;
            await Task.Delay(200);
        } while (DateTimeOffset.UtcNow < stop);
        throw new TimeoutException(
            $"MenuId {menuId} did not reach a terminal provisioning status within {deadline.TotalSeconds}s; last observed: {status ?? "(null)"}");
    }

    // Direct PG reads/writes — bypass the HTTP layer so the test can prepare
    // state that the API would not normally let us reach (e.g. flip a Success
    // row back to Pending to simulate the Dapper-EF dual-write hazard).

    private async Task<string?> GetProvisioningStatusFromPgAsync(Guid menuId)
    {
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT provisioning_status FROM menus WHERE id = @id";
            cmd.Parameters.AddWithValue("id", menuId);
            var result = await cmd.ExecuteScalarAsync();
            return result is DBNull or null ? null : (string)result;
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    private async Task ForceProvisioningStatusAsync(Guid menuId, string status)
    {
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE menus SET provisioning_status = @status WHERE id = @id";
            cmd.Parameters.AddWithValue("status", status);
            cmd.Parameters.AddWithValue("id", menuId);
            var rowsAffected = await cmd.ExecuteNonQueryAsync();
            Assert.Equal(1, rowsAffected);
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    private async Task<bool> TableExistsInPostgresAsync(string tableName)
    {
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(1) > 0
                FROM information_schema.tables
                WHERE table_schema = 'public' AND table_name = @tableName
                """;
            cmd.Parameters.AddWithValue("tableName", tableName);
            return (bool)(await cmd.ExecuteScalarAsync())!;
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    private async Task<IReadOnlyList<(string Name, string DataType)>> GetTableColumnsAsync(string tableName)
    {
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT column_name, data_type
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = @tableName
                ORDER BY ordinal_position
                """;
            cmd.Parameters.AddWithValue("tableName", tableName);
            await using var reader = await cmd.ExecuteReaderAsync();
            var result = new List<(string Name, string DataType)>();
            while (await reader.ReadAsync())
                result.Add((reader.GetString(0), reader.GetString(1)));
            return result;
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    // Seeding helpers — duplicated from ProvisioningIntegrationTests (private there;
    // a true shared helper class would require touching that test file too).

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

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record MenuResponseDto(
        Guid Id,
        string Name,
        int Order,
        string? Icon,
        bool IsActive,
        Guid? ParentId,
        IReadOnlyList<Guid> AllowedRoleIds,
        DateTimeOffset CreatedAt,
        DateTimeOffset? UpdatedAt,
        string? DesignerId,
        int? BoundVersion,
        string? ProvisioningStatus,
        string? ProvisioningError);
}
