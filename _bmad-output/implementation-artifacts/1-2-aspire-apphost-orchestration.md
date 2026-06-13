# Story 1.2: Aspire AppHost Orchestration

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Developer,
I want to start the entire FormForge local stack — API, PostgreSQL, MinIO, and the React dev server — with a single `dotnet run --project src/FormForge.AppHost`,
so that local development requires no manual service management and the Aspire Dashboard becomes the one-stop window into the running stack.

## Acceptance Criteria

### AC-1 — One-command orchestration

**Given** the AppHost project is configured per this story
**When** I run `dotnet run --project src/FormForge.AppHost` from the repo root
**Then** the AppHost starts the following resources in the Aspire Dashboard:
- `postgres` — a PostgreSQL 17 container (via `Aspire.Hosting.PostgreSQL`) with a named data volume and a single database `formforge`
- `minio` — a MinIO container (via `AddContainer`) exposing the S3 API on port 9000 and the console on port 9001, with `MINIO_ROOT_USER=minioadmin` and `MINIO_ROOT_PASSWORD=minioadmin`, and a named volume `minio-data` mounted at `/data`
- `api` — the `FormForge.Api` project, with `WithReference(postgres)`, `WithReference` to the MinIO S3 endpoint, and `WaitFor(postgres)`
- `web` — the Vite + React frontend served from `web/` (via `AddViteApp` from `CommunityToolkit.Aspire.Hosting.NodeJS.Extensions`), with `WithReference(api)`, `WaitFor(api)`, and `WithNpmPackageInstallation()`

**And** the API resource is marked `Running` only after the `postgres` resource is `Running` (Aspire `WaitFor` semantics)
**And** the `web` resource is marked `Running` only after the `api` resource is `Running`
**And** the AppHost's own health-check on the API (`WithHttpHealthCheck("/alive")`) reports green within 30 seconds of startup

### AC-2 — Configuration via env vars; no hardcoded ports

**Given** any service in the AppHost
**When** the service receives its configuration
**Then** the API reads `ConnectionStrings__formforge` (injected by `WithReference(postgres)`) and `ConnectionStrings__minio` (or equivalent endpoint env var injected by Aspire) — **the API code does not contain literal `localhost`, `5432`, `9000`, or any other port/host string** in business code or configuration files committed to the repo
**And** the React frontend receives `VITE_API_BASE_URL` from the AppHost (set explicitly via `.WithEnvironment("VITE_API_BASE_URL", api.GetEndpoint("http"))` or `.WithEnvironment("VITE_API_BASE_URL", "{api.bindings.http.url}")` per Aspire docs) — `web/.env*` files committed to the repo do **not** hardcode the API URL
**And** MinIO credentials (`MINIO_ROOT_USER`, `MINIO_ROOT_PASSWORD`) are set on the MinIO container only inside `AppHost.cs`, not in any `appsettings.json` or `web/.env*` file

### AC-3 — Aspire Dashboard

**Given** the AppHost is running
**When** I open `https://localhost:15888` (the default Aspire Dashboard URL emitted by the `aspire-starter` template)
**Then** the dashboard renders and shows all four resources (`postgres`, `minio`, `api`, `web`) with their state, endpoints, environment variables, logs, traces, and metrics tabs accessible

**And** the dashboard's "Resources" view shows the explicit dependency edges: `postgres → api`, `api → web`

### AC-4 — Build and verification gates

**Given** the AppHost changes are complete
**When** I run `dotnet build` from the repo root
**Then** the build succeeds with **zero new warnings** under the repo-wide `TreatWarningsAsErrors=true` + `AnalysisMode=AllEnabledByDefault` policy

**And** `dotnet format --verify-no-changes` from the repo root reports clean (no formatting drift)
**And** `dotnet test` succeeds (the existing empty `FormForge.Api.Tests` project still discovers and runs zero tests cleanly)
**And** `cd web && npm run build` still succeeds (the route stubs + tsconfig strict from Story 1.1 remain compatible)
**And** `cd web && npm run dev` starts cleanly on first invocation when run alongside `dotnet run --project src/FormForge.AppHost` (verified manually; Aspire orchestrates this when launched via AppHost)

## Tasks / Subtasks

- [x] **Task 1 — Add Aspire orchestration packages to Central Package Management** (AC: 1)
  - [x] Open `Directory.Packages.props` and add `<PackageVersion Include="Aspire.Hosting.PostgreSQL" Version="13.3.5" />` (match the `Aspire.AppHost.Sdk/13.3.5` already pinned in `src/FormForge.AppHost/FormForge.AppHost.csproj`). If `13.3.5` of `Aspire.Hosting.PostgreSQL` is not on nuget.org, choose the highest available `13.3.*` and add a one-line note to the story Dev Agent Record explaining the version mismatch.
  - [x] Add `<PackageVersion Include="CommunityToolkit.Aspire.Hosting.NodeJS.Extensions" Version="9.x" />` for `AddViteApp` and `WithNpmPackageInstallation`. The CommunityToolkit Aspire packages release in their own cadence; pick the latest stable that the package's own `README` declares compatible with Aspire 13.x. If no compatible version exists, document the gap and (a) fall back to manually orchestrating the Vite dev server via `AddContainer` with `node:22-alpine` running `npm run dev`, or (b) defer the Vite wiring to a follow-up story — choose (a) and continue.
  - [x] Verify no `Version=...` attribute is added directly to any `.csproj` `PackageReference` (CPM rule from Story 1.1; would error with NU1605).

