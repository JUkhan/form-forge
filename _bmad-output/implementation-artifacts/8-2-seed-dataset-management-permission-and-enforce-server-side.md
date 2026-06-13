# Story 8.2: Seed Dataset Management Permission and Enforce Server-Side

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Platform Administrator,
I want the `platform-admin` role to have `can_manage_datasets = true` by default and all dataset write endpoints to enforce that permission,
so that only authorised users can create, update, or delete Datasets while any authenticated user can read them.

## Acceptance Criteria

**AC-1 — `platform-admin` seeded with `can_manage_datasets = true`**
**Given** the migration from Story 8.1 has run
**When** `EffectivePermissions` is computed for a `platform-admin` user
**Then** `CanManageDatasets` is `true`
**Note:** The DB seed (`UPDATE roles SET can_manage_datasets = true WHERE id = '...'`) was already applied by Story 8.1. This story wires the DB value into the computed permissions object.

**AC-2 — Write endpoints return HTTP 403 for users without `can_manage_datasets`**
**Given** a request to `POST /api/datasets`, `PUT /api/datasets/{id}`, `DELETE /api/datasets/{id}`, or `POST /api/datasets/preview`
**When** the calling user does not have `can_manage_datasets = true` on any of their roles
**Then** the response is HTTP 403 with `{ code: "FORBIDDEN", action: "dataset-management" }` (FR-56 AC-2 / AR-58)

**AC-3 — Read endpoints require only authentication**
**Given** `GET /api/datasets` or `GET /api/datasets/{id}`
**When** the user is authenticated (any role, including the `viewer` role)
**Then** the request proceeds — read access does not require `dataset-management` (FR-56 / A18)

**AC-4 — Admin > Roles UI exposes a "Dataset Management" toggle**
**Given** the Admin > Roles detail page (Story 2.9)
**When** a Platform Admin edits a role
**Then** a labelled "Dataset Management" toggle is visible above the per-resource permission matrix
**And** it is writable for non-system roles and read-only (disabled) for system roles
**And** saving the role persists the new `canManageDatasets` value via the PUT `/api/admin/roles/{id}` endpoint

**AC-5 — `EffectivePermissions.CanManageDatasets` is exposed on `/api/users/me/permissions`**
**Given** a call to `GET /api/users/me/permissions`
**When** the user holds roles with/without `can_manage_datasets`
**Then** the response body includes `canManageDatasets: bool` reflecting the union across the user's roles

---

## Tasks / Subtasks

- [x] **Task 1 — Extend `EffectivePermissions` record** (AC-1, AC-2, AC-3, AC-5)
  - [x] Add `bool CanManageDatasets` as a new positional parameter to the end of `src/FormForge.Api/Features/Permissions/EffectivePermissions.cs`
  - [x] Fix the single constructor call site in `PermissionService.ComputePermissionsAsync` (compiler will flag any missing)

- [x] **Task 2 — Compute `CanManageDatasets` in `PermissionService`** (AC-1, AC-2)
  - [x] In `ComputePermissionsAsync` in `PermissionService.cs`, after resolving `roleIds`, query:
    ```csharp
    var canManageDatasets = roleIds.Count > 0
        && await db.Roles
            .Where(r => roleIds.Contains(r.Id) && r.CanManageDatasets)
            .AnyAsync(ct)
            .ConfigureAwait(false);
    ```
  - [x] Pass `CanManageDatasets: canManageDatasets` to the `EffectivePermissions` constructor

- [x] **Task 3 — Add `RequireDatasetManagement()` endpoint filter** (AC-2, AC-3)
  - [x] Add to `src/FormForge.Api/Common/Endpoints/RouteGroupExtensions.cs`:
    ```csharp
    internal static RouteHandlerBuilder RequireDatasetManagement(
        this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEndpointFilter(async (ctx, next) =>
        {
            var userIdClaim = ctx.HttpContext.User.FindFirst("userId")?.Value;
            if (!Guid.TryParse(userIdClaim, out var userId))
                return Results.Unauthorized();
            var permissionService = ctx.HttpContext.RequestServices
                .GetRequiredService<IPermissionService>();
            var permissions = await permissionService
                .GetEffectivePermissionsAsync(userId, ctx.HttpContext.RequestAborted)
                .ConfigureAwait(false);
            if (!permissions.CanManageDatasets)
            {
                return Results.Problem(
                    detail: "Dataset management permission required.",
                    title: "Permission denied",
                    statusCode: StatusCodes.Status403Forbidden,
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["code"] = "FORBIDDEN",
                        ["action"] = "dataset-management",
                        ["messageKey"] = "errors.forbidden",
                    });
            }
            return await next(ctx).ConfigureAwait(false);
        });
    }
    ```
  - [x] Note: This is a `RouteHandlerBuilder` extension (per-endpoint, like `RequirePermission`), not `RouteGroupBuilder` — because write and read endpoints on the same group have different requirements.

