# Story 8.4: Create Dataset

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a user with the `dataset-management` permission,
I want to create a new Dataset by providing a `dataset_name`, authoring mode, and optional query,
so that a row and a backing PostgreSQL VIEW are created atomically.

## Acceptance Criteria

**AC-1 — Happy path: create returns 201 with DatasetDto**
**Given** I POST `/api/datasets` with `{ "datasetName": "my_report", "isCustomQuery": true, "query": "SELECT id FROM users" }`
**When** `dataset_name` validation passes (already enforced inline per Story 8.3) and the transaction commits
**Then** within one NpgsqlTransaction (per AR-59):
  - A row is inserted into `custom_dataset` with `version = 1`, `created_at = now()`, `created_by = actorId`
  - `CREATE VIEW datasets."my_report" AS SELECT id FROM users` executes
**And** HTTP 201 is returned with body `{ id, dataset_name, is_custom_query, query, builder_state, version, created_at, created_by }`
**And** the `Location` response header is set to `/api/datasets/{id}`

**AC-2 — Null/empty query: placeholder VIEW is created**
**Given** I POST `/api/datasets` with `{ "datasetName": "builder_ds", "isCustomQuery": false }` (no `query` field)
**When** the transaction commits
**Then** the `custom_dataset` row stores `query = NULL`
**And** `CREATE VIEW datasets."builder_ds" AS SELECT 1 AS placeholder` is executed as the VIEW definition
**And** HTTP 201 is returned; the response body's `query` field is `null`

**AC-3 — Duplicate name returns 409 DATASET_NAME_CONFLICT**
**Given** a Dataset named `existing_ds` already exists
**When** I POST `/api/datasets` with `{ "datasetName": "existing_ds", "isCustomQuery": true }`
**Then** HTTP 409 is returned with `{ code: "DATASET_NAME_CONFLICT" }` (per AR-57 / FR-57 AC-3)
**And** no new `custom_dataset` row is inserted and no VIEW is created

**AC-4 — VIEW DDL failure returns 422 INVALID_QUERY and rolls back**
**Given** I POST `/api/datasets` with `{ "datasetName": "bad_query_ds", "isCustomQuery": true, "query": "NOT VALID SQL AT ALL" }`
**When** the server attempts `CREATE VIEW datasets."bad_query_ds" AS NOT VALID SQL AT ALL`
**Then** PostgreSQL rejects it with a syntax error
**And** the transaction rolls back — no `custom_dataset` row exists, no VIEW is created
**And** HTTP 422 is returned with `{ code: "INVALID_QUERY" }` and the PostgreSQL error message in `detail`

**AC-5 — Audit log: success path**
**Given** the transaction commits successfully (AC-1 or AC-2 path)
**When** I query `dataset_audit_log`
**Then** one row exists with `operation = 'CREATE'`, `dataset_name = <name>`, `actor_id = <actorId>`, `succeeded = true`, `ddl = <exact CREATE VIEW SQL>`, `correlation_id = <request correlationId>`

**AC-6 — Audit log: failure path (DDL failure)**
**Given** the CREATE VIEW DDL fails and the transaction rolls back (AC-4 path)
**When** I query `dataset_audit_log`
**Then** one row exists with `operation = 'CREATE'`, `dataset_name = <name>`, `actor_id = <actorId>`, `succeeded = false`, `ddl = <attempted CREATE VIEW SQL>`, `correlation_id = <request correlationId>`
**Note:** No audit row is written for 23505 name-conflict rejections (the INSERT failed — no DDL was attempted)

---

## Tasks / Subtasks

- [x] **Task 1 — Create `DatasetDto.cs` response DTO** (AC-1, AC-2)
  - [x] Create `src/FormForge.Api/Features/Datasets/Dtos/DatasetDto.cs`:
    ```csharp
    namespace FormForge.Api.Features.Datasets.Dtos;

    internal sealed record DatasetDto(
        Guid Id,
        string DatasetName,
        bool IsCustomQuery,
        string? Query,
        string? BuilderState,
        int Version,
        DateTimeOffset CreatedAt,
        Guid? CreatedBy);
    ```
  - [x] This record is returned in the 201 body and will also be used by Stories 8.5 (update), 8.7 (get).

