---
title: 'Write tenant_user_index on admin user creation'
type: 'bugfix'
created: '2026-09-16'
status: 'done'
route: 'dispatch'
baseline_commit: '12dd241d1444a4ee731d18734e9a29bbb145c286'
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** A tenant admin creating a user via Admin Settings → Users (`POST /api/admin/users`) gets a row in the tenant schema's `users` table but no row in `public.tenant_user_index`, so login cannot resolve that email to a tenant — it falls through to the legacy `public.users` check and returns 401. Architecture Decision 7.3 requires the index to be populated by "the tenant provisioning service **and every user-creation call**"; only the provisioning side was ever implemented.

**Approach:** Make `UserService.CreateUserAsync` write the `tenant_user_index` row in the same `SaveChangesAsync` as the user insert, using `ITenantContext.TenantId`, mirroring `TenantOnboardingService`. Extend the existing duplicate-email pre-check and `23505` catch filters so a globally-taken email returns the documented 409 instead of a 500.

## Boundaries & Constraints

**Always:**
- Index email uses the same normalization as the user row: `Trim().ToLowerInvariant()` — one normalized value written to both tables.
- User row and index row commit atomically (one `SaveChangesAsync`, no explicit transaction needed).
- `public.tenant_user_index` is reached via the existing model-level schema pin; never hardcode a schema or touch `search_path`.
- When `ITenantContext.TenantId` is `null` (legacy non-tenant token), skip the index write and leave today's behavior unchanged.
- Conflict responses stay on the existing generic 409 message — never reveal that the email exists in another tenant.

**Never:**
- No new migration, no schema change, no change to the `tenant_user_index` PK (email is globally unique by design).
- Do not change deactivate/reactivate: the index row stays, because `IsActive` is enforced in the tenant schema after routing.
- Do not touch the web UI, the welcome-email flow, or `AuthService` login logic.
- No new user-facing outcome enum value: cross-tenant and platform-admin collisions reuse `CreateUserOutcome.DuplicateEmail`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Happy path | Tenant-admin JWT with `tenantId`; unused email | 201 Created; `users` row in tenant schema **and** `public.tenant_user_index (email, tenant_id)` row; the new user can log in | N/A |
| Email already in this tenant | Email exists in tenant `users` | 409, unchanged message; no index row written | Existing pre-check |
| Email used by another tenant | Email present in `tenant_user_index` for a different tenant | 409, generic message; no user row written | Pre-check on index + `23505` on `PK_tenant_user_index` |
| Email belongs to a platform admin | Email exists in `public.platform_admins` | 409, generic message; nothing written | Pre-check, mirroring `TenantOnboardingService:213-221` |
| Concurrent duplicate POSTs | Two requests race past the pre-check | Second returns 409, not 500 | `23505` catch extended to `PK_tenant_user_index` (both `DbUpdateException` and bare `PostgresException`) |
| No tenant in context | Legacy token, `TenantId` is null | 201 Created, user row only, no index row | N/A |

## Decisions

- **Scope is the creation path only.** Fix forward: no backfill script and no startup reconciliation for users already created without an index row. Any such account is repaired by re-creating the user (or a manual insert) in the affected environment.

</frozen-after-approval>

## Code Map

- `src/FormForge.Api/Features/Users/UserService.cs:299-363` — `CreateUserAsync`: pre-check at `:309-316`, `db.Users.Add` at `:326`, `SaveChangesAsync` at `:334`, `23505` catches at `:336-350` filtering `uq_users_email` only. All changes land here. Constructor at `:57-60` — add `ITenantContext`.
- `src/FormForge.Api/Features/Tenancy/TenantOnboardingService.cs:213-230` — working precedent: platform-admin collision guard, then `db.TenantUserIndex.Add(new TenantUserIndexEntry { Email = ..., TenantId = ... })`. Reuse the shape; do not modify this file.
- `src/FormForge.Api/Features/Tenancy/ITenantContext.cs:11-22` — `Guid? TenantId`, `string? SchemaName`; scoped (`Program.cs:263`), same lifetime as `UserService` (`Program.cs:176`).
- `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs:29-30,490-503` — `DbSet TenantUserIndex` / `PlatformAdmins`; `ToTable("tenant_user_index", schema: "public")`, PK `PK_tenant_user_index` on `email`, FK to `tenants` (cascade). Entity: `Domain/Entities/TenantUserIndexEntry.cs:9-14`.
- `src/FormForge.Api/Features/Auth/AuthService.cs:178-187` (login reader this unblocks) and `Features/Users/UserEndpoints.cs:97-118` (already maps `DuplicateEmail` → 409) — read-only, no change expected.
- `src/FormForge.Api.Tests/Features/Users/UserAdminIntegrationTests.cs` — create-user cases at `:204,:237,:260`; its TRUNCATE list at `:46` excludes `tenants`/`tenant_user_index` and must be extended before asserting index rows.
- `src/FormForge.Api.Tests/Features/Auth/TenantAwareLoginIntegrationTests.cs:75-118` — reusable `ProvisionTenantAsync` / `SeedTenantUserAsync` helpers for an end-to-end "created user can log in" test.

