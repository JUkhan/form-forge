using FormForge.Api.Features.SchemaRegistry;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FormForge.Api.Features.Provisioning;

// Story 5.5 — pre-bind cycle detector for the Repeater reference graph. Runs
// inside MenuService.BindDesignerAsync BEFORE any state is written so a 422
// REPEATER_CYCLE is returned to the admin immediately instead of waiting for
// the background provisioning job to fail.
//
// Walks the published version graph: the root is the (rootDesignerId, rootVersion)
// the admin wants to bind; each Repeater's rowDesignerId resolves to that child
// designer's LATEST Published version (the same version that the provisioner
// would walk via ProvisionChildTablesAsync). An unpublished child is skipped
// because it cannot be provisioned and therefore cannot contribute to a cycle
// in the produced schema.
//
// DFS uses two sets per classic 3-colouring: inStack (gray — on the active
// path) and visited (black — fully explored, no cycle from here). The root is
// seeded into inStack before any recursion so a transitive back-edge to it
// (A→B→A) is caught.
//
// A DIRECT self-edge (a component whose Repeater references itself) is NOT a
// cycle — it is an intentional adjacency-list TREE. The provisioner emits a
// single table with a parent_<table>_id self-FK and does not recurse into the
// self-edge, so it is finite and safe. We therefore skip an edge whose target
// equals the node currently being expanded, at every level, while still
// reporting genuine multi-node cycles (A→B→A, A→B→C→B, …).
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class CycleDetector(FormForgeDbContext db)
{
    public async Task<bool> HasCycleAsync(
        string rootDesignerId,
        int rootVersion,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rootDesignerId);

        var rootVersionRow = await db.ComponentSchemaVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                v => v.DesignerId == rootDesignerId && v.Version == rootVersion,
                ct)
            .ConfigureAwait(false);

        if (rootVersionRow is null) return false;

        var (_, childIds) = RootElementParser.ParseFull(rootVersionRow.RootElement);
        if (childIds.Count == 0) return false;

        var inStack = new HashSet<string>(StringComparer.Ordinal) { rootDesignerId };
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var childId in childIds)
        {
            // A root self-edge is a tree, not a cycle — skip it (see class note).
            if (string.Equals(childId, rootDesignerId, StringComparison.Ordinal)) continue;
            if (await DfsAsync(childId, inStack, visited, ct).ConfigureAwait(false))
                return true;
        }
        return false;
    }

    private async Task<bool> DfsAsync(
        string designerId,
        HashSet<string> inStack,
        HashSet<string> visited,
        CancellationToken ct)
    {
        if (inStack.Contains(designerId)) return true;     // back-edge → cycle
        if (visited.Contains(designerId)) return false;    // fully explored, safe

        // Latest Published version is the only thing the provisioner will walk; an
        // unpublished child cannot reach the DDL pipeline and therefore cannot form
        // a cycle in the produced schema.
        var latestPublished = await db.ComponentSchemaVersions
            .AsNoTracking()
            .Where(v => v.DesignerId == designerId && v.Status == "Published")
            .OrderByDescending(v => v.Version)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (latestPublished is null) return false;

        inStack.Add(designerId);
        var (_, childIds) = RootElementParser.ParseFull(latestPublished.RootElement);
        foreach (var childId in childIds)
        {
            // A node's self-edge is a tree, not a cycle — skip it (see class note).
            if (string.Equals(childId, designerId, StringComparison.Ordinal)) continue;
            if (await DfsAsync(childId, inStack, visited, ct).ConfigureAwait(false))
                return true;
        }
        inStack.Remove(designerId);
        visited.Add(designerId);
        return false;
    }
}
