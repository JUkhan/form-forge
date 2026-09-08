using Dapper;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Features.Designer;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FormForge.Api.Features.Tenancy;

// Story 12.2 (FR-74 / Decision 7.7) — the first dynamic-schema EF Core migration path
// in this codebase. No prior art exists (confirmed: zero hits for HasDefaultSchema /
// search_path anywhere in the project), so this is deliberately narrow: validate,
// CREATE SCHEMA, replay the unmodified static migration set. Advancing Tenant.Status,
// seeding, Dataset Manager scoping, and recovery are all Story 12.7's job.
//
// HasDefaultSchema is not used — it is static per compiled model and cannot vary per
// call. Instead, a dedicated NpgsqlConnection whose SearchPath targets the new schema
// drives a fresh FormForgeDbContext instance's migrator: MigrateAsync() resolves every
// unqualified name in every migration's Up() — including __EFMigrationsHistory —
// through the *connection's* effective schema resolution, not a value baked into the
// compiled model. Every migration runs unmodified, in original order.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed partial class TenantProvisioningService(
    FormForgeDbContext db,
    DbConnectionFactory connectionFactory,
    IConfiguration configuration,
    ILogger<TenantProvisioningService> logger) : ITenantProvisioningService
{
    // Postgres system/reserved schema names. SafeIdentifier's format+keyword rules don't
    // reject these (they're all valid lowercase identifiers, none is a reserved SQL
    // keyword), so without this check schema_name = "public" would pass validation and
    // the collision check, then fail at CREATE SCHEMA with a raw Postgres exception
    // instead of the friendly validation error the I/O matrix promises.
    // internal (not private) + IsReservedSchemaName below so TenantEndpoints.CreateTenantHandler
    // (Story 12.5) can run this same check BEFORE inserting a tenant row — reused, not
    // duplicated, per that story's "do not reimplement it" boundary.
    private static readonly HashSet<string> ReservedSchemaNames = new(StringComparer.Ordinal)
    {
        "public", "pg_catalog", "information_schema", "pg_temp",
    };

    internal static bool IsReservedSchemaName(string schemaName) => ReservedSchemaNames.Contains(schemaName);

    public async Task ProvisionSchemaAsync(Tenant tenant, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        // 1. Validate schema_name format — same defense-in-depth posture as every other
        // dynamic identifier in this codebase (designerId, fieldKey, ...). Runs before
        // any DDL touches the value.
        if (!SafeIdentifier.TryCreate(tenant.SchemaName, out var safeSchemaName, out var formatError))
        {
            throw new InvalidOperationException(
                $"Tenant schema_name '{tenant.SchemaName}' is invalid: {formatError}");
        }

        var schemaName = safeSchemaName!.Value;

        if (ReservedSchemaNames.Contains(schemaName))
        {
            throw new InvalidOperationException(
                $"Tenant schema_name '{schemaName}' is a reserved PostgreSQL system schema and cannot be used.");
        }

        // 2. App-level collision pre-check, ahead of Story 12.1's DB-level unique index
        // (uq_tenants_schema_name) backstop. Excludes the tenant's own row so re-running
        // provisioning for the same already-persisted tenant does not self-collide.
        var collision = await db.Tenants
            .AsNoTracking()
            .AnyAsync(t => t.SchemaName == schemaName && t.Id != tenant.Id, ct)
            .ConfigureAwait(false);

        if (collision)
        {
            throw new InvalidOperationException(
                $"Tenant schema_name '{schemaName}' is already in use by another tenant.");
        }

        // 3. CREATE SCHEMA via the existing DbConnectionFactory raw-DDL pattern (Decision
        // 1.6 / Story 5.3's DdlEmitter shape) — open, execute, dispose. No transaction
        // needed for a single DDL statement.
        var schemaConnection = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await schemaConnection.ExecuteAsync(
                new CommandDefinition(
                    $"CREATE SCHEMA \"{schemaName}\"",
                    commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds,
                    cancellationToken: ct))
                .ConfigureAwait(false);
        }
        finally
        {
            await schemaConnection.DisposeAsync().ConfigureAwait(false);
        }

        LogSchemaCreated(logger, schemaName);

        // 4. Replay the full, unmodified static-schema EF Core migration set into the new
        // schema (Design Notes). ConnectionStrings:formforge is reused as-is — it already
        // has the privileges this needs (the existing migrations already issue
        // CREATE ROLE/GRANT under it).
        var baseConnectionString = configuration.GetConnectionString("formforge")
            ?? throw new InvalidOperationException("Connection string 'formforge' not configured.");

        // Story 5.3's DdlEmitter pattern: try/finally rather than `await using var` so
        // every DisposeAsync call can carry ConfigureAwait(false) (CA2007).
        var csb = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schemaName };
        var tenantConnection = new NpgsqlConnection(csb.ConnectionString);
        try
        {
            var options = new DbContextOptionsBuilder<FormForgeDbContext>()
                .UseNpgsql(
                    tenantConnection,
                    npgsqlOptions => npgsqlOptions.CommandTimeout(DbConnectionFactory.DdlCommandTimeoutSeconds))
                .Options;
            var tenantDb = new FormForgeDbContext(options);
            try
            {
                await tenantDb.Database.MigrateAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                await tenantDb.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await tenantConnection.DisposeAsync().ConfigureAwait(false);
        }

        LogSchemaMigrated(logger, schemaName);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "TenantProvisioningService — CREATE SCHEMA {SchemaName} succeeded")]
    private static partial void LogSchemaCreated(ILogger logger, string schemaName);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "TenantProvisioningService — static migration set replayed into schema {SchemaName}")]
    private static partial void LogSchemaMigrated(ILogger logger, string schemaName);
}
