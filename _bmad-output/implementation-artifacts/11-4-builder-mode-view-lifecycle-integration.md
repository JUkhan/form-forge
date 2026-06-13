# Story 11.4: Builder-Mode View Lifecycle Integration

Status: done

## Story

As the system,
I generate SQL from `builder_state` and apply the Epic 8 transactional view lifecycle on every builder-mode save,
so that Query Builder saves have identical atomicity and rollback safety to Custom Query saves.

## Acceptance Criteria

1. **Given** PUT /api/datasets/{id} with `is_custom_query = false`, **When** the server processes the request, **Then** `DatasetSqlGenerator.Generate` runs from `builder_state` **before** any DDL; if generation fails, HTTP 422 `BUILDER_STATE_INVALID` is returned and no DDL runs (per FR-73 AC-1).

2. **Given** SQL is generated successfully, **When** the VIEW lifecycle runs, **Then** the same Epic 8 transactional lifecycle applies: same-name edit → `DROP + CREATE VIEW`; rename → `ALTER VIEW … RENAME TO`; rename + query change → `ALTER VIEW … RENAME TO` then `DROP + CREATE VIEW`; all within one `NpgsqlTransaction` with full rollback on failure (per FR-73 AC-2 / AR-59).

3. **Given** a VIEW DDL operation fails (e.g., referenced table doesn't exist in Postgres), **When** the transaction rolls back, **Then** the `custom_dataset` row is unchanged at the previous version, and the original VIEW at the original name remains intact (per FR-73 AC-3).

4. **Given** the save succeeds, **When** I inspect `dataset_audit_log`, **Then** the `ddl` field contains the server-generated VIEW DDL prefixed with `-- Builder-generated` to mark it as server-derived (per FR-73 AC-4).

## Tasks / Subtasks

---

### Task 1: Backend — Annotate builder-generated DDL in audit log (AC: 4)

- [x] Modify `src/FormForge.Api/Features/Datasets/DatasetService.cs` (MODIFY — `UpdateAsync` method only):
  - [x] Locate the **Step E** comment block (around line 417) where `primaryDdl` is built:
    ```csharp
    var primaryDdl = isRename
        ? DatasetViewManager.BuildRenameViewDdl(current.DatasetName, effectiveNewName)
        : DatasetViewManager.BuildReplaceViewDdl(resolvedNewName!, effectiveViewQuery);
    if (isRename && queryChanged)
    {
        primaryDdl = primaryDdl + "\n"
            + DatasetViewManager.BuildReplaceViewDdl(resolvedNewName!, effectiveViewQuery);
    }
    ```
  - [x] Immediately **after** that block (before the transaction opens at "Step F"), add:
    ```csharp
    // Story 11.4 (FR-73 AC-4) — annotate builder-generated DDL for audit log readability.
    if (builderRegenerated)
        primaryDdl = "-- Builder-generated\n" + primaryDdl;
    ```
  - [x] **Do NOT** change `CreateAsync` — Story 11.4 ACs only apply to the PUT (update) path.
  - [x] **Do NOT** change anything else in `UpdateAsync` — ACs 1–3 are already satisfied by the existing Story 11.1 code path (lines 361-383).

---

### Task 2: Backend — Integration tests for Story 11.4 (AC: 1–4)

- [x] Create `src/FormForge.Api.Tests/Features/Datasets/DatasetBuilderLifecycleTests.cs` (NEW):

  Use the same test infrastructure as `DatasetBuilderModeTests.cs`:
  - `[Collection("DatasetIntegrationTests")]` + `IClassFixture<PostgresFixture>` + `IAsyncLifetime`
  - `WebApplicationFactory<Program>` with `UseSetting` for `ConnectionStrings:formforge`, `Jwt:SigningKey`, `Cors:AllowedOrigins:0`
  - `UseSetting("DatasetManager:AllowedTables:0", "blc_probe")` and `UseSetting("DatasetManager:AllowedTables:1", "blc_ghost")` — see Task 2 §3 below for why two tables are needed
  - `InitializeAsync`: `MigrateAsync`, TRUNCATE tables, drop datasets schema VIEWs, seed `public.blc_probe` table (real), do NOT seed `public.blc_ghost` (intentionally absent for AC-3 test), seed admin role + user
  - `DisposeAsync`: drop `public.blc_probe`
  - Use `builder_probe` / `blc_probe` names that cannot collide with other test class tables in the shared `PostgresFixture`

  **Test 1: `Put_BuilderMode_Save_ViewExistsWithGeneratedSql` (AC: 1, 2)**
  - Create a builder-mode dataset (no builder_state — placeholder VIEW)
  - PUT with valid `builder_state` pointing to `blc_probe.id` (left node, checked)
  - Assert 200, `dto.Query` contains `FROM "public"."blc_probe"`
  - Assert VIEW `datasets.<name>` exists and `pg_get_viewdef` contains `blc_probe`

  **Test 2: `Put_BuilderMode_Rename_ViewLifecycleApplies` (AC: 2)**
  - Create a builder-mode dataset with `builder_state` (generates VIEW from `blc_probe.id`)
  - PUT with new `datasetName` + same `builder_state` → triggers rename + query change branch
  - Assert 200, old VIEW name is gone, new VIEW name exists with `blc_probe`

  **Test 3: `Put_BuilderMode_DdlFailure_OriginalViewIntact` (AC: 3)**
  - Create a builder-mode dataset with valid builder_state pointing to `blc_probe.id`
  - PUT with new builder_state pointing to `blc_ghost.id` (`blc_ghost` is in allowlist but NOT created in Postgres)
  - The SQL generator succeeds (allowlist check passes), but CREATE VIEW fails at Postgres level (42P01 table not found)
  - Assert 422 `BUILDER_STATE_INVALID` or `INVALID_QUERY` (whichever the endpoint returns on DDL NpgsqlException)
  - Assert via raw connection: `custom_dataset.version` is still 1 (row rolled back)
  - Assert original VIEW `datasets.<name>` still exists and references `blc_probe`

  **Test 4: `Put_BuilderMode_Save_AuditDdl_HasBuilderGeneratedMarker` (AC: 4)**
  - Create a builder-mode dataset (placeholder)
  - PUT with valid `builder_state`
  - Assert 200
  - Query `dataset_audit_log` via EF for the UPDATE entry
  - Assert `entry.Ddl` starts with `"-- Builder-generated\n"` (`StringComparison.Ordinal`)
  - Assert `entry.Succeeded == true`

  **Test 5: `Put_BuilderMode_InvalidBuilderState_Returns422_NoDdl` (AC: 1)**
  - Create a builder-mode dataset
  - PUT with builder_state where node has `side = "right"` (no left node → generation fails)
  - Assert 422 `BUILDER_STATE_INVALID`
  - Assert no UPDATE entry in `dataset_audit_log` (generation failure returns before audit is written)

---

### Task 3: Verify

- [x] `dotnet build src/FormForge.Api` → 0 warnings / 0 errors
- [x] `dotnet test src/FormForge.Api.Tests` → all 5 new lifecycle tests pass; pre-existing 2 audit 405 failures remain (do NOT reinvestigate); `DatasetBuilderModeTests` still fully passes (existing builder tests are unaffected by the annotation-only change)
- [x] `npm run check` → no TypeScript errors (no frontend changes in this story) — repo has no `check` script; ran the equivalent `tsc -b --noEmit` (exit 0)

---

### Review Findings

_Code review 2026-06-05 (Blind Hunter + Edge Case Hunter + Acceptance Auditor). Production code verified correct on all 4 ACs; one test-coverage gap found._

- [x] [Review][Patch] AC-2 "rename + query-change" sub-case has no test coverage [src/FormForge.Api.Tests/Features/Datasets/DatasetBuilderLifecycleTests.cs:142] — Test 2 PUTs the *same* `builder_state` on rename, so the deterministic generator yields SQL identical to `current.Query` → `queryChanged == false` → only `RenameAsync` runs, never the `RenameAsync`→`ReplaceAsync` branch (DatasetService.cs:485-489). That regen+rename+REPLACE path (the one Story 11.1's `queryChanged` expansion at DatasetService.cs:389-390 and this story's `-- Builder-generated` annotation target) is untested. Fix: PUT a *different* `builder_state` (e.g. a different `blc_probe` column) alongside the new name so `effectiveNewQuery != current.Query`, then assert the renamed VIEW reflects the new definition. Flagged independently by Acceptance Auditor + Edge Case Hunter (Med). **RESOLVED 2026-06-05:** added Test 6 `Put_BuilderMode_RenameAndQueryChange_ViewRedefinedUnderNewName` — creates with `blc_probe.id`, PUTs new name + `blc_probe.status` (queryChanged=true), asserts old VIEW gone / new VIEW redefined with `status`, and audit DDL = `-- Builder-generated\n` + `ALTER VIEW` + `CREATE VIEW datasets."blc_renqc_done"`. All 6 lifecycle tests pass.

---

## Dev Notes

### 1. What Already Exists — ACs 1, 2, 3 Are Already Satisfied

**The hard part of this story was done in Story 11.1.** Looking at `DatasetService.UpdateAsync` (lines 361-383), the builder-mode path already:

- Runs `DatasetSqlGenerator.Generate` before the transaction (AC-1): if `HasErrors`, returns `UpdateDatasetResult(UpdateDatasetOutcome.BuilderStateInvalid, …)` — endpoint maps to 422, no DDL.
- Sets `effectiveNewQuery = generated.ViewSql` (uses `ViewSql` NOT `ParameterizedSql` for VIEW DDL — correct)
- Sets `builderRegenerated = true` which factors into `queryChanged` (line 390), ensuring a builder-mode query change triggers the VIEW REPLACE even on a rename (AC-2)
- The `NpgsqlTransaction` wraps row UPDATE + all VIEW DDL steps — rollback on any `NpgsqlException` rolls back both (AC-3)

**The only missing piece is AC-4**: the audit `primaryDdl` needs `"-- Builder-generated\n"` prepended when `builderRegenerated == true`. This is Task 1 — a 2-line change.

### 2. Exact Change Location in `DatasetService.UpdateAsync`

The `builderRegenerated` variable is declared at line 368 and set at line 382. The `primaryDdl` variable is built at lines 418-424 (Step E). The transaction opens at line 428 (Step F). Insert the annotation **between Step E and Step F**:

```csharp
// EXISTING Step E (do NOT change):
var primaryDdl = isRename
    ? DatasetViewManager.BuildRenameViewDdl(current.DatasetName, effectiveNewName)
    : DatasetViewManager.BuildReplaceViewDdl(resolvedNewName!, effectiveViewQuery);
if (isRename && queryChanged)
{
    primaryDdl = primaryDdl + "\n"
        + DatasetViewManager.BuildReplaceViewDdl(resolvedNewName!, effectiveViewQuery);
}

// ADD HERE (Story 11.4 AC-4):
// Story 11.4 (FR-73 AC-4) — annotate builder-generated DDL for audit log readability.
if (builderRegenerated)
    primaryDdl = "-- Builder-generated\n" + primaryDdl;

// EXISTING Step F (unchanged):
var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
```

### 3. `blc_ghost` Table Strategy for AC-3 Test

The goal of Test 3 is to trigger a DDL failure after the row UPDATE has been issued inside the transaction (to prove rollback works). The generator validates against the allowlist (`IsAllowed`) but does NOT check PostgreSQL table existence — so a table that's allowlisted but not in the DB will pass generation and fail at `CREATE VIEW`.

Configure both tables in the allowlist via `UseSetting`:
```csharp
builder.UseSetting("DatasetManager:AllowedTables:0", "blc_probe");
builder.UseSetting("DatasetManager:AllowedTables:1", "blc_ghost");
```
Seed `public.blc_probe` in `InitializeAsync`. Do NOT seed `public.blc_ghost`. When `builder_state` points to `blc_ghost`, the SQL generator succeeds, but `CREATE VIEW datasets."..." AS SELECT "blc_ghost"."id" … FROM "public"."blc_ghost"` fails at PostgreSQL with 42P01 (undefined table) — the transaction rolls back, row stays at version 1, original VIEW survives.

The endpoint maps the `NpgsqlException` catch in `UpdateAsync` to `UpdateDatasetOutcome.InvalidQuery` → HTTP 422 with `code = "INVALID_QUERY"`. Assert `HttpStatusCode.UnprocessableEntity`.

### 4. Avoiding Table Name Collisions in Shared `PostgresFixture`

The `PostgresFixture` shares one Postgres instance across all test classes in the `DatasetIntegrationTests` collection. Tables in the `public` schema persist between test class runs (they're not TRUNCATED). Use table names prefixed with `blc_` (Builder Lifecycle) to avoid collisions with:
- `builder_probe` (used by `DatasetBuilderModeTests`)
- Any existing public tables

Always `DROP TABLE IF EXISTS public.blc_probe CASCADE` in `DisposeAsync` to clean up.

### 5. ViewSql vs ParameterizedSql — Use ViewSql for DDL

The existing code correctly uses `generated.ViewSql` (not `generated.ParameterizedSql`) for the VIEW DDL. PostgreSQL VIEWs cannot contain `$1` parameter placeholders. `ParameterizedSql` is only for the preview endpoint (`POST /api/datasets/preview`). Do NOT change this.

### 6. `DatasetViewManager` DDL Methods — Already Correct

`DatasetViewManager.BuildReplaceViewDdl` emits `DROP VIEW IF EXISTS datasets."...";\nCREATE VIEW datasets."..." AS {query}`. This handles arbitrary column set changes (the Story 8.10 fix). The builder-mode code path uses this exact method — no changes needed there.

`DatasetViewManager.BuildRenameViewDdl` emits `ALTER VIEW datasets."..." RENAME TO "..."`. PostgreSQL DDL is fully transactional, so RENAME + REPLACE both roll back atomically if either step fails.

### 7. No Frontend Changes

Story 11.4 is entirely backend. The builder-page save button (`datasets_.$id.tsx`) already sends `is_custom_query = false` + `builder_state` from Story 11.2. No UI changes.

### 8. `DatasetBuilderModeTests` — Do Not Break

`DatasetBuilderModeTests` already tests that builder-mode PUT returns 200 with a server-generated query. After Task 1, the audit DDL will have `-- Builder-generated\n` prepended, but those tests don't assert the DDL content — they're unaffected. Run them as part of the verify step to confirm no regression.

### 9. CA2007 and Connection Disposal Pattern

This project enforces CA2007 (`ConfigureAwait(false)` on every await). The test code for `NpgsqlConnection` must use:
```csharp
await using var conn = new NpgsqlConnection(_postgres.ConnectionString);
await conn.OpenAsync();
```
Or the `OpenRawAsync()` helper pattern from `DatasetUpdateTests.cs`. Do NOT use bare `await using` on a `NpgsqlConnection` — the project's test infrastructure uses the same helper pattern.

### Project Structure Notes

**Modified files:**
- `src/FormForge.Api/Features/Datasets/DatasetService.cs` — 2-line change in `UpdateAsync` after Step E

**New files:**
- `src/FormForge.Api.Tests/Features/Datasets/DatasetBuilderLifecycleTests.cs` — 5 integration tests

**No changes to:**
- `DatasetViewManager.cs` — DDL builders already correct
- `DatasetSqlGenerator.cs` — generator already correct
- `DatasetEndpoints.cs` — endpoint error mapping already correct
- Any frontend files
- Any migrations (no schema changes)

### References

- Epics: `_bmad-output/planning-artifacts/epics.md` §Story 11.4 (FR-73 ACs 1–4)
- Architecture: `_bmad-output/planning-artifacts/architecture.md` §6.3 (Transactional View Lifecycle / AR-59), §6.10 (SQL Generator — ViewSql vs ParameterizedSql)
- `src/FormForge.Api/Features/Datasets/DatasetService.cs:361-383` — Story 11.1 builder-mode generation block (`builderRegenerated`)
- `src/FormForge.Api/Features/Datasets/DatasetService.cs:417-424` — Step E: `primaryDdl` construction (insert annotation after line 424)
- `src/FormForge.Api/Features/Datasets/DatasetViewManager.cs:51-53` — `BuildReplaceViewDdl` (DROP + CREATE, Story 8.10 fix)
- `src/FormForge.Api/Features/Datasets/DatasetViewManager.cs:57-58` — `BuildRenameViewDdl` (ALTER RENAME)
- `src/FormForge.Api/Features/Datasets/DatasetSqlGenerator.cs:22-24` — `ViewSql` vs `ParameterizedSql` distinction
- `src/FormForge.Api.Tests/Features/Datasets/DatasetBuilderModeTests.cs` — test infrastructure pattern to replicate (WebApplicationFactory, allowlist config, `BuilderStateJson` helper)
- `src/FormForge.Api.Tests/Features/Datasets/DatasetUpdateTests.cs` — `OpenRawAsync`, `GetAuditEntriesAsync`, `ViewExistsAsync` helpers to replicate
- Memory: Pre-existing audit 405 test failures — 2 tests fail on clean tree, do NOT reinvestigate
- Memory: Validators registered explicitly — no new validators needed for this story

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (1M context)

### Debug Log References

- Full API test suite run was abnormally long (5h40m, Docker/Testcontainers contention). It reported 3 failures: the 2 known pre-existing audit `DELETE→405` tests (`MutationAuditLogIntegrationTests`, `SchemaAuditLogIntegrationTests` — not reinvestigated per project memory) plus a flaky `DynamicCrudIntegrationTests.ExportRecords_NonAdminWithExport_Returns200` provisioning-poll timeout ("Pending" vs "Success" in `SetupProvisionedDesignerWithTitleAsync`). Re-ran that export test in isolation → passed in 7s. It is unrelated to this dataset-only change (DynamicCrud provisioning subsystem) and was a load-induced timeout, not a regression.

### Completion Notes List

- **AC-4 (Task 1):** Added a 2-line annotation in `DatasetService.UpdateAsync` between Step E (`primaryDdl` construction) and Step F (transaction open): when `builderRegenerated == true`, `primaryDdl` is prefixed with `"-- Builder-generated\n"`. `Ddl = primaryDdl` (line ~552) is persisted to `dataset_audit_log` on both the success and failure paths, so builder-mode saves are now marked as server-derived. `CreateAsync` and all other `UpdateAsync` logic were left unchanged.
- **ACs 1–3 (verified, not re-implemented):** confirmed against the live code that the Story 11.1 path already satisfies them — `DatasetSqlGenerator.Generate` runs before the transaction and returns 422 `BUILDER_STATE_INVALID` with no DDL on failure (AC-1); the row UPDATE + VIEW DDL share one `NpgsqlTransaction` with full rollback (AC-2); a Postgres DDL failure (`NpgsqlException`) rolls back the row and leaves the original VIEW intact, mapped to 422 `INVALID_QUERY` (AC-3).
- **Task 2:** Added `DatasetBuilderLifecycleTests.cs` with 5 integration tests (save→VIEW exists with generated SQL, rename lifecycle, DDL-failure rollback via the allowlisted-but-absent `blc_ghost` table, `-- Builder-generated` audit marker, invalid builder_state→422 with no audit row). All 5 pass; the 7 existing `DatasetBuilderModeTests` still pass.
- **Deviation note:** `GetViewDefAsync` uses a parameterized `@rc::regclass` cast instead of an interpolated identifier to satisfy CA2100 (interpolating the view name into the command text was a build error). Behavior is identical.
- **Verify:** `dotnet build src/FormForge.Api` → 0/0; test project build → 0/0; 5 new + 7 existing builder tests pass; `tsc -b --noEmit` → exit 0 (repo exposes no `npm run check` script; ran its TypeScript equivalent).

### File List

- `src/FormForge.Api/Features/Datasets/DatasetService.cs` — MODIFIED (2-line builder-generated DDL annotation in `UpdateAsync`, between Step E and Step F)
- `src/FormForge.Api.Tests/Features/Datasets/DatasetBuilderLifecycleTests.cs` — NEW (6 integration tests for AC 1–4; Test 6 added during code review to cover the rename + query-change branch)

## Change Log

| Date       | Version | Description                                                                 | Author |
| ---------- | ------- | --------------------------------------------------------------------------- | ------ |
| 2026-06-05 | 1.0     | Implemented Story 11.4: builder-generated DDL audit annotation (AC-4) + 5 lifecycle integration tests (AC 1–4). ACs 1–3 already satisfied by Story 11.1. | Amelia (Dev) |
| 2026-06-05 | 1.1     | Code review (Blind Hunter + Edge Case Hunter + Acceptance Auditor): production code verified correct on all 4 ACs. Resolved 1 patch finding — added Test 6 covering the rename + query-change VIEW lifecycle branch. 7 findings dismissed as noise/out-of-scope. Status → done. | Amelia (Dev) |
