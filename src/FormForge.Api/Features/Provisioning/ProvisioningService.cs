using System.Threading.Channels;

namespace FormForge.Api.Features.Provisioning;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class ProvisioningService(ChannelWriter<ProvisioningJob> writer) : IProvisioningService
{
    // WriteAsync, not TryWrite — silent capacity-failure drops would lose binds.
    // Channel is bounded to 256 (Program.cs); writes block briefly if full, which
    // is acceptable for an admin-only action.
    public async ValueTask EnqueueAsync(ProvisioningJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        await writer.WriteAsync(job, ct).ConfigureAwait(false);
    }
}
