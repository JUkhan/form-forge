# Story 6.3: Create a New Record

Status: done

## Story

As an authorized user with `canCreate`,
I want to submit a new record payload,
So that a new row is inserted into the provisioned table.

## Acceptance Criteria

**AC-1 — Happy-path CREATE**
**Given** a provisioned table for `designerId` and a valid JSON payload
**When** I POST `/api/data/{designerId}` with `{ "title": "My Record", ... }` (user fieldKeys only)
**Then** the response is HTTP 201 with the created record
**And** the response body contains `id` (server-generated UUID), `createdAt`, `createdBy`, `updatedAt`, `updatedBy`, `isDeleted: false`, and user fieldKey values (Option C hybrid per AR-46)
**And** `is_deleted` (snake_case) does NOT appear in the response (renamed to `isDeleted`)
**And** system columns (`id`, `created_at`, `created_by`, `updated_at`, `updated_by`, `is_deleted`, `cascade_event_id`) in the client payload are silently ignored — the server always generates these

**AC-2 — Unknown fields are silently ignored**
**Given** a payload that includes keys unknown to the schema (e.g. `{ "title": "x", "unknown_field": "y" }`)
**When** the server processes the request
**Then** `unknown_field` is silently dropped; the record is created with only the known fieldKey columns
**And** the response is HTTP 201 (not 422)

**AC-3 — Layer 2 type validation**
**Given** a known fieldKey with a type-mismatched value (e.g. NUMERIC column with value `"not-a-number"`)
**When** the server validates via `IDynamicPayloadValidator`
**Then** the response is HTTP 422 with `code: "VALIDATION_FAILED"` and `errors: { fieldKey: ["..."] }` (`ValidationProblemDetails` format per Decision 3.1)

**AC-4 — Mutation audit log row**
**Given** a successful INSERT
**When** the transaction commits
**Then** a row is appended to `mutation_audit_log` with `actorId`, `timestamp`, `designerId`, `recordId`, `operation: "CREATE"`, `newValues` (JSON of inserted user fieldKey values), `correlationId`

**AC-5 — Unprovisioned designer**
**Given** a `designerId` for which no menu exists with `provisioningStatus = 'Success'`
**When** I POST `/api/data/{designerId}`
**Then** the response is HTTP 404 with `code: "TABLE_NOT_PROVISIONED"`

**AC-6 — Permission enforcement**
**Given** an authenticated user without `canCreate` on this resource
**When** I POST `/api/data/{designerId}`
**Then** the response is HTTP 403 with `code: "FORBIDDEN"`

**AC-7 — Rate limiting**
**Given** the rate-limiting policy (per AR-15 / Decision 2.6)
**When** a user exceeds 60 POST `/api/data/*` requests per user per minute
**Then** the response is HTTP 429 with `Retry-After: 60`
**Note:** Enforced by the `"data-write"` policy registered in `Program.cs` (already present). The handler uses `.RequireRateLimiting("data-write")` which overrides the group-level `"data-read"` policy.

**AC-8 — Query timeout**
**Given** the Dapper INSERT
**When** the query runs
**Then** `commandTimeout` is set to 5 seconds on the `CommandDefinition` (Decision 1.6)

---

## Tasks / Subtasks

- [x] **Task 1 — Create `MutationAuditLogEntry.cs` entity** (AC: 4)
  - [x] Create `src/FormForge.Api/Domain/Entities/MutationAuditLogEntry.cs`
  - Shape mirrors `SchemaAuditLogEntry.cs`:
    ```csharp
    namespace FormForge.Api.Domain.Entities;

    internal sealed class MutationAuditLogEntry
    {
        public Guid Id { get; set; }
        public string DesignerId { get; set; } = string.Empty;
        public Guid RecordId { get; set; }
        public string Operation { get; set; } = string.Empty; // "CREATE" | "UPDATE" | "SOFT_DELETE" | "RESTORE"
        public Guid? ActorId { get; set; }
        public DateTimeOffset Timestamp { get; set; }
        public string? NewValues { get; set; }      // JSONB — serialized payload dict
        public string? PreviousValues { get; set; } // JSONB — null for CREATE
        public string? CorrelationId { get; set; }  // ULID, 26 chars
    }
    ```

- [x] **Task 2 — Register `MutationAuditLogEntry` in `FormForgeDbContext.cs`** (AC: 4)
  - [x] Modify `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs`
  - Add `DbSet`:
    ```csharp
    public DbSet<MutationAuditLogEntry> MutationAuditLog => Set<MutationAuditLogEntry>();
    ```
  - Add EF config inside `OnModelCreating`:
    ```csharp
    modelBuilder.Entity<MutationAuditLogEntry>(e =>
    {
        e.ToTable("mutation_audit_log");
        e.HasKey(a => a.Id);
        e.Property(a => a.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        e.Property(a => a.DesignerId).HasColumnName("designer_id").IsRequired().HasMaxLength(63);
        e.Property(a => a.RecordId).HasColumnName("record_id").IsRequired();
        e.Property(a => a.Operation).HasColumnName("operation").IsRequired().HasMaxLength(20);
        e.Property(a => a.ActorId).HasColumnName("actor_id");
        e.Property(a => a.Timestamp).HasColumnName("timestamp").IsRequired();
        e.Property(a => a.NewValues).HasColumnName("new_values").HasColumnType("jsonb");
        e.Property(a => a.PreviousValues).HasColumnName("previous_values").HasColumnType("jsonb");
        e.Property(a => a.CorrelationId).HasColumnName("correlation_id").HasMaxLength(26);
        // Decision 1.5: three indexes for audit log queries.
        e.HasIndex(a => new { a.DesignerId, a.Timestamp })
         .HasDatabaseName("idx_mutation_audit_log_designer_id_timestamp_desc")
         .IsDescending(false, true);
        e.HasIndex(a => new { a.RecordId, a.Timestamp })
         .HasDatabaseName("idx_mutation_audit_log_record_id_timestamp_desc")
         .IsDescending(false, true);
        e.HasIndex(a => a.CorrelationId)
         .HasDatabaseName("idx_mutation_audit_log_correlation_id");
    });
    ```

