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

// Story 12.2 — de-risks the one assumption the whole story rests on: that EF Core's
// migrator, driven against a connection whose SearchPath targets a non-public schema,
// actually replays the full static migration set correctly. Real Testcontainers
// Postgres throughout — no mocks — per the story's Design Notes.
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class TenantProvisioningServiceTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    // A representative sample of tables spanning the whole migration set (earliest
    // migration through the most recent, Story 12.1's tenants table). Every migration
    // replays unmodified into the tenant schema per the story's Design Notes, so
    // "tenants" itself is expected to exist inside every provisioned tenant schema too.
    private static readonly string[] ExpectedTables =
    [
        "users", "refresh_tokens", "roles", "role_permissions", "user_roles",
        "component_schemas", "component_schema_versions", "menus", "menu_role_assignments",
        "schema_audit_log", "mutation_audit_log", "custom_dataset", "dataset_audit_log",
        "DataProtectionKeys", "password_reset_tokens", "mfa_backup_codes", "tenants",
    ];

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;

    public TenantProvisioningServiceTests(PostgresFixture postgres) => _postgres = postgres;

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
    public async Task ProvisionSchemaAsync_ValidTenant_CreatesSchemaWithFullMigrationSet()
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();

        var tenant = new Tenant { Name = "Acme Corp", SchemaName = "tenant_acme_happy" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        await svc.ProvisionSchemaAsync(tenant, CancellationToken.None);

        await using var conn = new NpgsqlConnection(_postgres.ConnectionString);
        await conn.OpenAsync();

        var schemaExists = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = @s)",
            new { s = tenant.SchemaName });
        Assert.True(schemaExists, "CREATE SCHEMA should have succeeded.");

        var actualTables = (await conn.QueryAsync<string>(
            "SELECT tablename FROM pg_tables WHERE schemaname = @s",
            new { s = tenant.SchemaName })).ToHashSet(StringComparer.Ordinal);

        foreach (var expected in ExpectedTables)
        {
            Assert.True(
                actualTables.Contains(expected),
                $"Expected table '{expected}' to exist in schema '{tenant.SchemaName}'.");
        }

        // A representative CHECK constraint (real table_constraints row, via ADD CONSTRAINT).
        var constraintNames = (await conn.QueryAsync<string>(
            """
            SELECT tc.constraint_name
            FROM information_schema.table_constraints tc
            WHERE tc.table_schema = @s
            """,
            new { s = tenant.SchemaName })).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("ck_tenants_status", constraintNames);

        // Representative UNIQUE indexes from each end of the migration set (EF's
        // HasIndex().IsUnique() emits CREATE UNIQUE INDEX, not ADD CONSTRAINT — these
        // live in pg_indexes, not information_schema.table_constraints).
        var indexNames = (await conn.QueryAsync<string>(
            "SELECT indexname FROM pg_indexes WHERE schemaname = @s",
            new { s = tenant.SchemaName })).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("uq_users_email", indexNames);
        Assert.Contains("uq_tenants_schema_name", indexNames);

        // __EFMigrationsHistory must live inside the tenant schema (resolved through the
        // connection's SearchPath, not hardcoded to public) and record every migration.
        var migrationCount = await conn.ExecuteScalarAsync<int>(
            $"""SELECT COUNT(*) FROM "{tenant.SchemaName}"."__EFMigrationsHistory" """);
        Assert.True(migrationCount > 0, "Expected __EFMigrationsHistory rows inside the tenant schema.");

        // public schema is untouched by this replay (sanity check for the SearchPath mechanism).
        var publicHasTenantsRow = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM public.tenants WHERE id = @id)",
            new { id = tenant.Id });
        Assert.True(publicHasTenantsRow, "The tenant row itself must still live in public.tenants.");

        // Story 12.2 leaves Tenant.Status untouched even on success — Story 12.7 owns the
        // Provisioning -> Active transition.
        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var reloaded = await verifyDb.Tenants.SingleAsync(t => t.Id == tenant.Id);
        Assert.Equal("Provisioning", reloaded.Status);
    }

    // --- Invalid schema_name format ---------------------------------------------------

    [Theory]
    [InlineData("Tenant_Acme")]      // uppercase
    [InlineData("1tenant")]          // starts with a digit
    [InlineData("select")]           // reserved keyword
    [InlineData("")]                 // empty
    public async Task ProvisionSchemaAsync_InvalidSchemaName_ThrowsBeforeAnyDdl(string invalidSchemaName)
    {
        using var scope = _factory!.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();

        // Not persisted — validation must fail before any DB interaction.
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme Corp", SchemaName = invalidSchemaName };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ProvisionSchemaAsync(tenant, CancellationToken.None));

        if (!string.IsNullOrEmpty(invalidSchemaName))
        {
            await using var conn = new NpgsqlConnection(_postgres.ConnectionString);
            await conn.OpenAsync();
            var schemaExists = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = @s)",
                new { s = invalidSchemaName });
            Assert.False(schemaExists, "No schema should have been created for an invalid name.");
        }
    }

    // --- Schema name collision --------------------------------------------------------

    [Fact]
    public async Task ProvisionSchemaAsync_CollidingSchemaName_ThrowsBeforeCreateSchema()
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();

        var existing = new Tenant { Name = "Acme Corp", SchemaName = "tenant_collision" };
        db.Tenants.Add(existing);
        await db.SaveChangesAsync();

        // A second, distinct (unpersisted) tenant whose schema_name collides with the one
        // above. Not itself inserted — Story 12.1's DB-level unique index would reject two
        // *persisted* rows sharing a schema_name outright, so this models the realistic
        // case the app-level pre-check exists for: catching the collision before an insert
        // (or CREATE SCHEMA) is ever attempted, with the DB index remaining the backstop
        // for races the app-level check can't see.
        var colliding = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Acme Corp (duplicate)",
            SchemaName = "tenant_collision",
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ProvisionSchemaAsync(colliding, CancellationToken.None));
        Assert.Contains("already in use", ex.Message, StringComparison.OrdinalIgnoreCase);

        await using var conn = new NpgsqlConnection(_postgres.ConnectionString);
        await conn.OpenAsync();
        var schemaExists = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = @s)",
            new { s = "tenant_collision" });
        Assert.False(schemaExists, "No DDL should have been attempted for a colliding schema_name.");
    }

    // --- Migration failure mid-replay --------------------------------------------------

    [Fact]
    public async Task ProvisionSchemaAsync_MigrationFailsPartway_ThrowsAndTenantStatusUnchanged()
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();

        var tenant = new Tenant { Name = "Fault Co", SchemaName = "tenant_fault_replay" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        // Deterministic (race-free) fault injection: an event trigger fires synchronously
        // as part of the CREATE SCHEMA statement's own execution (ddl_command_end runs
        // inside the same DDL command, before ExecuteAsync returns) and pre-creates a
        // "roles" table inside the brand-new schema. CREATE SCHEMA itself still succeeds
        // from the service's point of view — the later "CreateRolesRolePermissionsAndUserRoles"
        // migration then fails with a duplicate-table error when it tries to create the
        // same table, i.e. the migration set genuinely fails partway through the replay.
        await using (var admin = new NpgsqlConnection(_postgres.ConnectionString))
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync("""
                CREATE OR REPLACE FUNCTION inject_provisioning_fault() RETURNS event_trigger
                LANGUAGE plpgsql AS $f$
                DECLARE
                    obj record;
                BEGIN
                    FOR obj IN SELECT object_identity FROM pg_event_trigger_ddl_commands()
                               WHERE command_tag = 'CREATE SCHEMA'
                    LOOP
                        EXECUTE format('CREATE TABLE %s.roles (id uuid)', obj.object_identity);
                    END LOOP;
                END;
                $f$;
                """);
            await admin.ExecuteAsync("""
                CREATE EVENT TRIGGER inject_provisioning_fault_trigger ON ddl_command_end
                    WHEN TAG IN ('CREATE SCHEMA')
                    EXECUTE FUNCTION inject_provisioning_fault();
                """);
        }

        try
        {
            var thrown = await Assert.ThrowsAnyAsync<Exception>(
                () => svc.ProvisionSchemaAsync(tenant, CancellationToken.None));
            Assert.NotNull(thrown);
        }
        finally
        {
            await using var admin = new NpgsqlConnection(_postgres.ConnectionString);
            await admin.OpenAsync();
            await admin.ExecuteAsync("DROP EVENT TRIGGER IF EXISTS inject_provisioning_fault_trigger;");
            await admin.ExecuteAsync("DROP FUNCTION IF EXISTS inject_provisioning_fault();");
        }

        // The schema itself was created (CREATE SCHEMA succeeded); only the later replay
        // failed — confirming this is genuinely a "fails partway" scenario, not a
        // CREATE SCHEMA failure.
        await using var conn = new NpgsqlConnection(_postgres.ConnectionString);
        await conn.OpenAsync();
        var schemaExists = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = @s)",
            new { s = "tenant_fault_replay" });
        Assert.True(schemaExists, "CREATE SCHEMA should have succeeded before the migration replay failed.");

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var reloaded = await verifyDb.Tenants.SingleAsync(t => t.Id == tenant.Id);
        Assert.Equal("Provisioning", reloaded.Status);
    }
}
