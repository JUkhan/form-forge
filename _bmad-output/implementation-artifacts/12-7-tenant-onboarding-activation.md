---
title: 'Story 12.7: Tenant Onboarding & Activation'
type: 'feature'
created: '2026-09-08'
status: 'done'
route: 'dispatch'
review_loop_iteration: 0
context: []
baseline_commit: '02b51c5df88081a43d3df797a8cbe1458bcb51b2'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Story 12.2's `ITenantProvisioningService.ProvisionSchemaAsync` deliberately stops once a tenant's schema exists and the static migration set has been replayed into it — the tenant row stays `status = 'Provisioning'` forever unless something finishes the job. A tenant in that state has no Dataset Manager namespace, no admin user, and is not usable.

**Approach:** Add `ITenantOnboardingService.OnboardTenantAsync`, a second, independently callable step that assumes Story 12.2's work is already done for the given tenant and finishes onboarding: create the tenant-scoped `{schema_name}_datasets` Dataset VIEW namespace, scope `formforge_preview`'s grants to the tenant's own schema, seed the tenant's first user against the tenant-admin role the migration replay already seeded, fire the existing best-effort welcome-email flow, then set `status = 'Active'`. Add `TenantProvisioningRecoveryService`, mirroring the existing `ProvisioningRecoveryService` startup-scan shape, to flag (never silently retry) any tenant still stuck at `'Provisioning'` when the API starts.

## Boundaries & Constraints

**Always:**
- Treat the incoming `Tenant` as already schema-provisioned and migrated (Story 12.2 complete) — do not re-run `CREATE SCHEMA` or the migration replay.
- Leave `Tenant.Status` at `'Provisioning'` if any onboarding step before the final activation fails — mirrors Story 12.2's own failure contract.
- Never let a welcome-email failure (timeout, SMTP error, unconfigured SMTP) block activation — reuse `IEmailService.TrySendWelcomeEmailAsync`'s existing best-effort contract and the 3-second-timeout + catch-and-swallow pattern used at the admin-created-user call site.
- Reuse the tenant-admin role the static migration set already seeds (deterministic id `00000000-0000-0000-0000-000000000001`, replayed into every tenant schema by Story 12.2) — do not insert a new role row.
- Scope every grant/revoke statement to the tenant's own schema name, quoted independently via the same `SafeIdentifier`-validated value Story 12.2 already validated.

**Never:**
- Do not build an HTTP endpoint or wire this into an admin UI — Story 12.5 consumes both this service and Story 12.2's later.
- Do not touch `ITenantContext`, JWT claims, or login (Story 12.3), or `platform_admins` (Story 12.4).
- Do not make `DatasetSqlGenerator`, `DatasetAllowlist`, `DatasetViewManager`, or `DdlEmitter`'s preview-role grant tenant-schema-aware at query time — that rewiring is Story 12.6's job. This story only provisions the new tenant's own namespace and grants.
- Do not modify the global `public` schema's `datasets` namespace or its existing `formforge_preview` grants.
- Do not rename the seeded role's `name` column value from `"platform-admin"` to `"tenant-admin"` — a cosmetic/terminology change out of scope here.
- Do not implement retry/resume logic in the recovery service — flag only, per FR-75 AC-3.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Happy path | Tenant with schema+migrations already applied (12.2 done); valid admin email/display name/temporary password | `{schema_name}_datasets` schema exists; `formforge_preview` scoped to `{schema_name}` (sensitive tables revoked, `users` column-restricted); first user + tenant-admin `UserRole` exist in the tenant schema; welcome email attempted; `Tenant.Status == 'Active'` | N/A |
| Onboarding DDL/seed step fails | Grant/revoke or user-seed step throws | `Tenant.Status` remains `'Provisioning'`; exception propagates to caller | Exception surfaces; no partial state is hidden |
| Welcome email fails or times out | SMTP unconfigured, connect/auth error, or 3s timeout | Onboarding still completes; `Tenant.Status` still becomes `'Active'` | Failure is caught and logged inside the existing email-send try/catch; never rethrown |
| Duplicate admin email across tenants | `adminEmail` already exists in another tenant's own schema | Succeeds — each tenant's `users` table is schema-isolated, no cross-tenant uniqueness constraint applies | N/A |
| Recovery scan at startup | One or more tenants at `status = 'Provisioning'` | Each is logged at Warning level with `TenantId`/`SchemaName` for admin attention; no DDL or status change is attempted | Scan-level failures (DB unreachable, etc.) are caught and logged; app startup still proceeds |

