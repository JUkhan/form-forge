---
title: 'Story 12.2: Tenant Schema Provisioning'
type: 'feature'
created: '2026-09-08'
status: 'done'
route: 'dispatch'
review_loop_iteration: 0
context: []
baseline_commit: '9c9db4a35e9b9963b4268d9bb014827fd7a1da40'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** There is no way to turn a validated `Tenant` row (Story 12.1) into a real, isolated PostgreSQL schema. Every table that has always lived in `public` (`users`, `roles`, `menus`, `component_schemas`, etc.) needs to exist inside each tenant's own schema, but nothing in this codebase has ever pointed EF Core's migration runner at a schema other than `public` — there is no dynamic-schema precedent to build on (confirmed by investigation: no `HasDefaultSchema`, no `search_path` usage anywhere in the project).

**Approach:** Add `ITenantProvisioningService.ProvisionSchemaAsync(tenant)` that (1) validates `schema_name` via `SafeIdentifier.TryCreate` — the same format/reserved-keyword rule as `designerId` — and rejects a collision with an existing tenant, ahead of Story 12.1's DB-level unique-index backstop; (2) issues `CREATE SCHEMA "{schema_name}"` via the existing `DbConnectionFactory` raw-DDL pattern; (3) replays the full, unmodified static-schema EF Core migration set into that schema by opening a dedicated connection whose `SearchPath` targets the new schema and driving a fresh `FormForgeDbContext` instance's `Database.MigrateAsync()` against it. This proves out the project's first dynamic-schema migration path in isolation. The tenant's `status` stays `Provisioning` throughout this story regardless of outcome — advancing to `Active`, seeding, Dataset Manager scoping, and the recovery service are Story 12.7's job (split from the original bundled story; see `epics.md` Story 12.2's note and `deferred-work.md`).

## Boundaries & Constraints

