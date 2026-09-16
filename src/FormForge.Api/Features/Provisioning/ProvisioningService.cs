using System.Threading.Channels;
using FormForge.Api.Features.Tenancy;

namespace FormForge.Api.Features.Provisioning;

// Story 12.6 follow-up — Scoped (was Singleton) so it can read the enqueueing request's
// ITenantContext and stamp it onto every job. Stamping here rather than at each call site
// means no caller has to remember to do it, and a future enqueue site gets it for free.
// The ChannelWriter it wraps is still a Singleton, so the queue itself is unaffected.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class ProvisioningService(
    ChannelWriter<ProvisioningJob> writer,
    ITenantContext tenantContext) : IProvisioningService
{
    // WriteAsync, not TryWrite — silent capacity-failure drops would lose binds.
    // Channel is bounded to 256 (Program.cs); writes block briefly if full, which
    // is acceptable for an admin-only action.
    public async ValueTask EnqueueAsync(ProvisioningJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        // Only stamp when the caller hasn't already set a tenant explicitly
        // (ProvisioningRecoveryService constructs fully-stamped jobs of its own) and when
        // a tenant is actually resolved — otherwise the job stays tenant-less and the
        // consumer runs it against `public`, unchanged from the single-tenant behaviour.
        if (job.TenantId is null
            && tenantContext is { TenantId: { } tenantId, SchemaName: { } schemaName })
        {
            job = job with { TenantId = tenantId, TenantSchema = schemaName };
        }

        await writer.WriteAsync(job, ct).ConfigureAwait(false);
    }
}