</frozen-after-approval>

## Code Map

- `Features/Tenancy/ITenantProvisioningService.cs`, `TenantProvisioningService.cs` (Story 12.2) — reuse its patterns (`DbConnectionFactory.CreateOpenConnectionAsync` + Dapper `ExecuteAsync` for raw DDL; schema-scoped `NpgsqlConnectionStringBuilder{SearchPath=...}` + fresh `FormForgeDbContext` for schema-scoped EF work); do not call or modify it — precondition is that it already ran.
- `Infrastructure/Persistence/DbConnectionFactory.cs` — `CreateOpenConnectionAsync(ct)`, `DdlCommandTimeoutSeconds` (60s); reuse for the new DDL.
- Migrations `20260602234849_CreateDatasetManagerFoundation.cs` (lines 137-166) + `20260605164457_RestrictPreviewRoleUsersColumns.cs` — source of the exact `formforge_preview` grant/revoke SQL to replicate schema-qualified against `"{schema_name}"` instead of `public`: bulk `GRANT SELECT ON ALL TABLES`, then guarded per-table `REVOKE SELECT` on `users, roles, refresh_tokens, password_reset_tokens, mfa_backup_codes, mfa_sessions, schema_audit_log, mutation_audit_log, dataset_audit_log, custom_dataset`, then column-level `GRANT SELECT (id, display_name, email, is_active)` on `.users`. Same `IF EXISTS (pg_roles...)` / `to_regclass` guards as the migrations.
- `Features/Auth/EmailService.cs` (`IEmailService.TrySendWelcomeEmailAsync`) + `Features/Users/UserEndpoints.cs` (~131-169) — best-effort call shape to replicate: linked CTS with `CancelAfter(3s)`, catch-and-swallow, never rethrows. No `HttpContext` here — use `smtpOptions.Value.BaseUrl` as `loginBaseUrl`, `tenant.Id.ToString()` as correlation id.
- `Features/Users/UserService.cs` (`CreateUserAsync`, ~line 299) — `User` construction + `IPasswordHasher.Hash(password)` shape to mirror for the tenant's first user.
- Migration `20260523021147_CreateRolesRolePermissionsAndUserRoles.cs` (~100-121) — confirms the tenant-admin role (deterministic id `00000000-0000-0000-0000-000000000001`) already exists in every tenant schema post-replay; do not re-seed it.
- `Domain/Entities/Tenant.cs`, `User.cs`, `UserRole.cs` — `Tenant.Status` is a plain `string`; set to `"Active"` as a literal.
- `Features/Provisioning/ProvisioningRecoveryService.cs` — `BackgroundService` + `[LoggerMessage]` shape to mirror for `TenantProvisioningRecoveryService`, except this one logs-and-flags instead of re-enqueueing (no consumer channel exists for tenant onboarding).
- `Program.cs:227` (`AddScoped<ITenantProvisioningService,...>`) and `:204` (`AddHostedService<ProvisioningRecoveryService>()`) — register the two new services adjacent.
- `FormForge.Api.Tests/Infrastructure/PostgresFixture.cs` — Testcontainers fixture to reuse.

## Tasks & Acceptance

**Execution:**
- [x] `src/FormForge.Api/Features/Tenancy/ITenantOnboardingService.cs` -- define `Task OnboardTenantAsync(Tenant tenant, string adminEmail, string adminDisplayName, string adminTemporaryPassword, CancellationToken ct)` -- narrow contract; caller supplies the first user's identity, same as `CreateUserRequest` requires today (no server-side password generation exists in this codebase)
- [x] `src/FormForge.Api/Features/Tenancy/TenantOnboardingService.cs` -- implement the sequence in Intent/Approach: `CREATE SCHEMA "{schema_name}_datasets"`, scoped `formforge_preview` grant/revoke, seed first user + `UserRole` against the existing tenant-admin role id in a schema-scoped `FormForgeDbContext`, best-effort welcome email, then `Tenant.Status = "Active"` + `SaveChangesAsync`
- [x] `src/FormForge.Api/Features/Tenancy/TenantProvisioningRecoveryService.cs` -- `BackgroundService` scanning `Tenants.Where(t => t.Status == "Provisioning")` at startup, logging each at Warning level; no retry, no status mutation
- [x] `src/FormForge.Api/Program.cs` -- register `ITenantOnboardingService`/`TenantOnboardingService` (scoped) and `TenantProvisioningRecoveryService` (hosted service) -- wiring only
- [x] `src/FormForge.Api.Tests/Features/Tenancy/TenantOnboardingServiceTests.cs` -- integration tests covering the happy path and the email-failure/DDL-failure edge cases in the I/O matrix
- [x] `src/FormForge.Api.Tests/Features/Tenancy/TenantProvisioningRecoveryServiceTests.cs` -- covers the recovery-scan scenario in the I/O matrix