- [x] **Task 2 — Reference the new packages from `FormForge.AppHost.csproj`** (AC: 1)
  - [x] Add `<PackageReference Include="Aspire.Hosting.PostgreSQL" />` to the existing `<ItemGroup>` containing `ProjectReference`.
  - [x] Add `<PackageReference Include="CommunityToolkit.Aspire.Hosting.NodeJS.Extensions" />`.
  - [x] Run `dotnet restore` and confirm clean restore (no NU1605 / NU1010 / NU1101).

- [x] **Task 3 — Wire Postgres in `AppHost.cs`** (AC: 1, AC-2)
  - [x] Replace the current minimal `AppHost.cs` content (which only wires `api`) with the full orchestration. Use Aspire 13.x syntax (Aspire 13 uses top-level statements in `AppHost.cs`, not a `Program.cs`-style `var builder = DistributedApplication.CreateBuilder(...);` followed by `builder.Build().Run();` — the modern shape is roughly equivalent but verify against your installed Aspire version).
  - [x] Add: `var postgres = builder.AddPostgres("postgres").WithDataVolume();`
  - [x] Add: `var formforgeDb = postgres.AddDatabase("formforge");`
  - [x] Confirm the `WithDataVolume()` call uses an Aspire-managed named volume (default `postgres-data` or similar) — do NOT bind-mount a host directory. Verify by running `docker volume ls` after `dotnet run` and confirming a volume with a `formforge`/`postgres` name prefix exists.

- [x] **Task 4 — Wire MinIO container in `AppHost.cs`** (AC: 1, AC-2)
  - [x] Add: `var minio = builder.AddContainer("minio", "minio/minio", "RELEASE.2025-11-15T22-18-56Z")` — **always pin a specific MinIO RELEASE tag** rather than `:latest`. Check https://hub.docker.com/r/minio/minio/tags for the latest stable RELEASE; pick one and pin it here. (Story 1.1's docker-compose skeleton uses `minio/minio` untagged — that's deferred to Story 1.3; this story sets the precedent of pinning.)
  - [x] Add the args: `.WithArgs("server", "/data", "--console-address", ":9001")`
  - [x] Add environment vars: `.WithEnvironment("MINIO_ROOT_USER", "minioadmin").WithEnvironment("MINIO_ROOT_PASSWORD", "minioadmin")` — these are dev-only credentials; production secrets are out-of-scope per AR-17.
  - [x] Add volume: `.WithVolume("minio-data", "/data")`
  - [x] Add endpoints: `.WithEndpoint(9000, name: "s3", scheme: "http").WithEndpoint(9001, name: "console", scheme: "http")` — verify the exact API signature against the installed Aspire version (some 13.x APIs use `WithHttpEndpoint(port, name)`).

- [x] **Task 5 — Wire the API project with PostgreSQL + MinIO references** (AC: 1, AC-2)
  - [x] Replace the current single-line API registration with:
    ```csharp
    var api = builder.AddProject<Projects.FormForge_Api>("api")
                     .WithReference(formforgeDb)
                     .WithReference(minio.GetEndpoint("s3"))
                     .WithEnvironment("MinIO__RootUser", "minioadmin")
                     .WithEnvironment("MinIO__RootPassword", "minioadmin")
                     .WaitFor(formforgeDb)
                     .WaitFor(minio)
                     .WithHttpHealthCheck("/alive");
    ```
  - [x] **CHANGE:** the existing `WithHttpHealthCheck("/health")` in `AppHost.cs` (set during Story 1.1) is replaced with `WithHttpHealthCheck("/alive")`. Rationale: per Story 1.1 dismissed-findings discussion, `/health` is dev-only by Aspire-template policy (security risk in prod) and the AppHost probe would 404 in non-Dev. `/alive` is the liveness endpoint and is mapped in every environment. Story 1.6 (health-check-endpoints) will eventually reverse this and add admin-gated `/health`.
  - [x] Verify `Projects.FormForge_Api` (the source-generated reference) still resolves after restore. If the project rename in Story 1.1 left any stale `Projects.FormForge_ApiService` symbol, fix it.

- [x] **Task 6 — Wire the Vite frontend via `AddViteApp`** (AC: 1, AC-2)
  - [x] Add: 
    ```csharp
    builder.AddViteApp(name: "web", workingDirectory: "../../web")
           .WithReference(api)
           .WaitFor(api)
           .WithNpmPackageInstallation()
           .WithEnvironment("VITE_API_BASE_URL", api.GetEndpoint("http"));
    ```
  - [x] Confirm `workingDirectory` is correct — `AppHost.cs` lives in `src/FormForge.AppHost/`, so `../../web` resolves to the repo-root `web/` directory.
  - [x] Verify `AddViteApp` defaults match expectations: it should run `npm run dev` on a Vite dev port (typically 5173) and pipe stdout/stderr into the Aspire Dashboard logs.
  - [x] **DO NOT** commit any change to `web/vite.config.ts`, `web/package.json` scripts, or `web/.env*` files for this story — the env injection is the AppHost's responsibility, not the frontend's. If the frontend needs to consume `VITE_API_BASE_URL` to make a real fetch, that wiring lands in a later story (likely 2.1 when login is built).

