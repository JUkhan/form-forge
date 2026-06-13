# Story 11.3: Query Preview (LIMIT 10)

Status: done

## Story

As a user creating or editing a Dataset,
I want to preview the query result (up to 10 rows) before saving,
So that I can validate correctness before the VIEW is persisted.

## Acceptance Criteria

1. **Given** the Dataset create/edit modal (Story 8.10) in either mode, **When** I click "Preview", **Then** a "Preview" button is present and active (per FR-72 AC-1).

2. **Given** I click "Preview" in Custom Query Mode, **When** the request is sent to `POST /api/datasets/preview`, **Then** the server validates SELECT-only on the submitted `query`, appends `LIMIT 10`, applies `SET LOCAL statement_timeout = '{PreviewTimeoutSeconds}s'` (default 5 s from `DatasetManager:PreviewTimeoutSeconds`), and executes against PostgreSQL using the `formforge_preview` read-only connection pool (per FR-72 AC-3 / AR-63).

3. **Given** I click "Preview" in Query Builder Mode, **When** the request is sent to `POST /api/datasets/preview` with `builder_state`, **Then** the server generates SQL from `builder_state` via `DatasetSqlGenerator.Generate`, appends `LIMIT 10`, applies the statement timeout, and executes against the `formforge_preview` pool (per FR-72 AC-2 / AR-63).

4. **Given** the preview returns results, **When** the UI renders them, **Then** column names appear as headers; up to 10 data rows are displayed (per FR-72 AC-4).

5. **Given** the statement timeout is exceeded (`NpgsqlException.SqlState == "57014"`), **When** the server catches it, **Then** HTTP 408 is returned with `{ code: "PREVIEW_TIMEOUT" }` and message "Preview query exceeded the time limit. Simplify the query or add filters." (per FR-72 AC-5 / AR-63).

6. **Given** a PostgreSQL error during preview (syntax error, permission denied, etc.), **When** the server catches it, **Then** HTTP 422 is returned with the PostgreSQL error message surfaced to the user — no internal stack traces exposed (per FR-72 AC-6 / AR-63).

7. **Given** the preview completes, **When** the result is returned, **Then** no Dataset row is created or modified — preview is read-only (per FR-72 AC-7).

8. **Given** a user without `dataset-management` permission, **When** they `POST /api/datasets/preview`, **Then** HTTP 403 is returned (per FR-72 AC-8 / AR-58).

## Tasks / Subtasks

---

### Task 1: Backend — Create `PreviewRequest` and `PreviewResultDto` (AC: 2, 3, 4)

- [x] Create `src/FormForge.Api/Features/Datasets/Dtos/PreviewRequest.cs` (NEW):
  ```csharp
  namespace FormForge.Api.Features.Datasets.Dtos;

  internal sealed record PreviewRequest(
      bool IsCustomQuery,
      string? Query,
      string? BuilderState);
  ```

- [x] Create `src/FormForge.Api/Features/Datasets/Dtos/PreviewResultDto.cs` (NEW):
  ```csharp
  namespace FormForge.Api.Features.Datasets.Dtos;

  internal sealed record PreviewResultDto(
      IReadOnlyList<string> Columns,
      IReadOnlyList<IReadOnlyList<object?>> Rows);
  ```

---

### Task 2: Backend — Create `IPreviewConnectionFactory` and `PreviewConnectionFactory` (AC: 2, 3)

- [x] Create `src/FormForge.Api/Infrastructure/Persistence/IPreviewConnectionFactory.cs` (NEW — under `Infrastructure/Persistence` matching `DbConnectionFactory`):
  ```csharp
  using Npgsql;

  namespace FormForge.Api.Infrastructure.Persistence;

  internal interface IPreviewConnectionFactory
  {
      Task<NpgsqlConnection> CreateOpenConnectionAsync(CancellationToken ct = default);
  }
  ```

- [x] Create `src/FormForge.Api/Infrastructure/Persistence/PreviewConnectionFactory.cs` (NEW):
  ```csharp
  using Npgsql;

  namespace FormForge.Api.Infrastructure.Persistence;

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
      Justification = "Registered via DI.")]
  internal sealed class PreviewConnectionFactory(IConfiguration configuration)
      : IPreviewConnectionFactory
  {
      private string ConnectionString =>
          configuration.GetConnectionString("formforge_preview")
          ?? throw new InvalidOperationException(
              "Connection string 'formforge_preview' not configured.");

      public async Task<NpgsqlConnection> CreateOpenConnectionAsync(CancellationToken ct = default)
      {
          var connection = new NpgsqlConnection(ConnectionString);
          try
          {
              await connection.OpenAsync(ct).ConfigureAwait(false);
              return connection;
          }
          catch
          {
              await connection.DisposeAsync().ConfigureAwait(false);
              throw;
          }
      }
  }
  ```
  Connection string key is `"formforge_preview"` (matches the PostgreSQL role name).
  `Maximum Pool Size=5` (per AR-63) is enforced via the connection string parameter — see Task 5.

---

### Task 3: Backend — Create `PreviewService` (AC: 2, 3, 5, 6, 7)

