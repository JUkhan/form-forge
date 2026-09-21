---
title: 'Seed hidden platform-dev role and per-role Settings tab visibility'
type: 'feature'
created: '2026-09-21'
status: 'done'
route: 'dispatch'
review_loop_iteration: 0
baseline_commit: 'be9378d72aea1fd7649e2d87749b7a8360c47b4f'
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-12-context.md'
---

<frozen-after-approval reason="human-owned intent â€” do not modify unless human renegotiates">

## Intent

**Problem:** Every Settings tab (gear icon) shows for any tenant admin, gated only by the `platform-admin` role. There is no cross-tenant developer identity with a narrower tab set, and nothing keeps such an identity invisible to tenants.

**Approach:** On tenant onboarding, seed a system role `platform-dev` and one developer user holding it. `platform-dev` sees the gear icon and only Roles, Menus, Datasets, Constraints, Table Provisioning, Component Library; `platform-admin` sees only Users, Roles, Menus, Audit Logs. The `platform-dev` role and its users are filtered from every tenant-facing Users, Roles and Menus surface.

## Boundaries & Constraints

**Always:** Tab visibility is enforced server-side on the matching `/api/admin/*` routes as well as hidden in the UI. Hiding is server-side filtering, never client-only. The dev user is created in the tenant schema with a `public.tenant_user_index` row, like the admin user. Onboarding stays idempotent and failure leaves the tenant `Provisioning`.

**Never:** No `tenant_id` columns. No tenant-facing endpoint, count, dropdown or error message may reveal the role or its users. No new self-service way to assign `platform-dev`. No change to the `platform_admins` super-admin flow.

## Decisions

- Dev user is per tenant (one login per tenant schema); no cross-tenant login or tenant-switching. Existing tenants are not backfilled.
- Email is derived per tenant like the admin's (e.g. `dev@{label}.tenant.local`, underscores sanitized); password is CSPRNG via `ITemporaryPasswordGenerator`.
- Email and password are saved on the `public.tenants` row (new columns via migration) and also returned in the platform-super-admin's create-tenant response. The password is stored encrypted (ASP.NET Core Data Protection), never plaintext, never in logs. The tenant list/get DTOs for the Tenants page must not expose them beyond the super-admin create response.
- Tab gating uses role-name checks (`platform-admin` vs `platform-dev`) per `/api/admin` route group and per tab; no new permission keys.
- Tenant admins can never deactivate, reset password/MFA, delete, or reassign the dev user; it is excluded from counts and the last-admin guard.
- `platform-dev` sees the same filtered Users/Roles/Menus lists as tenant admins (role hidden from itself too).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Onboard tenant | POST create tenant | Schema has `platform-dev` role + dev user; tenant Active | Any failure keeps `Provisioning` |
| Tenant admin lists roles/users | GET roles, users, active users | No `platform-dev` role, no dev user, counts exclude them | N/A |
| Assign roles | Admin submits `platform-dev` id to user/menu role assignment | Rejected as if role did not exist | 404/validation same as unknown role |
| Dev user opens Settings | Role `platform-dev` | Gear visible; only 6 listed tabs; other routes redirect | Direct URL to Users/Audit gets 403 |
| Admin opens Settings | Role `platform-admin` | Only Users, Roles, Menus, Audit Logs tabs | Direct URL to Datasets etc. gets 403 |

</frozen-after-approval>

## Code Map

- `Domain/Entities/Tenant.cs` (+ its EF config and a public-schema migration) -- add `dev_user_email`, `dev_user_password_encrypted` columns.