**Acceptance Criteria:**
- Given a tenant whose schema and static tables already exist, when `OnboardTenantAsync` completes successfully, then `information_schema.schemata` contains `"{schema_name}_datasets"` and `formforge_preview` can `SELECT` from `"{schema_name}".users` only its four allowed columns (verified by querying grants, not just absence of exceptions).
- Given onboarding completes successfully, when the tenant schema's `users`/`user_roles` tables are queried, then exactly one user exists with a `UserRole` referencing role id `00000000-0000-0000-0000-000000000001`.
- Given onboarding completes successfully regardless of whether the welcome email succeeded, when the tenant row is re-read, then `Status == "Active"`.
- Given a tenant stuck at `Status == "Provisioning"`, when the API starts, then `TenantProvisioningRecoveryService` logs it and does not change its status or attempt any DDL.

## Implementation Notes

- `UserRole` seeding uses navigation-based fixup (`UserRole.User = adminUser`, not `UserId = adminUser.Id`) because `User.Id` is store-generated (`gen_random_uuid()`) and still the CLR default at construction time; setting `UserId` directly failed `SaveChangesAsync` with an "unknown value" error. Only the navigation lets EF resolve the FK once both rows insert together.
- Activation re-fetches the tenant through the service's own `FormForgeDbContext` (`db.Tenants.FirstOrDefaultAsync(t => t.Id == tenant.Id)`) rather than mutating the caller's `tenant` instance directly, so the update works whether or not that instance is already tracked by this context.
- Full `FormForge.Api.Tests` suite run (1151 tests): 1149 passed, 2 pre-existing failures (`SchemaAuditLogIntegrationTests`/`MutationAuditLogIntegrationTests` DELETE-verb 405 checks) confirmed unrelated via a stash/rerun against the pre-Story-12.7 baseline — same 2 failures occur without this story's changes. Not this story's scope to fix (no Audit code touched).

## Spec Change Log

## Review Triage Log

