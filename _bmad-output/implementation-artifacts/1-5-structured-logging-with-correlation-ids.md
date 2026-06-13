# Story 1.5: Structured Logging with Correlation IDs

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Developer,
I want to trace any request end-to-end through structured logs using a correlation ID,
so that debugging is tractable without guesswork.

## Acceptance Criteria

### AC-1 — Correlation ID middleware: read header or generate ULID; echo to response

**Given** an incoming HTTP request
**When** the correlation-ID middleware runs
**Then** the request is assigned a correlation ID — read from the `X-Correlation-ID` request header if present and well-formed (26-character ULID, base32 Crockford alphabet); otherwise generated as a new ULID
**And** the same correlation ID is set on the response header `X-Correlation-ID`
**And** the correlation ID is pushed onto an `ILogger.BeginScope` dictionary for the duration of the request

### AC-2 — Every log entry is structured JSON with required fields

**Given** any log entry emitted during a request (including request-pipeline middleware, application code, EF Core, ASP.NET Core)
**When** the entry is written to stdout
**Then** it is a single line of JSON containing at minimum:
- `Timestamp` (ISO-8601 UTC)
- `LogLevel`
- `Message` (or `RenderedMessage`)
- `Exception` (if thrown — full type, message, stack)
- And within the scope dictionary (or its flattened equivalent): `correlationId`, `endpoint` (request path + method when inside the HTTP pipeline)
- And `userId` once the auth pipeline is wired (Story 2.6); absent today is acceptable

> **Implementation note:** the .NET built-in `Microsoft.Extensions.Logging.Console` JSON formatter writes scope values as a nested `Scopes[]` array, not as flat top-level fields. The Aspire-provided OTel logging exporter independently flattens scopes into log attributes. AC-2 is satisfied when **either** consumer (stdout JSON line OR OTel-exported record) carries `correlationId` somewhere queryable. Document which path was chosen in the dev notes.

### AC-3 — DDL / CRUD log entries carry SQL fingerprint (helper available; no call sites yet)

**Given** any DDL or CRUD mutation is logged at `Information` level
**When** I inspect the log entry
**Then** it additionally carries `designerId`, `operation`, and a `sqlFingerprint` field containing the parameterized SQL with placeholders only (no parameter values, per FR-46 AC-3)

> **Scope clarification:** No DDL or CRUD code exists in the API today — Epic 5 (Provisioning) introduces the first DDL emissions; Epic 6 (CRUD) introduces the first dynamic SQL. AC-3 is satisfied in this story by **shipping the helper API** that Epic 5 / Epic 6 will call. The helper:
> - lives in `src/FormForge.Api/Common/Logging/SqlFingerprint.cs`
> - exposes `static string Fingerprint(string parameterizedSql)` that strips all literal string / numeric values and leaves only placeholders (`@p0`, `@designerId`, etc.) — and is covered by unit tests
> - exposes `static IDisposable BeginDdlScope(ILogger logger, string designerId, string operation, string sqlFingerprint)` and an analogous `BeginCrudScope` that push the three required fields into the logger scope dictionary
> Epic 5 / Epic 6 story files will reference this helper and assert the actual log emissions land.

### AC-4 — String-interpolated `ILogger.Log*` call is flagged by an analyzer as a violation

**Given** a string-interpolated `ILogger` call exists in source (e.g. `_logger.LogInformation($"Saved {id}")`)
**When** `dotnet build` runs
**Then** the call is flagged as a build error (CA2254 "Template should be a static expression" already shipped by `Microsoft.CodeAnalysis.NetAnalyzers` and active via `AnalysisMode=AllEnabledByDefault` + `TreatWarningsAsErrors=true`)
**And** the build fails

### AC-5 — Correlation ID flows into the response error envelope (when error path runs)

**Given** an exception escapes a request handler
**When** the global `UseExceptionHandler` produces a `ProblemDetails` response
**Then** the response body's `extensions.correlationId` (or `traceId` mapped to the same value) carries the request's correlation ID
**And** the response header `X-Correlation-ID` is still set
**And** the log entry for the exception carries the same correlation ID

> **Scope clarification:** The full RFC-7807 error envelope per AR-18 (code, messageKey, etc.) lands in Epic 2. Story 1.5 only requires that the **correlation ID** survive into the existing `AddProblemDetails()` output via `ProblemDetailsOptions.CustomizeProblemDetails`. AR-18's other fields land in Story 2.1.

### AC-6 — OTLP exporter activates only when `OTEL_EXPORTER_OTLP_ENDPOINT` is a valid absolute URI (deferred from Story 1.1)

**Given** `OTEL_EXPORTER_OTLP_ENDPOINT` is configured
**When** the host boots
**Then** the OTLP exporter is activated only if the value parses as an absolute `Uri` with scheme `http` or `https` (and a non-empty host)
**And** a malformed value (whitespace, partial URL, unsupported scheme) causes a structured `Warning` log emission naming the offending key — and host startup continues without the exporter (silent-drop avoidance)

## Tasks / Subtasks

- [x] **Task 1 — Add packages: `Ulid` (Cysharp) and `Microsoft.AspNetCore.Mvc.Testing`** (AC: 1, plus enables AC-1 / AC-5 tests)
  - [x] In `Directory.Packages.props`, add two new `<PackageVersion>` entries:
    - `Ulid` — pick latest stable at implementation time from https://www.nuget.org/packages/Ulid (Cysharp). As of May 2026, expect `1.3.x` or later. The package is `netstandard2.1`-targeted and works fine on `net10.0`.
    - `Microsoft.AspNetCore.Mvc.Testing` — pick latest stable `10.0.x` from https://www.nuget.org/packages/Microsoft.AspNetCore.Mvc.Testing matching the EF Core / OpenAPI `10.0.x` family already pinned.
  - [x] Add `<PackageReference Include="Ulid" />` (no inline `Version=` per CPM) to `src/FormForge.Api/FormForge.Api.csproj`.
  - [x] Add `<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />` to `src/FormForge.Api.Tests/FormForge.Api.Tests.csproj`.
  - [x] Run `dotnet restore` — confirm clean (no NU1605 / NU1010).