- [x] **Task 2 — Create `DatasetViewManager.cs`** (AC-1, AC-2, AC-4)
  - [x] Create `src/FormForge.Api/Features/Datasets/DatasetViewManager.cs`:
    ```csharp
    using Dapper;
    using FormForge.Api.Domain.ValueTypes;
    using FormForge.Api.Infrastructure.Persistence;
    using Npgsql;

    namespace FormForge.Api.Features.Datasets;

    // Story 8.4 — CREATE VIEW. Stories 8.5/8.6 will add
    // CREATE OR REPLACE / ALTER RENAME / DROP VIEW operations.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
        Justification = "Registered via DI.")]
    internal sealed class DatasetViewManager(ILogger<DatasetViewManager> logger)
    {
        // Builds the schema-qualified CREATE VIEW DDL string.
        // dataset_name is validated (DatasetName value type guarantees safe identifier).
        // Double-quotes the view name for SQL standards compliance.
        internal static string BuildCreateViewDdl(DatasetName name, string effectiveQuery) =>
            $"""CREATE VIEW datasets."{name.Value}" AS {effectiveQuery}""";

        // Executes CREATE VIEW inside the caller-supplied transaction.
        // Throws NpgsqlException on PostgreSQL errors — caller is responsible for rollback.
        internal async Task CreateAsync(
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            DatasetName name,
            string effectiveQuery,
            CancellationToken ct)
        {
            var ddl = BuildCreateViewDdl(name, effectiveQuery);
            await conn.ExecuteAsync(new CommandDefinition(
                ddl,
                transaction: tx,
                commandTimeout: DbConnectionFactory.DdlCommandTimeoutSeconds,
                cancellationToken: ct)).ConfigureAwait(false);

            logger.LogInformation("Created VIEW datasets.\"{Name}\"", name.Value);
        }
    }
    ```

- [x] **Task 3 — Create `IDatasetService` interface and `DatasetService` class** (AC-1 through AC-6)
  - [x] Create `src/FormForge.Api/Features/Datasets/DatasetService.cs`
  - [x] Define outcome enum and result record at the top of the file:
    ```csharp
    internal enum CreateDatasetOutcome { Success, NameConflict, InvalidQuery }

    internal sealed record CreateDatasetResult(
        CreateDatasetOutcome Outcome,
        DatasetDto? Dataset = null,
        string? ErrorDetail = null);
    ```
  - [x] Define the interface:
    ```csharp
    internal interface IDatasetService
    {
        Task<CreateDatasetResult> CreateAsync(
            CreateDatasetRequest request,
            DatasetName name,
            Guid actorId,
            string? correlationId,
            CancellationToken ct);
    }
    ```
  - [x] Implement `DatasetService` — constructor-inject `FormForgeDbContext db`, `DbConnectionFactory connectionFactory`, `DatasetViewManager viewManager`, `ILogger<DatasetService> logger`
  - [x] `CreateAsync` implementation (see Dev Notes §1 for full pattern):
    - Resolve actor name: `var actorName = await db.Users.Where(u => u.Id == actorId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);`
    - Compute effective query: `var effectiveQuery = string.IsNullOrWhiteSpace(request.Query) ? "SELECT 1 AS placeholder" : request.Query;`
    - Pre-build the DDL string so it can be recorded in the audit log even on failure: `var viewDdl = DatasetViewManager.BuildCreateViewDdl(name, effectiveQuery);`
    - Generate `newId = Guid.NewGuid()` and `now = DateTimeOffset.UtcNow`
    - Open a raw NpgsqlConnection via `connectionFactory.CreateOpenConnectionAsync(ct)`
    - Begin NpgsqlTransaction on the connection
    - In a try/catch:
      - **INSERT** into `custom_dataset` via Dapper (see §1 for full SQL + params)
      - **CREATE VIEW** via `viewManager.CreateAsync(conn, tx, name, effectiveQuery, ct)`
      - **CommitAsync(CancellationToken.None)** — once reached, do not cancel the commit
    - Catch `PostgresException pg when pg.SqlState == PostgresErrorCodes.UniqueViolation`:
      - `await tx.RollbackAsync(CancellationToken.None)`
      - Return `new CreateDatasetResult(CreateDatasetOutcome.NameConflict)`
      - **Do NOT write an audit log entry for 23505** — no DDL was attempted
    - Catch `NpgsqlException ex` (all other PG errors — DDL failure):
      - `await tx.RollbackAsync(CancellationToken.None)`
      - Record the error message: `pgErrorDetail = ex.Message`
    - After try/catch: **always** dispose `tx` and `conn` in finally blocks (see §2 for disposal pattern)
    - If `succeeded`:
      - Write success audit log entry via EF (see §3)
      - `await db.SaveChangesAsync(CancellationToken.None)`
      - Return `new CreateDatasetResult(CreateDatasetOutcome.Success, Dataset: dto)`
    - If `!succeeded` (DDL failure path):
      - Write failed audit log entry via EF (see §3)
      - `await db.SaveChangesAsync(CancellationToken.None)`
      - Return `new CreateDatasetResult(CreateDatasetOutcome.InvalidQuery, ErrorDetail: pgErrorDetail)`

