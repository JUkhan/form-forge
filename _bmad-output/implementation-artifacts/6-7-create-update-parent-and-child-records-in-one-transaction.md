# Story 6.7: Create/Update Parent and Child Records in One Transaction

Status: done

## Story

As a Content Editor,
I want to submit a form with Repeater sections in a single save,
so that parent and child data land atomically.

## Acceptance Criteria

**AC-1 — POST: parent + children inserted in one transaction**
**Given** a provisioned parent designer with at least one child Repeater designer
**When** I POST `/api/data/{parentDesignerId}` with `{ ...parentFields, children: { [childDesignerId]: [{ ...childFields }] } }`
**Then** the response is HTTP 201 with the parent DynamicRecord (same shape as Story 6.3)
**And** the parent row is inserted first; each child row is inserted with `parent_{parentDesignerId}_id` = the new parent's `id`
**And** all inserts run in a single `NpgsqlTransaction`; if any INSERT fails the transaction rolls back and no rows persist

**AC-2 — POST: `children` key absent → backward-compatible**
**Given** a POST payload without the `children` key (or with `children: {}`)
**When** the server processes the request
**Then** the existing single-table INSERT path runs (same as Story 6.3), no transaction opened

**AC-3 — POST: multiple children in one designer array**
**Given** a POST payload with `children: { childDesignerId: [{ ...record1 }, { ...record2 }] }`
**When** the server processes the request
**Then** both child rows are inserted in the same transaction
**And** both child rows have `parent_{parentDesignerId}_id` = new parent's `id`

**AC-4 — PUT: UPDATE existing child (id present in payload)**
**Given** an existing parent record and an existing child record
**When** I PUT `/api/data/{parentDesignerId}/{parentId}` with `children: { childDesignerId: [{ id: "{childId}", fieldKey: "updated" }] }`
**Then** the child record is partially updated (same semantics as Story 6.4: only supplied fields updated, `updated_at`/`updated_by` set server-side, WHERE `is_deleted = false`)
**And** the child UPDATE runs in the same `NpgsqlTransaction` as the parent UPDATE

**AC-5 — PUT: INSERT new child (no `id` in child payload)**
**Given** an existing parent record
**When** I PUT with `children: { childDesignerId: [{ fieldKey: "new value" }] }` (child entry has no `id`)
**Then** a new child record is inserted with `parent_{parentDesignerId}_id` = `parentId`
**And** the INSERT runs in the same `NpgsqlTransaction`

**AC-6 — PUT: SOFT-DELETE omitted child**
**Given** an existing parent record linked to 2 non-deleted child records (`childId1`, `childId2`)
**When** I PUT with `children: { childDesignerId: [{ id: "{childId1}", ... }] }` (`childId2` absent from array)
**Then** `childId2` is soft-deleted (`is_deleted = true`, `updated_at`/`updated_by` refreshed, `cascade_event_id = NULL`)
**And** the SOFT-DELETE runs in the same transaction
**And** `childId2` gets `cascade_event_id = NULL` (individual soft-delete — no cascade chain)

**AC-7 — PUT: mixed operations in one transaction**
**Given** a parent with 2 existing children and a PUT payload that updates one child, inserts a new one, and omits the other
**When** the server processes the request
**Then** the UPDATE, INSERT, and SOFT-DELETE all execute atomically in one `NpgsqlTransaction` with the parent UPDATE

**AC-8 — PUT: `children` key absent → backward-compatible**
**Given** a PUT payload without the `children` key
**When** the server processes the request
**Then** the existing partial-update path runs unchanged (same as Story 6.4, no transaction overhead)

**AC-9 — Validation failure in child field type → 422, nothing committed**
**Given** a child payload with a type mismatch (e.g., a string for a NUMERIC column)
**When** I POST or PUT
**Then** the response is HTTP 422 with `ValidationProblemDetails` containing the offending field path
**And** no rows are inserted or updated in any table (validation runs before any DB connection opens)

**AC-10 — Child designerId not in parent's Repeater schema → 422**
**Given** a `children` payload referencing a `childDesignerId` that is NOT listed in the parent designer's `ChildRepeaterDesignerIds`
**When** I POST or PUT
**Then** the response is HTTP 422 with `code: "VALIDATION_FAILED"`, message indicating invalid child designerId
**And** no rows are written

**AC-11 — Child designerId has no Published version → 404**
**Given** a `children` payload referencing a valid `childDesignerId` that is in the parent schema but has no `Status == "Published"` version in `component_schema_versions`
**When** I POST or PUT
**Then** the response is HTTP 404 with `code: "TABLE_NOT_PROVISIONED"`

**AC-12 — Submitted child `id` not found among parent's non-deleted children → 404**
**Given** a PUT `children` entry containing `{ id: "{unknownOrDeletedChildId}", ... }`
**When** the server queries the child table and the id does not correspond to a non-deleted row linked to this parent
**Then** the response is HTTP 404 with `code: "CHILD_NOT_FOUND"`, `messageKey: "errors.childNotFound"`
**And** no rows are written

**AC-13 — Mutation audit log: one row per record touched**
**Given** a successful POST with parent + 2 children
**When** the transaction commits
**Then** `mutation_audit_log` has 3 rows: `operation = "CREATE"` for the parent, `operation = "CREATE"` for each child
**And** each row contains `actor_id`, `timestamp`, `correlation_id`, `new_values` (non-null for CREATE), `previous_values = null` for INSERT operations
**Given** a successful PUT with UPDATE+INSERT+SOFT-DELETE of children
**Then** corresponding `"UPDATE"`, `"CREATE"`, `"SOFT_DELETE"` audit rows are appended (one per record)
**And** audit rows are inserted via EF in a separate transaction AFTER the Dapper transaction commits (Decision 1.6)

**AC-14 — All parent-level ACs from Stories 6.3 and 6.4 are preserved**
- TABLE_NOT_PROVISIONED → 404 (parent designer not bound/provisioned)
- Invalid SafeIdentifier → 422 VALIDATION_FAILED
- Parent payload type mismatch → 422 ValidationProblemDetails
- Record not found (PUT) → 404 NOT_FOUND
- Record soft-deleted (PUT) → 422 RECORD_DELETED
- FORBIDDEN → 403 (no `canCreate` / `canUpdate`)
- Rate limiting → 429 `Retry-After: 60` (data-write, 60 req/min)

**AC-15 — `commandTimeout: 5` on all Dapper calls**
Every `CommandDefinition` — SELECTs, INSERTs, UPDATEs — uses `commandTimeout: 5` (Decision 1.6)

**AC-16 — Response: parent DynamicRecord, camelCase system columns**
The 201 (POST) and 200 (PUT) response bodies contain the parent record only, with AR-46 Option C camelCase system columns. Children are fetchable via `GET /{id}?include=children` after TanStack Query invalidation.

---

## Tasks / Subtasks

- [x] **Task 1 — Add `BuildChildInsertQuery` to `DynamicQueryBuilder.cs`** (AC: 1, 3, 5, 7, 15)
  - [x] Modify `src/FormForge.Api/Features/DynamicCrud/DynamicQueryBuilder.cs`
  - Add after `BuildRestoreCascadeChildrenQuery` (last method before `BuildFkColumnName`):

  ```csharp
  // Story 6.7 — INSERT for child records in the Repeater nested-write flow.
  // Identical structure to BuildInsertQuery but inserts the FK column that links
  // the child to its parent. fkColumnName is produced by BuildFkColumnName(parentDesignerId).
  // The FK column is NOT in childUserColumns (it is a schema-relationship column created
  // by DdlEmitter during provisioning, not a user fieldKey), so it is injected explicitly.
  // cascade_event_id = NULL literal (same pattern as BuildInsertQuery — avoids Dapper
  // Npgsql UUID null-parameter edge case).
  internal static (string Sql, DynamicParameters Parameters, Guid NewRecordId, DateTimeOffset InsertedAt)
      BuildChildInsertQuery(
          SafeIdentifier childTableName,
          IReadOnlyList<ColumnDefinition> childUserColumns,
          IReadOnlyDictionary<string, object?> coercedPayload,
          string fkColumnName,
          Guid parentId,
          Guid? actorId)
  {
      ArgumentNullException.ThrowIfNull(childTableName);
      ArgumentNullException.ThrowIfNull(childUserColumns);
      ArgumentNullException.ThrowIfNull(coercedPayload);
      ArgumentException.ThrowIfNullOrEmpty(fkColumnName);

      var newId      = Guid.NewGuid();
      var insertedAt = DateTimeOffset.UtcNow;

      var presentUserCols = new List<ColumnDefinition>(childUserColumns.Count);
      foreach (var col in childUserColumns)
      {
          if (coercedPayload.ContainsKey(col.ColumnName))
              presentUserCols.Add(col);
      }

      var colSb = new StringBuilder(256);
      colSb.Append("\"id\", \"created_at\", \"created_by\", \"updated_at\", \"updated_by\", \"is_deleted\", \"cascade_event_id\"");
      colSb.Append(CultureInfo.InvariantCulture, $", \"{fkColumnName}\"");
      foreach (var col in presentUserCols)
          colSb.Append(CultureInfo.InvariantCulture, $", \"{col.ColumnName}\"");

      var valSb = new StringBuilder(256);
      valSb.Append("@p_id, @p_created_at, @p_created_by, @p_updated_at, @p_updated_by, false, NULL");
      valSb.Append(", @p_fk_parent_id");
      foreach (var col in presentUserCols)
          valSb.Append(CultureInfo.InvariantCulture, $", @f_{col.ColumnName}");

      var sql = $"INSERT INTO \"{childTableName.Value}\" ({colSb}) VALUES ({valSb})";

      var parameters = new DynamicParameters();
      parameters.Add("p_id",          newId);
      parameters.Add("p_created_at",  insertedAt);
      parameters.Add("p_created_by",  actorId);
      parameters.Add("p_updated_at",  insertedAt);
      parameters.Add("p_updated_by",  actorId);
      parameters.Add("p_fk_parent_id", parentId);

      foreach (var col in presentUserCols)
          parameters.Add($"f_{col.ColumnName}", coercedPayload[col.ColumnName]);

      return (sql, parameters, newId, insertedAt);
  }
  ```

