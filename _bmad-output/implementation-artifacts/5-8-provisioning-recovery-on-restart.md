# Story 5.8: Provisioning Recovery on Restart

Status: done

## Story

As the system,
I re-enqueue all in-flight provisioning jobs on API startup,
So that schema bindings whose provisioning was interrupted by a restart can complete.

## Acceptance Criteria

**AC-1 — Recovery Service Scans and Re-enqueues**
**Given** the API process starts
**When** `ProvisioningRecoveryService.ExecuteAsync` initializes
**Then** it queries `menus WHERE provisioningStatus = 'Pending' AND designerId IS NOT NULL AND boundVersion IS NOT NULL`
**And** for each result, it constructs a `ProvisioningJob(menu.Id, menu.DesignerId, menu.BoundVersion, ActorId: null, FromVersion: null)` and writes it to `Channel<ProvisioningJob>`
**And** logs the count of re-enqueued jobs at `Information` level

**AC-2 — Recovered Job Runs Idempotently**
**Given** a recovered job runs through `ProvisioningBackgroundService.ProcessJobAsync`
**When** the consumer processes it
**Then** the same Stories 5.3/5.4/5.5 DDL logic applies — `CREATE TABLE IF NOT EXISTS` falls through to the ALTER path if the table already exists (safe idempotency from Story 5.3 code review patch D1)
**And** the `ProvisioningStatus` is updated to `Success` or `Error` exactly as for a first-time bind

**AC-3 — Integration Test: End-to-End Restart Simulation**
**Given** a binding has been committed to the database with `provisioningStatus = 'Pending'`
**And** the API process is restarted (simulated by disposing the first factory and starting a second)
**When** the second factory's `ProvisioningRecoveryService` scans the database on startup
**Then** the Pending menu is re-enqueued into the fresh `Channel<ProvisioningJob>`
**And** `ProvisioningBackgroundService` processes it to `Success`

---

## Tasks / Subtasks

- [x] **Task 1 — Create `ProvisioningRecoveryService.cs`** (AC: 1, 2)
  - [x] Create `src/FormForge.Api/Features/Provisioning/ProvisioningRecoveryService.cs`:
    ```csharp
    using System.Threading.Channels;
    using FormForge.Api.Infrastructure.Persistence;
    using Microsoft.EntityFrameworkCore;

    namespace FormForge.Api.Features.Provisioning;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
        Justification = "Registered via AddHostedService.")]
    internal sealed partial class ProvisioningRecoveryService(
        ChannelWriter<ProvisioningJob> writer,
        IServiceScopeFactory scopeFactory,
        ILogger<ProvisioningRecoveryService> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();

            var pendingMenus = await db.Menus
                .AsNoTracking()
                .Where(m => m.ProvisioningStatus == "Pending"
                         && m.DesignerId != null
                         && m.BoundVersion != null)
                .ToListAsync(stoppingToken)
                .ConfigureAwait(false);

            if (pendingMenus.Count == 0)
            {
                LogNoJobsRecovered(logger);
                return;
            }

            LogRecoveringJobs(logger, pendingMenus.Count);

            foreach (var menu in pendingMenus)
            {
                // ActorId = null: the original actor is not recorded on the Menu row; 
                // same accepted limitation as RetryBindingAsync.
                // FromVersion = null: the previous version is unknown at restart;
                // same accepted limitation as RetryBindingAsync.
                var job = new ProvisioningJob(
                    menu.Id,
                    menu.DesignerId!,
                    menu.BoundVersion!.Value,
                    ActorId: null,
                    FromVersion: null);

                await writer.WriteAsync(job, stoppingToken).ConfigureAwait(false);
                LogJobReenqueued(logger, menu.Id, menu.DesignerId!, menu.BoundVersion!.Value);
            }
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "ProvisioningRecoveryService — no pending menus found at startup; nothing to re-enqueue")]
        private static partial void LogNoJobsRecovered(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "ProvisioningRecoveryService — re-enqueuing {Count} pending provisioning job(s) from previous run")]
        private static partial void LogRecoveringJobs(ILogger logger, int count);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "ProvisioningRecoveryService — re-enqueued MenuId {MenuId} DesignerId {DesignerId} v{Version}")]
        private static partial void LogJobReenqueued(ILogger logger, Guid menuId, string designerId, int version);
    }
    ```
  - **Key design points**:
    - `BackgroundService` (not `IHostedService` directly) — consistent with `ProvisioningBackgroundService` and gets the standard lifecycle management.
    - `ExecuteAsync` runs ONCE and exits — it is a startup-only scan, not a loop. The host keeps the service registered but it completes immediately after the scan; this is normal and does not affect the host lifecycle.
    - `IServiceScopeFactory` is required because `FormForgeDbContext` is scoped; the recovery service itself is registered as a hosted service (singleton lifetime). Same pattern as `ProvisioningBackgroundService`.
    - `AsNoTracking()` — read-only scan; no EF tracking needed.
    - `WHERE designerId IS NOT NULL AND boundVersion IS NOT NULL` — defensive guard. In practice, a `Pending` row always has both fields set (the bind API enforces it), but a hand-edited DB row or a future bug could produce a row with null fields; skipping those prevents a `NullReferenceException` in job construction.
    - `stoppingToken` on `WriteAsync` — if the host is shutting down during startup (race condition during tests or abrupt shutdown), the write is cancelled rather than blocking; acceptable because if we're shutting down, we don't need to process the jobs.

