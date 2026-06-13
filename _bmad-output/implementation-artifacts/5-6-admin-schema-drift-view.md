# Story 5.6: Admin Schema Drift View

Status: done

## Story

As a Platform Admin,
I want to view orphaned columns (in the DB but not in the current Designer schema) and selectively drop them,
so that I can make informed decisions about manual cleanup after schema evolution.

## Acceptance Criteria

**AC-1 — Drift View: Orphaned Column List**
**Given** I call `GET /api/admin/designers/{designerId}/drift`
**When** the provisioned table has columns not present in the latest Published Designer schema
**Then** the response lists each orphaned column with: `columnName`, `pgDataType`, `estimatedNonNullRowCount`
**And** system columns (`id`, `created_at`, `created_by`, `updated_at`, `updated_by`, `is_deleted`, `cascade_event_id`) are **never** included
**And** FK columns matching `parent_*_id` are **never** included
**And** if no Published version exists, all user columns are treated as orphaned
**And** if the table does not exist, the response is `{ orphanedColumns: [] }` (not a 404)

**AC-2 — Confirmation Before Drop**
**Given** the drift view is open in the browser
**When** I click "Drop Column" on an orphaned column
**Then** a confirmation dialog opens with text "This will permanently delete all data in this column"
**And** I must explicitly confirm before the `DELETE` request fires
**And** I can cancel without side-effect

**AC-3 — Drop Column: DDL, Audit, and Rollback**
**Given** I confirm a drop on orphaned column `{col}` for designer `{designerId}`
**When** `DELETE /api/admin/designers/{designerId}/columns/{columnName}` executes
**Then** `ALTER TABLE {designerId} DROP COLUMN {col}` runs inside an explicit Dapper transaction
**And** on success, `schema_audit_log` gains a row: `ddl_operation = "DROP"`, `columns_dropped = ["{col}"]`, fresh ULID `correlation_id`, `actor_id` from the JWT, `to_version = 0` (sentinel — DROP is not version-bound), `from_version = NULL`
**And** on DDL failure, the transaction rolls back, the column is retained, and the server returns HTTP 500

**AC-4 — Schema Registry Invalidation**
**Given** a DROP succeeds
**Then** all cached `SchemaRegistryEntry` entries for `{designerId}` (across all versions) are removed so the next Epic 6 CRUD request re-fetches the live schema

**AC-5 — Refresh on Demand**
**Given** the drift view is open
**When** I click "Refresh"
**Then** the orphaned column list re-queries the server (TanStack Query `refetch()`)

**AC-6 — Guard: Drop Rejected for Non-Orphaned Columns**
**Given** a caller attempts to drop a column that is:
- a system column (`id`, `created_at`, etc.)
- a FK column (`parent_*_id`)
- still present in the current Published schema (not actually orphaned)
**Then** the server returns HTTP 422 with `code: "COLUMN_NOT_ORPHANED"` and no DDL is executed

**AC-7 — Deferred 5.2 Fix: `GetBindingDiffHandler` Guard**
**Given** `GET /api/admin/menus/{id}/binding-diff?targetVersion=0` (or any `<= 0`)
**When** the handler executes
**Then** it returns HTTP 422 with `code: "INVALID_TARGET_VERSION"` before calling the diff service

## Tasks / Subtasks

- [x] **Task 1 — EF Entity + Migration: add `columns_dropped` to `schema_audit_log`** (AC: 3)
  - [x] In `src/FormForge.Api/Domain/Entities/SchemaAuditLogEntry.cs`, add:
    ```csharp
    public string[]? ColumnsDropped { get; set; }  // column names removed by a DROP op
    ```
  - [x] In `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs`, in the `SchemaAuditLogEntry` config block, add:
    ```csharp
    e.Property(a => a.ColumnsDropped).HasColumnName("columns_dropped").HasColumnType("text[]");
    ```
    Place it immediately after the `ColumnsAdded` property mapping (line ~244).
  - [x] Run `dotnet ef migrations add AddColumnsDroppedToSchemaAuditLog --project src/FormForge.Api --startup-project src/FormForge.Api --no-build`
  - [x] Manually add `ArgumentNullException.ThrowIfNull(migrationBuilder)` to both `Up()` and `Down()` per the CA1062 pattern in all prior migrations
  - [x] Verify the snapshot regenerated correctly; rebuild clean

- [x] **Task 2 — Extract `SystemColumnNames` to `internal static`** (AC: 1, 6)
  - [x] In `src/FormForge.Api/Features/Provisioning/DdlEmitter.cs`, change the `SystemColumnNames` modifier from `private` to `internal`:
    ```csharp
    internal static readonly HashSet<string> SystemColumnNames = new(StringComparer.Ordinal)
    {
        "id", "created_at", "created_by", "updated_at", "updated_by", "is_deleted", "cascade_event_id",
    };
    ```
  - [x] This resolves the deferred item from Story 5.4 code review: "SystemColumnNames has no compile-time link to CreateTableAsync SQL". `SchemaDriftService` will reference `DdlEmitter.SystemColumnNames` directly so the two never diverge.

- [x] **Task 3 — `ISchemaRegistry.InvalidateDesigner` method** (AC: 4)
  - [x] In `src/FormForge.Api/Features/SchemaRegistry/ISchemaRegistry.cs`, add:
    ```csharp
    void InvalidateDesigner(string designerId);
    ```
  - [x] In `src/FormForge.Api/Features/SchemaRegistry/SchemaRegistry.cs`:
    - Add a `ConcurrentDictionary<string, ConcurrentBag<int>> _populatedVersions` field to track which versions have been cached per designer
    - In `Populate()`, after `cache.Set(...)`, add:
      ```csharp
      _populatedVersions.GetOrAdd(entry.DesignerId, _ => new ConcurrentBag<int>()).Add(entry.Version);
      ```
    - Add the new method:
      ```csharp
      public void InvalidateDesigner(string designerId)
      {
          if (_populatedVersions.TryRemove(designerId, out var versions))
          {
              foreach (var v in versions)
                  cache.Remove(CacheKey(designerId, v));
          }
      }
      ```
  - **Note**: `ConcurrentBag<int>` permits duplicate version entries (a version re-provisioned twice adds its number twice), but `cache.Remove` on a non-existent key is a no-op, so correctness is unaffected. Documented as an acceptable v1 characteristic.

