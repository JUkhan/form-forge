# Story 6.6: View and Restore Soft-Deleted Records

Status: done

## Story

As a Platform Admin,
I want to view soft-deleted records and restore them,
So that accidental deletions can be recovered.

## Acceptance Criteria

**AC-1 — Happy-path restore (individual or cascade)**
**Given** a soft-deleted record whose `is_deleted = true`
**When** I PUT `/api/data/{designerId}/{id}/restore`
**Then** the response is HTTP 200 with the restored parent record
**And** `isDeleted: false` in the response
**And** `cascadeEventId: null` in the response (cleared on restore per Decision 1.3)
**And** `updatedAt` is server-side (the restore timestamp)
**And** `updatedBy` equals the JWT actor UUID
**And** response uses camelCase system columns per AR-46 Option C

**AC-2 — Cascade restore of children with matching `cascade_event_id`**
**Given** a soft-deleted record that was cascade-deleted (parent has a non-null `cascade_event_id`)
**When** I PUT `/api/data/{designerId}/{id}/restore`
**Then** all descendant rows in child tables whose `cascade_event_id` equals the parent's cascade event are restored (`is_deleted = false`) within the **same NpgsqlTransaction** as the parent restore
**And** their `cascade_event_id` is set to NULL on restore
**And** the transaction commits atomically
**And** the response is HTTP 200 with the restored parent record

**AC-3 — Children with different or NULL `cascade_event_id` are NOT restored**
**Given** a soft-deleted parent record
**When** any child rows exist with `cascade_event_id = NULL` or a **different** cascade event UUID (individually soft-deleted before or after the parent cascade)
**Then** those rows are NOT restored — they retain `is_deleted = true`

**AC-4 — Mutation audit log row**
**Given** a successful restore (AC-1 or AC-2)
**When** the Dapper UPDATE(s) complete
**Then** a row is appended to `mutation_audit_log` with:
  - `operation: "RESTORE"`
  - `designer_id`: the parent designerId
  - `record_id`: the restored record's id
  - `actor_id`: the JWT actor UUID (null for unauthenticated)
  - `timestamp`: the same `updatedAt` used in the UPDATE
  - `correlation_id`: the request correlation ULID
  - `new_values: null`
  - `previous_values: null`

**AC-5 — Record not found → 404**
**Given** an `id` that does not exist in the provisioned table
**When** I PUT `/api/data/{designerId}/{id}/restore`
**Then** the response is HTTP 404 with `code: "NOT_FOUND"`

**AC-6 — Record is NOT soft-deleted → 422**
**Given** a record with `is_deleted = false` (already active)
**When** I PUT `/api/data/{designerId}/{id}/restore`
**Then** the response is HTTP 422 with `code: "RECORD_NOT_DELETED"` and `messageKey: "errors.recordNotDeleted"`

**AC-7 — Unprovisioned designer → 404**
**Given** a `designerId` for which no menu exists with `provisioningStatus = 'Success'`
**When** I PUT `/api/data/{designerId}/{id}/restore`
**Then** the response is HTTP 404 with `code: "TABLE_NOT_PROVISIONED"`

**AC-8 — Permission enforcement**
**Given** an authenticated user without `canUpdate` on this resource
**When** I PUT `/api/data/{designerId}/{id}/restore`
**Then** the response is HTTP 403 with `code: "FORBIDDEN"`

**AC-9 — Rate limiting**
**Given** the `"data-write"` rate-limiting policy
**When** a user exceeds 60 PUT `/api/data/*` requests per user per minute
**Then** the response is HTTP 429 with `Retry-After: 60`
**Note:** Applied per-endpoint via `.RequireRateLimiting("data-write")`. The policy is already registered.

**AC-10 — Query timeout**
**Given** the Dapper SELECT and UPDATE(s) queries
**When** each query runs
**Then** `commandTimeout` is set to 5 seconds on every `CommandDefinition` (Decision 1.6)

**AC-11 — "Show deleted" toggle in the admin record list view (frontend)**
**Given** a record list page that uses `useRecordList(designerId, params)`
**When** the user toggles "Show deleted" to **on**
**Then** the query adds `filter[isDeleted]=true` to the params (shows only deleted records)
**When** the toggle is **off**
**Then** `filter[isDeleted]=false` is applied (only active records)
**Note:** The backend already supports `filter[isDeleted]=true|false` from Story 6.1. This AC is purely a frontend filter-parameter toggle; no new backend work is required.

---

## Tasks / Subtasks

