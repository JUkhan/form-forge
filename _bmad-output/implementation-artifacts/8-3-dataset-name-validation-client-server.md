# Story 8.3: dataset_name Validation (Client + Server)

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As the system,
I validate any proposed `dataset_name` both client-side and server-side before it is used as a VIEW name,
so that invalid or unsafe identifiers are rejected before any DDL runs.

## Acceptance Criteria

**AC-1 — Regex/length failures return HTTP 422 INVALID_DATASET_NAME**
**Given** a `dataset_name` submitted to any Dataset write endpoint
**When** server-side validation runs (implemented in `DatasetName.cs` per AR-57)
**Then** valid names match `^[a-z_][a-z0-9_]*$` and are ≤63 bytes; any other value returns HTTP 422 `{ code: "INVALID_DATASET_NAME" }` (per FR-57 AC-1)

**AC-2 — Reserved PostgreSQL keywords return HTTP 422 INVALID_DATASET_NAME**
**Given** a `dataset_name` matching the regex but appearing in the PostgreSQL reserved-keyword list (e.g., "select", "from", "table", "group")
**When** server-side validation runs
**Then** HTTP 422 is returned with `{ code: "INVALID_DATASET_NAME" }` and a message identifying the reserved keyword (per FR-57 AC-2)

**AC-3 — Permanent denylist names return HTTP 422 INVALID_DATASET_NAME**
**Given** a `dataset_name` in the permanent identifier denylist — `users`, `roles`, `user_roles`, `menus`, `menu_role_assignments`, `component_schemas`, `refresh_tokens`, `password_reset_tokens`, `mfa_backup_codes`, `mfa_sessions`, `schema_audit_log`, `mutation_audit_log`, `dataset_audit_log`, `custom_dataset` (per AR-57)
**When** validation runs
**Then** HTTP 422 is returned with `{ code: "INVALID_DATASET_NAME" }` even if the name passes the regex and reserved-keyword checks

**AC-4 — Duplicate name during CREATE returns HTTP 409 DATASET_NAME_CONFLICT**
**Given** a `dataset_name` identical to an existing Dataset's name
**When** the server validates during create
**Then** HTTP 409 is returned with `{ code: "DATASET_NAME_CONFLICT" }` (per FR-57 AC-3)
**Note:** This AC is validated by Story 8.4's integration tests — the stub handler in this story returns 501 for valid names. The `DatasetName.cs` value type and the DB UNIQUE constraint together enforce this; the 409 envelope is wired in Story 8.4.

**AC-5 — Client-side inline validation fires before any API call**
**Given** the Dataset create/edit form in the UI (rendered by Story 8.10)
**When** the user blurs the `dataset_name` field or clicks submit
**Then** client-side Zod validation fires inline before any API call, applying the same regex, max-length, denylist checks (per FR-57 AC-4)
**Note:** The Zod schema (`datasetNameSchema`) is delivered in this story as `web/src/features/datasets/validation.ts`; the form UI that consumes it ships in Story 8.10.

**AC-6 — Defense-in-depth: `dataset_name` re-validated immediately before any VIEW DDL**
**Given** any code path that is about to execute Dataset VIEW DDL (Stories 8.4, 8.5, 8.6)
**When** it runs immediately before the DDL
**Then** `dataset_name` is re-validated as a final defense-in-depth check (per FR-57 AC-6 / AR-57)
**Note:** This AC is satisfied structurally: `DatasetName.cs` uses a sealed class with a private constructor (same pattern as `SafeIdentifier`). Any code path executing DDL MUST accept a `DatasetName` value object (not a raw `string`), making unsafe string interpolation physically impossible. Stories 8.4/8.5/8.6 service methods MUST accept `DatasetName`, not `string`.

---

## Tasks / Subtasks

