using System.Threading.Channels;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FormForge.Api.Features.Provisioning;

// Story 5.2 — single-consumer background service that drains the provisioning
// Channel. Single-consumer is intentional (AR-9 / AR-37): sequential DDL avoids
// concurrent CREATE/ALTER conflicts on shared tables. The service is registered
// as a singleton (AddHostedService default lifetime), so scoped services like
// FormForgeDbContext MUST be resolved via IServiceScopeFactory per job — never
// injected directly into the constructor.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via AddHostedService.")]
internal sealed partial class ProvisioningBackgroundService(
    ChannelReader<ProvisioningJob> reader,
    IServiceScopeFactory scopeFactory,
    ILogger<ProvisioningBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            await ProcessJobAsync(job, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessJobAsync(ProvisioningJob job, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();

        // Menu-less job (admin Table Provisioning tab): no menu row to load or update.
        // Run the emit and persist the audit row only; status is derived downstream
        // from table existence + the schema audit log.
        if (job.MenuId is null)
        {
            await ProcessMenulessJobAsync(scope, db, job).ConfigureAwait(false);
            return;
        }

        // CancellationToken.None: once a job is dequeued we see it through to completion
        // so host shutdown doesn't abandon a job mid-flight, leaving the row stuck Pending.
        // stoppingToken is honoured at the ReadAllAsync loop boundary in ExecuteAsync.
        var menu = await db.Menus.FirstOrDefaultAsync(m => m.Id == job.MenuId.Value, CancellationToken.None).ConfigureAwait(false);
        if (menu is null)
        {
            LogMenuMissing(logger, job.MenuId.Value);
            return;
        }

        // Guard: provisioning status may have changed between enqueue and dispatch
        // (a Retry or a re-bind raced ahead). Only process if still Pending so a
        // duplicate job from a retry queue cannot stomp a fresher state.
        if (menu.ProvisioningStatus != "Pending")
        {
            LogNotPending(logger, job.MenuId.Value, menu.ProvisioningStatus);
            return;
        }

        try
        {
            var emitter = scope.ServiceProvider.GetRequiredService<DdlEmitter>();
            // CancellationToken.None: once a job is dequeued we run it to completion.
            // Host shutdown is honoured at the ReadAllAsync loop boundary in ExecuteAsync.
            await emitter.EmitAsync(job, CancellationToken.None).ConfigureAwait(false);

            menu.ProvisioningStatus = "Success";
            menu.ProvisioningError = null;
            LogProvisioningSucceeded(logger, job.MenuId.Value, job.DesignerId, job.Version);
        }
        catch (OperationCanceledException)
        {
            // Defensive re-throw: EmitAsync is called with CancellationToken.None so
            // this path cannot be triggered by token cancellation today. Kept as a
            // safeguard for future callers that may pass a live token — OCE must not
            // be swallowed and recorded as Error; Story 5.8's recovery scanner will
            // re-enqueue any Pending rows on next startup.
            throw;
        }
#pragma warning disable CA1031 // catch-all is intentional — record any failure on the row, never crash the consumer loop
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogProvisioningFailed(logger, ex, job.MenuId.Value, job.DesignerId, job.Version);
            menu.ProvisioningStatus = "Error";
            menu.ProvisioningError = ex.Message;
        }
        finally
        {
            menu.UpdatedAt = DateTimeOffset.UtcNow;
            // Use CancellationToken.None for the terminal save so a host shutdown
            // mid-job still persists the final status. ReadAllAsync already
            // honours stoppingToken at the loop boundary.
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    // Menu-less provisioning (admin Table Provisioning tab). No menu row exists, so
    // there is nowhere to record a Pending/Error status — by design the request-time
    // validations (DesignerNotFound / VersionNotPublished / RepeaterCycle / InvalidPgType)
    // catch the common failures synchronously and return them to the admin. A rare
    // failure in the async DDL emit is logged here; the table simply will not appear
    // as provisioned and the admin can re-run. On success the DdlEmitter has appended a
    // SchemaAuditLogEntry to the context — we SaveChanges to persist it (mirrors the
    // menu path's finally-block save).
    private async Task ProcessMenulessJobAsync(IServiceScope scope, FormForgeDbContext db, ProvisioningJob job)
    {
        try
        {
            var emitter = scope.ServiceProvider.GetRequiredService<DdlEmitter>();
            await emitter.EmitAsync(job, CancellationToken.None).ConfigureAwait(false);
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            LogMenulessProvisioned(logger, job.DesignerId, job.Version);
        }
#pragma warning disable CA1031 // catch-all is intentional — a failed emit must never crash the consumer loop
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // The audit row (if added before the throw) is discarded by not calling
            // SaveChanges; the Dapper DDL transaction has already rolled back inside EmitAsync.
            LogMenulessFailed(logger, ex, job.DesignerId, job.Version);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Provisioned menu-less table for DesignerId {DesignerId} v{Version} — Success")]
    private static partial void LogMenulessProvisioned(ILogger logger, string designerId, int version);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Menu-less provisioning failed for DesignerId {DesignerId} v{Version}")]
    private static partial void LogMenulessFailed(ILogger logger, Exception ex, string designerId, int version);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ProvisioningJob for MenuId {MenuId} — menu no longer exists; skipping")]
    private static partial void LogMenuMissing(ILogger logger, Guid menuId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ProvisioningJob for MenuId {MenuId} — status is {Status}, not Pending; skipping")]
    private static partial void LogNotPending(ILogger logger, Guid menuId, string? status);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Provisioned MenuId {MenuId} DesignerId {DesignerId} v{Version} — Success")]
    private static partial void LogProvisioningSucceeded(ILogger logger, Guid menuId, string designerId, int version);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Provisioning failed for MenuId {MenuId} DesignerId {DesignerId} v{Version}")]
    private static partial void LogProvisioningFailed(ILogger logger, Exception ex, Guid menuId, string designerId, int version);
}
