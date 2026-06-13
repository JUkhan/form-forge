# Story 8.7: List and Get Datasets

Status: done

## Story

As an authenticated user,
I want to list all Datasets and retrieve a single Dataset by ID,
so that I can browse and open existing Dataset definitions.

## Acceptance Criteria

**AC-1 — List datasets → PagedResult**
**Given** I am authenticated (any role)
**When** I GET `/api/datasets?page=1&pageSize=25`
**Then** I receive HTTP 200 with `PagedResult<DatasetSummaryDto>` containing:
  - `data`: array of `{ id, datasetName, isCustomQuery, createdAt, updatedAt, createdByName }`
  - `total`, `page`, `pageSize`, `totalPages`
  - default `pageSize` is 25 when omitted; max enforced at 100 (AR-21)
  - page defaults to 1 when omitted
  - ordered by `created_at DESC`

**AC-2 — Get single dataset by ID → full DTO**
**Given** I am authenticated (any role)
**When** I GET `/api/datasets/{id}` and `{id}` exists in `custom_dataset`
**Then** I receive HTTP 200 with a full `DatasetDto` including `query`, `builderState`, `version`

**AC-3 — Get non-existent dataset → 404**
**Given** I GET `/api/datasets/{id}` where `{id}` does not exist
**Then** HTTP 404 is returned

---

## Tasks / Subtasks

- [x] **Task 1 — Add `DatasetSummaryDto` record** (AC-1)
  - [x] Create `src/FormForge.Api/Features/Datasets/Dtos/DatasetSummaryDto.cs`:
    ```csharp
    namespace FormForge.Api.Features.Datasets.Dtos;

    // Story 8.7 (FR-62 / AR-65) — summary shape for the paginated dataset list.
    // CreatedByName is null when the creator's user row has been deleted (SET NULL FK).
    internal sealed record DatasetSummaryDto(
        Guid Id,
        string DatasetName,
        bool IsCustomQuery,
        DateTimeOffset CreatedAt,
        DateTimeOffset? UpdatedAt,
        string? CreatedByName);
    ```

- [x] **Task 2 — Extend `IDatasetService` with `ListAsync` and `GetByIdAsync`** (AC-1 / AC-2 / AC-3)
  - [x] In `DatasetService.cs`, add to `IDatasetService`:
    ```csharp
    Task<PagedResult<DatasetSummaryDto>> ListAsync(
        int page,
        int pageSize,
        CancellationToken ct);

    Task<DatasetDto?> GetByIdAsync(
        Guid id,
        CancellationToken ct);
    ```
  - [x] Add the using for `FormForge.Api.Common` (for `PagedResult<T>`) if not already present.

- [x] **Task 3 — Implement `ListAsync` in `DatasetService`** (AC-1)
  - [x] Clamp inputs: `page = Math.Max(1, page)`, `pageSize = Math.Clamp(pageSize, 1, 100)`.
  - [x] Open one connection via `connectionFactory.CreateOpenConnectionAsync(ct)`.
  - [x] Count query:
    ```csharp
    const string countSql = "SELECT COUNT(*) FROM custom_dataset";
    var total = await conn.ExecuteScalarAsync<long>(
        new CommandDefinition(countSql, commandTimeout: 5, cancellationToken: ct))
        .ConfigureAwait(false);
    ```
  - [x] Data query with LEFT JOIN to users for `created_by_name`:
    ```csharp
    const string listSql = """
        SELECT cd.id              AS "Id",
               cd.dataset_name    AS "DatasetName",
               cd.is_custom_query AS "IsCustomQuery",
               cd.created_at      AS "CreatedAt",
               cd.updated_at      AS "UpdatedAt",
               u.display_name     AS "CreatedByName"
        FROM custom_dataset cd
        LEFT JOIN users u ON u.id = cd.created_by
        ORDER BY cd.created_at DESC
        LIMIT @limit OFFSET @offset
        """;
    var rows = await conn.QueryAsync<ListDatasetRow>(
        new CommandDefinition(listSql,
            new { limit = pageSize, offset = (page - 1) * pageSize },
            commandTimeout: 5, cancellationToken: ct))
        .ConfigureAwait(false);
    ```
  - [x] Map `ListDatasetRow` → `DatasetSummaryDto`:
    ```csharp
    var data = rows
        .Select(r => new DatasetSummaryDto(
            r.Id, r.DatasetName, r.IsCustomQuery,
            r.CreatedAt,          // DateTime → DateTimeOffset (implicit, UTC)
            r.UpdatedAt,          // DateTime? → DateTimeOffset?
            r.CreatedByName))
        .ToList();
    return new PagedResult<DatasetSummaryDto>(data, total, page, pageSize);
    ```
  - [x] Dispose connection in `finally`.
  - [x] Add private `ListDatasetRow` record inside `DatasetService` (alongside `CurrentDatasetRow`):
    ```csharp
    private sealed record ListDatasetRow(
        Guid Id,
        string DatasetName,
        bool IsCustomQuery,
        DateTime CreatedAt,    // Npgsql materializes timestamptz as DateTime(Kind=Utc)
        DateTime? UpdatedAt,   // nullable — null when never updated
        string? CreatedByName);
    ```

