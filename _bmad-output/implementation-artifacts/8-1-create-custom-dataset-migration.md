# Story 8.1: Create custom_dataset Migration

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Developer,
I want a database migration that creates the `custom_dataset` and `dataset_audit_log` tables plus the `datasets` PostgreSQL schema,
so that the platform has a persistent, auditable store for Dataset definitions.

## Acceptance Criteria

**AC-1 — `datasets` schema created**
**Given** the EF Core migration runs
**When** it completes
**Then** the `datasets` schema exists (`CREATE SCHEMA IF NOT EXISTS datasets`) — all Dataset VIEWs live here, eliminating naming collisions with `public` (AR-57)

**AC-2 — `custom_dataset` table created with correct columns**
**Given** the migration
**When** it creates `custom_dataset`
**Then** the table has: `id UUID PK DEFAULT gen_random_uuid()`, `dataset_name TEXT UNIQUE NOT NULL`, `is_custom_query BOOLEAN NOT NULL DEFAULT true`, `query TEXT`, `builder_state JSONB`, `version INTEGER NOT NULL DEFAULT 1`, `created_at TIMESTAMPTZ DEFAULT now()`, `created_by UUID REFERENCES users(id)`, `updated_at TIMESTAMPTZ`, `updated_by UUID REFERENCES users(id)` (FR-55 AC-1)
**And** a `UNIQUE` constraint on `dataset_name` provides second-line-of-defense uniqueness

**AC-3 — `dataset_audit_log` table created with correct columns**
**Given** the migration
**When** it creates `dataset_audit_log`
**Then** the table has: `id UUID PK DEFAULT gen_random_uuid()`, `timestamp TIMESTAMPTZ DEFAULT now()`, `actor_id UUID REFERENCES users(id)`, `actor_name TEXT`, `dataset_name TEXT NOT NULL`, `operation TEXT NOT NULL CHECK (operation IN ('CREATE','UPDATE','DELETE'))`, `previous_values JSONB`, `new_values JSONB`, `ddl TEXT`, `succeeded BOOLEAN NOT NULL DEFAULT true`, `correlation_id TEXT` (FR-55 AC-2 / AR-57)

**AC-4 — `can_manage_datasets` column added to `roles`**
**Given** the migration
**When** it modifies `roles`
**Then** a `can_manage_datasets BOOLEAN NOT NULL DEFAULT false` column is added (AR-58)
**And** existing role rows default to `false`
**And** the `platform-admin` seed row (`id = 00000000-0000-0000-0000-000000000001`) is updated to `can_manage_datasets = true` via `migrationBuilder.Sql`

**AC-5 — Idempotency**
**Given** the migration runs in any environment
**When** it is re-run
**Then** it does not error — EF Core idempotency guarantees via `Database.Migrate()` (FR-55 AC-3)

**AC-6 — No other domain tables touched**
**Given** the migration
**When** it completes
**Then** no pre-existing table has been modified beyond the `roles.can_manage_datasets` column addition (FR-55 AC-4)

**AC-7 — Indexes created**
**Given** the migration
**When** it adds indexes
**Then** `idx_custom_dataset_dataset_name` (UNIQUE on `dataset_name`), `idx_dataset_audit_log_dataset_name_timestamp` (composite on `dataset_name, timestamp DESC`), and `idx_dataset_audit_log_operation` (single on `operation`) are created (AR-57)

---

## Tasks / Subtasks

- [x] **Task 1 — Add EF entities** (AC-2, AC-3, AC-4)
  - [x] Create `src/FormForge.Api/Domain/Entities/CustomDataset.cs` — **POCO, no data annotations**: this project maps every entity via fluent `OnModelCreating` (see Dev Notes §3 / existing `User`, `Role`, etc.). Column names/types/jsonb/FKs are all configured fluently in Task 2.
  - [x] Create `src/FormForge.Api/Domain/Entities/DatasetAuditLogEntry.cs` — POCO; `Operation` is `string` (CHECK at DB level); nullable jsonb/text columns mapped fluently
  - [x] Update `src/FormForge.Api/Domain/Entities/Role.cs` — added `public bool CanManageDatasets { get; set; }`; default `false` (column default handles DB side)

