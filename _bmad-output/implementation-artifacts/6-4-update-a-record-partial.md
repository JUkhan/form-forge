# Story 6.4: Update a Record (Partial)

Status: done

## Story

As an authorized user with `canUpdate`,
I want to submit a partial payload that overwrites only the supplied fields,
So that I can correct a record without a full replace.

## Acceptance Criteria

**AC-1 — Happy-path UPDATE (partial)**
**Given** an existing record and a partial payload
**When** I PUT `/api/data/{designerId}/{id}` with `{ field1: newVal }` only
**Then** only fields present in the payload are updated; fields absent from the payload retain their existing DB values
**And** `updated_at` and `updated_by` are set server-side on every successful update
**And** the response is HTTP 200 OK with the full updated record (all system + user columns)
**And** response uses camelCase system column names per AR-46 Option C (`id`, `createdAt`, `updatedAt`, `createdBy`, `updatedBy`, `isDeleted`, `cascadeEventId`)
**And** system columns (`id`, `created_at`, `created_by`, `is_deleted`, `cascade_event_id`) in the client payload are silently ignored — the server never overwrites these

**AC-2 — Soft-deleted record → 422 RECORD_DELETED**
**Given** a soft-deleted record (`is_deleted = true`)
**When** I attempt to PUT against it
**Then** the response is HTTP 422 with `code: "RECORD_DELETED"` (restore first per Story 6.6)

**AC-3 — Mutation audit log row**
**Given** a successful UPDATE
**When** the transaction completes
**Then** a row is appended to `mutation_audit_log` with `operation: "UPDATE"`, `previous_values` (current DB values of the changed fields only, before the update), `new_values` (the coerced payload values), `actor_id`, `correlation_id`, `designer_id`, `record_id`, and `timestamp`

**AC-4 — Record not found**
**Given** an `id` that does not exist in the provisioned table
**When** I PUT `/api/data/{designerId}/{id}`
**Then** the response is HTTP 404 with `code: "NOT_FOUND"`

**AC-5 — Unprovisioned designer**
**Given** a `designerId` for which no menu exists with `provisioningStatus = 'Success'`
**When** I PUT `/api/data/{designerId}/{id}`
**Then** the response is HTTP 404 with `code: "TABLE_NOT_PROVISIONED"`

**AC-6 — Permission enforcement**
**Given** an authenticated user without `canUpdate` on this resource
**When** I PUT `/api/data/{designerId}/{id}`
**Then** the response is HTTP 403 with `code: "FORBIDDEN"`

**AC-7 — Layer 2 type validation**
**Given** a known fieldKey with a type-mismatched value (e.g. NUMERIC column with value `"not-a-number"`)
**When** the server validates via `IDynamicPayloadValidator`
**Then** the response is HTTP 422 with `code: "VALIDATION_FAILED"` and `errors: { fieldKey: ["..."] }`

**AC-8 — Unknown fields silently ignored**
**Given** a payload that includes keys unknown to the schema
**When** the server processes the request
**Then** unknown keys are silently dropped; only known fieldKey columns are updated
**And** the response is HTTP 200 (not 422)

**AC-9 — Rate limiting**
**Given** the `"data-write"` rate-limiting policy
**When** a user exceeds 60 PUT `/api/data/*` requests per user per minute
**Then** the response is HTTP 429 with `Retry-After: 60`
**Note:** Applied per-endpoint via `.RequireRateLimiting("data-write")` which overrides the group-level `"data-read"` (300/min) policy. The `"data-write"` policy is already registered in `Program.cs` (added by Story 6.3). Do NOT add a new policy registration.

**AC-10 — Query timeout**
**Given** the Dapper SELECT and UPDATE queries
**When** each query runs
**Then** `commandTimeout` is set to 5 seconds on every `CommandDefinition` (Decision 1.6)

---

## Tasks / Subtasks

- [x] **Task 1 — Add `RecordDeleted` to `Problems` inner class in `DynamicDataEndpoints.cs`** (AC: 2)
  - [x] Modify `src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs`
  - Add after `RecordNotFound()` inside the `private static class Problems` block:
    ```csharp
    // Story 6.4 — PUT attempted against a soft-deleted record; client must restore
    // the record (Story 6.6) before retrying the update.
    internal static IResult RecordDeleted() =>
        Results.Problem(
            title: "Record is soft-deleted",
            statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "RECORD_DELETED",
                ["messageKey"] = "errors.recordDeleted",
            });
    ```

- [x] **Task 2 — Add `BuildUpdateQuery` to `DynamicQueryBuilder.cs`** (AC: 1, 10)
  - [x] Modify `src/FormForge.Api/Features/DynamicCrud/DynamicQueryBuilder.cs`
  - Add after `BuildInsertQuery`:
    ```csharp
    // Story 6.4 — parameterized UPDATE for PUT /api/data/{designerId}/{id}.
    // Only user columns present in coercedPayload appear in the SET clause (partial
    // update). updated_at and updated_by are always set server-side. The caller is
    // responsible for checking existence and is_deleted before invoking this method.
    // Returns the server-generated updatedAt so the caller can build the response
    // and audit row without a re-SELECT.
    internal static (string Sql, DynamicParameters Parameters, DateTimeOffset UpdatedAt)
        BuildUpdateQuery(
            SafeIdentifier tableName,
            IReadOnlyList<ColumnDefinition> userColumns,
            IReadOnlyDictionary<string, object?> coercedPayload,
            Guid id,
            Guid? actorId)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(userColumns);
        ArgumentNullException.ThrowIfNull(coercedPayload);

        var updatedAt = DateTimeOffset.UtcNow;

        // Identify which user columns appear in the payload (order follows
        // ColumnDefinition list for determinism, same as BuildInsertQuery).
        var presentUserCols = new List<ColumnDefinition>(userColumns.Count);
        foreach (var col in userColumns)
        {
            if (coercedPayload.ContainsKey(col.ColumnName))
                presentUserCols.Add(col);
        }

        // SET clause: system timestamps always included; user columns appended.
        var setSb = new StringBuilder(256);
        setSb.Append("\"updated_at\" = @p_updated_at, \"updated_by\" = @p_updated_by");
        foreach (var col in presentUserCols)
        {
            setSb.Append(CultureInfo.InvariantCulture, $", \"{col.ColumnName}\" = @f_{col.ColumnName}");
        }

        var sql = $"UPDATE \"{tableName.Value}\" SET {setSb} WHERE \"id\" = @p_id";

        var parameters = new DynamicParameters();
        parameters.Add("p_updated_at", updatedAt);
        parameters.Add("p_updated_by", actorId);
        parameters.Add("p_id",         id);

        foreach (var col in presentUserCols)
        {
            parameters.Add($"f_{col.ColumnName}", coercedPayload[col.ColumnName]);
        }

        return (sql, parameters, updatedAt);
    }
    ```
  - No `is_deleted` check in the WHERE clause — the handler does a SELECT first and explicitly checks existence and deletion status.