- [x] **Task 2 — Register `ProvisioningRecoveryService` in `Program.cs`** (AC: 1)
  - [x] In `src/FormForge.Api/Program.cs`, immediately after the `AddHostedService<ProvisioningBackgroundService>()` line (~line 145), add:
    ```csharp
    // Story 5.8 — on-startup recovery: re-enqueues any menus left Pending by a prior
    // process crash. Registered after ProvisioningBackgroundService so the consumer is
    // already listening when recovery writes to the channel (channel buffers the writes
    // regardless, but this order is semantically cleaner).
    builder.Services.AddHostedService<ProvisioningRecoveryService>();
    ```
  - No `using` statement change needed — `ProvisioningRecoveryService` is in the `FormForge.Api.Features.Provisioning` namespace which is already in scope via the existing `using FormForge.Api.Features.Provisioning;` import in `Program.cs`. Verify the import is present; if not, add it.

- [x] **Task 3 — Tests** (AC: 1–3)
  - [x] Create `src/FormForge.Api.Tests/Features/Provisioning/ProvisioningRecoveryIntegrationTests.cs`
  - Class fixture: `IClassFixture<PostgresFixture>, IAsyncLifetime`
  - **`InitializeAsync`**: create a temporary setup factory (with both `ProvisioningBackgroundService` and `ProvisioningRecoveryService` removed) purely for migrations + TRUNCATE + seed; dispose the setup factory before any test runs. This avoids the standard class-level `_factory` / `_client` fields that the `ProvisioningIntegrationTests` class uses — each recovery test manages its own factories.
  - **`DisposeAsync`**: `Task.CompletedTask` (no persistent factory to clean up).

  | Test | Coverage |
  |---|---|
  | `Recovery_PendingMenuAfterRestart_CompletesToSuccess` | Two-factory AC-3 test: factory1 (no consumer) does bind → Pending; dispose factory1; factory2 (full services) starts → recovery re-enqueues → poll Success |
  | `Recovery_NoPendingMenus_StartsClean` | Factory with full services, no Pending rows; verify normal bind → Success still works (recovery service found 0 Pending rows and exited cleanly without interfering with the normal flow) |
  | `Recovery_PendingMenuIdempotent_TableAlreadyExists` | Factory1 runs a full bind to Success; update menu status back to Pending via direct DB (simulating EF-Dapper dual-write hazard — table exists but row stuck Pending); dispose factory1; factory2 recovers → status goes back to Success; asserts table still exists with same columns (idempotency per AC-2) |

  - **Estimated test count**: 3 integration tests. Total project count goes from 404 → ~407.

---

## Dev Notes

### Architecture compliance — critical constraints