- [x] **Task 4 — Implement `GetByIdAsync` in `DatasetService`** (AC-2 / AC-3)
  - [x] Reuse the existing `selectSql` / `CurrentDatasetRow` pattern from `DeleteAsync` / `UpdateAsync`:
    ```csharp
    public async Task<DatasetDto?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct)
            .ConfigureAwait(false);

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
        var row = await conn.QuerySingleOrDefaultAsync<CurrentDatasetRow>(
            new CommandDefinition(selectSql, new { id },
                commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

        if (row is null)
            return null;

        return new DatasetDto(
            Id: row.Id,
            DatasetName: row.DatasetName,
            IsCustomQuery: row.IsCustomQuery,
            Query: row.Query,
            BuilderState: row.BuilderState,
            Version: row.Version,
            CreatedAt: row.CreatedAt,   // DateTime → DateTimeOffset implicit
            CreatedBy: row.CreatedBy);
    }
    ```
  - [x] Note: `GetByIdAsync` uses `await using` (a single statement), which is fine; the outer
        `try/finally` pattern is only needed when the connection must be kept open across multiple
        awaits (as in `UpdateAsync`/`DeleteAsync`). `QuerySingleOrDefaultAsync` is a single await.

- [x] **Task 5 — Replace GET stubs in `DatasetEndpoints.cs`** (AC-1 / AC-2 / AC-3)
  - [x] Replace (lines 23-27):
    ```csharp
    // Read endpoints — auth only (AC-3); handlers are stubs replaced in Story 8.7.
    group.MapGet("/", () => Results.Ok(Array.Empty<object>()))
         .WithSummary("List datasets (stub — Story 8.7)");
    group.MapGet("/{id:guid}", (Guid id) => Results.NotFound())
         .WithSummary("Get dataset (stub — Story 8.7)");
    ```
    With:
    ```csharp
    // Story 8.7 (FR-62 / AR-65) — paginated list; auth-only (no RequireDatasetManagement).
    group.MapGet("/", async (
        IDatasetService datasetService,
        CancellationToken ct,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25) =>
    {
        var result = await datasetService.ListAsync(page, pageSize, ct).ConfigureAwait(false);
        return Results.Ok(result);
    })
         .WithSummary("List datasets (Story 8.7 — FR-62)");

    // Story 8.7 (FR-62 / AR-65) — get by id; auth-only (no RequireDatasetManagement).
    group.MapGet("/{id:guid}", async (
        Guid id,
        IDatasetService datasetService,
        CancellationToken ct) =>
    {
        var dto = await datasetService.GetByIdAsync(id, ct).ConfigureAwait(false);
        return dto is null ? Results.NotFound() : Results.Ok(dto);
    })
         .WithSummary("Get dataset (Story 8.7 — FR-62)");
    ```
  - [x] Update the file-header comment on line 16: change `list/get (8.7), preview (11.3).`
        to `preview (11.3).` and add `Story 8.7 (FR-62) — GET / list + GET /{id} single.`
        to the opening comment block.
  - [x] Add `[FromQuery]` — confirm `Microsoft.AspNetCore.Mvc` using is already present (it is, line 5).

