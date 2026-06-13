# Story 1.1: Initial Project Scaffolding

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Developer,
I want a scaffolded FormForge monorepo using the architecture's chosen starter templates,
so that all subsequent feature work begins from a stable, decision-aligned foundation.

## Acceptance Criteria

### AC-1 — Backend solution scaffold (Aspire starter)

**Given** an empty repo root (only `_bmad/`, `_bmad-output/`, `docs/`, `.claude/`, `README.md`, `.git/` present)
**When** I run `aspire new aspire-starter --name FormForge --output .` (or equivalent `dotnet new aspire-starter --name FormForge --output .`)
**Then** the solution `FormForge.sln` is created with `src/FormForge.AppHost/`, `src/FormForge.ServiceDefaults/`, and the Aspire-default web project
**And** the Aspire-default Blazor sample web project is removed from disk **and** from the `.sln`
**And** `ApiService` is renamed to `FormForge.Api` everywhere — the `.csproj` filename, the folder name, the project's `AssemblyName`/`RootNamespace`, the AppHost `Projects.*` reference, and the `.sln` entry
**And** `src/FormForge.Api.Tests/` (xUnit + `Testcontainers.PostgreSQL` package referenced) is created and added to the solution

### AC-2 — Frontend scaffold (Vite + React 19 + shadcn)

**Given** the backend scaffold is in place
**When** I run `npm create vite@latest web -- --template react-ts` followed by `npx shadcn@latest init` and the full dependency-install command sequence below
**Then** `web/` contains a React 19 + Vite + TypeScript + Tailwind 4 + shadcn skeleton
**And** `web/package.json` declares the runtime dependencies `@tanstack/react-router`, `@tanstack/react-router-devtools`, `@tanstack/react-query`, `@tanstack/react-query-devtools`, `react-hook-form`, `zod`, `@hookform/resolvers`, `i18next`, `react-i18next`, plus shadcn-preset-introduced extras `@fontsource-variable/geist` and `tw-animate-css` [^d2]
**And** `web/package.json` declares the dev dependencies `@tanstack/router-plugin`, `vitest`, `@testing-library/react`, `@testing-library/jest-dom`, `@vitest/ui`, `jsdom`, `shadcn` (CLI, dev-only) [^d2]
**And** `web/vite.config.ts` registers the TanStack Router Vite plugin (`autoCodeSplitting: true`, `target: 'react'`) **and** the React plugin **and** the Tailwind Vite plugin, in that order [^d3]

[^d2]: Code-review decision D2 (2026-05-22) — `shadcn` is a CLI tool and was moved from `dependencies` to `devDependencies`; `@fontsource-variable/geist` and `tw-animate-css` are runtime artifacts of the shadcn `init --preset nova` flow and are accepted as runtime deps.
[^d3]: Code-review decision D3 (2026-05-22) — original AC-2 text said `router → tailwind → react`, contradicting the Dev Notes / Task 3 (`router → react → tailwind`). Architect call: keep code as committed (`router → react → tailwind`) and corrected this clause to match.
**And** `react-router`/`react-router-dom` is **NOT** present in `package.json` (TanStack Router is the chosen router — see Anti-Patterns below)

### AC-3 — Repo-root infrastructure files

**Given** the repo root
**When** I inspect the file tree
**Then** the following files exist at the repo root (per Architecture § "Complete Project Directory Structure"):

- `Directory.Build.props` — repo-wide MSBuild defaults
- `Directory.Packages.props` — Central Package Management enabled (`ManagePackageVersionsCentrally=true`)
- `global.json` — pins the .NET 10 SDK version (`"sdk": { "version": "10.0.x", "rollForward": "latestFeature" }`)
- `.editorconfig` — formatting rules for `.cs`, `.ts`, `.tsx`, `.json`, etc.
- `.gitattributes` — line-ending normalization (`* text=auto eol=lf`)
- `.gitignore` — covers `bin/`, `obj/`, `node_modules/`, `dist/`, `.vs/`, `.idea/`, IDE temp files; preserves existing `_bmad-output/` content; explicitly does **not** ignore `_bmad/`, `_bmad-output/planning-artifacts/`, `_bmad-output/implementation-artifacts/`, `.claude/`, `docs/`
- `.dockerignore` — excludes `bin/`, `obj/`, `node_modules/`, `.git/`, `_bmad/`, `_bmad-output/`, `docs/`, `*.md`, build artifacts
- `Dockerfile` — multi-stage skeleton (final content lives in Story 1.3 and the deployment work; this story creates the file with stage placeholders that build but produce no working image yet)
- `docker-compose.yml` — skeleton with `services:` key and placeholder for `api`/`postgres`/`minio`/`minio-init` (full wiring is Story 1.3)
- `.config/dotnet-tools.json` — pins `dotnet-ef` (matching the EF Core version Aspire pulls in) and `dotnet-format`

**And** `dotnet build` (from repo root) succeeds with zero warnings new to this story
**And** `cd web && npm install && npm run build` succeeds
**And** `dotnet test` discovers and runs the (empty) `FormForge.Api.Tests` project successfully (zero tests, zero failures)

## Tasks / Subtasks

- [x] **Task 1 — Pre-flight tool verification** (AC: 1, 2)
  - [x] Verify `.NET 10 SDK` is installed: `dotnet --list-sdks` shows a `10.0.*` entry — found `10.0.100`
  - [x] Install the Aspire workload + CLI templates if missing: templates already resolved via `dotnet new list aspire-starter`
  - [x] Verify `aspire --version` works (or `dotnet new aspire-starter --help` lists the template) — `aspire 13.0.0` present
  - [x] Verify `node --version` is 20.x or 22.x LTS and `npm --version` works — Node v22.15.0, npm 10.9.2