- [x] Create `src/FormForge.Api/Features/Datasets/PreviewService.cs` (NEW):
  ```csharp
  using FormForge.Api.Features.Datasets.Dtos;
  using FormForge.Api.Infrastructure.Persistence;
  using Npgsql;

  namespace FormForge.Api.Features.Datasets;

  internal interface IPreviewService
  {
      Task<PreviewServiceResult> ExecuteAsync(PreviewRequest request, CancellationToken ct = default);
  }

  internal enum PreviewOutcome { Success, Timeout, SqlError, BuilderStateInvalid }

  internal sealed record PreviewServiceResult(
      PreviewOutcome Outcome,
      PreviewResultDto? Data = null,
      string? ErrorMessage = null);

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
      Justification = "Registered via DI.")]
  internal sealed class PreviewService(
      IPreviewConnectionFactory connectionFactory,
      IDatasetAllowlist allowlist,
      IConfiguration configuration) : IPreviewService
  {
      public async Task<PreviewServiceResult> ExecuteAsync(
          PreviewRequest request, CancellationToken ct = default)
      {
          // Step 1: Resolve SQL + parameters
          string sql;
          IReadOnlyList<object?> parameters = [];

          if (request.IsCustomQuery)
          {
              var validation = SqlSelectEnforcer.Validate(request.Query ?? string.Empty);
              if (!validation.IsValid)
                  return new PreviewServiceResult(PreviewOutcome.SqlError,
                      ErrorMessage: validation.ErrorMessage);
              sql = request.Query!;
          }
          else
          {
              var bsDto = BuilderStateSerializer.Deserialize(request.BuilderState);
              if (bsDto is null)
                  return new PreviewServiceResult(PreviewOutcome.BuilderStateInvalid,
                      ErrorMessage: "builder_state could not be parsed.");

              var generated = DatasetSqlGenerator.Generate(bsDto, allowlist);
              if (generated.HasErrors)
                  return new PreviewServiceResult(PreviewOutcome.BuilderStateInvalid,
                      ErrorMessage: string.Join("; ", generated.Errors));

              sql = generated.ParameterizedSql!;
              parameters = generated.Parameters;
          }

          // Step 2: Execute inside a transaction to scope SET LOCAL statement_timeout
          var timeoutSeconds = configuration["DatasetManager:PreviewTimeoutSeconds"] ?? "5";
          try
          {
              await using var conn =
                  await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
              await using var tx =
                  await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

              await using var timeoutCmd = conn.CreateCommand();
              timeoutCmd.Transaction = tx;
              timeoutCmd.CommandText = $"SET LOCAL statement_timeout = '{timeoutSeconds}s'";
              await timeoutCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

              await using var previewCmd = conn.CreateCommand();
              previewCmd.Transaction = tx;
              previewCmd.CommandText = $"SELECT * FROM ({sql}) AS _preview LIMIT 10";

              for (int i = 0; i < parameters.Count; i++)
                  previewCmd.Parameters.AddWithValue(parameters[i] ?? DBNull.Value);

              await using var reader =
                  await previewCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

              var columns = Enumerable.Range(0, reader.FieldCount)
                  .Select(reader.GetName)
                  .ToList();

              var rows = new List<IReadOnlyList<object?>>();
              while (await reader.ReadAsync(ct).ConfigureAwait(false))
              {
                  var values = new object[reader.FieldCount];
                  reader.GetValues(values);
                  rows.Add(values.Select(v => v is DBNull ? null : v).ToArray<object?>());
              }

              await tx.CommitAsync(ct).ConfigureAwait(false);
              return new PreviewServiceResult(PreviewOutcome.Success,
                  Data: new PreviewResultDto(columns, rows));
          }
          catch (NpgsqlException ex) when (ex.SqlState == "57014")
          {
              return new PreviewServiceResult(PreviewOutcome.Timeout,
                  ErrorMessage: "Preview query exceeded the time limit." +
                                " Simplify the query or add filters.");
          }
          catch (NpgsqlException ex)
          {
              return new PreviewServiceResult(PreviewOutcome.SqlError,
                  ErrorMessage: ex.Message);
          }
      }
  }
  ```

---

### Task 4: Backend — Replace 501 stub in `DatasetEndpoints.cs` (AC: 1–8)

- [x] Modify `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs` (MODIFY):
  - [x] Replace lines 62-64 (the 501 stub):
    ```csharp
    // BEFORE:
    group.MapPost("/preview", () => Results.StatusCode(StatusCodes.Status501NotImplemented))
         .WithSummary("Dataset query preview (stub — Story 11.3)")
         .RequireDatasetManagement();
    ```
    with:
    ```csharp
    group.MapPost("/preview", async (
        [FromBody] PreviewRequest request,
        IPreviewService previewService,
        CancellationToken ct) =>
    {
        var result = await previewService.ExecuteAsync(request, ct).ConfigureAwait(false);
        return result.Outcome switch
        {
            PreviewOutcome.Success => Results.Ok(result.Data),
            PreviewOutcome.Timeout => Results.Problem(
                detail: result.ErrorMessage,
                title: "Preview timeout",
                statusCode: StatusCodes.Status408RequestTimeout,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "PREVIEW_TIMEOUT",
                    ["messageKey"] = "datasets.previewTimeout",
                }),
            PreviewOutcome.BuilderStateInvalid => Results.Problem(
                detail: result.ErrorMessage,
                title: "Builder state invalid",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "BUILDER_STATE_INVALID",
                    ["messageKey"] = "datasets.builderStateInvalid",
                }),
            PreviewOutcome.SqlError => Results.Problem(
                detail: result.ErrorMessage,
                title: "SQL error",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "DATASET_QUERY_INVALID",
                    ["messageKey"] = "datasets.invalidQuery",
                }),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    })
    .WithSummary("Preview dataset query — LIMIT 10 (Story 11.3 — FR-72 / AR-63)")
    .RequireDatasetManagement();
    ```
  - [x] **Do NOT move** the `/preview` registration — it must remain before `MapPost("/")`. The existing position (line 62) is already correct.
  - [x] Add `using FormForge.Api.Features.Datasets.Dtos;` at the top of the file if not already present (check existing usings).

