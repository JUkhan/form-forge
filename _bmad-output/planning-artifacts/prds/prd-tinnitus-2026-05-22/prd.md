---
title: FormForge
status: draft
created: 2026-05-22
updated: 2026-06-03
---

# PRD: FormForge

---

## 0. Document Purpose

This PRD defines requirements for a schema-driven, low-code Content Management System targeting authenticated internal users. Audience: development team (architecture, front-end, back-end), stakeholders validating scope, and downstream workflow owners (UX, epics/stories). Technology choices and existing asset details live in `addendum.md`. All Functional Requirements are globally numbered (FR-1 through FR-54) for stable downstream reference. Each Epic section contains its User Stories and Acceptance Criteria inline.

---

## 1. Vision

The FormForge lets non-developer Platform Admins design data-entry forms visually, bind each form to a navigation menu item, and have the platform automatically provision a backing PostgreSQL table. End-users then Create, Read, Update, and Delete records through a generic, permission-gated UI — no code written, no migrations run manually.

The platform is a **low-code internal data application factory**: one design session produces a database table, a menu entry, and a fully functional CRUD UI in a single workflow. Schema changes are always additive — no data is lost when a form evolves. Role-based permissions are enforced on both server and client. The platform ships with three visual themes switchable at runtime, a mobile-first responsive layout, and a public REST API with an OpenAPI specification for integrator use.

---

## 2. Target Users

### 2.1 Primary Personas

| Persona | Description | Primary Needs |
|---|---|---|
| **Platform Admin** | Configures the platform: designs schemas, manages menus, assigns roles, controls themes | Visual form builder requiring no SQL; safe, reversible schema migrations; full system visibility |
| **Content Editor** | Day-to-day data-entry user | Fast forms, mobile access, only sees menus they're entitled to |
| **Viewer** | Read-only consumer of records | Filterable, sortable, paginated record lists |
| **Developer / Integrator** | Extends or integrates with the platform | Predictable REST API, OpenAPI spec, structured error responses |

### 2.2 Jobs To Be Done

- **Platform Admin:** "When I need a new internal data-collection module, I can design and deploy it in an afternoon without involving a developer."
- **Platform Admin:** "When a form changes, I can evolve the schema without worrying I'll break existing records."
- **Content Editor:** "When I arrive at work, I see exactly the forms I need — nothing extra, nothing missing."
- **Content Editor:** "When I'm away from my desk, I can fill in records on my phone without a degraded experience."
- **Viewer:** "When I need to check a record, I can filter and find it without digging through pages."
- **Developer / Integrator:** "When I need to pull data, I can call a predictable, documented API without reverse-engineering the schema."

### 2.3 Non-Users (v1)

- Anonymous or public users — all access requires authentication.
- External customers — this is an internal-user platform.
- Users needing real-time collaborative editing on the designer canvas.
- Users needing a workflow or approval engine on records.

### 2.4 Key User Journeys

**UJ-1. Platform Admin deploys a new data module end-to-end.**
- **Persona + context:** Platform Admin, logged in to the admin area, needs a new "Incident Report" form for the operations team.
- **Entry state:** Authenticated; on the Component Library page.
- **Path:** Creates a new Designer (`designerId: incident_report`). Drags TextField (Title), TextArea (Description), DatePicker (Occurred At), Dropdown (Severity) onto the canvas; sets `fieldKey` values. Publishes version 1. Navigates to Menu Management, creates menu item "Incident Reports" under an "Operations" sub-menu, binds schema `incident_report@v1`, assigns role `ops-team`, saves. Table `incident_report` provisions automatically. Navigates to the live menu — sees the record list and a working Create form.
- **Climax:** Admin fills in a test record; it saves and appears in the list.
- **Resolution:** Admin invites an Ops Editor; they log in and see "Operations > Incident Reports" in their nav.
- **Edge case:** If `incident_report` table already exists, the system detects it and falls through to ALTER TABLE (no-op if schema matches); emits a warning in the admin UI.

**UJ-2. Content Editor enters a record on mobile.**
- **Persona + context:** Ops Editor on a mobile device, logging an incident in the field.
- **Entry state:** Authenticated via JWT; opens the app on a phone browser.
- **Path:** Taps the hamburger menu → "Operations" → "Incident Reports." Taps "New Record." DynamicComponent renders the form single-column. Fills in fields; taps the DatePicker; selects Severity "High." Taps "Save."
- **Climax:** Success toast appears; the record appears in the list immediately.
- **Resolution:** Editor closes the form; list shows the new entry with a timestamp.
- **Edge case:** Network drops mid-submit — form retains input; error banner explains the failure with a retry option.

**UJ-3. Platform Admin grants a narrowly scoped role.**
- **Persona + context:** Platform Admin onboarding a contractor who should see Incident Reports but not create or modify them.
- **Entry state:** In Admin > Roles.
- **Path:** Creates role `contractor-viewer` with `canRead: true` on resource `incident_report`, all others false. Creates the contractor user. Assigns role. Contractor logs in — sees "Operations > Incident Reports" in nav. "New Record" button is absent. Clicking a record shows a read-only view.
- **Climax:** Admin confirms the contractor sees only what they need.
- **Resolution:** Contractor is active; access is as configured.

---

## 3. Glossary

- **Designer** — a named, versioned form layout definition. Identified by `designerId` (snake_case string that also becomes the backing PostgreSQL table name for CRUD-mode components). A Designer has one or more Versions. Synonymous with **Component** in the Component Library UI.
- **Component Mode** — a `CRUD` or `VIEW` designation on each component (Designer), set at creation and stored in the `component_schemas.mode` column. `CRUD` components provision a backing table and support full data entry; `VIEW` components never provision a table and are rendered read-only by the DynamicComponent. See FR-54.
- **Version** — an integer-incremented snapshot of a Designer's `rootElement`. Immutable once Published. Status lifecycle: Draft → Published → Archived.
- **RootElement** — the JSON tree representing a Designer's layout. Stored as `jsonb` in `component_schemas`. Consumed by the DynamicComponent renderer to produce a live form.
- **Component** — a single node in the RootElement tree. Has a `type` (e.g., `TextInput`, `Dropdown`, `Repeater`), an `id`, optional `children`, and `properties` (including `fieldKey`, `label`, display options).
- **fieldKey** — the `properties.fieldKey` on a leaf Component. Becomes the PostgreSQL column name in the provisioned table. Must be a valid SQL identifier.
- **Repeater** — a structural Component that holds a `{ designerId, version }` reference to another Designer, representing a one-to-many child relationship. Triggers recursive child table provisioning.
- **Menu Item** — a navigation entry in the left sidebar. Top-level or sub-item (max depth 2). Carries a Schema Binding, role assignments, icon, and ordering.
- **Sub-menu** — a Menu Item whose `parentId` references another top-level Menu Item. Max 1 level of nesting enforced.
- **Schema Binding** — the association between a Menu Item and a specific Designer Version (`{ designerId, version }`). Triggers table provisioning when created or updated for CRUD-mode components; bindings to VIEW-mode components skip provisioning (see Component Mode).
- **Table Provisioning** — the server-side process that reads a Designer's RootElement and emits `CREATE TABLE` or `ALTER TABLE` DDL against PostgreSQL. Runs only for CRUD-mode components.
- **Schema Drift** — columns present in a provisioned table that no longer appear in the current Designer schema (result of additive-only migrations over time).
- **Resource** — the unit of permission scoping. A Resource is identified by `designerId`; Role permissions are granted per-Resource.
- **Effective Permission** — the union of all CRUD flags a user holds across all their Roles for a given Resource. If any Role grants `canCreate`, the user can create.
- **Soft Delete** — marking a record `is_deleted = true` rather than removing the row. Preserved and restorable by a Platform Admin. List endpoints return all records (including soft-deleted) by default.
- **Schema Change Audit Log** — append-only log of DDL events: actor, timestamp, and column diff.
- **CRUD Mutation Audit Log** — append-only log of data-level changes: actor, timestamp, and field-level diff.
- **DynamicComponent** — the React component that consumes a RootElement JSON and renders a live, interactive form. Used in the designer preview and all data-entry views.
- **Aspire AppHost** — the .NET Aspire orchestration entry point wiring the API, PostgreSQL, MinIO, and frontend into a single local dev environment.
- **MFA (Multi-Factor Authentication)** — A security mechanism requiring two or more verification factors to authenticate. FormForge implements TOTP as a voluntary second factor.
- **TOTP (Time-Based One-Time Password)** — An algorithm (RFC 6238) generating a rotating 6-digit code every 30 seconds. Compatible with Google Authenticator, Authy, 1Password, and any TOTP-compliant app.
- **Password Reset Token** — A short-lived (1-hour TTL), single-use opaque token emailed to a user to allow setting a new password without knowing the current one. Only its SHA-256 hash is stored server-side.
- **Backup Code** — A one-time-use alphanumeric code generated at MFA enrolment. Allows a user to authenticate when their authenticator device is unavailable. Stored as bcrypt hashes; shown only once at enrolment.
- **Welcome Email** — An automated email sent to a newly created user containing their email address, temporary password, and a login link.
- **Dataset** — a named, user-defined query abstraction persisted as a row in `custom_dataset` and materialized as a PostgreSQL VIEW whose name equals `dataset_name`. A Dataset is either Custom Query Mode or Query Builder Mode, toggled by `is_custom_query`.
- **dataset_name** — the unique identifier for a Dataset and the exact name of its backing PostgreSQL VIEW. Must satisfy PostgreSQL unquoted-identifier rules: matches `^[a-z_][a-z0-9_]*$`, ≤63 bytes, unique, not a PostgreSQL reserved keyword. Validated client-side and server-side before any DDL.
- **Custom Query Mode** — a Dataset authoring mode (`is_custom_query = true`) where the user writes SQL directly in a textarea. Only SELECT statements are accepted; DDL and DML are rejected server-side before any VIEW DDL executes.
- **Query Builder Mode** — a Dataset authoring mode (`is_custom_query = false`) where the user constructs a query visually on a React Flow canvas. The server re-derives authoritative SQL from `builder_state` before any VIEW DDL or preview execution — the client never sends raw SQL in this mode.
- **builder_state** — the JSONB column on `custom_dataset` storing the complete React Flow canvas state (table nodes with positions, join edges, column selections with aggregates/aliases, filter conditions, CASE/calculated columns, ORDER BY clauses) for Query Builder Mode. Persisted on save; restored in full on reopen.
- **Dataset View** — the PostgreSQL VIEW created, replaced, or dropped in lockstep with each Dataset row write. The view name equals `dataset_name`. All Dataset DDL is transactional with the row write so the table row and VIEW never diverge.
- **Table Palette** — the left-side panel in the Query Builder canvas listing server-allowlisted/authorized tables that users can drag onto the canvas.
- **Join Edge** — a React Flow edge connecting a column handle on one table node to a column handle on another, representing a SQL JOIN between those tables. Clicking a join edge opens the Join Inspector.
- **Join Inspector** — a property panel opened by clicking a Join Edge, allowing configuration of join type (INNER / LEFT / RIGHT / FULL OUTER) and display of the two joined columns.
- **dataset-management** — the RBAC permission required to create, update, and delete Datasets. Enforced server-side on write/delete endpoints. Read endpoints (GET /api/datasets) require authentication but not this permission.
- **Table Allowlist** — the server-side list of PostgreSQL tables that are authorized for use in the Query Builder. Enforced during SQL generation; tables outside the allowlist cannot be referenced in any Dataset query.

---

## 4. Features

---

### Epic A — Identity, Roles & Permissions

Identity is the first required deliverable. Every other Epic depends on authenticated, authorized requests.

#### FR-1: User Account Management (Admin-Managed)

Platform Admins create, edit, and deactivate user accounts. Self-registration is not supported. User record: `id` (uuid PK), `email` (unique), `passwordHash` (bcrypt), `displayName`, `themePreference`, `isActive`, `createdAt`, `updatedAt`.

**Consequences:**
- Admin POST /api/admin/users creates a user; duplicate email → HTTP 409.
- Deactivated user cannot obtain a new JWT; their refresh tokens are invalidated immediately on deactivation.
- Password stored as bcrypt hash; plaintext never persisted or logged.

**Out of Scope:** Self-service registration. Admin-triggered welcome email (FR-50) and self-service password reset (FR-51) are now in scope.

---

##### Story A-1: JWT Login
**As any** registered user, **I can** submit email and password to receive a JWT access token and a refresh token **so that** I can authenticate subsequent API requests.

**Acceptance Criteria:**
- AC-1: POST /api/auth/login with valid credentials returns `{ accessToken, refreshToken, expiresIn }`.
- AC-2: Access token is a signed JWT containing `userId`, `email`, `roles` (array of role names), `iat`, `exp` (15-minute TTL).
- AC-3: Refresh token is an opaque token stored server-side in a `refresh_tokens` table with a 7-day TTL.
- AC-4: Invalid credentials → HTTP 401 with a generic message (no user enumeration).
- AC-5: Inactive user (`isActive: false`) → HTTP 403 with `"Account is inactive"`.

##### Story A-2: Token Refresh
**As an** authenticated user with a valid refresh token, **I can** exchange it for a new access token **so that** my session persists without re-login.

**Acceptance Criteria:**
- AC-1: POST /api/auth/refresh returns new `{ accessToken, refreshToken, expiresIn }`.
- AC-2: Old refresh token is revoked on use (single-use rotation).
- AC-3: Expired or revoked refresh token → HTTP 401.

##### Story A-3: Logout
**As an** authenticated user, **I can** log out and revoke my refresh token **so that** my session cannot be resumed.

**Acceptance Criteria:**
- AC-1: POST /api/auth/logout revokes the submitted refresh token; subsequent use → HTTP 401.
- AC-2: Client-side access token discarded from memory.

#### FR-2: Role Definition with Per-Resource CRUD Flags

A Role carries four boolean flags per Resource (`canCreate`, `canRead`, `canUpdate`, `canDelete`). A Resource is identified by `designerId`. A Role can cover multiple Resources.

##### Story A-4: Role CRUD
**As a** Platform Admin, **I can** create, edit, and delete Roles with per-Resource CRUD flag configuration **so that** I can define what each Role is permitted to do on each data module.

**Acceptance Criteria:**
- AC-1: Role schema: `id`, `name` (unique), `description`, `permissions[]` → `{ resourceId, canCreate, canRead, canUpdate, canDelete }`.
- AC-2: Deleting a Role with active user assignments → HTTP 409 ("Remove user assignments first").
- AC-3: Two system roles seeded: `platform-admin` (full access to all resources and admin areas) and `viewer` (canRead only, all resources).
- AC-4: GET /api/admin/roles returns paginated role list.

#### FR-3: User-Role Assignments

##### Story A-5: User-Role Assignment
**As a** Platform Admin, **I can** assign and remove Roles from a User **so that** I can control their effective permissions.

**Acceptance Criteria:**
- AC-1: PUT /api/admin/users/{id}/roles accepts an array of `roleId` values and replaces the user's role set atomically.
- AC-2: A user can hold zero or more roles.
- AC-3: Role changes take effect within 30 seconds (server validates active role set from DB on each request, cached ≤ 30 s; cache invalidated on assignment change). `[ASSUMPTION: server-side DB role validation with short cache, not solely from JWT claims]`

#### FR-4: Effective Permission Computation

User's effective permission on a Resource = union of all their Roles' flags for that Resource. Any Role granting `canCreate` means the user can create. Implemented as a helper called by every endpoint and every client-side permission check.

#### FR-5: Server-Side Endpoint Authorization

##### Story A-6: Server-Side Permission Enforcement
**As the** system, **I** enforce role-based permissions on every CRUD and admin endpoint **so that** unauthorized actions are rejected server-side regardless of client behavior.