- [x] **Task 1 — Create `DatasetName.cs` value type** (AC-1, AC-2, AC-3, AC-6)
  - [x] Create `src/FormForge.Api/Domain/ValueTypes/DatasetName.cs`
  - [x] Define `DatasetNameError` enum: `InvalidPattern`, `ReservedKeyword`, `Denylist`
  - [x] Declare the sealed class with private constructor:
    ```csharp
    internal sealed partial class DatasetName
    {
        [GeneratedRegex(@"^[a-z_][a-z0-9_]*$", RegexOptions.None, matchTimeoutMilliseconds: 100)]
        private static partial Regex ValidPattern();

        private static readonly HashSet<string> PermanentDenylist = new(StringComparer.Ordinal)
        {
            "users", "roles", "user_roles", "menus", "menu_role_assignments",
            "component_schemas", "refresh_tokens", "password_reset_tokens",
            "mfa_backup_codes", "mfa_sessions", "schema_audit_log",
            "mutation_audit_log", "dataset_audit_log", "custom_dataset",
        };

        public string Value { get; }
        private DatasetName(string value) => Value = value;

        // 2-param overload: caller only needs success/failure + error message
        public static bool TryCreate(string? raw, out DatasetName? result, out string? error)
            => TryCreate(raw, out result, out _, out error);

        // 4-param overload: exposes the error-class enum for callers needing to branch
        public static bool TryCreate(
            string? raw,
            out DatasetName? result,
            out DatasetNameError? errorCode,
            out string? error)
        {
            result = null; errorCode = null; error = null;

            if (string.IsNullOrEmpty(raw))
            {
                errorCode = DatasetNameError.InvalidPattern;
                error = "Dataset name must not be empty.";
                return false;
            }
            if (raw.Length > 63 || !ValidPattern().IsMatch(raw))
            {
                errorCode = DatasetNameError.InvalidPattern;
                error = $"'{raw}' is not a valid dataset name. Use lowercase letters (a-z), digits (0-9), " +
                        "and underscores (_). Must start with a letter or underscore. Maximum 63 characters.";
                return false;
            }
            if (PgReservedKeywords.IsReserved(raw))  // in Features/Designer namespace
            {
                errorCode = DatasetNameError.ReservedKeyword;
                error = $"'{raw}' is a reserved PostgreSQL keyword and cannot be used as a dataset name.";
                return false;
            }
            if (PermanentDenylist.Contains(raw))
            {
                errorCode = DatasetNameError.Denylist;
                error = $"'{raw}' is a reserved internal table name and cannot be used as a dataset name.";
                return false;
            }
            result = new DatasetName(raw);
            return true;
        }

        public override string ToString() => Value;
    }
    ```
  - [x] The `PgReservedKeywords.IsReserved()` call is imported from `FormForge.Api.Features.Designer` — add the `using` statement.
  - [x] **IMPORTANT:** Length check `raw.Length > 63` must happen BEFORE the regex to avoid regex-matching a 10000-char input. Check length first, then regex.

- [x] **Task 2 — Create DTOs for create and update requests** (AC-1)
  - [x] Create `src/FormForge.Api/Features/Datasets/Dtos/CreateDatasetRequest.cs`:
    ```csharp
    namespace FormForge.Api.Features.Datasets.Dtos;

    internal sealed record CreateDatasetRequest(
        string DatasetName,
        bool IsCustomQuery,
        string? Query);
    ```
  - [x] Create `src/FormForge.Api/Features/Datasets/Dtos/UpdateDatasetRequest.cs`:
    ```csharp
    namespace FormForge.Api.Features.Datasets.Dtos;

    // dataset_name and query are nullable — omitting them means "keep existing value".
    // version is always required for optimistic concurrency (Story 8.5).
    internal sealed record UpdateDatasetRequest(
        string? DatasetName,
        bool? IsCustomQuery,
        string? Query,
        string? BuilderState,
        int Version);
    ```
  - [x] These DTOs will be fleshed out further in Stories 8.4 and 8.5. Fields here cover what validation needs.

- [x] **Task 3 — Create `DatasetNameValidator.cs` FluentValidation validator** (AC-1, AC-2, AC-3)
  - [x] Create `src/FormForge.Api/Features/Datasets/Validators/DatasetNameValidator.cs`:
    ```csharp
    using FluentValidation;
    using FormForge.Api.Domain.ValueTypes;

    namespace FormForge.Api.Features.Datasets.Validators;

    // Standalone validator for the dataset_name field.
    // Used as a child validator via SetValidator() in CreateUpdateDatasetValidator.
    // Also useful for testing the validation rules in isolation.
    internal sealed class DatasetNameValidator : AbstractValidator<string>
    {
        public DatasetNameValidator()
        {
            RuleFor(x => x)
                .Must((name, _) => DatasetName.TryCreate(name, out _, out _, out _))
                .WithMessage((name) =>
                {
                    DatasetName.TryCreate(name, out _, out _, out var error);
                    return error ?? "Invalid dataset name.";
                });
        }
    }
    ```
  - [x] Create `src/FormForge.Api/Features/Datasets/Validators/CreateUpdateDatasetValidator.cs`:
    ```csharp
    using FluentValidation;
    using FormForge.Api.Features.Datasets.Dtos;

    namespace FormForge.Api.Features.Datasets.Validators;

    // Story 8.3: validates dataset_name on create.
    // Stories 8.4–8.8 will extend this with query/builder_state rules.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
        Justification = "Registered via DI as IValidator<CreateDatasetRequest>.")]
    internal sealed class CreateDatasetRequestValidator : AbstractValidator<CreateDatasetRequest>
    {
        public CreateDatasetRequestValidator()
        {
            RuleFor(x => x.DatasetName)
                .SetValidator(new DatasetNameValidator());
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
        Justification = "Registered via DI as IValidator<UpdateDatasetRequest>.")]
    internal sealed class UpdateDatasetRequestValidator : AbstractValidator<UpdateDatasetRequest>
    {
        public UpdateDatasetRequestValidator()
        {
            // dataset_name is optional on update — only validate if present
            When(x => x.DatasetName is not null, () =>
            {
                RuleFor(x => x.DatasetName!)
                    .SetValidator(new DatasetNameValidator());
            });
        }
    }
    ```

