---
workflowType: 'correct-course'
project_name: 'FormForge (tinnitus)'
user_name: 'jukhan'
date: '2026-09-08'
status: 'approved'
scopeClassification: 'Major'
inputDocuments:
  - _bmad-output/planning-artifacts/prds/prd-tinnitus-2026-05-22/prd.md
  - _bmad-output/planning-artifacts/architecture.md
  - _bmad-output/planning-artifacts/epics.md
  - _bmad-output/planning-artifacts/ux-design-specification.md
---

# Sprint Change Proposal — Multi-Tenant Architecture

## 1. Issue Summary

FormForge was designed and substantially implemented as a **single-tenant** platform. This was a deliberate, locked decision, not an oversight:

- PRD Decision Log #3: "Tenancy: Single-tenant — Simpler data model; stated requirement" (alternative considered and rejected: Multi-tenant).
- PRD Non-Goals (§5, A10): "Single-tenant deployment."
- NFR-15: "Single-tenant, internal-users-only, ≤100k rows/table target."

The user has decided to reverse this decision and bring multi-tenant architecture to the platform, as a general product-direction change (no specific external deadline or customer driving it).

This is not a greenfield change. ~100 story implementation-artifact files exist under `_bmad-output/implementation-artifacts/`, indicating Epics 1–11 are substantially built on single-tenant assumptions — including a runtime DDL table-provisioning pipeline, an identifier-whitelisting security model, an in-process permission/schema-registry cache, and a Dataset Manager subsystem that lets trusted users author raw SQL against the database. Multi-tenancy is not a bolt-on feature here — it changes the security boundary for nearly every subsystem in the architecture document.

**Category:** Strategic pivot / product-direction change (not a technical failure or requirements misunderstanding).

## 2. Impact Analysis

### 2.1 Epic Impact