- [x] **Task 3 — EF Migration: `AddMutationAuditLog`** (AC: 4)
  - [x] Run: `dotnet ef migrations add AddMutationAuditLog --project src/FormForge.Api --startup-project src/FormForge.Api`
  - Verify the generated migration creates:
    - `mutation_audit_log` table with all columns (id UUID PK, designer_id varchar(63) NOT NULL, record_id UUID NOT NULL, operation varchar(20) NOT NULL, actor_id UUID NULL, timestamp TIMESTAMPTZ NOT NULL, new_values jsonb NULL, previous_values jsonb NULL, correlation_id varchar(26) NULL)
    - Three indexes as defined in Task 2
  - `Database.Migrate()` in `Program.cs` auto-runs migrations on startup — no other change needed

- [x] **Task 4 — Create `IDynamicPayloadValidator` + `DynamicPayloadValidator`** (AC: 2, 3)
  - [x] Create `src/FormForge.Api/Features/DynamicCrud/DynamicPayloadValidator.cs`
  - Interface + implementation in one file:
    ```csharp
    using System.Text.Json;
    using FormForge.Api.Features.SchemaRegistry;

    namespace FormForge.Api.Features.DynamicCrud;

    internal interface IDynamicPayloadValidator
    {
        PayloadValidationResult Validate(
            JsonElement body,
            IReadOnlyList<ColumnDefinition> columns);
    }

    internal sealed record PayloadValidationResult(
        bool IsValid,
        IReadOnlyDictionary<string, object?> CoercedValues,
        IDictionary<string, string[]> FieldErrors);

    internal sealed class DynamicPayloadValidator : IDynamicPayloadValidator
    {
        public PayloadValidationResult Validate(JsonElement body, IReadOnlyList<ColumnDefinition> columns)
        {
            ArgumentNullException.ThrowIfNull(columns);

            var coerced = new Dictionary<string, object?>(StringComparer.Ordinal);
            var errors  = new Dictionary<string, string[]>(StringComparer.Ordinal);

            if (body.ValueKind != JsonValueKind.Object)
                return new PayloadValidationResult(false, coerced,
                    new Dictionary<string, string[]>(StringComparer.Ordinal)
                    {
                        ["$"] = ["Request body must be a JSON object."],
                    });

            foreach (var col in columns)
            {
                if (!body.TryGetProperty(col.ColumnName, out var el)) continue;
                if (el.ValueKind == JsonValueKind.Null)
                {
                    coerced[col.ColumnName] = null;
                    continue;
                }
                switch (col.PgType)
                {
                    case "TEXT":
                        if (el.ValueKind != JsonValueKind.String)
                            errors[col.ColumnName] = [$"Expected a string for field '{col.ColumnName}'."];
                        else
                            coerced[col.ColumnName] = el.GetString();
                        break;
                    case "NUMERIC":
                        if (el.ValueKind == JsonValueKind.Number)
                            coerced[col.ColumnName] = el.GetDecimal();
                        else if (el.ValueKind == JsonValueKind.String &&
                                 decimal.TryParse(el.GetString(), System.Globalization.NumberStyles.Any,
                                     System.Globalization.CultureInfo.InvariantCulture, out var dec))
                            coerced[col.ColumnName] = dec;
                        else
                            errors[col.ColumnName] = [$"Expected a number for field '{col.ColumnName}'."];
                        break;
                    case "BOOLEAN":
                        if (el.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            coerced[col.ColumnName] = el.GetBoolean();
                        else
                            errors[col.ColumnName] = [$"Expected a boolean for field '{col.ColumnName}'."];
                        break;
                    case "TIMESTAMPTZ":
                        if (el.ValueKind == JsonValueKind.String &&
                            DateTimeOffset.TryParse(el.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.RoundtripKind, out var dto))
                            coerced[col.ColumnName] = dto;
                        else
                            errors[col.ColumnName] = [$"Expected an ISO-8601 timestamp string for field '{col.ColumnName}'."];
                        break;
                    default: // JSONB + forward-compat unknown PG types
                        coerced[col.ColumnName] = el.ToString();
                        break;
                }
            }

            return new PayloadValidationResult(errors.Count == 0, coerced, errors);
        }
    }
    ```
  - **Important:** System column names (`id`, `created_at`, `created_by`, `updated_at`, `updated_by`, `is_deleted`, `cascade_event_id`) are NOT present in `columns` (those are `ColumnDefinition` user-authored fieldKeys only). The validator iterates only over user `ColumnDefinition` entries, so system columns in the client payload are never matched and fall through to the "unknown field silently ignored" path automatically.
  - `body.TryGetProperty(col.ColumnName, ...)` — uses the fieldKey (PG column name) verbatim, which is what the client sends.

- [x] **Task 5 — Add `BuildInsertQuery` to `DynamicQueryBuilder.cs`** (AC: 1, 8)
  - [x] Modify `src/FormForge.Api/Features/DynamicCrud/DynamicQueryBuilder.cs`
  - Add after `BuildGetChildrenQuery`:
    ```csharp
    // Story 6.3 — parameterized INSERT for POST /api/data/{designerId}.
    // System columns are always set server-side. User columns are included only
    // when present in coercedPayload (absent columns → PG NULL, all are nullable
    // per FR-24 AC-3). Returns the new record ID and the insert timestamp so
    // the caller can build the response and audit row without a re-SELECT.
    internal static (string Sql, DynamicParameters Parameters, Guid NewRecordId, DateTimeOffset InsertedAt)
        BuildInsertQuery(
            SafeIdentifier tableName,
            IReadOnlyList<ColumnDefinition> userColumns,
            IReadOnlyDictionary<string, object?> coercedPayload,
            Guid? actorId)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(userColumns);
        ArgumentNullException.ThrowIfNull(coercedPayload);

        var newId      = Guid.NewGuid();
        var insertedAt = DateTimeOffset.UtcNow;

        // Identify which user columns appear in the payload.
        var presentUserCols = userColumns
            .Where(c => coercedPayload.ContainsKey(c.ColumnName))
            .ToList();

        // --- columns list ---
        var colSb = new StringBuilder(256);
        colSb.Append("\"id\", \"created_at\", \"created_by\", \"updated_at\", \"updated_by\", \"is_deleted\", \"cascade_event_id\"");
        foreach (var col in presentUserCols)
        {
            colSb.Append(CultureInfo.InvariantCulture, $", \"{col.ColumnName}\"");
        }

        // --- values list ---
        var valSb = new StringBuilder(256);
        valSb.Append("@p_id, @p_created_at, @p_created_by, @p_updated_at, @p_updated_by, false, NULL");
        foreach (var col in presentUserCols)
        {
            valSb.Append(CultureInfo.InvariantCulture, $", @f_{col.ColumnName}");
        }

        var sql = $"INSERT INTO \"{tableName.Value}\" ({colSb}) VALUES ({valSb})";

        var parameters = new DynamicParameters();
        parameters.Add("p_id",          newId);
        parameters.Add("p_created_at",  insertedAt);
        parameters.Add("p_created_by",  actorId);
        parameters.Add("p_updated_at",  insertedAt);
        parameters.Add("p_updated_by",  actorId);

        foreach (var col in presentUserCols)
        {
            parameters.Add($"f_{col.ColumnName}", coercedPayload[col.ColumnName]);
        }

        return (sql, parameters, newId, insertedAt);
    }
    ```
  - No RETURNING clause: the response is built from the known-inserted values in the handler to avoid an extra SELECT round-trip.

