# Story 8.8: Custom Query Authoring Mode — SQL Textarea & SELECT Enforcement

Status: done

## Story

As a user with `dataset-management`,
I want to write a raw SQL SELECT query in a textarea when in Custom Query Mode,
So that I can define a Dataset using hand-authored SQL, with the server rejecting any non-SELECT statements.

## Acceptance Criteria

**AC-1 — SQL textarea shown in Custom Query mode**
**Given** `is_custom_query = true` in the Dataset create/edit modal (Story 8.10)
**When** the form renders
**Then** a SQL textarea for `query` is shown; the textarea uses a monospace font (per FR-60 AC-1 / FR-62 H-8 AC-3)

**AC-2 — Server-side SELECT enforcement via `SqlSelectEnforcer`**
**Given** a non-empty `query` is submitted to POST /api/datasets or PUT /api/datasets/{id}
**When** `is_custom_query = true` and server-side `SqlSelectEnforcer` runs (per AR-61)
**Then** `PgQuery.Parse(sql)` is called — a parse failure returns HTTP 422 `{ code: "INVALID_QUERY", message: "SQL could not be parsed." }`
**And** if the root statement is not a `SelectStmt`, HTTP 422 `{ code: "INVALID_QUERY", message: "Only SELECT statements are permitted." }` is returned
**And** DML (INSERT, UPDATE, DELETE) and DDL (CREATE, DROP, ALTER, TRUNCATE) statements are rejected
**And** CTEs (`WITH … AS (… SELECT …) SELECT …`) are permitted (they parse as `SelectStmt` at root)

**AC-3 — Empty query disables submit button (UI)**
**Given** the submit button in the Dataset modal (Story 8.10)
**When** `is_custom_query = true` and `query` is empty or whitespace
**Then** the submit button is disabled with a visible empty-state hint text

**AC-4 — Server enforces independently of client**
**Given** a client that bypasses AC-3 (e.g. DevTools)
**When** an empty or non-SELECT query is submitted
**Then** the server's `SqlSelectEnforcer` runs independently and returns HTTP 422 INVALID_QUERY

**AC-5 — Enforcement is checkpoint (a) only; no audit log on rejection**
**Given** the enforcer rejects a query
**When** the rejection fires before the NpgsqlTransaction is opened
**Then** no DDL is attempted and no `dataset_audit_log` entry is written

---

## Tasks / Subtasks

- [x] **Task 1 — Add `PgQuery.NET` NuGet package** (AC-2)
  - [x] In `Directory.Packages.props`, add inside the `<!-- API -->` ItemGroup:
    ```xml
    <PackageVersion Include="PgQuery.NET" Version="2.1.2" />
    ```
    Verify the latest stable `PgQuery.NET` version on NuGet.org at implementation time (search "PgQuery.NET" — pick the build wrapping `libpg_query` with protobuf AST output). The package ships native binaries for linux-x64, linux-arm64, win-x64, and osx — no special RuntimeIdentifier flags needed.
  - [x] In `src/FormForge.Api/FormForge.Api.csproj`, add to the existing PackageReference ItemGroup (alphabetical order):
    ```xml
    <PackageReference Include="PgQuery.NET" />
    ```
  - [x] Run `dotnet restore` and confirm the package resolves without errors.
  - [x] **The tests project does NOT need `PgQuery.NET`** — it references `FormForge.Api.csproj` via `<ProjectReference>`, which transitively includes the runtime. `InternalsVisibleTo` already allows the tests to call `SqlSelectEnforcer.Validate` directly.