| Epic | Impact | Rework Severity |
|---|---|---|
| 1 — Foundation | Modified scope (tenant-aware env/deploy config) | Low |
| 2 — Identity, Roles & Permissions | Tenant column added to users/roles; JWT gains `tenantId` claim; permission cache key becomes `(tenantId, userId)`; single global `platform-admin` splits into platform-super-admin (new, cross-tenant) + tenant-admin (per-tenant) | **High** |
| 3 — Component Schema Designer | `component_schemas` table moves into per-tenant schema (resolves the bare-`designer_id`-as-PK collision risk) | Medium |
| 4 — Menu Management | `menus` table moves into per-tenant schema | Medium |
| 5 — Dynamic Table Provisioning | `SafeIdentifier`, `DdlEmitter`, and `SchemaRegistry` all currently assume a single global `public` schema — become tenant-schema-qualified | **High** (highest structural risk — this is where cross-tenant table collisions would occur today) |
| 6 — Generic CRUD Service & Data Entry | ~30 SQL-assembly methods in `DynamicQueryBuilder.cs` schema-qualify every table reference | **High** (mechanical but broad) |
| 7 — UX Polish & Cross-Cutting Hardening | Largely unaffected; no per-tenant branding hook exists today (noted as a gap, not in this phase's scope) | Low |
| 8 — Dataset Foundation & Custom Query | `DatasetAllowlist` and the `formforge_preview` DB role currently see every table in `public` — this is a **distinct, currently-real cross-tenant leak vector** once multiple tenants share the database, independent of which isolation model is chosen | **High** |
| 9–11 — Query Builder, Builder Config, SQL Generation | `DatasetSqlGenerator` builds unqualified `FROM "public"."<table>"` — same root cause as Epic 8 | **High** |
| **New: Tenant Foundation & Provisioning** | New epic, inserted after Epic 2 | New (foundational — blocks all rework above) |

No existing epic is invalidated outright. Epic 8's Custom-SQL authoring model (FR-60) needs a genuine security rework, not a mechanical pass, because it currently has no structural tenant boundary at all — a `dataset-management` user can author arbitrary SELECT SQL today.

### 2.2 Artifact Conflicts

**PRD:** Decision Log #3, Non-Goal A10, NFR-15, and Assumption A19 ("Table Allowlist is server-side configured … no per-user table access control") all require revision — see Detailed Change Proposals §4.1.

**Architecture (`architecture.md`):**
- Decision 1.1 (identifier sanitization) — extend to tenant schema names.
- Decision 1.4 (schema registry cache key) — add `tenantId`.
- Decision 1.6 (EF/Dapper transaction boundary) — `ProvisioningJob` gains tenant context.
- Decision 2.1/2.3 (JWT) — add `tenantId` claim.
- Decision 2.2 (permission cache) — compound cache key.
- Decision 2.5 (CORS) — explicitly unaffected (see rationale in §4.8; JWT-claim routing was chosen over subdomain routing).
- Decision 6.6/6.7 (Dataset allowlist + preview role) — full rework, not incremental.
- AR-10 (migration tooling), AR-15 (rate limiting), AR-26 (MinIO) — see §4.8.

**UX Design spec:** Minimal impact. No tenant-switcher or per-tenant branding UI exists; out of scope for this phase (single admin-provisioned tenant per user).

**Other artifacts:** Deployment/migration scripts need a tenant-schema migration fan-out (§4.8); a new tenant-isolation integration test suite becomes a release gate (§4.9); architecture doc's deployment section needs updating.

### 2.3 Technical Impact — Isolation Model Decision

Two isolation models were evaluated against the actual implementation (not just in the abstract):

- **Shared tables + `tenant_id` column:** would require a correct tenant predicate in ~30+ dynamic query-assembly call sites (`DynamicQueryBuilder.cs`, `SoftDeleteCascade.cs`, `RepeaterWriteCoordinator.cs`) plus every Dataset/Query-Builder SQL generation path. Critically, it provides **no defense** against a Custom-SQL Dataset author (a supported, documented feature — FR-60) simply omitting the filter from their own SQL.
- **Schema-per-tenant (selected):** the existing `SafeIdentifier`-validated table *name* is unchanged; only the schema qualifier changes at a small number of centralized call sites. PostgreSQL enforces isolation via `search_path`/schema grants rather than relying on every hand-written and user-authored query remembering a filter — this closes the Dataset Custom-SQL leak vector structurally.

**Decision: schema-per-tenant.** Confirmed by the user based on this risk analysis.

**Supporting scope decisions (confirmed by the user):**
- **Tenant routing:** JWT claim (`tenantId`), not subdomain — avoids DNS/wildcard-cert/per-tenant-CORS-origin work in this phase.
- **Tenant onboarding:** admin-provisioned only — a platform-super-admin creates tenants and seeds the first tenant-admin; no self-service signup, no billing/plan tiers in this phase.

## 3. Recommended Approach

### Options Evaluated

| Option | Viable? | Effort | Risk |
|---|---|---|---|
| **1. Direct Adjustment** — modify/add stories within the existing epic structure | Yes | High | Medium (broad but mostly mechanical changes once the isolation model is fixed) |
| **2. Rollback** — revert completed epics to simplify | **Not viable** | Medium (to roll back) | High (discards working, correct single-tenant functionality for no benefit — the existing features aren't wrong, they simply lack a tenant dimension) |
| **3. PRD MVP Review** — reduce/bound scope | Yes, as a scope-bounding companion to Option 1 | Low (reduces overall effort) | Low |

### Selected Approach: Hybrid of Option 1 + Option 3

Execute via **direct adjustment** — a new Tenant Foundation epic plus modifications to Epics 2–11 — but **bound the initial scope** per Option 3: admin-provisioned tenants only, JWT-claim routing, no self-service signup, no custom domains, no billing/plan tiers, no cross-tenant reporting. These are the new PRD non-goals added in §4.1.

**Rationale:**
- Rollback (Option 2) would discard substantial, structurally-sound work for no gain — the single-tenant features are correct, they just need a tenant dimension threaded through.
- Unbounded Option 1 (full enterprise-SaaS multi-tenancy: self-serve signup, subdomains, billing) would multiply effort and risk far beyond what a "general product direction" change (no specific deadline or customer) warrants.
- The bounded hybrid keeps the isolation-security work (the genuinely hard, must-get-right part) as the full-effort focus, while deferring the product/growth surface (signup, billing, custom domains) to a later phase once the isolation model is proven in production.

## 4. Detailed Change Proposals

*(All 9 proposal groups below were reviewed and approved individually with the user in Incremental mode.)*

### 4.1 PRD

| Section | OLD | NEW |
|---|---|---|
| Decision Log #3 | Tenancy: Single-tenant | Tenancy: Multi-tenant, schema-per-tenant isolation, JWT-claim tenant routing, admin-provisioned onboarding |
| Non-Goals (A10) | "Single-tenant deployment" | Removed. Added: "Self-service tenant signup", "Per-tenant custom domains/subdomains", "Per-tenant billing/plan tiers", "Cross-tenant data sharing or reporting" |
| NFR-15 | "Single-tenant, internal-users-only, ≤100k rows/table" | "Multi-tenant (admin-provisioned tenants), schema-per-tenant isolation, ≤100k rows/table per tenant" |
| Assumption A19 | "Table Allowlist is server-side configured … no per-user table access control" | "Table Allowlist and Dataset Custom Query are scoped to the requesting user's tenant schema; no cross-tenant table access is possible regardless of allowlist configuration" |

### 4.2 New Epic: Tenant Foundation & Provisioning

Inserted after Epic 2. **User outcome:** a platform-super-admin creates a tenant, which provisions an isolated schema and seeds a tenant-admin; every request resolves to exactly one tenant.

- **T-1** `tenants` table (EF-managed): id, name, schema_name, status, created_at.
- **T-2** Tenant provisioning service: `CREATE SCHEMA`, run static-schema migrations into it, seed tenant-admin role + first user, dispatch welcome email (reuses AR-53).
- **T-3** JWT gains `tenantId` claim, set at login.
- **T-4** Platform-super-admin role (new, lives outside tenant schemas) distinct from tenant-admin (formerly the single global `platform-admin`).
- **T-5** Request-scoped `ITenantContext` middleware: resolves `tenantId` from the JWT, sets schema context for the request.
- **T-6** Minimal "Tenants" admin page (list, create), visible only to platform-super-admins.

**Deferred to v2:** self-service signup, subdomain/custom-domain routing, per-tenant billing, tenant merge/migration tooling.

### 4.3 Epic 2: Identity, Roles & Permissions

| Story/AC | OLD | NEW |
|---|---|---|
| FR-1/FR-2 | `users`, `roles`, `user_roles` global in `public` | Move into each tenant's schema; a user exists in exactly one tenant in this phase |
| FR-4 | Permission cache key = `userId` | Cache key = `(tenantId, userId)` |
| FR-7 | Single global `platform-admin`, bootstrapped once | Becomes tenant-admin, seeded per-tenant by T-2 |
| New AC | — | JWT `tenantId` claim must match the tenant resolved by `ITenantContext` for every request, or reject with 401 |

### 4.4 Epic 3 (Designer) + Epic 4 (Menus)

| Story/AC | OLD | NEW |
|---|---|---|
| FR-9 | `component_schemas` in `public`, bare `designer_id` PK | Moves into tenant schema; per-tenant uniqueness falls out of schema isolation, no composite key needed |
| FR-16 | `menus` in `public` | Moves into tenant schema |
| AR-7 | Cache key `schema:{designerId}:{version}` | `schema:{tenantId}:{designerId}:{version}` |
| FR-22 | Navbar cache keyed per-user | No change — remains correct once underlying data is tenant-isolated |

### 4.5 Epic 5: Dynamic Table Provisioning

| Story/AC | OLD | NEW |
|---|---|---|
| FR-23 | `SafeIdentifier` validates a bare name | Unchanged logic; every call site also carries a validated `tenantSchemaName` |
| FR-24 | `DdlEmitter` checks `information_schema.tables WHERE table_schema='public'` | `WHERE table_schema = @tenantSchema`; `CREATE TABLE "{tenantSchema}"."{tableName}"` |
| FR-25 | Same `public`-schema assumption in `AddMissingColumnsCoreAsync` | Same schema-qualification fix |
| AR-9 | `ProvisioningJob` has no tenant context | Gains `TenantId`/`TenantSchema`; `ProvisioningRecoveryService` becomes tenant-aware |

### 4.6 Epic 6: Generic CRUD Service & Data Entry

| Story/AC | OLD | NEW |
|---|---|---|
| FR-29–FR-36 | `DynamicQueryBuilder.cs` (~30 methods) interpolates only the table name | Every method schema-qualifies as `"{tenantSchema}"."{tableName}"` via `ITenantContext` |
| FR-33/FR-35 | `SoftDeleteCascade.cs`, `RepeaterWriteCoordinator.cs` same gap | Same fix |
| New AC | — | Integration test: a request for Tenant A can never read/write a table that exists only in Tenant B's schema, even via a guessed `designerId` — must 404, not 403 (don't leak existence) |

### 4.7 Epics 8–11: Dataset Manager & Query Builder

| Story/AC | OLD | NEW |
|---|---|---|
| AR-57 | Single shared `datasets` schema | Provisioned per tenant (same step as T-2) |
| AR-62 | `DatasetAllowlist` discovers every table in `public` | Scoped to `information_schema.tables WHERE table_schema = @tenantSchema` |
| AR-63 | `formforge_preview` role: `GRANT SELECT ON ALL TABLES IN SCHEMA public` | Per-tenant-scoped grants, set at tenant provisioning time |
| AR-61 | SELECT-only enforcement is the only defense | Unchanged, now backstopped by schema-scoped allowlist + role grants |
| FR-70 | `DatasetSqlGenerator` builds `FROM "public"."<table>"` | `FROM "{tenantSchema}"."<table>"`, injected server-side, never client-supplied |

This is flagged as a hard acceptance-criteria gate, not just an implementation intent, given it is a currently-real leak vector.

### 4.8 Remaining Architecture Decisions

| Decision | OLD | NEW |
|---|---|---|
| AR-26 (MinIO) | Single bucket, prefix `{designerId}/{fieldKey}` | Prefix gains tenant segment: `{tenantSchema}/{designerId}/{fieldKey}/...`; bucket stays shared |
| AR-15 (Rate Limiting) | Per-IP / per-`userId` partitions only | Add per-tenant sliding-window policy on `/api/data/*` and `/api/admin/*`; partition key `(tenantId, userId)` |
| AR-10 (Migrations) | `Database.Migrate()` once against `public` | Startup loop applies migrations to all provisioned tenant schemas; new-tenant provisioning runs the same set once at creation |
| AR-14 (CORS) | Single static allowed-origins list | **Unaffected** — JWT-claim routing means one app origin serves all tenants |

### 4.9 Testing Strategy & Documentation

| Artifact | OLD | NEW |
|---|---|---|
| Testing | Per-epic Testcontainers.PostgreSQL integration tests | Add a dedicated tenant-isolation test suite (2+ tenants provisioned in-container; asserts no CRUD/Dataset/Query-Builder/MinIO/admin endpoint ever crosses tenants) as a **release gate** |
| CI/CD | No change | Isolation suite runs inside the existing `dotnet test` gate — no new pipeline stage |
| Docs | Architecture doc describes single-tenant topology | Update deployment section: schema-per-tenant provisioning, migration fan-out, admin tenant-creation flow |

## 5. Implementation Handoff

**Scope classification: Major.** This changes the PRD, the architecture document's data model and security boundary, and touches implemented code in Epics 2–11. It requires fundamental architecture-decision work, not direct developer implementation.

**Routing:**
1. **Product Manager (`bmad-agent-pm` / `bmad-prd`)** — formally apply the §4.1 PRD amendments (decision log, non-goals, NFR-15, A19).
2. **Solution Architect (`bmad-agent-architect` / `bmad-architecture`)** — add the new architecture decisions from §4.2–§4.8 as formal, numbered decisions in `architecture.md` (tenant data model, `ITenantContext`, schema-provisioning service, revised cache keys, revised Dataset/preview-role security model). This is the highest-value next step — the story-level breakdown below depends on these decisions being nailed down precisely (e.g., exact `tenants` table shape, exact provisioning transaction boundary).
3. **After architecture is amended:** regenerate/extend the epic and story breakdown (`bmad-create-epics-and-stories`) for the new Tenant Foundation epic and the modified stories in Epics 2–11, then re-run `bmad-sprint-planning` to produce an updated sprint status (no `sprint-status.yaml` currently exists in this project, so this will be a fresh generation, not a reconciliation).
4. **Developer agent (`bmad-agent-dev` / `bmad-build`)** — implements once the above is in place, epic by epic, starting with Tenant Foundation → Epic 2 → Epic 5 → Epic 6 → Epics 8–11 → Epics 3/4 (order reflects the dependency chain: identity and provisioning must land before CRUD and Dataset rework can be verified end-to-end).

**Success criteria:**
- All PRD/architecture amendments in §4.1–§4.9 are formally incorporated into their source documents.
- The tenant-isolation integration test suite (§4.9) exists and passes before any tenant-scoped epic is marked done.
- No epic is implemented against `public`-schema assumptions after the Tenant Foundation epic ships.