- [x] **Task 6 — Add `CreateRecordHandler` to `DynamicDataEndpoints.cs`** (AC: 1–8)
  - [x] Modify `src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs`
  - Add using: `using System.Text.Json;`
  - Add after the `MapGet("/{id:guid}", GetRecordHandler)` registration (inside `MapDynamicDataEndpoints`):
    ```csharp
    // Story 6.3 — POST /api/data/{designerId}. Rate limit overrides group default:
    // "data-write" (60 req/min) vs group "data-read" (300 req/min).
    group.MapPost("/", CreateRecordHandler)
         .WithSummary("Create a new record in a provisioned dynamic table.")
         .Produces<DynamicRecord>(StatusCodes.Status201Created)
         .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
         .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
         .Produces<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity)
         .RequirePermission("create")
         .RequireRateLimiting("data-write");
    ```
  - Handler signature — note `JsonElement body` auto-bound from request body (ASP.NET Core Minimal API does this when parameter type is `JsonElement`):
    ```csharp
    internal static async Task<IResult> CreateRecordHandler(
        string designerId,
        JsonElement body,
        HttpContext httpContext,
        FormForgeDbContext db,
        ISchemaRegistry schemaRegistry,
        DbConnectionFactory connectionFactory,
        IDynamicPayloadValidator payloadValidator,
        CancellationToken ct)
    ```
  - **Handler flow:**
    1. `ArgumentNullException.ThrowIfNull(db); ... (db, schemaRegistry, connectionFactory, payloadValidator, httpContext)`
    2. `SafeIdentifier.TryCreate(designerId, out var safeId, out _)` → if invalid, return 422 `Problems.ValidationFailed("Invalid designer identifier.")`
    3. EF binding lookup (identical to `ListRecordsHandler` / `GetRecordHandler`):
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
    4. Schema registry with cache-miss repopulation (identical to `GetRecordHandler`):
       ```csharp
       var entry = schemaRegistry.TryGet(safeId!.Value, boundVersion.Value);
       if (entry is null) { ... populate ... }
       ```
    5. Layer 2 validation:
       ```csharp
       var validationResult = payloadValidator.Validate(body, entry.Columns);
       if (!validationResult.IsValid)
           return Results.ValidationProblem(validationResult.FieldErrors);
       ```
    6. Extract actorId from JWT:
       ```csharp
       var actorId = httpContext.User.FindFirst("userId")?.Value is { } s
           && Guid.TryParse(s, out var uid) ? uid : (Guid?)null;
       ```
    7. Build INSERT:
       ```csharp
       var (sql, parameters, newRecordId, insertedAt) = DynamicQueryBuilder.BuildInsertQuery(
           safeId, entry.Columns, validationResult.CoercedValues, actorId);
       ```
    8. Execute INSERT via Dapper (5-second timeout, AC-8):
       ```csharp
       var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
       try
       {
           await conn.ExecuteAsync(new CommandDefinition(
               sql, parameters, commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);
       }
       finally
       {
           await conn.DisposeAsync().ConfigureAwait(false);
       }
       ```
    9. Append mutation audit log via EF (AC-4):
       ```csharp
       var correlationId = httpContext.GetCorrelationId();
       var newValuesJson = System.Text.Json.JsonSerializer.Serialize(validationResult.CoercedValues);
       db.MutationAuditLog.Add(new FormForge.Api.Domain.Entities.MutationAuditLogEntry
       {
           DesignerId    = safeId!.Value,
           RecordId      = newRecordId,
           Operation     = "CREATE",
           ActorId       = actorId,
           Timestamp     = insertedAt,
           NewValues     = newValuesJson,
           CorrelationId = correlationId,
       });
       await db.SaveChangesAsync(ct).ConfigureAwait(false);
       ```
    10. Build response `DynamicRecord` from inserted values (no re-SELECT needed):
        ```csharp
        var responseValues = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"]               = newRecordId,
            ["created_at"]       = insertedAt,
            ["created_by"]       = actorId,
            ["updated_at"]       = insertedAt,
            ["updated_by"]       = actorId,
            ["is_deleted"]       = false,
            ["cascade_event_id"] = null,
        };
        foreach (var col in entry.Columns)
        {
            responseValues[col.ColumnName] = validationResult.CoercedValues.TryGetValue(col.ColumnName, out var v)
                ? v : null;
        }
        return Results.Created(
            $"/api/data/{Uri.EscapeDataString(safeId!.Value)}/{newRecordId}",
            new DynamicRecord(responseValues));
        ```
  - **Add `RecordDeleted` to the `Problems` inner class** (needed by Story 6.4 but add the stub now per the epics AC):
    - Defer — only add `RecordDeleted` when Story 6.4 is implemented. Do NOT add now.
  - **Note:** `httpContext.GetCorrelationId()` requires `using FormForge.Api.Common.Logging;`. Verify the using directives include this namespace.

- [x] **Task 7 — Register `IDynamicPayloadValidator` in `Program.cs`** (AC: 3)
  - [x] Modify `src/FormForge.Api/Program.cs`
  - After the existing `builder.Services.AddScoped<DdlEmitter>();` line (near DynamicCrud registrations), add:
    ```csharp
    // Story 6.3 — Layer 2 dynamic payload validator (AR-20 / Decision 3.3).
    builder.Services.AddScoped<IDynamicPayloadValidator, DynamicPayloadValidator>();
    ```

