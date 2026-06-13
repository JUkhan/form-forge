namespace FormForge.Api.Features.Provisioning;

internal sealed record BindingDiffResponse(
    BindingInfo? CurrentBinding,
    int TargetVersion,
    IReadOnlyList<string> ColumnsToAdd,
    IReadOnlyList<string> ColumnsAlreadyPresent,
    IReadOnlyList<string> OrphanedColumns,
    IReadOnlyList<string> WillTriggerChildProvisioning,
    IReadOnlyList<string> EstimatedDdl);

internal sealed record BindingInfo(string DesignerId, int Version);

// Story 5.2 stub. Story 5.6 replaces this with real pg_attribute introspection
// so column lists reflect the live table shape. The stub returns empty arrays
// for the diff categories and a single placeholder DDL line so the admin diff
// modal renders without surfacing fake data.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class BindingDiffService
{
    // CA1822 suppressed: kept as an instance method because Story 5.6 will inject a
    // connection factory for pg_attribute introspection. Making it static now would
    // force a refactor (and a DI shape change) when that work lands.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822",
        Justification = "Stub; Story 5.6 will use injected instance state.")]
    public Task<BindingDiffResponse> ComputeAsync(
        string designerId,
        int currentVersion,
        int targetVersion,
        CancellationToken ct)
    {
        _ = ct;
        var response = new BindingDiffResponse(
            new BindingInfo(designerId, currentVersion),
            targetVersion,
            ColumnsToAdd: [],
            ColumnsAlreadyPresent: [],
            OrphanedColumns: [],
            WillTriggerChildProvisioning: [],
            EstimatedDdl: [$"-- CREATE/ALTER TABLE {designerId} (estimated DDL — Story 5.3/5.4 wires real DDL)"]);
        return Task.FromResult(response);
    }
}