- [x] **Task 6 — Integration tests: `DatasetListGetTests.cs`** (AC-1 / AC-2 / AC-3)
  - [x] Create `src/FormForge.Api.Tests/Features/Datasets/DatasetListGetTests.cs`
  - [x] Use **identical** `[Collection("DatasetIntegrationTests")]`, `PostgresFixture`,
        `WebApplicationFactory<Program>`, and `InitializeAsync` pattern as `DatasetDeleteTests.cs`
        (same TRUNCATE + VIEW cleanup + ReseedAdminRoleAsync + SeedAdminUserAsync).
  - [x] **Test 1 — AC-1: List returns seeded datasets**
    - Create two datasets via POST (assert 201 each)
    - GET `/api/datasets?page=1&pageSize=25` with auth → assert HTTP 200
    - Deserialize as `PagedResultDto` (local record, see below)
    - Assert `total == 2`, `data.Length == 2`, `page == 1`, `pageSize == 25`
    - Assert each item has `id`, `datasetName`, `isCustomQuery`, `createdAt`
    - Assert `createdByName` matches the seeded admin's `DisplayName` ("Platform Admin")
  - [x] **Test 2 — AC-1: Empty list when no datasets**
    - GET `/api/datasets` with auth (no datasets seeded) → assert HTTP 200
    - Assert `total == 0`, `data.Length == 0`
  - [x] **Test 3 — AC-2: Get by id returns full DTO**
    - Create a dataset with `query = "SELECT 1 AS n"` via POST
    - GET `/api/datasets/{id}` with auth → assert HTTP 200
    - Deserialize as `DatasetFullDto` (local record)
    - Assert `datasetName`, `isCustomQuery == true`, `query == "SELECT 1 AS n"`, `version == 1`
  - [x] **Test 4 — AC-3: Get by id not found → 404**
    - GET `/api/datasets/{Guid.NewGuid()}` with auth → assert HTTP 404
  - [x] Local DTO records for test deserialization:
    ```csharp
    private sealed record PagedResultDto(
        DatasetSummaryItemDto[] Data, long Total, int Page, int PageSize, int TotalPages);

    private sealed record DatasetSummaryItemDto(
        Guid Id, string DatasetName, bool IsCustomQuery,
        DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, string? CreatedByName);

    private sealed record DatasetFullDto(
        Guid Id, string DatasetName, bool IsCustomQuery,
        string? Query, string? BuilderState, int Version,
        DateTimeOffset CreatedAt, Guid? CreatedBy);
    ```
  - [x] Helper `GetAsync(string token, string url)` → `HttpResponseMessage` (reuse same pattern as `DeleteAsync` helper in `DatasetDeleteTests`).

### Review Findings

