using Dapper;
using FormForge.Api.Features.Designer;
using FormForge.Api.Features.SchemaRegistry;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FormForge.Api.Features.DynamicCrud;

// Story 6.5 — recursive cascade soft-delete walker. Two-phase design:
//   Phase 1 (BuildSchemaGraphAsync): load all descendant schemas via EF before
//   the transaction opens (mirrors GetRecordHandler's EF-before-Dapper pattern).
//   Phase 2 (ExecuteAsync): perform all UPDATEs within a caller-supplied
//   NpgsqlTransaction so parent + all descendants are atomically soft-deleted.
//   Cycle protection uses recursion-stack semantics — a designer id is added to
//   the visited set before its subtree recurses and removed on backtrack, so
//   siblings sharing a child designer at the same depth (legal in a tree) are
//   not over-restricted. The CycleDetector at bind time is the primary guard;
//   this is defence in depth.
//
//   SELF-referencing tables (a component whose Repeater references itself — an
//   adjacency-list tree) are the one case where the same designer id legitimately
//   recurs at every depth along a single descent path. The designer-id guard must
//   NOT stop a self-edge or the cascade would halt after one level and orphan every
//   deeper node. This is safe because recursion on a self-edge is bounded by DATA,
//   not schema: the child SELECT/UPDATE both filter is_deleted = false, so each row
//   is soft-deleted at most once and even a (corrupt) cyclic parent chain
//   terminates. Genuine multi-designer cycles remain blocked at bind, so the
//   non-self branch of the guard still only ever fires as defence in depth.
internal static class SoftDeleteCascade
{
    internal sealed record NodeInfo(SafeIdentifier TableName, IReadOnlyList<string> ChildIds);

    // Phase 1 — BFS schema graph loader. Starts from the direct child IDs of the
    // parent. Fills `graph` with (designerId → NodeInfo) for every reachable
    // descendant. Skips any designerId that is not a valid SafeIdentifier or has
    // no Published schema. Returns immediately when childIds is empty.
    internal static async Task BuildSchemaGraphAsync(
        IReadOnlyList<string> childIds,
        FormForgeDbContext db,
        ISchemaRegistry schemaRegistry,
        Dictionary<string, NodeInfo> graph,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(childIds);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(schemaRegistry);
        ArgumentNullException.ThrowIfNull(graph);

        if (childIds.Count == 0) return;

        var queue = new Queue<string>(childIds);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        while (queue.Count > 0)
        {
            var designerId = queue.Dequeue();
            if (!visited.Add(designerId)) continue;  // cycle guard for graph load
            if (!SafeIdentifier.TryCreate(designerId, out var safeChildId, out _)) continue;

            // Always go to EF for the latest Published version — no specific
            // version is known at this point (same approach as GetRecordHandler's
            // child-schema loader). The registry caches per-version entries, which
            // is not what we need here.
            var rootJson = await db.ComponentSchemaVersions
                .AsNoTracking()
                .Where(v => v.DesignerId == safeChildId!.Value && v.Status == "Published")
                .OrderByDescending(v => v.Version)
                .Select(v => v.RootElement)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (rootJson is null) continue;

            var (_, grandchildIds) = RootElementParser.ParseFull(rootJson);

            graph[safeChildId!.Value] = new NodeInfo(safeChildId!, grandchildIds);

            foreach (var gcId in grandchildIds)
            {
                if (!visited.Contains(gcId))
                    queue.Enqueue(gcId);
            }
        }
    }

    // Phase 2 — recursive cascade executor. Runs within the caller-supplied
    // NpgsqlTransaction. For each direct child designer of the current parent:
    //   1. SELECT the non-deleted child row ids (needed for grandchild recursion)
    //   2. UPDATE all those child rows in one statement (sets cascade_event_id)
    //   3. Recurse into each child row's own grandchildren.
    // `visited` is a recursion-stack set: a designer id is added before its
    // subtree recurses and removed on backtrack, so two sibling parent rows
    // sharing a child designer at the same depth both have their grandchildren
    // processed.
    internal static async Task ExecuteAsync(
        string parentDesignerId,
        IReadOnlyList<string> directChildDesignerIds,
        Dictionary<string, NodeInfo> graph,
        Guid parentId,
        Guid cascadeEventId,
        DateTimeOffset updatedAt,
        Guid? actorId,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        HashSet<string> visited,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parentDesignerId);
        ArgumentNullException.ThrowIfNull(directChildDesignerIds);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(visited);

