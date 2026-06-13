using System.Diagnostics.Metrics;

namespace FormForge.Api.Features.Auth;

internal sealed class AuthMetrics : IDisposable
{
    public const string MeterName = "FormForge.Auth";

    private readonly Meter _meter;
    private readonly Counter<long> _refreshIssued;
    private readonly Counter<long> _refreshRevoked;
    private readonly Counter<long> _refreshReplayed;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
        Justification = "Registered via DI.")]
    public AuthMetrics()
    {
        _meter = new Meter(MeterName);
        _refreshIssued = _meter.CreateCounter<long>("formforge.refresh_token.issued",
            description: "Number of refresh tokens successfully issued.");
        _refreshRevoked = _meter.CreateCounter<long>("formforge.refresh_token.revoked",
            description: "Number of refresh tokens revoked during rotation.");
        _refreshReplayed = _meter.CreateCounter<long>("formforge.refresh_token.replayed",
            description: "Number of replayed (already-revoked) refresh token presentations.");
    }

    public void RecordIssued() => _refreshIssued.Add(1);
    public void RecordRevoked() => _refreshRevoked.Add(1);
    public void RecordReplayed() => _refreshReplayed.Add(1);

    public void Dispose() => _meter.Dispose();
}