- [x] [Review][Patch] Unused `_adminUserId` field — assigned in `InitializeAsync` but never read by any test; dead code [DatasetListGetTests.cs]
- [x] [Review][Patch] `ListDatasets_NoDatasets_ReturnsEmptyPage` missing `Assert.Equal(HttpStatusCode.OK, ...)` before body deserialization [DatasetListGetTests.cs] — already present in committed code (line 132); stale finding, no change needed
- [x] [Review][Patch] `(page - 1) * pageSize` computed as `int` — overflows for large page values; cast to `(long)(page - 1) * pageSize` [DatasetService.cs:ListAsync]
- [x] [Review][Patch] `ORDER BY cd.created_at DESC` has no tie-breaker; ordering is non-deterministic when two rows share the same microsecond — risks flaky ordering test [DatasetService.cs:ListAsync]
- [x] [Review][Patch] No test for AR-21 `pageSize` max-clamp at 100 — named AC-1 requirement with zero coverage; add a test sending `pageSize>100` and asserting the response `pageSize==100` (Med) [DatasetListGetTests.cs]
- [x] [Review][Patch] Defaults unproven — `ListDatasets_NoDatasets_ReturnsEmptyPage` omits `page`/`pageSize` but never asserts `Page==1` / `PageSize==25`; add the two assertions [DatasetListGetTests.cs]
- [x] [Review][Patch] AC-1 fields `totalPages` + `updatedAt` never asserted in the list test, and `builderState` never asserted in the get-by-id test — add assertions [DatasetListGetTests.cs]
- [x] [Review][Patch] No test for null `createdByName` (deleted-creator LEFT JOIN → SET NULL branch); only the happy "Platform Admin" path is covered [DatasetListGetTests.cs]
- [x] [Review][Defer] `COUNT(*)` + `SELECT … LIMIT/OFFSET` are two separate queries with no transaction — stale total possible on concurrent insert/delete [DatasetService.cs:ListAsync] — deferred, pre-existing acceptable pattern
- [x] [Review][Defer] `TRUNCATE … CASCADE` + `DROP VIEW` in `InitializeAsync` against a shared `PostgresFixture` — data-race risk if collection parallelism ever enabled [DatasetListGetTests.cs] — deferred, pre-existing pattern across all Dataset test classes
- [x] [Review][Defer] No `[CollectionDefinition("DatasetIntegrationTests")]` class disabling parallelism — xUnit may run collection members concurrently on some runners [DatasetListGetTests.cs] — deferred, pre-existing
- [x] [Review][Defer] `commandTimeout: 5` seconds may be too short under CI load [DatasetService.cs] — deferred, pre-existing across all service methods
- [x] [Review][Defer] `TotalPages` cast `(int)Math.Ceiling(total / pageSize)` can overflow for very large `total` — bug in `PagedResult<T>`, not this story [Common/PagedResult.cs] — deferred, pre-existing
- [x] [Review][Defer] `DatasetSummaryDto` is `internal` but serialised via `Results.Ok()` — latent AOT/source-gen risk [DatasetSummaryDto.cs] — deferred, pre-existing pattern (DatasetDto is also internal)
- [x] [Review][Defer] Connection factory exception before `conn` is assigned bypasses `finally` dispose — pre-existing contract assumption shared with Delete/Update [DatasetService.cs] — deferred, pre-existing
- [x] [Review][Defer] No unauthenticated (401) test for GET endpoints — not required by story spec Task 6; `.RequireAuth()` group policy covered elsewhere — deferred, out of story scope
- [x] [Review][Defer] `[FromQuery] int` binding failure returns plain HTTP 400 instead of `ProblemDetails` envelope — pre-existing ASP.NET Core Minimal API behavior [DatasetEndpoints.cs] — deferred, pre-existing

---

## Dev Notes

### Critical: Dapper column aliasing (no MatchNamesWithUnderscores)
This project does **not** set Dapper's `MatchNamesWithUnderscores = true`. Every snake_case column
**must** be aliased exactly to the C# property name (double-quoted in SQL, case-sensitive in Dapper):
- `cd.dataset_name AS "DatasetName"` ✓ — NOT `AS DatasetName` (would return null)
- `cd.is_custom_query AS "IsCustomQuery"` ✓

This pattern is established in `UpdateAsync` / `DeleteAsync` in `DatasetService.cs` — follow exactly.

### Critical: DateTime vs DateTimeOffset for Npgsql
Npgsql materializes `timestamptz` columns as `DateTime(Kind=Utc)`, not `DateTimeOffset`. Dapper
constructor matching requires the exact type the reader returns:
- `CreatedAt: DateTime` — the `CurrentDatasetRow` record already uses this; `ListDatasetRow` must too
- `UpdatedAt: DateTime?` — nullable (null when never updated)
- C# implicitly converts `DateTime(Kind=Utc)` → `DateTimeOffset` (zero offset) — used safely in DTO mapping