- **`ProvisioningRecoveryService` is a `BackgroundService`** (same pattern as `ProvisioningBackgroundService`). It MUST NOT inject `FormForgeDbContext` directly into its constructor — that would require the scoped context to be used at singleton scope, causing a runtime `InvalidOperationException`. Use `IServiceScopeFactory.CreateScope()` in `ExecuteAsync` exactly as `ProvisioningBackgroundService` does in `ProcessJobAsync`.
- **Channel is bounded to 256** (set in `Program.cs` when `Channel.CreateBounded<ProvisioningJob>(256)` is called). On a system with more than 256 Pending menus at restart, `WriteAsync` would block until the consumer drains slots. This is acceptable — admin-only traffic, bounded capacity, the consumer runs concurrently. Do NOT switch to `TryWrite` (silent drop) or increase capacity without a discussion; 256 is deliberately chosen.
- **`ActorId = null` and `FromVersion = null`** — exactly the same rationale as `RetryBindingAsync` in `MenuService.cs`. The original actor who triggered the bind is not recorded on the `menus` row (only in the audit log). Threading an audit-log lookup through the recovery service is over-engineered for v1 and is documented as a known limitation in `deferred-work.md` (see entry: "Retry ALTER writes `from_version = NULL`"). The recovery service uses the same null values for the same reason.
- **Idempotency is guaranteed by Story 5.3 patch D1** — `DdlEmitter.CreateTableAsync` uses `CREATE TABLE IF NOT EXISTS`, so if the table was already created before the crash, the re-run simply skips table creation and goes to the ALTER path. `AddMissingColumnsAsync` then checks `information_schema.columns` and only adds genuinely missing columns. A fully idempotent re-run (table exists, schema matches) still records an `ALTER` audit row with `ColumnsAdded = []` — this is the Story 5.3 "idempotent no-op ALTER" deferred item; do NOT suppress or special-case it in the recovery service.
- **No frontend work** — Story 5.8 is backend-only. The frontend already handles `Pending` → `Success` state transitions via the polling pattern in `usePollProvisioning` (Story 5.2). After recovery completes, the next poll returns `Success` and the UI updates normally.

### File locations — new files

| New file | Path |
|---|---|
| `ProvisioningRecoveryService.cs` | `src/FormForge.Api/Features/Provisioning/ProvisioningRecoveryService.cs` |
| `ProvisioningRecoveryIntegrationTests.cs` | `src/FormForge.Api.Tests/Features/Provisioning/ProvisioningRecoveryIntegrationTests.cs` |

### File locations — modified files

| Modified file | Change |
|---|---|
| `Program.cs` | Add `AddHostedService<ProvisioningRecoveryService>()` after `AddHostedService<ProvisioningBackgroundService>()` |

### Channel and hosted service timing

Both `ProvisioningBackgroundService` and `ProvisioningRecoveryService` are `BackgroundService` instances. The ASP.NET Core host starts all hosted services concurrently: it calls `StartAsync` on each in registration order, but each `StartAsync` is non-blocking (it spawns a background `Task` and returns). The two services run concurrently:

- `ProvisioningBackgroundService.ExecuteAsync`: blocks on `reader.ReadAllAsync(stoppingToken)`, waiting for items on the channel.
- `ProvisioningRecoveryService.ExecuteAsync`: creates a DB scope, scans Pending rows, writes to `writer`, exits.

The `Channel` buffer (capacity 256) absorbs any jobs written before the consumer starts reading. No explicit ordering or synchronization between the two services is needed — the channel provides it.

**Registration order matters only for semantics**: registering recovery AFTER background service means the consumer is started first (already blocking on `ReadAllAsync`) when recovery writes. This is cleaner but functionally equivalent to the reverse order.

### Integration test design — two-factory pattern

The AC-3 integration test simulates a process restart without actually crashing anything:

**Factory 1 (crash simulation)**:
- Remove `ProvisioningBackgroundService` from DI so no consumer runs.
- Remove `ProvisioningRecoveryService` from DI so recovery doesn't interfere during setup.
- Bind a designer → `ProvisioningStatus = "Pending"` committed to DB; job written to channel buffer.
- Verify status is still "Pending" (no consumer).
- Dispose factory1 (channel is abandoned with the job buffered; DB row stays Pending).