- **[patch, medium]** `TenantOnboardingServiceTests`'s own `RevokedTables` array (lines 26-31) omits `"mfa_sessions"`, unlike production `TenantOnboardingService.RevokedTables` (lines 41-46) which does revoke SELECT on it for the tenant schema — a future regression that stops revoking `formforge_preview`'s access to `mfa_sessions` would ship with no test catching it. (verification-gap, pre-verified; corroborated by blind-hunter + edge-case-hunter)
- **[patch, medium]** No test builds a `WebApplicationFactory` with the real `AddHostedService<TenantProvisioningRecoveryService>()` registration intact — `TenantProvisioningRecoveryServiceTests.InitializeAsync` unconditionally removes it and drives a manually-constructed instance instead, unlike the precedent `ProvisioningRecoveryIntegrationTests` sets for the sibling service (which has both a real-registration recovery test and a real-registration scan-failure/host-still-starts test). The actual startup wiring is never exercised. (verification-gap, pre-verified)
- **[patch, medium]** `TenantOnboardingService.OnboardTenantAsync` builds `datasetsSchemaName = $"{schemaName}_datasets"` and never re-validates the combined length against Postgres's 63-byte identifier limit, unlike `schemaName` itself (capped at 63 by `SafeIdentifier`'s regex). A `schema_name` of roughly 55+ characters — valid input today — produces a `_datasets` name Postgres silently truncates, risking a cross-tenant `CREATE SCHEMA` collision that fails loudly but confusingly. Confirmed newly introduced by this story (12.2 never appends a suffix to `schemaName`). (blind-hunter + edge-case-hunter, duplicate claims, merged)
- **[patch, low]** `TenantProvisioningRecoveryService`'s warning log message ("...onboarding did not complete...") narrows the diagnosis to Story 12.7's own step, but the class's own leading comment correctly notes a stuck tenant could equally mean Story 12.2's schema-provisioning step crashed before onboarding ever began — the operator-facing message doesn't reflect that ambiguity. Trivial reword, no behavior change. (blind-hunter)
- **[patch, low]** No test exercises "DDL succeeds, EF user-seeding fails" — the specific half-done state `TenantProvisioningRecoveryService`'s own Design Notes cite as its reason for existing. The existing `OnboardTenantAsync_DdlStepFails_...` test only covers DDL failing *before* seeding starts. One additional test (e.g. pre-insert a colliding row in the tenant schema to fault `SaveChangesAsync`) would close this. (blind-hunter)
- **[false]** Claimed `ArgumentException.ThrowIfNullOrEmpty` on `adminEmail`/`adminDisplayName`/`adminTemporaryPassword` lets whitespace-only strings through to be silently persisted, and separately that there's no email-format validation. Disproven as a defect in this story's code: this exactly mirrors `UserService.CreateUserAsync`'s own established pattern (confirmed by reading it), where whitespace and format validation are owned entirely by the FluentValidation validator at the HTTP boundary (`CreateUserRequestValidator`'s `.Must(s => !string.IsNullOrWhiteSpace(s))` and `.EmailAddress()`), not the service method — `UserService.CreateUserAsync` itself has no independent whitespace or format check either. `TenantOnboardingService` has no HTTP endpoint yet (Story 12.5, explicitly out of scope here) and so correctly has no validator either, exactly matching the precedent its own code comment cites ("same as `CreateUserRequest` requires today"). (blind-hunter + edge-case-hunter, duplicate claims, merged)
- **[false]** Claimed `TenantProvisioningRecoveryService`'s lack of a `created_at` age filter risks flagging a tenant that is legitimately mid-onboarding at the instant of a restart as a false positive. Disproven: both `ProvisionSchemaAsync` and `OnboardTenantAsync` are synchronous, awaited-to-completion service calls with no background job queue (unlike the menu-provisioning `Channel<ProvisioningJob>` pattern) — a tenant can only be "mid-onboarding" during the single synchronous call that provisions it, which cannot span a process restart. Any row still at `Provisioning` at startup necessarily means that call crashed or never ran to completion; there is no legitimate concurrently-running state to misdiagnose, mirroring the exact reasoning already established for the precedent `ProvisioningRecoveryService` (Story 5.8). (blind-hunter + edge-case-hunter, duplicate claims, merged)
- **[false]** Claimed no test/guard covers two overlapping `OnboardTenantAsync` calls against the same tenant. Disproven as a defect: not reachable today (no HTTP endpoint exists yet to trigger concurrent calls — Story 12.5), and if it were, `CREATE SCHEMA`'s collision fails loudly and safely, leaving the tenant at `Provisioning` for the recovery service to flag — exactly the documented failure contract, not a new defect. (blind-hunter)
- **[defer]** `epic-12-context.md`'s Requirements & Constraints section dropped the pre-existing "≤100k rows/table scale target... " bullet during this session's context regeneration, without relocating it elsewhere in the file (confirmed via diff). Real content loss, but not part of Story 12.7's frozen Intent or code changes — a side effect of an earlier, separate context-compilation step (`compile-epic-context`), not this story's implementation diff. (blind-hunter + edge-case-hunter, duplicate claims, merged)
- **[defer]** `epic-12-context.md`'s Story 12.5 Cross-Story-Dependencies line still says the admin page "polls for Pending/Active/Error status," inconsistent with the actual `tenants.status` enum (`'Provisioning'`, `'Active'`, `'Suspended'`) documented elsewhere in the same file. Confirmed pre-existing: this exact phrasing is inherited verbatim from `epics.md`'s own Story 12.5 acceptance criteria (unchanged by this diff), not introduced by Story 12.7. (blind-hunter)

## Design Notes

DDL (schema+grants) and EF seeding are separate operations on separate connections, same split as 12.2's create-then-migrate — no encompassing transaction. A crash between them leaves grants applied but no seeded user; recovery only flags this, never reconciles it (12.2's own precedent).

## Verification

**Commands:**
- `dotnet build` -- expected: 0 errors, 0 warnings
- `dotnet test src/FormForge.Api.Tests --filter "TenantOnboardingServiceTests|TenantProvisioningRecoveryServiceTests"` -- expected: all pass, including a real query confirming the `_datasets` schema, scoped grants, and seeded user/role exist (not just "no exception thrown")