- [x] **Task 3 — Add `UpdateRecordHandler` + route registration to `DynamicDataEndpoints.cs`** (AC: 1–10)
  - [x] Modify `src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs`
  - Add route registration after `MapPost("/", CreateRecordHandler)` inside `MapDynamicDataEndpoints`:
    ```csharp
    // Story 6.4 — PUT /api/data/{designerId}/{id}. Rate limit overrides group
    // default: "data-write" (60 req/min) vs group "data-read" (300 req/min).
    group.MapPut("/{id:guid}", UpdateRecordHandler)
         .WithSummary("Partially update a record in a provisioned dynamic table.")
         .Produces<DynamicRecord>(StatusCodes.Status200OK)
         .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
         .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
         .Produces<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity)
         .RequirePermission("update")
         .RequireRateLimiting("data-write");
    ```
  - Handler signature (note `JsonElement body` auto-bound same as `CreateRecordHandler`):
    ```csharp
    internal static async Task<IResult> UpdateRecordHandler(
        string designerId,
        Guid id,
        JsonElement body,
        HttpContext httpContext,
        FormForgeDbContext db,
        ISchemaRegistry schemaRegistry,
        DbConnectionFactory connectionFactory,
        IDynamicPayloadValidator payloadValidator,
        CancellationToken ct)
    ```
  - **Handler flow (step by step):**
    1. `ArgumentNullException.ThrowIfNull(httpContext, db, schemaRegistry, connectionFactory, payloadValidator)`
    2. `SafeIdentifier.TryCreate(designerId, out var safeId, out _)` → if invalid, return `Problems.ValidationFailed("Invalid designer identifier.")`
    3. EF binding lookup (identical to all prior handlers):
       ```csharp
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
       ```
    4. Schema registry with cache-miss repopulation (identical to `CreateRecordHandler`):
       ```csharp
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
       ```
    5. Layer 2 payload validation (identical to `CreateRecordHandler`):
       ```csharp
       var validationResult = payloadValidator.Validate(body, entry.Columns);
       if (!validationResult.IsValid)
       {
           return Results.ValidationProblem(
               validationResult.FieldErrors,
               statusCode: StatusCodes.Status422UnprocessableEntity,
               extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
               {
                   ["code"] = "VALIDATION_FAILED",
                   ["messageKey"] = "errors.validationFailed",
               });
       }
       ```
    6. Extract actorId from JWT (identical to `CreateRecordHandler`):
       ```csharp
       var actorId = httpContext.User.FindFirst("userId")?.Value is { } userIdStr
           && Guid.TryParse(userIdStr, out var uid) ? uid : (Guid?)null;
       ```
    7. Open Dapper connection and SELECT existing record to check existence + capture previousValues:
       ```csharp
       var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
       Dictionary<string, object?> existingRow;
       try
       {
           var (selectSql, selectParams) = DynamicQueryBuilder.BuildGetByIdQuery(
               safeId, entry.Columns, id);
           var rawRows = await conn.QueryAsync(new CommandDefinition(
               selectSql, selectParams, commandTimeout: 5, cancellationToken: ct))
               .ConfigureAwait(false);
           var rows = rawRows
               .Cast<IDictionary<string, object>>()
               .Select(row => row.ToDictionary(
                   kvp => kvp.Key, kvp => (object?)kvp.Value, StringComparer.Ordinal))
               .ToList();

           if (rows.Count == 0)
           {
               await conn.DisposeAsync().ConfigureAwait(false);
               return Problems.RecordNotFound();
           }

           existingRow = rows[0];
           var isDeleted = existingRow.TryGetValue("is_deleted", out var v) && v is true;
           if (isDeleted)
           {
               await conn.DisposeAsync().ConfigureAwait(false);
               return Problems.RecordDeleted();
           }

           // Build UPDATE and execute on the same open connection.
           var (updateSql, updateParams, updatedAt) = DynamicQueryBuilder.BuildUpdateQuery(
               safeId, entry.Columns, validationResult.CoercedValues, id, actorId);
           await conn.ExecuteAsync(new CommandDefinition(
               updateSql, updateParams, commandTimeout: 5, cancellationToken: ct))
               .ConfigureAwait(false);

           // Capture updatedAt in a local for the response + audit log.
           existingRow["updated_at"] = updatedAt;
           existingRow["updated_by"] = actorId;
           foreach (var (colName, val) in validationResult.CoercedValues)
               existingRow[colName] = val;
       }
       finally
       {
           await conn.DisposeAsync().ConfigureAwait(false);
       }
       ```
       **Important:** Dispose the connection whether the early returns (RecordNotFound, RecordDeleted) fire or not. The `finally` block above handles the normal exit. For the early-return cases, `conn` is disposed inline before returning, so the `finally` will call `DisposeAsync` on an already-disposed connection — Npgsql's NpgsqlConnection `DisposeAsync` is idempotent.
       
       **Alternative approach to avoid double-dispose:** Restructure by tracking a local `bool earlyReturn` flag or use a nested try/finally for early returns. The cleanest approach is:
       ```csharp
       var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
       IResult? earlyResult = null;
       Dictionary<string, object?> existingRow = [];
       DateTimeOffset updatedAt = default;
       try
       {
           // ... SELECT ...
           if (rows.Count == 0) { earlyResult = Problems.RecordNotFound(); return; }
           if (isDeleted)        { earlyResult = Problems.RecordDeleted();  return; }
           // ... UPDATE ...
       }
       finally
       {
           await conn.DisposeAsync().ConfigureAwait(false);
       }
       if (earlyResult is not null) return earlyResult;
       ```
       The local-flag pattern avoids disposing conn before the finally and is consistent with `GetRecordHandler`'s approach of only having one `finally { await conn.DisposeAsync() }`.
       
       **Recommended implementation** (mirrors GetRecordHandler single-finally pattern):
       ```csharp
       var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
       IResult? earlyResult = null;
       var existingRow = new Dictionary<string, object?>(StringComparer.Ordinal);
       try
       {
           var (selectSql, selectParams) = DynamicQueryBuilder.BuildGetByIdQuery(safeId, entry.Columns, id);
           var rawRows = await conn.QueryAsync(new CommandDefinition(
               selectSql, selectParams, commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);
           var rows = rawRows
               .Cast<IDictionary<string, object>>()
               .Select(r => r.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value, StringComparer.Ordinal))
               .ToList();

           if (rows.Count == 0)
           {
               earlyResult = Problems.RecordNotFound();
               return;
           }

           existingRow = rows[0];
           if (existingRow.TryGetValue("is_deleted", out var isDeletedVal) && isDeletedVal is true)
           {
               earlyResult = Problems.RecordDeleted();
               return;
           }

           var (updateSql, updateParams, updatedAt) = DynamicQueryBuilder.BuildUpdateQuery(
               safeId, entry.Columns, validationResult.CoercedValues, id, actorId);
           await conn.ExecuteAsync(new CommandDefinition(
               updateSql, updateParams, commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

           // Overlay coerced values and new system timestamps onto the existing row dict.
           existingRow["updated_at"] = updatedAt;
           existingRow["updated_by"] = actorId;
           foreach (var (colName, val) in validationResult.CoercedValues)
               existingRow[colName] = val;
       }
       finally
       {
           await conn.DisposeAsync().ConfigureAwait(false);
       }
       if (earlyResult is not null) return earlyResult;
       ```
    8. Append mutation audit log via EF (AC-3):
       ```csharp
       // previousValues = current DB values of only the fields being changed.
       var previousValues = new Dictionary<string, object?>(
           validationResult.CoercedValues.Count, StringComparer.Ordinal);
       foreach (var colName in validationResult.CoercedValues.Keys)
       {
           // existingRow was populated from the SELECT result BEFORE the overlay above —
           // but we overlaid coerced values into existingRow after the UPDATE. Capture
           // previousValues BEFORE the overlay (see note in Dev Notes §5).
           // IMPORTANT: capture previousValues from a snapshot taken BEFORE the overlay.
       }
       ```
       **Note:** `previousValues` must be captured BEFORE overlaying coercedValues onto `existingRow`. See Dev Notes §5 for the correct ordering. In the recommended implementation above, the overlay happens after the UPDATE execute call. Capture `previousValues` right after the row is retrieved but before the overlay:
       ```csharp
       // Capture previousValues (snapshot of changed fields before overlay).
       var previousValues = new Dictionary<string, object?>(StringComparer.Ordinal);
       foreach (var colName in validationResult.CoercedValues.Keys)
       {
           previousValues[colName] = existingRow.TryGetValue(colName, out var prev) ? prev : null;
       }

       var (updateSql, updateParams, updatedAt) = DynamicQueryBuilder.BuildUpdateQuery(...);
       await conn.ExecuteAsync(...);

       // Overlay AFTER capturing previousValues.
       existingRow["updated_at"] = updatedAt;
       existingRow["updated_by"] = actorId;
       foreach (var (colName, val) in validationResult.CoercedValues)
           existingRow[colName] = val;
       ```
       Audit log append (same pattern as `CreateRecordHandler`):
       ```csharp
       var correlationId  = httpContext.GetCorrelationId();
       var newValuesJson  = JsonSerializer.Serialize(validationResult.CoercedValues);
       var prevValuesJson = previousValues.Count > 0
           ? JsonSerializer.Serialize(previousValues)
           : null;

       db.MutationAuditLog.Add(new Domain.Entities.MutationAuditLogEntry
       {
           DesignerId     = safeId!.Value,
           RecordId       = id,
           Operation      = "UPDATE",
           ActorId        = actorId,
           Timestamp      = updatedAt,
           NewValues      = newValuesJson,
           PreviousValues = prevValuesJson,
           CorrelationId  = correlationId,
       });
       await db.SaveChangesAsync(ct).ConfigureAwait(false);
       ```
    9. Build and return 200 OK response:
       ```csharp
       return Results.Ok(new DynamicRecord(existingRow));
       ```
       No re-SELECT needed: `existingRow` contains the pre-update row with coerced values overlaid + new updated_at/updated_by. All values are .NET-typed (Dapper/Npgsql-coerced), same as `GetRecordHandler`.