- [x] **Task 4 — `DdlEmitter.DropColumnAsync`** (AC: 3, 4)
  - [x] In `src/FormForge.Api/Features/Provisioning/DdlEmitter.cs`, add a new public method:
    ```csharp
    public async Task DropColumnAsync(
        string designerId,
        string columnName,
        Guid? actorId,
        CancellationToken ct)
    ```
  - Implementation steps:
    1. `ArgumentNullException.ThrowIfNull(designerId)` and `ArgumentNullException.ThrowIfNull(columnName)`
    2. Validate designerId → `SafeIdentifier.TryCreate(designerId, out var tableName, out _)` — throw `InvalidOperationException` if invalid (caller pre-validates; this is defence-in-depth)
    3. Validate columnName → `SafeIdentifier.TryCreate(columnName, out var colName, out _)` — throw `InvalidOperationException` if invalid
    4. Open connection via `connectionFactory.CreateOpenConnectionAsync(ct)`
    5. Begin transaction: `await connection.BeginTransactionAsync(ct)`
    6. Execute `ALTER TABLE {tableName.Value} DROP COLUMN {colName.Value}` via Dapper with `commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds`
    7. `await tx.CommitAsync(CancellationToken.None)` (CancellationToken.None on commit — same rationale as EmitAsync)
    8. On exception: `await tx.RollbackAsync(CancellationToken.None)` then rethrow
    9. In finally: dispose tx, then dispose connection (explicit `await DisposeAsync().ConfigureAwait(false)` pattern — same as EmitAsync)
    10. After the connection is closed, append audit row to DbContext (DO NOT call SaveChangesAsync — the caller does):
        ```csharp
        db.SchemaAuditLog.Add(new SchemaAuditLogEntry
        {
            DesignerId = designerId,
            FromVersion = null,
            ToVersion = 0,        // sentinel: DROP is not version-bound
            DdlOperation = "DROP",
            ColumnsDropped = [colName.Value],
            ColumnsAdded = null,
            CorrelationId = Ulid.NewUlid().ToString(),
            ActorId = actorId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        ```
    11. Add `[LoggerMessage]` partial `LogDroppedColumn` at `Information` level with `TableName` and `ColumnName` structured fields
  - The caller (`SchemaDriftService.DropColumnAsync`) calls `db.SaveChangesAsync()` after `DropColumnAsync` returns.

- [x] **Task 5 — `SchemaDriftService.cs`** (AC: 1, 3, 4, 6)
  - [x] Create `src/FormForge.Api/Features/Designer/SchemaDriftService.cs`
  - Constructor injection: `FormForgeDbContext db`, `DbConnectionFactory connectionFactory`, `DdlEmitter ddlEmitter`, `ISchemaRegistry schemaRegistry`
  - Add `[SuppressMessage("Performance", "CA1812", Justification = "Registered via DI.")]`
  - **`GetDriftAsync(string designerId, CancellationToken ct) → Task<SchemaDriftResponse?>`**:
    1. `SafeIdentifier.TryCreate(designerId, ...)` — return `null` (→ 404) if invalid
    2. Open a Dapper connection (read-only; no transaction needed)
    3. If table does not exist (`TableExistsAsync`-style query): return `new SchemaDriftResponse([])` — empty list, not 404 (the designer may not have been provisioned yet)
    4. Query `information_schema.columns` for all columns in `tableName.Value` — same Dapper query as `AddMissingColumnsCoreAsync` but also retrieve `data_type`
    5. Filter out `DdlEmitter.SystemColumnNames` and FK columns (`columnName.StartsWith("parent_") && columnName.EndsWith("_id")`)
    6. Load latest Published version from `db.ComponentSchemaVersions` (AsNoTracking, `Status == "Published"`, `OrderByDescending(v => v.Version).FirstOrDefault`)
    7. Compute target schema column names via `RootElementParser.ParseFull(version.RootElement).Columns.Select(c => c.ColumnName).ToHashSet(StringComparer.Ordinal)` — if no Published version, target set is empty (all user columns are orphaned)
    8. For each orphaned column (present in DB, absent from target, not system, not FK): query non-null row count:
       ```sql
       SELECT COUNT(*) FROM {tableName.Value} WHERE {colName.Value} IS NOT NULL
       ```
       Use `DdlCommandTimeoutSeconds` — these are admin queries on potentially large tables.
    9. Return `new SchemaDriftResponse(orphanedColumns.Select(...).ToList())`
  - **`DropColumnAsync(string designerId, string columnName, Guid? actorId, CancellationToken ct) → Task<DropColumnOutcome>`**:
    1. `SafeIdentifier.TryCreate(designerId, ...)` — return `DropColumnOutcome.DesignerNotFound` if invalid
    2. `SafeIdentifier.TryCreate(columnName, ...)` — return `DropColumnOutcome.ColumnNotFound` if invalid
    3. Guard against system column: `if (DdlEmitter.SystemColumnNames.Contains(columnName)) return DropColumnOutcome.ColumnProtected`
    4. Guard against FK column: `if (columnName.StartsWith("parent_", StringComparison.Ordinal) && columnName.EndsWith("_id", StringComparison.Ordinal)) return DropColumnOutcome.ColumnProtected`
    5. Open Dapper connection; verify column exists in `information_schema.columns` for this table — if not: return `DropColumnOutcome.ColumnNotFound`
    6. Verify column is orphaned: load latest Published version schema via `RootElementParser.ParseFull`; if the column IS in the current schema, return `DropColumnOutcome.ColumnNotOrphaned`
    7. `await ddlEmitter.DropColumnAsync(designerId, columnName, actorId, ct)`
    8. `await db.SaveChangesAsync(CancellationToken.None)` — commits the audit row
    9. `schemaRegistry.InvalidateDesigner(designerId)` — clear all cached versions
    10. Return `DropColumnOutcome.Success`
  - **`DropColumnOutcome` enum** (in same file or a sibling):
    ```csharp
    internal enum DropColumnOutcome { Success, DesignerNotFound, ColumnNotFound, ColumnProtected, ColumnNotOrphaned }
    ```

