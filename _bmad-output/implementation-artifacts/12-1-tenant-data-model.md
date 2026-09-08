---
title: 'Story 12.1: Tenant Data Model'
type: 'feature'
created: '2026-09-08'
status: 'done'
route: 'oneshot'
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** FormForge has no way to represent a tenant. Every subsequent piece of the multi-tenant architecture (provisioning, JWT tenant-context resolution, the tenants admin UI) needs a `tenants` row to resolve against, but no such table or entity exists yet.

**Approach:** Add a `Tenant` EF Core entity (`id`, `name`, `schema_name` unique, `status` with a CHECK constraint, `created_at`, `created_by`) mapped in `FormForgeDbContext`, following this codebase's existing plain-POCO + Fluent-API pattern — matching `users.theme_preference`'s `HasCheckConstraint`/`AddCheckConstraint` style rather than the older raw-SQL `component_schemas.mode` style. Generate the corresponding EF Core migration via `dotnet ef migrations add`. `schema_name` uniqueness and format validity are enforced at the database layer by this story (unique index + a length bound matching `SafeIdentifier`'s rule); the runtime creation flow that calls `SafeIdentifier.TryCreate` against user input and returns a friendly validation error is Story 12.2's responsibility (Tenant Provisioning Service), not this one — this story only produces the data model.

</frozen-after-approval>

## Implementation Notes

- Added `Domain/Entities/Tenant.cs` (plain POCO, matching `User`/`Role` conventions: `internal sealed class`, `Guid Id`, string properties `= string.Empty`, nullable `Guid? CreatedBy`).
- Mapped `Tenant` in `FormForgeDbContext.OnModelCreating`, following the `users.theme_preference` Fluent `HasCheckConstraint`/`AddCheckConstraint` pattern (not the older raw-SQL `component_schemas.mode` pattern the architecture doc originally cited — investigation found the Fluent approach is the current, EF-native one already used elsewhere in this codebase).
- `schema_name` uniqueness/format is enforced by a DB unique index (`uq_tenants_schema_name`) and a 63-char max length matching `SafeIdentifier`'s rule. Per-request `SafeIdentifier.TryCreate` validation and a friendly collision error are explicitly out of scope for this story — deferred to Story 12.2 (Tenant Provisioning Service), since this story has no creation endpoint or service yet. Noted in the frozen Intent.
- `created_by` is a nullable `Guid` with **no FK constraint** — `platform_admins` (Story 12.4) does not exist yet. The constraint is Story 12.4's job to add once the referenced table is real.
- Generated migration `20260908035446_AddTenants` via `dotnet ef migrations add`; verified the emitted SQL (CHECK constraint + unique index) matches intent by reading the generated file.
- Surprise: a clean build of the freshly generated migration failed on `CA1062` (parameter null-check) — a rule not yet in the project's existing Migrations-folder suppression block in `.editorconfig`, even though the same rule would apply to every prior migration's `Up`/`Down` methods. Added `dotnet_diagnostic.CA1062.severity = none` to that existing suppression block (same file, same pattern as CA1515/CA1814/CA1861) rather than to a new location.
- Added `Features/Tenancy/TenantIntegrationTests.cs` (3 tests: valid round-trip + default status, unique-constraint violation, CHECK-constraint violation) using the same `PostgresFixture` + `WebApplicationFactory<Program>` shape as every other integration test, since no lighter DbContext-only test pattern exists in this codebase yet. All 3 pass locally.
- Verification: `dotnet build src/FormForge.Api.Tests` — 0 errors, 0 warnings. `dotnet test --filter TenantIntegrationTests` — 3/3 passed.
- Full-suite regression run (`dotnet test src/FormForge.Api.Tests`, 1133 tests): 2 pre-existing, unrelated failures (audit-log DELETE-verb 405 checks) — logged to `deferred-work.md`, not caused by this change.
- Blind Hunter review ran; 4 findings patched, 1 disproven (false), 2 deferred. See Review Triage Log below. Post-patch: `dotnet build` (full solution) 0 errors/0 warnings; `dotnet test --filter TenantIntegrationTests` 8/8 passed.

## Review Triage Log

- **high** — Migration `Up`/`Down` omitted `ArgumentNullException.ThrowIfNull(migrationBuilder)`, present in every one of the other 23 migrations; the change had instead added a new `.editorconfig` CA1062 suppression with a factually-wrong comment. Evidence: read `20260617034800_AddDatasetQueryType.cs`, confirmed the pattern. **Fixed:** added the two null-checks to the new migration, reverted the `.editorconfig` change.
- **medium** — `AddTenant_DuplicateSchemaName_ThrowsOnUniqueConstraint` and `AddTenant_InvalidStatus_ThrowsOnCheckConstraint` only asserted `DbUpdateException`, not the specific `PostgresException.SqlState`/`ConstraintName`, so either test would pass for an unrelated failure. Evidence: compared against `DatasetMigrationTests.cs`'s `PostgresErrorCodes`/`ConstraintName` pattern. **Fixed:** both tests now unwrap `InnerException` and assert `SqlState` + `ConstraintName`.
- **low** — No test exercised the `Active`/`Suspended` CHECK values or the `SchemaName` 63-char boundary or a NOT NULL violation. **Fixed:** added a `[Theory]` for all three valid status values, plus `AddTenant_SchemaNameTooLong_ThrowsOnLengthLimit` and `AddTenant_MissingSchemaName_ThrowsOnNotNullConstraint`.
- **low** — `epic-12-context.md` stated as settled fact that `created_by` "references `platform_admins`" with no caveat, when no FK exists until Story 12.4. **Fixed:** reworded to say the relationship is conceptual only in this story, not yet enforced.
- **false** — Claimed `schema_name` lacking a DB-level format/reserved-keyword CHECK is "inconsistent" with `status` getting one. Disproof: grepped the DbContext/migrations for any equivalent format CHECK on `component_schemas.designer_id` (the closest analog, also `SafeIdentifier`-validated) — none exists. Identifier format validation is an application-layer concern (`SafeIdentifier`) everywhere else in this codebase; only small fixed-value columns like `status`/`mode` get DB CHECK constraints. Not an inconsistency.
- **maybe-false, not worth fixing (status timing)** — Reviewer flagged `sprint-status.yaml`/frontmatter as `in-progress` while Implementation Notes read as finished. This was correct at the time the review ran — the review runs mid-workflow, before the Finalize Spec step (this step) sets `status: done` and syncs sprint-status to `review`. No code change needed; sequencing works as designed.
- **defer** — `Tenant` has no `UpdatedAt` column. Not required by this story's spec or by architecture.md Decision 7.1; would be speculative to add now. Logged to `deferred-work.md`.
- **defer** — `Tenant.CreatedBy` has no index. No relationship is configured yet (no `platform_admins` table until Story 12.4); the index belongs with the FK Story 12.4 adds. Logged to `deferred-work.md`.