**Factory 2 (recovery)**:
- Standard configuration — all hosted services registered.
- New `Channel.CreateBounded<ProvisioningJob>(256)` is created in the new `Program.cs` startup.
- `ProvisioningRecoveryService.ExecuteAsync` scans DB: finds 1 Pending row, writes 1 job to the NEW channel.
- `ProvisioningBackgroundService.ExecuteAsync` reads the job, processes it → `Success`.
- Poll the menu status via factory2's HTTP client.

**Removing hosted services** in factory1 is done via `IWebHostBuilder.ConfigureServices`:
```csharp
builder.ConfigureServices(services =>
{
    var bg = services.FirstOrDefault(d => d.ImplementationType == typeof(ProvisioningBackgroundService));
    if (bg != null) services.Remove(bg);
    var rec = services.FirstOrDefault(d => d.ImplementationType == typeof(ProvisioningRecoveryService));
    if (rec != null) services.Remove(rec);
});
```
This is the standard pattern for testing hosted services in `WebApplicationFactory`.

**Helper methods** in `ProvisioningRecoveryIntegrationTests` must accept `HttpClient` as a parameter (not use a class-level `_client!`) because each test creates its own factory:
```csharp
private static async Task<string?> LoginAsync(HttpClient client, string email, string password) { ... }
private static async Task<Guid> CreateMenuViaApiAsync(HttpClient client, string token, string name, int order) { ... }
// etc.
```
Copy the implementation bodies from `ProvisioningIntegrationTests.cs` but change instance field references to parameters.

### Dapper-EF dual-write hazard recovery (AC-2 idempotency test)

The third integration test (`Recovery_PendingMenuIdempotent_TableAlreadyExists`) simulates the known Dapper-EF dual-write hazard documented in `deferred-work.md`:

> "Dapper-EF dual-write hazard: table altered but menu Pending forever if the EF commit fails after the Dapper commit succeeds."

The simulation:
1. factory1: bind normally → Success (table created, EF committed).
2. Directly update `menus SET provisioning_status = 'Pending'` via Npgsql connection (bypass the API to simulate the hazard).
3. Dispose factory1.
4. factory2: recovery re-enqueues → DdlEmitter runs → `CREATE TABLE IF NOT EXISTS` is a no-op (table exists) → ALTER path finds 0 columns to add → records an idempotent no-op audit row → EF updates status to Success.
5. Assert status = "Success" and table still exists.

This test validates that the recovery service is the formal mitigation for the Dapper-EF dual-write hazard.

### Deferred item resolved by this story

From `deferred-work.md`:
- **"EnqueueAsync failure post-commit leaves row permanently Pending"** (from 5-2 review): Story 5.8's recovery scanner is the documented formal mitigation — if a crash or `EnqueueAsync` failure leaves a row Pending, the scanner picks it up on next startup.
- **"existingUserColumns snapshot computed outside the DDL transaction"** (from 5-4 review): Mentions "Story 5.8 or a future hardening pass" — this story does NOT address the snapshot timing issue; it only provides the recovery mechanism. The snapshot race is separately deferred.

### Existing `ProvisioningIntegrationTests.cs` — no changes needed

Story 5.8 adds a NEW test file. The existing `ProvisioningIntegrationTests.cs` is not modified. The `PollUntilTerminalAsync` helper in that class is private — replicate the implementation in `ProvisioningRecoveryIntegrationTests.cs`.

### References