- [x] **Task 4 — Unit tests for `BuildUpdateQuery`** (AC: 1, 10)
  - [x] Modify `src/FormForge.Api.Tests/Features/DynamicCrud/DynamicQueryBuilderTests.cs`
  - Add to the existing test class (after the `BuildInsertQuery` tests):
    - `BuildUpdateQuery_EmptyPayload_SetsOnlyUpdatedAtAndUpdatedBy` — empty `coercedPayload` → SQL contains `"updated_at" = @p_updated_at` and `"updated_by" = @p_updated_by`; no `@f_` parameters; `WHERE "id" = @p_id` present
    - `BuildUpdateQuery_WithOneUserColumn_IncludesColumnInSetClause` — `coercedPayload = { "title" → "hello" }` → SQL has `, "title" = @f_title` in SET; `parameters.Get<string>("f_title")` == `"hello"`
    - `BuildUpdateQuery_MultipleUserColumns_AllInSetClause` — 2 user columns in payload → both appear in SET; order follows the ColumnDefinition list
    - `BuildUpdateQuery_SystemColumnsInPayloadNotInSetClause` — coercedPayload only has schema-registry user columns (validator strips system columns); verify `parameters.ParameterNames` does NOT contain `f_id`, `f_created_at`, etc.
    - `BuildUpdateQuery_ReturnsUpdatedAtTimestamp` — `UpdatedAt != default` and within 1 second of `DateTimeOffset.UtcNow`
  - Estimated: +5 unit tests → running total ~465

