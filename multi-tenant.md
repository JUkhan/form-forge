# Multi-Tenancy in FormForge

How tenant isolation works in this application, as implemented by Epic 12 (Stories 12.1–12.7)
and specified by `_bmad-output/planning-artifacts/architecture.md` §7.

---

## 1. The model: schema-per-tenant

Every tenant gets **its own PostgreSQL schema** inside the same database. Isolation is
*structural*, enforced by PostgreSQL's schema resolution and role grants — **not** by an
application-level `WHERE tenant_id = ...` filter.

> There is no `tenant_id` column on any table, and no query anywhere adds a tenant predicate.
> If the ambient schema is wrong, the row simply isn't there. This is the central
> risk-reduction property of the design (architecture.md Decision 7.7).

A consequence worth naming: `designer_id`, `dataset_name` and menu uniqueness are per-tenant
automatically, because each tenant's tables live in a different namespace. No composite keys
were introduced.

### What lives where

| Schema | Contents |
|---|---|
| `public` | **Only** the three platform tables: `tenants`, `platform_admins`, `tenant_user_index` |
| `{tenant_schema}` | `users`, `roles`, `user_roles`, `menus`, `menu_role_assignments`, `component_schemas`, `refresh_tokens`, `password_reset_tokens`, `mfa_backup_codes`, `mfa_sessions`, `schema_audit_log`, `mutation_audit_log`, `custom_dataset`, `dataset_audit_log`, plus every runtime-provisioned dynamic table |
| `{tenant_schema}_datasets` | That tenant's Dataset Manager `VIEW` namespace (replaces the formerly global `datasets` schema) |

The three `public` tables are **pinned explicitly** in the EF model
(`ToTable(name, schema: "public")` in `FormForgeDbContext.OnModelCreating`) so they resolve
correctly no matter what `search_path` the current connection carries.

---

## 2. The two identity tiers

The old single global `platform-admin` role split in two.

### Platform-super-admin

- Rows live in `public.platform_admins (id, user_email, password_hash, created_at)` —
  **never** in any tenant's `users` table.
- No RBAC lookup, no roles/permissions rows, no MFA. The JWT carries a single hardcoded
  `roles: "platform-super-admin"` claim and **never** a `tenantId` claim
  (`JwtTokenService.CreateAccessTokenForPlatformAdmin`).
- **Access-token only** — no refresh-token row is ever written for this tier, so there is no
  silent-refresh path for a platform-super-admin session.
- Can create and list tenants. Has **no** implicit access to any tenant's data: the schema
  boundary applies to this tier too. Tenant-facing route groups explicitly reject it via
  `DenyPlatformSuperAdmin()` (`Common/Endpoints/RouteGroupExtensions.cs`), which is an
  *endpoint filter* rather than a policy so the 403 carries the normal `FORBIDDEN` envelope.
- **Bootstrap:** `Program.cs` seeds a default account into `public.platform_admins` on first
  start, only when the table is empty, and logs the generated credentials once.

### Tenant-admin

What `platform-admin` used to mean, but scoped to one tenant's schema: users, roles, menus,
designers, datasets. The tenant-admin role is a deterministic seeded row
(`00000000-0000-0000-0000-000000000001`) that arrives in every tenant schema via the replayed
static migration set; `TenantOnboardingService` assigns the first user to it rather than
inserting a new role.

---

## 3. Login: finding the tenant before checking the password

Because `users` is per-tenant, login cannot do one global `SELECT * FROM users WHERE email = ...`.
`AuthService.LoginAsync` resolves in a fixed order:

```
1. public.platform_admins WHERE user_email = @email
      hit  -> verify BCrypt, issue platform-super-admin token (no refresh token). TERMINAL.
2. public.tenant_user_index WHERE email = @email        (+ Include(Tenant))
      hit  -> LoginAgainstTenantSchemaAsync: re-validate schema_name via SafeIdentifier,
              reject non-Active tenants, open a FormForgeDbContext whose connection
              SearchPath = that tenant's schema, verify credentials against ITS users table,
              issue a JWT carrying tenantId.
3. public.users  (legacy / pre-12.3 path, unchanged)
      -> issues a claim-less token.
```

`public.tenant_user_index (email PK, tenant_id)` is the one piece of user-identifying data that
must stay global. **It stores no credentials** — only the routing pointer needed to pick a
schema before the credential check runs.