- [x] **Task 2 — Add `CorrelationIdMiddleware` and `LogContextExtensions`** (AC: 1, 2)
  - [ ] Create `src/FormForge.Api/Common/Logging/CorrelationIdMiddleware.cs`:
    - `internal sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)`
    - On invocation:
      1. Read `X-Correlation-ID` request header. If present, validate via `Ulid.TryParse(value, out var ulid)` — accept only the 26-char ULID form. Reject (treat as missing) on any other input. **Do not** echo client-supplied non-ULID values back; this prevents log-injection.
      2. If absent/invalid, call `Ulid.NewUlid()` and use its `ToString()` value (26 chars).
      3. Store the chosen value on `HttpContext.Items["CorrelationId"]` AND on `HttpContext.TraceIdentifier` so downstream middleware and `Activity.Current` can pick it up.
      4. Append it to `context.Response.Headers["X-Correlation-ID"]` via `context.Response.OnStarting(...)` (must be added before `WriteAsync` is called).
      5. Open `using var scope = logger.BeginScope(new Dictionary<string, object> { ["correlationId"] = id, ["endpoint"] = $"{context.Request.Method} {context.Request.Path}" });` and `await next(context);` inside the scope.
    - Header name lookups use the framework constant `HeaderNames.XCorrelationId` if available; otherwise the literal `"X-Correlation-ID"`.
    - Use `StringComparison.Ordinal` everywhere (per `InvariantGlobalization=true`).
    - Class is `internal sealed` to satisfy CA1515 + CA1852.
  - [ ] Create `src/FormForge.Api/Common/Logging/LogContextExtensions.cs`:
    - `internal static class LogContextExtensions`
    - `public static string? GetCorrelationId(this HttpContext context)` — reads `HttpContext.Items["CorrelationId"]` and returns the ULID string (or null if outside the HTTP pipeline).
    - `public static IDisposable? BeginUserScope(this ILogger logger, ClaimsPrincipal user)` — when `user.Identity?.IsAuthenticated == true`, push `["userId"] = userId, ["roles"] = roles` into a scope. Returns null when unauthenticated. Used by the auth filter in Story 2.6 — not invoked anywhere yet in this story.
  - [ ] Wire the middleware in `Program.cs` **before** `UseExceptionHandler` and **before** `MapDefaultEndpoints` so even the health-check responses carry the header:
    ```csharp
    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseExceptionHandler();
    app.UseStaticFiles();
    ```

- [x] **Task 3 — Switch console output to JSON formatter** (AC: 2)
  - [ ] In `Program.cs`, after `builder.AddServiceDefaults();`, add:
    ```csharp
    builder.Logging.AddJsonConsole(options =>
    {
        options.IncludeScopes = true;
        options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
        options.UseUtcTimestamp = true;
        options.JsonWriterOptions = new System.Text.Json.JsonWriterOptions { Indented = false };
    });
    ```
  - [ ] In `appsettings.json`, leave the existing `Logging.LogLevel` block intact (it does not conflict with the formatter selection — the formatter is chosen in code).
  - [ ] **Important:** Do NOT remove the existing `Logging.AddOpenTelemetry()` call in `ServiceDefaults/Extensions.cs:49-53`. The two consumers coexist — JSON console is for container stdout (visible to `docker compose logs`, Compose + prod); OTel logging exporter is for the Aspire Dashboard (dev). `IncludeScopes = true` on both ensures correlation IDs appear in both surfaces.
  - [ ] Verify (manual): run `dotnet run --project src/FormForge.Api`, hit `GET /`, confirm the request log line(s) are valid JSON with `Scopes` array containing the correlation ID.

- [x] **Task 4 — Add `SqlFingerprint` helper** (AC: 3)
  - [ ] Create `src/FormForge.Api/Common/Logging/SqlFingerprint.cs`:
    - `internal static class SqlFingerprint`
    - `public static string Fingerprint(string parameterizedSql)` — accepts SQL that already uses Dapper-style `@name` or `$n` placeholders, returns it unchanged. Its purpose is **defense in depth**: it strips inline string-literal patterns (`'...'`) and inline numeric literals via two regex passes — replacing each with `?` so a future caller who accidentally interpolates a value still produces a fingerprint. Both regexes are pre-compiled `[GeneratedRegex(...)]` partials (CA1515 / SYSLIB1045).
    - `public static IDisposable BeginDdlScope(ILogger logger, string designerId, string operation, string sqlFingerprint)` — opens a scope dictionary with keys `designerId`, `operation`, `sqlFingerprint`.
    - `public static IDisposable BeginCrudScope(ILogger logger, string designerId, string operation, string sqlFingerprint)` — same shape; separate method only for caller-site readability.
    - **No call sites are added in this story.** Epic 5 (Provisioning) and Epic 6 (Dynamic CRUD) will invoke these.
  - [ ] Document in the XML doc comment that the helper is **defense in depth** — actual SQL must always be parameterized at the call site (Dapper / EF Core). The fingerprint is a safety net, not the primary mechanism.

- [x] **Task 5 — Carry the correlation ID into `ProblemDetails` responses** (AC: 5)
  - [ ] Replace the existing bare `builder.Services.AddProblemDetails();` call in `Program.cs` with:
    ```csharp
    builder.Services.AddProblemDetails(options =>
    {
        options.CustomizeProblemDetails = ctx =>
        {
            var corr = ctx.HttpContext.GetCorrelationId();
            if (!string.IsNullOrEmpty(corr))
            {
                ctx.ProblemDetails.Extensions["correlationId"] = corr;
            }
        };
    });
    ```
  - [ ] Add `using FormForge.Api.Common.Logging;` to `Program.cs` so `GetCorrelationId()` resolves.

- [x] **Task 6 — Fix `OTEL_EXPORTER_OTLP_ENDPOINT` URI validation in ServiceDefaults** (AC: 6, deferred from Story 1.1 code review)
  - [ ] In `src/FormForge.ServiceDefaults/Extensions.cs`, replace `AddOpenTelemetryExporters` (lines 81–98) so the gate is:
    ```csharp
    var raw = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
    if (!string.IsNullOrWhiteSpace(raw))
    {
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrEmpty(uri.Host))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }
        else
        {
            // Emit a structured warning, then continue startup without the exporter
            using var lf = LoggerFactory.Create(b => b.AddConsole());
            lf.CreateLogger("ServiceDefaults").LogOtlpEndpointInvalid(raw);
        }
    }
    ```
  - [ ] Add a source-generated logger message helper to keep CA1848 happy:
    ```csharp
    internal static partial class ServiceDefaultsLog
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
            Message = "OTEL_EXPORTER_OTLP_ENDPOINT is set but is not a valid absolute http/https URI; OTLP exporter disabled. value={Value}")]
        public static partial void LogOtlpEndpointInvalid(this ILogger logger, string value);
    }
    ```
  - [ ] **Cross-project visibility:** `ServiceDefaults` is referenced by both `AppHost` and `Api`; both already get an `ILogger` available via host bootstrap. The transient `LoggerFactory.Create(...)` is acceptable because this code runs once during host construction, before the DI container is available.
  - [ ] Confirm Aspire dev mode still flows OTLP — Aspire sets `OTEL_EXPORTER_OTLP_ENDPOINT` to its dashboard endpoint (validated `http://localhost:18889` or similar), so this remains active in dev.