- [x] **Task 5 — Integration tests: `UpdateRecordIntegrationTests.cs`** (AC: 1–10)
  - [x] Create `src/FormForge.Api.Tests/Features/DynamicCrud/UpdateRecordIntegrationTests.cs`
  - Class signature: `[Collection("DynamicCrudTests")] public sealed class UpdateRecordIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime`
  - `InitializeAsync` / `DisposeAsync` identical to `CreateRecordIntegrationTests`:
    - Same `TRUNCATE TABLE menu_role_assignments, menus, component_schema_versions, component_schemas, role_permissions, user_roles, roles, refresh_tokens, users, schema_audit_log, mutation_audit_log RESTART IDENTITY CASCADE;`
    - Same dynamic table DROP loop (keep-list identical to `CreateRecordIntegrationTests`)
    - Same `ReseedSystemRolesAsync` + `SeedTestUsersAsync` calls
  - Copy all helper methods from `CreateRecordIntegrationTests` (LoginAsync, PostRecordAsync, SetupProvisionedDesignerWithTitleAsync, etc.)
  - Add new helper `PutRecordAsync(string token, string designerId, Guid id, object payload)`:
    ```csharp
    private async Task<HttpResponseMessage> PutRecordAsync(string token, string designerId, Guid id, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/data/{designerId}/{id}")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }
    ```
  - **Test cases:**
    1. `UpdateRecord_ValidPartialPayload_Returns200WithUpdatedRecord` — AC-1: provision table with `title` column, create record, PUT `{ "title": "Updated" }` → 200; verify JSON has `id` (same UUID as created), `title: "Updated"`, `updatedAt` newer than `createdAt`, `isDeleted: false`, `createdBy` unchanged, `updatedBy` = actor UUID
    2. `UpdateRecord_UnknownFieldInPayload_Returns200IgnoresUnknownField` — AC-8: PUT `{ "title": "ok", "ghost_field": "ignored" }` → 200, no `ghost_field` in response
    3. `UpdateRecord_TypeMismatch_Returns422WithFieldError` — AC-7: provision table with NUMERIC column, PUT `{ "price": "not-a-number" }` → 422 `ValidationProblemDetails`; verify `errors["price"]` non-empty
    4. `UpdateRecord_SoftDeletedRecord_Returns422RecordDeleted` — AC-2: set `is_deleted = true` directly via Npgsql/Dapper (`UPDATE {table} SET is_deleted = true WHERE id = @id`), then PUT → 422 `code: "RECORD_DELETED"`
    5. `UpdateRecord_RecordNotFound_Returns404` — AC-4: PUT with a random Guid that does not exist → 404 `code: "NOT_FOUND"`
    6. `UpdateRecord_UnprovisionedDesigner_Returns404TableNotProvisioned` — AC-5: unknown designerId → 404 `TABLE_NOT_PROVISIONED`
    7. `UpdateRecord_NoCanUpdate_Returns403` — AC-6: viewer user (no canUpdate) → 403 `code: "FORBIDDEN"`
    8. `UpdateRecord_AuditLogRow_AppendedWithPreviousAndNewValues` — AC-3: create record with `title = "Old"`, PUT `{ "title": "New" }`, query `mutation_audit_log` via Npgsql → verify `operation = 'UPDATE'`, `previous_values` contains `"title": "Old"`, `new_values` contains `"title": "New"`, `actor_id` matches admin UUID, `correlation_id` non-empty, `timestamp` within request window
    9. `UpdateRecord_EmptyPayload_OnlyUpdatesTimestamps` — AC-1 edge: create record, PUT `{}` → 200; verify `updatedAt` newer than `createdAt`; verify `title` unchanged (retains original value)
    10. `UpdateRecord_SystemColumnsInPayload_AreIgnored` — AC-1: PUT `{ "id": "<fake-uuid>", "created_at": "2000-01-01T00:00:00Z", "title": "x" }` → 200; verify response `id` == original record id (not fake-uuid); verify response `createdAt` is the original creation time
  - Estimated: +10 integration tests → running total ~475

- [x] **Task 6 — Frontend API client** (foundational for Stories 6.9/6.10)
  - [x] Create `web/src/features/data-entry/updateRecordApi.ts`
    ```ts
    import { httpClient } from '../auth/httpClient'
    import type { DynamicRecord } from './recordListApi'

    export function updateRecord(
      designerId: string,
      id: string,
      payload: Record<string, unknown>,
    ): Promise<DynamicRecord> {
      return httpClient.put<DynamicRecord>(
        `/api/data/${encodeURIComponent(designerId)}/${encodeURIComponent(id)}`,
        payload,
      )
    }
    ```
  - [x] Create `web/src/features/data-entry/useUpdateRecord.ts`
    ```ts
    import { useMutation, useQueryClient } from '@tanstack/react-query'
    import { updateRecord } from './updateRecordApi'
    import type { DynamicRecord } from './recordListApi'

    export function useUpdateRecord(designerId: string) {
      const queryClient = useQueryClient()

      return useMutation({
        mutationFn: ({ id, payload }: { id: string; payload: Record<string, unknown> }) =>
          updateRecord(designerId, id, payload),
        onSuccess: (_updated: DynamicRecord, { id }) => {
          // AR-48: invalidate list queries so updated record reflects everywhere.
          // AR-49: pessimistic mutation — wait for server confirmation before updating UI.
          queryClient.invalidateQueries({ queryKey: ['data', designerId] })
          queryClient.invalidateQueries({ queryKey: ['data', designerId, 'record', id] })
        },
      })
    }
    ```