- [x] **Task 1 — Add restore query builders to `DynamicQueryBuilder.cs`** (AC: 1, 2, 3, 6, 10)
  - [x] Modify `src/FormForge.Api/Features/DynamicCrud/DynamicQueryBuilder.cs`
  - Add two new static methods after `BuildFkColumnName`:

  **Method A: `BuildRestoreByIdQuery`** — restores one row by primary key:
  ```csharp
  // Story 6.6 — parameterized UPDATE for PUT /api/data/{designerId}/{id}/restore.
  // Sets is_deleted=false, updated_at, updated_by; cascade_event_id cleared to NULL
  // unconditionally (individual restore clears unconditionally per Decision 1.3).
  // WHERE is_deleted=true so a concurrent restore returns 0 rowsAffected → caller
  // returns 422 RECORD_NOT_DELETED. Returns updatedAt for response + audit assembly.
  internal static (string Sql, DynamicParameters Parameters, DateTimeOffset UpdatedAt)
      BuildRestoreByIdQuery(
          SafeIdentifier tableName,
          Guid id,
          Guid? actorId)
  {
      ArgumentNullException.ThrowIfNull(tableName);

      var updatedAt = DateTimeOffset.UtcNow;

      var sql = $"UPDATE \"{tableName.Value}\" SET " +
                $"\"is_deleted\" = false, \"updated_at\" = @p_updated_at, " +
                $"\"updated_by\" = @p_updated_by, \"cascade_event_id\" = NULL " +
                $"WHERE \"id\" = @p_id AND \"is_deleted\" = true";

      var parameters = new DynamicParameters();
      parameters.Add("p_updated_at", updatedAt);
      parameters.Add("p_updated_by", actorId);
      parameters.Add("p_id",         id);

      return (sql, parameters, updatedAt);
  }
  ```

  **Method B: `BuildRestoreCascadeChildrenQuery`** — restores all descendant rows in one child table that share the given `cascade_event_id`:
  ```csharp
  // Story 6.6 — UPDATE all rows in a child table that were cascade-soft-deleted
  // as part of a specific cascade event. Matches by cascade_event_id (not by FK)
  // because all rows sharing the cascade event UUID are the correct set to restore,
  // regardless of parent_id — the UUID is unique per cascade event.
  // cascade_event_id is cleared to NULL on restore (Decision 1.3).
  // updatedAt is passed in so all restored rows share one server-generated timestamp.
  internal static (string Sql, DynamicParameters Parameters) BuildRestoreCascadeChildrenQuery(
      SafeIdentifier childTableName,
      Guid parentCascadeEventId,
      Guid? actorId,
      DateTimeOffset updatedAt)
  {
      ArgumentNullException.ThrowIfNull(childTableName);

      var sql = $"UPDATE \"{childTableName.Value}\" SET " +
                $"\"is_deleted\" = false, \"updated_at\" = @p_updated_at, " +
                $"\"updated_by\" = @p_updated_by, \"cascade_event_id\" = NULL " +
                $"WHERE \"cascade_event_id\" = @p_cascade_event_id AND \"is_deleted\" = true";

      var parameters = new DynamicParameters();
      parameters.Add("p_updated_at",         updatedAt);
      parameters.Add("p_updated_by",         actorId);
      parameters.Add("p_cascade_event_id",   parentCascadeEventId);
      return (sql, parameters);
  }
  ```

- [x] **Task 2 — Add `RestoreCascadeAsync` to `SoftDeleteCascade.cs`** (AC: 2, 3, 10)
  - [x] Modify `src/FormForge.Api/Features/DynamicCrud/SoftDeleteCascade.cs`
  - Add a new static method after `ExecuteAsync`:

  ```csharp
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
      // suffices because cascade_event_id is the correlation key.
      foreach (var (_, nodeInfo) in graph)
      {
          var (updateSql, updateParams) = DynamicQueryBuilder.BuildRestoreCascadeChildrenQuery(
              nodeInfo.TableName, parentCascadeEventId, actorId, updatedAt);
          await conn.ExecuteAsync(new CommandDefinition(
              updateSql, updateParams, transaction: tx,
              commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);
      }
  }
  ```