- [x] **Task 4 — Scaffold `/api/datasets` route group with stub handlers** (AC-2, AC-3)
  - [x] Create `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs`:
    ```csharp
    namespace FormForge.Api.Features.Datasets;

    internal static class DatasetEndpoints
    {
        internal static RouteGroupBuilder MapDatasetEndpoints(this RouteGroupBuilder group)
        {
            ArgumentNullException.ThrowIfNull(group);

            // Read endpoints — auth only (AC-3); handlers are stubs replaced in Stories 8.7
            group.MapGet("/", () => Results.Ok(Array.Empty<object>()))
                 .WithSummary("List datasets (stub — Story 8.7)");
            group.MapGet("/{id:guid}", (Guid id) => Results.NotFound())
                 .WithSummary("Get dataset (stub — Story 8.7)");

            // Write endpoints — require dataset-management (AC-2); handlers replaced in Stories 8.4/8.5/8.6
            group.MapPost("/", () => Results.StatusCode(StatusCodes.Status501NotImplemented))
                 .WithSummary("Create dataset (stub — Story 8.4)")
                 .RequireDatasetManagement();
            group.MapPut("/{id:guid}", (Guid id) => Results.StatusCode(StatusCodes.Status501NotImplemented))
                 .WithSummary("Update dataset (stub — Story 8.5)")
                 .RequireDatasetManagement();
            group.MapDelete("/{id:guid}", (Guid id) => Results.StatusCode(StatusCodes.Status501NotImplemented))
                 .WithSummary("Delete dataset (stub — Story 8.6)")
                 .RequireDatasetManagement();
            group.MapPost("/preview", () => Results.StatusCode(StatusCodes.Status501NotImplemented))
                 .WithSummary("Dataset query preview (stub — Story 11.3)")
                 .RequireDatasetManagement();

            return group;
        }
    }
    ```
  - [x] Register in `src/FormForge.Api/Program.cs` alongside the other `MapGroup` calls:
    ```csharp
    app.MapGroup("/api/datasets").RequireAuth().MapDatasetEndpoints();
    ```
    Place it in the same block as `/api/designers` and `/api/menus` (keep the route-group section tidy).

- [x] **Task 5 — Expose `CanManageDatasets` on the permissions endpoint** (AC-5)
  - [x] In `src/FormForge.Api/Features/Permissions/Dtos/PermissionsResponse.cs`, add `bool CanManageDatasets` as the last positional parameter
  - [x] In `PermissionsEndpoints.cs`, map `permissions.CanManageDatasets` into the `Results.Ok(new PermissionsResponse(...))` constructor call

- [x] **Task 6 — Update Role API to accept/persist `CanManageDatasets`** (AC-4)
  - [x] `src/FormForge.Api/Features/Roles/Dtos/UpdateRoleRequest.cs` — add `bool CanManageDatasets`
    ```csharp
    internal sealed record UpdateRoleRequest(
        string Name,
        string? Description,
        IReadOnlyList<PermissionRecord> Permissions,
        bool CanManageDatasets);
    ```
  - [x] `src/FormForge.Api/Features/Roles/Dtos/RoleResponse.cs` — add `bool CanManageDatasets`
    ```csharp
    internal sealed record RoleResponse(
        Guid Id,
        string Name,
        string? Description,
        bool IsSystem,
        DateTimeOffset CreatedAt,
        DateTimeOffset? UpdatedAt,
        IReadOnlyList<PermissionRecord> Permissions,
        bool CanManageDatasets);
    ```
  - [x] `src/FormForge.Api/Features/Roles/RoleService.cs`
    - In `UpdateRoleAsync`: add `role.CanManageDatasets = request.CanManageDatasets;` alongside the other property assignments (system roles are guarded by the `IsSystem` early-return check, so no extra guard needed)
    - In `ToResponse`: add `role.CanManageDatasets` as the last constructor argument