- [x] **Task 4 — Update `DatasetEndpoints.cs` POST handler** (AC-1 through AC-4)
  - [x] Update `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs`
  - [x] Inject `IDatasetService datasetService` in the `MapDatasetEndpoints` method signature (Minimal API route parameter binding pulls it from DI automatically)
  - [x] Replace the POST `/` stub handler:
    ```csharp
    group.MapPost("/", async (
        [FromBody] CreateDatasetRequest request,
        IDatasetService datasetService,
        HttpContext httpContext,
        CancellationToken ct) =>
    {
        if (!DatasetName.TryCreate(request.DatasetName, out var name, out _, out var nameError))
            return InvalidDatasetName(nameError);

        if (!Guid.TryParse(httpContext.User.FindFirst("userId")?.Value, out var actorId))
            return Results.Unauthorized();

        var correlationId = httpContext.GetCorrelationId();
        var result = await datasetService.CreateAsync(request, name!, actorId, correlationId, ct)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            CreateDatasetOutcome.Success => Results.Created(
                $"/api/datasets/{result.Dataset!.Id}", result.Dataset),
            CreateDatasetOutcome.NameConflict => Results.Problem(
                detail: $"A dataset named '{request.DatasetName}' already exists.",
                title: "Dataset name conflict",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "DATASET_NAME_CONFLICT",
                    ["messageKey"] = "datasets.nameConflict",
                }),
            CreateDatasetOutcome.InvalidQuery => Results.Problem(
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
    .WithSummary("Create dataset (Story 8.4 — FR-58)")
    .RequireDatasetManagement();
    ```
  - [x] **Add required usings** at the top of `DatasetEndpoints.cs`:
    - `using FormForge.Api.Common.Logging;` (for `GetCorrelationId()`)
    - `using System.Security.Claims;` (if not already present)
  - [x] Keep the PUT, DELETE, GET stubs unchanged — they are still 501 for this story
  - [x] **IMPORTANT — do NOT add `.AddValidationFilter<CreateDatasetRequest>()` to the POST endpoint.** The FV filter emits a 400 `ValidationProblemDetails` without a root `code` field. The inline `DatasetName.TryCreate` check (from Story 8.3) is the correct path. See Story 8.3 §3 and review finding §Defer.

- [x] **Task 5 — Register services in `Program.cs`** (AC-1)
  - [x] Update `src/FormForge.Api/Program.cs` — add after the existing Dataset validator registrations:
    ```csharp
    // Story 8.4 — Dataset lifecycle service and VIEW DDL manager.
    builder.Services.AddScoped<DatasetViewManager>();
    builder.Services.AddScoped<IDatasetService, DatasetService>();
    ```
  - [x] Add the required usings:
    - `using FormForge.Api.Features.Datasets;`
    - (If `DatasetViewManager` is in the same namespace, no extra using needed.)
  - [x] Verify the `MapDatasetEndpoints()` call in Program.cs does NOT need changes — Minimal API DI injects `IDatasetService` automatically.

