# Story 6.5: Soft-Delete a Record

Status: done

## Story

As an authorized user with `canDelete`,
I want to soft-delete a record,
So that the data is preserved and recoverable.

## Acceptance Criteria

**AC-1 — Happy-path soft-delete (individual, no Repeater children)**
**Given** an existing record that is not soft-deleted
**When** I DELETE `/api/data/{designerId}/{id}` with `canDelete` permission
**Then** the response is HTTP 200 with the full updated record
**And** `isDeleted: true` in the response
**And** `updatedAt` is server-side (newer than `createdAt`)
**And** `updatedBy` equals the JWT actor UUID
**And** `cascadeEventId: null` (individual soft-delete, no children)
**And** response uses camelCase system columns per AR-46 Option C (`id`, `createdAt`, `updatedAt`, `createdBy`, `updatedBy`, `isDeleted`, `cascadeEventId`)

**AC-2 — Cascade to Repeater children**
**Given** a record whose parent schema has `ChildRepeaterDesignerIds` indicating Repeater children
**When** I DELETE `/api/data/{designerId}/{id}`
**Then** a single `cascade_event_id` UUID is generated server-side
**And** the parent row is updated (`is_deleted = true`, `cascade_event_id = <uuid>`) within a single NpgsqlTransaction
**And** all non-deleted direct child rows (WHERE `parent_{designerId}_id = @parentId`) are updated with the same `cascade_event_id` within the same transaction
**And** grandchildren (children-of-children) are also cascade-soft-deleted transitively within the same transaction
**And** the transaction commits atomically (all or nothing)
**And** the response is HTTP 200 with the updated parent record (`cascadeEventId` non-null)