**Always:**
- Validate `schema_name` via `SafeIdentifier.TryCreate` before any DDL touches it — same defense-in-depth posture as every other dynamic identifier in this codebase.
- Reuse the existing static-schema EF migration set exactly as-is — never fork, copy, or hand-write a parallel migration set for tenant schemas.
- Leave `Tenant.Status` untouched (stays `'Provisioning'`, Story 12.1's default) on both success and failure paths.
- Reuse the existing `ConnectionStrings:formforge` connection string — it already has the privileges this needs (the existing migrations already issue `CREATE ROLE`/`GRANT` under it).

**Never:**
- Do not seed any data (roles, users) into the new schema — Story 12.7.
- Do not create the tenant-scoped `{schema_name}_datasets` VIEW namespace or touch `formforge_preview` grants — Story 12.7.
- Do not dispatch any email.
- Do not build `TenantProvisioningRecoveryService` — recovery needs the *full* schema+migrate+seed sequence to define what "stuck" means; that's Story 12.7's scope, once it exists to recover into.
- Do not add an HTTP endpoint or wire this into the admin UI — Story 12.5 consumes this service later; this story only builds the service itself.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Happy path | Valid `name` + `schema_name`, no existing tenant uses that `schema_name` | Schema created; every static table/constraint from a fresh `public` migration exists inside it | N/A |
| Invalid schema_name format | `schema_name` fails `SafeIdentifier` (uppercase, starts with a digit, reserved keyword, etc.) | Request rejected before any DDL runs | Friendly validation error; no schema created |
| Schema name collision | `schema_name` already used by another tenant | Request rejected by an app-level pre-check before `CREATE SCHEMA` — ahead of Story 12.1's DB unique index | Friendly collision error; no DDL attempted |
| Migration failure mid-replay | `CREATE SCHEMA` succeeds, migration set fails partway (e.g. a simulated fault) | Tenant row remains `'Provisioning'`; failure propagates to the caller | Exception surfaces; the schema may be left partially migrated — Story 12.7's recovery service is the intended remediation path, explicitly out of scope here |

</frozen-after-approval>

## Code Map

- `src/FormForge.Api/Features/Provisioning/ProvisioningRecoveryService.cs` — pattern reference only, not modified; shows the "scan for stuck rows, swallow all but cancellation" shape for when Story 12.7 builds its analog
- `src/FormForge.Api/Infrastructure/Persistence/DbConnectionFactory.cs` — reuse for opening the raw connection that issues `CREATE SCHEMA`; reads `ConnectionStrings:formforge`
- `src/FormForge.Api/Features/Provisioning/DdlEmitter.cs` — reference for this codebase's "open connection, run DDL in a transaction" shape (671 lines; don't copy wholesale, just the pattern)
- `src/FormForge.Api/Features/Designer/SafeIdentifier.cs` — reuse `TryCreate(string? raw, out SafeIdentifier? result, out string? error)` for `schema_name` validation
- `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` — the DbContext whose migration set must be replayed into the new schema; do not modify
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/` — the migration set being replayed, unmodified, in original order
- `src/FormForge.Api/Domain/Entities/Tenant.cs` — entity this service reads (Story 12.1); do not modify its schema here
- `src/FormForge.Api/Program.cs:110-111` — existing `AddDbContext<FormForgeDbContext>` DI pattern; mirror when registering the new service
- `src/FormForge.Api.Tests/Infrastructure/PostgresFixture.cs` — Testcontainers fixture to reuse for the new integration tests
- **No prior art for dynamic-schema EF migration anywhere in this repo** (confirmed: zero hits for `HasDefaultSchema`/`search_path`). See Design Notes for the approach.

## Tasks & Acceptance

**Execution:**
- [x] `src/FormForge.Api/Features/Tenancy/ITenantProvisioningService.cs` -- define `Task ProvisionSchemaAsync(Tenant tenant, CancellationToken ct)` -- narrow contract for this story's scope only
- [x] `src/FormForge.Api/Features/Tenancy/TenantProvisioningService.cs` -- implement `schema_name` validation (SafeIdentifier + collision check against existing tenants), `CREATE SCHEMA`, and the dynamic-schema migration replay -- the core of this story
- [x] `src/FormForge.Api/Program.cs` -- register `ITenantProvisioningService`/`TenantProvisioningService` in DI -- wiring, no behavior change elsewhere
- [x] `src/FormForge.Api.Tests/Features/Tenancy/TenantProvisioningServiceTests.cs` -- integration tests proving the dynamic-schema migration actually works, covering the happy path and all three edge cases in the I/O matrix -- this is what de-risks the novel mechanism

**Acceptance Criteria:**
- Given a valid `name` + `schema_name`, when `ProvisionSchemaAsync` runs, then the named PostgreSQL schema exists afterward and contains every static table/constraint a fresh migration of `public` would produce (verified by querying `information_schema.tables`/`information_schema.table_constraints` for that schema).
- Given an invalid or colliding `schema_name`, when `ProvisionSchemaAsync` is called, then it fails validation before any DDL executes and no schema is created.
- Given the service runs to completion (success or failure), when the tenant row is re-read, then `Status` is unchanged from Story 12.1's `'Provisioning'` default.

## Implementation Notes

## Spec Change Log

## Review Triage Log

- **[patch, medium]** Happy-path test never re-reads `tenant.Status` after a successful `ProvisionSchemaAsync` call — only the failure-path test asserts `Status == "Provisioning"`. A future regression that advances status on the success path (explicitly forbidden by this story's boundaries) would ship undetected. (verification-gap, pre-verified)
- **[patch, medium]** Design Notes claim "every migration's `Up()` runs unmodified... against whatever schema `SearchPath` resolves" is false for 3 migrations that hardcode `public`/`public.<table>` in raw SQL: `CreateDatasetManagerFoundation.cs`, `GrantPreviewRoleOnProvisionedTables.cs`, `RestrictPreviewRoleUsersColumns.cs` (confirmed by reading all three). Harmless in practice — the statements are idempotent (guarded `IF NOT EXISTS`/`IF EXISTS`, or naturally idempotent GRANT/REVOKE) and always target `public`, never the tenant schema — but the claim itself is inaccurate and could mislead Story 12.7. (blind-hunter + edge-case-hunter, duplicate claims, merged)
- **[patch, medium]** `SafeIdentifier`/`PgReservedKeywords` do not reject Postgres system schema names (`public`, `pg_catalog`, `information_schema`, `pg_temp`) — confirmed no such entries in `PgReservedKeywords.cs`. `schema_name = "public"` passes validation and the app-level collision check (no existing tenant uses it), then fails at `CREATE SCHEMA "public"` with a raw Postgres "already exists" exception instead of the friendly validation error the I/O matrix promises. (edge-case-hunter)
- **[patch, low]** `ProvisionSchemaAsync_InvalidSchemaName_ThrowsBeforeAnyDdl` asserts against `invalidSchemaName.ToLowerInvariant()`, but `SafeIdentifier`'s regex (`^[a-z_][a-z0-9_]{0,62}$`) never accepts uppercase input and the code never lowercases it — the assertion checks a schema name the SUT never attempts. Cosmetic; assertion still passes correctly, just doesn't verify real behavior. (blind-hunter)
- **[patch, low]** New `using FormForge.Api.Features.Tenancy;` in `Program.cs` is inserted between `FormForge.Api.Features.Roles` and `FormForge.Api.Features.Roles.Dtos`, splitting the contiguous `Roles`/`Roles.Dtos`/`Roles.Validators` using group. (blind-hunter)
- **[patch, low]** Migration replay's `DbContextOptionsBuilder.UseNpgsql(tenantConnection)` sets no explicit command timeout, unlike the raw-DDL connection which uses `DbConnectionFactory.DdlCommandTimeoutSeconds`. Negligible today (11 trivial migrations) but worth aligning for consistency as the migration set grows. (edge-case-hunter)
- **[false]** Claimed the `CREATE ROLE`/`GRANT`/`REVOKE` side effects in the replayed migrations are non-idempotent. Disproven: `CREATE ROLE formforge_preview` is guarded by `IF NOT EXISTS (SELECT FROM pg_roles ...)`, and repeated `GRANT`/`REVOKE` of the same privilege is a no-op in Postgres — none of it errors on replay. (blind-hunter)
- **[low, rejected]** Claimed no test exercises re-running `ProvisionSchemaAsync` for an already-persisted tenant (the `t.Id != tenant.Id` collision-check exclusion is unexercised). Real gap, but retry/idempotency semantics are undefined by this story's frozen intent (recovery/retry is explicitly Story 12.7's job) and not part of the frozen I/O matrix — fixing it means inventing untested-for behavior, not a direct correction. Rejected: low, fix is more than a direct correction. (blind-hunter)
- **[false]** Claimed `TenantProvisioningServiceTests`'s lack of schema-level teardown could cause "already exists" errors across test runs if the Testcontainers container is reused. Disproven: `PostgresFixture` starts a fresh `postgres:17-alpine` container per test-class run via `IAsyncLifetime` with no container-reuse configured, so no cross-run schema residue is possible. (blind-hunter)
- **[false]** Claimed the "ahead of the DB backstop" framing for the collision pre-check is unverified against a real caller. This is documentation framing about a not-yet-built caller (Story 12.5) — no bad outcome is demonstrated at any cited location in this story's own code or tests. (blind-hunter)
- **[false]** Claimed the migration-failure test's `CREATE EVENT TRIGGER` needs undocumented superuser privileges that may not be present. Disproven: Testcontainers' `PostgreSqlBuilder` sets `POSTGRES_USER=testuser`, which the official `postgres` Docker image grants cluster-superuser privileges to at `initdb` time, and the test already passed twice in the implementation subagent's verification run, empirically confirming the privilege is present. (blind-hunter)
- **[false]** Claimed `spec_file` frontmatter `status: 'in-review'` disagreeing with `sprint-status.yaml`'s `12-2-tenant-schema-provisioning: in-progress` is a within-diff inconsistency. Disproven: `sprint-status.yaml`'s own status vocabulary (see its header comment) is coarser than the spec's frontmatter and is synced only at specific bmad-build workflow transitions (step-03 syncs to `in-progress`; the review outcome syncs separately later) — intentional staged tracking by the workflow tool, not a defect in this story's implementation. (blind-hunter)
- **[defer, maybe-false — would be medium if true]** Claimed `ProvisionSchemaAsync` could be called with an unpersisted `Tenant` (`Id == Guid.Empty`), creating an orphaned schema with no `public.tenants` row. Not demonstrated reachable: every current caller (all tests) persists the tenant via `SaveChangesAsync` before calling `ProvisionSchemaAsync`, and the real production caller (Story 12.5) doesn't exist yet to confirm or violate this precondition. Settle by checking Story 12.5's call site once built, or add an explicit `Id != Guid.Empty` guard proactively. (edge-case-hunter)

## Design Notes

No existing code in this repository points EF Core's migration runner at a non-`public` schema. The approach: open a connection whose `SearchPath` targets the new schema, then drive a fresh `FormForgeDbContext` instance's migrator against it — since `MigrateAsync()` creates and reads `__EFMigrationsHistory` through the *connection's* effective schema resolution, not a value baked into the compiled model.

```csharp
var csb = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schemaName };
await using var connection = new NpgsqlConnection(csb.ConnectionString);
var options = new DbContextOptionsBuilder<FormForgeDbContext>()
    .UseNpgsql(connection)
    .Options;