- [x] **Task 6 — Integration tests: `DatasetViewLifecycleTests.cs`** (AC-1 through AC-6)
  - [x] Create `src/FormForge.Api.Tests/Features/Datasets/DatasetViewLifecycleTests.cs`
  - [x] Use the same `PostgresFixture` + `WebApplicationFactory<Program>` pattern from `DatasetPermissionTests.cs` (see Dev Notes §4)
  - [x] Seed: platform-admin role (id `00000000-0000-0000-0000-000000000001`, `can_manage_datasets=true`) + one admin user; login for JWT
  - [x] TRUNCATE `custom_dataset, dataset_audit_log` (in addition to roles/users) between test class setup to isolate from other test classes:
    ```csharp
    await db.Database.ExecuteSqlRawAsync(
        "TRUNCATE TABLE custom_dataset, dataset_audit_log, " +
        "role_permissions, user_roles, roles, refresh_tokens, users " +
        "RESTART IDENTITY CASCADE;");
    ```
  - [x] **Test 1 — AC-1: Create with query → 201, response body correct, VIEW exists**
    - POST `{ "datasetName": "lifecycle_test", "isCustomQuery": true, "query": "SELECT 1 AS n" }` with admin token
    - Assert HTTP 201
    - Assert response body: `id` (Guid), `datasetName = "lifecycle_test"`, `isCustomQuery = true`, `query = "SELECT 1 AS n"`, `builderState = null`, `version = 1`, `createdAt` (non-null), `createdBy` (non-null Guid)
    - Assert `Location` header = `/api/datasets/{id}`
    - Assert VIEW exists in `datasets` schema via raw SQL:
      ```csharp
      await using var viewCheck = new NpgsqlCommand(
          "SELECT COUNT(*) FROM information_schema.views " +
          "WHERE table_schema = 'datasets' AND table_name = 'lifecycle_test';", conn);
      Assert.Equal(1L, await viewCheck.ExecuteScalarAsync());
      ```
  - [x] **Test 2 — AC-2: Create without query → 201, query null, placeholder VIEW exists**
    - POST `{ "datasetName": "builder_placeholder", "isCustomQuery": false }` (no `query` field)
    - Assert HTTP 201, `query` in response is `null`
    - Assert VIEW `datasets.builder_placeholder` exists
    - Assert VIEW definition contains `SELECT 1 AS placeholder` (via `pg_get_viewdef`):
      ```csharp
      await using var defCmd = new NpgsqlCommand(
          "SELECT pg_get_viewdef('datasets.builder_placeholder'::regclass, true);", conn);
      var def = (string?)await defCmd.ExecuteScalarAsync();
      Assert.Contains("1", def, StringComparison.Ordinal);
      ```
  - [x] **Test 3 — AC-3: Duplicate name → 409 DATASET_NAME_CONFLICT**
    - Create `"conflict_ds"` successfully (first POST → 201)
    - Second POST with same name → Assert HTTP 409
    - Assert `code = "DATASET_NAME_CONFLICT"` at JSON root (same pattern as `INVALID_DATASET_NAME` assertions in Story 8.3 tests)
    - Assert only ONE `custom_dataset` row for `"conflict_ds"` (second request did not insert)
  - [x] **Test 4 — AC-4: Invalid SQL → 422 INVALID_QUERY, row rolled back**
    - POST `{ "datasetName": "bad_sql_ds", "isCustomQuery": true, "query": "NOT VALID SQL XYZ" }`
    - Assert HTTP 422
    - Assert `code = "INVALID_QUERY"` at JSON root
    - Assert `detail` field is non-null (PostgreSQL error message present)
    - Assert no `custom_dataset` row was created (SELECT COUNT from `custom_dataset WHERE dataset_name = 'bad_sql_ds'` → 0)
    - Assert VIEW `datasets.bad_sql_ds` does NOT exist
  - [x] **Test 5 — AC-5: Audit log success entry**
    - Create `"audit_success_ds"` with query `"SELECT 1 AS n"` → 201
    - Query `dataset_audit_log WHERE dataset_name = 'audit_success_ds'`
    - Assert 1 row, `operation = 'CREATE'`, `succeeded = true`, `ddl` starts with `CREATE VIEW datasets."audit_success_ds"`, `actor_id` is the admin user's ID
  - [x] **Test 6 — AC-6: Audit log failure entry**
    - POST with `"datasetname_failure"` and bad SQL → 422
    - Query `dataset_audit_log WHERE dataset_name = 'datasetname_failure'`
    - Assert 1 row, `operation = 'CREATE'`, `succeeded = false`, `ddl` contains `CREATE VIEW datasets."datasetname_failure"`, `actor_id` is non-null
  - [x] **Test 7 — No audit row for name conflict**
    - Create `"no_audit_conflict"` successfully
    - Create `"no_audit_conflict"` again (409 response)
    - Query `dataset_audit_log WHERE dataset_name = 'no_audit_conflict'`
    - Assert exactly 1 row (only the successful creation, not the conflict attempt)
  - [x] **Test 8 — Update Story 8.3 test regression guard**
    - `DatasetPermissionTests.PostDatasets_Admin_Returns501NotForbidden` currently asserts 501. This will now return 201 when a valid name is posted. Update that test to assert HTTP 201 (or 4xx for an unexpected failure), OR change the payload to something that will fail differently. **Best fix**: Update the test to post a unique probe name and assert 201 instead of 501:
      ```csharp
      // Story 8.4 implements the create handler; it now returns 201 (not 501).
      Assert.Equal(HttpStatusCode.Created, response.StatusCode);
      ```

- [x] **Task 7 — i18n keys for new error codes** (AC-3, AC-4)
  - [x] Update `web/src/lib/i18n/locales/en.json` — add under the existing `"datasets"` key:
    ```json
    "nameConflict": "A dataset with this name already exists.",
    "invalidQuery": "The query is invalid."
    ```
  - [x] The `datasets` block after this story should include:
    ```json
    "datasets": {
      "invalidDatasetName": "Invalid dataset name.",
      "nameConflict": "A dataset with this name already exists.",
      "invalidQuery": "The query is invalid.",
      "validation": { ... }   ← existing from Story 8.3
    }
    ```


### Review Findings