Notable properties of this path:

- Step 3 still exists deliberately. Tenancy was added **additively**, not as a hard cutover, which
  is what keeps the pre-existing integration tests that seed into `public.users` green.
- Every failure branch (bad schema name, inactive tenant, missing user) still runs a BCrypt
  verify against a dummy hash, so no branch is distinguishable by timing.
- `TenantOnboardingService` refuses to onboard a tenant whose first-user email already exists in
  `platform_admins` — otherwise step 1 would shadow that tenant admin permanently.
- MFA is **not** gated on the tenant path: `CompleteMfaLoginAsync` has no schema awareness and
  always queries `public.users`, so wiring a tenant user into an MFA session it could never
  complete was judged worse than skipping the gate. Newly onboarded admins get `MfaEnabled = false`.

### The refresh cookie carries the tenant

The refresh-token cookie value is `"{tenantId}.{secret}"`. The `secret` half is the same random
value hashed into `refresh_tokens.token_hash` as before; the prefix is what lets `RefreshAsync` /
`LogoutAsync` know *which schema's* `refresh_tokens` to query. The prefix is empty (`".{secret}"`)
for the legacy/tenant-less path. A malformed prefix, unknown tenant, non-Active tenant, or a
pre-12.6 cookie is treated as `REFRESH_TOKEN_INVALID`.

---

## 4. Per-request tenant resolution

`TenantContextMiddleware` runs **between `UseAuthentication()` and `UseAuthorization()`**
(`Program.cs:678`) — it needs `HttpContext.User` populated, and it must land before
`RequireAuth` / `RequirePermission`, which execute at the authorization stage.

```
tenantId claim absent  ->  pass through untouched; ITenantContext stays null
                           (anonymous, platform-super-admin, or a legacy token)
claim not a Guid       ->  401  TENANT_INVALID
tenant row not found   ->  401  TENANT_INVALID
tenant status != Active ->  401  TENANT_INACTIVE
otherwise              ->  ITenantContext.Set(tenantId, schemaName)
```

The status re-check is defense-in-depth: a still-valid JWT minted while a tenant was Active must
stop working once it is Suspended.

`ITenantLookupCache` (`IMemoryCache`, keyed `tenant:{tenantId}`) caches the `(SchemaName, Status)`
pair so the middleware doesn't hit `tenants` on every request. It uses **both** a 5-minute sliding
expiration *and* a 5-minute absolute expiration — without the absolute one, a tenant with steady
traffic would never expire its entry and suspension would never take effect.

`ITenantContext` itself is deliberately dumb and **Scoped** — one instance per request, matching
`FormForgeDbContext`'s lifetime. Both `TenantId` and `SchemaName` are null for a no-tenant request;
callers must read that as "no tenant", not as an error.

---

## 5. How the schema actually reaches the database

`HasDefaultSchema` is baked into the compiled EF model and cannot vary per call, so schema
targeting is done entirely through the **connection's `search_path`**, resolved at
**connection-open time** — never at DbContext or factory construction.

That timing matters: `TenantContextMiddleware` resolves `FormForgeDbContext` to look up the
`tenants` row *before* it calls `Set()`, in the same request scope. If the schema were captured at
construction it would be null forever. Because EF Core opens a fresh connection per query under
implicit connection management, every later query passes back through the same hook and sees the
resolved schema.

There are four schema-resolution sites, all driven by `ITenantContext`:

| Path | Component | Resulting `search_path` |
|---|---|---|
| EF Core | `TenantSchemaConnectionInterceptor` (`ConnectionOpening`/`Async`) | `{schema}, public`, or `public` when unresolved |
| Dapper (DDL + dynamic CRUD) | `DbConnectionFactory` (Scoped) | `{schema}, public`, or `public` |
| Dataset preview (least-privilege role) | `PreviewConnectionFactory` (Scoped) | `{schema}_datasets, public` |
| Dataset schema name derivation | `TenantDatasetSchemaResolver` | `{schema}_datasets`, falling back to legacy `datasets` |

`public` is appended to the tenant's schema so the three pinned platform tables stay reachable.
When no tenant is resolved the path is plain `public` — never a redundant `public, public` — and
**no other fallback exists**.

