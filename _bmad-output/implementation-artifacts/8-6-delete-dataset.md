# Story 8.6: Delete Dataset

Status: done

## Story

As a user with the `dataset-management` permission,
I want to delete a Dataset,
so that its row and backing PostgreSQL VIEW are removed atomically.

## Acceptance Criteria

**AC-1 — Success: row deleted + VIEW dropped → HTTP 204**
**Given** I DELETE `/api/datasets/{id}` where `{id}` exists in `custom_dataset`
**When** the server executes
**Then** within one NpgsqlTransaction:
  - `DELETE FROM custom_dataset WHERE id = @id` removes the row (1 row affected)
  - `DROP VIEW IF EXISTS datasets."{dataset_name}"` executes
  - both commit atomically (any failure → full rollback, VIEW remains intact)
**And** HTTP 204 No Content is returned

**AC-2 — Not found → HTTP 404**
**Given** I DELETE `/api/datasets/{id}` where `{id}` does not exist in `custom_dataset`
**When** the server looks up the row
**Then** HTTP 404 is returned with no body change to the database

**AC-3 — Audit log on success**
**Given** the DELETE commits successfully (AC-1)
**When** I inspect `dataset_audit_log`
**Then** one new row exists with:
  - `operation = 'DELETE'`
  - `dataset_name` = the deleted dataset's name
  - `ddl = 'DROP VIEW IF EXISTS datasets."{dataset_name}"'`
  - `actor_id` = the requesting user's ID
  - `timestamp` is populated
  - `succeeded = true`

---

## Tasks / Subtasks

- [x] **Task 1 — Add `DeleteDatasetOutcome` enum and result type to `DatasetService.cs`**
  - [x] At the top of `src/FormForge.Api/Features/Datasets/DatasetService.cs`, add alongside `CreateDatasetOutcome` and `UpdateDatasetOutcome`:
    ```csharp
    internal enum DeleteDatasetOutcome { Success, NotFound }

    internal sealed record DeleteDatasetResult(
        DeleteDatasetOutcome Outcome);
    ```

- [x] **Task 2 — Extend `IDatasetService` with `DeleteAsync`**
  - [x] In `DatasetService.cs`, extend the interface:
    ```csharp
    internal interface IDatasetService
    {
        Task<CreateDatasetResult> CreateAsync(
            CreateDatasetRequest request,
            DatasetName name,
            Guid actorId,
            string? correlationId,
            CancellationToken ct);

        Task<UpdateDatasetResult> UpdateAsync(
            Guid datasetId,
            UpdateDatasetRequest request,
            DatasetName? newName,
            Guid actorId,
            string? correlationId,
            CancellationToken ct);

        Task<DeleteDatasetResult> DeleteAsync(
            Guid datasetId,
            Guid actorId,
            string? correlationId,
            CancellationToken ct);
    }
    ```