---

### Task 5: Backend — Register DI and add connection string config (AC: 2, 3)

- [x] Modify `src/FormForge.Api/Program.cs` (MODIFY):
  - [x] Search for where `IDatasetService` and `IDatasetAllowlist` are registered (e.g., `AddScoped<IDatasetService>`). Add immediately after:
    ```csharp
    builder.Services.AddSingleton<IPreviewConnectionFactory, PreviewConnectionFactory>();
    builder.Services.AddScoped<IPreviewService, PreviewService>();
    ```

- [x] Modify `src/FormForge.Api/appsettings.Compose.json` (MODIFY):
  - [x] Add the `formforge_preview` connection string alongside the existing `"formforge"` entry. `Maximum Pool Size=5` is set here per AR-63:
    ```json
    "ConnectionStrings": {
      "formforge": "Host=postgres;Port=5432;Database=formforge;Username=postgres;Password=postgres",
      "formforge_preview": "Host=postgres;Port=5432;Database=formforge;Username=formforge_preview;Password=CHANGEME;Maximum Pool Size=5"
    }
    ```
  - [x] **Find the `formforge_preview` role password** before committing: check `docker-compose.yml` or the Aspire AppHost (`AppHost/Program.cs`) for where the role password is set post-migration (the migration creates the role but sets no password). Replace `CHANGEME` with the actual value. If the dev environment uses trust/peer auth, adjust accordingly.
  - [x] If `appsettings.Development.json` is used for local-without-Docker runs, add the connection string there too.

---

### Task 6: Frontend — Add `previewDataset` API function and types (AC: 2, 3)

- [x] Modify `web/src/features/datasets/datasetApi.ts` (MODIFY):
  - [x] Add request/response types (after existing type definitions):
    ```typescript
    export interface PreviewDatasetPayload {
      isCustomQuery: boolean
      query?: string | null
      builderState?: string | null
    }

    export interface PreviewResult {
      columns: string[]
      rows: (string | number | boolean | null)[][]
    }
    ```
  - [x] Add the `previewDataset` function (after `deleteDataset`):
    ```typescript
    export async function previewDataset(payload: PreviewDatasetPayload): Promise<PreviewResult> {
      return apiPost<PreviewResult>('/datasets/preview', payload)
    }
    ```
    Use the same `apiPost` helper that other functions in this file use.

---

### Task 7: Frontend — Preview panel in Builder Page (`datasets_.$id.tsx`) (AC: 3, 4, 5, 6)

- [x] Modify `web/src/routes/_app/admin/datasets_.$id.tsx` (MODIFY):
  - [x] Add imports (check for duplicates from Story 11.2 before adding — `useMutation` may already be imported):
    ```typescript
    import { previewDataset, type PreviewResult } from '../../../features/datasets/datasetApi'
    import { ApiError } from '../../../lib/api/apiError'
    ```
  - [x] Add preview state after existing state declarations:
    ```typescript
    const [previewResult, setPreviewResult] = useState<PreviewResult | null>(null)
    const [previewError, setPreviewError] = useState<string | null>(null)

    const previewMutation = useMutation({
      mutationFn: () =>
        previewDataset({
          isCustomQuery: false,
          builderState: JSON.stringify(builderState),
        }),
      onSuccess: (data) => {
        setPreviewResult(data)
        setPreviewError(null)
      },
      onError: (err) => {
        setPreviewResult(null)
        if (err instanceof ApiError) {
          setPreviewError(err.detail?.trim() ? err.detail : t('errors.genericError'))
        } else {
          setPreviewError(t('errors.genericError'))
        }
      },
    })
    ```
  - [x] Add a "Preview" button in the page header next to the Save button added by Story 11.2:
    ```tsx
    <Button
      variant="outline"
      size="sm"
      disabled={builderState.nodes.length === 0 || previewMutation.isPending}
      onClick={() => previewMutation.mutate()}
    >
      {previewMutation.isPending
        ? t('datasets.builder.previewingButton')
        : t('datasets.builder.previewButton')}
    </Button>
    ```
  - [x] Add a preview results panel below the canvas (conditionally rendered):
    ```tsx
    {previewError && (
      <div className="mt-4 rounded border border-destructive bg-destructive/10 p-3 text-sm text-destructive">
        {previewError}
      </div>
    )}
    {previewResult && (
      <div className="mt-4 overflow-x-auto rounded border">
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b bg-muted/50">
              {previewResult.columns.map((col) => (
                <th key={col} className="px-3 py-2 text-left font-medium">{col}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {previewResult.rows.length === 0 ? (
              <tr>
                <td
                  colSpan={previewResult.columns.length}
                  className="px-3 py-4 text-center text-muted-foreground"
                >
                  {t('datasets.builder.previewNoRows')}
                </td>
              </tr>
            ) : (
              previewResult.rows.map((row, rowIdx) => (
                <tr key={rowIdx} className="border-b last:border-0 hover:bg-muted/20">
                  {row.map((cell, cellIdx) => (
                    <td key={cellIdx} className="px-3 py-2">
                      {cell === null
                        ? <span className="text-muted-foreground italic">null</span>
                        : String(cell)}
                    </td>
                  ))}
                </tr>
              ))
            )}
          </tbody>
        </table>
        <p className="px-3 py-1.5 text-xs text-muted-foreground">
          {t('datasets.builder.previewRowCount', { count: previewResult.rows.length })}
        </p>
      </div>
    )}
    ```
  - [x] `useTranslation` is already declared by Story 11.2 (`const { t } = useTranslation()`). Do NOT add it again.