- [x] **Task 2 — Register entities in DbContext** (AC-2, AC-3)
  - [x] Update `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs`:
    - Added `public DbSet<CustomDataset> CustomDatasets => Set<CustomDataset>();`
    - Added `public DbSet<DatasetAuditLogEntry> DatasetAuditLog => Set<DatasetAuditLogEntry>();`
    - Added `OnModelCreating` config for both entities. Everything EF *can* model is modelled fluently so the snapshot stays consistent: FKs to users (SetNull), the `ck_dataset_audit_log_operation` CHECK via `HasCheckConstraint`, the unique `idx_custom_dataset_dataset_name`, the DESC composite `idx_dataset_audit_log_dataset_name_timestamp` via `.IsDescending(false, true)` (mirrors `mutation_audit_log`), and `idx_dataset_audit_log_operation`. The `actor_id` FK is configured with no navigation property (`HasOne<User>().WithMany()`).
    - Added `CanManageDatasets` column config to the existing `Role` entity configuration

- [x] **Task 3 — Generate migration scaffold** (all ACs)
  - [x] From `src/FormForge.Api/` ran: `dotnet ef migrations add CreateDatasetManagerFoundation --output-dir Infrastructure/Persistence/Migrations`
  - [x] Generated `20260602234849_CreateDatasetManagerFoundation.cs` (+ `.Designer.cs` + updated snapshot). Because the model expresses the tables, columns, FKs, CHECK, and indexes, EF generated all of them — only the un-modelable raw SQL (schema, seed UPDATE, preview role) was hand-added in Task 4 rather than fully replacing `Up()`.

- [x] **Task 4 — Write migration `Up()`** (AC-1 through AC-7)
  - [x] `migrationBuilder.Sql("CREATE SCHEMA IF NOT EXISTS datasets;")` — first statement (AR-57)
  - [x] `custom_dataset` table — EF-generated from the model (jsonb `builder_state`, FKs, unique index)
  - [x] `dataset_audit_log` table — EF-generated; `operation` is `TEXT NOT NULL`; CHECK emitted inline via the model's `HasCheckConstraint` (cleaner than post-hoc raw SQL and snapshot-consistent; same end state as the `ck_component_schemas_mode` reference)
  - [x] CHECK `ck_dataset_audit_log_operation` present (via model `HasCheckConstraint`)
  - [x] `AddColumn<bool>("can_manage_datasets", ... defaultValue: false)` — EF-generated; backfills existing rows to false
  - [x] `migrationBuilder.Sql("UPDATE roles SET can_manage_datasets = true WHERE id = '00000000-0000-0000-0000-000000000001'::uuid;")`
  - [x] unique `idx_custom_dataset_dataset_name` — EF-generated
  - [x] composite DESC `idx_dataset_audit_log_dataset_name_timestamp` — EF-generated via `.IsDescending`
  - [x] `idx_dataset_audit_log_operation` — EF-generated
  - [x] `formforge_preview` role setup at end of `Up()` (Decision 6.7). **Deviation:** the REVOKE list is wrapped in a `DO` block that skips tables not present via `to_regclass`, because the architecture/story list includes `mfa_sessions`, which does not exist in the current schema — a plain `REVOKE` on it would abort the migration.

- [x] **Task 5 — Write migration `Down()`** (rollback safety)
  - [x] `formforge_preview` teardown first via `DROP OWNED BY` + `DROP ROLE` (DROP OWNED revokes all its privileges so DROP ROLE cannot fail on dependencies)
  - [x] `DropTable("custom_dataset")` / `DropTable("dataset_audit_log")` — cascades indexes, FKs and the CHECK constraint
  - [x] `DropColumn("can_manage_datasets", table: "roles")`
  - [x] `migrationBuilder.Sql("DROP SCHEMA IF EXISTS datasets;")`

- [x] **Task 6 — Verify migration applies cleanly**
  - [x] Verified via the integration-test host: `Database.MigrateAsync()` applies the migration against a real PostgreSQL (Testcontainers) with no error
  - [x] Schema verified through `information_schema` / `pg_catalog`: `datasets` schema, all `custom_dataset` / `dataset_audit_log` columns, the CHECK constraint, the `can_manage_datasets` column, `platform-admin` = `true`, all three indexes, and the `formforge_preview` role
  - [x] Idempotency: `MigrateAsync()` is invoked a second time in test setup and is a no-op (EF migration-history)