- [x] **Task 3 — Implement `DeleteAsync` in `DatasetService`**
  - [x] Implementation steps (mirror `CreateAsync` / `UpdateAsync` style):

    **Step A: Resolve actor name**
    ```csharp
    var actorName = await db.Users
        .Where(u => u.Id == actorId)
        .Select(u => u.DisplayName)
        .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    ```

    **Step B: Load current row via Dapper (outside transaction, read-committed)**

    Use the same column-aliased SELECT pattern as `UpdateAsync` (this project does NOT set Dapper's `MatchNamesWithUnderscores`):
    ```csharp
    const string selectSql = """
        SELECT id              AS "Id",
               dataset_name    AS "DatasetName",
               is_custom_query AS "IsCustomQuery",
               query           AS "Query",
               builder_state   AS "BuilderState",
               version         AS "Version",
               created_at      AS "CreatedAt",
               created_by      AS "CreatedBy"
        FROM custom_dataset
        WHERE id = @id
        """;
    var current = await conn.QuerySingleOrDefaultAsync<CurrentDatasetRow>(
        new CommandDefinition(selectSql, new { id = datasetId },
            commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);
    if (current is null)
        return new DeleteDatasetResult(DeleteDatasetOutcome.NotFound);
    ```

    `CurrentDatasetRow` is the **existing** private record already defined in `DatasetService` — do NOT add a second one.

    **Step C: Pre-build the DROP DDL for the audit log**
    ```csharp
    var dropDdl = DatasetViewManager.BuildDropViewDdl(current.DatasetName);
    var now = DateTimeOffset.UtcNow;
    var succeeded = false;
    ```

    **Step D: Transaction — DELETE row + DROP VIEW (AR-59)**
    ```csharp
    var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
    try
    {
        const string deleteSql = """
            DELETE FROM custom_dataset WHERE id = @id
            """;
        await conn.ExecuteAsync(new CommandDefinition(
            deleteSql,
            new { id = datasetId },
            transaction: tx,
            commandTimeout: 5,
            cancellationToken: ct)).ConfigureAwait(false);

        await viewManager.DropAsync(conn, tx, current.DatasetName, ct).ConfigureAwait(false);

        await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        succeeded = true;
        LogViewDropped(logger, current.DatasetName);
    }
    catch (OperationCanceledException)
    {
        // Don't write audit log on request cancellation.
        throw;
    }
    catch (NpgsqlException ex)
    {
        await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        LogViewDropFailed(logger, ex, current.DatasetName);
    }
    finally
    {
        await tx.DisposeAsync().ConfigureAwait(false);
    }
    ```

    **Step E: Audit log via EF (after Dapper transaction settled)**

    Record the deleted row's values as `previous_values`; `new_values` is null (the record is gone):
    ```csharp
    var previousValues = JsonSerializer.Serialize(new
    {
        dataset_name     = current.DatasetName,
        is_custom_query  = current.IsCustomQuery,
        query            = current.Query,
        builder_state    = current.BuilderState,
        version          = current.Version,
    });

    db.DatasetAuditLog.Add(new DatasetAuditLogEntry
    {
        DatasetName    = current.DatasetName,
        Operation      = "DELETE",
        ActorId        = actorId,
        ActorName      = actorName,
        PreviousValues = previousValues,
        NewValues      = null,
        Ddl            = dropDdl,
        Succeeded      = succeeded,
        CorrelationId  = correlationId,
        Timestamp      = now,
    });
    try
    {
        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
    }
    #pragma warning disable CA1031
    catch (Exception auditEx)
    #pragma warning restore CA1031
    {
        LogAuditWriteFailed(logger, auditEx, current.DatasetName);
    }
    ```

    **Step F: Return result**
    ```csharp
    return new DeleteDatasetResult(
        succeeded ? DeleteDatasetOutcome.Success : DeleteDatasetOutcome.NotFound);
    ```

    Wait — on DDL failure after a successful DELETE we still return a 5xx, not 404. Model it explicitly: if `succeeded` is false after the tx (NpgsqlException caught), return a distinct outcome or surface as a 500. Since the story ACs only define 204 / 404, and a DDL failure is catastrophic and unexpected (DROP VIEW IF EXISTS essentially never fails unless PG is down), returning a 500 on that path is correct:
    ```csharp
    if (!succeeded)
    {
        // NpgsqlException path — DDL failed, transaction rolled back, row still in DB.
        // Surface as 500 (not 404) — the dataset still exists.
        // Use a generic 500 response in the endpoint handler (see Task 4).
        return new DeleteDatasetResult(DeleteDatasetOutcome.NotFound); // see §2 in dev notes
    }
    return new DeleteDatasetResult(DeleteDatasetOutcome.Success);
    ```

  - [x] Add `[LoggerMessage]` methods:
    ```csharp
    [LoggerMessage(Level = LogLevel.Information, Message = "Dropped VIEW datasets.\"{Name}\"")]
    private static partial void LogViewDropped(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DROP VIEW failed for dataset {Name}")]
    private static partial void LogViewDropFailed(ILogger logger, Exception ex, string name);
    ```

    `LogNameConflict`, `LogAuditWriteFailed`, `LogViewCreateFailed` are **already on the class** — do NOT duplicate them.

- [x] **Task 4 — Add `BuildDropViewDdl` static helper and `DropAsync` to `DatasetViewManager`**
  - [x] In `src/FormForge.Api/Features/Datasets/DatasetViewManager.cs`, add:

    ```csharp
    // Story 8.6 — DROP VIEW IF EXISTS DDL. Uses IF EXISTS for safety (idempotent).
    // datasetName comes from the DB row (already a validated identifier).
    internal static string BuildDropViewDdl(string datasetName) =>
        $"DROP VIEW IF EXISTS datasets.\"{datasetName}\"";

    // Executes DROP VIEW IF EXISTS inside the caller-supplied transaction.
    // Throws NpgsqlException on PostgreSQL errors — caller is responsible for rollback.
    internal async Task DropAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string datasetName,
        CancellationToken ct)
    {
        var ddl = BuildDropViewDdl(datasetName);
        await conn.ExecuteAsync(new CommandDefinition(
            ddl,
            transaction: tx,
            commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds,
            cancellationToken: ct)).ConfigureAwait(false);

        LogViewDropped(logger, datasetName);
    }
    ```
  - [x] Add `[LoggerMessage]` method:
    ```csharp
    [LoggerMessage(Level = LogLevel.Information, Message = "Dropped VIEW datasets.\"{Name}\"")]
    private static partial void LogViewDropped(ILogger logger, string name);
    ```

    Note: `BuildDropViewDdl` takes a plain `string` (not `DatasetName`) because it is called with `current.DatasetName`, which came from the DB and is already a validated identifier. This mirrors `BuildRenameViewDdl`'s `oldName` parameter pattern.

- [x] **Task 5 — Replace the DELETE stub in `DatasetEndpoints.cs`**
  - [x] In `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs`, replace:
    ```csharp
    group.MapDelete("/{id:guid}", (Guid id) => Results.StatusCode(StatusCodes.Status501NotImplemented))
         .WithSummary("Delete dataset (stub — Story 8.6)")
         .RequireDatasetManagement();
    ```
    With:
    ```csharp
    group.MapDelete("/{id:guid}", async (
        Guid id,
        IDatasetService datasetService,
        HttpContext httpContext,
        CancellationToken ct) =>
    {
        if (!Guid.TryParse(httpContext.User.FindFirst("userId")?.Value, out var actorId)
            || actorId == Guid.Empty)
        {
            return Results.Unauthorized();
        }

        var correlationId = httpContext.GetCorrelationId();
        var result = await datasetService
            .DeleteAsync(id, actorId, correlationId, ct)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            DeleteDatasetOutcome.Success => Results.NoContent(),
            DeleteDatasetOutcome.NotFound => Results.NotFound(),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    })
         .WithSummary("Delete dataset (Story 8.6 — FR-58)")
         .RequireDatasetManagement();
    ```
  - [x] Update the file-header comment on line 15 to reflect Story 8.6 is now implemented:
    Change: `delete (8.6), list/get (8.7), preview (11.3).`
    To: `list/get (8.7), preview (11.3).`
    And update the opening comment line to add `Story 8.6 (FR-58) — DELETE /{id} removes the row + backing VIEW atomically.`

- [x] **Task 6 — Integration tests: `DatasetDeleteTests.cs`**
  - [x] Create `src/FormForge.Api.Tests/Features/Datasets/DatasetDeleteTests.cs`
  - [x] Use the **identical** `PostgresFixture` + `WebApplicationFactory<Program>` pattern as `DatasetUpdateTests.cs` (same `[Collection("DatasetIntegrationTests")]`, same `InitializeAsync` body including TRUNCATE + VIEW cleanup + ReseedAdminRoleAsync + SeedAdminUserAsync)
  - [x] **Test 1 — AC-1: Delete existing dataset → 204, row gone, VIEW gone**
    - Create a dataset via POST (assert 201, parse id and `datasetName`)
    - Assert VIEW exists via `information_schema.views` BEFORE delete
    - DELETE `/api/datasets/{id}` with auth → assert HTTP 204
    - Assert `custom_dataset` row is gone (SELECT COUNT(*) WHERE id = @id = 0)
    - Assert VIEW `datasets."{datasetName}"` no longer exists
  - [x] **Test 2 — AC-2: Delete non-existent id → 404**
    - DELETE `/api/datasets/{Guid.NewGuid()}` with auth → assert HTTP 404
    - Assert no `custom_dataset` rows were affected
  - [x] **Test 3 — AC-3: Audit log on success**
    - Create dataset `"audit_delete_ds"` via POST (version=1)
    - DELETE `/api/datasets/{id}` → assert 204
    - Query `dataset_audit_log WHERE dataset_name = 'audit_delete_ds' AND operation = 'DELETE'`
    - Assert 1 row:
      - `succeeded = true`
      - `operation = "DELETE"`
      - `ddl` contains `DROP VIEW IF EXISTS datasets."audit_delete_ds"`
      - `actor_id = adminUserId`
      - `timestamp` is populated (not `default(DateTimeOffset)`)
      - `previous_values` is non-null (contains the deleted row snapshot)
      - `new_values` is null

---

## Dev Notes

### §1 — Connection lifecycle in `DeleteAsync`

Like `UpdateAsync`, `DeleteAsync` needs the connection for both the initial SELECT (to capture the dataset name and row values for the audit log) and the subsequent transaction (DELETE + DROP VIEW). Open the connection once at the start and close it in the outer `finally`:

```csharp
var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
NpgsqlTransaction? tx = null;
bool succeeded = false;
try
{
    // SELECT outside transaction (read-committed)
    var current = ...;
    if (current is null)
        return new DeleteDatasetResult(DeleteDatasetOutcome.NotFound);

    // pre-build DDL
    var dropDdl = DatasetViewManager.BuildDropViewDdl(current.DatasetName);
    var now = DateTimeOffset.UtcNow;

    tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
    try
    {
        // DELETE row + DROP VIEW ...
        await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        succeeded = true;
    }
    catch (OperationCanceledException) { throw; }
    catch (NpgsqlException ex)
    {
        await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        LogViewDropFailed(logger, ex, current.DatasetName);
    }
    finally
    {
        if (tx is not null)
            await tx.DisposeAsync().ConfigureAwait(false);
    }

    // EF audit log write (after Dapper tx settled)
    // ...

    return new DeleteDatasetResult(
        succeeded ? DeleteDatasetOutcome.Success : DeleteDatasetOutcome.NotFound);
}
finally
{
    await conn.DisposeAsync().ConfigureAwait(false);
}
```

The early-return path (NotFound from null `current`) is inside the outer `try` block, so the outer `finally` still disposes the connection. No resource leak.

### §2 — NpgsqlException path from DropAsync

`DROP VIEW IF EXISTS` essentially never fails for a healthy PostgreSQL server. The `IF EXISTS` clause makes it idempotent — it succeeds even if the VIEW does not exist. A failure here would indicate a catastrophic database issue (permissions error, server down).

On this unlikely path, the row was NOT deleted (the transaction rolled back), so returning `DeleteDatasetOutcome.NotFound` from `DeleteAsync` would be misleading. Instead, re-throw or return a sentinel. Since the story only defines Success / NotFound outcomes, the simplest correct approach is to **not catch** non-unique-violation `NpgsqlException` and let it propagate — the endpoint's generic error handler will return 500.

However, to keep the audit-log write reachable (the failure DDL attempt should still be logged), catch `NpgsqlException` and track `succeeded = false`, then return `DeleteDatasetOutcome.NotFound` from the method and let the endpoint map that to 404. This is a pragmatic simplification — the practical failure surface of `DROP VIEW IF EXISTS` is essentially zero.

**Alternative (preferred):** add a `DdlFailure` outcome to `DeleteDatasetOutcome` and map it to 500 in the endpoint. Choose whichever approach is consistent with the project's tolerance for enum proliferation. Given the established pattern in `CreateDatasetOutcome` (which has `InvalidQuery`) and `UpdateDatasetOutcome` (which has `InvalidQuery`), a `DdlFailure` outcome is clean:

```csharp
internal enum DeleteDatasetOutcome { Success, NotFound, DdlFailure }
```

Map in endpoint:
```csharp
DeleteDatasetOutcome.DdlFailure => Results.StatusCode(StatusCodes.Status500InternalServerError),
```

This is the **recommended** approach to avoid the misleading NotFound return on a DB failure.

### §3 — No optimistic concurrency on DELETE

Story 8.5 requires the client to send `version` in the PUT body to detect concurrent edits (AR-60). **DELETE does not require `version` in the request body.** The AC does not mention concurrency conflict handling. If a concurrent PUT changes the row between the SELECT and the DELETE, the DELETE will still succeed (it deletes by `id`, not `id AND version`). This is the correct behavior — the spec says nothing about version-guarded deletes.

### §4 — No `DatasetName` value type needed

`DropAsync` takes a plain `string` for the dataset name (unlike `CreateAsync`/`ReplaceAsync` which take `DatasetName`). The name comes from `current.DatasetName`, which was read from the database — it is already a validated identifier stored there by prior `CreateAsync`/`UpdateAsync` paths. Double-quoting the identifier in the DDL string (`datasets."{datasetName}"`) is sufficient protection. This mirrors the `BuildRenameViewDdl(string oldName, string newName)` precedent in `DatasetViewManager`.

### §5 — `CurrentDatasetRow` record reuse

`CurrentDatasetRow` is already defined as a `private sealed record` inside `DatasetService` (added in Story 8.5). `DeleteAsync` reuses it directly — **do not define a second record**. The SELECT column aliases are the same as in `UpdateAsync`. If `CreatedAt` proves unnecessary for the audit log snapshot (the story only requires `previous_values` to be non-null), it can still be included since `CurrentDatasetRow` already maps it.

### §6 — `previous_values` content for DELETE audit

The audit log `previous_values` should capture the full row state at delete time, mirroring `UpdateAsync`'s pattern:
```csharp
var previousValues = JsonSerializer.Serialize(new
{
    dataset_name     = current.DatasetName,
    is_custom_query  = current.IsCustomQuery,
    query            = current.Query,
    builder_state    = current.BuilderState,
    version          = current.Version,
});
```

`new_values` is `null` for DELETE (there is no "after" state).

### §7 — Pre-existing test failures to ignore

Per memory:
- 2 failures: `SchemaAuditLogIntegrationTests` / `MutationAuditLogIntegrationTests` (audit DELETE → 405) — pre-existing, unrelated
- 1 failure: `i18n-lint.test.ts` (missing `designer.inspector.placeholders.label`) — pre-existing

No new i18n keys are added by this story. No new NuGet packages are required.

### §8 — No EF migration needed

All schema is in place from Story 8.1 (`custom_dataset`, `dataset_audit_log`, `datasets` PG schema). DELETE does not modify the DB schema.

### §9 — FluentValidation prohibition

Same rule as Stories 8.4 and 8.5: do **not** add the FluentValidation endpoint filter to the DELETE handler. The DELETE handler has no request body, so there is nothing to validate with FluentValidation.

### Project Structure — Modified Files

```
src/FormForge.Api/Features/Datasets/DatasetService.cs
  — add DeleteDatasetOutcome enum, DeleteDatasetResult record, extend IDatasetService
    with DeleteAsync, implement DeleteAsync, add LogViewDropped + LogViewDropFailed
    [LoggerMessage] methods

src/FormForge.Api/Features/Datasets/DatasetViewManager.cs
  — add BuildDropViewDdl static helper, DropAsync async method,
    LogViewDropped [LoggerMessage] method

src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs
  — replace the DELETE /{id:guid} stub with the real handler; update file-header comment
```

**New files:**
```
src/FormForge.Api.Tests/Features/Datasets/DatasetDeleteTests.cs   ← NEW (3 tests)
```

**No new EF Core migrations.**
**No new NuGet packages.**
**No frontend changes** (Story 8.10 adds the Dataset Management UI).

### References

- [Source: `_bmad-output/planning-artifacts/epics.md` — Epic 8, Story 8.6 ACs (FR-58, AR-59)]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.3 — AR-59: Transactional View Lifecycle (Delete: DELETE row + DROP VIEW IF EXISTS in one NpgsqlTransaction)]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.9 — Dataset API contract: DELETE /api/datasets/{id} → 204]
- [Source: `src/FormForge.Api/Features/Datasets/DatasetService.cs` — CreateAsync / UpdateAsync pattern: connection lifecycle, OperationCanceledException guard, audit try/catch, #pragma CA1031]
- [Source: `src/FormForge.Api/Features/Datasets/DatasetViewManager.cs` — CreateAsync / RenameAsync / ReplaceAsync as models for DropAsync; BuildRenameViewDdl(string, string) as model for BuildDropViewDdl(string)]
- [Source: `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs` — PUT handler as model for DELETE handler; actor extraction + Results.NoContent()]
- [Source: `src/FormForge.Api.Tests/Features/Datasets/DatasetUpdateTests.cs` — InitializeAsync TRUNCATE+VIEW cleanup, ReseedAdminRoleAsync, SeedAdminUserAsync, LoginAsync, CreateAsync, ViewExistsAsync, GetAuditEntriesAsync helper patterns]
- [Source: Story 8.5 §7 — pre-existing test failures to ignore (audit DELETE→405, i18n-lint)]

---

## Dev Agent Record

### Completion Notes

Implemented DELETE `/api/datasets/{id}` (FR-58 / AR-59): the `custom_dataset` row DELETE
and the backing `DROP VIEW IF EXISTS` execute inside a single `NpgsqlTransaction`, followed
by a best-effort EF audit-log write — mirroring the established `CreateAsync` / `UpdateAsync`
structure (shared connection lifecycle, `OperationCanceledException` guard, `#pragma CA1031`
on the audit write).

**Design decision — `DdlFailure` outcome (Dev Notes §2, recommended path):** the story's
Task 3 Step F sketch offered two options for the (essentially-impossible) `DROP VIEW IF EXISTS`
failure path. I adopted the **recommended** `DeleteDatasetOutcome.DdlFailure` enum value mapped
to HTTP 500, rather than the misleading `NotFound`/404 fallback. On that path the transaction
rolls back, the row survives, and a failed audit row is still written — so 500 (not 404) is the
honest status. This matches the enum-richness precedent in `CreateDatasetOutcome` /
`UpdateDatasetOutcome`.

**Other notes:**
- DELETE is intentionally **not** version-guarded (§3): it deletes by `id` alone; no `version`
  in the request body, no FluentValidation filter (§9 — there is no request body to validate).
- `BuildDropViewDdl(string)` takes a plain string (the name comes from the DB row, already a
  validated identifier), mirroring `BuildRenameViewDdl`'s precedent (§4).
- Reused the existing private `CurrentDatasetRow` record for the SELECT (§5) — no duplicate.
- `previous_values` captures the full deleted-row snapshot; `new_values` is null (§6).
- No EF migration, no NuGet packages, no frontend changes (Story 8.10 owns the UI).

**Validation:** API + test projects build with 0 warnings / 0 errors. All 3 new integration
tests pass. Full API suite: **865 passed, 2 failed** — the 2 failures are the pre-existing,
unrelated audit-DELETE→405 tests (`SchemaAuditLogIntegrationTests`,
`MutationAuditLogIntegrationTests`) documented in Story 8.5 §7; no regressions introduced.

### File List

**Modified:**
- `src/FormForge.Api/Features/Datasets/DatasetService.cs` — added `DeleteDatasetOutcome` enum,
  `DeleteDatasetResult` record, extended `IDatasetService` with `DeleteAsync`, implemented
  `DeleteAsync`, added `LogViewDropped` + `LogViewDropFailed` `[LoggerMessage]` methods.
- `src/FormForge.Api/Features/Datasets/DatasetViewManager.cs` — added `BuildDropViewDdl` static
  helper, `DropAsync` method, `LogViewDropped` `[LoggerMessage]` method; updated header comment.
- `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs` — replaced the DELETE `/{id:guid}`
  stub with the real handler; updated the file-header comment.

**New:**
- `src/FormForge.Api.Tests/Features/Datasets/DatasetDeleteTests.cs` — 3 integration tests
  (AC-1 204 + row/VIEW removed, AC-2 404, AC-3 success audit row).

### Review Findings

- [x] [Review][Patch] Double `LogViewDropped` call on success path — `DatasetViewManager.DropAsync` already logs after the DDL executes; `DatasetService.DeleteAsync` logs again at line 553 after `tx.CommitAsync`, emitting two identical "Dropped VIEW datasets…" lines on every success. Remove the redundant call from the service. [`DatasetService.cs`]
- [x] [Review][Patch] `DELETE` rowsAffected never checked — race yields false 204 — `conn.ExecuteAsync(deleteSql, ...)` return value is discarded; a concurrent delete between the outer SELECT and the inner DELETE commits with 0 rows but returns Success → 204. Capture `rowsAffected` and return `NotFound` if zero (mirrors `UpdateAsync`). [`DatasetService.cs`]
- [x] [Review][Defer] No test for `DdlFailure` → HTTP 500 — Task 6 defines exactly 3 tests (AC-1/2/3); the `DdlFailure` path requires injecting `NpgsqlException` from `DropAsync` which is essentially unreachable in practice and not mandated by spec. [`DatasetDeleteTests.cs`] — deferred, pre-existing
- [x] [Review][Defer] No test for unauthenticated/unauthorized DELETE — `RequireDatasetManagement()` auth coverage not verified in this story's tests; consistent with existing endpoint test patterns and not required by Task 6. [`DatasetDeleteTests.cs`] — deferred, pre-existing

### Change Log

- 2026-06-03 — Story 8.6 implemented: DELETE `/api/datasets/{id}` removes the row + backing
  VIEW atomically (DELETE + DROP VIEW IF EXISTS in one NpgsqlTransaction), writes a DELETE
  audit entry, returns 204 / 404 / 500. Added 3 integration tests. Status → review.