- [Source: `src/FormForge.Api/Features/Provisioning/ProvisioningBackgroundService.cs`] — constructor pattern, `IServiceScopeFactory` usage, `LoggerMessage` partials, `SuppressMessage CA1812`. Mirror this pattern exactly.
- [Source: `src/FormForge.Api/Features/Provisioning/ProvisioningJob.cs`] — positional record: `ProvisioningJob(Guid MenuId, string DesignerId, int Version, Guid? ActorId, int? FromVersion = null)`. Named argument syntax `ActorId: null` avoids positional confusion.
- [Source: `src/FormForge.Api/Features/Provisioning/ProvisioningService.cs`] — `ChannelWriter<ProvisioningJob>` injection pattern (same writer the recovery service uses).
- [Source: `src/FormForge.Api/Program.cs:140-145`] — channel creation, `AddSingleton(provisioningChannel.Reader)`, `AddSingleton(provisioningChannel.Writer)`, `AddHostedService<ProvisioningBackgroundService>()`. Recovery service is registered immediately after.
- [Source: `src/FormForge.Api/Domain/Entities/Menu.cs`] — `DesignerId`, `BoundVersion`, `ProvisioningStatus` fields. The EF query filter uses these exact property names.
- [Source: `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs:menus config`] — filtered index `idx_menus_provisioning_status_pending` with `WHERE (provisioning_status = 'Pending')` was created in Story 5.2 specifically for this story's startup scan. The EF query `Where(m => m.ProvisioningStatus == "Pending")` will use it.
- [Source: `src/FormForge.Api.Tests/Features/Provisioning/ProvisioningIntegrationTests.cs`] — `InitializeAsync` (TRUNCATE + drop dynamic tables + seed roles + seed users + create client), `PollUntilTerminalAsync`, `LoginAsync`, `CreateMenuViaApiAsync`, `PutBindingAsync`, `GetMenuAsync`, `CreateAndPublishDesignerAsync` — copy all of these as `private static` methods with `HttpClient` as first parameter.
- [Architecture: AR-52] — "ProvisioningRecoveryService runs on startup, scans menus WHERE provisioningStatus = 'Pending', re-enqueues each."
- [Architecture: Decision 1.6] — EF+Dapper separation; BackgroundService single consumer; recovery is the async safety net for crashes.
- [Architecture: Decision 5.2] — `Channel<ProvisioningJob>` + BackgroundService single-consumer pattern.
- [Architecture: AR-9/AR-37] — single consumer, sequential DDL, no Hangfire/Quartz in v1.
- [`_bmad-output/implementation-artifacts/deferred-work.md`] — "EnqueueAsync failure post-commit" (5-2), "Dapper-EF dual-write hazard" (5-3), "existingUserColumns snapshot outside DDL transaction" (5-4) — Story 5.8's recovery scanner is the documented formal mitigation for the first two.

---

### Review Findings

- [x] [Review][Patch] No test covers the `catch(Exception)` / `LogRecoveryScanFailed` startup failure path — the safety-net deviation added in the Dev Agent Record has zero test coverage; add a test verifying the app starts clean when the DB is unreachable at startup [src/FormForge.Api.Tests/Features/Provisioning/ProvisioningRecoveryIntegrationTests.cs]
- [x] [Review][Patch] `LogRecoveryScanFailed` logs at `Warning` — a complete scan failure leaves all Pending menus unrecovered indefinitely; should be `Error` [src/FormForge.Api/Features/Provisioning/ProvisioningRecoveryService.cs:92]
- [x] [Review][Patch] `PollUntilTerminalAsync` returns the last-observed status (which may be `"Pending"` or `null`) on deadline expiry with no clear timeout signal — causes misleading assertion failures on slow CI [src/FormForge.Api.Tests/Features/Provisioning/ProvisioningRecoveryIntegrationTests.cs:362-374]
- [x] [Review][Patch] `ForceProvisioningStatusAsync` discards the `ExecuteNonQueryAsync` row count — a silent no-op if `menuId` doesn't match any row; assert `rowsAffected == 1` [src/FormForge.Api.Tests/Features/Provisioning/ProvisioningRecoveryIntegrationTests.cs:416-435]
- [x] [Review][Defer] BCrypt cost factor 12 in `SeedTestUsersAsync` adds unnecessary latency to test setup — pre-existing pattern from `ProvisioningIntegrationTests`; use cost 4 in tests [src/FormForge.Api.Tests/Features/Provisioning/ProvisioningRecoveryIntegrationTests.cs] — deferred, pre-existing
- [x] [Review][Defer] Shared `PostgresFixture` across test classes can race during parallel xUnit class execution if multiple provisioning test classes run concurrently — pre-existing pattern [src/FormForge.Api.Tests/Features/Provisioning/ProvisioningRecoveryIntegrationTests.cs] — deferred, pre-existing
- [x] [Review][Defer] No upper bound on Pending menus loaded into memory — `ToListAsync` over an unbounded result set; documented v1 design limitation [src/FormForge.Api/Features/Provisioning/ProvisioningRecoveryService.cs:35-41] — deferred, pre-existing
- [x] [Review][Defer] Multiple process instances starting simultaneously double-scan and enqueue duplicate jobs — single-instance v1 scope [src/FormForge.Api/Features/Provisioning/ProvisioningRecoveryService.cs:35-41] — deferred, pre-existing
- [x] [Review][Defer] Cascade `SetNull` on `DesignerId` leaves zombie `Pending` rows that the WHERE filter skips but never resolves to `Error` — pre-existing design limitation [src/FormForge.Api/Features/Provisioning/ProvisioningRecoveryService.cs:26-29] — deferred, pre-existing
- [x] [Review][Defer] `CreateAndPublishDesignerAsync` produces a designer with zero user columns — test assertions only verify table existence, not schema content; acceptable for recovery-focused tests [src/FormForge.Api.Tests/Features/Provisioning/ProvisioningRecoveryIntegrationTests.cs:333-337] — deferred, pre-existing
- [x] [Review][Defer] Idempotency test does not assert audit log side effects from recovery re-run — beyond AC-2 / Task 3 spec scope [src/FormForge.Api.Tests/Features/Provisioning/ProvisioningRecoveryIntegrationTests.cs] — deferred, pre-existing

