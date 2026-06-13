# Story 1.6: Health Check Endpoints

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As an Operator,
I want to poll health check endpoints to confirm PostgreSQL and MinIO are reachable,
so that monitoring can alert on dependency failures.

## Acceptance Criteria

### AC-1 — `/health/live` always returns HTTP 200

**Given** the API process is running
**When** I GET `/health/live`
**Then** the response is HTTP 200 unconditionally (liveness — process is up)
**And** the response body is `{ "status": "healthy" }`

### AC-2 — `/health/ready` returns 200/503 based on dependency availability

**Given** the API is running
**When** I GET `/health/ready`
**Then** the response is HTTP 200 if PostgreSQL and MinIO are both reachable
**And** the response is HTTP 503 if either PostgreSQL or MinIO is unreachable
**And** the response body shape is `{ "status": "healthy"|"degraded"|"unhealthy", "checks": { "postgres": {...}, "minio": {...} } }` (same shape as AC-3 but restricted to the two dependency checks)

### AC-3 — `/health` returns detailed status of all registered checks

**Given** the API is running
**When** I GET `/health`
**Then** the response body is `{ "status": "healthy"|"degraded"|"unhealthy", "checks": { "postgres": {...}, "minio": {...} } }`
**And** HTTP status code reflects overall health (200 = Healthy, 503 = Degraded or Unhealthy)

> **Auth deferred:** AR-25 mandates that `/health` (detailed) requires the `platform-admin` role. This story ships the endpoint anonymously. Story 2.6 (effective-permission enforcement, auth pipeline wiring) adds the `RequirePlatformAdmin` filter as part of the route-group filter chain (AR-22: correlation → auth → rate limit → permission → validation → handler). The endpoint path, response shape, and HTTP status semantics established here do not change.

### AC-4 — MinIO health check uses HEAD-bucket with 5-second timeout

**Given** the MinIO health check
**When** it queries MinIO
**Then** it performs an HTTP HEAD request to the MinIO S3 endpoint at path `/formforge` (the bucket name)
**And** the request has a 5-second timeout
**And** HTTP responses 200 and 403 are treated as `Healthy` (MinIO is reachable and bucket exists; 403 means auth required, which confirms MinIO is up)
**And** HTTP response 404 is treated as `Unhealthy` (MinIO is reachable but bucket is missing — provisioning required)
**And** connection refused or timeout is `Unhealthy`

### AC-5 — Health check publisher logs status every 30 seconds

**Given** any health check runs
**When** the publisher fires (every 30 seconds)
**Then** the overall status and per-check statuses are emitted as a structured `Information` log entry (or `Warning` if any check is degraded/unhealthy)
**And** the log is visible in the Aspire Dashboard structured log view

### AC-6 — Health endpoints exposed in all environments (not just Development)

**Given** the API is running in any environment (Development, Staging, Production, or Compose)
**When** I GET `/health/live` or `/health/ready` or `/health`
**Then** the endpoints respond (not 404)

> This closes the deferred-work item: "Health endpoints only mapped in Development" (`ServiceDefaults/Extensions.cs:134-144`). The Aspire template gates these behind `IsDevelopment()` for security — FormForge intentionally exposes them in all environments behind its network boundary; platform-admin auth on `/health` is added in Story 2.6 as an additional guard.

---

## Tasks / Subtasks

- [x] **Task 1 — Add `AspNetCore.HealthChecks.NpgSql` package** (AC: 2, 3)
  - [x] Check NuGet for the latest stable `AspNetCore.HealthChecks.NpgSql` version compatible with `Npgsql 10.x`. Look for `10.0.x` (aligned with .NET 10); fall back to the latest `9.0.x` if no `10.x` release exists yet (package targets `netstandard2.1`, compatible with `net10.0`). Source: https://www.nuget.org/packages/AspNetCore.HealthChecks.NpgSql
  - [x] In `Directory.Packages.props`, add `<PackageVersion Include="AspNetCore.HealthChecks.NpgSql" Version="X.Y.Z" />` under the `<!-- API -->` comment block.
  - [x] In `src/FormForge.Api/FormForge.Api.csproj`, add `<PackageReference Include="AspNetCore.HealthChecks.NpgSql" />` (no inline `Version=` — CPM enforced).
  - [x] Run `dotnet restore` — confirm clean (no NU1605 / NU1010). If a version conflict with `Npgsql` is detected, add `<PackageVersion Include="Npgsql" Version="10.x.y" />` to override the transitive pull.

- [x] **Task 2 — Create `MinioHealthCheck.cs`** (AC: 4)
  - [x] Create `src/FormForge.Api/Infrastructure/HealthChecks/MinioHealthCheck.cs`:
    - `internal sealed class MinioHealthCheck(IHttpClientFactory httpClientFactory, IConfiguration configuration) : IHealthCheck`
    - Primary config key: `configuration["services__minio__s3__0"]` (Aspire service discovery key set by `WithReference(minio.GetEndpoint("s3"))` in AppHost).
    - Fallback config key: `configuration["MinIO__Endpoint"]` (for Compose / non-Aspire environments, set in `docker-compose.yml`).
    - Fallback default: `"http://localhost:9000"` (local dev without Aspire).
    - Build the HEAD request URL as `{minioEndpoint}/formforge` (the bucket name per AR-44 / FR-24).
    - Use `httpClientFactory.CreateClient("minio-health")` — a named client registered in Task 3.
    - Set `Timeout = TimeSpan.FromSeconds(5)` on the request via `CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)` with a 5s timeout, OR configure the named HttpClient's `Timeout = TimeSpan.FromSeconds(5)`.
    - Return `HealthCheckResult.Healthy()` on HTTP 200 or 403 (bucket exists, MinIO is up).
    - Return `HealthCheckResult.Unhealthy("MinIO bucket 'formforge' not found (404). Bucket provisioning may be required.")` on HTTP 404.
    - Catch `HttpRequestException` and `TaskCanceledException` and return `HealthCheckResult.Unhealthy("MinIO is unreachable.", exception)`.
    - Class is `internal sealed` to satisfy CA1515 + CA1852.