- [x] **Task 3 — Add `RestoreRecordHandler` + route registration to `DynamicDataEndpoints.cs`** (AC: 1–10)
  - [x] Modify `src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs`

  **3a — Add `Problems.RecordNotDeleted()` to the `private static class Problems` block** (after `RecordAlreadyDeleted()`):
  ```csharp
  // Story 6.6 — PUT /restore attempted against a record that is not soft-deleted.
  // Distinct from the other 422 variants: the record exists but is already active,
  // so restore is a no-op client error.
  internal static IResult RecordNotDeleted() =>
      Results.Problem(
          title: "Record is not soft-deleted",
          statusCode: StatusCodes.Status422UnprocessableEntity,
          extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
          {
              ["code"] = "RECORD_NOT_DELETED",
              ["messageKey"] = "errors.recordNotDeleted",
          });
  ```

  **3b — Register the PUT /restore route** (add after the DELETE registration inside `MapDynamicDataEndpoints`):
  ```csharp
  // Story 6.6 — PUT /api/data/{designerId}/{id}/restore.
  // Permission: "update" (closest semantic fit; no separate "restore" action flag
  // exists in the current CanCreate/Read/Update/Delete model).
  group.MapPut("/{id:guid}/restore", RestoreRecordHandler)
       .WithSummary("Restore a soft-deleted record and its cascade children.")
       .Produces<DynamicRecord>(StatusCodes.Status200OK)
       .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
       .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
       .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)
       .RequirePermission("update")
       .RequireRateLimiting("data-write");
  ```

  **3c — Implement `RestoreRecordHandler`**:
  ```csharp
  // Story 6.6 — PUT /api/data/{designerId}/{id}/restore. Mirrors the prior handler
  // headers (SafeIdentifier → EF binding → registry). Then:
  //   1. Pre-loads cascade schema graph (EF, before connection open) only when
  //      entry.ChildRepeaterDesignerIds is non-empty — same pattern as DeleteRecordHandler.
  //   2. Opens Dapper connection, SELECTs the parent row (existence + is_deleted check).
  //   3. Reads the parent's cascade_event_id from the SELECT result.
  //   4. Executes a single NpgsqlTransaction:
  //        - BuildRestoreByIdQuery (parent)
  //        - SoftDeleteCascade.RestoreCascadeAsync (all descendants by cascade_event_id)
  //          — only when parent's cascade_event_id was non-null AND graph is non-empty.
  //   5. Commits, or rolls back on exception.
  //   6. Appends mutation_audit_log row via EF (Decision 1.6 separate transaction).
  //   7. Returns 200 with the restored parent record (overlay pattern — no re-SELECT).
  internal static async Task<IResult> RestoreRecordHandler(
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
      // connection (mirrors DeleteRecordHandler's EF-before-Dapper pattern).
      var cascadeGraph = new Dictionary<string, SoftDeleteCascade.NodeInfo>(StringComparer.Ordinal);
      if (entry.ChildRepeaterDesignerIds.Count > 0)
      {
          await SoftDeleteCascade.BuildSchemaGraphAsync(
              entry.ChildRepeaterDesignerIds, db, schemaRegistry, cascadeGraph, ct)
              .ConfigureAwait(false);
      }

      var actorId = httpContext.User.FindFirst("userId")?.Value is { } userIdStr
          && Guid.TryParse(userIdStr, out var uid) ? uid : (Guid?)null;

      IResult? earlyResult = null;
      var existingRow = new Dictionary<string, object?>(StringComparer.Ordinal);
      DateTimeOffset updatedAt = default;
      Guid? parentCascadeEventId = null;

      var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
      try
      {
          // SELECT the parent row (existence check for AC-5 + is_deleted check for AC-6)
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

              // AC-6 — record is already active; restore is a no-op client error.
              if (existingRow.TryGetValue("is_deleted", out var isDeletedVal) && isDeletedVal is false)
              {
                  earlyResult = Problems.RecordNotDeleted();
              }
              else
              {
                  // Read the parent's current cascade_event_id before the restore UPDATE
                  // clears it — needed to match child rows for cascade-restore.
                  if (existingRow.TryGetValue("cascade_event_id", out var ceVal) && ceVal is Guid ceGuid)
                      parentCascadeEventId = ceGuid;

                  var (updateSql, updateParams, ts) = DynamicQueryBuilder.BuildRestoreByIdQuery(
                      safeId, id, actorId);
                  updatedAt = ts;

                  // Always open a transaction: cascade or not, atomicity is cheap here.
                  var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
                  try
                  {
                      var rowsAffected = await conn.ExecuteAsync(new CommandDefinition(
                          updateSql, updateParams, transaction: tx,
                          commandTimeout: 5, cancellationToken: ct))
                          .ConfigureAwait(false);

                      if (rowsAffected == 0)
                      {
                          // Concurrent restore raced between SELECT and UPDATE.
                          await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                          earlyResult = Problems.RecordNotDeleted();
                      }
                      else
                      {
                          // AC-2 — cascade-restore children that share the cascade event UUID.
                          if (parentCascadeEventId.HasValue && cascadeGraph.Count > 0)
                          {
                              await SoftDeleteCascade.RestoreCascadeAsync(
                                  cascadeGraph,
                                  parentCascadeEventId.Value,
                                  actorId,
                                  updatedAt,
                                  conn,
                                  tx,
                                  ct).ConfigureAwait(false);
                          }

                          await tx.CommitAsync(ct).ConfigureAwait(false);

                          // Overlay restored system columns onto the SELECT result.
                          existingRow["is_deleted"]       = false;
                          existingRow["updated_at"]       = updatedAt;
                          existingRow["updated_by"]       = actorId;
                          existingRow["cascade_event_id"] = null;  // cleared on restore
                      }
                  }
                  catch
                  {
                      await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                      throw;
                  }
                  finally
                  {
                      await tx.DisposeAsync().ConfigureAwait(false);
                  }
              }
          }
      }
      finally
      {
          await conn.DisposeAsync().ConfigureAwait(false);
      }

      if (earlyResult is not null) return earlyResult;

      // AC-4 — audit row via EF (separate transaction per Decision 1.6)
      var correlationId = httpContext.GetCorrelationId();
      db.MutationAuditLog.Add(new Domain.Entities.MutationAuditLogEntry
      {
          DesignerId     = safeId!.Value,
          RecordId       = id,
          Operation      = "RESTORE",
          ActorId        = actorId,
          Timestamp      = updatedAt,
          NewValues      = null,
          PreviousValues = null,
          CorrelationId  = correlationId,
      });
      await db.SaveChangesAsync(ct).ConfigureAwait(false);

      return Results.Ok(new DynamicRecord(existingRow));
  }
  ```

- [x] **Task 4 — Unit tests for the two new query builder methods** (AC: 1, 2, 3, 6, 10)
  - [x] Modify `src/FormForge.Api.Tests/Features/DynamicCrud/DynamicQueryBuilderTests.cs`
  - Add to the existing class (after the `BuildSoftDelete*` tests at the end):

  Tests to add (3 unit tests):
  1. `BuildRestoreByIdQuery_SetsIsDeletedFalse_ClearsSystemColumns` — SQL sets `is_deleted=false`, `cascade_event_id=NULL` literal (not parameter); WHERE includes `AND "is_deleted" = true`; `p_id` param equals input UUID
  2. `BuildRestoreByIdQuery_ReturnsUpdatedAtTimestamp` — `updatedAt` is in range [before, after]
  3. `BuildRestoreCascadeChildrenQuery_MatchesCascadeEventId` — SQL sets `is_deleted=false`, `cascade_event_id=NULL`; WHERE `cascade_event_id=@p_cascade_event_id AND is_deleted=true`; param value matches input UUID

  Estimated: +3 unit tests → running total ~493

