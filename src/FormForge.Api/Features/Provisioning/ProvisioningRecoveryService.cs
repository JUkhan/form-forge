using System.Threading.Channels;
using FormForge.Api.Features.Tenancy;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FormForge.Api.Features.Provisioning;

// Story 5.8 — startup-only recovery scanner. On process start, queries any menus
// stuck at provisioning_status = 'Pending' (left behind by a prior crash or by
// the documented Dapper-EF dual-write hazard) and re-enqueues them onto the same
// Channel<ProvisioningJob> the bind endpoint uses. The single-consumer
// ProvisioningBackgroundService then drains them with the same DDL pipeline; CREATE
// TABLE IF NOT EXISTS + AddMissingColumnsAsync make the re-run idempotent (Story 5.3
// patch D1, Story 5.4 ALTER path). ActorId/FromVersion are null for the same reason
// as RetryBindingAsync: neither is recorded on the menu row, and threading an
// audit-log lookup through the recovery service is over-engineered for v1.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via AddHostedService.")]
internal sealed partial class ProvisioningRecoveryService(
    ChannelWriter<ProvisioningJob> writer,
    IServiceScopeFactory scopeFactory,
    ILogger<ProvisioningRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Story 12.6 follow-up (Decision 7.7) — `menus` is a per-tenant table now, so a
            // single scan can only ever see one schema's Pending rows. Sweep `public` (the
            // legacy single-tenant deployment) and then every Active tenant's own schema.
            var recovered = await ScanAndEnqueueAsync(tenant: null, stoppingToken).ConfigureAwait(false);

            foreach (var tenant in await LoadActiveTenantsAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    recovered += await ScanAndEnqueueAsync(tenant, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
#pragma warning disable CA1031 // one unreadable tenant schema (half-provisioned, manually altered) must not abort the sweep for every other tenant
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogTenantScanFailed(logger, ex, tenant.TenantId, tenant.SchemaName);
                }
            }

            if (recovered == 0)
            {
                LogNoJobsRecovered(logger);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown during startup — propagate without a noisy log.
            throw;
        }
#pragma warning disable CA1031 // catch-all is intentional — recovery is a safety net; if the scan itself fails (DB unreachable at startup, transient connection error, malformed state) the app MUST still come up. The next process start will retry the scan.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRecoveryScanFailed(logger, ex);
        }
    }

    // Only Active tenants: a Provisioning/Error tenant may have a half-built schema whose
    // `menus` table does not exist yet, and re-running DDL against it is exactly what
    // TenantProvisioningRecoveryService deliberately refuses to do.
    private async Task<List<TenantTarget>> LoadActiveTenantsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();

        // Tenant is pinned to schema "public" in the EF model, so this resolves correctly
        // without a tenant being set on this scope.
        return await db.Tenants
            .AsNoTracking()
            .Where(t => t.Status == "Active")
            .Select(t => new TenantTarget(t.Id, t.SchemaName))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    // A fresh scope per target: ITenantContext is write-once-per-request by design, and a
    // scope's DbContext/connection should target exactly one schema for its whole lifetime.
    private async Task<int> ScanAndEnqueueAsync(TenantTarget? tenant, CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();

        if (tenant is not null)
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>()
                 .Set(tenant.TenantId, tenant.SchemaName);
        }

        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();

        // AsNoTracking — read-only scan; we never mutate the menu rows here. The
        // BackgroundService updates ProvisioningStatus when each job completes.
        // The DesignerId/BoundVersion null guards are defensive: in practice a
        // Pending row always has both set (BindDesignerAsync enforces it), but a
        // hand-edited DB row would otherwise crash job construction.
        var pendingMenus = await db.Menus
            .AsNoTracking()
            .Where(m => m.ProvisioningStatus == "Pending"
                     && m.DesignerId != null
                     && m.BoundVersion != null)
            .ToListAsync(stoppingToken)
            .ConfigureAwait(false);

        if (pendingMenus.Count == 0)
        {
            return 0;
        }

        LogRecoveringJobs(logger, pendingMenus.Count, tenant?.SchemaName ?? "public");

        foreach (var menu in pendingMenus)
        {
            // Stamped directly rather than via IProvisioningService: that path infers the
            // tenant from an ambient request scope, which does not exist at startup.
            var job = new ProvisioningJob(
                menu.Id,
                menu.DesignerId!,
                menu.BoundVersion!.Value,
                ActorId: null,
                FromVersion: null,
                TenantId: tenant?.TenantId,
                TenantSchema: tenant?.SchemaName);

            // stoppingToken on WriteAsync — if the host is shutting down during
            // startup, cancel rather than block. Any unwritten jobs will be
            // re-scanned on the next process start.
            await writer.WriteAsync(job, stoppingToken).ConfigureAwait(false);
            LogJobReenqueued(logger, menu.Id, menu.DesignerId!, menu.BoundVersion!.Value, tenant?.SchemaName ?? "public");
        }

        return pendingMenus.Count;
    }

    private sealed record TenantTarget(Guid TenantId, string SchemaName);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ProvisioningRecoveryService — no pending menus found in any schema at startup; nothing to re-enqueue")]
    private static partial void LogNoJobsRecovered(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ProvisioningRecoveryService — re-enqueuing {Count} pending provisioning job(s) from previous run in schema {SchemaName}")]
    private static partial void LogRecoveringJobs(ILogger logger, int count, string schemaName);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ProvisioningRecoveryService — re-enqueued MenuId {MenuId} DesignerId {DesignerId} v{Version} in schema {SchemaName}")]
    private static partial void LogJobReenqueued(ILogger logger, Guid menuId, string designerId, int version, string schemaName);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "ProvisioningRecoveryService — scan failed for tenant {TenantId} (schema {SchemaName}); other tenants were still scanned")]
    private static partial void LogTenantScanFailed(ILogger logger, Exception ex, Guid tenantId, string schemaName);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "ProvisioningRecoveryService — startup scan failed; no jobs were re-enqueued. The next process start will retry.")]
    private static partial void LogRecoveryScanFailed(ILogger logger, Exception ex);
}