- [x] **Task 2 — Create `RepeaterWriteCoordinator.cs`** (AC: 1, 3–7, 9–13, 15)
  - [x] Create `src/FormForge.Api/Features/DynamicCrud/RepeaterWriteCoordinator.cs`

  ```csharp
  using System.Text.Json;
  using Dapper;
  using FormForge.Api.Common;
  using FormForge.Api.Features.Designer;
  using FormForge.Api.Features.SchemaRegistry;
  using Microsoft.AspNetCore.Http;
  using Npgsql;

  namespace FormForge.Api.Features.DynamicCrud;

  // Story 6.7 — orchestrates transactional nested writes for POST and PUT
  // /api/data/{parentDesignerId}[/{id}] when the payload includes a `children` key.
  // Design principles:
  //   - Validation (payload type-checking, id existence) runs BEFORE any DB connection
  //     opens, so early 422/404 returns never need a rollback.
  //   - All DB writes run within a caller-supplied NpgsqlTransaction.
  //   - Audit entries are assembled in-memory and returned to the handler for EF
  //     persistence after the Dapper tx commits (Decision 1.6).
  internal static class RepeaterWriteCoordinator
  {
      // One audit entry per record touched (INSERT, UPDATE, or SOFT_DELETE).
      internal sealed record WriteAuditEntry(
          string DesignerId,
          Guid RecordId,
          string Operation,
          string? NewValuesJson,
          string? PreviousValuesJson,
          DateTimeOffset Timestamp);

      // Parsed child descriptor after validation.
      internal sealed record ChildRecord(
          Guid? ExistingId,
          IReadOnlyDictionary<string, object?> CoercedValues);

      // Extracts the `children` property from a request body JsonElement.
      // Returns an empty dict when the key is absent, the body is not an object,
      // or `children` is not a JSON object — enabling backward-compatible no-op.
      internal static Dictionary<string, JsonElement> ParseChildrenElement(JsonElement body)
      {
          var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
          if (body.ValueKind != JsonValueKind.Object) return result;
          if (!body.TryGetProperty("children", out var childrenEl)
              || childrenEl.ValueKind != JsonValueKind.Object) return result;
          foreach (var prop in childrenEl.EnumerateObject())
          {
              if (prop.Value.ValueKind == JsonValueKind.Array)
                  result[prop.Name] = prop.Value;
          }
          return result;
      }

      // Validates and parses raw child JSON arrays into typed ChildRecord lists.
      // Each child element is validated via payloadValidator against its schema columns.
      // If any validation fails, validationError is set and the method returns false.
      // `id` in child elements is extracted before validation (system column, not in
      // userColumns, so the validator would silently drop it — we must read it first).
      internal static bool TryParseAndValidateChildren(
          Dictionary<string, JsonElement> childrenByDesignerId,
          Dictionary<string, SchemaRegistryEntry> childSchemas,
          IDynamicPayloadValidator payloadValidator,
          out Dictionary<string, List<ChildRecord>> parsedChildren,
          out IResult? validationError)
      {
          parsedChildren = new Dictionary<string, List<ChildRecord>>(StringComparer.Ordinal);
          validationError = null;

          foreach (var (designerId, arrayEl) in childrenByDesignerId)
          {
              if (!childSchemas.TryGetValue(designerId, out var schema)) continue;
              var childList = new List<ChildRecord>();
              var idx = 0;

              foreach (var element in arrayEl.EnumerateArray())
              {
                  // Extract `id` before the validator sees it — it is a system column
                  // that the validator would silently drop if left in the element.
                  Guid? existingId = null;
                  if (element.TryGetProperty("id", out var idProp)
                      && idProp.ValueKind == JsonValueKind.String
                      && Guid.TryParse(idProp.GetString(), out var parsedId))
                  {
                      existingId = parsedId;
                  }

                  var validation = payloadValidator.Validate(element, schema.Columns);
                  if (!validation.IsValid)
                  {
                      // Prefix field paths with child context so the client knows
                      // which record in which designer array failed.
                      var prefixed = new Dictionary<string, string[]>(StringComparer.Ordinal);
                      foreach (var (k, v) in validation.FieldErrors)
                          prefixed[$"children[{designerId}][{idx}].{k}"] = v;

                      validationError = Results.ValidationProblem(
                          prefixed,
                          statusCode: StatusCodes.Status422UnprocessableEntity,
                          extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                          {
                              ["code"] = "VALIDATION_FAILED",
                              ["messageKey"] = "errors.validationFailed",
                          });
                      return false;
                  }

                  childList.Add(new ChildRecord(existingId, validation.CoercedValues));
                  idx++;
              }

              parsedChildren[designerId] = childList;
          }

          return true;
      }

      // Inserts all parsed child records (POST path). Called within the
      // caller-supplied NpgsqlTransaction. parentDesignerId is used to derive
      // the FK column name via BuildFkColumnName. Returns audit entries for the
      // handler to persist via EF after commit.
      internal static async Task<IReadOnlyList<WriteAuditEntry>> InsertChildrenAsync(
          string parentDesignerId,
          Guid parentId,
          Dictionary<string, List<ChildRecord>> parsedChildren,
          Dictionary<string, SchemaRegistryEntry> childSchemas,
          Guid? actorId,
          NpgsqlConnection conn,
          NpgsqlTransaction tx,
          CancellationToken ct)
      {
          ArgumentNullException.ThrowIfNull(parsedChildren);
          ArgumentNullException.ThrowIfNull(childSchemas);
          ArgumentNullException.ThrowIfNull(conn);
          ArgumentNullException.ThrowIfNull(tx);

          var auditEntries = new List<WriteAuditEntry>();
          var fkColumnName = DynamicQueryBuilder.BuildFkColumnName(parentDesignerId);

          foreach (var (designerId, childList) in parsedChildren)
          {
              if (!childSchemas.TryGetValue(designerId, out var schema)) continue;
              if (!SafeIdentifier.TryCreate(designerId, out var childSafeId, out _)) continue;

              foreach (var child in childList)
              {
                  var (sql, parameters, newId, insertedAt) = DynamicQueryBuilder.BuildChildInsertQuery(
                      childSafeId, schema.Columns, child.CoercedValues, fkColumnName, parentId, actorId);

                  await conn.ExecuteAsync(new CommandDefinition(
                      sql, parameters, transaction: tx,
                      commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

                  auditEntries.Add(new WriteAuditEntry(
                      designerId, newId, "CREATE",
                      child.CoercedValues.Count > 0 ? JsonSerializer.Serialize(child.CoercedValues) : null,
                      null,
                      insertedAt));
              }
          }

          return auditEntries;
      }

      // UPSERT + prune child records (PUT path). Called within the caller-supplied
      // NpgsqlTransaction. Relies on pre-flight existingChildIdsByDesignerId computed
      // by the handler before the transaction opened (SELECT by FK, is_deleted=false).
      // INSERT children with no ExistingId; UPDATE children with ExistingId present in
      // existingChildIds; SOFT-DELETE existing children whose IDs are absent from the
      // submitted set. Returns audit entries for EF persistence after commit.
      // PRECONDITION: The handler has already verified that every ChildRecord.ExistingId
      // appears in existingChildIdsByDesignerId (i.e., AC-12 check passed pre-flight).
      internal static async Task<IReadOnlyList<WriteAuditEntry>> UpsertAndPruneChildrenAsync(
          string parentDesignerId,
          Guid parentId,
          Dictionary<string, List<ChildRecord>> parsedChildren,
          Dictionary<string, HashSet<Guid>> existingChildIdsByDesignerId,
          Dictionary<string, SchemaRegistryEntry> childSchemas,
          Guid? actorId,
          NpgsqlConnection conn,
          NpgsqlTransaction tx,
          CancellationToken ct)
      {
          ArgumentNullException.ThrowIfNull(parsedChildren);
          ArgumentNullException.ThrowIfNull(existingChildIdsByDesignerId);
          ArgumentNullException.ThrowIfNull(childSchemas);
          ArgumentNullException.ThrowIfNull(conn);
          ArgumentNullException.ThrowIfNull(tx);

          var auditEntries = new List<WriteAuditEntry>();
          var fkColumnName = DynamicQueryBuilder.BuildFkColumnName(parentDesignerId);

          foreach (var (designerId, childList) in parsedChildren)
          {
              if (!childSchemas.TryGetValue(designerId, out var schema)) continue;
              if (!SafeIdentifier.TryCreate(designerId, out var childSafeId, out _)) continue;

              existingChildIdsByDesignerId.TryGetValue(designerId, out var existingIds);
              existingIds ??= [];

              var toUpdate = childList.Where(c => c.ExistingId.HasValue).ToList();
              var toInsert = childList.Where(c => !c.ExistingId.HasValue).ToList();
              var submittedIds = toUpdate.Select(c => c.ExistingId!.Value).ToHashSet();

              // INSERT new children (no ExistingId)
              foreach (var child in toInsert)
              {
                  var (sql, parameters, newId, insertedAt) = DynamicQueryBuilder.BuildChildInsertQuery(
                      childSafeId, schema.Columns, child.CoercedValues, fkColumnName, parentId, actorId);

                  await conn.ExecuteAsync(new CommandDefinition(
                      sql, parameters, transaction: tx,
                      commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

                  auditEntries.Add(new WriteAuditEntry(
                      designerId, newId, "CREATE",
                      child.CoercedValues.Count > 0 ? JsonSerializer.Serialize(child.CoercedValues) : null,
                      null, insertedAt));
              }

              // UPDATE existing children — capture previousValues from a SELECT first
              foreach (var child in toUpdate)
              {
                  var (getPrevSql, getPrevParams) = DynamicQueryBuilder.BuildGetByIdQuery(
                      childSafeId, schema.Columns, child.ExistingId!.Value);
                  var rawPrevRows = await conn.QueryAsync(new CommandDefinition(
                      getPrevSql, getPrevParams, transaction: tx,
                      commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

                  var prevRow = rawPrevRows
                      .Cast<IDictionary<string, object>>()
                      .Select(r => r.ToDictionary(k => k.Key, k => (object?)k.Value, StringComparer.Ordinal))
                      .FirstOrDefault();

                  var previousValues = new Dictionary<string, object?>(StringComparer.Ordinal);
                  if (prevRow != null)
                  {
                      foreach (var colName in child.CoercedValues.Keys)
                          previousValues[colName] = prevRow.TryGetValue(colName, out var prev) ? prev : null;
                  }

                  var (updateSql, updateParams, updatedAt) = DynamicQueryBuilder.BuildUpdateQuery(
                      childSafeId, schema.Columns, child.CoercedValues, child.ExistingId!.Value, actorId);

                  await conn.ExecuteAsync(new CommandDefinition(
                      updateSql, updateParams, transaction: tx,
                      commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

                  auditEntries.Add(new WriteAuditEntry(
                      designerId, child.ExistingId.Value, "UPDATE",
                      child.CoercedValues.Count > 0 ? JsonSerializer.Serialize(child.CoercedValues) : null,
                      previousValues.Count > 0 ? JsonSerializer.Serialize(previousValues) : null,
                      updatedAt));
              }

              // SOFT-DELETE omitted children (cascade_event_id = NULL — individual soft-delete)
              var toSoftDelete = existingIds.Except(submittedIds).ToList();
              foreach (var omittedId in toSoftDelete)
              {
                  var (deleteSql, deleteParams, deletedAt) = DynamicQueryBuilder.BuildSoftDeleteByIdQuery(
                      childSafeId, omittedId, actorId, cascadeEventId: null);

                  await conn.ExecuteAsync(new CommandDefinition(
                      deleteSql, deleteParams, transaction: tx,
                      commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

                  auditEntries.Add(new WriteAuditEntry(
                      designerId, omittedId, "SOFT_DELETE", null, null, deletedAt));
              }
          }

          return auditEntries;
      }
  }
  ```