- [x] [Review][Patch] P1: 23505 guard not scoped to `custom_dataset` constraint — any other unique violation returns 409 incorrectly [DatasetService.cs:111]
- [x] [Review][Patch] P2: Audit `SaveChangesAsync` failure after successful Dapper commit returns 500 despite row+VIEW existing [DatasetService.cs:147]
- [x] [Review][Patch] P3: `OperationCanceledException` bypasses NpgsqlException catches and falls through to audit write, producing a misleading failed audit entry on cancellation [DatasetService.cs:79]
- [x] [Review][Patch] P4: Test fixture `DO $$ DROP VIEW $$` nukes ALL views in `datasets` schema — races with other Dataset test classes running in parallel [DatasetViewLifecycleTests.cs:56]
- [x] [Review][Patch] P5: AC-5/AC-6 audit tests do not assert `dataset_name` field in audit row [DatasetViewLifecycleTests.cs:281,307]
- [x] [Review][Patch] P6: AC-5/AC-6 audit tests do not assert `correlation_id` round-trip [DatasetViewLifecycleTests.cs:280,306]
- [x] [Review][Patch] P7: AC-6 failure test asserts `ActorId != null` instead of `Assert.Equal(_adminUserId, entry.ActorId)` [DatasetViewLifecycleTests.cs:311]
- [x] [Review][Patch] P8: AC-2 test missing `Version == 1`, `CreatedAt != default`, `CreatedBy == _adminUserId` assertions [DatasetViewLifecycleTests.cs:197]
- [x] [Review][Patch] P9: `Program.cs` comment says FV filter is "ready for Stories 8.4/8.5" — contradicts spec §7 prohibition on wiring it to POST [Program.cs:247]
- [x] [Review][Patch] P10: `Guid.Empty` accepted as valid `actorId` when JWT `userId` claim is the zero-GUID [DatasetEndpoints.cs:48]
- [x] [Review][Defer] D1: Application-side `DateTimeOffset.UtcNow` used for `created_at` instead of PostgreSQL `now()` at transaction start [DatasetService.cs:68] — deferred, consistent with existing DynamicQueryBuilder pattern throughout codebase
- [x] [Review][Defer] D2: 409 conflict detail string uses raw `request.DatasetName` instead of validated `name.Value` [DatasetEndpoints.cs:63] — deferred, functionally equivalent post-validation; only matters if DatasetName ever normalises

---

## Dev Notes

### §1 — Dapper INSERT pattern for `custom_dataset`

The INSERT runs via Dapper on the raw NpgsqlConnection (NOT via EF). This is required so both the INSERT and the CREATE VIEW execute within the **same NpgsqlTransaction** (AR-59 mandates synchronous atomicity). EF uses its own separate connection pool.

```csharp
const string insertSql = """
    INSERT INTO custom_dataset
        (id, dataset_name, is_custom_query, query, builder_state, version, created_at, created_by)
    VALUES
        (@id, @datasetName, @isCustomQuery, @query, NULL, 1, @now, @createdBy)
    """;

await conn.ExecuteAsync(new CommandDefinition(
    insertSql,
    new
    {
        id        = newId,
        datasetName  = name.Value,
        isCustomQuery = request.IsCustomQuery,
        query     = string.IsNullOrWhiteSpace(request.Query) ? (object?)DBNull.Value : request.Query,
        now,
        createdBy = (object?)actorId,   // nullable Guid — use object? for Dapper nullable handling
    },
    transaction: tx,
    commandTimeout: 5,   // data INSERT — short timeout (not DDL)
    cancellationToken: ct)).ConfigureAwait(false);
```

