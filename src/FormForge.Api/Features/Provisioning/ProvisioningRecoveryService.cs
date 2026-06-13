using System.Threading.Channels;
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
            using var scope = scopeFactory.CreateScope();
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
                LogNoJobsRecovered(logger);
                return;
            }

            LogRecoveringJobs(logger, pendingMenus.Count);

            foreach (var menu in pendingMenus)
            {
                var job = new ProvisioningJob(
                    menu.Id,
                    menu.DesignerId!,
                    menu.BoundVersion!.Value,
                    ActorId: null,
                    FromVersion: null);

                // stoppingToken on WriteAsync — if the host is shutting down during
                // startup, cancel rather than block. Any unwritten jobs will be
                // re-scanned on the next process start.
                await writer.WriteAsync(job, stoppingToken).ConfigureAwait(false);
                LogJobReenqueued(logger, menu.Id, menu.DesignerId!, menu.BoundVersion!.Value);
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

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ProvisioningRecoveryService — no pending menus found at startup; nothing to re-enqueue")]
    private static partial void LogNoJobsRecovered(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ProvisioningRecoveryService — re-enqueuing {Count} pending provisioning job(s) from previous run")]
    private static partial void LogRecoveringJobs(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ProvisioningRecoveryService — re-enqueued MenuId {MenuId} DesignerId {DesignerId} v{Version}")]
    private static partial void LogJobReenqueued(ILogger logger, Guid menuId, string designerId, int version);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "ProvisioningRecoveryService — startup scan failed; no jobs were re-enqueued. The next process start will retry.")]
    private static partial void LogRecoveryScanFailed(ILogger logger, Exception ex);
}