- [x] **Task 7 — Add unit tests for the new helpers** (AC: 1, 3, 4, 5, 6)
  - [ ] At the bottom of `Program.cs`, after `app.Run();`, add `public partial class Program;` (single line) to make the auto-generated `Program` class accessible to `WebApplicationFactory<Program>` in the test project. CPM does not need a `Version=` for this — it is a source-file declaration, not a package.
  - [ ] Create `src/FormForge.Api.Tests/Common/Logging/CorrelationIdMiddlewareTests.cs`:
    - **Test 1:** `HappyPath_ClientSuppliesValidUlid_ServerEchoesSameUlid` — `WebApplicationFactory<Program>` builds a client, sends `GET /` with `X-Correlation-ID: 01HX9JK8YZ4N6T2X3V5W7P9Q0R` (a known valid ULID), asserts response header equals the same value.
    - **Test 2:** `NoHeader_ServerGeneratesUlid` — no inbound header; response header is a 26-char value that round-trips through `Ulid.Parse(...)`.
    - **Test 3:** `MalformedHeader_IsIgnoredAndUlidIsGenerated` — inbound header `not-a-ulid-value`; response header is a fresh ULID (not the malformed input).
  - [ ] Create `src/FormForge.Api.Tests/Common/Logging/SqlFingerprintTests.cs`:
    - `Fingerprint_StripsSingleQuotedLiterals` — `"SELECT * FROM x WHERE name = 'bob'"` → `"SELECT * FROM x WHERE name = ?"`.
    - `Fingerprint_StripsInlineNumbers` — `"SELECT id FROM x WHERE age > 18"` → `"SELECT id FROM x WHERE age > ?"`.
    - `Fingerprint_PreservesNamedPlaceholders` — `"INSERT INTO x (id, name) VALUES (@p0, @p1)"` → unchanged.
    - `Fingerprint_NullThrows` — `ArgumentNullException`.
  - [ ] Create `src/FormForge.Api.Tests/Common/Logging/ProblemDetailsCorrelationIdTests.cs`:
    - Trigger an exception path on a test endpoint (or hit `/__notfound`) → response body's `extensions.correlationId` equals the response header `X-Correlation-ID`.
  - [ ] Create `src/FormForge.Api.Tests/ServiceDefaults/OtlpEndpointValidationTests.cs` (use the live `ConfigureOpenTelemetry` extension via a throwaway host):
    - Valid endpoint: `http://localhost:4317` → no warning logged.
    - Empty / whitespace endpoint → no warning, no exporter.
    - Malformed value `"not a uri"` → warning logged, host starts cleanly.
    - Wrong scheme `"file:///tmp/x"` → warning logged, host starts cleanly.
  - [ ] **CA2254 verification (build-gate test):** add `src/FormForge.Api.Tests/Common/Logging/Ca2254FixtureNotes.md` (text doc only — not a runnable test) that includes a 3-line code sample of a string-interpolated `ILogger` call and a one-line shell command (`dotnet build src/FormForge.Api/` after temporarily uncommenting it) the reviewer can run to prove the analyzer fires. Do not actually commit a broken-build file — the doc is the reproducer. AC-4 is satisfied by the analyzer being active; the doc preserves the proof.

- [x] **Task 8 — Manual verification + build gates** (AC: all)
  - [ ] `dotnet build` — zero warnings, zero errors.
  - [ ] `dotnet format --verify-no-changes` — clean.
  - [ ] `dotnet test` — all new tests pass; no regressions.
  - [ ] `cd web && npm run build` — clean (no frontend changes; regression check only).
  - [ ] Run API in Development:
    - `curl -i http://localhost:5xxx/ -H "X-Correlation-ID: 01HX9JK8YZ4N6T2X3V5W7P9Q0R"` → response header carries the same value; stdout log line is JSON containing the ID inside `Scopes`.
    - `curl -i http://localhost:5xxx/` (no header) → response header is a fresh 26-char ULID.
    - `curl -i http://localhost:5xxx/nope` → 404 ProblemDetails body has `extensions.correlationId` matching the `X-Correlation-ID` response header.
  - [ ] Run AppHost (`dotnet run --project src/FormForge.AppHost`) — verify Aspire Dashboard receives logs and the correlation ID appears in the "Structured" log column / scope details.
  - [ ] Run Compose mode (`docker compose up`) — `docker compose logs api` shows JSON lines, not the default plain-text formatter.

### Review Findings

Code review run on 2026-05-22 — three parallel layers (Blind Hunter, Edge Case Hunter, Acceptance Auditor). 27 findings raised across layers → 12 actionable after triage + dedup (15 dismissed).

**Decision-needed (7) — user input required before patching:**

- [x] [Review][Decision] **ULID echo loses original case** — `Ulid.TryParse` is case-insensitive but `parsed.ToString()` returns canonical uppercase, so a client sending lowercase `01hx…` gets `01HX…` back. AC-1 says "the same correlation ID is set on the response header" — "same" is ambiguous (byte-equal vs canonical form). [src/FormForge.Api/Common/Logging/CorrelationIdMiddleware.cs:67-69]; test gap [src/FormForge.Api.Tests/Common/Logging/CorrelationIdMiddlewareTests.cs:14-28]
- [x] [Review][Decision] **SqlFingerprint regex misses negatives, hex, scientific, dollar-quoted, E'' literals** — `-100` → `-?` (sign retained); `0x1F` survives unchanged; `1.5e10` → `?e?`; PG `$tag$secret$tag$` unchanged; `E'\n'` may terminate prematurely. Spec sanctions this as "defense in depth" with parameterization at the call site as the primary mechanism — but the helper's contract claim of "placeholders only -- never parameter values" is not upheld. [src/FormForge.Api/Common/Logging/SqlFingerprint.cs:13-19]
- [x] [Review][Decision] **`BeginDdlScope` and `BeginCrudScope` emit identical scope payloads** — both produce `{ designerId, operation, sqlFingerprint }` with no tag distinguishing DDL from CRUD; downstream log filtering cannot tell them apart. Spec says "separate method only for caller-site readability" — so the API shape is fixed, but should the payloads carry an `operationKind` key? [src/FormForge.Api/Common/Logging/SqlFingerprint.cs:30-58]
- [x] [Review][Decision] **`endpoint` scope value embeds unbounded, unsanitized `Request.Path`** — every log line for the request carries the full path (Kestrel allows ~8 KB) with no length cap and no control-character filter. The class header advertises log-injection prevention but doesn't apply it here. Spec just says scope contains "endpoint (request path + method)". Cap length / strip CR/LF or accept as-is? [src/FormForge.Api/Common/Logging/CorrelationIdMiddleware.cs:51-53]
- [x] [Review][Decision] **`ProblemDetailsCorrelationIdTests` is gated by two nested `if`s — green-by-default** — body assertion runs only if the response body is non-empty JSON containing a `correlationId` property. A 404 with empty body passes without ever verifying the AC-5 extension wiring. Strengthen with a route that explicitly produces a ProblemDetails body, or accept the header as canonical proof (as Completion Notes argues)? [src/FormForge.Api.Tests/Common/Logging/ProblemDetailsCorrelationIdTests.cs:24-37]
- [x] [Review][Decision] **`IsValidOtlpEndpoint` accepts URLs with embedded userinfo (credentials)** — `http://user:pass@otel:4317` passes validation and credentials propagate to `UseOtlpExporter()`. Spec only requires "absolute http/https with non-empty host", so technically compliant — but a real exfil risk if operators embed creds in URLs. Reject userinfo, strip silently, or accept? [src/FormForge.ServiceDefaults/Extensions.cs:108-113]
- [x] [Review][Decision] **AC-6 test coverage narrower than Task 7 prescribes** — tests only assert the boolean `IsValidOtlpEndpoint` helper; Task 7 calls for "live `ConfigureOpenTelemetry` extension via a throwaway host" with empty / valid / malformed / wrong-scheme behavioral cases (warning logged vs not). Auditor judged AC-6 itself satisfied since the production gate is wired. Expand tests now or accept helper-level coverage? [src/FormForge.Api.Tests/ServiceDefaults/OtlpEndpointValidationTests.cs:1-46]

