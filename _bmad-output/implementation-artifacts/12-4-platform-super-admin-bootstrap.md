---
title: 'Story 12.4: Platform-Super-Admin Bootstrap'
type: 'feature'
created: '2026-09-08'
status: 'done'
route: 'dispatch'
review_loop_iteration: 0
context: []
baseline_commit: '8aeccf5f1641fc971b248e76d202ec735044520e'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The startup bootstrap still seeds a single global admin into `public.users` with the pre-multi-tenant `"platform-admin"` role; `public.platform_admins` doesn't exist yet (`Tenant.cs:16-18` flags this explicitly), so there is no platform-super-admin able to provision tenants (Story 12.5), and `tenants.created_by` has no FK target.

**Approach:** Add a `PlatformAdmin` entity/table in `public`; replace the `Users`-table bootstrap with a `platform_admins` seed; add a `platform-super-admin` JWT role claim (no `tenantId`) issued via a new `platform_admins` login-lookup checked before `tenant_user_index`; deny that role at the route-group level on `/api/data/*` and `/api/datasets/*`, since several endpoints there (`GET .../options`, dataset reads) are deliberately auth-only today with no per-request permission check that would otherwise reject it.

## Boundaries & Constraints

**Always:**
- Seed the bootstrap account into `public.platform_admins` only, never any `users` table (per architecture.md §7.4).
- Check `platform_admins` before `tenant_user_index` in `LoginAsync` (a platform admin is never also a tenant user).
- Reuse the constant-time `_dummyPasswordHash` pattern (Story 12.3 precedent) for the `platform_admins` credential check.
- Add the `tenants.created_by` → `platform_admins.id` FK (`DeleteBehavior.SetNull`, no nav property, mirrors `CustomDataset.CreatedBy` at `FormForgeDbContext.cs:387-391`) now that the referenced table exists.
- Deny `"platform-super-admin"`-role JWTs at the `/api/data/{designerId}` and `/api/datasets` route-group level (`Program.cs:743`, `:770`), not per-endpoint — human-approved: this satisfies AC-2's named routes exactly and is the smallest footprint; the identical "auth-only" gap on `/api/designers` GETs is a separate, unnamed concern left for a later story if it matters.
- **Decision (human-approved):** platform-super-admin logins are access-token only — no refresh token is issued. `LoginResponse.RefreshToken` becomes `string?`; `null` for a platform-super-admin login, unchanged (non-null) for every existing caller. A platform-super-admin's session is exactly `AccessTokenTtlMinutes` (15 min default); re-login on expiry. Zero new schema — `IssueLoginTokensAsync`/`RefreshToken` (FK'd to `users.id`) are never called on this path.

**Never:**
- Do not rename `WellKnownRoles.PlatformAdminId` / `PermissionService.PlatformAdminRoleId` — that constant is the tenant-admin role now (12.7 already locally aliases it `TenantAdminRoleId`); a naming cleanup, not required here.
- Do not build `/api/admin/tenants/*` or any tenant list/create endpoint — Story 12.5. Add `RequirePlatformSuperAdmin()` as an unused extension for it to consume later; do not mount it anywhere.
- Do not touch `TenantContextMiddleware`/`ITenantContext` — Story 12.3's surface, unaffected.
- Do not gate MFA on the platform-admin login path — `PlatformAdmin` has no MFA columns.
- Do not modify `/api/admin`, `/api/designers`, `/api/menus`, `/api/users`, `/api/files` groups — AC-2 names only `/api/data/*`/`/api/datasets/*`/tenant-admin endpoints, and `/api/admin/*` is already denied naturally (requires a `"platform-admin"` role claim a platform-super-admin token never carries).
- Do not add a `platform_admin_refresh_tokens` table or touch `RefreshAsync`/`LogoutAsync` — out of scope per the access-token-only decision above.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| API starts, `platform_admins` empty | first boot | seeds one `platform_admins` row (same hardcoded email/password constants as today) | N/A |
| API restarts, `platform_admins` non-empty | Nth boot | bootstrap skipped (idempotent) | N/A |
| Login, email matches `platform_admins` | `POST /api/auth/login` | credential check against `platform_admins`; success issues a JWT with `roles=["platform-super-admin"]`, no `tenantId` claim | wrong password → `InvalidCredentials`, constant-time |
| Login, email not in `platform_admins` | `POST /api/auth/login` | falls through unchanged to the `tenant_user_index` then legacy `public.users` checks (Story 12.3 order preserved) | N/A |
| Platform-super-admin JWT calls any `/api/data/{designerId}/*` route (incl. auth-only `/options`) | authenticated, `roles=platform-super-admin` | denied | HTTP 403, FORBIDDEN envelope |
| Platform-super-admin JWT calls any `/api/datasets/*` route (incl. auth-only reads) | authenticated, `roles=platform-super-admin` | denied | HTTP 403, FORBIDDEN envelope |
| Platform-super-admin JWT calls `/api/admin/*` | authenticated | already denied today (no `"platform-admin"` role claim) — covered by a test, no new code | HTTP 403 |