- [x] **Task 3 — Extend `CreateRecordHandler` in `DynamicDataEndpoints.cs`** (AC: 1–3, 9–11, 13–16)
  - [x] Modify `src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs`

  **3a — Add `Problems.ChildNotFound()` to the `Problems` inner class** (after `RecordNotDeleted()`):
  ```csharp
  // Story 6.7 — PUT children array contained an id that does not correspond to
  // a non-deleted child row linked to this parent.
  internal static IResult ChildNotFound() =>
      Results.Problem(
          title: "Child record not found",
          statusCode: StatusCodes.Status404NotFound,
          extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
          {
              ["code"] = "CHILD_NOT_FOUND",
              ["messageKey"] = "errors.childNotFound",
          });
  ```

  **3b — Extend `CreateRecordHandler` signature** — add `IDynamicPayloadValidator payloadValidator` (already there) and no new params needed. The handler logic changes are:

  After `var actorId = ...`, add the children extraction + schema loading + validation block. Then the DB section branches on whether `parsedChildren` is non-empty:

  **Structure of the extended `CreateRecordHandler`** (replace the DB section starting at `var conn = ...`):

  ```csharp
  // --- Children extraction + pre-flight (before any DB connection) ---
  var childrenRaw = RepeaterWriteCoordinator.ParseChildrenElement(body);

  // Filter to only designer IDs that appear in the parent schema's Repeater list.
  // Unknown child designerIds → AC-10 (VALIDATION_FAILED).
  var filteredChildren = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
  foreach (var (childId, arrayEl) in childrenRaw)
  {
      if (!entry.ChildRepeaterDesignerIds.Contains(childId))
      {
          return Problems.ValidationFailed(
              $"Child designer '{childId}' is not a Repeater child of this designer.");
      }
      filteredChildren[childId] = arrayEl;
  }

  // Load child schemas from EF (latest Published version) for all child designers.
  // AC-11: if any child has no Published version → 404 TABLE_NOT_PROVISIONED.
  var childSchemas = new Dictionary<string, SchemaRegistryEntry>(StringComparer.Ordinal);
  foreach (var childDesignerId in filteredChildren.Keys)
  {
      if (!SafeIdentifier.TryCreate(childDesignerId, out var childSafeId, out _))
          return Problems.ValidationFailed($"Invalid child designer identifier: {childDesignerId}");

      var childRootJson = await db.ComponentSchemaVersions
          .AsNoTracking()
          .Where(v => v.DesignerId == childSafeId!.Value && v.Status == "Published")
          .OrderByDescending(v => v.Version)
          .Select(v => v.RootElement)
          .FirstOrDefaultAsync(ct)
          .ConfigureAwait(false);

      if (childRootJson is null)
          return Problems.TableNotProvisioned();

      var (childColumns, childChildIds) = RootElementParser.ParseFull(childRootJson);
      var childEntry = new SchemaRegistryEntry(
          childSafeId!.Value, 0, childColumns, childChildIds, DateTimeOffset.UtcNow);
      childSchemas[childDesignerId] = childEntry;
  }

  // Validate + parse child payloads. AC-9: type mismatch → 422 before DB opens.
  if (!RepeaterWriteCoordinator.TryParseAndValidateChildren(
          filteredChildren, childSchemas, payloadValidator,
          out var parsedChildren, out var childValidationError))
  {
      return childValidationError!;
  }

  // --- DB section ---
  bool hasChildren = parsedChildren.Count > 0
      && parsedChildren.Values.Any(list => list.Count > 0);

  var (sql, parameters, newRecordId, insertedAt) = DynamicQueryBuilder.BuildInsertQuery(
      safeId, entry.Columns, validationResult.CoercedValues, actorId);

  IReadOnlyList<RepeaterWriteCoordinator.WriteAuditEntry> childAuditEntries =
      Array.Empty<RepeaterWriteCoordinator.WriteAuditEntry>();

  var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
  try
  {
      if (hasChildren)
      {
          // AC-1: wrap parent INSERT + child INSERTs in one transaction.
          var tx = await ((NpgsqlConnection)conn).BeginTransactionAsync(ct).ConfigureAwait(false);
          try
          {
              await conn.ExecuteAsync(new CommandDefinition(
                  sql, parameters, transaction: tx,
                  commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

              childAuditEntries = await RepeaterWriteCoordinator.InsertChildrenAsync(
                  safeId!.Value, newRecordId, parsedChildren, childSchemas,
                  actorId, (NpgsqlConnection)conn, tx, ct).ConfigureAwait(false);

              await tx.CommitAsync(ct).ConfigureAwait(false);
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
      else
      {
          // AC-2: no children → existing single-INSERT path, no transaction overhead.
          await conn.ExecuteAsync(new CommandDefinition(
              sql, parameters, commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);
      }
  }
  finally
  {
      await conn.DisposeAsync().ConfigureAwait(false);
  }

  // AC-13: audit rows (parent + each child) via EF after Dapper tx commits (Decision 1.6).
  var correlationId = httpContext.GetCorrelationId();
  var newValuesJson = JsonSerializer.Serialize(validationResult.CoercedValues);
  db.MutationAuditLog.Add(new Domain.Entities.MutationAuditLogEntry
  {
      DesignerId    = safeId!.Value,
      RecordId      = newRecordId,
      Operation     = "CREATE",
      ActorId       = actorId,
      Timestamp     = insertedAt,
      NewValues     = newValuesJson,
      CorrelationId = correlationId,
  });
  foreach (var childAudit in childAuditEntries)
  {
      db.MutationAuditLog.Add(new Domain.Entities.MutationAuditLogEntry
      {
          DesignerId     = childAudit.DesignerId,
          RecordId       = childAudit.RecordId,
          Operation      = childAudit.Operation,
          ActorId        = actorId,
          Timestamp      = childAudit.Timestamp,
          NewValues      = childAudit.NewValuesJson,
          PreviousValues = childAudit.PreviousValuesJson,
          CorrelationId  = correlationId,
      });
  }
  await db.SaveChangesAsync(ct).ConfigureAwait(false);
  // ... rest of response assembly unchanged ...
  ```

  **Important:** The `conn` returned by `connectionFactory.CreateOpenConnectionAsync` is a `NpgsqlConnection` at runtime (see Dev Notes §1). Cast is needed only for `BeginTransactionAsync`. Follow the same pattern as `RestoreRecordHandler`/`DeleteRecordHandler`.

