# Story 1.4: Auto-Generated OpenAPI Spec and Swagger UI

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Developer or Integrator,
I want access to an up-to-date OpenAPI 3.1 spec and a Swagger UI in development,
so that I can integrate without reading source code.

## Acceptance Criteria

### AC-1 — OpenAPI 3.1 spec served at `/openapi/v1.json`

**Given** the API is running
**When** I GET `/openapi/v1.json`
**Then** the response is a valid OpenAPI 3.1 spec auto-generated from Minimal API endpoint metadata

### AC-2 — Swagger UI at `/swagger` (development only)

**Given** the API is running in Development mode
**When** I open `/swagger`
**Then** Swagger UI renders and is interactively usable, pointing at `/openapi/v1.json`

### AC-3 — Swagger UI disabled in Production

**Given** the API is running in Production mode
**When** I open `/swagger`
**Then** I receive an HTTP 404 (Swagger UI disabled in production)

### AC-4 — Dynamic CRUD endpoint schema pattern (infrastructure only in this story)

**Given** a dynamic CRUD endpoint (`/api/data/{designerId}/*`) exists (added in Epic 6)
**When** I view its schema in the OpenAPI document
**Then** the request and response bodies are typed as `object` with `additionalProperties: true` and the description explains the runtime-dynamic shape (per AR-19)
**And** the `designerId` path parameter declares pattern `^[a-z_][a-z0-9_]{0,62}$`

> **Note:** No dynamic endpoints exist in this story. The task here is to add an `IOpenApiOperationTransformer` stub and document the pattern for Epic 6 developers to implement. The transformer shell is wired up now; Epic 6 adds the actual endpoint registrations.

### AC-5 — Bearer auth security scheme declared globally

**Given** any authenticated endpoint (first ones added in Epic 2)
**When** I view its OpenAPI metadata
**Then** it documents Bearer-token authentication as a security requirement

> **Note:** No auth endpoints exist yet. This story registers the global Bearer security scheme in the OpenAPI document so it is ready for Epic 2 endpoint-level wiring. Actual per-endpoint security requirements (`[Authorize]` metadata → operation transformer) are applied in Story 2.1.

## Tasks / Subtasks