- [x] **Task 4 — Update stub endpoints to validate dataset_name and return INVALID_DATASET_NAME** (AC-1, AC-2, AC-3)
  - [x] Update `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs`
  - [x] The POST stub now accepts `CreateDatasetRequest`, calls `DatasetName.TryCreate()` inline, and returns `Results.Problem()` with `code: "INVALID_DATASET_NAME"` on failure. On success (valid name), it still returns 501 (handler not yet implemented in Story 8.4):
    ```csharp
    group.MapPost("/", ([FromBody] CreateDatasetRequest request) =>
    {
        if (!DatasetName.TryCreate(request.DatasetName, out _, out _, out var nameError))
            return Results.Problem(
                detail: nameError,
                title: "Invalid dataset name",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "INVALID_DATASET_NAME",
                    ["messageKey"] = "datasets.invalidDatasetName",
                });
        return Results.StatusCode(StatusCodes.Status501NotImplemented);
    })
    .WithSummary("Create dataset (stub — Story 8.4)")
    .RequireDatasetManagement();
    ```
  - [x] The PUT stub similarly accepts `UpdateDatasetRequest` and validates `dataset_name` only when non-null:
    ```csharp
    group.MapPut("/{id:guid}", (Guid id, [FromBody] UpdateDatasetRequest request) =>
    {
        if (request.DatasetName is not null
            && !DatasetName.TryCreate(request.DatasetName, out _, out _, out var nameError))
            return Results.Problem(
                detail: nameError,
                title: "Invalid dataset name",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "INVALID_DATASET_NAME",
                    ["messageKey"] = "datasets.invalidDatasetName",
                });
        return Results.StatusCode(StatusCodes.Status501NotImplemented);
    })
    .WithSummary("Update dataset (stub — Story 8.5)")
    .RequireDatasetManagement();
    ```
  - [x] **IMPORTANT — `/preview` stub registration order**: Story 8.2 review noted that `POST /preview` should be registered BEFORE `POST /` to avoid route-shadowing risk. Fix this ordering now:
    ```
    MapPost("/preview", ...) // FIRST
    MapPost("/", ...)        // SECOND
    ```
  - [x] The DELETE stub (`DELETE /{id:guid}`) has no body — no changes needed for this story.
  - [x] The two GET stubs remain unchanged.
  - [x] Register the FluentValidation validators in DI (in Program.cs `AddFluentValidation` call or validator assembly scan — see Dev Notes §4):
    - `IValidator<CreateDatasetRequest>` → `CreateDatasetRequestValidator`
    - `IValidator<UpdateDatasetRequest>` → `UpdateDatasetRequestValidator`
  - [x] **Do NOT add `.AddValidationFilter<CreateDatasetRequest>()` to the stub endpoints.** The stub uses an inline `DatasetName.TryCreate()` check (not the FluentValidation filter) to return the specific `INVALID_DATASET_NAME` code. FluentValidation's `ValidationProblemDetails` format doesn't emit a root `code` field. The inline pattern matches the `IDENTIFIER_INVALID` handling in `DesignerEndpoints.cs` (lines 104–112).

- [x] **Task 5 — Unit tests for `DatasetName` validation** (AC-1, AC-2, AC-3)
  - [x] Create `src/FormForge.Api.Tests/Features/Datasets/DatasetNameValidatorTests.cs`
  - [x] Test `DatasetName.TryCreate()` for each validation layer:
    - **AC-1 valid inputs** → `true`: `"my_dataset"`, `"_private"`, `"report_2024"`, `"a"` (single char), `"a" + new string('b', 62)` (63 chars exactly)
    - **AC-1 invalid pattern** → `false`, `InvalidPattern`: `""`, `" "`, `"MyDataset"` (uppercase), `"123abc"` (starts with digit), `"my-dataset"` (hyphen), `"a" + new string('b', 63)` (64 chars — exceeds limit), `"a b"` (space), `"pg_foo"` (pg_ prefix — caught by `PgReservedKeywords.IsReserved`)
    - **AC-2 reserved keywords** → `false`, `ReservedKeyword`: `"select"`, `"from"`, `"table"`, `"group"`, `"user"`, `"role"`
    - **AC-3 denylist** → `false`, `Denylist`: `"users"`, `"roles"`, `"menus"`, `"custom_dataset"`, `"dataset_audit_log"`, `"refresh_tokens"`
    - **AC-6 structural**: Assert `DatasetName` ctor is private (via reflection) — confirms sealed+private-ctor pattern
  - [x] Test the 2-param overload (`TryCreate(raw, out result, out error)`) matches the 4-param overload results
  - [x] No integration tests in this test file (unit-only); integration tests for the endpoint are in Task 6