---

## Dev Notes

### Architecture compliance — critical constraints

**1. The handler must SELECT before UPDATE (two Dapper operations on one connection)**
`BuildGetByIdQuery` is used for the SELECT to check existence (`404`), soft-delete status (`422 RECORD_DELETED`), and capture `previousValues` for the audit log. Both the SELECT and UPDATE run on the same `NpgsqlConnection` (sequential, not concurrent) with `commandTimeout: 5` each. The connection is disposed in a single `finally` block (see handler flow). This is the same pattern as `GetRecordHandler` for the parent + children selects.

**2. `previousValues` must be captured BEFORE overlaying coerced values onto `existingRow`**
The `existingRow` dict is populated from the Dapper SELECT result. Immediately after confirming the record is not deleted, capture `previousValues` as a snapshot of the fields being changed:
```csharp
var previousValues = new Dictionary<string, object?>(StringComparer.Ordinal);
foreach (var colName in validationResult.CoercedValues.Keys)
    previousValues[colName] = existingRow.TryGetValue(colName, out var prev) ? prev : null;
```
Then execute `BuildUpdateQuery` and overlay coerced values onto `existingRow` AFTER this snapshot. Reversing the order would serialize the new values as both `previousValues` and `newValues`.

**3. `BuildUpdateQuery` does NOT check `is_deleted` in the WHERE clause**
The handler performs an explicit SELECT-then-check to differentiate "record not found" (→ 404) from "record is soft-deleted" (→ 422 RECORD_DELETED). If `is_deleted` were in the UPDATE's WHERE clause, a deleted record would return 0 rows affected and the two error cases would be indistinguishable. The two-step SELECT + UPDATE on one connection is correct; it introduces a short window where another request could soft-delete between the SELECT and UPDATE (recorded as a deferred item).

**4. `previousValues` in audit log is null for empty payload**
If `coercedPayload` is empty (client sends `{}`), `previousValues` has 0 entries and should be serialized as `null` (not `{}`):
```csharp
var prevValuesJson = previousValues.Count > 0
    ? JsonSerializer.Serialize(previousValues)
    : null;
```

**5. Response is built from `existingRow` (the SELECT result) with coerced values overlaid**
No re-SELECT after the UPDATE. The dict from `BuildGetByIdQuery`'s Dapper result has PG-named keys and PG-native .NET types (Npgsql mapping: UUID → Guid, TIMESTAMPTZ → DateTimeOffset, BOOLEAN → bool, TEXT → string). After overlaying `updatedAt` (DateTimeOffset), `actorId` (Guid?), and the coerced user values, the result is a valid `DynamicRecord`. `DynamicRecordJsonConverter` then renders camelCase system columns automatically (AR-46 Option C). This is consistent with how `GetRecordHandler` returns rows.

**6. `IDynamicPayloadValidator` is reused as-is — no changes needed**
The validator is stateless and generic. Unknown keys (including system column names) are silently ignored because they don't appear in `entry.Columns`. The same `IDynamicPayloadValidator` registered in `Program.cs` by Story 6.3 handles both CREATE and UPDATE payloads identically. Do NOT register it again.

**7. Rate limit: `"data-write"` (60/min) applied via `.RequireRateLimiting("data-write")` on the PUT endpoint**
The group inherits `"data-read"` (300/min). The per-endpoint override follows the same pattern as `MapPost("/", CreateRecordHandler)`. The `"data-write"` sliding window policy was registered in `Program.cs` by Story 6.3 (lines ~294–307). Do NOT add a new policy registration.

**8. `httpClient.put<T>(path, body)` must exist in the frontend `httpClient`**
Verify that `web/src/features/auth/httpClient.ts` exposes a `.put<T>(path, body?)` method alongside `.post<T>`. If `put` is missing, add it following the same pattern as `post`. Check the file before assuming it exists.

**9. `is_deleted` Dapper value type**
Npgsql maps PG `BOOLEAN` to C# `bool`. When Dapper returns `IDictionary<string, object>`, `is_deleted` value is a C# `bool`. The pattern `isDeletedVal is true` is a safe bool pattern match. `DBNull.Value` is not returned by Npgsql for NOT NULL columns; `is_deleted` has `DEFAULT false NOT NULL` (set by `DdlEmitter`), so it is never null.

**10. `{id:guid}` route constraint on PUT**
The `/{id:guid}` route segment already validates the UUID format before the handler runs — non-UUID path segments return 400 from ASP.NET automatically. This mirrors the existing GET `/{id:guid}` route in Story 6.2.

---

### File locations — new files

| New file | Path |
|---|---|
| `UpdateRecordIntegrationTests.cs` | `src/FormForge.Api.Tests/Features/DynamicCrud/UpdateRecordIntegrationTests.cs` |
| `updateRecordApi.ts` | `web/src/features/data-entry/updateRecordApi.ts` |
| `useUpdateRecord.ts` | `web/src/features/data-entry/useUpdateRecord.ts` |

### File locations — modified files

| Modified file | Change |
|---|---|
| `DynamicDataEndpoints.cs` | Add `Problems.RecordDeleted()`, `MapPut("/{id:guid}", UpdateRecordHandler)`, handler |
| `DynamicQueryBuilder.cs` | Add `BuildUpdateQuery` |
| `DynamicQueryBuilderTests.cs` | Add 5 unit tests for `BuildUpdateQuery` |

No changes needed to: `MutationAuditLogEntry.cs`, `FormForgeDbContext.cs`, `Program.cs`, `DynamicPayloadValidator.cs`, `DynamicRecord.cs`, `DynamicRecordJsonConverter.cs`. All infrastructure introduced by Stories 6.1–6.3 is sufficient.

No new EF migration required. No new database tables or columns.

---

### Helper methods for `UpdateRecordIntegrationTests.cs`