- [x] **Task 2 — Create `SqlSelectEnforcer.cs`** (AC-2 / AC-4 / AC-5)
  - [x] Create `src/FormForge.Api/Features/Datasets/SqlSelectEnforcer.cs`:
    ```csharp
    using PgQuery;

    namespace FormForge.Api.Features.Datasets;

    // Story 8.8 (FR-60 / AR-61) — checkpoint (a): SELECT-only enforcement for
    // Custom Query create/update, before any VIEW DDL executes. Checkpoints (b)
    // and (c) — builder_state SQL and preview — are wired in Stories 11.x.
    internal static class SqlSelectEnforcer
    {
        internal static SqlValidationResult Validate(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return SqlValidationResult.Empty;

            ParseResult parseResult;
            try
            {
                parseResult = PgQuery.Parse(sql);
            }
            catch (Exception)
            {
                return SqlValidationResult.InvalidQuery("SQL could not be parsed.");
            }

            if (parseResult.Stmts.Count == 0)
                return SqlValidationResult.InvalidQuery("SQL could not be parsed.");

            if (parseResult.Stmts.Count > 1)
                return SqlValidationResult.InvalidQuery("Only a single SELECT statement is permitted.");

            var stmt = parseResult.Stmts[0].Stmt;

            // SelectStmt covers plain SELECT and CTEs (WITH … SELECT …).
            // In libpg_query's AST a CTE parses as SelectStmt with the WithClause
            // field populated — the root node is still SelectStmt.
            if (stmt.SelectStmt is not null)
                return SqlValidationResult.Valid;

            return SqlValidationResult.InvalidQuery("Only SELECT statements are permitted.");
        }
    }

    internal sealed record SqlValidationResult(bool IsValid, string? ErrorMessage)
    {
        internal static readonly SqlValidationResult Valid = new(true, null);
        internal static readonly SqlValidationResult Empty = new(false, "SQL query cannot be empty.");

        internal static SqlValidationResult InvalidQuery(string message) => new(false, message);
    }
    ```
  - [x] **API compatibility note — verify after `dotnet restore`:**
    The PgQuery.NET protobuf model exposes statement roots as nullable properties on the `Node` type.
    The expected check is `stmt.SelectStmt is not null`. If the installed version uses a `NodeCase`
    enum instead, use `stmt.NodeCase == Node.NodeOneofCase.SelectStmt`. Confirm against the package's
    README or IntelliSense after restore. The `catch (Exception)` is intentional — the exception type
    for parse failure varies by version (`ParseException` or `PgQueryException`); catching `Exception`
    is safe because this is a known boundary with no side effects.
  - [x] Run `dotnet build src/FormForge.Api` — 0 errors, 0 warnings.

- [x] **Task 3 — Wire enforcer into `DatasetService.CreateAsync`** (AC-2 / AC-4 / AC-5)
  - [x] Open `src/FormForge.Api/Features/Datasets/DatasetService.cs`.
  - [x] In `CreateAsync`, add the enforcer call **after** `effectiveQuery` and `viewDdl` are computed,
        **before** `var conn = await connectionFactory.CreateOpenConnectionAsync(ct)`.
        This ensures no DB resources are consumed on invalid input (AC-5):
    ```csharp
    // Story 8.8 (AR-61) — checkpoint (a): enforce SELECT-only before any DDL.
    // Only runs for Custom Query mode with a user-provided query; builder-mode and
    // placeholder-only creates bypass enforcement (Stories 11.x cover checkpoint b).
    if (request.IsCustomQuery && !string.IsNullOrWhiteSpace(request.Query))
    {
        var enforcement = SqlSelectEnforcer.Validate(request.Query);
        if (!enforcement.IsValid)
            return new CreateDatasetResult(CreateDatasetOutcome.InvalidQuery,
                ErrorDetail: enforcement.ErrorMessage);
    }
    ```
  - [x] The early return here means **no** `db.DatasetAuditLog.Add` is reached — correct per AC-5.
  - [x] `DatasetEndpoints.cs` needs **no changes** — the existing `CreateDatasetOutcome.InvalidQuery`
        mapping already surfaces `result.ErrorDetail` as the HTTP 422 `detail` field:
        `detail: result.ErrorDetail` → enforcer's `ErrorMessage` flows through untouched.