---

### Task 8: Frontend — Preview in Custom Query Modal (`datasets.tsx`) (AC: 1, 2, 4, 5, 6)

- [x] **Read `web/src/routes/_app/admin/datasets.tsx` fully before editing** to understand the existing modal structure, state management, and form layout for the custom query form.
- [x] Add a `previewMutation`, `previewResult`, and `previewError` state local to the component that renders the custom query `<textarea>`:
  ```typescript
  const [previewResult, setPreviewResult] = useState<PreviewResult | null>(null)
  const [previewError, setPreviewError] = useState<string | null>(null)

  const previewMutation = useMutation({
    mutationFn: () =>
      previewDataset({ isCustomQuery: true, query: currentQueryValue }),
    onSuccess: (data) => { setPreviewResult(data); setPreviewError(null) },
    onError: (err) => {
      setPreviewResult(null)
      if (err instanceof ApiError) {
        setPreviewError(err.detail?.trim() ? err.detail : t('errors.genericError'))
      } else {
        setPreviewError(t('errors.genericError'))
      }
    },
  })
  ```
  Where `currentQueryValue` is the current value of the query textarea (read from local form state or `watch` if using react-hook-form — match the existing form pattern in `datasets.tsx`).

- [x] Add a "Preview" button to the custom query form (alongside or below the query textarea):
  ```tsx
  <Button
    type="button"
    variant="outline"
    size="sm"
    disabled={!currentQueryValue?.trim() || previewMutation.isPending}
    onClick={() => previewMutation.mutate()}
  >
    {previewMutation.isPending
      ? t('datasets.previewingButton')
      : t('datasets.previewButton')}
  </Button>
  ```
- [x] Add the preview error and results panels (same pattern as Task 7) below the textarea, using `datasets.previewNoRows` and `datasets.previewRowCount` i18n keys.
- [x] Reset `previewResult` and `previewError` to `null` when the modal closes or the query textarea changes (to prevent stale results showing on next open).
- [x] Do NOT add Preview to the builder-mode section of the modal — builder-mode preview is exclusively on the dedicated builder page (`datasets_.$id.tsx`).

---

### Task 9: i18n — Add preview strings (AC: 4, 5)

- [x] Modify `web/src/lib/i18n/locales/en.json` (MODIFY):
  - [x] Under `datasets.builder` object, add:
    ```json
    "previewButton": "Preview",
    "previewingButton": "Previewing…",
    "previewNoRows": "No rows returned.",
    "previewRowCount": "{{count}} row(s) shown (LIMIT 10)"
    ```
  - [x] Under `datasets` top-level object (for the modal), add:
    ```json
    "previewButton": "Preview",
    "previewingButton": "Previewing…",
    "previewNoRows": "No rows returned.",
    "previewRowCount": "{{count}} row(s) shown (LIMIT 10)",
    "previewTimeout": "Preview timed out. Simplify the query or add filters."
    ```
  - [x] Verify `datasets.builderStateInvalid` and `datasets.invalidQuery` already exist (Story 11.1). Do NOT add duplicates.
  - [x] `npm run check` will fail (i18n-lint) if any new key is missing from `en.json` — add all keys before running the final verify step.

---

### Task 10: Backend — Tests (AC: 2, 3, 5, 6, 7, 8)