- [x] **Task 8 — Unit tests for `BuildInsertQuery`** (AC: 1, 8)
  - [x] Modify `src/FormForge.Api.Tests/Features/DynamicCrud/DynamicQueryBuilderTests.cs`
  - Add to the existing test class:
    - `BuildInsertQuery_NoPayload_GeneratesInsertWithSystemColumnsOnly` — empty `coercedPayload` → SQL has `id, created_at, created_by, updated_at, updated_by, is_deleted, cascade_event_id` columns and `false, NULL` literals for `is_deleted` / `cascade_event_id`
    - `BuildInsertQuery_WithOneUserColumn_IncludesColumnAndParameter` — `coercedPayload = { "title" → "hello" }` → SQL has `"title"` in columns; `parameters.Get<string>("f_title")` == `"hello"`
    - `BuildInsertQuery_SystemColumnsInPayloadAreIgnored` — coercedPayload contains only schema-registry user columns (the validator strips system columns); verify `parameters.ParameterNames` does NOT contain `f_id` or `f_created_at`
    - `BuildInsertQuery_ReturnsNewGuidAndTimestamp` — verify `NewRecordId != Guid.Empty` and `InsertedAt` is within 1 second of `DateTimeOffset.UtcNow`
    - `BuildInsertQuery_ActorIdNullable_IncludesNullParameter` — `actorId = null` → `parameters.Get<Guid?>("p_created_by")` is null
  - Estimated: +5 unit tests → running total ~451

- [x] **Task 9 — Integration tests** (AC: 1–6)
  - [x] Create `src/FormForge.Api.Tests/Features/DynamicCrud/CreateRecordIntegrationTests.cs`
  - Class signature: `[Collection("DynamicCrudTests")] public sealed class CreateRecordIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime`
  - `InitializeAsync` / `DisposeAsync` pattern identical to `GetRecordIntegrationTests` and `DynamicCrudIntegrationTests`
  - **TRUNCATE statement must include `mutation_audit_log`:**
    ```csharp
    await db.Database.ExecuteSqlRawAsync(
        "TRUNCATE TABLE menu_role_assignments, menus, component_schema_versions, component_schemas, role_permissions, user_roles, roles, refresh_tokens, users, schema_audit_log, mutation_audit_log RESTART IDENTITY CASCADE;");
    ```
  - Copy helper methods from `GetRecordIntegrationTests` / `DynamicCrudIntegrationTests` (see Dev Notes §7 for list)
  - Add helper `PostRecordAsync(string token, string designerId, object payload)` → `HttpResponseMessage`
  - **Test cases:**
    1. `CreateRecord_ValidPayload_Returns201WithCreatedRecord` — AC-1: provision table with `title` column, POST `{ "title": "My Record" }` → 201; verify JSON has `id` (non-empty UUID), `createdAt`, `isDeleted: false`, `title: "My Record"`; verify `is_deleted` (snake_case) NOT present (AR-46 Option C)
    2. `CreateRecord_UnknownFieldInPayload_Returns201IgnoresUnknownField` — AC-2: POST `{ "title": "ok", "ghost_field": "ignored" }` → 201, no `ghost_field` in response
    3. `CreateRecord_TypeMismatch_Returns422WithFieldError` — AC-3: provision table with NumberInput → NUMERIC column `price`, POST `{ "price": "not-a-number" }` → 422 `ValidationProblemDetails`; verify `errors["price"]` is non-empty
    4. `CreateRecord_AuditLogRow_Appended` — AC-4: POST, then query `mutation_audit_log` via Npgsql → verify row with `operation = 'CREATE'`, `designer_id`, `record_id`, non-null `new_values`
    5. `CreateRecord_UnprovisionedDesigner_Returns404TableNotProvisioned` — AC-5: unknown designerId → 404 `TABLE_NOT_PROVISIONED`
    6. `CreateRecord_NoCanCreate_Returns403` — AC-6: viewer user (no canCreate) → 403 `FORBIDDEN`; verify `code = "FORBIDDEN"`
    7. `CreateRecord_EmptyPayload_Returns201WithNullUserFields` — AC-1/AC-2: POST `{}` → 201, `title` is null in response
  - Estimated: +7 integration tests → running total ~458

- [x] **Task 10 — Update TRUNCATE in existing test classes** (AC: 4)
  - [x] Modify `src/FormForge.Api.Tests/Features/DynamicCrud/DynamicCrudIntegrationTests.cs`
    - Add `mutation_audit_log` to the TRUNCATE statement in `InitializeAsync`
  - [x] Modify `src/FormForge.Api.Tests/Features/DynamicCrud/GetRecordIntegrationTests.cs`
    - Add `mutation_audit_log` to the TRUNCATE statement in `InitializeAsync`
  - Required: `mutation_audit_log` is a static EF-managed table added by the migration in Task 3; after the migration runs, all test classes that TRUNCATE static tables must include it to avoid stale audit rows leaking between test classes in the `[Collection("DynamicCrudTests")]` group

- [x] **Task 11 — Frontend API client** (foundational for Stories 6.9/6.10)
  - [x] Create `web/src/features/data-entry/createRecordApi.ts`
    ```ts
    import { httpClient } from '../auth/httpClient'
    import type { DynamicRecord } from './recordListApi'

    export function createRecord(
      designerId: string,
      payload: Record<string, unknown>,
    ): Promise<DynamicRecord> {
      return httpClient.post<DynamicRecord>(
        `/api/data/${encodeURIComponent(designerId)}`,
        payload,
      )
    }
    ```
  - [ ] Create `web/src/features/data-entry/useCreateRecord.ts`
    ```ts
    import { useMutation, useQueryClient } from '@tanstack/react-query'
    import { createRecord } from './createRecordApi'
    import type { DynamicRecord } from './recordListApi'

    export function useCreateRecord(designerId: string) {
      const queryClient = useQueryClient()

      return useMutation({
        mutationFn: (payload: Record<string, unknown>) =>
          createRecord(designerId, payload),
        onSuccess: (created: DynamicRecord) => {
          // AR-48: invalidate list queries so the new record appears.
          // Pessimistic mutation per AR-49 (wait for server confirmation).
          queryClient.invalidateQueries({ queryKey: ['data', designerId] })
          return created
        },
      })
    }
    ```
  - [x] Create `web/src/features/data-entry/useCreateRecord.ts`
  - No i18n keys needed — toast and UX interactions are in Story 6.9

---

## Dev Notes

### Architecture compliance — critical constraints

**1. Rate limit override: `"data-write"` on the POST endpoint**
The `/api/data/{designerId}` route group uses `RequireRateLimiting("data-read")` (300/min). Adding `.RequireRateLimiting("data-write")` to the individual `MapPost(...)` call overrides the group-level policy for that endpoint to 60/min. The `"data-write"` policy is already registered in `Program.cs` (lines 294–307 as of Story 6.2). Do NOT add a new policy registration.