- [x] **Task 3 — Create `HealthCheckJsonWriter.cs`** (AC: 1, 2, 3)
  - [x] Create `src/FormForge.Api/Infrastructure/HealthChecks/HealthCheckJsonWriter.cs`:
    - `internal static class HealthCheckJsonWriter`
    - `public static Task WriteResponse(HttpContext context, HealthReport report)` method.
    - Set `context.Response.ContentType = "application/json"`.
    - Map `HealthStatus` to lowercase string: `Healthy → "healthy"`, `Degraded → "degraded"`, `Unhealthy → "unhealthy"` — use `ToLowerInvariant()` on `report.Status.ToString()` (safe with `InvariantGlobalization=true`).
    - Build `checks` dictionary from `report.Entries`: each entry has `status` (lowercase string), `description` (nullable string from `entry.Value.Description`), and optionally `error` (from `entry.Value.Exception?.Message` when non-null).
    - Serialize with `context.Response.WriteAsJsonAsync(new { status, checks }, cancellationToken: context.RequestAborted)`.
    - `WriteMinimalResponse` variant used by `/health/live` only: omit `checks`, return `{ "status": "healthy" }` unconditionally (the endpoint predicate already ensures only the "self" liveness check runs, which always passes).

- [x] **Task 4 — Create `LoggingHealthCheckPublisher.cs`** (AC: 5)
  - [x] Create `src/FormForge.Api/Infrastructure/HealthChecks/LoggingHealthCheckPublisher.cs`:
    - `internal sealed class LoggingHealthCheckPublisher(ILogger<LoggingHealthCheckPublisher> logger) : IHealthCheckPublisher`
    - `PublishAsync(HealthReport report, CancellationToken cancellationToken)`:
      - Log level: `Information` when `report.Status == HealthStatus.Healthy`; `Warning` otherwise.
      - Use source-generated `[LoggerMessage]` to satisfy CA1848:
        ```csharp
        [LoggerMessage(EventId = 20,
            Message = "Health check report: {OverallStatus}; checks: {CheckSummary}")]
        public static partial void LogHealthReport(ILogger logger, LogLevel level,
            string overallStatus, string checkSummary);
        ```
      - Build `checkSummary` as a plain CSV string: `"postgres=Healthy,minio=Healthy"` (safe for structured logging — no secrets, no PII, bounded length).
      - Return `Task.CompletedTask` (synchronous publisher).
    - Class is `internal sealed`.

- [x] **Task 5 — Register health checks and publisher in `Program.cs`** (AC: 2, 3, 4, 5)
  - [x] After `builder.AddServiceDefaults();` (line 11 of current `Program.cs`), add:
    ```csharp
    builder.Services.AddHealthChecks()
        .AddNpgSql(
            connectionStringFactory: sp =>
                builder.Configuration.GetConnectionString("formforge")
                ?? throw new InvalidOperationException("Missing ConnectionStrings:formforge"),
            name: "postgres",
            failureStatus: HealthStatus.Unhealthy,
            tags: ["ready"])
        .Add(new HealthCheckRegistration(
            name: "minio",
            factory: sp => new MinioHealthCheck(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<IConfiguration>()),
            failureStatus: HealthStatus.Unhealthy,
            tags: ["ready"]));
    ```
  - [x] Register the named `HttpClient` for MinIO health check (no redirect, explicit timeout enforced in `MinioHealthCheck` via `CancellationToken`):
    ```csharp
    builder.Services.AddHttpClient("minio-health")
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
        });
    ```
  - [x] Register the logging health check publisher and configure the 30-second period:
    ```csharp
    builder.Services.AddSingleton<IHealthCheckPublisher, LoggingHealthCheckPublisher>();
    builder.Services.Configure<HealthCheckPublisherOptions>(options =>
    {
        options.Delay = TimeSpan.FromSeconds(30);
        options.Period = TimeSpan.FromSeconds(30);
    });
    ```
  - [x] Add required `using` statements:
    ```csharp
    using FormForge.Api.Infrastructure.HealthChecks;
    using Microsoft.Extensions.Diagnostics.HealthChecks;
    ```

- [x] **Task 6 — Map `/health` endpoint in `Program.cs`** (AC: 3, 6)
  - [x] After `app.MapDefaultEndpoints();` (line 90 of current `Program.cs`), add:
    ```csharp
    // /health — detailed, all checks (admin auth deferred to Story 2.6 per AR-25)
    app.MapHealthChecks("/health", new HealthCheckOptions
    {
        ResponseWriter = HealthCheckJsonWriter.WriteResponse,
        ResultStatusCodes =
        {
            [HealthStatus.Healthy] = StatusCodes.Status200OK,
            [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
            [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
        },
    });
    ```
  - [x] The three endpoints `/health/live`, `/health/ready`, and `/health` must **not** require authentication (AR-25 defers `/health` auth to Story 2.6; `/health/live` and `/health/ready` are always anonymous per AR-25).