**`createdBy` as nullable Guid:** `custom_dataset.created_by` is `UUID` (nullable FK). Pass as `(object?)actorId` so Dapper maps it correctly. If actorId were null (which it won't be post-auth), use `DBNull.Value`. The actorId comes from the authenticated JWT claim and is guaranteed non-null after the `Guid.TryParse` check in the endpoint.

**`query` as nullable string:** Pass `DBNull.Value` when the query is null/empty. Dapper maps `null` as SQL NULL only when the param type is explicitly nullable or when using `DBNull.Value`.

### §2 — Transaction / connection disposal pattern

Mirror the DynamicDataEndpoints disposal pattern exactly (src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs lines 800–839). Dispose the transaction and connection in `finally` blocks with `ConfigureAwait(false)`, and use `CancellationToken.None` on Commit and Rollback so host shutdown doesn't abort them mid-flight.

```csharp
var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
NpgsqlTransaction? tx = null;
bool succeeded = false;
string? pgErrorDetail = null;
try
{
    tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
    try
    {
        // INSERT + CREATE VIEW ...
        await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        succeeded = true;
    }
    catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.UniqueViolation)
    {
        await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        return new CreateDatasetResult(CreateDatasetOutcome.NameConflict);
        // Return immediately — no audit entry for 23505 (no DDL was attempted)
    }
    catch (NpgsqlException ex)
    {
        await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        pgErrorDetail = ex.Message;  // PostgreSQL error message for the INVALID_QUERY detail
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
```

**PostgresException vs NpgsqlException:** `PostgresException` is a subclass of `NpgsqlException`. The 23505 catch (unique violation) must appear **before** the general `NpgsqlException` catch. The current structure is correct. Any non-23505 `NpgsqlException` (including `PostgresException` with other SqlState codes) falls into the general catch — this covers DDL syntax errors, semantic errors, etc.

**Why 23505 fires from INSERT, not CREATE VIEW:** The `custom_dataset.dataset_name` column has a UNIQUE index (`idx_custom_dataset_dataset_name`). If the name already exists, the INSERT fails with 23505 before the CREATE VIEW is ever attempted. A pre-existing VIEW with the same name would cause a different error (42P07) from the CREATE VIEW step; that is caught by the general NpgsqlException handler and returns INVALID_QUERY. In practice, if the row exists, the INSERT always fails first.

### §3 — Audit log via EF (separate from the Dapper transaction)

The audit log entry is written via EF **after** the Dapper transaction commits or rolls back. This is the same pattern as DynamicDataEndpoints (audit written after the main data transaction):

```csharp
// Serialize new_values for the audit log (on success only)
var newValues = succeeded
    ? System.Text.Json.JsonSerializer.Serialize(new
        {
            dataset_name   = name.Value,
            is_custom_query = request.IsCustomQuery,
            query          = request.Query,
        })
    : null;

db.DatasetAuditLog.Add(new DatasetAuditLogEntry
{
    DatasetName    = name.Value,
    Operation      = "CREATE",
    ActorId        = actorId,
    ActorName      = actorName,          // resolved from DB at top of method
    NewValues      = newValues,
    Ddl            = viewDdl,            // the full CREATE VIEW string (attempted or committed)
    Succeeded      = succeeded,
    CorrelationId  = correlationId,
    Timestamp      = now,
});
await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
```

The `Timestamp` is set to the same `now` computed at the start of the method, keeping it consistent with the `custom_dataset.created_at` value.

### §4 — Integration test setup pattern

Reuse the exact pattern from `DatasetNameValidationTests.cs` (which itself mirrors `DatasetPermissionTests.cs`):

```csharp
public async Task InitializeAsync()
{
    _factory = new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:formforge", _postgres.ConnectionString);
            builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
            builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
        });

    using var scope = _factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
    await db.Database.MigrateAsync();

    await db.Database.ExecuteSqlRawAsync(
        "TRUNCATE TABLE custom_dataset, dataset_audit_log, " +
        "role_permissions, user_roles, roles, refresh_tokens, users " +
        "RESTART IDENTITY CASCADE;");

    await ReseedAdminRoleAsync(db);
    _adminUserId = await SeedAdminUserAsync(db);

    _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        HandleCookies = false,
    });
}
```

**Raw NpgsqlConnection for VIEW inspection:** Open a raw `NpgsqlConnection` to `_postgres.ConnectionString` (not via the factory) to inspect `information_schema.views` and `pg_get_viewdef`. This avoids WAF middleware and EF mapping overhead for schema assertions:

```csharp
private async Task<NpgsqlConnection> OpenRawAsync()
{
    var conn = new NpgsqlConnection(_postgres.ConnectionString);
    await conn.OpenAsync();
    return conn;
}
```

**Asserting code at JSON root:** Same pattern as Story 8.3:
```csharp
var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
Assert.Equal("DATASET_NAME_CONFLICT", doc.RootElement.GetProperty("code").GetString());
```
`Results.Problem` extensions are flattened to the JSON root via `[JsonExtensionData]` in ASP.NET Core's `ProblemDetails` serialization.

### §5 — Story 8.3 test regression: `PostDatasets_Admin_Returns501NotForbidden`

`DatasetPermissionTests.PostDatasets_Admin_Returns501NotForbidden` currently posts `{ datasetName = "admin_probe", isCustomQuery = true }` and asserts `HttpStatusCode.NotImplemented` (501). After Story 8.4, that endpoint is fully implemented — a valid dataset name will trigger the real handler and return 201 (on first call) or 409 (if `admin_probe` already exists from a prior test run). 

**Fix:** Update the assertion in `DatasetPermissionTests` to expect 201:
```csharp
// Story 8.4 implements the create handler — valid names now return 201.
Assert.Equal(HttpStatusCode.Created, response.StatusCode);
```

Alternatively, if test isolation is a concern (because `PostgresFixture` is shared and the name might already exist), use a unique probe name per test run (e.g., `$"admin_probe_{Guid.NewGuid():N}"`). The simplest fix is changing the assertion to 201 and accepting that the first run succeeds, subsequent runs get 409. But the test class truncates the DB in `InitializeAsync`, so re-runs are clean. A 201 assertion is safe.

### §6 — Scope boundary: what Story 8.4 does NOT implement

| Feature | Target Story |
|---------|-------------|
| SELECT-only enforcement via `PgQuery.NET` / `SqlSelectEnforcer.cs` | 8.8 |
| UPDATE Dataset (PUT handler replaces stub) | 8.5 |
| DELETE Dataset (DELETE handler replaces stub) | 8.6 |
| LIST/GET datasets (GET handlers replace stubs) | 8.7 |
| Dataset Management UI | 8.10 |
| `DatasetViewManager.ReplaceAsync` / `RenameAsync` / `DropAsync` | 8.5 / 8.6 |
| `UpdateDatasetRequest.Version` minimum-value guard (Story 8.3 deferred review) | 8.5 |

**Note on `SqlSelectEnforcer`:** Story 8.4's `INVALID_QUERY` path is triggered by PostgreSQL rejecting the DDL (syntax errors, semantic errors such as DELETE in a VIEW, etc.). The pre-DDL PgQuery.NET SELECT-only check that runs *before* DDL is Story 8.8's responsibility. Both error paths surface to the caller as 422 `INVALID_QUERY`. The test for invalid SQL in Task 6 covers the PostgreSQL-rejection path; Story 8.8 will add tests for the PgQuery.NET rejection path.

### §7 — FluentValidation filter caution (Story 8.3 review finding: Defer)

Story 8.3's code review flagged: "if `AddValidationFilter<CreateDatasetRequest>()` is attached to the POST endpoint, the response will be a 400 `ValidationProblemDetails` (not 422 `INVALID_DATASET_NAME`)." **Do NOT add the FV filter to the POST endpoint.** The inline `DatasetName.TryCreate()` check is the validated path (matching the Designer's `IDENTIFIER_INVALID` pattern). The registered `CreateDatasetRequestValidator` in Program.cs is available for testing and future use, but the filter must not be wired to these Dataset endpoints.