- [x] **Task 4 — Wire enforcer into `DatasetService.UpdateAsync`** (AC-2 / AC-5)
  - [x] In `UpdateAsync`, add the enforcer call inside the outer `try { conn ... }` block,
        **after** Step D (effective values computed, including `effectiveIsCustomQuery`, `queryChanged`,
        `effectiveNewQuery`) and the `resolvedNewName` TryCreate guard (§3 comment), **before**
        Step E (the `primaryDdl` pre-build). This is inside the outer `try` block, so the outer
        `finally { conn.DisposeAsync() }` disposes the connection correctly on early return — same
        pattern as the existing `NotFound` and `ConcurrencyConflict` early returns:
    ```csharp
    // Story 8.8 (AR-61) — checkpoint (a): enforce SELECT-only before UPDATE transaction.
    // Only runs when the query was explicitly changed in Custom Query mode.
    if (effectiveIsCustomQuery && queryChanged && !string.IsNullOrWhiteSpace(effectiveNewQuery))
    {
        var enforcement = SqlSelectEnforcer.Validate(effectiveNewQuery);
        if (!enforcement.IsValid)
            return new UpdateDatasetResult(UpdateDatasetOutcome.InvalidQuery,
                ErrorDetail: enforcement.ErrorMessage);
    }
    ```
  - [x] Condition: `queryChanged` is already `true` only when `request.Query is not null && request.Query != current.Query`.
        The additional `!string.IsNullOrWhiteSpace(effectiveNewQuery)` guard ensures empty-string overwrites
        (which produce the placeholder VIEW) bypass enforcement — same as `CreateAsync`.
  - [x] The early return skips both the transaction and `db.DatasetAuditLog.Add` — correct per AC-5.
  - [x] `DatasetEndpoints.cs` PUT handler needs **no changes** — existing `UpdateDatasetOutcome.InvalidQuery`
        mapping already carries `result.ErrorDetail` through to HTTP 422.

- [x] **Task 5 — Unit tests: `SqlSelectEnforcerTests.cs`** (AC-2)
  - [x] Create `src/FormForge.Api.Tests/Features/Datasets/SqlSelectEnforcerTests.cs`.
  - [x] These are **pure unit tests** (no Testcontainers, no WebApplicationFactory, no `[Collection]`).
        They call `SqlSelectEnforcer.Validate(sql)` directly — fast and isolated.
  - [x] No `[Collection("DatasetIntegrationTests")]` attribute — this is NOT an integration test.
    ```csharp
    using FormForge.Api.Features.Datasets;

    namespace FormForge.Api.Tests.Features.Datasets;

    public sealed class SqlSelectEnforcerTests
    {
        // ── Valid: plain SELECT ────────────────────────────────────────────

        [Theory]
        [InlineData("SELECT 1")]
        [InlineData("SELECT id, name FROM users")]
        [InlineData("SELECT * FROM foo WHERE id = 1")]
        [InlineData("SELECT id FROM foo ORDER BY id DESC LIMIT 10")]
        [InlineData("SELECT count(*) FROM foo GROUP BY type")]
        [InlineData("SELECT a.id, b.name FROM a JOIN b ON a.id = b.a_id")]
        public void Validate_ValidSelectStatement_ReturnsIsValidTrue(string sql)
        {
            var result = SqlSelectEnforcer.Validate(sql);
            Assert.True(result.IsValid);
            Assert.Null(result.ErrorMessage);
        }

        // ── Valid: CTEs ────────────────────────────────────────────────────

        [Theory]
        [InlineData("WITH cte AS (SELECT id FROM foo) SELECT id FROM cte")]
        [InlineData("WITH a AS (SELECT 1 AS n), b AS (SELECT 2 AS n) SELECT a.n, b.n FROM a, b")]
        public void Validate_CteSelectStatement_ReturnsIsValidTrue(string sql)
        {
            var result = SqlSelectEnforcer.Validate(sql);
            Assert.True(result.IsValid);
        }

        // ── Invalid: DML rejected ──────────────────────────────────────────

        [Theory]
        [InlineData("INSERT INTO foo (id) VALUES (1)")]
        [InlineData("UPDATE foo SET name = 'x' WHERE id = 1")]
        [InlineData("DELETE FROM foo WHERE id = 1")]
        public void Validate_DmlStatement_ReturnsInvalid(string sql)
        {
            var result = SqlSelectEnforcer.Validate(sql);
            Assert.False(result.IsValid);
            Assert.NotNull(result.ErrorMessage);
        }

        // ── Invalid: DDL rejected ──────────────────────────────────────────

        [Theory]
        [InlineData("CREATE TABLE foo (id INT)")]
        [InlineData("DROP TABLE foo")]
        [InlineData("ALTER TABLE foo ADD COLUMN bar INT")]
        [InlineData("TRUNCATE TABLE foo")]
        public void Validate_DdlStatement_ReturnsInvalid(string sql)
        {
            var result = SqlSelectEnforcer.Validate(sql);
            Assert.False(result.IsValid);
            Assert.NotNull(result.ErrorMessage);
        }

        // ── Invalid: parse failure ─────────────────────────────────────────

        [Theory]
        [InlineData("not valid sql $$$$")]
        [InlineData("SELECT FROM WHERE")]
        [InlineData("SELECT (((")]
        [InlineData(";")]
        public void Validate_UnparsableSql_ReturnsInvalid(string sql)
        {
            var result = SqlSelectEnforcer.Validate(sql);
            Assert.False(result.IsValid);
            Assert.NotNull(result.ErrorMessage);
        }

        // ── Invalid: empty / whitespace ────────────────────────────────────

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t\n")]
        public void Validate_EmptyOrWhiteSpace_ReturnsInvalid(string sql)
        {
            var result = SqlSelectEnforcer.Validate(sql);
            Assert.False(result.IsValid);
        }

        // ── Error message content ──────────────────────────────────────────

        [Fact]
        public void Validate_InsertStatement_ErrorMessageMentionsSelect()
        {
            var result = SqlSelectEnforcer.Validate("INSERT INTO foo VALUES (1)");
            Assert.Contains("SELECT", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Validate_UnparsableSql_ErrorMessageMentionsParsed()
        {
            var result = SqlSelectEnforcer.Validate("$$$ garbage $$$");
            Assert.Contains("parsed", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
    }
    ```
  - [x] Run `dotnet test --filter SqlSelectEnforcerTests` → all pass.