---

## Dev Agent Record

### Agent Model Used

Claude Opus 4.7 (1M context) — `claude-opus-4-7[1m]`

### Debug Log References

- Initial full-suite run: 4 unrelated-looking failures in `HealthCheckEndpointsTests` — all `System.ObjectDisposedException: 'Microsoft.AspNetCore.TestHost.TestServer'`. Root cause was my own change: `FormForgeApiFactory` (in `CorrelationIdMiddlewareTests.cs:62`) deliberately points the connection string at an unreachable host (`ignored.invalid:1`) because the middleware tests only exercise the request pipeline and never need PG. `ProvisioningRecoveryService.ExecuteAsync` runs at startup, tries `db.Menus.Where(...)`, the connection times out, and the unhandled exception propagates back to `BackgroundService` → default `HostOptions.BackgroundServiceExceptionBehavior = StopHost` → TestServer is disposed → every subsequent request in that fixture's lifetime fails with `ObjectDisposedException`. The first test that ran (`LiveEndpoint_AlwaysReturns200`) raced ahead of the crash and passed; the four after it got the disposed server.
- Fix: wrap the entire scan in `try / catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; } catch (Exception ex) { LogRecoveryScanFailed(...) }`. Recovery is a safety net — if the scan itself fails (DB unreachable at startup, transient connection error, hand-edited corrupt state) the app MUST still come up so the operator can investigate. The next process start retries the scan. This also matches the failure-tolerance posture of `ProvisioningBackgroundService.ProcessJobAsync` (which records errors on the row rather than crashing the consumer loop). After the fix the same 5 health-check tests pass.

### Completion Notes List