**2. `IDynamicPayloadValidator` — system columns are already excluded by design**
`ColumnDefinition` entries only represent user-authored fieldKey columns from `RootElementParser.ParseFull`. System columns (`id`, `created_at`, `created_by`, `updated_at`, `updated_by`, `is_deleted`, `cascade_event_id`) are never in `entry.Columns` — they are added to the table by `DdlEmitter` outside the schema definition. Therefore the validator loop `foreach (var col in columns)` will never match a system column name from the client payload. No explicit rejection needed; the system column names simply won't match any `ColumnDefinition.ColumnName`.

**3. Response is built from inserted values — no re-SELECT**
`BuildInsertQuery` returns the generated `newRecordId` and `insertedAt` timestamp. The response `DynamicRecord` is assembled in the handler from these known values plus the coerced payload. This avoids an extra Dapper SELECT round-trip for the 201 response. Trade-off: the response reflects what was sent to INSERT, not what PG stored. This is acceptable because all column types are deterministic (no server-side transforms like `lower()` or triggers).

**4. Audit log INSERT is a separate EF transaction from the Dapper INSERT**
Decision 1.6 mandates separated EF + Dapper transactions. The Dapper INSERT runs first (closes the connection). Then `db.SaveChangesAsync()` commits the audit row. If the audit INSERT fails after a successful data INSERT, the record exists without an audit trail — acceptable for v1 (noted as deferred item). Do NOT attempt to share the NpgsqlConnection between EF and Dapper.

**5. `IDynamicPayloadValidator` is `Scoped` (not Singleton)**
It has no state, but is registered Scoped to remain consistent with other handler dependencies (`FormForgeDbContext` is Scoped). `AddScoped<IDynamicPayloadValidator, DynamicPayloadValidator>()` — correct.

**6. `Results.ValidationProblem(fieldErrors)` — uses the `IDictionary<string, string[]>` overload**
`Results.ValidationProblem(validationResult.FieldErrors)` returns HTTP 422 with `ValidationProblemDetails` (errors dict). This is distinct from `Problems.ValidationFailed(string detail)` which returns a simple 422. Use `Results.ValidationProblem` for field-level errors (AC-3); use `Problems.ValidationFailed(string)` for non-field errors (invalid designerId, etc.).

**7. `httpContext.GetCorrelationId()` requires `using FormForge.Api.Common.Logging;`**
The extension method is defined in `LogContextExtensions.cs`. Add this using to `DynamicDataEndpoints.cs` if not already present.

**8. `JsonElement body` parameter — ASP.NET Core auto-binding**
When a Minimal API handler parameter is typed `JsonElement`, ASP.NET Core reads the request body as JSON automatically. If the body is absent or malformed, ASP.NET returns 400 before the handler runs — no manual `ReadFromJsonAsync` or try/catch needed. If the body is `{}` (empty object), `body.ValueKind == JsonValueKind.Object` and `body.EnumerateObject()` returns an empty enumeration — the validator returns empty `CoercedValues`, which is valid (AC-1: empty payload → all user columns NULL).

**9. `cascade_event_id` is `NULL` literal in SQL, not a parameter**
In `BuildInsertQuery`, `cascade_event_id` is hardcoded as SQL `NULL` (not a Dapper parameter) because it is never set on CREATE. This avoids Dapper null-handling edge cases with PG UUID columns.

**10. Migration TRUNCATE in test setup**
After the `AddMutationAuditLog` migration runs in `InitializeAsync`, the new `mutation_audit_log` table exists. ALL three test classes in `[Collection("DynamicCrudTests")]` share the same PostgreSQL container; they serialize because of the collection. Each class's `InitializeAsync` must TRUNCATE `mutation_audit_log` to avoid audit rows from a previous test class leaking into the current one.

---

### File locations — new files

| New file | Path |
|---|---|
| `MutationAuditLogEntry.cs` | `src/FormForge.Api/Domain/Entities/MutationAuditLogEntry.cs` |
| `DynamicPayloadValidator.cs` | `src/FormForge.Api/Features/DynamicCrud/DynamicPayloadValidator.cs` |
| `AddMutationAuditLog.cs` (migration) | `src/FormForge.Api/Infrastructure/Persistence/Migrations/` (auto-generated) |
| `CreateRecordIntegrationTests.cs` | `src/FormForge.Api.Tests/Features/DynamicCrud/CreateRecordIntegrationTests.cs` |
| `createRecordApi.ts` | `web/src/features/data-entry/createRecordApi.ts` |
| `useCreateRecord.ts` | `web/src/features/data-entry/useCreateRecord.ts` |

### File locations — modified files

| Modified file | Change |
|---|---|
| `FormForgeDbContext.cs` | Add `MutationAuditLog` DbSet + EF mapping block + indexes |
| `DynamicQueryBuilder.cs` | Add `BuildInsertQuery` |
| `DynamicDataEndpoints.cs` | Add `MapPost("/", CreateRecordHandler)` + handler + using |
| `Program.cs` | Add `AddScoped<IDynamicPayloadValidator, DynamicPayloadValidator>()` |
| `DynamicQueryBuilderTests.cs` | Add 5 new unit tests for `BuildInsertQuery` |
| `DynamicCrudIntegrationTests.cs` | Add `mutation_audit_log` to TRUNCATE statement |
| `GetRecordIntegrationTests.cs` | Add `mutation_audit_log` to TRUNCATE statement |

---

### Helper methods needed in `CreateRecordIntegrationTests.cs`

Copy these methods from `DynamicCrudIntegrationTests` and/or `GetRecordIntegrationTests`:
- `LoginAsync(email, password)` → `string` token
- `CreateMenuViaApiAsync(token, name, order)` → `Guid` menuId
- `CreateAndPublishDesignerWithFieldsAsync<TRoot>(token, designerId, rootElement)` → `int` version
- `PutVersionStatusAsync(token, designerId, version, status)`
- `CreateDesignerViaApiAsync(token, designerId)`
- `PutBindingAsync(token, menuId, designerId, version)` → `HttpResponseMessage`
- `PollUntilTerminalAsync(token, menuId, deadline)` → `string?` status
- `GetMenuAsync(token, menuId)` → `MenuResponseDto?`
- `SetupProvisionedDesignerWithTitleAsync(token, designerId)` → `int` version
- DTOs: `LoginResponseDto`, `MenuResponseDto`