- [x] **Task 5 — Integration tests: `RestoreIntegrationTests.cs`** (AC: 1–10)
  - [x] Create `src/FormForge.Api.Tests/Features/DynamicCrud/RestoreIntegrationTests.cs`
  - Class signature: `[Collection("DynamicCrudTests")] public sealed class RestoreIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime`
  - `InitializeAsync` / `DisposeAsync` **identical** to `SoftDeleteIntegrationTests` (same TRUNCATE, same dynamic-table DROP loop, same `ReseedSystemRolesAsync` + `SeedTestUsersAsync`)
  - Copy all helper methods from `SoftDeleteIntegrationTests` verbatim (LoginAsync, PostRecordAsync, DeleteRecordAsync, SetupProvisionedDesignerWithTitleAsync, CreateRecordAndGetIdAsync, GetUserIdFromToken, GetRecordAsync, etc.)
  - Add new helper `RestoreRecordAsync(string token, string designerId, Guid id)`:
    ```csharp
    private async Task<HttpResponseMessage> RestoreRecordAsync(
        string token, string designerId, Guid id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"/api/data/{Uri.EscapeDataString(designerId)}/{id}/restore");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent("", Encoding.UTF8, "application/json");
        return await _client!.SendAsync(request);
    }
    ```

  **Test cases (8 integration tests):**

  1. `Restore_SoftDeletedRecord_Returns200WithIsDeletedFalse` — AC-1: provision table, create record, DELETE (soft-delete), PUT /restore → 200; verify `isDeleted: false`, `updatedAt` is refreshed, `updatedBy` = actor UUID, `cascadeEventId` is JSON null (present, not absent)

  2. `Restore_RecordNotFound_Returns404` — AC-5: PUT /restore with non-existent UUID → 404 `code: "NOT_FOUND"`

  3. `Restore_RecordNotDeleted_Returns422` — AC-6: create record (is_deleted=false by default), immediately PUT /restore → 422 `code: "RECORD_NOT_DELETED"`, `messageKey: "errors.recordNotDeleted"`

  4. `Restore_UnprovisionedDesigner_Returns404TableNotProvisioned` — AC-7: unknown designerId → 404 `TABLE_NOT_PROVISIONED`

  5. `Restore_NoCanUpdate_Returns403` — AC-8: viewer user → 403 `code: "FORBIDDEN"`

  6. `Restore_AuditLogRow_AppendedWithRestoreOperation` — AC-4: create → DELETE → PUT /restore → query `mutation_audit_log` with `operation = 'RESTORE'`; assert `actor_id` matches JWT actor, `timestamp` within request window, `new_values IS NULL`, `previous_values IS NULL`, `correlation_id` non-empty

  7. `Restore_SystemColumnsInResponse_AreCamelCase` — AC-1 AR-46: verify response has `id`, `createdAt`, `updatedAt`, `createdBy`, `updatedBy`, `isDeleted: false`, `cascadeEventId: null` in camelCase; snake_case keys absent

  8. `Restore_CascadeDelete_ThenRestore_OnlyRestoresMatchingChildren` — AC-2 + AC-3:
     - Provision parent designer with a child Repeater designer (same two-designer provisioning pattern as Story 6.5 cascade test if it exists, otherwise use SetupProvisionedDesignerWithTitleAsync to provision two designers and bind child to parent)
     - Create parent record, create child record (linked via FK), DELETE parent (cascade-delete) — both rows should have same `cascade_event_id`
     - Create another child record and individually soft-delete it (direct Npgsql UPDATE, different or null cascade_event_id)
     - PUT /restore parent → 200
     - Direct Npgsql SELECT verifies: parent `is_deleted=false`, original child `is_deleted=false` (cascade_event_id matched), individually-deleted child still `is_deleted=true` (not restored)

  Estimated: +8 integration tests → running total ~501

- [x] **Task 6 — Frontend API client and hook** (foundational for Stories 6.9/6.10)
  - [x] Create `web/src/features/data-entry/restoreRecordApi.ts`:
    ```ts
    import { httpClient } from '../auth/httpClient'
    import type { DynamicRecord } from './recordListApi'

    // Story 6.6 — typed client for PUT /api/data/{designerId}/{id}/restore.
    // Returns the restored record (isDeleted: false) per AR-46 Option C on 200.
    export function restoreRecord(
      designerId: string,
      id: string,
    ): Promise<DynamicRecord> {
      return httpClient.put<DynamicRecord>(
        `/api/data/${encodeURIComponent(designerId)}/${encodeURIComponent(id)}/restore`,
        {},
      )
    }
    ```
  - [x] Create `web/src/features/data-entry/useRestoreRecord.ts`:
    ```ts
    import { useMutation, useQueryClient } from '@tanstack/react-query'
    import { restoreRecord } from './restoreRecordApi'
    import type { DynamicRecord } from './recordListApi'

    // Story 6.6 — TanStack mutation hook for PUT /api/data/{designerId}/{id}/restore.
    // AR-48 + AR-49: pessimistic mutation. Invalidates the list root key and the
    // single-record key on success so list / get views re-fetch with the restored
    // record appearing as active.
    export function useRestoreRecord(designerId: string) {
      const queryClient = useQueryClient()

      return useMutation({
        mutationFn: (id: string) => restoreRecord(designerId, id),
        onSuccess: (_restored: DynamicRecord, id) => {
          queryClient.invalidateQueries({ queryKey: ['data', designerId] })
          queryClient.invalidateQueries({ queryKey: ['data', designerId, 'record', id] })
        },
      })
    }
    ```

---

## Dev Notes

### §1 — Transaction strategy: always transactional, flat cascade

**Individual restore (cascade_event_id IS NULL in parent):**
- Open NpgsqlTransaction, run single `BuildRestoreByIdQuery`, commit.
- Decision: always use a transaction even for non-cascade restores to keep the code path uniform and remove the branching complexity seen in `DeleteRecordHandler`.

**Cascade restore (cascade_event_id IS NOT NULL in parent):**
- Same NpgsqlTransaction: parent restore UPDATE + `SoftDeleteCascade.RestoreCascadeAsync`.
- `RestoreCascadeAsync` iterates the flat graph dict — one `BuildRestoreCascadeChildrenQuery` per descendant table. No per-row recursion needed because we're matching globally by `cascade_event_id`.
- All descendant tables are iterated regardless of FK depth — the cascade_event_id UUID uniquely identifies the event across all depths.