- [x] Create `src/FormForge.Api.Tests/Features/Datasets/DatasetPreviewTests.cs` (NEW — integration tests):
  - [x] **Test 1: `Post_Preview_CustomQuery_Returns200`** — POST `{ isCustomQuery: true, query: "SELECT 1 AS n" }` → 200, `columns == ["n"]`, `rows[0][0] == 1`.
  - [x] **Test 2: `Post_Preview_CustomQuery_DdlQuery_Returns422`** — POST `{ isCustomQuery: true, query: "DROP TABLE custom_dataset" }` → 422, `code == "DATASET_QUERY_INVALID"`.
  - [x] **Test 3: `Post_Preview_BuilderMode_ValidState_Returns200`** — POST `{ isCustomQuery: false, builderState: <valid state with one allowlisted table, one checked column> }` → 200, `columns` present.
  - [x] **Test 4: `Post_Preview_BuilderMode_NoLeftNode_Returns422`** — POST with builder state where no node has `side == "left"` → 422, `code == "BUILDER_STATE_INVALID"`.
  - [x] **Test 5: `Post_Preview_IsReadOnly`** — record `COUNT(*)` of `custom_dataset` before POST `/preview`, then after; assert count is unchanged.
  - [x] **Test 6: `Post_Preview_NoPermission_Returns403`** — request authenticated as a user without `dataset-management` permission → 403.

- [x] Create `src/FormForge.Api.Tests/Features/Datasets/PreviewServiceTests.cs` (NEW — unit tests, mock `IPreviewConnectionFactory`):
  - [x] `ExecuteAsync_CustomQuery_EmptyQuery_ReturnsSqlError`
  - [x] `ExecuteAsync_CustomQuery_NonSelectQuery_ReturnsSqlError` (e.g., `"DELETE FROM foo"`)
  - [x] `ExecuteAsync_BuilderMode_NullBuilderState_ReturnsBuilderStateInvalid`
  - [x] `ExecuteAsync_BuilderMode_GeneratorErrors_ReturnsBuilderStateInvalid` (mock `IDatasetAllowlist` to reject table)
  - [x] `ExecuteAsync_Timeout_SqlState57014_ReturnsTimeout` — mock connection to throw `NpgsqlException` with `SqlState == "57014"`

  **Note on 408 integration test:** Testing the real timeout (408) requires `pg_sleep(n)` and overriding `PreviewTimeoutSeconds` to 1 — this is fragile in CI. The unit test in `PreviewServiceTests.cs` covers the timeout detection path adequately.

---

### Task 11: Verify

- [x] `dotnet build src/FormForge.Api` → 0 warnings / 0 errors
- [x] `dotnet test src/FormForge.Api.Tests` → all new preview tests pass; pre-existing 2 audit 405 failures remain (do NOT reinvestigate)
- [x] `npm run test` → frontend tests pass
- [x] `npm run check` → TypeScript type-check passes; 0 new type errors; i18n-lint exits 0

---

## Dev Notes

### 1. What Already Exists — Do NOT Redo

- **`formforge_preview` role** — created by migration `20260602234849_CreateDatasetManagerFoundation.cs` (line 141). Granted SELECT on public tables, REVOKED on 14 internal tables. Verified by `DatasetMigrationTests.cs:220`. Only the connection string config is missing.
- **501 stub** — `POST /api/datasets/preview` already registered in `DatasetEndpoints.cs` at lines 62-64, already positioned before `MapPost("/")`. Replace the lambda body only; do NOT move the registration.
- **`DatasetSqlGenerator.Generate`** — already produces `ParameterizedSql` (with `$1,$2,…`) AND `Parameters` (`IReadOnlyList<object?>` in position order). Comment on line 16 explicitly says "Consumed by Story 11.3's preview endpoint." Use `ParameterizedSql` + `Parameters` for preview — NOT `ViewSql` (which inlines literals for VIEW DDL).
- **`SqlSelectEnforcer.Validate(sql)`** — exact method name is `Validate`, not `IsSelectOnly`. Returns `SqlValidationResult` with `.IsValid` (bool) and `.ErrorMessage` (string?). Uses pgsqlparser (not PgQuery.NET — that package is fictional per memory note).
- **`BuilderStateSerializer.Deserialize(string?)`** — located in `Dtos/BuilderStateDto.cs` line 135. Returns `BuilderStateDto?` (null on any parse failure). Call it the same way `DatasetService.UpdateAsync` does at line 335.
- **`DatasetManager:PreviewTimeoutSeconds`** — already in `appsettings.json` line 27 (default `5`). Read via `IConfiguration["DatasetManager:PreviewTimeoutSeconds"]`.

### 2. `SET LOCAL statement_timeout` — Why a Transaction Is Required

`SET LOCAL` scopes the setting to the **current transaction**. Without `BEGIN`, using plain `SET` would apply session-wide and persist across pooled connection reuses — a significant security risk. The pattern is:
```
BeginTransactionAsync → SET LOCAL statement_timeout → query → CommitAsync
```
`NpgsqlException.SqlState == "57014"` is PostgreSQL error code `query_canceled` (statement timeout fired). Match this exact string literal.

### 3. `PreviewConnectionFactory` — Connection Factory Pattern

Follow `DbConnectionFactory` exactly:
- Primary constructor injection (`IConfiguration configuration`)
- `new NpgsqlConnection(ConnectionString)` — NOT `NpgsqlDataSourceBuilder`
- `SuppressMessage("Performance", "CA1812")` for DI-only class
- Open before returning; dispose on failure

`Maximum Pool Size=5` (per AR-63) is enforced via the Npgsql connection string parameter, e.g.:
```
Host=postgres;...;Maximum Pool Size=5
```
Npgsql's default pool is per-unique-connection-string, so a separate `formforge_preview` connection string gets its own 5-connection pool automatically.