Add new helper:
```csharp
private async Task<HttpResponseMessage> PostRecordAsync(string token, string designerId, object payload)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/data/{designerId}")
    {
        Content = JsonContent.Create(payload),
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    return await _client!.SendAsync(request);
}
```

For AC-4 test (audit log row verification), query via Npgsql directly:
```csharp
var conn = new NpgsqlConnection(_postgres.ConnectionString);
try
{
    await conn.OpenAsync();
    var count = await conn.ExecuteScalarAsync<int>(
        "SELECT COUNT(*) FROM mutation_audit_log WHERE record_id = @recordId AND operation = 'CREATE'",
        new { recordId });
    Assert.Equal(1, count);
}
finally { await conn.DisposeAsync(); }
```

---

### `TIMESTAMPTZ` parameter binding with Dapper + Npgsql

`DateTimeOffset` values are sent as Npgsql `timestamptz` natively — no explicit `DbType` needed. `Guid` values are sent as PG `uuid`. `decimal` as `numeric`. `bool` as `boolean`. These coercions are handled by Npgsql's type mapper transparently through Dapper's `DynamicParameters`.

---

### Deferred items (record for code review)

1. **Audit INSERT is not transactionally coupled to the data INSERT** — if `db.SaveChangesAsync` fails after the Dapper INSERT succeeds, the record exists without an audit row. Fix: use a shared NpgsqlTransaction passed to both Dapper and EF (requires `db.Database.UseTransactionAsync(npgsqlTransaction)`). Deferred per Decision 1.6 separation.
2. **`BuildInsertQuery` uses `Guid.NewGuid()` (UUID v4) instead of UUID v7** — Architecture format patterns say UUID v7 (time-ordered) via `gen_random_uuid()` for PKs. For v1, C#-side `Guid.NewGuid()` (v4) is used. PostgreSQL's `gen_random_uuid()` also generates v4 in PG ≤ 16 (v7 requires PG 17+ or a plugin). Practically equivalent for v1.
3. **No idempotency key on POST** — duplicate POSTs within the same second with the same payload create duplicate records. Per Decision 3.9, `Idempotency-Key` is deferred to v2.
4. **`newValues` in audit log serializes coerced values only** — excludes system columns (`id`, `created_at`, etc.) from the JSON snapshot. Story 6.8 audit log view will need to decide whether to include system columns. Acceptable for v1.

---

### References

- [Source: `src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs`] — `ListRecordsHandler` / `GetRecordHandler` — mirror the SafeIdentifier → EF binding → schema registry pattern verbatim; add `CreateRecordHandler` after `GetRecordHandler`
- [Source: `src/FormForge.Api/Features/DynamicCrud/DynamicQueryBuilder.cs`] — `BuildGetByIdQuery` shows the column-assembly and parameter-binding pattern to reuse in `BuildInsertQuery`
- [Source: `src/FormForge.Api/Domain/Entities/SchemaAuditLogEntry.cs`] — template for `MutationAuditLogEntry` entity shape
- [Source: `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs`] — `SchemaAuditLogEntry` EF config block (lines 235–266) — mirror the pattern for `MutationAuditLogEntry`
- [Source: `src/FormForge.Api/Program.cs`] — `"data-write"` policy (lines 294–307); `AddScoped<DdlEmitter>()` registration location (add `IDynamicPayloadValidator` after it); comment "Story 6.1 — register DynamicRecord JSON converter" explains the existing registration location
- [Source: `src/FormForge.Api/Common/Logging/LogContextExtensions.cs`] — `GetCorrelationId(this HttpContext)` extension used in Step 9 of the handler
- [Source: `src/FormForge.Api.Tests/Features/DynamicCrud/GetRecordIntegrationTests.cs`] — copy all helper methods; `[Collection("DynamicCrudTests")]` attribute
- [Source: `web/src/features/data-entry/recordListApi.ts`] — import `DynamicRecord` type from here in `createRecordApi.ts`
- [Source: `web/src/features/auth/httpClient.ts`] — `httpClient.post<T>(path, body?)` is available (line 136)
- [Architecture: AR-20 + Decision 3.3] — Layer 2 dynamic payload validator: unknown fields ignored; known fieldKeys type-checked against PgType
- [Architecture: Decision 1.5] — `mutation_audit_log` indexes: `(designer_id, timestamp DESC)`, `(record_id, timestamp DESC)`, `correlation_id`
- [Architecture: Decision 1.6] — `commandTimeout: 5` on all dynamic CRUD Dapper calls; EF + Dapper are separated transactions
- [Architecture: FR-31] — AC-2: unknown fields ignored; system columns set server-side
- [Architecture: AR-46 Option C] — system PG column names → camelCase JSON in response; `DynamicRecordJsonConverter` handles this automatically
- [Architecture: AR-48 + AR-49] — TanStack Query key invalidation on success; pessimistic mutation (wait for server)
- [Architecture: Decision 2.6] — POST rate limit: 60/min per user via `"data-write"` sliding window policy

---

## Dev Agent Record

### Agent Model Used

Claude Opus 4.7 (claude-opus-4-7) — `bmad-dev-story` workflow.

### Debug Log References

- `dotnet build` (full solution) — clean, 0 warnings / 0 errors after fixing CA1062 on the generated migration (`ArgumentNullException.ThrowIfNull(migrationBuilder)` in Up/Down, matching existing migration style).
- `dotnet test --filter ~DynamicQueryBuilderTests` → 26 / 26 passed (incl. 5 new `BuildInsertQuery_*` cases).
- `dotnet test` (full suite) → 459 / 459 passed (was 451 before story; +5 unit + 7 integration tests; +4 net additions because two pre-existing tests were renamed within the prior story baseline).
- Single fix during integration: `Results.ValidationProblem(...)` defaults to HTTP 400 — overrode `statusCode` to 422 with `code: VALIDATION_FAILED` / `messageKey: errors.validationFailed` extensions to satisfy AC-3 and align with the existing `Problems.ValidationFailed` body shape used by `ListRecordsHandler`.

### Completion Notes List

