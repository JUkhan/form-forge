---
stepsCompleted: [1, 2, 3, 4, 5, 6, 7, 8]
inputDocuments:
  - _bmad-output/planning-artifacts/prds/prd-tinnitus-2026-05-22/prd.md
  - _bmad-output/planning-artifacts/prds/prd-tinnitus-2026-05-22/addendum.md
workflowType: 'architecture'
project_name: 'FormForge (tinnitus)'
user_name: 'jukhan'
date: '2026-05-22'
lastStep: 8
status: 'complete'
completedAt: '2026-05-22'
updatedAt: '2026-06-03'
prdUpdateNotes: 'FR-50..53 added (welcome email, forgot password, password change, TOTP MFA); decisions 2.9–2.12; AD-12 + OQ-7 resolved. 2026-06-02: FR-54 Component Mode (CRUD/VIEW) added — decisions 1.8 + 4.11, DynamicComponent VIEW read-only (4.10); story collision resolved (a11y DnD renumbered B-11); OQ-9 noted. 2026-06-03: FR-55..73 Dataset Manager (Epics H–K) added — decisions 6.1–6.13; AD-14..19 + OQ-11 + OQ-13 resolved; 19 new FRs, 13 new decisions'
---

# Architecture Decision Document — FormForge

_This document builds collaboratively through step-by-step discovery. Sections are appended as we work through each architectural decision together._

## Project Context Analysis

### Requirements Overview

**Functional Requirements:** 73 FRs across 11 epics — Identity & Permissions (A: FR-1..7, FR-50..53), Component Schema Designer (B, ported from ESG Platform; FR-54 component mode), Menu Management (C), Dynamic Table Provisioning (D), Generic CRUD Service (E), UI/UX & Theming (F), Platform/Cross-Cutting (G), Dataset Foundation & Custom Query (H: FR-55..62), Query Builder Canvas & Joins (I: FR-63..66), Builder Config (J: FR-67..69), SQL Generation, Preview & View Sync (K: FR-70..73).

The architecturally consequential FR clusters are:
- **Runtime DDL (FR-23 to FR-28):** Validated `designerId` and `fieldKey` identifiers, additive-only `CREATE`/`ALTER TABLE`, Repeater child-table provisioning with FK + index, recursive cycle detection, append-only schema audit log.
- **Generic dynamic CRUD (FR-29 to FR-36):** Whitelisted filter/sort against schema registry, soft-delete with application-level transitive cascade across Repeater graph, nested Repeater writes in a single transaction, append-only mutation audit log.
- **Dual-layer permission system (FR-4 to FR-7):** Server effective-permission helper on every endpoint; client hides controls; permission set derived from DB-validated active roles with ≤30s cache and immediate bust on assignment change.
- **Designer port (FR-8):** Lift-and-refactor of 6 files from ESG Platform reference codebase, preserving DynamicComponent behavioral contract (visibility engine, Repeater row scope, `submitRef`, validity/ready callbacks, shallow-equal initial data).
- **Schema versioning lifecycle (FR-13):** Draft → Published → Archived; at most one Published per Designer; menu bindings pin to `{designerId, version}`.
- **Component Mode (FR-54):** each Designer carries a `CRUD` or `VIEW` mode (`component_schemas.mode`, `NOT NULL`, set at creation, immutable). VIEW components never provision a table, expose no `/api/data/{designerId}` endpoints, render read-only via DynamicComponent, and are governed solely by Menu `allowedRoles` (the per-Resource CRUD flags are not evaluated). This bifurcates the provisioning, CRUD-endpoint, and permission paths on a single column.

**Non-Functional Requirements:**
- **Performance:** `/api/data/{designerId}` p95 < 200 ms at 100k rows with indexes on system columns; designer save p95 < 500 ms; navbar fetch p95 < 100 ms (cached, TTL 5 s, write-invalidated).
- **Security:** JWT access token in-memory (15-min TTL), refresh token in HttpOnly + SameSite=Strict cookie (7-day TTL, single-use rotation, server-stored). All dynamic SQL identifiers whitelisted against schema registry; all values parameterized. File uploads validated for type and size before MinIO storage.
- **Auditability:** Both schema-change and CRUD-mutation audit logs are append-only (no deletion API).
- **Reliability:** All DDL runs in explicit PostgreSQL transactions with full rollback. Aspire/Docker restart policy handles process restarts.
- **Accessibility:** WCAG 2.1 AA — keyboard reachable, axe-core zero critical violations on rendered forms, DnD keyboard equivalents.
- **Browser support:** Latest 2 versions of Chrome/Edge/Firefox/Safari.
- **i18n:** Architecture-ready (externalized strings, `t('key')` everywhere); English-only at launch.

**Scale & Complexity:**

- **Primary domain:** Full-stack web — React SPA + .NET 10 ASP.NET Core Minimal APIs + PostgreSQL + MinIO.
- **Complexity level:** High — driven by runtime DDL, recursive Repeater provisioning, EF/Dapper transaction boundary, and dual-layer permission caching. Feature count alone would suggest medium; the runtime-DDL backbone elevates it.
- **Deployment model:** Single-tenant, internal-users-only, ≤100k rows/table target (offset pagination acceptable for v1; keyset deferred to v2).
- **Estimated architectural components:**
  - Static-schema data layer (EF Core): `users`, `roles`, `user_roles`, `menus`, `component_schemas`, `refresh_tokens`, `schema_audit_log`, `mutation_audit_log`.
  - Dynamic-schema data layer (Dapper): runtime-provisioned tables per `designerId` + per-child Repeater table.
  - Auth subsystem (JWT issuance, refresh rotation, deactivation revocation).
  - Effective-permission engine + cache.
  - Schema registry cache (validates dynamic SQL, drives serialization, enables cycle detection and soft-delete cascade walk).
  - Provisioning service (DDL emitter, cycle detector, schema-drift reporter).
  - Generic CRUD service (list/get/create/partial-update/soft-delete/restore/nested-Repeater).
  - Designer (ported React UI: canvas, palette, properties panel, library, preview, version manager).
  - DynamicComponent renderer (shared between designer preview and data-entry UI).
  - Menu management UI + dynamic permission-filtered navbar.
  - Admin settings area (Users, Roles, Menus, Designers, Audit Logs).
  - Theming (3 themes, server-persisted preference, no-flash hydration).
  - Cross-cutting: structured logging, correlation IDs, health checks, OpenAPI spec, MinIO presigned URL generator.
  - **Dataset Manager (Epics H–K):** `custom_dataset` + `dataset_audit_log` EF tables; `datasets` PostgreSQL schema for VIEW isolation; transactional view lifecycle service (CREATE/REPLACE/RENAME/DROP); server-authoritative SQL generator from `builder_state` JSON; `PgQuery.NET` SELECT-only enforcer; table allowlist + `information_schema` catalog service; dedicated read-only preview execution pool; React Flow canvas (Table Palette, TableNode, JoinEdge, JoinInspector, FilterConditionsDialog, OrderByPanel); `builderState.ts` cross-layer schema contract.

### Technical Constraints & Dependencies

**Locked (from PRD addendum §1):**
- Backend: .NET Aspire orchestration, ASP.NET Core Minimal APIs on .NET 10 (assumed LTS), PostgreSQL, MinIO (S3-compatible), EF Core (static), Dapper (dynamic).
- Frontend: React 19.2.4, shadcn/ui (Tailwind), TanStack React Query v5, react-hook-form + Zod resolver, React Router v7 (assumption).

**Reused asset:**
- ESG Platform designer at `C:\Users\MY\Downloads\esg_platform\BkmeaEsgPlatform` (6 files audited; native HTML5 DnD; 14 component types; versioning already implemented).

**Environmental:**
- Dual local-dev paths: Aspire AppHost (`dotnet run`) and Docker Compose (`docker compose up`). Both must be first-class.
- All connection strings and service URLs injected via env vars; no hardcoded ports in business code.

### Cross-Cutting Concerns Identified

1. **Authentication & Dual-Layer Authorization** — gates every endpoint and every UI control. Permission cache invalidation strategy is an architect handoff item (AD-2).
2. **Append-Only Audit Logging** — two logs (schema DDL + CRUD mutations), both EF Core managed for migration consistency, both with retention/indexing strategy pending (AD-10).
3. **Schema Registry Cache** — validates every dynamic SQL identifier, drives serialization (presigned URLs for Image columns), enables Repeater cycle detection and transitive soft-delete cascade. Structure and eviction policy pending (AD-7).
4. **EF Core + Dapper Transaction Boundary** — Schema Binding (EF) triggers Table Provisioning (Dapper); must define shared connection/transaction or accept separated retry model (AD-11).
5. **Identifier Sanitization Pipeline** — input regex + server-side whitelist before any DDL or dynamic CRUD SQL; complete reserved-keyword list pending (AD-3).
6. **Structured Logging with Correlation IDs** — propagated through every request and audit entry; DDL/CRUD logged with SQL fingerprint, never parameter values.
7. **Schema Versioning Discipline** — at-most-one-Published invariant, version-pinned menu bindings, version diff preview before re-bind (AD-6).
8. **Mobile-First Responsive + WCAG 2.1 AA** — affects component primitives, DnD interactions, and designer save validation (FR-42, R-7).
9. **Theming with No-Flash Hydration** — 3 themes, server-persisted, applied before React hydrates (R-10).
10. **i18n Architecture** — every user-facing string keyed; API errors carry string key + English message.
11. **OpenAPI Generation** — dynamic-CRUD endpoints documented with `additionalProperties: true` for v1; per-Designer schema generation deferred (AD-8).
12. **Health Checks + Graceful Degradation** — MinIO outage must not block schema binding saves; image fields nullable by design (R-9).

## Starter Template Evaluation

### Primary Technology Domain

Full-stack web: .NET 10 LTS backend (ASP.NET Core Minimal APIs, EF Core + Dapper, orchestrated by .NET Aspire 13.1) + React 19 SPA (Vite, shadcn/ui, Tailwind 4, TanStack Router). Tech stack fully locked by PRD addendum §1; this step selects the scaffolding commands and repo layout, not the technologies.

### Starter Options Considered

**Backend scaffold:**
- **`aspire new aspire-starter`** (chosen) — official Aspire 13.1 starter producing AppHost + ServiceDefaults + ApiService + a Blazor web project. Provides battle-tested OTel exporters, health-check endpoints, service-discovery extensions, and resilience defaults in ServiceDefaults. Blazor web project deleted post-scaffold.
- Hand-rolled `dotnet new web` + manual Aspire wiring (rejected — re-implements ServiceDefaults).
- Community templates (e.g., `juliocasal/aspire-api-template`) (rejected — diverge from the canonical Microsoft path; stale risk).

