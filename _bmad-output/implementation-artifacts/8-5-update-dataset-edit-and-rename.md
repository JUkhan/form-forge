# Story 8.5: Update Dataset (Edit & Rename)

Status: done

## Story

As a user with the `dataset-management` permission,
I want to update an existing Dataset's `dataset_name`, `query`, or `builder_state`,
so that the row and its backing PostgreSQL VIEW are updated atomically with rollback safety.

## Acceptance Criteria

**AC-1 — Version mismatch returns 409 DATASET_CONCURRENCY_CONFLICT**
**Given** I PUT `/api/datasets/{id}` with `{ version: N }` where `N` does not match the current DB `version`
**When** the server reads the current row
**Then** HTTP 409 is returned with `{ code: "DATASET_CONCURRENCY_CONFLICT", currentVersion: M }` where `M` is the current DB version (per FR-59 / AR-60)
**And** no update is applied to the row or VIEW

**AC-2 — Edit same name: CREATE OR REPLACE VIEW + version increment**
**Given** I PUT `/api/datasets/{id}` with `{ version: N, dataset_name: "same_name", query: "SELECT 2 AS n" }` where `N` matches the current version
**When** the transaction executes (per AR-59)
**Then** within one NpgsqlTransaction:
  - `UPDATE custom_dataset SET query=..., updated_at=now(), updated_by=@actorId, version=N+1 WHERE id=@id AND version=@N` succeeds (1 row affected)
  - `CREATE OR REPLACE VIEW datasets."same_name" AS SELECT 2 AS n` executes
**And** HTTP 200 is returned with the updated `DatasetDto` including `version = N+1`
**And** the VIEW definition at `datasets."same_name"` reflects the new query

**AC-3 — Rename: ALTER VIEW RENAME + version increment**
**Given** I PUT `/api/datasets/{id}` with `{ version: N, dataset_name: "new_name" }` where `dataset_name` differs from the current value `"old_name"`
**When** the transaction executes (per AR-59 / AD-17 resolved)
**Then** within one NpgsqlTransaction:
  - `UPDATE custom_dataset SET dataset_name="new_name", version=N+1 ... WHERE id=@id AND version=@N` succeeds
  - `ALTER VIEW datasets."old_name" RENAME TO "new_name"` executes
**And** HTTP 200 is returned with `DatasetDto.dataset_name = "new_name"` and `version = N+1`
**And** the VIEW at `datasets."old_name"` no longer exists; `datasets."new_name"` exists

**AC-4 — Rename + query change: RENAME then REPLACE in same transaction**
**Given** I PUT `/api/datasets/{id}` with `{ version: N, dataset_name: "renamed_ds", query: "SELECT 3 AS x" }` where both name and query differ from current values
**When** the transaction executes
**Then** within one NpgsqlTransaction:
  - Row is updated (new name, new query, version N+1)
  - `ALTER VIEW datasets."old_name" RENAME TO "renamed_ds"` executes
  - `CREATE OR REPLACE VIEW datasets."renamed_ds" AS SELECT 3 AS x` executes
**And** HTTP 200 with updated `DatasetDto`
**And** `datasets."renamed_ds"` contains the new query definition

**AC-5 — Null query becomes placeholder (same rule as create)**
**Given** I PUT `/api/datasets/{id}` with `{ version: N, query: null }` (or field omitted, keeping existing null)
**When** the existing `query` is already `NULL` (builder dataset)
**Then** the placeholder VIEW `SELECT 1 AS placeholder` is used as the effective query on REPLACE
**And** the stored `query` column remains `NULL`

**AC-6 — Partial update: omitted fields keep existing values**
**Given** I PUT `/api/datasets/{id}` with `{ version: N, query: "SELECT 5 AS x" }` and no `dataset_name`
**When** the update runs
**Then** `dataset_name`, `is_custom_query`, and `builder_state` retain their current DB values
**And** only `query`, `version`, `updated_at`, `updated_by` are changed

**AC-7 — Duplicate name returns 409 DATASET_NAME_CONFLICT**
**Given** Dataset `"alpha"` already exists and I PUT `/api/datasets/{id_of_beta}` with `{ version: N, dataset_name: "alpha" }`
**When** the UPDATE fires and hits the `idx_custom_dataset_dataset_name` UNIQUE constraint (23505)
**Then** the transaction rolls back — the row and VIEW are unchanged
**And** HTTP 409 is returned with `{ code: "DATASET_NAME_CONFLICT" }`
**And** no audit log entry is written (same rule as Story 8.4 AC-6 note)

**AC-8 — VIEW DDL failure returns 422 INVALID_QUERY and rolls back**
**Given** I PUT `/api/datasets/{id}` with `{ version: N, query: "NOT VALID SQL" }`
**When** the server attempts `CREATE OR REPLACE VIEW datasets."name" AS NOT VALID SQL`
**Then** PostgreSQL rejects it with a syntax error; the transaction rolls back
**And** the existing VIEW and row are left intact at their prior state
**And** HTTP 422 is returned with `{ code: "INVALID_QUERY" }` and the PostgreSQL error in `detail`

**AC-9 — Not found returns 404**
**Given** I PUT `/api/datasets/{id}` with a UUID that does not exist in `custom_dataset`
**When** the fetch runs
**Then** HTTP 404 is returned

**AC-10 — Audit log: success path**
**Given** any successful update (AC-2, AC-3, or AC-4) commits
**When** I query `dataset_audit_log`
**Then** one new row exists with `operation = 'UPDATE'`, `actor_id`, `dataset_name` (the **new** name), `succeeded = true`, `ddl` (the primary DDL executed), `correlation_id`, `previous_values` (JSON of old values), `new_values` (JSON of updated values), `timestamp`