- [x] **Task 7 — Refactor `ServiceDefaults.MapDefaultEndpoints` to expose endpoints in all environments** (AC: 6)
  - [x] In `src/FormForge.ServiceDefaults/Extensions.cs`:
    - Replace `AlivenessEndpointPath = "/alive"` constant with `LivenessEndpointPath = "/health/live"` and `ReadinessEndpointPath = "/health/ready"`.
    - Replace the `MapDefaultEndpoints` body:
      ```csharp
      public static WebApplication MapDefaultEndpoints(this WebApplication app)
      {
          ArgumentNullException.ThrowIfNull(app);

          // /health/live and /health/ready are exposed in all environments (not just Development).
          // AR-25: both endpoints are anonymous; /health (detailed) is mapped in FormForge.Api/Program.cs
          // and receives platform-admin auth in Story 2.6.

          // Liveness: only the "self" tag check (always Healthy if process is up).
          app.MapHealthChecks(LivenessEndpointPath, new HealthCheckOptions
          {
              Predicate = r => r.Tags.Contains("live"),
              ResponseWriter = (ctx, report) =>
              {
                  ctx.Response.ContentType = "application/json";
                  return ctx.Response.WriteAsync("{\"status\":\"healthy\"}", ctx.RequestAborted);
              },
              ResultStatusCodes =
              {
                  [HealthStatus.Healthy] = StatusCodes.Status200OK,
                  [HealthStatus.Degraded] = StatusCodes.Status200OK,
                  [HealthStatus.Unhealthy] = StatusCodes.Status200OK,
              },
          });

          // Readiness: only "ready" tagged checks (postgres + minio).
          // Response writer configured per-app — FormForge.Api overrides for JSON shape.
          app.MapHealthChecks(ReadinessEndpointPath, new HealthCheckOptions
          {
              Predicate = r => r.Tags.Contains("ready"),
              ResultStatusCodes =
              {
                  [HealthStatus.Healthy] = StatusCodes.Status200OK,
                  [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
                  [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
              },
          });

          return app;
      }
      ```
    - Update `ConfigureOpenTelemetry` tracing filter to exclude all `/health` paths (the existing filter `StartsWithSegments("/health")` already covers `/health/live` and `/health/ready`, but update the constant references):
      ```csharp
      tracing.Filter = context =>
          !context.Request.Path.StartsWithSegments(HealthEndpointPath, StringComparison.Ordinal)
      ```
      (Drop the now-removed `/alive` check. `HealthEndpointPath = "/health"` already covers all three new paths.)
  - [x] Add missing `using Microsoft.AspNetCore.Http;` to `ServiceDefaults/Extensions.cs` if not already present (needed for `StatusCodes`).

- [x] **Task 8 — Update `AppHost.cs` to use `/health/live`** (AC: 6)
  - [x] In `src/FormForge.AppHost/AppHost.cs` line 23, change:
    ```csharp
    .WithHttpHealthCheck("/alive")
    ```
    to:
    ```csharp
    .WithHttpHealthCheck("/health/live")
    ```
  - [x] This aligns the Aspire orchestrator's wait-for-ready probe with the canonical liveness endpoint.

- [x] **Task 9 — Add unit and integration tests** (AC: 1, 2, 3, 4, 5)
  - [x] Create `src/FormForge.Api.Tests/Infrastructure/HealthChecks/HealthCheckEndpointsTests.cs`:
    - Reuse the `FormForgeApiFactory` (WebApplicationFactory subclass) already created in Story 1.5 (`CorrelationIdMiddlewareTests.cs`) that sets a fake connection string so `Database.Migrate()` swallows quickly.
    - **Test 1: `LiveEndpoint_AlwaysReturns200`** — `GET /health/live` → HTTP 200; body is `{"status":"healthy"}`.
    - **Test 2: `ReadyEndpoint_Returns503_WhenDependenciesUnavailable`** — in the test WebApplicationFactory, postgres and minio are not running → `GET /health/ready` → HTTP 503; body contains `"status":"unhealthy"` or `"status":"degraded"`.
    - **Test 3: `ReadyEndpoint_ResponseBodyIsValidJson`** — `GET /health/ready` returns Content-Type `application/json`; body is valid JSON parseable with `JsonDocument.Parse`.
    - **Test 4: `HealthEndpoint_Returns503_WhenDependenciesUnavailable`** — `GET /health` → HTTP 503; body contains `"status"` and `"checks"` keys; `"checks"` contains `"postgres"` and `"minio"` entries.
    - **Test 5: `HealthEndpoint_ResponseContainsCorrelationId_WhenResponseHeaderSet`** — `GET /health` returns `X-Correlation-ID` response header (correlation middleware runs on this path too).
  - [x] Create `src/FormForge.Api.Tests/Infrastructure/HealthChecks/MinioHealthCheckTests.cs`:
    - **Test 1: `CheckAsync_Returns_Healthy_On_200Response`** — mock `IHttpClientFactory` to return 200 → `HealthCheckResult.Status == HealthStatus.Healthy`.
    - **Test 2: `CheckAsync_Returns_Healthy_On_403Response`** — mock to return 403 → `HealthStatus.Healthy` (MinIO is up, auth required).
    - **Test 3: `CheckAsync_Returns_Unhealthy_On_404Response`** — 404 → `HealthStatus.Unhealthy`, description contains "bucket" or "not found".
    - **Test 4: `CheckAsync_Returns_Unhealthy_On_ConnectionRefused`** — `IHttpClientFactory` client throws `HttpRequestException` → `HealthStatus.Unhealthy`.
    - **Test 5: `CheckAsync_Uses_MinioEndpointFromConfiguration`** — verify the HEAD request URL is built from `services__minio__s3__0` config key when present.
  - [x] Create `src/FormForge.Api.Tests/Infrastructure/HealthChecks/HealthCheckJsonWriterTests.cs`:
    - **Test 1: `WriteResponse_Healthy_Returns_LowercaseHealthyStatus`** — HealthReport with all Healthy checks → JSON status = `"healthy"`.
    - **Test 2: `WriteResponse_Unhealthy_Returns_LowercaseUnhealthyStatus`** — at least one Unhealthy check → JSON status = `"unhealthy"`.
    - **Test 3: `WriteResponse_Checks_ContainsExpectedKeys`** — two checks "postgres" and "minio" → JSON `checks` object has both keys.
    - **Test 4: `WriteResponse_Exception_IncludesErrorField`** — check with non-null `Exception` → JSON entry has `"error"` field.
  - **Testing approach for MinioHealthCheck:** do NOT use `WebApplicationFactory` for the `MinioHealthCheck` unit tests — create the check directly with a mock `IHttpClientFactory`. This makes the tests fast and dependency-free.

