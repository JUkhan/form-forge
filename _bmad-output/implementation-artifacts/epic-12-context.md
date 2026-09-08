# Epic 12 Context: Tenant Foundation & Provisioning

<!-- Compiled from planning artifacts. Edit freely. Regenerate with compile-epic-context if planning docs change. -->

## Goal

This epic reverses the platform's original single-tenant decision, introducing multi-tenancy so a Platform-Super-Admin can onboard new customers/organizations without direct database access. Creating a tenant provisions an isolated PostgreSQL schema, migrates the full static-schema table set into it, and fully onboards it — Dataset Manager scoping, a seeded tenant-admin role and first user, welcome email, and activation. From then on, every request resolves to exactly one tenant via a JWT `tenantId` claim, and every tenant-scoped feature elsewhere in the system (identity, designer, menus, provisioning, CRUD, dataset manager) is threaded with that resolved tenant context. This epic must be completed before Epic 2, because the JWT shape and role model it introduces are consumed by every identity-dependent epic downstream — it is numbered last only to avoid renumbering existing story IDs, but is sequenced first in build order.

## Stories

- Story 12.1: Tenant Data Model
- Story 12.2: Tenant Schema Provisioning
- Story 12.3: Tenant Context Resolution
- Story 12.4: Platform-Super-Admin Bootstrap
- Story 12.5: Tenants Admin Page
- Story 12.6: Tenant Context Middleware Integration
- Story 12.7: Tenant Onboarding & Activation

## Requirements & Constraints

- Isolation is structural (schema-per-tenant), never a `tenant_id` column or row-level filter — this is the epic's central risk-reduction property and a deliberate rejection of the shared-tables + `tenant_id` alternative, judged an unacceptable residual leak risk given how much dynamic/user-authored SQL already exists (especially Dataset Custom Query).
- Tenant routing is via a JWT claim, not subdomain; onboarding is admin-provisioned only in this phase (no self-service signup, no billing/plan tiers, no custom domains).
- A tenant creation request must validate `schema_name` with the same identifier/reserved-keyword rules used elsewhere, and reject collisions with existing tenant schema names, both at the app level (friendly error) and via a DB-level uniqueness backstop.
- A new tenant starts at status `Provisioning` and only becomes `Active` after schema creation, migration, and full onboarding succeed; any failure before completion leaves the row at `Provisioning` for a recovery process to flag on startup, never silently retried.
- Only `tenants`, `platform_admins`, and `tenant_user_index` may remain in the `public` schema. Every other table that was previously global — users, roles, user_roles, menus, component_schemas, custom_dataset, all runtime-provisioned dynamic tables, and the Dataset VIEW namespace — must be provisioned per-tenant, never in `public`.
- Tenant context must be resolved from the validated JWT `tenantId` claim before any handler runs (immediately after correlation-ID assignment, before auth/permission checks). A claim referencing a non-existent or non-Active tenant must be rejected with HTTP 401, never a partial/degraded response.
- Login cannot assume a single global `users` table; email → tenant resolution must happen via a lookup table before the tenant-scoped credential check.
- Platform-super-admins must have no implicit access to any tenant's data; their only surface is the tenant-admin API/UI. A tenant's first seeded user receives the tenant-admin role, scoped to that tenant only.
- Every dynamic/static query touching a table this epic relocated must schema-qualify against the resolved tenant context — no code path may resolve a tenant schema independently, and no table name may be hardcoded against `public`.
- A request for a resource (designerId, dataset_name, record) that exists only in another tenant's schema must return HTTP 404, never HTTP 403 — another tenant's resource existence must never be confirmed or denied.
- A dedicated tenant-isolation integration test suite (2+ tenants provisioned, asserting no cross-tenant reads/writes across CRUD, Dataset, Query Builder, MinIO, and admin endpoints) is a release gate — not ordinary coverage — for this epic and for Epics 2, 3, 4, 5, 6, 8, 9, 10, and 11.
- Welcome-email dispatch for a tenant's first user must be best-effort and non-blocking (never fails onboarding on SMTP failure), mirroring the existing admin-created-user email pattern.

## Technical Decisions