**Frontend scaffold:**
- **`npm create vite@latest web -- --template react-ts` + `npx shadcn@latest init`** (chosen) — Vite official template followed by shadcn CLI initialization. React 19 upgrade is a single npm command. Lets us add only the libraries the PRD names (TanStack Router, TanStack Query v5, react-hook-form, Zod), no dead weight.
- Community starters like `doinel1a/vite-react-ts-shadcn-ui` (React 19 + Vite 7 + Tailwind 4 + Husky bundled) (rejected — opinionated extras (Husky, ESLint 10) that we'd want to evaluate independently; lock-in to a third-party maintainer).
- Next.js (rejected — server-side rendering is unwanted; the addendum explicitly chose Vite-style SPA; backend is .NET, not Node).

**Repo layout:**
- **Single solution monorepo** (chosen) — Aspire AppHost orchestrates the React dev server via `AddViteApp` (PRD G-1 AC-1 requires the AppHost to reference the frontend); a split repo would force either committed build artifacts or dual-stack dev.
- Polyrepo (rejected — fights the PRD).

### Selected Starter: `aspire-starter` (backend) + Vite React-TS + shadcn CLI (frontend)

**Rationale for Selection:**

The PRD locks every architectural technology. The starter question is therefore "which scaffold produces the minimum correct skeleton with the fewest assumptions to undo?" The `aspire-starter` template delivers AppHost + ServiceDefaults (OTel, health checks, service discovery, resilience) — exactly the cross-cutting infrastructure the PRD's Epic G demands (G-1, G-3, G-4). The Vite + shadcn CLI path delivers a frontend skeleton with no opinionated extras, allowing the PRD's exact dependency list to be added cleanly.

### Proposed Monorepo Structure

```
tinnitus/
├── FormForge.sln
├── src/
│   ├── FormForge.AppHost/         # Aspire 13.1 orchestrator
│   ├── FormForge.ServiceDefaults/ # OTel, health checks, service discovery, resilience
│   ├── FormForge.Api/             # ASP.NET Core Minimal APIs (.NET 10)
│   └── FormForge.Api.Tests/       # xUnit + Testcontainers (PostgreSQL)
├── web/                           # React 19 + Vite 7 + TS + shadcn/ui + Tailwind 4
├── docker-compose.yml             # Alternative orchestration (PRD G-5)
├── docs/
└── _bmad-output/                  # Planning artifacts (already exists)
```

### Initialization Command Sequence

```bash
# Backend
aspire new aspire-starter --name FormForge --output .
# (or: dotnet new aspire-starter --name FormForge --output .)

# Cleanup post-scaffold
# - Remove src/FormForge.Web (Blazor sample web project)
# - Rename ApiService → FormForge.Api in solution and folders
# - Add FormForge.Api.Tests project (xUnit + Testcontainers.PostgreSQL)

# Frontend (from repo root)
npm create vite@latest web -- --template react-ts
cd web
npm install react@latest react-dom@latest @types/react@latest @types/react-dom@latest
npm install tailwindcss@latest @tailwindcss/vite
npx shadcn@latest init
npm install @tanstack/react-router @tanstack/react-router-devtools
npm install @tanstack/react-query @tanstack/react-query-devtools
npm install react-hook-form zod @hookform/resolvers
npm install i18next react-i18next
npm install -D @tanstack/router-plugin
npm install -D vitest @testing-library/react @testing-library/jest-dom @vitest/ui jsdom
cd ..
```

### Vite Config Outline

```typescript
// web/vite.config.ts
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { tanstackRouter } from '@tanstack/router-plugin/vite'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [
    tanstackRouter({ target: 'react', autoCodeSplitting: true }),
    react(),
    tailwindcss(),
  ],
})
```

### Aspire AppHost Wiring Outline

```csharp
// src/FormForge.AppHost/Program.cs
var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
                      .WithDataVolume()
                      .AddDatabase("formforge");

var minio = builder.AddContainer("minio", "minio/minio")
                   .WithArgs("server", "/data", "--console-address", ":9001")
                   .WithEnvironment("MINIO_ROOT_USER", "minioadmin")
                   .WithEnvironment("MINIO_ROOT_PASSWORD", "minioadmin")
                   .WithVolume("minio-data", "/data")
                   .WithEndpoint(9000, name: "s3")
                   .WithEndpoint(9001, name: "console");

var mailpit = builder.AddContainer("mailpit", "axllent/mailpit")
                     .WithEndpoint(containerPort: 1025, hostPort: 1025, name: "smtp")
                     .WithEndpoint(containerPort: 8025, hostPort: 8025, name: "ui");

var api = builder.AddProject<Projects.FormForge_Api>("api")
                 .WithReference(postgres)
                 .WithEnvironment("SMTP_HOST", mailpit.GetEndpoint("smtp"))
                 .WaitFor(postgres);

builder.AddViteApp(name: "web", workingDirectory: "../../web")
       .WithReference(api)
       .WaitFor(api)
       .WithNpmPackageInstallation();

builder.Build().Run();
```

### Architectural Decisions Provided by Starter

**Language & Runtime:**
- Backend: C# 14, .NET 10 LTS (SDK 10.0.x), nullable reference types enabled, implicit usings enabled.
- Frontend: TypeScript 5.x, ES2022+ target, React 19.2.4.

**Styling Solution:**
- Tailwind CSS 4 (Vite plugin), shadcn/ui as a component registry copied into `web/src/components/ui/`. CSS variables for theming (supports the 3-theme requirement in FR-38 directly).

**Build Tooling:**
- Backend: `dotnet build` / `dotnet run` via Aspire AppHost (single command starts everything).
- Frontend: Vite 7 with SWC for fast HMR; build via `vite build`. Aspire orchestrates `npm run dev` in development; production builds served by the API project or a static host (deferred to deployment design).

**Testing Framework:**
- Backend: xUnit (.NET default). Testcontainers.PostgreSQL for integration tests against a real Postgres (critical given runtime-DDL behavior — see PRD R-3, Risk Register).
- Frontend: Vitest + React Testing Library + jsdom. Playwright deferred to E2E selection.

**Code Organization:**
- Solution: `src/` for projects (AppHost, ServiceDefaults, Api, Api.Tests).
- Frontend: `web/src/` with conventional Vite layout. shadcn components in `web/src/components/ui/`; route tree auto-generated to `web/src/routeTree.gen.ts` by the TanStack Router Vite plugin. Designer port code lands in `web/src/components/designer/` and `web/src/routes/`.
- Monorepo root holds `FormForge.sln`, `docker-compose.yml`, `_bmad-output/`, `docs/`.

**Development Experience:**
- `dotnet run --project src/FormForge.AppHost` starts: API, PostgreSQL, MinIO, React dev server, Aspire Dashboard (https://localhost:15888).
- Aspire Dashboard provides per-service logs, traces, metrics, environment, and resource state.
- `docker compose up` (PRD G-5) provides an alternative path for contributors without the .NET 10 SDK.

**Routing — Decision (overrides PRD addendum A2):**

PRD addendum A2 assumed React Router v7. **Overridden in favor of TanStack Router** (file-based mode with `@tanstack/router-plugin/vite` + `autoCodeSplitting: true`).

Rationale:
- **Type safety end-to-end** — route params, search params, and loader return types are inferred at every consumer. The dynamic `/data/{designerId}` route gets full IntelliSense without manual `useParams<{ designerId: string }>` casts.
- **Tighter TanStack Query integration** — route loaders can `ensureQueryData()` so transitions don't flash empty states for cached data, which directly supports the PRD's FR-41 loading/empty/error UX contract.
- **Search params as first-class state** — record-list filter/sort/pagination (FR-40) serialize cleanly into URL state via `validateSearch` Zod integration; avoids custom URL serialization code.
- **Built-in pending/error route boundaries** — match the PRD's FR-41 requirement at the framework level rather than per-page.

Action item: update PRD addendum A2 to reflect TanStack Router as the routing choice.

**Note:** Project initialization using the command sequence above should be the first implementation story (suggested: G-1.1, prepended to Sprint S0).

## Core Architectural Decisions

### Decision Priority Analysis

**Critical Decisions (block implementation, all resolved):** PRD architect handoff items AD-1 through AD-11 plus identifier sanitization, complete component→PG type mapping, EF/Dapper transaction model, cache backend, validation strategy, error envelope, hosting topology, and CSP/security headers.

**Important Decisions (shape architecture significantly, all resolved):** session-recovery flow, permission cache invalidation events, rate limiting policy, route group filter chain, JWT signing algorithm, observability metrics, container image strategy.

**Deferred Decisions (post-MVP):** Redis backend (v2 when horizontal scaling lands), per-Designer generated OpenAPI specs (v2), `Idempotency-Key` header support (v2), keyset pagination (v2 — already PRD non-goal), CDN/reverse-proxy hosting (when scale demands), Hangfire/Quartz job queue (when admin DDL workload outgrows single-process serialization).

---

### Data Architecture

#### 1.1 — Identifier Sanitization (resolves AD-3)
- **Regex:** `^[a-z_][a-z0-9_]{0,62}$`. Strict lowercase, no auto-folding (uppercase rejected at input).
- **Reserved keyword list:** curated subset (~100) of `pg_catalog.pg_get_keywords()` filtered to `reserved` and `reserved-unreserved` for PostgreSQL 17. Hardcoded constant refreshed per PG major version.
- **Defense in depth:** validation at Designer save (FR-9 AC-2 / FR-23 AC-1), DDL emit, and dynamic CRUD identifier substitution against the schema registry whitelist (AD-7).
- **No raw string concatenation into SQL.** Every dynamic identifier passes through a `SafeIdentifier` value type that re-validates on construction.

#### 1.2 — Component → PG Type Mapping (resolves AD-4)
Complete mapping for all 14 component types:

| Component | PG Type | Notes |
|---|---|---|
| `Stack`, `Row`, `Tabs` | — | Structural; no column |
| `Label`, `Button` | — | UI-only; no column |
| `TextInput` | `TEXT` | |
| `TextArea` | `TEXT` | |
| `NumberInput` | `NUMERIC` | Avoids float drift (addendum recommends over FLOAT8) |
| `Checkbox` | `BOOLEAN` | |
| `Dropdown` | `TEXT` | NOT a PG enum — enums are migration-hostile; stores option `value` |
| `DateTimePicker` | `TIMESTAMPTZ` | |
| `ColorPicker` | `TEXT` | Hex string `#RRGGBB` |
| `Repeater` | — | Triggers child table provisioning |
| `RepeaterField` | — | Lives in child table |
| `Image` | `TEXT` | MinIO object key (string) |
| Unknown / future | `JSONB` | Forward-compatibility fallback |

All dynamic columns are nullable (PRD FR-24 AC-3).

#### 1.3 — Soft-Delete Cascade Depth (resolves AD-5)
- **Full transitive cascade** with explicit event tracking.
- New system column on every dynamic table: `cascade_event_id UUID NULL`.
- On cascade soft-delete: generate one UUID; set on the parent and all descendants found via recursive Repeater graph walk, all in one transaction.
- On restore: cascade-restore only rows whose `cascade_event_id` matches the parent's last cascade event. Children that were individually soft-deleted before the parent are NOT accidentally restored.
- Recursive walk uses the schema registry's `ChildRepeaterDesignerIds` field; bounded by cycle detection from D-5 AC-4.
- **`cascade_event_id` NULL semantics:** individual (non-cascade) soft-deletes leave the column NULL. Cascade soft-deletes populate it for parent + all descendants in one transaction. On restore: a direct individual restore clears the column unconditionally; a cascade-restore re-activates only rows with matching `cascade_event_id` — children that were individually soft-deleted before a parent cascade are NOT incidentally restored.

#### 1.4 — Schema Registry Cache (resolves AD-7)
- **Structure:**
  ```csharp
  public sealed record ColumnDefinition(
      string ColumnName,      // PG column name (validated identifier)
      string PgType,          // TEXT | NUMERIC | BOOLEAN | TIMESTAMPTZ | JSONB
      string ComponentType,   // TextInput | NumberInput | Image | ...
      string FieldKey,        // properties.fieldKey from RootElement
      bool IsImage,           // drives presigned URL serialization
      bool IsRepeater);       // drives cascade walk

  public sealed record SchemaRegistryEntry(
      string DesignerId,
      int Version,
      IReadOnlyList<ColumnDefinition> Columns,
      IReadOnlyList<string> ChildRepeaterDesignerIds,
      DateTimeOffset CachedAt);
  ```
- **Backend:** `IMemoryCache` (in-process) for v1.
- **Population:** lazy on first CRUD request per `(designerId, publishedVersion)`. Reads from `component_schemas`, parses RootElement.
- **Invalidation:** in-process event bus — Designer version promotion to Published publishes `SchemaPublished` → cache handler evicts matching entries.
- **Eviction:** LRU, capacity 1000 entries, 1-hour TTL safety net.

#### 1.5 — Audit Log Retention & Indexing (resolves AD-10)
- **Retention:** unlimited for v1 (no auto-pruning).
- **Indexes:**
  - `mutation_audit_log (designer_id, created_at DESC)`
  - `mutation_audit_log (record_id, created_at DESC)`
  - `schema_audit_log (designer_id, created_at DESC)`
  - All audit tables: `correlation_id` column indexed (`idx_*_correlation`).
- **Partitioning:** deferred — re-evaluate if monthly `mutation_audit_log` volume exceeds 1M rows.
- **Storage:** both tables EF Core managed (per FR-36 AC-4).

#### 1.6 — EF Core + Dapper Transaction Boundary (resolves AD-11)
- **Separated transactions with async provisioning** (matches PRD FR-17 AC-2/AC-3).
- Flow:
  1. Admin saves Schema Binding → EF Core commits Menu Item with `provisioningStatus: Pending`. API returns 202.
  2. `IProvisioningService.EnqueueAsync()` publishes work to an in-process `Channel<ProvisioningJob>`.
  3. `BackgroundService` consumer dequeues; opens its own `NpgsqlConnection`; runs Dapper DDL in an explicit transaction; commits.
  4. On commit: Menu Item updated with `provisioningStatus: Success`. On failure: `provisioningStatus: Error` + `provisioningError` message; admin sees and retries.
- Rejected: shared connection across EF + Dapper. Complex (requires `DbContext.Database.GetDbConnection()` threading); PRD explicitly allows separation; async gives DDL room to exceed an HTTP request budget.
- **Query timeouts (PRD R-6 mitigation):** `DbConnectionFactory` configures Npgsql `CommandTimeout = 5` (seconds) for dynamic CRUD by default. Admin-triggered DDL paths (table provisioning, ALTER TABLE, schema-drift queries) override to `CommandTimeout = 60`. Health check probes use 5s.
- **Provisioning recovery on process restart:** if the API restarts while jobs are mid-flight, `Channel<ProvisioningJob>` does not survive. A `ProvisioningRecoveryService` runs on startup, scans `menus WHERE provisioningStatus = 'Pending'`, and re-enqueues each. Acceptable per PRD FR-17 AC-3.
- **Mode gate:** only `mode = 'CRUD'` bindings enter this pipeline; VIEW-mode bindings skip provisioning entirely and are marked `provisioningStatus = 'NotApplicable'` (Decision 1.8).

#### 1.7 — Migration Tooling
- **EF Core Migrations** for all static schemas: `users`, `roles`, `user_roles`, `menus`, `menu_role_assignments`, `component_schemas`, `refresh_tokens`, `schema_audit_log`, `mutation_audit_log`.
- **Dapper** owns all dynamic-schema DDL (provisioning service) — never represented in EF migrations.
- Strict separation. EF migrations don't touch dynamic tables; provisioning never touches static tables. Schema registry reads `component_schemas` read-only.
- Migrations run automatically on API startup in all environments (idempotent `Database.Migrate()` call).

#### 1.8 — Component Mode: CRUD vs VIEW (resolves FR-54)
- **`component_schemas.mode`** column: `TEXT NOT NULL` with `CHECK (mode IN ('CRUD','VIEW'))`. Set at Designer creation (FR-9 AC-5); **immutable** thereafter — the Designer update path rejects any attempt to change it → HTTP 422 (FR-54 AC-2). EF Core-managed; existing rows are backfilled to `'CRUD'` in the same migration that adds the column, preserving prior behavior (FR-54 AC-6).
- **Provisioning gate (extends Decision 1.6):** the provisioning pipeline is entered only when `mode = 'CRUD'`, gated at the source before any DDL is constructed. Binding a VIEW component runs **no** `CREATE`/`ALTER TABLE` and writes **no** `schema_audit_log` entry; the Menu Item's `provisioningStatus` is set to `NotApplicable` and surfaced in the admin UI as "Not applicable (view-only)" (FR-17 AC-5, FR-24 AC-7, FR-54 AC-3). `EnqueueAsync` is skipped — VIEW bindings never reach the `Channel<ProvisioningJob>`.
- **CRUD endpoint gate:** `/api/data/{designerId}/*` is served only for CRUD-mode designers. A request targeting a VIEW-mode (or otherwise unprovisioned) designer → HTTP 404 `{ code: "TABLE_NOT_PROVISIONED" }`, consistent with FR-29 AC-6 and FR-54 AC-4.
- **Permission model (extends Decision 2.2):** a VIEW component has no table and therefore no per-Resource `CrudFlags`; access is governed **solely** by the bound Menu Item's `allowedRoles` (FR-19, FR-54). The effective-permission helper is not consulted for VIEW bindings; the `PerResource` map only ever contains CRUD-mode resources.
- **Schema registry (extends Decision 1.4):** VIEW designers are never inserted into the registry — no provisioned column set exists. A registry miss for a VIEW designer is expected and resolves to the 404 above rather than a lazy population attempt.
- **Migration note:** the `mode` column ships in the EF migration for `component_schemas`; the `CHECK` constraint plus the NOT-NULL backfill default (`'CRUD'`) are applied in one transaction so no row is ever left mode-less.

---

### Authentication & Security

#### 2.1 — JWT Silent Re-Auth Flow on Page Reload (resolves AD-1)
- **App boot:** call `POST /api/auth/refresh` before TanStack Router renders protected routes. HttpOnly cookie travels automatically.
- **200:** new access token stored in module-level variable (NOT React state, NOT localStorage). Authenticated routes render.
- **401:** Router `beforeLoad` throws `redirect({ to: '/login', search: { redirect: currentPath } })`.
- **Refresh cadence:** TanStack Query manages an `useAuthQuery` with `refetchInterval ≈ 13 min` (just before 15-min access token expiry).
- **Deactivation handling:** refresh token revoked immediately server-side; access token honored up to its 15-min TTL (PRD-accepted lag); server middleware re-checks `users.is_active` on each request (cached ≤30 s).

#### 2.2 — Permission Cache Invalidation (resolves AD-2)
- **Cache structure:**
  ```csharp
  public sealed record EffectivePermissions(
      Guid UserId,
      DateTimeOffset ComputedAt,
      bool IsActive,
      IReadOnlyDictionary<string, CrudFlags> PerResource,
      IReadOnlySet<Guid> RoleIds);

  public readonly record struct CrudFlags(
      bool CanCreate, bool CanRead, bool CanUpdate, bool CanDelete);
  ```
- **Backend:** `IMemoryCache`, keyed by `userId`. TTL 30 s.
- **Bust events** (in-process event bus):
  - `UserRoleAssignmentChanged(userId)` — evict that user.
  - `RolePermissionsChanged(roleId)` — evict all users holding that role.
  - `UserDeactivated(userId)` — evict + revoke refresh token.
  - `MenuBindingCreated(designerId)` — no eviction (new Resource defaults to false flags).
- **Population:** lazy on first `/api/data/*` or `/api/users/me/permissions` request.
- **VIEW-mode designers (Decision 1.8):** no `CrudFlags` are computed or cached for them; access is decided solely by the bound Menu Item's `allowedRoles`. `PerResource` only ever holds CRUD-mode resources.

#### 2.3 — JWT Signing Algorithm
- **HS256** (symmetric, secret in env var).
- Secret rotation: quarterly via runbook; grace window equals access-token TTL (15 min) — old key honored briefly.
- RS256 rejected: no external verifier needs the public key in v1.

#### 2.4 — Password Hashing
- **`BCrypt.Net-Next` work factor 12** (~250 ms per hash; resistant to offline cracking).
- Plaintext never persisted or logged (PRD FR-1).
- No pepper in v1.

#### 2.5 — CORS Policy
- **Dev (Aspire):** allowlist the Vite dev server origin injected as env var. `AllowCredentials = true`.
- **Prod:** strict allowlist of the deployed frontend origin (single config value). `AllowCredentials = true`. No wildcards.
- Preflight cached 1 hour (`Access-Control-Max-Age: 3600`).

#### 2.6 — Rate Limiting
ASP.NET Core `Microsoft.AspNetCore.RateLimiting`:

| Endpoint pattern | Policy | Limit |
|---|---|---|
| `POST /api/auth/login` | Fixed per IP | 10 / 1 min |
| `POST /api/auth/refresh` | Fixed per IP | 30 / 1 min |
| `POST /api/data/{designerId}` | Sliding per user | 60 / 1 min |
| Other `/api/data/*` | Sliding per user | 300 / 1 min |
| `/api/admin/*` | Sliding per user | 120 / 1 min |

429 returned with `Retry-After`. Health endpoints excluded.

#### 2.7 — Security Headers + CSP
Via `NetEscapades.AspNetCore.SecurityHeaders`:
- `Strict-Transport-Security: max-age=31536000; includeSubDomains`
- `X-Frame-Options: DENY`
- `X-Content-Type-Options: nosniff`
- `Referrer-Policy: strict-origin-when-cross-origin`
- **CSP:** `default-src 'self'; img-src 'self' data: blob: <minio-presigned-host>; style-src 'self' 'unsafe-inline'; script-src 'self' 'nonce-{cspNonce}'; connect-src 'self'`
- CSP nonce generated per request, injected into the theme `<script>` (Decision 4.2).

#### 2.8 — Secret Storage
- **Dev:** `dotnet user-secrets` + Aspire injection. Never `appsettings.json`.
- **Prod:** env vars populated from the deployment platform's secret manager (Azure Key Vault / AWS Secrets Manager / Doppler — deferred).
- JWT signing key, PG connection string, MinIO root user/password — all secrets.

#### 2.9 — Email Service (resolves FR-50, FR-51)
- **Library:** MailKit standalone (`MailKit` NuGet). FluentEmail deferred to v2 if template complexity warrants.
- **Dev container:** Mailpit (`axllent/mailpit`) added to AppHost — SMTP on port 1025, web UI on port 8025.
- **Config:** `SMTP_HOST`, `SMTP_PORT`, `SMTP_USER`, `SMTP_PASS`, `SMTP_FROM` env vars. Aspire injects Mailpit endpoint into API via `WithEnvironment` in dev.
- **Dispatch pattern:** async fire-and-forget — `_ = Task.Run(() => SendAsync(...))` with `catch` logging the exception. Caller receives result immediately; SMTP failure does not block the creation endpoint.
- **Audit:** structured log only (AD-12 resolved — Option A). Every dispatch attempt logged at `Information` with `recipient`, `templateType`, `correlationId`, `success/failure`. No DB email audit table in v1.

#### 2.10 — Password Reset Token (resolves FR-51)
- **Generation:** `Convert.ToHexString(RandomNumberGenerator.GetBytes(32))` — 64-char hex string.
- **Storage:** only the SHA-256 hash persisted in `password_reset_tokens` (EF Core managed). Columns: `id`, `user_id`, `token_hash`, `expires_at`, `used_at`.
- **TTL:** 1 hour absolute. Single-use — `used_at` set on redemption.
- **Anti-enumeration:** `POST /api/auth/forgot-password` always returns HTTP 200 with a generic message regardless of whether the email is registered.
- **On success:** `passwordHash` updated; all refresh tokens for the user revoked.

#### 2.11 — Authenticated Password Change (resolves FR-52)
- `PUT /api/users/me/password { currentPassword, newPassword }`.
- `currentPassword` verified via bcrypt. Mismatch → 401.
- `newPassword` ≥ 8 chars; must differ from current (bcrypt comparison). Failure → 422.
- On success: `passwordHash` updated; all refresh tokens except the current session's revoked.

#### 2.12 — TOTP MFA Architecture (resolves FR-53)
- **Library:** `Otp.NET` NuGet. RFC 6238 — SHA-1, 30-second period, 6-digit codes, ±1 step clock-skew tolerance.
- **Secret encryption at rest:** `IDataProtector` (purpose: `"mfa-totp-secret"`). Stored as an encrypted blob in `users.mfa_secret_protected`. Keys managed by ASP.NET Core Data Protection; no external KMS in v1.
- **Two-step login exchange (when `users.mfa_enabled = true`):**
  1. `POST /api/auth/login` → HTTP 200 `{ mfaRequired: true, mfaSessionToken }` (no JWT issued yet).
  2. `POST /api/auth/mfa/verify { mfaSessionToken, code }` → JWT pair issued on success.
  - `mfaSessionToken`: random ULID stored as key in `IMemoryCache` mapping to `{ userId, issuedAt }`; 5-min absolute TTL; single-use (evicted on first successful verify).
- **Backup codes:** 8 codes, 8-character alphanumeric. Raw codes shown once at enrolment; bcrypt hashes stored in `mfa_backup_codes (id, user_id, code_hash, used_at)`. `POST /api/auth/mfa/verify` accepts a backup code in place of a TOTP code.
- **Enrolment guard:** secret not committed to DB until the user successfully verifies a TOTP code (prevents dangling unconfirmed secrets).
- **Re-enrolment:** replaces active secret; new backup codes generated; old ones invalidated atomically.
- **Admin reset:** `DELETE /api/admin/users/{userId}/mfa` — clears `mfa_enabled`, `mfa_secret_protected`, and all backup codes.
- **MFA enforcement policy (OQ-7 resolved — Option A):** voluntary per-user opt-in only. No platform-wide or role-based enforcement in v1. Policy hook deferred to v2.

---

### API & Communication Patterns

#### 3.1 — Standardized Error Envelope (RFC 7807 ProblemDetails)
```json
{
  "type": "https://docs.formforge.app/errors/forbidden",
  "title": "Forbidden",
  "status": 403,
  "detail": "...",
  "instance": "/api/data/incident_report",
  "code": "FORBIDDEN",
  "messageKey": "errors.forbidden",
  "resource": "incident_report",
  "action": "create",
  "correlationId": "01HX..."
}
```
- `code` — stable enum documented in OpenAPI (`FORBIDDEN`, `TABLE_NOT_PROVISIONED`, `RECORD_DELETED`, `VALIDATION_FAILED`, `IDENTIFIER_INVALID`, ...).
- `messageKey` — i18n key for client-side translation (FR-49 AC-3).
- Validation errors use `ValidationProblemDetails` with `errors: { fieldName: ["msg1", ...] }`.
- Wired via `IExceptionHandler` middleware (.NET 10); one global handler maps domain exceptions.

#### 3.2 — API Versioning Strategy
- **No URL prefix in v1.** Routes are `/api/auth/login`, `/api/data/{designerId}`, etc. Spec served at `/openapi/v1.json`.
- **Additive within v1:** new optional fields and endpoints ship in place.
- **Breaking changes:** `/api/v2/...` ships alongside; v1 stays live during deprecation window; `/openapi/v2.json` joins v1.

#### 3.3 — Validation Strategy (Two-Layer)
- **Layer 1 — Static endpoints:** FluentValidation with one `AbstractValidator<TRequest>` per DTO. Auto-invoked via Minimal API filter. Returns `ValidationProblemDetails`.
- **Layer 2 — Dynamic data endpoints (`/api/data/{designerId}/*`):** custom `IDynamicPayloadValidator` reading schema registry; unknown fields ignored (FR-31 AC-2); known fieldKeys type-checked against `ColumnDefinition.PgType`; filter/sort params whitelisted against cached column list.

#### 3.4 — Pagination Response Shape
Standardized across **all** list endpoints:
```csharp
public sealed record PagedResult<T>(
    IReadOnlyList<T> Data,
    long Total,
    int Page,
    int PageSize)
{
    public int TotalPages => (int)Math.Ceiling((double)Total / PageSize);
}
```
Query convention: `?page=1&pageSize=25&sort=col:dir&filter[key]=val`. `pageSize` ≤ 100. Default 25.

#### 3.5 — Endpoint Organization (Route Groups)
```csharp
app.MapGroup("/api/auth").MapAuthEndpoints();
app.MapGroup("/api/users").RequireAuth().MapUserSelfEndpoints();
app.MapGroup("/api/admin").RequireAuth().RequirePlatformAdmin().MapAdminEndpoints();
app.MapGroup("/api/menus").RequireAuth().MapMenuEndpoints();
app.MapGroup("/api/designers").RequireAuth().MapDesignerEndpoints();
app.MapGroup("/api/data/{designerId}").RequireAuth().MapDynamicDataEndpoints();
```
- One static class per group: `XxxEndpoints.MapXxxEndpoints(this RouteGroupBuilder)`.
- Group-level filters: `RequireAuth()`, `RequirePermission(action)`, `RequireRateLimiting(policy)`, `AddValidationFilter<T>()`.
- **Filter chain order:** correlation ID → auth → rate limit (per user) → permission → validation → handler.
- **Mode gate on `/api/data/{designerId}`:** the dynamic-data group resolves the designer's mode via the schema registry before the handler runs; VIEW-mode (and unprovisioned) designers short-circuit to 404 `TABLE_NOT_PROVISIONED` (Decision 1.8). For VIEW bindings the permission filter checks Menu `allowedRoles` only, never per-Resource CRUD flags.
- No controllers. Handlers are pure async functions.

#### 3.6 — Version Re-Bind Diff & Trigger (resolves AD-6)
- **Explicit admin action with diff preview before apply.**
- Endpoint: `GET /api/admin/menus/{menuId}/binding-diff?targetVersion={N}` returns:
  ```json
  {
    "currentBinding": { "designerId": "incident_report", "version": 1 },
    "targetVersion": 2,
    "columnsToAdd": [...],
    "columnsAlreadyPresent": [...],
    "orphanedColumns": [],
    "willTriggerChildProvisioning": [...],
    "estimatedDdl": [...]
  }
  ```
- **Apply:** `PUT /api/admin/menus/{menuId}/binding { targetVersion }` returns 202 + `provisioningStatus: Pending`.
- **Execution:** async via the Channel + BackgroundService pattern (Decision 1.6).
- **UI:** Menu Editor → "Update to version N" → diff modal → confirm → status card polls until Success/Error.

#### 3.7 — OpenAPI for Dynamic Endpoints (resolves AD-8)
- **v1:** dynamic endpoint bodies typed as `object` with `additionalProperties: true`; descriptions explain runtime-determined shape.
- **`designerId` path param:** `pattern: "^[a-z_][a-z0-9_]{0,62}$"`.
- **Error responses** referenced via `$ref` to centrally-defined `ProblemDetails` schemas keyed by `code` enum.
- **Per-Designer generated specs:** deferred to v2 (`/openapi/designer/{designerId}/v{version}.json`).

#### 3.8 — Correlation ID Propagation
- **Read:** `X-Correlation-ID` header; if absent, generate ULID (sortable, URL-safe, 26 chars).
- **Store:** `IHttpContextAccessor` via early middleware → injected into `ILogger` scope → flowed onto Dapper queries as `/* corr:01HX... */` SQL comment for PG log correlation.
- **Emit:** every log entry, every audit log row (new `correlation_id` column), every error response, response header.

#### 3.9 — Idempotency
- **v1:** no `Idempotency-Key` support. Mutations not idempotent by default.
- Exception: `POST /api/auth/refresh` is naturally single-use (token rotation).
- **v2 deferred:** `Idempotency-Key` on data POSTs for retry-safe integrators.

#### 3.10 — Health-Check Endpoint Authentication
- `/health/live` — anonymous, always 200 if process running.
- `/health/ready` — anonymous, 503 if PG or MinIO unreachable.
- `/health` (detailed) — **requires `platform-admin` role**.

---

### Frontend Architecture

#### 4.1 — MinIO File Access via Server-Enriched Presigned URLs (resolves AD-9)
- **Server enriches Image columns at serialization time.** Schema registry knows `IsImage: true`; serializer replaces raw object keys with a presigned URL bundle:
  ```json
  {
    "photo": {
      "objectKey": "incident_report/photos/01HX_photo.png",
      "url": "https://minio.local:9000/formforge/.../?X-Amz-...",
      "expiresAt": "2026-05-22T10:35:00Z"
    }
  }
  ```
- **TTL:** 5 minutes. **Bucket:** single `formforge` with path prefixes (`menus/icons/`, `{designerId}/{fieldKey}/`).
- **Upload:** `POST /api/files/upload` (multipart) → `{ objectKey, url, expiresAt }`. Form stores `objectKey`.
- **Refresh:** `POST /api/files/refresh-urls { objectKeys: [...] }` returns fresh URL bundles.
- **Client caching:** record-level TanStack Query cache; no separate URL cache.

#### 4.2 — Theme No-Flash Hydration (R-10 mitigation)
- Inline `<script nonce="{cspNonce}">` in `<head>` reads `localStorage.getItem('ff-theme')` synchronously, sets `data-theme` attribute before React.
- After login/refresh, server-persisted theme synced to localStorage and `data-theme` updated.
- Tailwind 4 CSS variables keyed off `[data-theme='slate-dark']` etc.
- First-ever visit flashes default → user theme on first authenticated render only (acceptable per PRD R-10).

#### 4.3 — Error Boundary & Loading Strategy
- **TanStack Router:** `errorComponent`, `pendingComponent` per route + `defaultPendingComponent` / `defaultErrorComponent` on `__root`. Skeletons via shadcn `Skeleton`.
- **React Error Boundary** at app root for uncaught errors outside Router scope. Catastrophic-fail page with reload + correlation ID.
- **TanStack Query errors** handled inline per FR-41 AC-3 (toast transient, banner with retry for blocking).
- **404s:** Router `notFoundComponent` on `__root`.

#### 4.4 — Toast / Notifications
- **`sonner`** (shadcn-supported, ~3 KB, accessibility built-in).
- Install: `npx shadcn@latest add sonner`. Mounted at `<Toaster />` in `__root`.
- Used for: form-save success, transient API errors, copy-to-clipboard confirmations, provisioning status transitions.

#### 4.5 — Designer DnD Keyboard Accessibility (R-7 mitigation)
- **Parallel keyboard interaction model alongside native HTML5 DnD** (HTML5 DnD has no native keyboard support).
- `Tab` focuses; `Space`/`Enter` picks up; arrows move focus to valid drop targets (announced via `aria-live="polite"`); `Space`/`Enter` confirms; `Escape` cancels.
- Each `DropZone` gets descriptive `aria-label`.
- Implemented as `useKeyboardDnD()` hook emitting the same canvas events the HTML5 DnD path emits.
- Menu reorder UI gets the same treatment.
- **New story:** B-11 "Keyboard-Accessible Designer DnD" added to Epic B, scheduled in Sprint S3 before designer ships. (Renumbered from B-10 to avoid collision: PRD Story **B-10** is now "Declare and Enforce Component Mode" — FR-54.)

#### 4.6 — Module / Feature Folder Structure
```
web/src/
├── routes/                          # TanStack Router file-based
│   ├── __root.tsx
│   ├── login.tsx
│   ├── _app.tsx                     # Authenticated layout
│   └── _app/
│       ├── data.$designerId.tsx
│       ├── data.$designerId.$recordId.tsx
│       └── admin/...
├── components/
│   ├── ui/                          # shadcn primitives
│   ├── designer/                    # Ported from ESG Platform
│   │   ├── DesignerCanvas.tsx
│   │   ├── DynamicComponent.tsx
│   │   ├── ElementRenderer.tsx
│   │   └── useKeyboardDnD.ts        # Decision 4.5
│   └── shared/
├── features/
│   ├── auth/
│   ├── designer/
│   ├── menu/
│   └── data-entry/
├── lib/
│   ├── i18n/
│   ├── theme/
│   └── api/
└── routeTree.gen.ts                 # Generated by router plugin
```
**Import rules:** `routes/` → `features/`, `components/`, `lib/`. `features/` → `components/`, `lib/`. `components/` → `lib/`. No reverse. Enforced via ESLint `import/no-restricted-paths`.

#### 4.7 — HTTP Client Wrapper
- Thin `fetch`-based wrapper at `features/auth/httpClient.ts`. **No Axios.**
- Reads access token from module-level `tokenStore` (Decision 2.1).
- Attaches `Authorization: Bearer ${token}` and `X-Correlation-ID: {clientUlid}`.
- **On 401:** one `POST /api/auth/refresh` attempt; on success retry; on failure redirect `/login`.
- **On 4xx/5xx:** throws typed `ApiError(code, status, problemDetails)`.
- All TanStack Query `queryFn`/`mutationFn` calls go through `httpClient`; no direct `fetch` elsewhere.

#### 4.8 — i18n Initialization
- `i18next` + `react-i18next` initialized synchronously at app boot, before React renders.
- Single `en.json` in `web/src/lib/i18n/locales/`. Default namespace pre-bundled; feature namespaces lazy-loaded per route in prod.
- Key naming: dot-notation (`auth.login.title`, `designer.canvas.empty`).
- API error `messageKey` resolved via `t(error.messageKey, error.details ?? {})`.
- Per FR-49 AC-4: no functional multi-language in v1.

#### 4.9 — Form Composition (react-hook-form + Zod)
- One Zod schema per form, co-located with the form component.
- `react-hook-form` with `@hookform/resolvers/zod`.
- Form fields wrap shadcn primitives through a `FormField` adapter wiring `name`, `control`, `error`, `aria-describedby`.
- Server validation errors → `setError(field, { type: 'server', message })` from `ValidationProblemDetails.errors`.
- `mode: 'onSubmit'` for high-volume data entry; `mode: 'onChange'` for admin forms with interdependencies.

#### 4.10 — DynamicComponent Integration (Bridge to Ported Code)
- DynamicComponent preserved as a black box per FR-8 AC-5 (visibility engine, Repeater scope, `submitRef`, validity/ready callbacks intact).
- **Two form systems coexist:** react-hook-form for static admin forms; DynamicComponent for runtime-rendered data entry. No shared state.
- **Data entry flow:** TanStack Query mutation owns the network call. External Save button calls `submitRef.current()` → `onSave(payload)` callback hands payload to `mutation.mutate()`.
- **Designer preview flow:** read-only, no submission.
- **VIEW-mode data view (FR-54):** a Menu Item bound to a VIEW-mode designer renders DynamicComponent read-only — no record list, no "New Record" button, no Save/submit. Reuses the same read-only code path as preview; no `/api/data` calls are made (the endpoints don't exist for VIEW — Decision 1.8).
- Designer metadata (display name, status changes) uses react-hook-form.

#### 4.11 — Designer Property Pickers: Component-Source Filtering (FR-11, FR-54)
- Two property-panel pickers reference another Designer for its **data**: the Dropdown **"Source component"** picker (options sourced from another component) and the Repeater **"Row form — Component"** picker.
- Both are implemented as **searchable comboboxes** (shadcn `Command` inside `Popover`) over the Designer list, replacing plain `<select>` elements for usability as the catalog grows.
- Both list **CRUD-mode designers only**; VIEW-mode designers are excluded (FR-11 AC-4, FR-54 AC-5) — a VIEW component has no table to source options from or to persist child rows in. The combobox query filters on the `mode` field returned by `GET /api/designers`; a **server-side guard** independently rejects a VIEW reference on save → HTTP 422 (defense in depth, mirroring the identifier-validation posture).

---

### Infrastructure & Deployment

#### 5.1 — Cache Backend
- **`IMemoryCache`** for v1 (single-process). Covers permission cache, schema registry, navbar/menu cache.
- Both caches behind an `ICacheStore` interface — Redis swap mechanical in v2.

#### 5.2 — Background Work
- **`Channel<ProvisioningJob>` + `BackgroundService`** (single consumer, sequential — prevents concurrent DDL conflicts on shared tables).
- Job outcomes persisted to `menus.provisioningStatus`.
- No Hangfire/Quartz in v1.

#### 5.3 — Observability Stack
- **OpenTelemetry SDK** via Aspire ServiceDefaults (traces, metrics, logs).
- **Logging:** built-in `Microsoft.Extensions.Logging` + JSON console formatter + OTel logging exporter. No Serilog.
- **Exporter:** Aspire Dashboard OTLP in dev; real backend (Tempo / Jaeger / App Insights / Honeycomb) in prod (deferred).
- **Custom metrics:**
  - `formforge.permission_cache.{hits,misses}`
  - `formforge.schema_registry.{hits,misses}`
  - `formforge.provisioning_jobs.{completed,failed}`
  - `formforge.dynamic_crud.request.duration` histogram tagged `designerId`, `operation`
  - `formforge.refresh_token.{issued,revoked,replayed}`
  - `formforge.auth.deactivated_token_use` counter, tagged `userId` — incremented when a request arrives with a still-valid JWT for a user whose `isActive: false` (within the 15-min grace window after deactivation; PRD R-5 observability)
- **Trace tags:** every span carries `correlation_id`, `user_id`, `roles[]`. DDL spans tag `db.statement` (fingerprint only — FR-46 AC-3).

#### 5.4 — Health Checks
- `AspNetCore.HealthChecks.NpgSql` for PostgreSQL.
- Custom MinIO check via HEAD bucket request; 5 s timeout.
- Publisher logs status every 30 s for Aspire Dashboard.
- Endpoints per Decision 3.10.

#### 5.5 — Frontend Production Hosting
- **API project serves the SPA** in v1. `app.UseStaticFiles()` + fallback to `/index.html` for SPA routes.
- Vite `dist/` copied into `src/FormForge.Api/wwwroot/` during container build.
- Single origin — simplifies CORS, refresh-cookie semantics, CSP.
- Production `index.html` rewriter injects CSP nonce (Decision 2.7) and theme `<script>` (Decision 4.2).
- Migration paths to reverse proxy / CDN deferred.

#### 5.6 — Container Image Strategy
Multi-stage Dockerfile producing single `formforge-api:tag`:
1. `mcr.microsoft.com/dotnet/sdk:10.0` — restore, build, test (excluded from final).
2. `node:22-alpine` — `npm ci`, `vite build`.
3. `mcr.microsoft.com/dotnet/aspnet:10.0-alpine` — copy API publish + `web/dist/` → `/app/wwwroot/`. Non-root user UID 1000. Entry `dotnet FormForge.Api.dll`.

Other images: `postgres:17-alpine`, `minio/minio` (official). Image labels carry git SHA, build timestamp, semver tag.

**Frontend build targets (PRD §7 browser support):** `web/vite.config.ts` sets `build.target: 'es2022'`; `web/package.json` declares `"browserslist": ["last 2 Chrome versions", "last 2 Edge versions", "last 2 Firefox versions", "last 2 Safari versions"]`. No IE11 / legacy transpile.

#### 5.7 — Database Backup & Restore (Architectural Minimum)
- **PG WAL archiving** to MinIO `formforge-wal-archive/`.
- **Daily `pg_dump --format=custom`** to MinIO `formforge-backups/`.
- **MinIO bucket replication** to a second instance / external S3 target.
- **Retention:** 30 days daily; 7 days WAL.
- **Targets:** RPO ≤24 h (daily) or ≤5 min (WAL replay); RTO ≤2 h.
- Tested quarterly via runbook procedure.

#### 5.8 — Environment Configuration
- **Layering:** `appsettings.json` → `appsettings.{Environment}.json` (no secrets) → env vars (secrets, mandatory) → user secrets (dev only).
- **Aspire injection:** `WithReference()` wires connection strings as `ConnectionStrings__formforge`, `ConnectionStrings__minio`, etc.
- **Frontend:** Vite reads `VITE_API_BASE_URL` (empty in prod when API serves SPA from same origin; explicit URL in dev).
- **Production secrets:** env vars from deployment platform's secret manager (deferred).
- No secrets in `appsettings.*.json`.

#### 5.9 — CI/CD Pipeline Outline
Tools: GitHub Actions assumed (substitutable).
- **On PR:** restore + build, `dotnet test` (xUnit + Testcontainers), `vitest run`, ESLint + TS typecheck, container build (no push), axe-core smoke audit, `dotnet list package --vulnerable`, `npm audit --audit-level=high`.
- **On merge to main:** all above + tagged image push (`ghcr.io/.../formforge-api:sha-{short}` + `:main`) + staging deploy.
- **Gates:** all tests pass, no high-severity vulns, axe-core zero critical.

#### 5.10 — Docker Compose Parity (G-5)
- `docker-compose.yml` services: `postgres`, `minio`, `minio-init` (bucket create — G-5 AC-3), `api`.
- No frontend-dev service in Compose mode; SPA served from API container (HMR unavailable here).
- Service URLs via Docker network names.
- **EF Core migrations auto-run on API startup in every environment** (idempotent `Database.Migrate()` after `WebApplication.Build()`).

---

### Dataset Manager Architecture — Epics H–K (FR-55–FR-73)

This addendum extends the architecture for the Dataset Manager subsystem. No existing decisions (1.x–5.x) are modified. Decisions 6.1–6.13 govern all Dataset Manager implementation. The subsystem is additive: it introduces no changes to existing domain tables, runs alongside all existing features, and all Dataset VIEWs are isolated in a dedicated `datasets` PostgreSQL schema.

---

#### Data Architecture — Dataset Manager

#### 6.1 — Dataset Schema, Migration & View Namespace (FR-55, FR-57, H-1)

**EF Core-managed tables:**

`custom_dataset`:
- `id UUID PK DEFAULT gen_random_uuid()`
- `dataset_name TEXT UNIQUE NOT NULL` — validated bare identifier; also the bare VIEW name within the `datasets` schema
- `is_custom_query BOOLEAN NOT NULL DEFAULT true`
- `query TEXT` — user-authored SQL (Custom Query) or server-generated SQL from `builder_state` (Builder); both stored here after save; both always in sync (FR-71 K-2)
- `builder_state JSONB` — full React Flow canvas state for Builder Mode; null for Custom Query datasets
- `version INTEGER NOT NULL DEFAULT 1` — optimistic concurrency counter
- `created_at TIMESTAMPTZ DEFAULT now()`, `created_by UUID REFERENCES users(id)`, `updated_at TIMESTAMPTZ`, `updated_by UUID REFERENCES users(id)`
- `UNIQUE` constraint on `dataset_name` enforces uniqueness at DB level (second line of defense behind application validation)

`dataset_audit_log`:
- `id UUID PK`, `timestamp TIMESTAMPTZ DEFAULT now()`, `actor_id UUID REFERENCES users(id)`, `actor_name TEXT`, `dataset_name TEXT NOT NULL`, `operation TEXT NOT NULL CHECK (operation IN ('CREATE','UPDATE','DELETE'))`, `previous_values JSONB`, `new_values JSONB`, `ddl TEXT`, `succeeded BOOLEAN NOT NULL DEFAULT true` (set false when DDL was attempted but rolled back — FR-61 H-9 AC-2), `correlation_id TEXT`
- Append-only; no API endpoint permits deletion

**View namespace:** `CREATE SCHEMA IF NOT EXISTS datasets` runs in the same EF migration that creates `custom_dataset`. All Dataset VIEWs are created as `datasets.{dataset_name}`. The `dataset_name` column stores only the bare name; all DDL uses the schema-qualified form. This eliminates all naming collision risk with application tables in `public` (e.g., a dataset named `users` becomes `datasets.users`; `public.users` is untouched).

**Identifier denylist** (enforced in `DatasetName.cs`): the following names are permanently blocked as `dataset_name` values even if they pass the regex and reserved-keyword checks: `users`, `roles`, `user_roles`, `menus`, `menu_role_assignments`, `component_schemas`, `refresh_tokens`, `password_reset_tokens`, `mfa_backup_codes`, `mfa_sessions`, `schema_audit_log`, `mutation_audit_log`, `dataset_audit_log`, `custom_dataset`. Defense-in-depth alongside the UNIQUE constraint.

**Indexes (added to migration):**
- `idx_custom_dataset_dataset_name` (UNIQUE — covers the constraint)
- `idx_dataset_audit_log_dataset_name_timestamp (dataset_name, timestamp DESC)`
- `idx_dataset_audit_log_operation (operation)`

#### 6.2 — dataset-management Permission Model (FR-56, OQ-13 resolved)

**Decision: `can_manage_datasets BOOLEAN NOT NULL DEFAULT false` column on `roles` table (EF migration).** Existing role rows default to `false`; `platform-admin` seed updated to `true`. This is a platform-wide capability flag, not per-resource.

- `EffectivePermissions` record (Decision 2.2) gains `CanManageDatasets: bool`.
- New route filter: `RequireDatasetManagement()` extension on `RouteGroupBuilder` — checks `CanManageDatasets`; HTTP 403 on failure with `{ code: "FORBIDDEN", action: "dataset-management" }`.
- Admin > Roles permission matrix gains a "Dataset Management" row toggle (distinct from per-resource CRUD flags; `PerResource` map is not modified).
- `GET /api/datasets` and `GET /api/datasets/{id}` require authentication but not `dataset-management` (read is open to all authenticated users per A18). All write/delete/preview endpoints require `dataset-management`.

#### 6.3 — Transactional View Lifecycle (FR-58, AD-17 resolved)

All row writes and their paired VIEW DDL execute **synchronously** in a Dapper `NpgsqlTransaction`. Unlike table provisioning (async via BackgroundService), Dataset VIEW DDL is fast; the HTTP response waits for commit and immediately surfaces errors to the caller.

| Operation | Transaction Steps |
|---|---|
| **Create** | `INSERT INTO custom_dataset ...` (version=1) + `CREATE VIEW datasets.{name} AS {query}` |
| **Edit — same name** | `UPDATE custom_dataset ... WHERE id=@id AND version=@v` (version+=1) + `CREATE OR REPLACE VIEW datasets.{name} AS {new_query}` |
| **Rename** | `UPDATE custom_dataset SET dataset_name={new}, version+=1 ... WHERE id=@id AND version=@v` + `ALTER VIEW datasets.{old_name} RENAME TO {new_name}` |
| **Delete** | `DELETE FROM custom_dataset WHERE id=@id` + `DROP VIEW IF EXISTS datasets.{name}` |

**Rename atomicity (AD-17):** `ALTER VIEW ... RENAME TO` is a single atomic DDL statement inside the transaction. PostgreSQL DDL is fully transactional — rollback reverts both the row update and the rename atomically. No intermediate state where neither name exists. Edge case (view missing at rename time): fall through to `CREATE VIEW datasets.{new_name} AS {query}` + structured warning log.

**Rollback guarantee:** any failure at any transaction step rolls back the entire unit. An existing working VIEW is never left corrupted. Failed DDL attempts are recorded in `dataset_audit_log` with `succeeded = false` and the attempted DDL string.

#### 6.4 — Optimistic Concurrency (FR-59)

`version INTEGER NOT NULL DEFAULT 1` on `custom_dataset`. Every PUT /api/datasets/{id} must include `version` in the request body. UPDATE uses `WHERE id = @id AND version = @expectedVersion`; 0 rows affected → HTTP 409 `{ code: "DATASET_CONCURRENCY_CONFLICT", currentVersion: N }`. `version` is incremented within the same transaction as the VIEW DDL so the counter and view state are always consistent.

---

#### Security — Dataset Manager

#### 6.5 — SELECT-Only SQL Enforcement (FR-60, AD-15 resolved)

**Decision: `PgQuery.NET` NuGet** (wraps PostgreSQL's own `libpg_query`). Implemented in `SqlSelectEnforcer.cs`.

```
Algorithm:
  1. PgQuery.Parse(sql)
     → throws ParseException: HTTP 422 INVALID_QUERY "SQL could not be parsed"
  2. Assert root statement node is SelectStmt
     Permitted roots: SelectStmt, WithClause → SelectStmt (CTEs)
     Rejected roots: InsertStmt, UpdateStmt, DeleteStmt, CreateStmt, DropStmt,
                     CopyStmt, DoStmt, CallStmt, any non-SELECT
     → HTTP 422 INVALID_QUERY "Only SELECT statements are permitted"
```

Applied at three checkpoints:
- (a) Custom Query create/update — before VIEW DDL.
- (b) Server-generated SQL from `builder_state` — before VIEW DDL or preview (generator output must be SELECT-only even though it was generated server-side).
- (c) Preview execution — before query runs (defense in depth; catches any edge case the generator could produce).

#### 6.6 — Table Allowlist & Catalog Source (FR-63, AD-14 resolved, OQ-11 resolved)

**Decision: `appsettings.json` configuration section**, overridable via standard .NET env vars.

```json
// appsettings.json
"DatasetManager": {
  "AllowedTables": ["incident_report", "products", "..."],
  "PreviewTimeoutSeconds": 5
}
```

Env-var override: `DatasetManager__AllowedTables__0=table_a`, `DatasetManager__AllowedTables__1=table_b` (standard .NET config array binding). No DB table in v1; admin UI management is out of scope per §5 Non-Goals.

**Startup validation:** `DatasetAllowlistValidator` (runs at `WebApplication.Build()`) cross-checks every configured table against `information_schema.tables WHERE table_schema = 'public'`. Missing tables → `ILogger.LogWarning` (not fatal; supports pre-migration deploys). Denylist (Decision 6.1) applied here — internal tables stripped from the effective allowlist even if manually listed in config.

**Catalog endpoint:** `GET /api/datasets/catalog` (requires `dataset-management`) returns:
```json
{
  "tables": [
    { "tableName": "incident_report",
      "columns": [{ "columnName": "title", "pgType": "text", "isNullable": true }] }
  ]
}
```
Column metadata from `information_schema.columns WHERE table_schema = 'public' AND table_name = ANY(@allowlist)`. Result cached in `IMemoryCache` with 5-min TTL.

**SQL Generator enforcement:** `DatasetSqlGenerator` validates every `node.data.tableName` in `builder_state.nodes` against the allowlist cache before generating SQL. Non-allowlisted table → HTTP 422 `TABLE_NOT_ALLOWLISTED` before any DDL or preview execution.

#### 6.7 — Preview Execution Security & Isolation (FR-72, AD-16 resolved)

**Dedicated read-only PostgreSQL role `formforge_preview`** created in the same migration (executed via Dapper DDL in an `IHostedService.StartAsync`):
```sql
CREATE ROLE formforge_preview LOGIN NOINHERIT;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO formforge_preview;
-- Revoke internal tables individually:
REVOKE SELECT ON users, roles, refresh_tokens, password_reset_tokens,
                 mfa_backup_codes, mfa_sessions, schema_audit_log,
                 mutation_audit_log, dataset_audit_log, custom_dataset
FROM formforge_preview;
```

**Separate Npgsql connection pool:** `IPreviewConnectionFactory` / `PreviewConnectionFactory` wraps a dedicated `NpgsqlDataSource` built with the `formforge_preview` credentials. `MaxPoolSize = 5` — caps preview concurrency to prevent starvation of CRUD operations.

**Execution:**
```sql
BEGIN;
SET LOCAL statement_timeout = '5s';  -- value from DatasetManager:PreviewTimeoutSeconds
SELECT * FROM ({user_or_generated_query}) AS _preview LIMIT 10;
-- parameters bound via Npgsql typed parameter array
COMMIT;
```
`NpgsqlException.SqlState == "57014"` (query_canceled / statement_timeout) → HTTP 408 `PREVIEW_TIMEOUT`.
PostgreSQL error → HTTP 422 with PG error message (no stack traces exposed).
Returns: `{ columns: string[], rows: object[][] }` — up to 10 rows.

#### 6.8 — CASE and Calculated Column Expression Security (FR-67, AD-19 resolved)

Three validation layers in `ExpressionSecurityValidator.cs`, applied before any expression is included in generated SQL:

1. **Per-expression keyword scan:** reject if expression (trimmed, case-insensitive) starts with `DROP`, `INSERT`, `UPDATE`, `DELETE`, `CREATE`, `ALTER`, `TRUNCATE`, `MERGE`, `CALL`; or contains an unquoted `;`.
2. **Wrap-parse:** `PgQuery.Parse($"SELECT ({expression}) AS _x FROM generate_series(1,1) _t")`. Failure → HTTP 422 identifying the offending expression by alias.
3. **Final assembled query:** full `SelectStmt` check (Decision 6.5) on the completely assembled SQL as the absolute backstop.

Residual risk: attack surface is `dataset-management` users only — the same users who can write raw SQL in Custom Query Mode. The layers prevent accidents and casual abuse; a determined admin can always use Custom Query Mode to write arbitrary SELECT queries anyway.

---

#### API & Communication — Dataset Manager

#### 6.9 — Dataset API Contract

Route group: `app.MapGroup("/api/datasets").RequireAuth().MapDatasetEndpoints()`

| Method | Path | Permission | Response | Description |
|---|---|---|---|---|
| GET | /api/datasets | auth | `PagedResult<DatasetSummaryDto>` | List datasets (paginated, default 25) |
| GET | /api/datasets/{id} | auth | `DatasetDto` | Get dataset incl. `query` + `builder_state` |
| GET | /api/datasets/catalog | `dataset-management` | `CatalogDto` | Allowlisted tables + columns |
| POST | /api/datasets | `dataset-management` | 201 `DatasetDto` | Create dataset + VIEW atomically |
| PUT | /api/datasets/{id} | `dataset-management` | 200 `DatasetDto` | Update dataset + VIEW atomically |
| DELETE | /api/datasets/{id} | `dataset-management` | 204 | Delete dataset + VIEW atomically |
| POST | /api/datasets/preview | `dataset-management` | `PreviewResultDto` | Execute preview (LIMIT 10, timeout) |
| GET | /api/admin/datasets/audit | `platform-admin` | `PagedResult<DatasetAuditEntryDto>` | Dataset audit log (filterable) |

**New error codes** (appended to `ErrorCodes.cs`):

| Code | HTTP | Meaning |
|---|---|---|
| `INVALID_QUERY` | 422 | SQL not SELECT-only or fails PgQuery.NET parse |
| `INVALID_DATASET_NAME` | 422 | Name fails identifier rules or denylist |
| `DATASET_NAME_CONFLICT` | 409 | Duplicate `dataset_name` |
| `DATASET_CONCURRENCY_CONFLICT` | 409 | `version` mismatch on update |
| `PREVIEW_TIMEOUT` | 408 | Statement timeout exceeded |
| `TABLE_NOT_ALLOWLISTED` | 422 | builder_state references a non-allowlisted table |
| `BUILDER_STATE_INVALID` | 422 | No left table, no columns selected, alias empty, etc. |

#### 6.10 — Server-Authoritative SQL Generator (FR-70, K-1)

`DatasetSqlGenerator.cs` — pure deterministic function, no I/O, no side effects:

```
Input:  BuilderStateDto (deserialized from custom_dataset.builder_state JSONB)
Output: SqlGenerationResult { Sql: string, Parameters: object[] }
     OR SqlGenerationResult { Errors: ValidationError[] }
```

**Algorithm:**
1. **Pre-flight validation:** one `side='left'` node exists; ≥1 column checked across all nodes; all CASE/calculated column aliases non-empty → else return `BUILDER_STATE_INVALID`.
2. **Allowlist validation:** every `node.data.tableName` in the allowlist cache → else `TABLE_NOT_ALLOWLISTED`.
3. **Identifier safety:** all `tableName`, `columnName`, `alias` values passed through `SafeIdentifier.Create()` (re-uses Decision 1.1 value type). Failure → `BUILDER_STATE_INVALID`.
4. **FROM clause:** `FROM "public"."<leftNode.tableName>"` (with self-join alias `"t<index>"` when the same table appears twice).
5. **JOIN clauses:** for each `JoinEdge`, emit `<joinType> JOIN "public"."<targetTable>" ON "<leftTable>"."<sourceHandle>" = "<targetTable>"."<targetHandle>"`.
6. **SELECT list:**
   - Plain checked column: `"table"."col" AS "alias_or_col"`
   - Aggregated column: `AGG("table"."col") AS "alias"`
   - CASE column: `CASE WHEN <condition> THEN <then> [ELSE <else>] END AS "alias"` (condition rendered as a parameterized predicate)
   - Calculated column: `(<expression>) AS "alias"` — expression validated by Decision 6.8 before inclusion
7. **GROUP BY:** if any aggregate exists → append `GROUP BY` listing all non-aggregated SELECT columns by their qualified form.
8. **WHERE clause:** recursively render `FilterGroup` → `((A AND B) OR (C AND D))`. Filter values collected into `Parameters[]` as `$1`, `$2`, … — never string-interpolated. NULL-handling operators (`IS NULL`, `IS NOT NULL`) emit no parameter.
9. **ORDER BY:** `ORDER BY "table"."col" ASC|DESC, …` in declared clause order. Empty → omit clause.
10. **SELECT-only validation** (Decision 6.5): final assembled SQL checked via `PgQuery.NET`. Failure → `INVALID_QUERY` (returned as error, no DDL runs).
11. Return `{ Sql, Parameters }`.

**`query` column sync:** on every successful builder-mode save, `custom_dataset.query` is set to the generated SQL within the same transaction, so `builder_state` and `query` are always in sync (FR-71, K-2 AC-4).

---

#### Frontend Architecture — Dataset Manager

#### 6.11 — builder_state Schema Contract (AD-18 resolved)

Canonical cross-layer contract defined in `web/src/features/datasets/types/builderState.ts`. The C# `BuilderStateDto` record hierarchy in `DatasetSqlGenerator.cs` mirrors this interface exactly. Changes are a **breaking cross-layer contract change** requiring coordinated frontend + backend update.

```typescript
export interface BuilderState {
  nodes: TableNode[];
  edges: JoinEdge[];
  filters: FilterGroup;
  orderBy: OrderByClause[];
  caseColumns: CaseColumn[];
  calculatedColumns: CalculatedColumn[];
}
export interface TableNode {
  id: string; type: 'table';
  position: { x: number; y: number };
  data: { tableName: string; side: 'left' | 'right'; columns: ColumnConfig[]; };
}
export interface ColumnConfig {
  columnName: string; checked: boolean;
  aggregate?: 'COUNT' | 'SUM' | 'AVG' | 'MIN' | 'MAX';
  alias?: string;
}
export interface JoinEdge {
  id: string; source: string; sourceHandle: string;
  target: string; targetHandle: string;
  data: { joinType: 'INNER' | 'LEFT' | 'RIGHT' | 'FULL OUTER'; };
}
export interface FilterGroup {
  combinator: 'AND' | 'OR';
  items: Array<FilterCondition | FilterGroup>;
}
export interface FilterCondition {
  nodeId: string; column: string;
  operator: '=' | '!=' | '<' | '<=' | '>' | '>='
          | 'IS NULL' | 'IS NOT NULL' | 'LIKE' | 'ILIKE'
          | 'IN' | 'NOT IN' | 'BETWEEN';
  value: unknown;
  valueType: 'string' | 'number' | 'boolean' | 'date' | 'array';
}
export interface OrderByClause { nodeId: string; column: string; direction: 'ASC' | 'DESC'; }
export interface CaseColumn {
  nodeId: string; alias: string;
  whens: Array<{ condition: FilterCondition; thenValue: string | number | null; }>;
  elseValue?: string | number | null;
}
export interface CalculatedColumn { nodeId: string; expression: string; alias: string; }
```

#### 6.12 — React Flow Integration

- **New dependency:** `@xyflow/react` v12 — `npm install @xyflow/react`. (This is the current package name for React Flow; the old `reactflow` package is deprecated.)
- **Custom node `TableNode.tsx`** (`components/query-builder/`): renders table name header, Left/Right side toggle, column list with checkbox per column, aggregate dropdown, alias input, "Add Case" and "Add Calculated Column" buttons. `onNodesChange` from `useNodesState` keeps React Flow state in sync with `builderState.nodes`.
- **Custom edge `JoinEdge.tsx`**: styled curve with delete control. Click handler calls `setSelectedEdge(edge)`.
- **`JoinInspector.tsx`**: popover rendered when `selectedEdge` is set; shows joined columns + join type selector (INNER / LEFT / RIGHT / FULL OUTER); commits to `builderState.edges[i].data.joinType`.
- **`builder_state` as the source of truth:** `builderState` in React state is the canonical canvas state. Serialized to JSON on every save (`PUT /api/datasets/{id}`). Deserialized from API response on dataset open (canvas restored position-for-position).

#### 6.13 — TanStack Query Keys (Dataset Manager)

Follows the tuple convention (Implementation Patterns — Communication Patterns):
- `['datasets', 'list', { page, pageSize }]`
- `['datasets', id]`
- `['datasets', 'catalog']`
- `['datasets', id, 'preview', previewKey]` — `previewKey` is a stable hash of `{ builderState | query }` to avoid double-fetch on identical preview requests
- `['datasets', 'audit', { page, datasetName?, operation? }]`

---

### Implementation Sequence (Decision Dependencies)

1. **S0 (Infrastructure):** Decisions 5.* (Aspire AppHost, Compose, observability, health checks, container, env config).
2. **S1 (Auth):** Decisions 2.* (JWT flow, password hashing, headers, CORS, rate limit) + 3.1 (error envelope) + 4.7 (httpClient).
3. **S2–S3 (Designer):** Decisions 4.5 (keyboard DnD), 4.10 (DynamicComponent bridge).
4. **S4 (Menu):** Decisions 3.5 (route groups), 3.4 (pagination shape).
5. **S5 (Provisioning):** Decisions 1.1–1.7 (identifier sanitization, type mapping, cascade, schema registry, audit indexing, EF/Dapper boundary, migrations).
6. **S6 (CRUD):** Decisions 3.3 (dynamic validation), 3.6 (re-bind diff), 3.7 (OpenAPI), 3.8 (correlation), 3.9 (idempotency posture), 4.1 (presigned URLs).
7. **S7–S8 (UX & polish):** Decisions 4.2 (theme), 4.3 (errors), 4.4 (toasts), 4.6 (folders), 4.8 (i18n), 4.9 (forms).
8. **S9 (Dataset Foundation):** Decisions 6.1–6.4 (schema + migration + `datasets` schema + view lifecycle + optimistic concurrency) + 6.2 (permission model) + 6.5 (SELECT-only enforcer) + 6.9 (API contract).
9. **S10 (Query Builder Canvas):** Decisions 6.6 (allowlist/catalog) + 6.12 (React Flow integration); catalog endpoint live; Table Palette, TableNode, JoinEdge, JoinInspector, side-designation all functional.
10. **S11 (Builder Config):** Decision 6.11 (`builder_state` contract) + 6.10 (SQL generator, incl. column selection, aggregates, GROUP BY, CASE, calculated columns, filter groups, ORDER BY); `builder_state` persisted and restored.
11. **S12 (SQL Gen, Preview & Sync):** Decision 6.7 (preview pool + `formforge_preview` role) + 6.8 (expression security) + preview endpoint live; builder-mode save reuses view lifecycle; builder_state + query always in sync.

### Cross-Component Dependencies

- **Schema registry (1.4)** feeds: AD-9 image serialization (4.1), AD-5 cascade walk (1.3), AD-7 dynamic validation (3.3), AD-3 identifier whitelisting (1.1).
- **Correlation ID (3.8)** flows through: all logs, both audit tables (1.5 schema addition), Dapper SQL comments, error envelope (3.1), HTTP client (4.7).
- **Async provisioning (1.6)** drives: re-bind UI polling (3.6), Menu Item `provisioningStatus` column, toast notifications (4.4).
- **In-memory cache backend (5.1)** is the integration point for 1.4 + 2.2 — both will swap to Redis simultaneously in v2.
- **Single-origin hosting (5.5)** simplifies 2.5 (CORS), 4.7 (no base URL needed in prod), 2.1 (refresh cookie path).
- **CSP nonce (2.7)** required by 4.2 (theme script) — wired via response rewriter in 5.5.
- **Dataset Manager cross-component dependencies:**
  - `SafeIdentifier` value type (1.1) reused by `DatasetName` validator (6.1) and `DatasetSqlGenerator` identifier quoting (6.10).
  - `ICacheStore` (5.1) hosts the allowlist/catalog cache (6.6) — Redis swap in v2 covers this automatically.
  - `EffectivePermissions` (2.2) extended with `CanManageDatasets` (6.2); `roles.can_manage_datasets` column is a single EF migration.
  - `IMemoryCache` (5.1) used for catalog TTL cache (6.6) and preview result cache (6.13).
  - Correlation ID (3.8) propagated into `dataset_audit_log.correlation_id` (6.1) and Dapper SQL comments for preview queries.
  - Error envelope (3.1) extended with new Dataset error codes (6.9); all codes follow the same `ProblemDetails` shape.
  - Pagination shape (3.4) reused for `GET /api/datasets` and audit log endpoints (6.9).

## Implementation Patterns & Consistency Rules

### Pattern Categories Defined

Six categories of patterns are defined below to prevent divergent choices across AI agents working on this project: naming, structure, format, communication, process, and logging. All patterns flow from the Core Architectural Decisions; only the dynamic-endpoint JSON casing required explicit user input (Option C selected).

### Naming Conventions

**Database (PostgreSQL):**
- Tables: `snake_case`. Plural for static (`users`, `roles`, `menus`). Singular for dynamic (table name = user-authored `designerId`, e.g., `incident_report`).
- Columns: `snake_case` (`created_at`, `is_deleted`, `cascade_event_id`).
- Foreign keys: `parent_{parentDesignerId}_id` for Repeater (FR-27 AC-2); `{entity}_id` for static FKs.
- Indexes: `idx_{table}_{columns_joined_by_underscore}` (matches FR-27 AC-3).
- Constraints: `pk_{table}`, `fk_{table}_{referenced}`, `uq_{table}_{columns}`, `ck_{table}_{rule}`.
- Audit tables: `schema_audit_log`, `mutation_audit_log` (per PRD).

**API endpoints:**
- Plural nouns for collections: `/api/users`, `/api/roles`, `/api/menus`, `/api/designers`. Exception: `/api/data/{designerId}` (designerId IS the user-authored collection name).
- Path parameters: `{name}` minimal-API style.
- Query parameters: `camelCase` (`pageSize`, `sort`, `filter[key]`). Filter syntax: `filter[fieldKey]=value`.
- Headers: `Title-Case-Hyphenated` (`X-Correlation-ID`, `Retry-After`).
- HTTP verbs: GET (read), POST (create), PUT (full or partial update — PRD FR-32 honored), DELETE (soft delete).

**C# code:**
- Types/methods: `PascalCase`. Interfaces: `IPrefix` (`IProvisioningService`).
- Private fields: `_camelCase` (Microsoft convention).
- Parameters / locals: `camelCase`.
- Constants: `PascalCase` public; `UPPER_SNAKE` for compile-time literals only.
- Async methods: `Async` suffix (`ProvisionTableAsync`).
- Filenames match primary type (`ProvisioningService.cs`).
- Test files: `{ClassUnderTest}Tests.cs` in `*.Tests` projects.

**TypeScript / React:**
- React components: `PascalCase` for names and filenames (`DesignerCanvas.tsx`).
- Hooks: `useCamelCase` (`useAuthQuery`, `useKeyboardDnD`).
- Non-component utilities: `camelCase.ts` (`tokenStore.ts`, `httpClient.ts`).
- Types/interfaces: `PascalCase` (`type EffectivePermissions`).
- Constants: `UPPER_SNAKE_CASE` for compile-time; `camelCase` for derived.
- Test files: `{Component}.test.tsx` co-located.

### JSON Field Naming

**Static endpoints:** `camelCase` end-to-end. `System.Text.Json` policy `JsonNamingPolicy.CamelCase`.

**Dynamic data endpoints (`/api/data/{designerId}`):** **Option C — hybrid translation.**
- System columns translated: `created_at` → `createdAt`, `cascade_event_id` → `cascadeEventId`, `is_deleted` → `isDeleted`, etc.
- User-authored fieldKeys preserved verbatim: `report_title` stays as `report_title`.
- Schema registry distinguishes system vs user columns; custom `JsonConverter<DynamicRecord>` applies translation selectively.

Rationale: user fieldKeys are authored intent (admin typed them in the canvas); preserving them respects authorship and avoids surprising integrators. System columns stay consistent with the rest of the API.

### Structure Patterns

**Backend layout (vertical feature slicing):**
```
src/
├── FormForge.AppHost/             # Aspire orchestrator only
├── FormForge.ServiceDefaults/     # OTel, health checks, resilience extensions
├── FormForge.Api/
│   ├── Features/                  # Feature folders own endpoints + services + validators + DTOs
│   │   ├── Auth/
│   │   ├── Users/
│   │   ├── Roles/
│   │   ├── Designers/
│   │   ├── Menus/
│   │   ├── Provisioning/          # IProvisioningService, ProvisioningBackgroundService, DDL emitter
│   │   ├── Permissions/           # PermissionService, EffectivePermissionsCache
│   │   ├── SchemaRegistry/        # ISchemaRegistry, ColumnDefinition, etc.
│   │   ├── DynamicCrud/           # Generic CRUD handlers, dynamic payload validator
│   │   ├── Files/                 # MinIO upload, presigned URL service
│   │   └── Audit/                 # Schema audit + mutation audit query endpoints
│   ├── Infrastructure/            # PG connection factory, MinIO client, cache primitives, event bus
│   ├── Domain/                    # Static EF entities, value types (SafeIdentifier, FieldKey)
│   ├── Common/                    # Cross-cutting: ProblemDetails mapper, endpoint filters, IExceptionHandler
│   ├── wwwroot/                   # SPA build artifacts (populated at container build)
│   └── Program.cs                 # Minimal API composition root
└── FormForge.Api.Tests/           # xUnit + Testcontainers.PostgreSQL
```

**Frontend layout:** see Decision 4.6.

**Rule:** vertical feature slicing wins over horizontal layer slicing. Each feature folder owns its endpoints, services, validators, DTOs, and tests.

### Format Patterns

**Response envelope:**
- **Success:** direct payload, no `{ data, error }` wrapping. List endpoints wrap in `PagedResult<T>` (Decision 3.4) — the only wrapper.
- **Error:** RFC 7807 `ProblemDetails` (Decision 3.1) — always.

**Dates and times:**
- Wire format: ISO 8601 with `Z` suffix (`"2026-05-22T10:35:00Z"`). UTC always.
- PostgreSQL: `TIMESTAMPTZ` everywhere; never `TIMESTAMP`.
- Frontend: parse to `Date` on receipt; format via `Intl.DateTimeFormat` keyed off i18n locale.

**Booleans:** JSON `true`/`false`; PG `BOOLEAN`. No 0/1, no `"Y"`/`"N"`.

**Nullability:** explicit `null` for nullable fields that are absent — don't omit.

**IDs:** `UUID` v7 (time-ordered) for primary keys via `gen_random_uuid()`. Correlation IDs are ULIDs (Decision 3.8). String form in JSON.

**Money / decimals:** `NUMERIC(precision, scale)` in PG, `decimal` in C#, `string` in JSON (forward-compatibility; not used in v1).

### Communication Patterns

**Domain events (in-process event bus):**
- Naming: `PascalCase` past-tense (`SchemaPublished`, `UserDeactivated`, `RoleAssignmentChanged`, `MenuBindingCreated`).
- Payloads: `record` types, immutable. Minimal — IDs + operation type; handlers re-fetch state.
- One handler does one thing. Multiple handlers per event allowed.

**TanStack Query keys (tuple convention):**
- `['scope', 'entity', ...params]`.
- Examples:
  - `['auth', 'me']`
  - `['designers', 'list']`
  - `['designers', designerId, 'versions']`
  - `['data', designerId, 'list', { page, pageSize, sort, filter }]`
  - `['data', designerId, 'record', recordId]`
  - `['files', 'urls', objectKeys]`
- Invalidation: `invalidateQueries({ queryKey: ['data', designerId] })` nukes all variants for a designer.

**Mutations:**
- Optimistic only for: theme change, soft-delete row removal, drag-reorder.
- Pessimistic (default) for: form saves, DDL-triggering operations, role assignments.

### Process Patterns

**Loading states:**
- Route-level: TanStack Router `pendingComponent`.
- Query-level: `useQuery().isPending` for inline UI; `isFetching` for "background refresh" indicators.
- Mutation-level: `useMutation().isPending` for disabling submit buttons.

**Error states (FR-41 AC-3):**
- **Toast (sonner)** — transient, non-blocking.
- **Inline banner with retry** — blocking (list/detail fetch failed).
- **Inline field error** — validation (from `ValidationProblemDetails.errors` via `setError`).
- **Error Boundary fallback** — catastrophic; shows correlation ID.
- All error text via `t(error.messageKey, error.details)`. Never raw English in JSX.

**Retry:**
- HTTP client: one automatic 401-handling retry after refresh (Decision 4.7).
- TanStack Query: default 3 retries with exponential backoff for `5xx` and network errors only. `retry: (count, err) => err.status >= 500 && count < 3`.
- Mutations: no auto-retry; user retries.

**Validation timing:**
- Forms: per Decision 4.9 (`onSubmit` data entry; `onChange` admin with interdependencies).
- API: per Decision 3.3 (FluentValidation static; dynamic validator for `/api/data/*`).

### Logging Conventions

**Levels:**
- `Trace` — explicit env var only; never prod.
- `Debug` — dev only.
- `Information` — request flow, DDL/CRUD fingerprints, lifecycle.
- `Warning` — recoverable degradation.
- `Error` — failed-but-caught.
- `Critical` — process-impacting.

**Required structured fields (FR-46 AC-2):**
- `timestamp`, `level`, `correlationId`, `userId` (if authenticated), `endpoint`, `message`, `exception` (if any).
- DDL/CRUD `Information` adds: `designerId`, `operation`, `sqlFingerprint` (parameterized only — never values, FR-46 AC-3).

**Anti-patterns (blocked at code review):**
- String interpolation in `ILogger` calls (defeats structured logging).
- Logging PII fields directly (`email`, `displayName`) in audit messages.
- Logging request bodies.

### Enforcement

**All AI agents MUST:**
- Run `dotnet format` + `eslint --fix` before committing.
- Use `SafeIdentifier` value type for any dynamic identifier touching SQL — never raw strings.
- Use the `httpClient` wrapper on frontend; never raw `fetch` outside `httpClient.ts`.
- Use TanStack Query keys following the tuple convention.
- Log via structured templates with named placeholders.
- Translate user-facing strings via `t(key)`.
- Add migrations via `dotnet ef migrations add`.

**Pattern verification:**
- ESLint config enforces import rules (Decision 4.6) + no raw `fetch` outside `httpClient.ts`.
- Roslyn analyzers (`Microsoft.CodeAnalysis.NetAnalyzers` + Meziantou.Analyzer) catch async-suffix, null-handling, string-interpolation in `ILogger`.
- CI runs `axe-core` against rendered DynamicComponent output (R-7 gate).
- PR template includes "Architecture pattern checklist" (folder structure, error envelope, JSON naming, log discipline).

### Pattern Anti-Examples

| Don't | Do |
|---|---|
| `DELETE FROM {designerId}` raw | `DELETE FROM {SafeIdentifier.Create(designerId)}` |
| `fetch('/api/data/...')` in a component | `httpClient.get('/api/data/...')` |
| `useQuery({ queryKey: ['users'] })` | `useQuery({ queryKey: ['users', 'list'] })` |
| `_logger.LogInformation($"Saved {id}")` | `_logger.LogInformation("Saved {RecordId}", id)` |
| `<button>Save</button>` | `<button>{t('common.actions.save')}</button>` |
| `{ "createdAt": "2026-05-22" }` | `{ "createdAt": "2026-05-22T00:00:00Z" }` |

## Project Structure & Boundaries

### Complete Project Directory Structure

```
tinnitus/                                       # Repo root (FormForge product)
├── .editorconfig
├── .gitattributes
├── .gitignore
├── .github/
│   └── workflows/
│       ├── ci.yml                              # PR pipeline (Decision 5.9)
│       ├── deploy-staging.yml                  # main-merge pipeline
│       └── codeql.yml                          # SAST
├── .config/
│   └── dotnet-tools.json                       # Pinned dotnet-ef, dotnet-format
├── FormForge.sln
├── Directory.Build.props                       # Repo-wide MSBuild defaults
├── Directory.Packages.props                    # Central Package Management
├── global.json                                 # Pinned .NET 10 SDK version
├── docker-compose.yml                          # Decision 5.10 (G-5)
├── docker-compose.override.yml                 # Dev overrides
├── Dockerfile                                  # Multi-stage build (Decision 5.6)
├── .dockerignore
├── README.md
├── LICENSE
│
├── docs/                                       # Living docs
│   ├── architecture.md                         # Stub pointing at _bmad-output
│   ├── runbooks/
│   │   ├── backup-restore.md                   # Decision 5.7
│   │   ├── secret-rotation.md                  # Decision 2.3
│   │   └── schema-drift-cleanup.md             # PRD R-3
│   ├── adr/                                    # Architecture Decision Records — post-architecture
│   └── onboarding.md
│
├── _bmad/                                      # BMad framework (vendored)
├── _bmad-output/                               # Planning + implementation artifacts
│   ├── planning-artifacts/
│   │   ├── architecture.md                     # THIS document
│   │   └── prds/
│   │       └── prd-tinnitus-2026-05-22/
│   │           ├── prd.md
│   │           └── addendum.md
│   └── implementation-artifacts/
│
├── src/
│   ├── FormForge.AppHost/                      # Aspire 13.1 orchestrator
│   │   ├── FormForge.AppHost.csproj
│   │   ├── Program.cs                          # AddPostgres + AddContainer(minio) + AddProject(api) + AddViteApp(web)
│   │   ├── appsettings.json
│   │   ├── appsettings.Development.json
│   │   └── Properties/launchSettings.json
│   │
│   ├── FormForge.ServiceDefaults/              # Shared service configuration
│   │   ├── FormForge.ServiceDefaults.csproj
│   │   ├── Extensions.cs                       # AddServiceDefaults() — OTel, health, resilience, discovery
│   │   └── OpenTelemetryExtensions.cs
│   │
│   ├── FormForge.Api/
│   │   ├── FormForge.Api.csproj
│   │   ├── Program.cs                          # Composition root; route group wiring
│   │   ├── appsettings.json
│   │   ├── appsettings.Development.json
│   │   ├── appsettings.Compose.json
│   │   │
│   │   ├── Common/                             # Cross-cutting (non-feature)
│   │   │   ├── Endpoints/
│   │   │   │   ├── EndpointFilters/
│   │   │   │   │   ├── ValidationFilter.cs
│   │   │   │   │   ├── CorrelationIdFilter.cs
│   │   │   │   │   └── RequirePermissionFilter.cs
│   │   │   │   └── RouteGroupExtensions.cs
│   │   │   ├── Errors/
│   │   │   │   ├── DomainException.cs
│   │   │   │   ├── ProblemDetailsExceptionHandler.cs
│   │   │   │   └── ErrorCodes.cs
│   │   │   ├── Json/
│   │   │   │   ├── DynamicRecordConverter.cs   # Option C casing translation
│   │   │   │   └── JsonOptions.cs
│   │   │   ├── Logging/
│   │   │   │   ├── CorrelationIdMiddleware.cs
│   │   │   │   └── LogContextExtensions.cs
│   │   │   ├── RateLimiting/
│   │   │   │   └── RateLimitPolicies.cs
│   │   │   ├── Security/
│   │   │   │   ├── SecurityHeadersExtensions.cs
│   │   │   │   └── CspNonceMiddleware.cs
│   │   │   └── Spa/
│   │   │       └── IndexHtmlRewriter.cs        # CSP nonce + theme injection
│   │   │
│   │   ├── Domain/
│   │   │   ├── Entities/
│   │   │   │   ├── User.cs                     # + MfaEnabled bool, MfaSecretProtected byte[]?
│   │   │   │   ├── Role.cs
│   │   │   │   ├── UserRole.cs
│   │   │   │   ├── Menu.cs
│   │   │   │   ├── MenuRoleAssignment.cs
│   │   │   │   ├── ComponentSchema.cs
│   │   │   │   ├── RefreshToken.cs
│   │   │   │   ├── PasswordResetToken.cs       # Decision 2.10
│   │   │   │   ├── MfaBackupCode.cs            # Decision 2.12
│   │   │   │   ├── SchemaAuditLogEntry.cs
│   │   │   │   └── MutationAuditLogEntry.cs
│   │   │   ├── ValueTypes/
│   │   │   │   ├── SafeIdentifier.cs           # Decision 1.1
│   │   │   │   ├── FieldKey.cs
│   │   │   │   └── DesignerVersion.cs
│   │   │   └── Enums/
│   │   │       ├── VersionStatus.cs            # Draft/Published/Archived
│   │   │       └── ProvisioningStatus.cs       # Pending/Success/Error
│   │   │
│   │   ├── Infrastructure/
│   │   │   ├── Persistence/
│   │   │   │   ├── FormForgeDbContext.cs
│   │   │   │   ├── DbConnectionFactory.cs
│   │   │   │   └── Migrations/
│   │   │   ├── Minio/
│   │   │   │   ├── MinioClientFactory.cs
│   │   │   │   └── PresignedUrlService.cs      # Decision 4.1
│   │   │   ├── Cache/
│   │   │   │   ├── ICacheStore.cs              # Decision 5.1
│   │   │   │   └── MemoryCacheStore.cs
│   │   │   ├── EventBus/
│   │   │   │   ├── IDomainEventBus.cs
│   │   │   │   └── InProcessEventBus.cs
│   │   │   └── HealthChecks/
│   │   │       └── MinioHealthCheck.cs
│   │   │
│   │   ├── Features/                           # Vertical feature folders
│   │   │   ├── Auth/
│   │   │   │   ├── AuthEndpoints.cs
│   │   │   │   ├── AuthService.cs
│   │   │   │   ├── PasswordHasher.cs
│   │   │   │   ├── JwtTokenService.cs
│   │   │   │   ├── EmailService.cs          # IEmailService + MailKitEmailService (Decision 2.9)
│   │   │   │   ├── MfaService.cs            # TOTP gen/verify, backup codes (Decision 2.12)
│   │   │   │   ├── PasswordResetService.cs  # Decision 2.10
│   │   │   │   ├── Validators/
│   │   │   │   ├── Dtos/
│   │   │   │   └── Events/UserLoggedIn.cs
│   │   │   ├── Users/
│   │   │   │   ├── UserEndpoints.cs            # /api/users/me + /api/admin/users/*
│   │   │   │   ├── UserService.cs
│   │   │   │   ├── Validators/
│   │   │   │   ├── Dtos/
│   │   │   │   └── Events/UserDeactivated.cs
│   │   │   ├── Roles/
│   │   │   │   ├── RoleEndpoints.cs
│   │   │   │   ├── RoleService.cs
│   │   │   │   └── Events/RolePermissionsChanged.cs
│   │   │   ├── Permissions/
│   │   │   │   ├── IPermissionService.cs
│   │   │   │   ├── EffectivePermissionsCache.cs
│   │   │   │   ├── EffectivePermissions.cs
│   │   │   │   └── PermissionsEndpoints.cs     # /api/users/me/permissions
│   │   │   ├── Menus/
│   │   │   │   ├── MenuEndpoints.cs
│   │   │   │   ├── MenuService.cs
│   │   │   │   ├── MenuCache.cs                # 5s TTL navbar cache
│   │   │   │   └── Events/MenuBindingCreated.cs
│   │   │   ├── Designers/
│   │   │   │   ├── DesignerEndpoints.cs
│   │   │   │   ├── DesignerService.cs
│   │   │   │   ├── VersionLifecycleService.cs  # Draft→Published→Archived rules
│   │   │   │   └── Events/SchemaPublished.cs
│   │   │   ├── SchemaRegistry/
│   │   │   │   ├── ISchemaRegistry.cs
│   │   │   │   ├── SchemaRegistry.cs
│   │   │   │   ├── ColumnDefinition.cs
│   │   │   │   ├── SchemaRegistryEntry.cs
│   │   │   │   ├── RootElementParser.cs
│   │   │   │   └── ComponentTypeMapper.cs      # Decision 1.2 table
│   │   │   ├── Provisioning/
│   │   │   │   ├── IProvisioningService.cs
│   │   │   │   ├── ProvisioningService.cs
│   │   │   │   ├── ProvisioningBackgroundService.cs
│   │   │   │   ├── DdlEmitter.cs
│   │   │   │   ├── CycleDetector.cs
│   │   │   │   ├── BindingDiffService.cs       # Decision 3.6
│   │   │   │   └── ProvisioningJob.cs
│   │   │   ├── DynamicCrud/
│   │   │   │   ├── DynamicDataEndpoints.cs     # /api/data/{designerId}/*
│   │   │   │   ├── DynamicQueryBuilder.cs
│   │   │   │   ├── DynamicPayloadValidator.cs  # Decision 3.3 Layer 2
│   │   │   │   ├── DynamicRecord.cs
│   │   │   │   ├── SoftDeleteCascade.cs        # Decision 1.3 recursive walk
│   │   │   │   └── RepeaterWriteCoordinator.cs # FR-35 nested write
│   │   │   ├── Files/
│   │   │   │   ├── FileEndpoints.cs            # /api/files/{upload,refresh-urls}
│   │   │   │   ├── UploadValidator.cs
│   │   │   │   └── Dtos/
│   │   │   └── Audit/
│   │   │       ├── AuditEndpoints.cs
│   │   │       └── AuditService.cs
│   │   │
│   │   ├── wwwroot/                            # SPA build artifacts (populated by Dockerfile stage 3)
│   │   └── Properties/launchSettings.json
│   │
│   └── FormForge.Api.Tests/
│       ├── FormForge.Api.Tests.csproj
│       ├── Features/
│       │   ├── Auth/AuthEndpointsTests.cs
│       │   ├── Provisioning/
│       │   │   ├── DdlEmitterTests.cs
│       │   │   ├── CycleDetectorTests.cs
│       │   │   └── ProvisioningIntegrationTests.cs
│       │   ├── DynamicCrud/
│       │   │   ├── DynamicQueryBuilderTests.cs
│       │   │   ├── SoftDeleteCascadeTests.cs
│       │   │   └── DynamicCrudIntegrationTests.cs
│       │   ├── SchemaRegistry/SchemaRegistryTests.cs
│       │   └── Permissions/EffectivePermissionsTests.cs
│       ├── Infrastructure/
│       │   ├── PostgresFixture.cs              # Testcontainers shared fixture
│       │   └── MinioFixture.cs
│       └── Common/
│           └── ApiTestHost.cs                  # WebApplicationFactory<Program>
│
├── web/                                        # React 19 + Vite + TS + shadcn/ui
│   ├── package.json
│   ├── package-lock.json
│   ├── vite.config.ts
│   ├── tsconfig.json
│   ├── tsconfig.app.json
│   ├── tsconfig.node.json
│   ├── eslint.config.js                        # Flat config + import/no-restricted-paths
│   ├── components.json                         # shadcn registry config
│   ├── tailwind.config.ts
│   ├── index.html                              # Inline theme script + CSP nonce slot
│   ├── .env.development
│   ├── .env.production
│   ├── public/
│   ├── src/
│   │   ├── main.tsx                            # i18n init + Router + QueryClient
│   │   ├── routeTree.gen.ts                    # Generated by TanStack Router plugin
│   │   ├── styles/
│   │   │   ├── globals.css
│   │   │   └── themes.css                      # [data-theme='*'] var definitions
│   │   ├── routes/
│   │   │   ├── __root.tsx                      # Toaster, ErrorBoundary, theme apply
│   │   │   ├── login.tsx
│   │   │   ├── forgot-password.tsx             # FR-51 AC-7
│   │   │   ├── reset-password.tsx              # FR-51 AC-7
│   │   │   ├── _app.tsx                        # Authenticated layout + navbar
│   │   │   └── _app/
│   │   │       ├── index.tsx
│   │   │       ├── data.$designerId.tsx        # Record list (FR-40)
│   │   │       ├── data.$designerId.$recordId.tsx
│   │   │       ├── data.$designerId.new.tsx
│   │   │       ├── designer.$designerId.tsx    # Canvas (Epic B)
│   │   │       ├── designer.library.tsx
│   │   │       └── admin/
│   │   │           ├── users.tsx
│   │   │           ├── users.$userId.tsx
│   │   │           ├── roles.tsx
│   │   │           ├── roles.$roleId.tsx
│   │   │           ├── menus.tsx
│   │   │           ├── designers.tsx
│   │   │           ├── designers.$designerId.tsx
│   │   │           ├── designers.$designerId.audit.tsx
│   │   │           └── designers.$designerId.drift.tsx
│   │   ├── components/
│   │   │   ├── ui/                             # shadcn primitives
│   │   │   ├── designer/                       # Ported from ESG Platform
│   │   │   │   ├── DesignerCanvas.tsx
│   │   │   │   ├── DynamicComponent.tsx
│   │   │   │   ├── ElementRenderer.tsx
│   │   │   │   ├── DesignerToolbar.tsx
│   │   │   │   ├── DropZone.tsx
│   │   │   │   ├── PropertiesPanel.tsx
│   │   │   │   ├── useKeyboardDnD.ts           # Decision 4.5 / Story B-11
│   │   │   │   ├── computeVisibility.ts
│   │   │   │   └── types.ts
│   │   │   ├── shared/
│   │   │   │   ├── PagedList.tsx
│   │   │   │   ├── ErrorBanner.tsx
│   │   │   │   ├── FormField.tsx               # shadcn + react-hook-form adapter
│   │   │   │   ├── PermissionGate.tsx
│   │   │   │   └── ProvisioningStatusBadge.tsx
│   │   │   └── icons/LucideIcon.tsx
│   │   ├── features/
│   │   │   ├── auth/
│   │   │   │   ├── tokenStore.ts
│   │   │   │   ├── httpClient.ts               # Decision 4.7
│   │   │   │   ├── useAuthQuery.ts
│   │   │   │   ├── permissions.ts
│   │   │   │   ├── usePermission.ts
│   │   │   │   ├── authMutations.ts
│   │   │   │   ├── useMfaEnrolment.ts          # Decision 2.12 enrolment flow
│   │   │   │   └── mfaMutations.ts             # TOTP verify + backup code verify
│   │   │   ├── designer/
│   │   │   ├── menu/
│   │   │   │   ├── useMenus.ts
│   │   │   │   ├── useBindingDiff.ts
│   │   │   │   └── usePollProvisioning.ts
│   │   │   ├── data-entry/
│   │   │   │   ├── useRecordList.ts
│   │   │   │   ├── useRecordDetail.ts
│   │   │   │   ├── useDynamicFormMutation.ts   # Decision 4.10 bridge
│   │   │   │   └── filterParams.ts             # validateSearch Zod schemas
│   │   │   ├── admin-users/
│   │   │   ├── admin-roles/
│   │   │   └── files/
│   │   │       ├── useUpload.ts
│   │   │       └── useRefreshUrls.ts
│   │   ├── lib/
│   │   │   ├── api/
│   │   │   │   ├── problemDetails.ts
│   │   │   │   └── apiError.ts
│   │   │   ├── i18n/
│   │   │   │   ├── config.ts
│   │   │   │   └── locales/en.json
│   │   │   ├── theme/
│   │   │   │   ├── ThemeProvider.tsx
│   │   │   │   ├── applyTheme.ts
│   │   │   │   └── themes.ts                   # 3 theme names per FR-38
│   │   │   ├── queryClient.ts
│   │   │   ├── correlationId.ts                # ULID generator
│   │   │   └── utils.ts
│   │   └── test/setup.ts
│   └── vitest.config.ts
│
└── tools/                                      # Helper scripts (added as needed)
```

### Project Structure Additions — Dataset Manager (Epics H–K)

**Backend additions to `src/FormForge.Api/`:**

`Domain/Entities/`:
- `CustomDataset.cs` — EF entity for `custom_dataset` table (Decision 6.1)
- `DatasetAuditLogEntry.cs` — EF entity for `dataset_audit_log` table (Decision 6.1)

`Domain/ValueTypes/`:
- `DatasetName.cs` — identifier validation + denylist, mirrors `SafeIdentifier` pattern (Decision 6.1)

`Infrastructure/Persistence/Migrations/`:
- New migration: creates `custom_dataset`, `dataset_audit_log`, `datasets` schema, indexes, `can_manage_datasets` column on `roles`; seeds `formforge_preview` role grants via Dapper (Decision 6.1, 6.2, 6.7)

`Infrastructure/Datasets/`:
- `IPreviewConnectionFactory.cs` — dedicated preview pool interface (Decision 6.7)
- `PreviewConnectionFactory.cs` — `formforge_preview` NpgsqlDataSource, MaxPoolSize 5 (Decision 6.7)

`Features/Datasets/`:
- `DatasetEndpoints.cs` — `/api/datasets/*` route handlers (Decision 6.9)
- `DatasetService.cs` — CRUD orchestration + transactional view lifecycle (Decision 6.3)
- `DatasetSqlGenerator.cs` — `builder_state` → SQL (Decision 6.10)
- `DatasetViewManager.cs` — CREATE / CREATE OR REPLACE / ALTER RENAME / DROP VIEW DDL (Decision 6.3)
- `PreviewService.cs` — LIMIT 10 + statement timeout execution (Decision 6.7)
- `DatasetAllowlist.cs` — config-backed allowlist + `information_schema` catalog cache (Decision 6.6)
- `SqlSelectEnforcer.cs` — `PgQuery.NET` SELECT-only validation (Decision 6.5)
- `ExpressionSecurityValidator.cs` — per-expression CASE/calculated column security (Decision 6.8)
- `Validators/DatasetNameValidator.cs` + `Validators/CreateUpdateDatasetValidator.cs`
- `Dtos/DatasetDto.cs`, `DatasetSummaryDto.cs`, `CreateDatasetRequest.cs`, `UpdateDatasetRequest.cs`, `PreviewRequest.cs`, `PreviewResultDto.cs`, `CatalogDto.cs`, `BuilderStateDto.cs` (C# mirror of the TS `BuilderState` interface — Decision 6.11)
- `Events/DatasetChanged.cs`

**Test additions to `src/FormForge.Api.Tests/Features/Datasets/`:**
- `DatasetNameValidatorTests.cs` — unit: regex, denylist, reserved keywords
- `DatasetSqlGeneratorTests.cs` — unit: FROM/JOIN, aggregates → GROUP BY, nested filter groups, parameterized values, ORDER BY, CASE columns, calculated columns, empty state → validation errors
- `SqlSelectEnforcerTests.cs` — unit: SELECT permitted; DML/DDL rejected; CTEs (WITH … SELECT) permitted; parse error → rejection
- `DatasetViewLifecycleTests.cs` — integration (Testcontainers): create → VIEW exists; update same name → CREATE OR REPLACE; rename → ALTER RENAME; delete → VIEW gone; VIEW DDL failure → row rolled back; optimistic concurrency conflict → 409
- `DatasetPreviewTests.cs` — integration: LIMIT 10 enforced; statement timeout → 408; non-allowlisted table → 422; read-only role cannot mutate

**Frontend additions to `web/src/`:**

Routes (`routes/_app/admin/`):
- `datasets.tsx` — Dataset Manager list (accessible to roles with `can_manage_datasets`)
- `datasets.$id.tsx` — create/edit dataset (Custom Query textarea or Builder canvas; mode toggle)
- `datasets.audit.tsx` — dataset audit log page

Components (`components/query-builder/`):
- `TableNode.tsx` — React Flow custom node: table header, Left/Right toggle, column list with checkboxes, aggregate dropdowns, alias inputs, Add Case + Add Calculated Column buttons (Decision 6.12)
- `JoinEdge.tsx` — React Flow custom edge: styled curve + delete control + click-to-inspect
- `JoinInspector.tsx` — popover: joined columns display + join type selector

Feature folder (`features/datasets/`):
- `useDatasets.ts`, `useDatasetMutations.ts`, `useDatasetPreview.ts` — TanStack Query hooks (keys per Decision 6.13)
- `QueryBuilderCanvas.tsx` — React Flow canvas wrapper: `useNodesState` / `useEdgesState` synced to `builderState`
- `TablePalette.tsx` — left-side table list from `/api/datasets/catalog`; searchable; drag-to-canvas
- `FilterConditionsDialog.tsx` — modal with recursive AND/OR group builder (FR-67 J-5/J-6/J-7)
- `OrderByPanel.tsx` — ORDER BY clause list editor (FR-69 J-8)
- `types/builderState.ts` — **canonical cross-layer `BuilderState` interface** (Decision 6.11)

**New dependencies:**
- `package.json`: `@xyflow/react` v12 (React Flow — Decision 6.12)
- `FormForge.Api.csproj`: `PgQuery.NET` NuGet (Decision 6.5)

---

### Architectural Boundaries

**API boundary (external):**
- All HTTP enters via `FormForge.Api`. Public surface: `/api/*`, `/openapi/v1.json`, `/health/*`, SPA fallback at `/`.
- Auth boundary: `RequireAuth()` filter on every group except `/api/auth/*`, `/openapi/*`, `/health/live`, `/health/ready`.
- Admin boundary: `RequirePlatformAdmin()` on `/api/admin/*`.

**Static schema vs dynamic schema boundary:**
- `FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` — EF Core; static tables only.
- `Features/Provisioning/*` + `Features/DynamicCrud/*` — Dapper via `DbConnectionFactory`.
- Bridge: `SchemaRegistry` reads `component_schemas` rows via EF; produces entries consumed by Dapper code.

**Frontend feature boundary:**
- `routes/` orchestrate; `features/` own business logic; `components/` render. Enforced via ESLint `import/no-restricted-paths`.

**Background work boundary:**
- `ProvisioningBackgroundService` is the only place running DDL outside an HTTP request lifecycle. Its `Channel<ProvisioningJob>` is the queue boundary.

**Cache boundary:**
- All caches consume `ICacheStore`. v2 Redis swap touches only that one binding.

**Event boundary:**
- `IDomainEventBus` is the only intra-feature communication channel for state changes.

### Requirements to Structure Mapping

| Epic | Locations |
|---|---|
| **A — Identity & Permissions (FR-1..7, FR-50..53)** | `Features/{Auth,Users,Roles,Permissions}/`, `Domain/Entities/{User,Role,UserRole,RefreshToken,PasswordResetToken,MfaBackupCode}.cs`, `web/src/routes/{login,forgot-password,reset-password}.tsx` + `routes/_app/admin/{users,roles}.tsx`, `features/auth/*`, `features/admin-{users,roles}/*` |
| **B — Component Schema Designer (FR-8..15)** | `Features/Designers/`, `Domain/Entities/ComponentSchema.cs`, `web/src/routes/_app/designer.*.tsx`, `components/designer/*` (ported), `features/designer/*`, new Story B-11 → `useKeyboardDnD.ts` |
| **C — Menu Management (FR-16..22)** | `Features/Menus/`, `Domain/Entities/{Menu,MenuRoleAssignment}.cs`, `routes/_app/admin/menus.tsx`, `features/menu/*`, `Features/Files/*` (icon upload) |
| **D — Dynamic Table Provisioning (FR-23..28)** | `Features/{Provisioning,SchemaRegistry,Audit}/`, `Domain/Entities/SchemaAuditLogEntry.cs`, `routes/_app/admin/designers.$designerId.drift.tsx` |
| **E — Generic CRUD Service (FR-29..36)** | `Features/DynamicCrud/`, `Domain/Entities/MutationAuditLogEntry.cs`, `routes/_app/data.$designerId*.tsx`, `features/data-entry/*` |
| **F — UI / UX & Theming (FR-37..43)** | `web/src/lib/{theme,i18n}/*`, `routes/__root.tsx`, `components/ui/*`, `Common/Spa/IndexHtmlRewriter.cs` |
| **G — Platform / Cross-Cutting (FR-44..49)** | `src/FormForge.AppHost/`, `src/FormForge.ServiceDefaults/`, `Common/Logging/*`, `Infrastructure/HealthChecks/*`, `docker-compose.yml`, OpenAPI emitted by Minimal API metadata |
| **H — Dataset Foundation & Custom Query (FR-55..62)** | `Features/Datasets/`, `Domain/Entities/{CustomDataset,DatasetAuditLogEntry}.cs`, `Domain/ValueTypes/DatasetName.cs`, `Infrastructure/Datasets/*ConnectionFactory.cs`, `Infrastructure/Persistence/Migrations/` (`datasets` schema), `web/src/routes/_app/admin/datasets.tsx`, `features/datasets/*` |
| **I — Query Builder Canvas & Joins (FR-63..66)** | `Features/Datasets/DatasetAllowlist.cs` (catalog endpoint), `web/src/components/query-builder/{TableNode,JoinEdge,JoinInspector}.tsx`, `features/datasets/{QueryBuilderCanvas,TablePalette}.tsx` |
| **J — Builder Config (FR-67..69)** | `Features/Datasets/DatasetSqlGenerator.cs` (column selection, aggregates, GROUP BY, CASE, calculated, filter groups, ORDER BY), `Features/Datasets/ExpressionSecurityValidator.cs`, `features/datasets/{FilterConditionsDialog,OrderByPanel}.tsx` |
| **K — SQL Generation, Preview & View Sync (FR-70..73)** | `Features/Datasets/{DatasetSqlGenerator,PreviewService,SqlSelectEnforcer,DatasetViewManager}.cs`, `Infrastructure/Datasets/PreviewConnectionFactory.cs`, `features/datasets/useDatasetPreview.ts`, `features/datasets/types/builderState.ts` |

### Integration Points

**Internal communication:**
- HTTP between SPA and API (single origin in prod).
- In-process `IDomainEventBus` for cross-feature notifications.
- `Channel<ProvisioningJob>` for async DDL work.
- EF DbContext + Dapper share PG instance, not connection (Decision 1.6).

**External integrations:**
- PostgreSQL via Npgsql (both EF and Dapper).
- MinIO via official `Minio` .NET client.
- OTel collector (Aspire Dashboard dev; prod target deferred).

**Representative data flow (record create):**
1. SPA: `useDynamicFormMutation` → `httpClient.post('/api/data/incident_report', payload)`.
2. API: `CorrelationIdMiddleware` → `RateLimiter` → `RequireAuth` → `RequirePermission('create')` → `ValidationFilter` (Layer 1) → handler.
3. Handler: resolves schema registry; `DynamicPayloadValidator` Layer 2; `DynamicQueryBuilder` emits parameterized INSERT via Dapper; transaction wraps parent + Repeater children; mutation audit row inserted (EF) in same transaction.
4. Response: 201 with serialized record (Option C JSON casing). `X-Correlation-ID` header.
5. SPA: TanStack Query invalidates `['data', 'incident_report']` keys; record list re-fetches.

### Development Workflow Integration

- **Dev (Aspire):** `dotnet run --project src/FormForge.AppHost` starts everything; Aspire Dashboard at https://localhost:15888.
- **Dev (Compose):** `docker compose up`; SPA served from API container (no HMR).
- **Build:** `dotnet build` + `npm run build`. Container build: `docker build -t formforge-api:local .`.
- **Test:** `dotnet test` (xUnit + Testcontainers) + `npm test` (Vitest).

## Architecture Validation Results

### Coherence Validation ✅

**Decision Compatibility:** All 51 Core Architectural Decisions interlock cleanly.
- TanStack Router + TanStack Query integrated via `ensureQueryData` in route loaders.
- EF Core + Dapper on shared PG instance with separated transactions (Decision 1.6).
- In-memory caches + single API instance + single-origin SPA hosting all hang on the v1 single-process invariant; v2 horizontal scaling triggers a coordinated Redis swap via `ICacheStore`.
- CSP nonce flow (2.7) → theme hydration script (4.2) → single-origin SPA (5.5) wired through the `IndexHtmlRewriter` middleware.
- Aspire orchestration + `Channel<ProvisioningJob>` + `BackgroundService` all live inside one API process.

**Pattern Consistency:** All naming, format, and communication patterns support the architectural decisions. The only non-standard pattern is **Option C JSON casing** for dynamic endpoints (snake-case user fieldKeys + camelCase system columns), explicitly documented with rationale.

**Structure Alignment:** Vertical feature folders honor every cross-cutting boundary. The static/dynamic schema boundary lives at exactly two integration points (`FormForgeDbContext` for static; `DbConnectionFactory` for dynamic) with `SchemaRegistry` as the single documented bridge.

### Requirements Coverage Validation ✅

All 73 FRs map to specific files/folders in the project structure (see Requirements-to-Structure table in Project Structure section). Coverage map by Epic:

| Cluster | Coverage location | Status |
|---|---|---|
| FR-1..7, FR-50..53 (Identity & Permissions) | `Features/{Auth,Users,Roles,Permissions}/` + `features/auth/*` | ✅ |
| FR-8..15, FR-54 (Designer + Component Mode) | `Features/Designers/` + `components/designer/*` (ported) + Decisions 1.8, 4.10, 4.11 | ✅ |
| FR-16..22 (Menus) | `Features/Menus/` + `features/menu/*` + `Features/Files/*` (icons) | ✅ |
| FR-23..28 (Provisioning) | `Features/{Provisioning,SchemaRegistry,Audit}/` + `SafeIdentifier` | ✅ |
| FR-29..36 (Dynamic CRUD) | `Features/DynamicCrud/*` + `SoftDeleteCascade` + `RepeaterWriteCoordinator` | ✅ |
| FR-37..43 (UI/UX) | `lib/theme/*` + `lib/i18n/*` + `routes/__root.tsx` + axe-core CI | ✅ |
| FR-44..49 (Cross-cutting) | `FormForge.AppHost/` + `ServiceDefaults/` + `Common/Logging/*` + Compose | ✅ |
| FR-55..62 (Dataset Foundation) | `Features/Datasets/` + `Domain/Entities/{CustomDataset,DatasetAuditLogEntry}` + `DatasetName` value type | ✅ |
| FR-63..66 (Query Builder Canvas) | `Features/Datasets/DatasetAllowlist` + `components/query-builder/*` + `features/datasets/TablePalette` | ✅ |
| FR-67..69 (Builder Config) | `Features/Datasets/DatasetSqlGenerator` + `ExpressionSecurityValidator` + `features/datasets/FilterConditionsDialog + OrderByPanel` | ✅ |
| FR-70..73 (SQL Gen, Preview & Sync) | `DatasetSqlGenerator` + `PreviewService` + `SqlSelectEnforcer` + `DatasetViewManager` + `PreviewConnectionFactory` | ✅ |

**NFR coverage:** performance (caches, indexes, query timeout, p95 targets), security (Decisions 2.1–2.8), auditability (EF-managed append-only logs), reliability (transactional DDL with rollback + provisioning recovery), browser support (Vite ES2022 target + browserslist), i18n (architecture-ready, en-only).

**PRD Architect Handoff Items:** all 19 resolved.

| Handoff | Decision |
|---|---|
| AD-1 JWT silent re-auth | 2.1 |
| AD-2 Permission cache invalidation | 2.2 |
| AD-3 Identifier sanitization | 1.1 |
| AD-4 Component → PG type mapping | 1.2 |
| AD-5 Soft-delete cascade depth | 1.3 |
| AD-6 Version re-bind trigger & diff | 3.6 |
| AD-7 Schema registry cache | 1.4 |
| AD-8 OpenAPI for dynamic endpoints | 3.7 |
| AD-9 MinIO file access | 4.1 |
| AD-10 Audit log volume & retention | 1.5 |
| AD-11 Dapper + EF Core transaction boundary | 1.6 |
| AD-12 Email transport & template strategy | 2.9 |
| AD-13 TOTP secret & backup code storage | 2.12 |
| AD-14 Table allowlist management | 6.6 |
| AD-15 SELECT-only SQL enforcement | 6.5 |
| AD-16 Preview execution security & isolation | 6.7 |
| AD-17 VIEW rename atomicity | 6.3 |
| AD-18 builder_state schema contract | 6.11 |
| AD-19 CASE/calculated column expression security | 6.8 |

**Risk Register (R-1..R-17):** all mitigated in architecture except items belonging to the operational runbook (R-3 quarterly hygiene; R-9 graceful-degradation runbook), which are referenced and tracked. Dataset Manager risks R-14 through R-17 added by PRD §11:

| Risk | Mitigation |
|---|---|
| R-14 SQL injection via Dataset filter values | Parameterized placeholders (`$1`, `$2`, …) via Npgsql binding — never interpolated (Decision 6.10); read-only preview role as second line (Decision 6.7) |
| R-15 Query Builder generates unbounded SQL | Hard LIMIT 10 + configurable statement timeout on Preview (Decision 6.7); Dataset VIEWs themselves have no limit — operational note added |
| R-16 Allowlist bypass via crafted builder_state | Server validates all `node.tableName` values against allowlist cache before SQL generation (Decision 6.6); client palette filtering is UX only |
| R-17 VIEW and row divergence on partial failure | All row writes + VIEW DDL in single PostgreSQL transaction (Decision 6.3); PG DDL is fully transactional; integration tests cover rollback scenarios |

### Implementation Readiness Validation ✅

**Decision Completeness:** 64 decisions documented (51 original + 13 Dataset Manager); all critical decisions versioned (.NET 10 LTS, Aspire 13.1, React 19, Vite 7, PG 17, @xyflow/react v12, PgQuery.NET).

**Structure Completeness:** Complete project tree (backend + frontend) with file-level paths.

**Pattern Completeness:** Six categories of patterns (naming, structure, format, communication, process, logging) plus anti-example table.

### Gap Analysis Results

**Critical Gaps:** None.

**Important Gaps — folded inline as architecture addendums (now part of the document):**
1. Query timeout (PRD R-6) — added to Decision 1.6 (`CommandTimeout = 5` for dynamic CRUD; 60 for admin DDL; 5 for health).
2. Browser support (PRD §7) — added to Decision 5.6 (Vite `build.target: 'es2022'` + browserslist).
3. Deactivated-user observability (PRD R-5) — added to Decision 5.3 (`formforge.auth.deactivated_token_use` counter).
4. `cascade_event_id` NULL semantics — added to Decision 1.3 (individual restore clears unconditionally; cascade restore matches by id).

**Minor Gaps — tracked for implementation phase (not blockers):**
5. PRD addendum A2 (React Router v7) update needed to reflect TanStack Router override. PM action post-architecture.
6. `refresh_tokens` indexes (`idx_refresh_tokens_user_id` + unique on token hash) — addressed during S1 (Auth) story.
7. Frontend route-level admin guard (`beforeLoad` permission check on `/admin/*` in addition to `PermissionGate`) — addressed during Epic F.
8. Explicit `validateSearch` Zod schemas for list pages — defined at S6 in `features/data-entry/filterParams.ts`.
9. Provisioning recovery service for in-flight jobs at process restart — now part of architecture (folded into Decision 1.6); to be implemented in `Features/Provisioning/ProvisioningRecoveryService.cs` during S5.

### Validation Issues Addressed

Important gaps 1–4 above were folded into the architecture document as inline addendums when this validation section was saved. Items 5–9 are tracked as implementation-phase responsibilities; they do not block S0.

**PRD Update — 2026-05-31 (FR-50..53 added to Epic A):** Architecture updated to incorporate four new Auth requirements. Decisions added:
- **2.9** Email Service (MailKit + Mailpit dev container; async fire-and-forget).
- **2.10** Password Reset Token (32-byte random, SHA-256 hash stored, 1-hour TTL, single-use).
- **2.11** Authenticated Password Change (bcrypt verify current; revoke other sessions).
- **2.12** TOTP MFA Architecture (Otp.NET; IDataProtector secret encryption; two-step login via mfaSessionToken; 8 bcrypt-hashed backup codes).
- **AD-12 resolved** — email audit: structured logs only, no DB table (Option A).
- **OQ-7 resolved** — MFA enforcement: voluntary per-user opt-in; no platform-wide enforcement in v1 (Option A).

FR count updated 49 → 53. Decision count updated 45 → 49. Project structure updated (new entities, services, routes, Mailpit container).

**PRD Update — 2026-06-02 (FR-54 Component Mode added to Epic B):** Architecture updated for the CRUD/VIEW component-mode feature. Changes:
- **Decision 1.8** Component Mode — `component_schemas.mode` (`NOT NULL`, immutable, `CHECK (CRUD|VIEW)`, existing rows backfilled `CRUD`); provisioning gated on `CRUD`; VIEW exposes no `/api/data` endpoints (404 `TABLE_NOT_PROVISIONED`) and is governed solely by Menu `allowedRoles`.
- **Decision 4.11** Designer property pickers (Dropdown "Source component", Repeater "Row form — Component") are searchable comboboxes listing **CRUD-mode designers only**, with a server-side guard rejecting VIEW references.
- **Decision 4.10** extended: VIEW-mode Menu bindings render DynamicComponent read-only (same path as preview).
- Cross-references added to Decisions 1.4 (registry skip), 1.6 (provisioning mode-gate), 2.2 (no CrudFlags for VIEW), 3.5 (endpoint mode-gate).
- **Story collision resolved:** the architecture-introduced keyboard-a11y story (Decision 4.5) is renumbered **B-11**; PRD **B-10** is now "Declare and Enforce Component Mode."
- **OQ-9 noted** (PRD §12): component-mode mutability (promote VIEW→CRUD, provisioning on demand) is deferred — mode is fixed at creation in v1. No architectural provision required now; a future promote path would re-enter Decision 1.6 provisioning.

FR count updated 53 → 54. Decision count updated 49 → 51. No new entities (mode is a column on the existing `component_schemas`); no new services.

**PRD Update — 2026-06-03 (FR-55..73 Dataset Manager, Epics H–K):** Architecture extended with the Dataset Manager subsystem. Decisions added:
- **6.1** Dataset schema, migration &  VIEW namespace (dedicated schema, identifier denylist,  with  flag).
- **6.2**  permission model ( boolean on ; OQ-13 resolved).
- **6.3** Transactional view lifecycle (synchronous Dapper NpgsqlTransaction for CREATE/REPLACE/RENAME/DROP;  for renames — AD-17 resolved).
- **6.4** Optimistic concurrency ( integer compare-and-swap).
- **6.5** SELECT-only SQL enforcement via  AST parser — AD-15 resolved.
- **6.6** Table allowlist:  config + env-var override; startup validation; catalog endpoint; SQL Generator enforcement — AD-14 + OQ-11 resolved.
- **6.7** Preview execution: dedicated  read-only PG role; separate capped pool (MaxPoolSize 5);  — AD-16 resolved.
- **6.8** CASE/calculated expression security: per-expression keyword scan + wrap-parse + final SELECT-only check — AD-19 resolved.
- **6.9** Dataset API contract (8 endpoints, 7 new error codes).
- **6.10** Server-authoritative SQL generator algorithm (10-step: FROM, JOINs, SELECT list, GROUP BY, WHERE with …, ORDER BY, final validation).
- **6.11**  canonical cross-layer TypeScript/C# contract — AD-18 resolved.
- **6.12** React Flow integration ( v12; TableNode, JoinEdge, JoinInspector custom types).
- **6.13** TanStack Query keys for Dataset Manager.

FR count updated 54 → 73. Decision count updated 51 → 64. Sprints S9–S12 added. All PRD handoff items AD-14–AD-19 resolved. Risk register extended with R-14–R-17. Project structure additions documented separately.

### Architecture Completeness Checklist

**Requirements Analysis**
- [x] Project context thoroughly analyzed (73 FRs categorized)
- [x] Scale and complexity assessed (high complexity; internal-user scope; ≤100k rows/table)
- [x] Technical constraints identified (tech stack locked; dual orchestration; designer port)
- [x] Cross-cutting concerns mapped (12 concerns)

**Architectural Decisions**
- [x] Critical decisions documented with versions (64 decisions; all 19 PRD handoffs resolved; versions pinned)
- [x] Technology stack fully specified
- [x] Integration patterns defined (EF/Dapper boundary, event bus, BackgroundService, presigned URLs, single-origin SPA)
- [x] Performance considerations addressed (caches, indexes, p95 targets, query timeout, async DDL)

**Implementation Patterns**
- [x] Naming conventions established (DB, API, C#, TS, JSON Option C)
- [x] Structure patterns defined (vertical feature slicing; import rules)
- [x] Communication patterns specified (events past-tense PascalCase; query-key tuples)
- [x] Process patterns documented (loading, error, retry, validation, logging)

**Project Structure**
- [x] Complete directory structure defined (full backend + frontend tree)
- [x] Component boundaries established (API/admin/static-vs-dynamic/cache/event)
- [x] Integration points mapped (HTTP, event bus, Channel, EF↔Dapper share-PG-not-connection)
- [x] Requirements to structure mapping complete (all 7 epics → specific paths)

### Architecture Readiness Assessment

**Overall Status:** **READY FOR IMPLEMENTATION** — all 16 checklist items checked; no critical gaps; four important gaps inline-resolved within the architecture document; five minor gaps tracked as implementation-phase responsibilities. Dataset Manager addendum (Epics H–K, FR-55..73, Decisions 6.1–6.13) fully integrated.

**Confidence Level:** **High** — the PRD was unusually thorough (locked stack, 11 explicit handoff items, complete risk register, dependency graph, sprint plan). The architecture extends that rigor rather than papering over gaps.

**Key Strengths:**
- Every architecturally consequential pattern (runtime DDL, Repeater graph, permission caching, EF/Dapper transaction boundary, soft-delete cascade) has been explicitly decided.
- Single-process v1 invariant is consistent and clearly documented — the v2 horizontal-scaling path swaps `ICacheStore` and nothing else fundamentally changes.
- Vertical feature slicing keeps Epic-to-folder mapping 1:1, directly supporting parallel AI agent work.
- The Option C JSON casing decision preserves user authorship of fieldKeys while keeping system fields consistent.
- Defense-in-depth identifier sanitization (regex → `SafeIdentifier` value type → schema-registry whitelist) closes off the highest-severity risk (R-1 SQL injection).

**Areas for Future Enhancement (v2):**
- Per-Designer generated OpenAPI specs (enables SDK generation).
- `Idempotency-Key` support on data POSTs (retry-safe integrators).
- Keyset / cursor pagination for tables exceeding 500k rows.
- Distributed cache (Redis) when horizontal scaling lands.
- Audit log table partitioning if `mutation_audit_log` exceeds 1M rows/month.
- Reverse proxy (Caddy / nginx) or CDN in front of the API for hosting at scale.

### Implementation Handoff

**AI Agent Guidelines:**
- Follow all 64 Core Architectural Decisions exactly as documented (Decisions 1.x–5.x for the core platform; Decisions 6.1–6.13 for the Dataset Manager).
- Use Implementation Patterns consistently across all features (especially `SafeIdentifier`, `httpClient`, structured logging templates, TanStack Query key tuples).
- Respect the project structure and boundaries — especially static-vs-dynamic schema separation and the import-direction rules.
- The Requirements-to-Structure table is authoritative — start each story by locating its target folder.

**First Implementation Priority:**

Story G-1.1 (prepended to Sprint S0): run the initialization command sequence from the Starter Template Evaluation section to scaffold the solution. Then proceed with the PRD's Sprint S0 (G-1, G-5, G-2, G-3, G-4) per the dependency graph.

**Sprint sequencing reminder (from PRD §9):**

| Sprint | Stories | Exit Criteria |
|---|---|---|
| S0 — Infrastructure | G-1.1 (new), G-1, G-5, G-2, G-3, G-4 | `dotnet run` starts all services; `/health` healthy; Swagger accessible; structured logs visible |
| S1 — Auth | A-1..A-9, A-10..A-13 | Login/logout work; roles assignable; permission enforcement; admin UI functional; welcome email; forgot/reset password; password change; TOTP MFA enrolment + two-step login flow |
| S2 — Designer Port | B-1 | Ported designer renders; all 14 component types; DynamicComponent renders forms |
| S3 — Designer Features | B-2..B-10, B-11 (new keyboard a11y), D-1 | Full designer workflow; library page; lifecycle management; component mode (CRUD/VIEW) set at creation and enforced; keyboard DnD passes axe-core |
| S4 — Menu | C-1, C-2, C-4..C-8 | Menus created, reordered, role-assigned; navbar permission-filtered |
| S5 — Table Provisioning | C-3, D-2..D-6 + ProvisioningRecoveryService | Schema bindings provision; additive ALTER; Repeater FK; drift view; audit log |
| S6 — CRUD API | E-1..E-8 | All CRUD endpoints permission-gated; soft-delete cascade; nested Repeater writes; mutation audit |
| S7 — Data Entry UI | F-4..F-6 | Content Editor flows; record list paginated/filterable/sortable; all UI states |
| S8 — Polish & Cross-Cutting | F-1..F-3, F-7..F-8, G-6 | Mobile layout; theming; WCAG audit passes; admin pages complete; i18n externalized |
| S9 — Dataset Foundation | H-1, H-2, H-3, H-4, H-5, H-6, H-7, H-8, H-9, H-10 | `custom_dataset` migration + `datasets` schema; `dataset-management` permission enforced; full CRUD API with transactional view lifecycle; name validation; Custom Query Mode with SELECT enforcement; Dataset Management UI functional; audit log populated |
| S10 — Query Builder Canvas | I-1, I-2, I-3, I-4, I-5 | Table Palette shows allowlisted tables; multi-table drag works; column-to-column join edges; Join Inspector configures type; left/right designation controls FROM anchor |
| S11 — Builder Config | J-1, J-2, J-3, J-4, J-5, J-6, J-7, J-8 | Column checkboxes control SELECT; aggregates + GROUP BY correct; CASE + calculated columns generate valid SQL; Filter dialog with nested groups; parameterized values confirmed; ORDER BY in declared order |
| S12 — SQL Gen, Preview & Sync | K-1, K-2, K-3, K-4 | Server generator produces correct SQL from builder_state; canvas restores exactly on reopen; Preview returns ≤10 rows with timeout; builder-mode save reuses transactional view lifecycle |