- [x] **Task 6 — Frontend: `SqlQueryTextarea` component** (AC-1 / AC-3)
  - [x] Create `web/src/features/datasets/SqlQueryTextarea.tsx`:
    ```tsx
    import { useTranslation } from 'react-i18next'
    import { Textarea } from '@/components/ui/textarea'
    import { cn } from '@/lib/utils'

    interface SqlQueryTextareaProps {
      id?: string
      value: string
      onChange: (value: string) => void
      disabled?: boolean
      error?: string
    }

    // Story 8.8 (FR-60 / FR-62 H-8 AC-3) — monospace SQL textarea for Custom Query
    // mode. Consumed by the Dataset create/edit modal in Story 8.10. AC-3 submit-button
    // disabling is form-level state in the parent modal (check `!query.trim()` there).
    export function SqlQueryTextarea({ id = 'sql-query', value, onChange, disabled, error }: SqlQueryTextareaProps) {
      const { t } = useTranslation()
      const errorId = `${id}-error`

      return (
        <div className="flex flex-col gap-1.5">
          <label
            htmlFor={id}
            className="text-sm font-medium leading-none peer-disabled:cursor-not-allowed peer-disabled:opacity-70"
          >
            {t('datasets.sqlTextarea.label')}
          </label>
          <Textarea
            id={id}
            value={value}
            onChange={(e) => onChange(e.target.value)}
            disabled={disabled}
            placeholder={t('datasets.sqlTextarea.placeholder')}
            className={cn('font-mono text-sm min-h-[200px] resize-y', error && 'border-destructive')}
            aria-describedby={error ? errorId : undefined}
            aria-invalid={error ? true : undefined}
            spellCheck={false}
            autoComplete="off"
            autoCorrect="off"
          />
          {error && (
            <p id={errorId} className="text-sm text-destructive">
              {error}
            </p>
          )}
        </div>
      )
    }
    ```
  - [x] Add i18n keys to `web/src/lib/i18n/locales/en.json` inside the existing `"datasets": { … }`
        object (add `"sqlTextarea"` as a new sibling key alongside `"validation"`, `"invalidQuery"`, etc.):
    ```json
    "sqlTextarea": {
      "label": "SQL Query",
      "placeholder": "SELECT id, name\nFROM my_table\nWHERE …",
      "emptyHint": "A SELECT statement is required in Custom Query mode."
    }
    ```
    Note: `"emptyHint"` is used by Story 8.10's modal to show an inline hint below the disabled submit
    button (AC-3). Export the key here so the modal can import `t('datasets.sqlTextarea.emptyHint')`.
  - [x] Do NOT touch `web/src/features/datasets/validation.ts` — that file is `dataset_name`
        client-side validation from Story 8.3 and is unrelated to SQL content validation.