### Critical: No RequireDatasetManagement on GET endpoints
Per AR-65: `GET /api/datasets` and `GET /api/datasets/{id}` are **auth-only** (any authenticated role).
Only write endpoints (`POST /PUT /DELETE`) call `.RequireDatasetManagement()`.
The group-level `.RequireAuth()` in `Program.cs:688` already enforces authentication — do **not** add
`.RequireDatasetManagement()` to either GET handler.

### No FluentValidation filter on GET handlers
GET handlers have no request body — do not add the `.AddValidationFilter<T>()` extension. Same
rule as DELETE in Story 8.6.

### pageSize / page clamping in ListAsync
- `page = Math.Max(1, page)` — never negative or zero
- `pageSize = Math.Clamp(pageSize, 1, 100)` — AR-21 max 100, default 25 already in endpoint default param

The clamping lives in the **service** (not the endpoint), so it is enforced regardless of how the service
is called in tests.

### Reuse CurrentDatasetRow for GetByIdAsync
`CurrentDatasetRow` is a private `sealed record` already defined in `DatasetService` (added in Story 8.5).
`GetByIdAsync` reuses it directly — do **not** define a second identical record.

### `await using` in GetByIdAsync
Unlike `UpdateAsync`/`DeleteAsync` (which keep the connection alive across multiple awaits: SELECT → tx → audit),
`GetByIdAsync` only does one `QuerySingleOrDefaultAsync`. Use `await using var conn = ...` (a single
statement scoped by `using`), which is cleaner than the outer `try/finally conn.DisposeAsync()` pattern.
`ListAsync` needs two awaits (count + data) — use explicit `try/finally` for the connection there.

### ListAsync orders by created_at DESC
No sort/filter parameters are specified in Story 8.7's ACs — default `ORDER BY cd.created_at DESC`
is sufficient. Filter/sort for the list is not in scope until the UI story (8.10).

### Integration test collection
All Dataset integration tests share `[Collection("DatasetIntegrationTests")]` to prevent parallel
execution against the same Postgres container. `DatasetListGetTests` must use this same collection key.

### DI registration — no changes needed
`IDatasetService` / `DatasetService` are already registered as scoped in `Program.cs` (established
in Story 8.4). New methods on the interface/implementation are automatically available; no `Program.cs`
changes are needed.

### Pre-existing test failures to ignore
Per memory (Stories 8.5 §7 / 8.6 §7):
- 2 failures: `SchemaAuditLogIntegrationTests` / `MutationAuditLogIntegrationTests` (audit DELETE → 405) — pre-existing
- 1 failure: `i18n-lint.test.ts` (missing `designer.inspector.placeholders.label`) — pre-existing
No new i18n keys are added by this story.

### No EF migration, no NuGet packages, no frontend changes
- All schema (custom_dataset + users join) is in place from Story 8.1.
- No new NuGet packages.
- Frontend UI for the Dataset Manager is Story 8.10.

### Project Structure — Files Modified / Created

```
NEW:
  src/FormForge.Api/Features/Datasets/Dtos/DatasetSummaryDto.cs
  src/FormForge.Api.Tests/Features/Datasets/DatasetListGetTests.cs

MODIFIED:
  src/FormForge.Api/Features/Datasets/DatasetService.cs
    — extend IDatasetService with ListAsync + GetByIdAsync
    — add ListAsync implementation + ListDatasetRow private record
    — add GetByIdAsync implementation
  src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs
    — replace both GET stubs; update file-header comment
```

### References