> **A subtle bug that is worth not reintroducing:** `TenantSchemaConnectionInterceptor` rebuilds
> the connection string from the *originally configured* `"formforge"` string captured once at
> construction, never from `connection.ConnectionString` at open time. EF Core reuses the same
> `NpgsqlConnection` object across open/close cycles, and Npgsql's default
> `Persist Security Info=false` strips the password after the first successful open — reading it
> back would silently produce a password-less connection string and fail every open after the first.

Alongside this, every hardcoded `WHERE table_schema = 'public'` / `'datasets'` literal in raw SQL
became a bound parameter fed by the resolved schema — across `DatasetAllowlist`,
`DatasetViewManager`, `DatasetSourceResolver`, `UniqueConstraintService`, `SchemaDriftService`,
`DynamicDataEndpoints`, `DdlEmitter`, and `TableProvisioningService`.

---

## 6. Provisioning a tenant

Creation is **fully synchronous** — a rare admin action, not a request-path hot path — and is
orchestrated by `TenantEndpoints.CreateTenantHandler` over two independently callable services.

```
POST /api/admin/tenants                       (platform-super-admin only)
  |
  |-- validate schema_name via SafeIdentifier  ^[a-z_][a-z0-9_]{0,62}$ + reserved-keyword check
  |-- reject reserved PG schemas               public, pg_catalog, information_schema, pg_temp
  |-- app-level collision pre-check            (ahead of the uq_tenants_schema_name backstop)
  |     ... all three run BEFORE any row is inserted, so an invalid name never creates a row
  |-- INSERT tenants (status = 'Provisioning', created_by = platform admin id)
  |-- generate a CSPRNG temporary password; derive admin@{schema}.tenant.local
  |
  |-- ITenantProvisioningService.ProvisionSchemaAsync      (Story 12.2)
  |     1. re-validate schema_name + reserved check + collision check
  |     2. CREATE SCHEMA "{schema}"                        (raw Dapper DDL)
  |     3. replay the FULL, UNMODIFIED static EF migration set into it, via a dedicated
  |        NpgsqlConnection whose SearchPath targets the new schema — including
  |        __EFMigrationsHistory, which also resolves through the connection
  |
  |-- ITenantOnboardingService.OnboardTenantAsync          (Story 12.7)
  |     1. CREATE SCHEMA "{schema}_datasets"
  |     2. formforge_preview grants scoped to "{schema}" (bulk GRANT SELECT, then the same
  |        guarded per-table REVOKE list and column-level users grant the migrations apply
  |        to public — never GRANT ... IN SCHEMA public any more)
  |     3. seed the first user + UserRole against the seeded tenant-admin role, in-schema
  |     4. best-effort welcome email (3s timeout, catch-and-swallow, never blocks activation)
  |     5. guard: first-user email must not collide with a platform_admins row
  |     6. INSERT public.tenant_user_index (email -> tenant_id)
  |     7. UPDATE tenants SET status = 'Active'
  |
  '-- 201 Created { tenant, temporaryPassword }
```

### Failure handling

- The endpoint's own `catch` sets `status = 'Error'` so a mid-flight failure is visible rather
  than an untracked 500. Response: `500 TENANT_PROVISIONING_FAILED`.
- A client disconnect (`OperationCanceledException` with the token cancelled) is **not** treated as
  a provisioning failure — it propagates, leaving the row at `Provisioning`.
- `TenantProvisioningRecoveryService` (a `BackgroundService`) scans at startup for rows stuck at
  `Provisioning` and **logs a warning for each — it never retries and never mutates status**.
  Re-running DDL that may have partially applied is more dangerous than flagging; an operator
  resolves it manually.
- The DDL steps and the EF seeding step run on separate connections with **no encompassing
  transaction**, so a crash between them can leave grants applied but no seeded user. That is a
  known, accepted half-state.

### Identifier safety

`SafeIdentifier` (the same value type used for `designerId` and table names) is reused as-is for
schema names and is **re-validated at every call site** before interpolation into DDL or a
connection string — `TenantProvisioningService`, `TenantOnboardingService`, `AuthService`'s tenant
login path, and `TenantDatasetSchemaResolver` each validate independently rather than trusting an
upstream check.