## Tasks & Acceptance

**Execution:**
- [x] `src/FormForge.Api/Features/Users/UserService.cs` — inject `ITenantContext`; in `CreateUserAsync`, when `TenantId` is non-null, pre-check `db.TenantUserIndex` and `db.PlatformAdmins` for the normalized email (returning `DuplicateEmail`), add the `TenantUserIndexEntry` before `SaveChangesAsync`, and extend both `23505` catch filters to also accept `PK_tenant_user_index` — closes the gap that leaves new users unroutable at login.
- [x] `src/FormForge.Api.Tests/Features/Users/UserAdminIntegrationTests.cs` — extend the TRUNCATE list and cover the matrix: index row written on success, cross-tenant duplicate → 409, platform-admin email → 409, no index row on the duplicate paths.
- [x] `src/FormForge.Api.Tests/Features/Auth/TenantAwareLoginIntegrationTests.cs` — add an end-to-end test: provision a tenant, create a user through `POST /api/admin/users`, then log in as that user and assert 200 with the tenant's `tenantId` claim.

**Acceptance Criteria:**
- Given a tenant admin authenticated with a `tenantId` claim, when they create a user through the Users tab, then the user can immediately log in and receive a JWT carrying that same `tenantId`.
- Given a create-user request that fails with 409, when the database is inspected, then neither a `users` row nor a `tenant_user_index` row exists for that email.
- Given the tenant-onboarding path, when a tenant is provisioned, then its behavior and its single index row are unchanged (no double insert).

## Implementation Notes

- `UserService` now takes `ITenantContext` as a fourth primary-constructor parameter. Nothing
  constructs it by hand (no `new UserService(...)` anywhere in the repo), so DI registration at
  `Program.cs:176` was the only wiring needed — both are Scoped, same lifetime.
- The two `23505` catch filters now share a `IsEmailUniqueViolation(PostgresException)` helper
  that accepts `uq_users_email` **or** `PK_tenant_user_index` (constraint name confirmed against
  `20260908084759_AddTenantUserIndex.cs:25`). Extracting the predicate keeps both catches — the
  wrapped `DbUpdateException` one and the bare-`PostgresException` one — in sync.
- Both index pre-checks (`TenantUserIndex`, `PlatformAdmins`) are skipped entirely when
  `TenantId` is null, so the legacy path issues exactly the same queries it did before.
- No explicit transaction: the user row and the index row are added to the same change tracker
  and flushed by one `SaveChangesAsync`, which EF already wraps in a transaction. Verified by
  the two "409 writes nothing" tests, which assert absence in both tables.
- Test seeding uses `ITenantProvisioningService` + a hand-seeded tenant admin (mirroring
  `TenantAwareLoginIntegrationTests`) rather than `ITenantOnboardingService`, which would also
  create a `{schema}_datasets` namespace and attempt a welcome email — neither is relevant here.
  The tenant admin needs the role row (id `0001`, seeded as `platform-admin` into every tenant
  schema by the static migration replay) so its JWT clears the `/api/admin` group policy.
- `SeedTenantUserAsync` in `TenantAwareLoginIntegrationTests` gained an optional
  `asTenantAdmin` parameter (default `false`); every existing call site is unaffected.

## Spec Change Log

## Review Triage Log

**Iteration 1 — all five findings accepted and fixed:**