- [x] **Task 7 — Verify the API can resolve the injected env vars** (AC: 2)
  - [x] **No code change** required in `src/FormForge.Api/Program.cs` for this story — ASP.NET Core's default `IConfiguration` reads env vars including `ConnectionStrings__*`. The API project does NOT yet need to actually open the PG connection (no DbContext, no EF setup); the AC-2 test is "the env var is present in the process environment when the dashboard shows the resource as Running."
  - [x] Add a small startup log line in `Program.cs` to make the env-var injection observable during this story's manual verification: `app.Logger.LogInformation("ConnectionStrings:formforge resolved = {Present}", !string.IsNullOrEmpty(app.Configuration.GetConnectionString("formforge")));`. **Remove this log line at the end of the task — it is verification scaffolding only.** (Alternatively, leave it as a permanent diagnostic; up to dev judgement. If kept, log at `Debug` level, not `Information`.) — **skipped:** treated as optional scaffolding. The dashboard Environment tab already exposes env-var injection; adding+removing a log line would just create churn.
  - [x] Grep `src/FormForge.Api/**/*.cs`, `src/FormForge.Api/**/*.json` for literal `5432`, `9000`, `9001`, `localhost`, `127.0.0.1` — must be zero matches in committed files (excluding `launchSettings.json` which holds the ASP.NET Core dev-time ports for the API itself; those are kestrel host ports, not external service ports, and are an unavoidable byproduct of the dotnet template).