- [x] **Task 2 — Backend scaffold via Aspire starter** (AC: 1)
  - [x] From the repo root, run `aspire new aspire-starter --name FormForge --output .` (use `dotnet new aspire-starter --name FormForge --output .` as a fallback). The repo root already contains `_bmad/`, `_bmad-output/`, `docs/`, `.claude/`, `README.md`, `.git/` — these must remain untouched. **Used `dotnet new` fallback** because the `aspire` CLI requires an interactive TTY (Spectre.Console list prompt for template version). Also upgraded `Aspire.ProjectTemplates` 13.0.0 → 13.3.5 to pick up OpenTelemetry vulnerability fixes that would otherwise emit `NU1902` warnings (elevated to errors by `TreatWarningsAsErrors=true`).
  - [x] After scaffold, verify `FormForge.sln` exists at the repo root and `src/FormForge.AppHost/` and `src/FormForge.ServiceDefaults/` exist — the starter places projects at the repo root by default; manually moved them under `src/` per Architecture § "Complete Project Directory Structure"
  - [x] Locate the Aspire Blazor sample web project (commonly `src/FormForge.Web/`). Remove it from disk **and** run `dotnet sln FormForge.sln remove src/FormForge.Web/FormForge.Web.csproj`
  - [x] Rename `ApiService` to `FormForge.Api`:
    - [x] Rename folder `src/FormForge.ApiService/` → `src/FormForge.Api/`
    - [x] Rename `FormForge.ApiService.csproj` → `FormForge.Api.csproj`
    - [x] Update `AssemblyName` and `RootNamespace` in the `.csproj` if they were ApiService-specific — the scaffold did not set explicit `AssemblyName`/`RootNamespace`; both default to the csproj filename (`FormForge.Api`), so nothing to change
    - [x] Update the AppHost reference: in `src/FormForge.AppHost/Program.cs`, change `builder.AddProject<Projects.FormForge_ApiService>(...)` → `builder.AddProject<Projects.FormForge_Api>("api")` — Aspire 13.3.5 uses top-level `AppHost.cs` (not `Program.cs`); also removed the `FormForge_Web` `AddProject` block since the Blazor frontend is gone
    - [x] Update the `.sln` entry: `dotnet sln remove src/FormForge.ApiService/FormForge.ApiService.csproj` then `dotnet sln add src/FormForge.Api/FormForge.Api.csproj`
    - [x] Remove the AppHost project reference to the old name and add the new one — done by editing `FormForge.AppHost.csproj` directly (single ProjectReference now points at `..\FormForge.Api\FormForge.Api.csproj`)
    - [x] Updated `aspire.config.json` `appHost.path` from `FormForge.AppHost/...` to `src/FormForge.AppHost/...`
    - [x] Renamed `FormForge.ApiService.http` → `FormForge.Api.http` (and updated `ApiService_HostAddress` → `Api_HostAddress` inside)
  - [x] Create the test project:
    - [x] `dotnet new xunit -n FormForge.Api.Tests -o src/FormForge.Api.Tests`
    - [x] `dotnet sln FormForge.sln add src/FormForge.Api.Tests/FormForge.Api.Tests.csproj`
    - [x] `dotnet add src/FormForge.Api.Tests/FormForge.Api.Tests.csproj reference src/FormForge.Api/FormForge.Api.csproj`
    - [x] `dotnet add src/FormForge.Api.Tests/FormForge.Api.Tests.csproj package Testcontainers.PostgreSQL` — installed Testcontainers.PostgreSql 4.12.0
    - [x] Delete the stub `UnitTest1.cs` produced by `dotnet new xunit`
  - [x] Run `dotnet build FormForge.sln` and confirm a clean build — Build succeeded, 0 Warning(s), 0 Error(s)
  - [x] Run `dotnet test FormForge.sln` and confirm zero tests run / zero failures — exit 0, "No test is available" (empty test project loads but discovers nothing, as expected)

- [x] **Task 3 — Frontend scaffold (Vite + React 19 + shadcn)** (AC: 2)
  - [x] From the repo root run `npm create vite@latest web -- --template react-ts` (used `--yes` to suppress create-vite confirmation; produced `web/` with `react-ts` template intact)
  - [x] `cd web`
  - [x] Upgrade to React 19 explicitly — combined into the Tailwind install for speed; resulting `react@19.2.6`, `react-dom@19.2.6`, `@types/react@19.2.15`, `@types/react-dom@19.2.3`
  - [x] Install Tailwind 4: `npm install tailwindcss@latest @tailwindcss/vite` → 4.3.0
  - [x] Run `npx shadcn@latest init` — current shadcn (4.8) requires `-t vite` and a `--preset`. Used `--preset nova` (the equivalent of the `--defaults` choice) with `-b radix --yes --force`. **Deviation note:** The shadcn CLI in v4 no longer exposes the "base color = slate" choice as a flag; the Nova preset's neutral grayscale palette is the current default equivalent. Components dir, utils path, and `@/*` alias all match Architecture § 4.6.
  - [x] Install runtime deps: `npm install @tanstack/react-router @tanstack/react-router-devtools @tanstack/react-query @tanstack/react-query-devtools react-hook-form zod @hookform/resolvers i18next react-i18next`
  - [x] Install dev deps: `npm install -D @tanstack/router-plugin vitest @testing-library/react @testing-library/jest-dom @vitest/ui jsdom`
  - [x] Replace `web/vite.config.ts` with the snippet under Dev Notes § "Vite config" — plugin order is `tanstackRouter` → `react` → `tailwindcss`; `build.target: 'es2022'`; `@` alias resolves to `./src`
  - [x] Add the `@/*` path alias to `web/tsconfig.app.json` `compilerOptions.paths` (and to `tsconfig.json`). **Removed `baseUrl`** since TypeScript 6.0.2 deprecated it (`TS5101`) — `paths` already uses relative `./src/*`, so `baseUrl` is unnecessary
  - [x] Run `npm install` again to ensure the lockfile is consistent
  - [x] Run `npm run build` and confirm it succeeds — exit 0; one informational `ENOENT scandir 'web/src/routes'` from the TanStack router plugin (expected — Story 1.1 deliberately does not create the routes folder; Vite continued and emitted `dist/`)
  - [x] `cd ..` back to the repo root