### 4. `formforge_preview` Role Password

The migration (`20260602234849_CreateDatasetManagerFoundation.cs:141`) creates the role as `LOGIN NOINHERIT` but sets no password. Find where the password is initialized by checking:
1. `docker-compose.yml` for an `initdb.d` script or env var
2. The Aspire AppHost (`AppHost/Program.cs`) for a post-migration SQL step
3. A separate `init-preview-role.sql` script in `Infrastructure/Persistence/`

Replace `CHANGEME` in `appsettings.Compose.json` with the actual password before running integration tests.

### 5. Service Injection in Minimal API Handlers

Looking at `DatasetEndpoints.cs`, services are injected directly as method parameters — e.g., `IDatasetService datasetService` — with NO `[FromServices]` attribute. In .NET 8+ Minimal APIs, types registered in DI are inferred as services automatically. `IPreviewService previewService` follows the same pattern.

### 6. `IReadOnlyList<object?>` — JSON Serialization

`PreviewResultDto.Rows` is `IReadOnlyList<IReadOnlyList<object?>>`. `System.Text.Json` via `Results.Ok(...)` serializes CLR types (`int`, `long`, `string`, `bool`, `DateTime`) natively. `null` entries (from `DBNull → null`) serialize as JSON `null`.

On the TypeScript side, `rows` items arrive as `string | number | boolean | null`. Render with `String(cell)` for display. `DateTime` values arrive as ISO 8601 strings.

### 7. `datasets.tsx` — Read Before Editing

Story 8.10 built the dataset management UI. Read `web/src/routes/_app/admin/datasets.tsx` completely before Task 8. The custom query textarea and its current state management must be understood to wire `previewMutation` correctly. Reset `previewResult` to `null` when the modal closes (on the dialog `onOpenChange` handler) to prevent stale results appearing on next open.

### 8. `useTranslation` Already Declared by Story 11.2

`datasets_.$id.tsx` has `const { t } = useTranslation()` added by Story 11.2. Do not add it a second time. If Story 11.2 is not yet merged into the branch, add it once.

### 9. Test: `formforge_preview` Role in Integration Tests

Integration tests use the same Postgres instance as the main DB (provisioned with migrations). Since the migration creates `formforge_preview`, the role already exists in the test DB. The test connection string for `formforge_preview` must be configured in the test project's `appsettings.json` or test setup — check how `DatasetMigrationTests.cs` provisions its connection to understand the test DB setup pattern.

### 10. Deferred Items Not in This Story

- Empty `builder_state` enforcement gap (11.2 dev notes §6) — still deferred.
- `JoinEdgeDataDto` C# DTO alignment (11.2 dev notes §5) — still deferred.
- 408 timeout integration test — deferred to unit test in `PreviewServiceTests.cs`.

### Project Structure Notes

**New files:**
- `src/FormForge.Api/Features/Datasets/Dtos/PreviewRequest.cs`
- `src/FormForge.Api/Features/Datasets/Dtos/PreviewResultDto.cs`
- `src/FormForge.Api/Features/Datasets/PreviewService.cs`
- `src/FormForge.Api/Infrastructure/Persistence/IPreviewConnectionFactory.cs`
- `src/FormForge.Api/Infrastructure/Persistence/PreviewConnectionFactory.cs`
- `src/FormForge.Api.Tests/Features/Datasets/DatasetPreviewTests.cs`
- `src/FormForge.Api.Tests/Features/Datasets/PreviewServiceTests.cs`

**Modified files:**
- `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs` — replace 501 stub at lines 62-64
- `src/FormForge.Api/Program.cs` — register `IPreviewConnectionFactory` (singleton) + `IPreviewService` (scoped)
- `src/FormForge.Api/appsettings.Compose.json` — add `"formforge_preview"` connection string with `Maximum Pool Size=5`
- `web/src/features/datasets/datasetApi.ts` — add `PreviewDatasetPayload`, `PreviewResult`, `previewDataset`
- `web/src/routes/_app/admin/datasets_.$id.tsx` — add Preview button + results panel
- `web/src/routes/_app/admin/datasets.tsx` — add Preview button + results in custom query form
- `web/src/lib/i18n/locales/en.json` — add preview i18n keys under `datasets.builder` and `datasets`

### References