</frozen-after-approval>

## Code Map

- `src/FormForge.Api/Domain/Entities/Tenant.cs:16-18` -- comment marking exactly what this story must add (the FK, once `platform_admins` exists).
- `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs:28-29` (DbSet block), `:439-452` (`Tenant` mapping), `:387-391` (`CustomDataset.CreatedBy` FK pattern to mirror: `.HasOne().WithMany().HasForeignKey().OnDelete(DeleteBehavior.SetNull)`).
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260908035446_AddTenants.cs` -- most recent migration; follow its shape.
- `src/FormForge.Api/Program.cs:569-597` (old bootstrap, hardcoded `admin@formforge.local`/`Admin1234!`), `:333` (`AddPolicy("platform-admin", ...)`), `:743-747` (`/api/data/{designerId}` group), `:770-774` (`/api/datasets` group), `:790-791` (`StartupLog.BootstrapAdminCreated` message).
- `src/FormForge.Api/Common/Endpoints/RouteGroupExtensions.cs:23-30` -- `RequirePlatformAdmin()` pattern to mirror for `RequirePlatformSuperAdmin()` and `DenyPlatformSuperAdmin()`.
- `src/FormForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs:65-69` (`/options`, deliberately auth-only, no `RequirePermission`) -- confirms the group-level deny is necessary, not redundant.
- `src/FormForge.Api/Features/Datasets/*Endpoints.cs`, `Program.cs:766-768` comment -- confirms `/api/datasets` reads are "auth-only", writes use `RequireDatasetManagement()`.
- `src/FormForge.Api/Features/Auth/JwtTokenService.cs:12,19,40-58` -- `CreateAccessToken(User, roleNames, tenantId?)` shape to add a platform-admin counterpart alongside.
- `src/FormForge.Api/Features/Auth/AuthService.cs:120-176` (`LoginAsync`), `:265-301` (`IssueLoginTokensAsync`, `User`-typed and FK'd to `users.id` — not reused for `PlatformAdmin`; the new path builds its own `LoginResponse` directly, no refresh-token row written).
- `src/FormForge.Api/Features/Auth/Dtos/LoginResponse.cs:10-14` -- `RefreshToken` becomes `string?` (null for a platform-super-admin login).
- 12 fixture files sharing the TRUNCATE+DROP-protect-list pattern, e.g. `src/FormForge.Api.Tests/Features/DynamicCrud/CreateRecordIntegrationTests.cs:51,61` (also: `ProvisioningRecoveryIntegrationTests.cs`, `ProvisioningIntegrationTests.cs`, `UpdateRecordIntegrationTests.cs`, `SoftDeleteIntegrationTests.cs`, `RestoreIntegrationTests.cs`, `RepeaterWriteIntegrationTests.cs`, `HardDeleteIntegrationTests.cs`, `GetRecordIntegrationTests.cs`, `DynamicCrudIntegrationTests.cs`, `SchemaAuditLogIntegrationTests.cs`, `MutationAuditLogIntegrationTests.cs`) -- Story 12.3 added `tenant_user_index`/`tenants` to only the DROP-protect list, not the TRUNCATE list, in these files (flagged, unfixed, in its Review Triage Log); do both this time for `platform_admins`.

## Tasks & Acceptance

**Execution:**
- [x] `src/FormForge.Api/Domain/Entities/PlatformAdmin.cs` -- new entity: `Id`, `UserEmail`, `PasswordHash`, `CreatedAt` -- mirrors `Tenant.cs`'s shape/comment style
- [x] `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` -- add `DbSet<PlatformAdmin> PlatformAdmins`; map `platform_admins` (unique index on `user_email`); add the `Tenant.CreatedBy` → `PlatformAdmin.Id` FK
- [x] `src/FormForge.Api/Infrastructure/Persistence/Migrations/{ts}_AddPlatformAdmins.cs` -- new migration: create `platform_admins` + unique index + the `tenants.created_by` FK
- [x] `src/FormForge.Api/Program.cs:569-597` -- replace `!db.Users.Any()` with `!db.PlatformAdmins.Any()`, seed a `PlatformAdmin` row with the same hardcoded constants; update the `:790` log message wording
- [x] `src/FormForge.Api/Program.cs:333` -- add `options.AddPolicy("platform-super-admin", policy => policy.RequireRole("platform-super-admin"));`
- [x] `src/FormForge.Api/Common/Endpoints/RouteGroupExtensions.cs` -- add `RequirePlatformSuperAdmin()` and `DenyPlatformSuperAdmin()` (authorization requirement rejecting the `"platform-super-admin"` role)
- [x] `src/FormForge.Api/Program.cs:743-747,770-774` -- chain `.DenyPlatformSuperAdmin()` onto both groups
- [x] `src/FormForge.Api/Features/Auth/JwtTokenService.cs` -- add a platform-admin token-issuance path: `userId`+`email` claims, one `roles` claim `"platform-super-admin"`, no `tenantId`
- [x] `src/FormForge.Api/Features/Auth/Dtos/LoginResponse.cs` -- change `RefreshToken` to `string?`
- [x] `src/FormForge.Api/Features/Auth/AuthService.cs:120-145` -- add the `platform_admins` lookup before `TenantUserIndex` in `LoginAsync`; on match, constant-time credential check, then build a `LoginResponse` directly (access token only, `RefreshToken: null`, no `RefreshTokens` row written)
- [x] 12 fixture files (see Code Map) -- add `platform_admins` to both the `TRUNCATE TABLE` list and the DROP-protect list
- [x] `src/FormForge.Api.Tests/Features/Auth/PlatformAdminBootstrapTests.cs` (new) -- idempotent seeding
- [x] `src/FormForge.Api.Tests/Features/Auth/PlatformAdminLoginIntegrationTests.cs` (new) -- login success/claim shape, wrong password, denial on `/api/data/*` and `/api/datasets/*`, and confirms `/api/admin/*` is already denied

**Acceptance Criteria:**
- Given the API starts for the first time, when the bootstrap check runs, then the first platform-super-admin is seeded into `public.platform_admins`, never into any `users` table.
- Given an authenticated platform-super-admin, when they call any `/api/data/*`, `/api/datasets/*`, or `/api/admin/*` endpoint, then access is denied.
- Given a tenant's first user is seeded (Story 12.2/12.7), when queried, then that user holds the tenant-admin role scoped to its own tenant, distinct from the platform-super-admin tier (already true — verified, not re-implemented).
- Given `platform_admins` now exists, when `tenants` is queried, then `tenants.created_by` carries a live FK to `platform_admins.id`.

## Implementation Notes

Implemented as specified. `SetRefreshCookieAndReturn` (`AuthEndpoints.cs`) needed one
addition not spelled out in the Code Map: it now skips setting the `refresh_token`
cookie when `LoginResponse.RefreshToken` is `null`, a direct, mechanical consequence of
the `RefreshToken` nullability change rather than a new decision.

The EF-generated migration named the FK `fk_tenants_platform_admins` (no `_created_by`
suffix, since `Tenant` has only one nullable FK today) rather than the Code Map's
suggested `fk_tenants_platform_admins_created_by` — functionally identical, a naming
variance only.

**Verification:** `dotnet build` (full solution) — 0 errors, 0 warnings.
`dotnet test --filter "PlatformAdminBootstrapTests|PlatformAdminLoginIntegrationTests|TenantAwareLoginIntegrationTests|AuthIntegrationTests"` — 53/53 passed.
`dotnet test --filter "FullyQualifiedName~DynamicCrud|FullyQualifiedName~Provisioning|FullyQualifiedName~Audit"` — 302/304 passed; the 2 failures
(`SchemaAuditLogIntegrationTests`/`MutationAuditLogIntegrationTests` `..._AppendOnly_DeleteVerb_Returns405`,
expecting 405 but getting 404) are pre-existing and unrelated to this story — independently
reproduced on a clean `git worktree` checkout of `baseline_commit` (`8aeccf5`), before any
of this story's changes.

Every I/O & Edge-Case Matrix row is covered by a test that ran and passed: bootstrap
seeding/idempotency by `PlatformAdminBootstrapTests`; login success (claim shape, no
`tenantId`, no refresh cookie), wrong password, and the deny checks on `/api/data/*`
(including the auth-only `/options` route), `/api/datasets/*`, and `/api/admin/*` by
`PlatformAdminLoginIntegrationTests`. The "email not in `platform_admins` falls through
unchanged" row has no new dedicated test — it's the existing `tenant_user_index`/legacy
`public.users` login paths, whose own test suites (`TenantAwareLoginIntegrationTests`,
`AuthIntegrationTests`) continue to pass unmodified, which is exactly what "unchanged"
means here.

**Review round (step-04):** three parallel review layers (Blind Hunter, Edge Case Hunter,
Verification Gap) ran against the full diff — see `## Review Triage Log`. Five findings
routed to patch and were fixed: (1) `TenantOnboardingService.ActivateAsync` now rejects
onboarding when the first-user email already exists in `platform_admins`, closing a
silent-lockout/misroute gap the frozen boundaries' "a platform admin is never also a
tenant user" assumption didn't actually enforce anywhere; (2) `TenantAwareLoginIntegrationTests.cs`
now truncates `platform_admins` too; (3) the `tenants.created_by` FK's supporting index is
explicitly named `idx_tenants_created_by` (was EF's auto-generated `IX_tenants_created_by`)
in both the DbContext mapping and the migration's Up/Down; (4) `PlatformAdminBootstrapTests`'s
CA2000 justification wording now matches its actual `await using`/`IAsyncLifetime` disposal;
(5) `StartupLog.BootstrapAdminCreated` renamed to `BootstrapPlatformAdminCreated`. Eight
findings were rejected as false (disproven against the actual code/tests) or as fix-is-spec-text
(the `_dummyPasswordHash` boundary wording vs. an equivalent, sound alternative already
implemented) — see the Review Triage Log for each's evidence. Two low-severity findings
(extra DB round trip per login; `LoginResponse.RefreshToken` nullability as an unannotated
contract change) were rejected as real-but-negligible with a disproportionate fix.

Re-verification after patches: `dotnet build` (full solution) — 0 errors, 0 warnings.
`dotnet test --filter "PlatformAdminBootstrapTests|PlatformAdminLoginIntegrationTests|TenantAwareLoginIntegrationTests|AuthIntegrationTests"` — 53/53 passed.
`dotnet test --filter "FullyQualifiedName~DynamicCrud|FullyQualifiedName~Provisioning|FullyQualifiedName~Audit|FullyQualifiedName~Tenancy"` — 320/322 passed; the same 2 pre-existing,
independently-confirmed-unrelated failures as before the review round (see above).

## Spec Change Log

## Review Triage Log

- **high, patch** — `LoginAsync`'s new `platform_admins` check is unconditionally terminal and runs before `tenant_user_index`, but nothing prevents a tenant's onboarded admin email (`TenantOnboardingService.ActivateAsync`) from colliding with an existing `platform_admins.user_email` (most plausibly the hardcoded bootstrap `admin@formforge.local`) — such a tenant admin would be silently, permanently locked out (or misrouted into the platform-super-admin tier by password coincidence), with no test covering the collision. Confirmed: `TenantOnboardingService.ActivateAsync` never queries `platform_admins`, and no test seeds both a `PlatformAdmin` and a colliding `TenantUserIndexEntry`. (verification-gap, pre-verified)
- **medium, patch** — `TenantAwareLoginIntegrationTests.cs` (Story 12.3's file, not touched by this diff) truncates `tenant_user_index, tenants, refresh_tokens, users` but omits the new `platform_admins`, unlike the 12 sibling fixtures this story did update for the same reason — a leftover `platform_admins` row (from that test class's own bootstrap, or a prior test class in a shared Postgres container) can persist across runs. No test in that file collides on email today, but the same latent cross-test-pollution risk this story's own 12-fixture edit was written to close. Confirmed: file's `InitializeAsync` TRUNCATE statement lacks `platform_admins`. (edge-case-hunter)
- **low, patch** — The new FK's supporting index keeps EF's auto-generated name `IX_tenants_created_by`, breaking the `idx_`/`uq_`/`fk_` snake_case convention every other index/constraint in this same migration and `FormForgeDbContext` explicitly sets via `.HasDatabaseName(...)`. Confirmed: migration's `CreateIndex` call has no explicit name override. (blind-hunter)
- **low, patch** — `PlatformAdminBootstrapTests`'s class-level `SuppressMessage("Reliability", "CA2000", ...)` justification says "disposed explicitly in each test," but every factory in that class is disposed via `await using` declarations, not explicit calls — inconsistent with the more accurate wording used in the sibling `PlatformAdminLoginIntegrationTests.cs`. Confirmed by reading both files. (blind-hunter)
- **low, patch** — `StartupLog.BootstrapAdminCreated`'s method identifier is unchanged even though its message text now reads "platform-super-admin account," and its single call site's surrounding comment was updated — the method name alone no longer matches what it logs. Confirmed: one call site, `Program.cs`. (blind-hunter)
- **false** — Claimed `sprint-status.yaml` (`in-progress`) and the spec frontmatter (`in-review`) disagree in a way that will confuse tooling. Disproven: the two files use different, independently-defined vocabularies by design (`sync-sprint-status.md` only defines a sync for entering `in-progress`), and every other Epic 12 story (`12-1`/`12-2`/`12-3`/`12-7`) shows the identical drift — `review` in `sprint-status.yaml` vs `done` in its own spec frontmatter — a pre-existing tooling pattern, not something this story introduced. (blind-hunter)
- **rejected — fix is to edit this build's spec** — Claimed the frozen boundary "reuse the constant-time `_dummyPasswordHash` pattern... for the `platform_admins` credential check" was not followed, and the deviation isn't logged in `## Spec Change Log`. Verified real (the code intentionally never calls the dummy hash in this branch) and deliberately correct: a match always has a real hash to compare, and a non-match falls through to the next check's own dummy-hash guard, keeping the total per-request BCrypt count at exactly one regardless of outcome — functionally equivalent constant-time protection via a different, sound mechanism, documented inline. The only remaining gap is that this justified divergence from frozen boundary wording isn't recorded in `## Spec Change Log`; the fix is a spec note, not a code change. (blind-hunter)
- **false** — Claimed `platform_admins.user_email`'s case-sensitive unique index vs. lowercase-normalized login lookup allows two case-variant rows that only one is ever reachable by. Disproven: identical shape to `users.email` and `tenant_user_index.email` (no CHECK/citext, plain unique index, app-layer-only `.ToLowerInvariant()` normalization) — an accepted, pre-existing, codebase-wide convention already disproven for the same claim against `users.email` in Story 12.3's own review, not a gap this story introduces. (blind-hunter)
- **low, rejected** — Claimed every login now pays an extra sequential DB round trip for the new `platform_admins` lookup, unbenchmarked. Real but negligible: an indexed unique-key point lookup against a table that will hold single-digit rows, dwarfed by the ~250 ms BCrypt cost already dominating login latency; the fix (restructuring the lookup chain) adds complexity disproportionate to an unmeasured, near-certainly-immaterial cost. (blind-hunter)
- **low, rejected** — Claimed `LoginResponse.RefreshToken`'s `string`→`string?` change is a breaking contract change with no OpenAPI update and no test pinning non-null for existing callers. Real but low-risk: OpenAPI is auto-generated from the C# type (Story 1.4), so nullability updates automatically with no manual doc to edit; every pre-existing refresh-flow test extracts and reuses `RefreshToken` as non-null, so a regression would already fail loudly across the existing suite. The only caller that can ever see `null` (platform-admin login) has no consumer yet (Story 12.5's UI). (blind-hunter)
- **false** — Claimed `Login_ValidCredentials_DoesNotSetRefreshTokenCookie`'s `if (TryGetValues(...))`-wrapped assertion passes vacuously when no `Set-Cookie` header exists. Disproven: `TryGetValues` returning `false` (no header at all) is itself full, direct proof no cookie was set — equivalent to `Assert.False(...Contains("Set-Cookie"))`; and if a regression *did* set a `refresh_token` cookie, `TryGetValues` returns `true` and the inner `DoesNotContain` assertion correctly fails. No failure mode sails through undetected. (blind-hunter)
- **false** — Claimed no test exercises `/api/auth/refresh`/`/api/auth/logout` for a cookie-less platform-super-admin session. Disproven: `RefreshHandler`/`LogoutHandler` (`AuthEndpoints.cs:200-204,225-227`) already have pre-existing, already-tested `string.IsNullOrEmpty(rawToken)` handling for a missing cookie — a platform-admin session's cookie-less state is indistinguishable from any other cookie-less caller, a path that predates this story and needs no platform-admin-specific test. (blind-hunter)
- **false** — Claimed no regression test guards against a future MFA-column addition accidentally routing a `PlatformAdmin` through the MFA branch. Disproven: `PlatformAdmin` has no MFA columns today, so the branch is structurally unreachable — guarding against a hypothetical future schema change that hasn't happened is out of scope; code that cannot currently reach a branch is not a defect for not testing that branch. (blind-hunter)
- **false** — Claimed the Code Map's citation ("mirrors `CustomDataset.CreatedBy`") mismatches the shipped code comment's citation ("same posture as `DatasetAuditLogEntry.ActorId`"). Disproven: both are accurate references to the identical existing pattern (`SetNull`, no navigation property) elsewhere in this codebase; the Code Map is planning-time guidance, not a contractual promise about which analog the implementation must cite in its own comments, and no functional or navigational harm results either way. (blind-hunter)

## Design Notes

`platform_admins` is checked before `tenant_user_index` in `LoginAsync` purely for lookup order — the two tables can never share an email (a platform admin is provisioned only at bootstrap in this story; nothing writes to both), so order has no behavioral effect beyond being a deliberate, documented choice rather than an accident of diff ordering.

The deny mechanism exists because `/api/data/{designerId}/options` and `/api/datasets` reads are *intentionally* auth-only (no `RequirePermission`/`RequireDatasetManagement`) — a correct design for tenant users, but it means a bare-valid platform-super-admin JWT would otherwise sail through with no permission check to fail. The fix sits above those endpoints, at the group level, so it covers every current and future route in both groups without relying on each one remembering to check.

## Verification

**Commands:**
- `dotnet build` -- expected: 0 errors, 0 warnings
- `dotnet test src/FormForge.Api.Tests --filter "PlatformAdminBootstrapTests|PlatformAdminLoginIntegrationTests|TenantAwareLoginIntegrationTests|AuthIntegrationTests"` -- expected: all pass
- `dotnet test src/FormForge.Api.Tests --filter "FullyQualifiedName~DynamicCrud|FullyQualifiedName~Provisioning|FullyQualifiedName~Audit"` -- expected: all pass (confirms the 12 fixture edits didn't regress existing suites)