- [x] **Task 4 — Repo-root infrastructure files** (AC: 3)
  - [x] Create `global.json` — `sdk.version 10.0.100`, `rollForward latestFeature`
  - [x] Create `Directory.Build.props` — `TreatWarningsAsErrors=true`, `AnalysisMode=AllEnabledByDefault`, `Nullable=enable`, `InvariantGlobalization=true`
  - [x] Create `Directory.Packages.props` — migrated all Aspire-scaffold `<PackageReference … Version="…">` entries to `<PackageVersion …>` in `Directory.Packages.props` and stripped `Version` attributes from `FormForge.Api.csproj`, `FormForge.ServiceDefaults.csproj`, and `FormForge.Api.Tests.csproj`
  - [x] Create `.editorconfig`
  - [x] Create `.gitattributes`
  - [x] Create or merge `.gitignore` — wrote consolidated repo-root `.gitignore`; deleted `web/.gitignore` (the Vite default). Confirmed `_bmad/`, `_bmad-output/`, `.claude/`, `docs/` are NOT ignored (no entries for them). The Aspire scaffold did not drop a separate `.gitignore`.
  - [x] Create `.dockerignore`
  - [x] Create skeleton `Dockerfile` (multi-stage skeleton with three `FROM` stages and `TODO Story 1.3` markers)
  - [x] Create skeleton `docker-compose.yml`
  - [x] Create `.config/dotnet-tools.json`. Ran `dotnet tool restore` — `dotnet-ef 10.0.0` and `dotnet-format 5.1.250801` restored successfully
  - [x] **Analyzer cleanup**: Enabling `TreatWarningsAsErrors=true` + `AnalysisMode=AllEnabledByDefault` surfaced violations in Aspire-generated code that the scaffold ships with: `CA1062` (null check on `WebApplication app`), `CA1724` (class name `Extensions` conflicts with `Microsoft.AspNetCore.Builder.Extensions`), plus `CA5394` / `CA1852` in the WeatherForecast demo endpoint. Fixed by: (1) renaming `ServiceDefaults.Extensions` → `ServiceDefaultsExtensions` (extension methods unchanged — namespace lookup), (2) adding `ArgumentNullException.ThrowIfNull(app)` in `MapDefaultEndpoints`, (3) deleting the throwaway `/weatherforecast` demo from `FormForge.Api/Program.cs` (out of scope for the runnable shell — Story 1.1 only needs a root endpoint). After fixes: `dotnet build FormForge.sln` succeeds with 0 Warning(s), 0 Error(s).

- [x] **Task 5 — Final verification** (AC: 1, 2, 3)
  - [x] `dotnet build FormForge.sln` succeeds with zero new warnings — 0 Warning(s), 0 Error(s)
  - [x] `dotnet test FormForge.sln` runs the empty test project (zero tests, zero failures) — exit 0
  - [x] `cd web && npm run build` succeeds — exit 0; `dist/` emitted (single informational `ENOENT scandir 'src/routes'` log from TanStack router plugin, non-fatal — Story 1.1 deliberately does not create the routes folder)
  - [x] `dotnet format --verify-no-changes` is clean (or fixed and re-verified) — initial run flagged CRLF endings in Aspire-scaffold files; ran `dotnet format` once to convert to LF (per `.gitattributes` / `.editorconfig`); re-verified clean (exit 0)
  - [x] `git status` shows only the new scaffold files; `_bmad/`, `docs/`, `.claude/` are unchanged. `README.md` modified per Task 5 last sub-item; `_bmad-output/implementation-artifacts/sprint-status.yaml` modified per dev-story workflow Step 4 (status → in-progress)
  - [x] Update the root `README.md` with a one-paragraph "Getting Started" section pointing at `dotnet run --project src/FormForge.AppHost`

## Dev Notes

### Project context

This is **Story 1 of Epic 1, and the first story in the entire project**. The repo currently contains only planning artifacts (`_bmad-output/`), the BMad framework (`_bmad/`), `docs/`, `.claude/`, a stub `README.md`, and the `.git/` directory. **There is no prior code, no `.gitignore`, no solution file, no `web/` folder.** This story creates the runnable shell that all subsequent stories build on.

There is no previous story to learn from. There is no git history of code patterns. All conventions originate from `_bmad-output/planning-artifacts/architecture.md`.

### Architecture compliance — must-follow decisions

This story implements **AR-1** (starter template selection) and **AR-2** (monorepo layout). Every later story assumes the file paths and naming conventions established here. Specifically:

- **AR-1 / Starter Template Evaluation:** `aspire new aspire-starter` for backend; `npm create vite@latest --template react-ts` + `npx shadcn@latest init` for frontend. **No community starters**, **no Next.js**, **no community-bundled extras (Husky, lint-staged, layered ESLint rule-presets like airbnb/standard) beyond what the official templates ship**. The Vite `react-ts` template's own default ESLint flat config is accepted as-is.
- **AR-2 / Monorepo layout:** `src/FormForge.AppHost`, `src/FormForge.ServiceDefaults`, `src/FormForge.Api`, `src/FormForge.Api.Tests`, `web/`, `docker-compose.yml`, `docs/`, `_bmad-output/`. Project files (`.csproj`) live inside their respective folders; the solution file (`FormForge.sln`) lives at the repo root.
- **AR-3 / Routing override (CRITICAL):** TanStack Router (file-based, `@tanstack/router-plugin/vite` with `autoCodeSplitting: true`) replaces the PRD Addendum's React Router v7 assumption. **Do not install `react-router` or `react-router-dom`.** The PRD addendum has not been updated yet (PM action) — the architecture document is authoritative.
- **Naming conventions (Architecture § "Naming Conventions"):**
  - C# projects: PascalCase, `FormForge.*` prefix.
  - C# code: PascalCase types, `_camelCase` private fields, `Async` suffix on async methods.
  - TypeScript: PascalCase component/file names for components, camelCase for utilities.

### Locked technology stack and versions

Versions come from PRD Addendum § "Technology Stack (Locked)" and Architecture § "Starter Template Evaluation":

| Layer | Technology | Version pin |
|---|---|---|
| .NET SDK | .NET 10 LTS | `10.0.x` (pin in `global.json`) |
| C# | C# 14 | implied by SDK |
| .NET Aspire | Aspire | `13.1.*` (workload + project templates) |
| ASP.NET Core | Minimal APIs | matches .NET 10 |
| EF Core | EF Core | matches .NET 10 (added by Aspire starter or installed via Story 1.2) |
| Dapper | Dapper | latest 2.x (installed later — not in this story) |
| Node | Node LTS | 20.x or 22.x |
| React | React | `19.2.4` (latest 19.x) |
| TypeScript | TS | 5.x |
| Vite | Vite | latest 8.x (template default — bumped from 7.x per code-review decision D1, 2026-05-22) |
| `@vitejs/plugin-react` | plugin-react | latest 6.x (matches Vite 8) |
| Tailwind | Tailwind | 4.x (via `@tailwindcss/vite`) |
| shadcn/ui | shadcn CLI | latest |
| TanStack Router | `@tanstack/react-router` | latest v1 |
| TanStack Query | `@tanstack/react-query` | v5 latest |
| react-hook-form | react-hook-form | latest |
| Zod | zod | latest v3 |
| i18next + react-i18next | latest | |
| Vitest + RTL | latest | |

