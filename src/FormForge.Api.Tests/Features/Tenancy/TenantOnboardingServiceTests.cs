using System.Diagnostics.CodeAnalysis;
using Dapper;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Features.Tenancy;
using FormForge.Api.Infrastructure.Persistence;
using FormForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FormForge.Api.Tests.Features.Tenancy;

// Story 12.7 (FR-75 / Decision 7.7) — exercises TenantOnboardingService end-to-end against
// a real Testcontainers Postgres, no mocks, mirroring TenantProvisioningServiceTests' shape.
// Every test first runs the real ITenantProvisioningService (Story 12.2) to satisfy this
// story's precondition (schema + static migration set already replayed) before calling
// OnboardTenantAsync — this story never re-runs that step itself.
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class TenantOnboardingServiceTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid TenantAdminRoleId = new("00000000-0000-0000-0000-000000000001");

    // Same sensitive-table list the migrations revoke SELECT on for formforge_preview
    // (mirrors TenantOnboardingService.RevokedTables exactly, so a future regression that
    // drops one of these from the production list breaks this test). mfa_sessions has no
    // backing table anywhere in the codebase today (architecture §6.7 forward-looking
    // name) — HasTablePrivilegeAsync below treats a nonexistent relation as "no privilege"
    // rather than erroring, so the assertion still holds trivially until that table exists.
    private static readonly string[] RevokedTables =
    [
        "roles", "refresh_tokens", "password_reset_tokens",
        "mfa_backup_codes", "mfa_sessions", "schema_audit_log",
        "mutation_audit_log", "dataset_audit_log", "custom_dataset",
    ];

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;

    public TenantOnboardingServiceTests(PostgresFixture postgres) => _postgres = postgres;

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
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE tenants RESTART IDENTITY CASCADE;");
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    // --- Happy path -----------------------------------------------------------------

    [Fact]
    public async Task OnboardTenantAsync_HappyPath_CreatesNamespaceScopesGrantsSeedsUserAndActivates()
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var provisioningSvc = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
        var onboardingSvc = scope.ServiceProvider.GetRequiredService<ITenantOnboardingService>();

        var tenant = new Tenant { Name = "Acme Corp", SchemaName = "tenant_onboard_happy" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        await provisioningSvc.ProvisionSchemaAsync(tenant, CancellationToken.None);

        await onboardingSvc.OnboardTenantAsync(
            tenant, "Admin@Acme.Example", "Acme Admin", "TempPassw0rd!", CancellationToken.None);

        await using var conn = new NpgsqlConnection(_postgres.ConnectionString);
        await conn.OpenAsync();

        // AC-1: the tenant-scoped Dataset Manager VIEW namespace exists.
        var datasetsSchemaExists = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = @s)",
            new { s = $"{tenant.SchemaName}_datasets" });
        Assert.True(datasetsSchemaExists, "The tenant's dataset VIEW namespace should have been created.");

        // AC-1: formforge_preview reads only the four allowed columns of {schema}.users —
        // has_column_privilege/has_table_privilege are the authoritative checks (a
        // column-level grant does not show up as a table-level privilege).
        var qualifiedUsers = $"\"{tenant.SchemaName}\".users";
        Assert.True(await HasColumnPrivilegeAsync(conn, qualifiedUsers, "id"));
        Assert.True(await HasColumnPrivilegeAsync(conn, qualifiedUsers, "display_name"));
        Assert.True(await HasColumnPrivilegeAsync(conn, qualifiedUsers, "email"));
        Assert.True(await HasColumnPrivilegeAsync(conn, qualifiedUsers, "is_active"));
        Assert.False(await HasColumnPrivilegeAsync(conn, qualifiedUsers, "password_hash"));
        Assert.False(
            await HasTablePrivilegeAsync(conn, qualifiedUsers),
            "users should only be readable via the column-level grant, not table-level SELECT.");

        // Sensitive tables have no SELECT grant at all.
        foreach (var sensitiveTable in RevokedTables)
        {
            var qualified = $"\"{tenant.SchemaName}\".{sensitiveTable}";
            Assert.False(
                await HasTablePrivilegeAsync(conn, qualified),
                $"formforge_preview should not have SELECT on {qualified}.");
        }

        // A non-sensitive table (menus, replayed by Story 12.2's migration set) still
        // carries the bulk SELECT grant.
        Assert.True(await HasTablePrivilegeAsync(conn, $"\"{tenant.SchemaName}\".menus"));

        // AC-2: exactly one user with a UserRole against the tenant-admin role id.
        var userCount = await conn.ExecuteScalarAsync<int>(
            $"""SELECT COUNT(*) FROM "{tenant.SchemaName}".users""");
        Assert.Equal(1, userCount);

        var roleIds = (await conn.QueryAsync<Guid>(
            $"""SELECT role_id FROM "{tenant.SchemaName}".user_roles""")).ToList();
        var actualRoleId = Assert.Single(roleIds);
        Assert.Equal(TenantAdminRoleId, actualRoleId);

        var seededEmail = await conn.ExecuteScalarAsync<string>(
            $"""SELECT email FROM "{tenant.SchemaName}".users""");
        Assert.Equal("admin@acme.example", seededEmail); // normalized lowercase

        // public.datasets / public.formforge_preview grants are untouched by this story.
        Assert.True(await HasTablePrivilegeAsync(conn, "public.menus"));

        // AC-3: Status == Active.
        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var reloaded = await verifyDb.Tenants.SingleAsync(t => t.Id == tenant.Id);
        Assert.Equal("Active", reloaded.Status);
    }

    // --- DDL step fails ---------------------------------------------------------------

    [Fact]
    public async Task OnboardTenantAsync_DdlStepFails_TenantStatusRemainsProvisioningAndNoUserSeeded()
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var provisioningSvc = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
        var onboardingSvc = scope.ServiceProvider.GetRequiredService<ITenantOnboardingService>();

        var tenant = new Tenant { Name = "Fault Co", SchemaName = "tenant_onboard_ddl_fail" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        await provisioningSvc.ProvisionSchemaAsync(tenant, CancellationToken.None);

        // Deterministic fault: pre-create the datasets namespace so the service's own
        // CREATE SCHEMA collides and throws before the EF seed step ever runs.
        await using (var admin = new NpgsqlConnection(_postgres.ConnectionString))
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync($"""CREATE SCHEMA "{tenant.SchemaName}_datasets" """);
        }

        await Assert.ThrowsAnyAsync<Exception>(() => onboardingSvc.OnboardTenantAsync(
            tenant, "admin@fault.example", "Fault Admin", "TempPassw0rd!", CancellationToken.None));

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var reloaded = await verifyDb.Tenants.SingleAsync(t => t.Id == tenant.Id);
        Assert.Equal("Provisioning", reloaded.Status);

        await using var conn = new NpgsqlConnection(_postgres.ConnectionString);
        await conn.OpenAsync();
        var userCount = await conn.ExecuteScalarAsync<int>(
            $"""SELECT COUNT(*) FROM "{tenant.SchemaName}".users""");
        Assert.Equal(0, userCount);
    }

    // --- EF seed step fails after the DDL step already succeeded ----------------------

    [Fact]
    public async Task OnboardTenantAsync_EfSeedStepFailsAfterDdlSucceeds_TenantStatusRemainsProvisioningAndNoAdditionalUserSeeded()
    {
        // The specific half-done state TenantProvisioningRecoveryService's Design Notes
        // cite as its reason for existing: DDL (dataset namespace + grants) commits, but
        // the EF seed step fails afterward on its own separate connection — distinct from
        // OnboardTenantAsync_DdlStepFails_... above, where the DDL step itself is what fails.
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var provisioningSvc = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
        var onboardingSvc = scope.ServiceProvider.GetRequiredService<ITenantOnboardingService>();

        var tenant = new Tenant { Name = "Seed Fail Co", SchemaName = "tenant_onboard_seed_fail" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        await provisioningSvc.ProvisionSchemaAsync(tenant, CancellationToken.None);

        const string collidingEmail = "admin@seedfail.example";

        // Pre-insert a row directly into the tenant schema's users table with the same
        // (already-normalized) email OnboardTenantAsync will try to insert. uq_users_email
        // is part of the same static migration set replayed into every tenant schema
        // (confirmed by TenantProvisioningServiceTests), so the service's own INSERT
        // collides on it and SaveChangesAsync throws — after the DDL step already committed.
        await using (var admin = new NpgsqlConnection(_postgres.ConnectionString))
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync(
                $"""
                INSERT INTO "{tenant.SchemaName}".users (email, display_name, password_hash)
                VALUES (@email, 'Pre-existing', 'x')
                """,
                new { email = collidingEmail });
        }

        await Assert.ThrowsAnyAsync<Exception>(() => onboardingSvc.OnboardTenantAsync(
            tenant, collidingEmail, "Seed Fail Admin", "TempPassw0rd!", CancellationToken.None));

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var reloaded = await verifyDb.Tenants.SingleAsync(t => t.Id == tenant.Id);
        Assert.Equal("Provisioning", reloaded.Status);

        await using var conn = new NpgsqlConnection(_postgres.ConnectionString);
        await conn.OpenAsync();

        // The DDL step DID succeed this time — proving this is genuinely a "seed fails
        // after DDL succeeds" scenario, not a repeat of the DDL-failure test above.
        var datasetsSchemaExists = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = @s)",
            new { s = $"{tenant.SchemaName}_datasets" });
        Assert.True(datasetsSchemaExists, "The DDL step should have succeeded before the seed step failed.");

        // Only the pre-existing row remains; no additional user or role assignment.
        var userCount = await conn.ExecuteScalarAsync<int>(
            $"""SELECT COUNT(*) FROM "{tenant.SchemaName}".users""");
        Assert.Equal(1, userCount);
        var userRoleCount = await conn.ExecuteScalarAsync<int>(
            $"""SELECT COUNT(*) FROM "{tenant.SchemaName}".user_roles""");
        Assert.Equal(0, userRoleCount);
    }

    // --- Welcome email fails / times out -----------------------------------------------

    [Fact]
    public async Task OnboardTenantAsync_WelcomeEmailFails_OnboardingStillCompletesAndActivates()
    {
        // Point SMTP at a closed local port so the connect attempt fails fast (connection
        // refused) — a genuine delivery failure through the real MailKit transport (per the
        // I/O matrix's "connect/auth error" case), proving the catch-and-swallow around
        // TrySendWelcomeEmailAsync never blocks activation.
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:formforge", _postgres.ConnectionString);
                builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
                builder.UseSetting("Smtp:Host", "127.0.0.1");
                builder.UseSetting("Smtp:Port", "1");
            });
        try
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            await db.Database.MigrateAsync();
            await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE tenants RESTART IDENTITY CASCADE;");

            var provisioningSvc = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
            var onboardingSvc = scope.ServiceProvider.GetRequiredService<ITenantOnboardingService>();

            var tenant = new Tenant { Name = "Email Fail Co", SchemaName = "tenant_onboard_email_fail" };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
            await provisioningSvc.ProvisionSchemaAsync(tenant, CancellationToken.None);

            await onboardingSvc.OnboardTenantAsync(
                tenant, "admin@emailfail.example", "Email Fail Admin", "TempPassw0rd!", CancellationToken.None);

            using var verifyScope = factory.Services.CreateScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var reloaded = await verifyDb.Tenants.SingleAsync(t => t.Id == tenant.Id);
            Assert.Equal("Active", reloaded.Status);

            await using var conn = new NpgsqlConnection(_postgres.ConnectionString);
            await conn.OpenAsync();
            var userCount = await conn.ExecuteScalarAsync<int>(
                $"""SELECT COUNT(*) FROM "{tenant.SchemaName}".users""");
            Assert.Equal(1, userCount);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // --- Duplicate admin email across tenants -------------------------------------------

    [Fact]
    public async Task OnboardTenantAsync_DuplicateAdminEmailAcrossTenants_BothSucceed()
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var provisioningSvc = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
        var onboardingSvc = scope.ServiceProvider.GetRequiredService<ITenantOnboardingService>();

        var tenantA = new Tenant { Name = "Tenant A", SchemaName = "tenant_dup_email_a" };
        var tenantB = new Tenant { Name = "Tenant B", SchemaName = "tenant_dup_email_b" };
        db.Tenants.AddRange(tenantA, tenantB);
        await db.SaveChangesAsync();

        await provisioningSvc.ProvisionSchemaAsync(tenantA, CancellationToken.None);
        await provisioningSvc.ProvisionSchemaAsync(tenantB, CancellationToken.None);

        const string sharedEmail = "shared-admin@example.com";
        await onboardingSvc.OnboardTenantAsync(tenantA, sharedEmail, "Admin A", "TempPassw0rd!", CancellationToken.None);
        await onboardingSvc.OnboardTenantAsync(tenantB, sharedEmail, "Admin B", "TempPassw0rd!", CancellationToken.None);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        Assert.Equal("Active", (await verifyDb.Tenants.SingleAsync(t => t.Id == tenantA.Id)).Status);
        Assert.Equal("Active", (await verifyDb.Tenants.SingleAsync(t => t.Id == tenantB.Id)).Status);
    }

    // ---------- helpers ----------

    private static async Task<bool> HasColumnPrivilegeAsync(NpgsqlConnection conn, string qualifiedTable, string column) =>
        await conn.ExecuteScalarAsync<bool>(
            "SELECT has_column_privilege('formforge_preview', @t, @c, 'SELECT')",
            new { t = qualifiedTable, c = column });

    // has_table_privilege errors ("relation does not exist") rather than returning false
    // for a relation that isn't there — guard with to_regclass first so a not-yet-real
    // table (mfa_sessions today) reads as "no privilege" instead of throwing.
    private static async Task<bool> HasTablePrivilegeAsync(NpgsqlConnection conn, string qualifiedTable)
    {
        var exists = await conn.ExecuteScalarAsync<bool>(
            "SELECT to_regclass(@t) IS NOT NULL", new { t = qualifiedTable });
        if (!exists)
        {
            return false;
        }

        return await conn.ExecuteScalarAsync<bool>(
            "SELECT has_table_privilege('formforge_preview', @t, 'SELECT')",
            new { t = qualifiedTable });
    }
}
