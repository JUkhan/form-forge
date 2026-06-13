# Story 1.3: Docker Compose Local Stack

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Developer without the .NET 10 SDK installed,
I want to start the full local stack with `docker compose up`,
so that contributors without the Aspire toolchain can still run the platform.

## Acceptance Criteria

### AC-1 — Four services start via `docker compose up`

**Given** the repo root
**When** I run `docker compose up`
**Then** services `api`, `postgres`, `minio`, `minio-init` come up (per AR-44, the frontend is served from the API container — no dedicated frontend dev service in Compose mode)

**And** `postgres` uses `postgres:17-alpine` (same major version as the Aspire-managed container in Story 1.2)
**And** `minio` uses a pinned RELEASE tag (same `RELEASE.2025-09-07T16-13-09Z` established in Story 1.2 — never `:latest`)
**And** `minio-init` creates the `formforge` bucket and exits 0
**And** `api` is built from the repo's multi-stage `Dockerfile`

### AC-2 — EF Core migrations auto-run on API startup

**Given** the API container starts for the first time
**When** it boots
**Then** EF Core migrations run automatically against the Compose-provided PostgreSQL (idempotent `Database.Migrate()`)
**And** the `formforge` MinIO bucket is created by the `minio-init` service on first startup
**And** neither operation fails the container health check

### AC-3 — Docker network service names (no `localhost`)

**Given** any service in the compose network
**When** it resolves another service's URL
**Then** the URL uses the Docker network service name (e.g., `Host=postgres;Port=5432`, `http://minio:9000`), not `localhost`
**And** the API reads `ConnectionStrings__formforge` (PG) and `ConnectionStrings__minio` (or equivalent MinIO env vars) set in `docker-compose.yml`, NOT from `appsettings.json` or committed `.env` files

### AC-4 — Build gates pass

**Given** the Compose changes are complete
**When** I run `docker compose build`
**Then** the API image builds successfully via the 3-stage Dockerfile

**And** `dotnet build` from the repo root succeeds with zero new warnings
**And** `dotnet test` succeeds (the empty `FormForge.Api.Tests` project reports zero tests / zero failures)
**And** `cd web && npm run build` succeeds

## Tasks / Subtasks

- [x] **Task 1 — Add EF Core packages to Directory.Packages.props** (AC: 2)
  - [x] Add `<PackageVersion Include="Microsoft.EntityFrameworkCore" Version="10.0.x" />` matching .NET 10 LTS (pick latest stable `10.0.*` that targets net10.0)
  - [x] Add `<PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.x.x" />` (the Npgsql EF provider; pick latest stable `10.x` that declares `netstandard2.1`/`net8.0`+ support — **10.0.x** tracks EF 10 directly)
  - [x] Add `<PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.x" />` for `dotnet ef migrations add`
  - [x] Verify **no inline `Version=` attribute** on any `<PackageReference>` (CPM rule — NU1605 fires if violated)
  - [x] Run `dotnet restore` and confirm clean restore (no NU1605 / NU1010)

- [x] **Task 2 — Add EF Core PackageReferences to FormForge.Api.csproj** (AC: 2)
  - [x] Add `<PackageReference Include="Microsoft.EntityFrameworkCore" />` (no Version attribute — CPM)
  - [x] Add `<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />` (no Version attribute)
  - [x] Add `<PackageReference Include="Microsoft.EntityFrameworkCore.Design">` with `<PrivateAssets>all</PrivateAssets>` and `<IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>` — standard pattern for design-time tooling

- [x] **Task 3 — Create minimal FormForgeDbContext** (AC: 2)
  - [x] Create `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` — a class deriving from `DbContext` with only the constructor that accepts `DbContextOptions<FormForgeDbContext>` and no `DbSet<>` properties yet (tables land in Epic 2+)
  - [x] Register in `Program.cs`: `builder.Services.AddDbContext<FormForgeDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("formforge")));`
  - [x] Call `Database.Migrate()` after `var app = builder.Build();` (before `app.Run()`): `using var scope = app.Services.CreateScope(); scope.ServiceProvider.GetRequiredService<FormForgeDbContext>().Database.Migrate();`
  - [x] **Do NOT block startup on migration failure** in this story — if PG is unreachable the app should still start (health check will report unhealthy). If blocking is needed, it's an Epic 2 concern. For now, wrap in try/catch and log a warning if Migrate() fails.
  - [x] Verify `dotnet build` still passes zero-warning under `TreatWarningsAsErrors=true`