**AC-3 — Mutation audit log row**
**Given** a successful soft-delete (AC-1 or AC-2)
**When** the Dapper UPDATE(s) complete
**Then** a row is appended to `mutation_audit_log` with:
  - `operation: "SOFT_DELETE"`
  - `designer_id`: the parent designerId
  - `record_id`: the deleted record's id
  - `actor_id`: the JWT actor UUID (null for unauthenticated)
  - `timestamp`: the same `updatedAt` used in the UPDATE
  - `correlation_id`: the request correlation ULID
  - `new_values: null` (soft-delete doesn't change field values; only system columns change)
  - `previous_values: null` (no field-level diff needed for soft-delete)
**Note:** Only one audit row for the parent; child-level audit rows are deferred to a future hardening pass.

**AC-4 — Record not found → 404**
**Given** an `id` that does not exist in the provisioned table
**When** I DELETE `/api/data/{designerId}/{id}`
**Then** the response is HTTP 404 with `code: "NOT_FOUND"`

**AC-5 — Unprovisioned designer → 404**
**Given** a `designerId` for which no menu exists with `provisioningStatus = 'Success'`
**When** I DELETE `/api/data/{designerId}/{id}`
**Then** the response is HTTP 404 with `code: "TABLE_NOT_PROVISIONED"`

**AC-6 — Permission enforcement**
**Given** an authenticated user without `canDelete` on this resource
**When** I DELETE `/api/data/{designerId}/{id}`
**Then** the response is HTTP 403 with `code: "FORBIDDEN"`

**AC-7 — Already-deleted record → 422**
**Given** a record with `is_deleted = true`
**When** I DELETE `/api/data/{designerId}/{id}`
**Then** the response is HTTP 422 with `code: "RECORD_ALREADY_DELETED"` and `messageKey: "errors.recordAlreadyDeleted"`
**Note:** The WHERE `AND "is_deleted" = false` predicate in the UPDATE causes 0 rows affected → handler returns 422. This is distinct from NOT_FOUND (404) where the row doesn't exist at all.

**AC-8 — Rate limiting**
**Given** the `"data-write"` rate-limiting policy
**When** a user exceeds 60 DELETE `/api/data/*` requests per user per minute
**Then** the response is HTTP 429 with `Retry-After: 60`
**Note:** Applied per-endpoint via `.RequireRateLimiting("data-write")`. The `"data-write"` policy is already registered in `Program.cs` by Story 6.3. Do NOT add a new policy registration.

**AC-9 — Query timeout**
**Given** the Dapper SELECT and UPDATE(s) queries
**When** each query runs
**Then** `commandTimeout` is set to 5 seconds on every `CommandDefinition` (Decision 1.6)

---

## Tasks / Subtasks

- [x] **Task 1 — Add soft-delete query builders to `DynamicQueryBuilder.cs`** (AC: 1, 2, 7, 9)
  - [x] Modify `src/FormForge.Api/Features/DynamicCrud/DynamicQueryBuilder.cs`
  - Add three new static methods after `BuildUpdateQuery`:

  **Method A: `BuildSoftDeleteByIdQuery`** — soft-deletes one row by primary key:
  ```csharp
  // Story 6.5 — parameterized UPDATE for DELETE /api/data/{designerId}/{id}. Sets
  // is_deleted=true, updated_at, updated_by, and cascade_event_id (null for
  // individual deletes, UUID for cascade). The WHERE clause includes
  // AND "is_deleted" = false so a concurrent double-delete returns 0 rows affected
  // instead of silently succeeding. The caller must check rowsAffected and surface
  // 422 RECORD_ALREADY_DELETED if 0. Returns updatedAt so the caller can build the
  // response and audit row without a re-SELECT.
  internal static (string Sql, DynamicParameters Parameters, DateTimeOffset UpdatedAt)
      BuildSoftDeleteByIdQuery(
          SafeIdentifier tableName,
          Guid id,
          Guid? actorId,
          Guid? cascadeEventId)
  {
      ArgumentNullException.ThrowIfNull(tableName);

      var updatedAt = DateTimeOffset.UtcNow;

      // cascade_event_id uses a SQL NULL literal (not a parameter) when there are
      // no children — same pattern as BuildInsertQuery's cascade_event_id = NULL
      // to avoid Dapper's Npgsql UUID null-parameter edge case.
      var cascadePart = cascadeEventId.HasValue
          ? ", \"cascade_event_id\" = @p_cascade_event_id"
          : ", \"cascade_event_id\" = NULL";

      var sql = $"UPDATE \"{tableName.Value}\" SET \"is_deleted\" = true, " +
                $"\"updated_at\" = @p_updated_at, \"updated_by\" = @p_updated_by{cascadePart} " +
                $"WHERE \"id\" = @p_id AND \"is_deleted\" = false";

      var parameters = new DynamicParameters();
      parameters.Add("p_updated_at", updatedAt);
      parameters.Add("p_updated_by", actorId);
      parameters.Add("p_id",         id);
      if (cascadeEventId.HasValue)
          parameters.Add("p_cascade_event_id", cascadeEventId.Value);

      return (sql, parameters, updatedAt);
  }
  ```

  **Method B: `BuildSelectChildIdsByFkQuery`** — selects non-deleted child row IDs for the cascade recursion:
  ```csharp
  // Story 6.5 — SELECT id of all non-deleted children of a parent row. Used
  // during the cascade walk to collect child IDs before recursing to their own
  // children. fkColumnName is produced by BuildFkColumnName and is already a
  // validated identifier.
  internal static (string Sql, DynamicParameters Parameters) BuildSelectChildIdsByFkQuery(
      SafeIdentifier childTableName,
      string fkColumnName,
      Guid parentId)
  {
      ArgumentNullException.ThrowIfNull(childTableName);
      ArgumentException.ThrowIfNullOrEmpty(fkColumnName);

      var sql = $"SELECT \"id\" FROM \"{childTableName.Value}\" " +
                $"WHERE \"{fkColumnName}\" = @p_parent_id AND \"is_deleted\" = false";

      var parameters = new DynamicParameters();
      parameters.Add("p_parent_id", parentId);
      return (sql, parameters);
  }
  ```

  **Method C: `BuildCascadeChildSoftDeleteQuery`** — soft-deletes all non-deleted children of a parent in one UPDATE:
  ```csharp
  // Story 6.5 — UPDATE all non-deleted children of a parent row within the cascade
  // transaction. fkColumnName is produced by BuildFkColumnName. updatedAt is
  // passed in (not generated here) so parent and all descendants share one timestamp
  // snapshot. cascadeEventId is always non-null for cascade calls.
  internal static (string Sql, DynamicParameters Parameters) BuildCascadeChildSoftDeleteQuery(
      SafeIdentifier childTableName,
      string fkColumnName,
      Guid parentId,
      Guid? actorId,
      Guid cascadeEventId,
      DateTimeOffset updatedAt)
  {
      ArgumentNullException.ThrowIfNull(childTableName);
      ArgumentException.ThrowIfNullOrEmpty(fkColumnName);

      var sql = $"UPDATE \"{childTableName.Value}\" SET " +
                $"\"is_deleted\" = true, \"updated_at\" = @p_updated_at, " +
                $"\"updated_by\" = @p_updated_by, \"cascade_event_id\" = @p_cascade_event_id " +
                $"WHERE \"{fkColumnName}\" = @p_parent_id AND \"is_deleted\" = false";

      var parameters = new DynamicParameters();
      parameters.Add("p_updated_at",         updatedAt);
      parameters.Add("p_updated_by",         actorId);
      parameters.Add("p_cascade_event_id",   cascadeEventId);
      parameters.Add("p_parent_id",          parentId);
      return (sql, parameters);
  }
  ```

- [x] **Task 2 — Add `SoftDeleteCascade.cs` (new static class)** (AC: 2, 9)
  - [x] Create `src/FormForge.Api/Features/DynamicCrud/SoftDeleteCascade.cs`
  - This class encapsulates the recursive cascade walk: schema graph pre-loading (via EF, before the transaction is opened) and execution (Dapper, within caller's NpgsqlTransaction).

  ```csharp
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
  //   Cycle protection: visited set prevents infinite recursion in graphs that
  //   somehow escaped the CycleDetector at bind time (defence in depth).
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
              if (!visited.Add(designerId)) continue;  // cycle guard
              if (!SafeIdentifier.TryCreate(designerId, out var safeChildId, out _)) continue;

              // Try schema registry first; fall back to EF for latest Published version.
              IReadOnlyList<string> grandchildIds;
              // Note: we don't have a specific version here, so always use EF for
              // the latest Published version (same approach as GetRecordHandler).
              var rootJson = await db.ComponentSchemaVersions
                  .AsNoTracking()
                  .Where(v => v.DesignerId == safeChildId!.Value && v.Status == "Published")
                  .OrderByDescending(v => v.Version)
                  .Select(v => v.RootElement)
                  .FirstOrDefaultAsync(ct)
                  .ConfigureAwait(false);

              if (rootJson is null) continue;

              var (_, gcIds) = RootElementParser.ParseFull(rootJson);
              grandchildIds = gcIds;

              graph[safeChildId!.Value] = new NodeInfo(safeChildId!, grandchildIds);

              foreach (var gcId in grandchildIds)
              {
                  if (!visited.Contains(gcId))
                      queue.Enqueue(gcId);
              }
          }
      }

      // Phase 2 — recursive cascade executor. Runs within the caller-supplied
      // NpgsqlTransaction. Soft-deletes all non-deleted children of parentId in
      // childTableName (one UPDATE per child table level), then recurses into
      // grandchildren using the pre-loaded graph. The SELECT of child row IDs
      // runs BEFORE the UPDATE so we have the IDs needed for recursion; both
      // operations run on the same connection within the same transaction.
      // The visited set is shared across the entire walk to prevent re-visiting
      // a table (additional defence for unexpected graph shapes).
      internal static async Task ExecuteAsync(
          string parentDesignerId,
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
          ArgumentNullException.ThrowIfNull(graph);
          ArgumentNullException.ThrowIfNull(conn);
          ArgumentNullException.ThrowIfNull(tx);
          ArgumentNullException.ThrowIfNull(visited);

          var fkColumnName = DynamicQueryBuilder.BuildFkColumnName(parentDesignerId);

          // Find each direct child designer in the graph.
          // graph was built from the parent's ChildRepeaterDesignerIds so only
          // those keys are relevant at this level.
          foreach (var (childDesignerId, nodeInfo) in graph)
          {
              // Only process direct children of this parent (graph is flat BFS result;
              // the recursion depth is controlled by parentDesignerId's ChildIds).
              // Use graph lookup by childDesignerId to find the node.
              // Note: at the top level, we iterate all direct children of the
              // parent passed in; at deeper levels we recursively call per child.
              _ = childDesignerId; // loop variable — see full implementation note below
              _ = nodeInfo;
          }
          // NOTE TO DEV AGENT: The above is a skeleton. The real implementation
          // should NOT iterate the whole flat graph dictionary at every level.
          // Instead, each level only processes the node's own ChildIds.
          // See the correct recursive pattern in Dev Notes §3 below.
      }
  }
  ```

  **IMPORTANT:** The skeleton above is intentionally simplified for the template. See **Dev Notes §3** below for the correct recursive pattern that the dev agent must implement. Do NOT implement `ExecuteAsync` as a flat iteration of all graph entries.

- [x] **Task 3 — Add `DeleteRecordHandler` + route registration to `DynamicDataEndpoints.cs`** (AC: 1–9)
  - [x] Modify `src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs`

  **3a — Add `Problems.RecordAlreadyDeleted()` to the `private static class Problems` block** (after `RecordDeleted()`):
  ```csharp
  // Story 6.5 — DELETE attempted against an already soft-deleted record.
  // Distinct from Story 6.4's RECORD_DELETED (422 on PUT) because the operation
  // context differs: a PUT against a deleted record is conceptually blocked by
  // the deletion state, whereas a DELETE against an already-deleted record is
  // a client error — the record is already in the desired state.
  internal static IResult RecordAlreadyDeleted() =>
      Results.Problem(
          title: "Record is already soft-deleted",
          statusCode: StatusCodes.Status422UnprocessableEntity,
          extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
          {
              ["code"] = "RECORD_ALREADY_DELETED",
              ["messageKey"] = "errors.recordAlreadyDeleted",
          });
  ```

  **3b — Register the DELETE route** (add after the PUT registration inside `MapDynamicDataEndpoints`):
  ```csharp
  // Story 6.5 — DELETE /api/data/{designerId}/{id}. Rate limit overrides group
  // default: "data-write" (60 req/min) vs group "data-read" (300 req/min).
  group.MapDelete("/{id:guid}", DeleteRecordHandler)
       .WithSummary("Soft-delete a record in a provisioned dynamic table.")
       .Produces<DynamicRecord>(StatusCodes.Status200OK)
       .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
       .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
       .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)
       .RequirePermission("delete")
       .RequireRateLimiting("data-write");
  ```

  **3c — Implement `DeleteRecordHandler`**:
  ```csharp
  // Story 6.5 — DELETE /api/data/{designerId}/{id}. Mirrors the prior handler
  // headers (SafeIdentifier → EF binding → registry). Then:
  //   1. Pre-loads child schema graph via SoftDeleteCascade.BuildSchemaGraphAsync
  //      (EF, before connection open) when ChildRepeaterDesignerIds is non-empty.
  //   2. Opens Dapper connection, SELECTs the parent row (existence + is_deleted check).
  //   3. Executes a single NpgsqlTransaction only when there are children:
  //      - BuildSoftDeleteByIdQuery (parent) + SoftDeleteCascade.ExecuteAsync (children)
  //      inside one transaction; commit; rollback on exception.
  //   4. For no-children case: single UPDATE, no explicit transaction needed.
  //   5. Appends mutation_audit_log row via EF (Decision 1.6 separate transaction).
  //   6. Returns 200 with the updated parent record (overlay pattern — no re-SELECT).
  internal static async Task<IResult> DeleteRecordHandler(
      string designerId,
      Guid id,
      HttpContext httpContext,
      FormForgeDbContext db,
      ISchemaRegistry schemaRegistry,
      DbConnectionFactory connectionFactory,
      CancellationToken ct)
  {
      ArgumentNullException.ThrowIfNull(httpContext);
      ArgumentNullException.ThrowIfNull(db);
      ArgumentNullException.ThrowIfNull(schemaRegistry);
      ArgumentNullException.ThrowIfNull(connectionFactory);

      if (!SafeIdentifier.TryCreate(designerId, out var safeId, out _))
          return Problems.ValidationFailed("Invalid designer identifier.");

      // EF binding lookup (identical pattern to all prior handlers)
      var boundVersion = await db.Menus
          .AsNoTracking()
          .Where(m => m.DesignerId == safeId!.Value
                   && m.ProvisioningStatus == "Success"
                   && m.BoundVersion != null)
          .OrderByDescending(m => m.BoundVersion)
          .Select(m => (int?)m.BoundVersion)
          .FirstOrDefaultAsync(ct)
          .ConfigureAwait(false);

      if (boundVersion is null)
          return Problems.TableNotProvisioned();

      // Schema registry with cache-miss repopulation (identical pattern)
      var entry = schemaRegistry.TryGet(safeId!.Value, boundVersion.Value);
      if (entry is null)
      {
          var rootElementJson = await db.ComponentSchemaVersions
              .AsNoTracking()
              .Where(v => v.DesignerId == safeId.Value && v.Version == boundVersion.Value)
              .Select(v => v.RootElement)
              .FirstOrDefaultAsync(ct)
              .ConfigureAwait(false);
          var (columns, childIds) = RootElementParser.ParseFull(rootElementJson);
          entry = new SchemaRegistryEntry(
              safeId.Value, boundVersion.Value, columns, childIds, DateTimeOffset.UtcNow);
          schemaRegistry.Populate(entry);
      }

      // Phase 1 — load cascade schema graph via EF before opening the Dapper
      // connection (mirrors GetRecordHandler's EF-before-Dapper pattern).
      var cascadeGraph = new Dictionary<string, SoftDeleteCascade.NodeInfo>(StringComparer.Ordinal);
      if (entry.ChildRepeaterDesignerIds.Count > 0)
      {
          await SoftDeleteCascade.BuildSchemaGraphAsync(
              entry.ChildRepeaterDesignerIds, db, schemaRegistry, cascadeGraph, ct)
              .ConfigureAwait(false);
      }

      var actorId = httpContext.User.FindFirst("userId")?.Value is { } userIdStr
          && Guid.TryParse(userIdStr, out var uid) ? uid : (Guid?)null;

      // Phase 2 — open Dapper connection; SELECT + soft-delete
      IResult? earlyResult = null;
      var existingRow = new Dictionary<string, object?>(StringComparer.Ordinal);
      DateTimeOffset updatedAt = default;
      Guid? cascadeEventId = null;

      var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
      try
      {
          // SELECT the parent record (existence + is_deleted check, AC-4 / AC-7)
          var (selectSql, selectParams) = DynamicQueryBuilder.BuildGetByIdQuery(
              safeId, entry.Columns, id);
          var rawRows = await conn.QueryAsync(new CommandDefinition(
              selectSql, selectParams, commandTimeout: 5, cancellationToken: ct))
              .ConfigureAwait(false);

          var rows = rawRows
              .Cast<IDictionary<string, object>>()
              .Select(r => r.ToDictionary(
                  kvp => kvp.Key, kvp => (object?)kvp.Value, StringComparer.Ordinal))
              .ToList();

          if (rows.Count == 0)
          {
              earlyResult = Problems.RecordNotFound();
          }
          else
          {
              existingRow = rows[0];
              if (existingRow.TryGetValue("is_deleted", out var isDeletedVal) && isDeletedVal is true)
              {
                  earlyResult = Problems.RecordAlreadyDeleted();
              }
              else
              {
                  // Determine if cascade is needed (Decision 1.3)
                  bool hasCascade = cascadeGraph.Count > 0;
                  cascadeEventId = hasCascade ? Guid.NewGuid() : (Guid?)null;

                  var (updateSql, updateParams, ts) = DynamicQueryBuilder.BuildSoftDeleteByIdQuery(
                      safeId, id, actorId, cascadeEventId);
                  updatedAt = ts;

                  if (hasCascade)
                  {
                      // Cascade: all UPDATEs within one NpgsqlTransaction (AC-2)
                      var npgsqlConn = (NpgsqlConnection)conn;
                      await using var tx = await npgsqlConn.BeginTransactionAsync(ct)
                          .ConfigureAwait(false);
                      try
                      {
                          var rowsAffected = await conn.ExecuteAsync(new CommandDefinition(
                              updateSql, updateParams, transaction: tx,
                              commandTimeout: 5, cancellationToken: ct))
                              .ConfigureAwait(false);

                          if (rowsAffected == 0)
                          {
                              // Concurrent double-delete raced between SELECT and UPDATE.
                              await tx.RollbackAsync(CancellationToken.None)
                                  .ConfigureAwait(false);
                              earlyResult = Problems.RecordAlreadyDeleted();
                          }
                          else
                          {
                              var visitedCascade = new HashSet<string>(StringComparer.Ordinal)
                                  { safeId!.Value };
                              await SoftDeleteCascade.ExecuteAsync(
                                  safeId!.Value, cascadeGraph, id,
                                  cascadeEventId!.Value, updatedAt, actorId,
                                  npgsqlConn, tx, visitedCascade, ct)
                                  .ConfigureAwait(false);
                              await tx.CommitAsync(ct).ConfigureAwait(false);
                          }
                      }
                      catch
                      {
                          await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                          throw;
                      }
                  }
                  else
                  {
                      // No children: simple UPDATE, no explicit transaction
                      var rowsAffected = await conn.ExecuteAsync(new CommandDefinition(
                          updateSql, updateParams, commandTimeout: 5, cancellationToken: ct))
                          .ConfigureAwait(false);

                      if (rowsAffected == 0)
                      {
                          // Concurrent double-delete raced between SELECT and UPDATE.
                          earlyResult = Problems.RecordAlreadyDeleted();
                      }
                  }

                  if (earlyResult is null)
                  {
                      // Overlay updated system columns onto the SELECT result for response.
                      existingRow["is_deleted"]       = true;
                      existingRow["updated_at"]       = updatedAt;
                      existingRow["updated_by"]       = actorId;
                      existingRow["cascade_event_id"] = (object?)cascadeEventId;
                  }
              }
          }
      }
      finally
      {
          await conn.DisposeAsync().ConfigureAwait(false);
      }

      if (earlyResult is not null) return earlyResult;

      // AC-3 — audit row via EF (separate transaction per Decision 1.6)
      var correlationId = httpContext.GetCorrelationId();
      db.MutationAuditLog.Add(new Domain.Entities.MutationAuditLogEntry
      {
          DesignerId    = safeId!.Value,
          RecordId      = id,
          Operation     = "SOFT_DELETE",
          ActorId       = actorId,
          Timestamp     = updatedAt,
          NewValues     = null,       // AC-3: no field-value diff for soft-delete
          PreviousValues = null,
          CorrelationId = correlationId,
      });
      await db.SaveChangesAsync(ct).ConfigureAwait(false);

      return Results.Ok(new DynamicRecord(existingRow));
  }
  ```

- [x] **Task 4 — Unit tests for the three new query builder methods** (AC: 1, 2, 7, 9)
  - [x] Modify `src/FormForge.Api.Tests/Features/DynamicCrud/DynamicQueryBuilderTests.cs`
  - Add to the existing class (after the `BuildUpdateQuery` tests):

  Tests to add (5 unit tests):
  1. `BuildSoftDeleteByIdQuery_NoCascadeEventId_SetsCascadeEventIdToNull` — `cascadeEventId: null` → SQL contains `"cascade_event_id" = NULL` literal; parameter list does NOT contain `p_cascade_event_id`
  2. `BuildSoftDeleteByIdQuery_WithCascadeEventId_IncludesParam` — `cascadeEventId: Guid.NewGuid()` → SQL contains `"cascade_event_id" = @p_cascade_event_id`; param value matches input UUID
  3. `BuildSoftDeleteByIdQuery_WhereClauseIncludesIsDeletedFalse` — SQL contains `AND "is_deleted" = false`; WHERE filters by `@p_id`
  4. `BuildSelectChildIdsByFkQuery_BuiltCorrectly` — SQL selects `"id"` from child table WHERE `"{fkColumnName}" = @p_parent_id AND "is_deleted" = false`
  5. `BuildCascadeChildSoftDeleteQuery_SetsAllSystemColumns` — SQL sets `is_deleted = true`, `updated_at`, `updated_by`, `cascade_event_id` WHERE fk and `is_deleted = false`; all params present

  Estimated: +5 unit tests → running total ~480

- [x] **Task 5 — Integration tests: `SoftDeleteIntegrationTests.cs`** (AC: 1–9)
  - [x] Create `src/FormForge.Api.Tests/Features/DynamicCrud/SoftDeleteIntegrationTests.cs`
  - Class signature: `[Collection("DynamicCrudTests")] public sealed class SoftDeleteIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime`
  - `InitializeAsync` / `DisposeAsync` **identical** to `UpdateRecordIntegrationTests` (same TRUNCATE statement, same dynamic-table DROP loop, same `ReseedSystemRolesAsync` + `SeedTestUsersAsync`)
  - Copy all helper methods from `UpdateRecordIntegrationTests` verbatim (LoginAsync, PostRecordAsync, PutRecordAsync, SetupProvisionedDesignerWithTitleAsync, CreateRecordAndGetIdAsync, GetUserIdFromToken, etc.)
  - Add new helper `DeleteRecordAsync(string token, string designerId, Guid id)`:
    ```csharp
    private async Task<HttpResponseMessage> DeleteRecordAsync(string token, string designerId, Guid id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/data/{Uri.EscapeDataString(designerId)}/{id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }
    ```

  **Test cases (9 integration tests):**

  1. `SoftDelete_ValidRecord_Returns200WithIsDeletedTrue` — AC-1: provision table with `title` column, create record, DELETE → 200; verify `isDeleted: true`, `updatedAt > createdAt`, `updatedBy` = actor UUID, `cascadeEventId` JSON is null (not absent), camelCase system column keys

  2. `SoftDelete_RecordNotFound_Returns404` — AC-4: DELETE with non-existent UUID → 404 `code: "NOT_FOUND"`

  3. `SoftDelete_UnprovisionedDesigner_Returns404TableNotProvisioned` — AC-5: unknown designerId → 404 `TABLE_NOT_PROVISIONED`

  4. `SoftDelete_NoCanDelete_Returns403` — AC-6: viewer user → 403 `code: "FORBIDDEN"`

  5. `SoftDelete_AlreadyDeletedRecord_Returns422RecordAlreadyDeleted` — AC-7: set `is_deleted = true` directly via Npgsql, then DELETE → 422 `code: "RECORD_ALREADY_DELETED"`

  6. `SoftDelete_AuditLogRow_AppendedWithSoftDeleteOperation` — AC-3: create record, DELETE, query `mutation_audit_log` directly via Npgsql → verify `operation = 'SOFT_DELETE'`, `new_values IS NULL`, `previous_values IS NULL`, `actor_id` matches JWT actor, `correlation_id` non-empty, `timestamp` within request window, `record_id` matches deleted record

  7. `SoftDelete_RecordDoesNotExistInDbAfterSoftDelete` — AC-1 extended: soft-delete then GET the same record → response still returns 200 with `isDeleted: true` (record is preserved, not hard-deleted); verify via direct SELECT with Npgsql that `is_deleted = true`

  8. `SoftDelete_DeletedRecordIsReturnedByList_WithIsDeletedTrue` — AC-1 extended: soft-delete a record, then GET `/api/data/{designerId}?filter[isDeleted]=true` → the deleted record appears in the list with `isDeleted: true`

  9. `SoftDelete_SystemColumnsInResponse_AreCamelCase` — AC-1 AR-46: verify `id`, `createdAt`, `updatedAt`, `createdBy`, `updatedBy`, `isDeleted`, `cascadeEventId` are present as camelCase; `is_deleted`, `created_by`, `updated_by`, `cascade_event_id` are absent

  Estimated: +9 integration tests → running total ~489

- [x] **Task 6 — Frontend API client and hook** (foundational for Stories 6.9/6.10)
  - [x] Create `web/src/features/data-entry/deleteRecordApi.ts`:
    ```ts
    import { httpClient } from '../auth/httpClient'
    import type { DynamicRecord } from './recordListApi'

    // Story 6.5 — typed client for DELETE /api/data/{designerId}/{id}.
    // Returns the updated record (isDeleted: true) per AR-46 Option C on 200.
    export function deleteRecord(
      designerId: string,
      id: string,
    ): Promise<DynamicRecord> {
      return httpClient.delete<DynamicRecord>(
        `/api/data/${encodeURIComponent(designerId)}/${encodeURIComponent(id)}`,
      )
    }
    ```
  - [x] Create `web/src/features/data-entry/useDeleteRecord.ts`:
    ```ts
    import { useMutation, useQueryClient } from '@tanstack/react-query'
    import { deleteRecord } from './deleteRecordApi'
    import type { DynamicRecord } from './recordListApi'

    // Story 6.5 — TanStack mutation hook for DELETE /api/data/{designerId}/{id}.
    // AR-48 + AR-49: pessimistic mutation (await server confirmation). Invalidates
    // the list root key and the single-record key on success so list / get views
    // re-fetch. AR-49: soft-delete row removal uses optimistic invalidation — the
    // record is not removed from the list immediately but disappears after re-fetch.
    export function useDeleteRecord(designerId: string) {
      const queryClient = useQueryClient()

      return useMutation({
        mutationFn: (id: string) => deleteRecord(designerId, id),
        onSuccess: (_deleted: DynamicRecord, id) => {
          queryClient.invalidateQueries({ queryKey: ['data', designerId] })
          queryClient.invalidateQueries({ queryKey: ['data', designerId, 'record', id] })
        },
      })
    }
    ```

---

## Dev Notes

### §1 — Transaction strategy: cascade vs individual

**Individual soft-delete (no Repeater children in schema registry):**
- No NpgsqlTransaction needed — single Dapper `ExecuteAsync` call
- `cascade_event_id = NULL` in the UPDATE (SQL literal, not a parameter)
- Pattern: same single-finally try/finally as UpdateRecordHandler

**Cascade soft-delete (ChildRepeaterDesignerIds.Count > 0):**
- Open `NpgsqlTransaction` via `npgsqlConn.BeginTransactionAsync(ct)`
- Pass the transaction via `CommandDefinition.Transaction` to every Dapper call inside the transaction
- All descendant levels in the same transaction — atomic commit
- Rollback on any exception: `await tx.RollbackAsync(CancellationToken.None)` (NOT the cancellation token — same rationale as DdlEmitter: rollback must complete even if the request was cancelled)
- `cascade_event_id` is the same UUID for parent + all descendants

**Cast `IDbConnection` to `NpgsqlConnection` for the transaction:** `DbConnectionFactory.CreateOpenConnectionAsync` returns an `IDbConnection`; the concrete runtime type is `NpgsqlConnection`. Cast with `(NpgsqlConnection)conn` before calling `BeginTransactionAsync`. This is safe because the factory is hard-coded to Npgsql.

### §2 — SoftDeleteCascade.ExecuteAsync — correct recursive pattern

The dev agent MUST NOT implement `ExecuteAsync` as a flat iteration of the entire `cascadeGraph` dictionary. The correct pattern is depth-first, driven by each node's `ChildIds`:

```csharp
internal static async Task ExecuteAsync(
    string parentDesignerId,           // the table whose children we are cascading
    Dictionary<string, NodeInfo> graph,// pre-loaded schema graph (all descendants)
    Guid parentId,                     // the specific parent row ID
    Guid cascadeEventId,
    DateTimeOffset updatedAt,
    Guid? actorId,
    NpgsqlConnection conn,
    NpgsqlTransaction tx,
    HashSet<string> visited,           // defence against unexpected graph cycles
    CancellationToken ct)
{
    // Look up the NodeInfo for this parent. It will be present iff parentDesignerId
    // is a child in the graph. At the top-level call from the handler, the parent
    // is NOT in the graph (the graph only contains descendants); iterate ChildIds
    // from the entry.ChildRepeaterDesignerIds (passed from handler) instead.
    // SEE NOTE: The handler passes entry.ChildRepeaterDesignerIds to
    // BuildSchemaGraphAsync. ExecuteAsync receives the parentDesignerId to build
    // the FK column name, and the graph to look up each child's NodeInfo.
    //
    // For each direct child of parentDesignerId:
    foreach (var childDesignerId in GetDirectChildIds(parentDesignerId, graph))
    {
        if (!visited.Add(childDesignerId)) continue;

        if (!graph.TryGetValue(childDesignerId, out var childNode)) continue;

        var fkColumnName = DynamicQueryBuilder.BuildFkColumnName(parentDesignerId);

        // SELECT child row IDs BEFORE the UPDATE (for recursion to grandchildren)
        var (selectSql, selectParams) = DynamicQueryBuilder.BuildSelectChildIdsByFkQuery(
            childNode.TableName, fkColumnName, parentId);
        var childIds = (await conn.QueryAsync<Guid>(new CommandDefinition(
            selectSql, selectParams, transaction: tx,
            commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false))
            .ToList();

        // UPDATE all non-deleted children of parentId in one statement
        var (updateSql, updateParams) = DynamicQueryBuilder.BuildCascadeChildSoftDeleteQuery(
            childNode.TableName, fkColumnName, parentId, actorId, cascadeEventId, updatedAt);
        await conn.ExecuteAsync(new CommandDefinition(
            updateSql, updateParams, transaction: tx,
            commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

        // Recurse to grandchildren for each child row
        foreach (var childId in childIds)
        {
            await ExecuteAsync(childDesignerId, graph, childId,
                cascadeEventId, updatedAt, actorId,
                conn, tx, visited, ct).ConfigureAwait(false);
        }
    }
}
```

**`GetDirectChildIds` helper** (private static in SoftDeleteCascade):
The graph is built from all descendants but doesn't track the parent→child edges explicitly (BFS flattens the graph). The parent's direct child designer IDs come from the **parent node's `ChildIds`**. For the top-level call, pass in `entry.ChildRepeaterDesignerIds`. For recursive calls, pass in the child node's `ChildIds` (from `childNode.ChildIds`). Therefore, instead of a separate `GetDirectChildIds` helper, restructure `ExecuteAsync` to accept `IReadOnlyList<string> directChildDesignerIds` explicitly:

```csharp
internal static async Task ExecuteAsync(
    string parentDesignerId,
    IReadOnlyList<string> directChildDesignerIds,   // the immediate children to process
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
    foreach (var childDesignerId in directChildDesignerIds)
    {
        if (!visited.Add(childDesignerId)) continue;
        if (!graph.TryGetValue(childDesignerId, out var childNode)) continue;

        var fkColumnName = DynamicQueryBuilder.BuildFkColumnName(parentDesignerId);

        var (selectSql, selectParams) = DynamicQueryBuilder.BuildSelectChildIdsByFkQuery(
            childNode.TableName, fkColumnName, parentId);
        var childRowIds = (await conn.QueryAsync<Guid>(new CommandDefinition(
            selectSql, selectParams, transaction: tx,
            commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false))
            .ToList();

        var (updateSql, updateParams) = DynamicQueryBuilder.BuildCascadeChildSoftDeleteQuery(
            childNode.TableName, fkColumnName, parentId, actorId, cascadeEventId, updatedAt);
        await conn.ExecuteAsync(new CommandDefinition(
            updateSql, updateParams, transaction: tx,
            commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

        // Recurse: each child row → its own grandchildren
        foreach (var childRowId in childRowIds)
        {
            await ExecuteAsync(
                childDesignerId, childNode.ChildIds, graph,
                childRowId, cascadeEventId, updatedAt, actorId,
                conn, tx, visited, ct).ConfigureAwait(false);
        }
    }
}
```

And the handler calls it as:
```csharp
await SoftDeleteCascade.ExecuteAsync(
    safeId!.Value,
    entry.ChildRepeaterDesignerIds,  // direct children of parent
    cascadeGraph,
    id,                              // parent row id
    cascadeEventId!.Value,
    updatedAt,
    actorId,
    npgsqlConn,
    tx,
    visitedCascade,
    ct).ConfigureAwait(false);
```

### §3 — Response building (overlay pattern, no re-SELECT)

The `existingRow` dictionary comes from `BuildGetByIdQuery`'s Dapper result (PG-native .NET types: UUID → `Guid`, TIMESTAMPTZ → `DateTimeOffset`, BOOLEAN → `bool`, TEXT → `string`). After the soft-delete:

```csharp
existingRow["is_deleted"]       = true;
existingRow["updated_at"]       = updatedAt;
existingRow["updated_by"]       = actorId;
existingRow["cascade_event_id"] = (object?)cascadeEventId;  // null or Guid
```

`DynamicRecordJsonConverter` (Story 6.1) translates PG names → camelCase automatically:
- `is_deleted` → `isDeleted: true`
- `updated_at` → `updatedAt: <timestamp>`
- `updated_by` → `updatedBy: <uuid>`
- `cascade_event_id` → `cascadeEventId: null` or `cascadeEventId: "<uuid>"`

**Critical**: `cascadeEventId` must serialize as `null` (JSON null), NOT as absent. This is the existing converter's behavior for nullable `Guid?` values — `DynamicRecordJsonConverter` will serialize `(object?)null` as `null`. Verify in the AC-1 integration test.

### §4 — `cascade_event_id = NULL` serialization in AC-1 response test

In the integration test `SoftDelete_ValidRecord_Returns200WithIsDeletedTrue`, verify that `cascadeEventId` is present as a null JSON value:

```csharp
Assert.True(root.TryGetProperty("cascadeEventId", out var cascadeEventIdProp));
Assert.Equal(JsonValueKind.Null, cascadeEventIdProp.ValueKind);
```

Do NOT just `Assert.True(root.TryGetProperty("cascadeEventId", out _))` without checking that it's null — the property must exist AND be null (not a UUID) for an individual (no-children) delete.

### §5 — Permission requirement: `"delete"` maps to `canDelete`

`.RequirePermission("delete")` integrates with the existing permission infrastructure (Story 2.6). The `EffectivePermissions.PerResource[designerId].CanDelete` flag is what gets checked. The endpoint needs **no** handler-level RBAC code — the policy filter handles it. The viewer test uses a seeded viewer user whose role has `canDelete: false` for the test designerId.

### §6 — No `SoftDeleteCascade` namespace injection needed; it is a static class

`SoftDeleteCascade` is a static class (like `DynamicQueryBuilder`), not a DI service. It is called directly from `DeleteRecordHandler` without injection. It does need `FormForgeDbContext` and `ISchemaRegistry` passed in from the handler's DI-injected parameters.

### §7 — `visited` HashSet in the cascade walk

The `visited` set is seeded with `safeId!.Value` (the parent designer ID) before calling `ExecuteAsync`. This prevents the cascade from looping back to the parent designer in the unlikely event the schema graph has a cycle (defence in depth — the `CycleDetector` at bind time is the primary guard per Decision 1.3). Pass `StringComparer.Ordinal` to the `HashSet` constructor (consistent with all other string sets in this codebase).

### §8 — `NpgsqlConnection` cast safety

`connectionFactory.CreateOpenConnectionAsync` returns `IDbConnection`. The concrete type is always `NpgsqlConnection` (see `DbConnectionFactory.cs`). The cast `(NpgsqlConnection)conn` is required to call `BeginTransactionAsync`. This is acceptable because the cascade logic is already Npgsql-specific (parameterized SQL against PG). Add a comment to explain the cast is safe.

### §9 — `CommandDefinition.Transaction` parameter for Dapper within NpgsqlTransaction

Every Dapper call inside the transaction block **must** pass `transaction: tx` in the `CommandDefinition`. Without this, Dapper creates an implicit transaction (or uses no transaction), breaking the atomicity guarantee. All existing `CommandDefinition` calls in `CreateRecordHandler` / `UpdateRecordHandler` omit the transaction parameter because they don't use one — do NOT copy them for the cascade path.

### §10 — Audit log for cascade: only one row per parent DELETE

Per AC-3, only one `mutation_audit_log` row is written (for the parent). Child-level audit rows (one per cascade-deleted child row) are explicitly deferred. The audit viewer in Story 6.8 will correlate child-row mutations via `cascade_event_id`. Do NOT write per-child audit rows in Story 6.5.

---

### File locations — new files

| New file | Path |
|---|---|
| `SoftDeleteCascade.cs` | `src/FormForge.Api/Features/DynamicCrud/SoftDeleteCascade.cs` |
| `SoftDeleteIntegrationTests.cs` | `src/FormForge.Api.Tests/Features/DynamicCrud/SoftDeleteIntegrationTests.cs` |
| `deleteRecordApi.ts` | `web/src/features/data-entry/deleteRecordApi.ts` |
| `useDeleteRecord.ts` | `web/src/features/data-entry/useDeleteRecord.ts` |

### File locations — modified files

| Modified file | Change |
|---|---|
| `DynamicDataEndpoints.cs` | Add `Problems.RecordAlreadyDeleted()`, `MapDelete("/{id:guid}", DeleteRecordHandler)`, handler; add `using Npgsql;` import |
| `DynamicQueryBuilder.cs` | Add `BuildSoftDeleteByIdQuery`, `BuildSelectChildIdsByFkQuery`, `BuildCascadeChildSoftDeleteQuery` |
| `DynamicQueryBuilderTests.cs` | Add 5 unit tests for the three new builder methods |

No changes to: `MutationAuditLogEntry.cs` (already supports `"SOFT_DELETE"` per its xmldoc), `FormForgeDbContext.cs`, `Program.cs`, `DynamicRecord.cs`, `DynamicRecordJsonConverter.cs`, `DynamicPayloadValidator.cs`. No new EF migration required.

**Import to add in `DynamicDataEndpoints.cs`:** `using Npgsql;` — required for the `NpgsqlConnection` cast and `NpgsqlTransaction`.

---

### Deferred items (record for code review)

1. **Child-level audit rows omitted** — each cascaded child soft-delete has no individual `mutation_audit_log` row. Story 6.8's audit view correlates children via `cascade_event_id`. Owner: Story 6.8 or a future hardening pass.
2. **CASCADE SELECT + UPDATE SELECT-before-UPDATE race window** — same pattern as Story 6.4. Between `BuildSelectChildIdsByFkQuery` and `BuildCascadeChildSoftDeleteQuery`, a concurrent create could add new children; those children would not be soft-deleted (the UPDATE covers only rows that were non-deleted at the SELECT time). The transaction isolation level is `READ COMMITTED` (Npgsql default) — not `REPEATABLE READ`. Owner: future hardening pass, coordinate with Story 6.6 restore.
3. **`cascade_event_id` not written to child SELECT result** — `BuildSelectChildIdsByFkQuery` only returns `id`, not the full row. The handler cannot verify cascade_event_id was written without a re-SELECT. The integration test AC-2 will verify via direct Npgsql query. Owner: acceptable for v1.
4. **No AC-8 rate-limit integration test (429 / Retry-After)** — same gap as Stories 6.3 and 6.4. Owner: add when a virtualised clock is available.
5. **No AC-9 commandTimeout integration test** — same gap as Stories 6.3 and 6.4. Owner: add with a Dapper `CommandDefinition` test abstraction.
6. **Audit INSERT not transactionally atomic with Dapper UPDATEs** — same pattern as Stories 6.3/6.4. If `db.SaveChangesAsync` throws after the Dapper commit, the record(s) are soft-deleted without an audit row. Owner: outbox pattern or shared transaction (architectural change).
7. **`SoftDeleteCascade.BuildSchemaGraphAsync` ignores schema registry** — the method always loads from EF (latest Published version) even if the schema registry has a cached entry. This adds an EF round-trip. Owner: check registry first, fall back to EF only on miss (same pattern as the handler's `entry` loading). Acceptable for v1.

---

### References

- [Source: `src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs`] — `UpdateRecordHandler` (lines 438–620) — mirror SafeIdentifier → EF binding → registry → actorId extraction; single-finally try/finally for connection disposal; `Problems` inner class for new `RecordAlreadyDeleted()`; route registration pattern after `MapPut`
- [Source: `src/FormForge.Api/Features/DynamicCrud/DynamicQueryBuilder.cs`] — `BuildUpdateQuery` (lines 307–358) — mirror the WHERE `AND "is_deleted" = false` pattern; `BuildGetByIdQuery` (lines 203–219) — used inside the handler for the pre-DELETE SELECT; `BuildFkColumnName` (lines 363–368) — used by `SoftDeleteCascade.ExecuteAsync` for each child level
- [Source: `src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs:GetRecordHandler`] — lines 230–246 — child schema loading via EF before opening Dapper connection; mirror in `SoftDeleteCascade.BuildSchemaGraphAsync`
- [Source: `src/FormForge.Api/Features/Provisioning/DdlEmitter.cs`] — NpgsqlTransaction pattern with `BeginTransactionAsync`, `CommitAsync`, `RollbackAsync(CancellationToken.None)` — mirror for the cascade transaction
- [Source: `src/FormForge.Api/Features/Provisioning/CycleDetector.cs`] — DFS with `inStack`/`visited` sets — the `visited` HashSet in `SoftDeleteCascade.ExecuteAsync` is a simpler variant of this pattern (no back-edge detection needed since cycles were already prevented at bind time)
- [Source: `src/FormForge.Api.Tests/Features/DynamicCrud/UpdateRecordIntegrationTests.cs`] — copy all helpers verbatim; TRUNCATE statement (line 50); `[Collection("DynamicCrudTests")]` attribute; dynamic-table DROP loop; `ReseedSystemRolesAsync` + `SeedTestUsersAsync`; `GetUserIdFromToken` helper
- [Architecture: Decision 1.3] — full transitive cascade; `cascade_event_id` UUID NULL semantics; individual vs cascade distinction; bounded by cycle detection
- [Architecture: AR-46 Option C] — `DynamicRecordJsonConverter` handles `cascade_event_id` → `cascadeEventId` automatically; `is_deleted` → `isDeleted: true`
- [Architecture: Decision 1.6] — `commandTimeout: 5` on all Dapper calls; EF + Dapper separated transactions (audit via EF AFTER cascade transaction commits)
- [Architecture: Decision 2.2] — `EffectivePermissions.CanDelete` checked by `.RequirePermission("delete")`

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-7 (Opus 4.7, 1M context)

### Completion Notes List

- **Implementation summary**: Added DELETE `/api/data/{designerId}/{id}` with optional Repeater-cascade. `DynamicQueryBuilder` gains three new builders: `BuildSoftDeleteByIdQuery` (parent UPDATE, cascade_event_id literal NULL or @param, WHERE-guarded against double-delete), `BuildSelectChildIdsByFkQuery` (per-level child id fetch for recursion), `BuildCascadeChildSoftDeleteQuery` (one-shot child UPDATE per level). `SoftDeleteCascade` is a new static class with two-phase design: `BuildSchemaGraphAsync` (BFS over Published child versions via EF, before connection open) and `ExecuteAsync` (depth-first recursive Dapper walker inside the caller-supplied NpgsqlTransaction). `DeleteRecordHandler` in `DynamicDataEndpoints` mirrors the existing handler header (SafeIdentifier → EF binding → registry cache-miss repopulation), pre-loads the cascade graph via EF, then SELECTs the parent on the Dapper connection. When the schema has no Repeater children the soft-delete is a single non-transactional UPDATE; when children exist the parent UPDATE plus `SoftDeleteCascade.ExecuteAsync` run inside one `BeginTransactionAsync` block, with explicit try/finally for `DisposeAsync().ConfigureAwait(false)` (matches `UserService` pattern; `await using` would fail CA2007 on the implicit DisposeAsync). `Problems.RecordAlreadyDeleted()` returns 422 with `code: "RECORD_ALREADY_DELETED"` and `messageKey: "errors.recordAlreadyDeleted"` — distinct from Story 6.4's `RECORD_DELETED` (which still applies to PUT against a soft-deleted record). Route registered as `MapDelete("/{id:guid}", DeleteRecordHandler)` with `.RequirePermission("delete")` and `.RequireRateLimiting("data-write")` (60 req/min). Mutation audit row written via EF after the Dapper transaction commits (Decision 1.6); `new_values` and `previous_values` are both NULL for soft-delete per AC-3.
- **Deviation from spec template — `visited` set semantics**: The story's §2 code template uses `if (!visited.Add(childDesignerId)) continue;` inside the recursion loop, which would prevent siblings of the same parent from having their grandchildren processed (after c_a's recursion adds grandchild designer G to visited, c_b's recursion skips G — leaving c_b's grandchildren orphaned). §7 of Dev Notes explicitly states `visited` is for "looping back to the parent designer" only. I implemented proper recursion-stack semantics: `Contains` check (not `Add`) on entry, then `Add` before descending into a child's grandchildren and `Remove` on backtrack via try/finally. This catches the C1→C2→C1 cycle case the spec aims at without over-restricting legitimate sibling traversal. The current 9 integration tests do not exercise multi-row cascade (Task 5 list has no cascade scenarios), so no test would have caught the spec's bug — but production with Repeater children + multiple parent rows would have lost data.
- **Test results**: 490/490 tests pass. +5 unit tests (BuildSoftDelete*, BuildSelectChildIds*, BuildCascadeChildSoftDelete*) and +9 integration tests (AC-1, AC-3, AC-4, AC-5, AC-6, AC-7 + AR-46 + GET-after-delete + filter-by-isDeleted=true). One integration test failed initially because I asserted on `items` instead of `data` (the PagedResult JSON property name); fixed by reading `PagedResult<T>.Data` directly.
- **Test fix during run**: `SoftDelete_DeletedRecordIsReturnedByList_WithIsDeletedTrue` initially read `listDoc.RootElement.GetProperty("items")` and threw `KeyNotFoundException`. Switched to `GetProperty("data")` (matches `PagedResult<T>` shape from Story 6.1 with web JSON defaults).
- **Build/CA fix**: First build attempt errored with CA2007 on `await using var tx = ...` because the implicit `DisposeAsync` drops the `ConfigureAwait(false)`. Refactored to explicit `var tx = ...; try { ... } finally { await tx.DisposeAsync().ConfigureAwait(false); }` matching the existing `UserService.AssignRolesAsync` pattern. Build is now warning-free.

### File List

**New:**
- `src/FormForge.Api/Features/DynamicCrud/SoftDeleteCascade.cs`
- `src/FormForge.Api.Tests/Features/DynamicCrud/SoftDeleteIntegrationTests.cs`
- `web/src/features/data-entry/deleteRecordApi.ts`
- `web/src/features/data-entry/useDeleteRecord.ts`

**Modified:**
- `src/FormForge.Api/Features/DynamicCrud/DynamicQueryBuilder.cs` — 3 new static methods
- `src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs` — `using Npgsql;`, `MapDelete` route, `Problems.RecordAlreadyDeleted()`, `DeleteRecordHandler`
- `src/FormForge.Api.Tests/Features/DynamicCrud/DynamicQueryBuilderTests.cs` — 5 new unit tests
- `_bmad-output/implementation-artifacts/sprint-status.yaml` — story 6-5 ready-for-dev → review

---

## Change Log

| Date | Change |
|---|---|
| 2026-05-26 | Story created — DELETE /api/data/{designerId}/{id} with cascade soft-delete for Repeater children, audit log, and frontend delete hook |
| 2026-05-26 | Implemented — 3 new query builders + SoftDeleteCascade walker + DeleteRecordHandler + 5 unit tests + 9 integration tests + frontend deleteRecord API/hook. 490/490 tests pass. Cascade `visited` set switched from spec template's `Add`-only to recursion-stack `Contains/Add/Remove` so sibling rows correctly process their grandchildren (see Completion Notes). |

---

### Review Findings

- [x] [Review][Patch] Silent partial cascade when child schemas have no Published version — add structured warning log when `entry.ChildRepeaterDesignerIds.Count > 0` but `cascadeGraph` is empty after `BuildSchemaGraphAsync`, so the skipped cascade is visible in structured logs. Current behavior (proceed without cascade) is preserved. [`DynamicDataEndpoints.cs:DeleteRecordHandler`]

- [x] [Review][Patch] No integration test for AC-2 cascade soft-delete — nine integration tests cover AC-1/3/4/5/6/7/AR-46 but AC-2 (cascade to Repeater children, single NpgsqlTransaction, same `cascadeEventId`, grandchild recursion, atomicity) is entirely untested. Need: provision parent schema with `ChildRepeaterDesignerIds`, create parent + child rows, DELETE parent, verify via direct Npgsql query that child rows have `is_deleted = true` with matching `cascade_event_id`. [`SoftDeleteIntegrationTests.cs`]

- [x] [Review][Patch] AC-1 happy-path test omits `createdBy` value assertion — `SoftDelete_ValidRecord_Returns200WithIsDeletedTrue` asserts `updatedBy` correctness but not `createdBy`; presence is covered only by the camelCase test which does not verify the value. [`SoftDeleteIntegrationTests.cs:92`]

- [x] [Review][Defer] `is_deleted is true` pattern — NULL `is_deleted` in DB bypasses already-deleted guard, `rowsAffected == 0` produces misleading 422 RECORD_ALREADY_DELETED [`DynamicDataEndpoints.cs:727`] — deferred, pre-existing (same pattern as UpdateRecordHandler; provisioned tables have NOT NULL DEFAULT false on is_deleted)

- [x] [Review][Defer] `BuildSchemaGraphAsync` uses latest Published version for child schemas, not BoundVersion — grandchild IDs diverge if child was re-published without re-provisioning [`SoftDeleteCascade.cs:55`] — deferred, pre-existing (Deferred item 7 in story; acceptable for v1)

- [x] [Review][Defer] `fkColumnName` raw string interpolated into SQL without per-method validation in `BuildSelectChildIdsByFkQuery` and `BuildCascadeChildSoftDeleteQuery` [`DynamicQueryBuilder.cs`] — deferred, pre-existing (consistent with `BuildGetChildrenQuery`; callers always derive fkColumnName from SafeIdentifier-validated designerId)

- [x] [Review][Defer] `visited` push/pop re-enables cycle path after sibling backtrack — cycle protection relies entirely on bind-time CycleDetector [`SoftDeleteCascade.cs:132`] — deferred, pre-existing (accepted per Dev Notes §7)

- [x] [Review][Defer] Audit `PreviousValues = null` — pre-delete field values not captured [`DynamicDataEndpoints.cs:830`] — deferred, intentional per AC-3 spec

- [x] [Review][Defer] Individual delete sets `cascade_event_id = NULL`, overwriting any prior cascade context in the row [`DynamicQueryBuilder.cs:BuildSoftDeleteByIdQuery`] — deferred, intentional behavior

- [x] [Review][Defer] SELECT-before-UPDATE race in cascade — concurrently inserted child rows between `BuildSelectChildIdsByFkQuery` and `BuildCascadeChildSoftDeleteQuery` miss grandchild recursion [`SoftDeleteCascade.cs`] — deferred, pre-existing (Deferred item 2 in story; READ COMMITTED accepted)

- [x] [Review][Defer] `createdBy`/`createdAt` not in post-delete overlay — correct only because `BuildGetByIdQuery` returns all system columns [`DynamicDataEndpoints.cs:804`] — deferred, pre-existing (consistent with UpdateRecordHandler)