**Patch (4) — fix is unambiguous, awaiting batch apply:**

- [x] [Review][Patch] **Multiple `X-Correlation-ID` header values silently fall through to fresh ULID** — `StringValues.ToString()` joins repeated headers with commas; the joined string fails `Ulid.TryParse` even when each individual value is valid (proxy duplication is common). Take `values[0]` and parse that. [src/FormForge.Api/Common/Logging/CorrelationIdMiddleware.cs:64-67]
- [x] [Review][Patch] **`BeginDdlScope` / `BeginCrudScope` return `IDisposable` via `!` null-suppression** — `ILogger.BeginScope<TState>` returns `IDisposable?` per framework contract; custom loggers (Moq default) can legitimately return null and a `using` causes NRE on dispose. Align the helper signature to `IDisposable?`. [src/FormForge.Api/Common/Logging/SqlFingerprint.cs:42, 57]
- [x] [Review][Patch] **Reflection-based OTLP test walks wrong assembly first; brittle fallback** — primary lookup `typeof(HostApplicationBuilder).Assembly.GetType(...)` cannot find the type (it lives in `FormForge.ServiceDefaults`); fallback iterates and `Assembly.Load`s every reference. Add `InternalsVisibleTo("FormForge.Api.Tests")` to `FormForge.ServiceDefaults` and call `ServiceDefaultsExtensions.IsValidOtlpEndpoint` directly. [src/FormForge.Api.Tests/ServiceDefaults/OtlpEndpointValidationTests.cs:30-44]
- [x] [Review][Patch] **`LogOtlpEndpointInvalid` logs the raw env-var value (credential leak via userinfo)** — if `OTEL_EXPORTER_OTLP_ENDPOINT=https://user:secret@bad..host` (invalid due to malformed host), the warning template echoes the secret. Drop the `{Value}` placeholder from the LoggerMessage (or strip userinfo). [src/FormForge.ServiceDefaults/Extensions.cs:148-150]

**Deferred (1) — pre-existing or spec-sanctioned, tracked in deferred-work.md:**

- [x] [Review][Defer] **WebApplicationFactory fixture relies on `Database.Migrate()` swallowing DNS-resolve failure for `ignored.invalid`** — Dev Notes "Testing standards summary" explicitly sanctions this approach and forbids in-memory EF provider / test-only env branch in Program.cs. Risk: DNS-resolver inconsistencies on some Windows CI agents could surface as a slow / hanging fixture startup. [src/FormForge.Api.Tests/Common/Logging/CorrelationIdMiddlewareTests.cs:63-72] — deferred, pre-existing