        var fkColumnName = DynamicQueryBuilder.BuildFkColumnName(parentDesignerId);

        foreach (var childDesignerId in directChildDesignerIds)
        {
            // A SELF-edge (childDesignerId == parentDesignerId) is an adjacency-list
            // tree, not a cycle — never skip it. Its recursion is bounded by data (the
            // is_deleted = false filter on the child SELECT/UPDATE), so it terminates
            // without the designer-id guard. For non-self edges the guard remains as
            // defence in depth. (See the class note.)
            var isSelfEdge = string.Equals(childDesignerId, parentDesignerId, StringComparison.Ordinal);
            if (!isSelfEdge && visited.Contains(childDesignerId)) continue;
            if (!graph.TryGetValue(childDesignerId, out var childNode)) continue;

            // SELECT child row IDs BEFORE the UPDATE — these IDs are the parents
            // for the next recursion level.
            var (selectSql, selectParams) = DynamicQueryBuilder.BuildSelectChildIdsByFkQuery(
                childNode.TableName, fkColumnName, parentId);
            var childRowIds = (await conn.QueryAsync<Guid>(new CommandDefinition(
                selectSql, selectParams, transaction: tx,
                commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false))
                .ToList();

            // UPDATE all non-deleted children of parentId in one statement.
            var (updateSql, updateParams) = DynamicQueryBuilder.BuildCascadeChildSoftDeleteQuery(
                childNode.TableName, fkColumnName, parentId, actorId, cascadeEventId, updatedAt);
            await conn.ExecuteAsync(new CommandDefinition(
                updateSql, updateParams, transaction: tx,
                commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

            // Recurse into each child row's grandchildren. visited is push/pop so
            // siblings sharing a child designer are not over-restricted. Only pop if
            // THIS call added the id: on a self-edge it is already on the stack (e.g.
            // the seeded root designer), and removing it on backtrack would corrupt an
            // ancestor's marker.
            var addedToStack = visited.Add(childDesignerId);
            try
            {
                foreach (var childRowId in childRowIds)
                {
                    await ExecuteAsync(
                        childDesignerId, childNode.ChildIds, graph,
                        childRowId, cascadeEventId, updatedAt, actorId,
                        conn, tx, visited, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                if (addedToStack) visited.Remove(childDesignerId);
            }
        }
    }

    // Story 6.6 — flat cascade-restore walker. Unlike ExecuteAsync (which recurses
    // per-row), this method iterates ALL nodes in the pre-loaded `graph` and issues
    // one UPDATE per child table, matching rows by cascade_event_id. No per-row
    // recursion is needed: the cascade_event_id is unique per cascade-delete event,
    // so all rows sharing it are the correct set to restore. Runs within the
    // caller-supplied NpgsqlTransaction so parent + all descendants are atomically
    // restored. Called only when parentCascadeEventId is non-null.
    internal static async Task RestoreCascadeAsync(
        Dictionary<string, NodeInfo> graph,
        Guid parentCascadeEventId,
        Guid? actorId,
        DateTimeOffset updatedAt,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(tx);

        // Iterate ALL nodes in the graph (flat BFS result). One UPDATE per table
        // suffices because cascade_event_id is the correlation key, unique per event.
        foreach (var (_, nodeInfo) in graph)
        {
            var (updateSql, updateParams) = DynamicQueryBuilder.BuildRestoreCascadeChildrenQuery(
                nodeInfo.TableName, parentCascadeEventId, actorId, updatedAt);
            await conn.ExecuteAsync(new CommandDefinition(
                updateSql, updateParams, transaction: tx,
                commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);
        }
    }
}