- [x] **Task 4 — Extend `UpdateRecordHandler` in `DynamicDataEndpoints.cs`** (AC: 4–8, 12–16)
  - [x] Modify `src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs`

  **Structure change in `UpdateRecordHandler`:** after the parent-level payload validation and before the DB section, add the same children extraction + schema loading + child payload validation block (same pattern as Task 3 above).

  Then replace the DB section with:
  ```csharp
  // Pre-flight: SELECT existing child IDs by FK (before tx opens) for determining
  // which children to SOFT-DELETE and validating submitted child ids. AC-12 check.
  var existingChildIdsByDesignerId = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
  if (hasChildren)
  {
      var prefightConn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
      try
      {
          foreach (var (childDesignerId, childList) in parsedChildren)
          {
              if (!SafeIdentifier.TryCreate(childDesignerId, out var childSafeId2, out _)) continue;
              var fkCol = DynamicQueryBuilder.BuildFkColumnName(safeId!.Value);
              var (selSql, selParams) = DynamicQueryBuilder.BuildSelectChildIdsByFkQuery(
                  childSafeId2, fkCol, id);
              var existingIds = (await prefightConn.QueryAsync<Guid>(new CommandDefinition(
                  selSql, selParams, commandTimeout: 5, cancellationToken: ct))
                  .ConfigureAwait(false)).ToHashSet();
              existingChildIdsByDesignerId[childDesignerId] = existingIds;

              // AC-12: verify submitted ids exist among non-deleted children of this parent.
              foreach (var childRec in childList.Where(c => c.ExistingId.HasValue))
              {
                  if (!existingIds.Contains(childRec.ExistingId!.Value))
                      return Problems.ChildNotFound();
              }
          }
      }
      finally
      {
          await prefightConn.DisposeAsync().ConfigureAwait(false);
      }
  }

  // Main DB section: SELECT parent → open tx → UPDATE parent + children.
  IResult? earlyResult = null;
  var existingRow = new Dictionary<string, object?>(StringComparer.Ordinal);
  var previousValues = new Dictionary<string, object?>(StringComparer.Ordinal);
  DateTimeOffset updatedAt = default;
  IReadOnlyList<RepeaterWriteCoordinator.WriteAuditEntry> childAuditEntries =
      Array.Empty<RepeaterWriteCoordinator.WriteAuditEntry>();

  var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
  try
  {
      // SELECT parent (same pre-check as existing UpdateRecordHandler).
      var (selectSql, selectParams) = DynamicQueryBuilder.BuildGetByIdQuery(safeId, entry.Columns, id);
      var rawRows = await conn.QueryAsync(new CommandDefinition(
          selectSql, selectParams, commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);
      var rows = rawRows
          .Cast<IDictionary<string, object>>()
          .Select(r => r.ToDictionary(k => k.Key, k => (object?)k.Value, StringComparer.Ordinal))
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
              earlyResult = Problems.RecordDeleted();
          }
          else
          {
              foreach (var colName in validationResult.CoercedValues.Keys)
                  previousValues[colName] = existingRow.TryGetValue(colName, out var prev) ? prev : null;

              var (updateSql, updateParams, ts) = DynamicQueryBuilder.BuildUpdateQuery(
                  safeId, entry.Columns, validationResult.CoercedValues, id, actorId);
              updatedAt = ts;

              if (hasChildren)
              {
                  // AC-4–7: wrap parent UPDATE + child operations in one transaction.
                  var tx = await ((NpgsqlConnection)conn).BeginTransactionAsync(ct).ConfigureAwait(false);
                  try
                  {
                      var rowsAffected = await conn.ExecuteAsync(new CommandDefinition(
                          updateSql, updateParams, transaction: tx,
                          commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

                      if (rowsAffected == 0)
                      {
                          await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                          earlyResult = Problems.RecordDeleted();
                      }
                      else
                      {
                          childAuditEntries = await RepeaterWriteCoordinator.UpsertAndPruneChildrenAsync(
                              safeId!.Value, id, parsedChildren,
                              existingChildIdsByDesignerId, childSchemas,
                              actorId, (NpgsqlConnection)conn, tx, ct).ConfigureAwait(false);

                          await tx.CommitAsync(ct).ConfigureAwait(false);

                          existingRow["updated_at"] = updatedAt;
                          existingRow["updated_by"] = actorId;
                          foreach (var (colName, val) in validationResult.CoercedValues)
                              existingRow[colName] = val;
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
              else
              {
                  // AC-8: no children → existing no-transaction UPDATE path.
                  var rowsAffected = await conn.ExecuteAsync(new CommandDefinition(
                      updateSql, updateParams, commandTimeout: 5, cancellationToken: ct))
                      .ConfigureAwait(false);

                  if (rowsAffected == 0)
                  {
                      earlyResult = Problems.RecordDeleted();
                  }
                  else
                  {
                      existingRow["updated_at"] = updatedAt;
                      existingRow["updated_by"] = actorId;
                      foreach (var (colName, val) in validationResult.CoercedValues)
                          existingRow[colName] = val;
                  }
              }
          }
      }
  }
  finally
  {
      await conn.DisposeAsync().ConfigureAwait(false);
  }

  if (earlyResult is not null) return earlyResult;

  // AC-13: audit rows via EF (Decision 1.6) — parent UPDATE + all child operations.
  var correlationId = httpContext.GetCorrelationId();
  var newValuesJson = validationResult.CoercedValues.Count > 0
      ? JsonSerializer.Serialize(validationResult.CoercedValues) : null;
  var prevValuesJson = previousValues.Count > 0
      ? JsonSerializer.Serialize(previousValues) : null;

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
  foreach (var childAudit in childAuditEntries)
  {
      db.MutationAuditLog.Add(new Domain.Entities.MutationAuditLogEntry
      {
          DesignerId     = childAudit.DesignerId,
          RecordId       = childAudit.RecordId,
          Operation      = childAudit.Operation,
          ActorId        = actorId,
          Timestamp      = childAudit.Timestamp,
          NewValues      = childAudit.NewValuesJson,
          PreviousValues = childAudit.PreviousValuesJson,
          CorrelationId  = correlationId,
      });
  }
  await db.SaveChangesAsync(ct).ConfigureAwait(false);

  return Results.Ok(new DynamicRecord(existingRow));
  ```