**AC-11 — Audit log: failure path (DDL failure)**
**Given** a VIEW DDL failure rolls back (AC-8)
**When** I query `dataset_audit_log`
**Then** one new row exists with `operation = 'UPDATE'`, `succeeded = false`, the attempted DDL string, and the relevant identifiers

---

## Tasks / Subtasks

- [x] **Task 1 — Add `UpdateDatasetOutcome` enum and result type to `DatasetService.cs`** (AC-1 through AC-11)
  - [x] At the top of `src/FormForge.Api/Features/Datasets/DatasetService.cs`, add alongside `CreateDatasetOutcome`:
    ```csharp
    internal enum UpdateDatasetOutcome { Success, NotFound, ConcurrencyConflict, NameConflict, InvalidQuery }

    internal sealed record UpdateDatasetResult(
        UpdateDatasetOutcome Outcome,
        DatasetDto? Dataset = null,
        string? ErrorDetail = null,
        int? CurrentVersion = null);
    ```

- [x] **Task 2 — Extend `IDatasetService` with `UpdateAsync`** (AC-1 through AC-11)
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
            DatasetName? newName,        // null if dataset_name was not provided in request
            Guid actorId,
            string? correlationId,
            CancellationToken ct);
    }
    ```
  - [x] `newName` is `null` when `request.DatasetName` was omitted (caller passes `null` when the field was null in the request — no validation needed, keep existing)

- [x] **Task 3 — Implement `UpdateAsync` in `DatasetService`** (all ACs)
  - [x] Implementation steps (full pattern — mirror `CreateAsync` style):

    **Step A: Resolve actor name**
    ```csharp
    var actorName = await db.Users
        .Where(u => u.Id == actorId)
        .Select(u => u.DisplayName)
        .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    ```

    **Step B: Load current row via Dapper**
    ```csharp
    const string selectSql = """
        SELECT id, dataset_name, is_custom_query, query, builder_state, version,
               created_at, created_by
        FROM custom_dataset
        WHERE id = @id
        """;
    var current = await conn.QuerySingleOrDefaultAsync<CurrentDatasetRow>(
        new CommandDefinition(selectSql, new { id = datasetId },
            commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);
    if (current is null) return new UpdateDatasetResult(UpdateDatasetOutcome.NotFound);
    ```
    Use a private `record CurrentDatasetRow(Guid Id, string DatasetName, bool IsCustomQuery, string? Query, string? BuilderState, int Version, DateTimeOffset CreatedAt, Guid? CreatedBy)` to map the Dapper result.

    **Step C: Version check (before opening a transaction)**
    ```csharp
    if (current.Version != request.Version)
        return new UpdateDatasetResult(UpdateDatasetOutcome.ConcurrencyConflict,
            CurrentVersion: current.Version);
    ```

    **Step D: Compute effective new values**
    ```csharp
    var effectiveNewName    = newName?.Value ?? current.DatasetName;
    var effectiveNewQuery   = request.Query is not null ? request.Query : current.Query;
    var effectiveBuilderState = request.BuilderState is not null ? request.BuilderState : current.BuilderState;
    var effectiveIsCustomQuery = request.IsCustomQuery ?? current.IsCustomQuery;
    var isRename           = effectiveNewName != current.DatasetName;
    var effectiveViewQuery = string.IsNullOrWhiteSpace(effectiveNewQuery)
                             ? "SELECT 1 AS placeholder" : effectiveNewQuery;
    var now = DateTimeOffset.UtcNow;
    ```

    **Step E: Pre-build DDL for audit log**
    - Rename only or rename + query change: `ALTER VIEW datasets."{oldName}" RENAME TO "{newName}"`
    - If also query changed (or always when not rename): `CREATE OR REPLACE VIEW datasets."{effectiveName}" AS {effectiveViewQuery}`
    - For the audit log `ddl` field, record the rename DDL if rename occurred, otherwise the REPLACE DDL.
    - If both rename and query change: record the rename DDL (primary operation; the replace is secondary).

    **Step F: Transaction — same disposal pattern as `CreateAsync`**
    ```csharp
    const string updateSql = """
        UPDATE custom_dataset
        SET dataset_name     = @newName,
            is_custom_query  = @isCustomQuery,
            query            = @query,
            builder_state    = @builderState,
            version          = version + 1,
            updated_at       = @now,
            updated_by       = @updatedBy
        WHERE id = @id AND version = @expectedVersion
        """;
    var rowsAffected = await conn.ExecuteAsync(new CommandDefinition(
        updateSql,
        new {
            newName         = effectiveNewName,
            isCustomQuery   = effectiveIsCustomQuery,
            query           = string.IsNullOrWhiteSpace(effectiveNewQuery)
                              ? (object?)DBNull.Value : effectiveNewQuery,
            builderState    = effectiveBuilderState is null ? (object?)DBNull.Value : effectiveBuilderState,
            now,
            updatedBy       = (object?)actorId,
            id              = datasetId,
            expectedVersion = request.Version,
        },
        transaction: tx, commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

    if (rowsAffected == 0)
    {
        await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        // Re-read current version for the 409 response.
        // (Rare path — version was correct when we read it but changed before UPDATE.)
        // Return ConcurrencyConflict with the version we read earlier (still accurate for the caller).
        return new UpdateDatasetResult(UpdateDatasetOutcome.ConcurrencyConflict,
            CurrentVersion: current.Version);
    }
    ```
    - **Catch 23505 from UPDATE** (name conflict):
      ```csharp
      catch (PostgresException pg) when (
          pg.SqlState == PostgresErrorCodes.UniqueViolation && pg.TableName == "custom_dataset")
      {
          await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
          return new UpdateDatasetResult(UpdateDatasetOutcome.NameConflict);
      }
      ```
    - **If rename**: call `viewManager.RenameAsync(conn, tx, oldDatasetName, effectiveNewName, ct)`
    - **If query changed OR not rename**: call `viewManager.ReplaceAsync(conn, tx, new DatasetName value for effectiveNewName, effectiveViewQuery, ct)`
    - **If rename AND query changed**: do both RENAME then REPLACE in the same tx
    - **Catch NpgsqlException for DDL failures** → rollback, set `pgErrorDetail`
    - Commit with `CancellationToken.None`

    **Step G: Audit log via EF (after Dapper tx)**
    ```csharp
    var previousValues = JsonSerializer.Serialize(new
    {
        dataset_name     = current.DatasetName,
        is_custom_query  = current.IsCustomQuery,
        query            = current.Query,
        builder_state    = current.BuilderState,
        version          = current.Version,
    });
    var newValues = succeeded
        ? JsonSerializer.Serialize(new
        {
            dataset_name     = effectiveNewName,
            is_custom_query  = effectiveIsCustomQuery,
            query            = string.IsNullOrWhiteSpace(effectiveNewQuery) ? null : effectiveNewQuery,
            builder_state    = effectiveBuilderState,
            version          = request.Version + 1,
        })
        : null;

    db.DatasetAuditLog.Add(new DatasetAuditLogEntry
    {
        DatasetName    = effectiveNewName,   // new name (or unchanged name) goes in the audit row
        Operation      = "UPDATE",
        ActorId        = actorId,
        ActorName      = actorName,
        PreviousValues = previousValues,
        NewValues      = newValues,
        Ddl            = primaryDdl,         // the rename DDL if rename; otherwise the replace DDL
        Succeeded      = succeeded,
        CorrelationId  = correlationId,
        Timestamp      = now,
    });
    // Same try/catch for audit write failure as CreateAsync.
    ```

    **Step H: Return result**
    ```csharp
    if (succeeded)
    {
        var dto = new DatasetDto(
            Id:            datasetId,
            DatasetName:   effectiveNewName,
            IsCustomQuery: effectiveIsCustomQuery,
            Query:         string.IsNullOrWhiteSpace(effectiveNewQuery) ? null : effectiveNewQuery,
            BuilderState:  effectiveBuilderState,
            Version:       request.Version + 1,
            CreatedAt:     current.CreatedAt,
            CreatedBy:     current.CreatedBy);
        return new UpdateDatasetResult(UpdateDatasetOutcome.Success, Dataset: dto);
    }
    return new UpdateDatasetResult(UpdateDatasetOutcome.InvalidQuery, ErrorDetail: pgErrorDetail);
    ```

  - [x] Add `[LoggerMessage]` methods for the new log calls (CA1848 is an error in this project):
    ```csharp
    [LoggerMessage(Level = LogLevel.Information, Message = "Updated VIEW datasets.\"{Name}\" (rename: {IsRename})")]
    private static partial void LogViewUpdated(ILogger logger, string name, bool isRename);

    [LoggerMessage(Level = LogLevel.Warning, Message = "UPDATE VIEW failed for dataset {Name}")]
    private static partial void LogViewUpdateFailed(ILogger logger, Exception ex, string name);
    ```

  - [x] **IMPORTANT**: The `conn` and `tx` must be opened **before** the Dapper SELECT (Step B), so all operations share the same connection object. The connection should be opened once at the top of `UpdateAsync`, used for the SELECT and then the transaction, then disposed in `finally`. See §1 for the connection lifecycle pattern.

- [x] **Task 4 — Add `ReplaceAsync` and `RenameAsync` to `DatasetViewManager`** (AC-2, AC-3, AC-4)
  - [x] In `src/FormForge.Api/Features/Datasets/DatasetViewManager.cs`, add:

    ```csharp
    internal static string BuildReplaceViewDdl(DatasetName name, string effectiveQuery) =>
        $"""CREATE OR REPLACE VIEW datasets."{name.Value}" AS {effectiveQuery}""";

    internal static string BuildRenameViewDdl(string oldName, string newName) =>
        $"""ALTER VIEW datasets."{oldName}" RENAME TO "{newName}" """;

    internal async Task ReplaceAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        DatasetName name,
        string effectiveQuery,
        CancellationToken ct)
    {
        var ddl = BuildReplaceViewDdl(name, effectiveQuery);
        await conn.ExecuteAsync(new CommandDefinition(
            ddl,
            transaction: tx,
            commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds,
            cancellationToken: ct)).ConfigureAwait(false);
        LogViewReplaced(logger, name.Value);
    }

    internal async Task RenameAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string oldName,
        string newName,
        CancellationToken ct)
    {
        var ddl = BuildRenameViewDdl(oldName, newName);
        await conn.ExecuteAsync(new CommandDefinition(
            ddl,
            transaction: tx,
            commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds,
            cancellationToken: ct)).ConfigureAwait(false);
        LogViewRenamed(logger, oldName, newName);
    }
    ```
  - [x] Add `[LoggerMessage]` methods:
    ```csharp
    [LoggerMessage(Level = LogLevel.Information, Message = "Replaced VIEW datasets.\"{Name}\"")]
    private static partial void LogViewReplaced(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Renamed VIEW datasets.\"{OldName}\" → \"{NewName}\"")]
    private static partial void LogViewRenamed(ILogger logger, string oldName, string newName);
    ```

- [x] **Task 5 — Replace the PUT stub in `DatasetEndpoints.cs`** (all ACs)
  - [x] In `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs`, replace the stub PUT handler:
    ```csharp
    group.MapPut("/{id:guid}", async (
        Guid id,
        [FromBody] UpdateDatasetRequest request,
        IDatasetService datasetService,
        HttpContext httpContext,
        CancellationToken ct) =>
    {
        // Validate dataset_name only if it was provided in the request body.
        DatasetName? newName = null;
        if (request.DatasetName is not null)
        {
            if (!DatasetName.TryCreate(request.DatasetName, out newName, out _, out var nameError))
                return InvalidDatasetName(nameError);
        }

        if (!Guid.TryParse(httpContext.User.FindFirst("userId")?.Value, out var actorId)
            || actorId == Guid.Empty)
        {
            return Results.Unauthorized();
        }

        var correlationId = httpContext.GetCorrelationId();
        var result = await datasetService
            .UpdateAsync(id, request, newName, actorId, correlationId, ct)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            UpdateDatasetOutcome.Success => Results.Ok(result.Dataset),
            UpdateDatasetOutcome.NotFound => Results.NotFound(),
            UpdateDatasetOutcome.ConcurrencyConflict => Results.Problem(
                detail: $"Version mismatch. Current version is {result.CurrentVersion}.",
                title: "Dataset concurrency conflict",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "DATASET_CONCURRENCY_CONFLICT",
                    ["messageKey"] = "datasets.concurrencyConflict",
                    ["currentVersion"] = result.CurrentVersion,
                }),
            UpdateDatasetOutcome.NameConflict => Results.Problem(
                detail: $"A dataset named '{request.DatasetName}' already exists.",
                title: "Dataset name conflict",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "DATASET_NAME_CONFLICT",
                    ["messageKey"] = "datasets.nameConflict",
                }),
            UpdateDatasetOutcome.InvalidQuery => Results.Problem(
                detail: result.ErrorDetail,
                title: "Invalid query",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "INVALID_QUERY",
                    ["messageKey"] = "datasets.invalidQuery",
                }),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    })
         .WithSummary("Update dataset (Story 8.5 — FR-58 / FR-59)")
         .RequireDatasetManagement();
    ```
  - [x] **No change needed to Program.cs** — `IDatasetService` / `DatasetService` and `DatasetViewManager` are already registered (Story 8.4). `UpdateAsync` is on the same interface/class.

- [x] **Task 6 — Add i18n key for new error code** (AC-1)
  - [x] In `web/src/lib/i18n/locales/en.json`, under the `"datasets"` key, add:
    ```json
    "concurrencyConflict": "This dataset was modified by someone else. Reload to see the latest version."
    ```
  - [x] The `datasets` block after this story:
    ```json
    "datasets": {
      "invalidDatasetName": "...",
      "nameConflict": "...",
      "invalidQuery": "...",
      "concurrencyConflict": "This dataset was modified by someone else. Reload to see the latest version.",
      "validation": { ... }
    }
    ```

- [x] **Task 7 — Integration tests: `DatasetUpdateTests.cs`** (AC-1 through AC-11)
  - [x] Create `src/FormForge.Api.Tests/Features/Datasets/DatasetUpdateTests.cs`
  - [x] Use the same `PostgresFixture` + `WebApplicationFactory<Program>` pattern as `DatasetViewLifecycleTests.cs`
  - [x] `InitializeAsync`: migrate, TRUNCATE `custom_dataset, dataset_audit_log, role_permissions, user_roles, roles, refresh_tokens, users RESTART IDENTITY CASCADE`, reseed admin role + user, drop any leftover `datasets` schema VIEWs with a safe cleanup query, login for JWT
  - [x] **Test 1 — AC-1: Version mismatch → 409 DATASET_CONCURRENCY_CONFLICT**
    - Create a dataset (POST → 201, parse id + returned version=1)
    - PUT with `{ version: 99, query: "SELECT 1" }` → assert HTTP 409
    - Assert `code = "DATASET_CONCURRENCY_CONFLICT"` at JSON root
    - Assert `currentVersion = 1` in the JSON response
    - Assert row in DB still has `version = 1` (no change occurred)
  - [x] **Test 2 — AC-2: Edit same name, query change → 200, VIEW definition updated**
    - Create dataset with `query = "SELECT 1 AS n"` (version=1)
    - PUT with `{ version: 1, dataset_name: "<same>", query: "SELECT 2 AS m" }` → assert HTTP 200
    - Assert response `query = "SELECT 2 AS m"`, `version = 2`
    - Verify VIEW definition via `pg_get_viewdef`: contains `2` (the new column expression)
    - Verify DB row has `version = 2`, `updated_at` is non-null, `updated_by = adminUserId`
  - [x] **Test 3 — AC-3: Rename only → 200, old VIEW gone, new VIEW exists**
    - Create dataset `"original_name"` (version=1)
    - PUT with `{ version: 1, dataset_name: "renamed_name" }` → assert HTTP 200
    - Assert response `dataset_name = "renamed_name"`, `version = 2`
    - Assert VIEW `datasets."original_name"` does NOT exist (check `information_schema.views`)
    - Assert VIEW `datasets."renamed_name"` DOES exist
  - [x] **Test 4 — AC-4: Rename + query change → 200, new name with new definition**
    - Create dataset `"before_rename"` with `query = "SELECT 1 AS a"` (version=1)
    - PUT with `{ version: 1, dataset_name: "after_rename", query: "SELECT 99 AS z" }` → assert HTTP 200
    - Assert response `dataset_name = "after_rename"`, `query = "SELECT 99 AS z"`, `version = 2`
    - Assert VIEW `datasets."before_rename"` does NOT exist
    - Assert VIEW `datasets."after_rename"` exists and definition contains `99`
  - [x] **Test 5 — AC-6: Partial update (only query, no name field) → existing name preserved**
    - Create dataset `"partial_test"` with `query = "SELECT 1"` (version=1)
    - PUT with `{ version: 1, query: "SELECT 7 AS q" }` (no `dataset_name` field) → assert HTTP 200
    - Assert `dataset_name = "partial_test"` in response (unchanged)
    - Assert VIEW `datasets."partial_test"` exists with updated definition
  - [x] **Test 6 — AC-7: Duplicate name → 409 DATASET_NAME_CONFLICT, row unchanged**
    - Create dataset `"alpha_ds"` (version=1)
    - Create dataset `"beta_ds"` (version=1)
    - PUT `/api/datasets/{beta_id}` with `{ version: 1, dataset_name: "alpha_ds" }` → assert HTTP 409
    - Assert `code = "DATASET_NAME_CONFLICT"` at JSON root
    - Assert DB `beta_ds` row still has `dataset_name = "beta_ds"`, `version = 1`
    - Assert VIEW `datasets."beta_ds"` still exists (was not dropped)
  - [x] **Test 7 — AC-8: Invalid SQL → 422 INVALID_QUERY, row rolled back**
    - Create dataset with `query = "SELECT 1"` (version=1)
    - PUT with `{ version: 1, query: "ABSOLUTELY NOT VALID SQL !!! @@@" }` → assert HTTP 422
    - Assert `code = "INVALID_QUERY"` at JSON root
    - Assert `detail` is non-null
    - Assert DB row still has `version = 1`, old query (no partial update)
    - Assert VIEW still reflects the original definition
  - [x] **Test 8 — AC-9: Not found → 404**
    - PUT `/api/datasets/{Guid.NewGuid()}` with `{ version: 1 }` → assert HTTP 404
  - [x] **Test 9 — AC-10: Audit log success entry**
    - Create `"audit_update_ds"` (version=1)
    - PUT with `{ version: 1, query: "SELECT 42 AS audit_col" }` → 200
    - Query `dataset_audit_log WHERE dataset_name = 'audit_update_ds' AND operation = 'UPDATE'`
    - Assert 1 row: `succeeded = true`, `ddl` contains `CREATE OR REPLACE VIEW`, `actor_id = adminUserId`, `correlation_id` non-null, `previous_values` contains `"version":1`, `new_values` contains `"version":2`
  - [x] **Test 10 — AC-11: Audit log failure entry**
    - Create `"audit_fail_ds"` (version=1)
    - PUT with bad SQL → 422
    - Query `dataset_audit_log WHERE dataset_name = 'audit_fail_ds' AND operation = 'UPDATE'`
    - Assert 1 row: `succeeded = false`, `ddl` contains `CREATE OR REPLACE VIEW datasets."audit_fail_ds"`, `actor_id = adminUserId`

---

## Dev Notes

### §1 — Connection lifecycle in `UpdateAsync`

Unlike `CreateAsync` (which only uses the connection inside the transaction), `UpdateAsync` needs the connection for the initial SELECT (to load the current row) AND for the subsequent transaction (UPDATE + DDL). Open the connection once at the start and close it in the outer `finally`:

```csharp
var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
NpgsqlTransaction? tx = null;
bool succeeded = false;
string? pgErrorDetail = null;
try
{
    // Step B — SELECT (outside transaction, read-committed)
    var current = await conn.QuerySingleOrDefaultAsync<CurrentDatasetRow>(
        new CommandDefinition(selectSql, new { id = datasetId },
            commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);
    if (current is null)
        return new UpdateDatasetResult(UpdateDatasetOutcome.NotFound);

    // Step C — version check (before touching the DB again)
    if (current.Version != request.Version)
        return new UpdateDatasetResult(UpdateDatasetOutcome.ConcurrencyConflict,
            CurrentVersion: current.Version);

    // Steps D/E — compute effective values and DDL strings

    tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
    try
    {
        // UPDATE row + VIEW DDL ...
        await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        succeeded = true;
    }
    catch (OperationCanceledException)
    {
        throw;  // Do not write audit on cancellation
    }
    catch (PostgresException pg) when (
        pg.SqlState == PostgresErrorCodes.UniqueViolation && pg.TableName == "custom_dataset")
    {
        await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        return new UpdateDatasetResult(UpdateDatasetOutcome.NameConflict);
    }
    catch (NpgsqlException ex)
    {
        await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        pgErrorDetail = ex.Message;
        LogViewUpdateFailed(logger, ex, effectiveNewName);
    }
    finally
    {
        if (tx is not null)
            await tx.DisposeAsync().ConfigureAwait(false);
    }
}
finally
{
    await conn.DisposeAsync().ConfigureAwait(false);
}
// ... audit log, result
```

**Early return paths** (NotFound, ConcurrencyConflict after SELECT, NameConflict): the connection is disposed by the outer `finally` even when these early returns happen from inside the `try`. This is correct because the `return` statements are inside the `try` block; the `finally` always runs.

**Why NOT do the SELECT inside the transaction?** Version conflict detection doesn't need serializable isolation — a read-committed SELECT then version-checked UPDATE (`WHERE id = @id AND version = @v`) is correct. If a concurrent write changes the version between our SELECT and the UPDATE, the UPDATE affects 0 rows and returns `ConcurrencyConflict`. No lost-update risk.

### §2 — Handling the "both rename AND query change" case

When `isRename && queryChanged` (where `queryChanged = request.Query is not null && request.Query != current.Query`):

1. Execute `ALTER VIEW datasets."{oldName}" RENAME TO "{newName}"` first
2. Execute `CREATE OR REPLACE VIEW datasets."{newName}" AS {effectiveViewQuery}` second

Both run inside the same `NpgsqlTransaction`. If the RENAME succeeds but the REPLACE fails, the whole transaction rolls back — the VIEW is still at its original name with its original definition.

For the audit log `ddl` field, record both DDL strings concatenated: `"<rename DDL>\n<replace DDL>"`. This gives the admin visibility into exactly what was attempted.

```csharp
var primaryDdl = isRename
    ? DatasetViewManager.BuildRenameViewDdl(current.DatasetName, effectiveNewName)
    : DatasetViewManager.BuildReplaceViewDdl(resolvedNewDatasetName, effectiveViewQuery);

// In the transaction:
if (isRename)
{
    await viewManager.RenameAsync(conn, tx, current.DatasetName, effectiveNewName, ct).ConfigureAwait(false);
    if (queryChanged)
    {
        var replaceDdl = DatasetViewManager.BuildReplaceViewDdl(resolvedNewDatasetName, effectiveViewQuery);
        primaryDdl = primaryDdl + "\n" + replaceDdl;
        await viewManager.ReplaceAsync(conn, tx, resolvedNewDatasetName, effectiveViewQuery, ct).ConfigureAwait(false);
    }
}
else
{
    await viewManager.ReplaceAsync(conn, tx, resolvedNewDatasetName, effectiveViewQuery, ct).ConfigureAwait(false);
}
```

Here `resolvedNewDatasetName` is a `DatasetName` instance — construct it from `effectiveNewName` using `DatasetName.TryCreate(effectiveNewName, ...)`. Since the string comes from either the validated `newName?.Value` (already validated) or `current.DatasetName` (already in the DB), validation will always pass.

**Alternate simpler approach**: If `isRename` is `true`, always do RENAME + REPLACE (even if only the name changed and query is identical). This is safe because `CREATE OR REPLACE VIEW` with the same query is a no-op effectively. But it adds an extra unnecessary DDL statement. The more correct approach branches as above.

### §3 — `DatasetName` value type for effectiveNewName

When the effective new name comes from `current.DatasetName` (no rename requested), you still need a `DatasetName` instance to call `ReplaceAsync`. Create it safely:

```csharp
// current.DatasetName is already validated (it's in the DB) — this will never fail.
DatasetName.TryCreate(effectiveNewName, out var resolvedNewDatasetName, out _, out _);
// resolvedNewDatasetName is non-null here.
```

### §4 — `UpdateDatasetRequest.Version` minimum-value guard

Story 8.3's code review included a deferred finding: "consider adding `[Range(1, int.MaxValue)]` to `Version` in `UpdateDatasetRequest`." This story resolves that deferral. However, given that Story 8.3's validator is registered but the FV filter is NOT wired to these endpoints, adding `[Range]` data annotations would have no effect. The version check in `UpdateAsync` already handles `version = 0` (the DB always starts at version 1, so 0 will always return `ConcurrencyConflict`). **Do not add the FV filter to the PUT endpoint** — same prohibition as for POST (see Story 8.3 review finding / Story 8.4 §7).

If an additional application-level guard is desired, add it inline in the endpoint handler:
```csharp
if (request.Version < 1)
    return Results.Problem(detail: "version must be ≥ 1", statusCode: 422,
        extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            { ["code"] = "INVALID_VERSION" });
```
This is optional — the `ConcurrencyConflict` path covers it functionally.

### §5 — Dapper mapping for `CurrentDatasetRow`

Define the private record inside `DatasetService` (namespace-private) so it doesn't pollute the public surface:

```csharp
private sealed record CurrentDatasetRow(
    Guid Id,
    string DatasetName,
    bool IsCustomQuery,
    string? Query,
    string? BuilderState,
    int Version,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy);
```

Dapper uses constructor injection when all parameter names match column names (snake_case → camelCase via Dapper's built-in mapping). The SELECT column aliases must match: `dataset_name` → Dapper maps to `DatasetName` ✓, `is_custom_query` → `IsCustomQuery` ✓, `builder_state` → `BuilderState` ✓, `created_at` → `CreatedAt` ✓, `created_by` → `CreatedBy` ✓.

Verify this works with the existing Dapper configuration in the project — prior stories use Dapper without a `DefaultTypeMap.MatchNamesWithUnderscores = true` setting. If snake_case auto-mapping is NOT globally configured, add column aliases to the SELECT:
```sql
SELECT id, dataset_name AS "DatasetName", is_custom_query AS "IsCustomQuery",
       query AS "Query", builder_state AS "BuilderState", version AS "Version",
       created_at AS "CreatedAt", created_by AS "CreatedBy"
FROM custom_dataset WHERE id = @id
```

Check the existing Dapper usage in `DynamicDataEndpoints.cs` or `DatasetService.cs` to confirm the convention.

### §6 — What Story 8.5 does NOT implement

| Feature | Target Story |
|---------|-------------|
| DELETE Dataset | 8.6 |
| LIST / GET datasets | 8.7 |
| SELECT-only enforcement via `PgQuery.NET` | 8.8 |
| Dataset audit log admin view | 8.9 |
| Dataset Management UI | 8.10 |
| `builder_state` semantic validation | Epic 11 |

**Note on `isCustomQuery` flag**: The `UpdateDatasetRequest.IsCustomQuery` is nullable (`bool?`). When `null`, the existing value is preserved. When `false` is explicitly sent (switching to Builder mode), the stored `is_custom_query` column is set to `false`. The VIEW is still updated to the effective query/placeholder (builder datasets get `SELECT 1 AS placeholder` until the builder state generates SQL). This story does NOT implement any mode-switch semantic validation — that's Story 8.10.

### §7 — Pre-existing test failures to ignore

Per memory:
- 2 failures: `SchemaAuditLogIntegrationTests` / `MutationAuditLogIntegrationTests` (audit DELETE → 405) — pre-existing, unrelated
- 1 failure: `i18n-lint.test.ts` (missing `designer.inspector.placeholders.label`) — pre-existing

The `"datasets.concurrencyConflict"` key added to `en.json` is consumed by the frontend only in Story 8.10, so `i18n-lint` will not report a new failure (the lint reports missing keys used in components, not orphan keys in the translation file).

### Project Structure Notes

**Modified files:**
```
src/FormForge.Api/Features/Datasets/DatasetService.cs
  — add UpdateDatasetOutcome enum, UpdateDatasetResult record, extend IDatasetService,
    implement UpdateAsync, add CurrentDatasetRow private record, add LoggerMessage methods

src/FormForge.Api/Features/Datasets/DatasetViewManager.cs
  — add BuildReplaceViewDdl, BuildRenameViewDdl static helpers,
    ReplaceAsync and RenameAsync async methods, LoggerMessage methods

src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs
  — replace the PUT /{id:guid} stub with the real handler

web/src/lib/i18n/locales/en.json
  — add "datasets.concurrencyConflict"
```

**New files:**
```
src/FormForge.Api.Tests/Features/Datasets/DatasetUpdateTests.cs   ← NEW (10 tests)
```

**No new EF Core migrations** — all required columns (`version`, `updated_at`, `updated_by`) exist on `custom_dataset` from Story 8.1.

**No new NuGet packages** — everything needed (`Dapper`, `Npgsql`, `EF Core`) was installed in prior stories.

### References

- [Source: `_bmad-output/planning-artifacts/epics.md` — Epic 8, Story 8.5 ACs (FR-58, FR-59)]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.3 — AR-59: Transactional View Lifecycle (CREATE OR REPLACE / ALTER RENAME)]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.4 — AR-60: Optimistic Concurrency (version compare-and-swap, 409 on mismatch)]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.9 — AR-65: Dataset API contract, DATASET_CONCURRENCY_CONFLICT 409]
- [Source: `src/FormForge.Api/Features/Datasets/DatasetService.cs` — CreateAsync pattern, disposal pattern, audit pattern]
- [Source: `src/FormForge.Api/Features/Datasets/DatasetViewManager.cs` — CreateAsync and BuildCreateViewDdl as models for ReplaceAsync/RenameAsync]
- [Source: `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs` — POST handler as model for PUT handler; InvalidDatasetName helper reused]
- [Source: `src/FormForge.Api/Features/Datasets/Dtos/UpdateDatasetRequest.cs` — existing record shape (all nullable except Version)]
- [Source: `src/FormForge.Api/Features/Datasets/Dtos/DatasetDto.cs` — existing response DTO reused]
- [Source: `src/FormForge.Api/Domain/Entities/CustomDataset.cs` — entity fields: UpdatedAt, UpdatedBy already present]
- [Source: `src/FormForge.Api/Domain/Entities/DatasetAuditLogEntry.cs` — PreviousValues field used for UPDATE audit]
- [Source: `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` — CustomDataset mapping (updated_at, updated_by columns confirmed)]
- [Source: `src/FormForge.Api.Tests/Features/Datasets/DatasetViewLifecycleTests.cs` — helper methods: ReseedAdminRoleAsync, SeedAdminUserAsync, LoginAsync, OpenRawAsync]
- [Source: Story 8.3 review finding — deferred: UpdateDatasetRequest.Version minimum-value guard → resolved in §4]
- [Source: Story 8.4 §7 — FluentValidation filter prohibition applies equally to PUT endpoint]

---

## Dev Agent Record

### Implementation Plan

Implemented in the task order from the spec: (1) `DatasetViewManager` gained
`BuildReplaceViewDdl` / `BuildRenameViewDdl` static helpers plus `ReplaceAsync` /
`RenameAsync`; (2) `DatasetService` gained `UpdateDatasetOutcome`, `UpdateDatasetResult`,
the `UpdateAsync` method, the private `CurrentDatasetRow` Dapper projection, and two
`[LoggerMessage]` methods; (3) the PUT stub in `DatasetEndpoints` was replaced with the
real handler and its 200/404/409/409/422 result switch; (4) the `datasets.concurrencyConflict`
i18n key was added; (5) `DatasetUpdateTests` (10 integration tests) was authored covering
AC-1..AC-4 and AC-6..AC-11. The connection-lifecycle pattern from §1 was followed: one
connection opened up front, used for the read-committed SELECT and then the
UPDATE + VIEW-DDL transaction, disposed in an outer `finally`.

### Completion Notes

- All 11 ACs satisfied. 10 integration tests pass (`DatasetUpdateTests`). Full Datasets
  feature suite green (85/85). Full backend suite: 860 passed, 2 failed — both are the
  documented pre-existing audit DELETE→405 failures (`SchemaAuditLogIntegrationTests`,
  `MutationAuditLogIntegrationTests`), unrelated to this story.
- AC-5 (null query → placeholder) is covered by the implementation
  (`effectiveViewQuery = "SELECT 1 AS placeholder"` when the effective query is
  null/whitespace); Task 7 did not request a dedicated test for it.

#### Deviations & decisions (read before review)

- **`CurrentDatasetRow.CreatedAt` is `DateTime`, not `DateTimeOffset`.** Npgsql
  materializes `timestamptz` as `DateTime(Kind=Utc)`, and Dapper's constructor matcher
  requires the exact reader CLR type — a `DateTimeOffset` parameter threw
  `InvalidOperationException` ("…matching signature… is required for CurrentDatasetRow
  materialization"). The value converts implicitly (UTC → offset zero) to the DTO's
  `DateTimeOffset` at the return site. (§5's alias guidance was followed; this is the
  type-mapping nuance §5 flagged to verify.)
- **`builder_state = @builderState::jsonb` cast in the UPDATE.** `builder_state` is a
  `jsonb` column; an untyped text/DBNull parameter is cast explicitly so a future
  non-null builder_state write does not fail with a text→jsonb type error. (`CreateAsync`
  sidestepped this by inserting a literal `NULL`.)
- **`#pragma warning disable CA1031`** wraps both the new `UpdateAsync` audit catch and
  the pre-existing `CreateAsync` audit catch. The project treats analyzer warnings as
  errors; the 8.4 `CreateAsync` catch lacked the pragma and blocked a clean analyzer
  build, so it was wrapped to match the established repo pattern (Program.cs,
  DdlEmitter.cs, etc.).
- **Test data keeps the same output column name across a query edit.** PostgreSQL
  `CREATE OR REPLACE VIEW` (AR-59) cannot rename/drop existing output columns. The
  spec's Task-7 examples that change the alias (`n`→`m`, `a`→`z`, `?column?`→`q`) would
  make the REPLACE fail with 422. The ACs' intent is editing a query's value while
  keeping its shape, so the success-path tests change only the expression and keep the
  column name. A query edit that *does* alter the column shape correctly surfaces as
  422 INVALID_QUERY under this architecture.
- **Updated one Story 8.3 test.** `DatasetNameValidationTests.PutDataset_NullName_*`
  asserted the old 501 stub; its own comment said "real handler lands in Story 8.5". It
  now asserts 404 (null name skips validation, reaches the handler, non-existent id →
  404) and was renamed `PutDataset_NullName_SkipsValidationReachesHandler`.

### File List

**Modified:**
- `src/FormForge.Api/Features/Datasets/DatasetService.cs`
- `src/FormForge.Api/Features/Datasets/DatasetViewManager.cs`
- `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs`
- `web/src/lib/i18n/locales/en.json`
- `src/FormForge.Api.Tests/Features/Datasets/DatasetNameValidationTests.cs` (one test updated for the now-implemented PUT)

**New:**
- `src/FormForge.Api.Tests/Features/Datasets/DatasetUpdateTests.cs` (10 tests)

### Change Log

- 2026-06-03 — Story 8.5 implemented: PUT `/api/datasets/{id}` updates the row + backing
  VIEW atomically (CREATE OR REPLACE / ALTER RENAME) with optimistic concurrency (409),
  name-uniqueness (409), invalid-query rollback (422), not-found (404), and success/failure
  audit logging. Added `datasets.concurrencyConflict` i18n key. Status → review.
- 2026-06-03 — Code review complete. Status → in-progress (patch findings outstanding).

### Review Findings

#### Decision Needed

- [x] [Review][Decision] Empty string `query: ""` — dismissed as false positive; `string.IsNullOrWhiteSpace` normalization already applied consistently at the DB parameter level, so `""` stores NULL. No inconsistency exists.

#### Patches

- [x] [Review][Patch] NameConflict catch uses `pg.TableName` which Npgsql may not populate — use `pg.ConstraintName` instead [DatasetService.cs — UpdateAsync catch clause] ✅ fixed
- [x] [Review][Patch] `resolvedNewName!` null-forgiving after discarded `TryCreate` return value — add explicit assertion that TryCreate succeeded [DatasetService.cs — UpdateAsync Step §3] ✅ fixed
- [x] [Review][Patch] AC-5 has no test — add test for `query: null` update path (placeholder VIEW, NULL stored in row) [DatasetUpdateTests.cs] ✅ fixed
- [x] [Review][Patch] AC-7 no-audit guarantee untested — add `GetAuditEntriesAsync` assertion to `PutDataset_DuplicateName_Returns409AndLeavesRowUnchanged` confirming zero audit rows [DatasetUpdateTests.cs] ✅ fixed
- [x] [Review][Patch] Stale `currentVersion` on `rowsAffected == 0` race path — re-read from DB instead of returning `current.Version` [DatasetService.cs — UpdateAsync rowsAffected == 0 block] ✅ fixed
- [x] [Review][Patch] AC-10 audit test never asserts `entry.DatasetName` equals the (new) name [DatasetUpdateTests.cs — PutDataset_Success_WritesSucceededAuditRow] ✅ fixed
- [x] [Review][Patch] AC-3 rename-only test does not verify VIEW body is unchanged (pg_get_viewdef) [DatasetUpdateTests.cs — PutDataset_RenameOnly_Returns200AndRenamesView] ✅ fixed
- [x] [Review][Patch] AC-10 audit test does not assert `Timestamp` is populated [DatasetUpdateTests.cs — PutDataset_Success_WritesSucceededAuditRow] ✅ fixed
- [x] [Review][Patch] `Assert.Contains("1", def)` in rollback test is too weak — assert exact original query text [DatasetUpdateTests.cs — PutDataset_InvalidSql_Returns422AndRollsBack] ✅ fixed
- [x] [Review][Patch] Trailing space in `BuildRenameViewDdl` DDL string [DatasetViewManager.cs — BuildRenameViewDdl] ✅ fixed

#### Deferred

- [x] [Review][Defer] All NpgsqlExceptions (lock timeout, permission error) returned as INVALID_QUERY — design choice per spec "VIEW DDL failure → 422"; expanding error taxonomy is out of scope — deferred, pre-existing design
- [x] [Review][Defer] Actor name null if user deleted between JWT issuance and request — pre-existing pattern from CreateAsync, DatasetAuditLogEntry.ActorName is nullable — deferred, pre-existing
- [x] [Review][Defer] Test isolation race: shared PostgresFixture TRUNCATE could race if collection parallelism enabled — pre-existing infrastructure concern shared with other Dataset test classes — deferred, pre-existing
- [x] [Review][Defer] Empty string stored in `query` column makes subsequent `queryChanged` detection unreliable — follow-on from D1 decision — deferred, pending D1 resolution
- [x] [Review][Defer] No-op PUT (same name + same query) still increments version and runs CREATE OR REPLACE VIEW — spec gap, not a regression — deferred, pre-existing

---