1. **Legacy `public.users` shadowing (correctness, `UserService.cs`).** The EF `db.Users`
   duplicate pre-check runs under the tenant's `search_path`, so it cannot see a legacy
   `public.users` row; writing an index row for that address would shadow the legacy account,
   because `AuthService.cs:178` reads `tenant_user_index` before `public.users` at `:189`.
   Added a fourth pre-check inside the same `if (tenantId is not null)` block —
   `db.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT 1 FROM public.users WHERE email = {0}) AS \"Value\"", email)`
   (raw parameterized SQL, matching the repo's existing `SqlQueryRaw<T> ... AS "Value"` idiom)
   returning the same generic `DuplicateEmail`.
2. **No deterministic coverage of the `PK_tenant_user_index` catch branch.** Added
   `CreateUser_AsTenantAdmin_IndexRowRaceLostAtInsert_Returns409AndRollsBackUserRow`: a
   per-test factory removes `DbContextOptions<FormForgeDbContext>` and re-registers it with
   the production `TenantSchemaConnectionInterceptor` plus a test-only
   `SaveChangesInterceptor` that commits the conflicting `public.tenant_user_index` row on a
   separate `NpgsqlConnection` during the race window. Asserts the interceptor fired, 409
   (not 500), and 0 user rows in the acting tenant's schema. (A plain
   `services.AddSingleton<IInterceptor>(...)` is NOT picked up — EF only honors interceptors
   attached to the options, hence the `RemoveAll` + re-`AddDbContext`.)
   Also added `CreateUser_AsTenantAdmin_EmailOwnedByLegacyPublicUser_Returns409AndWritesNothing`
   for finding 1, which additionally asserts the legacy owner can still log in.
3. **`Assert.Empty` over the whole `TenantUserIndex` table** in the legacy-token test scoped
   down to `legacy-created@example.com`.
4. **Brittle/absent body assertions.** Replaced the `DoesNotContain("tenant", ...)` heuristic
   with a shared `AssertGenericEmailConflictAsync` that pins the exact documented envelope
   (`USER_EMAIL_CONFLICT` / `users.emailConflict` / "A user with this email already exists." /
   "User email already exists"), applied to every 409 create test.
5. **Duplicate helper.** `TenantSchemaHasUserAsync` deleted; all call sites use
   `TenantSchemaUserCountAsync`.

Negative controls run for both new guards: narrowing `IsEmailUniqueViolation` back to
`uq_users_email` fails the race test; disabling the `legacyEmailTaken` branch fails the
legacy-shadowing test.

Pass 1 (2026-09-16) — blind-hunter, edge-case-hunter, verification-gap.

| # | Finding | Verdict | Evidence | Route |
|---|---------|---------|----------|-------|
| 1 | Index row shadows a legacy `public.users` account: under tenant context `db.Users` resolves to the tenant schema, so a legacy email passes the duplicate pre-check, and `AuthService.cs:178-187` reads the index **before** `public.users:189` | medium | Confirmed at `AuthService.cs:172-192` — the comment there states the legacy fallback is live and keeps ~40 seeded-in-`public.users` tests passing, so legacy rows genuinely coexist with tenants. Creating a user on such an email reroutes that account's login into the tenant schema and locks its owner out; before this change no index row was written and the legacy login was untouched | patch |
| 2 | Race test `CreateUser_ConcurrentPostsFromTwoTenants_...` passes identically whether the loser hit the widened `PK_tenant_user_index` catch or merely lost to the `emailIndexed` pre-check | medium | Pre-verified by the verification-gap layer; independently true — nothing forces interleaving, and ~250 ms BCrypt per request makes serialization likely on CI. A narrowed filter (500) or split `SaveChanges` (lost atomicity) could ship green | patch |
| 3 | `Assert.Empty(db.TenantUserIndex...ToListAsync())` asserts the whole table is empty rather than that this email has no row | low | Real: passes only by grace of the TRUNCATE sweep; any future shared tenant seeding breaks it spuriously. Fix is a direct correction | patch |
| 4 | `Assert.DoesNotContain("tenant", body)` is brittle and the platform-admin test has no body assertion at all | low | Real asymmetry in a confidentiality check; substring guard would also trip on a ProblemDetails URL containing "tenant". Fix is a direct correction | patch |
| 5 | `TenantSchemaHasUserAsync` is exactly `TenantSchemaUserCountAsync(...) > 0` | low | Real duplication introduced by this change; fix is a direct deletion | patch |
| 6 | Tenant-less path (`TenantId is null`) skips the platform-admin and index pre-checks, so a legacy admin can still create an unloggable account | medium | Real, but the identical outcome predates this change — `CreateUserAsync` had no platform-admin guard at all before. Not caused or exposed by the diff; the tenant-less boundary is also inside the approved frozen block | defer |
| 7 | A user row whose index row is missing cannot be repaired through the API: the `db.Users` pre-check returns 409 first and no user-delete endpoint exists (`UserEndpoints.cs` maps only `/{id}/mfa` for DELETE). The spec's "re-create the user" repair is therefore wrong; only manual SQL works. Same trap in reverse for an own-tenant index row with no user row | medium | Verified: no `MapDelete` for users, and the pre-check at `UserService.cs:309-316` precedes every index write. Pre-existing data state, and an idempotent-repair fix is new behavior the spec does not settle | defer |
| 8 | Index insert can violate `fk_tenant_user_index_tenants` (23503) if the tenant row vanishes mid-request → uncaught 500 | false | No code path deletes a tenant: `MapDelete` exists for roles, files, MFA, designer columns, records, menus and datasets — never tenants — and `Tenants.Remove` has zero matches in `src`. The situation was never shown reachable | rejected |
| 9 | Widened TRUNCATE wipes the startup-seeded bootstrap platform admin without reseeding | low | No test in the class authenticates as the bootstrap admin (all use `admin@example.com`), so no bad outcome occurs today; the fix adds reseed machinery | rejected |
| 10 | `TRUNCATE tenants` leaves physical schemas behind, so a re-run hits "schema already exists" | false | `PostgresFixture.cs:7-17` starts a fresh Testcontainers instance per class per run, and each schema name is used by exactly one test method within a run | rejected |
| 11 | Three collision causes collapse into one 409 with no server-side log; `UserService` injects no `ILogger` | low | Real diagnosability gap, but the fix means a new source-generated logger class plus a constructor dependency — more than a direct correction | rejected |
| 12 | The same role GUID is `PlatformAdminRoleId` in one test file and `TenantAdminRoleId` in the other | low | Cosmetic; both declarations carry comments explaining it is the `platform-admin`-named role seeded into every tenant schema | rejected |
| 13 | Spec mischaracterizes `TenantOnboardingService` as atomic precedent; AC-3 is vacuous; task list and verification counts are stale | n/a | Accurate observations, but every fix edits this build's spec | rejected |
| 14 | AC-2 wording "neither a users row nor a tenant_user_index row exists" — pre-existing rows obviously survive a 409 | n/a | Claim check is fair; the fix is a spec wording edit | rejected |