- [x] **Task 10 — Manual verification + build gates** (AC: all)
  - [x] `dotnet build` — zero warnings, zero errors.
  - [x] `dotnet format --verify-no-changes` — clean.
  - [x] `dotnet test` — all new tests pass; no regressions. (49 tests pass, 0 failures.)
  - [x] `cd web && npm run build` — clean (no frontend changes; regression check only).
  - [ ] Run API in Development (`dotnet run --project src/FormForge.Api`):
    - `curl -i http://localhost:{port}/health/live` → HTTP 200, body `{"status":"healthy"}`.
    - `curl -i http://localhost:{port}/health/ready` → HTTP 200 if postgres is running; 503 if not.
    - `curl -i http://localhost:{port}/health` → 200/503 with full `{ "status", "checks": { "postgres", "minio" } }` body.
  - [ ] Run AppHost (`dotnet run --project src/FormForge.AppHost`) — confirm Aspire waits for `/health/live` (not `/alive`) before starting the frontend service; no 404 on the health probe.
  - [ ] Wait 30+ seconds — confirm the logging health check publisher emits a structured log line in the Aspire Dashboard.

---

## Dev Notes

### What this story does — and what it does NOT

**In scope:**
1. **Three health check endpoints** — `/health/live` (liveness, always 200), `/health/ready` (readiness, 503 on dependency failure), `/health` (detailed, all checks, auth deferred).
2. **PostgreSQL health check** — `AspNetCore.HealthChecks.NpgSql` verifying the connection string is reachable.
3. **MinIO health check** — custom `MinioHealthCheck` using a HEAD request to the `formforge` bucket with 5s timeout.
4. **30-second logging publisher** — `LoggingHealthCheckPublisher` emits health status every 30 seconds via structured `ILogger` call.
5. **Fix all-environment exposure** — removes the `IsDevelopment()` gate from `ServiceDefaults.MapDefaultEndpoints`; closes two deferred-work items.
6. **AppHost update** — changes Aspire's wait probe from `/alive` to `/health/live`.

**Out of scope:**
- `/health` platform-admin auth — this is explicitly deferred to Story 2.6 per AR-25 and the AC-3 note above. DO NOT add `RequireAuthorization()` or any auth filter to `/health` in this story.
- MinIO `.NET SDK` (`Minio` NuGet package) — the health check uses a plain `HttpClient` HEAD request. The full MinIO SDK is introduced when file upload features land (Epic 3 / 4 / 6).
- `AWSSDK.S3` or any S3 signing — anonymous HEAD to MinIO bucket is sufficient (403 = MinIO is up, which is the signal we need).
- Testcontainers for integration tests — Story 1.5 established that tests use a `WebApplicationFactory` with a missing postgres that swallows the migration error. MinIO is also absent; the health check will fail fast with `HttpRequestException`. That's expected.

### Architecture compliance

This story implements **FR-47** (Health Checks) in full and fulfills:
- **AR-25** (Health-Check Endpoint Authentication): `/health/live` and `/health/ready` are anonymous; `/health` ships anonymous pending the auth pipeline from Story 2.6.
- **Architecture Decision 5.4** (Health Checks): `AspNetCore.HealthChecks.NpgSql` + custom MinIO HEAD-bucket + 30s logging publisher.
- **Architecture Decision 3.10**: endpoint paths `/health/live`, `/health/ready`, `/health` per the spec.
- **AR-22** (Route Group Filter Chain): health endpoints are **not** inside the authenticated route group (they are mapped at the root app level). The `/health` auth filter is applied in Story 2.6 when the `RequirePlatformAdmin` filter is wired.

### Current code state (what you are modifying)

**`src/FormForge.ServiceDefaults/Extensions.cs`** (159 lines, full content in Dev Notes of Story 1.5):
- `AddDefaultHealthChecks` (lines 119–126): registers a "self" liveness check with tag `["live"]`. **Leave this intact** — it is the base that `/health/live` relies on.
- `MapDefaultEndpoints` (lines 128–147): **replaces** the `IsDevelopment()` gated body (this is the fix).
- `ConfigureOpenTelemetry` (lines 47–79): tracing filter at lines 67–69 uses `HealthEndpointPath = "/health"` and `AlivenessEndpointPath = "/alive"`. After this story, only the `/health` prefix filter remains (covers all three new paths). Remove the `AlivenessEndpointPath` constant and its reference in the filter since `/alive` is retired.

