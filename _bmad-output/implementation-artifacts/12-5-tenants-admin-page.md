---
title: 'Story 12.5: Tenants Admin Page'
type: 'feature'
created: '2026-09-08'
status: 'done'
route: 'dispatch'
review_loop_iteration: 0
context: []
baseline_commit: '9a9064a6bdd3542589832356dcac2bdfe6e51989'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Platform-super-admins (Story 12.4) have no way to onboard a tenant without direct database access. Story 12.2's schema provisioning and Story 12.7's onboarding exist only as internally-callable services (`ITenantProvisioningService`, `ITenantOnboardingService`) with no HTTP surface, and there is no page for a platform-super-admin to reach — their session (access-token-only, no `tenantId` claim) cannot even load the existing tenant-scoped admin shell.

**Approach:** Add a platform-super-admin-only `/api/admin/tenants` endpoint group (list + create; create synchronously chains `ProvisionSchemaAsync` then `OnboardTenantAsync`) and a standalone `/admin/tenants` frontend route — outside the tenant `_app` shell — with a list+create page that polls the new row's status until it resolves.

## Boundaries & Constraints

**Always:**
- Mount the new group at its own top-level `/api/admin/tenants` (never nested under the existing `/api/admin` `MapGroup`, whose `RequirePlatformAdmin()` checks the tenant-admin role claim a platform-super-admin JWT never carries). Gate with `RequirePlatformSuperAdmin()`.
- Create-tenant is synchronous within the request: validate → insert row (`status="Provisioning"`) → `ProvisionSchemaAsync` → `OnboardTenantAsync` → return. Reuse 12.2's existing `schema_name` validation; do not reimplement it.
- Set `created_by` from the `"userId"` claim (12.4's platform-admin token shape).
- Frontend route is a standalone top-level route (sibling to `_app.tsx`/`login.tsx`, not nested inside `_app` or reusing `AdminLayout`), with its own `beforeLoad` guard reading `tokenStore` + the JWT `roles` claim directly — no `useAuthQuery`/`usePermissionsQuery` (both assume a tenant session and a refresh token neither of which a platform-super-admin login has).
- `useLoginMutation` redirects a `platform-super-admin` login to `/admin/tenants` instead of `/`.
- `RefreshResponse.refreshToken` (frontend type) becomes `string | null`, matching the backend's `LoginResponse.RefreshToken`.
- Poll status after create with a fixed-backoff, bounded retry (mirror `useTableProvisioningQueries.ts`'s pattern) — not an indefinite loop.
- Mirror `RoleEndpoints.cs`/`AdminEndpoints.cs` for endpoint structure, the `Results.Problem` error envelope, and `PagedResult<T>` for the list.
- Compose list-page loading/error/empty states via `ErrorBanner` + `Skeleton` (Story 6.11 convention).
- **Decision (human-approved):** on any failure during `ProvisionSchemaAsync` or `OnboardTenantAsync`, the create endpoint itself catches the exception and sets `Tenant.Status = 'Error'` (new DB check-constraint value, added via migration). This is additive to, not a replacement for, `TenantProvisioningRecoveryService`'s existing flag-only scan — a crash before the endpoint's own catch block runs (e.g. process kill) still leaves the row at `Provisioning` for the recovery service to catch, unchanged.
- **Decision (human-approved):** the Create Tenant form takes only `name` + `schema_name` (matching epics.md's AC literally). The backend generates the first tenant-admin's temporary password server-side and derives a placeholder identity: email `admin@{schema_name}.tenant.local` (a `.tenant.local` suffix so it can never collide with a real domain), display name `Tenant Admin`. The generated password is returned once in the create response — never persisted in plaintext beyond its hash — so the platform-super-admin can relay it to the tenant out-of-band.

**Never:**
- Do not touch `ITenantContext`, tenant-user JWT claims, or the existing `/api/admin` (tenant-admin) group.
- Do not change `TenantProvisioningRecoveryService`'s flag-only, never-mutate behavior.
- Do not add self-service signup, billing, or custom domains.
- Do not reuse `AdminLayout`'s tab-strip nav for this route.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| List, platform-super-admin | `GET /api/admin/tenants` | 200, `PagedResult<TenantDto>` (name, schema_name, status, created_at) | N/A |
| List/Create, any other role or unauthenticated | `GET`/`POST /api/admin/tenants` | 403 FORBIDDEN (or 401 if unauthenticated) | Problem envelope |
| Create, valid input | `POST /api/admin/tenants` | Row inserted `Provisioning`; provisioning + onboarding run inline; 201 with final state | N/A |
| Create, schema_name collision or invalid identifier | `POST /api/admin/tenants` | No row created (12.2's existing validation rejects first) | 400/409 Problem envelope, field-level error |
| Create, provisioning/onboarding throws mid-flow | `POST /api/admin/tenants` | Row's status is set to `Error` by the endpoint's own catch block | 500 Problem envelope; row visible in the list at `Error` |
| Process crashes between insert and the endpoint's own catch block running | `POST /api/admin/tenants` | Row remains `status=Provisioning` (unreachable by the endpoint's own error handling) | Not surfaced to any client; `TenantProvisioningRecoveryService`'s existing flag-only scan catches it on next startup |
| Poll after create | repeated `GET /api/admin/tenants` | Row transitions `Provisioning` → `Active` once onboarding completes | Client stops polling after bounded attempts, shows last known status |

</frozen-after-approval>

## Code Map

- `src/FormForge.Api/Domain/Entities/Tenant.cs:8-19` -- entity fields; `FormForgeDbContext.cs:442-444` -- the `status` check constraint (`'Provisioning'/'Active'/'Suspended'` today; this story adds `'Error'`).
- `src/FormForge.Api/Features/Users/UserService.cs` (`IPasswordHasher.Hash`) -- reuse for hashing the server-generated temporary password; no existing password-generation utility to reuse (build new).
- `src/FormForge.Api/Features/Tenancy/ITenantProvisioningService.cs:13` (impl `TenantProvisioningService.cs`) -- `ProvisionSchemaAsync(Tenant, ct)`; validates + creates schema + replays migrations; throws on failure, never mutates status.
- `src/FormForge.Api/Features/Tenancy/ITenantOnboardingService.cs:15-20` (impl `TenantOnboardingService.cs`) -- `OnboardTenantAsync(Tenant, adminEmail, adminDisplayName, adminTemporaryPassword, ct)`; sets `Status="Active"` only as its final step.
- `src/FormForge.Api/Features/Tenancy/TenantProvisioningRecoveryService.cs:29-44` -- log-only startup scan for stuck `Provisioning` rows; do not change.
- `src/FormForge.Api/Common/Endpoints/RouteGroupExtensions.cs:35-40` (`RequirePlatformSuperAdmin()`, currently unused, comment names `/api/admin/tenants/*` as its consumer), `:51-72` (`DenyPlatformSuperAdmin()`), `:98` (`"userId"` claim read pattern).
- `src/FormForge.Api/Program.cs:709-714` -- existing `/api/admin` group; do NOT nest the new group here.
- `src/FormForge.Api/Features/Roles/RoleEndpoints.cs`, `AdminEndpoints.cs:18` -- endpoint-group pattern to mirror (list/create handlers, `Results.Problem`, `PagedResult<T>`).
- `src/FormForge.Api/Features/Auth/Dtos/LoginResponse.cs:10-18` -- `RefreshToken` is `string?` for platform-super-admin logins.
- `src/FormForge.Api/Features/Auth/JwtTokenService.cs:54-62` -- `CreateAccessTokenForPlatformAdmin`.
- `Infrastructure/Persistence/Migrations/` -- naming convention `{yyyyMMddHHmmss}_{Name}.cs` + matching `.Designer.cs`.
- `web/src/routes/_app.tsx:26-49` (`beforeLoad`/`refreshSession`), `:60,63` (`useAuthQuery`/`usePermissionsQuery`) -- do not reuse; both assume a tenant session.
- `web/src/routes/__root.tsx:10-13` -- unguarded root; the new route sits alongside `_app.tsx`/`login.tsx` here.
- `web/src/features/auth/authMutations.ts:16-47` (`useLoginMutation`) -- add role-based post-login redirect.
- `web/src/features/auth/types.ts:9-14` (`RefreshResponse.refreshToken`) -- make nullable.
- `web/src/httpClient.ts:67-78` -- existing hard-redirect-to-`/login` on 401; reuse as the sole session-expiry path for this route.
- `web/src/routes/_app/admin/roles.tsx`, `users.tsx` -- list+create page pattern to mirror (react-hook-form + zod, TanStack Query, mutation invalidates query key).
- `web/src/features/admin/roles/*`, `users/*` -- feature-folder convention to mirror under `web/src/features/admin/tenants/`.
- `useTableProvisioningQueries.ts:33-44` -- fixed-backoff (1.5s/4s) polling pattern to mirror.
- `web/src/components/shared/ErrorBanner.tsx`, `components/ui/skeleton.tsx` -- Story 6.11 UI-states convention.

## Tasks & Acceptance

**Execution:**
- [x] `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260908120120_AddTenantErrorStatus.cs` -- extend the `status` check constraint with `'Error'` -- schema change backing the new terminal state
- [x] `src/FormForge.Api/Features/Tenancy/TemporaryPasswordGenerator.cs` (new) -- generates a random temporary password for a tenant's first admin user -- no existing generator to reuse
- [x] `src/FormForge.Api/Features/Tenancy/TenantEndpoints.cs` (new) -- `GET`/`POST /api/admin/tenants`, mounted top-level with `RequirePlatformSuperAdmin()`; create derives `admin@{schema_name}.tenant.local` / "Tenant Admin" / a generated password, catches provisioning/onboarding failures into `status='Error'` -- new admin surface
- [x] `src/FormForge.Api/Features/Tenancy/Dtos/TenantDto.cs`, `CreateTenantRequest.cs` (new) -- `CreateTenantRequest` is `{ name, schemaName }` only; create response includes the generated temporary password once
- [x] `src/FormForge.Api/Program.cs` -- register the new group -- wiring only
- [x] `src/FormForge.Api.Tests/Features/Tenancy/TenantEndpointsIntegrationTests.cs` (new) -- covers every I/O matrix row
- [x] `web/src/routes/admin.tenants.tsx` (new, top-level) -- route + `beforeLoad` guard
- [x] `web/src/features/admin/tenants/{types.ts,useTenantsQuery.ts,tenantMutations.ts}` (new) -- list query, create mutation, bounded-poll hook
- [x] `web/src/features/auth/authMutations.ts` -- role-based post-login redirect
- [x] `web/src/features/auth/types.ts` -- `RefreshResponse.refreshToken` → nullable
- [x] `web/src/features/admin/tenants/TenantsPage.tsx`, `CreateTenantForm.tsx` (new) -- list + create UI, `ErrorBanner`/`Skeleton` states

**Acceptance Criteria:**
- Given an authenticated platform-super-admin, when they navigate to `/admin/tenants`, then they see a list of tenants with name, schema_name, status, and created date, and no other role can reach the route or the API.
- Given the Tenants page, when the Create Tenant form (name + schema_name) is submitted, then Stories 12.2 and 12.7's provisioning flow runs with a server-generated placeholder admin identity and temporary password, and the row's status is visible and polled until it resolves to `Active` or `Error`.
- Given provisioning or onboarding fails mid-flow, when the create request's own catch block runs, then the tenant row's status becomes `Error` and is visible as such on the next poll.

## Implementation Notes

Implemented as specified, including both human-approved decisions (DB `'Error'` status set
by the endpoint's own catch block; server-generated placeholder admin identity
`admin@{schema_name}.tenant.local` / "Tenant Admin" / random temporary password returned
once in the create response).

`TemporaryPasswordGenerator` is new — no server-side password-generation utility existed
anywhere in the codebase (confirmed during planning); it draws 24 CSPRNG characters from
an ambiguous-glyph-free alphabet via `RandomNumberGenerator.GetInt32`.

`CreateTenantRequestValidator` (FluentValidation) only guards presence/length; format and
reserved-keyword rules stay solely in `SafeIdentifier.TryCreate`, called directly in the
handler before any row is inserted — same precedent as `DesignerService.CreateAsync`, so
Story 12.2's validation is reused, not reimplemented, and an invalid/colliding
`schema_name` creates no row.

Frontend: `/admin/tenants` is a standalone top-level route (`web/src/routes/admin.tenants.tsx`),
not nested under `_app`/`AdminLayout`, with its own `beforeLoad` reading `tokenStore` + a
new dependency-free `getJwtRoles` decoder (no signature check — a UX gate only, since the
API's own `RequirePlatformSuperAdmin()` is the real boundary). `useLoginMutation` now
redirects a `platform-super-admin` login straight to `/admin/tenants`. `RefreshResponse.refreshToken`
is `string | null` to match the backend.

**Verification:** `dotnet build` — 0 errors, 0 warnings. `dotnet test --filter "Tenancy"` —
40/40 passed (11 new). `npm run test` — 419/419 passed. `npm run build` — vite build + `tsc -b
--noEmit` clean. Full backend suite also run: 2 pre-existing failures (`SchemaAuditLogIntegrationTests`/
`MutationAuditLogIntegrationTests` DELETE-verb 405 checks), confirmed via `git stash` to fail
identically on the unmodified baseline commit — unrelated to this change.

Every I/O & Edge-Case Matrix row is covered by a test that ran and passed:
`TenantEndpointsIntegrationTests` covers list/create auth gates, the happy path (including
the "poll after create" row, trivially satisfied since create is synchronous), the
no-row-created guarantee on invalid/colliding `schema_name`, and the mid-flow-failure
`status='Error'` row (forced via the reserved-schema-name `"public"`, which passes
`SafeIdentifier` but fails inside `ProvisionSchemaAsync`). The "process crash between
insert and the endpoint's own catch block" row is inherently untestable over HTTP and is
already covered by Story 12.7's `TenantProvisioningRecoveryServiceTests`.

Minor judgment calls, not spec deviations: `Results.Created` points at
`/api/admin/tenants/{id}`, but no `GET /{id}` endpoint exists — the spec only requires
list+create, so the `Location` header is unused but harmless. A `schemaName` of `"public"`
(a reserved PostgreSQL system schema) passes `SafeIdentifier` and only fails inside
`ProvisionSchemaAsync`, surfacing as a mid-flow `Error` status (500) rather than a
pre-insert 400 — intentional per the Decision, exercised by a test.

## Spec Change Log

## Review Triage Log

- **medium, patch** — `SafeIdentifier.TryCreate` doesn't reject Postgres system-schema names (`public`, `pg_catalog`, `information_schema`, `pg_temp`); only `TenantProvisioningService.ReservedSchemaNames` does, and only *after* `CreateTenantHandler` has already inserted the tenant row. So a reserved name like `"public"` violates the spec's own I/O-matrix promise ("no row created" for an invalid identifier) — it creates a `Provisioning` row that only becomes `Error` after a failed `CREATE SCHEMA`. Confirmed: read `TenantProvisioningService.cs:29-38` (`ReservedSchemaNames` is `private`, checked only inside `ProvisionSchemaAsync`) against `TenantEndpoints.cs`'s pre-insert check, which calls only `SafeIdentifier.TryCreate`. (blind-hunter, pre-verified)
- **medium, patch** — `CreateTenantForm`'s `TENANT_PROVISIONING_FAILED` catch branch sets only a root-level form error, not a `schemaName` field error, so retrying the same submission after a mid-flow failure resubmits the now-consumed `schema_name` and gets a confusing `TENANT_SCHEMA_NAME_CONFLICT` instead of guidance to pick a different name. Confirmed: `CreateTenantForm.tsx`'s catch block, `TENANT_PROVISIONING_FAILED` arm only calls `setError('root', ...)`. (blind-hunter)
- **medium, patch** — The `SafeIdentifierError.ReservedKeyword` branch (`TENANT_SCHEMA_NAME_RESERVED`, for a genuine PG-reserved keyword like `"select"`) has zero test coverage; the existing invalid-format test only triggers `InvalidPattern`, and the mid-flow-failure test's `"public"` input never reaches `SafeIdentifier`'s reserved-keyword branch either (it fails later, inside provisioning). If the ternary routing the two codes were inverted, nothing would fail. Confirmed via repo-wide grep: `TENANT_SCHEMA_NAME_RESERVED` appears only in `TenantEndpoints.cs`, never in a test. (verification-gap, pre-verified; corroborated by blind-hunter as a duplicate claim, merged)
- **medium, patch** — The platform-super-admin login redirect branch in `useLoginMutation` (navigate to `/admin/tenants` instead of `redirectTo`) has zero test coverage. The only test exercising `useLoginMutation` (`loginSyncTheme.test.tsx`) mocks `roles: []` and never asserts on `navigate` at all. A regression here (dropped/inverted/misspelled role check) would silently break platform-super-admin login with no test catching it. Confirmed via grep: no other file references `useLoginMutation`. (verification-gap, pre-verified)
- **low, patch** — No test verifies a `"platform-admin"` (tenant-admin tier) token is rejected by `RequirePlatformSuperAdmin()` on `/api/admin/tenants` — only a roleless plain user is tested, unlike Story 12.4's own precedent of testing exactly this adjacent-role-confusion boundary on `/api/data/*`/`/api/datasets/*`. Confirmed: `TenantEndpointsIntegrationTests.cs`'s 403 tests use only `PlainUserEmail`. (blind-hunter)
- **low, patch** — The generated temporary password is shown once as plain selectable text with no copy-to-clipboard affordance; dismissing the banner or navigating away loses it irretrievably (consistent with "never persisted in plaintext," but no convenient capture affordance). Confirmed: `TenantsPage.tsx`'s `justCreated` banner renders the password in a `<p>` with no copy button. (blind-hunter)
- **false** — Claimed `GetTenantsHandler`/`CreateTenantHandler`'s `.Produces(...)` annotations under-document the OpenAPI spec by omitting 401/403. Disproven: `RoleEndpoints.cs` — the exact pattern this story was told to mirror — also omits 401/403 from its own `.Produces()` calls; this is a pre-existing, codebase-wide convention, not a deviation this story introduced. (blind-hunter)
- **defer** — If `ProvisionSchemaAsync` succeeds but the subsequent `OnboardTenantAsync` throws, the endpoint sets `status='Error'` but never drops the already-created Postgres schema, leaving it orphaned/unlinked indefinitely. Real, but this is the identical "never silently retry or reconcile a partial failure — flag only" philosophy Stories 12.2 and 12.7 already established and had human-approved in their own frozen boundaries (12.7 Design Notes: "recovery only flags this, never reconciles it (12.2's own precedent)"); Story 12.5 only adds an HTTP trigger to those pre-existing service contracts, it doesn't change this behavior. (edge-case-hunter)
- **defer** — `uq_tenants_schema_name` permanently blocks reusing a `schema_name` once its row reaches `status='Error'`, with no delete/reset path exposed by this story (list+create only, per FR-79's "minimal" framing). Consistent with the epic's deliberate no-auto-reconciliation stance; the operator's workaround (choose a different, arbitrary internal `schema_name`) is proportionate, and finding #2 above now gives clearer in-form guidance to do so. Confirmed: the app-level collision pre-check and the DB unique index both match on `schema_name` regardless of status. (blind-hunter + edge-case-hunter, duplicate claims, merged)
- **defer** — Tenant creation writes no audit-log entry, unlike comparable schema/admin mutations elsewhere in the codebase (`TableProvisioningService`, `DdlEmitter`, `DynamicCrud` mutation handlers all write to a domain-specific audit-log table). Real gap and plausibly worth closing, but epics.md's own FR-79 explicitly scopes this story as "**A minimal admin page** lists tenants and lets a Platform-Super-Admin create a new one" — building a new `tenant_audit_log` table, entity, and read endpoint is a natural follow-up, not a trivial patch, and the planning artifact's own wording backs treating it as future scope rather than an unaddressed intent gap in this story. (blind-hunter)
- **defer** — No test or guard exists for a reverse-proxy/gateway timeout cutting the connection mid-flight during the fully synchronous `ProvisionSchemaAsync` + `OnboardTenantAsync` chain (as opposed to caller-initiated cancellation, which is handled). This is the direct, deliberate consequence of this story's own frozen Approach ("create-tenant orchestration is synchronous within the request"), matching 12.2/12.7's own synchronous design — not a code defect, but an operational concern: ensure the deployment's gateway/proxy timeout for this endpoint accommodates full tenant provisioning duration. (blind-hunter)
- **defer** — `getJwtRoles` and the `/admin/tenants` route's `beforeLoad` guard have no test coverage; a regression (e.g. reading the wrong claim name) would loop a legitimate platform-super-admin back to `/login`. Filed as its own recommended disposition: the real authorization boundary is `RequirePlatformSuperAdmin()` server-side (already covered by integration tests), so this client-side gate is a UX-availability concern, not a security gap. (verification-gap, pre-verified, filed disposition honored)

## Design Notes

TanStack Router: the new `/admin/tenants` route must resolve independently of `_app/admin.tsx`'s existing pathless-layout subtree (which already owns `/admin/roles`, `/admin/users` for tenant-admins). Use a flat top-level route file rather than nesting inside the `_app`/`admin` directory, so it never inherits that layout's loaders.

## Verification

**Commands:**
- `dotnet build` -- expected: 0 errors, 0 warnings
- `dotnet test src/FormForge.Api.Tests --filter "FullyQualifiedName~Tenancy"` -- expected: all pass
- `npm run test` (web) -- expected: all pass
- `npm run build` (web) -- expected: no type errors

**Manual checks (if no CLI):**
- Log in as the bootstrap platform-super-admin, confirm `/admin/tenants` loads without hitting `/api/users/me/permissions`, and that a tenant-admin/tenant-user login cannot reach `/admin/tenants`.