## Verification

**Commands:**
- `dotnet build` — expected: 0 errors, 0 warnings
- `dotnet test src/FormForge.Api.Tests --filter "UserAdminIntegrationTests|TenantAwareLoginIntegrationTests|TenantOnboardingServiceTests"` — expected: all pass, with the new tests asserting real `public.tenant_user_index` rows (queried, not mocked)

**Results (2026-09-16):**
- `dotnet build` — Build succeeded, 0 warnings, 0 errors.
- Targeted filter (`FullyQualifiedName~` form of the command above) — 52/52 passed, including the
  six new cases.
- Negative control: with the `db.TenantUserIndex.Add(...)` call disabled,
  `CreateUser_AsTenantAdmin_WritesTenantUserIndexRow` and
  `UserCreatedThroughAdminApi_CanLogInImmediately_AndJwtCarriesSameTenantId` both fail — the new
  assertions are not vacuous.
- Full suite (`dotnet test src/FormForge.Api.Tests`) — 1208 passed, 2 failed:
  `SchemaAuditLogIntegrationTests.GetSchemaAuditLog_AppendOnly_DeleteVerb_Returns405` and
  `MutationAuditLogIntegrationTests.GetMutationAuditLog_AppendOnly_DeleteVerb_Returns405`.
  Both fail identically on the unmodified baseline (`12dd241`, verified by stashing this change)
  and are unrelated to user creation or tenancy — pre-existing, not a regression from this work.

**Matrix audit follow-up (2026-09-16):**
- The matrix row "Concurrent duplicate POSTs" had no covering test, so
  `CreateUser_ConcurrentPostsFromTwoTenants_SameEmail_OneCreatedOneConflict` was added. It races
  the same email across **two** tenants on purpose: a same-tenant race trips `uq_users_email`
  first (the `users` insert is ordered before the index insert), so only a cross-tenant race can
  reach the `PK_tenant_user_index` branch of the widened catch filter.
- Negative control: narrowing `IsEmailUniqueViolation` back to `uq_users_email` only makes that
  test fail with the loser returning 500 instead of 409 — the new branch is genuinely exercised.
- Re-run after the addition: `dotnet build` clean; targeted filter 53/53 passed.

**Final verification after the review patch round (2026-09-16):**
- `dotnet build` — 0 warnings, 0 errors.
- Targeted filter — 55/55 passed.
- Full suite — 1211 passed, 2 failed, 1213 total: the same two pre-existing
  `AppendOnly_DeleteVerb_Returns405` audit-endpoint tests, which fail identically at baseline
  `12dd241` and are unreachable from anything this change touches.
- An earlier full-suite run reported 7 failures; the extra 5 were Docker/Testcontainers
  container-start errors under parallel load (the same classes pass in isolation), and they
  disappear when the run is capped at `xUnit.MaxParallelThreads=3`. Infra contention, not
  assertion failures.