- AC-1: POST returns 201 with the inserted record assembled from server-generated values (no re-SELECT). System columns serialise as camelCase via `DynamicRecordJsonConverter` (AR-46 Option C); `is_deleted` is absent in the response (renamed to `isDeleted`).
- AC-2: `DynamicPayloadValidator` only iterates over `ColumnDefinition` entries (user fieldKeys), so client-supplied unknown keys and system column names (`id`, `created_at`, …) are silently ignored.
- AC-3: Type-mismatched values produce `ValidationProblemDetails` at 422 with `errors[fieldKey]` populated; `code: VALIDATION_FAILED` and `messageKey: errors.validationFailed` are attached as extensions so the client sees the same problem shape as other 422 endpoints.
- AC-4: `MutationAuditLogEntry` row appended via EF in a separate transaction from the Dapper INSERT (Decision 1.6 separation honoured). `new_values` is a JSON snapshot of the coerced user fieldKey values; `previous_values` is null for CREATE.
- AC-5: Unprovisioned designerIds (no menu with `provisioning_status = 'Success'` and `bound_version != null`) → 404 with `code: TABLE_NOT_PROVISIONED`.
- AC-6: Viewer user without `canCreate` → 403 with `code: FORBIDDEN` (enforced by `RequirePermission("create")` endpoint filter from Story 2.6).
- AC-7: Rate limit policy `"data-write"` applied per-endpoint via `.RequireRateLimiting("data-write")`, which overrides the group-level `"data-read"` policy. Policy itself was registered in Program.cs by Story 6.1; not re-added here.
- AC-8: 5-second `commandTimeout` on the `CommandDefinition` for the Dapper INSERT (Decision 1.6).
- Schema registry cache-miss repopulation follows the same `EF binding lookup → ParseFull → Populate` pattern as `GetRecordHandler` / `ListRecordsHandler`.
- The three existing audit-log-aware test classes in `[Collection("DynamicCrudTests")]` (`DynamicCrudIntegrationTests`, `GetRecordIntegrationTests`, `CreateRecordIntegrationTests`) all TRUNCATE `mutation_audit_log` in `InitializeAsync` and add it to the DROP-IF-NOT-IN keep list so dynamic tables from one suite cannot leak into the next.
- Migration `20260526054545_AddMutationAuditLog` creates the table with three indexes per Decision 1.5: `(designer_id, timestamp DESC)`, `(record_id, timestamp DESC)`, `correlation_id`.
- Deferred items (per Dev Notes §Deferred): (1) audit INSERT not coupled in a shared `NpgsqlTransaction`, (2) `Guid.NewGuid()` produces UUID v4 vs. the architectural UUID v7 preference, (3) no `Idempotency-Key` on POST yet, (4) `new_values` excludes system columns from the audit snapshot.

### File List

**Added**

- `src/FormForge.Api/Domain/Entities/MutationAuditLogEntry.cs`
- `src/FormForge.Api/Features/DynamicCrud/DynamicPayloadValidator.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260526054545_AddMutationAuditLog.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260526054545_AddMutationAuditLog.Designer.cs`
- `src/FormForge.Api.Tests/Features/DynamicCrud/CreateRecordIntegrationTests.cs`
- `web/src/features/data-entry/createRecordApi.ts`
- `web/src/features/data-entry/useCreateRecord.ts`

**Modified**