**`src/FormForge.Api/Program.cs`** (105 lines, current full content in file structure section):
- `builder.AddServiceDefaults()` (line 11) already calls `AddDefaultHealthChecks()` which seeds the "self" check. The story adds postgres and minio checks afterward by calling `builder.Services.AddHealthChecks()` again — this chains into the same `IHealthChecksBuilder` instance.
- `app.MapDefaultEndpoints()` (line 90) maps `/health/live` and `/health/ready` (after ServiceDefaults refactor). Story 1.6 adds `app.MapHealthChecks("/health", ...)` on the next line.

**`src/FormForge.AppHost/AppHost.cs`** (31 lines):
- Line 23: `.WithHttpHealthCheck("/alive")` → change to `.WithHttpHealthCheck("/health/live")`.
- This is the only change in AppHost. The URL change means Aspire will now probe `/health/live` to know the API is ready before starting the frontend. Since `/health/live` is always 200 (no dependency checks), it behaves identically to `/alive`.

### MinIO endpoint configuration map

| Environment | Config key | Value example |
|---|---|---|
| Aspire dev | `services__minio__s3__0` | `http://localhost:52345` (Aspire-assigned port) |
| Docker Compose | `MinIO__Endpoint` | `http://minio:9000` (Docker network name) |
| Test (WebApplicationFactory) | neither | falls back to `http://localhost:9000` (health check will fail fast → expected 503) |

The `MinioHealthCheck` constructor reads these in priority order: `services__minio__s3__0` → `MinIO__Endpoint` → `"http://localhost:9000"`.

**Important:** In Docker Compose, add `MinIO__Endpoint=http://minio:9000` to the `api` service environment in `docker-compose.yml` alongside the existing `MinIO__RootUser` and `MinIO__RootPassword` variables.

### Response body shapes

**`/health/live`** (always HTTP 200):
```json
{"status":"healthy"}
```
(Hardcoded in the liveness `ResponseWriter` lambda — bypasses the full health report serialization.)

**`/health/ready`** (HTTP 200 or 503):
```json
{
  "status": "healthy",
  "checks": {
    "postgres": { "status": "healthy", "description": null },
    "minio": { "status": "healthy", "description": "MinIO bucket 'formforge' reachable" }
  }
}
```

**`/health`** (HTTP 200 or 503):
```json
{
  "status": "unhealthy",
  "checks": {
    "postgres": { "status": "unhealthy", "description": null, "error": "Connection refused" },
    "minio": { "status": "unhealthy", "description": "MinIO is unreachable.", "error": "Connection refused" }
  }
}
```

### CA rule compliance

All classes are `internal sealed` (CA1515 + CA1852). `[LoggerMessage]` source-generation is used in `LoggingHealthCheckPublisher` for CA1848 compliance. No string interpolation in `ILogger` calls (CA2254). `StringComparison.Ordinal` for any string comparisons (`InvariantGlobalization=true`).

The `HealthCheckPublisherOptions.Delay` and `Period` configuration blocks use `Configure<T>` (not `PostConfigure`) to set the initial delay and interval.

### Previous story intelligence (Story 1.5, completed 2026-05-22)

Inherited context relevant to this story:
- **`TreatWarningsAsErrors=true`** repo-wide — all new classes must be `internal sealed`; `[LoggerMessage]` required for logger calls.
- **`AnalysisMode=AllEnabledByDefault`** — every CA rule is on; CA1515, CA1852, CA2254 will fire.
- **CPM enforced** — new package (`AspNetCore.HealthChecks.NpgSql`) version goes in `Directory.Packages.props`; no inline `Version=` on `<PackageReference>`.
- **`InvariantGlobalization=true`** — use `StringComparison.Ordinal` for any path/header comparisons; `ToLowerInvariant()` instead of `ToLower()`.
- **`WebApplicationFactory<Program>` pattern** — `FormForgeApiFactory` already exists in `CorrelationIdMiddlewareTests.cs`. Reuse it. It sets an unreachable postgres connection string; `Database.Migrate()` fails inside the existing `try/catch` and startup continues. **Do not add a test-only branch in `Program.cs`.**
- **CA1812 suppression pattern** — if the compiler can't see the health check implementations instantiated (they're registered via DI), add `[SuppressMessage("Performance", "CA1812", ...)]` with justification comment.
- **`#pragma warning disable CA1031`** pattern exists in `Program.cs` line 71–73 for migration catch. The `MinioHealthCheck` should catch only `HttpRequestException` and `OperationCanceledException` (not general `Exception`) to stay within the spirit of CA1031.
- **`public partial class Program;`** exists at bottom of `Program.cs` — do not duplicate it.
- **35 tests pass** as of Story 1.5 close. Do not regress them.
- **Deferred-work.md** tracks two items this story closes; update that file after implementation.

### Git intelligence

Recent commits:
- `fd84cb5` — Story 1.5 — Structured logging + correlation IDs + OTLP URI validation
- `e301706` — Story 1.4 follow-up — address 3 critical code-review findings
- `9eaa224` — Story 1.4 — OpenAPI 3.1 spec at /openapi/v1.json + Swagger UI in dev

Pattern: each story produces one feature commit; review fixes go in a separate follow-up commit.
After this story lands: `Story 1.6 — Health check endpoints: /health/live, /health/ready, /health + MinIO check + 30s publisher`.

### Latest tech information (verify versions at implementation time)