- [x] **Task 6 — Integration tests for the stub endpoints** (AC-1, AC-2, AC-3)
  - [x] Create `src/FormForge.Api.Tests/Features/Datasets/DatasetNameValidationTests.cs`
  - [x] Reuse `PostgresFixture` and `WebApplicationFactory<Program>` pattern from `DatasetMigrationTests.cs` / `DatasetPermissionTests.cs`
  - [x] Tests (all use platform-admin token):
    - **AC-1 invalid pattern** — `POST /api/datasets { "datasetName": "InvalidName", ... }` → 422, body contains `code: "INVALID_DATASET_NAME"`
    - **AC-1 too long** — `POST /api/datasets { "datasetName": 64-char lowercase string }` → 422, `INVALID_DATASET_NAME`
    - **AC-2 reserved keyword** — `POST /api/datasets { "datasetName": "select" }` → 422, `INVALID_DATASET_NAME`
    - **AC-3 denylist** — `POST /api/datasets { "datasetName": "users" }` → 422, `INVALID_DATASET_NAME`
    - **AC-3 denylist** — `POST /api/datasets { "datasetName": "custom_dataset" }` → 422, `INVALID_DATASET_NAME`
    - **Valid name passes validation** — `POST /api/datasets { "datasetName": "my_report" }` → 501 (stub; NOT 422/403)
    - **PUT with invalid name** — `PUT /api/datasets/{uuid} { "datasetName": "BAD_NAME", "version": 1 }` → 422, `INVALID_DATASET_NAME`
    - **PUT with null name (omitted rename)** — `PUT /api/datasets/{uuid} { "version": 1 }` → 501 (null dataset_name skips validation)
  - [x] Assert `code` field:
    ```csharp
    var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    Assert.Equal("INVALID_DATASET_NAME", doc.RootElement.GetProperty("code").GetString());
    ```
    Per Story 8.2 Dev Record §deviation — `Results.Problem` extensions are flattened to the root by ASP.NET Core `[JsonExtensionData]`.

- [x] **Task 7 — Client-side Zod schema for `dataset_name`** (AC-5)
  - [x] Create `web/src/features/datasets/validation.ts`:
    ```ts
    // Client-side dataset_name validation — mirrors DatasetName.cs rules.
    // Consumed by Story 8.10's create/edit modal form (datasetNameSchema).

    const DATASET_NAME_REGEX = /^[a-z_][a-z0-9_]*$/;

    // Mirrors the permanent denylist in DatasetName.cs (AR-57).
    const DATASET_NAME_DENYLIST = new Set<string>([
      'users', 'roles', 'user_roles', 'menus', 'menu_role_assignments',
      'component_schemas', 'refresh_tokens', 'password_reset_tokens',
      'mfa_backup_codes', 'mfa_sessions', 'schema_audit_log',
      'mutation_audit_log', 'dataset_audit_log', 'custom_dataset',
    ]);

    // PG reserved keywords subset — client checks the most common ones.
    // Server is the authority; this list provides fast client-side feedback.
    const PG_RESERVED_SUBSET = new Set<string>([
      'all', 'and', 'any', 'as', 'asc', 'authorization', 'binary', 'both',
      'case', 'cast', 'check', 'collate', 'column', 'constraint', 'create',
      'cross', 'default', 'deferrable', 'desc', 'distinct', 'do', 'else',
      'end', 'except', 'false', 'fetch', 'for', 'foreign', 'from', 'full',
      'grant', 'group', 'having', 'in', 'inner', 'into', 'is', 'join',
      'lateral', 'left', 'like', 'limit', 'natural', 'not', 'null', 'offset',
      'on', 'only', 'or', 'order', 'outer', 'primary', 'references', 'right',
      'role', 'select', 'similar', 'some', 'symmetric', 'table', 'then', 'to',
      'true', 'union', 'unique', 'user', 'using', 'variadic', 'when',
      'where', 'window', 'with',
    ]);

    import { z } from 'zod';

    export const datasetNameSchema = z
      .string()
      .min(1, 'Dataset name is required.')
      .max(63, 'Dataset name must be 63 characters or fewer.')
      .regex(
        DATASET_NAME_REGEX,
        'Use lowercase letters (a-z), digits (0-9), and underscores (_). Must start with a letter or underscore.',
      )
      .refine(name => !name.startsWith('pg_'), {
        message: "Names starting with 'pg_' are reserved by PostgreSQL.",
      })
      .refine(name => !PG_RESERVED_SUBSET.has(name), {
        message: 'This is a reserved PostgreSQL keyword.',
      })
      .refine(name => !DATASET_NAME_DENYLIST.has(name), {
        message: 'This name is reserved for internal use.',
      });
    ```
  - [x] The `import { z }` should be at the top of the file — move it before the const declarations.
  - [x] This module is intentionally standalone (no React, no hooks) so it can be imported into any form component or test.