- [x] **Task 7 — Build and test verification**
  - [x] `dotnet build src/FormForge.Api` → 0 errors, 0 warnings
  - [x] `dotnet test --filter SqlSelectEnforcerTests` → all pass (fast unit tests, no containers)
  - [x] `dotnet test` (full suite) → 869+ passed, 2 pre-existing failures (audit DELETE→405 only)
  - [x] Web: `cd web && pnpm tsc --noEmit` → 0 errors (or equivalent type-check script in package.json)

### Review Findings

- [x] [Review][Defer] AC-3 deferred to Story 8.10 — submit-button disabling when query is empty is a modal-level concern; `emptyHint` i18n key pre-registered here for Story 8.10 to consume — deferred, by design
- [x] [Review][Patch] Writable CTEs bypass enforcer — `WITH x AS (INSERT/UPDATE/DELETE … RETURNING) SELECT …` parses as a root `SelectStmt`, so `stmt.SelectStmt is not null` is true and validation passes; mutating CTEs reach `CREATE VIEW` unchecked [SqlSelectEnforcer.cs]
- [x] [Review][Patch] `SELECT INTO` bypasses enforcer — `SELECT id INTO newtable FROM foo` parses as `SelectStmt` with `isSelectInto=true`; enforcer returns Valid, then `CREATE VIEW` fails with a raw Postgres error instead of a controlled `INVALID_QUERY` 422 [SqlSelectEnforcer.cs]
- [x] [Review][Patch] Mode-switch without new query skips enforcer on existing stored query — PUT with `{ isCustomQuery: true }` and no `query` field yields `queryChanged=false`; if the stored query is non-SELECT the enforcer is bypassed and the VIEW is rebuilt with it [DatasetService.cs:UpdateAsync]
- [x] [Review][Patch] Pre-existing non-SELECT query bypasses enforcer on unchanged PUT — if stored `query` equals `request.Query`, `queryChanged=false` and the enforcer is skipped; defense-in-depth gap for datasets created before Story 8.8 [DatasetService.cs:UpdateAsync]
- [x] [Review][Patch] Default `id='sql-query'` causes duplicate DOM IDs — if `SqlQueryTextarea` renders twice on one page without an explicit `id` prop, two elements share the same id, breaking the label/input association and violating HTML uniqueness [SqlQueryTextarea.tsx:16]
- [x] [Review][Patch] Trailing-semicolon case untested — `SELECT 1;` may produce `Stmts.Count==2` (stmt + empty RawStmt) in some pgsqlparser versions, incorrectly rejecting a valid single-SELECT query with "Only a single SELECT statement is permitted." [SqlSelectEnforcerTests.cs]
- [x] [Review][Patch] No test for `Stmts.Count > 1` path — the multi-statement guard (`"SELECT 1; SELECT 2"`) has no unit test; the branch is unverified and could silently pass multi-statement input if the parser reports only the first statement [SqlSelectEnforcerTests.cs]
- [x] [Review][Defer] `SELECT 1; DELETE FROM foo` handled by Stmts.Count guard IF parser counts both — `Stmts.Count > 1` correctly rejects multi-statement input assuming pgsqlparser reports all statements; covered by the missing test above [SqlSelectEnforcerTests.cs] — deferred, pre-existing
- [x] [Review][Defer] Multi-statement message ("Only a single SELECT statement is permitted.") not in AC-2 spec and lacks an integration test — unspecified third rejection message, low risk [SqlSelectEnforcer.cs:36] — deferred, pre-existing
- [x] [Review][Defer] `GetAuditEntriesAsync` filter may miss differently-typed audit entries — the UPDATE-enforcer test only asserts no "UPDATE" entry; a bug writing a differently-typed entry would not be caught [DatasetUpdateTests.cs] — deferred, pre-existing
- [x] [Review][Defer] POST test does not verify dataset row was never created — only the absence of audit rows is asserted; a partial-create bug would be missed [DatasetViewLifecycleTests.cs] — deferred, pre-existing
- [x] [Review][Defer] `PutDataset_DdlFailure` test assertion is ambiguous between enforcer rejection and DDL failure — both cases return 422 with INVALID_QUERY, making the test a weaker regression guard than before [DatasetUpdateTests.cs] — deferred, pre-existing