**Acceptance Criteria:**
- AC-1: Every request to /api/data/{designerId}/* checks the user's effective permission for that Resource.
- AC-2: Insufficient permission → HTTP 403 with `{ code: "FORBIDDEN", resource: designerId, action: "create|read|update|delete" }`.
- AC-3: Unauthenticated request (no or invalid JWT) → HTTP 401.
- AC-4: Admin endpoints (/api/admin/*) require the `platform-admin` role.

#### FR-6: Client-Side UI Permission Adaptation

##### Story A-7: Client-Side Permission Hiding
**As a** Content Editor or Viewer, **I** see only UI controls I am authorized to use **so that** I'm not confused by actions I cannot perform.

**Acceptance Criteria:**
- AC-1: "New Record" button hidden if user lacks `canCreate` on the current Resource.
- AC-2: Edit/Delete controls in record lists and detail views hidden if user lacks `canUpdate`/`canDelete`.
- AC-3: Menu items for which the user has no `canRead` are absent from the navbar (not disabled — absent).
- AC-4: Permission state derived from decoded JWT claims + GET /api/users/me/permissions; refreshed on token refresh.

#### FR-7: Admin UI — Users, Roles, Assignments

##### Story A-8: Admin User Management UI
**As a** Platform Admin, **I can** manage users from a dedicated settings page **so that** I have full control over who accesses the platform without touching the database.

**Acceptance Criteria:**
- AC-1: Admin > Users page lists all users: name, email, status (Active/Inactive), role count.
- AC-2: Create User form: email, display name, temporary password. On creation, a welcome email is dispatched automatically to the new user (see FR-50, Story A-10).
- AC-3: Deactivate is reversible (Reactivate available).
- AC-4: Role assignment shows available roles as a multi-select; saves on confirm.
- AC-5: Admin cannot deactivate their own account.

##### Story A-9: Admin Role Management UI
**As a** Platform Admin, **I can** manage Roles and their per-Resource permissions from a dedicated admin page **so that** I can define access control without writing SQL.

**Acceptance Criteria:**
- AC-1: Admin > Roles page lists all roles: name, description, permission count.
- AC-2: Role editor shows a matrix: rows = Resources (all Designers with a Menu Binding), columns = CRUD flags; each cell is a checkbox.
- AC-3: Creating a new Menu Binding (new Resource) adds a row to the permission matrix for all existing Roles, defaulting all flags to false.

#### FR-50: Admin Welcome Email on User Creation

When a Platform Admin creates a new user, the platform automatically dispatches a welcome email to the user's registered email address containing their credentials and a login link.

**Consequences:**
- Email is dispatched asynchronously on successful user creation; SMTP delivery failure does not block account creation.
- Email body contains: platform name, the user's email address, their temporary password (plaintext, as set by the admin), and a link to the login page.
- Email transport is configured via environment variables; a local-dev SMTP catcher (Mailpit) is used when not configured.

---

##### Story A-10: Welcome Email on User Creation
**As a** newly created user, **I** receive a welcome email when a Platform Admin creates my account **so that** I have my credentials without requiring an out-of-band handoff.

**Acceptance Criteria:**
- AC-1: On successful POST /api/admin/users, the system dispatches a welcome email to the new user's email address.
- AC-2: Email contains: platform name, the user's email address, their temporary password (plaintext), and a link to the login page.
- AC-3: Email delivery failure does not block user creation; the failure is logged and a non-blocking warning is included in the API response (`warnings: ["Welcome email could not be sent"]`).
- AC-4: Email is dispatched asynchronously (fire-and-forget with error logging); the creation endpoint does not wait for SMTP delivery.
- AC-5: Email transport configured via environment variables (`SMTP_HOST`, `SMTP_PORT`, `SMTP_USER`, `SMTP_PASS`, `SMTP_FROM`); Mailpit used as dev default.

#### FR-51: Forgot Password Self-Service Flow

Unauthenticated users can request a password reset by submitting their registered email address. The platform sends a time-limited, single-use reset link. Following the link presents a form to set a new password.

**Consequences:**
- Password reset tokens are 32-byte random values; only the SHA-256 hash is stored server-side.
- Token TTL is 1 hour; tokens are single-use and immediately invalidated after successful use.
- Successful reset invalidates all active refresh tokens for the user.
- The API never reveals whether a given email is registered — no user enumeration.

---

##### Story A-11: Forgot Password Flow
**As an** unauthenticated user who cannot access my account, **I can** request a password reset via email and follow the reset link to set a new password **so that** I can regain access without admin involvement.

**Acceptance Criteria:**
- AC-1: POST /api/auth/forgot-password accepts `{ email }`. Always returns HTTP 200 with a generic message ("If that email is registered, a reset link has been sent") — no user enumeration regardless of whether the email exists.
- AC-2: If the email belongs to an active user account, a 32-byte random token is generated; only its SHA-256 hash is stored in `password_reset_tokens` with a 1-hour TTL and a `usedAt` column (null until used).
- AC-3: A reset-link email is dispatched asynchronously containing the raw token (e.g., `https://<host>/reset-password?token=<raw>`); delivery failure is logged and does not surface to the caller.
- AC-4: POST /api/auth/reset-password accepts `{ token, newPassword }`. Validates the token: hash match, not expired, `usedAt` is null. On success: updates `passwordHash`, sets `usedAt`, invalidates all refresh tokens for the user, returns HTTP 200.
- AC-5: Invalid, expired, or already-used token → HTTP 400 ("Reset link is invalid or has expired").
- AC-6: `newPassword` minimum 8 characters; must differ from the previous password (bcrypt comparison). Failure → HTTP 422.
- AC-7: Frontend: `/forgot-password` route — email input, submit button, success confirmation message. `/reset-password` route — new password + confirm password fields; on success, redirect to `/login` with a success toast.

#### FR-52: Authenticated Password Change

Authenticated users can change their own password from their account settings by providing their current password. On success, all other active sessions are revoked.

**Consequences:**
- Requires the current password to verify intent — no reset token needed.
- All refresh tokens for the user except the token belonging to the current session are revoked on success.

---

##### Story A-12: Authenticated Password Change
**As an** authenticated user, **I can** change my password from my account settings **so that** I can update my credentials without admin involvement.

**Acceptance Criteria:**
- AC-1: PUT /api/users/me/password accepts `{ currentPassword, newPassword }`.
- AC-2: `currentPassword` verified via bcrypt comparison. Mismatch → HTTP 401 ("Current password is incorrect").
- AC-3: `newPassword` minimum 8 characters; must differ from `currentPassword` (bcrypt comparison). Failure → HTTP 422.
- AC-4: On success: `passwordHash` updated; all refresh tokens for the user except the current session's are revoked; HTTP 200 returned.
- AC-5: Frontend: "Change Password" section in the user profile/settings area — current password, new password, and confirm new password fields; success toast; form cleared after success.

#### FR-53: TOTP-Based Multi-Factor Authentication

Users can voluntarily enable TOTP MFA on their account. When MFA is enabled, login requires a valid TOTP code (or backup code) after password verification. TOTP secrets are stored encrypted at rest. Eight single-use backup codes are generated at enrolment. Platform Admins can reset MFA for any user.

**Consequences:**
- TOTP conforms to RFC 6238 (SHA-1, 30-second period, 6-digit codes, ±1 step clock-skew tolerance).
- Login becomes a two-step exchange when MFA is active: password → intermediate `mfaSessionToken` → TOTP challenge → JWT pair.
- The TOTP secret is not committed until the user successfully verifies a code at enrolment (prevents dangling unconfirmed secrets).
- Only one TOTP method per user in v1; re-enrolment replaces the prior secret and generates new backup codes.

---

##### Story A-13: TOTP MFA Enrolment
**As a** user, **I can** enrol in TOTP multi-factor authentication by scanning a QR code with an authenticator app and confirming with a one-time code **so that** my account is protected by a second factor.

**Acceptance Criteria:**
- AC-1: GET /api/users/me/mfa/enrol returns `{ secret, qrCodeDataUrl, backupCodes[] }`. `secret` is a base32-encoded TOTP secret; `qrCodeDataUrl` is a `data:image/png;base64,...` QR code encoding the `otpauth://totp/FormForge:<email>?secret=<secret>&issuer=FormForge` URI.
- AC-2: `backupCodes` is an array of 8 single-use 8-character alphanumeric codes; only their bcrypt hashes are stored in the DB; the raw codes are shown to the user only at this enrolment step.
- AC-3: POST /api/users/me/mfa/verify accepts `{ code }`. Verifies a 6-digit TOTP code (±1 step tolerance) against the pending secret. On success: `mfaEnabled` set to `true` on the user record; encrypted `mfaSecret` and backup code hashes persisted; HTTP 200 returned.
- AC-4: Wrong or expired TOTP code → HTTP 400. The secret is not persisted until this verification succeeds.
- AC-5: Re-enrolment (calling GET /api/users/me/mfa/enrol when MFA is already active) replaces the active secret; the new secret is not committed until a subsequent POST /api/users/me/mfa/verify succeeds.
- AC-6: Frontend: a "Security" tab in the user profile. "Enable MFA" button opens a multi-step modal: step 1 — QR code display and manual-entry secret; step 2 — TOTP code input to confirm; step 3 — backup codes display with a "I have saved these codes" confirmation before closing.

##### Story A-14: TOTP MFA Verification on Login
**As a** user with MFA enabled, **I am** prompted for a TOTP code after submitting my password **so that** a second factor is required to obtain a session.

**Acceptance Criteria:**
- AC-1: POST /api/auth/login for an MFA-enabled user with a valid password returns HTTP 200 `{ mfaRequired: true, mfaSessionToken: "<opaque>" }` — no access or refresh token is issued at this step.
- AC-2: `mfaSessionToken` is stored server-side (`mfa_sessions` table) with a 5-minute TTL; it is single-use and cryptographically opaque.
- AC-3: POST /api/auth/mfa-verify accepts `{ mfaSessionToken, code }`. Validates the session token (not expired, not yet used); verifies the TOTP code or a backup code (a matched backup code is immediately invalidated). On success: issues `{ accessToken, refreshToken, expiresIn }` and invalidates the `mfaSessionToken`.
- AC-4: Invalid `mfaSessionToken` → HTTP 401. Wrong code → HTTP 401. After 5 consecutive wrong-code attempts on the same `mfaSessionToken`, the token is revoked and the user must restart the login flow from the password step.
- AC-5: A used backup code is immediately marked `usedAt` in the DB; remaining backup codes are unaffected.
- AC-6: Frontend: after the password step, a "Two-factor authentication" screen shows a 6-digit code input that autofocuses; a "Use a backup code instead" link switches to a plain-text code input.

##### Story A-15: Admin MFA Reset
**As a** Platform Admin, **I can** disable MFA for any user account **so that** I can restore access for a user who has lost both their authenticator device and their backup codes.

**Acceptance Criteria:**
- AC-1: DELETE /api/admin/users/{id}/mfa clears `mfaEnabled`, `mfaSecret`, and all backup codes for the target user. Requires `platform-admin` role.
- AC-2: Admin UI: a "Reset MFA" button appears on the user detail view only when the user has MFA enabled; a confirmation dialog must be accepted before the action executes.
- AC-3: Resetting MFA invalidates all active refresh tokens for the affected user, forcing re-login without an MFA challenge.

---

### Epic B — Component Schema Designer

The Designer is ported from the ESG Platform reference codebase. The port story (B-1) is the prerequisite for all other Designer stories. See `addendum.md` for the full asset audit.

#### FR-8: Designer Port and Refactor

##### Story B-1: Port and Refactor Designer Code
**As a** Developer, **I** audit and port the component designer (ComponentDesignerPage, ComponentLibraryPage, DynamicComponent, DesignerCanvas, ElementRenderer, DesignerToolbar) from the ESG Platform reference codebase, refactoring for shadcn/ui, React 19 patterns, TanStack React Query v5, and the new project structure **so that** the designer is a first-class part of the new CMS with no ESG-platform-specific dependencies.

**Acceptance Criteria:**
- AC-1: All 14 component types (Stack, Row, Tabs, Label, Button, TextInput, TextArea, NumberInput, Checkbox, Dropdown, DateTimePicker, ColorPicker, Repeater, RepeaterField, Image) render correctly on the canvas.
- AC-2: Native HTML5 drag-and-drop behavior preserved: drag from palette, reorder on canvas, precise insertion via DropZone between children.
- AC-3: No references to ESG Platform-specific API paths, data models, or business logic remain.
- AC-4: All UI components use shadcn/ui primitives; no prior component library imports.
- AC-5: DynamicComponent preserves its full behavioral contract: conditional visibility (`computeVisibility`), Repeater row scoping, external `submitRef`, `onValidityChange`, `onReadyChange` callbacks, shallow-equal `initialData` comparison.
- AC-6: TanStack React Query v5 patterns used for all data fetching (no v4 API patterns).

#### FR-9: Designer Creation

##### Story B-2: Create New Designer
**As a** Platform Admin, **I can** create a new Designer by providing a `displayName`, `designerId`, and `mode` (CRUD or VIEW) **so that** I can start building a new form layout.

**Acceptance Criteria:**
- AC-1: POST /api/designers `{ displayName, designerId, mode }` returns the created Designer with `version: 1`, `status: Draft`.
- AC-2: `designerId` validated server-side: lowercase letters, digits, underscores; starts with a letter or underscore; 1–63 characters; not a reserved PostgreSQL keyword. Invalid → HTTP 422 with descriptive message.
- AC-3: Duplicate `designerId` → HTTP 409.
- AC-4: New Designer opens immediately in the canvas with an empty RootElement (single Stack root node).
- AC-5: `mode` is required and must be `CRUD` or `VIEW`; omitting it or supplying any other value → HTTP 422. The creation form surfaces mode as a required selector with no default. See FR-54.

#### FR-10: Canvas Drag-and-Drop

##### Story B-3: Design Canvas Interaction
**As a** Platform Admin, **I can** drag components from the palette onto the canvas, reorder them, nest them within structural components, and delete them **so that** I can lay out my form design.

**Acceptance Criteria:**
- AC-1: Palette shows all 14 component types, categorized as Structural (Stack, Row, Tabs) and Leaf (all others).
- AC-2: Dropping a component onto a DropZone inserts it at the correct index within the parent.
- AC-3: Components can be reordered by drag within the same parent.
- AC-4: Structural components accept children; Leaf components do not.
- AC-5: Delete icon on a component removes it and all its children from the tree.
- AC-6: Canvas emits the updated RootElement JSON after every structural change.

#### FR-11: Component Property Configuration

##### Story B-4: Configure Component Properties
**As a** Platform Admin, **I can** select any component on the canvas and edit its properties in a properties panel **so that** I can define how each field behaves and is stored.

**Acceptance Criteria:**
- AC-1: Clicking a component opens a panel specific to its type (Dropdown shows an options editor; TextInput shows placeholder, maxLength; Repeater shows a Designer/version picker; etc.).
- AC-2: `fieldKey` is required for all input-bearing leaf components (TextInput, TextArea, NumberInput, Checkbox, Dropdown, DateTimePicker, ColorPicker, Image, RepeaterField). Validated as a SQL-safe identifier.
- AC-3: Changes reflect immediately in the canvas preview.
- AC-4: The Dropdown "Source component" picker (shown when the Dropdown's options source is another component) and the Repeater "Row form — Component" picker list only CRUD-mode components; VIEW-mode components are excluded from both. See FR-54.

#### FR-12: Live Preview

##### Story B-5: Designer Live Preview
**As a** Platform Admin, **I can** toggle a preview mode that renders the current design as a DynamicComponent **so that** I can validate the form experience before publishing.

**Acceptance Criteria:**
- AC-1: Preview uses the same DynamicComponent code path used in data entry — not a separate renderer.
- AC-2: Preview is read-only; no data is submitted.
- AC-3: Visibility conditions are active in preview (form behaves as a real form).
- AC-4: Repeater sections show an "Add row" control in preview.

#### FR-13: Designer Save and Versioning

##### Story B-6: Save Designer Version
**As a** Platform Admin, **I can** save the current canvas state, creating a new version **so that** I have an immutable snapshot of the design.

**Acceptance Criteria:**
- AC-1: Save POST /api/designers/{id}/versions creates a new version with `rootElement` (current canvas JSON), auto-incremented `version`, `status: Draft`, `createdAt`, `createdBy`.
- AC-2: Previous versions are not mutated.
- AC-3: Save completes in < 500 ms p95.
- AC-4: Any input-bearing component with an empty `fieldKey` blocks save with an inline validation error identifying the offending component.

##### Story B-7: Version Status Management
**As a** Platform Admin, **I can** promote a version from Draft to Published, or Archive a Published version **so that** I control which version is used in production bindings.

**Acceptance Criteria:**
- AC-1: At most one Published version per Designer at any time. Promoting a new version auto-demotes the previously Published version to Archived.
- AC-2: Archived versions remain queryable and can be the basis for a new version.
- AC-3: An Archived version can be re-Published.
- AC-4: Menu bindings pinned to a specific `designerId@vN` continue functioning even if that version is Archived.
- AC-5: Only Published versions can be bound to a Menu Item (Draft binding → HTTP 422).

#### FR-14: Component Library / Designer Listing

##### Story B-8: Designer Library Page
**As a** Platform Admin, **I can** browse all Designers, view their versions and statuses, preview any version, and manage version lifecycle **so that** I have a central catalog of all form definitions.

**Acceptance Criteria:**
- AC-1: Library page lists Designers: Display Name, Designer ID, Mode (CRUD/VIEW badge), Current Version, Status, Last Updated, Creator.
- AC-2: Per-row actions: Open in Canvas (specific version), New Version (modal with auto-incremented number), Duplicate, Archive.
- AC-3: Version list flyout shows all versions with status badge and relative timestamp.
- AC-4: Preview modal renders the DynamicComponent for the selected version in-place.

#### FR-15: DynamicComponent in Data Entry

##### Story B-9: DynamicComponent Renderer for Data Entry
**As the** system, **I** render any bound Designer version as a live, submittable form for end-users **so that** data entry requires no per-module custom code.

**Acceptance Criteria:**
- AC-1: DynamicComponent fetches RootElement from /api/designers/{designerId}/versions/{version} (TanStack Query, staleTime 300 s).
- AC-2: All 14 component types render as appropriate input controls.
- AC-3: Visibility conditions (computed from `computeVisibility`) correctly show/hide subtrees based on current form values.
- AC-4: Repeater sections allow adding, editing, and removing child rows.
- AC-5: On submit, calls `onSave(payload)` with collected form data, excluding hidden fields and undefined values.

#### FR-54: Component Mode (VIEW / CRUD)

Every component (Designer) declares a **mode** at creation — `CRUD` or `VIEW` — stored as the `mode` column on the component's `component_schemas` record. Mode is required at creation and fixed for the life of the component (it does not change between versions).

- **CRUD mode** — the default, full-featured behavior described throughout Epics B–E. Binding the component to a Menu Item provisions a backing PostgreSQL table, and the generic CRUD API (Epic E) serves create / read / update / delete against it.
- **VIEW mode** — a display-only component. It **never** provisions a table and exposes **no** `/api/data/{designerId}` endpoints. When bound to a Menu Item it is rendered read-only by the DynamicComponent renderer — no record list, no "New Record" control, no save.

**Consequences:**
- `component_schemas.mode` is `NOT NULL`. Components created before this feature are backfilled to `CRUD`, preserving existing behavior.
- Binding a VIEW component to a Menu Item skips Table Provisioning (Epic D) entirely; no DDL runs and no `schema_audit_log` entry is written.
- Because a VIEW component has no table, the per-Resource CRUD flags (FR-2) are not evaluated for it; access is governed solely by the Menu Item's `allowedRoles` (FR-19).
- Designer property pickers that reference another component for its **data** are filtered to **CRUD-mode components only**: the Dropdown "Source component" picker (options source = component) and the Repeater "Row form — Component" picker. VIEW-mode components are excluded because they have no table to source options from or to persist child rows in.

---

##### Story B-10: Declare and Enforce Component Mode
**As a** Platform Admin, **I can** set a component's mode to CRUD or VIEW at creation **so that** display-only components don't create database tables and data-bearing components do.

**Acceptance Criteria:**
- AC-1: `mode` is a required field on POST /api/designers (`CRUD` | `VIEW`); omitting it or supplying any other value → HTTP 422.
- AC-2: `mode` is persisted on the component's `component_schemas` record (`mode` column, `NOT NULL`) and is immutable after creation; the API rejects attempts to change it → HTTP 422.
- AC-3: Binding a `VIEW`-mode component to a Menu Item does not trigger table provisioning and writes no `schema_audit_log` DDL entry.
- AC-4: A `VIEW`-mode component bound to a Menu Item renders read-only via DynamicComponent — no record list, no "New Record" control, no submit/save; no `/api/data/{designerId}` endpoint is exposed for it.
- AC-5: The Dropdown "Source component" picker and the Repeater "Row form — Component" picker list only `CRUD`-mode components; `VIEW`-mode components are excluded from both.
- AC-6: Existing components created before this feature are backfilled to `CRUD` mode (no behavior change).

---

### Epic C — Menu Management

#### FR-16: Menu Item CRUD

##### Story C-1: Create and Manage Top-Level Menu Items
**As a** Platform Admin, **I can** create, edit, and delete top-level Menu Items **so that** I can define the platform's navigation structure.

**Acceptance Criteria:**
- AC-1: POST /api/admin/menus creates a Menu Item; required: `name`, `order`; optional: `icon`, `isActive` (defaults true).
- AC-2: Delete blocked if item has active sub-menu children; admin must remove children first → HTTP 409.
- AC-3: Menu items ordered by `order` integer; gaps permitted.

##### Story C-2: Create Sub-menu Items (2-Level Max)
**As a** Platform Admin, **I can** nest a Menu Item one level under a parent **so that** I can group related data modules under a section heading.

**Acceptance Criteria:**
- AC-1: POST /api/admin/menus with `parentId` creates a sub-menu item.
- AC-2: `parentId` pointing to an existing sub-menu item (attempt at 3rd level) → HTTP 422.
- AC-3: Top-level items without a Schema Binding serve as section headers (no data view).

#### FR-17: Schema Binding

##### Story C-3: Bind Designer Version to Menu Item
**As a** Platform Admin, **I can** bind a specific Published Designer version to a Menu Item **so that** the platform provisions the backing table and connects the CRUD UI.

**Acceptance Criteria:**
- AC-1: Binding set via `{ designerId, version }` on the Menu Item. Only Published versions accepted (Draft → HTTP 422).
- AC-2: Saving a binding triggers table provisioning (Epic D) asynchronously; admin UI shows provisioning status (Pending / Success / Error).
- AC-3: If provisioning fails, the Menu Item is saved but flagged with an error; admin can retry provisioning independently.
- AC-4: Updating the bound version (e.g., v1 → v2) triggers additive ALTER TABLE.
- AC-5: Binding a VIEW-mode component triggers no provisioning (no CREATE/ALTER TABLE, no `schema_audit_log` entry); the binding is saved and the Menu Item renders the component read-only. Provisioning status is shown as "Not applicable (view-only)". See FR-54.

#### FR-18: Menu Icon

##### Story C-4: Assign Icon to Menu Item
**As a** Platform Admin, **I can** assign a lucide-react icon name or upload an image as a Menu Item icon **so that** the navbar is visually navigable.

**Acceptance Criteria:**
- AC-1: Icon stored as `{ type: "lucide", name: "HomeIcon" }` or `{ type: "minio", objectKey: "menus/{uuid}.png" }`.
- AC-2: Image upload: POST /api/admin/menus/upload-icon; validates type (PNG, JPG, SVG), max 2 MB; stores in MinIO; returns object key.
- AC-3: Lucide icon name validated against available lucide-react icons; invalid → HTTP 422.
- AC-4: Null icon falls back to a default placeholder.

#### FR-19: Role-Based Menu Access

##### Story C-5: Assign Roles to Menu Item
**As a** Platform Admin, **I can** configure which Roles can access a Menu Item **so that** only authorized users see and interact with that data module.

**Acceptance Criteria:**
- AC-1: Menu Item has an `allowedRoles` array of `roleId` values.
- AC-2: A user sees a Menu Item in the navbar only if at least one of their Roles is in `allowedRoles`.
- AC-3: Server-side: /api/data/{designerId}/* additionally checks `allowedRoles` membership before evaluating CRUD flags.

#### FR-20: Menu Ordering

##### Story C-6: Reorder Menu Items via Drag-and-Drop
**As a** Platform Admin, **I can** reorder Menu Items and sub-menu items by drag-and-drop in the menu editor **so that** the navbar presents items in the correct order.

**Acceptance Criteria:**
- AC-1: Drag-and-drop reordering calls PUT /api/admin/menus/reorder with `[{ id, order }]`.
- AC-2: Sub-menu items can be reordered within their parent only; cannot be promoted to top-level by drag.
- AC-3: Order changes propagate to all user navbars within ≤ 5 s (cache TTL or write-time invalidation).

#### FR-21: isActive Toggle

##### Story C-7: Activate and Deactivate Menu Items
**As a** Platform Admin, **I can** toggle a Menu Item's `isActive` flag **so that** I can hide items without deleting them.

**Acceptance Criteria:**
- AC-1: Inactive items excluded from the navbar for all users (including Platform Admins browsing as a regular user).
- AC-2: Platform Admins can access inactive items from Admin > Menus.

#### FR-22: Dynamic Navbar

##### Story C-8: Render Permission-Filtered Navigation
**As an** authenticated user, **I can** see a dynamic left-side navbar showing only the Menu Items my Roles authorize me to view **so that** I am not presented with inaccessible options.

**Acceptance Criteria:**
- AC-1: GET /api/menus returns only items where `isActive = true` AND requesting user's Roles intersect `allowedRoles`.
- AC-2: Navbar renders top-level items and their sub-items in `order` sequence.
- AC-3: On mobile, navbar collapses to a hamburger menu; tapping an item auto-closes it.

---

### Epic D — Dynamic Table Provisioning

All DDL executes via Dapper in explicit PostgreSQL transactions. Triggered by Schema Binding creation or version update.

#### FR-23: designerId Identifier Validation

##### Story D-1: Validate designerId as Safe PostgreSQL Identifier
**As the** system, **I** validate any `designerId` before using it as a table name **so that** DDL statements cannot be constructed with unsafe input.

**Acceptance Criteria:**
- AC-1: Valid: lowercase letters, digits, underscores; starts with a letter or underscore; 1–63 characters.
- AC-2: Reserved PostgreSQL keywords (e.g., `user`, `table`, `select`) rejected → HTTP 422.
- AC-3: Leading digits, hyphens, spaces, uppercase letters rejected.
- AC-4: Validation runs at Designer creation and again server-side before any DDL (defense in depth).

#### FR-24: Initial Table Creation

##### Story D-2: Provision New Table from Designer Schema
**As the** system, **I** `CREATE TABLE` when a Designer is bound to a Menu Item for the first time **so that** a real PostgreSQL table backs the data module.

**Acceptance Criteria:**
- AC-1: Table name = `designerId`. System columns: `id UUID PRIMARY KEY DEFAULT gen_random_uuid()`, `created_at TIMESTAMPTZ DEFAULT now()`, `created_by UUID REFERENCES users(id)`, `updated_at TIMESTAMPTZ`, `updated_by UUID REFERENCES users(id)`, `is_deleted BOOLEAN DEFAULT false`.
- AC-2: Component type → PG type mapping: TextInput/TextArea/Dropdown/ColorPicker/Image → `TEXT`; NumberInput → `NUMERIC`; DateTimePicker → `TIMESTAMPTZ`; Checkbox → `BOOLEAN`. Structural (Stack, Row, Tabs), Label, Button → no column. RepeaterField → no column (data lives in child table). Unknown/fallback → `JSONB`.
- AC-3: All generated columns are nullable (no `NOT NULL` constraints from DDL).
- AC-4: DDL runs in a transaction; any failure rolls back entirely; admin UI surfaces the error.
- AC-5: If the table already exists (edge case), system falls through to ALTER TABLE logic (idempotent).
- AC-6: Success → record written to `schema_audit_log`.
- AC-7: Provisioning is skipped entirely for VIEW-mode components — no DDL runs and no `schema_audit_log` entry is written. The provisioning path is gated on `mode = CRUD` before any DDL is constructed. See FR-54.

#### FR-25: Additive-Only Schema Migration

##### Story D-3: Evolve Schema with a New Designer Version
**As the** system, **I** `ALTER TABLE ... ADD COLUMN` for each new field in an updated Designer version **so that** existing records are never broken by schema changes.

**Acceptance Criteria:**
- AC-1: Columns present in the existing table are never dropped or renamed automatically.
- AC-2: New columns (in new version's rootElement but absent from the table) added as nullable, no default.
- AC-3: Runs in a transaction; partial completion rolls back.
- AC-4: `schema_audit_log` entry: `actorId`, `timestamp`, `designerId`, `fromVersion`, `toVersion`, `ddlOperation: "ALTER"`, `columnsAdded[]`, `columnsDiff` (JSON before/after).

#### FR-26: Schema Drift Visibility

##### Story D-4: Admin Schema Drift View
**As a** Platform Admin, **I can** view orphaned columns (in the DB but not in the current Designer schema) **so that** I can make informed decisions about manual cleanup.

**Acceptance Criteria:**
- AC-1: Admin > Designers > [Designer] > Schema Drift shows: orphaned column name, PG data type, estimated non-null row count.
- AC-2: "Drop Column" action per orphaned column; requires explicit confirmation ("This will permanently delete all data in this column").
- AC-3: Drop Column executes `ALTER TABLE {designerId} DROP COLUMN {col}` in a transaction; records to `schema_audit_log`.
- AC-4: View refreshes on demand.

#### FR-27: Repeater Child Table Provisioning

##### Story D-5: Provision Child Tables for Repeater Components
**As the** system, **I** recursively provision the child Designer's table and add a foreign-key column when a Repeater component is encountered **so that** one-to-many relationships are correctly modeled.

**Acceptance Criteria:**
- AC-1: Repeater component's `{ designerId, version }` triggers provisioning of the child table (if not already provisioned).
- AC-2: Child table gains column `parent_{parentDesignerId}_id UUID REFERENCES {parentDesignerId}(id) ON DELETE CASCADE`.
- AC-3: Index created: `CREATE INDEX IF NOT EXISTS idx_{childDesignerId}_parent ON {childDesignerId}(parent_{parentDesignerId}_id)`.
- AC-4: Cycle detection: if child schema transitively references the parent schema (circular Repeater), provisioning rejected → HTTP 422 at bind time.
- AC-5: All provisioning (parent + all children) runs in a single transaction.

#### FR-28: Schema Change Audit Log

##### Story D-6: View Schema Audit Log
**As a** Platform Admin, **I can** view the schema change audit log for any Designer **so that** I have full traceability of DDL history.

**Acceptance Criteria:**
- AC-1: GET /api/admin/designers/{designerId}/audit returns paginated log entries.
- AC-2: Each entry: `id`, `timestamp`, `actorId`, `actorName`, `designerId`, `fromVersion`, `toVersion`, `ddlOperation`, `columnsAdded`, `columnsDropped`, `notes`.
- AC-3: Append-only; no API endpoint allows deletion.

---

### Epic E — Generic CRUD Service

All endpoints served by Dapper against dynamically provisioned tables. Authorization enforced before any query.

#### FR-29: Paginated List

##### Story E-1: List Records with Pagination, Filtering, and Sorting
**As an** authorized user with `canRead`, **I can** retrieve a paginated, filtered, and sorted list of records from any dynamic table **so that** I can browse and find data efficiently.

**Acceptance Criteria:**
- AC-1: GET /api/data/{designerId}?page=1&pageSize=25&sort=created_at:desc&filter[title]=foo returns `{ data: [...], total: N, page: 1, pageSize: 25 }`.
- AC-2: `sort` accepts up to 3 `column:direction` pairs; columns whitelisted against active schema's `fieldKeys` + system columns (`id`, `created_at`, `updated_at`, `is_deleted`).
- AC-3: `filter` keys whitelisted against schema fieldKeys + system columns; values parameterized — never interpolated into SQL.
- AC-4: Response p95 < 200 ms for tables up to 100k rows with indexes on system columns.
- AC-5: Includes soft-deleted records by default (`is_deleted` filtering is the consumer's responsibility via the `filter` param).
- AC-6: Unprovisioned table → HTTP 404 `{ code: "TABLE_NOT_PROVISIONED" }`.

#### FR-30: Single Record Retrieval

##### Story E-2: Get a Single Record with Optional Children
**As an** authorized user with `canRead`, **I can** retrieve a single record by ID, optionally including its Repeater child records **so that** I can view a complete entry.

**Acceptance Criteria:**
- AC-1: GET /api/data/{designerId}/{id} returns the record or HTTP 404.
- AC-2: `?include=children` returns child table rows as `{ children: { [childDesignerId]: [...] } }`.
- AC-3: Soft-deleted records returned; response includes `is_deleted: true` so the UI can show a "Deleted" indicator.

#### FR-31: Record Creation

##### Story E-3: Create a New Record
**As an** authorized user with `canCreate`, **I can** submit a new record payload **so that** a new row is inserted into the provisioned table.

**Acceptance Criteria:**
- AC-1: POST /api/data/{designerId} validates the payload against the current Published schema's fieldKeys and types.
- AC-2: Unknown fields in the payload are ignored (not rejected).
- AC-3: System columns (`id`, `created_at`, `created_by`, `is_deleted`) set server-side; client cannot supply them.
- AC-4: Returns HTTP 201 with the created record including `id`.
- AC-5: Mutation audit log entry: `actorId`, `timestamp`, `designerId`, `recordId`, `operation: "CREATE"`, `newValues`.

#### FR-32: Partial Record Update

##### Story E-4: Update a Record (Partial)
**As an** authorized user with `canUpdate`, **I can** submit a partial payload that overwrites only the supplied fields **so that** I can correct a record without a full replace.

**Acceptance Criteria:**
- AC-1: PUT /api/data/{designerId}/{id} updates only fields present in the payload (non-null, non-undefined values).
- AC-2: `updated_at` and `updated_by` set server-side on every successful update.
- AC-3: Updating a soft-deleted record → HTTP 422 `{ code: "RECORD_DELETED" }` (restore first).
- AC-4: Mutation audit log entry: `operation: "UPDATE"`, `previousValues` (changed fields only), `newValues`.

#### FR-33: Soft Delete

##### Story E-5: Soft-Delete a Record
**As an** authorized user with `canDelete`, **I can** soft-delete a record **so that** the data is preserved and recoverable.

**Acceptance Criteria:**
- AC-1: DELETE /api/data/{designerId}/{id} sets `is_deleted = true`, `updated_at = now()`, `updated_by = requestingUserId`.
- AC-2: Returns HTTP 200 with the updated record (`is_deleted: true`).
- AC-3: Soft-delete cascades to child Repeater rows — all child rows with `parent_{parentDesignerId}_id = recordId` have `is_deleted = true` set in the same transaction as the parent soft-delete.
- AC-4: Mutation audit log entry: `operation: "SOFT_DELETE"`.

#### FR-34: View and Restore Deleted Records

##### Story E-6: View and Restore Soft-Deleted Records
**As a** Platform Admin, **I can** view soft-deleted records and restore them **so that** accidental deletions can be recovered.

**Acceptance Criteria:**
- AC-1: PUT /api/data/{designerId}/{id}/restore sets `is_deleted = false`, `updated_at = now()`, `updated_by = requestingUserId` on the parent record AND all child Repeater rows that were soft-deleted in the same cascade event — all in one transaction. Requires `platform-admin` role.
- AC-2: Admin record list view has a "Show deleted / Show all" toggle.
- AC-3: Mutation audit log entry: `operation: "RESTORE"`.

#### FR-35: Nested Repeater Write

##### Story E-7: Create/Update Parent and Child Records in One Transaction
**As a** Content Editor, **I can** submit a form with Repeater sections in a single save **so that** parent and child data land atomically.

**Acceptance Criteria:**
- AC-1: POST /api/data/{designerId} payload may include `{ children: { [childDesignerId]: [...childRecords] } }`.
- AC-2: Parent record inserted first; child records receive the parent's `id` as FK value.
- AC-3: All inserts run in a single database transaction; any failure rolls back parent and all children.
- AC-4: PUT /api/data/{designerId}/{id} may include `children`; children upserted (insert if no `id`, update if `id` present, soft-delete if omitted from the payload). All child operations run in the same transaction as the parent update.

#### FR-36: CRUD Mutation Audit Log

##### Story E-8: View CRUD Mutation Audit Log
**As a** Platform Admin, **I can** view the mutation audit log for any dynamic table **so that** I have full traceability of who changed data and when.

**Acceptance Criteria:**
- AC-1: GET /api/admin/data/{designerId}/audit returns paginated entries.
- AC-2: Each entry: `id`, `timestamp`, `actorId`, `actorName`, `designerId`, `recordId`, `operation`, `previousValues`, `newValues`.
- AC-3: Append-only; no API endpoint allows deletion.
- AC-4: Stored in `mutation_audit_log` table (EF Core-managed static schema).

---

### Epic F — UI / UX & Theming

#### FR-37: Mobile-First Responsive Layout

##### Story F-1: Responsive Layout with Collapsible Navigation
**As a** Content Editor on mobile, **I can** use all core CMS functions on my phone **so that** I'm not limited to desktop access.

**Acceptance Criteria:**
- AC-1: Single-column layout below 768 px; sidebar + content layout at 768 px and above.
- AC-2: Left navbar collapses to hamburger on mobile; tapping an item auto-closes the nav.
- AC-3: All interactive controls have touch targets ≥ 44 × 44 px.
- AC-4: No horizontal scrolling at any viewport width above 320 px.

#### FR-38: Theme Selection and Persistence

##### Story F-2: Select and Apply a Theme
**As any** user, **I can** select from three visual themes **so that** the interface suits my preference.

**Acceptance Criteria:**
- AC-1: Theme selection in user profile / settings dropdown.
- AC-2: Theme applies immediately without page reload via Tailwind CSS variables and shadcn theme switching.
- AC-3: Three themes shipped: `default-light`, `slate-dark`, `solarized`.

##### Story F-3: Server-Side Theme Persistence
**As any** user, **I can** expect my theme preference to be restored automatically on next login **so that** I don't re-select it each session.

**Acceptance Criteria:**
- AC-1: PUT /api/users/me/preferences `{ themePreference }` persists the choice.
- AC-2: Theme preference included in the user profile response on login/token refresh; applied before first render (no theme flash).

#### FR-39: Data Entry Form UI

##### Story F-4: Dynamic Data Entry View
**As a** Content Editor, **I can** open any Menu Item and see a record list and a DynamicComponent form for creating new records **so that** I can enter data into any dynamically provisioned module.

**Acceptance Criteria:**
- AC-1: Navigating to a Menu Item with a Schema Binding shows a paginated record list + "New Record" button (visible only if `canCreate`).
- AC-2: "New Record" opens a DynamicComponent form using the bound schema.
- AC-3: Clicking a record row opens it in a detail/edit view (read-only if user lacks `canUpdate`).
- AC-4: Delete button (visible only if `canDelete`) soft-deletes with a confirmation dialog.
- AC-5: If the bound component is VIEW-mode, the page renders the DynamicComponent read-only: no record list, no "New Record" button, no submit/save controls. See FR-54.

#### FR-40: Record List UI

##### Story F-5: Paginated, Filterable, Sortable Record List
**As a** Viewer, **I can** view a paginated list of records, filter by column values, and sort by multiple columns **so that** I can find data quickly.

**Acceptance Criteria:**
- AC-1: Table columns derived from bound schema's `fieldKeys`; system columns (`id`, `created_at`, `updated_at`) optionally displayable.
- AC-2: Column header clicks cycle sort direction; shift-click adds secondary sort.
- AC-3: Filter bar above the table with per-column text inputs.
- AC-4: Pagination: page size selector (10/25/50), previous/next, total count displayed.
- AC-5: Soft-deleted records shown with a visual indicator (strikethrough row or "Deleted" badge).

#### FR-41: Loading, Empty, and Error States

##### Story F-6: Explicit UI States for All Data Operations
**As any** user, **I can** always tell when the system is loading, a list is empty, or an error occurred **so that** I'm never left staring at a blank screen.

**Acceptance Criteria:**
- AC-1: All data fetching shows a skeleton loader or spinner while in-flight.
- AC-2: Empty lists show an empty-state message and a CTA if `canCreate`.
- AC-3: API errors surface as a toast (transient) for non-blocking errors; inline error banner with retry for blocking errors.
- AC-4: Form submission errors highlight offending fields inline with messages.

#### FR-42: WCAG 2.1 AA Accessibility

##### Story F-7: Accessibility Compliance
**As a** user with assistive technology, **I can** navigate and use the CMS with a keyboard and screen reader **so that** the platform is accessible to all users.

**Acceptance Criteria:**
- AC-1: All interactive controls keyboard-reachable in logical tab order.
- AC-2: All form inputs have associated labels or `aria-label`; errors linked via `aria-describedby`.
- AC-3: Color contrast ratio ≥ 4.5:1 for all body text on background (AA).
- AC-4: DnD interactions (canvas, menu reorder) have keyboard equivalents.
- AC-5: DynamicComponent-rendered forms pass axe-core automated audit with zero critical violations.

#### FR-43: Admin Settings Pages

##### Story F-8: Dedicated Admin Settings Area
**As a** Platform Admin, **I can** access a dedicated admin area containing all management pages **so that** platform configuration is separated from day-to-day data entry.

**Acceptance Criteria:**
- AC-1: Admin area accessed from a fixed "Settings" link in the navbar, visible to `platform-admin` role only.
- AC-2: Admin section pages: Users, Roles, Menus, Designers (library), Audit Logs.
- AC-3: Non-admin users attempting admin routes are redirected to their home page (client and server-side guard).

---

### Epic G — Platform / Cross-Cutting

#### FR-44: .NET Aspire AppHost

##### Story G-1: Aspire AppHost Orchestration
**As a** Developer, **I can** start all services with a single `dotnet run` from the AppHost project **so that** local development requires no manual service management.

**Acceptance Criteria:**
- AC-1: AppHost references: API project, PostgreSQL (Aspire.Hosting.PostgreSQL), MinIO (container), React frontend (NodeJs or npm serve).
- AC-2: Connection strings and service URLs injected as environment variables; no hardcoded ports in business code.
- AC-3: Aspire dashboard accessible at https://localhost:15888 showing all service health.

#### FR-45: OpenAPI and Swagger UI

##### Story G-2: Auto-Generated OpenAPI Spec
**As a** Developer or Integrator, **I can** access an up-to-date OpenAPI 3.1 spec and browse it via Swagger UI in development **so that** I can integrate without reading source code.

**Acceptance Criteria:**
- AC-1: OpenAPI spec auto-generated from Minimal API declarations; available at /openapi/v1.json.
- AC-2: Swagger UI at /swagger in development; disabled in production.
- AC-3: Dynamic CRUD endpoints documented with `additionalProperties: true` schema and a prose note explaining the runtime-dynamic response shape.
- AC-4: All endpoints document Bearer token authentication requirement.

#### FR-46: Structured Logging with Correlation IDs

##### Story G-3: Structured Logging
**As a** Developer, **I can** trace any request end-to-end through structured logs using a correlation ID **so that** debugging is tractable without guesswork.

**Acceptance Criteria:**
- AC-1: Every request assigned a correlation ID (from `X-Correlation-ID` header or generated); propagated through all log entries for that request.
- AC-2: Logs are structured JSON; each entry includes: `timestamp`, `level`, `correlationId`, `userId`, `endpoint`, `message`, `exception` (if any).
- AC-3: DDL operations and CRUD mutations logged at `Information` level with SQL fingerprint (no parameter values — data leakage prevention).

#### FR-47: Health Checks

##### Story G-4: Health Check Endpoints
**As an** operator, **I can** poll health check endpoints to confirm PostgreSQL and MinIO are reachable **so that** monitoring can alert on dependency failures.

**Acceptance Criteria:**
- AC-1: GET /health returns `{ status: "healthy"|"degraded"|"unhealthy", checks: { postgres: ..., minio: ... } }`.
- AC-2: GET /health/live → always HTTP 200 if process is running (liveness).
- AC-3: GET /health/ready → HTTP 503 if any required dependency is unreachable (readiness).

#### FR-48: Docker Compose

##### Story G-5: Docker Compose Local Stack
**As a** Developer, **I can** start the full local stack with `docker compose up` **so that** contributors without the full Aspire toolchain can still run the platform.

**Acceptance Criteria:**
- AC-1: `docker-compose.yml` defines services: `api`, `postgres`, `minio`, `frontend`.
- AC-2: EF Core migrations run automatically on API startup against the Compose-provided PostgreSQL.
- AC-3: MinIO default bucket created on first startup via an init script.
- AC-4: All service URLs resolved via Docker network service names.

#### FR-49: i18n Architecture

##### Story G-6: Externalized String Architecture
**As a** Developer, **I can** find all user-facing strings in resource files **so that** translation to additional languages is a configuration task, not a code change.

**Acceptance Criteria:**
- AC-1: React frontend uses react-i18next; all user-facing strings use `t('key')` calls; no hardcoded English strings in JSX.
- AC-2: Single `en.json` resource file contains all frontend strings.
- AC-3: API error responses include both a string key and the English message.
- AC-4: Zero functional scope for additional languages in v1; the architecture merely enables it.

---

### Epic H — Dataset Foundation & Custom Query Mode

The Dataset Manager lets permitted users define named SQL datasets that are persisted as rows in `custom_dataset` and materialized as PostgreSQL VIEWs. Epic H is shippable standalone: it delivers the schema, RBAC, full CRUD with view lifecycle, name validation, Custom Query mode, optimistic concurrency, and audit logging. Epics I–K layer the visual Query Builder on top.

All VIEW DDL in this epic is transactional with the row write so the table row and the PostgreSQL VIEW never diverge. A failed VIEW operation never corrupts an existing working VIEW.

#### FR-55: Dataset Data Model

The `custom_dataset` table is the authoritative store for Dataset definitions.

**Consequences:**
- Schema: `id UUID PK DEFAULT gen_random_uuid()`, `dataset_name TEXT UNIQUE NOT NULL`, `is_custom_query BOOLEAN NOT NULL DEFAULT true`, `query TEXT`, `builder_state JSONB`, `version INTEGER NOT NULL DEFAULT 1`, plus standard audit columns (`created_at`, `created_by`, `updated_at`, `updated_by`).
- `UNIQUE` constraint on `dataset_name` enforces uniqueness at the DB level as a second line of defense behind application-level validation.
- No changes to any existing domain tables.

---

##### Story H-1: Create custom_dataset Migration
**As a** Developer, **I can** run a database migration that creates the `custom_dataset` table and its `dataset_audit_log` table **so that** the platform has a persistent store for Dataset definitions and a complete audit trail.

**Acceptance Criteria:**
- AC-1: Migration creates `custom_dataset` with columns: `id UUID PK DEFAULT gen_random_uuid()`, `dataset_name TEXT UNIQUE NOT NULL`, `is_custom_query BOOLEAN NOT NULL DEFAULT true`, `query TEXT`, `builder_state JSONB`, `version INTEGER NOT NULL DEFAULT 1`, `created_at TIMESTAMPTZ DEFAULT now()`, `created_by UUID REFERENCES users(id)`, `updated_at TIMESTAMPTZ`, `updated_by UUID REFERENCES users(id)`.
- AC-2: Migration creates `dataset_audit_log` with columns: `id UUID PK DEFAULT gen_random_uuid()`, `timestamp TIMESTAMPTZ DEFAULT now()`, `actor_id UUID REFERENCES users(id)`, `actor_name TEXT`, `dataset_name TEXT NOT NULL`, `operation TEXT NOT NULL` (CHECK in `('CREATE','UPDATE','DELETE')`), `previous_values JSONB`, `new_values JSONB`, `ddl TEXT`.
- AC-3: Migration is idempotent.
- AC-4: Migration does not touch any existing domain tables.

#### FR-56: Dataset-Management RBAC Permission

A new `dataset-management` permission gates write access to Dataset endpoints. Read access is open to all authenticated users.

**Consequences:**
- POST /api/datasets, PUT /api/datasets/{id}, DELETE /api/datasets/{id}, and POST /api/datasets/preview require the `dataset-management` permission; unauthorized requests → HTTP 403.
- GET /api/datasets and GET /api/datasets/{id} require authentication but not `dataset-management`. `[ASSUMPTION: dataset read access is not separately gated — any authenticated user can list and view Dataset definitions]`
- `platform-admin` role has `dataset-management` by default.

---

##### Story H-2: Seed dataset-management Permission and Enforce Server-Side
**As a** Platform Admin, **I can** grant the `dataset-management` permission to a Role so that only authorized users can create, edit, and delete Datasets.

**Acceptance Criteria:**
- AC-1: `dataset-management` is seeded as a named permission in the RBAC registry.
- AC-2: POST /api/datasets, PUT /api/datasets/{id}, DELETE /api/datasets/{id} check for `dataset-management`; missing permission → HTTP 403 `{ code: "FORBIDDEN", action: "dataset-management" }`.
- AC-3: Admin > Roles permission matrix includes `dataset-management` as a toggleable permission.
- AC-4: `platform-admin` role seeded with `dataset-management` enabled.

#### FR-57: dataset_name Identifier Validation

`dataset_name` must satisfy PostgreSQL unquoted-identifier rules so it can be used verbatim as the VIEW name.

**Consequences:**
- Valid: matches `^[a-z_][a-z0-9_]*$`, ≤63 bytes.
- Reserved PostgreSQL keywords are rejected.
- Duplicate `dataset_name` → HTTP 409.
- Validation runs client-side (inline) and server-side (before any DDL) independently.

---

##### Story H-3: dataset_name Validation (Client + Server)
**As the** system, **I** validate any proposed `dataset_name` both client-side and server-side before it is used as a VIEW name **so that** invalid or unsafe identifiers are rejected before any DDL runs.

**Acceptance Criteria:**
- AC-1: Valid `dataset_name`: matches `^[a-z_][a-z0-9_]*$`, ≤63 bytes. Any other value → HTTP 422 with a descriptive message.
- AC-2: Reserved PostgreSQL keywords (validated against `pg_catalog.pg_get_keywords()` output or a curated equivalent list) → HTTP 422 identifying the keyword.
- AC-3: Duplicate `dataset_name` → HTTP 409.
- AC-4: Client-side validation fires on blur and before submit; inline error displayed before the API call is made.
- AC-5: Server-side validation runs independently; client bypass cannot produce an invalid VIEW name.
- AC-6: Validation runs again immediately before any DDL statement (defense in depth).

#### FR-58: Transactional Dataset View Lifecycle

Every Dataset row write is paired with a PostgreSQL VIEW DDL operation in a single transaction. The table row and VIEW are never allowed to diverge.

**Consequences:**
- Create: `INSERT` row + `CREATE VIEW {dataset_name} AS {query}` — one transaction.
- Edit (same name): `UPDATE` row + `CREATE OR REPLACE VIEW {dataset_name} AS {new_query}` — one transaction.
- Rename: `UPDATE` row (new `dataset_name`) + `DROP VIEW {old_name}` + `CREATE VIEW {new_name} AS {query}` — one transaction.
- Delete: `DELETE` row + `DROP VIEW IF EXISTS {dataset_name}` — one transaction.
- On any failure: full rollback; any existing working VIEW is left intact.

---

##### Story H-4: Create Dataset
**As a** user with the `dataset-management` permission, **I can** create a new Dataset by providing a `dataset_name`, authoring mode, and query **so that** a row and a backing PostgreSQL VIEW are created atomically.

**Acceptance Criteria:**
- AC-1: POST /api/datasets `{ dataset_name, is_custom_query, query? }` validates `dataset_name` (FR-57) and, if provided, `query` (SELECT-only — FR-60).
- AC-2: On success: within one transaction, (a) inserts the row with `version = 1`, (b) executes `CREATE VIEW {dataset_name} AS {query}` (a placeholder `SELECT 1 AS placeholder` is used when `query` is null or empty). If either step fails, full rollback — neither row nor VIEW is created.
- AC-3: Returns HTTP 201 with the created Dataset: `id`, `dataset_name`, `is_custom_query`, `query`, `builder_state`, `version`, `created_at`, `created_by`.
- AC-4: `CREATE VIEW` failure (e.g., invalid SQL) → HTTP 422 with the PostgreSQL error message; transaction rolled back.
- AC-5: Audit log entry: `operation: "CREATE"`, `dataset_name`, `actor_id`, `timestamp`, `ddl` (exact SQL executed).

##### Story H-5: Update Dataset (Edit & Rename)
**As a** user with the `dataset-management` permission, **I can** update an existing Dataset's `dataset_name`, `query`, or `builder_state` **so that** the row and its backing VIEW are updated atomically with rollback safety.

**Acceptance Criteria:**
- AC-1: PUT /api/datasets/{id} body: `{ dataset_name?, is_custom_query?, query?, builder_state?, version }`. `version` is required.
- AC-2: Optimistic concurrency: submitted `version` must equal the current DB `version`; mismatch → HTTP 409 `{ code: "CONFLICT", currentVersion: N }`.
- AC-3: Name change (dataset_name differs): within one transaction — (a) `DROP VIEW {old_name}`, (b) update row with new `dataset_name`, increment `version`, (c) `CREATE VIEW {new_name} AS {query}`. Failure at any step → full rollback; old VIEW remains intact.
- AC-4: Query-only change (same name): within one transaction — (a) `CREATE OR REPLACE VIEW {dataset_name} AS {new_query}`, (b) update row, increment `version`. Rollback on failure; existing VIEW not corrupted.
- AC-5: Returns HTTP 200 with updated Dataset including incremented `version`.
- AC-6: Audit log entry: `operation: "UPDATE"`, `previous_values`, `new_values`, `ddl`, `actor_id`, `timestamp`.

##### Story H-6: Delete Dataset
**As a** user with the `dataset-management` permission, **I can** delete a Dataset **so that** its row and backing VIEW are removed atomically.

**Acceptance Criteria:**
- AC-1: DELETE /api/datasets/{id}: within one transaction — (a) delete the `custom_dataset` row, (b) `DROP VIEW IF EXISTS {dataset_name}`. Failure → full rollback.
- AC-2: Returns HTTP 204 on success.
- AC-3: Non-existent ID → HTTP 404.
- AC-4: Audit log entry: `operation: "DELETE"`, `dataset_name`, `ddl: "DROP VIEW IF EXISTS {dataset_name}"`, `actor_id`, `timestamp`.

##### Story H-7: List and Get Datasets
**As an** authenticated user, **I can** list all Datasets and retrieve a single Dataset by ID **so that** I can browse and open existing Dataset definitions.

**Acceptance Criteria:**
- AC-1: GET /api/datasets returns paginated list: `{ data: [{ id, dataset_name, is_custom_query, created_at, updated_at, created_by_name }], total, page, pageSize }`. Default `pageSize: 25`.
- AC-2: GET /api/datasets/{id} returns full record including `query`, `builder_state`, `version`.
- AC-3: Non-existent ID → HTTP 404.

#### FR-59: Optimistic Concurrency on Dataset Edit

Concurrent Dataset edits are detected via an integer `version` column incremented on every successful write.

**Consequences:**
- Every PUT /api/datasets/{id} must include `version`. Mismatch → HTTP 409 so users can re-fetch and retry.
- `version` is incremented atomically in the same transaction as the VIEW DDL.

*(Implemented via Story H-5 AC-2 and AC-5 above.)*

#### FR-60: Custom Query Authoring Mode

When `is_custom_query = true`, the user writes SQL directly. The server enforces SELECT-only before any VIEW DDL.

**Consequences:**
- Top-level statement must be SELECT; DDL keywords (`CREATE`, `DROP`, `ALTER`, `TRUNCATE`) or DML keywords (`INSERT`, `UPDATE`, `DELETE`, `MERGE`) at the statement start → HTTP 422 `{ code: "INVALID_QUERY" }`.
- CTEs (`WITH … SELECT`) and subqueries are permitted as long as the top-level clause is SELECT.
- Empty `query` is permitted at creation (VIEW uses a placeholder); non-empty query must pass SELECT enforcement before view creation or replacement.

---

##### Story H-8: Custom Query Authoring Mode — SQL Textarea & SELECT Enforcement
**As a** user with `dataset-management`, **I can** write a raw SQL SELECT query in a textarea when in Custom Query Mode **so that** I can define a Dataset using hand-authored SQL, with the server rejecting any non-SELECT statements.

**Acceptance Criteria:**
- AC-1: When `is_custom_query = true`, the create/edit form shows a SQL textarea for `query`.
- AC-2: Server-side enforcement: before executing any VIEW DDL, the server inspects the submitted SQL. A DDL or DML keyword at the top-level → HTTP 422 `{ code: "INVALID_QUERY", message: "Only SELECT statements are permitted." }`. CTEs (`WITH … AS (… SELECT …) SELECT …`) are allowed.
- AC-3: Client-side: textarea provides a monospace font; submit button disabled if `query` is empty and mode is Custom Query (with a visible empty-state hint).
- AC-4: Server validation runs independently of client-side disable state (client-side is UX only; server is the authority).

#### FR-61: Dataset Audit Logging

Every Dataset CRUD operation and DDL event is recorded in `dataset_audit_log`.

---

##### Story H-9: Dataset Audit Log
**As a** Platform Admin, **I can** view the audit log for Dataset CRUD operations and DDL events **so that** I have full traceability of who created, changed, or deleted a Dataset and what DDL was executed.

**Acceptance Criteria:**
- AC-1: Every create, update, and delete writes an entry to `dataset_audit_log`: `id`, `timestamp`, `actor_id`, `actor_name`, `dataset_name`, `operation`, `previous_values`, `new_values`, `ddl` (exact SQL that ran or was attempted).
- AC-2: `ddl` is populated even on rollback (the attempted DDL is recorded, marked as rolled back in `notes` or by a separate `succeeded` boolean column).
- AC-3: Append-only; no API endpoint permits deletion.
- AC-4: GET /api/admin/datasets/audit returns paginated entries filterable by `dataset_name` and `operation`.

#### FR-62: Dataset Management UI

A dedicated Dataset Manager page provides full CRUD for Datasets, mode toggling, and audit log access.

---

##### Story H-10: Dataset Management UI
**As a** user with `dataset-management`, **I can** access a Dataset Manager page that lists Datasets and provides Create, Edit, and Delete actions **so that** I can fully manage Datasets without touching the database.

**Acceptance Criteria:**
- AC-1: Dataset Manager page accessible from the Admin area (visible to roles with `dataset-management`); lists Datasets: `dataset_name`, mode badge (Custom Query / Query Builder), `created_at`, `updated_at`.
- AC-2: "New Dataset" button opens a create modal/drawer: `dataset_name` field (validated inline per FR-57), Mode toggle (Custom Query / Query Builder). When Custom Query mode selected, SQL textarea is shown.
- AC-3: Row actions: Edit (opens modal with current values prefilled, `version` included for optimistic concurrency), Delete (confirmation dialog required before DELETE).
- AC-4: Edit modal: mode toggle available; switching from Query Builder to Custom Query populates the textarea with the current `query`; switching from Custom Query to Query Builder opens the canvas (reset or restore from `builder_state`).
- AC-5: Conflict (HTTP 409) on edit → inline error: "This dataset was modified by someone else. Reload to see the latest version."
- AC-6: "Audit" link or icon per row opens the dataset's `dataset_audit_log` entries.
- AC-7: Preview button on create/edit modal (see FR-68 / Story K-3).

---

### Epic I — Query Builder Canvas & Joins

The Query Builder replaces the SQL textarea with a visual React Flow canvas. Users drag tables, connect columns to create joins, and configure join properties. Epic I delivers the canvas foundation and join authoring; Epic J adds column/filter/order configuration; Epic K adds SQL generation and preview.

#### FR-63: Table Palette & Drag-to-Canvas

A left-side Table Palette lists server-allowlisted authorized tables; dragging a table onto the canvas creates a table node.

---

##### Story I-1: Table Palette with Allowlisted Tables
**As a** user with `dataset-management`, **I can** see the Table Palette listing allowlisted tables and drag them onto the canvas **so that** I can build a query from real database tables.

**Acceptance Criteria:**
- AC-1: Query Builder canvas shows a left-side Table Palette listing all tables in the server allowlist that the current user is authorized to use.
- AC-2: Each palette entry shows: table name and column list (name + PG data type), fetched from `information_schema.columns` at canvas load.
- AC-3: Dragging a table from the palette onto the canvas creates a table node containing: table name header, column list with checkboxes (see Epic J), left/right designation control (see I-5).
- AC-4: The same table may be dragged multiple times (for self-joins); each instance is a distinct node with a unique node ID.
- AC-5: Palette is searchable/filterable by table name.
- AC-6: Tables not in the server allowlist are absent from the palette entirely; no client-side filtering can expose them.

##### Story I-2: Multi-Table Canvas
**As a** user building a Dataset, **I can** place multiple table nodes on the React Flow canvas **so that** I can construct multi-table queries.

**Acceptance Criteria:**
- AC-1: Canvas accepts any number of table nodes; node positions are freely adjustable and persisted in `builder_state`.
- AC-2: Canvas provides zoom and pan controls.
- AC-3: Removing a table node from the canvas also removes all join edges connected to it and their join configuration from `builder_state`.
- AC-4: Removing a table node that has column selections, CASE columns, or calculated columns clears those from `builder_state` and updates the SELECT clause accordingly.

#### FR-64: Column-to-Column Join Creation

Users connect a column handle on one table node to a column handle on another to create a JOIN.

---

##### Story I-3: Column-to-Column Join Edge Creation
**As a** user building a Dataset, **I can** connect a column handle on one table node to a column handle on another to create a JOIN **so that** I can define the join predicate visually.

**Acceptance Criteria:**
- AC-1: Each column in a table node exposes a connection handle; dragging from one column handle to another creates a Join Edge between the two tables.
- AC-2: A join edge cannot connect two column handles on the same table node instance (a self-join requires two separate node instances of the same table).
- AC-3: In v1, one join edge per table-pair per combination of nodes (additional join conditions beyond the primary equality are handled in the Join Inspector or filter conditions). `[ASSUMPTION: single join edge per node-pair in v1]`
- AC-4: Join edges are rendered as styled curves with a delete control; deleting an edge removes its join configuration from `builder_state`.

#### FR-65: Join Property Inspector

Clicking a Join Edge opens a panel to configure join type and confirm joined columns.

---

##### Story I-4: Join Property Inspector
**As a** user building a Dataset, **I can** click a join edge and configure the join type in a property inspector **so that** the SQL JOIN clause is fully defined.

**Acceptance Criteria:**
- AC-1: Clicking a join edge opens a property panel/popover showing: (a) the two joined columns (table name + column name for each side), (b) a join type selector: INNER / LEFT / RIGHT / FULL OUTER.
- AC-2: Default join type is INNER on edge creation.
- AC-3: The inspector displays which side is the left table and which is the right table (derived from the left/right designation on the respective table nodes — see I-5).
- AC-4: Join type and confirmed column pair are saved to `builder_state` as edge data.

#### FR-66: Table Left/Right Designation

Each table node carries a left/right property that determines join sidedness and the FROM clause order in generated SQL.

---

##### Story I-5: Table Left/Right Designation
**As a** user building a Dataset, **I can** designate each table node as "left table" or "right table" **so that** join sidedness is explicit and the SQL generator can produce a correct FROM / JOIN clause.

**Acceptance Criteria:**
- AC-1: Each table node header has a "Left" / "Right" toggle. The SQL generator uses the left-designated table as the `FROM` anchor; all others appear as `JOIN` clauses.
- AC-2: The first table dragged onto the canvas defaults to "Left"; subsequent tables default to "Right." `[ASSUMPTION: exactly one table is designated as the primary left (FROM) table; others are JOINed to it]`
- AC-3: If no table is designated as left and more than one table is on canvas, Save and Preview are disabled with a validation message: "Designate one table as the left (FROM) table."
- AC-4: Left/right designation is stored per node in `builder_state`.

---

### Epic J — Builder Config

Epic J adds the column selection, aggregate/alias/CASE/calculated-column, filter condition, and ORDER BY panels to the Query Builder canvas built in Epic I.

#### FR-67: Select Columns Configuration

Per-table column checkboxes control the SELECT clause; each column can carry an aggregate function, a custom alias, a CASE expression, or a calculated expression.

---

##### Story J-1: Per-Table Column Selection
**As a** user building a Dataset, **I can** check and uncheck columns for each table node **so that** only the relevant columns appear in the SELECT clause.

**Acceptance Criteria:**
- AC-1: Each table node displays a column list with a checkbox per column; checked = included in SELECT.
- AC-2: All columns are unchecked by default on first drag (user opts in per column).
- AC-3: Column selections are stored per-table-node in `builder_state`.
- AC-4: If no column is checked across all table nodes, Save and Preview are blocked: "Select at least one column."

##### Story J-2: Aggregate Function and Custom Column Alias
**As a** user building a Dataset, **I can** assign an aggregate function and a custom alias to any selected column **so that** the generated SELECT clause includes the correct aggregation and naming.

**Acceptance Criteria:**
- AC-1: Each selected column row in the table node shows an "Aggregate" dropdown (None / COUNT / SUM / AVG / MIN / MAX) and an "Alias" text input.
- AC-2: SQL generator wraps aggregated columns: `SUM("table"."col") AS alias`.
- AC-3: Any aggregate set on any column causes the SQL generator to produce a `GROUP BY` clause for all non-aggregated selected columns across all table nodes.
- AC-4: Alias defaults to `{table}_{column}` (disambiguated to avoid duplicates); user may override. Alias must satisfy the same identifier rules as `dataset_name` (FR-57): validated inline.
- AC-5: Aggregate and alias settings stored per-column in `builder_state`.

##### Story J-3: Add Case (CASE/WHEN Derived Column)
**As a** user building a Dataset, **I can** add a CASE/WHEN expression as a derived column on any table node **so that** conditional logic can be expressed in the SELECT clause without writing raw SQL.

**Acceptance Criteria:**
- AC-1: Each table node has an "Add Case" button. Clicking it adds a derived column row with a CASE/WHEN builder: one or more WHEN conditions (column + operator + value) and THEN values, plus an optional ELSE value.
- AC-2: WHEN condition uses the same operator vocabulary as the Filter Conditions dialog (FR-70, Story J-6).
- AC-3: THEN and ELSE values can be string literals, numeric literals, or column references from any table on canvas.
- AC-4: A custom alias is required for every CASE column; the alias follows FR-57 identifier rules. Save is blocked if alias is empty.
- AC-5: CASE definition stored in `builder_state`; SQL generator produces a syntactically valid CASE expression.

##### Story J-4: Add Calculated Column (Expression)
**As a** user building a Dataset, **I can** add a free-form SQL expression as a calculated column on any table node **so that** arithmetic or string operations can appear in the SELECT clause.

**Acceptance Criteria:**
- AC-1: Each table node has an "Add Calculated Column" button; clicking it adds a row with a free-form expression textarea and an alias field.
- AC-2: Expression must be non-empty. The server applies the conservative DDL/DML keyword check (same as FR-60 H-8 AC-2) to the expression before including it in the generated SELECT.
- AC-3: A custom alias is required; alias follows FR-57 identifier rules. Save is blocked if alias is empty.
- AC-4: SQL generator inserts: `({expression}) AS {alias}` in the SELECT clause.
- AC-5: Calculated column definition stored in `builder_state`. `[ASSUMPTION: calculated expressions are raw SQL fragments; injection risk is mitigated by SELECT-only enforcement of the final assembled query and by table/column allowlist validation — per-expression sandboxing is out of scope v1]`

#### FR-68: Filter Conditions Dialog

A WHERE clause builder accessible from the canvas toolbar, supporting AND/OR combinators, arbitrarily nested groups, and operator-adapted value inputs with parameterized values.

---

##### Story J-5: Filter Conditions Dialog
**As a** user building a Dataset, **I can** open a Filter Conditions dialog that lets me define a WHERE clause with AND/OR combinators and groups **so that** the query result is filtered without writing SQL.

**Acceptance Criteria:**
- AC-1: A "Filter" button on the canvas toolbar opens a modal dialog showing the current WHERE clause builder.
- AC-2: The dialog has a root combinator (AND / OR selector) and a list of top-level conditions and groups.
- AC-3: An "Add" button in the dialog opens a popover offering two options: "Add condition" and "Add group" (creates a nested sub-clause with its own AND/OR combinator).
- AC-4: Groups render with visible parenthesis indicators and can contain conditions and/or further nested groups (arbitrary depth).
- AC-5: Conditions and groups can be reordered (drag) and deleted.
- AC-6: Filter state stored in `builder_state`; dialog re-opens to the same state.

##### Story J-6: Filter Condition Definition
**As a** user building a Dataset, **I can** define each filter condition as a table + column + operator + value expression **so that** the WHERE clause is precise and the value input adapts to the operator.

**Acceptance Criteria:**
- AC-1: Each condition row: (a) table selector (tables on canvas), (b) column selector (selected table's columns), (c) operator selector, (d) value input.
- AC-2: Operators: `=`, `!=`, `<`, `<=`, `>`, `>=`, `IS NULL`, `IS NOT NULL`, `LIKE`, `ILIKE`, `IN`, `NOT IN`, `BETWEEN`.
- AC-3: Value input adapts: `IS NULL` / `IS NOT NULL` → no value; `IN` / `NOT IN` → multi-value tag input; `BETWEEN` → two inputs (from / to); all others → single input.
- AC-4: Input type adapts to column PG type: `NUMERIC` columns → number input; `TIMESTAMPTZ` → date/time picker; `BOOLEAN` → checkbox; `TEXT` → text input.
- AC-5: Values stored as typed parameters in `builder_state`; SQL generator emits parameterized placeholders (`$1`, `$2`, …) with a separate parameters array — filter values are never interpolated into SQL strings.

##### Story J-7: Nested Filter Groups
**As a** user building a Dataset, **I can** nest filter groups to arbitrary depth **so that** complex boolean logic can be expressed visually.

**Acceptance Criteria:**
- AC-1: Each group can contain conditions and/or sub-groups; sub-groups have their own combinator.
- AC-2: UI renders nesting clearly with indentation and/or color differentiation per depth level.
- AC-3: SQL generator produces correctly parenthesized SQL from the recursive group tree: `(A AND B) OR (C AND D)` rendered as `((A AND B) OR (C AND D))`.
- AC-4: No artificial depth limit enforced by the application in v1.

#### FR-69: Order By Configuration

---

##### Story J-8: Order By Panel
**As a** user building a Dataset, **I can** define ORDER BY clauses as table + column + direction **so that** the dataset results are sorted in a predictable order.

**Acceptance Criteria:**
- AC-1: An "Order By" panel (canvas toolbar or dedicated sidebar tab) lists ORDER BY clauses.
- AC-2: Each clause: table selector (tables on canvas) + column selector + direction toggle (ASC / DESC).
- AC-3: Clauses are ordered (first = primary sort); user can reorder via drag; clause order maps directly to SQL ORDER BY precedence.
- AC-4: Order By state stored in `builder_state`; SQL generator emits `ORDER BY {clauses}` in declared order.
- AC-5: Empty Order By is valid; no ORDER BY clause is emitted in generated SQL.

---

### Epic K — SQL Generation, Preview & Builder View Sync

Epic K closes the loop: the server generates authoritative SQL from `builder_state`, builder-mode saves reuse the Epic H view lifecycle, preview executes with hard LIMIT 10 and a statement timeout, and `builder_state` is persisted and restored faithfully.

#### FR-70: Server-Authoritative SQL Generation

The server generates the SQL query from `builder_state`; the client never sends a raw SQL string in Query Builder Mode.

---

##### Story K-1: Server-Authoritative SQL Generation from builder_state
**As the** system, **I** re-derive the SQL SELECT statement from `builder_state` on the server before any VIEW DDL or preview execution **so that** the client cannot bypass Query Builder security constraints with a hand-crafted SQL string.

**Acceptance Criteria:**
- AC-1: The server SQL generator accepts `builder_state` JSON and produces a syntactically valid SQL SELECT statement.
- AC-2: Generator produces: FROM clause from the left-designated table, JOIN clauses for each join edge (with configured type and ON condition), SELECT list (checked columns, aggregates, aliases, CASE expressions, calculated columns), GROUP BY (auto-derived from aggregate presence), WHERE clause from filter conditions (parameterized), ORDER BY from Order By clauses.
- AC-3: All table and column identifiers are double-quoted (`"table_name"."column_name"`) to handle reserved words and preserve case.
- AC-4: Filter values emitted as parameterized placeholders (`$1`, `$2`, …) alongside a parameters array — never interpolated.
- AC-5: Generated SQL is validated as SELECT-only (FR-60 enforcement) as a final check before DDL or preview.
- AC-6: If `builder_state` is incomplete (no left table, no columns selected), the generator returns a validation error list → HTTP 422 before any DDL runs.
- AC-7: The generated SQL is stored in `custom_dataset.query` on every save; `builder_state` and `query` are always in sync after a successful save.

#### FR-71: builder_state Persistence & Restore

The full canvas state is persisted on every save and restored exactly on reopen.

---

##### Story K-2: builder_state Persistence and Restore
**As a** user in Query Builder Mode, **I can** save my canvas and reopen the same Dataset later to find my canvas exactly as I left it **so that** work is not lost between sessions.

**Acceptance Criteria:**
- AC-1: On PUT /api/datasets/{id} with `is_custom_query = false`, the full `builder_state` JSON (nodes with positions and all configuration, edges, column selections, filters, order clauses) is persisted to `custom_dataset.builder_state`.
- AC-2: On opening a Dataset in the Query Builder, the frontend restores the canvas from `builder_state`: table nodes at saved positions, edges drawn, column checkboxes checked, filter dialog state loaded, order clauses loaded.
- AC-3: If `builder_state` is null, the canvas opens empty.
- AC-4: `custom_dataset.query` always holds the last server-generated SQL; `builder_state` holds the raw canvas state. Both are updated in the same transaction on every save (FR-58).

#### FR-72: Query Preview (Hard LIMIT 10)

Preview executes the current query with LIMIT 10 and a server-side statement timeout; results are shown in a table up to 10 rows.

---

##### Story K-3: Query Preview (LIMIT 10)
**As a** user creating or editing a Dataset, **I can** preview the query result (up to 10 rows) before saving **so that** I can validate correctness before the VIEW is persisted.

**Acceptance Criteria:**
- AC-1: A "Preview" button is available on the create/edit form in both Custom Query Mode and Query Builder Mode.
- AC-2: Query Builder Mode: Preview sends `builder_state` to the server; server generates SQL (K-1), appends `LIMIT 10`, applies a statement timeout (configurable via environment variable; default 5 s via `SET LOCAL statement_timeout`), and executes against PostgreSQL using the allowlisted/read-only connection context.
- AC-3: Custom Query Mode: Preview sends `query`; server validates SELECT-only, appends `LIMIT 10`, applies statement timeout, executes.
- AC-4: Results display as a table: column names as headers, up to 10 data rows.
- AC-5: Statement timeout exceeded → HTTP 408 with message "Preview query exceeded the time limit. Simplify the query or add filters."; user can adjust and retry.
- AC-6: PostgreSQL error → HTTP 422 with the PostgreSQL error message surfaced to the user (no internal stack traces).
- AC-7: Preview is a read-only operation; it does not save or create the Dataset.
- AC-8: Preview requires `dataset-management` permission (same gate as create/edit). `[ASSUMPTION: preview execution uses a PostgreSQL connection limited to SELECT against allowlisted tables, enforced at the DB level by a read-only role or row-security policy]`

#### FR-73: Builder-Mode View Lifecycle Integration

When a Dataset in Query Builder Mode is saved, the server generates SQL from `builder_state` and reuses the Epic H transactional view lifecycle (FR-58) — same atomicity and rollback guarantees as Custom Query Mode.

---

##### Story K-4: Builder-Mode View Lifecycle Integration
**As the** system, **I** generate SQL from `builder_state` and apply the Epic H transactional view lifecycle on every builder-mode save **so that** Query Builder saves have identical atomicity and rollback safety to Custom Query saves.

**Acceptance Criteria:**
- AC-1: On PUT /api/datasets/{id} with `is_custom_query = false`, the server generates SQL from `builder_state` (K-1) before any DDL; if generation fails, HTTP 422 is returned before any DDL executes.
- AC-2: The generated SQL is stored in `custom_dataset.query` and used as the VIEW body; the same transactional lifecycle as FR-58 applies (same-name → CREATE OR REPLACE; rename → DROP + CREATE; rollback on failure).
- AC-3: A failed VIEW operation never corrupts an existing working VIEW.
- AC-4: Audit log `ddl` field contains the server-generated SQL, marked as "Builder-generated."

---

## 5. Non-Goals (v1)

- Public-facing or anonymous content delivery — all access requires authentication.
- Real-time collaborative editing on the designer canvas.
- Workflow or approval engine on data records.
- Native mobile apps (responsive web only).
- Plugin or extension marketplace.
- Admin impersonation ("View as user" — not in v1; admin creates a separate test account to verify permission configurations).
- Self-service user registration (without admin involvement).
- Keyset / cursor-based pagination (offset only in v1).
- Full-text search (column-level filtering only).
- Bulk import / export per table.
- Multi-tenant deployment.
- **Dataset Manager (Epics H–K):**
  - Datasets are not directly bindable to Menu Items as data sources in v1 (they are a separate subsystem from the Designer-based menu binding).
  - Query Builder does not support window functions (`OVER`, `PARTITION BY`, `ROW_NUMBER`) in v1.
  - No scheduling, caching, or materialization of Dataset VIEWs — VIEWs are computed at query time.
  - No export of generated SQL to a file or clipboard in v1.
  - No Dataset versioning in v1 — save overwrites the current definition in place (optimistic concurrency via `version` integer prevents blind overwrites, but prior query text is not snapshotted).
  - Table allowlist is server-side configured (not managed via the admin UI in v1); see OQ-11.
  - No per-user table access control within the allowlist in v1 — all `dataset-management` users share the same allowlisted table set.

---

## 6. MVP Scope

### 6.1 In Scope
- JWT authentication (login, refresh, logout)
- Platform Admin: user and role management, user-role assignment
- Welcome email on user creation (FR-50)
- Forgot-password self-service flow (FR-51)
- Authenticated password change (FR-52)
- TOTP-based MFA with QR enrolment and backup codes (FR-53)
- Component Schema Designer (ported from ESG Platform) — all 14 component types
- Component mode (CRUD / VIEW) — VIEW components render read-only with no backing table (FR-54)
- Schema versioning (Draft → Published → Archived lifecycle)
- Menu management (2 levels, schema binding, role assignment, ordering, icon)
- Dynamic table provisioning (CREATE + ALTER, Repeater FK, schema drift view)
- Generic CRUD API (list, get, create, partial update, soft-delete, restore, nested Repeater)
- Schema change and CRUD mutation audit logs
- DynamicComponent data-entry and record-list UIs
- 3 selectable themes persisted server-side
- Mobile-first responsive layout with collapsible nav
- WCAG 2.1 AA compliance
- .NET Aspire AppHost + Docker Compose
- OpenAPI + Swagger UI (dev only)
- Structured logging with correlation IDs
- Health checks (PostgreSQL, MinIO)
- i18n architecture (English-only)

- Dataset Manager (Epic H) — `custom_dataset` table, `dataset-management` RBAC, Dataset CRUD with full transactional view lifecycle, `dataset_name` validation, Custom Query Mode with SELECT enforcement, optimistic concurrency, audit logging, Dataset Management UI
- Dataset Query Builder — Canvas & Joins (Epic I): React Flow canvas, Table Palette with allowlisted tables, multi-table drag, column-to-column join creation, Join Inspector, left/right table designation
- Dataset Query Builder — Config (Epic J): per-table column selection, aggregate functions, column aliases, CASE derived columns, calculated columns, Filter Conditions dialog with arbitrarily nested groups, parameterized filter values, ORDER BY panel
- Dataset Query Builder — SQL Generation, Preview & View Sync (Epic K): server-authoritative SQL generation from `builder_state`, `builder_state` persistence and restore, Query Preview with hard LIMIT 10 and statement timeout, builder-mode view lifecycle integration

### 6.2 Out of Scope for MVP
- Self-service registration
- Keyset pagination — deferred to v2; offset sufficient at 100k rows with indexes
- Full-text search — deferred to v2
- Bulk import/export — deferred to v2
- Real-time collaboration, native apps, multi-tenant — not planned
- Dataset Menu Binding (Datasets as Menu Item data sources) — deferred post-MVP
- Dataset Versioning / snapshot history — deferred to v2
- Admin UI for table allowlist management — v1 is server-configured
- Query Builder window functions — deferred to v2

---

## 7. Cross-Cutting NFRs

### Performance
- GET /api/data/{designerId} p95 < 200 ms at 100k rows with indexes on `id`, `created_at`, `is_deleted`, and common filter columns.
- Designer version save < 500 ms p95.
- Navbar menu fetch < 100 ms p95 (cached, TTL 5 s, invalidated on write).

### Security
- All endpoints require a valid JWT; unauthenticated requests → HTTP 401.
- Access token stored in-memory on the client (not in localStorage or cookies). Lost on page reload; silent re-auth via a refresh token stored in an HttpOnly cookie restores the session automatically.
- CSRF: not applicable for the access token (in-memory, no cookie). The refresh token cookie uses `SameSite=Strict` + `HttpOnly` to prevent CSRF on the /api/auth/refresh endpoint.
- SQL injection prevention: Dapper queries parameterize all user-supplied values; dynamic identifiers (table names, column names) validated against a server-side schema registry whitelist before use in SQL; raw user input never interpolated into identifiers.
- File uploads validated for type and size before MinIO storage.
- Secrets (JWT signing key, DB connection string, MinIO credentials) injected as environment variables; never committed to source control.
- Password reset tokens stored as SHA-256 hashes only; raw token never persisted or logged. Tokens expire after 1 hour and are single-use.
- TOTP secrets stored encrypted at rest (AES-256 or `IDataProtector`); the plaintext secret is never returned to the client after enrolment completes.
- MFA login uses an intermediate `mfaSessionToken` (5-minute TTL, single-use); no access or refresh token is issued until the TOTP challenge is passed.
- Backup codes stored as bcrypt hashes; shown to the user only once at enrolment.
- Email transport requires TLS (STARTTLS or SMTPS); plaintext SMTP connections are not used in production.

### Auditability
- Schema change audit log: every `CREATE TABLE`, `ALTER TABLE`, `DROP COLUMN` recorded with actor, timestamp, and diff.
- CRUD mutation audit log: every create, update, soft-delete, restore recorded with actor, timestamp, and field-level diff.
- Logs append-only; no API endpoint permits deletion of audit records.

### Reliability
- All DDL operations run in explicit PostgreSQL transactions with full rollback on failure.
- Backup strategy documented in operational runbook (not enforced by the application).
- API process restarts handled by Aspire/Docker orchestrator restart policy.

### Browser Support
- Latest 2 versions of Chrome, Edge, Firefox, Safari at time of release.

### i18n
- Architecture-ready (externalized strings); English-only at launch.

### Dataset Manager Security (Cross-Cutting)
- **Identifier quoting:** All table and column identifiers in Dataset-generated SQL are double-quoted (`"table"."column"`). No identifier is interpolated as a raw string; all pass through the quoting layer.
- **Parameterized filter values:** Filter condition values from the Query Builder are always emitted as parameterized placeholders (`$1`, `$2`, …) with a separate parameters array. Filter values are never concatenated into the SQL string.
- **SELECT-only enforcement:** Server validates that the final assembled SQL (Custom Query or Builder-generated) has a SELECT as the top-level statement before any VIEW DDL or preview execution. DDL/DML keywords at statement start → HTTP 422.
- **Table allowlist:** Server enforces the table allowlist on all references in `builder_state` before SQL generation. Tables absent from the allowlist cannot appear in any generated query regardless of client input.
- **Transactional DDL:** All Dataset view DDL (CREATE, CREATE OR REPLACE, DROP) runs inside the same PostgreSQL transaction as the row write. A failed VIEW operation rolls back the entire transaction; an existing working VIEW is never left in a corrupted state.
- **Optimistic concurrency:** The `version` integer on `custom_dataset` prevents silent overwrites by concurrent editors.

---

## 8. Success Metrics

**Primary**
- **SM-1:** A Platform Admin completes the full end-to-end flow (designer → menu binding → live CRUD UI) in under 30 minutes without developer assistance. Validates FR-9 through FR-36 chain.
- **SM-2:** GET /api/data/{designerId} p95 latency < 200 ms at 100k rows with indexes. Validates FR-29.
- **SM-3:** Zero data loss incidents caused by schema version changes over the first 6 months of operation. Validates FR-25.

**Secondary**
- **SM-4:** Content Editors report ≥ 4/5 average satisfaction on form usability (quarterly survey). Validates FR-39 through FR-41.
- **SM-5:** Platform Admins manage user roles without developer support 100% of the time. Validates FR-1 through FR-7.

**Counter-metrics (do not optimize)**
- **SM-C1:** Time-to-deploy (SM-1) must not be reduced by removing validation steps or schema review — speed must not come at the cost of correctness.
- **SM-C2:** p95 latency (SM-2) must not be achieved by removing audit logging or bypassing permission checks.

---

## 9. Story Dependency Graph & Recommended Sequencing

### Dependency Map

Hard dependencies unless marked *(soft)*.

```
G-1 (Aspire AppHost)         ──► All other stories
G-5 (Docker Compose)         ──► Developer environment (parallel to G-1)
G-3 (Structured Logging)     ──► All *(soft — add early, cross-cutting)*
G-6 (i18n Architecture)      ──► All *(soft — add early, cross-cutting)*

A-1 (JWT Login)              ──► A-2, A-3, A-4, A-5, A-6, A-7, A-8, A-9
A-4 (Role CRUD)              ──► A-5, A-6
A-5 (User-Role Assignment)   ──► A-6
A-6 (Permission Enforcement) ──► A-7, all /api/data/* stories
A-8 (Admin User Mgmt UI)     ──► A-10 (adds email dispatch to user creation)
A-1 (JWT Login)              ──► A-11 (forgot-password shares auth context)
A-1 (JWT Login)              ──► A-12 (password change requires authentication)
A-1 (JWT Login)              ──► A-13 (MFA enrolment requires authentication)
A-13 (TOTP MFA Enrolment)    ──► A-14 (MFA verification gate requires enrolment)
A-8 (Admin User Mgmt UI)     ──► A-15 (admin MFA reset is a UI extension of user management)

B-1 (Port Designer)          ──► B-2, B-3, B-4, B-5, B-6, B-7, B-8, B-9, D-1
B-2 (Create Designer)        ──► B-10 (mode declared at creation)
B-10 (Component Mode)        ──► C-3, D-2 (mode gates provisioning behavior)
B-6 (Save Version)           ──► B-7, C-3
B-7 (Version Status)         ──► C-3 (only Published versions bindable)

D-1 (Identifier Validation)  ──► D-2, D-3, D-5
D-2 (Create Table)           ──► D-3, D-4, D-5, E-1 through E-8
D-5 (Repeater Tables)        ──► E-7

C-1 (Create Menu Item)       ──► C-2, C-3, C-4, C-5, C-6, C-7, C-8
C-3 (Schema Binding)         ──► D-2 (triggers provisioning)

A-6 + D-2                   ──► E-1, E-2, E-3, E-4, E-5, E-6, E-7, E-8
E-1 + E-3 + B-9 + C-8       ──► F-4, F-5

G-4 (Health Checks)          ──► Deployment readiness
G-2 (OpenAPI)                ──► Integrator use (parallel to G-1)

H-1 (custom_dataset Migration)  ──► H-2, H-3, H-4, H-5, H-6, H-7, H-8, H-9, H-10
A-4 (Role CRUD)                  ──► H-2 (dataset-management permission added to role model)
H-4 + H-5 + H-6 (View Lifecycle) ──► H-10 (UI drives CRUD APIs)
H-8 (Custom Query Mode)          ──► H-10 (textarea shown in modal)

H-1 + H-2                   ──► I-1, I-2, I-3, I-4, I-5 (canvas requires migration and RBAC)
I-1 (Table Palette)          ──► I-2, I-3
I-2 (Multi-Table Canvas)     ──► I-3, I-4, I-5
I-3 (Join Edges)             ──► I-4 (inspector requires edges)

I-1 + I-2 + I-3             ──► J-1, J-2, J-3, J-4, J-5, J-6, J-7, J-8 (config requires canvas)
J-1 (Column Selection)       ──► J-2 (aggregate/alias is per-selected-column)
J-5 (Filter Dialog)          ──► J-6, J-7 (conditions live inside the dialog)

J-1 + J-2 + J-3 + J-4 + J-5 + J-6 + J-7 + J-8 ──► K-1 (generator consumes all builder config)
K-1 (SQL Generator)          ──► K-2, K-3, K-4
H-4 + H-5 + H-6 (View Lifecycle) ──► K-4 (builder save reuses lifecycle)
```

### Recommended Sprint Sequencing

| Sprint | Stories | Exit Criteria |
|--------|---------|---------------|
| **S0 — Infrastructure** | G-1, G-5, G-2, G-3, G-4 | `dotnet run` starts all services; /health healthy; Swagger UI accessible; structured logs visible |
| **S1 — Auth** | A-1, A-2, A-3, A-4, A-5, A-6, A-7, A-8, A-9 | Login/logout work; roles assignable; permission enforcement blocks unauthorized requests; admin UI functional |
| **S1b — Account Security** | A-10, A-11, A-12, A-13, A-14, A-15 | Welcome email dispatched on user creation; forgot-password flow works end-to-end; authenticated password change works; TOTP MFA can be enrolled and verified at login; admin can reset MFA |
| **S2 — Designer Port** | B-1 | Ported designer renders in new project; all 14 component types drag onto canvas; DynamicComponent renders forms; no ESG dependencies remain |
| **S3 — Designer Features** | B-2, B-3, B-4, B-5, B-6, B-7, B-8, B-9, B-10, D-1 | Full designer workflow: create → design → preview → save → publish; mode set at creation; library page shows versions with lifecycle management |
| **S4 — Menu** | C-1, C-2, C-4, C-5, C-6, C-7, C-8 | Menus created, reordered, role-assigned; navbar renders permission-filtered; icon upload works |
| **S5 — Table Provisioning** | C-3, D-2, D-3, D-4, D-5, D-6 | Binding a schema provisions the table; new version migrates additively; Repeater FK provisioned; schema drift view shows orphaned columns; audit log captures all DDL |
| **S6 — CRUD API** | E-1, E-2, E-3, E-4, E-5, E-6, E-7, E-8 | All CRUD endpoints functional and permission-gated; soft-delete and restore work; nested Repeater writes succeed; mutation audit log records all operations |
| **S7 — Data Entry UI** | F-4, F-5, F-6 | Content Editor can create/edit/delete records via DynamicComponent form; record list is paginated, filterable, sortable; all states (loading/empty/error) designed |
| **S8 — Polish & Cross-Cutting** | F-1, F-2, F-3, F-7, F-8, G-6 | Mobile layout correct on 320–768 px; themes apply and persist; WCAG 2.1 AA audit passes; admin settings pages complete; i18n strings externalized |
| **S9 — Dataset Foundation** | H-1, H-2, H-3, H-4, H-5, H-6, H-7, H-8, H-9, H-10 | `custom_dataset` migration run; dataset-management permission enforced; full CRUD API with transactional view lifecycle; name validation end-to-end; Custom Query Mode with SELECT enforcement; Dataset Management UI functional; audit log populated |
| **S10 — Query Builder Canvas** | I-1, I-2, I-3, I-4, I-5 | Table Palette shows allowlisted tables; multi-table drag-to-canvas works; column-to-column join edges created; Join Inspector configures type; left/right designation controls FROM anchor |
| **S11 — Builder Config** | J-1, J-2, J-3, J-4, J-5, J-6, J-7, J-8 | Column checkboxes control SELECT; aggregates + aliases correct; CASE and calculated columns generate valid SQL; Filter dialog with nested groups; parameterized values confirmed; ORDER BY clauses in declared order |
| **S12 — SQL Gen, Preview & Sync** | K-1, K-2, K-3, K-4 | Server generator produces correct SQL from builder_state; canvas restores exactly on reopen; Preview returns ≤10 rows with LIMIT 10 and times out gracefully; builder-mode save reuses transactional view lifecycle |

---

## 10. Architect Handoff

The following decisions carry significant implementation consequences. Each is listed with the PRD's assumed position and the open question for the architect to resolve before detailed design begins.

### AD-1: JWT Token Storage and Session Recovery
**PRD position:** Access token in-memory (15-min TTL); refresh token in HttpOnly `SameSite=Strict` cookie (7-day TTL, single-use rotation, stored server-side in `refresh_tokens` table). **Resolved: in-memory access token confirmed.**
**Resolve (remaining):** Define the silent re-auth flow on page reload — the client should call /api/auth/refresh on startup using the HttpOnly cookie before rendering any protected route. If the refresh token is expired or revoked, redirect to login. Define the exact token revocation path when a user is deactivated mid-session: refresh token invalidated immediately on deactivation; access token valid until its 15-min TTL expires (acceptable given the short TTL).

### AD-2: Permission Validation Source and Cache
**PRD position:** Server validates active role set from DB on each request; cache ≤ 30 s; invalidated on role assignment change.
**Resolve:** Define the cache key (e.g., `userId` → `{ [designerId]: EffectivePermission }`). Define the invalidation event (admin writes a role assignment → publishes an in-process or Redis event → cache busted for that `userId`). If Redis is not in scope for v1, define the fallback (per-request DB hit, or stale JWT claims accepted with a known deactivation lag).

### AD-3: PostgreSQL Identifier Sanitization
**PRD position:** `designerId` must be lowercase snake_case, 1–63 chars, no reserved keywords, no leading digits. Same rules apply to `fieldKey` column names.
**Resolve:** (a) Exact reserved keyword list (pg_catalog.pg_keywords or a curated subset?). (b) Case folding: uppercase rejected at input, or silently lowercased? (c) Server-side "schema registry" cache structure: `{ [designerId]: string[] }` (array of valid column names) keyed from published Designer schemas, used to whitelist dynamic WHERE/ORDER BY identifiers. Define cache population (on Designer publish) and eviction.

### AD-4: Complete Component Type → PG Type Mapping
**PRD position:** TextInput/TextArea/Dropdown/ColorPicker/Image → `TEXT`; NumberInput → `NUMERIC`; DateTimePicker → `TIMESTAMPTZ`; Checkbox → `BOOLEAN`; structural/Label/Button/RepeaterField → no column; unknown → `JSONB`.
**Resolve:** (a) Confirm `NUMERIC` vs `FLOAT8` for NumberInput (NUMERIC avoids floating-point errors but is slower for arithmetic). (b) Confirm Dropdown uses `TEXT` (not a PG enum — enums are migration-hostile). (c) Explicitly map all 14 component types before DDL code is written. Publish the complete mapping as an architectural decision record.

### AD-5: Soft-Delete Cascade for Repeater Children
**PRD position:** Soft-delete cascades to children (application-level: parent soft-delete updates all child rows `is_deleted = true` in the same transaction). Restore also cascades. **Resolved: cascade confirmed.**
**Resolve (remaining):** Define the cascade depth — if a child table itself has a Repeater referencing a grandchild table, does the cascade continue transitively? Recommend: yes, full transitive cascade to maintain consistency. Implement as a recursive helper that walks the Repeater reference graph and soft-deletes all descendant rows in a single transaction.

### AD-6: Version Re-Bind Trigger
**PRD position:** Menu bindings pin to `{ designerId, version }`; updating to a new version is an explicit admin action.
**Resolve:** Recommend: an explicit "Update binding" action in the Menu editor that shows a column diff preview before applying. The diff preview requires the architect to define the diff algorithm (compare current rootElement's fieldKeys against the live table's column list). Define whether the ALTER TABLE migration runs synchronously (blocks the admin UI) or asynchronously with a progress indicator.

### AD-7: Schema Registry Cache
**Resolve:** The dynamic CRUD service needs the active column set for each `designerId` to whitelist filter/sort columns and validate payloads. Recommend: in-memory cache keyed by `(designerId, publishedVersion)` — populated lazily on first CRUD request, invalidated when a Designer version is promoted to Published. Define the cache struct (at minimum: `{ columnName: string, pgType: string, componentType: string }[]`). Define the cache eviction policy (LRU, size limit, TTL fallback).

### AD-8: OpenAPI for Dynamic Endpoints
**PRD position:** Dynamic CRUD endpoints documented as `object` with `additionalProperties: true` in v1.
**Resolve:** Confirm this is acceptable for the Developer / Integrator persona. If per-Designer generated schemas are needed sooner (e.g., for SDK generation), define the `/openapi/{designerId}.json` endpoint and its relationship to the main spec.

### AD-9: MinIO File Access Pattern
**Resolve:** When an Image field contains a MinIO object key (`TEXT`), how does the client display it? Recommended: API returns a presigned URL (5-min TTL) inline in the CRUD response. Define caching behavior (can the client cache the presigned URL? How does it refresh on expiry?). Define whether presigned URL generation happens for all `TEXT` columns or only those whose `componentType` is `Image` (requires schema registry awareness in the serializer).

### AD-10: Audit Log Volume and Retention
**Resolve:** `mutation_audit_log` can grow large in active deployments. Define: (a) retention policy (90-day rolling, or unlimited); (b) indexes: at minimum `(designer_id, created_at)` and `(record_id)` on `mutation_audit_log`; (c) whether to use table partitioning by month if volume is expected to be high; (d) confirm both audit tables are EF Core-managed (preferred for migration consistency).

### AD-11: Dapper + EF Core Transaction Boundary
**Resolve:** When a Schema Binding save (EF Core) triggers table provisioning (Dapper DDL), both must either run in the same transaction or accept that the Menu Item can be saved while provisioning fails (the PRD allows this — admin retries). If they run in the same transaction, define how the EF Core `DbContext` connection is shared with the Dapper `DbConnection`. If separate, define the retry mechanism and the "provisioning failed" state model on the Menu Item record.

### AD-12: Email Transport and Template Strategy
**PRD position:** Asynchronous email dispatch (fire-and-forget) via SMTP. Transport configured via environment variables; Mailpit used as local-dev SMTP catcher. TLS required in production.
**Resolve:** (a) Choose the .NET email library — MailKit is the de facto standard; FluentEmail wraps it with a higher-level API. (b) Define the email template strategy — plain Razor templates or a logic-free templating library (Fluid, Scriban). (c) Define SMTP connection pooling and retry behaviour under transient failures (exponential back-off, max 3 retries). (d) Confirm whether .NET Aspire AppHost should wire up a Mailpit container as a named resource for local dev. (e) Decide whether sent-email records (timestamp, recipient, template, success/failure) are audited in the DB.

### AD-13: TOTP Secret and Backup Code Storage
**PRD position:** TOTP secrets stored encrypted at rest; backup codes stored as bcrypt hashes. `mfa_sessions` table holds intermediate MFA session tokens with a 5-minute TTL.
**Resolve:** (a) Choose the TOTP library — `Otp.NET` (NuGet) is RFC 6238-compliant and actively maintained. (b) Define the encryption scheme for `mfaSecret`: AES-256-GCM with a key from an environment variable / secrets manager, or EF Core's `IDataProtector` (simpler, managed key rotation). (c) Confirm the column type (`bytea` or base64 `text`) and EF Core mapping for the encrypted secret. (d) Define the `mfa_sessions` table schema and an automated cleanup job for expired rows. (e) Confirm whether the backup code count (8) and length (8 characters) are final, or configurable.

### AD-14: Table Allowlist Management
**PRD position:** The Table Allowlist is server-side configured; tables absent from the list cannot be referenced in any Dataset query regardless of client input. Management via admin UI is out of scope for v1.
**Resolve:** (a) Define the allowlist storage mechanism — static JSON/YAML config file checked into source, an environment variable (comma-separated), or a `dataset_allowed_tables` DB table managed by a future admin UI. (b) Confirm the allowlist granularity: table-level only in v1, or can specific columns be excluded per table? (c) Define how the Query Builder Table Palette is populated — directly from the allowlist plus `information_schema.columns`, or via a dedicated `/api/datasets/allowlist` endpoint that filters columns the server is willing to expose. (d) Define whether system/audit tables (e.g., `users`, `refresh_tokens`, `mutation_audit_log`) are explicitly blocklisted even if not in the allowlist.

### AD-15: SELECT-Only SQL Enforcement Strategy
**PRD position:** Server rejects non-SELECT top-level statements before any DDL or preview execution. A conservative keyword check is assumed.
**Resolve:** (a) Choose the enforcement mechanism — (i) regex/keyword scan at statement start (simple, may miss edge cases like `DO $$ INSERT… $$` blocks), or (ii) parse the SQL with a proper AST parser (e.g., `PgQuery.NET` which wraps `libpg_query`). Recommend (ii) for correctness; it can extract the statement type reliably. (b) Define acceptable complex forms: `WITH … AS (subquery) SELECT …` (CTEs) must be permitted. `COPY TO` must be rejected. (c) Decide whether the check applies to each CTE branch or only the final SELECT.

### AD-16: Preview Execution Security and Isolation
**PRD position:** Preview appends `LIMIT 10` and applies a statement timeout; executes against PostgreSQL in a read-only context. A `dataset-management` permission gate is required.
**Resolve:** (a) Define the PostgreSQL connection used for preview — a dedicated read-only role (`CREATE ROLE dataset_preview LOGIN NOINHERIT; GRANT SELECT ON allowlisted tables TO dataset_preview`) is strongly recommended to prevent preview queries from mutating data even if SELECT enforcement fails. (b) Confirm `SET LOCAL statement_timeout = '5s'` per query (within the preview transaction) as the timeout mechanism. (c) Define the Dapper / connection pool strategy for preview queries — should they share the main pool or a separate capped pool to prevent preview traffic from starving CRUD operations? (d) Confirm that parameterized placeholders (`$1`, `$2`, …) in builder-generated SQL are passed through the Dapper/Npgsql parameter binding layer, not via string interpolation.

### AD-17: VIEW Rename Atomicity
**PRD position:** Renaming a Dataset = `DROP VIEW {old_name}` + `CREATE VIEW {new_name}` in one transaction. No native PostgreSQL `RENAME VIEW` in the DDL path.
**Resolve:** (a) Confirm the transaction-based DROP + CREATE approach is acceptable — within a transaction, PostgreSQL's MVCC means concurrent readers at REPEATABLE READ or higher do not see the gap. (b) Evaluate `ALTER VIEW {old_name} RENAME TO {new_name}` as an alternative (it is a single atomic DDL, no intermediate absence of the view). This is simpler and safer — recommend it as the primary path. (c) If using ALTER RENAME, define behavior when the rename itself is inside a transaction with the row update — confirm PostgreSQL DDL-in-transaction behavior (DDL is transactional in PG, so rollback reverts the rename). (d) Define the error message and UI behavior when the old VIEW name is in active use by a consumer at rename time.

### AD-18: builder_state Schema Contract
**PRD position:** `builder_state` is JSONB persisted in `custom_dataset`. The SQL generator on the server reads it; the React Flow canvas on the frontend reads and writes it. This is a shared contract between front-end and back-end.
**Resolve:** Define the `builder_state` JSON schema formally in the architecture document before Story K-1 is implemented. Minimum required fields: `nodes[]` (each: `id`, `type: "table"`, `data.tableName`, `data.alias?`, `data.side: "left"|"right"`, `data.columns[]` each with `checked`, `aggregate?`, `alias?`; position `{x,y}`), `edges[]` (each: `id`, `source`, `sourceHandle`, `target`, `targetHandle`, `data.joinType`), `filters` (recursive group/condition tree), `orderBy[]` (each: `nodeId`, `column`, `direction`), `caseColumns[]`, `calculatedColumns[]`. Publish this as a JSON Schema or TypeScript interface and make it the contract that both the frontend renderer and the server generator implement against.

### AD-19: CASE and Calculated Column Expression Security
**PRD position:** Calculated column expressions are raw SQL fragments validated by SELECT-only enforcement of the final assembled query. Per-expression parsing is not in v1 scope.
**Resolve:** (a) Assess the residual injection risk: a calculated expression like `1); DROP TABLE users; --` would fail SELECT-only enforcement of the *final* query only if the generator wraps expressions with `({expression}) AS alias` inside the SELECT list — PostgreSQL would reject the syntax. Verify this guarantee holds in the Npgsql execution path. (b) If per-expression validation is added (recommended for defense in depth), define the validation: must not contain statement-terminating semicolons outside string literals, must not start with DDL/DML keywords. (c) Confirm the UI allows only users with `dataset-management` to author expressions, keeping the attack surface limited to privileged users.

---

## 11. Risk Register

| # | Risk | Likelihood | Impact | Mitigation |
|---|------|-----------|--------|-----------|
| R-1 | **SQL injection via dynamic identifier** — `designerId` or `fieldKey` values used unsanitized in Dapper SQL enable injection or DB privilege escalation. | Low (with mitigations) | Critical | Strict regex validation at Designer creation; server-side schema registry whitelist for all dynamic SQL identifiers; never interpolate raw request data; penetration test before production. |
| R-2 | **Designer port scope creep** — The existing designer has complex internals (visibility engine, Repeater scoping, external `submitRef`). Porting may surface hidden behaviors or ESG-platform-specific logic that requires significant refactoring. | Medium | High | Treat B-1 as a time-boxed audit spike (≤ 2 sprints). Document all non-obvious behaviors. Escalate scope estimate before S3 begins. |
| R-3 | **Schema drift accumulation** — Additive-only migrations mean orphaned columns accumulate over time. Tables grow cluttered; query plans may degrade; storage grows. | High | Medium | Provide schema drift view (D-4) with non-null row counts before any drop action. Establish a quarterly hygiene review practice. Document cleanup process in the operational runbook. |
| R-4 | **Repeater circular reference** — Designer A has a Repeater referencing Designer B, which references Designer A. Without cycle detection, provisioning recurses infinitely. | Low | High | Implement DFS cycle detection on the Repeater reference graph at Schema Binding time (Story D-5, AC-4). Reject circular references with a descriptive error before any DDL runs. |
| R-5 | **JWT not revoked on user deactivation** — Access token remains valid for up to 15 minutes after a user is deactivated (stateless JWT). | Medium | Medium | Short access token TTL (15 min). Server-side middleware checks `users.isActive` on each request (cached ≤ 30 s). Refresh token invalidated immediately on deactivation. |
| R-6 | **Performance at high row count** — Generic list queries on large tables with dynamic ORDER BY on unindexed columns may not use indexes effectively. | Medium | Medium | Mandate indexes on all system columns at table creation. Enforce a query timeout (5 s). Document that admin-added columns are not auto-indexed; provide admin tooling for index management in v2. |
| R-7 | **DynamicComponent accessibility gap** — Admins can configure forms with missing `label` or `fieldKey` values, producing inaccessible rendered output. | Medium | Medium | Enforce non-empty `label` and `fieldKey` for all interactive components at designer save time (B-6, AC-4). Add an accessibility lint step in the designer that warns on WCAG-relevant missing properties before publish. |
| R-8 | **Transitive soft-delete cascade complexity** — Soft-delete cascades application-level to all descendant Repeater rows. A deep Repeater graph (A → B → C) requires a recursive walk to cascade correctly; missing a level leaves orphaned active rows. | Low | Medium | Implement cascade as a recursive helper walking the Repeater reference graph; covered by the schema registry cache. Integration-test cascade depth ≥ 3 levels. Restore also cascades via the same helper. |
| R-9 | **MinIO outage blocks file uploads** — Image fields and icon uploads fail if MinIO is unavailable, potentially blocking a Platform Admin from completing a schema binding. | Low | Medium | Health check surfaces MinIO status. Degrade gracefully: form save succeeds without the image field value; warning shown. File fields are nullable by design. |
| R-10 | **Theme flash on page load** — Server-persisted theme applied after initial render causes a brief flash of the default theme on page reload. | High | Low | Inline a `<script>` in the HTML `<head>` that reads the theme preference from localStorage or a server-injected meta tag and applies it before React hydrates. Return theme preference in the login/refresh response for immediate application. |
| R-11 | **Password reset token interception** — Reset token delivered in a URL query parameter via email; if the email is intercepted or the link forwarded, an attacker can reset the account. | Low | High | Token is single-use, 1-hour TTL, 32 bytes (256-bit) of entropy. Reset link served over HTTPS only. Token hash stored; raw token never logged. Residual risk accepted given TLS-secured email transport. |
| R-12 | **TOTP secret storage breach** — If the database is compromised alongside the encryption key, TOTP secrets could be decrypted, enabling account takeover. | Low | High | Secrets encrypted with a key stored outside the DB (environment variable or secrets manager, not co-located). Rate-limit MFA verify and login endpoints. Rotate encryption keys on suspected breach. |
| R-13 | **MFA lockout with no recovery path** — A user who loses their authenticator device and all backup codes is permanently locked out without admin action. | Medium | Medium | Admin MFA reset (Story A-15) provides the recovery path. Document the reset procedure in the admin guide. UI at enrolment strongly prompts users to save backup codes (step 3 of enrolment modal). |
| R-14 | **SQL injection via Dataset filter values** — A user crafts a filter value that contains SQL when included in a query. | Low (with mitigations) | Critical | All filter values emitted as parameterized placeholders (`$1`, `$2`, …) via Npgsql parameter binding — values are never string-interpolated into SQL (J-6 AC-5, K-1 AC-4). Read-only preview connection as second line (AD-16). |
| R-15 | **Query Builder generates invalid or unbounded SQL** — Complex builder configurations (many joins, deep filter nesting, cartesian products) produce SQL that runs for minutes and exhausts resources. | Medium | High | Hard `LIMIT 10` + configurable statement timeout on Preview (K-3). The Dataset VIEW itself has no limit — very large datasets accessed without a filter could be slow. Mitigate: document that consumers must add their own WHERE/LIMIT; add an operational note. Consider a per-VIEW query timeout via `ALTER VIEW … SET (...)` in a future release. |
| R-16 | **Allowlist bypass via crafted builder_state** — A client sends a `builder_state` referencing a non-allowlisted table (e.g., `users`, `refresh_tokens`); if only client-side enforcement, the server may generate SQL against it. | Low (with server enforcement) | Critical | Server validates all table references in `builder_state` against the allowlist before SQL generation — any non-allowlisted table reference → HTTP 422 before any DDL or preview. Client-side palette filtering is UX only. |
| R-17 | **VIEW and row divergence on partial failure** — An application bug or DB error that commits the row write but fails the DDL (or vice versa) leaves the system in an inconsistent state. | Low (with transactions) | High | All row writes and VIEW DDL run in the same PostgreSQL transaction. PostgreSQL DDL is fully transactional; a failed `CREATE VIEW` or `DROP VIEW` within a transaction rolls back the entire unit. Integration tests must cover rollback scenarios (simulate VIEW failure after row insert). |

---

## 12. Open Questions

Most open questions resolved. New open questions from the v1.1 update:

1. ~~Product name~~ — **FormForge.**
2. ~~Soft-delete child cascade~~ — **Resolved:** cascade confirmed. See FR-33 AC-3, FR-34 AC-1, AD-5.
3. ~~Nested Repeater omit semantics~~ — **Resolved:** omit = soft-delete. See FR-35 AC-4.
4. ~~Re-bind trigger~~ — **Resolved:** explicit manual admin action. See FR-17.
5. ~~Access token storage~~ — **Resolved:** in-memory access token + HttpOnly SameSite=Strict refresh cookie. See §7 NFRs, AD-1.
6. ~~Admin impersonation~~ — **Not in v1.** See §5 Non-Goals.
7. **OQ-7:** Should MFA be enforceable platform-wide (admin can mandate MFA for all users or for specific roles)? Currently assumed voluntary per-user (A14). Revisit if security requirements tighten.
8. **OQ-8:** Should sent-email records (recipient, template, timestamp, success/failure) be audited in the database? Currently unspecified. See AD-12.
9. **OQ-9:** Should component mode be mutable after creation (e.g., promote a VIEW component to CRUD, provisioning a table on demand)? Currently mode is fixed at creation (FR-54, A17). Revisit if admins need to convert display-only prototypes into data-bearing modules.
10. **OQ-10:** Should Datasets be bindable to Menu Items as a data source in a future epic — i.e., render the VIEW's rows in the existing CRUD record-list UI? Currently Datasets and the Designer-based menu binding are separate subsystems. Flag if convergence is needed for v2.
11. **OQ-11:** What is the table allowlist management mechanism for v1? Candidates: (a) static JSON/YAML config file in source, (b) environment variable (comma-separated), (c) a `dataset_allowed_tables` DB table managed manually. An admin UI for allowlist management is explicitly out of scope for v1 (§5 Non-Goals). Decision needed before AD-14 is resolved.
12. **OQ-12:** Should Query Builder Mode support window functions (`OVER`, `PARTITION BY`, `ROW_NUMBER`, etc.) in a v2 scope? Currently excluded from v1 (§5 Non-Goals). Confirm with users whether ranking / running-total use cases are needed.
13. **OQ-13:** Is `dataset-management` a flag on the existing Role permission model (added as a column or permission entry alongside per-Resource CRUD flags), or does it require a separate permission model? The PRD assumes it is a new named permission in the existing RBAC registry (FR-56). Confirm with AD-14 resolution.

---

## 13. Assumptions Index

| ID | Assumption | Section |
|----|-----------|---------|
| A1 | .NET 10 (latest stable LTS at time of writing) is the target runtime. | §7 NFRs |
| A2 | React Router v7 for client-side routing. | §4 Epic G |
| A3 | Menu bindings use pinned `{ designerId, version }`; re-bind to a new version is a manual admin action. | §4 Epic C |
| A4 | Image/file fields store a MinIO object key (`TEXT`) in the dynamic table column; the API returns a presigned URL in CRUD responses. | §4 Epic D |
| A5 | Repeater FK `ON DELETE CASCADE` applies to hard deletes only. Soft-delete cascades application-level (parent soft-delete → all child rows soft-deleted in same transaction; restore also cascades transitively). | §4 Epic E |
| A6 | Soft-delete: `is_deleted` system column on all dynamic tables. List endpoints return all records (including soft-deleted) by default; filtering is UI-configurable. | §4 Epic E |
| A7 | Bulk import/export: out of scope for v1. | §5 Non-Goals |
| A8 | Full-text search: out of scope for v1. | §5 Non-Goals |
| A9 | i18n: externalized strings architecture only; English-only at launch. | §4 Epic G |
| A10 | Single-tenant deployment. | §5 Non-Goals |
| A11 | Access token in-memory; refresh token in HttpOnly SameSite=Strict cookie. CSRF not applicable for in-memory access token; refresh cookie protected by SameSite=Strict. | §7 NFRs |
| A12 | Server validates active role set from DB on each request with a ≤ 30 s cache; not solely from JWT claims. | §4 Epic A |
| A13 | Nested Repeater write: child rows omitted from a PUT `children` payload are soft-deleted. **Confirmed.** | §4 Epic E, FR-35 |
| A14 | TOTP MFA is voluntary per-user; no platform-wide enforcement policy in v1. | §4 Epic A, FR-53 |
| A15 | Email transport uses SMTP with STARTTLS/SMTPS. Mailpit used as local-dev SMTP catcher. | §4 Epic A, FR-50, FR-51 |
| A16 | TOTP conforms to RFC 6238 (SHA-1, 30-second period, 6 digits, ±1 step clock-skew tolerance). | §4 Epic A, FR-53 |
| A17 | Component mode (`CRUD`/`VIEW`) is set at creation, stored in `component_schemas.mode` (`NOT NULL`), and immutable across versions; components predating this feature are backfilled to `CRUD`. VIEW components never provision a table and expose no CRUD data API; they render read-only via DynamicComponent. The Dropdown "Source component" and Repeater "Row form — Component" pickers list CRUD-mode components only. | §4 Epic B, FR-54 |
| A18 | Dataset read access (GET /api/datasets, GET /api/datasets/{id}) is open to all authenticated users; create/update/delete/preview requires the `dataset-management` permission. | §4 Epic H, FR-56 |
| A19 | The Table Allowlist is server-side configured (not per-user); all users with `dataset-management` can query any allowlisted table. No per-user table access control within the allowlist in v1. | §4 Epic H, FR-63, §5 Non-Goals |
| A20 | Query Builder: one join edge per node-pair in v1 (one table-node instance connected to another by a single edge). Additional join conditions beyond the primary equality are handled via filter conditions. Self-joins require two separate node instances of the same table. | §4 Epic I, Story I-3 |
| A21 | Calculated column expressions in the Query Builder are raw SQL fragments included verbatim (wrapped in parentheses) in the SELECT clause. Injection risk is mitigated by: (a) SELECT-only enforcement of the final assembled query, (b) table allowlist enforcement on table references, (c) the `dataset-management` permission gate limiting the attack surface to privileged users. Per-expression sandboxing is not in v1. | §4 Epic J, Story J-4 |
| A22 | Preview execution uses a configurable statement timeout (default 5 s via `SET LOCAL statement_timeout`) and returns at most 10 rows via appended `LIMIT 10`. The timeout value is an environment variable. Preview runs against a read-only PostgreSQL connection/role (exact mechanism resolved in AD-16). | §4 Epic K, Story K-3 |
| A23 | `ALTER VIEW {old_name} RENAME TO {new_name}` is the preferred mechanism for Dataset renames (atomic DDL, no intermediate VIEW absence). The final choice between RENAME and DROP+CREATE is deferred to AD-17. | §4 Epic H, FR-58, Story H-5 |