- [x] **Task 8 — i18n keys** (AC-5)
  - [x] Add to `web/src/lib/i18n/locales/en.json` under a new top-level `datasets` key:
    ```json
    "datasets": {
      "invalidDatasetName": "Invalid dataset name.",
      "validation": {
        "required": "Dataset name is required.",
        "tooLong": "Dataset name must be 63 characters or fewer.",
        "invalidPattern": "Use lowercase letters (a-z), digits (0-9), and underscores (_). Must start with a letter or underscore.",
        "reservedKeyword": "This is a reserved PostgreSQL keyword.",
        "denylist": "This name is reserved for internal use."
      }
    }
    ```
  - [x] Verify the i18n-lint test still passes after adding these keys (the test counts keys and checks for orphans).

---

## Dev Notes

### §1 — `DatasetName.cs` mirrors `SafeIdentifier.cs` but lives in a different location

`SafeIdentifier.cs` lives at `src/FormForge.Api/Features/Designer/SafeIdentifier.cs` (Feature folder). Per AR-57 and the architecture's Domain map, `DatasetName.cs` goes to `src/FormForge.Api/Domain/ValueTypes/DatasetName.cs` (the Domain layer). The `Domain/ValueTypes/` directory does not exist yet — create it.

Both types use the same sealed+private-ctor pattern, and both use `PgReservedKeywords.IsReserved()` from `FormForge.Api.Features.Designer`. You must add:
```csharp
using FormForge.Api.Features.Designer;
```
in `DatasetName.cs`.

**Key regex difference:** `SafeIdentifier` uses `^[a-z_][a-z0-9_]{0,62}$` (length enforced in regex). `DatasetName` uses `^[a-z_][a-z0-9_]*$` (unbounded) with a **separate** `raw.Length > 63` length check. Check length first, then regex — this avoids running the regex against adversarially long input.

**`DatasetNameError` enum** adds a third value compared to `SafeIdentifierError`:
```csharp
internal enum DatasetNameError { InvalidPattern, ReservedKeyword, Denylist }
```

### §2 — Complete permanent denylist (AR-57)

These 14 names are permanently blocked regardless of regex/keyword status:

| Name | Reason |
|------|--------|
| `users` | Static EF table |
| `roles` | Static EF table |
| `user_roles` | Static EF junction table |
| `menus` | Static EF table |
| `menu_role_assignments` | Static EF junction table |
| `component_schemas` | Static EF table |
| `refresh_tokens` | Static EF table |
| `password_reset_tokens` | Static EF table |
| `mfa_backup_codes` | Static EF table |
| `mfa_sessions` | Static EF table |
| `schema_audit_log` | Static EF table |
| `mutation_audit_log` | Static EF table |
| `dataset_audit_log` | Static EF table (this epic) |
| `custom_dataset` | Static EF table (this epic) |

Note: `roles` is NOT in `PgReservedKeywords` (`role` is, not `roles`). The denylist provides this additional guard.

### §3 — Stub endpoint update strategy

The current stubs in `DatasetEndpoints.cs` return:
- `POST /` → 501 (no body parsed)
- `PUT /{id:guid}` → 501 (no body parsed)

After this story, both parse their respective DTOs and call `DatasetName.TryCreate()` inline. The validation uses `Results.Problem(...)` with `extensions["code"] = "INVALID_DATASET_NAME"` — **not** FluentValidation's `ValidationProblemDetails`. This matches the designer's `IDENTIFIER_INVALID` pattern at `DesignerEndpoints.cs:104–112`.

**Why not FluentValidation filter?** `ValidationFilter<T>` returns `Results.ValidationProblem(errors, ...)` where `errors` is a `Dictionary<string, string[]>` — this format has no root `code` field. The AC requires `{ code: "INVALID_DATASET_NAME" }` at the root. Using `Results.Problem()` with extensions achieves this directly.