---

## Dev Notes

### Critical: PgQuery.NET API — check after `dotnet restore`

PgQuery.NET wraps `libpg_query` via protobuf. After adding the package, confirm the exact API:

```csharp
// Typical usage (verify against IntelliSense after dotnet restore):
using PgQuery;

var result = PgQuery.Parse(sql);          // static method on PgQuery class
var stmt = result.Stmts[0].Stmt;          // Node type (protobuf oneof)

// Root-type check — prefer nullable property form:
stmt.SelectStmt is not null               // ← true for SELECT and CTE-wrapped SELECT
stmt.InsertStmt is not null               // ← true for INSERT
// If NodeCase enum is used instead:
stmt.NodeCase == Node.NodeOneofCase.SelectStmt
```

The `ParseResult.Stmts` is `RepeatedField<RawStmt>` (Google Protobuf). Each `RawStmt.Stmt` is a `Node` protobuf oneof. Ships native binaries for all major platforms in the NuGet package — no extra runtime IDs needed.

### Critical: CTEs parse as SelectStmt at root

`WITH cte AS (SELECT ...) SELECT ...` produces a `SelectStmt` root node in libpg_query's AST — the `WithClause` is a field *inside* `SelectStmt`, not a root-level node. The `stmt.SelectStmt is not null` check permits CTEs automatically. No special CTE branch needed.

### Critical: Enforcer placement in `CreateAsync` — before connection open

The enforcer call in `CreateAsync` goes BEFORE `var conn = await connectionFactory.CreateOpenConnectionAsync(ct)`. Reject first, consume resources never. The return site is before `db.DatasetAuditLog.Add` — no audit log written (AC-5).

### Critical: Enforcer placement in `UpdateAsync` — inside outer `try`, before transaction

The enforcer call in `UpdateAsync` goes inside the outer `try { ... } finally { conn.DisposeAsync() }` block, after Step D (effective values computed) and before Step E (DDL pre-build). The early return from inside the outer `try` is the established pattern for `NotFound` and `ConcurrencyConflict`. The outer `finally` disposes `conn` correctly. No audit log is written (the `db.DatasetAuditLog.Add` block is later in the method, after Step G).

### Critical: `DatasetEndpoints.cs` — no changes needed

Both POST `/` and PUT `/{id}` already map `InvalidQuery` → HTTP 422 with `detail: result.ErrorDetail`. The enforcer's `ErrorMessage` flows through as-is. Do not touch endpoints.

### Critical: `ValidationFilter` — not applicable

GET handlers have no body. The enforcer is called from the service, not as a filter. Do not add `.AddValidationFilter<T>()` to any endpoint for this story.

### Builder mode (`is_custom_query = false`) — not enforced in Story 8.8

Builder-mode queries are server-generated (Stories 11.x). The enforcer's conditions guard with `request.IsCustomQuery` (CreateAsync) and `effectiveIsCustomQuery && queryChanged` (UpdateAsync). Builder-mode datasets with `is_custom_query = false` use the placeholder VIEW until the query builder generates SQL.

### Pre-existing test failures to ignore

- 2 failures: `SchemaAuditLogIntegrationTests` / `MutationAuditLogIntegrationTests` (audit DELETE → 405) — pre-existing, unrelated
- 1 failure: `i18n-lint.test.ts` (missing `designer.inspector.placeholders.label`) — pre-existing; this story does NOT add that key. Adding `datasets.sqlTextarea.*` keys will not fix the pre-existing failure.

### No EF migration, no schema changes

All required columns (`query`, `is_custom_query`) are in `custom_dataset` from Story 8.1. Story 8.8 is additive only.

### `SqlQueryTextarea` — leaf component only