One extra rule: `SafeIdentifier` caps names at 63 chars, but appending `_datasets` can push a valid
schema name past PostgreSQL's 63-byte identifier limit — and Postgres *silently truncates* rather
than erroring, which could collide two tenants' dataset namespaces. Both the onboarding service and
the resolver check the combined identifier and fail loudly.

---

## 7. HTTP surface and the admin UI

```
/api/admin/tenants        RequirePlatformSuperAdmin()   GET (paginated list), POST (create)
/api/admin/*              RequirePlatformAdmin()        tenant-admin tier
/api/data/*, /api/datasets  RequireAuth() + DenyPlatformSuperAdmin()
```

`/api/admin/tenants` is mounted as its **own top-level group**, never nested under `/api/admin` —
that group's `platform-admin` policy is one a platform-super-admin JWT never satisfies.

On the web side, `/admin/tenants` (`web/src/routes/admin.tenants.tsx`) is likewise a **standalone
top-level route**, a sibling of `_app.tsx` and `login.tsx` rather than a child of the `_app/admin`
subtree. A platform-super-admin session cannot resolve `_app`'s `beforeLoad` at all — no `tenantId`
claim, and no refresh token to silently refresh with. Its guard is therefore deliberately narrower:
just `tokenStore` plus the JWT `roles` claim, with no `refreshSession()` slow path and no
auth/permissions queries. Session expiry is handled solely by the first API call 401-ing through
`httpClient`'s hard redirect to `/login`. `TenantsPage` composes its own minimal chrome instead of
`AdminLayout`.

The create form takes only **name + schema_name**; the first admin's identity and temporary
password are entirely server-derived. The generated email maps `_` to `-` for the domain label
(`_` is a valid Postgres identifier char but not a valid domain-label char, and browsers' native
`<input type="email">` validation would otherwise block that admin from ever logging in), padding
with a digit if the label would start or end with `-` so distinct schema names can't collapse onto
the same email.

---

## 8. Data model reference

```
public.tenants
  id           uuid   PK  default gen_random_uuid()
  name         varchar(200)  not null
  schema_name  varchar(63)   not null   UNIQUE (uq_tenants_schema_name)
  status       varchar(20)   not null   default 'Provisioning'
                             CHECK (status IN ('Provisioning','Active','Suspended'))
  created_at   timestamptz   not null   default now()
  created_by   uuid   null   -> platform_admins.id, ON DELETE SET NULL, no navigation property

public.platform_admins
  id, user_email, password_hash, created_at

public.tenant_user_index
  email      text  PK   (stored lowercase-normalized)
  tenant_id  uuid  FK -> tenants.id
```

A note on the `PinPlatformTablesToPublicSchema` migration: it **intentionally emits no DDL**. EF
originally proposed `RenameTable(..., newSchema: "public")` for the model-pin delta, which is a
harmless no-op for the main deployment but catastrophic under the per-tenant replay — a literal
`"public"` is not relative to the tenant's schema, so it would try to relocate the tenant-local copy
into the real `public` schema and collide. The migration exists purely to keep the model snapshot in
sync; the model-level pin does the real work at query time.

---

## 9. Known gaps (as of the current `multi-tenant` branch)

These are documented deferrals, not oversights. Sources:
`_bmad-output/implementation-artifacts/deferred-work.md` and each story's Boundaries section.

**The "fresh DI scope loses the tenant" class of bug — now fixed in both known places:**

Both `PermissionService` and `ProvisioningBackgroundService` are Singletons that called
`IServiceScopeFactory.CreateScope()` and then used that scope's `FormForgeDbContext`. A fresh
scope gets its own *unset* `ITenantContext`, so `TenantSchemaConnectionInterceptor` silently
resolved `search_path` to `public`. Each is fixed by a different mechanism, because they get the
tenant from different places:

- **`PermissionService`** computed against `public`, where a tenant user has no `users` /
  `user_roles` rows, so every tenant user's snapshot came back `IsActive=false` with no `roleIds`.
  That hid every permission-gated control in the UI (including the admin gear in `_app.tsx`, whose
  `canSeeAdmin` depends on the seeded `platform-admin` role id appearing in `roleIds`) and 403'd
  every `RequirePermission`-gated route. It now reads the request's tenant via
  `IHttpContextAccessor` → `HttpContext.RequestServices` and replays it onto the child scope. The
  permission cache key gained a tenant segment per Decision 7.6; because the domain events carry
  only a `UserId` and predate tenancy, a `_userCacheKeys` map lets invalidation still find the key.