- **Tenant data model:** `tenants (id, name, schema_name UNIQUE, status IN ('Provisioning','Active','Suspended'), created_at, created_by)` lives in `public`. `schema_name` reuses the existing safe-identifier validator; `created_by` conceptually references `platform_admins`, never a tenant `users` row.
- **Tenant provisioning (schema + migration only):** a synchronous `ITenantProvisioningService` validates the schema name, inserts the tenant row (status=Provisioning), runs `CREATE SCHEMA`, then applies the full static-schema migration set into it. This is the one genuinely novel mechanism in the epic — no prior art in the codebase for running the migration set against a runtime-chosen schema — so it is scoped to its own story and never advances status past `Provisioning`.
- **Tenant onboarding (everything after schema+migration exist):** creates the tenant's own Dataset VIEW namespace and scopes the dataset preview role's grants to that schema, seeds the tenant-admin role plus first user, fires the welcome-email flow, and only then sets status=Active. A companion `TenantProvisioningRecoveryService`, mirroring the existing table-provisioning recovery pattern, scans for stuck `Provisioning` rows on startup and flags them for admin attention rather than retrying possibly-partial DDL/onboarding.
- **Tenant context:** JWT gains a `tenantId` claim set at login (a user belongs to exactly one tenant in this phase). A new `ITenantContext` middleware, registered right after correlation-ID assignment and before auth, resolves and caches the tenant's schema name, exposes it to both EF (per-request DbContext with dynamic schema) and Dapper (explicit schema name consumed at every query-building call site), and defense-in-depth checks that the tenant still exists and is Active.
- **Login/email routing:** a small `public`-schema `tenant_user_index (email, tenant_id)` table — populated at tenant provisioning and at every user creation — is the one piece of user-identifying data that stays global; it stores no credentials, only a routing pointer.
- **Role split:** the former single global admin role splits into tenant-admin (full admin rights within one tenant's schema, seeded per-tenant) and platform-super-admin (a new tier stored in `public.platform_admins`, able to create/suspend tenants and view the tenant list, with no implicit access to any tenant's data). The first platform-super-admin is seeded via the existing startup bootstrap mechanism, now targeting `platform_admins` instead of a tenant.
- **Cache keys** gain a tenant dimension: schema registry cache keyed by `(tenantId, designerId, version)`, permission cache keyed by `(tenantId, userId)`, dataset allowlist/catalog cache keyed by tenant. Per-user navbar/menu cache needs no key change since underlying data becomes tenant-isolated by schema alone.
- **Cross-cutting scoping:** MinIO object-key prefixes gain a tenant segment (single shared bucket, isolation via prefix + presigned URL scoping); rate-limit partition keys become `(tenantId, userId)`; a startup migration step applies the static-schema migration set to every existing tenant schema in turn (new tenant provisioning reuses the same method). CORS is explicitly unaffected — one application origin serves all tenants under JWT-claim routing.
- **Dataset Manager isolation** requires more than mechanical schema-qualification because Custom Query Mode lets a trusted user author raw SQL: the Dataset VIEW namespace, table allowlist/catalog discovery, the preview role's grants (plus a per-request `search_path` set to the tenant schema), and the SQL generator's FROM clause all become tenant-scoped, with the tenant schema injected server-side only — never accepted from the client or present in persisted builder state.
- Nothing in this epic adds a `tenant_id` column or a `WHERE tenant_id = ...` predicate anywhere — that is a deliberate, explicit non-change.

## Cross-Story Dependencies

- Story 12.2 (Tenant Schema Provisioning) depends on Story 12.1's tenant data model existing first.
- Story 12.7 (Tenant Onboarding & Activation) depends on Story 12.2's schema+migration existing; it owns everything downstream (Dataset Manager scoping, tenant-admin/user seeding, welcome email, status activation, recovery service). Story 12.2 never advances tenant status past `Provisioning` — only 12.7 sets `Active`.
- Story 12.3 (Tenant Context Resolution) and Story 12.4 (Platform-Super-Admin Bootstrap) depend on Stories 12.1, 12.2, and 12.7 (a tenant and its seeded roles must exist to resolve against).
- Story 12.5 (Tenants Admin Page) depends on Stories 12.2 and 12.7's combined provisioning flow, which it triggers and polls for Pending/Active/Error status.
- Story 12.6 (Tenant Context Middleware Integration) depends on Story 12.3 and is the integration point every other epic's data access relies on.
- This epic as a whole must be completed before Epic 2 (Identity, Roles & Permissions) starts, since Epic 2's JWT shape and role model are produced here.
- Epics 3, 4, 5, 6, 8, 9, 10, and 11 all consume the tenant context this epic produces and carry schema-qualification changes as a result; none of them is considered done until the tenant-isolation test suite (introduced here) passes.
- Epic 8 (Dataset Foundation) additionally depends on this epic for its per-tenant Dataset VIEW schema, and Epic 11 depends on it for the tenant-scoped preview connection role.