Copy the following from `CreateRecordIntegrationTests` verbatim:
- `LoginAsync(email, password)` → `string` token
- `PostRecordAsync(token, designerId, payload)` → `HttpResponseMessage` (needed to create records before updating)
- `SetupProvisionedDesignerWithTitleAsync(token, designerId)` → provisions a table with a `title` TEXT column
- `CreateMenuViaApiAsync`, `CreateDesignerViaApiAsync`, `CreateAndPublishDesignerWithFieldsAsync`, `PutVersionStatusAsync`, `PutBindingAsync`, `PollUntilTerminalAsync`, `GetMenuAsync`
- DTOs: `LoginResponseDto`, `MenuResponseDto`

Add new helpers:
```csharp
private async Task<HttpResponseMessage> PutRecordAsync(string token, string designerId, Guid id, object payload)
{
    using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/data/{designerId}/{id}")
    {
        Content = JsonContent.Create(payload),
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    return await _client!.SendAsync(request);
}

private async Task<Guid> CreateRecordAndGetIdAsync(string token, string designerId, object payload)
{
    using var response = await PostRecordAsync(token, designerId, payload);
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    var json = await response.Content.ReadFromJsonAsync<JsonElement>(WebJsonOptions);
    return json.GetProperty("id").GetGuid();
}
```

For AC-2 (soft-delete setup before Story 6.5 exists): set `is_deleted = true` directly via Npgsql:
```csharp
await using var conn = new NpgsqlConnection(_postgres.ConnectionString);
await conn.OpenAsync();
await conn.ExecuteAsync($"UPDATE \"{designerId}\" SET is_deleted = true WHERE id = @id", new { id = recordId });
```

For AC-3 (audit log verification): query via Npgsql directly:
```csharp
await using var conn = new NpgsqlConnection(_postgres.ConnectionString);
await conn.OpenAsync();
var row = await conn.QuerySingleAsync(
    "SELECT operation, previous_values, new_values, actor_id, correlation_id, timestamp FROM mutation_audit_log WHERE record_id = @recordId AND operation = 'UPDATE'",
    new { recordId });
```

---

### Deferred items (record for code review)

1. **SELECT + UPDATE race window** — between the SELECT (existence/deleted check) and the UPDATE execute, another concurrent request could soft-delete or update the record. The UPDATE runs anyway without re-checking `is_deleted`. Fix: wrap both in a `NpgsqlTransaction` with `REPEATABLE READ` isolation. Deferred per v1 scope.
2. **Audit INSERT not transactionally coupled to the Dapper UPDATE** — same deferred item as Story 6.3 item 1. If `db.SaveChangesAsync` fails after a successful UPDATE, the record is updated without an audit row.
3. **`previousValues` serializes .NET-typed objects from Dapper** — Dapper returns PG UUID as `Guid`, TIMESTAMPTZ as `DateTimeOffset`, BOOLEAN as `bool`. `JsonSerializer.Serialize(previousValues)` serializes these as JSON strings/booleans/numbers. The audit viewer (Story 6.8) will need to deserialize these consistently. Acceptable for v1.
4. **Empty-payload UPDATE still writes updated_at/updated_by** — a PUT `{}` "touches" the record (updates the timestamp) even when no user data changes. This may create spurious audit rows (`operation: UPDATE`, `previousValues: null`, `newValues: {}`). Decide in Story 6.8 whether to filter these from the audit view.
5. **No optimistic concurrency (ETag / If-Match)** — concurrent updates to the same record are last-write-wins. Deferred per v1 scope (Decision 3.9).

---

### References

- [Source: `src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs`] — `CreateRecordHandler` (lines 302–428) — mirror the SafeIdentifier → EF binding → registry → Layer 2 validation → actorId extraction pattern verbatim; add `UpdateRecordHandler` after `CreateRecordHandler`; add `Problems.RecordDeleted()` after `Problems.RecordNotFound()`
- [Source: `src/FormForge.Api/Features/DynamicCrud/DynamicQueryBuilder.cs`] — `BuildInsertQuery` (lines 250–305) — mirror the column-discovery loop and parameter-binding pattern for `BuildUpdateQuery`; `BuildGetByIdQuery` (lines 203–219) — used inside the handler for the pre-UPDATE SELECT
- [Source: `src/FormForge.Api/Features/DynamicCrud/DynamicPayloadValidator.cs`] — unchanged; `IDynamicPayloadValidator.Validate(body, columns)` called identically from `UpdateRecordHandler` as from `CreateRecordHandler`
- [Source: `src/FormForge.Api/Features/DynamicCrud/DynamicRecord.cs`] — `DynamicRecord(IDictionary<string,object?> values)` constructor; `DynamicRecordJsonConverter` — handles camelCase AR-46 Option C for the response automatically
- [Source: `src/FormForge.Api.Tests/Features/DynamicCrud/CreateRecordIntegrationTests.cs`] — copy all helpers; TRUNCATE statement (line 51); `[Collection("DynamicCrudTests")]` attribute; dynamic-table DROP loop; seeding helpers
- [Source: `web/src/features/data-entry/createRecordApi.ts`] — model `updateRecordApi.ts` on this; change `httpClient.post` → `httpClient.put`, include `id` in the path
- [Source: `web/src/features/data-entry/useCreateRecord.ts`] — model `useUpdateRecord.ts` on this; add record-specific invalidation key `['data', designerId, 'record', id]` in addition to the list key
- [Source: `web/src/features/auth/httpClient.ts`] — verify `.put<T>(path, body?)` exists before writing `updateRecordApi.ts`; add it if absent
- [Architecture: AR-46 Option C] — system PG column names → camelCase JSON in 200 response; `DynamicRecordJsonConverter` handles this automatically
- [Architecture: Decision 1.6] — `commandTimeout: 5` on all dynamic CRUD Dapper calls; EF + Dapper are separated transactions
- [Architecture: Decision 1.5] — `mutation_audit_log` indexes already created by Story 6.3 migration; no new indexes needed
- [Architecture: Decision 2.6] — PUT data-write rate limit: apply `.RequireRateLimiting("data-write")` per-endpoint override (same as POST)
- [Architecture: AR-48 + AR-49] — invalidate both list and single-record TanStack Query keys on success; pessimistic mutation