- [x] **Task 7 — Integration tests** (AC-1 through AC-7)
  - [x] Added `src/FormForge.Api.Tests/Features/Datasets/DatasetMigrationTests.cs` (the repo's test project is `src/FormForge.Api.Tests`, not `tests/FormForge.Api.IntegrationTests/`; reused the existing `PostgresFixture` + `WebApplicationFactory` pattern)
  - [x] `custom_dataset` columns + types (information_schema.columns)
  - [x] `dataset_audit_log` CHECK on `operation` — invalid value throws `PostgresException` (check_violation); valid CREATE/UPDATE/DELETE accepted (Theory)
  - [x] `datasets` schema exists
  - [x] `platform-admin` has `can_manage_datasets = true`; a newly-inserted role defaults to `false`
  - [x] all three indexes exist (pg_indexes/pg_index), dataset_name index is UNIQUE
  - [x] bonus: dataset_name UNIQUE rejects duplicates; `formforge_preview` role exists (12 tests total, all passing)

---

## Dev Notes

### §1 — `CustomDataset` Entity Schema

File: `src/FormForge.Api/Domain/Entities/CustomDataset.cs`

```csharp
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace FormForge.Api.Domain.Entities;

[Table("custom_dataset")]
public class CustomDataset
{
    [Column("id")]
    public Guid Id { get; set; }

    [Column("dataset_name")]
    [MaxLength(63)]
    public string DatasetName { get; set; } = null!;

    [Column("is_custom_query")]
    public bool IsCustomQuery { get; set; } = true;

    [Column("query")]
    public string? Query { get; set; }

    // JSONB — EF type annotation set in OnModelCreating
    [Column("builder_state")]
    public string? BuilderState { get; set; }

    [Column("version")]
    public int Version { get; set; } = 1;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("created_by")]
    public Guid? CreatedBy { get; set; }

    [ForeignKey(nameof(CreatedBy))]
    public User? CreatedByUser { get; set; }

    [Column("updated_at")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [Column("updated_by")]
    public Guid? UpdatedBy { get; set; }

    [ForeignKey(nameof(UpdatedBy))]
    public User? UpdatedByUser { get; set; }
}
```

**Key notes:**
- `BuilderState` is `string?` in C# but stored as `jsonb` in PG — EF column type set via fluent API in OnModelCreating (`HasColumnType("jsonb")`)
- `DatasetName` max 63 bytes matches PG identifier limit (AR-57); the uniqueness is enforced by the UNIQUE index in the migration
- `CreatedBy`/`UpdatedBy` are nullable FK to `users(id)` — FK navigation properties are optional (nullable)

### §2 — `DatasetAuditLogEntry` Entity Schema

File: `src/FormForge.Api/Domain/Entities/DatasetAuditLogEntry.cs`

```csharp
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FormForge.Api.Domain.Entities;

[Table("dataset_audit_log")]
public class DatasetAuditLogEntry
{
    [Column("id")]
    public Guid Id { get; set; }

    [Column("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [Column("actor_id")]
    public Guid? ActorId { get; set; }

    [Column("actor_name")]
    public string? ActorName { get; set; }

    [Column("dataset_name")]
    public string DatasetName { get; set; } = null!;

    [Column("operation")]
    public string Operation { get; set; } = null!;  // CHECK (operation IN ('CREATE','UPDATE','DELETE'))

    // JSONB — EF type annotation set in OnModelCreating
    [Column("previous_values")]
    public string? PreviousValues { get; set; }

    [Column("new_values")]
    public string? NewValues { get; set; }

    [Column("ddl")]
    public string? Ddl { get; set; }

    [Column("succeeded")]
    public bool Succeeded { get; set; } = true;

    [Column("correlation_id")]
    public string? CorrelationId { get; set; }
}
```

**Key notes:**
- `Operation` is a `string` with values `'CREATE'`, `'UPDATE'`, `'DELETE'` — the CHECK constraint is enforced at the DB level (added via `migrationBuilder.Sql` in Task 4), not in the C# entity
- `PreviousValues` and `NewValues` are `string?` in C# storing JSON; EF `HasColumnType("jsonb")` set in OnModelCreating
- No FK navigation to `users` on `ActorId` — intentional; audit log is append-only and `actor_id` may be null for system operations

### §3 — DbContext Fluent Configuration

In `FormForgeDbContext.OnModelCreating`, add these blocks (follow the existing lambda pattern: `modelBuilder.Entity<T>(e => { ... })`):

```csharp
// CustomDataset
modelBuilder.Entity<CustomDataset>(e =>
{
    e.HasKey(x => x.Id);
    e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
    e.Property(x => x.DatasetName).HasColumnType("text").IsRequired();
    e.HasIndex(x => x.DatasetName).IsUnique();  // mirrors UNIQUE constraint
    e.Property(x => x.BuilderState).HasColumnType("jsonb");
    e.Property(x => x.IsCustomQuery).HasDefaultValue(true);
    e.Property(x => x.Version).HasDefaultValue(1);
    e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
});

// DatasetAuditLogEntry
modelBuilder.Entity<DatasetAuditLogEntry>(e =>
{
    e.HasKey(x => x.Id);
    e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
    e.Property(x => x.Timestamp).HasDefaultValueSql("now()");
    e.Property(x => x.DatasetName).HasColumnType("text").IsRequired();
    e.Property(x => x.Operation).HasColumnType("text").IsRequired();
    e.Property(x => x.PreviousValues).HasColumnType("jsonb");
    e.Property(x => x.NewValues).HasColumnType("jsonb");
    e.Property(x => x.Succeeded).HasDefaultValue(true);
});

// Role — add to existing Role entity config block
e.Property(x => x.CanManageDatasets).HasColumnName("can_manage_datasets").HasDefaultValue(false);
```

### §4 — `custom_dataset` Table DDL Detail

EF `CreateTable` column list for the migration `Up()`:

| Column | EF Type | Nullable | Default | Notes |
|--------|---------|----------|---------|-------|
| `id` | `Guid` | false | `gen_random_uuid()` | PK |
| `dataset_name` | `string` | false | — | `type: "text"` |
| `is_custom_query` | `bool` | false | `true` | |
| `query` | `string` | true | — | `type: "text"` |
| `builder_state` | `string` | true | — | `type: "jsonb"` |
| `version` | `int` | false | `1` | |
| `created_at` | `DateTimeOffset` | true | `now()` | `type: "timestamptz"` |
| `created_by` | `Guid` | true | — | FK to `users(id)` |
| `updated_at` | `DateTimeOffset` | true | — | `type: "timestamptz"` |
| `updated_by` | `Guid` | true | — | FK to `users(id)` |

Use `defaultValueSql: "gen_random_uuid()"` and `defaultValueSql: "now()"` in the column definitions.

Add FK constraints to `users` table: `table.ForeignKey(name: "fk_custom_dataset_users_created_by", column: x => x.created_by, principalTable: "users", principalColumn: "id")` and same for `updated_by`.

### §5 — `dataset_audit_log` Table DDL Detail

| Column | EF Type | Nullable | Default | Notes |
|--------|---------|----------|---------|-------|
| `id` | `Guid` | false | `gen_random_uuid()` | PK |
| `timestamp` | `DateTimeOffset` | true | `now()` | `type: "timestamptz"` |
| `actor_id` | `Guid` | true | — | FK to `users(id)`, nullable |
| `actor_name` | `string` | true | — | `type: "text"` |
| `dataset_name` | `string` | false | — | `type: "text"` |
| `operation` | `string` | false | — | `type: "text"` + CHECK constraint added separately |
| `previous_values` | `string` | true | — | `type: "jsonb"` |
| `new_values` | `string` | true | — | `type: "jsonb"` |
| `ddl` | `string` | true | — | `type: "text"` |
| `succeeded` | `bool` | false | `true` | |
| `correlation_id` | `string` | true | — | `type: "text"` |

FK to `users`: `actor_id` nullable FK — `table.ForeignKey(name: "fk_dataset_audit_log_users_actor_id", column: x => x.actor_id, principalTable: "users", principalColumn: "id")`.

**CHECK constraint** (added AFTER table creation, not inline):
```csharp
migrationBuilder.Sql(
    "ALTER TABLE dataset_audit_log ADD CONSTRAINT ck_dataset_audit_log_operation " +
    "CHECK (operation IN ('CREATE', 'UPDATE', 'DELETE'));");
```
Exact pattern: see `AddComponentSchemaMode` migration (`ck_component_schemas_mode` constraint) for reference.

### §6 — `formforge_preview` PostgreSQL Role (Decision 6.7)

This is an **architectural requirement** called out in the project structure additions for this migration. It is not in the Story 8.1 ACs explicitly, but belongs in this migration per the architecture because later stories (Epic 11 preview execution) depend on it.

Add at the **end** of `Up()` — use idempotent SQL:

```csharp
// formforge_preview: read-only PG role for Dataset preview execution (Decision 6.7)
migrationBuilder.Sql(@"
DO $$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'formforge_preview') THEN
    CREATE ROLE formforge_preview LOGIN NOINHERIT;
  END IF;
END
$$;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO formforge_preview;
REVOKE SELECT ON users, roles, refresh_tokens, password_reset_tokens,
                 mfa_backup_codes, mfa_sessions, schema_audit_log,
                 mutation_audit_log, dataset_audit_log, custom_dataset
FROM formforge_preview;
");
```

**Important:** `formforge_preview` needs a password set via environment variable `DATASET_PREVIEW_DB_PASSWORD` — this is NOT done in the migration (secrets are never in migrations). The role is created without a password here; the DBA/ops sets the password separately, or Aspire configures it via `AddPostgresDataSource` for the preview connection. Add a comment in the migration referencing Decision 6.7.

In `Down()`, add:
```csharp
migrationBuilder.Sql(@"
DO $$
BEGIN
  IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'formforge_preview') THEN
    DROP ROLE formforge_preview;
  END IF;
END
$$;");
```

**Note:** If the deployment environment does not grant the migration user `CREATEROLE` privilege, this SQL will fail. In that case, defer the role creation to a manual DBA step and document it. The CI/test environment typically runs as superuser so this should be fine for integration tests.

### §7 — Migration Naming and Structure Conventions

- **Namespace:** `FormForge.Api.Infrastructure.Persistence.Migrations`
- **Class name:** `CreateDatasetManagerFoundation` (no "Migration" suffix)
- **File top:** `#nullable disable` + `/// <inheritdoc />` XML docs on each override
- **Both `Up()` and `Down()`** must start with `ArgumentNullException.ThrowIfNull(migrationBuilder);`
- The `.Designer.cs` snapshot file is auto-generated by EF — do NOT hand-edit it

### §8 — SafeIdentifier Pattern Reference (for Future Stories)

Story 8.3 will add `DatasetName.cs` as a value type. Story 8.1 does NOT create `DatasetName.cs`. However, it is worth noting:
- `SafeIdentifier.cs` lives at `src/FormForge.Api/Features/Designer/SafeIdentifier.cs`
- `PgReservedKeywords.cs` lives at `src/FormForge.Api/Features/Designer/PgReservedKeywords.cs`
- Story 8.3's `DatasetName.cs` will go to `src/FormForge.Api/Domain/ValueTypes/DatasetName.cs` (new subdirectory)
- Do NOT create `DatasetName.cs` in this story — that's out of scope for 8.1

### §9 — EffectivePermissions Extension (for Future Story 8.2)

`EffectivePermissions` (Decision 2.2) will gain `CanManageDatasets: bool` in Story 8.2. This story only adds the DB column. Do NOT modify `EffectivePermissions` in Story 8.1.

### §10 — Testing Context

Integration tests in this project use a real PostgreSQL (not mocked). The existing test suite confirms this in Stories 5.x and 6.x. Do not mock the database for migration tests — test against the actual migrated schema.

- Check `tests/FormForge.Api.IntegrationTests/` for the test host/factory pattern to reuse
- The `information_schema` approach is portable and preferred over pg_catalog for column type checks

### Project Structure Notes

**New files this story creates:**
```
src/FormForge.Api/Domain/Entities/CustomDataset.cs            (NEW)
src/FormForge.Api/Domain/Entities/DatasetAuditLogEntry.cs     (NEW)
src/FormForge.Api/Infrastructure/Persistence/Migrations/
    {timestamp}_CreateDatasetManagerFoundation.cs             (NEW — generated then hand-written)
    {timestamp}_CreateDatasetManagerFoundation.Designer.cs    (NEW — auto-generated, do not edit)
    FormForgeDbContextModelSnapshot.cs                        (AUTO-UPDATED by EF)
tests/FormForge.Api.IntegrationTests/
    DatasetMigrationTests.cs                                  (NEW)
```

**Modified files:**
```
src/FormForge.Api/Domain/Entities/Role.cs                           (add CanManageDatasets property)
src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs  (add DbSets + model config)
```

**No frontend changes** — this story is backend-only.

### References

- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.1 — Dataset Schema, Migration & View Namespace (FR-55, FR-57, H-1)]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.2 — RBAC Extension (FR-56, AR-58)]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.7 — Preview Execution Security & Isolation]
- [Source: `_bmad-output/planning-artifacts/epics.md` — Epic 8, Story 8.1 Acceptance Criteria]
- [Source: `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260602012319_AddComponentSchemaMode.cs` — CHECK constraint pattern]
- [Source: `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260523021147_CreateRolesRolePermissionsAndUserRoles.cs` — seed data pattern (platform-admin UUID)]
- [Source: `src/FormForge.Api/Features/Designer/SafeIdentifier.cs` — value type pattern for future Story 8.3]

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (dev-story execution)