- [x] **Task 6 — DTOs** (AC: 1)
  - [x] Create `src/FormForge.Api/Features/Designer/Dtos/SchemaDriftResponse.cs`:
    ```csharp
    internal sealed record OrphanedColumnInfo(
        string ColumnName,
        string PgDataType,
        long EstimatedNonNullRowCount);

    internal sealed record SchemaDriftResponse(IReadOnlyList<OrphanedColumnInfo> OrphanedColumns);
    ```
  - These are returned as JSON responses; serialized by `System.Text.Json` with default camelCase (same pipeline as all other endpoints).

- [x] **Task 7 — `DesignerAdminEndpoints.cs`** (AC: 1, 3, 6, 7)
  - [x] Create `src/FormForge.Api/Features/Designer/DesignerAdminEndpoints.cs`:
    ```csharp
    internal static class DesignerAdminEndpoints
    {
        internal static RouteGroupBuilder MapDesignerAdminEndpoints(this RouteGroupBuilder group)
        {
            ArgumentNullException.ThrowIfNull(group);

            group.MapGet("/{designerId}/drift", GetDriftHandler)
                 .WithSummary("List orphaned columns for a provisioned designer table")
                 .Produces<SchemaDriftResponse>(StatusCodes.Status200OK)
                 .Produces(StatusCodes.Status404NotFound);

            group.MapDelete("/{designerId}/columns/{columnName}", DropColumnHandler)
                 .WithSummary("Drop an orphaned column from a provisioned designer table")
                 .Produces(StatusCodes.Status204NoContent)
                 .Produces(StatusCodes.Status404NotFound)
                 .Produces(StatusCodes.Status422UnprocessableEntity);

            return group;
        }
    }
    ```
  - `GetDriftHandler`: resolve `SchemaDriftService`, call `GetDriftAsync(designerId, ct)` — if null, return 404; else return `Results.Ok(drift)`
  - `DropColumnHandler`: extract actorId from `httpContext.User.FindFirst("userId")` (same pattern as `BindDesignerHandler`), call `SchemaDriftService.DropColumnAsync`, map outcome to HTTP results:
    - `Success` → `Results.NoContent()` (204)
    - `DesignerNotFound` → 404
    - `ColumnNotFound` → 404
    - `ColumnProtected` → 422 with `code: "COLUMN_NOT_ORPHANED"`, `messageKey: "admin.designers.drift.columnProtected"`
    - `ColumnNotOrphaned` → 422 with `code: "COLUMN_NOT_ORPHANED"`, `messageKey: "admin.designers.drift.columnNotOrphaned"`
    - default → 500

- [x] **Task 8 — Wire into `AdminEndpoints.cs` and DI in `Program.cs`** (AC: 1, 3)
  - [x] In `src/FormForge.Api/Features/Roles/AdminEndpoints.cs`, add to `MapAdminEndpoints`:
    ```csharp
    group.MapGroup("/designers").WithTags("Admin — Designers").MapDesignerAdminEndpoints();
    ```
    Add this BEFORE or AFTER the `/menus` line — any order is fine (DI resolves lazily).
  - [x] Add `using FormForge.Api.Features.Designer;` to `AdminEndpoints.cs`
  - [x] In `Program.cs`, register the scoped service immediately after the other designer services:
    ```csharp
    // Story 5.6 — admin drift view: inspect + drop orphaned columns on provisioned tables
    builder.Services.AddScoped<SchemaDriftService>();
    ```
    Place after the existing `IDesignerService` line (line ~174).

- [x] **Task 9 — Fix deferred 5.2 item: `GetBindingDiffHandler` guard** (AC: 7)
  - [x] In `src/FormForge.Api/Features/Menus/MenuAdminEndpoints.cs`, in `GetBindingDiffHandler`:
    ```csharp
    private static async Task<IResult> GetBindingDiffHandler(
        Guid id,
        int targetVersion,
        IMenuService menuService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(menuService);
        if (targetVersion <= 0)
            return Results.Problem(
                title: "targetVersion must be a positive integer",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "INVALID_TARGET_VERSION",
                    ["messageKey"] = "admin.menus.invalidTargetVersion",
                });
        var diff = await menuService.GetBindingDiffAsync(id, targetVersion, ct).ConfigureAwait(false);
        return diff is null ? MenuNotFoundProblem() : Results.Ok(diff);
    }
    ```
  - [x] Add i18n key: in `web/src/lib/i18n/locales/en.json`, inside `admin.menus`, add:
    ```json
    "invalidTargetVersion": "Target version must be a positive number."
    ```

- [x] **Task 10 — Frontend: API client** (AC: 1, 2, 3, 5)
  - [x] Create `web/src/features/admin/designers/designerAdminApi.ts`:
    ```ts
    import { httpClient } from '../../auth/httpClient'

    export interface OrphanedColumnInfo {
      columnName: string
      pgDataType: string
      estimatedNonNullRowCount: number
    }
    export interface SchemaDriftResponse {
      orphanedColumns: OrphanedColumnInfo[]
    }

    export function getSchemaDrift(designerId: string): Promise<SchemaDriftResponse> {
      return httpClient.get<SchemaDriftResponse>(`/api/admin/designers/${designerId}/drift`)
    }

    export function dropColumn(designerId: string, columnName: string): Promise<void> {
      return httpClient.delete<void>(`/api/admin/designers/${designerId}/columns/${columnName}`)
    }
    ```
  - **Note**: verify that `httpClient` (at `web/src/features/auth/httpClient.ts`) exposes a `.delete<T>()` method; if only `.get/.post/.put` exist, add `.delete` following the same pattern. If using raw `fetch`, ensure the method is `DELETE`.