This component renders the textarea and error message. It does NOT:
- Control the modal submit button (that is Story 8.10's form state)
- Perform client-side SQL validation (server is the authority per AC-4)
- Replace or extend `validation.ts` (that is dataset_name validation)

Story 8.10 will use `t('datasets.sqlTextarea.emptyHint')` for the disabled-submit hint text (AC-3).

### Central Package Management (`Directory.Packages.props`)

This project uses `ManagePackageVersionsCentrally = true`. All package versions are in `Directory.Packages.props` — the `.csproj` has NO `Version` attributes. Adding a new package requires two edits: version in `Directory.Packages.props` AND reference in `FormForge.Api.csproj`.

### Project Structure — Files Modified / Created

```
NEW:
  src/FormForge.Api/Features/Datasets/SqlSelectEnforcer.cs
  src/FormForge.Api.Tests/Features/Datasets/SqlSelectEnforcerTests.cs
  web/src/features/datasets/SqlQueryTextarea.tsx

MODIFIED:
  Directory.Packages.props
    — add <PackageVersion Include="PgQuery.NET" Version="x.y.z" />
  src/FormForge.Api/FormForge.Api.csproj
    — add <PackageReference Include="PgQuery.NET" />
  src/FormForge.Api/Features/Datasets/DatasetService.cs
    — CreateAsync: add SqlSelectEnforcer.Validate call before connection open
    — UpdateAsync: add SqlSelectEnforcer.Validate call inside outer try, before primaryDdl Step E
  web/src/lib/i18n/locales/en.json
    — add datasets.sqlTextarea.{label, placeholder, emptyHint}
```

### References

- [Source: `_bmad-output/planning-artifacts/epics.md` — Epic 8, Story 8.8 ACs (FR-60 AC-1/4, FR-62 H-8 AC-3, AR-61)]
- [Source: `_bmad-output/planning-artifacts/architecture.md` — Decision 6.5: SqlSelectEnforcer, PgQuery.NET algorithm, three checkpoints]
- [Source: `src/FormForge.Api/Features/Datasets/DatasetService.cs` — CreateAsync lines ~100-115 (enforcer insertion point), UpdateAsync Step D/E boundary (enforcer insertion point)]
- [Source: `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs` — InvalidQuery → HTTP 422 mapping (no changes needed)]
- [Source: `Directory.Packages.props` — CPM format (PackageVersion, ManagePackageVersionsCentrally = true)]
- [Source: `src/FormForge.Api/FormForge.Api.csproj` — PackageReference format (no Version attribute in CPM)]
- [Source: `src/FormForge.Api.Tests/FormForge.Api.Tests.csproj` — tests project structure; InternalsVisibleTo already configured in API project]
- [Source: `web/src/features/datasets/validation.ts` — existing datasets feature folder (dataset_name validation, unrelated to this story)]
- [Source: `web/src/components/ui/textarea.tsx` — existing shadcn/ui Textarea to wrap in SqlQueryTextarea]
- [Source: `web/src/lib/i18n/locales/en.json` — existing `"datasets"` section (~lines 490-502); add `"sqlTextarea"` as new sibling key]
- [Source: Story 8.7 Dev Notes — pre-existing test failures, CA2007 ConfigureAwait pattern, Dapper alias pattern]

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-8[1m] (Opus 4.8, 1M context) — BMad Dev Story workflow.

### Debug Log References

- `dotnet build src/FormForge.Api` → 0 errors, 0 warnings (after package add + enforcer + Create/Update wiring).
- `dotnet test --filter SqlSelectEnforcerTests` → 24 passed (pure unit, no containers).
- First full suite run surfaced 2 regressions (`PostDataset_DdlFailure_WritesFailedAuditRow`, `PutDataset_DdlFailure_WritesFailedAuditRow`) — see Completion Notes #3.
- After regression fixes: affected classes (`DatasetViewLifecycleTests` + `DatasetUpdateTests`) → 20 passed.
- Web: `npx tsc -b --noEmit` → clean. `vitest run` → 280 passed, 1 failed (pre-existing `i18n-lint.test.ts`, missing `designer.inspector.placeholders.label` — unrelated, confirmed via `process.exit(missing.length ? 1 : 0)`; my orphaned `emptyHint` is a warning, not a failure).

### Completion Notes List

1. **Critical dependency substitution (user-approved).** The story/epic named `PgQuery.NET` 2.1.2, which **does not exist on NuGet** (verified: `pgquery.net` → 404 on the flat-container API). Per user decision, substituted **`pgsqlparser` 1.0.0** (mysticmind/pgsqlparser-dotnet) — the real libpg_query protobuf-AST binding. Namespace is **`PgSqlParser`** (not `PgQuery`).
2. **API adaptation.** pgsqlparser's `Parser.Parse(sql)` returns `Result<ParseResult?>` (`.IsSuccess` / `.Value` / `.Error`) and **does not throw** on a parse failure, so `SqlSelectEnforcer` branches on `!result.IsSuccess || result.Value is null` instead of the story's `try/catch`. The enforcement logic is unchanged: zero stmts / >1 stmt / non-`SelectStmt` root all rejected; single `SelectStmt` (incl. CTEs) accepted. All 24 unit tests in Task 5 pass against the real API, confirming CTEs parse as `SelectStmt`, `;`→0 stmts is rejected, and DML/DDL/garbage are rejected.
3. **Regression fix (not in story spec).** Pre-existing tests `PostDataset_DdlFailure_WritesFailedAuditRow` (Story 8.4) and `PutDataset_DdlFailure_WritesFailedAuditRow` (Story 8.5) submitted syntactic garbage (`"NOT VALID SQL XYZ"`) expecting it to reach the VIEW DDL and write a *failed* audit row. AC-5 now (correctly) rejects garbage at checkpoint (a) **before** any audit row. Fixed both tests to use a syntactically-valid SELECT that fails at `CREATE VIEW` (`SELECT * FROM nonexistent_table_xyz` → Postgres 42P01), preserving their DDL-failure→failed-audit-row coverage. The sibling `*_InvalidSql_Returns422AndRollsBack` tests were unaffected (they assert only 422 + `INVALID_QUERY` + rollback, all of which the enforcer path still satisfies).
4. **Added AC-4/AC-5 integration coverage.** New tests `PostDataset_CustomQueryNonSelect_RejectedByEnforcer_WritesNoAuditRow` and `PutDataset_CustomQueryNonSelect_RejectedByEnforcer_WritesNoAuditRow` submit a parseable non-SELECT (INSERT/DELETE) in Custom Query mode and assert 422 `INVALID_QUERY` **and zero `dataset_audit_log` rows** (PUT also asserts the row is untouched at version 1).
5. **DatasetEndpoints.cs unchanged** — the existing `InvalidQuery → 422` mapping carries the enforcer's `ErrorMessage` through as `detail`, as the story predicted. No EF migration, no schema change.

### File List

NEW:
- `src/FormForge.Api/Features/Datasets/SqlSelectEnforcer.cs`
- `src/FormForge.Api.Tests/Features/Datasets/SqlSelectEnforcerTests.cs`
- `web/src/features/datasets/SqlQueryTextarea.tsx`

MODIFIED:
- `Directory.Packages.props` — add `<PackageVersion Include="pgsqlparser" Version="1.0.0" />` (substitute for the non-existent PgQuery.NET)
- `src/FormForge.Api/FormForge.Api.csproj` — add `<PackageReference Include="pgsqlparser" />`
- `src/FormForge.Api/Features/Datasets/DatasetService.cs` — `CreateAsync` + `UpdateAsync` enforcer calls (checkpoint a)
- `web/src/lib/i18n/locales/en.json` — add `datasets.sqlTextarea.{label, placeholder, emptyHint}`
- `src/FormForge.Api.Tests/Features/Datasets/DatasetViewLifecycleTests.cs` — fix `PostDataset_DdlFailure...` query; add `PostDataset_CustomQueryNonSelect_RejectedByEnforcer...`
- `src/FormForge.Api.Tests/Features/Datasets/DatasetUpdateTests.cs` — fix `PutDataset_DdlFailure...` query; add `PutDataset_CustomQueryNonSelect_RejectedByEnforcer...`

## Change Log

| Date       | Version | Description | Author |
| ---------- | ------- | ----------- | ------ |
| 2026-06-03 | 1.0     | Story created — ready for dev. | jukhan |
| 2026-06-03 | 1.1     | Implemented SqlSelectEnforcer (checkpoint a) + Create/Update wiring + SqlQueryTextarea + i18n keys. Substituted non-existent PgQuery.NET with pgsqlparser 1.0.0 (user-approved). Fixed 2 pre-existing DdlFailure tests broken by earlier-rejection semantics; added 2 AC-4/AC-5 integration tests. Status → review. | Amelia (dev agent) |