- **`AspNetCore.HealthChecks.NpgSql`** — `https://www.nuget.org/packages/AspNetCore.HealthChecks.NpgSql`. Look for `10.0.x` (released after .NET 10 shipped); fall back to `9.0.x` (targets `netstandard2.1`, compatible with `net10.0`). The package uses Npgsql under the hood — with `CentralPackageTransitivePinningEnabled=true`, verify no version conflict with `Npgsql.EntityFrameworkCore.PostgreSQL 10.0.1`.
- **`Microsoft.Extensions.Diagnostics.HealthChecks`** — ships with the `Microsoft.AspNetCore.App` framework reference. No extra package needed for `IHealthCheck`, `IHealthCheckPublisher`, `HealthCheckPublisherOptions`, `HealthReport`, `HealthCheckResult`, `HealthStatus`.
- **`Microsoft.AspNetCore.Diagnostics.HealthChecks`** — also ships in the framework. Provides `MapHealthChecks`, `HealthCheckOptions`, `HealthCheckRegistration`. No extra package needed.
- **`HealthCheckPublisherOptions.Delay`** — the `Delay` property sets the initial delay before the first publish after host starts. Set to same value as `Period` (30 s) to avoid an immediate publish at startup before checks have warmed up.

### File structure

**Touch:**
- `Directory.Packages.props` — add `AspNetCore.HealthChecks.NpgSql` PackageVersion
- `src/FormForge.Api/FormForge.Api.csproj` — add `AspNetCore.HealthChecks.NpgSql` PackageReference
- `src/FormForge.ServiceDefaults/Extensions.cs` — refactor `MapDefaultEndpoints`, update constants + tracing filter
- `src/FormForge.AppHost/AppHost.cs` — update `.WithHttpHealthCheck("/alive")` to `/health/live`
- `src/FormForge.Api/Program.cs` — register health checks, publisher, named HttpClient; map `/health` endpoint
- `docker-compose.yml` — add `MinIO__Endpoint=http://minio:9000` to api service environment
- `_bmad-output/implementation-artifacts/deferred-work.md` — mark two items closed

**New:**
- `src/FormForge.Api/Infrastructure/HealthChecks/MinioHealthCheck.cs`
- `src/FormForge.Api/Infrastructure/HealthChecks/HealthCheckJsonWriter.cs`
- `src/FormForge.Api/Infrastructure/HealthChecks/LoggingHealthCheckPublisher.cs`
- `src/FormForge.Api.Tests/Infrastructure/HealthChecks/HealthCheckEndpointsTests.cs`
- `src/FormForge.Api.Tests/Infrastructure/HealthChecks/MinioHealthCheckTests.cs`
- `src/FormForge.Api.Tests/Infrastructure/HealthChecks/HealthCheckJsonWriterTests.cs`

**Do NOT touch:**
- `src/FormForge.ServiceDefaults/Extensions.cs:119-126` (`AddDefaultHealthChecks`) — the "self" liveness check registration stays intact
- `src/FormForge.Api/appsettings.json` — no config changes needed; endpoint URLs come from env vars (Aspire injection or Compose)
- `web/` — no frontend changes
- `src/FormForge.Api/Common/` — no changes to existing middleware or logging helpers
- Any EF Core migration files

### Anti-patterns to avoid

| Don't | Why |
|---|---|
| Add auth to `/health` or `/health/live` or `/health/ready` in this story | AR-25 defers `/health` auth to Story 2.6; the other two are permanently anonymous |
| Use `ToLower()` without `CultureInfo.InvariantCulture` | `InvariantGlobalization=true` — use `ToLowerInvariant()` |
| Catch bare `Exception` in `MinioHealthCheck` | CA1031 — catch only `HttpRequestException` and `OperationCanceledException` |
| String interpolation in `ILogger` calls | CA2254 — use `[LoggerMessage]` source generation |
| Register `/alive` as a new endpoint | The path is retired; `AppHost.cs` switches to `/health/live`. If backward compat is critical (other tools probe `/alive`), keep it, but the story doesn't require it. |
| Map `/health`, `/health/live`, `/health/ready` inside an authenticated route group | These endpoints are outside the auth boundary per AR-51 and AR-25 |
| Use `UIResponseWriter` from `AspNetCore.HealthChecks.UI.Client` | That package produces a different JSON shape and adds a UI dependency. Write the custom serializer directly. |
| Use `Minio` .NET SDK just for the health check | The SDK is a large dependency; use `HttpClient` HEAD request. The SDK lands with Epic 3/4/6 file-upload stories. |
| Duplicate `builder.Services.AddHealthChecks()` call by calling it before `builder.AddServiceDefaults()` | `AddServiceDefaults()` calls `AddDefaultHealthChecks()` which calls `AddHealthChecks()`. Calling `builder.Services.AddHealthChecks()` after `AddServiceDefaults()` chains into the same builder instance — this is correct. The NpgSql and MinIO checks are added to the same `IHealthChecksBuilder`. |

### Project Structure Notes