- [x] **Task 7 — Frontend: update role types and API** (AC-4)
  - [x] `web/src/features/admin/roles/types.ts` — add `canManageDatasets: boolean` to `RoleDetail` and `UpdateRoleRequest`
  - [x] `web/src/features/admin/roles/roleMutations.ts` — no change needed (already passes the full `UpdateRoleRequest` body; the type change in `types.ts` is sufficient)

- [x] **Task 8 — Frontend: add "Dataset Management" toggle to role detail page** (AC-4)
  - [x] `web/src/routes/_app/admin/roles.$roleId.tsx`:
    - Add local state: `const [canManageDatasets, setCanManageDatasets] = useState(role.canManageDatasets)`
    - Sync with `useEffect` on `role.canManageDatasets` (same guard as `isDirty` for name/description)
    - Render the toggle **above** `<PermissionMatrix>` inside the form:
      ```tsx
      <div className="flex items-center justify-between rounded-lg border border-border bg-muted/40 p-4">
        <div>
          <p className="text-sm font-medium text-foreground">
            {t('admin.roles.datasetManagementLabel')}
          </p>
          <p className="mt-0.5 text-xs text-muted-foreground">
            {t('admin.roles.datasetManagementHelp')}
          </p>
        </div>
        <input
          type="checkbox"
          aria-label={t('admin.roles.datasetManagementLabel')}
          checked={canManageDatasets}
          disabled={role.isSystem}
          onChange={(e) => setCanManageDatasets(e.target.checked)}
          className="h-4 w-4 rounded border-field-border"
        />
      </div>
      ```
    - In `submit()`, pass `canManageDatasets` to `updateMutation.mutateAsync`:
      ```tsx
      await updateMutation.mutateAsync({
        name: values.name,
        description: trimmedDescription,
        permissions: draft,
        canManageDatasets,
      })
      ```
    - After save reset: sync `canManageDatasets` state on mutation success (it re-fetches via `invalidateQueries`, so the `useEffect` sync from role refetch handles it)
  - [x] `web/src/lib/i18n/locales/en.json` — add under `admin.roles`:
    ```json
    "datasetManagementLabel": "Dataset Management",
    "datasetManagementHelp": "Allow this role to create, edit, and delete Datasets."
    ```

- [x] **Task 9 — Integration tests** (AC-1 through AC-5)
  - [x] Create `src/FormForge.Api.Tests/Features/Datasets/DatasetPermissionTests.cs`
  - [x] **AC-1 / AC-2 — `EffectivePermissions.CanManageDatasets` union computation:**
    - Seed: platform-admin user → `CanManageDatasets` = `true`
    - Seed: viewer user (no dataset permission) → `CanManageDatasets` = `false`
    - Create custom role with `can_manage_datasets = true`, assign to a user → `CanManageDatasets` = `true`
    - Create custom role with `can_manage_datasets = false`, assign to a user → `CanManageDatasets` = `false`
    - Union: user with both a dataset role and a non-dataset role → `CanManageDatasets` = `true`
  - [x] **AC-2 — Write endpoints return 403 for users without permission:**
    - `POST /api/datasets` with viewer token → 403, `code: "FORBIDDEN"`, `action: "dataset-management"`
    - `PUT /api/datasets/{id}` with viewer token → 403
    - `DELETE /api/datasets/{id}` with viewer token → 403
    - `POST /api/datasets/preview` with viewer token → 403
  - [x] **AC-2 — Write endpoints return non-4xx for authorised users:**
    - `POST /api/datasets` with admin token → 501 (stub response, not 401/403)
  - [x] **AC-3 — Read endpoints require only auth:**
    - `GET /api/datasets` without token → 401
    - `GET /api/datasets` with viewer token → 200
    - `GET /api/datasets/{id}` with viewer token → 404 (stub response, not 401/403)
  - [x] **AC-5 — `/api/users/me/permissions` includes `canManageDatasets`:**
    - Admin token: response body has `canManageDatasets: true`
    - Viewer token: response body has `canManageDatasets: false`
    - After granting `can_manage_datasets` to viewer's role: `canManageDatasets: true`

---

## Dev Notes

### §1 — `EffectivePermissions` Is a Positional Record

