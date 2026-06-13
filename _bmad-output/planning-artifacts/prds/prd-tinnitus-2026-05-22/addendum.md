# Addendum — FormForge

*Technical depth, existing asset details, and options-considered content that belongs in downstream documents (architecture, solution design) rather than the PRD itself.*

---

## Technology Stack (Locked)

### Backend
- **Orchestration:** .NET Aspire
- **Object / File Storage:** MinIO (S3-compatible) — icons, image uploads, file fields
- **API:** ASP.NET Core Minimal APIs — latest stable .NET (assumed .NET 10 LTS)
- **Primary DB:** PostgreSQL
- **ORM (static schemas):** Entity Framework Core — `users`, `roles`, `menus`, `component_schemas`, audit tables
- **Dynamic Data Access:** Dapper — runtime DDL (`CREATE TABLE` / `ALTER TABLE`) and CRUD against generated tables
- **Email Transport:** MailKit (recommended) or FluentEmail wrapping MailKit — SMTP with STARTTLS/SMTPS; Mailpit as local-dev SMTP catcher
- **TOTP / MFA:** Otp.NET (NuGet) — RFC 6238-compliant TOTP; AES-256 / `IDataProtector` for secret encryption at rest

### Frontend
- **Framework:** React 19.2.4
- **Components:** shadcn/ui (Tailwind-based)
- **Server State:** @tanstack/react-query v5
- **Forms:** react-hook-form + @hookform/resolvers (Zod resolver)
- **Routing:** React Router v7 [ASSUMPTION]

---

## Existing Asset Audit: ESG Platform Designer

**Reference project:** `C:\Users\MY\Downloads\esg_platform\BkmeaEsgPlatform`

### Key files to port
| File | Purpose |
|------|---------|
| `client\src\pages\ComponentDesignerPage.tsx` | Designer canvas page |
| `client\src\pages\ComponentLibraryPage.tsx` | Library/versioning page |
| `client\src\components\designer\DynamicComponent.tsx` | Renderer |
| `client\src\components\designer\DesignerCanvas.tsx` | DnD canvas |
| `client\src\components\designer\ElementRenderer.tsx` | Per-element render logic |
| `client\src\components\designer\DesignerToolbar.tsx` | Palette + toolbar |