**Cast `IDbConnection` to `NpgsqlConnection`:** Same rationale as `DeleteRecordHandler` — `DbConnectionFactory` always returns an `NpgsqlConnection`; cast for `BeginTransactionAsync`. (NOTE: Looking at the existing code in `DynamicDataEndpoints.cs`, `conn` is `IDbConnection` returned from `connectionFactory.CreateOpenConnectionAsync(ct)`. The connection is cast to `NpgsqlConnection` only when needed for `BeginTransactionAsync`. In `RestoreRecordHandler`, call `conn.BeginTransactionAsync(ct)` directly — `IDbConnection` doesn't have `BeginTransactionAsync`, so the cast is needed here too. Look at how `DeleteRecordHandler` handles this: `var tx = await conn.BeginTransactionAsync(ct)` — yes, this works because the underlying type IS `NpgsqlConnection`. The `IDbConnection` interface from `System.Data` does have `BeginTransaction()` but NOT the async version. Use `((NpgsqlConnection)conn).BeginTransactionAsync(ct)` and pass the `NpgsqlTransaction` to `CommandDefinition.Transaction`.)

Actually, looking at the actual `DeleteRecordHandler` implementation:
```csharp
var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
```
This works because at runtime `conn` is a `NpgsqlConnection`. The `IDbConnection` interface in System.Data does NOT define `BeginTransactionAsync`. Since .NET 6+ the compiler can resolve extension methods and interface co-variance here. Let me verify... actually `NpgsqlConnection` inherits from `DbConnection` which has `BeginTransactionAsync`. So you need `((DbConnection)conn).BeginTransactionAsync(ct)` or `((NpgsqlConnection)conn).BeginTransactionAsync(ct)`. Look at the DeleteRecordHandler — it uses `conn.BeginTransactionAsync(ct)` — this works because `CreateOpenConnectionAsync` presumably returns `NpgsqlConnection`. Follow the same pattern exactly.

### §2 — Reading `cascade_event_id` from the Dapper SELECT result

`BuildGetByIdQuery` returns all system columns including `cascade_event_id`. In the Dapper result dict, `cascade_event_id` will be:
- `Guid` (NpgsqlDataReader maps `uuid` → `System.Guid`) if non-null
- `DBNull.Value` or `null` if the column value is NULL

The safe read pattern:
```csharp
if (existingRow.TryGetValue("cascade_event_id", out var ceVal) && ceVal is Guid ceGuid)
    parentCascadeEventId = ceGuid;
// else parentCascadeEventId remains null
```

`is Guid ceGuid` pattern correctly handles both DBNull.Value (not a Guid) and null (not a Guid), leaving `parentCascadeEventId = null` for individual deletes.

### §3 — Decision 1.3 `cascade_event_id` NULL semantics on restore

From Architecture Decision 1.3:
- **Individual restore** (`parentCascadeEventId == null`): clear `cascade_event_id = NULL` unconditionally — no child cascade needed
- **Cascade restore** (`parentCascadeEventId != null`): restore children WHERE `cascade_event_id = @cascadeId AND is_deleted = true` — only rows from THIS cascade event; rows with different cascade_event_id (different event, individually deleted, or from a prior event) are NOT touched

The `BUILD RestoreByIdQuery` always sets `cascade_event_id = NULL` on the parent (literal, not parameter) — this is the "cleared on restore" guarantee. The child restore via `BuildRestoreCascadeChildrenQuery` also sets `cascade_event_id = NULL` on all restored children.

### §4 — `is_deleted` check: `isDeletedVal is false` vs `isDeletedVal is true`

For the "already active" check (AC-6), use:
```csharp
if (existingRow.TryGetValue("is_deleted", out var isDeletedVal) && isDeletedVal is false)
```

This is the inverse of the DeleteRecordHandler pattern which checks `isDeletedVal is true`. Note the same caveat as Story 6.5's deferred item: if `is_deleted` were NULL in a DB row (impossible given NOT NULL DEFAULT false), `isDeletedVal is false` would evaluate false and the handler would proceed to attempt restore of an indeterminate row. This is impossible in practice but consistent with the existing pattern.

### §5 — `RestoreCascadeAsync` does NOT need the schema graph to be non-empty to call

The caller already guards:
```csharp
if (parentCascadeEventId.HasValue && cascadeGraph.Count > 0)
```

So `RestoreCascadeAsync` is only called when there are known descendant tables. The graph was loaded by `BuildSchemaGraphAsync` which populates it from Published child schemas. If `cascadeGraph.Count == 0` but `parentCascadeEventId` is non-null (child schemas were unpublished after the cascade delete), the restore proceeds without touching child tables — same silent degradation behavior as the soft-delete path. A warning log here (similar to `LogCascadeSoftDeleteSkipped`) would be good for observability; mark as a deferred item.

### §6 — Route conflict: `/{id:guid}/restore` vs `/{id:guid}`

ASP.NET Minimal API route matching is longest-match. `/{id:guid}/restore` is more specific than `/{id:guid}`. The route constraint `{id:guid}` matches only valid UUIDs before the next segment, so `/restore` as a literal segment disambiguation works correctly. This is the same pattern used in admin routes. No changes to existing routes needed.

### §7 — `RestoreRecordHandler` does NOT need a `loggerFactory` parameter

Unlike `DeleteRecordHandler` which has `LogCascadeSoftDeleteSkipped`, the restore handler doesn't currently have a structured log message (deferring a "cascade restore skipped" warning to a future story). No `ILoggerFactory` injection is required. All prior handlers that don't log use `(designerId, id, httpContext, db, schemaRegistry, connectionFactory, ct)` as the parameter set — follow that pattern exactly.

### §8 — AC-11: "Show deleted" toggle is purely frontend, no new backend work

The backend list endpoint already supports `filter[isDeleted]=true` (shows only deleted) and `filter[isDeleted]=false` (shows only active). The frontend toggle sets/clears this filter key in `RecordListParams`. Since Stories 6.9/6.10 build the full record list UI, the toggle implementation belongs there. Story 6.6's contribution is the API/hook only. Mark AC-11 as satisfied by the filter capability; the UI affordance will ship in 6.9/6.10.

### §9 — `NpgsqlTransaction` passing to `CommandDefinition` in `RestoreCascadeAsync`

Every Dapper call inside `RestoreCascadeAsync` **must** pass `transaction: tx` in the `CommandDefinition`. This is the same requirement as `SoftDeleteCascade.ExecuteAsync`. The method receives `NpgsqlTransaction tx` from the caller and passes it through. Without the transaction param, Dapper uses an implicit connection-level transaction which does NOT guarantee atomicity with the caller's transaction.

### §10 — Test infrastructure: cascade test requires two provisioned designers

Test 8 (`Restore_CascadeDelete_ThenRestore_OnlyRestoresMatchingChildren`) is the most complex. It requires:
1. Two provisioned designers: a parent and a child Repeater designer
2. The child designer's schema must have a FK column referencing the parent

**Simplified approach**: Since setting up cross-designer relationships in integration tests is complex (requires provisioning the child designer, binding it as a Repeater under the parent, etc.), consider a simpler verification:
- Use a single table with a `self-referencing` parent approach (not applicable here)
- OR: test AC-3 by directly inserting rows into the child table with the correct cascade_event_id, bypassing the full cascade-delete path

**Recommended approach for test 8**: 
- Provision one designer (e.g., `restore_parent`) normally
- Insert two rows directly into the provisioned table with the same `cascade_event_id` UUID via Npgsql (simulating what a cascade delete would do)
- Insert a third row with a DIFFERENT `cascade_event_id` (simulating individually deleted)
- GET the first row to confirm it's "soft-deleted" (is_deleted=true)
- PUT /restore the first row
- Assert: row 1 → is_deleted=false (the one we restored), row 2 → is_deleted=false (same cascade_event_id), row 3 → is_deleted=true (different cascade_event_id)

But wait — this test is for the PARENT designer; the cascade restore iterates `cascadeGraph` which is built from `entry.ChildRepeaterDesignerIds`. If the schema has no child Repeater designers, `cascadeGraph` will be empty and `RestoreCascadeAsync` won't run. The test would only exercise the parent path.

To test AC-2 properly, we need a parent schema with at least one child. The existing `SoftDeleteIntegrationTests` test for AC-2 cascade likely uses a helper to set up the two-designer provisioning. Check if `SoftDeleteIntegrationTests.cs` has such a helper (lines not shown above) — if it does, copy it verbatim.

**Fallback**: Mark the cascade restore integration test as a deferred item if the cascade setup is too complex. The unit test for `BuildRestoreCascadeChildrenQuery` and the `RestoreCascadeAsync` method logic covers the correctness; the integration test is belt-and-suspenders.

---

### File locations — new files

| New file | Path |
|---|---|
| `RestoreIntegrationTests.cs` | `src/FormForge.Api.Tests/Features/DynamicCrud/RestoreIntegrationTests.cs` |
| `restoreRecordApi.ts` | `web/src/features/data-entry/restoreRecordApi.ts` |
| `useRestoreRecord.ts` | `web/src/features/data-entry/useRestoreRecord.ts` |

### File locations — modified files

| Modified file | Change |
|---|---|
| `DynamicDataEndpoints.cs` | Add `Problems.RecordNotDeleted()`, `MapPut("/{id:guid}/restore", RestoreRecordHandler)`, handler |
| `DynamicQueryBuilder.cs` | Add `BuildRestoreByIdQuery`, `BuildRestoreCascadeChildrenQuery` |
| `SoftDeleteCascade.cs` | Add `RestoreCascadeAsync` static method |
| `DynamicQueryBuilderTests.cs` | Add 3 unit tests for the two new builder methods |

No changes to: `MutationAuditLogEntry.cs` (already supports `"RESTORE"` per its xmldoc from Story 6.3), `FormForgeDbContext.cs`, `Program.cs`, `DynamicRecord.cs`, `DynamicRecordJsonConverter.cs`, `DynamicPayloadValidator.cs`. No new EF migration required.

**No new `using` imports needed** in `DynamicDataEndpoints.cs` — `using Npgsql;` was added in Story 6.5.

---

### Deferred items (record for code review)

1. **No "cascade restore skipped" warning log** — When `parentCascadeEventId` is non-null but `cascadeGraph.Count == 0` (child schemas unpublished since cascade delete), restore proceeds silently without touching child tables. A structured warning analogous to `LogCascadeSoftDeleteSkipped` would aid operations. Owner: Story 6.8 or observability hardening pass.
2. **No AC-9 rate-limit integration test** — Same gap as Stories 6.3/6.4/6.5. Owner: when a virtualised clock is available.
3. **No AC-10 commandTimeout integration test** — Same gap. Owner: with a Dapper `CommandDefinition` test abstraction.
4. **Audit INSERT not transactionally atomic with Dapper UPDATEs** — Same pattern as 6.3/6.4/6.5. If `db.SaveChangesAsync` throws after Dapper commit, rows are restored without an audit row. Owner: outbox pattern (architectural change).
5. **`cascade_event_id` IS NULL for individually-deleted parent** — If the parent was soft-deleted without cascade (e.g., no Repeater children at deletion time), `parentCascadeEventId` will be null and `RestoreCascadeAsync` is not called. Children that were cascade-deleted in a prior event are NOT touched. This is correct per Decision 1.3 but may surprise operators who expect a "restore all related rows" behavior. Owner: document in admin UI tooltip.
6. **AC-11 "Show deleted" toggle UI** — The filter infrastructure is ready; the visual affordance ships in Story 6.9/6.10. Owner: those stories.
7. **Concurrent restore race window** — Between SELECT and UPDATE, a concurrent operation could re-delete or restore the row. READ COMMITTED isolation. Owner: future hardening, same as Stories 6.4/6.5.

---

### References

- [Source: `src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs`] — `DeleteRecordHandler` (lines 660–853) — mirror for `RestoreRecordHandler`: SafeIdentifier → EF binding → registry → actorId extraction; cascade graph pre-loading; SELECT-before-UPDATE; NpgsqlTransaction pattern; try/finally for tx.DisposeAsync and conn.DisposeAsync; Problems inner class for `RecordNotDeleted()`; route registration after `MapDelete`
- [Source: `src/FormForge.Api/Features/DynamicCrud/SoftDeleteCascade.cs`] — `BuildSchemaGraphAsync` (reused unchanged); `ExecuteAsync` (reference for `RestoreCascadeAsync` structure, though simpler — no per-row recursion)
- [Source: `src/FormForge.Api/Features/DynamicCrud/DynamicQueryBuilder.cs`] — `BuildSoftDeleteByIdQuery` (lines 360–397) — parallel structure for `BuildRestoreByIdQuery`; `BuildGetByIdQuery` (lines 203–219) — used inside handler for pre-restore SELECT; `BuildFkColumnName` — used by cascade
- [Source: `src/FormForge.Api.Tests/Features/DynamicCrud/SoftDeleteIntegrationTests.cs`] — copy all helpers verbatim; TRUNCATE statement; `[Collection("DynamicCrudTests")]`; `ReseedSystemRolesAsync` + `SeedTestUsersAsync`; `GetUserIdFromToken`
- [Architecture: Decision 1.3] — cascade_event_id NULL semantics for restore; individual restore clears unconditionally; cascade restore matches by id; children NOT incidentally restored
- [Architecture: AR-46 Option C] — `DynamicRecordJsonConverter` handles `cascade_event_id` → `cascadeEventId`; `is_deleted` → `isDeleted: false` automatically
- [Architecture: Decision 1.6] — `commandTimeout: 5` on all Dapper calls; EF + Dapper separated transactions (audit via EF AFTER restore transaction commits)
- [Architecture: Decision 2.2] — `EffectivePermissions.CanUpdate` checked by `.RequirePermission("update")`

---

## Dev Agent Record

### Agent Model Used

Opus 4.7 (1M context) — `claude-opus-4-7[1m]`

### Debug Log References

- Build of API: `dotnet build src/FormForge.Api/FormForge.Api.csproj` → 0 warnings, 0 errors after restore handler addition.
- Unit tests scope: `dotnet test ... --filter "FullyQualifiedName~DynamicQueryBuilderTests"` → 39/39 passed (3 new + 36 existing).
- Integration tests scope: `dotnet test ... --filter "FullyQualifiedName~RestoreIntegrationTests"` → 8/8 passed in 20.3 s.
- Full backend regression: `dotnet test src/FormForge.Api.Tests/...` → 502/502 passed in 2 m 18 s.
- Frontend typecheck (`npx tsc -b --noEmit`): no errors from new files (`restoreRecordApi.ts`, `useRestoreRecord.ts`); pre-existing unrelated errors in `Navbar.test.tsx` and `usePollProvisioning.test.tsx` left untouched.
- Frontend lint (`eslint`) on new files: clean.

### Completion Notes List

- `BuildRestoreByIdQuery` and `BuildRestoreCascadeChildrenQuery` added to `DynamicQueryBuilder`. Both set `cascade_event_id = NULL` as a SQL literal (not a parameter) — same Dapper/Npgsql workaround pattern used by `BuildSoftDeleteByIdQuery`/`BuildInsertQuery`. Both WHERE clauses guard on `is_deleted = true` so concurrent restores return 0 rowsAffected.
- `SoftDeleteCascade.RestoreCascadeAsync` iterates the flat schema graph (one UPDATE per child table). No per-row recursion is needed because `cascade_event_id` is unique per event — matching globally by that UUID is the correct restore set across all depths.
- `RestoreRecordHandler` always opens an `NpgsqlTransaction` even when the schema has no children (Dev Notes §1). Cascade restore runs only when the parent's prior `cascade_event_id` was non-null AND the schema graph has descendant tables. The `is Guid ceGuid` pattern correctly handles `DBNull.Value` / null cases for individually-deleted parents.
- `BeginTransactionAsync` is called directly on `conn` because `DbConnectionFactory.CreateOpenConnectionAsync` returns `NpgsqlConnection` (not `IDbConnection`) — verified in source. No cast required, matching the existing `DeleteRecordHandler` pattern.
- Route `PUT /api/data/{designerId}/{id}/restore` registered with `.RequirePermission("update")` and `.RequireRateLimiting("data-write")` — same per-endpoint rate-limit override as Stories 6.3/6.4/6.5. ASP.NET picks `/{id:guid}/restore` over `/{id:guid}` because the literal segment is more specific.
- `Problems.RecordNotDeleted()` returns 422 with `code: "RECORD_NOT_DELETED"` and `messageKey: "errors.recordNotDeleted"` — distinct from the four other 422 codes used in this feature area (`RECORD_DELETED`, `RECORD_ALREADY_DELETED`, `VALIDATION_FAILED`).
- Audit row is appended via EF in a separate transaction from the Dapper UPDATEs (Decision 1.6), with `operation = "RESTORE"`, `new_values = null`, `previous_values = null`. Same atomicity caveat as Stories 6.3/6.4/6.5 — recorded as a deferred item.
- Integration test 8 (`Restore_CascadeDelete_ThenRestore_OnlyRestoresMatchingChildren`) provisions a parent+Repeater-child pair via the public API (matching Story 6.5's cascade-delete test), creates a cascade-deleted child set, plus a separately-deleted child with `cascade_event_id = NULL`, then PUT /restore the parent. Direct Npgsql `SELECT` confirms parent + matching children restored, mismatched child untouched. This is the AC-2 + AC-3 acceptance witness.
- Frontend `restoreRecordApi.ts` and `useRestoreRecord.ts` mirror Story 6.5's `deleteRecord*` files. Pessimistic mutation; invalidates both the list root and the single-record query key on success.

### File List

**New files:**
- `src/FormForge.Api.Tests/Features/DynamicCrud/RestoreIntegrationTests.cs`
- `web/src/features/data-entry/restoreRecordApi.ts`
- `web/src/features/data-entry/useRestoreRecord.ts`

**Modified files:**
- `src/FormForge.Api/Features/DynamicCrud/DynamicQueryBuilder.cs` (added `BuildRestoreByIdQuery`, `BuildRestoreCascadeChildrenQuery`)
- `src/FormForge.Api/Features/DynamicCrud/SoftDeleteCascade.cs` (added `RestoreCascadeAsync`)
- `src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs` (added `MapPut("/{id:guid}/restore")`, `Problems.RecordNotDeleted()`, `RestoreRecordHandler`)
- `src/FormForge.Api.Tests/Features/DynamicCrud/DynamicQueryBuilderTests.cs` (added 3 unit tests under `Story 6.6` region)
- `_bmad-output/implementation-artifacts/sprint-status.yaml` (6-6 → review)
- `_bmad-output/implementation-artifacts/6-6-view-and-restore-soft-deleted-records.md` (Status, Tasks/Subtasks checkboxes, Dev Agent Record, File List, Change Log)

### Change Log

- 2026-05-26 — Story 6.6 implemented: `PUT /api/data/{designerId}/{id}/restore` endpoint with cascade-restore semantics (matches descendants by `cascade_event_id` within one NpgsqlTransaction). 3 new builder unit tests + 8 new integration tests = 502/502 backend tests passing. Frontend API client and TanStack mutation hook added. Status → review.

### Review Findings

- [x] [Review][Patch] Cascade test does not exercise "different non-null cascade_event_id" case — only NULL is tested [`RestoreIntegrationTests.cs:782`] — applied: added `childId4` with a fresh non-null `unrelatedCascadeId`; post-restore assertion verifies `is_deleted=true` and the original `cascade_event_id` preserved
- [x] [Review][Patch] Happy-path test should verify the DB row actually changed and add an `Assert.InRange(updatedAt, before, after)` server-side timestamp assertion [`RestoreIntegrationTests.cs:94`] — applied: response `updatedAt` is now `InRange(before, after)` and a direct Npgsql `SELECT` confirms `is_deleted=false`, `cascade_event_id=NULL`, `updated_at` refreshed
- [x] [Review][Patch] Audit timestamp should be asserted equal to the response `updatedAt` (currently only `InRange(before, after)`) [`RestoreIntegrationTests.cs:227`] — applied with a 1µs tolerance to absorb PG TIMESTAMPTZ microsecond truncation vs .NET 100ns Tick precision
- [x] [Review][Patch] Add negative-path assertion: `mutation_audit_log` is empty after 404 / 422 responses [`RestoreIntegrationTests.cs`] — applied to both `Restore_RecordNotFound_Returns404` and `Restore_RecordNotDeleted_Returns422`
- [x] [Review][Patch] Remove or repurpose stray `Task.Delay(50)` in happy-path test — currently does not gate any assertion [`RestoreIntegrationTests.cs:103`] — applied: removed; `before`/`after` window now provides the ordering witness
- [x] [Review][Defer] Cascade-restore silently skipped when child designer unpublished between delete and restore — add warning log mirroring `LogCascadeSoftDeleteSkipped` [`DynamicDataEndpoints.cs:256`] — deferred, acknowledged in story Deferred Item #1
- [x] [Review][Defer] SELECT-then-UPDATE race window (no row lock; default READ COMMITTED) [`DynamicDataEndpoints.cs:198-265`] — deferred, project-wide pattern (same as 6.4 / 6.5)
- [x] [Review][Defer] Audit row not transactionally atomic with Dapper UPDATE (separate EF transaction per Decision 1.6) [`DynamicDataEndpoints.cs:1056`] — deferred, accepted architectural trade-off until outbox pattern
- [x] [Review][Defer] `tx.RollbackAsync` / `tx.DisposeAsync` exception in catch/finally masks original exception [`DynamicDataEndpoints.cs:280-285`] — deferred, mirrors `DeleteRecordHandler`
- [x] [Review][Defer] `isDeletedVal is false` pattern is brittle to DBNull or non-bool values; safe only because 0-rowsAffected guard catches it [`DynamicDataEndpoints.cs:219`] — deferred, behaviorally safe today
- [x] [Review][Defer] Idempotency test missing — delete → restore (200) → restore (422) sequence is not exercised — deferred, low-priority coverage gap
- [x] [Review][Defer] Multi-level cascade depth (grandparent → parent → child → grandchild) not exercised; flat-graph correctness at depth >2 relies on `BuildSchemaGraphAsync` BFS — deferred, single-level happy path is covered
- [x] [Review][Defer] Comment on `is_deleted = true` predicate in `BuildRestoreCascadeChildrenQuery` says "defensive — should never match" but it DOES match in the individual-restore-then-cascade-restore sequence [`DynamicQueryBuilder.cs:476`] — deferred, comment-only fix
- [x] [Review][Defer] `updatedAt` captured from `DateTimeOffset.UtcNow` (app clock) rather than PG `NOW()` [`DynamicQueryBuilder.cs:361`] — deferred, consistent with 6.3 / 6.4 / 6.5 builders
- [x] [Review][Defer] 422 `RECORD_NOT_DELETED` is returned for both "already active at SELECT" and "concurrent restore raced between SELECT and UPDATE" — semantically distinct cases share one code [`DynamicDataEndpoints.cs:221,250`] — deferred, acceptable per spec