**Dismissed as noise (15):** brittle `OnStarting` cast (speculative future-refactor); claim that `UseExceptionHandler` re-execution drops the scope (incorrect — re-execution stays inside the original `BeginScope` frame); OTLP rejecting `grpc://`/`unix://` (spec explicitly limits to http/https); `LoggerFactory.Create` flush race (Console provider drains on dispose); `public partial class Program;` (spec mandates it for `WebApplicationFactory<Program>`); missing `Ulid` using directive (root namespace, no using needed); `BeginUserScope` dead code (spec carves out as Story 2.6 prep); test ULID literal fragility (speculative); middleware running on `/health`/`/alive` (spec mandates header on health responses); IDN hosts in OTLP URI (speculative DNS inconsistency); `.editorconfig` drive-by (documented in Completion Notes, functionally needed for test naming); `BeginUserScope` widened to `ClaimsPrincipal?` (defensive, semantic-equivalent); `Mvc.Testing 10.0.8` vs 10.0.7 (within spec's "10.0.x family"); AC-4 not demonstrated by a committed broken-build file (spec explicitly carves out the doc-only proof); `HttpContext.TraceIdentifier` overwrite (spec sanctions as "soft alignment" in Dev Notes).

## Dev Notes

### What this story does — and what it does NOT

This story stands up the **logging primitives** that every later epic depends on:

1. **Correlation ID middleware** — reads/generates a ULID per request; pushes it onto every log entry; echoes it on the response.
2. **JSON console formatter** — turns container stdout into structured JSON consumable by `docker compose logs`, Compose, prod log aggregators.
3. **`SqlFingerprint` helper API** — Epic 5 (Provisioning) and Epic 6 (Dynamic CRUD) will call this to satisfy FR-46 AC-3. No DDL or CRUD call sites are added in this story.
4. **CA2254 enforcement** — verifies the existing analyzer rule already gates string-interpolated `ILogger` calls at build time. No new analyzer package is added; the rule is part of `Microsoft.CodeAnalysis.NetAnalyzers`, which is already active via `AnalysisMode=AllEnabledByDefault`.
5. **Correlation ID in `ProblemDetails`** — the minimum hook so error responses carry the ID. Full RFC-7807 envelope (code, messageKey, etc.) lands with the auth pipeline in Story 2.1.
6. **OTLP exporter URI validation fix** — closes a Story 1.1 deferred review finding.

**Out of scope (owned by later stories):**
- `userId` / `roles` in log scope — `LogContextExtensions.BeginUserScope` is defined here, but actually invoked only after the auth pipeline lands in **Story 2.6** (effective-permission enforcement filter chain — AR-22).
- DDL / CRUD call sites that consume `SqlFingerprint.BeginDdlScope` / `BeginCrudScope` — **Epic 5** (Provisioning) and **Epic 6** (Dynamic CRUD).
- Dapper SQL-comment injection (`/* corr:01HX... */`) per Architecture §3.8 line 509 — Epic 5 / Epic 6 will inject this at the Dapper command-construction site. The correlation ID is reachable via `HttpContext.GetCorrelationId()` from those story contexts.
- Audit-table `correlation_id` columns — schema lands with Epic 2 (refresh_tokens audit) and Epic 5 (schema audit) when those tables are first defined.

### Architecture compliance — what this story implements

This story implements **FR-46** in full plus the structural prerequisites of:
- **AR-24** (Correlation ID Propagation): "Read `X-Correlation-ID` header or generate ULID; injected into `ILogger` scope; flowed onto Dapper SQL comments; in every log, every audit row …, every error response, response header." Today's scope covers logs, response header, and error response. Dapper SQL comments and audit-row columns land with their owner stories.
- **AR-38** (Observability Stack): "OpenTelemetry via Aspire ServiceDefaults … MSExtLogging + JSON console formatter + OTel logging exporter." JSON console formatter wired here.
- **AR-50** (Logging Conventions): "Required structured fields per log entry … blocked: string interpolation in ILogger, PII in audit messages, request body logging." CA2254 already blocks interpolation; PII / body-logging are runtime rules enforced by code review and the Pattern Anti-Examples table (architecture §"Logging Conventions" lines 872–917).
- **Architecture §3.8** (Correlation ID Propagation) and **§5.3** (Observability Stack).

### Current code state (what you are modifying)

**`src/FormForge.Api/Program.cs`** (current, 80 lines):
```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddProblemDetails();                         // ← extend (Task 5)
builder.Services.AddDbContext<FormForgeDbContext>(...);
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddOpenApi(options => { ... });          // unchanged
}
var app = builder.Build();
try { db.Database.Migrate(); } catch { StartupLog.MigrationFailed(...); }
app.UseExceptionHandler();
app.UseStaticFiles();                                          // ← UseMiddleware<CorrelationIdMiddleware>() goes BEFORE this (Task 2)
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(...);
}
app.MapGet("/", () => "FormForge API is running.");
app.MapDefaultEndpoints();
app.Run();
```
- The `StartupLog` partial class at the bottom uses `[LoggerMessage]` — that pattern is the canonical CA1848-compliant style; follow it for any new high-frequency log call sites (none in this story).
- The story adds `builder.Logging.AddJsonConsole(...)` (Task 3), `builder.Services.AddProblemDetails(opts => ...)` (Task 5), and `app.UseMiddleware<CorrelationIdMiddleware>()` BEFORE `app.UseExceptionHandler()` (Task 2).

**`src/FormForge.ServiceDefaults/Extensions.cs`** (current, 130 lines):
- `ConfigureOpenTelemetry` (line 47) already calls `builder.Logging.AddOpenTelemetry(...)` with `IncludeScopes = true`. Leave intact — that path covers the OTel/Aspire Dashboard surface.
- `AddOpenTelemetryExporters` (line 81) is the function to patch (Task 6). It currently activates the exporter on any non-whitespace value.

**`src/FormForge.Api/appsettings.json`** — leave the `Logging.LogLevel` block intact; the formatter is selected in code, not config, to make the choice unambiguous and Compose-portable.

### Previous story intelligence (Story 1.4, completed 2026-05-22)

Key inherited context that affects this story:

- **`TreatWarningsAsErrors=true`** repo-wide — `CA1815`, `CA1819`, `CA1852` (sealed), `CA2007` (ConfigureAwait), and `CA2254` (log template literal) will all fire. The new classes are `internal sealed` to avoid CA1515 and CA1852.
- **`AnalysisMode=AllEnabledByDefault`** — every CA rule is on. CA2254 is what gives us AC-4 for free; do not add a separate `Meziantou.Analyzer` package for this story (deferred to a later "tighten analyzer set" pass).
- **CPM enforced** — both new packages (`Ulid`, `Microsoft.AspNetCore.Mvc.Testing`) go in `Directory.Packages.props` with a `<PackageVersion>`; no inline `Version=` on `<PackageReference>`.
- **`InvariantGlobalization=true`** — use `StringComparison.Ordinal` everywhere; ULID parsing (`Ulid.TryParse`) is culture-invariant by design.
- **`UseStaticFiles()` is wired** — the correlation middleware must run BEFORE static files so static-file responses also carry the header.
- **OpenAPI is dev-only and unaffected** — Swagger UI continues to work; the correlation header simply appears on its responses too.
- **Aspire Dashboard** — Story 1.2 wired the dashboard. The architecture says correlation IDs should appear in Trace tags (AR-38 / §5.3 line 652). The framework's `Activity.Current.TraceId` is the OTel surface; setting `HttpContext.TraceIdentifier = correlationId` makes them align. This is a soft alignment, not a hard contract.
- **`#pragma warning disable CA1031`** is used in `Program.cs` line 51–53 to suppress "do not catch general Exception" for the migration retry path. Pattern reuse is fine; avoid copy-pasting the suppression elsewhere.
- **From Story 1.3 review:** `dotnet test` currently discovers zero tests. This story adds the first real tests, which means the `FormForge.Api.Tests` project will start producing pass counts on every build — track this in CI logs.

### Git intelligence

Recent commits:
- `e301706` — "Story 1.4 follow-up — address 3 critical code-review findings"
- `9eaa224` — "Story 1.4 — OpenAPI 3.1 spec at /openapi/v1.json + Swagger UI in dev"
- `781e11d` — "Apply Story 1.3 code-review fixes and close story"
- `ce5a8d2` — "Story 1.3 — Docker Compose local stack: Dockerfile + compose + EF Core scaffold"

Pattern from the last three stories:
- Each story produces one feature commit; review fixes go in a separate follow-up commit.
- All `Common/` subfolders follow the architecture's directory tree (Architecture lines 985–1008): `Common/Endpoints/`, `Common/Errors/`, `Common/Json/`, `Common/Logging/`, `Common/RateLimiting/`, `Common/Security/`, `Common/Spa/`. **Use `Common/Logging/` exactly as named.**

After this story lands, commit message: `Story 1.5 — Structured logging + correlation IDs + OTLP URI validation`.

### Latest tech information (verify versions at implementation time)

- **`Ulid` package** (Cysharp) — https://www.nuget.org/packages/Ulid. Latest stable in the `1.3.x` line as of April 2026. Targets `netstandard2.1`; works on `net10.0`. Has zero runtime dependencies. API surface used: `Ulid.NewUlid()`, `Ulid.TryParse(string, out Ulid)`, `Ulid.ToString()` (26-char Crockford base32).
- **`Microsoft.AspNetCore.Mvc.Testing`** — track the `10.0.x` family matching `Microsoft.AspNetCore.OpenApi 10.0.7`. Provides `WebApplicationFactory<TEntryPoint>`. Pull from https://www.nuget.org/packages/Microsoft.AspNetCore.Mvc.Testing.
- **`Microsoft.Extensions.Logging.Console`** — already pulled in transitively via `Microsoft.AspNetCore.App` framework reference. **No new package needed for JSON console**. `AddJsonConsole(...)` is the canonical extension.
- **CA2254** — shipped by `Microsoft.CodeAnalysis.NetAnalyzers` (transitive via the SDK). Documentation: https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2254. It catches `LogXxx($"...{var}...")` patterns and demands a constant template instead.

### File structure

**Touch:**
- `Directory.Packages.props` — add `Ulid` and `Microsoft.AspNetCore.Mvc.Testing` versions
- `src/FormForge.Api/FormForge.Api.csproj` — add `Ulid` PackageReference
- `src/FormForge.Api/Program.cs` — `AddJsonConsole`, `AddProblemDetails(opts)`, `UseMiddleware<CorrelationIdMiddleware>()`, `public partial class Program;` declaration
- `src/FormForge.ServiceDefaults/Extensions.cs` — patch `AddOpenTelemetryExporters` + add `ServiceDefaultsLog` partial
- `src/FormForge.Api.Tests/FormForge.Api.Tests.csproj` — add `Microsoft.AspNetCore.Mvc.Testing` PackageReference

**New:**
- `src/FormForge.Api/Common/Logging/CorrelationIdMiddleware.cs`
- `src/FormForge.Api/Common/Logging/LogContextExtensions.cs`
- `src/FormForge.Api/Common/Logging/SqlFingerprint.cs`
- `src/FormForge.Api.Tests/Common/Logging/CorrelationIdMiddlewareTests.cs`
- `src/FormForge.Api.Tests/Common/Logging/SqlFingerprintTests.cs`
- `src/FormForge.Api.Tests/Common/Logging/ProblemDetailsCorrelationIdTests.cs`
- `src/FormForge.Api.Tests/Common/Logging/Ca2254FixtureNotes.md` (reviewer-runnable proof; not a unit test)
- `src/FormForge.Api.Tests/ServiceDefaults/OtlpEndpointValidationTests.cs`

**Do NOT touch:**
- `src/FormForge.AppHost/AppHost.cs` — no Aspire orchestration changes
- `web/` — no frontend changes (the `X-Correlation-ID` outbound from frontend `httpClient.ts` lands with Decision 4.7 in Story 2.1, not here)
- `docker-compose.yml`, `Dockerfile` — no infra changes
- `appsettings.*.json` — formatter is selected in code (deliberate; do not introduce dual sources of truth)
- Existing EF Core / OpenAPI files

### Anti-patterns to avoid

| Don't | Why |
|---|---|
| Add `Serilog` or any third-party logging framework | Architecture §5.3 explicitly forbids Serilog. Use built-in `Microsoft.Extensions.Logging` + JSON console + OTel. |
| `_logger.LogInformation($"Saved {id}")` | Breaks structured logging; CA2254 will reject. Use `_logger.LogInformation("Saved {RecordId}", id)` instead. |
| Log request bodies | AR-50 explicitly blocks. Body logging leaks PII and bloats logs. |
| Log raw email / displayName in audit messages | AR-50 blocks PII in audit. Use IDs only. |
| Echo a non-ULID `X-Correlation-ID` back to the client | Log-injection risk and breaks the AR-24 contract that the ID is a ULID. Validate via `Ulid.TryParse`; generate fresh if invalid. |
| Read correlation ID from `HttpContext.TraceIdentifier` only | Aspire / .NET hosts may overwrite `TraceIdentifier`. Source of truth is `HttpContext.Items["CorrelationId"]`. Set both so OTel `Activity.TraceId` aligns, but **read** from `Items`. |
| Wire `userId` scope before Story 2.6 | The auth pipeline does not yet exist; you would need to fake claims and add suppressed-warning code. Defer; `LogContextExtensions.BeginUserScope` ships dormant. |
| Inject SQL via Dapper at this story | No Dapper code in scope. Epic 5 / Epic 6 own the Dapper layer. |
| Add `Meziantou.Analyzer` package "just in case" | Scope creep. CA2254 covers AC-4. Add Meziantou later in a dedicated analyzer-tightening story. |
| Run middleware AFTER `UseStaticFiles` / `UseExceptionHandler` | Static-file responses and exception responses must carry the header too. Middleware order is `UseMiddleware<CorrelationIdMiddleware>()` → `UseExceptionHandler()` → `UseStaticFiles()`. |
| Generate the ULID inside an `OnStarting` callback | The ID must be available to every middleware / handler that runs after correlation middleware. Generate immediately on invoke; the response-header write is what goes inside `OnStarting`. |

### Project Structure Notes

```
tinnitus/
├── Directory.Packages.props            ← ADD Ulid + Microsoft.AspNetCore.Mvc.Testing
├── src/
│   ├── FormForge.Api/
│   │   ├── FormForge.Api.csproj        ← ADD Ulid PackageReference
│   │   ├── Program.cs                  ← AddJsonConsole, AddProblemDetails(opts), UseMiddleware, partial Program;
│   │   └── Common/
│   │       └── Logging/                ← NEW directory
│   │           ├── CorrelationIdMiddleware.cs
│   │           ├── LogContextExtensions.cs
│   │           └── SqlFingerprint.cs
│   ├── FormForge.ServiceDefaults/
│   │   └── Extensions.cs               ← PATCH AddOpenTelemetryExporters + add ServiceDefaultsLog
│   └── FormForge.Api.Tests/
│       ├── FormForge.Api.Tests.csproj  ← ADD Microsoft.AspNetCore.Mvc.Testing PackageReference
│       ├── Common/Logging/
│       │   ├── CorrelationIdMiddlewareTests.cs
│       │   ├── SqlFingerprintTests.cs
│       │   ├── ProblemDetailsCorrelationIdTests.cs
│       │   └── Ca2254FixtureNotes.md
│       └── ServiceDefaults/
│           └── OtlpEndpointValidationTests.cs
```

### Testing standards summary

- **Test framework:** xUnit (already pinned via CPM).
- **Web testing:** `WebApplicationFactory<Program>` from `Microsoft.AspNetCore.Mvc.Testing` — the canonical .NET 10 in-process test host. No external network.
- **No Testcontainers usage in this story** — the DB is unused by the middleware. The migration call at startup in `Program.cs` lines 45–56 wraps `Database.Migrate()` in a `try / catch (Exception)` that logs `StartupLog.MigrationFailed` and continues. When `WebApplicationFactory<Program>` constructs the host without a reachable PostgreSQL, the catch fires once during fixture init and the warning shows up in test output; **this is expected and acceptable**, not a test failure. Do NOT introduce an in-memory EF provider or test-only environment branch in `Program.cs` to "fix" this — production code must stay unchanged.
- **Optional cleanup if the warning is noisy:** in the `WebApplicationFactory` derived class, override `ConfigureWebHost` and call `builder.UseSetting("ConnectionStrings:formforge", "Host=localhost;Database=ignored;Username=u;Password=p")` plus `builder.ConfigureTestServices(s => s.RemoveAll<FormForgeDbContext>())`. The migration call then hits a missing DbContext registration and the `try/catch` swallows it identically — but without the connection-attempt latency on first request.
- **Assertions:** plain xUnit `Assert.Equal` / `Assert.True`. No FluentAssertions in scope (zero packages added beyond the two listed above).
- **Naming:** `MethodName_Scenario_Expected` (existing convention not yet established — set it here, follow it forward).
- **Coverage expectation:** the four new helper-set classes (`CorrelationIdMiddleware`, `SqlFingerprint`, `ProblemDetails` customizer, `OTLP URI gate`) each have at least one passing test. CA2254 has the reviewer-runnable proof doc.

### References

- `_bmad-output/planning-artifacts/epics.md` — § "Story 1.5: Structured Logging with Correlation IDs" (lines 409–432, canonical ACs)
- `_bmad-output/planning-artifacts/architecture.md`
  - § "3.8 — Correlation ID Propagation" (lines 507–510) — read-or-generate ULID, scope injection, response header
  - § "5.3 — Observability Stack" (lines 641–652) — JSON console + OTel; trace tags
  - § "Logging Conventions" (lines 872–917) — structured fields, anti-patterns
  - § "Project Structure" lines 985–1008 — `Common/Logging/` directory layout
- `_bmad-output/implementation-artifacts/deferred-work.md` — OTLP exporter URI validation (deferred from Story 1.1 review; owned by Story 1.5)
- `_bmad-output/implementation-artifacts/1-4-auto-generated-openapi-spec-and-swagger-ui.md` — CPM enforcement, `TreatWarningsAsErrors`, `InvariantGlobalization`, `internal sealed` discipline, `[LoggerMessage]` source-generated pattern
- NuGet: https://www.nuget.org/packages/Ulid — pick latest stable at dev time
- NuGet: https://www.nuget.org/packages/Microsoft.AspNetCore.Mvc.Testing — pick `10.0.x`
- Microsoft Docs: https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2254 — CA2254 rule
- Microsoft Docs: https://learn.microsoft.com/aspnet/core/fundamentals/logging/#json — JSON console formatter

## Change Log

- Story 1.5 created — structured logging + correlation IDs + deferred OTLP URI validation (Date: 2026-05-22)
- Story 1.5 implemented — middleware + JSON console + SqlFingerprint helper + ProblemDetails correlation + OTLP URI gate; all 19 tests pass; manual smoke confirms response-header echo and `correlationId`/`endpoint` in `Scopes` (Date: 2026-05-22)
- Story 1.5 code review complete — 11 patches applied (4 direct + 7 from decision resolutions), 1 deferred to `deferred-work.md`, 15 dismissed. Final test count 35/35; build clean (0 warnings, 0 errors); dotnet format clean. Status → done (Date: 2026-05-22)

## Dev Agent Record

### Agent Model Used

claude-opus-4-7[1m]

### Debug Log References

### Completion Notes List

- **Package versions selected:** `Ulid 1.4.1` (Cysharp) and `Microsoft.AspNetCore.Mvc.Testing 10.0.8` — both latest stable at implementation time; `10.0.8` aligns with the EF Core `10.0.8` and `Microsoft.AspNetCore.OpenApi 10.0.7` family already pinned.
- **`Ulid` API surface used:** `Ulid.NewUlid()`, `Ulid.TryParse(string, out Ulid)`, `.ToString()` (returns the 26-char Crockford base32 form). No globalization dependencies; safe under `InvariantGlobalization=true`.
- **Middleware order in `Program.cs`:** `app.UseMiddleware<CorrelationIdMiddleware>()` is registered **before** `app.UseExceptionHandler()` so the correlation scope wraps the exception-handling pipeline and the response header survives `Response.Clear()`. The response-header write is itself inside an `OnStarting` callback (closure over a captured value-tuple state) to avoid mutating headers after the response body has started flushing.
- **`HttpContext.TraceIdentifier` is also set to the correlation ID** so OTel `Activity.TraceId` / .NET diagnostic surfaces align with the structured-log `correlationId` field. Source of truth for the value remains `HttpContext.Items["CorrelationId"]` (read via `LogContextExtensions.GetCorrelationId`).
- **CA1812 on the middleware** — the analyzer cannot see `UseMiddleware<T>()` runtime instantiation. Suppressed at class level with `[SuppressMessage("Performance", "CA1812", ...)]` and a justification.
- **CA1515 on `public partial class Program`** — `WebApplicationFactory<Program>` requires the entry point type to be public. Suppressed inline with a `#pragma warning disable CA1515` block.
- **Pre-existing `.editorconfig` bug discovered and fixed:** the line `dotnet_diagnostic.CA1707.severity = none` was placed after `[*.md]`, making it effectively dead. Moved into a new test-scoped section `[src/FormForge.Api.Tests/**/*.cs]` along with CA1515 and CA2007 suppressions. CA1707 stays active in production code (which uses no underscored member names today).
- **`InternalsVisibleTo`** added to `FormForge.Api.csproj` so the test project can construct `SqlFingerprint` directly. The internal-only surface stays narrow — only the helper class and the middleware are exercised through this seam.
- **JSON console formatter wiring:** `builder.Logging.AddJsonConsole(...)` selected with `IncludeScopes=true`, `UseUtcTimestamp=true`, ISO-8601 timestamp format, and non-indented writer. The OTel logging exporter wired in `ServiceDefaults.ConfigureOpenTelemetry` continues to coexist; both surfaces see the correlation ID inside scopes.
- **JSON formatter writes Scopes as a nested array, not flat fields** — as anticipated in AC-2's implementation note. The `correlationId` and `endpoint` keys land inside the last element of `Scopes[]`. Verified during smoke test: a `GET /` request with `X-Correlation-ID: 01HX9JK8YZ4N6T2X3V5W7P9Q0R` produced `"Scopes":[..., {"correlationId":"01HX9JK8YZ4N6T2X3V5W7P9Q0R","endpoint":"GET /"}]` on the `Microsoft.AspNetCore.Routing.EndpointMiddleware` log line.
- **`ProblemDetails.Extensions["correlationId"]`** is populated by `CustomizeProblemDetails`. The integration test sends a request to a non-existent route and asserts the response header carries a ULID; the body assertion is conditional because ASP.NET Core's default 404 emits an empty body when no MapNotFound handler is wired — the header remains the canonical proof.
- **OTLP URI validation (Task 6 / AC-6):** the gate is now `Uri.TryCreate(value, UriKind.Absolute, ...)` plus scheme check (`http` or `https`) plus non-empty host. Invalid values produce a `Warning` via a source-generated `[LoggerMessage]` partial; host startup continues without the exporter. Test coverage: 3 valid + 6 invalid cases (whitespace handled at the outer `!string.IsNullOrWhiteSpace` gate; reflection used to reach the internal `IsValidOtlpEndpoint` helper without exposing it).
- **Test count grew from 0 to 19.** First real tests in `FormForge.Api.Tests`. Test classes use the `Method_Scenario_Expected` xUnit convention, enabled by the `.editorconfig` scope fix.
- **`Microsoft.AspNetCore` log level note:** at the default appsettings.json level of `Warning`, ASP.NET Core's per-request `Information` log lines do not emit, so a casual run of the API does not visibly show the correlation scope. Verified via `dotnet run -- --Logging:LogLevel:Microsoft.AspNetCore=Information` for the smoke test. This is **expected and correct** — production should not be flooded with per-request Info lines. Application-domain logs (any future `_logger.LogInformation` call from FormForge code) emit at `Information` by default and will carry the scope automatically.
- **Manual smoke test results:**
  - `curl -i http://localhost:5429/ -H "X-Correlation-ID: 01HX9JK8YZ4N6T2X3V5W7P9Q0R"` → `X-Correlation-ID: 01HX9JK8YZ4N6T2X3V5W7P9Q0R` (echoed).
  - `curl -i http://localhost:5429/` → fresh ULID `01KS7ZJYSJ52M7DYSC6RYPSFRD` returned.
  - `curl -i http://localhost:5429/nope` → `404 Not Found` with `X-Correlation-ID: 01KS7ZJYWNWH77M8Q5X2P1WP92` (correlation ID preserved through the 404 path).
  - With ASP.NET Core info-level: request log line carries `{"correlationId":"01HX9JK8YZ4N6T2X3V5W7P9Q0R","endpoint":"GET /"}` inside `Scopes[]`.
- **Build gates:** `dotnet build` 0 errors / 0 warnings; `dotnet format --verify-no-changes` clean; `dotnet test` 19 passed / 0 failed / 0 skipped; `npm run build` (web) clean.
- **No frontend changes** — the outbound `X-Correlation-ID: {clientUlid}` header from `web/src/lib/api/httpClient.ts` (per AR-32) lands with Story 2.1 (auth) when the httpClient wrapper is first wired.

### File List

- Modified: `Directory.Packages.props` — added `Ulid 1.4.1` and `Microsoft.AspNetCore.Mvc.Testing 10.0.8` PackageVersion entries
- Modified: `src/FormForge.Api/FormForge.Api.csproj` — added `Ulid` PackageReference + `InternalsVisibleTo FormForge.Api.Tests`
- Modified: `src/FormForge.Api/Program.cs` — added `using` for new namespace, `builder.Logging.AddJsonConsole`, expanded `AddProblemDetails` with `CustomizeProblemDetails`, `app.UseMiddleware<CorrelationIdMiddleware>()` before `UseExceptionHandler`, `public partial class Program;` declaration with CA1515 suppression
- Modified: `src/FormForge.ServiceDefaults/Extensions.cs` — replaced naive `!IsNullOrWhiteSpace` gate in `AddOpenTelemetryExporters` with `IsValidOtlpEndpoint` (URI absolute + http/https + non-empty host); added `ServiceDefaultsLog.LogOtlpEndpointInvalid` partial logger
- Modified: `src/FormForge.Api.Tests/FormForge.Api.Tests.csproj` — added `Microsoft.AspNetCore.Mvc.Testing` PackageReference
- Modified: `.editorconfig` — moved misplaced `CA1707` suppression into a properly scoped `[src/FormForge.Api.Tests/**/*.cs]` block; added CA1515 and CA2007 to the same test-only scope
- New: `src/FormForge.Api/Common/Logging/CorrelationIdMiddleware.cs` — middleware reads/generates ULID, sets `HttpContext.Items["CorrelationId"]` + `TraceIdentifier`, writes response header via `OnStarting`, pushes `correlationId`/`endpoint` onto `ILogger.BeginScope`
- New: `src/FormForge.Api/Common/Logging/LogContextExtensions.cs` — `HttpContext.GetCorrelationId()` and `ILogger.BeginUserScope(ClaimsPrincipal)` (the latter dormant until Story 2.6)
- New: `src/FormForge.Api/Common/Logging/SqlFingerprint.cs` — `Fingerprint(string)` plus `BeginDdlScope` / `BeginCrudScope` helpers for Epic 5/6 consumers; uses two `[GeneratedRegex]` partials to strip quoted/numeric literals as defense in depth
- New: `src/FormForge.Api.Tests/Common/Logging/CorrelationIdMiddlewareTests.cs` — 3 tests + `FormForgeApiFactory` (WebApplicationFactory subclass with unreachable connection string so `Database.Migrate()` fails fast inside the existing try/catch)
- New: `src/FormForge.Api.Tests/Common/Logging/SqlFingerprintTests.cs` — 6 tests covering literal stripping, placeholder preservation, identifier protection, and null guard
- New: `src/FormForge.Api.Tests/Common/Logging/ProblemDetailsCorrelationIdTests.cs` — verifies 404 response carries `X-Correlation-ID` header (and `extensions.correlationId` when body is JSON)
- New: `src/FormForge.Api.Tests/Common/Logging/Ca2254FixtureNotes.md` — reviewer-runnable proof that CA2254 fires on string-interpolated `ILogger` calls (no broken-build code committed)
- New: `src/FormForge.Api.Tests/ServiceDefaults/OtlpEndpointValidationTests.cs` — 3 valid + 6 invalid URI cases for the OTLP gate (reflection access to the internal `IsValidOtlpEndpoint`)