- [x] **Task 11 — Frontend: TanStack Query hook and mutation** (AC: 1, 3, 5)
  - [x] Create `web/src/features/admin/designers/useSchemaDriftQuery.ts`:
    ```ts
    import { useQuery } from '@tanstack/react-query'
    import { getSchemaDrift } from './designerAdminApi'

    export function useSchemaDriftQuery(designerId: string) {
      return useQuery({
        queryKey: ['admin', 'designers', designerId, 'drift'],
        queryFn: () => getSchemaDrift(designerId),
        staleTime: 0,   // always re-fetch on focus — drift data should be fresh
      })
    }
    ```
  - [x] Create `web/src/features/admin/designers/schemaDriftMutations.ts`:
    ```ts
    import { useMutation, useQueryClient } from '@tanstack/react-query'
    import { dropColumn } from './designerAdminApi'

    export function useDropColumnMutation(designerId: string) {
      const queryClient = useQueryClient()
      return useMutation({
        mutationFn: (columnName: string) => dropColumn(designerId, columnName),
        onSuccess: () => {
          queryClient.invalidateQueries({
            queryKey: ['admin', 'designers', designerId, 'drift'],
          })
        },
      })
    }
    ```

- [x] **Task 12 — Frontend: `SchemaDriftView.tsx` component** (AC: 1, 2, 3, 5)
  - [x] Create `web/src/features/admin/designers/SchemaDriftView.tsx`
  - Structure:
    - Uses `useSchemaDriftQuery(designerId)` and `useDropColumnMutation(designerId)`
    - Shows loading/error states (same pattern as other admin components)
    - Renders a table/list of orphaned columns: name, PG type, estimated non-null count
    - Each row has a "Drop Column" button
    - On click: sets `confirmingColumn` state; shows a confirmation `<dialog>` or inline panel:
      - Dialog text: _"This will permanently delete all data in this column"_
      - Two buttons: "Confirm Drop" and "Cancel"
    - On confirm: calls `dropColumnMutation.mutate(columnName)`; on success the query re-fetches automatically via `onSuccess` invalidation
    - A "Refresh" button calls `query.refetch()` (AC-5)
  - **Styling**: inline CSS / flexbox matching existing admin components; no new CSS dependencies
  - **i18n**: all user-visible text via `t('admin.designers.drift.*')` keys (see Task 13)

- [x] **Task 13 — Frontend: i18n keys** (AC: 1, 2, 3, 5)
  - [x] In `web/src/lib/i18n/locales/en.json`, add inside the `"admin"` block (after the `"menus"` block):
    ```json
    "designers": {
      "drift": {
        "title": "Schema Drift",
        "subtitle": "Orphaned columns exist in the database but are not in the current Designer schema.",
        "noOrphans": "No orphaned columns. Schema is clean.",
        "loading": "Loading drift data…",
        "loadError": "Could not load drift data.",
        "refreshButton": "Refresh",
        "columnNameHeader": "Column",
        "pgTypeHeader": "PG Type",
        "nonNullCountHeader": "Non-null Rows",
        "dropButton": "Drop Column",
        "droppingButton": "Dropping…",
        "confirmTitle": "Drop column?",
        "confirmBody": "This will permanently delete all data in this column",
        "confirmButton": "Confirm Drop",
        "cancelButton": "Cancel",
        "dropSuccess": "Column dropped.",
        "dropError": "Failed to drop column.",
        "columnProtected": "This column cannot be dropped.",
        "columnNotOrphaned": "This column is part of the current Designer schema and cannot be dropped."
      }
    }
    ```
  - **Note**: the existing `"designers"` block at the top level is under `web/src/lib/i18n/locales/en.json` (lines ~249+). The `admin.designers.drift` path is NESTED under `"admin"` — a separate subtree from the top-level `"designers"` block used by the designer canvas feature.

- [x] **Task 14 — Frontend: route file** (AC: 1, 2, 3, 5)
  - [x] Create `web/src/routes/_app/admin/designers.$designerId.drift.tsx`:
    ```tsx
    import { createFileRoute } from '@tanstack/react-router'
    import { SchemaDriftView } from '../../../features/admin/designers/SchemaDriftView'

    export const Route = createFileRoute('/_app/admin/designers/$designerId/drift')({
      component: SchemaDriftPage,
    })

    function SchemaDriftPage() {
      const { designerId } = Route.useParams()
      return <SchemaDriftView designerId={designerId} />
    }
    ```
  - The route is navigable at `/admin/designers/{designerId}/drift`. No parent `designers.tsx` layout route is needed for Story 5.6 — navigating to this URL directly is sufficient for the admin workflow.

- [x] **Task 15 — Tests** (AC: 1–7)
  - [x] **Backend integration tests** — add to `src/FormForge.Api.Tests/Features/Provisioning/ProvisioningIntegrationTests.cs` (or a new `SchemaDriftIntegrationTests.cs` in the same folder for clarity):

    | Test | Coverage |
    |---|---|
    | `GetDrift_NoOrphanedColumns_ReturnsEmpty` | Bind + provision v1; call drift; assert `orphanedColumns = []` |
    | `GetDrift_OrphanedColumns_ReturnsListWithCounts` | Bind v1 (field1+field2), bind v2 (field1 only) → field2 orphaned; insert 2 rows with non-null field2; call drift; assert field2 in list with `estimatedNonNullRowCount = 2` |
    | `GetDrift_TableNotProvisioned_ReturnsEmpty` | Call drift for a designerId that has no provisioned table; assert 200 + empty list |
    | `GetDrift_SystemColumnsExcluded_NeverInList` | Bind + provision; call drift; assert `id`, `created_at`, etc. not in orphaned list even if drift computes them |
    | `DropColumn_OrphanedColumn_Returns204AndAuditsAndInvalidatesRegistry` | Full flow: bind v1 (field1+field2), bind v2 (field1), call `DELETE …/columns/field2`; assert 204; verify column gone from `information_schema.columns`; verify audit row `ddl_operation = "DROP"`, `columns_dropped @> ARRAY['field2']`; verify `schemaRegistry.TryGet` for v1 and v2 both null |
    | `DropColumn_SystemColumn_Returns422` | Attempt `DELETE …/columns/id`; assert 422 + `code = "COLUMN_NOT_ORPHANED"` |
    | `DropColumn_ActiveColumn_Returns422` | Attempt `DELETE …/columns/field1` while field1 IS in current Published schema; assert 422 |
    | `DropColumn_UnknownColumn_Returns404` | Attempt `DELETE …/columns/nonexistent_col`; assert 404 |
    | `GetBindingDiff_TargetVersionZero_Returns422` | `GET /api/admin/menus/{id}/binding-diff?targetVersion=0`; assert 422 + `code = "INVALID_TARGET_VERSION"` |
    | `GetBindingDiff_TargetVersionNegative_Returns422` | Same but `targetVersion=-1` |

  - [x] **Backend unit test** — add to existing or new `SchemaRegistryTests.cs`:
    - `InvalidateDesigner_RemovesAllVersions_TryGetReturnsNull` — Populate v1 and v2 for designer "d1", Populate v1 for "d2", call InvalidateDesigner("d1"), assert TryGet("d1", 1) = null, TryGet("d1", 2) = null, TryGet("d2", 1) still returns entry

  - **Estimated test count**: 10 integration + 1 unit = 11 new tests. Total project count goes from 374 → ~385.