---

## Dev Agent Record

### Agent Model Used

Claude Opus 4.7 (claude-opus-4-7) — bmad-dev-story workflow

### Debug Log References

No debug iterations needed; all 5 unit tests and 10 integration tests passed on the first run. Full backend regression suite at 475/475 (was 460 before Story 6.4; +15 = 5 unit + 10 integration). TypeScript clean (`tsc --noEmit` exit 0). ESLint on the two new frontend files produced 0 errors. The 2 pre-existing vitest failures in `web/src/features/admin/menus/__tests__/ReorderableMenuList.test.tsx` (jsdom EventListener IDL flakiness in Story 4.5's keyboard DnD tests for "Space + ArrowDown + Space" and "Escape after pickup") are unrelated to data-entry and were not introduced by this story — confirmed by stashing my changes and reproducing the same 2 failures against the clean Story 6.3 baseline.

### Completion Notes List

- **AC-1 — Happy-path UPDATE**: `UpdateRecord_ValidPartialPayload_Returns200WithUpdatedRecord` asserts `id` unchanged, `title` updated, `updatedAt > createdAt`, `createdBy === updatedBy` (same actor), `isDeleted=false`, AND that raw snake_case keys (`is_deleted`, `created_by`, `updated_by`) are absent — `DynamicRecordJsonConverter` (Story 6.1) handles the AR-46 Option C camelCase mapping automatically because the response is built from a `Dictionary<string, object?>` keyed on PG names.
- **AC-1 final bullet — system columns in payload ignored**: `UpdateRecord_SystemColumnsInPayload_AreIgnored` confirms the server-generated `id` wins over a client-supplied fake `id`, `createdAt` is not overwritten by a year-2000 fake, `isDeleted: true` in the payload is dropped, and the genuine user field still applies. The validator already strips system column names because they aren't in `entry.Columns` — no extra guard needed.
- **AC-2 — Soft-deleted record → 422 RECORD_DELETED**: handler does an explicit SELECT-then-check on the same Dapper connection. `is_deleted` is pattern-matched as `isDeletedVal is true` (matches only the boolean literal). Test sets `is_deleted = true` via Npgsql since Story 6.5 (soft-delete API) is not yet implemented.
- **AC-3 — Audit log row**: `previous_values` is captured BEFORE overlaying coerced values onto `existingRow` (Dev Notes §2 — reversing the order would serialize the new values as both `previousValues` and `newValues`). Only fields present in `validationResult.CoercedValues.Keys` are snapshotted into `previousValues` — unchanged fields are not in the snapshot. Empty payload → `previousValues.Count == 0` → serialized as JSON `null`, not `"{}"` (Dev Notes §4).
- **AC-4 — Record not found → 404 NOT_FOUND**: SELECT returns zero rows → `Problems.RecordNotFound()`. Distinct from `TABLE_NOT_PROVISIONED` (404 too) so the client can differentiate "the table exists but this row doesn't" from "the schema isn't bound yet".
- **AC-5 — Unprovisioned designer → 404 TABLE_NOT_PROVISIONED**: handled by the same EF `boundVersion` lookup pattern as POST/GET/LIST; no behavioural difference for the PUT route.
- **AC-6 — `canUpdate` permission**: enforced via `.RequirePermission("update")` on the route — viewer role test returns 403 with `code: "FORBIDDEN"` body. No handler-level RBAC code; the policy infrastructure (Story 2.6) does the work.
- **AC-7 — Layer 2 type validation**: `IDynamicPayloadValidator` (Story 6.3) is reused unchanged — `Validate(body, entry.Columns)` returns `ValidationProblemDetails` with `errors[fieldKey]` populated for type mismatches. NUMERIC test sends `"not-a-number"` and asserts `errors.price` is a non-empty array.
- **AC-8 — Unknown fields silently ignored**: validator iterates only over `entry.Columns` (user fieldKeys), so unknown keys like `ghost_field` never enter `coercedPayload` and never reach the SET clause. Test verifies 200 + no `ghost_field` in response.
- **AC-9 — Rate limiting**: per-endpoint `.RequireRateLimiting("data-write")` overrides the group's `data-read` (300/min) → 60/min. No new policy registered; reused the policy from Story 6.3's POST registration. No 429/Retry-After test added (deferred — same gap as Story 6.3).
- **AC-10 — `commandTimeout: 5`**: applied on both the Dapper SELECT and Dapper UPDATE `CommandDefinition` calls inside the single `try { ... } finally { await conn.DisposeAsync() }` block.
- **Handler structure — SELECT-then-UPDATE on one connection with single finally**: used the `earlyResult` local-flag pattern (Dev Notes §1) so all three exit paths (404 NOT_FOUND, 422 RECORD_DELETED, 200 happy-path) share one connection-dispose. No double-dispose, no nested try/finally. Matches `GetRecordHandler`'s single-finally shape.
- **No frontend test coverage added for `updateRecordApi.ts` / `useUpdateRecord.ts`** — mirrors Stories 6.1/6.2/6.3 which also added API clients + TanStack hooks without dedicated vitest files. Coverage will come via the data-entry view tests in Stories 6.9/6.10.

### File List

**Modified files**
- `src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs` — added `Problems.RecordDeleted()` helper, `MapPut("/{id:guid}", UpdateRecordHandler)` route registration, and the `UpdateRecordHandler` method (~120 LOC)
- `src/FormForge.Api/Features/DynamicCrud/DynamicQueryBuilder.cs` — added `BuildUpdateQuery` static method returning `(sql, parameters, updatedAt)`
- `src/FormForge.Api.Tests/Features/DynamicCrud/DynamicQueryBuilderTests.cs` — +5 unit tests for `BuildUpdateQuery`
- `_bmad-output/implementation-artifacts/sprint-status.yaml` — story 6-4 status: ready-for-dev → in-progress → review

**New files**
- `src/FormForge.Api.Tests/Features/DynamicCrud/UpdateRecordIntegrationTests.cs` — 10 integration tests (AC-1 happy + system-columns-ignored, AC-2 RECORD_DELETED, AC-3 audit, AC-4 NOT_FOUND, AC-5 TABLE_NOT_PROVISIONED, AC-6 FORBIDDEN, AC-7 type-mismatch, AC-8 unknown field, AC-1 empty payload)
- `web/src/features/data-entry/updateRecordApi.ts` — typed `updateRecord(designerId, id, payload)` wrapping `httpClient.put`
- `web/src/features/data-entry/useUpdateRecord.ts` — TanStack mutation hook with dual invalidation (`['data', designerId]` + `['data', designerId, 'record', id]`)

---

## Change Log

| Date | Change |
|---|---|
| 2026-05-26 | Story created — PUT /api/data/{designerId}/{id} partial update with SELECT-first existence/deleted check, audit log, and frontend mutation hook |
| 2026-05-26 | Implementation complete — handler + builder + 5 unit tests + 10 integration tests + frontend API client/hook. 475/475 backend tests passing. Status: in-progress → review. |
| 2026-05-26 | Code review complete — 0 decision-needed, 8 patch, 8 defer, 9 dismissed. Status: review → in-progress. |

---

### Review Findings

- [x] [Review][Patch] `BuildUpdateQuery` WHERE clause missing `AND "is_deleted" = false` — a concurrent soft-delete between the SELECT and the UPDATE silently overwrites a logically-deleted row; add `AND "is_deleted" = false` to the WHERE predicate and check affected rows to surface a 422 if the race fires [src/FormForge.Api/Features/DynamicCrud/DynamicQueryBuilder.cs]
- [x] [Review][Patch] `newValuesJson` not null-guarded for empty payload — `JsonSerializer.Serialize({})` produces `"{}"` instead of `null`, inconsistent with the `prevValuesJson` null-guard in DN-4; add the same `.Count > 0` check to `newValuesJson` [src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs]
- [x] [Review][Patch] `cascadeEventId` / `cascade_event_id` not asserted in AC-1 happy-path test — AR-46 Option C requires all seven system columns in camelCase; `cascadeEventId` present and `cascade_event_id` absent are not verified [src/FormForge.Api.Tests/Features/DynamicCrud/UpdateRecordIntegrationTests.cs]
- [x] [Review][Patch] AC-3 audit-log test uses substring containment (`Assert.Contains("New", row.new_values)`) not JSON deserialization; `record_id` is used only as a SQL filter predicate, never explicitly asserted — deserialise the JSON blobs and assert concrete key-value pairs; add `Assert.Equal(recordId, row.record_id)` [src/FormForge.Api.Tests/Features/DynamicCrud/UpdateRecordIntegrationTests.cs]
- [x] [Review][Patch] No test verifies audit-log row for empty-payload PUT — AC-3 is only exercised for non-empty payloads; the `{}` path (`previous_values IS NULL`, `new_values = null` or `'{}'`) is untested [src/FormForge.Api.Tests/Features/DynamicCrud/UpdateRecordIntegrationTests.cs]
- [x] [Review][Patch] `BuildUpdateQuery_SystemColumnsInPayloadNotInSetClause` test does not actually pass system column names in `coercedPayload` — the test title claims to verify the builder independently excludes system columns, but the payload dict only contains the user column `"title"`; pass `"id"`, `"created_at"`, etc. in the payload to make the assertion meaningful [src/FormForge.Api.Tests/Features/DynamicCrud/DynamicQueryBuilderTests.cs]
- [x] [Review][Patch] `PutRecordAsync` test helper does not URL-encode `designerId` — production `updateRecordApi.ts` uses `encodeURIComponent`; test paths use raw string interpolation; any future test with a non-ASCII designer ID will silently misbehave [src/FormForge.Api.Tests/Features/DynamicCrud/UpdateRecordIntegrationTests.cs]
- [x] [Review][Patch] `updatedBy` assertion in happy-path test is an indirect proxy (`createdBy == updatedBy`) — it passes only because the same actor creates and updates; independently verify `updatedBy` equals the JWT actor UUID extracted from the token [src/FormForge.Api.Tests/Features/DynamicCrud/UpdateRecordIntegrationTests.cs]

- [x] [Review][Defer] SELECT→UPDATE race window with no transaction / `SELECT FOR UPDATE` — explicitly in story deferred items §1 [src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs] — deferred, pre-existing
- [x] [Review][Defer] Audit INSERT not transactionally atomic with Dapper UPDATE — explicitly in story deferred items §2 [src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs] — deferred, pre-existing
- [x] [Review][Defer] Non-object request body not guarded before payloadValidator — pre-existing deferred from Story 6.3 [src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs] — deferred, pre-existing
- [x] [Review][Defer] `DateTimeOffset.UtcNow` captured in `BuildUpdateQuery` before DB execution — client-side timestamp subject to clock skew; design choice consistent with `CreateRecordHandler` [src/FormForge.Api/Features/DynamicCrud/DynamicQueryBuilder.cs] — deferred, pre-existing
- [x] [Review][Defer] No typed error surface in `updateRecordApi.ts` — 422/404 not mapped to typed errors; consistent with other `*Api.ts` modules [web/src/features/data-entry/updateRecordApi.ts] — deferred, pre-existing
- [x] [Review][Defer] `RootElementParser.ParseFull(null)` caches empty columns when `ComponentSchemaVersions` row is missing — pre-existing from Stories 6.1–6.3 [src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs] — deferred, pre-existing
- [x] [Review][Defer] No AC-9 rate-limit (429 / Retry-After) integration test — explicitly deferred in story completion notes; same gap as Story 6.3 [src/FormForge.Api.Tests/Features/DynamicCrud/UpdateRecordIntegrationTests.cs] — deferred, pre-existing
- [x] [Review][Defer] No AC-10 commandTimeout integration test — explicitly deferred in story completion notes; same gap as Story 6.3 [src/FormForge.Api.Tests/Features/DynamicCrud/UpdateRecordIntegrationTests.cs] — deferred, pre-existing