- [x] **Task 5 — Unit tests for `BuildChildInsertQuery`** (AC: 1, 5, 15)
  - [x] Modify `src/FormForge.Api.Tests/Features/DynamicCrud/DynamicQueryBuilderTests.cs`
  - Add 2 unit tests after the `BuildRestoreCascadeChildrenQuery` region:

  ```csharp
  // --- Story 6.7: BuildChildInsertQuery ---

  [Fact]
  public void BuildChildInsertQuery_IncludesFkColumn_WithCorrectParam()
  {
      var parentId  = Guid.NewGuid();
      var actorId   = Guid.NewGuid();
      var columns   = new List<ColumnDefinition>
      {
          new("note", "text", isUserColumn: true),
      };
      var payload = new Dictionary<string, object?>(StringComparer.Ordinal) { ["note"] = "hello" };

      var (sql, parameters, newId, insertedAt) = DynamicQueryBuilder.BuildChildInsertQuery(
          MakeTable("child_tbl"),
          columns,
          payload,
          fkColumnName: "parent_parent_tbl_id",
          parentId: parentId,
          actorId: actorId);

      // System columns present
      Assert.Contains("\"id\"",         sql, StringComparison.Ordinal);
      Assert.Contains("\"created_at\"", sql, StringComparison.Ordinal);
      Assert.Contains("\"is_deleted\"", sql, StringComparison.Ordinal);
      // FK column present in INSERT columns AND value list
      Assert.Contains("\"parent_parent_tbl_id\"", sql, StringComparison.Ordinal);
      Assert.Contains("@p_fk_parent_id",          sql, StringComparison.Ordinal);
      // cascade_event_id is SQL NULL literal (not parameter)
      Assert.Contains("cascade_event_id\" = NULL", sql, StringComparison.Ordinal);
      // User column present
      Assert.Contains("\"note\"",          sql, StringComparison.Ordinal);
      Assert.Contains("@f_note",           sql, StringComparison.Ordinal);

      Assert.Equal(parentId,  parameters.Get<Guid>("p_fk_parent_id"));
      Assert.Equal("hello",   parameters.Get<string>("f_note"));
      Assert.NotEqual(Guid.Empty, newId);
  }

  [Fact]
  public void BuildChildInsertQuery_EmptyPayload_OnlySystemAndFkColumns()
  {
      var parentId = Guid.NewGuid();
      var (sql, _, newId, _) = DynamicQueryBuilder.BuildChildInsertQuery(
          MakeTable("child_empty"),
          columns: new List<ColumnDefinition>(),
          coercedPayload: new Dictionary<string, object?>(StringComparer.Ordinal),
          fkColumnName: "parent_x_id",
          parentId: parentId,
          actorId: null);

      // Only system + FK columns — no user column params
      Assert.DoesNotContain("@f_", sql, StringComparison.Ordinal);
      Assert.Contains("@p_fk_parent_id", sql, StringComparison.Ordinal);
      Assert.NotEqual(Guid.Empty, newId);
  }
  ```

  Estimated: +2 unit tests → running total ~504

- [x] **Task 6 — Create `RepeaterWriteIntegrationTests.cs`** (AC: 1–16)
  - [x] Create `src/FormForge.Api.Tests/Features/DynamicCrud/RepeaterWriteIntegrationTests.cs`
  - Class signature: `[Collection("DynamicCrudTests")] public sealed class RepeaterWriteIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime`
  - `InitializeAsync`/`DisposeAsync`: **copy verbatim** from `SoftDeleteIntegrationTests` (same TRUNCATE statement including `mutation_audit_log`, same dynamic-table DROP loop, same `ReseedSystemRolesAsync`+`SeedTestUsersAsync`)
  - Copy all helpers from `SoftDeleteIntegrationTests`: `LoginAsync`, `PostRecordAsync`, `CreateRecordAndGetIdAsync`, `GetRecordAsync`, `CreateMenuViaApiAsync`, `CreateDesignerViaApiAsync`, `PutVersionStatusAsync`, `CreateAndPublishDesignerWithFieldsAsync`, `PutBindingAsync`, `PollUntilTerminalAsync`, `InsertParentRowAsync`, `InsertChildRowAsync`, `GetUserIdFromToken`
  - Add helper `PutRecordAsync(string token, string designerId, Guid id, object payload)`:
    ```csharp
    private async Task<HttpResponseMessage> PutRecordAsync(
        string token, string designerId, Guid id, object payload)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/data/{Uri.EscapeDataString(designerId)}/{id}")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }
    ```
  - Add shared helper `SetupParentChildDesignersAsync(string token, string parentId, string childId)` that:
    1. Creates + publishes child designer (one TextInput `note`)
    2. Creates + publishes parent designer (TextInput `title` + Repeater `rowDesignerId=childId`)
    3. Binds parent to a new menu item and polls until `Success`
    Returns `parentVersion`
    (This mirrors the cascade setup in `SoftDelete_CascadeToRepeaterChildren_DeletesChildRowsAtomically`)

  **Test cases (8 integration tests):**

  1. `Post_WithChildren_Returns201AndInsertsChildAtomically` — AC-1, AC-16:
     - Setup parent+child designers, POST `{ title: "parent", children: { childId: [{ note: "child1" }] } }`
     - Assert 201, parent id present, camelCase system columns
     - Direct Npgsql SELECT child table: row exists, FK = parent id, `is_deleted=false`

  2. `Post_ChildrenAbsent_Returns201_BackwardCompatible` — AC-2:
     - POST without `children` key → 201, no child rows inserted

  3. `Post_MultipleChildren_AllInsertedWithParentFk` — AC-3:
     - POST `{ title: "p", children: { childId: [{ note: "c1" }, { note: "c2" }] } }` → 201
     - Direct Npgsql SELECT: 2 child rows with correct FK

  4. `Put_WithChildren_UpdateExistingChild_UpdatesAtomically` — AC-4, AC-7:
     - Setup, create parent+child via POST with children
     - PUT `{ title: "updated", children: { childId: [{ id: childId, note: "updated note" }] } }` → 200
     - Direct SELECT: child row has `note = "updated note"`, `updated_at` refreshed

  5. `Put_WithChildren_InsertNewChild_InsertsAtomically` — AC-5:
     - Setup parent record, PUT `{ children: { childId: [{ note: "brand new" }] } }` → 200
     - Direct SELECT child table: new row present with FK = parentId

  6. `Put_WithChildren_OmittedChild_SoftDeleted` — AC-6:
     - Setup parent with 2 child rows inserted via DB, PUT with only child1 in children
     - Assert 200, child2 `is_deleted=true`, `cascade_event_id=NULL`

  7. `Put_WithChildren_AuditLog_AllOperationsRecorded` — AC-13:
     - Setup, POST with 1 child (gets parent CREATE + child CREATE audit rows = 2)
     - Then PUT with INSERT new child + UPDATE existing + omit third child (to SOFT-DELETE)
     - Query `mutation_audit_log`: assert 3 audit rows for the PUT (CREATE, UPDATE, SOFT_DELETE)
     - Assert each row has actor_id, correlation_id, correct operation

  8. `Post_ChildValidationFails_Returns422_NothingPersisted` — AC-9:
     - POST `{ title: "ok", children: { childId: [{ note: 99999 }] } }` where `note` is TEXT but JSON number won't pass typed validation... actually TEXT accepts anything, try a NUMERIC field. If child has a NUMERIC field, send a string → 422
     - OR: use a boolean field and send a non-boolean string
     - Assert 422 ValidationProblemDetails, direct Npgsql SELECT: no parent row, no child row

  Estimated: +8 integration tests → running total ~512

- [x] **Task 7 — Frontend: extend API clients with children support** (AC: 1, 4–5)
  - [x] Modify `web/src/features/data-entry/createRecordApi.ts` — extend `createRecord` payload type:
    ```typescript
    export interface ChildRecordPayload {
      id?: string                           // present → UPDATE semantics on PUT
      [fieldKey: string]: unknown
    }

    export interface RecordPayloadWithChildren {
      children?: Record<string, ChildRecordPayload[]>
      [fieldKey: string]: unknown
    }

    export function createRecord(
      designerId: string,
      payload: RecordPayloadWithChildren,
    ): Promise<DynamicRecord> {
      return httpClient.post<DynamicRecord>(
        `/api/data/${encodeURIComponent(designerId)}`,
        payload,
      )
    }
    ```

  - [x] Modify `web/src/features/data-entry/updateRecordApi.ts` — same payload type:
    ```typescript
    import type { RecordPayloadWithChildren } from './createRecordApi'

    export function updateRecord(
      designerId: string,
      id: string,
      payload: RecordPayloadWithChildren,
    ): Promise<DynamicRecord> {
      return httpClient.put<DynamicRecord>(
        `/api/data/${encodeURIComponent(designerId)}/${encodeURIComponent(id)}`,
        payload,
      )
    }
    ```
  - Hooks (`useCreateRecord.ts`, `useUpdateRecord.ts`) require no changes — they pass payload through. Update their `mutationFn` param types to `RecordPayloadWithChildren` for type safety.