```csharp
// BEFORE (current)
internal sealed record EffectivePermissions(
    Guid UserId,
    DateTimeOffset ComputedAt,
    bool IsActive,
    IReadOnlyDictionary<string, CrudFlags> PerResource,
    IReadOnlySet<Guid> RoleIds);

// AFTER (this story)
internal sealed record EffectivePermissions(
    Guid UserId,
    DateTimeOffset ComputedAt,
    bool IsActive,
    IReadOnlyDictionary<string, CrudFlags> PerResource,
    IReadOnlySet<Guid> RoleIds,
    bool CanManageDatasets);
```

The **only constructor call site** is in `PermissionService.ComputePermissionsAsync`. The record is used by `GetEffectivePermissionsAsync` and `GetCrudFlagsAsync` — those access individual properties, not the constructor, so no changes needed beyond the construction site.

Named arguments in the constructor call means you can append `CanManageDatasets: canManageDatasets` without reordering. The compiler will catch any missed sites.

### §2 — `PermissionService.ComputePermissionsAsync` — DB query pattern

The `can_manage_datasets` column lives on the `roles` table. Query pattern that follows the existing style:

```csharp
// After the existing roleIds and perms queries:
var canManageDatasets = roleIds.Count > 0
    && await db.Roles
        .Where(r => roleIds.Contains(r.Id) && r.CanManageDatasets)
        .AnyAsync(ct)
        .ConfigureAwait(false);
```

This is a single extra `AnyAsync` call on the already-loaded `roleIds`. It does NOT need a `.Include` (EF generates a simple `EXISTS` subquery). The `CanManageDatasets` property on `Role` was added by Story 8.1 and is already in the EF model and snapshot.

### §3 — `RequireDatasetManagement()` Is `RouteHandlerBuilder`, Not `RouteGroupBuilder`

Architecture §6.2 says "extension on `RouteGroupBuilder`" but the actual requirement is per-endpoint (read endpoints in the same group are auth-only). Use `RouteHandlerBuilder`, exactly like `RequirePermission()`. The pattern is proven in `RouteGroupExtensions.cs`.

The filter response shape must match the existing `FORBIDDEN` envelope. Compare:
- `RequirePermission()` includes `"resource"` and `"action"` keys
- `RequireDatasetManagement()` includes only `"action": "dataset-management"` (no resource — this is a platform-level capability, not per-resource)

### §4 — Stub Endpoints Are Intentionally Minimal

`DatasetEndpoints.cs` creates 6 route entries:
- `GET /api/datasets` → `200 []` (replaced in Story 8.7)
- `GET /api/datasets/{id:guid}` → `404` (replaced in Story 8.7)
- `POST /api/datasets` → `501` + `RequireDatasetManagement()` (replaced in Story 8.4)
- `PUT /api/datasets/{id:guid}` → `501` + `RequireDatasetManagement()` (replaced in Story 8.5)
- `DELETE /api/datasets/{id:guid}` → `501` + `RequireDatasetManagement()` (replaced in Story 8.6)
- `POST /api/datasets/preview` → `501` + `RequireDatasetManagement()` (replaced in Story 11.3)

The 501 responses from stub write endpoints are only reachable with a valid `can_manage_datasets` token — so they won't confuse users. All stubs are replaced before these endpoints are surfaced in production UI (Stories 8.4–8.7, 11.3).

The `GET /api/datasets` stub returns `200 []` (empty JSON array), not 501, because the permission test for AC-3 needs the route to respond with a non-error status. The real implementation (Story 8.7) will replace this with the actual paginated list.

### §5 — Role API Shape Changes

`UpdateRoleRequest` gains `CanManageDatasets: bool`. Default value for bool in C# is `false`, which is safe — any existing clients not sending the field will default to `false` when deserialized by System.Text.Json (which initialises records from JSON positionally / by property name match).

`RoleResponse` gains `CanManageDatasets: bool`. This is additive — no existing client is broken by receiving an extra field.

`RoleService.UpdateRoleAsync` must set `role.CanManageDatasets = request.CanManageDatasets` before `SaveChangesAsync`. The system-role guard (`IsSystem` early-return) already prevents modifying platform-admin, so there is **no need for an extra IsSystem guard** inside the dataset management setter. Attempting to save a system role returns `SystemProtected` before reaching the assignment code.

