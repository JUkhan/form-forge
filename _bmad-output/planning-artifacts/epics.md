---
stepsCompleted: [1, 2, 3, 4]
lastAmended: '2026-06-03'
lastValidated: '2026-06-03'
amendmentNotes: 'Augment run (FR-55..73, Dataset Manager): added FR-55..73 across new Epics 8–11 (Dataset Foundation & Custom Query, Query Builder Canvas & Joins, Builder Config, SQL Generation/Preview/Sync) — 19 new FRs, 13 new architecture decisions (6.1–6.13), AR-57–69, 27 new stories (8.1–8.10, 9.1–9.5, 10.1–10.8, 11.1–11.4); FR coverage map extended; Epic List and dependency diagram updated. Prior: Augment run (FR-54, 2026-06-02): added FR-54 Component Mode (VIEW/CRUD) — new Story 3.11 + mode-aware ACs threaded into Stories 3.2/3.4/3.8, 5.2, 6.9; NOTE: theme UX-DRs (UX-DR1..11) were found already IMPLEMENTED in code — Stories 7.7–7.10 removed; UX-DR inventory retained as documentation. Prior: extracted 11 UX-DRs; added FR-50..53 stories (welcome email, forgot password, password change, TOTP MFA) to Epic 2'
inputDocuments:
  - _bmad-output/planning-artifacts/prds/prd-tinnitus-2026-05-22/prd.md
  - _bmad-output/planning-artifacts/prds/prd-tinnitus-2026-05-22/addendum.md
  - _bmad-output/planning-artifacts/architecture.md
  - _bmad-output/planning-artifacts/ux-design-specification.md
project_name: 'FormForge (tinnitus)'
user_name: 'jukhan'
date: '2026-05-22'
---

# FormForge (tinnitus) - Epic Breakdown

## Overview

This document provides the complete epic and story breakdown for FormForge (tinnitus), decomposing the requirements from the PRD, the Architecture document, and the PRD Addendum into implementable stories. No standalone UX Design specification exists; UX requirements are extracted from PRD Epic F and Architecture Section 4 (Frontend Architecture).

## Requirements Inventory

### Functional Requirements

FR-1: User Account Management (Admin-Managed) — Platform Admins create, edit, and deactivate user accounts; no self-registration. Bcrypt password hashing; deactivation invalidates refresh tokens immediately.
FR-2: Role Definition with Per-Resource CRUD Flags — A Role carries four boolean flags (canCreate, canRead, canUpdate, canDelete) per Resource (designerId). A Role can cover multiple Resources.
FR-3: User-Role Assignments — Platform Admins assign multiple roles to a user; changes take effect within 30 seconds.
FR-4: Effective Permission Computation — User's effective permission on a Resource = union of all their Roles' flags. Implemented as a server-side helper called by every endpoint and every client-side permission check.
FR-5: Server-Side Endpoint Authorization — Every CRUD and admin endpoint enforces role-based permissions; insufficient permission returns HTTP 403; unauthenticated returns HTTP 401.
FR-6: Client-Side UI Permission Adaptation — UI controls (buttons, menus) hidden when user lacks permission; menu items absent (not disabled) when canRead is false.
FR-7: Admin UI — Users, Roles, Assignments — Dedicated admin pages to manage users, roles, and assignments; permission matrix UI; cannot deactivate self.
FR-8: Designer Port and Refactor — Audit and port the component designer (ComponentDesignerPage, ComponentLibraryPage, DynamicComponent, DesignerCanvas, ElementRenderer, DesignerToolbar) from the ESG Platform reference codebase; refactor for shadcn/ui, React 19, TanStack Query v5, new project structure.
FR-9: Designer Creation — Platform Admin creates a new Designer with displayName and designerId (validated as a SQL-safe identifier).
FR-10: Canvas Drag-and-Drop — Drag from palette, reorder, nest within structural components, delete; emits updated RootElement JSON.
FR-11: Component Property Configuration — Per-component properties panel; fieldKey required and validated for all input-bearing leaf components.
FR-12: Live Preview — Toggle a preview mode using the same DynamicComponent code path used in data entry.
FR-13: Designer Save and Versioning — Save creates a new immutable version; Draft → Published → Archived lifecycle; at most one Published version per Designer.
FR-14: Component Library / Designer Listing — Browse all Designers, view versions, preview, manage lifecycle; per-row actions (Open, New Version, Duplicate, Archive).
FR-15: DynamicComponent in Data Entry — Renders any bound Designer version as a live, submittable form for end-users; supports all 14 component types and visibility conditions.
FR-16: Menu Item CRUD — Create, edit, delete top-level menu items with name, order, icon, isActive; deletion blocked if children exist.
FR-17: Schema Binding — Bind a Menu Item to a specific Published Designer version; saving triggers asynchronous table provisioning with status (Pending/Success/Error).
FR-18: Menu Icon — Lucide icon name or uploaded image (PNG/JPG/SVG, max 2 MB) stored in MinIO.
FR-19: Role-Based Menu Access — Menu Item has allowedRoles; user sees an item only if a role intersects; server also enforces.
FR-20: Menu Ordering — Drag-and-drop reorder; sub-menu items reorder within parent only; order changes propagate within ≤5 s.
FR-21: isActive Toggle — Toggle to hide items without deletion; inactive items excluded from navbar for all users including admins (admin can access from Admin > Menus).
FR-22: Dynamic Navbar — Authenticated user sees a left-side navbar showing only Menu Items their roles authorize; mobile collapses to hamburger with auto-close on tap.
FR-23: designerId Identifier Validation — Validated regex (lowercase letters, digits, underscores; starts with letter/underscore; 1–63 chars; reserved keywords rejected) before any DDL.
FR-24: Initial Table Creation — CREATE TABLE on first Schema Binding; standardized system columns; component → PG type mapping; nullable columns; transactional with rollback; audit log entry on success.
FR-25: Additive-Only Schema Migration — New Designer versions trigger ALTER TABLE ADD COLUMN for new fields; existing columns never dropped/renamed automatically; runs in transaction; audit log entry recorded.
FR-26: Schema Drift Visibility — Admin view of orphaned columns with non-null row counts; per-column "Drop Column" action with explicit confirmation; transactional; audited.
FR-27: Repeater Child Table Provisioning — Recursive provisioning for Repeater components; parent FK column with ON DELETE CASCADE; FK index; cycle detection rejects circular references; all in one transaction.
FR-28: Schema Change Audit Log — Append-only log of all DDL events (actor, timestamp, fromVersion, toVersion, columnsAdded/Dropped, columnsDiff); paginated admin view; no deletion API.
FR-29: Paginated List — GET /api/data/{designerId} with page, pageSize, sort (up to 3 columns, whitelisted), filter (whitelisted, parameterized); p95 <200 ms at 100k rows; includes soft-deleted by default.
FR-30: Single Record Retrieval — GET single record by id; ?include=children returns Repeater child rows; soft-deleted record returned with is_deleted flag.
FR-31: Record Creation — POST creates record; validates payload against schema fieldKeys + types; ignores unknown fields; system columns set server-side; returns 201; CRUD mutation audit entry.
FR-32: Partial Record Update — PUT updates only fields present in payload; updated_at + updated_by set server-side; updating soft-deleted record returns HTTP 422; audit entry with previous + new values.
FR-33: Soft Delete — DELETE sets is_deleted=true; cascades to Repeater child rows in same transaction; returns updated record; audit entry recorded.
FR-34: View and Restore Deleted Records — PUT /restore restores parent and same-cascade-event child rows in one transaction; Platform Admin only; toggle in admin record list; audit entry.
FR-35: Nested Repeater Write — POST/PUT payloads may include children: { [childDesignerId]: [...] }; parent + children inserted/upserted in single transaction; omitted children = soft-deleted.
FR-36: CRUD Mutation Audit Log — Append-only log of all create/update/soft-delete/restore (actor, timestamp, recordId, operation, previous + new values); paginated admin view; EF Core-managed; no deletion API.
FR-37: Mobile-First Responsive Layout — Single-column below 768 px; sidebar+content at ≥768 px; collapsible nav; touch targets ≥44×44 px; no horizontal scroll above 320 px.
FR-38: Theme Selection and Persistence — 3 themes (default-light, slate-dark, solarized) selectable from user profile; applies immediately without page reload via Tailwind CSS variables.
FR-39: Data Entry Form UI — Navigating a bound Menu Item shows paginated record list + "New Record" form (DynamicComponent); detail/edit view; soft-delete confirmation dialog.
FR-40: Record List UI — Columns derived from bound schema fieldKeys; multi-column sort (header click + shift-click secondary); per-column filter bar; paginated (10/25/50); soft-deleted indicator.
FR-41: Loading, Empty, and Error States — Skeletons/spinners while loading; empty state messages + CTA if canCreate; toast for transient errors; inline banner with retry for blocking errors; field-level form errors.
FR-42: WCAG 2.1 AA Accessibility — Keyboard reachable in logical tab order; labels/aria-label on all inputs; aria-describedby on errors; contrast ratio ≥4.5:1; DnD has keyboard equivalents; axe-core zero critical violations on rendered DynamicComponent forms.
FR-43: Admin Settings Pages — Dedicated admin area (Users, Roles, Menus, Designers, Audit Logs) accessed via "Settings" link visible to platform-admin only; non-admins redirected client + server side.
FR-44: .NET Aspire AppHost — Single dotnet run starts API + PostgreSQL + MinIO + React frontend; connection strings via env vars; Aspire Dashboard at https://localhost:15888.
FR-45: OpenAPI and Swagger UI — Auto-generated OpenAPI 3.1 at /openapi/v1.json; Swagger UI at /swagger in dev only; dynamic endpoints documented with additionalProperties: true; all endpoints document Bearer auth.
FR-46: Structured Logging with Correlation IDs — Every request assigned correlation ID; JSON-structured logs include timestamp/level/correlationId/userId/endpoint/message/exception; DDL and CRUD logged with SQL fingerprint (never parameter values).
FR-47: Health Checks — /health (detailed), /health/live (liveness, always 200), /health/ready (readiness; 503 if any dependency unreachable).
FR-48: Docker Compose — docker-compose.yml defines api/postgres/minio/frontend; EF Core migrations auto-run on API startup; MinIO bucket created on first startup; service URLs via Docker network.
FR-49: i18n Architecture — react-i18next; all user-facing strings use t('key'); single en.json file; API error responses include both a string key and English message; zero functional multi-language in v1.
FR-50: Admin Welcome Email on User Creation — On successful user creation, dispatch a welcome email asynchronously (fire-and-forget) containing the platform name, the new user's email address, their temporary password, and a login link. SMTP failure does not block creation; failure is logged and surfaced as a non-blocking warning in the API response.
FR-51: Forgot Password Self-Service Flow — Unauthenticated users submit their registered email to receive a time-limited (1-hour TTL), single-use reset link via email. Only the SHA-256 hash of a 32-byte random token is stored server-side. Successful reset invalidates all refresh tokens for the user. No user enumeration — the endpoint always returns HTTP 200.
FR-52: Authenticated Password Change — Authenticated users can change their own password by supplying their current password. Current password verified via bcrypt. New password must be ≥ 8 characters and differ from the current password. On success, all refresh tokens for the user except the current session's are revoked.
FR-53: TOTP-Based Multi-Factor Authentication — Voluntary per-user TOTP MFA conforming to RFC 6238 (SHA-1, 30-second period, 6-digit codes, ±1 step clock-skew tolerance). TOTP secrets stored encrypted at rest via `IDataProtector`. Login becomes a two-step exchange when MFA is enabled: password → `mfaSessionToken` (5-min TTL, in-process, single-use) → TOTP/backup-code challenge → JWT pair. 8 single-use backup codes generated at enrolment (bcrypt-hashed, shown once). Platform Admins can reset MFA for any user.

FR-54: Component Mode (VIEW / CRUD) — Every component (Designer) declares a `mode` (`CRUD` or `VIEW`) at creation, stored on `component_schemas.mode` (`NOT NULL`, immutable across versions; pre-existing rows backfilled to `CRUD`). CRUD-mode components provision a backing table and support full data entry. VIEW-mode components never provision a table, expose no `/api/data/{designerId}` endpoints (404 `TABLE_NOT_PROVISIONED`), render read-only via DynamicComponent, and are governed solely by the Menu Item's `allowedRoles` (per-Resource CRUD flags not evaluated). Designer property pickers that reference another component for its data (Dropdown "Source component", Repeater "Row form — Component") list CRUD-mode components only, with a server-side guard rejecting VIEW references on save.

### NonFunctional Requirements

NFR-1 (Performance): GET /api/data/{designerId} p95 <200 ms at 100k rows with indexes on id, created_at, is_deleted, and common filter columns.
NFR-2 (Performance): Designer version save p95 <500 ms.
NFR-3 (Performance): Navbar menu fetch p95 <100 ms (cached, TTL 5 s, invalidated on write).
NFR-4 (Security): All endpoints require a valid JWT; unauthenticated returns HTTP 401.
NFR-5 (Security): Access token stored in-memory only (not localStorage, not cookies). Refresh token in HttpOnly + SameSite=Strict cookie (7-day TTL, single-use rotation, server-stored).
NFR-6 (Security): SQL injection prevention — Dapper queries parameterize all values; dynamic identifiers (table, column names) validated against server-side schema-registry whitelist; raw user input never interpolated.
NFR-7 (Security): File uploads validated for MIME type and size before MinIO storage.
NFR-8 (Security): Secrets (JWT key, DB conn string, MinIO creds) injected as env vars; never committed.
NFR-9 (Auditability): Schema change audit log records every CREATE/ALTER/DROP DDL with actor, timestamp, diff. Append-only.
NFR-10 (Auditability): CRUD mutation audit log records every create/update/soft-delete/restore with actor, timestamp, field-level diff. Append-only. No deletion API.
NFR-11 (Reliability): All DDL operations run in explicit PostgreSQL transactions with full rollback on failure.
NFR-12 (Reliability): API process restarts handled by Aspire/Docker orchestrator restart policy; in-flight provisioning jobs recovered on startup via ProvisioningRecoveryService.
NFR-13 (Browser Support): Latest 2 versions of Chrome, Edge, Firefox, Safari at time of release. Vite build.target=es2022; browserslist enforced.
NFR-14 (i18n): Architecture-ready (externalized strings, t('key') everywhere); English-only at launch.
NFR-15 (Scale Target): Single-tenant, internal-users-only, ≤100k rows/table target (offset pagination acceptable; keyset deferred to v2).

### Additional Requirements

Architecture-Derived Requirements:

- AR-1: Starter Template — Solution scaffolded with `aspire new aspire-starter --name FormForge --output .` for the backend (AppHost + ServiceDefaults + ApiService) and `npm create vite@latest web -- --template react-ts` + `npx shadcn@latest init` for the frontend. **This impacts Epic 1 / Story 1 — see new Story G-1.1 (project scaffolding) prepended to Sprint S0.**
- AR-2: Monorepo layout — `src/FormForge.AppHost`, `src/FormForge.ServiceDefaults`, `src/FormForge.Api`, `src/FormForge.Api.Tests`, `web/` (frontend), `docker-compose.yml`, `docs/`, `_bmad-output/`.
- AR-3: Routing override — TanStack Router (file-based, with `@tanstack/router-plugin` + `autoCodeSplitting: true`) replaces the PRD Addendum's React Router v7 assumption (A2). PRD addendum must be updated.
- AR-4: Identifier sanitization pipeline — Regex `^[a-z_][a-z0-9_]{0,62}$`; hardcoded reserved keyword list (PG 17 reserved subset); `SafeIdentifier` value type re-validates on construction; whitelist check against schema registry before SQL composition.
- AR-5: Complete component → PG type mapping — 14 types mapped; unknowns fall back to JSONB. All dynamic columns nullable.
- AR-6: Soft-delete cascade — `cascade_event_id UUID NULL` system column on every dynamic table; recursive Repeater graph walk in one transaction; restore re-activates only rows whose cascade_event_id matches the parent's last event.
- AR-7: Schema Registry Cache — In-process `IMemoryCache`, keyed by (designerId, publishedVersion); LRU 1000 entries, 1-hour TTL fallback; invalidated on SchemaPublished event.
- AR-8: Audit Log Retention & Indexing — Unlimited retention v1; indexes (designer_id, created_at DESC), (record_id, created_at DESC), correlation_id; EF-Core-managed.
- AR-9: EF Core + Dapper Transaction Boundary — Separated transactions; menu binding (EF) commits with provisioningStatus=Pending → `Channel<ProvisioningJob>` → `BackgroundService` consumer runs Dapper DDL → updates status. ProvisioningRecoveryService re-enqueues Pending jobs on startup.
- AR-10: Migration Tooling — EF Core Migrations for all static schemas; Dapper owns all dynamic DDL (never represented in EF migrations); migrations run automatically on API startup in all environments (idempotent Database.Migrate()).
- AR-11: Permission Cache — `IMemoryCache` keyed by userId; TTL 30 s; in-process event bus busts on UserRoleAssignmentChanged, RolePermissionsChanged, UserDeactivated.
- AR-12: JWT Signing — HS256, secret in env var; quarterly rotation per runbook; 15-min grace window equal to access-token TTL.
- AR-13: Password Hashing — `BCrypt.Net-Next` work factor 12.
- AR-14: CORS — Dev: allowlist Vite dev origin. Prod: strict allowlist of deployed frontend origin. AllowCredentials=true; no wildcards; preflight cached 1 hour.
- AR-15: Rate Limiting — Per-IP for /api/auth/login (10/min), /api/auth/refresh (30/min); per-user sliding for POST /api/data/* (60/min), other /api/data/* (300/min), /api/admin/* (120/min). 429 with Retry-After.
- AR-16: Security Headers + CSP — HSTS, X-Frame-Options=DENY, X-Content-Type-Options=nosniff, Referrer-Policy strict; CSP with per-request nonce for the theme script.
- AR-17: Secret Storage — Dev: `dotnet user-secrets` + Aspire injection. Prod: env vars from deployment platform's secret manager (deferred).
- AR-18: Error Envelope — RFC 7807 ProblemDetails with `code` enum, i18n `messageKey`, `correlationId`, optional `resource`/`action`. Validation errors via ValidationProblemDetails.
- AR-19: API Versioning — No URL prefix in v1; `/openapi/v1.json`. Additive changes ship in place; breaking changes ship `/api/v2/...`.
- AR-20: Two-Layer Validation — Layer 1 FluentValidation per-DTO for static endpoints. Layer 2 IDynamicPayloadValidator for /api/data/* using schema registry + filter/sort whitelist.
- AR-21: Pagination Response — Standardized `PagedResult<T> { Data, Total, Page, PageSize }`; pageSize ≤100, default 25. Query convention `?page=1&pageSize=25&sort=col:dir&filter[key]=val`.
- AR-22: Endpoint Organization — Minimal API route groups with group-level filters: correlation → auth → rate limit → permission → validation → handler. No controllers.
- AR-23: Version Re-Bind Diff — `GET /api/admin/menus/{menuId}/binding-diff?targetVersion={N}` returns column diff preview. `PUT /api/admin/menus/{menuId}/binding` returns 202 + provisioningStatus=Pending. Async via Channel + BackgroundService.
- AR-24: Correlation ID Propagation — Read X-Correlation-ID header or generate ULID; injected into ILogger scope; flowed onto Dapper SQL comments; in every log, every audit row (new correlation_id column), every error response, response header.
- AR-25: Health-Check Endpoint Authentication — /health/live anonymous; /health/ready anonymous; /health (detailed) requires platform-admin role.
- AR-26: MinIO Presigned URLs — Schema registry marks IsImage=true columns; serializer enriches with `{ objectKey, url, expiresAt }`; TTL 5 min; single bucket `formforge` with path prefixes; `POST /api/files/upload` and `POST /api/files/refresh-urls` endpoints.
- AR-27: Theme No-Flash Hydration — Inline `<script nonce>` in `<head>` reads localStorage.getItem('ff-theme') synchronously; sets data-theme attribute before React; CSP nonce flowed via IndexHtmlRewriter.
- AR-28: Error Boundary & Loading Strategy — TanStack Router pendingComponent/errorComponent per route + defaultPendingComponent/defaultErrorComponent on __root; React Error Boundary at app root; 404s via notFoundComponent.
- AR-29: Toast Notifications — `sonner` (shadcn-supported) mounted at <Toaster /> in __root; used for form-save success, transient API errors, copy-to-clipboard, provisioning status.
- AR-30: Designer DnD Keyboard Accessibility — `useKeyboardDnD` hook providing parallel keyboard interaction alongside native HTML5 DnD (Tab focus, Space/Enter pickup, arrow move, aria-live announcements, Escape cancel). Required for FR-42 / R-7 mitigation. **New Story B-10 added to Epic B.**
- AR-31: Frontend Folder Structure — Feature folders: routes/ → features/ → components/ → lib/; enforced via ESLint import/no-restricted-paths.
- AR-32: HTTP Client Wrapper — Thin fetch-based `httpClient.ts`; attaches Authorization + X-Correlation-ID; on 401 attempts one refresh and retry; throws typed `ApiError`. No Axios.
- AR-33: i18n Initialization — `i18next` + `react-i18next` initialized synchronously at app boot before React renders; dot-notation keys; API error messageKey resolved via t().
- AR-34: Form Composition — react-hook-form + zod resolver; FormField adapter wrapping shadcn primitives wiring name/control/error/aria-describedby; server validation errors via setError.
- AR-35: DynamicComponent Bridge — Preserved as a black box (visibility engine, Repeater scope, submitRef, validity/ready callbacks intact). External Save button calls submitRef.current() → onSave(payload) → TanStack Query mutation.
- AR-36: Cache Backend — IMemoryCache for v1 (single process); all caches behind ICacheStore for v2 Redis swap.
- AR-37: Background Work — Channel<ProvisioningJob> + BackgroundService, single consumer (prevents concurrent DDL conflicts); outcomes persisted to menus.provisioningStatus; no Hangfire/Quartz in v1.
- AR-38: Observability Stack — OpenTelemetry via Aspire ServiceDefaults (traces, metrics, logs); MSExtLogging + JSON console formatter + OTel logging exporter; custom metrics (permission cache hits/misses, schema registry hits/misses, provisioning jobs completed/failed, dynamic CRUD request duration, refresh token issued/revoked/replayed, deactivated_token_use counter).
- AR-39: Frontend Production Hosting — API project serves SPA in v1 (UseStaticFiles + fallback to /index.html); Vite dist/ copied into wwwroot/ during container build; single origin.
- AR-40: Container Image — Multi-stage Dockerfile producing single formforge-api:tag; sdk 10.0 → node:22-alpine (vite build) → aspnet:10.0-alpine (non-root UID 1000).
- AR-41: Database Backup & Restore — Architectural minimum: PG WAL archiving + daily pg_dump to MinIO + MinIO bucket replication; retention 30 days daily, 7 days WAL; RPO ≤24 h (daily) or ≤5 min (WAL); RTO ≤2 h; quarterly restore test.
- AR-42: Environment Configuration — Layering appsettings.json → appsettings.{Environment}.json (no secrets) → env vars (secrets, mandatory) → user secrets (dev only); Aspire WithReference() wires ConnectionStrings__formforge etc.; frontend reads VITE_API_BASE_URL.
- AR-43: CI/CD Pipeline — GitHub Actions; on PR: build + dotnet test + vitest + ESLint + TS typecheck + container build + axe-core smoke + dotnet list package --vulnerable + npm audit. On merge: tagged image push + staging deploy. Gates: tests pass, no high-severity vulns, axe-core zero critical.
- AR-44: Docker Compose Parity — services: postgres, minio, minio-init (bucket create), api; no frontend dev service; EF Core migrations auto-run.
- AR-45: Naming Conventions — DB: snake_case (tables plural for static, singular for dynamic = designerId); FKs `parent_{parentDesignerId}_id` for Repeater, `{entity}_id` for static. Indexes idx_{table}_{cols}. API: plural camelCase params, kebab-Title-Case headers. C#: PascalCase types, _camelCase fields, Async suffix. TS: PascalCase components, useCamelCase hooks.
- AR-46: JSON Field Naming (Option C hybrid) — Static endpoints camelCase end-to-end. Dynamic /api/data/{designerId} endpoints: system columns translated to camelCase (created_at → createdAt), user-authored fieldKeys preserved verbatim (report_title stays as report_title). Custom JsonConverter<DynamicRecord>.
- AR-47: Domain Events — In-process IDomainEventBus; events named PascalCase past-tense (SchemaPublished, UserDeactivated, RoleAssignmentChanged, MenuBindingCreated); record types, immutable, minimal payload (IDs + operation).
- AR-48: TanStack Query Keys — Tuple convention `['scope', 'entity', ...params]`; standard examples documented; invalidation via prefix match.
- AR-49: Mutation Strategy — Optimistic only for theme change, soft-delete row removal, drag-reorder; pessimistic for form saves, DDL-triggering operations, role assignments.
- AR-50: Logging Conventions — Required structured fields per log entry; DDL/CRUD Information level adds designerId, operation, sqlFingerprint (parameterized only); blocked: string interpolation in ILogger, PII in audit messages, request body logging.
- AR-51: Architectural Boundaries — API boundary at FormForge.Api; auth boundary RequireAuth filter; admin boundary RequirePlatformAdmin on /api/admin/*; static vs dynamic schema boundary (EF DbContext vs DbConnectionFactory); SchemaRegistry is the single documented bridge.
- AR-52: ProvisioningRecoveryService — Runs on startup; scans menus WHERE provisioningStatus='Pending' and re-enqueues each into Channel<ProvisioningJob>. Story added to Sprint S5.
- AR-53: Email Service — MailKit standalone NuGet; Mailpit container (`axllent/mailpit`) added to Aspire AppHost (SMTP :1025, web UI :8025) and injected into API via `WithEnvironment` in dev; async fire-and-forget dispatch pattern (`Task.Run` + catch); structured log per dispatch attempt with `recipient`, `templateType`, `correlationId`, `success/failure`; no DB email audit table (AD-12 resolved — Option A); config env vars: `SMTP_HOST`, `SMTP_PORT`, `SMTP_USER`, `SMTP_PASS`, `SMTP_FROM`. Decision 2.9.
- AR-54: Password Reset Token — 32-byte random via `RandomNumberGenerator.GetBytes(32)` encoded as 64-char hex; only the SHA-256 hash persisted in `password_reset_tokens (id, user_id, token_hash, expires_at, used_at)` (EF Core managed); 1-hour TTL absolute; single-use (`used_at` set on redemption); on success invalidates all refresh tokens for the user; anti-enumeration: forgot-password endpoint always HTTP 200 regardless of email presence. Decision 2.10.
- AR-55: Authenticated Password Change — `PUT /api/users/me/password { currentPassword, newPassword }`; bcrypt comparison of `currentPassword`; mismatch → 401; `newPassword` ≥ 8 chars and differs from current (bcrypt comparison); on success: `passwordHash` updated, all refresh tokens for the user except the current session's revoked. Decision 2.11.
- AR-56: TOTP MFA — `Otp.NET` NuGet (RFC 6238); `IDataProtector` (purpose: `"mfa-totp-secret"`) encrypts raw base32 secret stored as blob in `users.mfa_secret_protected` (no external KMS in v1); `mfaSessionToken` is a random ULID stored in `IMemoryCache` (key → `{ userId, issuedAt }`; 5-min absolute TTL; evicted on first successful verify); backup codes: 8 × 8-char alphanumeric; bcrypt hashes in `mfa_backup_codes (id, user_id, code_hash, used_at)`; enrolment guard: secret not committed until TOTP verification succeeds; re-enrolment replaces secret and backup codes atomically; admin reset clears `mfa_enabled`, `mfa_secret_protected`, all backup codes; OQ-7 resolved (voluntary per-user). Decision 2.12.

Dataset Manager Architecture-Derived Requirements (Decisions 6.1–6.13):

- AR-57: Dataset Schema, Migration & View Namespace (Decision 6.1) — `custom_dataset` EF entity + `dataset_audit_log` EF entity; `CREATE SCHEMA IF NOT EXISTS datasets` runs in the same EF migration; all Dataset VIEWs created as `datasets.{dataset_name}` (eliminates naming collision with `public` schema); identifier permanent denylist of internal table names (users, roles, refresh_tokens, etc.) enforced in `DatasetName.cs`; indexes: `idx_custom_dataset_dataset_name` (UNIQUE), `idx_dataset_audit_log_dataset_name_timestamp`, `idx_dataset_audit_log_operation`; `dataset_audit_log.succeeded BOOLEAN NOT NULL DEFAULT true` (set false when DDL rolled back).
- AR-58: dataset-management Permission Model (Decision 6.2, OQ-13 resolved) — `can_manage_datasets BOOLEAN NOT NULL DEFAULT false` column added to `roles` table via EF migration (platform-wide capability flag, not per-resource); `platform-admin` seeded with `true`; `EffectivePermissions` record gains `CanManageDatasets: bool`; `RequireDatasetManagement()` route-group extension; Admin > Roles permission matrix gains "Dataset Management" toggle row.
- AR-59: Transactional View Lifecycle (Decision 6.3, AD-17 resolved) — all Dataset row writes + VIEW DDL execute **synchronously** in a Dapper `NpgsqlTransaction` (unlike table provisioning which is async); Create: `INSERT` + `CREATE VIEW datasets.{name} AS {query}`; Edit same name: `UPDATE` + `CREATE OR REPLACE VIEW`; Rename: `UPDATE` + `ALTER VIEW datasets.{old} RENAME TO {new}` (atomic DDL; AD-17 resolved as ALTER RENAME over DROP+CREATE); Delete: `DELETE` + `DROP VIEW IF EXISTS`; any failure → full rollback, existing VIEW left intact; failed DDL attempts recorded in `dataset_audit_log` with `succeeded = false`.
- AR-60: Optimistic Concurrency (Decision 6.4) — `version INTEGER NOT NULL DEFAULT 1` on `custom_dataset`; every PUT must include `version`; `UPDATE WHERE id=@id AND version=@expectedVersion`; 0 rows affected → HTTP 409 `{ code: "DATASET_CONCURRENCY_CONFLICT", currentVersion: N }`; `version` incremented within same transaction as VIEW DDL.
- AR-61: SELECT-Only SQL Enforcement (Decision 6.5, AD-15 resolved) — `PgQuery.NET` NuGet (wraps libpg_query); `SqlSelectEnforcer.cs`: (1) parse fails → 422 INVALID_QUERY; (2) root node must be SelectStmt or WithClause→SelectStmt (CTEs allowed); rejected roots: InsertStmt, UpdateStmt, DeleteStmt, CreateStmt, DropStmt, CopyStmt, DoStmt, CallStmt; applied at three checkpoints: (a) Custom Query create/update before VIEW DDL, (b) generated SQL from builder_state before VIEW DDL, (c) preview execution defense-in-depth.
- AR-62: Table Allowlist & Catalog Source (Decision 6.6, AD-14 + OQ-11 resolved) — `appsettings.json DatasetManager:AllowedTables` array; env-var override `DatasetManager__AllowedTables__0=...`; startup cross-check against `information_schema.tables`; `DatasetAllowlist.cs` permanently strips denylist tables even if listed in config; `GET /api/datasets/catalog` (requires dataset-management) returns allowlisted tables + columns from `information_schema.columns`; catalog result cached in `IMemoryCache` 5 min; `DatasetSqlGenerator` validates every `node.data.tableName` against allowlist before SQL generation.
- AR-63: Preview Execution Security & Isolation (Decision 6.7, AD-16 resolved) — dedicated `formforge_preview` PostgreSQL read-only role (NOINHERIT; GRANT SELECT on allowlisted tables; REVOKE on internal audit/auth tables); `IPreviewConnectionFactory` / `PreviewConnectionFactory` wraps dedicated `NpgsqlDataSource` with `formforge_preview` credentials and `MaxPoolSize = 5`; execution: `BEGIN; SET LOCAL statement_timeout = '5s'; SELECT * FROM ({query}) AS _preview LIMIT 10; COMMIT;`; `NpgsqlException.SqlState == "57014"` → HTTP 408 PREVIEW_TIMEOUT; PostgreSQL error → HTTP 422 with PG error message (no stack traces); preview is read-only and does not save or create the Dataset.
- AR-64: CASE and Calculated Column Expression Security (Decision 6.8, AD-19 resolved) — `ExpressionSecurityValidator.cs` applies three layers: (1) per-expression keyword scan (reject if starts with DROP/INSERT/UPDATE/DELETE/CREATE/ALTER/TRUNCATE/MERGE/CALL or contains unquoted `;`); (2) wrap-parse via PgQuery.NET (`SELECT ({expression}) AS _x FROM generate_series(1,1) _t`); (3) final assembled query SELECT-only check (Decision 6.5); residual attack surface limited to `dataset-management` users.
- AR-65: Dataset API Contract (Decision 6.9) — 8 endpoints: GET /api/datasets (auth), GET /api/datasets/{id} (auth), GET /api/datasets/catalog (dataset-management), POST /api/datasets (dataset-management), PUT /api/datasets/{id} (dataset-management), DELETE /api/datasets/{id} (dataset-management), POST /api/datasets/preview (dataset-management), GET /api/admin/datasets/audit (platform-admin); 7 new error codes added to `ErrorCodes.cs`: INVALID_QUERY (422), INVALID_DATASET_NAME (422), DATASET_NAME_CONFLICT (409), DATASET_CONCURRENCY_CONFLICT (409), PREVIEW_TIMEOUT (408), TABLE_NOT_ALLOWLISTED (422), BUILDER_STATE_INVALID (422).
- AR-66: Server-Authoritative SQL Generator (Decision 6.10) — `DatasetSqlGenerator.cs` pure deterministic function, no I/O; 10-step algorithm: (1) pre-flight validation (left node exists, ≥1 column, aliases non-empty); (2) allowlist validation; (3) identifier safety via `SafeIdentifier`; (4) FROM clause (left-designated table, self-join aliases `t<index>`); (5) JOIN clauses per join edge; (6) SELECT list (plain/aggregated/CASE/calculated columns, all double-quoted `"table"."col" AS "alias"`); (7) GROUP BY auto-derived from aggregate presence; (8) WHERE with parameterized placeholders `$1,$2,...`; (9) ORDER BY in declared clause order; (10) final SELECT-only validation; returns `{ Sql, Parameters }` or `{ Errors }`; `custom_dataset.query` always set to generated SQL on save.
- AR-67: builder_state Schema Contract (Decision 6.11, AD-18 resolved) — canonical TypeScript interface in `web/src/features/datasets/types/builderState.ts`; C# `BuilderStateDto` record hierarchy in `DatasetSqlGenerator.cs` mirrors exactly; `BuilderState { nodes: TableNode[], edges: JoinEdge[], filters: FilterGroup, orderBy: OrderByClause[], caseColumns: CaseColumn[], calculatedColumns: CalculatedColumn[] }`; changes are a breaking cross-layer contract change requiring coordinated frontend + backend update.
- AR-68: React Flow Integration (Decision 6.12) — `@xyflow/react` v12 new dependency; `TableNode.tsx` custom node (table name header, Left/Right toggle, column checkboxes, aggregate dropdown, alias input, Add Case + Add Calculated Column buttons); `JoinEdge.tsx` custom edge (styled curve + delete control + click-to-inspect); `JoinInspector.tsx` (popover: joined columns display + join type selector); `builder_state` in React state is canonical; serialized to JSON on every PUT /api/datasets/{id}.
- AR-69: TanStack Query Keys for Dataset Manager (Decision 6.13) — `['datasets', 'list', { page, pageSize }]`; `['datasets', id]`; `['datasets', 'catalog']`; `['datasets', id, 'preview', previewKey]` (previewKey = stable hash of query/builderState to avoid double-fetch); `['datasets', 'audit', { page, datasetName?, operation? }]`.

### UX Design Requirements

Source: `ux-design-specification.md` (focused token spec, 2026-05-31) — semantic CSS-variable token system for three themes (Default Light, Slate Dark, Solarized), three regions (left menu, header, body), and five component groups (buttons, icon buttons, breadcrumbs, tabs, forms). Brownfield theming remediation of the `dark:`-variant + hardcoded-color breakage.

> **Status (verified 2026-06-02): IMPLEMENTED in code, not tracked as separate stories.** The `@custom-variant dark` line is removed from `web/src/index.css`; the new tokens (`--field`, `--field-border`, `--destructive-foreground`, `--ring-offset`, `--overlay-hover/active`, `--primary/accent-hover/active`) are present in `themes.css`/`index.css`; a `web/src/styles/__tests__/themeContrast.test.ts` guards the WCAG ratios; the hardcoded-color sweep is effectively complete (one straggler in `designer/ElementRenderer.tsx`). These UX-DRs are retained below as a record of the realized design contract — no separate Epic 7 stories are tracked for them.

UX-DR1: Remove the `@custom-variant dark` architectural defect — delete `index.css:7` (`@custom-variant dark (&:is([data-theme='slate-dark'] *))`). Themes are selected ONLY via `data-theme`; no `dark:` variant may survive as a theme axis. (§2.1 D1, §8.1)
UX-DR2: Token-system expansion — add per-theme tokens `--field`, `--field-border`, `--destructive-foreground`, `--ring-offset` to all three `[data-theme]` blocks in `themes.css`; add the `:root` overlay/alias block (`--field-foreground`, `--placeholder`, `--overlay-hover`, `--overlay-active`, `--primary-hover`, `--primary-active`, `--accent-hover`, `--accent-active` via `color-mix`); add the 10 `@theme inline` Tailwind utility mappings in `index.css` so `bg-field`, `border-field-border`, `text-destructive-foreground`, `bg-overlay-hover`, etc. resolve. (§3.1, §3.4, §3.5)
UX-DR3: WCAG color-value remediation — apply the 3 changed-token fixes (Solarized `--primary` 0.57→0.50 [white-on-primary 4.28→5.77]; Solarized `--accent` 0.61→0.52 [3.48→5.03]; Slate-Dark `--destructive` 0.704→0.55 [2.77→5.21]) plus the Default-Light `--ring` darken 0.708→0.55 (2.59→≈4.8). Every measured pair must meet AA (4.5:1 normal text / 3:1 non-text UI boundary). (§3.3, §7.2)
UX-DR4: Uniform interactive-state model — implement one convention applied across all themes with no `dark:` branch: Hover (solid controls → `--*-hover`; ghost/transparent → `--overlay-hover` 8% veil); Active/Pressed (`--*-active` / `--overlay-active` 14% veil); Focus-visible (2px `--ring` + 2px `--ring-offset`, keyboard-only via `:focus-visible`); Disabled (opacity-0.5 + not-allowed, no color token); Invalid (border + ring → destructive). (§4)
UX-DR5: Five brief components → tokens — wire each to semantic tokens: Button (`ui/button.tsx`, all 6 variants: default/secondary/outline/ghost/destructive + removals of `dark:`/`input` pairs on lines 8,14,18,20); Icon button (`size="icon"` + ad-hoc triggers, ≥44px touch targets, `currentColor` icons; fixes in `SearchBox.tsx:43`, `Navbar.tsx:267`, `SortHeader.tsx:37`); Breadcrumbs; Tabs; Forms (`ui/input.tsx`, `ui/textarea.tsx`, `ui/select.tsx`, `ui/checkbox.tsx`, `ui/label.tsx`). (§5.1–5.5)
UX-DR6: Supporting audited controls → tokens — `ui/tooltip.tsx` (`bg-slate-900`→`bg-popover`, etc.), `ui/popover.tsx`, `ui/command.tsx`, `ui/combobox.tsx`, `shared/ErrorBanner.tsx` (`bg-red-50/...`→destructive token family), `designer/RepeaterRowDrawer.tsx` (`bg-slate-800`→`bg-primary`). (§5.6)
UX-DR7: Three regions → tokens — Left menu (`shared/Navbar.tsx`: `bg-sidebar`, `border-sidebar-border`, active = `bg-sidebar-accent`; remove all `bg-white`/`slate-*` on lines 49,61–63,80–81,87,150–151,161,187–189,267); Top header (`routes/_app.tsx`: `bg-background`/`bg-card`, `text-foreground`; remove `border-slate-200 bg-white` line 97, `text-slate-700` line 108); Main body (`dataEntry/*`, `designer/*` incl. 50+ `DesignerCanvas.tsx` instances, `DesignerToolbar.tsx`, `ElementRenderer.tsx`, `DynamicComponent.tsx`). (§6.1–6.3)
UX-DR8: Hardcoded-color sweep (exit criterion) — zero `slate-*` / `bg-white` / `red-*` / `dark:` pairs remain across the ~19 audited files; every such class replaced by its mapped semantic token. Verifiable by repo-wide grep returning no matches in `web/src/components/**` and the audited region files. (§8.4)
UX-DR9: Theme & accessibility verification — toggle all three themes across header, left menu, body, and each of the five components; run an automated contrast check + keyboard-focus pass. Extends the existing Epic 7 accessibility story (Story 7.4) scope. (§7, §8.5)
UX-DR10: Lint guardrail (optional) — ESLint rule forbidding `dark:` and raw `bg-white` / `*-slate-*` utilities in `web/src/components/**` to prevent regression. (§8.6)
UX-DR11: Stakeholder decision — Solarized accent fidelity — choose: (A, default) darkened accent cyan (`0.52`) for AA-compliant white text, OR (B) exact Schoonover cyan (`0.61`) with dark `--accent-foreground` (`oklch(0.25 0.058 232)`). A product decision gates the final Solarized `--accent` / `--accent-foreground` values. (§3.3 note, §7.3)

### FR Coverage Map

| FR | Epic | Notes |
|---|---|---|
| FR-1 User Account Management | Epic 2 | Admin-managed users; bcrypt; deactivation revokes refresh |
| FR-2 Role Definition (per-Resource CRUD flags) | Epic 2 | `platform-admin` + `viewer` seeded |
| FR-3 User-Role Assignments | Epic 2 | ≤30s propagation via cache TTL |
| FR-4 Effective Permission Computation | Epic 2 | Server-side helper called from every endpoint |
| FR-5 Server-Side Endpoint Authorization | Epic 2 | HTTP 401/403 with stable codes |
| FR-6 Client-Side UI Permission Adaptation | Epic 2 | PermissionGate component |
| FR-7 Admin UI — Users, Roles, Assignments | Epic 2 | Self-deactivation blocked |
| FR-8 Designer Port and Refactor | Epic 3 | Ported from ESG Platform; foundational story |
| FR-9 Designer Creation | Epic 3 | Identifier validation kicks in here |
| FR-10 Canvas Drag-and-Drop | Epic 3 | Native HTML5 DnD preserved |
| FR-11 Component Property Configuration | Epic 3 | `fieldKey` validated SQL-safe |
| FR-12 Live Preview | Epic 3 | Uses production DynamicComponent code path |
| FR-13 Designer Save and Versioning | Epic 3 | Draft/Published/Archived lifecycle |
| FR-14 Component Library / Designer Listing | Epic 3 | Library page; per-row lifecycle actions |
| FR-15 DynamicComponent in Data Entry | Epic 3 | Renderer; consumed later by Epic 6 |
| FR-16 Menu Item CRUD | Epic 4 | Top-level + sub-menu (max depth 2) |
| FR-17 Schema Binding | Epic 5 | **Moved**: trigger of provisioning, lives with Epic 5 |
| FR-18 Menu Icon | Epic 4 | MinIO upload + lucide-react |
| FR-19 Role-Based Menu Access | Epic 4 | `allowedRoles` filter |
| FR-20 Menu Ordering | Epic 4 | Drag-and-drop reorder |
| FR-21 isActive Toggle | Epic 4 | Inactive items excluded from navbar |
| FR-22 Dynamic Navbar | Epic 4 | Permission-filtered; mobile hamburger |
| FR-23 designerId Identifier Validation | Epic 5 | Defense-in-depth at multiple points |
| FR-24 Initial Table Creation | Epic 5 | Transactional CREATE; system columns |
| FR-25 Additive-Only Schema Migration | Epic 5 | ALTER ADD COLUMN never DROP |
| FR-26 Schema Drift Visibility | Epic 5 | Admin drift view; explicit drop |
| FR-27 Repeater Child Table Provisioning | Epic 5 | FK + index + cycle detection |
| FR-28 Schema Change Audit Log | Epic 5 | Append-only; admin paginated view |
| FR-29 Paginated List | Epic 6 | <200ms p95 @100k rows |
| FR-30 Single Record Retrieval | Epic 6 | Optional `?include=children` |
| FR-31 Record Creation | Epic 6 | Unknown fields ignored |
| FR-32 Partial Record Update | Epic 6 | PUT honors partial semantics |
| FR-33 Soft Delete | Epic 6 | Cascade to Repeater children |
| FR-34 View and Restore Deleted Records | Epic 6 | Cascade-event-aware restore |
| FR-35 Nested Repeater Write | Epic 6 | Single transaction; omit = soft-delete |
| FR-36 CRUD Mutation Audit Log | Epic 6 | Append-only; field-level diff |
| FR-37 Mobile-First Responsive Layout | Epic 7 | <768px single column; touch targets 44px |
| FR-38 Theme Selection and Persistence | Epic 7 | 3 themes; no-flash hydration |
| FR-39 Data Entry Form UI | Epic 6 | **Bundled with CRUD** — user-facing surface |
| FR-40 Record List UI | Epic 6 | **Bundled with CRUD** — depends on FR-29 |
| FR-41 Loading, Empty, and Error States | Epic 6 | **Bundled with CRUD** — first concrete use |
| FR-42 WCAG 2.1 AA Accessibility | Epic 7 | Final hardening + axe-core CI gate |
| FR-43 Admin Settings Pages | Epic 7 | Shell + route guards; non-admin redirect |
| FR-44 .NET Aspire AppHost | Epic 1 | Foundation |
| FR-45 OpenAPI and Swagger UI | Epic 1 | Foundation |
| FR-46 Structured Logging with Correlation IDs | Epic 1 | Foundation (consumed throughout) |
| FR-47 Health Checks | Epic 1 | Foundation |
| FR-48 Docker Compose | Epic 1 | Foundation (parallel orchestration path) |
| FR-49 i18n Architecture | Epic 7 | Externalized strings audit (used throughout) |
| FR-50 Admin Welcome Email | Epic 2 | Dispatched async on user creation; SMTP failure non-blocking; Story 2.10 |
| FR-51 Forgot Password Flow | Epic 2 | Unauthenticated self-service; SHA-256 hash stored; anti-enumeration; Story 2.11 |
| FR-52 Authenticated Password Change | Epic 2 | Current password bcrypt-verified; other sessions revoked; Story 2.12 |
| FR-53 TOTP MFA | Epic 2 | Voluntary per-user; two-step login; backup codes; admin reset; Stories 2.13–2.15 |
| FR-54 Component Mode (VIEW/CRUD) | Epic 3 | Primary Story 3.11; mode-aware ACs threaded into Stories 3.2/3.4/3.8 (Designer surface), 5.2 (provisioning skip), 6.9 (read-only data entry); Architecture Decisions 1.8, 4.10, 4.11 |
| FR-55 Dataset Data Model | Epic 8 | custom_dataset + dataset_audit_log tables; datasets PG schema; Story 8.1 |
| FR-56 Dataset-Management RBAC | Epic 8 | can_manage_datasets on roles; RequireDatasetManagement filter; Story 8.2 |
| FR-57 dataset_name Identifier Validation | Epic 8 | regex + reserved keywords + denylist; client + server; Story 8.3 |
| FR-58 Transactional Dataset View Lifecycle | Epic 8 | CREATE / CREATE OR REPLACE / ALTER RENAME / DROP in single transaction; Stories 8.4–8.6 |
| FR-59 Optimistic Concurrency on Dataset Edit | Epic 8 | version integer compare-and-swap; 409 on mismatch; Story 8.5 |
| FR-60 Custom Query Authoring Mode | Epic 8 | SQL textarea; PgQuery.NET SELECT-only enforcement; Story 8.8 |
| FR-61 Dataset Audit Logging | Epic 8 | dataset_audit_log every CRUD+DDL event; succeeded flag for rolled-back DDL; Story 8.9 |
| FR-62 Dataset Management UI | Epic 8 | Admin Dataset Manager page with CRUD modal, mode toggle, audit link, preview button; Stories 8.7 + 8.10 |
| FR-63 Table Palette & Drag-to-Canvas | Epic 9 | Allowlisted tables left palette; TableNode; same-table multi-drag; Stories 9.1–9.2 |
| FR-64 Column-to-Column Join Creation | Epic 9 | JoinEdge from column handle to column handle; Story 9.3 |
| FR-65 Join Property Inspector | Epic 9 | Click join edge → join type + column display; Story 9.4 |
| FR-66 Table Left/Right Designation | Epic 9 | FROM anchor control per TableNode; disabled save/preview if none Left; Story 9.5 |
| FR-67 Select Columns Configuration | Epic 10 | Column checkboxes; aggregate/alias; CASE columns; calculated columns; Stories 10.1–10.4 |
| FR-68 Filter Conditions Dialog | Epic 10 | AND/OR combinator; arbitrarily nested groups; parameterized values; Stories 10.5–10.7 |
| FR-69 Order By Configuration | Epic 10 | ORDER BY panel; drag reorder; builder_state persisted; Story 10.8 |
| FR-70 Server-Authoritative SQL Generation | Epic 11 | DatasetSqlGenerator: FROM/JOIN/SELECT/GROUP BY/WHERE/ORDER BY; SELECT-only final check; Story 11.1 |
| FR-71 builder_state Persistence & Restore | Epic 11 | builder_state JSONB persisted on save; canvas restores exactly on reopen; query always in sync; Story 11.2 |
| FR-72 Query Preview (Hard LIMIT 10) | Epic 11 | LIMIT 10 + statement timeout + formforge_preview read-only role; Story 11.3 |
| FR-73 Builder-Mode View Lifecycle Integration | Epic 11 | Builder-mode save reuses FR-58 transactional lifecycle; Story 11.4 |

### UX-DR Coverage Map

| UX-DR | Epic | Notes |
|---|---|---|
| UX-DR1 Remove `@custom-variant dark` | Epic 7 | Architectural defect; prerequisite for all theming work |
| UX-DR2 Token-system expansion | Epic 7 | New per-theme tokens + `:root` overlays + `@theme inline` mappings |
| UX-DR3 WCAG color-value remediation | Epic 7 | 3 changed-token fixes + Default-Light `--ring` darken |
| UX-DR4 Interactive-state model | Epic 7 | Uniform hover/active/focus-visible/disabled/invalid; no `dark:` |
| UX-DR5 Five components → tokens | Epic 7 | Button, icon button, breadcrumbs, tabs, forms |
| UX-DR6 Supporting controls → tokens | Epic 7 | tooltip, popover, command, combobox, ErrorBanner, RepeaterRowDrawer |
| UX-DR7 Three regions → tokens | Epic 7 | Navbar (left menu), header (`_app.tsx`), body (designer/dataEntry) |
| UX-DR8 Hardcoded-color sweep | Epic 7 | Exit criterion — zero `slate-*`/`white`/`red-*`/`dark:` in audited files |
| UX-DR9 Theme & a11y verification | Epic 7 | Extends existing Story 7.4 (accessibility) |
| UX-DR10 Lint guardrail (optional) | Epic 7 | ESLint rule forbidding `dark:`/`bg-white`/`*-slate-*` in components |
| UX-DR11 Solarized accent decision | Epic 7 | Stakeholder decision gate (accent fidelity vs AA) |

## Epic List

### Epic 1: Foundation & Infrastructure
Stand up the runnable shell of FormForge so all subsequent feature epics have a working dev loop, observability, and deployment story. After this epic, a developer can clone the repo and run the full stack (API + PostgreSQL + MinIO + React SPA) via either `dotnet run` on the Aspire AppHost or `docker compose up`; structured logs flow with correlation IDs, OpenAPI is browsable, and health checks gate readiness.

**User outcome:** Developer / Integrator and Operator can run, integrate with, and monitor the platform. Enables every subsequent epic.

**FRs covered:** FR-44, FR-45, FR-46, FR-47, FR-48
**NFRs covered:** NFR-12 (restart resilience), NFR-13 (browser support / build target)
**Architecture stories added:** G-1.1 (project scaffolding — `aspire new aspire-starter` + Vite/shadcn CLI per AR-1)
**Architecture-derived scope:** AR-1, AR-2, AR-3 (TanStack Router override), AR-16 (security headers + CSP base), AR-24 (correlation ID middleware), AR-38 (OpenTelemetry/observability), AR-40 (multi-stage Dockerfile), AR-42 (env configuration), AR-43 (CI/CD pipeline), AR-44 (Compose parity)

---

### Epic 2: Identity, Roles & Permissions
Authenticated, authorized requests gate every other epic. Platform Admins manage users and roles; users log in and receive JWTs; effective permissions are computed and enforced on both server and client. Email-based account flows (welcome email on creation, forgot-password self-service, authenticated password change) and optional TOTP MFA complete the identity surface.

**User outcome:** Platform Admin can onboard users and grant access, with a welcome email dispatched automatically. Any user can authenticate (including via a two-step TOTP challenge when MFA is enabled), change their password from settings, and reset it via email if forgotten. Content Editors and Viewers see UI tailored to their permissions.

**FRs covered:** FR-1, FR-2, FR-3, FR-4, FR-5, FR-6, FR-7, FR-50, FR-51, FR-52, FR-53
**NFRs covered:** NFR-4 (auth required), NFR-5 (token storage), NFR-8 (secrets via env)
**Architecture-derived scope:** AR-11 (permission cache + invalidation events), AR-12 (HS256 JWT), AR-13 (BCrypt-12), AR-14 (CORS), AR-15 (rate limiting), AR-17 (secret storage), AR-18 (ProblemDetails error envelope), AR-19 (API versioning posture), AR-22 (route groups with filter chain), AR-32 (httpClient with refresh-and-retry), AR-53 (MailKit email service + Mailpit dev container), AR-54 (password reset token — SHA-256 hash, 1-hour TTL), AR-55 (authenticated password change), AR-56 (TOTP MFA — Otp.NET, IDataProtector, mfaSessionToken, backup codes)

---

### Epic 3: Component Schema Designer
Platform Admins design data-entry forms visually. The designer is ported from the ESG Platform reference codebase and refactored for the new stack. Admins create Designers, drag components onto a canvas, configure properties, preview, and manage versioned drafts/publishes.

**User outcome:** Platform Admin can author, version, and publish form layouts without writing code. DynamicComponent renderer is available for downstream data entry (Epic 6).

**FRs covered:** FR-8, FR-9, FR-10, FR-11, FR-12, FR-13, FR-14, FR-15, FR-54
**NFRs covered:** NFR-2 (designer save p95 <500ms)
**Architecture stories added:** Story 3.10 Keyboard-Accessible Designer DnD (`useKeyboardDnD` per AR-30; PRD label B-11); Story 3.11 Declare and Enforce Component Mode (FR-54; PRD label B-10)
**Architecture-derived scope:** AR-30 (parallel keyboard interaction model), AR-35 (DynamicComponent bridge preserved as black box), AR-31 (frontend folder structure for `components/designer/`), Decision 1.8 (Component Mode `component_schemas.mode`), Decision 4.10 (VIEW read-only DynamicComponent), Decision 4.11 (CRUD-only property pickers)

---

### Epic 4: Menu Management
Platform Admins configure the navigation structure. Menu Items live in a 2-level hierarchy with icons, role-based visibility, ordering, and isActive toggles. The dynamic navbar renders a permission-filtered view for each user. **Schema binding (FR-17) is intentionally deferred to Epic 5** — this epic establishes the navigation shape; Epic 5 wires it to backing tables.

**User outcome:** Platform Admin can shape the platform's navigation; authenticated users see the correct menus for their roles.

**FRs covered:** FR-16, FR-18, FR-19, FR-20, FR-21, FR-22
**NFRs covered:** NFR-3 (navbar fetch <100ms cached), NFR-7 (file upload validation — icon uploads establish the pattern)
**Architecture-derived scope:** AR-21 (PagedResult shape used in admin menu list), AR-26 (MinIO upload pattern for icons — establishes pattern reused in Epic 6 for Image fields)

---

### Epic 5: Dynamic Table Provisioning
Binding a Published Designer version to a Menu Item provisions a real PostgreSQL table. Schema evolves additively across versions; Repeater components produce child tables with FKs; orphaned columns are visible and droppable on demand. Every DDL event is audited. In-flight provisioning jobs survive process restart.

**User outcome:** Platform Admin's design becomes a real, queryable database table. Schema evolution is safe and observable. Data integrity is preserved across version changes.

**FRs covered:** FR-17, FR-23, FR-24, FR-25, FR-26, FR-27, FR-28
**NFRs covered:** NFR-6 (SQL injection defense), NFR-9 (schema audit log append-only), NFR-11 (transactional DDL with rollback)
**Architecture stories added:** ProvisioningRecoveryService (re-enqueues Pending jobs at startup per AR-52)
**Architecture-derived scope:** AR-4 (identifier sanitization pipeline + `SafeIdentifier`), AR-5 (complete 14-component PG type mapping), AR-6 (soft-delete cascade column — schema-level prep), AR-7 (schema registry cache), AR-8 (audit log indexing), AR-9 (EF/Dapper separated transactions + Channel + BackgroundService), AR-10 (EF migrations on startup), AR-23 (binding-diff endpoint for re-bind), AR-37 (single-consumer DDL queue), AR-52 (recovery service)

---

### Epic 6: Generic CRUD Service & Data Entry
End-users create, read, update, and soft-delete records through a generic, permission-gated UI. The CRUD service serves any provisioned table via Dapper with whitelisted filter/sort, parameterized payloads, and Repeater-aware nested writes. The user-facing surface — data entry forms (DynamicComponent), record lists, and standard loading/empty/error states — ships in the same epic because it has no value without the endpoints, and the endpoints have no observable surface without it.

**User outcome:** Content Editor enters and edits records via dynamic forms; Viewer browses paginated, filterable, sortable lists; Platform Admin restores soft-deleted records. All actions are permission-gated and audited.

**FRs covered:** FR-29, FR-30, FR-31, FR-32, FR-33, FR-34, FR-35, FR-36, FR-39, FR-40, FR-41
**NFRs covered:** NFR-1 (p95 <200ms @100k rows), NFR-7 (file upload validation — Image fields), NFR-10 (CRUD mutation audit log append-only)
**Architecture-derived scope:** AR-6 (soft-delete cascade walk via schema registry), AR-20 (Layer 2 dynamic payload validator), AR-21 (PagedResult), AR-26 (presigned URLs for Image fields + /api/files/refresh-urls), AR-28 (error/loading boundaries), AR-29 (sonner toasts), AR-34 (react-hook-form + Zod), AR-46 (Option C hybrid JSON casing for dynamic endpoints), AR-48 (TanStack Query key tuples), AR-49 (mutation strategy — pessimistic for form saves)

---

### Epic 7: UX Polish & Cross-Cutting Hardening
Final epic — verify and harden the cross-cutting concerns that have been built incrementally throughout, plus ship the user-facing polish features (mobile responsive nav, themes, admin shell). Runs accessibility audits, externalized-string audits, and validates that the admin settings area meets the design contract.

**User outcome:** Every user has a responsive, themeable, accessible interface. Platform Admins access settings from a dedicated, route-guarded area. Translation to other languages is a configuration task, not a code change.

**FRs covered:** FR-37, FR-38, FR-42, FR-43, FR-49
**NFRs covered:** NFR-14 (i18n architecture-ready, English-only at launch)
**UX-DRs covered:** UX-DR1–UX-DR11 (theme token system remediation — from `ux-design-specification.md`; **implemented directly in code, verified 2026-06-02 — not tracked as separate Epic 7 stories**; see UX Design Requirements section)
**Architecture-derived scope:** AR-27 (theme no-flash hydration with CSP nonce), AR-33 (i18n synchronous init), AR-39 (single-origin SPA hosting from API)

---

### Epic 8: Dataset Foundation & Custom Query Mode
Deliver the Dataset Manager subsystem foundation: the `custom_dataset` table and `dataset_audit_log`, the `datasets` PostgreSQL schema for VIEW isolation, the `dataset-management` RBAC permission, `dataset_name` identifier validation with a permanent denylist, full Dataset CRUD with the transactional VIEW lifecycle, optimistic concurrency, Custom Query Mode with PgQuery.NET SELECT enforcement, and the Dataset Management UI. This epic is shippable standalone; Epics 9–11 layer the visual Query Builder on top.

**User outcome:** Users with `dataset-management` permission can create, edit, and delete named Datasets, each materialized as a PostgreSQL VIEW, with full audit traceability and rollback safety. Custom SQL can be authored directly with SELECT enforcement protecting the database.

**FRs covered:** FR-55, FR-56, FR-57, FR-58, FR-59, FR-60, FR-61, FR-62
**NFRs covered:** NFR-17 (Dataset Manager security — identifier quoting, parameterized values, SELECT-only enforcement, table allowlist, transactional DDL, optimistic concurrency)
**Architecture-derived scope:** AR-57 (schema + migration + datasets PG schema), AR-58 (dataset-management permission model), AR-59 (transactional view lifecycle — synchronous NpgsqlTransaction), AR-60 (optimistic concurrency version column), AR-61 (SELECT-only enforcement via PgQuery.NET), AR-65 (Dataset API contract + 7 new error codes)

**Dependencies:** Epic 2 (RBAC infrastructure for dataset-management permission), Epic 1 (EF migrations + Dapper infrastructure)

---

### Epic 9: Query Builder Canvas & Joins
The visual Query Builder replaces the SQL textarea with a React Flow canvas. Users drag allowlisted tables from the Table Palette, connect column handles to create JOIN edges, configure join type and sidedness in the Join Inspector, and designate one table as the LEFT (FROM) anchor. This epic delivers the canvas foundation; Epics 10–11 add column/filter/ORDER BY configuration and SQL generation.

**User outcome:** Users can visually place multiple tables on a canvas, connect them with typed JOIN edges, and see the query structure take shape without writing SQL.

**FRs covered:** FR-63, FR-64, FR-65, FR-66
**Architecture-derived scope:** AR-62 (table allowlist via appsettings.json + catalog endpoint GET /api/datasets/catalog), AR-68 (React Flow @xyflow/react v12; TableNode, JoinEdge, JoinInspector custom types)

**Dependencies:** Epic 8 (Dataset Foundation — RBAC + API contract + catalog endpoint foundation)

---

### Epic 10: Builder Config
Adds the column selection, aggregate/alias, CASE derived columns, calculated columns, Filter Conditions dialog (arbitrarily nested AND/OR groups with parameterized values), and ORDER BY panel to the Query Builder canvas from Epic 9. All configuration is stored in `builder_state` JSON. Server-side expression security validation gates CASE and calculated columns.

**User outcome:** Users can fully configure the content and filtering of their Dataset query visually — selecting columns, applying aggregations and aliases, adding derived expressions, building complex WHERE conditions with nesting, and setting sort order.

**FRs covered:** FR-67, FR-68, FR-69
**Architecture-derived scope:** AR-64 (CASE/calculated expression security — per-expression keyword scan + wrap-parse + final SELECT-only check), AR-67 (builder_state schema contract — canonical TS interface + C# BuilderStateDto), AR-68 (TableNode column list + FilterConditionsDialog + OrderByPanel)

**Dependencies:** Epic 9 (Query Builder Canvas — canvas nodes and edges are prerequisites for column/filter/ORDER BY configuration)

---

### Epic 11: SQL Generation, Preview & Builder View Sync
Closes the Query Builder loop: the server generates authoritative SQL from `builder_state` (never trusting client SQL in Builder Mode), `builder_state` is persisted on every save and restored faithfully on reopen, Preview executes with LIMIT 10 and a statement timeout against a dedicated read-only PostgreSQL role, and builder-mode saves reuse the Epic 8 transactional VIEW lifecycle with identical atomicity guarantees.

**User outcome:** Users can preview their visually-built query (up to 10 rows) before saving, see the canvas exactly as they left it when reopening a Dataset, and rely on the same rollback safety for builder-mode saves as for hand-authored SQL.

**FRs covered:** FR-70, FR-71, FR-72, FR-73
**Architecture-derived scope:** AR-63 (preview execution isolation — formforge_preview read-only PG role; IPreviewConnectionFactory MaxPoolSize=5; SET LOCAL statement_timeout), AR-66 (DatasetSqlGenerator 10-step algorithm), AR-67 (builder_state contract — generator and canvas implement same interface)

**Dependencies:** Epic 10 (Builder Config — SQL generator consumes full builder_state including columns, filters, ORDER BY, CASE, calculated columns), Epic 8 (view lifecycle — builder-mode saves reuse FR-58 lifecycle)

---

## Epic Dependency Summary

```
Epic 1 (Foundation)
  └─► Epic 2 (Identity)
        ├─► Epic 3 (Designer)
        │     └─► Epic 5 (Provisioning) ──► Epic 6 (CRUD + Data Entry)
        └─► Epic 4 (Menus) ─────────────┘                │
                                                          └─► Epic 7 (Polish)
        └─► Epic 8 (Dataset Foundation)
                └─► Epic 9 (Query Builder Canvas)
                        └─► Epic 10 (Builder Config)
                                └─► Epic 11 (SQL Gen, Preview & Sync)
```

- Epic 1 unblocks every other epic.
- Epic 2 (Identity) unblocks Epics 3, 4 (admin pages require auth + admin role).
- Epic 3 (Designer) and Epic 4 (Menus) are independent of each other and can run in parallel after Epic 2.
- Epic 5 (Provisioning) requires Epic 3 (Designer versions to bind) and Epic 4 (Menu Items to bind to).
- Epic 6 (CRUD) requires Epic 5 (tables must exist) and Epic 3's DynamicComponent renderer.
- Epic 7 (Polish) runs last — exercises the rest of the platform.
- **Epic 8 (Dataset Foundation)** requires Epic 2 (RBAC infrastructure for `dataset-management` permission) and Epic 1 (EF migrations + Dapper). It is independent of Epics 3–7 and can run in parallel with any of them.
- **Epic 9 (Query Builder Canvas)** requires Epic 8 (catalog endpoint + Dataset CRUD API contract).
- **Epic 10 (Builder Config)** requires Epic 9 (canvas nodes + edges as prerequisites for column/filter config).
- **Epic 11 (SQL Gen, Preview & Sync)** requires Epic 10 (full builder_state including columns, filters, ORDER BY) and Epic 8 (view lifecycle for builder-mode saves).

---

## Epic 1: Foundation & Infrastructure

Stand up the runnable shell of FormForge so all subsequent feature epics have a working dev loop, observability, and deployment story. After this epic, a developer can clone the repo and run the full stack via either `dotnet run` on the Aspire AppHost or `docker compose up`; structured logs flow with correlation IDs, OpenAPI is browsable, and health checks gate readiness.

### Story 1.1: Initial Project Scaffolding

As a Developer,
I want a scaffolded FormForge monorepo using the architecture's chosen starter templates,
So that all subsequent feature work begins from a stable, decision-aligned foundation.

**Acceptance Criteria:**

**Given** an empty repo root
**When** I run `aspire new aspire-starter --name FormForge --output .`
**Then** the solution `FormForge.sln` is created with `src/FormForge.AppHost/`, `src/FormForge.ServiceDefaults/`, and the Aspire-default web project
**And** the Aspire-default Blazor sample web project is removed
**And** `ApiService` is renamed to `FormForge.Api` in the solution and folder names
**And** `src/FormForge.Api.Tests/` (xUnit + `Testcontainers.PostgreSQL`) is added to the solution

**Given** the backend scaffold is in place
**When** I run `npm create vite@latest web -- --template react-ts` followed by `npx shadcn@latest init` and the full dependency-install command sequence from the Architecture's Starter Template Evaluation section
**Then** `web/` contains a React 19 + Vite + TS + Tailwind 4 + shadcn skeleton
**And** `web/package.json` declares `@tanstack/react-router`, `@tanstack/react-query`, `react-hook-form`, `zod`, `@hookform/resolvers`, `i18next`, `react-i18next`, plus dev deps `@tanstack/router-plugin`, `vitest`, `@testing-library/react`, `@testing-library/jest-dom`, `jsdom`
**And** `web/vite.config.ts` registers the TanStack Router and Tailwind Vite plugins

**Given** the repo root
**When** I inspect the file tree
**Then** `docker-compose.yml`, `Dockerfile`, `.dockerignore`, `Directory.Build.props`, `Directory.Packages.props`, `global.json` (pinning .NET 10 SDK), `.editorconfig`, `.gitattributes`, and `.config/dotnet-tools.json` are present per Architecture Section "Complete Project Directory Structure"

### Story 1.2: Aspire AppHost Orchestration

As a Developer,
I want to start all services with a single `dotnet run` from the AppHost project,
So that local development requires no manual service management.

**Acceptance Criteria:**

**Given** the AppHost project is configured
**When** I run `dotnet run --project src/FormForge.AppHost`
**Then** the AppHost starts PostgreSQL (via `Aspire.Hosting.PostgreSQL`), MinIO (as an Aspire container with endpoints 9000/9001), the API project (`FormForge.Api`), and the React frontend (via `AddViteApp`)
**And** the API project waits for PostgreSQL to be ready before starting
**And** the React frontend waits for the API to be ready before starting

**Given** any service in the AppHost
**When** the service receives its configuration
**Then** connection strings and service URLs arrive via environment variables (Aspire `WithReference` injection)
**And** no hardcoded ports appear in business code

**Given** the AppHost is running
**When** I open `https://localhost:15888`
**Then** the Aspire Dashboard renders showing health, logs, traces, metrics, and environment for every service

### Story 1.3: Docker Compose Local Stack

As a Developer without the .NET 10 SDK installed,
I want to start the full local stack with `docker compose up`,
So that contributors without the Aspire toolchain can still run the platform.

**Acceptance Criteria:**

**Given** the repo root
**When** I run `docker compose up`
**Then** services `api`, `postgres`, `minio`, `minio-init` come up (per AR-44, the frontend is served from the API container, with no dedicated frontend dev service in Compose mode)

**Given** the API container starts for the first time
**When** it boots
**Then** EF Core migrations run automatically against the Compose-provided PostgreSQL (idempotent `Database.Migrate()`)
**And** the `formforge` MinIO bucket is created by the `minio-init` service on first startup

**Given** any service in the compose network
**When** it resolves another service's URL
**Then** the URL uses the Docker network service name (e.g., `http://postgres:5432`, `http://minio:9000`), not `localhost`

### Story 1.4: Auto-Generated OpenAPI Spec and Swagger UI

As a Developer or Integrator,
I want access to an up-to-date OpenAPI 3.1 spec and a Swagger UI in development,
So that I can integrate without reading source code.

**Acceptance Criteria:**

**Given** the API is running
**When** I GET `/openapi/v1.json`
**Then** the response is a valid OpenAPI 3.1 spec auto-generated from Minimal API endpoint metadata

**Given** the API is running in Development mode
**When** I open `/swagger`
**Then** Swagger UI renders and is interactively usable

**Given** the API is running in Production mode
**When** I open `/swagger`
**Then** I receive an HTTP 404 (Swagger UI disabled in production)

**Given** a dynamic CRUD endpoint (`/api/data/{designerId}/*`)
**When** I view its schema in the OpenAPI document
**Then** the request and response bodies are typed as `object` with `additionalProperties: true` and the description explains the runtime-dynamic shape (per AR-19)
**And** the `designerId` path parameter declares pattern `^[a-z_][a-z0-9_]{0,62}$`

**Given** any authenticated endpoint
**When** I view its OpenAPI metadata
**Then** it documents Bearer-token authentication as a security requirement

### Story 1.5: Structured Logging with Correlation IDs

As a Developer,
I want to trace any request end-to-end through structured logs using a correlation ID,
So that debugging is tractable without guesswork.

**Acceptance Criteria:**

**Given** an incoming HTTP request
**When** the correlation-ID middleware runs
**Then** the request is assigned a correlation ID — read from the `X-Correlation-ID` request header if present, otherwise generated as a ULID
**And** the same correlation ID is set on the response header `X-Correlation-ID`

**Given** any log entry emitted during a request
**When** I inspect the log output
**Then** the entry is structured JSON containing `timestamp`, `level`, `correlationId`, `userId` (if authenticated), `endpoint`, `message`, and `exception` (if any)

**Given** any DDL or CRUD mutation is logged at `Information` level
**When** I inspect the log entry
**Then** it additionally carries `designerId`, `operation`, and a `sqlFingerprint` field containing the parameterized SQL with placeholders only (no parameter values, per FR-46 AC-3)

**Given** a string-interpolated `ILogger` call exists in source
**When** the analyzer (`Meziantou.Analyzer` or equivalent) runs in CI
**Then** the call is flagged as a violation (anti-pattern enforcement)

### Story 1.6: Health Check Endpoints

As an Operator,
I want to poll health check endpoints to confirm PostgreSQL and MinIO are reachable,
So that monitoring can alert on dependency failures.

**Acceptance Criteria:**

**Given** the API process is running
**When** I GET `/health/live`
**Then** the response is HTTP 200 unconditionally (liveness — process is up)

**Given** the API is running
**When** I GET `/health/ready`
**Then** the response is HTTP 200 if PostgreSQL and MinIO are reachable
**And** the response is HTTP 503 if either dependency is unreachable

**Given** the API is running
**When** I GET `/health`
**Then** the response body is `{ status: "healthy"|"degraded"|"unhealthy", checks: { postgres: {...}, minio: {...} } }`
**And** the admin-only authorization on this endpoint (per AR-25) is layered on as part of the auth-filter chain in Epic 2 — Story 1.6 ships the endpoint and its response shape; admin authorization is configured once the auth pipeline exists

**Given** the MinIO health check
**When** it queries MinIO
**Then** it performs a HEAD-bucket request with a 5-second timeout

**Given** any health check runs
**When** the publisher fires
**Then** status is logged every 30 seconds and visible in the Aspire Dashboard

---

## Epic 2: Identity, Roles & Permissions

Authenticated, authorized requests gate every other epic. Platform Admins manage users and roles; users log in and receive JWTs; effective permissions are computed and enforced on both server and client.

### Story 2.1: JWT Login

As any registered user,
I want to submit email and password to receive a JWT access token and a refresh token,
So that I can authenticate subsequent API requests.

**Acceptance Criteria:**

**Given** a registered, active user with valid credentials
**When** I POST `/api/auth/login` with `{ email, password }`
**Then** I receive HTTP 200 with `{ accessToken, refreshToken, expiresIn }`
**And** the access token is a signed JWT (HS256, per AR-12) containing `userId`, `email`, `roles` (array of role names), `iat`, `exp` with a 15-minute TTL
**And** the refresh token is opaque, stored server-side in `refresh_tokens` with a 7-day TTL, and returned both in the JSON body and as an HttpOnly + `SameSite=Strict` cookie (per NFR-5)

**Given** an invalid email or password
**When** I POST `/api/auth/login`
**Then** I receive HTTP 401 with a generic message (no user enumeration — same response for unknown email and wrong password)

**Given** a user whose `isActive: false`
**When** I POST `/api/auth/login` with their correct credentials
**Then** I receive HTTP 403 with `code: "ACCOUNT_INACTIVE"` and `messageKey: "auth.accountInactive"`

**Given** rate-limiting policy AR-15 is active
**When** I exceed 10 login attempts from the same IP within 1 minute
**Then** I receive HTTP 429 with a `Retry-After` header

### Story 2.2: Token Refresh

As an authenticated user with a valid refresh token,
I want to exchange it for a new access token,
So that my session persists without re-login.

**Acceptance Criteria:**

**Given** a valid, unrevoked refresh token
**When** I POST `/api/auth/refresh` (HttpOnly cookie travels automatically)
**Then** I receive a new `{ accessToken, refreshToken, expiresIn }`
**And** the old refresh token is immediately revoked (single-use rotation)

**Given** an expired or revoked refresh token
**When** I POST `/api/auth/refresh`
**Then** I receive HTTP 401 with `code: "REFRESH_TOKEN_INVALID"`

**Given** the page is reloaded and the access token is lost from memory
**When** the SPA boots via the `useAuthQuery` flow (Decision 2.1)
**Then** the SPA calls `/api/auth/refresh` before rendering any protected route
**And** on 200, protected routes render seamlessly without redirecting to login

### Story 2.3: Logout

As an authenticated user,
I want to log out and revoke my refresh token,
So that my session cannot be resumed.

**Acceptance Criteria:**

**Given** I am logged in
**When** I POST `/api/auth/logout`
**Then** the submitted refresh token is revoked server-side; subsequent use returns HTTP 401
**And** the SPA discards the in-memory access token

### Story 2.4: Role CRUD

As a Platform Admin,
I want to create, edit, and delete Roles with per-Resource CRUD-flag configuration,
So that I can define what each Role is permitted to do on each data module.

**Acceptance Criteria:**

**Given** the system is initialized
**When** EF Core migrations complete
**Then** two system roles are seeded: `platform-admin` (full CRUD on all resources + admin areas) and `viewer` (canRead only, all resources)

**Given** I am authenticated as a Platform Admin
**When** I POST `/api/admin/roles` with `{ name, description, permissions: [{ resourceId, canCreate, canRead, canUpdate, canDelete }] }`
**Then** a new Role is created with a unique name
**And** duplicate name returns HTTP 409

**Given** a Role with active user assignments
**When** I DELETE that Role
**Then** I receive HTTP 409 with `messageKey: "roles.hasAssignments"`

**Given** the admin role list endpoint
**When** I GET `/api/admin/roles?page=1&pageSize=25`
**Then** I receive a `PagedResult<RoleListItem>` (per AR-21) with name, description, and permission count

### Story 2.5: User-Role Assignment

As a Platform Admin,
I want to assign and remove Roles from a User,
So that I can control their effective permissions.

**Acceptance Criteria:**

**Given** an existing user and a set of role IDs
**When** I PUT `/api/admin/users/{id}/roles` with `{ roleIds: [...] }`
**Then** the user's role set is replaced atomically
**And** a user may hold zero or more roles

**Given** a user's roles change
**When** the `UserRoleAssignmentChanged` event fires (per AR-11)
**Then** that user's permission cache entry is evicted immediately
**And** the user's effective permissions reflect the new role set on the next request (≤30 s worst case via TTL fallback)

### Story 2.6: Effective Permission Computation and Server-Side Endpoint Authorization

As the system,
I enforce role-based permissions on every CRUD and admin endpoint,
So that unauthorized actions are rejected server-side regardless of client behavior.

**Acceptance Criteria:**

**Given** an authenticated user with one or more roles
**When** any `/api/data/{designerId}/*` endpoint is invoked
**Then** the `EffectivePermissions` for that user are resolved (from cache or DB per AR-11)
**And** the permission for the requested action (`create`, `read`, `update`, `delete`) is the union of all the user's roles' flags on that Resource (FR-4)

**Given** the user lacks the required CRUD flag for the requested action
**When** the endpoint is invoked
**Then** the response is HTTP 403 with `ProblemDetails { code: "FORBIDDEN", resource: designerId, action: "create|read|update|delete", messageKey: "errors.forbidden" }`

**Given** the request carries no JWT or an invalid JWT
**When** the endpoint is invoked
**Then** the response is HTTP 401 with `code: "UNAUTHENTICATED"`

**Given** an `/api/admin/*` endpoint
**When** the authenticated user lacks the `platform-admin` role
**Then** the response is HTTP 403

**Given** the route-group filter chain is configured (per AR-22)
**When** a request flows through any group
**Then** the order is: correlation ID → rate limit → auth → permission → validation → handler

**Given** the `/health` (detailed) endpoint from Story 1.6
**When** the auth pipeline is wired in this story
**Then** `/health` requires `platform-admin` (per AR-25); `/health/live` and `/health/ready` remain anonymous

### Story 2.7: Client-Side Permission Hiding

As a Content Editor or Viewer,
I see only UI controls I am authorized to use,
So that I am not confused by actions I cannot perform.

**Acceptance Criteria:**

**Given** I am logged in
**When** the SPA renders the navbar
**Then** Menu Items for which I have no `canRead` are absent (not disabled — absent) per FR-6 AC-3

**Given** I am on a record list page
**When** the page renders
**Then** the "New Record" button is hidden if I lack `canCreate` on that Resource
**And** Edit/Delete controls on rows and detail views are hidden if I lack `canUpdate`/`canDelete`

**Given** a permission check is needed in a component
**When** the component renders
**Then** it uses the `PermissionGate` shared component or the `usePermission` hook (per AR-31 / Architecture file layout) — never reads JWT claims directly

**Given** permissions change server-side (role assignment, deactivation)
**When** the access token is next refreshed
**Then** the client re-fetches `/api/users/me/permissions` and updates the cached set used by `PermissionGate`

### Story 2.8: Admin User Management UI

As a Platform Admin,
I want to manage users from a dedicated settings page,
So that I have full control over who accesses the platform without touching the database.

**Acceptance Criteria:**

**Given** I am logged in as a Platform Admin
**When** I navigate to Admin > Users
**Then** I see a paginated list of all users with name, email, status (Active/Inactive), and role count

**Given** the Create User form
**When** I submit `{ email, displayName, temporaryPassword }`
**Then** a user is created via `POST /api/admin/users` and I see the new user in the list
**And** the email field is rejected as duplicate with inline error if the email already exists (HTTP 409 mapped to `setError`)
**And** a welcome email is dispatched asynchronously to the new user's address per FR-50 (Story 2.10)

**Given** the user detail page
**When** I toggle Deactivate or Reactivate
**Then** the user's `isActive` flag flips and any active refresh tokens for the user are revoked immediately (per FR-1)

**Given** I am the currently logged-in admin
**When** I attempt to deactivate myself
**Then** the action is blocked with inline error (per FR-7 AC-5)

**Given** the role-assignment control on a user
**When** I open it
**Then** I see a multi-select listing all available roles; saving sends `PUT /api/admin/users/{id}/roles`

### Story 2.9: Admin Role Management UI

As a Platform Admin,
I want to manage Roles and their per-Resource permissions from a dedicated admin page,
So that I can define access control without writing SQL.

**Acceptance Criteria:**

**Given** I am logged in as a Platform Admin
**When** I navigate to Admin > Roles
**Then** I see a paginated list of all roles with name, description, and permission count

**Given** I open a role for editing
**When** the editor renders
**Then** I see a permission matrix: rows = all Resources (every Designer that has a Menu Binding), columns = `canCreate`/`canRead`/`canUpdate`/`canDelete` checkboxes

**Given** a new Menu Binding is created (adding a new Resource to the system)
**When** the role editor re-loads
**Then** the matrix shows a new row for the new Resource with all flags defaulting to `false` for every existing Role (per FR-7 AC-3)

**Given** I save the role
**When** the save succeeds
**Then** the `RolePermissionsChanged` event fires (per AR-11) and all users holding that role have their permission cache evicted

### Story 2.10: Welcome Email on User Creation

As a newly created user,
I receive a welcome email when a Platform Admin creates my account,
So that I have my credentials without requiring an out-of-band handoff.

**Acceptance Criteria:**

**Given** a Platform Admin submits a valid `POST /api/admin/users` request
**When** the user record is created successfully
**Then** a welcome email is dispatched asynchronously (fire-and-forget via `Task.Run` + catch per AR-53)
**And** the email is sent to the new user's registered email address
**And** the email body contains: the platform name ("FormForge"), the user's email address, their temporary password (plaintext as supplied by the admin), and a link to the login page

**Given** the SMTP delivery fails (network error, server unavailable, etc.)
**When** the dispatch attempt throws
**Then** the exception is caught and logged at `Warning` with `recipient`, `templateType: "welcome"`, `correlationId`, and the error message (per AR-53)
**And** user creation still returns HTTP 201 — SMTP failure does not block account creation (per FR-50 AC-3)
**And** the API response body includes `warnings: ["Welcome email could not be sent"]`

**Given** the dev environment is running via Aspire AppHost
**When** a user is created
**Then** the welcome email arrives in Mailpit (accessible at http://localhost:8025 per AR-53)
**And** no real SMTP server is required for local development

**Given** the email transport configuration
**When** the API starts
**Then** `SMTP_HOST`, `SMTP_PORT`, `SMTP_USER`, `SMTP_PASS`, and `SMTP_FROM` are read from environment variables (per AR-53)
**And** in the Aspire dev environment, these are auto-injected via `WithEnvironment` pointing at the Mailpit container

### Story 2.11: Forgot Password Flow

As an unauthenticated user who cannot access my account,
I want to request a password reset via email and follow the reset link to set a new password,
So that I can regain access without admin involvement.

**Acceptance Criteria:**

**Given** any user submits an email address to `POST /api/auth/forgot-password`
**When** the endpoint processes the request
**Then** it always returns HTTP 200 with a generic message ("If that email is registered, a reset link has been sent") regardless of whether the email is registered (per AR-54 anti-enumeration)

**Given** the submitted email belongs to an active, registered user
**When** the forgot-password endpoint processes the request
**Then** a 32-byte random token is generated via `RandomNumberGenerator.GetBytes(32)` and encoded as a 64-char hex string (per AR-54)
**And** only the SHA-256 hash is stored in `password_reset_tokens` with `expires_at = now() + 1 hour`, `used_at = null`
**And** a reset-link email is dispatched asynchronously containing the raw token (e.g., `https://<host>/reset-password?token=<raw>`)
**And** SMTP failure is logged but does not affect the HTTP 200 response to the caller

**Given** a valid, unexpired, single-use reset token is submitted to `POST /api/auth/reset-password` with `{ token, newPassword }`
**When** the endpoint validates the token (SHA-256 hash match, `expires_at` not yet passed, `used_at` is null)
**Then** `passwordHash` is updated with the new bcrypt hash
**And** `used_at` is set to `now()` (token immediately invalidated)
**And** all active refresh tokens for the user are revoked (per AR-54)
**And** HTTP 200 is returned

**Given** the reset token is invalid, expired, or already used
**When** I POST `/api/auth/reset-password`
**Then** I receive HTTP 400 with `code: "RESET_TOKEN_INVALID"` and `messageKey: "auth.resetTokenInvalid"`

**Given** `newPassword` is fewer than 8 characters or is identical to the current password (bcrypt comparison)
**When** I POST `/api/auth/reset-password`
**Then** I receive HTTP 422 with a descriptive `ValidationProblemDetails` (per FR-51 AC-6)

**Given** the frontend
**When** I navigate to `/forgot-password`
**Then** a form renders with an email input, a submit button, and a success confirmation message shown after submission (per FR-51 AC-7)

**Given** I follow the reset link to `/reset-password?token=<raw>`
**When** the page loads
**Then** a form renders with new password and confirm password fields
**And** on successful submission the SPA redirects to `/login` with a success toast (per FR-51 AC-7)

### Story 2.12: Authenticated Password Change

As an authenticated user,
I want to change my password from my account settings,
So that I can update my credentials without admin involvement.

**Acceptance Criteria:**

**Given** I am authenticated and submit `PUT /api/users/me/password` with `{ currentPassword, newPassword }`
**When** the endpoint runs a bcrypt comparison of `currentPassword` against my stored `passwordHash`
**Then** on mismatch I receive HTTP 401 with `code: "CURRENT_PASSWORD_INCORRECT"` and `messageKey: "auth.currentPasswordIncorrect"` (per AR-55)

**Given** `newPassword` is fewer than 8 characters or matches `currentPassword` (bcrypt comparison)
**When** I PUT `/api/users/me/password`
**Then** I receive HTTP 422 with a descriptive `ValidationProblemDetails` (per AR-55)

**Given** `currentPassword` is correct and `newPassword` passes validation
**When** the update commits
**Then** `passwordHash` is updated to the bcrypt hash of the new password
**And** all refresh tokens for my account **except** the current session's are revoked (per AR-55)
**And** HTTP 200 is returned

**Given** the frontend
**When** I navigate to my user profile / settings area
**Then** a "Change Password" section is present with current password, new password, and confirm new password fields
**And** on success a sonner toast confirms the change and the three fields are cleared (per FR-52 AC-5)

### Story 2.13: TOTP MFA Enrolment

As a user,
I want to enrol in TOTP multi-factor authentication by scanning a QR code with an authenticator app and confirming with a one-time code,
So that my account is protected by a second factor.

**Acceptance Criteria:**

**Given** I am authenticated and call `GET /api/users/me/mfa/enrol`
**When** the endpoint responds
**Then** I receive `{ secret, qrCodeDataUrl, backupCodes[] }`
**And** `secret` is a base32-encoded TOTP secret
**And** `qrCodeDataUrl` is a `data:image/png;base64,...` QR code encoding the URI `otpauth://totp/FormForge:<email>?secret=<secret>&issuer=FormForge` (per FR-53 AC-1)
**And** `backupCodes` is an array of 8 single-use 8-character alphanumeric codes — only their bcrypt hashes are stored in `mfa_backup_codes`; the raw codes are shown to the user at this step only (per AR-56)

**Given** I submit a 6-digit TOTP code to `POST /api/users/me/mfa/verify` with `{ code }`
**When** `Otp.NET` validates the code against the pending secret with ±1 step tolerance (per AR-56)
**Then** on success: the encrypted secret is persisted to `users.mfa_secret_protected` via `IDataProtector` (purpose: `"mfa-totp-secret"`), `mfa_enabled` is set to `true`, backup code hashes are written to `mfa_backup_codes`, and HTTP 200 is returned
**And** if the code is wrong, HTTP 400 is returned and the secret is NOT persisted (enrolment guard — dangling unconfirmed secrets are prevented per AR-56)

**Given** I already have MFA enabled and call `GET /api/users/me/mfa/enrol` again
**When** I subsequently verify a new code via `POST /api/users/me/mfa/verify`
**Then** the old secret is replaced, old backup codes are invalidated, and the new secret and new backup codes are committed atomically (re-enrolment per AR-56)

**Given** the frontend
**When** I click "Enable MFA" in the Security tab of my profile
**Then** a multi-step modal opens: step 1 — QR code display and manual-entry secret; step 2 — 6-digit code input to confirm enrolment; step 3 — backup codes display with a "I have saved these codes" confirmation required before closing (per FR-53 AC-6)

### Story 2.14: TOTP MFA Verification on Login

As a user with MFA enabled,
I am prompted for a TOTP code after submitting my password,
So that a second factor is required to obtain a session.

**Acceptance Criteria:**

**Given** I POST `/api/auth/login` with valid credentials for an account where `mfa_enabled = true`
**When** the password check passes
**Then** I receive HTTP 200 `{ mfaRequired: true, mfaSessionToken: "<opaque ULID>" }` — no access or refresh token is issued at this step (per AR-56)
**And** the `mfaSessionToken` is stored in `IMemoryCache` (key → `{ userId, issuedAt }`) with a 5-minute absolute TTL

**Given** I submit `POST /api/auth/mfa/verify` with `{ mfaSessionToken, code }` where `code` is a valid 6-digit TOTP code
**When** the endpoint validates the session token (present in cache, not expired) and the TOTP code (±1 step tolerance)
**Then** I receive `{ accessToken, refreshToken, expiresIn }` and the `mfaSessionToken` is evicted from the cache (single-use per AR-56)

**Given** I submit a valid backup code in place of a TOTP code to `POST /api/auth/mfa/verify`
**When** the endpoint finds a matching `mfa_backup_codes` row for this user where `used_at` is null
**Then** the JWT pair is issued and `used_at` is set on that backup code row immediately (per AR-56)
**And** all remaining backup codes for the user are unaffected

**Given** the `mfaSessionToken` has expired (> 5 min) or does not exist in the cache
**When** I POST `/api/auth/mfa/verify`
**Then** I receive HTTP 401 with `code: "MFA_SESSION_INVALID"` and `messageKey: "auth.mfaSessionInvalid"`

**Given** I submit 5 consecutive wrong codes on the same `mfaSessionToken`
**When** the 5th failure is processed
**Then** the `mfaSessionToken` is evicted from the cache, forcing the user to restart from the password step (per FR-53 AC-4)

**Given** the frontend receives `{ mfaRequired: true, mfaSessionToken }` from the login response
**When** the SPA renders the next screen
**Then** it shows a "Two-factor authentication" screen with an autofocused 6-digit code input
**And** a "Use a backup code instead" link is present that switches the input to a plain-text backup code field (per FR-53 AC-6)

### Story 2.15: Admin MFA Reset

As a Platform Admin,
I want to disable MFA for any user account,
So that I can restore access for a user who has lost both their authenticator device and their backup codes.

**Acceptance Criteria:**

**Given** I am authenticated as a Platform Admin
**When** I DELETE `/api/admin/users/{userId}/mfa`
**Then** the user's `mfa_enabled` is set to `false`, `mfa_secret_protected` is cleared, and all rows in `mfa_backup_codes` for that user are deleted (per AR-56)
**And** all active refresh tokens for the affected user are revoked, forcing re-login without an MFA challenge (per FR-53 AC-15 / AR-56)
**And** HTTP 200 is returned

**Given** the requester does not have the `platform-admin` role
**When** they call `DELETE /api/admin/users/{userId}/mfa`
**Then** the response is HTTP 403 (per Story 2.6's `/api/admin/*` route-group guard)

**Given** I open the User detail page in Admin > Users for a user with `mfa_enabled = true`
**When** the page renders
**Then** a "Reset MFA" button is visible on the user detail view (per FR-53 AC-15 / Story A-15 AC-2)
**And** clicking it displays a confirmation dialog that must be accepted before the `DELETE` is issued

**Given** the confirmation is accepted and the reset completes
**When** the affected user next attempts to log in
**Then** the login flow proceeds to JWT issuance without any TOTP challenge (MFA is disabled for their account)

---

## Epic 3: Component Schema Designer

Platform Admins design data-entry forms visually. The designer is ported from the ESG Platform reference codebase (`C:\Users\MY\Downloads\esg_platform\BkmeaEsgPlatform\client\src\components\designer` + `pages\`) and refactored for the new stack. Admins create Designers, drag components onto a canvas, configure properties, preview, and manage versioned drafts/publishes. Keyboard-accessible DnD ensures WCAG 2.1 AA.

### Story 3.1: Port and Refactor Designer Code

As a Developer,
I want to audit and port the component designer (ComponentDesignerPage, ComponentLibraryPage, DynamicComponent, DesignerCanvas, ElementRenderer, DesignerToolbar) from the ESG Platform reference codebase, refactoring for shadcn/ui, React 19 patterns, TanStack React Query v5, and the new project structure,
So that the designer is a first-class part of FormForge with no ESG-platform-specific dependencies.

**Acceptance Criteria:**

**Given** the ESG Platform source at `C:\Users\MY\Downloads\esg_platform\BkmeaEsgPlatform\client\src\` (with the four designer components at `components\designer\` and the two pages at `pages\`)
**When** I run the port
**Then** the six files land in FormForge at `web/src/components/designer/` (canvas, DynamicComponent, ElementRenderer, DesignerToolbar) and at `web/src/routes/_app/designer.*.tsx` (page wrappers) per Architecture Decision 4.6

**Given** the ported designer
**When** I drag any of the 14 component types (Stack, Row, Tabs, Label, Button, TextInput, TextArea, NumberInput, Checkbox, Dropdown, DateTimePicker, ColorPicker, Repeater, RepeaterField, Image) from the palette onto the canvas
**Then** the component renders correctly
**And** native HTML5 drag-and-drop is preserved: drag from palette, reorder on canvas, precise insertion via DropZone between children

**Given** the ported source files
**When** I grep for ESG Platform-specific API paths, data models, or business logic
**Then** zero matches remain

**Given** the ported UI primitives
**When** I inspect imports
**Then** all UI components use shadcn/ui primitives from `web/src/components/ui/`; no prior component library imports remain

**Given** the DynamicComponent contract
**When** I exercise it with conditional visibility, Repeater row scoping, external `submitRef`, `onValidityChange`, `onReadyChange`, and shallow-equal `initialData`
**Then** every behavior from the source matches (per FR-8 AC-5)

**Given** all data fetching in the ported code
**When** I inspect TanStack React Query usage
**Then** v5 API patterns are used (object-form `useQuery({ queryKey, queryFn })`, `gcTime` not `cacheTime`, etc.); no v4 patterns remain

### Story 3.2: Create New Designer

As a Platform Admin,
I want to create a new Designer by providing a `displayName`, `designerId`, and `mode` (CRUD or VIEW),
So that I can start building a new form layout.

**Acceptance Criteria:**

**Given** I am authenticated as a Platform Admin
**When** I POST `/api/designers` with `{ displayName, designerId, mode }`
**Then** the response is HTTP 201 with the created Designer (`version: 1`, `status: Draft`)

**Given** the creation form
**When** it renders
**Then** `mode` is surfaced as a required selector (`CRUD` | `VIEW`) with no default; omitting `mode` or supplying any other value → HTTP 422 (per FR-54 / FR-9 AC-5; see Story 3.11)

**Given** an invalid `designerId` (uppercase, leading digit, hyphen, space, length > 63, reserved PG keyword, or otherwise failing regex `^[a-z_][a-z0-9_]{0,62}$`)
**When** I POST `/api/designers`
**Then** the response is HTTP 422 with `code: "IDENTIFIER_INVALID"` and a descriptive `detail` field per AR-4

**Given** a `designerId` that already exists
**When** I POST `/api/designers`
**Then** the response is HTTP 409 with `code: "DESIGNER_EXISTS"`

**Given** a Designer is created successfully
**When** I am redirected to the canvas
**Then** the canvas opens with an empty RootElement (a single `Stack` root node)

### Story 3.3: Design Canvas Interaction

As a Platform Admin,
I want to drag components from the palette onto the canvas, reorder them, nest them within structural components, and delete them,
So that I can lay out my form design.

**Acceptance Criteria:**

**Given** I am on the designer canvas
**When** the palette renders
**Then** all 14 component types are listed, categorized as Structural (Stack, Row, Tabs) and Leaf (all others)

**Given** I drag a component from the palette
**When** I drop it onto a DropZone
**Then** the component is inserted at the correct index within the parent

**Given** a component on the canvas
**When** I drag it within the same parent
**Then** it is reordered to the new position

**Given** a Leaf component on the canvas
**When** I attempt to drop another component into it
**Then** the drop is rejected (Leaf components do not accept children)

**Given** a component with children
**When** I click its delete icon
**Then** the component and all its descendants are removed from the tree

**Given** any structural change on the canvas
**When** the change settles
**Then** the canvas emits the updated RootElement JSON

### Story 3.4: Configure Component Properties

As a Platform Admin,
I want to select any component on the canvas and edit its properties in a properties panel,
So that I can define how each field behaves and is stored.

**Acceptance Criteria:**

**Given** I click a component on the canvas
**When** the properties panel opens
**Then** the panel content is specific to the component type (Dropdown shows an options editor; TextInput shows placeholder + maxLength; Repeater shows a Designer/version picker; etc.)

**Given** an input-bearing leaf component (TextInput, TextArea, NumberInput, Checkbox, Dropdown, DateTimePicker, ColorPicker, Image, RepeaterField)
**When** I edit its properties
**Then** the `fieldKey` field is required and validated as a SQL-safe identifier (per AR-4 regex)

**Given** I edit any property
**When** the property changes
**Then** the canvas preview updates immediately

**Given** the Dropdown "Source component" picker (shown when the Dropdown's options source is another component) or the Repeater "Row form — Component" picker
**When** the picker lists candidate components
**Then** only `CRUD`-mode components appear; `VIEW`-mode components are excluded (per FR-54 AC-5 / Architecture Decision 4.11), and a server-side guard independently rejects a VIEW reference on save → HTTP 422

### Story 3.5: Designer Live Preview

As a Platform Admin,
I want to toggle a preview mode that renders the current design as a DynamicComponent,
So that I can validate the form experience before publishing.

**Acceptance Criteria:**

**Given** I am on the canvas with a non-empty design
**When** I toggle preview mode
**Then** the canvas renders the design via the same `DynamicComponent` used in production data entry (not a separate renderer)

**Given** preview is active
**When** I fill in fields and tap any submit affordance
**Then** no data is submitted (preview is read-only)

**Given** the design has conditional visibility rules
**When** I change input values in preview
**Then** visibility behaves as it would in the live form (visibility engine active)

**Given** a Repeater section exists
**When** I am in preview
**Then** the section shows an "Add row" control

### Story 3.6: Save Designer Version

As a Platform Admin,
I want to save the current canvas state, creating a new version,
So that I have an immutable snapshot of the design.

**Acceptance Criteria:**

**Given** I am on the canvas with valid design content
**When** I POST `/api/designers/{id}/versions`
**Then** a new version is created with `rootElement` (current canvas JSON), auto-incremented `version`, `status: Draft`, `createdAt`, `createdBy`
**And** previous versions are not mutated
**And** the save completes within 500 ms (p95) per NFR-2

**Given** any input-bearing component has an empty `fieldKey`
**When** I attempt to save
**Then** save is blocked with an inline validation error identifying the offending component (per FR-13 AC-4)

**Given** the saved version's RootElement
**When** I retrieve it via `GET /api/designers/{id}/versions/{version}`
**Then** the persisted JSON exactly matches the canvas state at save time

### Story 3.7: Version Status Management

As a Platform Admin,
I want to promote a version from Draft to Published, or Archive a Published version,
So that I control which version is used in production bindings.

**Acceptance Criteria:**

**Given** a Draft version exists
**When** I PUT `/api/designers/{id}/versions/{version}/status` to `Published`
**Then** the version becomes Published
**And** if a previously Published version existed for this Designer, it is auto-demoted to Archived (at most one Published per Designer invariant)
**And** the `SchemaPublished` domain event fires (per AR-7 / AR-47)

**Given** an Archived version exists
**When** I attempt to re-Publish it
**Then** the action succeeds (Archived versions are queryable and can be the basis for a new version)

**Given** a Menu Binding pinned to `designerId@vN`
**When** vN is Archived
**Then** the binding continues functioning (pinned bindings survive archival)

**Given** an attempt to bind a Menu Item to a Draft version (this AC validates the constraint here even though the bind action lives in Epic 5)
**When** the bind endpoint receives a Draft version
**Then** the response is HTTP 422 with `code: "VERSION_NOT_PUBLISHED"`

### Story 3.8: Designer Library Page

As a Platform Admin,
I want to browse all Designers, view their versions and statuses, preview any version, and manage version lifecycle,
So that I have a central catalog of all form definitions.

**Acceptance Criteria:**

**Given** I navigate to the Designer Library
**When** the page renders
**Then** I see a paginated list of Designers: Display Name, Designer ID, Mode (CRUD/VIEW badge), Current Version, Status, Last Updated, Creator (per FR-54 / FR-14 AC-1)

**Given** any row in the Library
**When** I view the per-row actions
**Then** I see: Open in Canvas (specific version), New Version (modal with auto-incremented number), Duplicate, Archive

**Given** I click the version flyout on a row
**When** the flyout opens
**Then** all versions appear with status badge and relative timestamp

**Given** I open the Preview action on any version
**When** the modal opens
**Then** the `DynamicComponent` renders that version in-place (read-only)

### Story 3.9: DynamicComponent Renderer for Data Entry

As the system,
I render any bound Designer version as a live, submittable form for end-users,
So that data entry requires no per-module custom code.

**Acceptance Criteria:**

**Given** an authenticated user opens a record entry view
**When** the page needs the schema
**Then** `DynamicComponent` fetches the RootElement from `GET /api/designers/{designerId}/versions/{version}` via TanStack Query with `staleTime: 300_000` (5 min)

**Given** the fetched RootElement
**When** `DynamicComponent` renders it
**Then** all 14 component types render as appropriate input controls

**Given** the form has visibility conditions
**When** the user changes input values
**Then** subtrees correctly show or hide based on `computeVisibility` evaluated against the current form values

**Given** a Repeater section exists
**When** the user interacts with it
**Then** child rows can be added, edited, and removed

**Given** the user submits the form via the external Save button (per AR-35)
**When** `submitRef.current()` is invoked
**Then** `onSave(payload)` is called with collected form values, excluding hidden fields and undefined values

### Story 3.10: Keyboard-Accessible Designer DnD

As a Platform Admin using only a keyboard,
I want to interact with the designer canvas (and reuse the same hook for menu reorder in Epic 4) via keyboard,
So that the designer is usable without a pointing device and FormForge meets WCAG 2.1 AA (FR-42 AC-4).

**Acceptance Criteria:**

**Given** I focus a draggable component in the palette or canvas
**When** I press Tab to reach it
**Then** the focused element receives a visible focus outline

**Given** a draggable component has focus
**When** I press Space or Enter
**Then** the component is "picked up" and the SPA announces the pickup via `aria-live="polite"`

**Given** a component is picked up
**When** I press arrow keys
**Then** focus moves through valid drop targets (DropZones); each DropZone has a descriptive `aria-label`

**Given** focus is on a valid drop target
**When** I press Space or Enter
**Then** the component is inserted at that position and the SPA announces the drop
**And** the canvas emits the same updated RootElement JSON it would have emitted via HTML5 DnD

**Given** a component is picked up
**When** I press Escape
**Then** the pickup is cancelled and no canvas state changes

**Given** the `useKeyboardDnD()` hook in `web/src/components/designer/`
**When** the menu reorder UI uses it (Epic 4 reuses this hook)
**Then** the same keyboard interaction model applies to menu item reordering

**Given** the rendered designer
**When** an axe-core audit runs in CI
**Then** zero critical violations are reported

### Story 3.11: Declare and Enforce Component Mode (CRUD / VIEW)

As a Platform Admin,
I want to set a component's mode to CRUD or VIEW at creation,
So that display-only components don't create database tables and data-bearing components do.

> Covers FR-54. Source: PRD FR-54 / Story B-10; Architecture Decisions 1.8, 4.10, 4.11. This story owns the mode contract; Stories 3.2, 3.4, 3.8 (Designer surface), Story 5.x (provisioning skip), and Story 6.9 (read-only data entry) carry the mode-aware ACs that depend on it.

**Acceptance Criteria:**

**Given** I POST `/api/designers`
**When** the payload omits `mode` or supplies a value other than `CRUD` | `VIEW`
**Then** the response is HTTP 422 (`mode` is required; FR-54 AC-1)

**Given** a Designer is created
**When** the record is persisted
**Then** `mode` is stored on `component_schemas.mode` (`TEXT NOT NULL`, `CHECK (mode IN ('CRUD','VIEW'))`, per Architecture Decision 1.8)
**And** the column ships in the EF Core migration for `component_schemas`, with existing rows backfilled to `'CRUD'` in the same transaction (FR-54 AC-6)

**Given** an existing Designer
**When** any update path attempts to change its `mode`
**Then** the API rejects the change → HTTP 422 (mode is immutable for the life of the component; FR-54 AC-2)

**Given** a `VIEW`-mode component is bound to a Menu Item
**When** the binding is saved
**Then** Table Provisioning is skipped entirely — no CREATE/ALTER TABLE runs and no `schema_audit_log` DDL entry is written; `provisioningStatus` is `NotApplicable` (FR-54 AC-3 / Decision 1.8; see Epic 5)

**Given** a `VIEW`-mode component bound to a Menu Item
**When** an end-user opens it
**Then** it renders read-only via DynamicComponent — no record list, no "New Record" control, no submit/save — and no `/api/data/{designerId}` endpoint is exposed; a request to such an endpoint returns HTTP 404 `{ code: "TABLE_NOT_PROVISIONED" }` (FR-54 AC-4 / Decisions 1.8, 4.10; see Story 6.9)

**Given** a `VIEW`-mode component
**When** access is evaluated
**Then** the per-Resource CRUD flags (FR-2) are not consulted; access is governed solely by the bound Menu Item's `allowedRoles` (FR-19 / FR-54 / Decision 2.2 extension)

**Given** the Dropdown "Source component" picker and the Repeater "Row form — Component" picker
**When** they list candidate components
**Then** only `CRUD`-mode components appear; `VIEW`-mode components are excluded, and a server-side guard rejects a VIEW reference on save → HTTP 422 (FR-54 AC-5 / Decision 4.11)

---

## Epic 4: Menu Management

Platform Admins configure the navigation structure. Menu Items live in a 2-level hierarchy with icons, role-based visibility, ordering, and isActive toggles. The dynamic navbar renders a permission-filtered view for each user. **Schema Binding (FR-17) is intentionally deferred to Epic 5** — this epic establishes the navigation shape; Epic 5 wires it to backing tables.

### Story 4.1: Create and Manage Top-Level Menu Items

As a Platform Admin,
I want to create, edit, and delete top-level Menu Items,
So that I can define the platform's navigation structure.

**Acceptance Criteria:**

**Given** I am authenticated as a Platform Admin
**When** I POST `/api/admin/menus` with `{ name, order, icon?, isActive? }` (no `parentId`)
**Then** a top-level Menu Item is created with `isActive` defaulting to `true`

**Given** a top-level Menu Item with active sub-menu children
**When** I DELETE it
**Then** the response is HTTP 409 with `messageKey: "menus.hasChildren"` instructing the admin to remove children first

**Given** any Menu Item without a Schema Binding
**When** the navbar renders
**Then** the item appears as a section header (not a data view link)

**Given** two top-level items with the same `order` value
**When** the navbar renders
**Then** they appear in stable insertion order; gaps in the `order` integer sequence are permitted

### Story 4.2: Create Sub-menu Items

As a Platform Admin,
I want to nest a Menu Item one level under a parent,
So that I can group related data modules under a section heading.

**Acceptance Criteria:**

**Given** an existing top-level Menu Item
**When** I POST `/api/admin/menus` with `{ name, order, parentId: <topLevelId> }`
**Then** a sub-menu item is created under that parent

**Given** a sub-menu item
**When** I attempt to POST another item with `parentId` pointing to that sub-menu (attempt at 3rd level)
**Then** the response is HTTP 422 with `code: "MAX_MENU_DEPTH_EXCEEDED"`

### Story 4.3: Assign Icon to Menu Item

As a Platform Admin,
I want to assign a lucide-react icon name or upload an image as a Menu Item icon,
So that the navbar is visually navigable.

**Acceptance Criteria:**

**Given** a Menu Item being edited
**When** I set its icon to a lucide icon (`{ type: "lucide", name: "HomeIcon" }`)
**Then** the icon name is validated against available lucide-react icons; invalid names return HTTP 422

**Given** I upload an icon image
**When** I POST a PNG, JPG, or SVG file ≤2 MB to `/api/admin/menus/upload-icon` (multipart)
**Then** the file is validated for MIME type and size per NFR-7
**And** the file is stored in MinIO under the `menus/icons/` path prefix (per AR-26 bucket layout)
**And** the response returns `{ type: "minio", objectKey: "menus/icons/{uuid}.{ext}" }`

**Given** I upload an oversized or wrong-type file
**When** the upload endpoint processes it
**Then** the response is HTTP 422 with `code: "UPLOAD_INVALID"` and the file is not persisted

**Given** a Menu Item with no icon (`icon: null`)
**When** the navbar renders
**Then** a default placeholder icon is shown

### Story 4.4: Assign Roles to Menu Item

As a Platform Admin,
I want to configure which Roles can access a Menu Item,
So that only authorized users see and interact with that data module.

**Acceptance Criteria:**

**Given** a Menu Item being edited
**When** I save its `allowedRoles: [roleId, ...]` array
**Then** the assignment persists
**And** a user sees the Menu Item in the navbar only if at least one of their Roles is in `allowedRoles` (FR-19 AC-2)
**And** the `MenuBindingCreated` event fires when a new Resource is introduced (per AR-11 — used by the role-editor matrix in Story 2.9)

**Given** the server enforces dual-gating on data endpoints
**When** a request reaches `/api/data/{designerId}/*`
**Then** the server checks `allowedRoles` membership *in addition to* CRUD-flag evaluation (FR-19 AC-3) — this AC validates the contract here; Epic 6 implements the check in handler code

### Story 4.5: Reorder Menu Items via Drag-and-Drop

As a Platform Admin,
I want to reorder Menu Items and sub-menu items by drag-and-drop in the menu editor,
So that the navbar presents items in the correct order.

**Acceptance Criteria:**

**Given** I am in the menu editor
**When** I drag a Menu Item to a new position
**Then** the SPA calls `PUT /api/admin/menus/reorder` with `[{ id, order }]` payload and the new order persists

**Given** a sub-menu item
**When** I drag it within its parent
**Then** it reorders
**And** when I drag it outside its parent's sub-menu list (attempt to promote to top-level)
**Then** the drop is rejected client-side; no API call is made

**Given** the menu editor's keyboard reorder flow
**When** an admin uses keyboard-only interaction (via the `useKeyboardDnD` hook reused from Story 3.10)
**Then** the same reorder semantics apply via Tab/Space/Arrow/Escape keys

**Given** menus are reordered
**When** any user's navbar refreshes
**Then** the new order propagates within ≤5 s (FR-20 AC-3) via the 5 s navbar cache TTL (per NFR-3) or write-time invalidation

### Story 4.6: Activate and Deactivate Menu Items

As a Platform Admin,
I want to toggle a Menu Item's `isActive` flag,
So that I can hide items without deleting them.

**Acceptance Criteria:**

**Given** a Menu Item with `isActive: true`
**When** I toggle it to `false`
**Then** the item is excluded from the navbar for all users (including Platform Admins browsing in normal view)

**Given** a Platform Admin in Admin > Menus
**When** they view the menus list
**Then** inactive items remain visible and editable from that admin page

### Story 4.7: Render Permission-Filtered Navigation

As an authenticated user,
I want to see a dynamic left-side navbar showing only the Menu Items my Roles authorize me to view,
So that I am not presented with inaccessible options.

**Acceptance Criteria:**

**Given** I am logged in
**When** the SPA fetches `GET /api/menus`
**Then** the response contains only items where `isActive = true` AND my roles intersect `allowedRoles`
**And** the response p95 is <100 ms per NFR-3 (served from a 5 s in-memory cache)

**Given** the navbar renders
**When** I view it
**Then** top-level items and their sub-items appear in `order` sequence

**Given** I am on a viewport <768 px
**When** the navbar renders
**Then** it collapses to a hamburger menu (per FR-37 AC-2)
**And** tapping any item auto-closes the nav

**Given** menus are mutated server-side (created, reordered, deleted, role-reassigned, isActive toggled)
**When** the cache's write-time invalidation fires
**Then** the next `GET /api/menus` reflects the change

---

## Epic 5: Dynamic Table Provisioning

Binding a Published Designer version to a Menu Item provisions a real PostgreSQL table. Schema evolves additively across versions; Repeater components produce child tables with FKs; orphaned columns are visible and droppable on demand. Every DDL event is audited. In-flight provisioning jobs survive process restart.

### Story 5.1: Validate designerId as a Safe PostgreSQL Identifier

As the system,
I validate any `designerId` before using it as a table name,
So that DDL statements cannot be constructed with unsafe input.

**Acceptance Criteria:**

**Given** a `designerId` value entering the validation pipeline
**When** it is checked against the regex `^[a-z_][a-z0-9_]{0,62}$` (lowercase letters, digits, underscores; starts with a letter or underscore; 1–63 characters) (per AR-4)
**Then** invalid values are rejected with HTTP 422, `code: "IDENTIFIER_INVALID"`

**Given** a `designerId` that matches the regex but appears in the hardcoded PostgreSQL 17 reserved-keyword list (per AR-4)
**When** it is validated
**Then** the response is HTTP 422 with `code: "IDENTIFIER_RESERVED_KEYWORD"`

**Given** any code path that composes SQL with a dynamic identifier
**When** it builds the SQL
**Then** the identifier must pass through the `SafeIdentifier` value type (per AR-4); raw strings are never interpolated
**And** a Roslyn analyzer (or code-review pattern check) flags any direct string interpolation of a `designerId` or `fieldKey` into SQL

**Given** validation runs at Designer creation (Story 3.2) and at every DDL emit and dynamic-CRUD identifier substitution
**When** any layer encounters an invalid identifier
**Then** the request is rejected at that layer (defense in depth — FR-23 AC-4)

### Story 5.2: Bind Designer Version to Menu Item

As a Platform Admin,
I want to bind a specific Published Designer version to a Menu Item,
So that the platform provisions the backing table and connects the CRUD UI.

**Acceptance Criteria:**

**Given** a Menu Item and a Published Designer version
**When** I PUT `/api/admin/menus/{menuId}/binding` with `{ designerId, version }`
**Then** the binding is saved with `provisioningStatus: Pending`
**And** the response is HTTP 202 (per AR-23)
**And** a `ProvisioningJob` is enqueued on `Channel<ProvisioningJob>` (per AR-9 / AR-37)

**Given** a binding whose Designer is `VIEW`-mode (per FR-54 / Story 3.11)
**When** the bind request is processed
**Then** Table Provisioning is skipped entirely — no `ProvisioningJob` is enqueued, no DDL runs, and no `schema_audit_log` entry is written; `provisioningStatus` is set to `NotApplicable` and surfaced in the admin UI as "Not applicable (view-only)" (per Architecture Decision 1.8)

**Given** an attempt to bind to a Draft or Archived version (only Published bindable)
**When** the bind request is processed
**Then** the response is HTTP 422 with `code: "VERSION_NOT_PUBLISHED"` (validating the contract from Story 3.7)

**Given** the admin SPA after a bind request
**When** the SPA polls the Menu Item's binding state
**Then** the provisioningStatus transitions Pending → Success or Pending → Error, and a sonner toast announces the transition (per AR-29)

**Given** a binding's provisioning failed
**When** the admin clicks Retry
**Then** the binding is re-enqueued without changing the binding values themselves; provisioningStatus resets to Pending

**Given** I update the bound version (e.g., v1 → v2)
**When** I PUT `/api/admin/menus/{menuId}/binding` with the new version
**Then** the same async pipeline runs; an ALTER TABLE migration is triggered by Story 5.4

**Given** the binding-diff preview endpoint
**When** I GET `/api/admin/menus/{menuId}/binding-diff?targetVersion={N}` (per AR-23)
**Then** I receive `{ currentBinding, targetVersion, columnsToAdd, columnsAlreadyPresent, orphanedColumns, willTriggerChildProvisioning, estimatedDdl }` for the admin diff modal

### Story 5.3: Provision New Table from Designer Schema

As the system,
I `CREATE TABLE` when a Designer is bound to a Menu Item for the first time,
So that a real PostgreSQL table backs the data module.

**Acceptance Criteria:**

**Given** a `ProvisioningJob` for a `{ designerId, version }` that has not been provisioned before
**When** the `ProvisioningBackgroundService` consumer dequeues the job (per AR-9)
**Then** a PostgreSQL `CREATE TABLE` is issued via Dapper in an explicit transaction, with table name = `designerId`, system columns (`id UUID PRIMARY KEY DEFAULT gen_random_uuid()`, `created_at TIMESTAMPTZ DEFAULT now()`, `created_by UUID REFERENCES users(id)`, `updated_at TIMESTAMPTZ`, `updated_by UUID REFERENCES users(id)`, `is_deleted BOOLEAN DEFAULT false`, `cascade_event_id UUID NULL` per AR-6), and per-fieldKey columns mapped per the AR-5 component-type table (TextInput/TextArea/Dropdown/ColorPicker/Image → TEXT; NumberInput → NUMERIC; DateTimePicker → TIMESTAMPTZ; Checkbox → BOOLEAN; structural/Label/Button/RepeaterField → no column; unknown → JSONB)

**Given** any generated column
**When** the DDL is composed
**Then** the column is nullable (no `NOT NULL` constraint, per FR-24 AC-3)

**Given** the transaction wrapping the DDL
**When** any statement fails
**Then** the entire transaction rolls back and `provisioningStatus` flips to `Error` with `provisioningError` populated; no partial schema is left in PG (per NFR-11)

**Given** the target table already exists (idempotent edge case)
**When** the provisioner runs
**Then** it falls through to ALTER TABLE logic (Story 5.4) and the operation is a no-op if the schema already matches

**Given** a successful CREATE TABLE
**When** the transaction commits
**Then** a row is appended to `schema_audit_log` with `actorId`, `timestamp`, `designerId`, `fromVersion: null`, `toVersion: version`, `ddlOperation: "CREATE"`, `columnsAdded: [...]`, `correlationId` (per AR-8 / AR-24)
**And** the schema registry entry for `(designerId, version)` is populated for future CRUD use (per AR-7)

### Story 5.4: Evolve Schema with a New Designer Version

As the system,
I `ALTER TABLE ... ADD COLUMN` for each new field in an updated Designer version,
So that existing records are never broken by schema changes.

**Acceptance Criteria:**

**Given** an updated `{ designerId, version }` binding
**When** the `ProvisioningBackgroundService` consumer processes it and a table already exists
**Then** the provisioner computes the diff between the target version's RootElement and the live table's column list
**And** new columns (present in target but absent from table) are added via `ALTER TABLE ... ADD COLUMN ... NULL` (no default), in a single transaction

**Given** the diff computation
**When** existing columns are present in the table but absent from the target schema
**Then** they are **never** dropped or renamed automatically (FR-25 AC-1)

**Given** partial completion of an ALTER
**When** any sub-statement fails
**Then** the transaction rolls back entirely (FR-25 AC-3)

**Given** a successful ALTER
**When** the transaction commits
**Then** a row is appended to `schema_audit_log` with `actorId`, `timestamp`, `designerId`, `fromVersion`, `toVersion`, `ddlOperation: "ALTER"`, `columnsAdded[]`, `columnsDiff` (JSON before/after), `correlationId` (per FR-25 AC-4 / AR-8 / AR-24)
**And** the schema registry entry for the new `(designerId, version)` is populated/refreshed

### Story 5.5: Provision Child Tables for Repeater Components

As the system,
I recursively provision the child Designer's table and add a foreign-key column when a Repeater component is encountered,
So that one-to-many relationships are correctly modeled.

**Acceptance Criteria:**

**Given** a parent Designer's RootElement contains a Repeater with `{ designerId: childId, version: childVersion }`
**When** the parent is provisioned (CREATE or ALTER)
**Then** the child table is provisioned (if not already) in the same transaction as the parent (FR-27 AC-5)
**And** the child table gains a column `parent_{parentDesignerId}_id UUID REFERENCES {parentDesignerId}(id) ON DELETE CASCADE` (FR-27 AC-2)
**And** an index is created: `CREATE INDEX IF NOT EXISTS idx_{childDesignerId}_parent ON {childDesignerId}(parent_{parentDesignerId}_id)` (FR-27 AC-3)

**Given** a circular Repeater reference (Designer A's Repeater points to Designer B, whose Repeater points to A)
**When** the provisioner runs cycle detection (DFS on the Repeater reference graph, per AR-52 / FR-27 AC-4)
**Then** the binding is rejected at request time with HTTP 422 and `code: "REPEATER_CYCLE"`
**And** no DDL is executed

**Given** Repeater nesting deeper than one level (A → B → C)
**When** the provisioner walks the graph
**Then** all descendants are provisioned in the same transaction; depth is bounded by cycle detection

### Story 5.6: Admin Schema Drift View

As a Platform Admin,
I want to view orphaned columns (in the DB but not in the current Designer schema),
So that I can make informed decisions about manual cleanup.

**Acceptance Criteria:**

**Given** I navigate to Admin > Designers > [Designer] > Schema Drift
**When** the view loads
**Then** I see each orphaned column with: column name, PG data type, estimated non-null row count

**Given** I click "Drop Column" on an orphaned column
**When** the confirmation dialog opens
**Then** the dialog text reads "This will permanently delete all data in this column" and I must confirm explicitly to proceed

**Given** I confirm a drop
**When** the server executes `ALTER TABLE {designerId} DROP COLUMN {col}`
**Then** the operation runs in an explicit transaction
**And** on success, `schema_audit_log` records `ddlOperation: "DROP"`, `columnsDropped: [{col}]`, `correlationId`
**And** the schema registry entry for affected versions is invalidated

**Given** the drift view is open
**When** I click Refresh
**Then** the orphaned column list re-queries on demand

### Story 5.7: View Schema Audit Log

As a Platform Admin,
I want to view the schema change audit log for any Designer,
So that I have full traceability of DDL history.

**Acceptance Criteria:**

**Given** I am authenticated as a Platform Admin
**When** I GET `/api/admin/designers/{designerId}/audit?page=1&pageSize=25`
**Then** I receive a `PagedResult<SchemaAuditEntry>` (per AR-21)
**And** each entry contains `id`, `timestamp`, `actorId`, `actorName`, `designerId`, `fromVersion`, `toVersion`, `ddlOperation`, `columnsAdded`, `columnsDropped`, `correlationId`, `notes`

**Given** the audit log table
**When** I inspect database constraints and exposed APIs
**Then** no API endpoint allows deletion of `schema_audit_log` rows (append-only per NFR-9 / FR-28 AC-3)

**Given** the audit table at any volume
**When** queries run against it
**Then** indexes `(designer_id, created_at DESC)` and `correlation_id` are present (per AR-8)

### Story 5.8: Provisioning Recovery on Restart

As the system,
I re-enqueue all in-flight provisioning jobs on API startup,
So that schema bindings whose provisioning was interrupted by a restart can complete.

**Acceptance Criteria:**

**Given** the API process starts
**When** `ProvisioningRecoveryService` initializes
**Then** it queries `SELECT * FROM menus WHERE provisioningStatus = 'Pending'`
**And** for each result, it constructs a `ProvisioningJob` and writes it to `Channel<ProvisioningJob>`

**Given** a recovered job runs
**When** the consumer processes it
**Then** the same Story 5.3 / 5.4 / 5.5 logic applies — the job is idempotent (CREATE falls through to ALTER if table exists)

**Given** the recovery service is implemented
**When** I run an integration test that submits a binding, force-kills the API process before the consumer drains the channel, then restarts the API
**Then** the binding completes provisioning to `Success` after restart

---

## Epic 6: Generic CRUD Service & Data Entry

End-users create, read, update, and soft-delete records through a generic, permission-gated UI. The CRUD service serves any provisioned table via Dapper with whitelisted filter/sort, parameterized payloads, and Repeater-aware nested writes. The user-facing surface — data entry forms (DynamicComponent), record lists, and standard loading/empty/error states — ships in the same epic because it has no value without the endpoints, and the endpoints have no observable surface without it.

### Story 6.1: List Records with Pagination, Filtering, and Sorting

As an authorized user with `canRead`,
I want to retrieve a paginated, filtered, and sorted list of records from any dynamic table,
So that I can browse and find data efficiently.

**Acceptance Criteria:**

**Given** a provisioned table for `designerId`
**When** I GET `/api/data/{designerId}?page=1&pageSize=25&sort=created_at:desc&filter[title]=foo`
**Then** the response is `PagedResult<DynamicRecord>` with `data`, `total`, `page`, `pageSize` (per AR-21)
**And** the response p95 is <200 ms at 100k rows per NFR-1 (with indexes on `id`, `created_at`, `is_deleted`, and common filter columns)
**And** the records are serialized per AR-46 (Option C hybrid: system columns translated to camelCase, user fieldKeys preserved verbatim)

**Given** the `sort` parameter
**When** the server parses it
**Then** up to 3 `column:direction` pairs are accepted; columns are whitelisted against the schema registry's column list + system columns (`id`, `created_at`, `updated_at`, `is_deleted`) — unknown columns return HTTP 422

**Given** the `filter` parameter
**When** the server parses it
**Then** filter keys are whitelisted against the schema registry; values are parameterized via Dapper — never interpolated into SQL

**Given** a request for an unprovisioned table
**When** I GET `/api/data/{designerId}`
**Then** the response is HTTP 404 with `code: "TABLE_NOT_PROVISIONED"`

**Given** the default behavior
**When** the list query runs
**Then** soft-deleted records are included (the consumer filters via `filter[isDeleted]=false` if desired)

**Given** the dynamic CRUD database connection
**When** any query runs
**Then** Npgsql `CommandTimeout` is 5 seconds (per AR-9 query-timeout mitigation for NFR-6 / PRD R-6)

### Story 6.2: Get a Single Record with Optional Children

As an authorized user with `canRead`,
I want to retrieve a single record by ID, optionally including its Repeater child records,
So that I can view a complete entry.

**Acceptance Criteria:**

**Given** a provisioned table and a record ID
**When** I GET `/api/data/{designerId}/{id}`
**Then** the response is HTTP 200 with the record, or HTTP 404 if not found

**Given** I request `?include=children`
**When** the server processes the request
**Then** the response shape includes `children: { [childDesignerId]: [...] }` for every Repeater referenced in the schema

**Given** a soft-deleted record
**When** I GET it
**Then** the response includes `isDeleted: true` so the UI can show a "Deleted" indicator (per FR-30 AC-3)

### Story 6.3: Create a New Record

As an authorized user with `canCreate`,
I want to submit a new record payload,
So that a new row is inserted into the provisioned table.

**Acceptance Criteria:**

**Given** a provisioned table for `designerId` and a payload
**When** I POST `/api/data/{designerId}` with the payload
**Then** the payload is validated by the Layer 2 `IDynamicPayloadValidator` (per AR-20) — known fieldKeys are type-checked against `ColumnDefinition.PgType`; unknown fields are ignored (FR-31 AC-2)
**And** system columns (`id`, `created_at`, `created_by`, `is_deleted`, `cascade_event_id`) are set server-side; client cannot supply them
**And** the response is HTTP 201 with the created record including `id`

**Given** the record is created
**When** the transaction commits
**Then** a row is appended to `mutation_audit_log` with `actorId`, `timestamp`, `designerId`, `recordId`, `operation: "CREATE"`, `newValues`, `correlationId`

**Given** the rate-limiting policy (per AR-15)
**When** I exceed 60 POST `/api/data/*` requests per user per minute
**Then** I receive HTTP 429 with `Retry-After`

### Story 6.4: Update a Record (Partial)

As an authorized user with `canUpdate`,
I want to submit a partial payload that overwrites only the supplied fields,
So that I can correct a record without a full replace.

**Acceptance Criteria:**

**Given** an existing record and a partial payload
**When** I PUT `/api/data/{designerId}/{id}` with `{ field1: newVal }` only
**Then** only fields present in the payload (non-null, non-undefined values) are updated
**And** `updated_at` and `updated_by` are set server-side on every successful update

**Given** a soft-deleted record
**When** I attempt to PUT against it
**Then** the response is HTTP 422 with `code: "RECORD_DELETED"` (restore first per Story 6.6)

**Given** the update commits
**When** the transaction completes
**Then** a row is appended to `mutation_audit_log` with `operation: "UPDATE"`, `previousValues` (changed fields only), `newValues`, `correlationId`

### Story 6.5: Soft-Delete a Record

As an authorized user with `canDelete`,
I want to soft-delete a record,
So that the data is preserved and recoverable.

**Acceptance Criteria:**

**Given** a record
**When** I DELETE `/api/data/{designerId}/{id}`
**Then** the record's `is_deleted = true`, `updated_at = now()`, `updated_by = requestingUserId` are set
**And** the response is HTTP 200 with the updated record (`isDeleted: true`)

**Given** the schema registry indicates Repeater children
**When** the soft-delete cascade walks the Repeater graph (per AR-6 / FR-33 AC-3)
**Then** a single `cascade_event_id` UUID is generated and set on the parent and every transitively-reached child row, all within one transaction

**Given** the cascade commits
**When** the transaction completes
**Then** a row is appended to `mutation_audit_log` with `operation: "SOFT_DELETE"`, `correlationId`

### Story 6.6: View and Restore Soft-Deleted Records

As a Platform Admin,
I want to view soft-deleted records and restore them,
So that accidental deletions can be recovered.

**Acceptance Criteria:**

**Given** I am a Platform Admin and a soft-deleted record
**When** I PUT `/api/data/{designerId}/{id}/restore`
**Then** the parent record's `is_deleted = false`, `updated_at = now()`, `updated_by = requestingUserId` are set
**And** all child Repeater rows whose `cascade_event_id` matches the parent's last cascade event are restored in the same transaction (per AR-6 cascade-event-id semantics)
**And** children that were individually soft-deleted before the parent's cascade (`cascade_event_id NULL` or different) are NOT incidentally restored

**Given** the admin record list view
**When** I toggle "Show deleted"
**Then** the list includes soft-deleted records (visually marked)

**Given** the restore commits
**When** the transaction completes
**Then** `mutation_audit_log` records `operation: "RESTORE"`, `correlationId`

### Story 6.7: Create/Update Parent and Child Records in One Transaction

As a Content Editor,
I want to submit a form with Repeater sections in a single save,
So that parent and child data land atomically.

**Acceptance Criteria:**

**Given** a POST payload with `{ ...parentFields, children: { [childDesignerId]: [...childRecords] } }`
**When** the server processes the request
**Then** the parent record is inserted first; each child record receives the parent's `id` as its `parent_{parentDesignerId}_id` FK value
**And** all inserts run in a single database transaction; any failure rolls back parent and all children

**Given** a PUT payload including `children`
**When** the server processes the request
**Then** for each child: if it has an `id` and is present → UPDATE; if it has no `id` → INSERT; if a child previously linked to this parent is *omitted* from the payload → SOFT-DELETE (per A13 / FR-35 AC-4)
**And** all child operations run in the same transaction as the parent update

### Story 6.8: View CRUD Mutation Audit Log

As a Platform Admin,
I want to view the mutation audit log for any dynamic table,
So that I have full traceability of who changed data and when.

**Acceptance Criteria:**

**Given** I am a Platform Admin
**When** I GET `/api/admin/data/{designerId}/audit?page=1&pageSize=25`
**Then** I receive a `PagedResult<MutationAuditEntry>` (per AR-21)
**And** each entry contains `id`, `timestamp`, `actorId`, `actorName`, `designerId`, `recordId`, `operation`, `previousValues`, `newValues`, `correlationId`

**Given** the audit log is stored in `mutation_audit_log` (EF Core-managed per FR-36 AC-4 / AR-8)
**When** I inspect APIs
**Then** no endpoint allows deletion (append-only per NFR-10)
**And** indexes `(designer_id, created_at DESC)`, `(record_id, created_at DESC)`, and `correlation_id` are present

### Story 6.9: Dynamic Data Entry View

As a Content Editor,
I want to open any Menu Item and see a record list plus a DynamicComponent form for creating new records,
So that I can enter data into any dynamically provisioned module.

**Acceptance Criteria:**

**Given** I navigate to a Menu Item with a Schema Binding
**When** the route `data.$designerId.tsx` loads
**Then** I see a paginated record list AND a "New Record" button
**And** the "New Record" button is visible only if I have `canCreate` on this Resource (per Story 2.7)

**Given** I click "New Record"
**When** the new-record route opens
**Then** a DynamicComponent form renders using the bound `{ designerId, version }` schema (consuming Story 3.9)
**And** an external Save button on the page invokes `submitRef.current()` (per AR-35)

**Given** I click a record row
**When** the detail route opens
**Then** the record renders in DynamicComponent in read-only mode if I lack `canUpdate`; in edit mode if I have `canUpdate`

**Given** I click Delete on a record (visible only if `canDelete`)
**When** I confirm the dialog
**Then** Story 6.5's soft-delete fires and the record disappears from the active list (optimistic update per AR-49)

**Given** the form save succeeds
**When** the mutation completes
**Then** a sonner success toast appears (per AR-29) and TanStack Query invalidates `['data', designerId]` keys (per AR-48)

**Given** the bound component is `VIEW`-mode (per FR-54 / Story 3.11)
**When** the route loads
**Then** the page renders the DynamicComponent read-only — no record list, no "New Record" button, no submit/save controls — and makes no `/api/data/{designerId}` calls (the endpoints do not exist for VIEW; a direct call returns HTTP 404 `{ code: "TABLE_NOT_PROVISIONED" }`, per Decisions 1.8, 4.10)

### Story 6.10: Paginated, Filterable, Sortable Record List

As a Viewer,
I want to view a paginated list of records, filter by column values, and sort by multiple columns,
So that I can find data quickly.

**Acceptance Criteria:**

**Given** a record list page
**When** the table renders
**Then** columns are derived from the bound schema's `fieldKeys`; system columns (`id`, `createdAt`, `updatedAt`) are optionally displayable

**Given** I click a column header
**When** the click fires
**Then** sort direction cycles (none → asc → desc → none) and the list re-fetches
**And** shift-click adds a secondary sort column

**Given** the filter bar above the table
**When** I type into a per-column input
**Then** the URL search params update via TanStack Router's `validateSearch` Zod schema (per Architecture minor-gap #8)
**And** the list re-fetches via `useRecordList(designerId, searchParams)`

**Given** the pagination control
**When** I change page or page size (10/25/50)
**Then** the URL search params update and the list re-fetches
**And** the total count is displayed

**Given** any soft-deleted record in the visible page
**When** the row renders
**Then** it shows a visual indicator (strikethrough row or "Deleted" badge per FR-40 AC-5)

### Story 6.11: Explicit UI States for All Data Operations

As any user,
I want to always tell when the system is loading, a list is empty, or an error occurred,
So that I am never left staring at a blank screen.

**Acceptance Criteria:**

**Given** any TanStack Query is in-flight
**When** it has not yet resolved
**Then** the UI shows a shadcn `Skeleton` placeholder for list cells or a `Spinner` for action affordances (per AR-28)

**Given** a list query returns empty results
**When** the list renders
**Then** an empty-state message displays
**And** a "Create the first record" CTA is rendered if the user has `canCreate`

**Given** a non-blocking API error (e.g., toast-suitable)
**When** the error is caught by TanStack Query
**Then** a sonner toast surfaces it (per AR-29 / FR-41 AC-3) with `t(error.messageKey, error.details)` translation

**Given** a blocking error (list or detail fetch failure)
**When** the error is caught by the route's `errorComponent`
**Then** an inline `ErrorBanner` with retry button renders (per AR-28)
**And** the banner displays the correlation ID to aid support

**Given** a form submission validation error from the server
**When** the response is `ValidationProblemDetails`
**Then** offending fields highlight inline with messages via `setError(field, ...)` per AR-34

---

## Epic 7: UX Polish & Cross-Cutting Hardening

Final epic — verify and harden the cross-cutting concerns that have been built incrementally throughout, plus ship the user-facing polish features (mobile responsive nav, themes, admin shell). Runs accessibility audits, externalized-string audits, and validates that the admin settings area meets the design contract.

### Story 7.1: Responsive Layout with Collapsible Navigation

As a Content Editor on mobile,
I want to use all core CMS functions on my phone,
So that I am not limited to desktop access.

**Acceptance Criteria:**

**Given** a viewport <768 px
**When** any FormForge route renders
**Then** the layout is single-column

**Given** a viewport ≥768 px
**When** any route renders
**Then** the layout is sidebar + content

**Given** I am on mobile
**When** the navbar renders
**Then** it collapses to a hamburger menu (validating FR-22 AC-3 / Story 4.7 across all pages)
**And** tapping any item auto-closes the nav

**Given** any interactive control on any FormForge page
**When** I inspect it on mobile
**Then** its touch target is ≥44×44 px (per FR-37 AC-3)

**Given** any viewport ≥320 px
**When** I scroll horizontally
**Then** no horizontal scroll bar appears (per FR-37 AC-4)

**Given** a CI visual-regression or Playwright sweep at viewports 320 / 768 / 1024 / 1440 px
**When** the suite runs against the full route set
**Then** the layout passes for every route at every breakpoint

### Story 7.2: Select and Apply a Theme

As any user,
I want to select from three visual themes,
So that the interface suits my preference.

**Acceptance Criteria:**

**Given** I open the user profile / settings dropdown
**When** I view the theme selector
**Then** three themes are listed: `default-light`, `slate-dark`, `solarized`

**Given** I select a theme
**When** the selection commits
**Then** the theme applies immediately without page reload
**And** the underlying mechanism is Tailwind 4 CSS variables keyed off `[data-theme='...']` (per AR-27)

**Given** the theme is applied
**When** I navigate between routes
**Then** the theme persists across navigations (no flash, no reset)

### Story 7.3: Server-Side Theme Persistence and No-Flash Hydration

As any user,
I want to expect my theme preference to be restored automatically on next login,
So that I do not re-select it each session.

**Acceptance Criteria:**

**Given** I have selected a theme
**When** I PUT `/api/users/me/preferences` with `{ themePreference }`
**Then** the choice is persisted to my user record
**And** the login and refresh responses include `themePreference` in the user profile

**Given** I reload the page
**When** the inline `<script nonce="{cspNonce}">` in `<head>` runs (per AR-27)
**Then** it reads `localStorage.getItem('ff-theme')` synchronously and sets `data-theme` before React hydrates
**And** there is no flash of the default theme on reload for an already-authenticated user

**Given** the first-ever visit before login
**When** the page loads
**Then** the default theme is shown; after login, `localStorage` is updated and the user's theme applies on subsequent reloads (per FR R-10 acceptable behavior)

**Given** the CSP nonce flow
**When** the `IndexHtmlRewriter` middleware runs (per AR-27 / AR-39)
**Then** a fresh nonce is generated per request and threaded into the theme script tag and the CSP `script-src` directive

### Story 7.4: Accessibility Compliance

As a user with assistive technology,
I want to navigate and use FormForge with a keyboard and screen reader,
So that the platform is accessible to all users.

**Acceptance Criteria:**

**Given** any interactive control on any FormForge page
**When** I navigate via keyboard
**Then** the control is keyboard-reachable in a logical tab order

**Given** any form input
**When** I inspect its accessibility tree
**Then** it has an associated `<label>` or `aria-label`
**And** any validation errors are linked via `aria-describedby` pointing to the error message

**Given** body text against background
**When** I run a contrast audit
**Then** the contrast ratio is ≥4.5:1 across all themes (default-light, slate-dark, solarized)

**Given** all DnD interactions (designer canvas, menu reorder)
**When** I attempt them with keyboard only
**Then** the `useKeyboardDnD` hook from Story 3.10 provides equivalent functionality

**Given** a rendered DynamicComponent form (any production form)
**When** the axe-core CI gate runs (per AR-43)
**Then** zero critical violations are reported

**Given** the CI pipeline
**When** a PR is opened
**Then** the axe-core smoke audit is a required check

### Story 7.5: Dedicated Admin Settings Area

As a Platform Admin,
I want to access a dedicated admin area containing all management pages,
So that platform configuration is separated from day-to-day data entry.

**Acceptance Criteria:**

**Given** I am logged in as a Platform Admin
**When** the navbar renders
**Then** a fixed "Settings" link is visible (visible only to users with the `platform-admin` role)

**Given** I click "Settings"
**When** the admin area opens
**Then** I see sub-pages: Users, Roles, Menus, Designers (library), Audit Logs (schema + mutation)

**Given** a user without the `platform-admin` role
**When** they navigate to any `/admin/*` route
**Then** the TanStack Router `beforeLoad` guard (per Architecture minor-gap #7) throws `redirect({ to: '/' })`
**And** if they bypass the client-side guard, the server returns HTTP 403 (validating Story 2.6)

**Given** the admin area pages
**When** I navigate between them
**Then** the admin shell layout (breadcrumbs, sub-nav) is consistent

### Story 7.6: Externalized String Architecture

As a Developer,
I want to find all user-facing strings in resource files,
So that translation to additional languages is a configuration task, not a code change.

**Acceptance Criteria:**

**Given** any TSX file in `web/src/`
**When** I grep for user-facing English strings in JSX
**Then** zero hardcoded strings appear; every user-facing string uses `t('key', ...)` per AR-33

**Given** the `web/src/lib/i18n/locales/en.json` resource file
**When** I inspect it
**Then** every `t('key')` call in the codebase has a corresponding entry

**Given** any API error response
**When** I inspect the `ProblemDetails` payload
**Then** it contains both a `messageKey` (i18n key per AR-18) and an English `detail` message (per FR-49 AC-3)

**Given** an automated string-extractor or lint pass
**When** it runs against `web/src/`
**Then** orphaned `t('key')` calls (no en.json entry) and orphaned en.json entries (no `t()` callsite) are reported

**Given** i18next is initialized
**When** the SPA boots
**Then** initialization is synchronous and completes before React renders (per AR-33)

**Given** v1 scope
**When** I inspect the locales directory
**Then** only `en.json` is present; the architecture supports adding additional locales as a config-only change (FR-49 AC-4)

---

## Epic 8: Dataset Foundation & Custom Query Mode

The Dataset Manager lets permitted users define named SQL datasets persisted as rows in `custom_dataset` and materialized as PostgreSQL VIEWs in the `datasets` schema. Epic 8 delivers the complete foundation: DB migration, RBAC permission, `dataset_name` validation, full CRUD with transactional VIEW lifecycle, optimistic concurrency, Custom Query Mode with SELECT enforcement, audit logging, and the Dataset Management UI. Epics 9–11 layer the visual Query Builder on top.

### Story 8.1: Create custom_dataset Migration

As a Developer,
I want a database migration that creates the `custom_dataset` and `dataset_audit_log` tables plus the `datasets` PostgreSQL schema,
So that the platform has a persistent, auditable store for Dataset definitions.

**Acceptance Criteria:**

**Given** the EF Core migration runs
**When** it completes
**Then** the `datasets` schema is created via `CREATE SCHEMA IF NOT EXISTS datasets` (all Dataset VIEWs will live here, eliminating naming collision with `public` schema per AR-57)

**Given** the migration
**When** it creates `custom_dataset`
**Then** the table has columns: `id UUID PK DEFAULT gen_random_uuid()`, `dataset_name TEXT UNIQUE NOT NULL`, `is_custom_query BOOLEAN NOT NULL DEFAULT true`, `query TEXT`, `builder_state JSONB`, `version INTEGER NOT NULL DEFAULT 1`, `created_at TIMESTAMPTZ DEFAULT now()`, `created_by UUID REFERENCES users(id)`, `updated_at TIMESTAMPTZ`, `updated_by UUID REFERENCES users(id)` (per FR-55 AC-1)
**And** a `UNIQUE` constraint on `dataset_name` enforces uniqueness at the DB level as a second line of defense

**Given** the migration
**When** it creates `dataset_audit_log`
**Then** the table has columns: `id UUID PK DEFAULT gen_random_uuid()`, `timestamp TIMESTAMPTZ DEFAULT now()`, `actor_id UUID REFERENCES users(id)`, `actor_name TEXT`, `dataset_name TEXT NOT NULL`, `operation TEXT NOT NULL CHECK (operation IN ('CREATE','UPDATE','DELETE'))`, `previous_values JSONB`, `new_values JSONB`, `ddl TEXT`, `succeeded BOOLEAN NOT NULL DEFAULT true`, `correlation_id TEXT` (per FR-55 AC-2 / AR-57)

**Given** the migration
**When** it also modifies the `roles` table (per AR-58)
**Then** a `can_manage_datasets BOOLEAN NOT NULL DEFAULT false` column is added to `roles`
**And** existing role rows default to `false`

**Given** the migration runs in any environment
**When** it is re-run
**Then** it is idempotent — no error if tables already exist (per FR-55 AC-3)

**Given** the migration
**When** it completes
**Then** it does not touch any other existing domain table (per FR-55 AC-4)

**Given** the migration
**When** it adds indexes
**Then** `idx_custom_dataset_dataset_name` (UNIQUE), `idx_dataset_audit_log_dataset_name_timestamp (dataset_name, timestamp DESC)`, and `idx_dataset_audit_log_operation (operation)` are created (per AR-57)

### Story 8.2: Seed dataset-management Permission and Enforce Server-Side

As a Platform Admin,
I want to grant the `dataset-management` permission to a Role,
So that only authorized users can create, edit, and delete Datasets.

**Acceptance Criteria:**

**Given** the `platform-admin` role is seeded (from Story 2.4)
**When** the migration from Story 8.1 runs
**Then** `platform-admin` is updated to have `can_manage_datasets = true`

**Given** the `EffectivePermissions` record (per AR-58)
**When** permissions are computed for a user
**Then** the record includes `CanManageDatasets: bool` derived from `can_manage_datasets` on any role the user holds

**Given** a request to POST /api/datasets, PUT /api/datasets/{id}, DELETE /api/datasets/{id}, or POST /api/datasets/preview
**When** the user does not have `can_manage_datasets = true` on any of their roles
**Then** the response is HTTP 403 with `{ code: "FORBIDDEN", action: "dataset-management" }` (per FR-56 AC-2 / AR-58)

**Given** GET /api/datasets or GET /api/datasets/{id}
**When** the user is authenticated (any role)
**Then** the request proceeds — read access requires only authentication, not dataset-management (per FR-56 / A18)

**Given** the Admin > Roles permission matrix UI (Story 2.9)
**When** a Platform Admin edits a role
**Then** a "Dataset Management" row toggle is visible and writable, distinct from the per-resource CRUD flags (per AR-58)

### Story 8.3: dataset_name Validation (Client + Server)

As the system,
I validate any proposed `dataset_name` both client-side and server-side before it is used as a VIEW name,
So that invalid or unsafe identifiers are rejected before any DDL runs.

**Acceptance Criteria:**

**Given** a `dataset_name` submitted to any Dataset write endpoint
**When** server-side validation runs (implemented in `DatasetName.cs` + `DatasetNameValidator.cs` per AR-57)
**Then** valid names match `^[a-z_][a-z0-9_]*$` and are ≤63 bytes; any other value returns HTTP 422 `{ code: "INVALID_DATASET_NAME" }` (per FR-57 AC-1)

**Given** a `dataset_name` matching the regex but appearing in the PostgreSQL reserved-keyword list
**When** server-side validation runs
**Then** HTTP 422 is returned identifying the reserved keyword (per FR-57 AC-2)

**Given** a `dataset_name` in the permanent identifier denylist (users, roles, refresh_tokens, password_reset_tokens, mfa_backup_codes, mfa_sessions, schema_audit_log, mutation_audit_log, dataset_audit_log, custom_dataset, etc. per AR-57)
**When** validation runs
**Then** HTTP 422 is returned even if the name passes the regex and keyword checks

**Given** a `dataset_name` identical to an existing Dataset's name
**When** the server validates during create
**Then** HTTP 409 is returned with `{ code: "DATASET_NAME_CONFLICT" }` (per FR-57 AC-3)

**Given** the Dataset create/edit form in the UI
**When** I blur the `dataset_name` field or click submit
**Then** client-side validation fires inline before any API call, showing the error message (per FR-57 AC-4)

**Given** a client that bypasses client-side validation
**When** the POST or PUT request reaches the server
**Then** server-side validation runs independently and returns the same error codes (per FR-57 AC-5)

**Given** any code path that is about to execute Dataset VIEW DDL
**When** it runs immediately before the DDL
**Then** `dataset_name` is re-validated as a final defense-in-depth check (per FR-57 AC-6 / AR-57)

### Story 8.4: Create Dataset

As a user with the `dataset-management` permission,
I want to create a new Dataset by providing a `dataset_name`, authoring mode, and optional query,
So that a row and a backing PostgreSQL VIEW are created atomically.

**Acceptance Criteria:**

**Given** I POST /api/datasets with `{ dataset_name, is_custom_query, query? }`
**When** validation passes (FR-57 name rules; FR-60 SELECT-only if query provided)
**Then** within one NpgsqlTransaction (per AR-59): the row is inserted with `version = 1`; `CREATE VIEW datasets.{dataset_name} AS {query}` executes (using a placeholder `SELECT 1 AS placeholder` when `query` is null or empty)
**And** HTTP 201 is returned with `{ id, dataset_name, is_custom_query, query, builder_state, version, created_at, created_by }`

**Given** the VIEW DDL fails (e.g., invalid SQL syntax in `query`)
**When** the transaction processes
**Then** full rollback occurs — neither the row nor the VIEW is created
**And** HTTP 422 is returned with `{ code: "INVALID_QUERY" }` and the PostgreSQL error message (per FR-58 AC-4)

**Given** the transaction commits successfully
**When** I inspect `dataset_audit_log`
**Then** a row is appended with `operation: "CREATE"`, `dataset_name`, `actor_id`, `timestamp`, `ddl` (exact SQL executed), `succeeded: true`, `correlation_id`

**Given** the audit log entry
**When** the DDL was part of a rolled-back transaction
**Then** the entry records `succeeded: false` and the attempted DDL string (per FR-61 / AR-57)

### Story 8.5: Update Dataset (Edit & Rename)

As a user with the `dataset-management` permission,
I want to update an existing Dataset's `dataset_name`, `query`, or `builder_state`,
So that the row and its backing VIEW are updated atomically with rollback safety.

**Acceptance Criteria:**

**Given** I PUT /api/datasets/{id} with `{ dataset_name?, is_custom_query?, query?, builder_state?, version }`
**When** `version` in the request does not match the current DB `version`
**Then** HTTP 409 is returned with `{ code: "DATASET_CONCURRENCY_CONFLICT", currentVersion: N }` (per FR-59 / AR-60)

**Given** `version` matches and `dataset_name` is unchanged
**When** the transaction executes (per AR-59)
**Then** within one NpgsqlTransaction: `CREATE OR REPLACE VIEW datasets.{dataset_name} AS {new_query}` + row update with `version += 1`; on failure → full rollback, existing VIEW untouched

**Given** `version` matches and `dataset_name` has changed
**When** the transaction executes (per AR-59 / AD-17 resolved)
**Then** within one NpgsqlTransaction: `ALTER VIEW datasets.{old_name} RENAME TO {new_name}` + row update with new `dataset_name` and `version += 1`; on failure → full rollback, old VIEW still intact at its original name

**Given** a successful update
**When** the response is returned
**Then** HTTP 200 is returned with the updated Dataset including the incremented `version`
**And** `dataset_audit_log` records `operation: "UPDATE"`, `previous_values`, `new_values`, `ddl`, `actor_id`, `timestamp`

### Story 8.6: Delete Dataset

As a user with the `dataset-management` permission,
I want to delete a Dataset,
So that its row and backing VIEW are removed atomically.

**Acceptance Criteria:**

**Given** I DELETE /api/datasets/{id}
**When** the id exists
**Then** within one NpgsqlTransaction: the `custom_dataset` row is deleted + `DROP VIEW IF EXISTS datasets.{dataset_name}` executes; on failure → full rollback
**And** HTTP 204 is returned on success (per FR-58 / FR-56 H-6 AC-2)

**Given** a non-existent id
**When** I DELETE /api/datasets/{id}
**Then** HTTP 404 is returned

**Given** the deletion commits
**When** I inspect `dataset_audit_log`
**Then** an entry records `operation: "DELETE"`, `dataset_name`, `ddl: "DROP VIEW IF EXISTS datasets.{dataset_name}"`, `actor_id`, `timestamp`, `succeeded: true`

### Story 8.7: List and Get Datasets

As an authenticated user,
I want to list all Datasets and retrieve a single Dataset by ID,
So that I can browse and open existing Dataset definitions.

**Acceptance Criteria:**

**Given** I am authenticated (any role)
**When** I GET /api/datasets?page=1&pageSize=25
**Then** I receive `PagedResult<DatasetSummaryDto>` with `{ id, dataset_name, is_custom_query, created_at, updated_at, created_by_name }`; default pageSize 25 (per FR-62 partial / AR-65)

**Given** I GET /api/datasets/{id}
**When** the id exists
**Then** I receive the full `DatasetDto` including `query`, `builder_state`, and `version`

**Given** a non-existent id
**When** I GET /api/datasets/{id}
**Then** HTTP 404 is returned

### Story 8.8: Custom Query Authoring Mode — SQL Textarea & SELECT Enforcement

As a user with `dataset-management`,
I want to write a raw SQL SELECT query in a textarea when in Custom Query Mode,
So that I can define a Dataset using hand-authored SQL, with the server rejecting any non-SELECT statements.

**Acceptance Criteria:**

**Given** `is_custom_query = true` in the Dataset create/edit modal (Story 8.10)
**When** the form renders
**Then** a SQL textarea for `query` is shown; the textarea uses a monospace font (per FR-60 AC-1 / FR-62 H-8 AC-3)

**Given** a non-empty `query` is submitted
**When** server-side `SqlSelectEnforcer` runs (per AR-61)
**Then** it calls `PgQuery.Parse(sql)` — a parse failure returns HTTP 422 `{ code: "INVALID_QUERY", message: "SQL could not be parsed" }`
**And** if the root statement node is not `SelectStmt` (or `WithClause → SelectStmt` for CTEs), HTTP 422 `{ code: "INVALID_QUERY", message: "Only SELECT statements are permitted" }` is returned
**And** DDL keywords (CREATE, DROP, ALTER, TRUNCATE) and DML keywords (INSERT, UPDATE, DELETE, MERGE) at statement start are rejected
**And** CTEs (`WITH … AS (… SELECT …) SELECT …`) are permitted

**Given** the submit button in the modal
**When** `query` is empty and mode is Custom Query
**Then** the submit button is disabled with a visible empty-state hint (per FR-62 H-8 AC-3)

**Given** a client that enables the submit button via DevTools
**When** an empty `query` is submitted
**Then** the server's `SqlSelectEnforcer` runs independently and returns the appropriate error (per FR-60 AC-4 — server is the authority)

**Given** SELECT-only enforcement runs at three checkpoints (per AR-61)
**When** any checkpoint fires
**Then** the check runs independently of all others (defense-in-depth); a failure at any checkpoint halts the request before DDL executes

### Story 8.9: Dataset Audit Log

As a Platform Admin,
I want to view the audit log for Dataset CRUD operations and DDL events,
So that I have full traceability of who created, changed, or deleted a Dataset.

**Acceptance Criteria:**

**Given** any Dataset CREATE, UPDATE, or DELETE operation completes (or fails after DDL attempt)
**When** the audit entry is written
**Then** `dataset_audit_log` receives a row with `id`, `timestamp`, `actor_id`, `actor_name`, `dataset_name`, `operation`, `previous_values`, `new_values`, `ddl` (exact SQL executed or attempted), `succeeded` (true if committed; false if rolled back), `correlation_id` (per FR-61 AC-1 / AR-57)

**Given** a rolled-back transaction
**When** the audit entry is written
**Then** `succeeded = false` and the attempted DDL is recorded (per FR-61 AC-2 / AR-57)

**Given** the audit log
**When** any API is inspected
**Then** no endpoint permits deletion of `dataset_audit_log` rows (append-only per FR-61 AC-3)

**Given** I am a Platform Admin
**When** I GET /api/admin/datasets/audit?page=1&pageSize=25&datasetName=foo&operation=UPDATE
**Then** I receive `PagedResult<DatasetAuditEntryDto>` filterable by `dataset_name` and `operation` (per FR-61 AC-4 / AR-65)

### Story 8.10: Dataset Management UI

As a user with `dataset-management`,
I want to access a Dataset Manager page that lists Datasets and provides Create, Edit, and Delete actions,
So that I can fully manage Datasets without touching the database.

**Acceptance Criteria:**

**Given** I navigate to Admin > Datasets (visible to roles with `can_manage_datasets`)
**When** the page renders
**Then** I see a paginated list with columns: `dataset_name`, mode badge ("Custom Query" or "Query Builder"), `created_at`, `updated_at` (per FR-62 AC-1)

**Given** I click "New Dataset"
**When** the create modal opens
**Then** I see a `dataset_name` field (validated inline per Story 8.3), a Mode toggle (Custom Query / Query Builder), and — when Custom Query is selected — a SQL textarea (Story 8.8) (per FR-62 AC-2)

**Given** I click Edit on a row
**When** the edit modal opens
**Then** all current values are prefilled, and the `version` field is included for optimistic concurrency (Story 8.5) (per FR-62 AC-3)

**Given** I switch from Query Builder mode to Custom Query in the edit modal
**When** the toggle fires
**Then** the SQL textarea is shown, pre-populated with the current `query` from `builder_state`-generated SQL
**And** switching from Custom Query to Query Builder opens the canvas (restored from `builder_state` if available, or empty) (per FR-62 AC-4)

**Given** PUT /api/datasets/{id} returns HTTP 409 (optimistic concurrency conflict)
**When** the frontend receives it
**Then** an inline error displays: "This dataset was modified by someone else. Reload to see the latest version." (per FR-62 AC-5)

**Given** a row in the list
**When** I click the "Audit" icon or link
**Then** the Dataset's `dataset_audit_log` entries are shown (Story 8.9) (per FR-62 AC-6)

**Given** the create/edit modal
**When** I click "Preview"
**Then** Story 11.3's preview endpoint is called (per FR-62 AC-7); preview is available in both Custom Query and Query Builder mode

**Given** I click Delete on a row
**When** the confirmation dialog is accepted
**Then** DELETE /api/datasets/{id} is called (Story 8.6) and the row disappears from the list

---

## Epic 9: Query Builder Canvas & Joins

The visual Query Builder Canvas replaces the SQL textarea with a React Flow canvas. Users drag allowlisted tables from the left-side Table Palette onto the canvas, connect column handles between table nodes to create typed JOIN edges, inspect and configure each join in a property panel, and designate one table as the LEFT (FROM) anchor. Epic 9 delivers the canvas foundation; Epics 10–11 add column/filter/ORDER BY configuration and SQL generation.

### Story 9.1: Table Palette with Allowlisted Tables

As a user with `dataset-management`,
I want to see the Table Palette listing allowlisted tables and drag them onto the canvas,
So that I can build a query from real database tables.

**Acceptance Criteria:**

**Given** I open the Query Builder canvas for a Dataset
**When** the canvas loads
**Then** the left-side Table Palette lists all tables returned by `GET /api/datasets/catalog` (per AR-62 / FR-63 AC-1)
**And** each palette entry shows: table name and column list (name + PG data type) fetched from `information_schema.columns` at canvas load (per FR-63 AC-2)

**Given** the Table Palette
**When** I search or filter by table name
**Then** only matching entries are shown (per FR-63 AC-5)

**Given** I drag a table from the palette onto the React Flow canvas (per AR-68 @xyflow/react v12)
**When** the drop completes
**Then** a `TableNode` is created containing: table name header, Left/Right designation control (Story 9.5), column list with checkboxes (Epic 10), a unique node ID (per FR-63 AC-3)

**Given** the same table is dragged multiple times
**When** each drag completes
**Then** each instance is a distinct node with a unique node ID (supports self-joins per FR-63 AC-4)

**Given** a table not in the server allowlist
**When** I inspect the palette
**Then** it is absent entirely — no client-side filtering can expose it (per FR-63 AC-6 / AR-62)

### Story 9.2: Multi-Table Canvas

As a user building a Dataset,
I want to place multiple table nodes on the React Flow canvas,
So that I can construct multi-table queries.

**Acceptance Criteria:**

**Given** I have placed table nodes on the canvas
**When** I drag them
**Then** node positions are freely adjustable and persisted in `builder_state.nodes[*].position` on save (per FR-63 / AR-67)

**Given** the canvas
**When** it renders
**Then** zoom and pan controls are available (per FR-63 I-2 AC-2)

**Given** I remove a table node from the canvas
**When** the removal completes
**Then** all join edges connected to that node are also removed
**And** their join configuration is cleared from `builder_state` (per FR-63 I-2 AC-3)

**Given** I remove a table node that has column selections, CASE columns, or calculated columns configured
**When** the removal completes
**Then** those configurations are cleared from `builder_state` and the SELECT clause is updated accordingly (per FR-63 I-2 AC-4)

### Story 9.3: Column-to-Column Join Edge Creation

As a user building a Dataset,
I want to connect a column handle on one table node to a column handle on another to create a JOIN,
So that I can define the join predicate visually.

**Acceptance Criteria:**

**Given** each column in a table node exposes a connection handle (per AR-68 `JoinEdge.tsx`)
**When** I drag from one column handle to another on a different table node
**Then** a Join Edge is created between the two nodes (per FR-64 AC-1)

**Given** two column handles on the same table node instance
**When** I attempt to connect them
**Then** the connection is rejected — a self-join requires two separate node instances (per FR-64 AC-2)

**Given** a join edge is created
**When** it renders
**Then** it appears as a styled curve with a delete control; deleting it removes the join configuration from `builder_state` (per FR-64 AC-4)

**Given** V1 scope
**When** I inspect the join edge constraints
**Then** one join edge per node-pair per combination of nodes is supported (additional join conditions handled via filter conditions per FR-64 I-3 AC-3 / A20)

### Story 9.4: Join Property Inspector

As a user building a Dataset,
I want to click a join edge and configure the join type in a property inspector,
So that the SQL JOIN clause is fully defined.

**Acceptance Criteria:**

**Given** a join edge exists
**When** I click it
**Then** a property panel/popover opens showing (per FR-65 AC-1 / AR-68 `JoinInspector.tsx`): (a) the two joined columns (table name + column name for each side), (b) a join type selector: INNER / LEFT / RIGHT / FULL OUTER

**Given** the inspector opens for a newly created edge
**When** I inspect the default join type
**Then** it is INNER (per FR-65 AC-2)

**Given** the inspector shows the join sides
**When** I inspect which side is "left" vs "right"
**Then** the designation matches the Left/Right toggle on each respective table node (Story 9.5) (per FR-65 AC-3)

**Given** I change the join type
**When** I close the inspector
**Then** the selected join type is saved to `builder_state.edges[i].data.joinType` (per FR-65 AC-4)

### Story 9.5: Table Left/Right Designation

As a user building a Dataset,
I want to designate each table node as "left table" or "right table",
So that join sidedness is explicit and the SQL generator can produce a correct FROM / JOIN clause.

**Acceptance Criteria:**

**Given** each table node header (per AR-68 `TableNode.tsx`)
**When** the node renders
**Then** a "Left" / "Right" toggle is visible

**Given** the first table dragged onto the canvas
**When** it lands
**Then** its designation defaults to "Left"; all subsequently dragged tables default to "Right" (per FR-66 AC-2 / A21)

**Given** more than one table is on the canvas and no table is designated as "Left"
**When** I attempt to Save or Preview
**Then** both actions are disabled with the validation message: "Designate one table as the left (FROM) table." (per FR-66 AC-3)

**Given** I set a table to "Left"
**When** the SQL generator runs (Story 11.1)
**Then** it uses the left-designated table as the `FROM` anchor; all other tables appear as `JOIN` clauses (per FR-66 AC-1)

**Given** a designation change
**When** I save
**Then** the Left/Right value is stored per node in `builder_state.nodes[*].data.side` (per FR-66 AC-4 / AR-67)

---

## Epic 10: Builder Config

Adds the column selection, aggregate/alias, CASE derived columns, calculated columns, Filter Conditions dialog (arbitrarily nested AND/OR groups with parameterized values), and ORDER BY panel to the Query Builder canvas from Epic 9. All configuration is stored in `builder_state` JSON. Server-side expression security validation gates CASE and calculated columns.

### Story 10.1: Per-Table Column Selection

As a user building a Dataset,
I want to check and uncheck columns for each table node,
So that only the relevant columns appear in the SELECT clause.

**Acceptance Criteria:**

**Given** a table node on the canvas
**When** it renders
**Then** each column is shown with a checkbox; all columns start unchecked by default on first drag (user opts in per-column) (per FR-67 J-1 AC-1–AC-2)

**Given** I check a column
**When** I save
**Then** the column selection is stored in `builder_state.nodes[*].data.columns[*].checked` (per FR-67 J-1 AC-3)

**Given** no column is checked across any table node
**When** I attempt to Save or Preview
**Then** both are blocked with the message: "Select at least one column." (per FR-67 J-1 AC-4)

### Story 10.2: Aggregate Function and Custom Column Alias

As a user building a Dataset,
I want to assign an aggregate function and a custom alias to any selected column,
So that the generated SELECT clause includes the correct aggregation and naming.

**Acceptance Criteria:**

**Given** a selected column row in a table node (per AR-68 `TableNode.tsx`)
**When** the row renders
**Then** it shows an "Aggregate" dropdown (None / COUNT / SUM / AVG / MIN / MAX) and an "Alias" text input (per FR-67 J-2 AC-1)

**Given** I set aggregate to SUM on a column
**When** the SQL generator runs (Story 11.1)
**Then** it emits `SUM("table"."col") AS "alias"` in the SELECT clause (per FR-67 J-2 AC-2)

**Given** any aggregate is set on any column
**When** the SQL generator runs
**Then** it auto-derives a GROUP BY clause listing all non-aggregated selected columns (per FR-67 J-2 AC-3)

**Given** an alias field
**When** I leave it empty
**Then** the alias defaults to `{table}_{column}` (disambiguated to avoid duplicates)
**And** the alias is validated to follow FR-57 identifier rules inline (per FR-67 J-2 AC-4)

**Given** I set aggregate + alias values
**When** I save
**Then** they are stored per-column in `builder_state` (per FR-67 J-2 AC-5)

### Story 10.3: Add CASE Column (CASE/WHEN Derived Column)

As a user building a Dataset,
I want to add a CASE/WHEN expression as a derived column on any table node,
So that conditional logic can be expressed in the SELECT clause without writing raw SQL.

**Acceptance Criteria:**

**Given** a table node on the canvas
**When** I click "Add Case"
**Then** a derived column row is added with a CASE/WHEN builder: one or more WHEN conditions (column + operator + value) and THEN values, plus an optional ELSE value (per FR-67 J-3 AC-1)

**Given** the WHEN condition builder
**When** I configure an operator
**Then** the same operator vocabulary is used as the Filter Conditions dialog (Story 10.6) (per FR-67 J-3 AC-2)

**Given** THEN and ELSE values
**When** I configure them
**Then** they can be string literals, numeric literals, or column references from any table on canvas (per FR-67 J-3 AC-3)

**Given** a CASE column without an alias
**When** I attempt to Save
**Then** Save is blocked: "A custom alias is required for every CASE column." (per FR-67 J-3 AC-4)

**Given** a valid CASE definition
**When** the SQL generator runs (Story 11.1)
**Then** it produces a syntactically valid `CASE WHEN … THEN … ELSE … END AS "alias"` expression (per FR-67 J-3 AC-5)

**Given** the expression security validator (per AR-64)
**When** CASE column is saved
**Then** `ExpressionSecurityValidator.cs` applies the three-layer check (keyword scan + wrap-parse + final SELECT-only); failure → HTTP 422 identifying the offending CASE column by alias

### Story 10.4: Add Calculated Column (Expression)

As a user building a Dataset,
I want to add a free-form SQL expression as a calculated column on any table node,
So that arithmetic or string operations can appear in the SELECT clause.

**Acceptance Criteria:**

**Given** a table node on the canvas
**When** I click "Add Calculated Column"
**Then** a row is added with a free-form expression textarea and an alias field (per FR-67 J-4 AC-1)

**Given** the expression textarea
**When** I type an expression
**Then** the server applies the `ExpressionSecurityValidator.cs` three-layer check (per AR-64 / FR-67 J-4 AC-2) before including it in generated SQL

**Given** a calculated column without an alias
**When** I attempt to Save
**Then** Save is blocked: "A custom alias is required for every calculated column." (per FR-67 J-4 AC-3)

**Given** a valid expression and alias
**When** the SQL generator runs (Story 11.1)
**Then** it inserts `({expression}) AS "alias"` in the SELECT clause (per FR-67 J-4 AC-4)

**Given** the calculated column definition
**When** I save
**Then** it is stored in `builder_state.calculatedColumns[]` (per FR-67 J-4 AC-5)

### Story 10.5: Filter Conditions Dialog

As a user building a Dataset,
I want to open a Filter Conditions dialog that lets me define a WHERE clause with AND/OR combinators and groups,
So that the query result is filtered without writing SQL.

**Acceptance Criteria:**

**Given** the canvas toolbar
**When** I click "Filter"
**Then** a modal dialog opens showing the current WHERE clause builder (per FR-68 J-5 AC-1)

**Given** the dialog
**When** it renders
**Then** it shows a root combinator (AND / OR selector) and a list of top-level conditions and groups (per FR-68 J-5 AC-2)

**Given** I click "Add" inside the dialog
**When** a popover opens
**Then** it offers two options: "Add condition" and "Add group" (creates a nested sub-clause with its own AND/OR combinator) (per FR-68 J-5 AC-3)

**Given** a nested group
**When** it renders
**Then** visible parenthesis indicators and indentation indicate the depth level (per FR-68 J-5 AC-4)

**Given** conditions and groups in the dialog
**When** I drag or click delete
**Then** I can reorder and delete any condition or group (per FR-68 J-5 AC-5)

**Given** I save and reopen the Dataset
**When** the canvas restores
**Then** the Filter Conditions dialog reopens to the same state (per FR-68 J-5 AC-6 / AR-67 builder_state)

### Story 10.6: Filter Condition Definition

As a user building a Dataset,
I want to define each filter condition as a table + column + operator + value expression,
So that the WHERE clause is precise and the value input adapts to the operator.

**Acceptance Criteria:**

**Given** a condition row in the Filter Conditions dialog
**When** it renders
**Then** it shows: (a) table selector (tables on canvas), (b) column selector (selected table's columns), (c) operator selector, (d) value input (per FR-68 J-6 AC-1)

**Given** the operator selector
**When** it lists operators
**Then** the available operators are: `=`, `!=`, `<`, `<=`, `>`, `>=`, `IS NULL`, `IS NOT NULL`, `LIKE`, `ILIKE`, `IN`, `NOT IN`, `BETWEEN` (per FR-68 J-6 AC-2)

**Given** I select an operator
**When** the value input renders
**Then** `IS NULL` / `IS NOT NULL` → no value input; `IN` / `NOT IN` → multi-value tag input; `BETWEEN` → two inputs (from / to); all others → single input (per FR-68 J-6 AC-3)

**Given** the column's PG type
**When** the value input type adapts
**Then** `NUMERIC` → number input; `TIMESTAMPTZ` → date/time picker; `BOOLEAN` → checkbox; `TEXT` → text input (per FR-68 J-6 AC-4)

**Given** the SQL generator runs (Story 11.1)
**When** it processes filter conditions
**Then** values are emitted as parameterized placeholders `$1`, `$2`, … with a separate parameters array — never string-interpolated into SQL (per FR-68 J-6 AC-5 / NFR-17 / AR-66)

### Story 10.7: Nested Filter Groups

As a user building a Dataset,
I want to nest filter groups to arbitrary depth,
So that complex boolean logic can be expressed visually.

**Acceptance Criteria:**

**Given** a group in the Filter Conditions dialog
**When** I click "Add group" inside it
**Then** a sub-group is added with its own AND/OR combinator

**Given** groups at multiple depth levels
**When** the UI renders
**Then** indentation and/or color differentiation per depth level clearly show nesting structure (per FR-68 J-7 AC-2)

**Given** the SQL generator runs (Story 11.1)
**When** it processes nested groups
**Then** it emits correctly parenthesized SQL from the recursive group tree: `((A AND B) OR (C AND D))` (per FR-68 J-7 AC-3)

**Given** deeply nested groups (depth > 5 or any depth)
**When** the application processes them
**Then** no artificial depth limit is enforced in v1 (per FR-68 J-7 AC-4)

### Story 10.8: Order By Panel

As a user building a Dataset,
I want to define ORDER BY clauses as table + column + direction,
So that the dataset results are sorted in a predictable order.

**Acceptance Criteria:**

**Given** the canvas toolbar or dedicated sidebar tab
**When** I open the "Order By" panel
**Then** I see the current list of ORDER BY clauses (per FR-69 J-8 AC-1)

**Given** an ORDER BY clause
**When** I configure it
**Then** it shows: table selector (tables on canvas) + column selector + direction toggle (ASC / DESC) (per FR-69 J-8 AC-2)

**Given** multiple clauses in the ORDER BY panel
**When** I drag to reorder them
**Then** clause order maps directly to SQL ORDER BY precedence — first clause = primary sort (per FR-69 J-8 AC-3)

**Given** the SQL generator runs (Story 11.1)
**When** ORDER BY clauses are configured
**Then** it emits `ORDER BY "table"."col" ASC|DESC, …` in declared clause order (per FR-69 J-8 AC-4)

**Given** the ORDER BY panel is empty
**When** the SQL generator runs
**Then** no ORDER BY clause is emitted in generated SQL (empty is valid) (per FR-69 J-8 AC-5)

**Given** I save
**When** the save completes
**Then** ORDER BY state is persisted in `builder_state.orderBy[]` and restored on reopen (per AR-67)

---

## Epic 11: SQL Generation, Preview & Builder View Sync

Closes the Query Builder loop: the server generates authoritative SQL from `builder_state`, `builder_state` is persisted and restored faithfully on reopen, Preview executes with LIMIT 10 + statement timeout against a dedicated read-only PostgreSQL role, and builder-mode saves reuse the Epic 8 transactional VIEW lifecycle.

### Story 11.1: Server-Authoritative SQL Generation from builder_state

As the system,
I re-derive the SQL SELECT statement from `builder_state` on the server before any VIEW DDL or preview execution,
So that the client cannot bypass Query Builder security constraints with a hand-crafted SQL string.

**Acceptance Criteria:**

**Given** PUT /api/datasets/{id} with `is_custom_query = false`
**When** the server processes the request
**Then** `DatasetSqlGenerator.cs` accepts the `BuilderStateDto` JSON and runs the 10-step algorithm (per AR-66): (1) pre-flight validation — one left node, ≥1 column checked, all CASE/calculated aliases non-empty; (2) allowlist validation for all table names; (3) identifier safety via `SafeIdentifier`; (4) FROM clause from left-designated table; (5) JOIN clauses per edge; (6) SELECT list (plain/aggregated/CASE/calculated); (7) GROUP BY auto-derived; (8) WHERE with `$1,$2,...` parameters; (9) ORDER BY in declared order; (10) SELECT-only validation via `SqlSelectEnforcer`

**Given** the generator
**When** it produces the SQL
**Then** all table and column identifiers are double-quoted (`"table_name"."column_name"`) to handle reserved words (per FR-70 AC-3 / AR-66)

**Given** the generator
**When** it processes filter values
**Then** they are emitted as parameterized placeholders `$1`, `$2`, … alongside a `Parameters[]` array — never interpolated (per FR-70 AC-4 / NFR-17)

**Given** `builder_state` is incomplete (no left table, no columns selected, alias empty on a CASE/calculated column)
**When** the generator runs
**Then** it returns a validation error list → HTTP 422 `{ code: "BUILDER_STATE_INVALID" }` before any DDL executes (per FR-70 AC-6 / AR-65)

**Given** a successful generator run
**When** the save transaction commits
**Then** `custom_dataset.query` is set to the generated SQL within the same transaction as `builder_state` and the VIEW DDL, so both are always in sync (per FR-70 AC-7 / AR-66)

### Story 11.2: builder_state Persistence and Restore

As a user in Query Builder Mode,
I want to save my canvas and reopen the same Dataset later to find my canvas exactly as I left it,
So that work is not lost between sessions.

**Acceptance Criteria:**

**Given** PUT /api/datasets/{id} with `is_custom_query = false`
**When** the save transaction commits
**Then** the full `builder_state` JSON (nodes with positions and all configuration, edges, column selections, filter state, order clauses, CASE columns, calculated columns) is persisted to `custom_dataset.builder_state` (per FR-71 AC-1 / AR-67)

**Given** I open an existing Dataset in Query Builder Mode
**When** the canvas loads
**Then** the frontend restores the canvas from `builder_state`: table nodes at saved positions, edges drawn, column checkboxes checked, aggregate/alias values filled, filter dialog state loaded, ORDER BY clauses listed (per FR-71 AC-2)

**Given** `builder_state` is null
**When** the canvas loads
**Then** the canvas opens empty (per FR-71 AC-3)

**Given** any successful builder-mode save
**When** I inspect `custom_dataset`
**Then** both `query` (server-generated SQL) and `builder_state` (raw canvas state) are updated in the same transaction — they are always in sync (per FR-71 AC-4 / AR-66)

### Story 11.3: Query Preview (LIMIT 10)

As a user creating or editing a Dataset,
I want to preview the query result (up to 10 rows) before saving,
So that I can validate correctness before the VIEW is persisted.

**Acceptance Criteria:**

**Given** the Dataset create/edit modal (Story 8.10) in either mode
**When** I click "Preview"
**Then** a "Preview" button is present and active (per FR-72 AC-1)

**Given** I click "Preview" in Custom Query Mode
**When** the request is sent to POST /api/datasets/preview
**Then** the server validates SELECT-only on the submitted `query`, appends `LIMIT 10`, applies `SET LOCAL statement_timeout = '{PreviewTimeoutSeconds}s'` (default 5 s from `DatasetManager:PreviewTimeoutSeconds` env var per AR-63), and executes against PostgreSQL using the `formforge_preview` read-only connection pool (per FR-72 AC-3 / AR-63)

**Given** I click "Preview" in Query Builder Mode
**When** the request is sent to POST /api/datasets/preview with `builder_state`
**Then** the server generates SQL from `builder_state` (Story 11.1), appends `LIMIT 10`, applies the statement timeout, and executes against the `formforge_preview` pool (per FR-72 AC-2 / AR-63)

**Given** the preview returns results
**When** the UI renders them
**Then** column names appear as headers; up to 10 data rows are displayed (per FR-72 AC-4)

**Given** the statement timeout is exceeded (`NpgsqlException.SqlState == "57014"`)
**When** the server catches it
**Then** HTTP 408 is returned with `{ code: "PREVIEW_TIMEOUT", message: "Preview query exceeded the time limit. Simplify the query or add filters." }` (per FR-72 AC-5 / AR-63)

**Given** a PostgreSQL error during preview (syntax error, permission denied, etc.)
**When** the server catches it
**Then** HTTP 422 is returned with the PostgreSQL error message surfaced to the user — no internal stack traces exposed (per FR-72 AC-6 / AR-63)

**Given** the preview completes
**When** the result is returned
**Then** no Dataset row is created or modified — preview is read-only (per FR-72 AC-7)

**Given** a user without `dataset-management` permission
**When** they POST /api/datasets/preview
**Then** HTTP 403 is returned (per FR-72 AC-8 / AR-58)

### Story 11.4: Builder-Mode View Lifecycle Integration

As the system,
I generate SQL from `builder_state` and apply the Epic 8 transactional view lifecycle on every builder-mode save,
So that Query Builder saves have identical atomicity and rollback safety to Custom Query saves.

**Acceptance Criteria:**

**Given** PUT /api/datasets/{id} with `is_custom_query = false`
**When** the server processes the request
**Then** `DatasetSqlGenerator` generates SQL from `builder_state` (Story 11.1) **before** any DDL executes; if generation fails, HTTP 422 is returned and no DDL runs (per FR-73 AC-1)

**Given** SQL is generated successfully
**When** the VIEW lifecycle runs (Story 8.5)
**Then** the same Epic 8 transactional lifecycle applies: same-name edit → `CREATE OR REPLACE VIEW`; rename → `ALTER VIEW ... RENAME TO`; all within one NpgsqlTransaction with full rollback on failure (per FR-73 AC-2 / AR-59)

**Given** a VIEW DDL operation fails
**When** the transaction rolls back
**Then** any existing working VIEW at the original name remains intact (per FR-73 AC-3)

**Given** the save succeeds
**When** I inspect `dataset_audit_log`
**Then** the `ddl` field contains the server-generated SQL, and a note or metadata indicates "Builder-generated" (per FR-73 AC-4)