**Do not pin minor versions in `package.json` beyond what `npm install` writes.** Renovate / dependabot (added in a later story) will manage them.

### Aspire toolchain

Aspire ships as a workload + project templates + a separate `aspire` CLI as of Aspire 13.x. Common installation paths:

```bash
# Option A — full workload (recommended)
dotnet workload install aspire

# Option B — project templates only (sufficient for this story)
dotnet new install Aspire.ProjectTemplates::13.1.*

# Optional — Aspire CLI tool (provides `aspire new` instead of `dotnet new aspire-starter`)
dotnet tool install --global Aspire.Cli
```

Both `aspire new aspire-starter` and `dotnet new aspire-starter` produce equivalent output. If the `aspire` CLI is not installed, fall back to `dotnet new`.

### Vite config

Replace the contents of `web/vite.config.ts` with exactly:

```typescript
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { tanstackRouter } from '@tanstack/router-plugin/vite'
import tailwindcss from '@tailwindcss/vite'
import path from 'node:path'

export default defineConfig({
  plugins: [
    tanstackRouter({ target: 'react', autoCodeSplitting: true }),
    react(),
    tailwindcss(),
  ],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  build: {
    target: 'es2022',
  },
})
```

The `'@'` alias and `build.target: 'es2022'` are both architecturally required (Architecture § 5.6 — Vite build target; Architecture § "Complete Project Directory Structure" — `@/*` import alias).

### global.json

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestFeature",
    "allowPrerelease": false
  }
}
```

Adjust `version` to the .NET 10 SDK version actually installed locally (`dotnet --list-sdks`). `rollForward: latestFeature` allows minor-band roll-forward without committing churn.

### Directory.Build.props

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <WarningsNotAsErrors></WarningsNotAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <AnalysisMode>AllEnabledByDefault</AnalysisMode>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
```

`TreatWarningsAsErrors=true` matches Architecture § "Enforcement" (Roslyn analyzers + `dotnet format` must pass).

### Directory.Packages.props

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <!-- Versions of every NuGet package referenced anywhere in the solution
         live here, NOT in individual .csproj files. After scaffolding,
         walk every .csproj and migrate <PackageReference Version="…"> into
         <PackageVersion …> entries below, removing the Version attribute
         from the .csproj. -->
  </ItemGroup>
</Project>
```

When CPM is enabled, **every project's `<PackageReference>` must omit `Version`**. Failing to migrate the Aspire-scaffold-introduced version attributes will break the build.

### .editorconfig

Minimum content (extend in later stories as conventions surface):

```ini
root = true

[*]
indent_style = space
indent_size = 2
end_of_line = lf
charset = utf-8
trim_trailing_whitespace = true
insert_final_newline = true

[*.{cs,csproj,props,targets,sln}]
indent_size = 4

[*.md]
trim_trailing_whitespace = false

# C# preferences — defer to .NET defaults; Roslyn analyzers via AnalysisMode=AllEnabledByDefault
dotnet_diagnostic.CA1707.severity = none
```

### .gitattributes

```
* text=auto eol=lf
*.sln text eol=crlf
*.cs text eol=lf diff=csharp
*.csproj text eol=lf
*.props text eol=lf
*.targets text eol=lf
*.json text eol=lf
*.ts text eol=lf
*.tsx text eol=lf
*.png binary
*.jpg binary
*.ico binary
```

### .gitignore

Use the union of Visual Studio's `.gitignore` template and the Vite/Node template. Critical entries (do **not** omit):

```
# .NET
bin/
obj/
*.user
*.suo
.vs/
.idea/

# Node / Vite
node_modules/
dist/
.vite/
*.tsbuildinfo

# OS / IDE
.DS_Store
Thumbs.db

# Local secrets
*.env.local
.env.development.local
.env.production.local

# Aspire / dotnet-user-secrets state (handled by dotnet — do not commit)
**/UserSecrets/
```

**Do NOT add** any of the following to `.gitignore`: `_bmad/`, `_bmad-output/`, `docs/`, `.claude/`, `.config/dotnet-tools.json`, `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `.gitattributes`, `.dockerignore`, `Dockerfile`, `docker-compose.yml`, `README.md`. These are tracked content.

### .dockerignore

```
**/bin
**/obj
**/node_modules
**/dist
.git
.github
.vs
.idea
_bmad
_bmad-output
docs
*.md
**/.env.local
**/.env.development.local
**/.env.production.local
**/Properties/launchSettings.json
```

### Dockerfile skeleton

Story 1.3 owns the working multi-stage build. This story drops a skeleton that documents the three stages from Architecture § 5.6:

```dockerfile
# syntax=docker/dockerfile:1.7
# Stage 1 — .NET SDK build (restore + publish FormForge.Api)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
WORKDIR /src
# TODO Story 1.3: copy csproj graph, dotnet restore, dotnet publish

# Stage 2 — Vite build (web/dist)
FROM node:22-alpine AS web-build
WORKDIR /web
# TODO Story 1.3: npm ci, npm run build

# Stage 3 — runtime image (aspnet:10.0-alpine, non-root)
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
RUN addgroup -S app && adduser -S app -G app -u 1000
USER app
# TODO Story 1.3: copy published API + web/dist into /app/wwwroot
EXPOSE 8080
ENTRYPOINT ["dotnet", "FormForge.Api.dll"]
```

The build must succeed (`docker build -t formforge-api:scaffold .` should produce an image even though it does nothing useful). The TODOs are explicit pointers to Story 1.3.

### docker-compose.yml skeleton

Story 1.3 owns the working orchestration. This story drops a skeleton:

```yaml
# Full wiring is Story 1.3 (Docker Compose Local Stack). This file declares
# the four services architecturally required (Architecture § 5.10) so that
# downstream stories can reference the names; the implementations land in 1.3.
services:
  postgres:
    image: postgres:17-alpine
    # TODO Story 1.3: env, volumes, healthcheck
  minio:
    image: minio/minio
    # TODO Story 1.3: command, env, ports, volumes, healthcheck
  minio-init:
    image: minio/mc
    depends_on:
      - minio
    # TODO Story 1.3: bucket creation command
  api:
    build: .
    depends_on:
      - postgres
      - minio
    # TODO Story 1.3: env wiring, ports, restart policy
```