`RoleService.ToResponse` adds the new field:
```csharp
private static RoleResponse ToResponse(Role role) =>
    new(
        role.Id,
        role.Name,
        role.Description,
        role.IsSystem,
        role.CreatedAt,
        role.UpdatedAt,
        role.Permissions
            .OrderBy(p => p.ResourceId, StringComparer.Ordinal)
            .Select(p => new PermissionRecord(p.ResourceId, p.CanCreate, p.CanRead, p.CanUpdate, p.CanDelete, p.CanExport))
            .ToList(),
        role.CanManageDatasets);  // ← new
```

### §6 — `PermissionsResponse` Shape Change

```csharp
// BEFORE (current)
internal sealed record PermissionsResponse(
    Guid UserId,
    DateTimeOffset ComputedAt,
    bool IsActive,
    IReadOnlyDictionary<string, CrudFlagsResponse> PerResource,
    IReadOnlySet<Guid> RoleIds);

// AFTER
internal sealed record PermissionsResponse(
    Guid UserId,
    DateTimeOffset ComputedAt,
    bool IsActive,
    IReadOnlyDictionary<string, CrudFlagsResponse> PerResource,
    IReadOnlySet<Guid> RoleIds,
    bool CanManageDatasets);
```

In `PermissionsEndpoints.cs`, the `Results.Ok(new PermissionsResponse(...))` constructor call needs `permissions.CanManageDatasets` appended as the final argument.

### §7 — Frontend Toggle State Management