await using var tenantDb = new FormForgeDbContext(options);
await tenantDb.Database.MigrateAsync(ct);
```

`HasDefaultSchema` is not used — it is static per compiled model and can't vary per call. Every migration's `Up()` runs unmodified, in original order, against whatever schema `SearchPath` resolves unqualified names to. Verify this actually works against a real Postgres container before trusting it (Testcontainers, not mocks) — this is the one assumption the whole story rests on.

**Exception:** three migrations (`CreateDatasetManagerFoundation.cs`, `GrantPreviewRoleOnProvisionedTables.cs`, `RestrictPreviewRoleUsersColumns.cs`) contain raw SQL that hardcodes `public`/`public.<table>` rather than relying on `SearchPath` resolution — so those specific statements always target `public`, never the tenant schema, regardless of which schema is being replayed into. This is harmless: the statements are idempotent (guarded `IF NOT EXISTS`/`IF EXISTS`, or naturally idempotent GRANT/REVOKE), so replaying them into every tenant schema is a redundant no-op against `public` rather than a fork of the migration set. But it means the "every migration... against whatever schema `SearchPath` resolves" claim above is not literally true for these three; keep that in mind if a future migration adds non-idempotent hardcoded-schema SQL.

## Verification

**Commands:**
- `dotnet build` -- expected: 0 errors, 0 warnings
- `dotnet test src/FormForge.Api.Tests --filter TenantProvisioningServiceTests` -- expected: all pass, including a real query confirming migrated tables exist in the new schema (not just "no exception thrown")