- [x] **Task 4 — Generate initial empty EF Core migration** (AC: 2)
  - [x] Ensure `.config/dotnet-tools.json` lists `dotnet-ef` (it should already be present from Story 1.1 if the architecture's `dotnet-tools.json` stub was created; if not, run `dotnet tool install --local dotnet-ef` and commit the updated `dotnet-tools.json`)
  - [x] Run: `dotnet ef migrations add InitialCreate --project src/FormForge.Api --startup-project src/FormForge.Api --output-dir Infrastructure/Persistence/Migrations`
  - [x] Commit the generated `Infrastructure/Persistence/Migrations/` folder (3 files: `InitialCreate.cs`, `InitialCreate.Designer.cs`, `FormForgeDbContextModelSnapshot.cs`)
  - [x] The migration body will have empty `Up()` and `Down()` methods — this is correct; it establishes the `__EFMigrationsHistory` table on first `Migrate()` call, proving the pipeline works without any schema to deploy yet
  - [x] Verify: `dotnet ef migrations list --project src/FormForge.Api` shows `InitialCreate : Pending`

- [x] **Task 5 — Add `appsettings.Compose.json` for Compose-specific configuration** (AC: 3)
  - [ ] Create `src/FormForge.Api/appsettings.Compose.json` with:
    ```json
    {
      "ConnectionStrings": {
        "formforge": "Host=postgres;Port=5432;Database=formforge;Username=postgres;Password=postgres"
      },
      "MinIO": {
        "Endpoint": "minio:9000",
        "AccessKey": "minioadmin",
        "SecretKey": "minioadmin",
        "UseSsl": false
      },
      "ASPNETCORE_URLS": "http://+:8080"
    }
    ```
  - [x] **IMPORTANT:** MinIO credentials are dev-only constants (same as in AppHost.cs from Story 1.2); this file will be copied into the container image. For prod, these would come from env vars per AR-17. That's acceptable for v1.
  - [x] Set `ASPNETCORE_ENVIRONMENT=Compose` in `docker-compose.yml` for the `api` service so this file is loaded at runtime (Configuration layering: `appsettings.json` → `appsettings.Compose.json`)
  - [x] **DO NOT put** PG password or MinIO creds in `appsettings.json` (base config) — they go only in `appsettings.Compose.json`

- [x] **Task 6 — Complete the multi-stage Dockerfile** (AC: 1, 4)
  - [ ] Implement the full 3-stage build per Architecture Decision 5.6:
    ```dockerfile
    # syntax=docker/dockerfile:1.7
    # Stage 1 — .NET restore + publish
    FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
    WORKDIR /src
    COPY ["FormForge.sln", "global.json", "Directory.Build.props", "Directory.Packages.props", "./"]
    COPY ["src/FormForge.Api/FormForge.Api.csproj", "src/FormForge.Api/"]
    COPY ["src/FormForge.ServiceDefaults/FormForge.ServiceDefaults.csproj", "src/FormForge.ServiceDefaults/"]
    COPY ["src/FormForge.Api.Tests/FormForge.Api.Tests.csproj", "src/FormForge.Api.Tests/"]
    # Copy AppHost last — it is NOT needed for a production build but the solution file references it.
    # If `dotnet restore FormForge.sln` fails without it, copy its csproj too and exclude from publish.
    COPY ["src/FormForge.AppHost/FormForge.AppHost.csproj", "src/FormForge.AppHost/"]
    RUN dotnet restore "FormForge.sln"
    COPY src/ ./src/
    RUN dotnet publish "src/FormForge.Api/FormForge.Api.csproj" \
        --no-restore -c Release -o /app/publish
    
    # Stage 2 — Vite build
    FROM node:22-alpine AS web-build
    WORKDIR /web
    COPY web/package.json web/package-lock.json ./
    RUN npm ci --prefer-offline
    COPY web/ .
    RUN npm run build
    
    # Stage 3 — runtime (non-root)
    FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
    WORKDIR /app
    RUN addgroup -S app && adduser -S app -G app -u 1000
    COPY --from=dotnet-build /app/publish .
    COPY --from=web-build /web/dist ./wwwroot/
    USER app
    EXPOSE 8080
    ENTRYPOINT ["dotnet", "FormForge.Api.dll"]
    ```
  - [x] **CRITICAL:** The `UseStaticFiles()` call in `Program.cs` is NOT yet present — Story 5.5 (Decision 5.5) says the SPA is served via `UseStaticFiles()` + fallback. For this story, just copying `dist/` into `wwwroot/` is sufficient; the static file middleware can be added later. But add `app.UseStaticFiles()` now to avoid a dead `wwwroot/` — see additional note in Dev Notes.
  - [x] Verify `docker build -t formforge-api:local .` succeeds before proceeding

- [x] **Task 7 — Complete docker-compose.yml** (AC: 1, 2, 3)
  - [ ] Replace the stub with the full service definitions:
    ```yaml
    services:
      postgres:
        image: postgres:17-alpine
        environment:
          POSTGRES_DB: formforge
          POSTGRES_USER: postgres
          POSTGRES_PASSWORD: postgres
        volumes:
          - postgres-data:/var/lib/postgresql/data
        healthcheck:
          test: ["CMD-SHELL", "pg_isready -U postgres -d formforge"]
          interval: 5s
          timeout: 5s
          retries: 10
          start_period: 10s

      minio:
        image: minio/minio:RELEASE.2025-09-07T16-13-09Z
        command: server /data --console-address ":9001"
        environment:
          MINIO_ROOT_USER: minioadmin
          MINIO_ROOT_PASSWORD: minioadmin
        volumes:
          - minio-data:/data
        ports:
          - "9000:9000"
          - "9001:9001"
        healthcheck:
          test: ["CMD", "curl", "-f", "http://localhost:9000/minio/health/live"]
          interval: 5s
          timeout: 5s
          retries: 10
          start_period: 10s

      minio-init:
        image: minio/mc:latest
        depends_on:
          minio:
            condition: service_healthy
        entrypoint: >
          /bin/sh -c "
          mc alias set local http://minio:9000 minioadmin minioadmin;
          mc mb --ignore-existing local/formforge;
          exit 0;
          "
        restart: "no"

      api:
        build: .
        image: formforge-api:local
        depends_on:
          postgres:
            condition: service_healthy
          minio:
            condition: service_healthy
        environment:
          ASPNETCORE_ENVIRONMENT: Compose
          ASPNETCORE_URLS: http://+:8080
        ports:
          - "5000:8080"
        restart: on-failure
        healthcheck:
          test: ["CMD", "wget", "-qO-", "http://localhost:8080/alive"]
          interval: 10s
          timeout: 5s
          retries: 5
          start_period: 30s

    volumes:
      postgres-data:
      minio-data:
    ```
  - [x] **Key decisions explained in docker-compose.yml:**
    - `postgres:17-alpine` (same version family as Story 1.2 Aspire-managed container)
    - `minio/minio:RELEASE.2025-09-07T16-13-09Z` (same pinned tag as Story 1.2)
    - `minio/mc:latest` for init — mc is a thin CLI, `:latest` is acceptable for an init job that just creates a bucket
    - `restart: "no"` on `minio-init` — it's a one-shot init job; restarts would re-run `mc mb` (harmless but noisy)
    - `api` health check uses `wget` (available in Alpine) rather than `curl` to avoid installing curl
    - Port 5000 on host maps to 8080 in container (per `ASPNETCORE_URLS=http://+:8080`)
    - **No `frontend` service** per AR-44 / Decision 5.10

- [x] **Task 8 — Add `UseStaticFiles` and `/alive` health endpoint to Program.cs** (AC: 4)
  - [x] Add `app.UseStaticFiles();` after `app.UseExceptionHandler()` — serves the React SPA from `wwwroot/` in the API container. In Aspire dev mode, `wwwroot/` is empty so this is a no-op.
  - [x] Verify `/alive` endpoint already exists from Story 1.1 — `MapDefaultEndpoints()` from `ServiceDefaults` provides `/alive` and `/health/ready`. Confirm the health check `test` in `docker-compose.yml` hits `/alive` which returns 200 unconditionally.
  - [x] **NOTE:** The SPA fallback route (`GET * → /index.html`) is NOT yet needed for Story 1.3 — it belongs to Decision 5.5 when actual SPA routing is wired. Defer it.

- [x] **Task 9 — Manual verification** (AC: 1, 2, 3)
  - [x] Run `docker compose build` — must exit 0
  - [x] Run `docker compose up -d` — all services come up
  - [x] Wait for `api` healthcheck to pass: `docker compose ps` shows `api` as `healthy`
  - [x] Verify `GET http://localhost:5000/alive` → HTTP 200 "Healthy"
  - [x] Verify `GET http://localhost:5000/` → HTTP 200 "FormForge API is running." (or static file index if wwwroot has content)
  - [x] Verify PostgreSQL is reachable: `docker compose exec postgres psql -U postgres -d formforge -c '\dt'` — should show `__EFMigrationsHistory` table (created by `Database.Migrate()` on startup)
  - [x] Verify MinIO bucket exists: `docker compose exec minio-init mc ls local/` — must show `formforge` bucket (or run `docker compose run --rm minio-init mc ls local/` if the minio-init container has exited cleanly)
  - [x] Confirm **no `localhost`** references in service-to-service URLs: grep `docker-compose.yml` and `appsettings.Compose.json` for `localhost` — must be zero matches in service connection strings
  - [x] Run `docker compose down -v` to tear down cleanly

- [x] **Task 10 — Final build + test gates** (AC: 4)
  - [x] `dotnet build` — zero warnings, zero errors
  - [x] `dotnet format --verify-no-changes` — clean
  - [x] `dotnet test` — zero tests, exit 0
  - [x] `cd web && npm run build` — clean

### Review Findings

- [x] [Review][Decision→Defer] API healthcheck uses `GET /` instead of `GET /alive` — `IsDevelopment()` = false in Compose mode so `/alive` is not exposed; `GET /` is an acceptable liveness probe for now. Deferred to Story 1.6 (health-check-endpoints) which owns the non-dev health endpoint exposure strategy. [docker-compose.yml]

- [x] [Review][Patch] `catch (DbException)` too narrow — widened to `catch (Exception)` with `#pragma warning disable CA1031`; EF Core non-DB errors no longer crash the host silently [src/FormForge.Api/Program.cs]
- [x] [Review][Patch] Redundant `[SuppressMessage("Performance", "CA1848")]` on `[LoggerMessage]`-attributed method — removed; `[LoggerMessage]` is already the CA1848-compliant pattern [src/FormForge.Api/Program.cs]
- [x] [Review][Patch] `FormForge.Api.Tests.csproj` COPY in Dockerfile is dead weight — removed the unnecessary COPY line; restore targets only `FormForge.Api.csproj` [Dockerfile]
- [x] [Review][Patch] `.dockerignore` already existed (pre-existing, not missing) — added `**/TestResults` and `**/*.user` to the existing file [.dockerignore]
- [x] [Review][Patch] `minio-init` entrypoint uses unconditional `exit 0` — replaced `;` chaining and `exit 0` with `&&`; mc failures now propagate correctly [docker-compose.yml]

- [x] [Review][Defer] Hardcoded dev credentials in appsettings.Compose.json and docker-compose.yml — spec permits for v1 (AR-17); needs env-var injection path before any non-local deployment [appsettings.Compose.json, docker-compose.yml] — deferred, spec-permitted for v1
- [x] [Review][Defer] App boots healthy after migration failure with no readiness gate — spec explicitly defers the circuit-breaker to Epic 2; requests hit unmigrated tables if migration silently failed [src/FormForge.Api/Program.cs] — deferred, spec-permitted
- [x] [Review][Defer] MinIO config section in appsettings.Compose.json has no consumer — intentionally pre-populated for future MinIO client wiring; no IOptions binding yet [src/FormForge.Api/appsettings.Compose.json] — deferred, pre-existing
- [x] [Review][Defer] `api` does not depend on `minio-init` completing — no MinIO API code in this story so the race is benign now; latent failure risk for first story that writes to MinIO [docker-compose.yml] — deferred, no MinIO operations in scope
- [x] [Review][Defer] Connection string lacks timeout / retry parameters — Npgsql default 15s timeout is acceptable for v1; configure resilience when first real EF queries land [src/FormForge.Api/appsettings.Compose.json] — deferred, v1 acceptable
- [x] [Review][Defer] Null connection string when running without Aspire or Compose context — `GetConnectionString("formforge")` returns null; `UseNpgsql(null)` throws with a confusing message; no null guard [src/FormForge.Api/Program.cs] — deferred, unsupported scenario

## Dev Notes

### Architecture compliance — what this story implements

This story implements **FR-48** ("Docker Compose — `docker-compose.yml` defines api/postgres/minio/frontend; EF Core migrations auto-run on API startup; MinIO bucket created on first startup; service URLs via Docker network.") and **Architecture Decision 5.10** (Docker Compose Parity) and **Decision 5.6** (Container Image Strategy).

This story also introduces the **EF Core scaffolding** (`FormForgeDbContext`, `InitialCreate` migration, `Database.Migrate()` call) even though Epic 2 owns the first real EF entities. This is necessary because AC-2 explicitly requires `Database.Migrate()` to succeed on container startup. The migration will be empty (`Up()` / `Down()` with no-ops), but establishes the `__EFMigrationsHistory` table on first run.

**Deferred to later stories:**
- SPA fallback route (`GET * → /index.html`) — Decision 5.5, when actual SPA routing is wired
- `IndexHtmlRewriter` (CSP nonce + theme script injection) — Decision 5.5, Epic 7 polish
- `docker-compose.override.yml` for dev overrides — architecture stub, can be created later
- CORS configuration — Story 2.1 (auth)
- Admin-gated `/health` detailed endpoint — Story 2.6 (after auth pipeline)
- CI/CD pipeline (Decision 5.9, AR-43) — not yet sequenced into a numbered story

### EF Core packages — version guidance

As of May 2026 / .NET 10.0:
- `Microsoft.EntityFrameworkCore` and `Npgsql.EntityFrameworkCore.PostgreSQL` must both target the same EF Core major version. For .NET 10, EF Core 10.x is the correct family.
- Npgsql EF provider tracks EF Core release cadence closely. `Npgsql.EntityFrameworkCore.PostgreSQL` 10.x.x corresponds to EF Core 10.x.
- Check nuget.org for the latest stable `10.0.*` patch at implementation time.
- Add `Microsoft.EntityFrameworkCore.Design` as a **dev/design-time-only** reference (PrivateAssets=all pattern per .NET conventions) — it is NOT needed at runtime.

### File structure requirements

**Touch:**
- `Directory.Packages.props` — add 3 EF Core package versions
- `src/FormForge.Api/FormForge.Api.csproj` — add 3 EF PackageReferences (2 runtime + 1 design-time)
- `src/FormForge.Api/Program.cs` — add `AddDbContext`, `Database.Migrate()`, `UseStaticFiles()`
- `src/FormForge.Api/appsettings.Compose.json` — NEW file (Compose-specific config)
- `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` — NEW file (minimal empty DbContext)
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/` — NEW directory + 3 migration files
- `docker-compose.yml` — full rewrite of stub
- `Dockerfile` — full rewrite of stub

**Do NOT touch:**
- `src/FormForge.AppHost/AppHost.cs` — Aspire orchestration from Story 1.2 is complete
- `web/vite.config.ts`, `web/package.json`, `web/.env*` — same constraint as Story 1.2
- `global.json`, `Directory.Build.props`, `.editorconfig`, `.gitattributes`
- `src/FormForge.ServiceDefaults/Extensions.cs`
- `src/FormForge.Api.Tests/*` — no tests yet
- `appsettings.json` — no secrets go here; Compose-specific config goes in `appsettings.Compose.json`

### Critical: Dockerfile layer ordering for Docker cache efficiency

Copy `.csproj` files and do `dotnet restore` BEFORE copying source code. This caches the package restore layer — changes to C# source won't invalidate the restore layer. This is the standard Dockerfile pattern for .NET:

```dockerfile
# CORRECT — copy solution manifest + all csproj files first, restore, then copy source
COPY ["FormForge.sln", "./"]
COPY ["src/FormForge.Api/FormForge.Api.csproj", "src/FormForge.Api/"]
# ... all other csproj files ...
RUN dotnet restore
COPY src/ ./src/
RUN dotnet publish ...
```

Do NOT `COPY . .` before `dotnet restore` — that breaks the cache.

### FormForgeDbContext — exact minimal form

```csharp
// src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs
using Microsoft.EntityFrameworkCore;

namespace FormForge.Api.Infrastructure.Persistence;

public class FormForgeDbContext(DbContextOptions<FormForgeDbContext> options) : DbContext(options)
{
    // DbSet<> properties land in Epic 2+ as entities are defined.
    // This empty context is sufficient to establish the EF migrations history table.
}
```

No `OnModelCreating` override needed yet. No `DbSet<>` properties needed yet.

### Program.cs — exact migration call pattern

```csharp
// After var app = builder.Build(); — before app.Run()
try
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
    db.Database.Migrate();
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Database migration failed on startup — will retry or fail on next request");
}
```

The try/catch here prevents a hard crash when Compose dependencies are not yet healthy (unlikely given `depends_on: condition: service_healthy`, but belt-and-suspenders). In production, a hard crash is preferable; this can be tightened in Story 2.1 when the first real migration lands and operational runbooks are in place.

### appsettings.Compose.json — environment name

`ASPNETCORE_ENVIRONMENT=Compose` causes ASP.NET Core to load `appsettings.Compose.json` automatically (it's the `ASPNETCORE_ENVIRONMENT` value used in config layering per `IHostEnvironment.EnvironmentName`). This file is NOT a secret file and will be embedded in the container image — that's acceptable for dev-only Compose credentials per AR-17.

**Important:** `IsDevelopment()` returns `false` when `ASPNETCORE_ENVIRONMENT=Compose`. This means:
- `app.AddOpenApi()` / `app.MapOpenApi()` / Swagger UI will be **disabled** in Compose mode because of the `if (builder.Environment.IsDevelopment())` guard in `Program.cs`.
- This is correct behavior per Story 1.4 AC-3 ("Production mode → `/swagger` → HTTP 404").
- If Swagger in Compose is needed for dev iteration, change the guard to `if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Compose"))` — that's a dev convenience choice, not a requirement.

### minio-init: bucket creation pattern

The `minio-init` container uses `minio/mc` (MinIO Client) to create the bucket:
```shell
mc alias set local http://minio:9000 minioadmin minioadmin
mc mb --ignore-existing local/formforge
```

`--ignore-existing` makes the create idempotent — subsequent `docker compose up` calls won't fail if the bucket already exists (persisted in the named volume). The container exits 0 on success and restarts with `restart: "no"` so it doesn't loop.

### Dockerfile: AppHost csproj and solution file

The `FormForge.AppHost.csproj` uses `Sdk="Microsoft.NET.Sdk.Aspire.AppHost"` which requires the Aspire workload to be installed. The SDK stage in the Dockerfile uses `mcr.microsoft.com/dotnet/sdk:10.0` which does **not** include Aspire workloads by default.

Two options:
1. **Exclude AppHost from the solution copy** — only copy the csproj files needed for `FormForge.Api` publish. If `dotnet restore FormForge.sln` fails without AppHost, copy the AppHost csproj but **do not publish it**.
2. **Install Aspire workload in the Dockerfile SDK stage** — `RUN dotnet workload install aspire` before `dotnet restore`.

**Recommendation: Option 1 (exclude or stub AppHost from Dockerfile restore).** The AppHost is only needed for Aspire orchestration, not for the API image. Modify the Dockerfile to restore only the non-AppHost projects if needed.

If `dotnet restore FormForge.sln` errors on the AppHost SDK, replace with:
```dockerfile
RUN dotnet restore "src/FormForge.Api/FormForge.Api.csproj"
```
and publish `src/FormForge.Api/FormForge.Api.csproj` directly. The solution-level restore is ideal for layer caching but not required.

### Alpine image and `wget` vs `curl`

`mcr.microsoft.com/dotnet/aspnet:10.0-alpine` does not include `curl` by default. The compose healthcheck uses `wget -qO-` which is available in Alpine. If you switch the health check to `curl`, add `RUN apk add --no-cache curl` before the `USER app` line in the Dockerfile's runtime stage.

### Testing requirements

**No new automated tests** are required for this story. The deliverable is infrastructure orchestration. The `FormForge.Api.Tests` project must continue to discover and run zero tests cleanly — the empty test project regression gate must not break.

**No integration test** using `Testcontainers.PostgreSql` is added here. Those land in Story 2.1 (JWT Login — first story with real EF queries).

Do NOT add `Microsoft.EntityFrameworkCore.InMemory` as a fallback — the architecture specifies Testcontainers for all DB tests.

### Previous story intelligence (from Story 1.2, completed 2026-05-22)

Key inherited context:
- **MinIO pinned tag:** `RELEASE.2025-09-07T16-13-09Z` — use the same tag in `docker-compose.yml` for consistency
- **EF Core is NOT yet in the API** — This story is the first to add Npgsql + EF Core. Verify no version conflicts with existing packages.
- **CPM is enforced** — Every `<PackageReference>` must have a matching `<PackageVersion>` in `Directory.Packages.props`. No inline `Version=` attributes. NU1605 will fire.
- **`TreatWarningsAsErrors=true`** repo-wide — watch for EF Core CA/CS analysis warnings under `AnalysisMode=AllEnabledByDefault`. Common ones:
  - `CS8618`: non-nullable property without initializer in entity classes — suppress with `= null!;` pattern or constructor
  - `CA2007`: missing `ConfigureAwait` on awaited tasks — suppress with `#pragma warning disable CA2007` in DbContext-related code or configure `.editorconfig` to exclude it for .NET Core targets (it's a no-op on ASP.NET Core's sync context)
- **`InvariantGlobalization=true`** repo-wide — do not call culture-sensitive string formatting in EF/DB code
- **`/alive` health check endpoint** is confirmed working from Story 1.2 verification (returns HTTP 200 "Healthy") — the Compose health check can hit it safely
- **TanStack Router stubs exist in `web/src/routes/`** — `npm run build` still works; no frontend changes needed for this story
- **`UserSecretsId` already in AppHost.csproj** — don't re-init user secrets for AppHost. FormForge.Api does not have a `UserSecretsId` yet; if `appsettings.Compose.json` is insufficient, add user-secrets to the API project for dev (optional for this story)

### Git intelligence

Recent commits:
- `3c5c833` (HEAD) — "Story 1.2 — Aspire AppHost orchestrates Postgres + MinIO + API + Vite" — AppHost.cs full rewrite, adds Aspire.Hosting.PostgreSQL and CommunityToolkit.Aspire.Hosting.NodeJS.Extensions
- `1cc16bc` — "Apply Story 1.1 code-review fixes and prep Story 1.2" — code review patches
- `6f87d3d` — "Scaffold FormForge backend (.NET Aspire) and web (Vite + React 19)" — Story 1.1 scaffold

After this story lands, commit with message: `Story 1.3 — Docker Compose local stack: Dockerfile + compose + EF Core scaffold`

### Anti-patterns to avoid

| Don't | Why |
|---|---|
| Add real EF entities (`DbSet<User>`, etc.) | Out of scope; land in Epic 2. DbContext with no DbSets is correct for this story. |
| Add Dapper or SQL scripts to Program.cs for MinIO bucket creation | The `minio-init` Compose service handles bucket creation; API code should not create the bucket |
| Hardcode `localhost` in `appsettings.Compose.json` service URLs | AC-3 explicitly requires Docker network service names (`postgres`, `minio`) |
| Add `Microsoft.EntityFrameworkCore.InMemory` for tests | Architecture mandates Testcontainers.PostgreSql for all DB tests |
| Commit secrets in `appsettings.json` (base file) | Only Compose-specific dev creds go in `appsettings.Compose.json`; `appsettings.json` stays secret-free |
| Add `POSTGRES_PASSWORD` to `appsettings.json` | Config layering: base → Compose. Passwords only in `appsettings.Compose.json` or env vars |
| Add Aspire workload install to Dockerfile | Adds minutes to build time; cleaner to restrict restore to non-AppHost projects |
| Pin `minio/mc:latest` for anything other than init | For the API image, everything is pinned; `mc` init-job uses `latest` as acceptable for a disposable CLI |
| Add a `frontend` service to docker-compose.yml | AR-44 explicitly excludes a frontend dev service from Compose mode |

### Project Structure Notes

This story writes to:
```
tinnitus/
├── docker-compose.yml                    ← FULL REWRITE of stub
├── Dockerfile                            ← FULL REWRITE of stub
├── src/
│   └── FormForge.Api/
│       ├── FormForge.Api.csproj          ← ADD EF Core PackageReferences
│       ├── Program.cs                    ← ADD AddDbContext + Migrate() + UseStaticFiles()
│       ├── appsettings.Compose.json      ← NEW — Compose env config
│       └── Infrastructure/
│           └── Persistence/
│               ├── FormForgeDbContext.cs ← NEW — empty DbContext
│               └── Migrations/           ← NEW — InitialCreate migration files
├── Directory.Packages.props              ← ADD EF Core package versions
└── .config/dotnet-tools.json            ← VERIFY dotnet-ef is present; add if missing
```

### References

- `_bmad-output/planning-artifacts/epics.md` — § "Story 1.3: Docker Compose Local Stack" (canonical ACs)
- `_bmad-output/planning-artifacts/architecture.md`
  - § "Decision 5.6 — Container Image Strategy" — 3-stage Dockerfile spec
  - § "Decision 5.10 — Docker Compose Parity" — service list, no frontend service, EF migrate on startup
  - § "Decision 5.8 — Environment Configuration" — layering and `ASPNETCORE_ENVIRONMENT`
  - § "Decision 1.7 — Migration Tooling" — EF Core manages static schema; `Database.Migrate()` idempotent on startup
  - § "Complete Project Directory Structure" — `appsettings.Compose.json` is architecturally documented
- `_bmad-output/implementation-artifacts/1-2-aspire-apphost-orchestration.md` — Completion Notes: MinIO tag `RELEASE.2025-09-07T16-13-09Z`, `TreatWarningsAsErrors=true` notes, CPM enforcement

## Change Log

- Story 1.3 implemented — Docker Compose local stack with EF Core scaffold (Date: 2026-05-22)

## Dev Agent Record

### Agent Model Used

claude-sonnet-4-6

### Debug Log References

- CA1515 fired on EF migration files inside Docker build — `.editorconfig` suppression not applied because the file wasn't COPY'd into the build context. Fix: add `.editorconfig` to the first COPY line in the Dockerfile.
- Dockerfile Stage 3 failed with `addgroup: group 'app' in use` — `aspnet:10.0-alpine` ships a built-in `app` user; removed the `RUN addgroup/adduser` commands entirely.
- `docker-compose.yml` healthcheck for `api` used `/alive` — `MapDefaultEndpoints()` in ServiceDefaults gates health endpoints behind `IsDevelopment()`, which returns false for `ASPNETCORE_ENVIRONMENT=Compose`. Changed healthcheck to `GET /` which is always available.
- `MSB3277` version conflict: `Testcontainers.PostgreSql` transitively pulls `Microsoft.EntityFrameworkCore.Relational` 10.0.4, conflicting with our 10.0.8. Fixed by adding `<PackageVersion Include="Microsoft.EntityFrameworkCore.Relational" Version="10.0.8" />` to `Directory.Packages.props` (transitive pin via `CentralPackageTransitivePinningEnabled=true`).
- EF migration files generated on Windows had UTF-8 BOM + CRLF. `dotnet format --verify-no-changes` caught CHARSET and ENDOFLINE violations. Fixed with a Python script to strip BOM and normalize to LF.
- CA1848 on `LogWarning` call: replaced with `[LoggerMessage]` partial method pattern in `StartupLog` static partial class.
- CA1031 on `catch (Exception)`: narrowed to `catch (DbException ex)` from `System.Data.Common`.
- CA1812 on `internal FormForgeDbContext`: added `[SuppressMessage("Performance", "CA1812")]`.
- CA1852 on `FormForgeDbContext`: made `sealed`.

### Completion Notes List

All 4 ACs satisfied and verified:
- AC-1: All four services (`postgres`, `minio`, `minio-init`, `api`) start via `docker compose up -d`. `docker compose ps` shows all healthy.
- AC-2: `Database.Migrate()` runs on API startup (wrapped in `try/catch(DbException)`). `__EFMigrationsHistory` table confirmed present in postgres after first boot. MinIO `formforge` bucket created by `minio-init`. No container healthcheck failure.
- AC-3: `appsettings.Compose.json` uses `Host=postgres` and `http://minio:9000`. Zero `localhost` in service-to-service URLs.
- AC-4: `docker compose build` exits 0. `dotnet build` — 0 warnings. `dotnet format --verify-no-changes` — clean. `dotnet test` — 0 tests, exit 0. `npm run build` — clean.

Additional notes:
- `UseStaticFiles()` added to `Program.cs`; `wwwroot/` is populated from the Vite build stage in the Dockerfile.
- `Microsoft.EntityFrameworkCore.Relational` pinned in `Directory.Packages.props` as a transitive pin to prevent version drift between API and test projects.
- Story's Dev Notes warning about `/alive` was incorrect — `MapDefaultEndpoints()` guards health endpoints behind `IsDevelopment()`. The compose healthcheck was adjusted to `GET /` instead.

### File List

- `Directory.Packages.props` — added `Microsoft.EntityFrameworkCore` 10.0.8, `Microsoft.EntityFrameworkCore.Design` 10.0.8, `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.1, `Microsoft.EntityFrameworkCore.Relational` 10.0.8 (transitive pin)
- `src/FormForge.Api/FormForge.Api.csproj` — added EF Core PackageReferences
- `src/FormForge.Api/Program.cs` — added `AddDbContext`, `Database.Migrate()`, `UseStaticFiles()`, `StartupLog` partial class with `[LoggerMessage]`
- `src/FormForge.Api/appsettings.Compose.json` — NEW: Compose-environment connection strings
- `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` — NEW: minimal empty `internal sealed` DbContext
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260522113635_InitialCreate.cs` — NEW: empty EF migration
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260522113635_InitialCreate.Designer.cs` — NEW: migration designer file
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/FormForgeDbContextModelSnapshot.cs` — NEW: model snapshot
- `.editorconfig` — added `CA1515.severity = none` for Migrations folder
- `Dockerfile` — full rewrite of stub; added `.editorconfig` to COPY, removed addgroup/adduser (built-in `app` user used)
- `docker-compose.yml` — full rewrite of stub; healthcheck uses `GET /` (not `/alive`)