- **`ProvisioningBackgroundService`** emitted every tenant's `CREATE`/`ALTER TABLE` into `public`.
  It has no `HttpContext` — it drains a queue long after the originating request's scope was
  disposed — so the fix is the one Decision 7.7 specifies: `ProvisioningJob` gained
  `TenantId`/`TenantSchema`, `ProvisioningService` (now **Scoped**) stamps them at enqueue time,
  and the consumer replays them onto its per-job scope before resolving anything. Because
  `FormForgeDbContext`, `DbConnectionFactory` and `DdlEmitter` all read `ITenantContext`, that one
  call redirects the whole DDL pipeline. `ProvisioningRecoveryService`'s startup scan now sweeps
  `public` **plus every Active tenant's schema** (one scope per target, one tenant's failure
  logged without aborting the rest) — previously it could only ever see `public`'s Pending rows.

**Architecture decisions not yet implemented:**

- **7.6 — tenant-keyed caches.** `PermissionService` is now keyed
  `permissions:{tenantId}:{userId}`, but **`SchemaRegistry` still keys on
  `schema:{designerId}:{version}`** and the dataset catalog cache is still global. The
  `SchemaRegistry` one is the sharpest remaining edge: `DdlEmitter.EmitAsync` populates it after a
  successful provision, so two tenants that both have a designer named `foo` v1 share one cache
  entry — whichever provisioned last wins, and the other tenant reads the wrong column set for the
  entry's TTL. Worth fixing before multiple tenants share a deployment.
- **7.9 — MinIO tenant prefix.** Object keys are still `{designerId}/{fieldKey}_...` with no tenant
  segment (`Features/Files/FilesEndpoints.cs`), and menu icons are still `menus/icons/...`.
- **7.9 — rate-limit partitioning.** Partitions are still per-IP / per-user, not `(tenantId, userId)`.
- **7.9 — startup migration fan-out.** `Database.Migrate()` covers `public` only; there is no
  startup loop applying new migrations to every existing tenant schema. Today a tenant's schema
  receives the migration set exactly once, at provisioning time.
  (`ProvisioningRecoveryService` now shows the per-tenant sweep shape this would follow.)
- **7.10 — the tenant-isolation test suite** exists only in the narrow form of
  `TenantSchemaRoutingIntegrationTests`. The full release-gate suite (cross-tenant CRUD, dataset
  catalog, MinIO presigned URLs, admin surfaces) applies once Epics 2–11 are rebuilt on this
  foundation.

**Operational / lifecycle gaps:**

- No tenant **suspend, delete, or reset** endpoint. `Suspended` is a valid status the code honors
  everywhere, but nothing can set it. Once a row reaches `Error`, its `schema_name` is permanently
  unusable because `uq_tenants_schema_name` blocks reuse.
- If `ProvisionSchemaAsync` succeeds and `OnboardTenantAsync` throws, the **created schema is left
  orphaned** — only the tenant row is flagged, there is no schema cleanup path.
- Tenant creation writes **no audit-log entry**, despite being the highest-privilege mutation in
  the system. `Tenant.CreatedBy` is the only trail.
- `AuthService`'s tenant-login path builds an ad hoc `NpgsqlConnection` + `FormForgeDbContext` per
  login. Distinct connection strings mean **Npgsql pools one pool per tenant schema** rather than
  sharing the DI-managed pool. No demonstrated failure yet; the real fix is a pooled per-schema
  DbContext factory.
- The create-tenant request is fully synchronous, so a reverse-proxy timeout can cut it mid-flight.
  Ensure the gateway timeout for this endpoint accommodates full provisioning duration.
- MFA does not work for tenant users (see §3).

---

## 10. File map

**Backend — tenancy core**