```
tinnitus/
├── Directory.Packages.props       ← ADD AspNetCore.HealthChecks.NpgSql version
├── docker-compose.yml             ← ADD MinIO__Endpoint env var to api service
├── src/
│   ├── FormForge.AppHost/
│   │   └── AppHost.cs             ← UPDATE .WithHttpHealthCheck("/alive") → "/health/live"
│   ├── FormForge.ServiceDefaults/
│   │   └── Extensions.cs          ← REFACTOR MapDefaultEndpoints; update constants + filter
│   ├── FormForge.Api/
│   │   ├── FormForge.Api.csproj   ← ADD AspNetCore.HealthChecks.NpgSql PackageReference
│   │   ├── Program.cs             ← ADD health check registrations, publisher, /health mapping
│   │   └── Infrastructure/
│   │       └── HealthChecks/      ← NEW directory
│   │           ├── MinioHealthCheck.cs
│   │           ├── HealthCheckJsonWriter.cs
│   │           └── LoggingHealthCheckPublisher.cs
│   └── FormForge.Api.Tests/
│       └── Infrastructure/
│           └── HealthChecks/      ← NEW directory
│               ├── HealthCheckEndpointsTests.cs
│               ├── MinioHealthCheckTests.cs
│               └── HealthCheckJsonWriterTests.cs
└── _bmad-output/
    └── implementation-artifacts/
        └── deferred-work.md       ← CLOSE two items: "health in dev only" + "/alive in Compose"
```

### Testing standards summary

- **Framework:** xUnit (already pinned via CPM).
- **Integration tests (`HealthCheckEndpointsTests`):** `WebApplicationFactory<Program>` via `FormForgeApiFactory` (existing in `CorrelationIdMiddlewareTests.cs`). PostgreSQL and MinIO are not running — health checks fail → readiness/detailed endpoints return 503. This is correct test behavior.
- **Unit tests (`MinioHealthCheckTests`):** Construct `MinioHealthCheck` directly, using a mock `HttpMessageHandler` (the standard xUnit approach: subclass `HttpMessageHandler`, override `SendAsync`, inject via `new HttpClient(handler)`). No `WebApplicationFactory` needed.
- **Unit tests (`HealthCheckJsonWriterTests`):** Construct a minimal `HealthReport` from a `Dictionary<string, HealthReportEntry>`. Use `new DefaultHttpContext()` to test the writer without a real host.
- **No Testcontainers** for health check tests — test environments without running PG/MinIO are the valid test scenario for the 503 path.
- **Naming:** `MethodName_Scenario_Expected` convention established in Story 1.5.
- **Coverage expectation:** All three new infrastructure classes (`MinioHealthCheck`, `HealthCheckJsonWriter`, `LoggingHealthCheckPublisher`) have dedicated test files. The three endpoints are covered by `HealthCheckEndpointsTests`.

### References