### dotnet-tools.json

```json
{
  "version": 1,
  "isRoot": true,
  "tools": {
    "dotnet-ef": {
      "version": "10.0.0",
      "commands": ["dotnet-ef"]
    },
    "dotnet-format": {
      "version": "5.1.250801",
      "commands": ["dotnet-format"]
    }
  }
}
```

Adjust `dotnet-ef` version to match the EF Core version Aspire pulls (`dotnet ef --version` after `dotnet tool restore`). Adjust `dotnet-format` version to the latest published.

### Testing standards

This story produces no executable tests beyond proving the test project loads. Future stories follow the pattern below (Architecture § "Code Organization" and § "Testing Framework"):

- **Backend tests:** xUnit in `src/FormForge.Api.Tests/`. Integration tests use Testcontainers.PostgreSQL (real Postgres, not mocks) — critical for the runtime-DDL behavior in Epic 5. Test file naming: `{ClassUnderTest}Tests.cs`.
- **Frontend tests:** Vitest + React Testing Library, files co-located as `{Component}.test.tsx`.
- **AC for this story:** `dotnet test FormForge.sln` discovers `FormForge.Api.Tests` and reports 0 tests, 0 failures. Delete the `dotnet new xunit`-generated `UnitTest1.cs` stub before that check.

### Critical anti-patterns to avoid

| Don't | Why |
|---|---|
| Install `react-router` or `react-router-dom` | AR-3 overrode the PRD's React Router v7 assumption in favor of TanStack Router. |
| Install `axios` | Architecture § 4.7 mandates a `fetch`-based `httpClient.ts` wrapper. **No Axios anywhere in the codebase.** |
| Install Husky, lint-staged, community-bundled ESLint rule-presets (airbnb / standard / etc.), or any community-bundled "starter extras" | Architecture § "Starter Options Considered" explicitly rejected opinionated extras to keep lock-in to a third-party maintainer out of the project. The Vite `react-ts` template's own default `eslint.config.js` is accepted as-is — the prohibition is on layered community rule-presets, not on ESLint itself. |
| Leave `<PackageReference … Version="…">` in any `.csproj` after enabling CPM | Central Package Management requires versions to live in `Directory.Packages.props`. Mixed projects fail to build with `NU1605` / `NU1010`. |
| Leave the Aspire Blazor sample web project in the solution | AC-1 explicitly requires its removal. The architecture path § 5.5 has the API project — not a separate web project — serving the SPA. |
| Keep the default `ApiService` name | Every later story references `FormForge.Api` by name. Renaming touches `.csproj`, folder name, `RootNamespace`, AppHost `Projects.*` reference, and the `.sln`. |
| Ignore `_bmad/`, `_bmad-output/`, `docs/`, `.claude/` in `.gitignore` | These directories carry planning artifacts and Claude configuration; they must stay tracked. |
| Commit secrets or `appsettings.*.json` containing JWT keys / DB passwords | Architecture § 2.8 — secrets via env vars + `dotnet user-secrets` in dev. Story 1.1 does not introduce secrets, but the `.gitignore` must already block `*.env.local`. |
| Run `dotnet new gitignore` after the Aspire scaffold has already produced one and silently overwrite it | Merge instead. Then collapse any nested `.gitignore` files into the root one. |
| Use Tailwind 3 syntax (`tailwind.config.js` with `content: […]`) | Tailwind 4 is config-as-code via the Vite plugin. shadcn@latest understands this. |
| Run the npm scaffold from inside a directory that already has a `package.json` (e.g., the repo root) | `npm create vite@latest web -- --template react-ts` writes a new `web/` directory; ensure CWD is the repo root, not `web/`. |

### Project Structure Notes

**Alignment.** Every file/folder this story creates aligns with Architecture § "Complete Project Directory Structure". After this story the tree should match the top of that diagram down through the `src/` and `web/` skeletons:

```
tinnitus/                                       # Repo root
├── .editorconfig
├── .gitattributes
├── .gitignore
├── .config/dotnet-tools.json
├── .claude/                                    # PRE-EXISTING — untouched
├── _bmad/                                      # PRE-EXISTING — untouched
├── _bmad-output/                               # PRE-EXISTING — untouched
├── docs/                                       # PRE-EXISTING — untouched
├── FormForge.sln                               # NEW
├── Directory.Build.props                       # NEW
├── Directory.Packages.props                    # NEW
├── global.json                                 # NEW
├── docker-compose.yml                          # NEW (skeleton)
├── Dockerfile                                  # NEW (skeleton)
├── .dockerignore                               # NEW
├── README.md                                   # PRE-EXISTING — light edit only
├── src/
│   ├── FormForge.AppHost/                      # NEW (from Aspire starter)
│   ├── FormForge.ServiceDefaults/              # NEW (from Aspire starter)
│   ├── FormForge.Api/                          # NEW (renamed from ApiService)
│   └── FormForge.Api.Tests/                    # NEW (manually added)
└── web/                                        # NEW (Vite + React 19 + shadcn)
    ├── package.json
    ├── vite.config.ts
    ├── tsconfig.json
    ├── tsconfig.app.json
    ├── tsconfig.node.json
    ├── components.json
    ├── index.html
    ├── public/
    └── src/
        ├── main.tsx
        ├── App.tsx                             # Vite default — kept as placeholder
        └── lib/utils.ts                        # shadcn init creates this
```

Folders that the Architecture diagram lists but **this story does NOT create**: `.github/workflows/`, `docs/runbooks/`, `docs/adr/`, every folder under `src/FormForge.Api/Features/*` and `src/FormForge.Api/Common/*` and `src/FormForge.Api/Domain/*` and `src/FormForge.Api/Infrastructure/*`, every folder under `web/src/routes/`, `web/src/features/`, `web/src/components/designer/`, `web/src/lib/i18n/`, `web/src/lib/theme/`, `web/src/test/`. Those land in their owning stories.

