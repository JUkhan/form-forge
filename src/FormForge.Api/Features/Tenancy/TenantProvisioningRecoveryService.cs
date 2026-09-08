using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FormForge.Api.Features.Tenancy;

// Story 12.7 (FR-75 AC-3) — startup-only recovery scanner mirroring ProvisioningRecoveryService's
// shape (Story 5.8), but with a deliberately different outcome: a tenant stuck at
// Status == "Provisioning" means either Story 12.2's schema-provisioning step or Story 12.7's
// own onboarding step crashed partway (Design Notes — DDL and EF seeding run on separate,
// non-transactional connections, so a crash between them leaves a tenant in an ambiguous
// half-done state). Unlike the menu-provisioning recovery path, there is no consumer channel
// to re-enqueue onto, and blindly re-running either step against a schema in an unknown state
// risks a partial-DDL double-apply. So this service only flags — never retries or mutates
// Tenant.Status — per FR-75 AC-3; an operator investigates and resolves manually.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via AddHostedService.")]
internal sealed partial class TenantProvisioningRecoveryService(
    IServiceScopeFactory scopeFactory,
    ILogger<TenantProvisioningRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();

            // AsNoTracking — read-only scan; flag only, never mutate.
            var stuckTenants = await db.Tenants
                .AsNoTracking()
                .Where(t => t.Status == "Provisioning")
                .ToListAsync(stoppingToken)
                .ConfigureAwait(false);

            if (stuckTenants.Count == 0)
            {
                LogNoTenantsStuck(logger);
                return;
            }

            foreach (var tenant in stuckTenants)
            {
                LogTenantStuckProvisioning(logger, tenant.Id, tenant.SchemaName);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown during startup — propagate without a noisy log.
            throw;
        }
#pragma warning disable CA1031 // catch-all is intentional — recovery is a safety net; if the scan itself fails (DB unreachable at startup, transient connection error) the app MUST still come up. The next process start will retry the scan.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRecoveryScanFailed(logger, ex);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "TenantProvisioningRecoveryService — no tenants stuck at 'Provisioning' found at startup")]
    private static partial void LogNoTenantsStuck(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "TenantProvisioningRecoveryService — tenant {TenantId} (schema {SchemaName}) is stuck at status 'Provisioning'; schema provisioning or onboarding did not complete and requires manual investigation")]
    private static partial void LogTenantStuckProvisioning(ILogger logger, Guid tenantId, string schemaName);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "TenantProvisioningRecoveryService — startup scan failed; the next process start will retry.")]
    private static partial void LogRecoveryScanFailed(ILogger logger, Exception ex);
}