```
src/FormForge.Api/Domain/Entities/
  Tenant.cs                          tenants row
  TenantUserIndexEntry.cs            email -> tenant routing
  PlatformAdmin.cs                   platform-super-admin account store

src/FormForge.Api/Features/Tenancy/
  ITenantContext.cs / TenantContext.cs        request-scoped resolved tenant
  TenantContextMiddleware.cs                  JWT claim -> ITenantContext, 401 gates
  TenantLookupCache.cs                        (SchemaName, Status) cache, 5-min TTL
  ITenantProvisioningService.cs / TenantProvisioningService.cs    CREATE SCHEMA + migration replay
  ITenantOnboardingService.cs / TenantOnboardingService.cs        datasets ns, grants, first user, activate
  TenantProvisioningRecoveryService.cs        startup flag-only scan
  TenantDatasetSchemaResolver.cs              {schema}_datasets derivation
  TenantEndpoints.cs                          GET/POST /api/admin/tenants
  Dtos/, Validators/

src/FormForge.Api/Infrastructure/Persistence/
  TenantSchemaConnectionInterceptor.cs        EF search_path
  DbConnectionFactory.cs                      Dapper search_path
  PreviewConnectionFactory.cs                 dataset-preview search_path
  FormForgeDbContext.cs                       public-schema pins in OnModelCreating
  Migrations/…AddTenants, …AddTenantUserIndex, …AddPlatformAdmins,
             …AddTenantErrorStatus, …PinPlatformTablesToPublicSchema

src/FormForge.Api/Features/Auth/
  AuthService.cs                              3-stage login, tenant-prefixed refresh cookie
  JwtTokenService.cs                          tenantId claim, platform-admin token path

src/FormForge.Api/Common/Endpoints/RouteGroupExtensions.cs
  RequirePlatformSuperAdmin(), DenyPlatformSuperAdmin()

src/FormForge.Api/Program.cs
  :113-121  interceptor wiring    :233-256  tenancy DI    :589-608  bootstrap admin
  :678      middleware order      :736-741  /api/admin/tenants group
```

**Frontend**

```
web/src/routes/admin.tenants.tsx
web/src/features/admin/tenants/{TenantsPage,CreateTenantForm,useTenantsQuery,tenantMutations}.tsx|ts
```

**Tests**

```
src/FormForge.Api.Tests/Features/Tenancy/
  TenantContextMiddlewareTests, TenantEndpointsIntegrationTests, TenantIntegrationTests,
  TenantOnboardingServiceTests, TenantProvisioningServiceTests,
  TenantProvisioningRecoveryServiceTests, TenantSchemaRoutingIntegrationTests
src/FormForge.Api.Tests/Features/Auth/
  AuthServiceRefreshTenantTests, TenantAwareLoginIntegrationTests
```

**Specs**

```
_bmad-output/planning-artifacts/architecture.md              §7.1–7.10
_bmad-output/planning-artifacts/epics.md                     Epic 12, FR-74..79
_bmad-output/implementation-artifacts/12-{1..7}-*.md          per-story intent + boundaries
_bmad-output/implementation-artifacts/deferred-work.md        the gaps in §9
```

---

## 11. Rules to keep in mind when extending this

1. **Never add a `tenant_id` column or a `WHERE tenant_id = ...` predicate.** Isolation is
   structural. Adding a predicate creates a second, weaker boundary that will drift.
2. **Never hardcode `'public'` or `'datasets'`** as a schema literal in SQL. Bind the schema
   resolved from `ITenantContext`.
3. **Resolve the schema at connection-open time**, never at construction. See §5.
4. **Re-validate any schema name via `SafeIdentifier` at your own call site** before interpolating
   it into DDL or a connection string, even if an upstream caller already did.
5. **A cross-tenant reference must 404, never 403** — a 403 confirms that Tenant B's resource
   exists. The schema boundary gives you this for free; don't add a check that turns it into a 403.
6. **A fresh DI scope does not inherit the request's tenant.** `IServiceScopeFactory.CreateScope()`
   hands you a brand-new, *unset* `ITenantContext`, and the interceptor will silently fall back to
   `public` — no error, just the wrong schema. If a Singleton must create a scope, replay the
   tenant onto it *before* resolving anything scoped. Two worked examples: within a request, read
   it via `IHttpContextAccessor` (`PermissionService.ComputePermissionsAsync`); across a queue or
   a restart, carry it on the work item (`ProvisioningJob.TenantId`/`TenantSchema`).
7. **Anything queued, scheduled, or retried must carry its tenant explicitly.** By the time it
   runs, the originating request's scope is gone.
8. **Anything new added to `public` needs an explicit `ToTable(name, schema: "public")` pin**, or
   it will be created once per tenant schema by the migration replay.