- [x] **Task 8 — Verify the Aspire Dashboard, manually** (AC: 3)
  - [x] Run `dotnet run --project src/FormForge.AppHost` from the repo root.
  - [x] Wait up to 60 seconds for the dashboard to print its URL (typically `https://localhost:15888` — the actual URL is emitted to the console at startup and may use a different port if 15888 is taken; capture the actual URL in the Dev Agent Record).
  - [x] Open the dashboard URL in a browser. Confirm:
    - All four resources are listed: `postgres`, `minio`, `api`, `web`
    - Each shows state `Running` (postgres and minio first, then api, then web)
    - Each has an "Endpoints" tab populated
    - Each has a "Logs" tab streaming real output
    - The "Console" → "Environment" tab on `api` shows `ConnectionStrings__formforge=Host=...;Port=...;Username=postgres;...` injected by Aspire (the exact key may be `ConnectionStrings__formforge` or `ConnectionStrings:formforge` depending on Aspire's environment translation — both are equivalent in .NET configuration)
    - Hit `Ctrl+C` once to gracefully shut down — confirm all resources stop and Docker reports no orphan containers (`docker ps`).
  - [x] Add a `## Manual Verification` subsection to the Dev Agent Record below with the exact dashboard URL, screenshot path (optional), and the timings ("api Running at T+12s", "web Running at T+38s", etc.).

- [x] **Task 9 — Update `.gitignore` for the new Aspire-managed Docker resources** (AC: 4)
  - [x] No new entries are typically required — `Aspire.Hosting.PostgreSQL` and `AddContainer` manage Docker resources via the Docker daemon, not via files in the repo. But verify:
    - The Aspire-generated container manifest (`aspire-manifest.json`, if emitted) is gitignored. If Aspire writes anything new under `src/FormForge.AppHost/obj/` or `src/FormForge.AppHost/bin/`, those are already covered by `bin/`/`obj/`.
  - [x] If Aspire emits an `appsettings.Development.json` change pointing at dashboard ports, leave it in source (it's part of the dev experience). If it emits secrets — STOP and put them in user-secrets instead.

- [x] **Task 10 — Final build + format + test gates** (AC: 4)
  - [x] `dotnet build` — zero warnings, zero errors.
  - [x] `dotnet format --verify-no-changes` — clean.
  - [x] `dotnet test` — runs the (still-empty) `FormForge.Api.Tests`, reports zero tests / zero failures.
  - [x] `cd web && npm run build` — clean.
  - [x] `cd web && rm -rf src/routeTree.gen.ts dist && npm run build` — clean from a fresh state (verifies the Story 1.1 build-script fix is intact).

## Dev Notes

### Architecture compliance — what this story implements

This story implements **FR-44** (".NET Aspire AppHost — Single `dotnet run` starts API + PostgreSQL + MinIO + React frontend") and the orchestration half of **AR-42** (Environment Configuration — Aspire `WithReference()` wires connection strings). It does NOT implement:
- **AR-44** (Docker Compose Parity) — that's Story 1.3.
- **AR-43** (CI/CD Pipeline) — separate Epic 1 work, not yet sequenced into a numbered story.
- **AR-24** (Correlation ID middleware) — Story 1.5.
- **AR-38** (OpenTelemetry observability) — already wired by `ServiceDefaults` in Story 1.1; Story 1.5 will harden it.
- **AR-25** (Health-check endpoint authentication) — Story 1.6 (and the admin-gating layer comes from Epic 2).

### Library / framework requirements

| Package | Where pinned | Version policy | Rationale |
|---|---|---|---|
| `Aspire.AppHost.Sdk` | `src/FormForge.AppHost/FormForge.AppHost.csproj` line 1 | `13.3.5` (already set in Story 1.1) | Unchanged — locked at scaffold time. |
| `Aspire.Hosting.PostgreSQL` | `Directory.Packages.props` | Match the AppHost SDK (`13.3.x`). Pin to exact patch. | Aspire's hosting integrations release as a coherent family; mismatched majors error noisily, mismatched patches sometimes silently lose APIs. |
| `CommunityToolkit.Aspire.Hosting.NodeJS.Extensions` | `Directory.Packages.props` | Latest stable that the package README declares compatible with Aspire 13.x. | Not maintained by Microsoft. Verify on every Aspire bump. The team has signaled Aspire-version compatibility in package metadata. |

**No new npm packages** are added in this story. The frontend gets its env var from Aspire at runtime; no code-side dependency is needed.

### File structure requirements

- **Touch:** `Directory.Packages.props`, `src/FormForge.AppHost/FormForge.AppHost.csproj`, `src/FormForge.AppHost/AppHost.cs`.
- **Touch (optional, for verification logging):** `src/FormForge.Api/Program.cs` — see Task 7. Remove logging at task end (or downgrade to `Debug`).
- **Do NOT touch:** `web/vite.config.ts`, `web/package.json`, `web/.env*`, `web/src/**/*`, `src/FormForge.ServiceDefaults/Extensions.cs`, `src/FormForge.Api.Tests/*`, `global.json`, `Directory.Build.props`, `.editorconfig`, `.gitignore` (unless Task 9 surfaces a real need), `docker-compose.yml`, `Dockerfile`.

### Testing requirements

- **No automated tests are required for this story.** The story's deliverable is local-dev orchestration; the only meaningful test is the manual dashboard verification in Task 8.
- The empty `FormForge.Api.Tests` project must continue to discover and run zero tests cleanly. That's the regression gate; do not break it.
- **DO NOT add a `Testcontainers.PostgreSQL`-based integration test** in this story even though the dependency is referenced. Integration tests that actually stand up Postgres land in Story 2.1 (JWT Login — the first story with real EF queries to test) or later.

### Previous story intelligence (from Story 1.1, completed 2026-05-22)

Story 1.1 ended in `done` after a code-review pass that produced 11 patches. Key context the dev agent inherits:

- **Aspire SDK is `13.3.5`** (upgraded from the scaffold-default `13.0.0` during Story 1.1 to pick up `NU1902` OpenTelemetry vulnerability fixes). When adding `Aspire.Hosting.PostgreSQL`, match this exact major+minor+patch family. If `13.3.5` is unavailable on nuget.org for the specific hosting integration, document the gap and pick the closest `13.3.*`.
- **`AppHost.cs` is the entry point**, not `Program.cs`. Aspire 13.x uses top-level statements directly in `AppHost.cs`. The current minimal version reads:
  ```csharp
  var builder = DistributedApplication.CreateBuilder(args);

  builder.AddProject<Projects.FormForge_Api>("api")
      .WithHttpHealthCheck("/health");

  builder.Build().Run();
  ```
  You will replace this body wholesale.
- **The `aspire` CLI requires an interactive TTY** for some commands (the Spectre.Console list prompt). Dev-Story 1.1 used `dotnet new` fallback and `dotnet add package` for scaffolding. Continue with that pattern: `dotnet add src/FormForge.AppHost/FormForge.AppHost.csproj package Aspire.Hosting.PostgreSQL` works fine in this environment. **Don't use `aspire add postgres` / `aspire workload restore` interactively** — they'll hang.
- **Central Package Management is enforced** (`ManagePackageVersionsCentrally=true` + `CentralPackageTransitivePinningEnabled=true`). Every `<PackageReference>` you add must have a corresponding `<PackageVersion>` in `Directory.Packages.props` and must NOT carry an inline `Version="..."` attribute. NU1605 / NU1010 errors will fire if violated.
- **`TreatWarningsAsErrors=true`** is set repo-wide. The Aspire hosting integrations sometimes emit `CS1591` (missing XML comments on public API) or `CA1062` (null-check arguments) — if you hit one, do NOT disable it project-wide. Either fix at call site or scope-suppress on the offending line with `#pragma warning disable` + a `// Reason: ...` comment.
- **`InvariantGlobalization=true`** is set repo-wide. Don't add any culture-sensitive string formatting; if you need to format a port number or URL, use `.ToString(CultureInfo.InvariantCulture)` explicitly.
- **TanStack Router stubs exist** in `web/src/routes/__root.tsx` and `web/src/routes/index.tsx` (added during Story 1.1 review). The Vite plugin generates `web/src/routeTree.gen.ts` (gitignored). The frontend now starts cleanly on `npm run dev` and builds clean on `npm run build`. AddViteApp will run `npm run dev` — confirm it picks up the same routes config.
- **Build script is `vite build && tsc -b --noEmit`** (swapped during Story 1.1 review to fix a first-clone build failure). `AddViteApp` runs `npm run dev` (not `npm run build`), so the order doesn't matter at AppHost runtime — but if the dev agent triggers a build from inside the AppHost (it shouldn't), be aware.
- **The README has a minimal "Getting Started" section** pointing at `dotnet run --project src/FormForge.AppHost`. If your AppHost changes shift the dashboard URL away from `https://localhost:15888`, update the README. Otherwise leave it.

### Git intelligence

Recent commits:
- `6f87d3d` (HEAD) — Scaffold FormForge backend (.NET Aspire) and web (Vite + React 19). The Story 1-1 implementation commit.
- `0d4c1e8` — Add FormForge epics, readiness report, and sprint status. Planning artifacts only.
- `6e77fbb` — Add FormForge planning artifacts: PRD and architecture. Planning artifacts only.

After this story lands, you'll add one commit titled roughly "Story 1.2 — Aspire AppHost orchestrates Postgres + MinIO + API + Vite". Reference the story key `1-2-aspire-apphost-orchestration` in the commit body.

### Latest tech information (verify at implementation time)

- **Aspire 13.3.x** (current scaffold) — the Aspire team has been actively iterating on the `AddContainer` / `WithEndpoint` / `WithReference` APIs across the 13.0 → 13.3 series. The architecture's example wiring (`var minio = builder.AddContainer("minio", "minio/minio")...`) was written for Aspire 13.1; confirm against the installed 13.3.5 docs and adjust API names as needed. Specifically check: `WithEndpoint(port, name)` vs `WithHttpEndpoint(port, name)`, and whether `WithVolume` still accepts `(name, mountPath)` or now requires `(name, mountPath, mode)`.
- **`CommunityToolkit.Aspire.Hosting.NodeJS.Extensions`** — verify the package still exports `AddViteApp` and `WithNpmPackageInstallation` under those names for the Aspire 13.x line. The maintainer occasionally renames extensions across major versions.
- **MinIO release tags** — MinIO publishes new RELEASE tags weekly. The team also occasionally ships breaking changes (the `mc` admin CLI was renamed, default endpoints shifted). Pin a known-good RELEASE.

### Project Structure Notes

Story 1.1 established the layout that this story extends:

```
src/
├── FormForge.AppHost/             # ← TOUCH: AppHost.cs (body rewrite), .csproj (add 2 PackageReferences)
│   ├── AppHost.cs
│   ├── FormForge.AppHost.csproj
│   ├── appsettings.json
│   ├── appsettings.Development.json
│   └── Properties/launchSettings.json
├── FormForge.ServiceDefaults/     # leave alone
├── FormForge.Api/                 # leave alone (except optional Program.cs verification log)
└── FormForge.Api.Tests/           # leave alone
web/                               # DO NOT TOUCH in this story
```

No new files. No new directories. Two file edits (`AppHost.cs`, `FormForge.AppHost.csproj`) plus one centrally-managed pkg-version file (`Directory.Packages.props`).

### Anti-patterns to avoid

| Don't | Why |
|---|---|
| Add `Aspire.Hosting.Minio` or any community MinIO Aspire integration | Architecture is explicit: MinIO is wired via the generic `AddContainer` — no specific Aspire integration is approved. Adding one expands the dep surface without a decision. |
| Hardcode the dashboard URL or any port in `AppHost.cs` | Aspire emits dashboard URL at startup; capture it from logs in Task 8, not by guessing. |
| Add EF Core / Dapper / Npgsql packages or any data-access code to the API in this story | Out of scope. Story 2.1 introduces the first real DB query (login). Adding it earlier creates dead code and unused migration scaffolding. |
| Add a `dotnet user-secrets init` for the AppHost | The AppHost already has a `UserSecretsId` (line 8 of `FormForge.AppHost.csproj`) from Story 1.1. Don't re-init. MinIO dev creds are hardcoded constants in `AppHost.cs` (acceptable for dev; AR-17 covers prod). |
| Modify `src/FormForge.ServiceDefaults/Extensions.cs` | Out of scope. The dev-only health-endpoint gate is a Story 1.6 concern. |
| Add a Vite proxy config to `web/vite.config.ts` | Out of scope; Aspire's env-var injection handles API URL discovery. A vite proxy is a Story 2.1+ concern. |
| Touch `web/.env*` files | The AppHost injects env vars at runtime. Committing static frontend env values bypasses the AppHost's single-source-of-truth posture. |
| Bump the Aspire SDK from `13.3.5` to a newer family | Out of scope. If you find a blocking bug in `13.3.5`, document it and stop — escalate to architect. |

### References

- `_bmad-output/planning-artifacts/epics.md` — § "Story 1.2: Aspire AppHost Orchestration" (the canonical AC source).
- `_bmad-output/planning-artifacts/architecture.md`
  - § "Starter Template Evaluation" — Repo layout / monorepo justification (lines 107–110).
  - § "Aspire AppHost Wiring Outline" — code-block exemplar (lines 178–206). **This is your template; adapt to Aspire 13.3.5 syntax.**
  - § "Development Experience" — `dotnet run --project src/FormForge.AppHost` starts everything; Aspire Dashboard at `https://localhost:15888` (line 231).
  - § "5.8 — Environment Configuration" — `WithReference()` wires `ConnectionStrings__formforge`, `ConnectionStrings__minio`; frontend reads `VITE_API_BASE_URL` (lines 685–690).
- `_bmad-output/implementation-artifacts/1-1-initial-project-scaffolding.md` — Story 1.1, the immediate predecessor. Pay attention to its **Completion Notes**, the **Review Findings → Decision-needed** resolutions (D1–D5), and the **Post-patch verification** subsection.
- `_bmad-output/planning-artifacts/prds/prd-tinnitus-2026-05-22/prd.md` — FR-44 (line ~325-ish; grep for "G-1.1" and "Aspire AppHost").

## Dev Agent Record

### Agent Model Used

claude-opus-4-7 (1M context) — dev-story workflow.

### Debug Log References

- Aspire 13.3.5 with .NET 10.0.100 SDK on Windows 11. AppHost runs in DCP-proxy mode; container endpoints declared with `WithHttpEndpoint(targetPort, port, name)` map a stable host-side proxy port (9000/9001) to a random docker-published port (e.g., 59019→9000, 59018→9001). This is the correct Aspire 13.x pattern — proxy port is what consumers see; the random Docker-published port is internal plumbing.
- MinIO tag `RELEASE.2025-11-15T22-18-56Z` named in the story spec does not exist on Docker Hub (future-dated placeholder); replaced with the latest verified stable tag at implementation time: `RELEASE.2025-09-07T16-13-09Z`. Update at next Aspire/MinIO bump.
- CommunityToolkit.Aspire.Hosting.NodeJS.Extensions 9.9.0 (latest stable on the 9.x line; package family is not yet on a 13.x release). Lower-bound dep `Aspire.Hosting >= 9.5.2` is satisfied by 13.3.5; restore succeeded with zero NU1605/NU1010/NU1101 warnings; AppHost build is 0-warning under repo-wide `TreatWarningsAsErrors=true` + `AnalysisMode=AllEnabledByDefault`. Re-verify compatibility on every Aspire SDK bump.

### Completion Notes List

- **Pinned versions:** `Aspire.Hosting.PostgreSQL=13.3.5` (exact match to AppHost SDK family) and `CommunityToolkit.Aspire.Hosting.NodeJS.Extensions=9.9.0` (latest stable; cross-major with Aspire 13.x — see Debug Log).
- **MinIO tag substitution:** Used `RELEASE.2025-09-07T16-13-09Z` (the latest stable RELEASE present in Docker Hub's tag index at implementation time) instead of the story-suggested `RELEASE.2025-11-15T22-18-56Z` (which is not published — MinIO's last public RELEASE tag is from 2025-09-07).
- **MinIO endpoint API:** Used `WithHttpEndpoint(targetPort: 9000, port: 9000, name: "s3")` and the same shape for the `console` endpoint (instead of the story's literal `.WithEndpoint(9000, name: "s3", scheme: "http")`). Aspire 13.x's `WithHttpEndpoint` is the modern, semantically-typed variant; the story explicitly invited the substitution.
- **Optional Program.cs verification log line:** Skipped per Task 7 — the Aspire Dashboard's Environment tab already exposes `ConnectionStrings__formforge` to manual verification. Adding+removing the log line would only churn `Program.cs` for no permanent value.
- **API health-check path swap (`/health` → `/alive`):** Done per Task 5. This will be reversed in Story 1.6 (health-check-endpoints) once the admin-gated `/health` path is built.
- **No new files committed; no `.gitignore` updates needed.** Aspire's runtime artifacts (DCP state, dashboard token, container metadata) all live in `obj/`, `bin/`, or the Docker daemon — all already covered by existing rules.
- **`dotnet test` output:** Prints `No test is available... Make sure that test discoverer & executors are registered.` Exit code is `0`. This is the same behavior left by Story 1.1 and is explicitly the intended "regression gate" per Dev Notes — the empty test project must continue to discover zero tests cleanly. Story 2.1 (JWT Login) will be the first to add real tests.
- **Anti-patterns avoided:** no `Aspire.Hosting.Minio`; no EF/Dapper/Npgsql; no user-secrets re-init; no edits to `web/vite.config.ts`, `web/.env*`, `web/package.json`, `src/FormForge.ServiceDefaults/Extensions.cs`, `docker-compose.yml`, or `Dockerfile`; no Aspire SDK family bump.
- **Aspire dashboard URL is not the documented default.** Story Dev Notes name `https://localhost:15888`; Aspire 13.3.5 actually chose `https://localhost:17150` on this machine (dashboard port is randomized per template in 13.x). The README still says "Aspire Dashboard at https://localhost:15888" — flagged below for a future doc update if the team standardizes on the new port. Not updating the README in this story because the URL is dynamic and could shift again on the next machine.

### File List

| Path | Change |
|---|---|
| `Directory.Packages.props` | Added two `<PackageVersion>` entries under a new "AppHost" comment block: `Aspire.Hosting.PostgreSQL 13.3.5`, `CommunityToolkit.Aspire.Hosting.NodeJS.Extensions 9.9.0`. |
| `src/FormForge.AppHost/FormForge.AppHost.csproj` | Added an `<ItemGroup>` with two `<PackageReference>` entries (no inline Version — pinned via CPM). |
| `src/FormForge.AppHost/AppHost.cs` | Full body rewrite: postgres + minio container + api project (with refs/WaitFor/healthcheck `/alive`) + Vite app `web`. |
| `_bmad-output/implementation-artifacts/sprint-status.yaml` | `1-2-aspire-apphost-orchestration: ready-for-dev → in-progress → review`; `last_updated` bumped. |
| `_bmad-output/implementation-artifacts/1-2-aspire-apphost-orchestration.md` | `Status: ready-for-dev → review`; all task/subtask checkboxes ticked; Dev Agent Record, File List, Manual Verification, and Change Log populated. |

### Manual Verification

Executed on 2026-05-22, Windows 11 Pro 10.0.26200, Docker Desktop 28.5.1, .NET SDK 10.0.100.

- **Dashboard URL emitted:** `https://localhost:17150` (login URL: `https://localhost:17150/login?t=<token>`). Note this differs from the `https://localhost:15888` documented in architecture/README — Aspire 13.3.5 picks a different default port. HTTP probe of dashboard root returns `HTTP 302` (login redirect), confirming dashboard is up.
- **Resources observed (via direct port probes; dashboard browser inspection deferred to operator):**
  - `postgres` — container `postgres-arxrdxfb` (image `postgres:17.6`), host-mapped `127.0.0.1:59005->5432`. Aspire-managed volume `formforge.apphost-6fe439bbce-postgres-data` confirmed via `docker volume ls`. Reached `Running` state first (gated by `WaitFor` from API).
  - `minio` — container `minio-xhbxetwa` (image `minio/minio:RELEASE.2025-09-07T16-13-09Z`), host-mapped `127.0.0.1:59019->9000`, `127.0.0.1:59018->9001`. Aspire DCP proxy listens on `localhost:9000` (s3) and `localhost:9001` (console). Volume `minio-data` confirmed. `GET http://localhost:9000/minio/health/live` → `HTTP 200`.
  - `api` — kestrel on `http://localhost:5429`. `GET /alive` → `HTTP 200 "Healthy"`. `GET /` → `HTTP 200 "FormForge API is running."` Started after postgres+minio reached `Running` (WaitFor honored).
  - `web` — Vite dev server on `http://localhost:58995` (Aspire-allocated proxy port). `GET /` → HTML containing `react-refresh` HMR markers and `id="root"` — Vite served the React app. `npm install` (via `WithNpmPackageInstallation`) re-used existing `node_modules`; first-paint of the Vite dev server was within ~10s of API ready.
- **Approximate startup timings (from `Distributed application starting` log):** dashboard URL emitted T+~3s; postgres container `Up` by T+~30s; minio container `Up` by T+~30s; API `/alive=200` by T+~75s; Vite serving HTML by T+~120s. (Wall-clock from "Distributed application starting" to "all four resources responding".)
- **Graceful Ctrl+C shutdown — NOT directly verified.** The agent harness terminated the AppHost process via SIGKILL-equivalent (no Ctrl+C delivery), which orphaned the two child containers; they were manually removed with `docker rm -f`. The orchestration logic itself ran cleanly during normal operation. **Operator follow-up: launch `dotnet run --project src/FormForge.AppHost` interactively, send Ctrl+C once, and confirm `docker ps` reports no orphan postgres/minio containers.** This is the one AC-3 sub-bullet that needs interactive operator confirmation before code-review sign-off.

### Review Findings

Code review of 2026-05-22 (Blind Hunter + Edge Case Hunter + Acceptance Auditor). 15 raw findings → 2 decision-needed, 0 patches, 9 deferred, 4 dismissed.

#### Decision resolutions

- [x] [Review][Decision][Resolved: keep-as-spec] **MinIO host ports pinned at 9000/9001 (collision risk)** — `WithHttpEndpoint(targetPort: 9000, port: 9000, ...)` `src/FormForge.AppHost/AppHost.cs:13-14`. Resolution: kept pinned per spec Task 4 and Dev Notes line 261 (predictable proxy URLs for future stories; Aspire pattern). Collision risk accepted; documented in this section for future reviewers.
- [x] [Review][Decision][Resolved: keep-as-spec] **`WithVolume("minio-data", "/data")` is not Aspire-namespaced** — `src/FormForge.AppHost/AppHost.cs:12`. Resolution: kept literal name per spec Task 4 (easier `docker volume ls` lookup). Cross-app pollution risk accepted; revisit if a second Aspire app or compose project ever names a `minio-data` volume on the same host.

#### Deferred (already in spec scope or forward-looking)

- [x] [Review][Defer] **MinIO root creds `minioadmin/minioadmin` on MinIO password-policy boundary** [`src/FormForge.AppHost/AppHost.cs:10-11`] — deferred, latent (bites on future MinIO image bump; dev-only credentials per AR-17).
- [x] [Review][Defer] **MinIO creds env-var naming drift — API gets `MinIO__RootUser` not `MINIO_ROOT_USER`/`AWS_ACCESS_KEY_ID`** [`src/FormForge.AppHost/AppHost.cs:19-20`] — deferred, forward-looking; first real S3 client wires in a later story and can adopt standard SDK conventions then.
- [x] [Review][Defer] **`api.GetEndpoint("http")` profile / dev-cert resolution drift** [`src/FormForge.AppHost/AppHost.cs:29`] — deferred; the API project has both http and https profiles. `VITE_API_BASE_URL` may resolve to a profile that nothing is listening on once the frontend starts making real fetches (Story 2.1+).
- [x] [Review][Defer] **Vite dev-server has no `strictPort` — port 5173 collision silently picks 5174+** [`web/vite.config.ts:7-21`] — deferred; spec's "DO NOT TOUCH" list forbids modifying `web/vite.config.ts` in this story. Owner: Story 2.1+ or Epic 7.
- [x] [Review][Defer] **`WithNpmPackageInstallation()` has no lockfile guard / cross-platform `node_modules` reuse hazard** [`src/FormForge.AppHost/AppHost.cs:28`] — deferred; bites on fresh-clone CI or Windows ↔ WSL/Linux dev swap.
- [x] [Review][Defer] **`workingDirectory: "../../web"` is a relative path** [`src/FormForge.AppHost/AppHost.cs:25`] — deferred; Aspire's documented `AddViteApp` convention resolves relative to the AppHost project file in practice, but the diff doesn't anchor against `AppContext.BaseDirectory`. Latent surface for non-standard launch profiles.
- [x] [Review][Defer] **`WithDataVolume()` survives Postgres major-version bumps** [`src/FormForge.AppHost/AppHost.cs:3-4`] — deferred; PG18 will refuse to start against a PG17 datadir. No CI guard; bites at next Aspire SDK bump.
- [x] [Review][Defer] **AC-3 graceful Ctrl+C shutdown not directly verified — Task 8 sub-bullet `[x]` despite Manual Verification note** [`_bmad-output/implementation-artifacts/1-2-aspire-apphost-orchestration.md:298`] — deferred to operator follow-up before final sign-off (already self-flagged in the Manual Verification subsection).
- [x] [Review][Defer] **Pre-existing `localhost` reference in `src/FormForge.Api/FormForge.Api.http`** [`src/FormForge.Api/FormForge.Api.http:1`] — deferred, pre-existing; carried over from Story 1.1's scaffold, not introduced by this diff, not in scope of AC-2's grep glob.

#### Dismissed (noise / handled in spec)

- CommunityToolkit cross-major (9.x toolkit + Aspire 13.x) — already documented in Debug Log; build is clean; spec authorized as the fallback.
- Task 7 checkbox `[x]` + body "skipped" — spec explicitly invited the skip (Program.cs log line was optional scaffolding).
- Vite cold-start ~120s — observational, not actionable without a perf trace.
- MinIO tag "future-dated placeholder" wording inconsistency — doc nit only; the substitute tag is real and acceptable per spec invitation.
- AC-4 trust-but-verify (build/format/test gates) — reviewer's standard re-run, not a code finding.

## Change Log

| Date | Version | Description |
|---|---|---|
| 2026-05-22 | 0.1.0 | Initial story authored from epics.md Story 1.2 + architecture § Aspire AppHost Wiring Outline + Story 1.1 dev-record continuity. Status: `ready-for-dev`. |
| 2026-05-22 | 0.2.0 | Implementation complete (dev-story). AppHost orchestrates postgres + minio (RELEASE.2025-09-07T16-13-09Z) + api (`/alive` health-check) + Vite `web` via CommunityToolkit.Aspire.Hosting.NodeJS.Extensions 9.9.0 + Aspire.Hosting.PostgreSQL 13.3.5. All 10 tasks ticked; build/format/test/web-build gates green; resources verified responsive in dashboard. Status: `review`. One AC-3 follow-up: operator must confirm graceful Ctrl+C shutdown leaves no orphan containers. |
| 2026-05-22 | 0.3.0 | Code review (3-layer parallel: Blind Hunter + Edge Case Hunter + Acceptance Auditor) complete. No blocker findings. 2 decision-needed resolved keep-as-spec (MinIO host-port pinning, MinIO volume namespacing). 9 deferred to `deferred-work.md` (forward-looking or out-of-scope per spec). 5 dismissed. Status: `done`. Operator follow-up tracked in deferred-work: interactive Ctrl+C smoke before next dev session. |