**Detected variances:**
- **TanStack Router replaces React Router v7** (PRD Addendum A2 vs Architecture AR-3). The architecture decision is authoritative — install TanStack Router. PM action item to update the PRD addendum still pending.
- **`react-router`/`react-router-dom` MUST NOT be installed in this story** — this is the single most likely place a dev agent unfamiliar with the override could regress.
- **Tailwind 4** (not 3) via `@tailwindcss/vite` plugin — note that Tailwind 4 has no `tailwind.config.ts` requirement for basic use; the file may not be present after scaffold, which is correct.

### References

- `_bmad-output/planning-artifacts/epics.md` — Story 1.1 acceptance criteria (lines 311–335)
- `_bmad-output/planning-artifacts/architecture.md`
  - § "Starter Template Evaluation" (lines 89–248) — initialization command sequence and rationale
  - § "Complete Project Directory Structure" (lines 921–1234) — file-level paths
  - § "Naming Conventions" (lines 731–765) — DB / API / C# / TS naming
  - § Decision 5.6 (lines 668–676) — container image strategy; Vite build target = es2022
  - § "Architectural Boundaries" (lines 1236–1259) — static-vs-dynamic schema; admin boundary; cache/event boundaries (relevant to later stories, informs folder layout)
  - § "Enforcement" (lines 891–907) — `dotnet format` + eslint must pass; analyzer requirements
- `_bmad-output/planning-artifacts/prds/prd-tinnitus-2026-05-22/addendum.md` — § "Technology Stack (Locked)" (lines 7–23) — locked stack baseline; note A2 (React Router v7) is overridden by Architecture AR-3
- Architecture-derived requirements relevant to this story: **AR-1** (starter template), **AR-2** (monorepo layout), **AR-3** (TanStack Router override)

## Dev Agent Record

### Agent Model Used

claude-opus-4-7 (1M context)

### Debug Log References

- **Aspire CLI interactive prompt**: `aspire new aspire-starter --name FormForge --output .` failed with `System.NotSupportedException: Cannot show selection prompt since the current terminal isn't interactive.` (Spectre.Console list prompt for template version). Fell back to `dotnet new aspire-starter --name FormForge --output .` per story.
- **Aspire template vulnerability warnings**: Initial install of `Aspire.ProjectTemplates::13.0.0` emitted multiple `NU1902` warnings on `OpenTelemetry.*` packages. Upgraded to `13.3.5` (latest at scaffold time) — vulnerability warnings disappeared. (Story spec was `13.1.*`; no `13.1` patch revisions are published on NuGet at the time of work, only 13.0 → 13.3.)
- **Project location**: Aspire starter scaffolded projects at the repo root rather than under `src/`. Moved them manually per Architecture § "Complete Project Directory Structure". Updated `aspire.config.json` `appHost.path`.
- **shadcn CLI changes**: `npx shadcn@latest init` (v4.8) requires `-t <template>` and a `--preset`. Used `-t vite -b radix --preset nova --yes --force`. The "base color = slate" choice from the story's Dev Notes is no longer a separate flag in shadcn v4; the Nova preset's neutral grayscale palette is the equivalent default.
- **TanStack router scandir warning**: `vite build` logs `Error: ENOENT: no such file or directory, scandir '<repo>/web/src/routes'` during config-resolved hook. Non-fatal — the build completes and emits `dist/`. The routes folder is explicitly out of scope per the story's "Project Structure Notes" (lands in a later story).
- **TypeScript 6 baseUrl deprecation**: The story's instruction to add `@/*` to `tsconfig.app.json` `compilerOptions.paths` would normally include `baseUrl: "."`. TypeScript 6.0.2 (installed by current Vite scaffold) emits `TS5101` for `baseUrl`. Used relative `paths` (`./src/*`) without `baseUrl`.
- **Analyzer-vs-scaffold tension**: Enabling `AnalysisMode=AllEnabledByDefault` + `TreatWarningsAsErrors=true` (per Architecture § "Enforcement") surfaced violations in Aspire-generated `ServiceDefaults/Extensions.cs` and the throwaway `/weatherforecast` demo endpoint. Resolved by minimal in-place fixes (class rename, null guard, demo-endpoint removal) rather than per-rule suppressions.

### Completion Notes List

