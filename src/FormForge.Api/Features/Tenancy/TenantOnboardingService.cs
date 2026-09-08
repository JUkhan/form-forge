using Dapper;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Features.Auth;
using FormForge.Api.Features.Designer;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FormForge.Api.Features.Tenancy;

// Story 12.7 (FR-75 / Decision 7.7) — finishes onboarding for a tenant whose schema and
// static migration set already exist (Story 12.2 complete): the tenant's own Dataset
// Manager VIEW namespace, formforge_preview grants scoped to that schema, the tenant's
// first user + tenant-admin UserRole, a best-effort welcome email, then Status = "Active".
//
// Design Notes: the DDL step (dataset namespace + grants) and the EF seeding step (first
// user) run on separate connections with no encompassing transaction — same split as
// Story 12.2's create-schema-then-migrate. A crash between them leaves grants applied but
// no seeded user; TenantProvisioningRecoveryService only flags that, it never reconciles it
// (12.2's own precedent).
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed partial class TenantOnboardingService(
    FormForgeDbContext db,
    DbConnectionFactory connectionFactory,
    IConfiguration configuration,
    IEmailService emailService,
    IOptions<SmtpOptions> smtpOptions,
    IPasswordHasher passwordHasher,
    ILogger<TenantOnboardingService> logger) : ITenantOnboardingService
{
    // The tenant-admin role's deterministic id, seeded by the static migration set
    // (CreateRolesRolePermissionsAndUserRoles) into every tenant schema via Story 12.2's
    // replay. Reused as-is — this service never inserts a new role row.
    private static readonly Guid TenantAdminRoleId = new("00000000-0000-0000-0000-000000000001");

    // Same sensitive-table list the migrations revoke SELECT on for formforge_preview
    // (CreateDatasetManagerFoundation), replicated schema-qualified against the tenant's
    // own schema instead of public.
    private static readonly string[] RevokedTables =
    [
        "users", "roles", "refresh_tokens", "password_reset_tokens",
        "mfa_backup_codes", "mfa_sessions", "schema_audit_log",
        "mutation_audit_log", "dataset_audit_log", "custom_dataset",
    ];

    public async Task OnboardTenantAsync(
        Tenant tenant,
        string adminEmail,
        string adminDisplayName,
        string adminTemporaryPassword,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentException.ThrowIfNullOrEmpty(adminEmail);
        ArgumentException.ThrowIfNullOrEmpty(adminDisplayName);
        ArgumentException.ThrowIfNullOrEmpty(adminTemporaryPassword);

        // Re-validate schema_name the same defense-in-depth way Story 12.2 does — this
        // service must never trust that Tenant.SchemaName is safe to interpolate into DDL,
        // even though the precondition is that Story 12.2 already validated and used it.
        if (!SafeIdentifier.TryCreate(tenant.SchemaName, out var safeSchemaName, out var formatError))
        {
            throw new InvalidOperationException(
                $"Tenant schema_name '{tenant.SchemaName}' is invalid: {formatError}");
        }

        var schemaName = safeSchemaName!.Value;
        const string DatasetsSuffix = "_datasets";
        var datasetsSchemaName = $"{schemaName}{DatasetsSuffix}";

        // SafeIdentifier caps schemaName at 63 chars on its own, but Postgres identifiers
        // are capped at 63 bytes too — appending "_datasets" can push a valid schemaName
        // over that limit. Postgres silently truncates rather than erroring, which risks
        // two different tenants' derived dataset namespaces colliding. Fail loudly instead,
        // before any DDL runs.
        if (datasetsSchemaName.Length > 63)
        {
            throw new InvalidOperationException(
                $"Tenant schema_name '{schemaName}' is too long to derive a dataset namespace " +
                $"('{datasetsSchemaName}', {datasetsSchemaName.Length} bytes) within PostgreSQL's 63-byte identifier limit.");
        }

        // 1. Tenant-scoped Dataset Manager VIEW namespace + formforge_preview grants,
        // scoped to the tenant's own schema. Raw DDL via the existing DbConnectionFactory
        // pattern (Decision 1.6) — open, execute, dispose. No transaction: two independent
        // DDL statements, same posture as Story 12.2's single CREATE SCHEMA.
        var ddlConnection = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await ddlConnection.ExecuteAsync(
                new CommandDefinition(
                    $"CREATE SCHEMA \"{datasetsSchemaName}\"",
                    commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds,
                    cancellationToken: ct))
                .ConfigureAwait(false);

            await ddlConnection.ExecuteAsync(
                new CommandDefinition(
                    BuildPreviewGrantSql(schemaName),
                    commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds,
                    cancellationToken: ct))
                .ConfigureAwait(false);
        }
        finally
        {
            await ddlConnection.DisposeAsync().ConfigureAwait(false);
        }

        LogNamespaceAndGrantsReady(logger, tenant.Id, datasetsSchemaName, schemaName);

        // 2. Seed the tenant's first user + UserRole against the existing tenant-admin
        // role, via a schema-scoped FormForgeDbContext (same SearchPath-driven pattern as
        // Story 12.2's migration replay — HasDefaultSchema can't vary per call).
        var baseConnectionString = configuration.GetConnectionString("formforge")
            ?? throw new InvalidOperationException("Connection string 'formforge' not configured.");

        var csb = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schemaName };
        var tenantConnection = new NpgsqlConnection(csb.ConnectionString);
        User adminUser;
        try
        {
            var options = new DbContextOptionsBuilder<FormForgeDbContext>()
                .UseNpgsql(tenantConnection)
                .Options;
            var tenantDb = new FormForgeDbContext(options);
            try
            {
                var normalizedEmail = adminEmail.Trim().ToLowerInvariant();
                adminUser = new User
                {
                    Email = normalizedEmail,
                    DisplayName = adminDisplayName.Trim(),
                    PasswordHash = passwordHasher.Hash(adminTemporaryPassword),
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                tenantDb.Users.Add(adminUser);

                // Set via the User navigation, not UserId directly: User.Id is
                // store-generated (gen_random_uuid()) so it is still the CLR default at
                // this point — only navigation-based fixup lets EF resolve the FK once
                // both rows are inserted in the same SaveChangesAsync call.
                tenantDb.UserRoles.Add(new UserRole
                {
                    User = adminUser,
                    RoleId = TenantAdminRoleId,
                    CreatedAt = DateTimeOffset.UtcNow,
                });

                await tenantDb.SaveChangesAsync(ct).ConfigureAwait(false);
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

        LogAdminUserSeeded(logger, tenant.Id, schemaName);

        // 3. Best-effort welcome email — mirrors UserEndpoints' admin-created-user call
        // site exactly: linked CTS with a 3-second timeout, catch-and-swallow, never
        // rethrows. No HttpContext here, so smtpOptions.Value.BaseUrl is the only source
        // for the login base URL, and tenant.Id is the correlation id.
        using (var emailCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            emailCts.CancelAfter(TimeSpan.FromSeconds(3));
            var correlationId = tenant.Id.ToString();
            var loginBaseUrl = smtpOptions.Value.BaseUrl ?? string.Empty;
            try
            {
                await emailService
                    .TrySendWelcomeEmailAsync(
                        adminUser.Email,
                        adminTemporaryPassword,
                        loginBaseUrl,
                        correlationId,
                        emailCts.Token)
                    .ConfigureAwait(false);
            }
#pragma warning disable CA1031 // welcome email is best-effort; never block activation
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogWelcomeEmailFailed(logger, ex, tenant.Id);
            }
        }

        // 4. Activate. Re-fetch through this service's own FormForgeDbContext so the
        // update works whether or not the caller's `tenant` instance is already tracked
        // here — EF's identity resolution returns the same tracked instance either way,
        // so no double-tracking conflict is possible.
        var trackedTenant = await db.Tenants
            .FirstOrDefaultAsync(t => t.Id == tenant.Id, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Tenant '{tenant.Id}' was not found.");

        trackedTenant.Status = "Active";
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        tenant.Status = trackedTenant.Status;

        LogTenantActivated(logger, tenant.Id, schemaName);
    }

    // Replicates CreateDatasetManagerFoundation's bulk GRANT + guarded per-table REVOKE,
    // and RestrictPreviewRoleUsersColumns' column-level users grant, schema-qualified
    // against the tenant's own schema instead of public. Same IF EXISTS (pg_roles...) /
    // to_regclass guards as the migrations — schemaName is safe to interpolate here
    // because it was just re-validated via SafeIdentifier above.
    private static string BuildPreviewGrantSql(string schemaName) => $"""
        DO $$
        BEGIN
          IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'formforge_preview') THEN
            GRANT SELECT ON ALL TABLES IN SCHEMA "{schemaName}" TO formforge_preview;
          END IF;
        END
        $$;

        DO $$
        DECLARE t text;
        BEGIN
          IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'formforge_preview') THEN
            FOREACH t IN ARRAY ARRAY[{RevokedTablesSqlArray}]
            LOOP
              IF to_regclass('"{schemaName}".' || t) IS NOT NULL THEN
                EXECUTE format('REVOKE SELECT ON "{schemaName}".%I FROM formforge_preview', t);
              END IF;
            END LOOP;

            IF to_regclass('"{schemaName}".users') IS NOT NULL THEN
              GRANT SELECT (id, display_name, email, is_active)
                ON "{schemaName}".users TO formforge_preview;
            END IF;
          END IF;
        END
        $$;
        """;

    private static string RevokedTablesSqlArray =>
        string.Join(", ", Array.ConvertAll(RevokedTables, t => $"'{t}'"));

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "TenantOnboardingService — dataset namespace {DatasetsSchemaName} and formforge_preview grants scoped to {SchemaName} ready for tenant {TenantId}")]
    private static partial void LogNamespaceAndGrantsReady(ILogger logger, Guid tenantId, string datasetsSchemaName, string schemaName);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "TenantOnboardingService — first admin user seeded for tenant {TenantId} in schema {SchemaName}")]
    private static partial void LogAdminUserSeeded(ILogger logger, Guid tenantId, string schemaName);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "TenantOnboardingService — welcome email failed for tenant {TenantId}; onboarding proceeds regardless")]
    private static partial void LogWelcomeEmailFailed(ILogger logger, Exception exception, Guid tenantId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "TenantOnboardingService — tenant {TenantId} (schema {SchemaName}) activated")]
    private static partial void LogTenantActivated(ILogger logger, Guid tenantId, string schemaName);
}