**`/preview` ordering fix (Story 8.2 deferred):** The review for Story 8.2 flagged that `POST /preview` should be registered before `POST /` to avoid potential route-shadowing. Fix this now when touching `DatasetEndpoints.cs`:
```csharp
group.MapPost("/preview", ...).RequireDatasetManagement(); // FIRST
group.MapPost("/", ...).RequireDatasetManagement();        // SECOND
```

**DELETE stub unchanged:** `DELETE /{id:guid}` has no request body, so no dataset_name validation is needed in this story (the deletion path validates by ID lookup, implemented in Story 8.6).

### §4 — FluentValidation DI registration

The existing codebase registers FluentValidation validators via assembly scanning (check `Program.cs` for `AddFluentValidation` or `AddValidatorsFromAssemblyContaining`). The new `CreateDatasetRequestValidator` and `UpdateDatasetRequestValidator` will be auto-discovered if the assembly scan is in place. Verify this by checking `Program.cs` — look for `AddFluentValidation(cfg => ...)` or `AddValidatorsFromAssemblyContaining<Program>()`. If the scan is in place, no explicit DI registration is needed.

### §5 — Integration test pattern

Integration test pattern from `DatasetPermissionTests.cs`:
```csharp
public sealed class DatasetNameValidationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;

    public DatasetNameValidationTests(PostgresFixture postgres) => _postgres = postgres;

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
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
    }
    // ...
}
```

To obtain an admin token for tests, use the same helper pattern from `DatasetPermissionTests.cs` (login as the seeded platform-admin user).