The `canManageDatasets` toggle is **outside** `PermissionMatrix` (which manages per-resource CRUD flags) and lives directly in `RoleDetailContent`. The form already uses `useForm` for `name`/`description`; `canManageDatasets` is simpler as a plain `useState` (same approach as if it were a checkbox outside the form's zod schema). On submit, read the local state and include it in the mutation payload alongside the matrix draft.

Sync the toggle with server truth when the role refetches (window-focus refetch, etc.):
```tsx
useEffect(() => {
  if (isDirty) return          // respect in-progress name/description edits
  setCanManageDatasets(role.canManageDatasets)
}, [role.canManageDatasets, isDirty])
```

The `isDirty` guard mirrors the pattern already used for `name`/`description` reset (line ~156 in roles.$roleId.tsx). It is intentional to gate `setCanManageDatasets` on `isDirty` from the name form — a strict implementation would track dirty state per-field, but this simpler heuristic is consistent with the existing page behaviour and good enough for the admin-only context.

### §8 — Test File Location and Patterns

Integration tests for the Dataset feature live under `src/FormForge.Api.Tests/Features/Datasets/` (same directory as `DatasetMigrationTests.cs` from Story 8.1). Reuse `PostgresFixture` and `WebApplicationFactory` pattern from that file.

To set up a user without dataset permission, use the `viewer@example.com` seeded test account (same credential used across the test suite). To set up a user WITH a non-admin dataset permission: create a custom role via `POST /api/admin/roles` with `canManageDatasets: true`, assign it to a test user, then login as that user.

Key assertions for 403 response:
```csharp
var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
Assert.Equal("FORBIDDEN", doc.RootElement.GetProperty("extensions")
    .GetProperty("code").GetString());
Assert.Equal("dataset-management", doc.RootElement.GetProperty("extensions")
    .GetProperty("action").GetString());
```

Check the existing forbidden-response assertions in `CreateRecordIntegrationTests.cs` (around line 276) for the exact JSON path — `Results.Problem` nests the extensions under `"extensions"` in the response body.

### §9 — `Program.cs` Route Group Placement

Currently in `Program.cs`:
```csharp
app.MapGroup("/api/auth").MapAuthEndpoints();
app.MapGroup("/api/users").RequireAuth().MapUserSelfEndpoints();
app.MapGroup("/api/admin").RequireAuth().RequirePlatformAdmin().MapAdminEndpoints();
app.MapGroup("/api/menus").RequireAuth().MapMenuEndpoints();
app.MapGroup("/api/designers").RequireAuth().MapDesignerEndpoints();
app.MapGroup("/api/data/{designerId}").RequireAuth().MapDynamicDataEndpoints();
```

Add:
```csharp
app.MapGroup("/api/datasets").RequireAuth().MapDatasetEndpoints();
```

Place it after `/api/designers` and before `/api/data/{designerId}` — thematic proximity.

### §10 — Cache Busting After Role `can_manage_datasets` Change

`UpdateRoleAsync` already publishes `RolePermissionsChanged(roleId)` after save (see Story 2.4 pattern). `PermissionService` subscribes to this event and evicts all users holding that role. Since `CanManageDatasets` is now part of `EffectivePermissions`, the existing event publication is sufficient — the next permissions request will recompute `CanManageDatasets` from the DB. No additional event or cache-bust logic is needed.

### §11 — No `/api/admin/roles` Filter Change

The Admin > Roles endpoint group already uses `RequireAuth().RequirePlatformAdmin()` at the group level. Dataset management editing is therefore only accessible to platform-admins — which is correct for the current MVP. The `UpdateRoleRequest.CanManageDatasets` field will be silently rejected (or default to `false`) for non-admin callers because they never reach the handler.

### Project Structure Notes

**New files:**
```
src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs          (NEW — stub route group)
src/FormForge.Api.Tests/Features/Datasets/DatasetPermissionTests.cs  (NEW — integration tests)
```

**Modified files:**
```
src/FormForge.Api/Features/Permissions/EffectivePermissions.cs        (+CanManageDatasets field)
src/FormForge.Api/Features/Permissions/PermissionService.cs           (+compute CanManageDatasets)
src/FormForge.Api/Common/Endpoints/RouteGroupExtensions.cs            (+RequireDatasetManagement)
src/FormForge.Api/Features/Permissions/Dtos/PermissionsResponse.cs    (+CanManageDatasets field)
src/FormForge.Api/Features/Permissions/PermissionsEndpoints.cs        (+map CanManageDatasets)
src/FormForge.Api/Features/Roles/Dtos/UpdateRoleRequest.cs            (+CanManageDatasets)
src/FormForge.Api/Features/Roles/Dtos/RoleResponse.cs                 (+CanManageDatasets)
src/FormForge.Api/Features/Roles/RoleService.cs                       (+set + map CanManageDatasets)
src/FormForge.Api/Program.cs                                           (+/api/datasets group)
web/src/features/admin/roles/types.ts                                  (+canManageDatasets)
web/src/routes/_app/admin/roles.$roleId.tsx                           (+toggle UI)
web/src/lib/i18n/locales/en.json                                      (+2 keys)
```

**No new migrations** — the `can_manage_datasets` column was added in Story 8.1.

### References

- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.2 — dataset-management Permission Model (FR-56, OQ-13)]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §2.2 — EffectivePermissions record and cache]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §3.5 — Endpoint Organisation / Route Groups]
- [Source: `_bmad-output/planning-artifacts/epics.md` — Epic 8, Story 8.2 Acceptance Criteria (FR-56)]
- [Source: `src/FormForge.Api/Features/Permissions/EffectivePermissions.cs` — current record shape]
- [Source: `src/FormForge.Api/Features/Permissions/PermissionService.cs` — ComputePermissionsAsync pattern]
- [Source: `src/FormForge.Api/Common/Endpoints/RouteGroupExtensions.cs` — RequirePermission() filter pattern]
- [Source: `src/FormForge.Api/Features/Permissions/Dtos/PermissionsResponse.cs` — current response DTO]
- [Source: `src/FormForge.Api/Features/Permissions/PermissionsEndpoints.cs` — mapping pattern]
- [Source: `src/FormForge.Api/Features/Roles/Dtos/UpdateRoleRequest.cs` / `RoleResponse.cs` — current shapes]
- [Source: `src/FormForge.Api/Features/Roles/RoleService.cs` — UpdateRoleAsync + ToResponse pattern]
- [Source: `web/src/routes/_app/admin/roles.$roleId.tsx` — role detail page, PermissionMatrix, state pattern]
- [Source: `web/src/features/admin/roles/types.ts` — RoleDetail / UpdateRoleRequest TS types]
- [Source: `web/src/lib/i18n/locales/en.json` — admin.roles key namespace]
- [Source: `src/FormForge.Api.Tests/Features/DynamicCrud/CreateRecordIntegrationTests.cs:269` — 403 test pattern]
- [Source: `src/FormForge.Api.Tests/Features/Datasets/DatasetMigrationTests.cs` — test project location, fixture pattern]
- [Source: Story 8.1 Dev Notes §9 — "EffectivePermissions extension for future Story 8.2"]

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (1M context) — BMad Dev Story workflow