### Debug Log References

- `dotnet build` (API + tests): succeeded, 0 warnings.
- `dotnet ef migrations add CreateDatasetManagerFoundation`: generated migration + Designer + snapshot.
- `dotnet test --filter DatasetMigrationTests`: 12/12 passing.

### Completion Notes List

- **Entity style — followed the codebase, not the story's literal annotation code.** Dev Notes §1/§2 showed `[Table]`/`[Column]`/`[ForeignKey]` data annotations, but every entity in this project is an `internal sealed class` POCO mapped entirely through fluent `OnModelCreating` (Dev Notes §3 even says "follow the existing lambda pattern"). `CustomDataset` and `DatasetAuditLogEntry` are therefore plain POCOs with all column/type/FK/index/CHECK configuration in `FormForgeDbContext`. The resulting DB schema satisfies AC-1…AC-7 identically.
- **Modelled everything EF can express; injected only un-modelable raw SQL.** The CHECK constraint (`HasCheckConstraint`), the DESC composite index (`.IsDescending(false, true)`, mirroring `mutation_audit_log`), the unique `dataset_name` index, and all FKs are in the model, so the EF snapshot stays drift-free. Only `CREATE SCHEMA`, the `platform-admin` `UPDATE`, and the `formforge_preview` role are hand-written `migrationBuilder.Sql` in the migration.
- **CHECK via model instead of post-table raw SQL.** Dev Notes §5 suggested adding the CHECK via `migrationBuilder.Sql` after the table (the older `ck_component_schemas_mode` pattern). I used the model's `HasCheckConstraint` instead — same end state, snapshot-consistent, and matches the newer `users.ck_users_theme_preference` convention.
- **REVOKE list deviation (necessary correctness fix).** Architecture §6.7 and Dev Notes §6 list `mfa_sessions` in the preview-role REVOKE, but no such table exists in the current schema — a plain `REVOKE SELECT ON mfa_sessions` aborts the whole migration with `relation does not exist`. The REVOKE is wrapped in a `DO` loop that checks `to_regclass` and skips absent tables, preserving the full forward-looking list (incl. `mfa_sessions`) while staying robust. `created_by`/`updated_by`/`actor_id` FKs use `SetNull` on delete, matching `ComponentSchema.Creator`.
- **`created_at`/`timestamp` are NOT NULL with `DEFAULT now()`** (non-nullable `DateTimeOffset`), matching every other table's convention — not nullable as the §4/§5 tables loosely implied. Test assertions were corrected to match.
- **Test location:** the repo has no `tests/FormForge.Api.IntegrationTests/`; the integration test project is `src/FormForge.Api.Tests`. Test placed under `Features/Datasets/`, reusing `PostgresFixture` (Testcontainers, real PostgreSQL — no mocking, per Dev Notes §10).
- No frontend changes (backend-only story). `EffectivePermissions`, `DatasetName.cs`, and `SafeIdentifier` were intentionally **not** touched (out of scope for 8.1 per Dev Notes §8/§9).