**Asserting `code` field:** Per Story 8.2 dev record deviation — `Results.Problem` with `extensions` flattens those keys to the JSON root (ASP.NET Core's `[JsonExtensionData]` behavior). Assert at the root:
```csharp
var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
Assert.Equal("INVALID_DATASET_NAME", doc.RootElement.GetProperty("code").GetString());
```

NOT nested under `"extensions"`.

### §6 — Client-side `validation.ts` is intentionally plain TypeScript

The `web/src/features/datasets/validation.ts` module has no React imports and no i18n `t()` hooks. This makes it importable from tests, form components, and plain TypeScript utilities without requiring a React context. Error messages are static English strings — the i18n keys are defined in `en.json` for Story 8.10 to wire up via `t()` in the form component.

The `DATASET_NAME_REGEX` and `DATASET_NAME_DENYLIST` constants are exported so Story 8.10's form component can reference them directly if needed (e.g., for `aria-describedby` messages).

### §7 — What this story does NOT implement

| Feature | Story |
|---------|-------|
| `DATASET_NAME_CONFLICT` (409 on duplicate) | 8.4 (wired during create handler) |
| Actual create handler (replaces 501) | 8.4 |
| Actual update handler (replaces 501) | 8.5 |
| Dataset Management UI (create/edit modal) | 8.10 |
| `DatasetSqlGenerator.DatasetName` usage | 11.1 |

The `POST /preview` endpoint already has its stub (`RequireDatasetManagement()`) — this story fixes its registration order but does not add name validation to preview (preview validates the SQL query, not the dataset name).

### §8 — Pre-existing test failures to ignore

Per memory:
- 2 `SchemaAuditLogIntegrationTests` / `MutationAuditLogIntegrationTests` failures (audit DELETE→405) — pre-existing, unrelated
- `i18n-lint.test.ts` — 1 pre-existing failure (missing `designer.inspector.placeholders.label`)

Expected after this story: same baseline (804 backend + 1 new unit file + N integration tests; frontend 246+, 1 pre-existing failure, 0 new failures).

### Project Structure Notes

**New files created:**
```
src/FormForge.Api/Domain/ValueTypes/              ← NEW directory
src/FormForge.Api/Domain/ValueTypes/DatasetName.cs ← NEW
src/FormForge.Api/Features/Datasets/Dtos/CreateDatasetRequest.cs  ← NEW
src/FormForge.Api/Features/Datasets/Dtos/UpdateDatasetRequest.cs  ← NEW
src/FormForge.Api/Features/Datasets/Validators/   ← NEW directory
src/FormForge.Api/Features/Datasets/Validators/DatasetNameValidator.cs  ← NEW
src/FormForge.Api/Features/Datasets/Validators/CreateUpdateDatasetValidator.cs  ← NEW
src/FormForge.Api.Tests/Features/Datasets/DatasetNameValidatorTests.cs  ← NEW
src/FormForge.Api.Tests/Features/Datasets/DatasetNameValidationTests.cs ← NEW (integration)
web/src/features/datasets/                        ← NEW directory
web/src/features/datasets/validation.ts           ← NEW
```

**Modified files:**
```
src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs  (stub handlers accept DTOs + inline validation)
web/src/lib/i18n/locales/en.json                         (new "datasets" top-level key)
```

**No new EF Core migrations** — this story adds no database schema changes.
**No changes to `Program.cs`** — FluentValidation validators are auto-discovered by the existing assembly scan.

### References

- [Source: `_bmad-output/planning-artifacts/epics.md` — Epic 8, Story 8.3 ACs (FR-57)]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.1 — AR-57: Dataset Schema + DatasetName.cs + identifier denylist]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.5 — AR-61: SELECT-only enforcement (context for SqlSelectEnforcer, not this story)]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.9 — AR-65: Dataset API contract + 7 error codes including INVALID_DATASET_NAME (422)]
- [Source: `src/FormForge.Api/Features/Designer/SafeIdentifier.cs` — sealed+private-ctor pattern to mirror]
- [Source: `src/FormForge.Api/Features/Designer/PgReservedKeywords.cs` — shared keyword check (reused via `PgReservedKeywords.IsReserved()`)]
- [Source: `src/FormForge.Api/Features/Designer/DesignerEndpoints.cs:104–112` — IDENTIFIER_INVALID Results.Problem pattern to follow]
- [Source: `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs` — current stub state; /preview ordering fix needed]
- [Source: `src/FormForge.Api/Domain/Entities/CustomDataset.cs` — entity structure; DatasetName used in Value column]
- [Source: `src/FormForge.Api/Common/Endpoints/EndpointFilters/ValidationFilter.cs` — why FluentValidation ValidationProblemDetails can't emit root `code` field]
- [Source: `src/FormForge.Api/Common/Endpoints/RouteGroupExtensions.cs` — RequireDatasetManagement() pattern]
- [Source: `src/FormForge.Api.Tests/Features/Datasets/DatasetPermissionTests.cs` — PostgresFixture + WAF pattern, admin login helper, 403-body assertion technique]
- [Source: `src/FormForge.Api.Tests/Features/Datasets/DatasetMigrationTests.cs` — test project initialization pattern]
- [Source: Story 8.2 Dev Agent Record §Deviation — code/action assertions at JSON root (not under "extensions")]
- [Source: Story 8.2 Dev Agent Record §Review Findings — /preview before / ordering fix (deferred to this story)]

---

### Review Findings

- [x] [Review][Patch] DatasetNameValidator calls `DatasetName.TryCreate` twice per validation — use `.Custom()` to call once and capture error in a single pass [`src/FormForge.Api/Features/Datasets/Validators/DatasetNameValidator.cs`]
- [x] [Review][Defer] FluentValidation filter risk for Stories 8.4/8.5 — if `AddValidationFilter<CreateDatasetRequest>()` is attached in Story 8.4, the response will be a 400 `ValidationProblemDetails` (not 422 `INVALID_DATASET_NAME`); Stories 8.4/8.5 must keep inline validation for `dataset_name` rather than using the FV filter [`src/FormForge.Api/Features/Datasets/Validators/DatasetNameValidator.cs`, `Program.cs`] — deferred, actionable in Story 8.4
- [x] [Review][Defer] `UpdateDatasetRequest.Version` has no minimum-value check — negative/zero version reaches the handler without a 422; guard belongs in Story 8.5's real update handler [`src/FormForge.Api/Features/Datasets/Dtos/UpdateDatasetRequest.cs`] — deferred, pre-existing gap for Story 8.5
- [x] [Review][Defer] `pg_`-prefix rejection produces a different client error message ("Names starting with 'pg_' are reserved by PostgreSQL.") than the server's generic `messageKey: "datasets.invalidDatasetName"` — cosmetic UX inconsistency; align in Story 8.10 when form UI wires i18n [`web/src/features/datasets/validation.ts`] — deferred, pre-existing for Story 8.10

## Dev Agent Record

### Agent Model Used

claude-opus-4-8[1m] (Opus 4.8, 1M context)

### Debug Log References

- **DatasetNameValidator compile error (CS1503):** The spec's `.Must((name, _) => DatasetName.TryCreate(name, out _, out _, out _))` failed to compile — the lambda's `_` parameter shadowed the `out _` discards, so the compiler bound `out _` to the lambda's context parameter. Fixed by switching to the single-arg `.Must(name => …)` overload so `out _` are true discards.
- **Regression in Story 8.2 test:** `DatasetPermissionTests.PostDatasets_Admin_Returns501NotForbidden` posted an empty `{}` body and asserted 501. With inline validation now in place, a null `datasetName` returns 422 INVALID_DATASET_NAME. Updated that test to post a valid name (`admin_probe`) so it still exercises the "authorised user is not blocked → 501 stub" intent.

### Completion Notes List

- **AC-1/AC-2/AC-3/AC-6** — `DatasetName.cs` value type created in the new `Domain/ValueTypes/` directory, mirroring `SafeIdentifier`'s sealed + private-ctor pattern. Length check runs BEFORE the regex (avoids regex on adversarial input). Three-class `DatasetNameError` enum (`InvalidPattern`/`ReservedKeyword`/`Denylist`). Reuses `PgReservedKeywords.IsReserved()`.
- **AC-1/AC-2/AC-3** — POST/PUT stub endpoints now parse their DTOs and validate inline via `DatasetName.TryCreate`, returning `Results.Problem` with root `code: "INVALID_DATASET_NAME"` (matching the designer's `IDENTIFIER_INVALID` pattern; FluentValidation's `ValidationProblemDetails` can't emit a root `code`). PUT skips validation when `datasetName` is null (omitted-rename semantics).
- **Story 8.2 deferral resolved** — `POST /preview` is now registered BEFORE `POST /` to eliminate route-shadowing risk.
- **AC-5** — `web/src/features/datasets/validation.ts` Zod schema (`datasetNameSchema`) mirrors the server rules (regex, max-63, `pg_` guard, reserved-keyword subset, denylist). Standalone (no React/i18n) so it's importable anywhere. `DATASET_NAME_REGEX` and `DATASET_NAME_DENYLIST` exported for Story 8.10's form.
- **i18n** — New top-level `datasets` key added to `en.json`. These keys are intentionally orphaned (warning, not error) until Story 8.10 wires them via `t()`; the i18n-check script only fails on *missing* keys, so the baseline is unchanged.
- **DEVIATION from Dev Notes §4 / Project Structure Notes** — The story assumed FluentValidation validators are auto-discovered via an assembly scan and that Program.cs needs no changes. The codebase actually registers validators **explicitly** per-type (no scan exists). I registered `CreateDatasetRequestValidator` and `UpdateDatasetRequestValidator` explicitly in `Program.cs`, consistent with every other validator. The stub endpoints use inline validation today, but the DI registrations are ready for the FluentValidation filter in Stories 8.4/8.5.
- **AC-4 (DATASET_NAME_CONFLICT 409)** — Not implemented here by design; wired in Story 8.4's create handler. The `DatasetName` value type + DB UNIQUE constraint provide the underlying enforcement.

**Test results:**
- Backend unit (`DatasetNameValidatorTests`): 31/31 pass.
- Backend integration (`DatasetNameValidationTests`): 8/8 pass.
- All Dataset backend tests together (incl. updated 8.2 test): 56/56 pass.
- Full backend suite: 844 passed, 2 failed — both pre-existing, unrelated (audit DELETE→405: `MutationAuditLogIntegrationTests` + `SchemaAuditLogIntegrationTests`). 0 new failures.
- Frontend (`validation.test.ts`): 34/34 pass. Full web suite: 280 passed, 1 failed — pre-existing `i18n-lint` (missing `designer.inspector.placeholders.label`), unaffected by this story. ESLint clean on new files.

### File List

**New files:**
- `src/FormForge.Api/Domain/ValueTypes/DatasetName.cs`
- `src/FormForge.Api/Features/Datasets/Dtos/CreateDatasetRequest.cs`
- `src/FormForge.Api/Features/Datasets/Dtos/UpdateDatasetRequest.cs`
- `src/FormForge.Api/Features/Datasets/Validators/DatasetNameValidator.cs`
- `src/FormForge.Api/Features/Datasets/Validators/CreateUpdateDatasetValidator.cs`
- `src/FormForge.Api.Tests/Features/Datasets/DatasetNameValidatorTests.cs`
- `src/FormForge.Api.Tests/Features/Datasets/DatasetNameValidationTests.cs`
- `web/src/features/datasets/validation.ts`
- `web/src/features/datasets/__tests__/validation.test.ts`

**Modified files:**
- `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs` (DTO parsing + inline validation; `/preview` reordered before `/`)
- `src/FormForge.Api/Program.cs` (explicit DI registration of the two dataset validators + usings)
- `src/FormForge.Api.Tests/Features/Datasets/DatasetPermissionTests.cs` (updated `PostDatasets_Admin_Returns501NotForbidden` to post a valid name)
- `web/src/lib/i18n/locales/en.json` (new `datasets` top-level key)

### Change Log

| Date | Change |
|------|--------|
| 2026-06-03 | Story created — dataset_name validation (client + server), ready-for-dev |
| 2026-06-03 | Implemented all 8 tasks: DatasetName value type, DTOs, FluentValidation validators (explicit DI registration — deviation from §4 assembly-scan assumption), inline-validated POST/PUT stubs with INVALID_DATASET_NAME envelope, /preview reordered, Zod schema, i18n keys. Added 31 unit + 8 integration + 34 frontend tests. Fixed regression in Story 8.2's PostDatasets_Admin test. Status → review. |