---

## Dev Notes

### §1 — `conn` cast to `NpgsqlConnection` for `BeginTransactionAsync`

`connectionFactory.CreateOpenConnectionAsync` returns `NpgsqlConnection` at runtime (confirmed in `DbConnectionFactory.cs`). The variable is typed as `IDbConnection` (or `DbConnection`) in some handlers. `BeginTransactionAsync` is defined on `DbConnection` (not `IDbConnection`), so cast: `((NpgsqlConnection)conn).BeginTransactionAsync(ct)`. Follow the exact pattern in the existing `DeleteRecordHandler` and `RestoreRecordHandler` — those cast inline.

**Verify before implementing:** open `DynamicDataEndpoints.cs` and find how `DeleteRecordHandler` declares `conn`. If it's already typed as `NpgsqlConnection` (not `IDbConnection`), no cast is needed in the new code — just follow the same type declaration.

### §2 — `children` key is already reserved — no collision risk

Story 6.2 patch P1 added `"children"` to `PgReservedKeywords`, preventing any user from creating a `fieldKey = "children"` in the designer. This guarantees `ParseChildrenElement` never conflicts with a user column name, and the payload validator silently drops `children` without treating it as a user field.

### §3 — Child schema loading uses latest Published version (not BoundVersion)

Child designers do NOT have their own Menu binding. The child table was provisioned as a side-effect of the parent's provisioning (Story 5.5). Loading child schemas:
- Query `ComponentSchemaVersions WHERE designerId = X AND status = "Published"` ordered by `Version DESC`
- Use `RootElementParser.ParseFull` to get `(columns, childChildIds)`
- Cache in `schemaRegistry.Populate(...)` with version number from the result

**Do NOT** query `Menus` for child designers — there is no Menu entry for them. If there is no Published version → `TABLE_NOT_PROVISIONED` (AC-11).

To get the version number for registry caching, use `.Select(v => new { v.Version, v.RootElement })` instead of just `Select(v => v.RootElement)`:
```csharp
var childVersionRow = await db.ComponentSchemaVersions
    .AsNoTracking()
    .Where(v => v.DesignerId == childSafeId!.Value && v.Status == "Published")
    .OrderByDescending(v => v.Version)
    .Select(v => new { v.Version, v.RootElement })
    .FirstOrDefaultAsync(ct);

if (childVersionRow is null) return Problems.TableNotProvisioned();

var (childColumns, childChildIds) = RootElementParser.ParseFull(childVersionRow.RootElement);
var childEntry = schemaRegistry.TryGet(childSafeId!.Value, childVersionRow.Version)
    ?? new SchemaRegistryEntry(childSafeId!.Value, childVersionRow.Version, childColumns, childChildIds, DateTimeOffset.UtcNow);
schemaRegistry.Populate(childEntry);
childSchemas[childDesignerId] = childEntry;
```

### §4 — `entry.ChildRepeaterDesignerIds` contains only DIRECT Repeater children

`entry.ChildRepeaterDesignerIds` is populated by `RootElementParser.ParseFull` which reads `Repeater` component `rowDesignerId` properties from the parent schema. This list contains only direct children (not grandchildren). Story 6.7 only handles one level of nesting (direct children). Grandchild Repeater writes are a deferred item.

Validation in `CreateRecordHandler`/`UpdateRecordHandler`: for each key in the `children` dict, assert it is in `entry.ChildRepeaterDesignerIds`. If not → 422 VALIDATION_FAILED (AC-10). This prevents FK column injection attacks.

### §5 — FK column name derivation

`DynamicQueryBuilder.BuildFkColumnName(parentDesignerId)` produces `parent_{parentDesignerId[..53]}_id`. This is the column name in the child table that holds the parent's `id`. Always call this with the parent's `designerId` (the `safeId.Value`), NOT the child's designerId.

The `BuildChildInsertQuery` in Task 1 takes `fkColumnName` as a parameter so the coordinator can inject it cleanly. Compute it once in the handler from `DynamicQueryBuilder.BuildFkColumnName(safeId!.Value)` and pass through.

### §6 — Pre-flight vs in-transaction child ID validation (AC-12)

The AC-12 check ("submitted child id exists among non-deleted children of this parent") runs PRE-FLIGHT (before the main transaction opens) to keep error returns clean. There is a TOCTOU window: a concurrent SOFT-DELETE of a child between the pre-flight SELECT and the in-transaction UPDATE could cause `rowsAffected = 0` on the child UPDATE. This is acceptable (consistent with the existing parent UPDATE TOCTOU pattern in Stories 6.4/6.5 — deferred item).

The pre-flight SELECT uses `BuildSelectChildIdsByFkQuery` which already filters `is_deleted = false`.

### §7 — SOFT-DELETE of omitted children uses individual semantics (`cascade_event_id = NULL`)

Omitting a child from the PUT `children` array triggers an individual soft-delete (not cascade). `BuildSoftDeleteByIdQuery(childSafeId, omittedId, actorId, cascadeEventId: null)` sets `cascade_event_id = NULL`. This is correct: these children are being explicitly removed by the editor, not cascade-deleted due to a parent deletion. Their `cascade_event_id = NULL` state means a future restore of the parent (Story 6.6) will NOT accidentally restore them.

### §8 — No new EF migration required

Story 6.7 adds no new tables or columns. `mutation_audit_log`, child tables, and all system columns already exist. No `dotnet ef migrations add` step.

### §9 — `RepeaterWriteCoordinator` is a `static` class — no DI registration

Like `SoftDeleteCascade` (which is also `internal static`), `RepeaterWriteCoordinator` takes all dependencies as method parameters. No `Program.cs` registration required.

### §10 — Audit rows: one EF `SaveChangesAsync` for parent + all children

All audit entries (parent + children) are added to `db.MutationAuditLog` before calling `await db.SaveChangesAsync(ct)` once. This is a batch insert within a single EF-managed transaction, consistent with Decision 1.6. The EF transaction is separate from the Dapper transaction per the established pattern.

### §11 — `UpdateRecordHandler` pre-flight uses a separate connection for child ID SELECT

The pre-flight child-ID SELECT (Task 4) opens a separate connection (for clarity and to avoid lifetime issues before the main connection + tx). This adds a connection per child designer to the request path. This is acceptable for v1 (Repeater nesting is typically 1 level, 1 child designer). Mark as a deferred optimization (batch child-ID SELECTs with one multi-table query).

### §12 — `parsedChildren` check: `parsedChildren.Count > 0 && .Any(list => list.Count > 0)`

An empty `children: {}` in the body (or all arrays empty) results in `parsedChildren` being empty or containing empty lists. The `hasChildren` flag handles this: if `parsedChildren` is empty or all lists are empty, fall through to the existing no-children path (backward-compatible, no transaction).

### §13 — Integration test: child NUMERIC field for AC-9 validation failure test

In `SoftDeleteIntegrationTests.SetupProvisionedDesignerWithTitleAsync`, the schema has a `TextInput` with `fieldKey = "title"`. For the AC-9 test, create a designer with a `NumberInput` (or use `fieldKey` with PgType `NUMERIC`). Sending an invalid string like `"not-a-number"` for a NUMERIC column triggers Layer 2 validation failure via `IDynamicPayloadValidator.TryGetDecimal`. Alternatively, use a BOOLEAN field and send a string like `"not-a-bool"`.

**Simplest approach**: define the child designer with a `fieldKey = "count"` mapped to a NUMERIC type in its schema JSON. Send `children: { childId: [{ count: "invalid-number" }] }` → triggers 422. Verify no parent or child rows inserted via direct Npgsql SELECT.

### §14 — `SchemaRegistryEntry` constructor for child entries

The `SchemaRegistryEntry` constructor takes `(string designerId, int version, IReadOnlyList<ColumnDefinition> columns, IReadOnlyList<string> childIds, DateTimeOffset loadedAt)`. For child schemas loaded by version number from the query (§3), pass the actual version. Don't pass `0` for version — use `childVersionRow.Version` so the registry cache works correctly across requests.