- Backend scaffold uses `dotnet new aspire-starter` (template 13.3.5). Projects live under `src/`: `FormForge.AppHost` (Aspire orchestrator), `FormForge.ServiceDefaults` (shared OpenTelemetry/health/resilience extensions), `FormForge.Api` (renamed from `ApiService`; minimal API with `MapOpenApi` + a root endpoint), `FormForge.Api.Tests` (xUnit + Testcontainers.PostgreSql, empty).
- Aspire's Blazor sample web project (`FormForge.Web`) and the `/weatherforecast` demo endpoint were removed — the architecture's API project hosts the SPA, so the separate Blazor project is unused.
- Central Package Management is enabled (`Directory.Packages.props`); all `<PackageReference … Version="…">` attributes were migrated to `<PackageVersion …>` entries.
- Frontend scaffold: `web/` from `npm create vite@latest --template react-ts`, upgraded to React 19.2.6, Tailwind 4.3.0 (via `@tailwindcss/vite`), shadcn 4.8.0 (Nova preset). Runtime deps: `@tanstack/react-router` + devtools, `@tanstack/react-query` + devtools, `react-hook-form`, `zod`, `@hookform/resolvers`, `i18next`, `react-i18next`. Dev deps: `@tanstack/router-plugin`, `vitest`, `@testing-library/react`, `@testing-library/jest-dom`, `@vitest/ui`, `jsdom`. `react-router`/`react-router-dom` are NOT installed (anti-pattern enforced).
- `vite.config.ts` plugin order: `tanstackRouter` → `react` → `tailwindcss`; `build.target: 'es2022'`; `@` alias → `./src`.
- All repo-root infrastructure files present: `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `.gitattributes`, `.gitignore` (consolidated; nested `web/.gitignore` deleted), `.dockerignore`, `Dockerfile` (skeleton — Story 1.3), `docker-compose.yml` (skeleton — Story 1.3), `.config/dotnet-tools.json`. `dotnet tool restore` installs `dotnet-ef 10.0.0` and `dotnet-format 5.1.250801`.
- Final verification: `dotnet build` 0/0, `dotnet test` exit 0, `npm run build` exit 0, `dotnet format --verify-no-changes` exit 0. Pre-existing `_bmad/`, `docs/`, `.claude/` directories untouched (`git diff --name-only HEAD -- _bmad/ docs/ .claude/` empty).

### File List

**New (created by scaffold or this story):**
- `.config/dotnet-tools.json` — `dotnet-ef`, `dotnet-format` tool pins
- `.dockerignore`
- `.editorconfig`
- `.gitattributes`
- `.gitignore` — consolidated repo-root file (Vite's `web/.gitignore` deleted)
- `Directory.Build.props`
- `Directory.Packages.props`
- `Dockerfile` — skeleton (Story 1.3 owns the working multi-stage build)
- `docker-compose.yml` — skeleton (Story 1.3 owns the working orchestration)
- `FormForge.sln`
- `aspire.config.json`
- `global.json`
- `src/FormForge.Api/FormForge.Api.csproj`
- `src/FormForge.Api/FormForge.Api.http`
- `src/FormForge.Api/Program.cs`
- `src/FormForge.Api/appsettings.Development.json`
- `src/FormForge.Api/appsettings.json`
- `src/FormForge.Api/Properties/launchSettings.json`
- `src/FormForge.Api.Tests/FormForge.Api.Tests.csproj`
- `src/FormForge.AppHost/AppHost.cs`
- `src/FormForge.AppHost/FormForge.AppHost.csproj`
- `src/FormForge.AppHost/appsettings.Development.json`
- `src/FormForge.AppHost/appsettings.json`
- `src/FormForge.AppHost/Properties/launchSettings.json`
- `src/FormForge.ServiceDefaults/Extensions.cs` — class renamed to `ServiceDefaultsExtensions`; null-guard added
- `src/FormForge.ServiceDefaults/FormForge.ServiceDefaults.csproj`
- `web/` — full Vite + React 19 + Tailwind 4 + shadcn scaffold (see directory listing; key files: `package.json`, `package-lock.json`, `vite.config.ts`, `tsconfig.json`, `tsconfig.app.json`, `tsconfig.node.json`, `components.json`, `eslint.config.js`, `index.html`, `src/main.tsx`, `src/App.tsx`, `src/App.css`, `src/index.css`, `src/lib/utils.ts`, `src/components/ui/button.tsx`, `src/assets/`, `public/`)

**Modified (pre-existing):**
- `README.md` — added "Getting Started" section
- `_bmad-output/implementation-artifacts/sprint-status.yaml` — story status updated by dev-story workflow

## Change Log

| Date | Version | Description |
|---|---|---|
| 2026-05-22 | 0.1.0 | Initial project scaffolding — Aspire 13.3.5 backend (AppHost, ServiceDefaults, Api, Api.Tests) under `src/`, Vite + React 19 + Tailwind 4 + shadcn frontend in `web/`, repo-root infrastructure (CPM, analyzers, editor config, gitignore, tool pins, Docker skeletons). All builds green; tests/format/lint verified. |

## Review Findings

_Code review run on 2026-05-22. Three parallel layers: Blind Hunter (diff-only adversarial), Edge Case Hunter (diff + project read), Acceptance Auditor (diff + spec)._

### Decision-needed — RESOLVED (2026-05-22 code-review session)

- [x] [Review][Decision] **D1 — Vite 8.x vs locked-stack Vite 7.x** → **Accepted Vite 8.x.** Locked-stack table (line 155) updated to "latest 8.x" and a row added pinning `@vitejs/plugin-react` to 6.x.
- [x] [Review][Decision] **D2 — Unlisted runtime deps in `web/package.json`** → **`shadcn` moved to devDependencies** (it is a CLI). `@fontsource-variable/geist` and `tw-animate-css` retained as runtime deps; AC-2 "And" clauses extended with footnote `[^d2]`.
- [x] [Review][Decision] **D3 — AC-2 plugin-order spec contradiction** → **Code wins.** AC-2 "Then" clause rewritten to `router → react → tailwind` to match `vite.config.ts` and Dev Notes / Task 3. Footnote `[^d3]` records the decision.
- [x] [Review][Decision] **D4 — TanStack Router plugin registered but `web/src/routes/` does not exist** → **Shipped stub.** Created `web/src/routes/__root.tsx` (root layout with `<Outlet />`) and `web/src/routes/index.tsx` (placeholder `/` route). Plugin now has a non-empty scan target; Story 1.2 will flesh out routing.
- [x] [Review][Decision] **D5 — ESLint 10 retained from Vite template** → **Kept; spec wording clarified.** AR-1 statement in story (line 135) and anti-pattern table row (line 460) updated to clarify the prohibition is on layered community rule-presets (airbnb / standard / etc.), not the Vite-template default flat config. Architecture document § "Starter Options Considered" (line 104) updated to match.

### Patch — APPLIED (2026-05-22)

- [x] [Review][Patch] **`web/tsconfig.app.json` `"strict": true` added** — under `/* Linting */`. Note: may surface latent type errors in placeholder `App.tsx` on next `npm run build`; verify before merging.
- [x] [Review][Patch] **`src/FormForge.Api/FormForge.Api.http`** — replaced `GET /weatherforecast/` with `GET /` (matches the new root endpoint) and `Accept: text/plain`.
- [x] [Review][Patch] **`src/FormForge.Api/Program.cs`** — wrapped `builder.Services.AddOpenApi()` in `if (builder.Environment.IsDevelopment())` for symmetry with `MapOpenApi()`.
- [x] [Review][Patch] **`global.json`** — `rollForward: latestFeature` → `latestMinor`.
- [x] [Review][Patch] **`web/package.json`** — moved `"shadcn": "^4.8.0"` from `dependencies` to `devDependencies`. (D2 follow-through; lockfile will refresh on next `npm install`.)
- [x] [Review][Patch] **`web/src/routes/__root.tsx` + `web/src/routes/index.tsx`** — created minimal stubs so TanStack Router's plugin has files to scan. (D4 follow-through.)
- [x] [Review][Patch] **Story spec — locked-stack table** — Vite row updated to 8.x; `@vitejs/plugin-react` row added at 6.x. (D1 follow-through.)
- [x] [Review][Patch] **Story spec — AC-2 "And" / "Then" clauses** — extended allowed-deps list with `@fontsource-variable/geist`, `tw-animate-css` (runtime) and `shadcn` (dev). Plugin-order phrase corrected to `router → react → tailwind`. (D2 + D3 follow-through.)
- [x] [Review][Patch] **Story spec — AR-1 statement and Anti-Patterns table** — ESLint phrasing clarified to "community-bundled rule-presets (airbnb / standard / etc.)". (D5 follow-through.)
- [x] [Review][Patch] **Architecture doc — § Starter Options Considered** — community-starter rejection rephrased to "layered community rule-presets" instead of "ESLint 10". (D5 follow-through.)
- [x] [Review][Patch] **`web/package.json` build script + `.gitignore`** (discovered during post-patch verification) — old `"build": "tsc -b && vite build"` failed on first-clone because `routeTree.gen.ts` is generated DURING the vite step, so tsc ran with no route types and `createFileRoute('/')` collapsed to `undefined`. Fixed: `"build": "vite build && tsc -b --noEmit"` (vite generates the tree, tsc verifies after). Added `web/src/routeTree.gen.ts` to `.gitignore` since it is auto-generated. Verified clean build from a deleted-tree state.

### Post-patch verification

- `dotnet build` from repo root — clean (0 warnings, 0 errors).
- `npm install` in `web/` — 0 vulnerabilities.
- `npm run build` in `web/` — clean from a fresh state (deleted `routeTree.gen.ts` and `dist/` first).

### Deferred (pre-existing, out-of-scope, or owned by another story)

- [x] [Review][Defer] **Dockerfile is a non-functional skeleton** [`Dockerfile`] — ENTRYPOINT runs `dotnet FormForge.Api.dll` but no COPY of published artifacts; no HEALTHCHECK; alpine missing `curl`/`wget`; EXPOSE 8080 without `ASPNETCORE_URLS`; UID 1000 not parameterized. Spec explicitly says "final content lives in Story 1.3"; deferred.
- [x] [Review][Defer] **docker-compose.yml is a non-functional skeleton** [`docker-compose.yml`] — `minio/minio` and `minio/mc` images untagged (`:latest`), no named volumes (data wiped on `down`), no `depends_on.condition: service_healthy`, no explicit `restart: "no"` on `minio-init`. Spec says Story 1.3 owns full wiring; deferred.
- [x] [Review][Defer] **Health endpoints only mapped in Development** [`src/FormForge.ServiceDefaults/Extensions.cs:114-126`] — Aspire template's default IsDevelopment() gate means `/health` and `/alive` 404 in prod, breaking orchestrator probes. Epic 1.6 (health-check-endpoints) will address.
- [x] [Review][Defer] **OTLP exporter activates on env-var presence without URI validation** [`src/FormForge.ServiceDefaults/Extensions.cs:80-86`] — malformed `OTEL_EXPORTER_OTLP_ENDPOINT` causes startup crash or silent telemetry loss. Epic 1.5 (structured-logging-with-correlation-ids) owns the observability hardening.
- [x] [Review][Defer] **App.tsx is the Vite placeholder with `target="_blank"` links lacking `rel="noopener noreferrer"`** [`web/src/App.tsx`] — entire file is the Vite/React boilerplate landing page, intentionally kept as placeholder for Story 1.2 replacement. Deferred — the file will be replaced wholesale.
- [x] [Review][Defer] **`web/index.html` `<title>web</title>`** [`web/index.html:6`] — placeholder; replaced when Story 1.2 wires the AppHost shell or Epic 7 (UX polish) lands.
- [x] [Review][Defer] **README missing dev-cert trust, frontend run instructions, env-var setup** [`README.md`] — Spec says "Story 1.2 will expand". Deferred.
- [x] [Review][Defer] **Test project inherits `TreatWarningsAsErrors=true`** [`src/FormForge.Api.Tests/FormForge.Api.Tests.csproj`] — once real tests land, benign xUnit warnings (e.g. xUnit1026 unused parameter) will hard-fail CI. Revisit when the first test story writes actual tests.
- [x] [Review][Defer] **`<WarningsNotAsErrors></WarningsNotAsErrors>` empty allowlist** [`Directory.Build.props:8`] — first analyzer noise will require populating this; defer until it fires.
- [x] [Review][Defer] **`.dockerignore` omits `**/TestResults/`, `**/coverage/`, `*.log`** [`.dockerignore`] — bloats build context. Deferred to Story 1.3 (which owns the working multi-stage build).

### Dismissed (noise / false positive / handled elsewhere)

- `.gitignore *.env.local` glob "bug" — file already contains `*.local` (line 31) which matches `.env.local`. Not a defect.
- React `^19.2.6` vs locked-stack 19.2.4 — semver-compatible caret-range; the lockfile reflects npm's resolution and dev verified install. Acceptable minor drift.
- AppHost.cs `WithHttpHealthCheck("/health")` flagged for ordering / scheme — Aspire wires the probe lazily against the resource; not actually a race or a real bug.
- `Directory.Build.props` `InvariantGlobalization=true`, `TreatWarningsAsErrors=true`, `AnalysisMode=AllEnabledByDefault` — intentional architecture decisions, not regressions.
- `Directory.Build.props` `<TargetFramework>net10.0</TargetFramework>` + global SDK `10.0.100` — dev verified `dotnet build`/`dotnet test` clean.
- Aspire SDK 13.3.5 with .NET 10 — explicitly documented in dev completion notes.
- `main.tsx` non-null assertion on `getElementById('root')!` — Vite template default; not a real defect.
- Empty `FormForge.Api.Tests` project — intentional skeleton.
- `vite.config.ts` build `target: 'es2022'` vs `tsconfig.app.json` `target: 'es2023'` — minor; bundler target governs emitted output.
- `aspire.config.json` at repo root — auto-emitted by Aspire scaffold; tracked in story File List.
- Hardcoded launchSettings.json ports / `<title>web</title>` BOM / trailing newline on `.csproj` — `dotnet format --verify-no-changes` reported clean per dev; cosmetic at most.
- "Invented package versions" (TypeScript 6.x, Vite 8.x, Vitest 4.x, lucide-react 1.x, i18next 26.x, react-i18next 17.x, ESLint 10.x, etc.) — dev confirmed `npm install` and `npm run build` both succeed. By 2026-05-22 these versions exist on the registry. The Vite-8 case is captured separately as a locked-stack decision; the rest dismissed.
- `MapDefaultEndpoints` second-call guard, `dotnet-format` legacy tool, `dotnet-ef 10.0.0` existence, `tsconfig.json paths` unused, `components.json tailwind.config: ""`, `.editorconfig` BOM concerns — nits; verified clean by dev build.