- `src/FormForge.Api/Features/Tenancy/TenantOnboardingService.cs` -- `OnboardTenantAsync` step 2 (~131-152) seeds admin user/role; add dev role+user here; `tenant_user_index` write ~223-227; `platform_admins` email collision check ~213-221.
- `src/FormForge.Api/Features/Tenancy/TenantEndpoints.cs` -- derives admin email (~156), temp password (~73), welcome email (~170-192).
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260523021147_CreateRolesRolePermissionsAndUserRoles.cs` -- seeds `platform-admin`(`...01`), `viewer`(`...02`); new role seed goes in a new EF migration (deterministic GUID, e.g. `...03`). Check `20260908153134_PinPlatformTablesToPublicSchema.cs` does not pin `roles`.
- `src/FormForge.Api/Common/Endpoints/RouteGroupExtensions.cs` -- `RequirePlatformAdmin`, `RequirePermission`; `Program.cs:358-366,738-743` policies and `/api/admin` mount; `Features/Admin/AdminEndpoints.cs:18-28` route groups to split per role.
- `Features/Roles/RoleService.cs:34-82` (`GetRolesAsync`), `Features/Users/UserService.cs` (list ~205-253, get ~288-308, assign ~77-200 incl. last-admin guard ~113-142), `ActiveUsersEndpoints.cs:26-38`, `Features/Menus/MenuService.cs:309` -- filter hidden role/users here.
- `Features/Auth/AuthService.cs` (login via `tenant_user_index` ~233, token issue ~310, refresh ~466); `JwtTokenService.cs:24-37` role claims.
- `web/src/routes/_app.tsx:76,132-143` gear gating; `web/src/routes/_app/admin.tsx:12-30,40-50,101-166` guard, breadcrumbs, hard-coded tabs; `features/auth/usePermission.ts`, `usePermissionsQuery.ts`.
- Tests: `Features/Tenancy/TenantOnboardingServiceTests.cs`, `Users/UserAdminIntegrationTests.cs`, web `routes/_app/__tests__/admin-layout.test.tsx`.
- Do not change: `platform_admins` bootstrap (`Program.cs:601-621`), `AuthService` super-admin branch (~132-170). Existing tenant schemas are not auto-migrated at startup.

## Tasks & Acceptance

**Execution:** (to be finalized after Open Questions are answered)
- [x] EF migration -- seed `platform-dev` system role -- new tenants get it via schema migration
- [x] `Tenant` entity + public migration -- store dev email and encrypted password on `tenants`
- [x] `TenantOnboardingService.cs` / `TenantEndpoints.cs` -- create dev user, UserRole, `tenant_user_index` row, persist credentials, return them in create response
- [x] Role/user/menu services and endpoints -- exclude `platform-dev` role and its users
- [x] `/api/admin` route groups -- gate each by role (admin: users, roles, menus, audit; dev: roles, menus, datasets, constraints, table-provisioning, designer library)
- [x] `web/src/routes/_app/admin.tsx`, `_app.tsx` -- per-role tab set, gear for both, route guards
- [x] Tests -- onboarding, filtering, per-role authorization, admin-layout tabs

**Acceptance Criteria:**
- Given a newly onboarded tenant, when the tenant admin views Users, Roles, and menu role pickers, then no trace of `platform-dev` appears.
- Given a `platform-dev` user, when they open Settings, then exactly Roles, Menus, Datasets, Constraints, Table Provisioning and Component Library are available.
- Given a `platform-admin` user, when they open Settings, then exactly Users, Roles, Menus and Audit Logs are available.

## Implementation Notes

## Spec Change Log

## Review Triage Log

| # | Finding | Verdict | Evidence / route |
|---|---------|---------|------------------|
| 1 | Tenant admin can create/rename a role `platform-dev`; JWT gating is by role name | medium | RoleService create/update not changed; name-only policy. patch |
| 2 | `/api/admin` parent lost its role default; `/designers` group has no group policy (fail-open for new endpoints) | medium | AdminEndpoints.cs, Program.cs:744. patch |
| 3 | Dev can read `/api/admin/data` and designer schema-audit routes; AC says dev gets 403 on Audit | medium | AdminEndpoints.cs, DesignerAdminEndpoints.cs use PlatformAdminOrDev. patch |
| 4 | Dev-authored actions show `ActorName` ("Platform Developer") in tenant admins' audit views, revealing the hidden user | medium | AuditService.cs:178, Schema/Mutation/Dataset audit DTOs store ActorName. resolved by the user: accepted as-is (option c); recorded as a decision. |
| 5 | No test for menu role assignment / menu response filtering of the dev role | medium | verification-gap, pre-verified. patch |
| 6 | No test for per-role gating of shared audit routes, drift/constraints, designer writes | medium | verification-gap, pre-verified. patch |
| 7 | Onboarding: whitespace-only dev email not rejected; no test for dev-email vs platform_admins collision | low | ThrowIfNullOrEmpty. patch |
| 8 | Policy names duplicated as literals in Program.cs instead of AuthPolicies constants | low | direct fix. patch |
| 9 | Existing tenants have no dev user; admins there lose Datasets/Constraints/Table Provisioning/Library | medium | Excluded by decision 3a in the frozen block. defer (recorded in deferred-work.md) |
| 10 | Encrypted dev password has no reveal/rotate path | low | Not requested; keys persist via PersistKeysToDbContext (Program.cs:130). defer |
| 11 | Data Protection keys may not persist | false | Program.cs:130-131 persists keys to the DB. |
| 12 | Migration fails in tenant schemas lacking `tenants` | false | Onboarding and tenant-creation tests provision schemas by replaying the migration set and pass. |
| 13 | Migration Down FK / name-conflict seed skip | low | Rejected: unlikely, fix adds branches. |
| 14 | `isActive !== false` looser than old check | low | Rejected: typed boolean; fix risks fixtures. |
| 15 | `/admin` root section guard, BOM/mojibake in spec, i18n, perf, doc-comment placement | low | Rejected: cosmetic or not reachable in practice. |
| 16 | Gear-link/homePath untested in AppLayout | low | defer, low-risk wiring. |
| 17 | Admin reaches `/designer/library` by direct URL | low | Client-only route; server writes are shared by design. Rejected. |