---

### File locations — new files

| File | Path |
|---|---|
| `RepeaterWriteCoordinator.cs` | `src/FormForge.Api/Features/DynamicCrud/RepeaterWriteCoordinator.cs` |
| `RepeaterWriteIntegrationTests.cs` | `src/FormForge.Api.Tests/Features/DynamicCrud/RepeaterWriteIntegrationTests.cs` |

### File locations — modified files

| File | Change |
|---|---|
| `DynamicQueryBuilder.cs` | Add `BuildChildInsertQuery` |
| `DynamicDataEndpoints.cs` | Extend `CreateRecordHandler`, `UpdateRecordHandler`, add `Problems.ChildNotFound()` |
| `DynamicQueryBuilderTests.cs` | Add 2 unit tests for `BuildChildInsertQuery` |
| `createRecordApi.ts` | Add `RecordPayloadWithChildren`, `ChildRecordPayload` types; update signature |
| `updateRecordApi.ts` | Import and use `RecordPayloadWithChildren` |
| `useCreateRecord.ts` | Update `mutationFn` param type to `RecordPayloadWithChildren` |
| `useUpdateRecord.ts` | Update `mutationFn` param type to `RecordPayloadWithChildren` |

No new EF migration. No changes to: `SoftDeleteCascade.cs`, `DynamicPayloadValidator.cs`, `DynamicRecord.cs`, `DynamicRecordJsonConverter.cs`, `Program.cs`, `MutationAuditLogEntry.cs`, `FormForgeDbContext.cs`.

---

### Deferred items (record for code review)

1. **Grandchild Repeater writes not supported** — `entry.ChildRepeaterDesignerIds` contains only direct children. If a child schema itself has Repeaters, those grandchildren are not handled in Story 6.7. Owner: future story.
2. **Pre-flight child-ID SELECT TOCTOU** — a concurrent SOFT-DELETE of a child between the pre-flight SELECT and in-transaction UPDATE could cause 0 rowsAffected on the child UPDATE. Handler does not detect this case specifically (UPDATE proceeds, 0-rowsAffected for a child is silently accepted). Owner: hardening pass.
3. **Audit INSERT not transactionally atomic with Dapper writes** — same deferred item as Stories 6.3–6.6. Owner: outbox pattern.
4. **Multiple child designers: separate pre-flight connections** — each child designer's pre-flight SELECT opens its own connection (§11). Owner: batch query optimization.
5. **Response includes parent record only** — clients must follow up with `GET /{id}?include=children` to see inserted/updated child rows. An option to include children in the POST/PUT response would reduce round-trips for the data-entry UI. Owner: Story 6.9 can decide.
6. **No per-child rate-limit or permission check** — child operations are authorized by the parent's `canCreate`/`canUpdate` permission. Owner: if cross-designer permission granularity is required.
7. **Child UPDATE with 0 rowsAffected (concurrent soft-delete between pre-flight and tx)** — child is `is_deleted=true` by the time `BuildUpdateQuery` runs; its `WHERE is_deleted=false` guard returns 0. Story 6.7 does not surface this as a distinct error. Owner: deferred, low-risk for v1.
8. **No test for `children: {}` empty object** — AC-2 backward-compatibility test only covers absent key; an explicit empty object is also valid. Owner: low-priority coverage gap.

---

### References

- [Source: `DynamicDataEndpoints.cs`] — `CreateRecordHandler` (lines ~347–473) for the template to extend; `UpdateRecordHandler` (lines ~481–650); `DeleteRecordHandler` / `RestoreRecordHandler` for NpgsqlTransaction + try/catch/finally pattern; `Problems` inner class (add `ChildNotFound` at end)
- [Source: `DynamicQueryBuilder.cs`] — `BuildInsertQuery` (exact template for `BuildChildInsertQuery`); `BuildUpdateQuery` (reused for child UPDATE); `BuildSoftDeleteByIdQuery` (reused for omitted children); `BuildSelectChildIdsByFkQuery` (pre-flight child-ID SELECT); `BuildGetByIdQuery` (previousValues SELECT for child UPDATE); `BuildFkColumnName` (FK column name for child INSERT)
- [Source: `SoftDeleteCascade.cs`] — `BuildSchemaGraphAsync` pattern for loading child schemas via EF; `ExecuteAsync` for within-transaction Dapper pattern
- [Source: `SoftDeleteIntegrationTests.cs`] — copy all helpers; copy `InitializeAsync`/`DisposeAsync`; copy `SoftDelete_CascadeToRepeaterChildren_DeletesChildRowsAtomically` for parent+child designer setup pattern
- [Source: `createRecordApi.ts`, `updateRecordApi.ts`] — existing patterns to extend
- [Architecture: Decision 1.6] — `commandTimeout: 5` on all Dapper; EF audit in separate tx after Dapper tx commits
- [Architecture: Decision 1.3] — `cascade_event_id = NULL` for individual (non-cascade) soft-deletes
- [Architecture: AR-46 Option C] — `DynamicRecordJsonConverter` applies to all `DynamicRecord` returns; no change needed
- [Architecture: 3.3 Decision] — `IDynamicPayloadValidator` Layer 2 validation; unknown keys silently dropped; `children` key dropped because it is not a user column
- [Architecture: FR-35] — nested Repeater writes in a single transaction; `RepeaterWriteCoordinator.cs` is the designated file per project structure

---

## Dev Agent Record

### Agent Model Used

Opus 4.7 (1M context) via `/bmad-dev-story` skill.

### Debug Log References

- Initial backend build: 0 warnings / 0 errors.
- Unit tests: `dotnet test --filter "FullyQualifiedName~DynamicQueryBuilderTests"` → 41/41 passing (2 new for `BuildChildInsertQuery`).
- Integration tests: `dotnet test --filter "FullyQualifiedName~RepeaterWriteIntegrationTests"` → 10/10 passing.
- Full backend suite: `dotnet test src/FormForge.Api.Tests/FormForge.Api.Tests.csproj` → 514/514 passing (was 502 prior to this story).
- Frontend type-check: `npx tsc --noEmit` exit 0.

### Completion Notes List

- **Task 1 — `BuildChildInsertQuery` added** to `DynamicQueryBuilder.cs` directly above `BuildFkColumnName`. Mirrors `BuildInsertQuery` exactly; the only differences are an extra FK column injected into the column list (positioned right after `cascade_event_id`) and a corresponding `@p_fk_parent_id` parameter in the VALUES list. `cascade_event_id = NULL` stays as a SQL literal (avoids Dapper Npgsql null-UUID edge case, same as the parent builder).
- **Task 2 — `RepeaterWriteCoordinator.cs` created.** Static class (no DI registration per Dev Notes §9). Four entry points: `ParseChildrenElement` (extract `children` from request body), `TryParseAndValidateChildren` (Layer 2 type-check + extract `id` before validator strips system columns), `InsertChildrenAsync` (POST path), `UpsertAndPruneChildrenAsync` (PUT path). Each returns `WriteAuditEntry` records so the handler can persist audit rows via EF after the Dapper transaction commits (Decision 1.6).
- **Task 3 — `CreateRecordHandler` extended.** Added child-extraction + schema-load + validation block before the DB section. Introduced shared private static helper `TryLoadChildSchemasAsync` (declared on the same class so it accesses the nested `Problems` class) to keep both handlers DRY. When `hasChildren` is true the parent INSERT + child INSERTs run inside a single `NpgsqlTransaction`. Audit rows for parent and every child are added to `db.MutationAuditLog` and persisted in one `SaveChangesAsync`.
- **Task 4 — `UpdateRecordHandler` extended.** Children extraction + child schemas + payload validation run before the main DB section. Pre-flight SELECT (Dev Notes §11) per child designer collects existing non-deleted child ids, also validates AC-12 (returns 404 `CHILD_NOT_FOUND` if a submitted child id is not among them). Main flow opens a single `NpgsqlTransaction` only when there are children; otherwise reuses the existing no-tx UPDATE path (AC-8 backward compatibility). Inside the tx, parent UPDATE runs first, then `UpsertAndPruneChildrenAsync` (INSERT new / UPDATE existing / SOFT-DELETE omitted). `previousValues` for the parent are captured pre-overlay; child UPDATE rows also capture their own `previous_values` from a per-child SELECT inside the coordinator. The transaction commits, then audit rows go in via EF.
- **Task 5 — Unit tests added.** Story spec drafted assertions that wouldn't compile against the actual SQL shape (`cascade_event_id" = NULL` appeared only in SET clauses, not INSERT VALUES). Re-wrote them to match: `BuildChildInsertQuery_IncludesFkColumn_WithCorrectParam` asserts `"cascade_event_id", "parent_parent_tbl_id"` in the column list and `false, NULL, @p_fk_parent_id` in the VALUES list; `BuildChildInsertQuery_EmptyPayload_OnlySystemAndFkColumns` asserts the VALUES list terminates at `@p_fk_parent_id)` and that `p_created_by` is null.
- **Task 6 — `RepeaterWriteIntegrationTests.cs` created.** 10 integration tests (not 8 as the spec estimated — added two extra: explicit AC-10 unknown-child-designer test and AC-12 unknown-child-id test, since they belong to this feature surface). All test setup helpers copied verbatim from `SoftDeleteIntegrationTests` (TRUNCATE list includes `mutation_audit_log`; dynamic-table DROP loop). Tests cover AC-1, AC-2, AC-3, AC-4+AC-7, AC-5, AC-6, AC-9, AC-10, AC-12, AC-13. The audit-log test (AC-13) verifies all four operations land in `mutation_audit_log` (parent UPDATE, child UPDATE, child SOFT_DELETE, child CREATE) with matching `actor_id` and non-empty `correlation_id`.
- **Task 7 — Frontend API clients extended.** Added `ChildRecordPayload` and `RecordPayloadWithChildren` interfaces in `createRecordApi.ts`; `updateRecordApi.ts` re-imports them. Hooks updated to use the new payload type so children-aware UI code gets compile-time validation. No new tanstack key shape — invalidation continues to broadcast under `['data', designerId]`.
- **AC coverage tally:** AC-1 (test 1), AC-2 (test 2), AC-3 (test 3), AC-4 (test 4), AC-5 (test 5), AC-6 (test 6), AC-7 (test 4 covers mixed update+atomic), AC-8 (existing CreateRecordIntegrationTests + UpdateRecordIntegrationTests still pass via the no-children path), AC-9 (test 8), AC-10 (test 9), AC-11 (covered by handler logic — `TryLoadChildSchemasAsync` returns `Problems.TableNotProvisioned()`; story spec did not request a dedicated test for this), AC-12 (test 10), AC-13 (test 7), AC-14 (existing AC parity verified by 514/514 prior tests still passing), AC-15 (every Dapper call uses `commandTimeout: 5`), AC-16 (test 1 verifies camelCase, snake-case absent).