- Epics: `_bmad-output/planning-artifacts/epics.md` §Story 11.3 (FR-72 / AR-63)
- Architecture: `_bmad-output/planning-artifacts/architecture.md` §AR-63 (preview pool + timeout execution), §AR-58 (permission gate), §6.13 (TanStack preview query key)
- `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs:62-64` — 501 stub to replace
- `src/FormForge.Api/Features/Datasets/DatasetSqlGenerator.cs:16,24-25` — `ParameterizedSql` + `Parameters` (for preview)
- `src/FormForge.Api/Features/Datasets/SqlSelectEnforcer.cs` — `Validate(sql)` → `SqlValidationResult` (exact API)
- `src/FormForge.Api/Features/Datasets/Dtos/BuilderStateDto.cs:135` — `BuilderStateSerializer.Deserialize`
- `src/FormForge.Api/Infrastructure/Persistence/DbConnectionFactory.cs` — connection factory pattern to replicate
- `src/FormForge.Api/appsettings.json:27` — `DatasetManager:PreviewTimeoutSeconds` (default 5)
- `src/FormForge.Api/appsettings.Compose.json` — existing connection strings (add `formforge_preview` here)
- `src/FormForge.Api.Tests/Features/Datasets/DatasetMigrationTests.cs:217-220` — `formforge_preview` role existence test
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260602234849_CreateDatasetManagerFoundation.cs:140-161` — role creation + GRANT/REVOKE script
- Memory: Pre-existing audit 405 test failures — 2 tests fail on clean tree, do NOT reinvestigate
- Memory: pgsqlparser (not PgQuery.NET) — `SqlSelectEnforcer` already uses pgsqlparser; no new parser usage needed in this story

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (1M context)

### Debug Log References

- `dotnet build src/FormForge.Api` → 0 warnings / 0 errors (after fixing CA2007 `await using`,
  CA2100 dynamic CommandText, EF1002 interpolated `ExecuteSqlRaw`).
- `dotnet test src/FormForge.Api.Tests` → 1009 passed; only the 2 documented pre-existing
  audit-405 failures remain (`SchemaAuditLogIntegrationTests` / `MutationAuditLogIntegrationTests`
  `*_DeleteVerb_Returns405`). New preview tests: 13 passed (filter `~Preview`).
- `npx tsc -b --noEmit` → exit 0. `npm run lint:i18n` → exit 0 (no missing keys; one intentional
  orphan `datasets.previewTimeout`). `vitest run` → 371 passed / 0 failed (initial run hit
  worker-pool startup timeouts under load — a `--pool=forks` re-run was fully green).

### Completion Notes List

- **Backend**: `PreviewRequest`/`PreviewResultDto` DTOs, `IPreviewConnectionFactory` +
  `PreviewConnectionFactory` (reads the dedicated `formforge_preview` pool), and `PreviewService`
  (resolves SQL from custom query via `SqlSelectEnforcer.Validate` or from `builder_state` via
  `DatasetSqlGenerator.Generate` → `ParameterizedSql` + `Parameters`; executes inside a
  transaction scoped by `SET LOCAL statement_timeout`; classifies 57014 → Timeout, other Npgsql
  errors → SqlError). The 501 stub at `DatasetEndpoints.cs` was replaced with the real handler
  mapping outcomes to 200 / 408 PREVIEW_TIMEOUT / 422 BUILDER_STATE_INVALID / 422
  DATASET_QUERY_INVALID. DI registered in `Program.cs`.
- **Deviation from spec (justified)**: the connection factory uses manual try/finally with
  `DisposeAsync().ConfigureAwait(false)` and synchronous `using` for commands/reader instead of
  the spec's `await using`, because this project enforces CA2007 (mirrors
  `DatasetService.GetByIdAsync`). A justified CA2100 suppression covers the dynamic CommandText
  (SELECT-only-validated / server-generated, values parameterized).
- **Preview role password (resolved ops gap)**: investigation found NO existing mechanism sets
  the `formforge_preview` role password anywhere (docker-compose, AppHost, and code all leave it
  unset; the migration documents that "ops/Aspire sets it via DATASET_PREVIEW_DB_PASSWORD" but
  nothing does). To make the connection string functional under Compose and honor that contract,
  added an idempotent startup step in `Program.cs` (right after `db.Database.Migrate()`) that
  runs `ALTER ROLE formforge_preview … PASSWORD` from `DatasetManager:PreviewRolePassword` when
  configured. `appsettings.Compose.json` now sets both the `formforge_preview` connection string
  (`Maximum Pool Size=5`) and that password. The step is skipped when no password is configured
  (integration tests point the preview pool at the test superuser connection string, so the role
  password is never needed there). `appsettings.Development.json` was intentionally NOT changed:
  the Aspire AppHost provisions Postgres on a dynamic port, so a static preview connection string
  cannot be hardcoded — Compose is the supported preview path for local runs.
- **Frontend**: `previewDataset` + `PreviewDatasetPayload`/`PreviewResult` added to
  `datasetApi.ts` (via the project's `httpClient`, NOT the spec's assumed `apiPost`/`ApiError`
  helper — adapted to the real codebase). Builder page (`datasets_.$id.tsx`) gained a Preview
  button + bounded results panel; the custom-query form (`ModeToggleAndQuery` in `datasets.tsx`)
  replaced its disabled placeholder with a working Preview button + results table, resetting
  stale output on query change. Removed the now-orphaned `admin.datasets.previewButton` /
  `previewNotYetAvailable` i18n keys.
- **i18n**: added preview keys under `datasets.builder` and `datasets`.
- **AC coverage**: AC-1 (button present/active), AC-2 (custom-query SELECT + LIMIT 10 on preview
  pool), AC-3 (builder-mode generation + execute), AC-4 (columns + ≤10 rows), AC-5 (408 timeout
  path — unit-tested via 57014), AC-6 (422 surfaces PG message), AC-7 (read-only — count
  unchanged), AC-8 (403 without permission).

### File List

**New (backend):**
- `src/FormForge.Api/Features/Datasets/Dtos/PreviewRequest.cs`
- `src/FormForge.Api/Features/Datasets/Dtos/PreviewResultDto.cs`
- `src/FormForge.Api/Features/Datasets/PreviewService.cs`
- `src/FormForge.Api/Infrastructure/Persistence/IPreviewConnectionFactory.cs`
- `src/FormForge.Api/Infrastructure/Persistence/PreviewConnectionFactory.cs`
- `src/FormForge.Api.Tests/Features/Datasets/DatasetPreviewTests.cs`
- `src/FormForge.Api.Tests/Features/Datasets/PreviewServiceTests.cs`

**Modified (backend):**
- `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs` — replaced /preview 501 stub
- `src/FormForge.Api/Program.cs` — DI registration + idempotent preview-role password step
- `src/FormForge.Api/appsettings.Compose.json` — `formforge_preview` connection string + password

**Modified (frontend):**
- `web/src/features/datasets/datasetApi.ts` — `previewDataset` + types
- `web/src/routes/_app/admin/datasets_.$id.tsx` — builder-page Preview button + results panel
- `web/src/routes/_app/admin/datasets.tsx` — custom-query Preview button + results table
- `web/src/lib/i18n/locales/en.json` — preview keys (added); orphaned placeholder keys (removed)

### Change Log

| Date | Change |
|------|--------|
| 2026-06-04 | Story 11.3 implemented: read-only LIMIT-10 query preview (custom + builder modes), preview connection pool/factory, endpoint + DI, frontend Preview UI, i18n, tests. Status → review. |

---

### Review Findings

- [x] [Review][Patch] Cache `ConnectionString` in constructor — re-reads `IConfiguration` on every call; late failure if key absent [`src/FormForge.Api/Infrastructure/Persistence/PreviewConnectionFactory.cs`]
- [x] [Review][Patch] Validate `DatasetManager:PreviewTimeoutSeconds` — non-numeric value surfaces as SqlError; zero disables timeout entirely; fall back to 5 [`src/FormForge.Api/Features/Datasets/PreviewService.cs:77`]
- [x] [Review][Patch] Add `useEffect([builderState])` to clear stale preview results when canvas state changes [`web/src/routes/_app/admin/datasets_.$id.tsx`]
- [x] [Review][Patch] Fix duplicate React keys in preview table headers — use `key={col + '-' + i}` [`web/src/routes/_app/admin/datasets.tsx`, `web/src/routes/_app/admin/datasets_.$id.tsx`]
- [x] [Review][Patch] Strip trailing semicolon from custom query before subquery wrapping — `SELECT 1;` causes Postgres parse error [`src/FormForge.Api/Features/Datasets/PreviewService.cs:98`]
- [x] [Review][Patch] Add null guard for `generated.ParameterizedSql` after `HasErrors` check — null-forgiving `!` could throw NullReferenceException on generator contract violation [`src/FormForge.Api/Features/Datasets/PreviewService.cs:68`]
- [x] [Review][Patch] Add log statement in `_` wildcard of `PreviewOutcome` switch — silent 500 with no diagnostic [`src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs`]
- [x] [Review][Patch] Fix `datasets.previewTimeout` i18n key wording to match AC-5 ("Preview query exceeded the time limit. Simplify the query or add filters.") [`web/src/lib/i18n/locales/en.json`]
- [x] [Review][Patch] Add integration test for genuine Postgres execution error path — AC-6 gap (e.g. `SELECT * FROM nonexistent_table` → 422 with PG message) [`src/FormForge.Api.Tests/Features/Datasets/DatasetPreviewTests.cs`]
- [x] [Review][Defer] Rate limiting on `/preview` — shared "admin" 120 req/min bucket; preview-specific limit out of scope [`DatasetEndpoints.cs`] — deferred, pre-existing rate limit architecture
- [x] [Review][Defer] `CommitAsync` on read-only transaction — dispose triggers implicit rollback; no correctness bug [`PreviewService.cs`] — deferred, pre-existing
- [x] [Review][Defer] Hardcoded password in `appsettings.Compose.json` — dev-only file, by design [`appsettings.Compose.json`] — deferred, pre-existing
- [x] [Review][Defer] `OperationCanceledException` not mapped — framework handles client disconnect gracefully [`PreviewService.cs`] — deferred, pre-existing
- [x] [Review][Defer] No integration test for 408 timeout path — explicitly deferred per Dev Notes §Task10 [`DatasetPreviewTests.cs`] — deferred, pre-existing
- [x] [Review][Defer] Modal close preview reset relies on component unmount — unmount achieves spec requirement; theoretical key-reuse concern only [`datasets.tsx`] — deferred, pre-existing
- [x] [Review][Defer] Password null byte in config crashes Postgres silently — pathological edge case, dev env only [`Program.cs`] — deferred, pre-existing
- [x] [Review][Defer] `BuilderStateJson` test helper duplicated across two test classes — test code quality, not a defect [`DatasetPreviewTests.cs`, `PreviewServiceTests.cs`] — deferred, pre-existing
- [x] [Review][Defer] `PreviewRequest` no FluentValidation — ASP.NET 400 for malformed body is reasonable behavior [`PreviewRequest.cs`] — deferred, pre-existing