### Audit findings
- **DnD:** Native HTML5 DnD (no third-party lib) — compatible with target stack as-is.
- **14 component types:** Stack, Row, Tabs (structural); Label, Button, TextInput, TextArea, NumberInput, Checkbox, Dropdown, DateTimePicker, ColorPicker, Repeater, RepeaterField, Image (leaves).
- **RootElement shape:** `DesignerElement { id: string, type: string, children?: DesignerElement[], properties: Record<string, unknown> }`. Stored as JSON string in `component_schemas.rootElement`.
- **Version management:** Already implemented — Draft/Published/Archived statuses, per-version preview modal, "New Version" spawns next integer version with a modal. Maps cleanly to the PRD versioning model.
- **DynamicComponent behaviors to preserve:** conditional visibility via `computeVisibility`; Repeater row scope (child fields don't pollute parent form keys); external `submitRef` for Save buttons outside the form; `onValidityChange` and `onReadyChange` callbacks; shallow-equal `initialData` comparison to prevent reset on parent re-renders.
- **Tech already compatible:** Lucide React ✓, Tailwind CSS ✓, TanStack React Query ✓. Minimal replacement needed.
- **Tech to replace:** Any ESG-platform-specific API paths, data models, or business logic. Any non-shadcn component library imports.

---

## Options Considered: Pagination Strategy

**Offset pagination (chosen for v1):**
- Simple to implement with Dapper; total count query is straightforward.
- Degrades at high offsets (full table scan to skip rows).
- Sufficient for tables up to ~100k rows with proper indexes.

**Keyset / cursor pagination (deferred to v2):**
- Constant-time at any offset; no degradation on large tables.
- Requires a stable sort key (e.g., `id` or `created_at`).
- More complex to implement (cursor encoding, sort key management).
- Recommended if any table is expected to exceed 500k rows.

---

## Options Considered: Schema Registry Cache

**Option 1 — Re-parse rootElement from DB on every request:**
- Always correct; no invalidation complexity.
- Adds DB round-trip + JSON parse on every CRUD request.
- Acceptable at low traffic; degrades under load.

**Option 2 — In-memory cache of parsed column list (recommended for v1):**
- Cache keyed by `(designerId, version)`.
- Invalidated when a new version is promoted to Published.
- Requires a cache-bust mechanism (publish event triggers invalidation).

**Option 3 — Denormalized `designer_columns` table:**
- Queryable from any replica; survives process restarts.
- Adds write complexity (must be kept in sync with rootElement).

---

## Options Considered: OpenAPI for Dynamic Endpoints

**Option 1 — Document as `object` with `additionalProperties: true` (chosen for v1):**
- Valid OpenAPI; honest about the dynamic shape.
- Poor DX for consumers expecting a defined schema.

**Option 2 — Per-designer generated OpenAPI schemas:**
- Expose `/openapi/{designerId}.json` generated from the active schema.
- Better DX; enables client SDK generation.
- Significant complexity; deferred to v2.

---

## Persona Depth (overflow from §2)

**Platform Admin:** Likely an operations or IT lead. Technically comfortable (understands tables, columns, basic data types) but not a developer. Has used tools like Airtable, Notion, or Google Forms. Frustrated by needing developer tickets for simple form additions.

**Content Editor:** Front-line staff entering operational data (incident reports, inventory, survey responses). Mobile access is critical — often away from a desk. Speed matters: a form that takes more than 2 minutes to fill out will be abandoned or bypassed.

**Viewer:** Manager or auditor. Primarily interested in filtering and exporting data for review. Does not need to create or modify records.

**Developer / Integrator:** Builds integrations or automation on top of the CMS API. Needs the OpenAPI spec, predictable error codes, and documented pagination behavior.

---

## Options Considered: Email Library

**MailKit (recommended):**
- De facto .NET standard for SMTP; actively maintained; RFC-compliant.
- Supports STARTTLS, SMTPS, OAuth2, and connection pooling.
- Works standalone or wrapped by FluentEmail for a friendlier API.

**FluentEmail + MailKit:**
- Higher-level fluent builder API; easier template integration (Razor, Liquid, Scriban).
- Adds a dependency but reduces boilerplate.
- Recommended if email templates grow complex.

**SendGrid / Mailgun (cloud):**
- Reliable at scale; built-in tracking and analytics.
- Adds external SaaS dependency and cost.
- Deferred to v2 if SMTP volume or deliverability becomes a concern.

---

## Options Considered: TOTP Library

**Otp.NET (NuGet) — recommended:**
- Pure .NET, RFC 6238/4226 compliant; actively maintained.
- Simple API: `Totp.ComputeTotp(secret)` and `Totp.VerifyTotp(secret, code)`.
- Supports window/step tolerance (±1 step default).

**GoogleAuthenticator NuGet package:**
- Thinner wrapper; less maintained; fewer options.
- Not recommended over Otp.NET.

---

## Options Considered: TOTP Secret Encryption

**EF Core `IDataProtector` (recommended for v1):**
- Built-in ASP.NET Core; managed key rotation; no external KMS dependency.
- Keys stored in the file system (Aspire) or in a configurable key ring.
- Simple integration with EF Core value converters.

**AES-256-GCM with manual key management:**
- More explicit control over key material.
- Requires a key-management strategy (env var, Azure Key Vault, AWS Secrets Manager).
- Recommended if the platform moves to a cloud secrets manager.

---

## Backup and Restore Strategy (Operational Runbook — TBD)

The PRD defers backup strategy to the operational runbook. Recommendations for the architect:
- PostgreSQL: use `pg_dump` on a schedule (daily at minimum); store backups in MinIO or an external S3 bucket.
- MinIO: use MinIO's built-in replication or external sync to a backup bucket.
- Aspire / Docker Compose: document volume mount paths for persistent data.
- Test restore quarterly; document RTO/RPO targets.