### §8 — Pre-existing test failures to ignore

Per memory:
- 2 failures: `SchemaAuditLogIntegrationTests` / `MutationAuditLogIntegrationTests` (audit DELETE→405) — pre-existing, unrelated
- 1 failure: `i18n-lint.test.ts` (missing `designer.inspector.placeholders.label`) — pre-existing

Expected after this story:
- Backend: all Dataset tests pass, including updated Story 8.3 permission test (501→201 assertion)
- Frontend: 0 new failures (i18n keys added to `en.json` are not yet consumed by components, so `i18n-lint` won't fail on orphans; it only fails on missing keys)

### Project Structure Notes

**New files:**
```
src/FormForge.Api/Features/Datasets/Dtos/DatasetDto.cs              ← NEW
src/FormForge.Api/Features/Datasets/DatasetViewManager.cs           ← NEW
src/FormForge.Api/Features/Datasets/DatasetService.cs               ← NEW
src/FormForge.Api.Tests/Features/Datasets/DatasetViewLifecycleTests.cs ← NEW
```

**Modified files:**
```
src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs  (POST handler — real impl replaces 501 stub)
src/FormForge.Api/Program.cs                             (register DatasetViewManager + IDatasetService)
src/FormForge.Api.Tests/Features/Datasets/DatasetPermissionTests.cs  (update 501→201 assertion)
web/src/lib/i18n/locales/en.json                         (add nameConflict + invalidQuery keys)
```

**No new EF Core migrations** — this story adds no database schema changes.
**No new NuGet packages** — `PgQuery.NET` (for SELECT-only enforcement) is deferred to Story 8.8.

### References

- [Source: `_bmad-output/planning-artifacts/epics.md` — Epic 8, Story 8.4 ACs (FR-58)]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.3 — AR-59: Transactional View Lifecycle]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.9 — AR-65: Dataset API contract, error codes table (INVALID_QUERY 422, DATASET_NAME_CONFLICT 409)]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.1 — AR-57: dataset_name UNIQUE constraint; datasets schema; audit log]
- [Source: `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs` — current stub state; inline validation pattern from Story 8.3]
- [Source: `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs` — `InvalidDatasetName()` helper to mirror for other error shapes]
- [Source: `src/FormForge.Api/Domain/Entities/CustomDataset.cs` — entity fields; version=1 default; created_by nullable FK]
- [Source: `src/FormForge.Api/Domain/Entities/DatasetAuditLogEntry.cs` — audit log fields including actor_name, ddl, succeeded, correlation_id]
- [Source: `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` lines 362–388 — CustomDataset fluent mapping; `idx_custom_dataset_dataset_name` UNIQUE index]
- [Source: `src/FormForge.Api/Infrastructure/Persistence/DbConnectionFactory.cs` — raw NpgsqlConnection; `DdlCommandTimeoutSeconds = 60`]
- [Source: `src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs` lines 800–840 — Dapper + NpgsqlTransaction disposal pattern]
- [Source: `src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs` line 844 — `httpContext.GetCorrelationId()` usage]
- [Source: `src/FormForge.Api/Common/Logging/LogContextExtensions.cs` — `GetCorrelationId()` extension method]
- [Source: `src/FormForge.Api/Common/Endpoints/RouteGroupExtensions.cs` lines 112–113 — `httpContext.User.FindFirst("userId")` pattern]
- [Source: `src/FormForge.Api/Features/Datasets/Validators/DatasetNameValidator.cs` — validator registered explicitly (no assembly scan) per Story 8.3 deviation]
- [Source: `src/FormForge.Api/Program.cs` lines 247–249 — explicit Dataset validator DI registrations]
- [Source: `src/FormForge.Api.Tests/Features/Datasets/DatasetNameValidationTests.cs` — integration test setup, ReseedAdminRoleAsync, SeedAdminUserAsync, LoginAsync helpers]
- [Source: `src/FormForge.Api.Tests/Features/Datasets/DatasetPermissionTests.cs` lines 169–181 — `PostDatasets_Admin_Returns501NotForbidden` test to update]
- [Source: Story 8.3 Dev Agent Record §deviation — validators registered explicitly (no assembly scan)]
- [Source: Story 8.3 Review Findings — FV filter risk (deferred to Story 8.4); UpdateDatasetRequest.Version guard (deferred to Story 8.5); pg_ prefix UX inconsistency (deferred to Story 8.10)]

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-8[1m] (Opus 4.8, 1M context) — bmad-dev-story workflow