### Deferred items (recorded for code review)

1. **Grandchild Repeater writes not supported** — `entry.ChildRepeaterDesignerIds` contains only direct children. If a child schema itself has Repeaters, those grandchildren are not handled in Story 6.7. Owner: future story.
2. **Pre-flight child-ID SELECT TOCTOU** — a concurrent SOFT-DELETE of a child between the pre-flight SELECT and in-transaction UPDATE could cause 0 rowsAffected on the child UPDATE. The handler does not detect this case (the UPDATE proceeds, 0-rowsAffected for a child is silently accepted). Owner: hardening pass.
3. **Audit INSERT not transactionally atomic with Dapper writes** — same deferred item as Stories 6.3–6.6. Owner: outbox pattern.
4. **Multiple child designers: separate pre-flight connections** — each child designer opens its own pre-flight connection (Dev Notes §11). For v1 (typically 1 Repeater, 1 designer) this is fine, but it could be batched into one multi-table query. Owner: optimization pass.
5. **Response includes parent record only** — clients must follow up with `GET /{id}?include=children` to see inserted/updated child rows. Returning children in POST/PUT responses would reduce round-trips for data-entry UIs. Owner: Story 6.9.
6. **No per-child rate-limit or permission check** — child operations are authorized by the parent's `canCreate` / `canUpdate` permission. Owner: cross-designer permission granularity (not v1).
7. **Child UPDATE with 0 rowsAffected silently accepted** — if a child becomes `is_deleted=true` between pre-flight and the transactional UPDATE, the `WHERE is_deleted=false` guard returns 0 and the loop continues. No distinct error is surfaced. Owner: deferred, low-risk for v1.
8. **No test for `children: {}` empty object** — AC-2 backward-compatibility test covers only the absent-key path. An explicit empty object also exercises the no-tx branch; coverage gap is low-priority because `ParseChildrenElement` short-circuits identically for both.
9. **No AC-11 integration test** — handler logic surfaces 404 `TABLE_NOT_PROVISIONED` when a referenced child designer has no Published version, but no dedicated integration test was added (the path is covered by the existing 6.2 / 6.3 unprovisioned-table tests using the same `Problems.TableNotProvisioned()`). Owner: low-priority coverage gap.
10. **Audit `correlation_id` is not asserted matching across rows in AC-13** — the test asserts each row has a non-empty `correlation_id`, but does not assert that all four rows share the *same* correlation id. Owner: tighten the assertion.
11. **AC-13 audit row ordering not tested** — rows are added in deterministic order in the handler (parent → INSERT children → UPDATE children → SOFT-DELETE children), but the integration test does not assert any ordering. Owner: low priority.
12. **No commandTimeout regression test for child paths** — AC-15 is verified by inspection (every `new CommandDefinition` includes `commandTimeout: 5`), not by an integration test forcing a slow query.

### File List

- **NEW** `src/FormForge.Api/Features/DynamicCrud/RepeaterWriteCoordinator.cs`
- **NEW** `src/FormForge.Api.Tests/Features/DynamicCrud/RepeaterWriteIntegrationTests.cs`
- **MODIFIED** `src/FormForge.Api/Features/DynamicCrud/DynamicQueryBuilder.cs` — added `BuildChildInsertQuery`.
- **MODIFIED** `src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs` — extended `CreateRecordHandler`, extended `UpdateRecordHandler`, added shared `TryLoadChildSchemasAsync` helper, added `Problems.ChildNotFound()`.
- **MODIFIED** `src/FormForge.Api.Tests/Features/DynamicCrud/DynamicQueryBuilderTests.cs` — 2 unit tests for `BuildChildInsertQuery`.
- **MODIFIED** `web/src/features/data-entry/createRecordApi.ts` — added `ChildRecordPayload`, `RecordPayloadWithChildren`, updated `createRecord` signature.
- **MODIFIED** `web/src/features/data-entry/updateRecordApi.ts` — re-uses `RecordPayloadWithChildren`.
- **MODIFIED** `web/src/features/data-entry/useCreateRecord.ts` — `mutationFn` typed with `RecordPayloadWithChildren`.
- **MODIFIED** `web/src/features/data-entry/useUpdateRecord.ts` — `mutationFn` typed with `RecordPayloadWithChildren`.

### Review Findings

- [x] [Review][Patch] AC-13 audit test: missing row-count assertion and SQL operator-precedence bug in `Put_WithChildren_AuditLog_AllOperationsRecorded` [`src/FormForge.Api.Tests/Features/DynamicCrud/RepeaterWriteIntegrationTests.cs`]
- [x] [Review][Patch] AC-8 backward-compat: no PUT-without-children test exercises the new `hasChildren=false` branch in the 6.7 `UpdateRecordHandler` extension [`src/FormForge.Api.Tests/Features/DynamicCrud/RepeaterWriteIntegrationTests.cs`]
- [x] [Review][Defer] Child UPDATE 0-rowsAffected silently swallowed in `UpsertAndPruneChildrenAsync` — pre-existing deferred item #7
- [x] [Review][Defer] Pre-flight child-ID SELECT TOCTOU (SELECT→UPDATE race in `UpdateRecordHandler`) — pre-existing deferred item #2
- [x] [Review][Defer] No integration test for AC-11 TABLE_NOT_PROVISIONED via child designer — pre-existing deferred item #9
- [x] [Review][Defer] Duplicate child IDs in same payload (one with `id`, one without) creates unexpected records — spec does not address
- [x] [Review][Defer] `TryParseAndValidateChildren` silently skips designer when schema key missing — fragile coupling, not reachable in current call flow
- [x] [Review][Defer] PUT pre-flight connection opens after type validation (letter of AC-9 "before any DB connection" violated for PUT path) — design decision
- [x] [Review][Defer] `CoercedValues` JSON serialisation inconsistency for non-primitive CLR types in audit `NewValuesJson` — pre-existing since Story 6.3
- [x] [Review][Defer] Soft-deleted child indistinguishable from non-existent child in AC-12 404 response — design choice, same as existing UPDATE/DELETE patterns

### Change Log

| Date | Change |
|---|---|
| 2026-05-26 | Story 6.7 implementation complete. 514/514 backend tests passing (added 12: 2 unit + 10 integration). Frontend `tsc --noEmit` clean. Status → review. |
| 2026-05-26 | Code review complete. 2 patches, 8 deferred, 11 dismissed. Status → in-progress. |