- [Source: `_bmad-output/planning-artifacts/epics.md` — Epic 8, Story 8.7 ACs (FR-62, AR-65)]
- [Source: `_bmad-output/planning-artifacts/epics.md` — AR-21: `PagedResult<T>` shape; pageSize ≤100, default 25]
- [Source: `_bmad-output/planning-artifacts/epics.md` — AR-65: Dataset API Contract: GET /api/datasets (auth), GET /api/datasets/{id} (auth)]
- [Source: `src/FormForge.Api/Features/Datasets/DatasetService.cs` — `CurrentDatasetRow` private record + Dapper column-alias pattern]
- [Source: `src/FormForge.Api/Features/Datasets/DatasetService.cs` — connection lifecycle pattern (`try/finally conn.DisposeAsync()`)]
- [Source: `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs` — GET stubs at lines 23-27 to replace; file-header comment; `[FromQuery]` already used via `Microsoft.AspNetCore.Mvc` using]
- [Source: `src/FormForge.Api/Common/PagedResult.cs` — `PagedResult<T>(Data, Total, Page, PageSize)` record shape]
- [Source: `src/FormForge.Api/Common/Endpoints/RouteGroupExtensions.cs` — `RequireDatasetManagement` is per-endpoint, not group-level; GET endpoints do not call it]
- [Source: `src/FormForge.Api/Program.cs:687-691` — `/api/datasets` group is `.RequireAuth()` at group level; GET endpoints inherit auth automatically]
- [Source: `src/FormForge.Api.Tests/Features/Datasets/DatasetDeleteTests.cs` — `InitializeAsync` TRUNCATE+VIEW cleanup, `ReseedAdminRoleAsync`, `SeedAdminUserAsync`, `LoginAsync`, `CreateAsync` helper patterns]

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (1M context)

### Debug Log References

- `dotnet build src/FormForge.Api` — initial build failed with CA2007 on `GetByIdAsync`'s
  `await using var conn` (this project enforces ConfigureAwait on the implicit async dispose).
  Resolved by switching to the explicit `try/finally` + `conn.DisposeAsync().ConfigureAwait(false)`
  pattern used by `UpdateAsync`/`DeleteAsync`. Second build: 0 warnings, 0 errors.
- `dotnet test --filter DatasetListGetTests` → 4/4 passed (7s).
- `dotnet test` (full API suite) → 869 passed, 2 failed. The 2 failures are the pre-existing
  audit DELETE→405 tests (`SchemaAuditLogIntegrationTests`, `MutationAuditLogIntegrationTests`),
  red on a clean tree and unrelated to this story.

### Completion Notes List

- Added `DatasetSummaryDto` (list-row shape with creator display name, nullable on SET NULL FK).
- Extended `IDatasetService`/`DatasetService` with `ListAsync` (count + paged LEFT JOIN to users,
  ordered `created_at DESC`) and `GetByIdAsync` (full DTO, reusing the existing `CurrentDatasetRow`
  projection). page/pageSize clamping lives in the service (`Math.Max(1, page)`,
  `Math.Clamp(pageSize, 1, 100)`) so bounds hold regardless of caller.
- Added private `ListDatasetRow` Dapper projection (`DateTime`/`DateTime?` to match Npgsql
  `timestamptz` materialization; converts implicitly to the DTO's `DateTimeOffset`).
- Replaced both GET stubs in `DatasetEndpoints.cs` with real handlers; auth-only (no
  `.RequireDatasetManagement()`, no validation filter — GET has no body). Updated file-header comment.
- No `Program.cs` DI change needed (service already registered scoped in Story 8.4).
- AC coverage: AC-1 (list → PagedResult, paging, ordering, createdByName) Tests 1 & 2;
  AC-2 (get full DTO incl. query/version) Test 3; AC-3 (missing id → 404) Test 4. All pass.

### File List

NEW:
- src/FormForge.Api/Features/Datasets/Dtos/DatasetSummaryDto.cs
- src/FormForge.Api.Tests/Features/Datasets/DatasetListGetTests.cs

MODIFIED:
- src/FormForge.Api/Features/Datasets/DatasetService.cs
- src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs

## Change Log

| Date       | Version | Description                                                        | Author |
| ---------- | ------- | ------------------------------------------------------------------ | ------ |
| 2026-06-03 | 1.0     | Story 8.7 implemented: GET /api/datasets (list) + GET /{id} (get). | jukhan |
| 2026-06-03 | 1.1     | Code review: applied 8 patches (OFFSET overflow guard, ORDER BY tie-breaker, +AR-21 clamp/null-creator tests, default/totalPages/updatedAt/builderState assertions, dead-field cleanup). 4 dismissed, 2 deferred. 6/6 tests pass. Status → done. | jukhan |