### Debug Log References

- Build initially failed on CA1848/CA1873 (the API enforces source-generated
  `LoggerMessage` delegates as errors). Converted `DatasetViewManager` and
  `DatasetService` to `partial` classes with `[LoggerMessage]` methods.
- A long-running dev instance of `FormForge.Api` (PID 35876) locked the build
  output; stopped it (with user approval) to rebuild and run integration tests.

### Completion Notes List

- POST `/api/datasets` now creates the `custom_dataset` row and the backing
  `datasets."<name>"` VIEW inside one `NpgsqlTransaction` (AR-59), then writes the
  audit log via EF after commit/rollback.
- Error contract: 409 `DATASET_NAME_CONFLICT` (23505 from the INSERT, no audit row),
  422 `INVALID_QUERY` (any other PG error from the VIEW DDL, with a failed audit row),
  201 `DatasetDto` + `Location` header on success.
- Null/empty query → placeholder VIEW `SELECT 1 AS placeholder`; the response `query`
  field and the stored `custom_dataset.query` are both `NULL` in that case.
- Inline `DatasetName.TryCreate` is the validated path; the FluentValidation filter was
  deliberately NOT wired to the POST endpoint (§7) to preserve the root `code` envelope.
- Regression fixes: updated two Story 8.3 tests that asserted a valid name returns 501
  (`DatasetPermissionTests.PostDatasets_Admin_Returns201NotForbidden` and
  `DatasetNameValidationTests.PostDataset_ValidName_Returns201NotInvalid`) to expect 201,
  using unique probe names since those classes do not truncate `custom_dataset`.
- Tests: all 75 Dataset tests pass (8 new lifecycle tests covering AC-1…AC-6 + the
  no-audit-on-conflict case). DatasetViewLifecycleTests also drops leftover VIEWs in the
  `datasets` schema during setup to isolate from prior runs.

### File List

**New:**
- `src/FormForge.Api/Features/Datasets/Dtos/DatasetDto.cs`
- `src/FormForge.Api/Features/Datasets/DatasetViewManager.cs`
- `src/FormForge.Api/Features/Datasets/DatasetService.cs`
- `src/FormForge.Api.Tests/Features/Datasets/DatasetViewLifecycleTests.cs`

**Modified:**
- `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs` (real POST handler + usings + class comment)
- `src/FormForge.Api/Program.cs` (register `DatasetViewManager` + `IDatasetService`)
- `src/FormForge.Api.Tests/Features/Datasets/DatasetPermissionTests.cs` (501→201 assertion, unique probe name)
- `src/FormForge.Api.Tests/Features/Datasets/DatasetNameValidationTests.cs` (501→201 assertion, unique probe name)
- `web/src/lib/i18n/locales/en.json` (add `datasets.nameConflict` + `datasets.invalidQuery`)

### Change Log

| Date | Change |
|------|--------|
| 2026-06-03 | Story created — ready-for-dev |
| 2026-06-03 | Implemented create handler (row + VIEW atomic), service, VIEW manager, DTO, audit logging; 8 integration tests; i18n keys; regression fixes. Status → review. |