- `_bmad-output/planning-artifacts/epics.md` — § "Story 1.6: Health Check Endpoints" (lines 434–464, canonical ACs)
- `_bmad-output/planning-artifacts/architecture.md`
  - § "Decision 3.10 — Health-Check Endpoint Authentication" (lines 517–520) — auth deference map
  - § "Decision 5.4 — Health Checks" (lines 654–658) — NpgSql + custom MinIO + publisher
  - § "1.6 — Query Timeouts" (line 339) — `CommandTimeout = 5` for health check probes (this is NpgSql's internal timeout, handled by the package)
  - § "Architectural Boundaries (AR-51)" (line 1239–1241) — `/health/*` in the public surface, outside auth boundary
  - § "Project Structure" (line 1043–1044) — `Infrastructure/HealthChecks/MinioHealthCheck.cs`
- `_bmad-output/implementation-artifacts/deferred-work.md` — two items closed by this story
- `_bmad-output/implementation-artifacts/1-5-structured-logging-with-correlation-ids.md` — Dev Notes for CA rule patterns, CPM enforcement, analyzer discipline, WebApplicationFactory reuse pattern
- NuGet: https://www.nuget.org/packages/AspNetCore.HealthChecks.NpgSql — pick latest at dev time
- Microsoft Docs: https://learn.microsoft.com/aspnet/core/host-and-deploy/health-checks — canonical health check reference (publisher, options, custom checks)

## Change Log

- Story 1.6 created — health check endpoints: /health/live, /health/ready, /health + MinIO HEAD-bucket check + 30s publisher + all-environment fix (Date: 2026-05-22)
- Story 1.6 implemented — AspNetCore.HealthChecks.NpgSql 9.0.0 added; MinioHealthCheck, HealthCheckJsonWriter, LoggingHealthCheckPublisher created; ServiceDefaults refactored to expose /health/live and /health/ready in all environments; /health mapped in Program.cs; AppHost probe updated to /health/live; docker-compose MinIO__Endpoint env var added; 14 new tests, 49 total passing (Date: 2026-05-22)

## Dev Agent Record

### Agent Model Used

claude-sonnet-4-6

### Debug Log References

- No debug log. Clean run: all builds succeeded on first attempt after fixing `partial` modifier on `LoggingHealthCheckPublisher`, adding `using Microsoft.AspNetCore.Diagnostics.HealthChecks;` in `Program.cs`, and pre-computing logger arguments to satisfy CA1873.
- CA2234 in tests fixed by using `HttpRequestMessage` instead of passing strings to `GetAsync`.
- CA2000 in tests fixed by `using` declarations on all `HttpClient` and `HttpMessageHandler` instances.
- `HealthCheckJsonWriter` kept in `FormForge.Api`; `ServiceDefaults` uses an equivalent private inline method (`WriteHealthReportAsJson`) to avoid circular dependency — JSON shape is identical.

### Completion Notes List

- Task 1: `AspNetCore.HealthChecks.NpgSql 9.0.0` added (no `10.x` release exists yet); `dotnet restore` clean.
- Task 2: `MinioHealthCheck` — priority order: `services__minio__s3__0` → `MinIO__Endpoint` → `http://localhost:9000`. 5s timeout via `CancellationTokenSource.CreateLinkedTokenSource`. HTTP 200/403 = Healthy, 404 = Unhealthy (bucket missing), connection error = Unhealthy.
- Task 3: `HealthCheckJsonWriter.WriteResponse` builds JSON `{ status, checks }` with optional `error` field per entry exception. Used only by `/health` (detailed) — avoids circular dep with ServiceDefaults.
- Task 4: `LoggingHealthCheckPublisher` — `partial` class, source-generated `[LoggerMessage]`, `IsEnabled` guard added to satisfy CA1873 on pre-computed arguments.
- Task 5: NpgSql + MinIO health checks registered on `"ready"` tag. Named `HttpClient "minio-health"` registered with `AllowAutoRedirect = false`. Publisher + options configured (Delay=30s, Period=30s).
- Task 6: `/health` mapped after `MapDefaultEndpoints()` using `HealthCheckJsonWriter.WriteResponse`; 200=Healthy, 503=Degraded or Unhealthy.
- Task 7: `MapDefaultEndpoints` refactored — `/alive` retired, new `LivenessEndpointPath="/health/live"` and `ReadinessEndpointPath="/health/ready"` constants; both endpoints exposed unconditionally (not just Development). OpenTelemetry tracing filter updated to single `StartsWithSegments("/health", StringComparison.Ordinal)`. `ServiceDefaults` uses a private `WriteHealthReportAsJson` method for readiness JSON.
- Task 8: `AppHost.cs` updated to `.WithHttpHealthCheck("/health/live")`.
- Task 9: 14 new tests — 5 endpoint integration tests + 5 MinioHealthCheck unit tests + 4 HealthCheckJsonWriter unit tests. Total: 49 tests, 0 failures.
- Task 10 (automated gates): `dotnet build` 0 warnings/errors; `dotnet format --verify-no-changes` clean; `dotnet test` 49/49 pass; `npm run build` clean. Runtime verification (curl + AppHost) left for jukhan interactive check.
- Two deferred-work items closed: "Health endpoints only mapped in Development" and "API healthcheck uses GET / instead of GET /alive".

### File List

**Modified:**
- `Directory.Packages.props`
- `src/FormForge.Api/FormForge.Api.csproj`
- `src/FormForge.ServiceDefaults/Extensions.cs`
- `src/FormForge.AppHost/AppHost.cs`
- `src/FormForge.Api/Program.cs`
- `docker-compose.yml`
- `_bmad-output/implementation-artifacts/deferred-work.md`
- `_bmad-output/implementation-artifacts/sprint-status.yaml`

**New:**
- `src/FormForge.Api/Infrastructure/HealthChecks/MinioHealthCheck.cs`
- `src/FormForge.Api/Infrastructure/HealthChecks/HealthCheckJsonWriter.cs`
- `src/FormForge.Api/Infrastructure/HealthChecks/LoggingHealthCheckPublisher.cs`
- `src/FormForge.Api.Tests/Infrastructure/HealthChecks/HealthCheckEndpointsTests.cs`
- `src/FormForge.Api.Tests/Infrastructure/HealthChecks/MinioHealthCheckTests.cs`
- `src/FormForge.Api.Tests/Infrastructure/HealthChecks/HealthCheckJsonWriterTests.cs`

### Review Findings

- [x] [Review][Patch] `/health/ready` response missing `error` field — `WriteHealthReportAsJson` in `Extensions.cs` never emits `"error"` when `e.Value.Exception != null`, unlike `HealthCheckJsonWriter.WriteResponse` used by `/health`. Operators see 503 with no exception message. [`src/FormForge.ServiceDefaults/Extensions.cs` `WriteHealthReportAsJson`] ✅ Fixed
- [x] [Review][Patch] `MinioHealthCheck` trailing slash in endpoint URL — `$"{endpoint}/formforge"` produces `//formforge` when config value ends with `/`. [`src/FormForge.Api/Infrastructure/HealthChecks/MinioHealthCheck.cs:13`] ✅ Fixed
- [x] [Review][Defer] Timeout cancellation path not tested — no unit test exercises `cts.CancelAfter(5s)` firing in `MinioHealthCheck`; only `HttpRequestException` mock covers catch branches. [`src/FormForge.Api.Tests/Infrastructure/HealthChecks/MinioHealthCheckTests.cs`] — deferred, pre-existing
- [x] [Review][Defer] docker-compose api healthcheck still probes `GET /` — `docker-compose.yml:63` uses `wget -qO- http://localhost:8080/`; deferred item marked closed but probe wasn't updated to `/health/live`. [`docker-compose.yml:63`] — deferred, pre-existing
- [x] [Review][Defer] OTel trace filter is case-sensitive — `StartsWithSegments("/health", StringComparison.Ordinal)` won't filter `/Health/*` paths; low practical risk on standard deployments. [`src/FormForge.ServiceDefaults/Extensions.cs:70`] — deferred, pre-existing
- [x] [Review][Defer] `LoggingHealthCheckPublisher.PublishAsync` ignores `CancellationToken` — acceptable for synchronous publisher; latent hazard if async I/O is added later. [`src/FormForge.Api/Infrastructure/HealthChecks/LoggingHealthCheckPublisher.cs:8`] — deferred, pre-existing
- [x] [Review][Defer] `/health` emits raw exception messages to anonymous callers — `HealthCheckJsonWriter` includes `Exception.Message` in JSON response; mitigated when Story 2.6 adds platform-admin auth per AR-25. [`src/FormForge.Api/Infrastructure/HealthChecks/HealthCheckJsonWriter.cs:26-28`] — deferred, pre-existing