## Dev Notes

### Architecture compliance — critical constraints

- **All DDL identifiers MUST go through `SafeIdentifier.Value`** (NFR-6). Both `designerId` (table name) and `columnName` are validated via `SafeIdentifier.TryCreate` before interpolation. Column names retrieved from `information_schema.columns` are safe by construction (PG enforces identifier rules), but the route parameter (`columnName` from the URL path) could be tampered — always re-validate.
- **DDL via Dapper, audit rows via EF** (Decision 1.6). `DdlEmitter.DropColumnAsync` does the DDL + appends to `db.SchemaAuditLog`; the caller (`SchemaDriftService.DropColumnAsync`) calls `db.SaveChangesAsync(CancellationToken.None)` after. Same dual-write hazard pattern as `EmitAsync`/`ProcessJobAsync`. If `SaveChangesAsync` fails after the DROP succeeds, the column is gone but the audit row is missing — acceptable for v1 (audit gap is better than leaving the column).
- **DDL command timeout**: Always pass `commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds` (60 s) to Dapper. For the non-null row count `COUNT(*)` queries in `GetDriftAsync`, also use 60 s — these are admin-only reads on potentially large tables.
- **`CancellationToken.None` on DDL commit/rollback** (same rationale as `EmitAsync`): once the transaction is open, cancellation must not leave a half-committed DDL state.

### File locations — new files

| New file | Path |
|---|---|
| `SchemaAuditLogEntry.cs` update | `src/FormForge.Api/Domain/Entities/SchemaAuditLogEntry.cs` |
| EF migration | `src/FormForge.Api/Infrastructure/Persistence/Migrations/AddColumnsDroppedToSchemaAuditLog.cs` |
| `SchemaDriftService.cs` | `src/FormForge.Api/Features/Designer/SchemaDriftService.cs` |
| `SchemaDriftResponse.cs` (DTOs) | `src/FormForge.Api/Features/Designer/Dtos/SchemaDriftResponse.cs` |
| `DesignerAdminEndpoints.cs` | `src/FormForge.Api/Features/Designer/DesignerAdminEndpoints.cs` |
| `designerAdminApi.ts` | `web/src/features/admin/designers/designerAdminApi.ts` |
| `useSchemaDriftQuery.ts` | `web/src/features/admin/designers/useSchemaDriftQuery.ts` |
| `schemaDriftMutations.ts` | `web/src/features/admin/designers/schemaDriftMutations.ts` |
| `SchemaDriftView.tsx` | `web/src/features/admin/designers/SchemaDriftView.tsx` |
| `designers.$designerId.drift.tsx` | `web/src/routes/_app/admin/designers.$designerId.drift.tsx` |

### File locations — modified files

| Modified file | Change |
|---|---|
| `DdlEmitter.cs` | `SystemColumnNames` → `internal static`; add `DropColumnAsync` method + `LogDroppedColumn` partial |
| `ISchemaRegistry.cs` | Add `InvalidateDesigner(string designerId)` |
| `SchemaRegistry.cs` | Add version tracking dict + `InvalidateDesigner` implementation |
| `AdminEndpoints.cs` | Add `/designers` sub-group line + using |
| `Program.cs` | Register `SchemaDriftService` |
| `MenuAdminEndpoints.cs` | Add `targetVersion <= 0` guard in `GetBindingDiffHandler` |
| `FormForgeDbContext.cs` | Add `ColumnsDropped` EF config line |
| `en.json` | Add `admin.designers.drift.*` keys + `admin.menus.invalidTargetVersion` |

### `httpClient.delete<T>()` — check before Task 10

Before implementing `designerAdminApi.ts`, grep the codebase for `httpClient` to verify whether a `.delete` method exists:
```
Grep: "delete" in web/src/features/auth/httpClient.ts
```
If only `.get/.post/.put` are defined, add `.delete` following the same pattern. DELETE requests have no body; the method should accept a URL and return `Promise<T>`.

### Orphaned column detection logic — precise definition

An orphaned column is one that:
1. Exists in `information_schema.columns` for the designer's provisioned table (`table_schema = 'public' AND table_name = @tableName`)
2. Is NOT in `DdlEmitter.SystemColumnNames` 
3. Does NOT match the FK column pattern (`columnName.StartsWith("parent_", Ordinal) && columnName.EndsWith("_id", Ordinal)`)
4. Is NOT in the current Published schema's column set (from `RootElementParser.ParseFull(latestPublished.RootElement).Columns`)

If there is no Published version (all versions Archived or only Draft), condition 4's target set is empty — all user columns become orphaned. This is intentional: if an admin archived all versions, the data table is orphaned entirely.

### `ToVersion = 0` sentinel for DROP audit rows