### Debug Log References

- `dotnet build src/FormForge.Api/FormForge.Api.csproj` → Build succeeded, 0 warnings.
- `dotnet test --filter DatasetPermissionTests` → 17 passed.
- `dotnet test src/FormForge.Api.Tests` (full suite) → 804 passed, 2 failed. The 2 failures
  (`SchemaAuditLogIntegrationTests.GetSchemaAuditLog_AppendOnly_DeleteVerb_Returns405`,
  `MutationAuditLogIntegrationTests.GetMutationAuditLog_AppendOnly_DeleteVerb_Returns405`)
  are pre-existing on a clean tree and unrelated to this story.
- Web: `tsc -b --noEmit` → 0 errors. `vitest run` → 246 passed, 1 failed. The single failure
  (`i18n-lint.test.ts`, missing `designer.inspector.placeholders.label`) is pre-existing on a
  clean tree. The two new keys (`admin.roles.datasetManagementLabel/Help`) are correctly
  referenced — they appear in neither the missing nor orphaned i18n-check lists.
- Web ESLint on changed files: only the 3 pre-existing `react-refresh/only-export-components`
  errors on `roles.$roleId.tsx` remain (same as clean-tree baseline); no new violations.

### Completion Notes List

- **AC-1** — `EffectivePermissions` gained `CanManageDatasets`; `PermissionService.ComputePermissionsAsync`
  computes it as a unioned `EXISTS` over the user's roles. Verified by
  `Permissions_PlatformAdmin_CanManageDatasetsTrue`.
- **AC-2** — New `RouteHandlerBuilder.RequireDatasetManagement()` filter returns 403 with
  `{ code: "FORBIDDEN", action: "dataset-management" }` (no `resource` key — platform-level capability).
  Verified for POST/PUT/DELETE/preview (4 tests) plus the authorised 501 case.
- **AC-3** — `/api/datasets` read endpoints (`GET /`, `GET /{id}`) are auth-only; write endpoints
  attach the filter per-endpoint. Verified 401 (no token), 200 (viewer list), 404 (viewer get stub).
- **AC-4** — Role API DTOs (`UpdateRoleRequest`, `RoleResponse`) and `RoleService` (set + map) carry
  `CanManageDatasets`; the system-role guard already blocks system roles so no extra guard was needed.
  Frontend adds a "Dataset Management" toggle above the permission matrix (read-only for system roles),
  included in the PUT payload. Backend persistence verified by `UpdateRole_SetsCanManageDatasets_PersistsAndComputes`.
- **AC-5** — `PermissionsResponse` gained `CanManageDatasets`, mapped in `PermissionsEndpoints`.
  Verified admin=true, viewer=false, and flip-to-true after granting a dataset role (with cache bust).
- **Deviation (minor, justified):** The `/api/datasets` group registration adds `.RequireRateLimiting("admin")`
  and `.WithTags("Datasets")` beyond the story's illustrative `RequireAuth().MapDatasetEndpoints()` snippet,
  to match the surrounding route-group block (designers/menus/users) per the story's own "keep the
  route-group section tidy" guidance.
- **Deviation (test pattern):** The 403-body assertion reads `code`/`action` at the JSON root, not nested
  under `extensions` as Dev Notes §8 suggested. ASP.NET Core flattens ProblemDetails extension members to
  the root via `[JsonExtensionData]`; the existing passing `CreateRecordIntegrationTests` confirms this.
- **Lint fix:** Dev Notes §7's `useEffect`+`setState` sync pattern trips the repo's
  `react-hooks/set-state-in-effect` rule; replaced with React's "adjust state during render" pattern
  (tracking the last-synced server value) — same behaviour, no new lint error, no extra re-render.
- No new migration (the `can_manage_datasets` column came from Story 8.1). Only `en.json` locale exists,
  so no multi-locale sync was required.

### File List

**New:**
- `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs`
- `src/FormForge.Api.Tests/Features/Datasets/DatasetPermissionTests.cs`