- ✅ AC-1: `ProvisioningRecoveryService.ExecuteAsync` queries `menus WHERE provisioningStatus = "Pending" AND designerId IS NOT NULL AND boundVersion IS NOT NULL` using `AsNoTracking()` (read-only scan) and the existing filtered index `idx_menus_provisioning_status_pending` (created in Story 5.2's migration specifically for this scan — verified in `FormForgeDbContext.cs:205-207`). Logs the result count at `Information` level (`LogRecoveringJobs` if non-empty, `LogNoJobsRecovered` if zero).
- ✅ AC-2: Recovered jobs run through the unchanged `ProvisioningBackgroundService.ProcessJobAsync` and exercise the same `DdlEmitter.EmitAsync` pipeline as a first-time bind. The CREATE-path's `CREATE TABLE IF NOT EXISTS` (Story 5.3 patch D1) plus the ALTER-path's `AddMissingColumnsAsync` (Story 5.4) make the re-run fully idempotent. Verified by `Recovery_PendingMenuIdempotent_TableAlreadyExists`.
- ✅ AC-3: End-to-end restart simulation in `Recovery_PendingMenuAfterRestart_CompletesToSuccess` — factory1 binds with both hosted services removed (via `RemoveProvisioningHostedServices` ConfigureServices hook) so the menu row commits as `Pending` and the channel buffer dies with factory1's container; factory2 starts with full services and the recovery scan re-enqueues the orphaned bind. Final status polled = `"Success"` and the dynamic table is verified to exist in PostgreSQL.
- ✅ Architectural compliance: `ProvisioningRecoveryService` is a `BackgroundService` (matches `ProvisioningBackgroundService` pattern). Constructor injects `ChannelWriter<ProvisioningJob>` (singleton), `IServiceScopeFactory` (singleton), and `ILogger<>` (singleton) — no scoped services are injected directly. `FormForgeDbContext` is resolved per-execution via `scopeFactory.CreateScope()`, never directly into the constructor. `[SuppressMessage("Performance", "CA1812")]` mirrors `ProvisioningBackgroundService` since `AddHostedService` registers via `IHostedService` rather than the concrete type. `ActorId = null` and `FromVersion = null` for each recovered job — same accepted limitation as `RetryBindingAsync`, documented in the deferred-work log (the historical actor and previous version are not stored on the menu row, only in the audit log).
- ✅ Safety net deviation from spec: added a top-level `try/catch` around the entire scan body (NOT in the original spec) because `BackgroundService.ExecuteAsync` exceptions stop the host under .NET's default `BackgroundServiceExceptionBehavior = StopHost`. This is a recovery scanner, not the consumer — if it can't reach the DB at startup, the app must still come up. `OperationCanceledException` triggered by `stoppingToken` is re-thrown so shutdown semantics are preserved. The catch is gated by `#pragma warning disable CA1031` with the same justification comment style as `ProvisioningBackgroundService.cs:72`.
- ✅ Test isolation: `ProvisioningRecoveryIntegrationTests` does NOT carry class-level `_factory`/`_client` fields the way `ProvisioningIntegrationTests` does — each test creates its own `WebApplicationFactory<Program>` (sometimes two) and disposes them inside the test method. `InitializeAsync` uses a throwaway setup factory with both hosted services removed (so no provisioning runs during the TRUNCATE) purely for migrations + table truncation + dynamic-table drop + role/user seeding; that factory is disposed before any test body runs. All HTTP helpers are `static` with `HttpClient` as the first parameter to make the helper signatures explicit about not relying on instance state.
- ✅ Channel buffer GC: factory1 disposing means its `Channel<ProvisioningJob>` (registered as a singleton, owned by factory1's host container) is GC'd along with any buffered jobs. Factory2 creates a fresh `Channel.CreateBounded<ProvisioningJob>(256)` in its own `Program.cs` startup. The Pending menu row is the only persistent state that survives the restart — exactly the invariant the scanner relies on.
- ✅ Documented in story Dev Notes that this story is the formal mitigation for the "EnqueueAsync failure post-commit leaves row permanently Pending" deferred item from Story 5.2 and the Dapper-EF dual-write hazard surfaced in Story 5.3. Both are now actively recoverable on the next process start.
- ✅ Backend test count: 404 (end of Story 5.7 code review) → 407 (+3, matches spec estimate). Full backend suite: `Failed: 0, Passed: 407, Skipped: 0, Total: 407, Duration: 5m22s`.
- ✅ Build is clean: `dotnet build` returns `0 Warning(s) 0 Error(s)` for both `FormForge.Api` and `FormForge.Api.Tests`.

### File List

**New (backend):**
- `src/FormForge.Api/Features/Provisioning/ProvisioningRecoveryService.cs`
- `src/FormForge.Api.Tests/Features/Provisioning/ProvisioningRecoveryIntegrationTests.cs`

**Modified (backend):**
- `src/FormForge.Api/Program.cs` — register `AddHostedService<ProvisioningRecoveryService>()` after the `ProvisioningBackgroundService` registration

### Change Log

| Date | Change | Author |
|---|---|---|
| 2026-05-26 | Story 5.8 implementation complete; 3 integration tests added; full backend suite 407/407 green. | Amelia (Dev) |