`schema_audit_log.to_version` is `INT NOT NULL` in the database. DROP operations are not version-bound. `0` is used as a sentinel value. The Story 5.7 audit-log view must handle `to_version = 0` as a special case meaning "column drop" (not a schema version). This is an explicit known design decision; do not make `to_version` nullable in this story (that requires an additional migration that's out of scope — record the decision in completion notes for Story 5.7 to handle).

### Schema registry invalidation — race conditions

`InvalidateDesigner` removes all cached versions for the designer. If a concurrent Epic 6 CRUD request is mid-flight at the moment of invalidation, it will use a stale entry from its already-resolved `TryGet` result — this is safe because the entry was valid when it was fetched (the column hadn't been dropped yet). Any subsequent request after invalidation will miss the cache and re-fetch from the live DB. Acceptable for v1.

### FK column detection in drift view

FK columns added by Story 5.5 follow the pattern `parent_{parentTableName}_id` (with `parentTableName` truncated to ≤53 chars). The exact regex from `DdlEmitter.BuildFkColumnName` is `parent_{p}_id`. Detection via `StartsWith("parent_") && EndsWith("_id")` is sufficient — no legitimate user field key matches this pattern because `SafeIdentifier` allows `[a-z_][a-z0-9_]{0,62}` and `parent_…_id` is not a reserved shape for user-defined fields.

### Non-null row count — performance note

`SELECT COUNT(*) FROM {table} WHERE {col} IS NOT NULL` performs a full table scan. For tables with millions of rows, this is slow. This is acceptable for an admin-only endpoint where the user explicitly triggers a drift analysis. The 60 s command timeout provides a reasonable upper bound. A future story can replace with `pg_stats.null_frac` × `pg_class.reltuples` for an instant estimate.

### `TestForge` — test database isolation

Integration tests use `InitializeAsync` which truncates the static EF-managed tables. The new `schema_audit_log` table is already in the truncation list (added in Story 5.3). Dynamically provisioned tables are dropped by the DO block that removes all public tables not on the allow-list. No changes to test infrastructure needed.

### `DesignerAdminEndpoints.cs` — no validators needed

The `designerId` and `columnName` route parameters are strings validated inline via `SafeIdentifier.TryCreate`. No FluentValidation pipeline is needed. The 422 for invalid identifiers returns from the service via the outcome enum (same approach as `BindMenuOutcome.DesignerNotFound`).

### Deferred 5.2 fix — `GetBindingDiffHandler` guard scope

The Task 9 fix is minimal: just the guard + i18n key. Do NOT refactor `BindingDiffService` or add any other changes to the diff flow in this story — that's out of scope.

### Project Structure Notes

- New `web/src/features/admin/designers/` directory follows the existing `admin/menus/`, `admin/roles/`, `admin/users/` pattern
- TanStack Router auto-discovers `routes/_app/admin/designers.$designerId.drift.tsx` — no manual route registration needed
- The `/api/admin/designers/{designerId}/drift` route is secured by the `/api/admin` group's `.RequirePlatformAdmin()` inherited from `Program.cs` line ~454-458

### References

- [Source: `src/FormForge.Api/Features/Provisioning/DdlEmitter.cs`] — `SystemColumnNames`, `TableExistsAsync`, `AddMissingColumnsCoreAsync` patterns for information_schema queries
- [Source: `src/FormForge.Api/Features/SchemaRegistry/SchemaRegistry.cs`] — `CacheKey`, `EntryOptions`, `Populate`/`TryGet` pattern to follow for `InvalidateDesigner`
- [Source: `src/FormForge.Api/Domain/Entities/SchemaAuditLogEntry.cs`] — entity shape; `ColumnsAdded HasColumnType("text[]")` pattern to replicate for `ColumnsDropped`
- [Source: `src/FormForge.Api/Features/Roles/AdminEndpoints.cs`] — `MapAdminEndpoints` registration pattern
- [Source: `src/FormForge.Api/Features/Menus/MenuAdminEndpoints.cs`] — `BindDesignerHandler` actorId extraction, `Results.Problem` extensions dictionary pattern, `GetBindingDiffHandler` to modify for Task 9
- [Source: `web/src/routes/_app/admin/menus.$menuId.tsx`] — `createFileRoute`, `Route.useParams()`, error state pattern
- [Source: `web/src/features/admin/menus/DesignerBindingSection.tsx`] — component pattern: inline CSS, i18n, mutation hooks, confirmation flow
- [Source: `web/src/features/admin/menus/menuAdminMutations.ts`] — `useMutation` + `queryClient.invalidateQueries` pattern
- [Source: `_bmad-output/implementation-artifacts/deferred-work.md`] — Story 5.2 deferred item (GetBindingDiffHandler targetVersion guard), Story 5.4 deferred item (SystemColumnNames shared constant)
- [Architecture Decision 1.6] — EF owns static schema, Dapper owns dynamic-schema DDL; dual-write hazard accepted for v1

## Dev Agent Record

### Agent Model Used

claude-opus-4-7

### Debug Log References

- 2026-05-25 — initial `dotnet ef migrations add` against the stale build (the spec's `--no-build` flag) produced an empty `Up()`/`Down()` migration because the entity changes weren't compiled into the assembly the EF tooling reflected over. Removed the empty migration via `dotnet ef migrations remove --force` (snapshot was reverted automatically) and regenerated without `--no-build`; the second pass produced the correct `AddColumn<string[]>` body. Lesson: when the entity was just edited, the spec's `--no-build` shortcut is wrong — re-build first.
- 2026-05-25 — first build flagged two CA2007 errors on the `await using var connection = …` form in `SchemaDriftService`. The implicit `DisposeAsync` at scope exit isn't `ConfigureAwait(false)`-compatible, which the project's analyzer set requires. Replaced both `await using var` blocks with explicit `try { … } finally { await connection.DisposeAsync().ConfigureAwait(false); }`, matching the established `DdlEmitter` pattern. Build clean after the fix.
- 2026-05-25 — `dotnet test` run #1 had 394/395 passing; the lone failure was `DropColumn_SystemColumn_Returns422` expecting 422 but receiving 404. Root cause: `PgReservedKeywords.IsReserved` includes the system-column names ("id", "created_at", …), so `SafeIdentifier.TryCreate("id", …)` returns `false`, and my service's column-name `SafeIdentifier` check fired BEFORE the `DdlEmitter.SystemColumnNames.Contains(columnName)` guard — surfacing `ColumnNotFound`/404 instead of `ColumnProtected`/422. Reordered `SchemaDriftService.DropColumnAsync` so the hardcoded system-column and FK-shape guards run BEFORE the `SafeIdentifier` validation on `columnName`. The semantics are correct either way (both reject the column) but the outcome enum drives the HTTP code, and the test contract specifies 422. Re-run: 395/395 pass.
- 2026-05-25 — `npm run build` produced a clean Vite production bundle (including a 4.29 kB `designers._designerId.drift-…js` chunk for the new route) but the post-build `tsc -b --noEmit` typecheck reported TS2339/TS2322 errors in two pre-existing test files (`Navbar.test.tsx`, `usePollProvisioning.test.tsx`) that Story 5.6 did not touch. Verified the errors pre-existed by `git stash`ing my web changes and re-running `tsc -b --noEmit` against the baseline — same errors. Not a Story 5.6 regression. Restored via `git stash pop`.

### Completion Notes List

- **Architecture compliance**: All DDL goes through `SafeIdentifier.Value` (NFR-6). `DdlEmitter.DropColumnAsync` re-validates both `designerId` and `columnName` even though the service layer pre-validates — defence-in-depth at the SQL boundary. The Dapper transaction commits before the EF `SaveChangesAsync` for the audit row — same dual-write pattern as `EmitAsync`/`ProcessJobAsync`; the dual-write hazard is the same as Story 5.3 and accepted for v1.
- **`PgReservedKeywords` interaction with system-column guards** (debug-log #3): the system-column check in `SchemaDriftService.DropColumnAsync` must run BEFORE `SafeIdentifier.TryCreate(columnName, …)` because the reserved-keyword set includes the system-column names. Reordering changes the returned outcome from `ColumnNotFound` (404) to `ColumnProtected` (422), which is what the test contract for AC-6 requires. Documented inline in the service.
- **CA1873 on the `IsEnabled` guard around `LoggerMessage` partials**: the existing `LogOrphanedColumns` site in `DdlEmitter` already documents this in a `#pragma`; the new `LogDroppedColumn` site doesn't need the workaround because it's called unconditionally without a string-formatting hot-path argument (the only structured fields are `tableName` and `columnName`, both already validated `SafeIdentifier.Value` strings, no allocation).
- **`InvalidateDesigner` is best-effort**: `ConcurrentBag<int>` permits duplicate version entries from repeated `Populate` calls; `cache.Remove` on a non-existent key is a no-op, so duplicates don't break correctness. Recorded inline. A future story can swap to a `ConcurrentDictionary<string, HashSet<int>>` with an explicit lock if duplicate cache churn becomes measurable.
- **Drift inspection performance**: `SELECT COUNT(*) FROM {table} WHERE {col} IS NOT NULL` performs a full table scan per orphaned column. Acceptable for admin-only endpoints; the 60 s `DdlCommandTimeoutSeconds` provides an upper bound. A future story can replace with `pg_stats.null_frac * pg_class.reltuples` for an instant estimate — recorded as a future optimisation.
- **Frontend uses inline CSS and existing patterns**: `SchemaDriftView.tsx` mirrors the structure of `DesignerBindingSection.tsx` (inline styles, `useTranslation` for all user text, TanStack Query + mutation hooks, ApiError-based error code mapping). No new design tokens or component libraries introduced.
- **Pre-existing TS-test errors** (debug-log #4): the project's existing typecheck has 12 unrelated errors in `Navbar.test.tsx` (jest-dom typing) and `usePollProvisioning.test.tsx` (literal-type narrowing). Confirmed not introduced by Story 5.6 via a stash-pop comparison. Recorded so a future cleanup story can address them.
- **`ToVersion = 0` sentinel for DROP**: the `schema_audit_log.to_version` column is `INT NOT NULL`. DROP rows use `0` as a sentinel meaning "not version-bound". Story 5.7's audit log view must handle this — recorded in story Dev Notes already.
- **`AdminEndpoints.cs` registration order**: added `MapDesignerAdminEndpoints()` after `/menus`, so the JSON dispatcher reads top-down. DI resolves lazily, so the order is purely a readability preference.
- **`Program.cs` registration placement**: `SchemaDriftService` is `Scoped` (it injects `FormForgeDbContext`); registered alongside the other designer-feature services after `IDesignerService`.
- **Test counts**: total backend tests 374 → 395 (+21 = 10 Story 5.6 integration + 1 Story 5.6 unit + 10 from prior commits that landed in the suite between the spec's 374 baseline and this run). All 395 pass. Frontend production build: ✓ built in 17.02s with the new `designers._designerId.drift` chunk in the bundle.

### File List

**Added (12)**

- `src/FormForge.Api/Features/Designer/SchemaDriftService.cs`
- `src/FormForge.Api/Features/Designer/DesignerAdminEndpoints.cs`
- `src/FormForge.Api/Features/Designer/Dtos/SchemaDriftResponse.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260525203424_AddColumnsDroppedToSchemaAuditLog.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260525203424_AddColumnsDroppedToSchemaAuditLog.Designer.cs`
- `src/FormForge.Api.Tests/Features/SchemaRegistry/SchemaRegistryTests.cs`
- `web/src/features/admin/designers/designerAdminApi.ts`
- `web/src/features/admin/designers/useSchemaDriftQuery.ts`
- `web/src/features/admin/designers/schemaDriftMutations.ts`
- `web/src/features/admin/designers/SchemaDriftView.tsx`
- `web/src/routes/_app/admin/designers.$designerId.drift.tsx`

**Modified (11)**

- `src/FormForge.Api/Domain/Entities/SchemaAuditLogEntry.cs` — added `ColumnsDropped` property
- `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` — added `ColumnsDropped` text[] mapping
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/FormForgeDbContextModelSnapshot.cs` — regenerated by `dotnet ef`
- `src/FormForge.Api/Features/Provisioning/DdlEmitter.cs` — `SystemColumnNames` → `internal`; added `DropColumnAsync` + `LogDroppedColumn`
- `src/FormForge.Api/Features/SchemaRegistry/ISchemaRegistry.cs` — added `InvalidateDesigner`
- `src/FormForge.Api/Features/SchemaRegistry/SchemaRegistry.cs` — added `_populatedVersions` dict + `InvalidateDesigner` implementation
- `src/FormForge.Api/Features/Roles/AdminEndpoints.cs` — wired `/designers` admin sub-group + using
- `src/FormForge.Api/Features/Menus/MenuAdminEndpoints.cs` — added `targetVersion <= 0` guard to `GetBindingDiffHandler`
- `src/FormForge.Api/Program.cs` — registered `SchemaDriftService` scoped
- `src/FormForge.Api.Tests/Features/Provisioning/ProvisioningIntegrationTests.cs` — appended 10 Story 5.6 integration tests + 3 helpers + 2 DTOs
- `web/src/lib/i18n/locales/en.json` — added `admin.designers.drift.*` block, `admin.designers.notFound`, `admin.menus.invalidTargetVersion`

### Review Findings

- [x] [Review][Patch] P1 — `GetDriftAsync` COUNT loop unguarded against concurrent DROP: if a column is concurrently dropped between the `information_schema` fetch and the per-column `COUNT(*)`, Postgres throws "column does not exist" which propagates unhandled through `GetDriftAsync` to a 500 on GET /drift — add a try/catch around the `ExecuteScalarAsync<long>` call per orphaned column and skip/log the missing column gracefully [`SchemaDriftService.cs` COUNT loop]
- [x] [Review][Patch] P2 — No integration test for FK-column drop rejection (AC-6): the `IsFkColumn` guard (`parent_*_id`) is implemented but untested at the HTTP level — add `DropColumn_FkColumn_Returns422` following the same pattern as `DropColumn_SystemColumn_Returns422` [`ProvisioningIntegrationTests.cs`]
- [x] [Review][Defer] D1 — TOCTOU between probe connection and `DdlEmitter.DropColumnAsync`: a concurrent DROP by another request causes Postgres to throw "column does not exist" inside the DDL transaction, which propagates as an unhandled exception (uncontrolled 500) rather than a graceful 404/rollback path; no `DROP COLUMN IF EXISTS` guard [`SchemaDriftService.DropColumnAsync`] — deferred, pre-existing dual-write architecture; acceptable per AC-3 (DDL failure → 500 is spec-correct)
- [x] [Review][Defer] D2 — `IsFkColumn` over-matches user-authored fieldKeys shaped like `parent_notes_id` — such columns are silently excluded from the drift list and return `ColumnProtected`/422 even though they are legitimate data columns; `SafeIdentifier` does not block this shape [`SchemaDriftService.IsFkColumn`] — deferred, design gap inherited from Story 5.5 FK column naming; fix requires either blocking `parent_*_id` in `FieldKeyValidator` or switching to explicit FK-column metadata tracking
- [x] [Review][Defer] D3 — `GetTargetColumnNamesAsync` uses latest Published version, not the currently bound version — if a designer has Published v3 but the menu is still provisioned at v2, v2-only columns are reported as orphaned and can be dropped while they are still active data columns [`SchemaDriftService.GetTargetColumnNamesAsync`] — deferred, spec-defined behavior (AC-1 specifies "latest Published Designer schema")
- [x] [Review][Defer] D4 — `SchemaRegistry.InvalidateDesigner` races with a concurrent `Populate` call: a very narrow window can cause a version that was just re-populated to be evicted by the in-progress invalidation loop; worst consequence is a cache miss on the next Epic 6 read [`SchemaRegistry.cs:InvalidateDesigner`] — deferred, low severity, acceptable v1 race
- [x] [Review][Defer] D5 — DROP audit row has no recovery path if `SaveChangesAsync` fails after the DDL commits: the column is permanently gone but no audit row is written; Story 5.8 recovery scanner only handles Pending provisioning rows (CREATE/ALTER), not missing DROP audit rows [`SchemaDriftService.DropColumnAsync:159`] — deferred, more severe than the CREATE/ALTER dual-write hazard because there is no sentinel row for Story 5.8 to scan; document explicitly
- [x] [Review][Defer] D6 — `DropColumnAsync` returns `ColumnNotFound` (→ 404 `COLUMN_NOT_FOUND`) when the table itself isn't provisioned; the code `COLUMN_NOT_FOUND` is semantically misleading when the absence is at the table level [`SchemaDriftService.DropColumnAsync:139`] — deferred, low impact; 404 response is correct behavior
- [x] [Review][Defer] D7 — Confirmation `<div role="alertdialog">` lacks focus trapping; keyboard users can Tab out of the modal without dismissing it; native `<dialog>` with `.showModal()` would handle focus automatically [`SchemaDriftView.tsx:131`] — deferred, accessibility debt for Story 7.4
- [x] [Review][Defer] D8 — No integration test for `GET /drift` with a well-formed but non-existent `designerId` (returns 200 empty per spec; behavior is correct but the code path is untested) — deferred, low risk; code path is a straightforward one-liner via `TableExistsAsync`
- [x] [Review][Defer] D9 — `_populatedVersions ConcurrentBag<int>` grows unboundedly over repeated re-provisions; `cache.Remove` on non-existent keys is a no-op so correctness is unaffected, but the bag itself is never trimmed [`SchemaRegistry.cs`] — deferred, pre-existing, already documented in completion notes

### Change Log

| Date | Change |
|---|---|
| 2026-05-25 | Story 5.6 implementation complete: drift inspection endpoint (GET /api/admin/designers/{id}/drift), drop-column endpoint (DELETE /api/admin/designers/{id}/columns/{col}) with system-column / FK / active-column guards, schema-registry invalidation, DROP audit-row trail with `columns_dropped` + `correlation_id` + `actor_id`, drift view SPA route + component + i18n, deferred Story 5.2 `targetVersion ≤ 0` guard, +11 backend tests. Build clean (0 errors, 0 warnings); 395/395 backend tests pass; Vite production frontend build clean. Status: ready-for-dev → review. |
| 2026-05-26 | Code review complete: 2 patches, 9 deferred, 4 dismissed. Status: review → in-progress. |
| 2026-05-26 | Both patches applied (P1: concurrent-DROP guard in GetDriftAsync; P2: DropColumn_FkColumn_Returns422 test). Build clean. Status: in-progress → done. |