**Modified:**
- `src/FormForge.Api/Features/Permissions/EffectivePermissions.cs`
- `src/FormForge.Api/Features/Permissions/PermissionService.cs`
- `src/FormForge.Api/Common/Endpoints/RouteGroupExtensions.cs`
- `src/FormForge.Api/Features/Permissions/Dtos/PermissionsResponse.cs`
- `src/FormForge.Api/Features/Permissions/PermissionsEndpoints.cs`
- `src/FormForge.Api/Features/Roles/Dtos/UpdateRoleRequest.cs`
- `src/FormForge.Api/Features/Roles/Dtos/RoleResponse.cs`
- `src/FormForge.Api/Features/Roles/RoleService.cs`
- `src/FormForge.Api/Program.cs`
- `web/src/features/admin/roles/types.ts`
- `web/src/routes/_app/admin/roles.$roleId.tsx`
- `web/src/lib/i18n/locales/en.json`

### Review Findings

- [x] [Review][Decision→Patch] `PUT /api/admin/roles/{id}` missing `canManageDatasets` silently defaults to `false` — Fixed: `CanManageDatasets` is now `bool?` in `UpdateRoleRequest`; `UpdateRoleAsync` only writes it when non-null. Omitting the field from a PUT body preserves the existing value. [`UpdateRoleRequest.cs`, `RoleService.cs:UpdateRoleAsync`]

- [x] [Review][Patch] Checkbox `canManageDatasets` not tracked by `isDirty` — Fixed: added `checkboxDirty = canManageDatasets !== lastSyncedDatasets` guard to the sync-during-render block so a background refetch does not overwrite an in-progress toggle. [`web/src/routes/_app/admin/roles.$roleId.tsx:~170`]
- [x] [Review][Patch] `lastSyncedDatasets` not updated after save — Fixed: `setLastSyncedDatasets(canManageDatasets)` added after `reset(...)` in `submit()` to mark the saved value as the new server baseline. [`web/src/routes/_app/admin/roles.$roleId.tsx:~184`]
- [x] [Review][Patch] `ReseedSystemRolesAsync` check-then-insert not atomic — Fixed: replaced `AnyAsync` + `db.Roles.Add` + `SaveChangesAsync` with atomic `ExecuteSqlRawAsync` + `ON CONFLICT (id) DO NOTHING`. [`src/FormForge.Api.Tests/Features/Datasets/DatasetPermissionTests.cs:376`]
- [x] [Review][Patch] `AssertForbiddenDatasetManagementAsync` does not assert absence of `"resource"` key — Fixed: added `Assert.False(TryGetProperty("resource", out _))` assertion. [`src/FormForge.Api.Tests/Features/Datasets/DatasetPermissionTests.cs:312`]

- [x] [Review][Defer] `RequireDatasetManagement` omits `IsActive` check — consistent with existing `RequirePermission` pattern (same filter omits it); pre-existing architectural choice across all permission filters [`src/FormForge.Api/Common/Endpoints/RouteGroupExtensions.cs:106`] — deferred, pre-existing
- [x] [Review][Defer] `POST /preview` registered after `POST /` — latent route-shadowing risk when Story 8.4 replaces the `/` stub; register `/preview` before `/` when real handlers land [`src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs:32`] — deferred, pre-existing
- [x] [Review][Defer] `CreateRoleAsync` has no `CanManageDatasets` — asymmetric create/update API; new roles silently default to `false` with no create-time affordance; out of Story 8.2 scope, address in Story 8.10 [`src/FormForge.Api/Features/Roles/RoleService.cs`] — deferred, pre-existing
- [x] [Review][Defer] TOCTOU gap between `RequireDatasetManagement` filter and real write handlers — acknowledged in dev notes; deferred to Stories 8.4–8.6 [`src/FormForge.Api/Common/Endpoints/RouteGroupExtensions.cs:106`] — deferred, pre-existing
- [x] [Review][Defer] Write endpoints share `"admin"` rate-limit bucket with reads — consistent with existing route-group pattern; consider a separate `"data-write"` bucket when real handlers are built in Stories 8.4–8.6 [`src/FormForge.Api/Program.cs:675`] — deferred, pre-existing

---

### Change Log

| Date | Change |
|------|--------|
| 2026-06-03 | Story 8.2 implemented: dataset-management permission wired into `EffectivePermissions`, `RequireDatasetManagement()` filter, `/api/datasets` stub route group, role API + Admin Roles toggle, and `canManageDatasets` on `/api/users/me/permissions`. 17 new integration tests; full backend suite green except 2 pre-existing audit failures. Status → review. |