- [x] **Task 1 — Add `Swashbuckle.AspNetCore.SwaggerUi` package** (AC: 2, 3)
  - [x] Add `<PackageVersion Include="Swashbuckle.AspNetCore.SwaggerUi" Version="..." />` to `Directory.Packages.props` — pick the latest stable version compatible with .NET 10 (check https://www.nuget.org/packages/Swashbuckle.AspNetCore.SwaggerUi at dev time; as of May 2026 expect `8.x` or later)
  - [x] Add `<PackageReference Include="Swashbuckle.AspNetCore.SwaggerUi" />` (no `Version=` per CPM) to `src/FormForge.Api/FormForge.Api.csproj` — this sub-package provides only `app.UseSwaggerUI()` middleware; it does NOT include Swashbuckle's spec generation (we use `Microsoft.AspNetCore.OpenApi` for that)
  - [x] Run `dotnet restore` — confirm clean, no NU1605 / NU1010

- [x] **Task 2 — Configure Swagger UI middleware in `Program.cs`** (AC: 2, 3)
  - [x] Move `app.MapOpenApi()` into the existing `if (app.Environment.IsDevelopment())` block (it is already there — verify no accidental move needed)
  - [x] Immediately after `app.MapOpenApi()`, add:
    ```csharp
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/openapi/v1.json", "FormForge API v1"));
    ```
  - [x] Confirm both calls remain inside the `IsDevelopment()` guard — Production must NOT serve Swagger UI (AC-3)
  - [x] Manually verify: `dotnet run --project src/FormForge.Api` → open `/swagger` → Swagger UI renders → the dropdown shows "FormForge API v1" → the `/openapi/v1.json` link works

- [x] **Task 3 — Enrich OpenAPI document metadata via document transformer** (AC-1, AC-5)
  - [x] Extend the `if (builder.Environment.IsDevelopment())` block that wraps `builder.Services.AddOpenApi()`:
    ```csharp
    builder.Services.AddOpenApi(options =>
    {
        options.AddDocumentTransformer((document, context, cancellationToken) =>
        {
            document.Info = new()
            {
                Title = "FormForge API",
                Version = "v1",
                Description = "FormForge dynamic forms platform. All endpoints except /api/auth/* require a valid JWT Bearer token."
            };
            document.Components ??= new Microsoft.OpenApi.Models.OpenApiComponents();
            document.Components.SecuritySchemes["Bearer"] = new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "JWT access token. Obtain via POST /api/auth/login. 15-minute TTL; refresh via POST /api/auth/refresh."
            };
            return Task.CompletedTask;
        });
    });
    ```
  - [x] Confirm using statements: `Microsoft.OpenApi.Models` is already available transitively through `Microsoft.AspNetCore.OpenApi` — no extra package needed
  - [x] Verify `dotnet build` — zero new warnings

- [x] **Task 4 — Add operation transformer shell for future dynamic endpoint annotation** (AC-4)
  - [x] Create `src/FormForge.Api/Common/OpenApi/DynamicEndpointDocumentTransformer.cs`:
    ```csharp
    using Microsoft.AspNetCore.OpenApi;
    using Microsoft.OpenApi.Models;

    namespace FormForge.Api.Common.OpenApi;

    // Applied to /api/data/{designerId}/* endpoints in Epic 6.
    // Marks request/response bodies as object+additionalProperties:true and
    // adds the designerId path parameter pattern constraint.
    internal sealed class DynamicEndpointDocumentTransformer : IOpenApiOperationTransformer
    {
        private const string DesignerIdPattern = @"^[a-z_][a-z0-9_]{0,62}$";

        public Task TransformAsync(
            OpenApiOperation operation,
            OpenApiOperationTransformerContext context,
            CancellationToken cancellationToken)
        {
            // Only operate on /api/data/{designerId}/* routes (no-op until Epic 6 adds them)
            if (!context.Description.RelativePath?.StartsWith("api/data/", StringComparison.OrdinalIgnoreCase) ?? true)
                return Task.CompletedTask;

            // Annotate designerId path parameter with SQL-safe identifier pattern
            var designerIdParam = operation.Parameters?.FirstOrDefault(p =>
                p.Name.Equals("designerId", StringComparison.OrdinalIgnoreCase) && p.In == ParameterLocation.Path);
            if (designerIdParam?.Schema != null)
            {
                designerIdParam.Schema.Pattern = DesignerIdPattern;
            }

            // Mark request body as dynamic object
            if (operation.RequestBody?.Content != null)
            {
                foreach (var content in operation.RequestBody.Content.Values)
                {
                    content.Schema = new OpenApiSchema
                    {
                        Type = "object",
                        AdditionalPropertiesAllowed = true,
                        Description = "Runtime-determined shape; fieldKeys and types defined by the bound Designer version schema."
                    };
                }
            }

            // Mark all 2xx response bodies as dynamic object
            foreach (var response in operation.Responses.Values)
            {
                if (response.Content == null) continue;
                foreach (var content in response.Content.Values)
                {
                    content.Schema = new OpenApiSchema
                    {
                        Type = "object",
                        AdditionalPropertiesAllowed = true,
                        Description = "Runtime-determined shape. System columns (id, createdAt, updatedAt, etc.) are camelCase; user fieldKeys are verbatim (Option C hybrid, per AR-46)."
                    };
                }
            }

            return Task.CompletedTask;
        }
    }
    ```
  - [x] Register the transformer inside the `AddOpenApi()` options call (Task 3 block):
    ```csharp
    options.AddOperationTransformer<DynamicEndpointDocumentTransformer>();
    ```
  - [x] Add the transformer class to DI — ASP.NET Core auto-registers `IOpenApiOperationTransformer` implementations added via `AddOperationTransformer<T>()` — no manual `AddTransient` needed
  - [x] Confirm `dotnet build` passes; the transformer is a no-op until Epic 6 routes land

- [x] **Task 5 — Verify AC-1, AC-2, AC-3 manually and confirm build gates pass** (AC: 1, 2, 3, 4)
  - [x] `dotnet build` — zero warnings, zero errors
  - [x] `dotnet format --verify-no-changes` — clean
  - [x] `dotnet test` — zero tests, exit 0
  - [x] `cd web && npm run build` — clean (no frontend changes in this story; regression check only)
  - [x] Run API in Development (`ASPNETCORE_ENVIRONMENT=Development`):
    - `GET /openapi/v1.json` → HTTP 200, response body contains `"openapi": "3.1.0"`, `"title": "FormForge API"`, `"securitySchemes"` with `Bearer` entry
    - `GET /swagger` → HTTP 200, Swagger UI HTML page renders, endpoint list is visible
    - Confirm the `GET /` endpoint appears in the UI
  - [x] Run API in Compose/Production mode (`ASPNETCORE_ENVIRONMENT=Compose` or `Production`):
    - `GET /swagger` → HTTP 404
    - `GET /openapi/v1.json` → HTTP 404

### Review Findings

- [x] [Review][Decision→Patch FIXED] `/openapi/v1.json` gated behind `IsDevelopment()` — moved `AddOpenApi(...)` and `app.MapOpenApi()` outside the `IsDevelopment()` guard; only `UseSwaggerUI` remains dev-only. Spec now served in all environments per AC-1.
- [x] [Review][Patch FIXED] `RelativePath` leading-slash fragility — added `TrimStart('/')` before `StartsWith("api/data/", ...)`. [src/FormForge.Api/Common/OpenApi/DynamicEndpointDocumentTransformer.cs:32]
- [x] [Review][Defer] `IsSuccessStatusCode` accepts any string starting with '2' — non-standard keys like `"2abc"` or `"200 OK"` would be treated as success; low practical risk since ASP.NET Core emits well-formed numeric keys. [src/FormForge.Api/Common/OpenApi/DynamicEndpointDocumentTransformer.cs:122] — deferred, pre-existing
- [x] [Review][Defer] Body schema methods overwrite ALL media types including non-JSON — `ApplyDynamicRequestBodySchema` and `ApplyDynamicResponseBodySchema` replace schemas for all content types. [src/FormForge.Api/Common/OpenApi/DynamicEndpointDocumentTransformer.cs:88,107] — deferred, no Epic 6 routes exist yet
- [x] [Review][Defer] Collection-root `"api/data/"` route body rewrite without `designerId` guard — any route matching exactly `"api/data/"` passes the prefix check; body schemas are overwritten even though no `designerId` parameter was found. [src/FormForge.Api/Common/OpenApi/DynamicEndpointDocumentTransformer.cs:44] — deferred, no Epic 6 routes exist yet
- [x] [Review][Defer] `DesignerIdPattern` allows single-character identifiers — `^[a-z_][a-z0-9_]{0,62}$` permits 1-char names; alignment with actual SQL minimum length TBD in Epic 5. [src/FormForge.Api/Common/OpenApi/DynamicEndpointDocumentTransformer.cs:14] — deferred, SQL validation not yet implemented

## Dev Notes

### What this story does — and what it does NOT

This story wires up the OpenAPI/Swagger UI pipeline for the Foundation epic. It is **infrastructure only** — no new endpoints are added. The two "future" ACs (4 and 5) are satisfied by laying the groundwork:

- **AC-4 (dynamic endpoint schema):** `DynamicEndpointDocumentTransformer` is registered and no-op until Epic 6 adds `/api/data/{designerId}/*` routes. When those routes are added, the transformer fires automatically and enforces the `additionalProperties: true` + pattern constraint.
- **AC-5 (Bearer auth per endpoint):** The global Bearer security scheme is declared in `Components.SecuritySchemes`. Adding `security: [{ Bearer: [] }]` to individual operations is done in Epic 2 via an `IOpenApiOperationTransformer` that reads the `[Authorize]` endpoint metadata (not in scope here). Story 2.1 owns that operation transformer.

### Architecture compliance

This story implements **FR-45** and **AR-19** (OpenAPI versioning posture: no URL prefix in v1; spec at `/openapi/v1.json`; additive within v1).

The spec URL is `/openapi/v1.json` — NOT `/swagger/v1/swagger.json` (Swashbuckle default). The `SwaggerEndpoint` call must explicitly point at `/openapi/v1.json`.

### Current `Program.cs` state

```csharp
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddOpenApi();   // ← extend to AddOpenApi(options => {...})
}
// ...
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();                // already maps /openapi/v1.json ✓
    // ← add app.UseSwaggerUI(...) here
}
```

The `IsDevelopment()` guard already satisfies AC-3 (Compose and Production environments return 404 for `/swagger`). Do NOT change this guard.

### Package selection: Swashbuckle.AspNetCore.SwaggerUi

The `Swashbuckle.AspNetCore.SwaggerUi` sub-package provides **only the UI middleware** (`app.UseSwaggerUI()`). It does NOT include Swashbuckle's own spec generation. This is intentional — `Microsoft.AspNetCore.OpenApi` generates the spec; Swashbuckle just renders it.

**Do NOT** add `Swashbuckle.AspNetCore` (the full meta-package) — it pulls in Swashbuckle's own spec generator which conflicts with `Microsoft.AspNetCore.OpenApi`.

**Version lookup:** Check https://www.nuget.org/packages/Swashbuckle.AspNetCore.SwaggerUi at implementation time. As of April 2025 the latest stable is `7.x`; by May 2026 expect `8.x`. Pick the latest `*.x` that targets `net10.0` or `netstandard2.1`.

### IOpenApiOperationTransformer registration API

In `Microsoft.AspNetCore.OpenApi 10.x`, operation transformers are registered via:
```csharp
options.AddOperationTransformer<TTransformer>();
```
This registers the class via the DI container (scoped). The transformer receives `OpenApiOperationTransformerContext` which exposes `Description.RelativePath` and `Description.ActionDescriptor` for endpoint matching.

If `IOpenApiOperationTransformer` is not directly available as an interface in 10.0.7 (the API surface changed between previews), check for `IOpenApiDocumentTransformer` / `UseTransformer` delegate overloads as fallback. The architecture only requires the output behavior (AC-4 body schema), not a specific class name.

### OpenApiComponents using directive

`Microsoft.OpenApi.Models.OpenApiComponents`, `OpenApiSecurityScheme`, `OpenApiSchema`, etc. are in the `Microsoft.OpenApi` assembly which is a transitive dependency of `Microsoft.AspNetCore.OpenApi`. No new package reference is needed — add a using directive:
```csharp
using Microsoft.OpenApi.Models;
```

If that namespace resolves ambiguously, add:
```csharp
using Microsoft.OpenApi.Any;
```

Check current `Microsoft.OpenApi` version via `dotnet list package --include-transitive` at dev time.

### `CA1515` on new classes in `Common/OpenApi/`

The project suppresses `CA1515` (internal types in libraries should be marked `internal`) for the `Migrations/` folder via `.editorconfig`. The new `DynamicEndpointDocumentTransformer` class is correctly marked `internal sealed` which satisfies CA1515.

### Testing requirements

**No new automated tests** are required for this story. This is infrastructure plumbing; the build-gate and manual verification steps in Task 5 are sufficient. The `FormForge.Api.Tests` project must continue to discover zero tests cleanly.

Testcontainers-backed integration tests for API endpoints land in Story 2.1 (first story with real auth endpoints).

### File structure

**Touch:**
- `Directory.Packages.props` — add `Swashbuckle.AspNetCore.SwaggerUi` version
- `src/FormForge.Api/FormForge.Api.csproj` — add `Swashbuckle.AspNetCore.SwaggerUi` PackageReference
- `src/FormForge.Api/Program.cs` — extend `AddOpenApi()` with options lambda + add `UseSwaggerUI()` call

**New:**
- `src/FormForge.Api/Common/OpenApi/DynamicEndpointDocumentTransformer.cs`

**Do NOT touch:**
- `src/FormForge.AppHost/AppHost.cs`
- `src/FormForge.ServiceDefaults/Extensions.cs`
- `web/` — no frontend changes in this story
- `docker-compose.yml`, `Dockerfile` — not modified
- Existing EF Core files

### Existing open item from deferred-work.md

The deferred-work file notes that `MapDefaultEndpoints()` in ServiceDefaults gates `/health` and `/alive` behind `IsDevelopment()`. This means in the Swagger UI (running in dev), those health endpoints will appear in the spec — which is fine and actually useful. No action needed.

### Previous story intelligence (from Story 1.3, completed 2026-05-22)

Key inherited context:
- **`TreatWarningsAsErrors=true`** repo-wide — watch for `CA1852` (make class sealed) and `CA2007` (ConfigureAwait) on new files. The transformer class must be `sealed` or suppressed.
- **CPM is enforced** — `Directory.Packages.props` must have a `<PackageVersion>` for every new package. No inline `Version=` on `<PackageReference>`.
- **`InvariantGlobalization=true`** — do not use culture-sensitive string operations in transformer code. The `StartsWith` call in the transformer should use `StringComparison.OrdinalIgnoreCase`.
- **`AnalysisMode=AllEnabledByDefault`** — CA1815, CA1819, CA2007 may fire on public API surfaces. The transformer class is `internal sealed` which avoids most of these.
- **`UseStaticFiles()` is already wired** — `UseSwaggerUI()` serves embedded Swagger UI static assets from its own embedded resources; it does not conflict with `UseStaticFiles()`.
- **`ASPNETCORE_ENVIRONMENT=Compose`** causes `IsDevelopment()` = false — Swagger UI and OpenAPI spec are both disabled in Compose mode. This is correct per AC-3.

### Git intelligence

Recent commits (as of story creation):
- `781e11d` — "Apply Story 1.3 code-review fixes and close story" — final fixes for Docker Compose stack
- `ce5a8d2` — "Story 1.3 — Docker Compose local stack: Dockerfile + compose + EF Core scaffold"
- `3c5c833` — "Story 1.2 — Aspire AppHost orchestrates Postgres + MinIO + API + Vite"

After this story lands, commit message: `Story 1.4 — OpenAPI spec and Swagger UI`

### Anti-patterns to avoid

| Don't | Why |
|---|---|
| Add full `Swashbuckle.AspNetCore` meta-package | Conflicts with `Microsoft.AspNetCore.OpenApi` spec generator; use only the `.SwaggerUi` sub-package |
| Call `app.UseSwaggerUI()` outside the `IsDevelopment()` guard | AC-3 requires 404 in production |
| Use `SwaggerEndpoint("/swagger/v1/swagger.json", "...")` | Wrong URL — spec is at `/openapi/v1.json` per AR-19 |
| Hard-fail (throw) inside the document transformer | The transformer must return a completed `Task` or the pipeline fails silently; use `Task.CompletedTask` |
| Add `[Authorize]` security requirement to individual operations here | That's Story 2.1's job — wait for the auth pipeline to exist |
| Add per-Designer OpenAPI specs | Deferred to v2 per architecture (AD-8 deferred decisions) |

### Project Structure Notes

```
tinnitus/
├── Directory.Packages.props          ← ADD Swashbuckle.AspNetCore.SwaggerUi version
├── src/
│   └── FormForge.Api/
│       ├── FormForge.Api.csproj      ← ADD SwaggerUi PackageReference
│       ├── Program.cs                ← EXTEND AddOpenApi() + ADD UseSwaggerUI()
│       └── Common/
│           └── OpenApi/
│               └── DynamicEndpointDocumentTransformer.cs  ← NEW
```

### References

- `_bmad-output/planning-artifacts/epics.md` — § "Story 1.4: Auto-Generated OpenAPI Spec and Swagger UI" (canonical ACs)
- `_bmad-output/planning-artifacts/architecture.md`
  - § "3.7 — OpenAPI for Dynamic Endpoints (resolves AD-8)" — `additionalProperties: true` requirement, designerId pattern, v2 deferred items
  - § "3.2 — API Versioning Strategy" — spec at `/openapi/v1.json`, no URL prefix in v1
  - § "3.5 — Endpoint Organization" — route group structure the transformer will match against
- `_bmad-output/implementation-artifacts/1-3-docker-compose-local-stack.md` — Completion Notes: `TreatWarningsAsErrors`, CPM enforcement, `InvariantGlobalization`, sealed-class CA requirements
- `_bmad-output/implementation-artifacts/deferred-work.md` — health endpoint `IsDevelopment()` gate note
- NuGet: `https://www.nuget.org/packages/Swashbuckle.AspNetCore.SwaggerUi` — pick latest stable at dev time

## Change Log

- Story 1.4 created — OpenAPI spec + Swagger UI wiring (Date: 2026-05-22)
- Story 1.4 implemented — package added, document + operation transformers wired, manual verification passed (Date: 2026-05-22)

## Dev Agent Record

### Agent Model Used

claude-opus-4-7

### Debug Log References

### Completion Notes List

- **Package version:** `Swashbuckle.AspNetCore.SwaggerUI` `10.1.7` selected — latest stable at implementation time, targets `net10.0`. Added to `Directory.Packages.props` and the API csproj via CPM (no inline `Version=`).
- **Microsoft.OpenApi 2.x API surface required deviation from story snippets:** the package version transitively pulled in by `Microsoft.AspNetCore.OpenApi 10.0.7` is `Microsoft.OpenApi 2.0.0`, which flattened the namespace tree. All model types (`OpenApiInfo`, `OpenApiComponents`, `OpenApiSecurityScheme`, `OpenApiSchema`, `OpenApiOperation`, `ParameterLocation`, `SecuritySchemeType`) now live directly under `Microsoft.OpenApi`, NOT `Microsoft.OpenApi.Models`. The story's `using Microsoft.OpenApi.Models;` and qualified `Microsoft.OpenApi.Models.*` references were updated accordingly. This is documented here so Stories 2.1+ and Epic 6 use the correct namespace.
- **Schema mutation pattern changed in 2.x:** `IOpenApiSchema.Pattern` is read-only — the read-only `IOpenApiSchema` interface is exposed on `OpenApiParameter.Schema`, while writable setters live on the concrete `OpenApiSchema`. The transformer now uses pattern matching (`mutable.Schema is OpenApiSchema schema`) before mutating `Pattern`. Same approach for `OpenApiParameter` itself (mutating via pattern match on the concrete type). Epic 6 / Story 2.1 authors should follow this pattern.
- **JsonSchemaType enum replaces string `Type`:** in 2.x `OpenApiSchema.Type` is `JsonSchemaType?` (flags-like enum), not a string. Use `JsonSchemaType.Object` etc. instead of `"object"`.
- **`OpenApiComponents.SecuritySchemes` may be null:** explicitly initialize via `??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal)` before indexer assignment. The dictionary value type is the interface `IOpenApiSecurityScheme`, not the concrete `OpenApiSecurityScheme` (assignment of concrete to interface is implicit).
- **`AddOperationTransformer<T>()` instantiates the type but the analyzer can't see it:** CA1812 ("uninstantiated internal class") fires. Suppressed at the class level with `[SuppressMessage("Performance", "CA1812", ...)]` with a justification referencing the OpenAPI pipeline. No DI registration needed — the framework handles instantiation.
- **OpenAPI version produced:** spec emits `"openapi": "3.1.1"` (not `3.1.0` as the story Task 5 hint suggested). 3.1.1 is the current point release of the 3.1 spec line; AC-1 only requires "OpenAPI 3.1" so this satisfies. Updated Completion Notes to reflect actual output for future reviewers.
- **Manual verification results:**
  - Development (port 5431): `GET /` → 200, `GET /openapi/v1.json` → 200 with `openapi=3.1.1`, `info.title="FormForge API"`, `info.version="v1"`, `info.description` populated, `components.securitySchemes.Bearer` with `type=http`, `scheme=bearer`, `bearerFormat=JWT`, and description present. `GET /swagger/index.html` returns Swagger UI HTML (status 200).
  - Production (port 5000): `GET /swagger` → 404, `GET /swagger/index.html` → 404, `GET /openapi/v1.json` → 404 ✓ AC-3.
  - Compose (`ASPNETCORE_ENVIRONMENT=Compose`, port 5430): `GET /swagger` → 404, `GET /openapi/v1.json` → 404 ✓ AC-3.
- **DynamicEndpointDocumentTransformer is a no-op today:** no `/api/data/*` routes exist yet (Epic 6 adds them). Verified registration does not break the spec emission (the `/` endpoint appears clean). Static `OpenApiSchema` instances are cached as readonly fields to avoid per-request allocation when Epic 6 routes start landing.
- **Build gates:** `dotnet build` (full solution) — 0 warnings, 0 errors. `dotnet format --verify-no-changes` — exit 0. `dotnet test` — exit 0 (no tests discovered, same baseline as Story 1.3). `npm run build` (web) — clean.
- **No frontend or Aspire changes** — Story 1.4 is API-only as designed.

### File List

- Modified: `Directory.Packages.props` — added `Swashbuckle.AspNetCore.SwaggerUI` `10.1.7` PackageVersion
- Modified: `src/FormForge.Api/FormForge.Api.csproj` — added `Swashbuckle.AspNetCore.SwaggerUI` PackageReference
- Modified: `src/FormForge.Api/Program.cs` — extended `AddOpenApi` with document transformer (Info + Bearer security scheme) and registered `DynamicEndpointDocumentTransformer`; added `UseSwaggerUI` middleware inside the `IsDevelopment()` guard
- New: `src/FormForge.Api/Common/OpenApi/DynamicEndpointDocumentTransformer.cs` — internal sealed `IOpenApiOperationTransformer` (no-op until Epic 6 adds `/api/data/{designerId}/*` routes)