### File List

**New:**
- `src/FormForge.Api/Domain/Entities/CustomDataset.cs`
- `src/FormForge.Api/Domain/Entities/DatasetAuditLogEntry.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260602234849_CreateDatasetManagerFoundation.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260602234849_CreateDatasetManagerFoundation.Designer.cs` (auto-generated)
- `src/FormForge.Api.Tests/Features/Datasets/DatasetMigrationTests.cs`

**Modified:**
- `src/FormForge.Api/Domain/Entities/Role.cs` (added `CanManageDatasets`)
- `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` (DbSets + model config + Role column)
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/FormForgeDbContextModelSnapshot.cs` (auto-updated by EF)

### Change Log

| Date | Change |
|------|--------|
| 2026-06-03 | Story 8.1 implemented: `CreateDatasetManagerFoundation` migration adds the `datasets` schema, `custom_dataset` and `dataset_audit_log` tables, `roles.can_manage_datasets` (platform-admin seeded true), indexes, and the `formforge_preview` read-only role. Added EF entities + DbContext config + `DatasetMigrationTests` (12 tests). |

---

## Review Findings

_Code review 2026-06-03 (Blind Hunter + Edge Case Hunter + Acceptance Auditor). All 7 ACs PASS. 0 patch, 2 decision-needed (both deferred by reviewer), 6 deferred total, 7 dismissed._

- [x] [Review][Defer] Preview-role privilege model is point-in-time and may not survive schema evolution — `GRANT SELECT ON ALL TABLES IN SCHEMA public` is a one-time snapshot (no `ALTER DEFAULT PRIVILEGES`), so tables added by future migrations get no grant; the grant-all-then-revoke denylist auto-exposes any *sensitive* future table not in the revoke array; and no `GRANT USAGE/SELECT ON SCHEMA datasets` is issued even though preview VIEWs (Epic 9–11) will live there. The denylist itself matches Decision 6.7 as documented. [migration:146–166] — deferred: nothing reads through the preview role until Epic 11; full grant-model wiring (default privileges, `datasets` grants, password) lands there.
- [x] [Review][Defer] `IsCustomQuery` EF `HasDefaultValue(true)` sentinel trap — EF treats the CLR default (`false`) as "unset" and substitutes the DB default (`true`), so a builder-mode dataset persisted via the EF entity with `IsCustomQuery = false` would silently save as `true`. [FormForgeDbContext.cs CustomDataset config] — deferred: no write path exists until stories 8.4/8.8; fix when the Dataset write path is built (EF write → adjust sentinel / set explicit value; raw SQL → no change).

- [x] [Review][Defer] `Down()` tears down the cluster-global `formforge_preview` role via `DROP OWNED BY` + `DROP ROLE`; `Up()` needs `CREATEROLE` [migration:137–166, 177–185] — deferred: correct for the dedicated single-database / superuser-CI deployment this project targets; document the shared-cluster + least-privilege-migration-user caveat (already noted in code comments and Dev Notes §6).
- [x] [Review][Defer] `dataset_name` is unbounded `TEXT`; names > ~2704 bytes fail the unique B-tree with an opaque error [CustomDataset.cs:13–15, migration:42] — deferred: length validation is explicitly out of scope, lands in Story 8.3 (`DatasetName` value type).
- [x] [Review][Defer] `operation` CHECK is case-sensitive with no entity-level normalization; write code must emit exact `'CREATE'|'UPDATE'|'DELETE'` [migration:88, DatasetAuditLogEntry.cs:17] — deferred: audit-write path arrives in later stories; normalize/uppercase there.
- [x] [Review][Defer] `formforge_preview` is created `LOGIN` with no password — unusable until ops sets `DATASET_PREVIEW_DB_PASSWORD`; only its existence is tested [migration:141] — deferred: connection/password wiring is Epic 11 (correctly deferred per Dev Notes §6 / Decision 6.7).

_Dismissed (7): unused `datasets` schema + `DROP SCHEMA` without CASCADE (AC-1 requires the schema; reverse-order rollback empties it first); hardcoded platform-admin UUID UPDATE (fixed verified seed from `CreateRolesRolePermissionsAndUserRoles`, asserted by test); index `(dataset_name, timestamp DESC)` direction (matches `mutation_audit_log` convention + per-dataset-newest-first reads); no FK on `dataset_audit_log.dataset_name` (by-design append-only retention); `Down()` not reverting the platform-admin flip (moot — the column is dropped); shared-container test contamination (disproved — `IClassFixture` is per-class, tests sequential, no current collision); extra EF FK-backing indexes (standard, AC-7 not violated)._