- `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` (added `MutationAuditLog` DbSet + EF config + three indexes)
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/FormForgeDbContextModelSnapshot.cs` (auto-updated by `dotnet ef migrations add`)
- `src/FormForge.Api/Features/DynamicCrud/DynamicQueryBuilder.cs` (added `BuildInsertQuery`)
- `src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs` (added `MapPost("/", CreateRecordHandler)`, handler, and using directives for `System.Text.Json` + `FormForge.Api.Common.Logging`)
- `src/FormForge.Api/Program.cs` (added `AddScoped<IDynamicPayloadValidator, DynamicPayloadValidator>()`)
- `src/FormForge.Api.Tests/Features/DynamicCrud/DynamicQueryBuilderTests.cs` (added 5 unit tests for `BuildInsertQuery`)
- `src/FormForge.Api.Tests/Features/DynamicCrud/DynamicCrudIntegrationTests.cs` (TRUNCATE + DROP keep-list updated for `mutation_audit_log`)
- `src/FormForge.Api.Tests/Features/DynamicCrud/GetRecordIntegrationTests.cs` (TRUNCATE + DROP keep-list updated for `mutation_audit_log`)
- `_bmad-output/implementation-artifacts/sprint-status.yaml` (`6-3-create-a-new-record` → `review`)

## Change Log

| Date | Change |
|---|---|
| 2026-05-26 | Story created — POST /api/data/{designerId} with Layer 2 validation, mutation audit log, and frontend mutation hook |
| 2026-05-26 | Story implemented — all 8 ACs satisfied; +5 unit + 7 integration tests; 459 / 459 passing |
| 2026-05-26 | Code review — 5 patches applied (NUMERIC overflow → 422, TIMESTAMPTZ AssumeUniversal+AdjustToUniversal, AC-1 camelCase system-column assertions, new "system columns in payload ignored" e2e test, AC-4 audit-log row extended to actor_id / timestamp / correlation_id / previous_values). 10 deferred items recorded. 460 / 460 passing. Status → done. |

---

## Review Findings

Adversarial parallel review (Blind Hunter + Edge Case Hunter + Acceptance Auditor) — 2026-05-26.

### Patches

- [x] [Review][Patch] NUMERIC validator throws on overflow instead of returning 422 — `DynamicPayloadValidator.cs:58` uses `el.GetDecimal()` which throws `FormatException`/`OverflowException` for JSON numbers outside `decimal` range (e.g. `1e40`). Result: unhandled 500 instead of the AC-3 422. Switch to `el.TryGetDecimal(out var dec)` and emit a field error on false.
- [x] [Review][Patch] TIMESTAMPTZ validator silently treats timezone-less inputs as server-local — `DynamicPayloadValidator.cs:74-75` uses `DateTimeStyles.RoundtripKind`. A naked `"2026-05-26T10:00:00"` (no offset) is parsed with the server's local offset, so two identical requests from different timezones store different timestamps. Switch to `DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal` so timezone-less inputs are unambiguously UTC.
- [x] [Review][Patch] AC-1 test does not assert `createdBy` / `updatedBy` / `cascadeEventId` response keys — `CreateRecordIntegrationTests.cs:93-114` only asserts `id`/`createdAt`/`updatedAt`/`isDeleted`/`title`. The spec lists `createdBy`, `updatedBy`, and `cascadeEventId` as required body fields. A regression renaming or dropping them in `DynamicRecordJsonConverter.SystemColumnJsonNames` would go undetected. Extend the test to assert all three system keys are present with the expected null/Guid shape.
- [x] [Review][Patch] AC-1 test does not assert "system columns in client payload silently ignored" — `CreateRecordIntegrationTests.cs` — AC-1's final bullet promises that `{ "id": <fake-guid>, "created_at": "2000-...", "title": "x" }` returns 201 with the server-generated id (not the client's). No integration test asserts this. Add a test that POSTs a fake `id` + `created_at` in the payload and verifies `response.id != fakeId` and `response.createdAt > 2026-01-01`.
- [x] [Review][Patch] AC-4 audit-log test does not assert `actor_id`, `timestamp`, `correlation_id`, `previous_values` — `CreateRecordIntegrationTests.cs:158-186` selects only `designer_id, operation, new_values`. AC-4 names 7 required fields; only 3 are verified end-to-end. A null `userId` claim or a regression in `GetCorrelationId()` would silently pass. Extend the SELECT to include the remaining 4 columns and assert `actor_id == admin.Id`, `correlation_id` non-empty, `previous_values IS NULL`, and `timestamp` within the request window.

### Deferred (pre-existing or accepted scope)

- [x] [Review][Defer] `operation` column has no CHECK constraint enforcing `CREATE`/`UPDATE`/`SOFT_DELETE`/`RESTORE` — `FormForgeDbContext.cs` (MutationAuditLogEntry mapping). Mirrors existing `schema_audit_log` pattern. Owner: future hardening — add a CHECK constraint or domain.
- [x] [Review][Defer] Down migration drops `mutation_audit_log` outright — `20260526054545_AddMutationAuditLog.cs` `Down`. Audit data is forensic; rollback destroys evidence with no warning. Owner: future migration-policy pass — rename rather than drop, or block destructive downgrades in prod.
- [x] [Review][Defer] `RootElementParser.ParseFull(null)` silently returns empty columns on missing `ComponentSchemaVersions` row — `DynamicDataEndpoints.cs:340-349` (handler cache-miss path). If the row for `boundVersion` is missing, the schema is cached with zero user columns and subsequent INSERTs silently drop all user fields. Pre-existing pattern from Story 6.1; also recorded against Story 6.2. Owner: future hardening — return 500/404 when `rootElementJson` is null at cache-miss.
- [x] [Review][Defer] JSONB user columns may fail to INSERT without explicit `NpgsqlDbType.Jsonb` — `DynamicQueryBuilder.cs:301`. `DynamicParameters.Add(name, value)` lets Npgsql infer the DbType from the .NET type; for the JSONB fall-through case (`coerced[col] = el.ToString()`) the value is a `string`, which PG won't implicitly cast to `jsonb`. No currently-defined component maps to JSONB (per `ComponentTypeMapper`), so the path is unreachable in v1. Owner: when the first JSONB-producing component lands, add a per-type DbType branch or a Dapper TypeHandler.
- [x] [Review][Defer] Non-object request body (array / scalar) may return ASP.NET 400 instead of the project's 422 envelope — `DynamicDataEndpoints.cs:302-310`. `JsonElement` model binding behaviour for non-object roots depends on the configured `JsonOptions`; the validator's own `ValueKind != Object` check at line 31 of `DynamicPayloadValidator.cs` only fires if the binder forwards the value. Owner: confirm with a test or pin a JSON binder policy.
- [x] [Review][Defer] `mutation_audit_log.actor_id` has no FK to `users.id` — `FormForgeDbContext.cs` (MutationAuditLogEntry mapping). Deleted-user audit rows retain a dangling Guid forever. Mirrors `schema_audit_log.actor_id`. Owner: cross-cutting decision (add FK with `OnDelete(SetNull)` to both tables) when audit-viewer story 6.8 lands.
- [x] [Review][Defer] Concurrent POST race: binding may flip between EF lookup and Dapper INSERT — `DynamicDataEndpoints.cs:322-387`. Request A reads version N's columns, request B publishes/binds N+1 which renames a column, A's Dapper INSERT then fails with `column does not exist` returning 500. Owner: architectural — needs a per-designer read lock or version pin on the SchemaRegistry entry.
- [x] [Review][Defer] Unhandled `PostgresException` from the Dapper INSERT returns 500 with no `code`/`messageKey` envelope — `DynamicDataEndpoints.cs:381-382`. Affects all dynamic CRUD endpoints, not unique to CREATE. Owner: add a global middleware that maps known `PostgresException` SQLSTATE codes to structured ProblemDetails.
- [x] [Review][Defer] AC-7: no test asserts 429 / `Retry-After: 60` on POST — `CreateRecordIntegrationTests.cs`. Rate-limit policy is registered and `Retry-After: 60` is set globally in `Program.cs:327` via `OnRejected`, so production is correct. A focused 60+ request test is brittle / slow. Owner: add a single shared rate-limit integration test under a fast clock if one is added.
- [x] [Review][Defer] AC-8: no test asserts `commandTimeout = 5` on the INSERT `CommandDefinition` — `DynamicQueryBuilder.cs` / `DynamicDataEndpoints.cs:381`. The value is set in code; timing-based assertions are flaky. Owner: when a builder-level abstraction lands that surfaces the CommandDefinition, add a unit assertion.

### Dismissed (noise / false positives)

- AC-1 raw snake_case response keys: `DynamicRecordJsonConverter.SystemColumnJsonNames` (`DynamicRecord.cs:32-42`) renames every system column to camelCase; integration tests pass per the Dev Agent Record's 459/459.
- Empty-payload test depending on null serialization: handler explicitly writes `null` into `responseValues` for absent user columns; default `JsonSerializer` writes JSON `null` for null values.
- `BuildInsertQuery` `col.ColumnName` interpolated into SQL: `ColumnDefinition.ColumnName` is itself a `SafeIdentifier`-validated fieldKey, written at provisioning time.
- `JsonValueKind.Null` for "non-nullable" columns: all dynamic-table user columns are nullable per FR-24 AC-3 (`BuildInsertQuery` comment at line 247 makes this explicit).
- 422 vs 404 for invalid identifiers: intentional consistency with `ListRecordsHandler` and `GetRecordHandler`.
- `useCreateRecord.onSuccess` returns the created value: harmless dead expression; React Query ignores it.
- `Uri.EscapeDataString(safeId)` in Location header: `SafeIdentifier` already restricts charset; escape is a no-op but defence-in-depth.
- `TRUNCATE ... RESTART IDENTITY CASCADE` on UUID PK: harmless, mirrors existing pattern.
