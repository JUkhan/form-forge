# Story 3.11: Declare and Enforce Component Mode (CRUD / VIEW)

Status: done

## Story

As a Platform Admin,
I want to set a component's mode to CRUD or VIEW at creation,
so that display-only components don't create database tables and data-bearing components do.

## Acceptance Criteria

**AC-1 — mode required on creation**
Given I POST `/api/designers`
When the payload omits `mode` or supplies a value other than `CRUD` | `VIEW`
Then the response is HTTP 422 (mode is required and must be `CRUD` or `VIEW`)

**AC-2 — mode persisted on `component_schemas`**
Given a Designer is created
When the record is persisted
Then `mode` is stored on `component_schemas.mode` (`TEXT NOT NULL`, `CHECK (mode IN ('CRUD','VIEW'))`, per Architecture Decision 1.8)
And the column ships in the EF Core migration with existing rows backfilled to `'CRUD'` via the column DEFAULT in the same transaction (FR-54 AC-6)

**AC-3 — mode is immutable**
Given an existing Designer
When any update path attempts to change its `mode`
Then the API rejects the change → HTTP 422 (mode is immutable for the life of the component; FR-54 AC-2)
Note: current update paths (`UpdateVersionAsync`, `DuplicateAsync`) don't accept a `mode` in their request shapes — this is naturally protected. The immutability guard is a defensive check in the service layer.

**AC-4 — VIEW binding skips provisioning**
Given a `VIEW`-mode component is bound to a Menu Item
When the binding is saved
Then Table Provisioning is skipped entirely — no `ProvisioningJob` is enqueued, no CREATE/ALTER TABLE runs, and no `schema_audit_log` DDL entry is written
And `provisioningStatus` is set to `"NotApplicable"` and returned in the admin UI as "Not applicable (view-only)"

**AC-5 — VIEW binding exposes no data endpoints**
Given a `VIEW`-mode component bound to a Menu Item
When an end-user navigates to its data route
Then the page renders the DynamicComponent read-only (preview mode — same as `ComponentPreviewModal`) — no record list, no "New Record" control, no submit/save
And a direct request to `/api/data/{designerId}` (any verb) returns HTTP 404 `{ code: "TABLE_NOT_PROVISIONED" }` because VIEW designers are never provisioned and therefore never registered in the schema registry
And no `/api/data/{designerId}` calls are made by the UI

**AC-6 — VIEW access via allowedRoles only**
Given a `VIEW`-mode component
When access is evaluated
Then the per-Resource CRUD flags (FR-2 / `CrudFlags`) are not consulted
And access is governed solely by the bound Menu Item's `allowedRoles` (FR-19, FR-54 / Decision 2.2)

**AC-7 — Property pickers list CRUD-mode only**
Given the Dropdown "Source component" picker (when `optionsSource === "designer"`) and the Repeater "Row form — Component" picker
When they list candidate components
Then only `CRUD`-mode components appear (VIEW-mode components are excluded)
And a server-side guard independently rejects a VIEW reference on Save → HTTP 422 `{ code: "VIEW_REFERENCE_REJECTED" }` (FR-54 AC-5 / Architecture Decision 4.11)

**AC-8 — Library shows mode badge**
Given I navigate to the Designer Library
When the page renders
Then each row shows a `Mode` badge (`CRUD` | `VIEW`) alongside the existing Status chip (per FR-54 / FR-14 AC-1)

**AC-9 — Creation form requires mode selection**
Given the Designer Library "New Designer" dialog
When it renders
Then a `mode` field is shown as a required selector (`CRUD` | `VIEW`) with no default — the user must explicitly choose before the Create button is enabled
And omitting mode or supplying any other value → HTTP 422

## Tasks / Subtasks

- [x] Task 1: Backend — EF Core: add `mode` column to `component_schemas` (AC-2)
  - [x] Add `public string Mode { get; set; } = "CRUD";` to `ComponentSchema.cs`
  - [x] Add EF configuration in `FormForgeDbContext.cs` under the `ComponentSchema` entity block (lines ~172–191):
        ```csharp
        e.Property(s => s.Mode).HasColumnName("mode").IsRequired().HasMaxLength(5).HasDefaultValue("CRUD");
        ```
  - [x] Generate EF Core migration: `dotnet ef migrations add AddComponentSchemaMode --project src/FormForge.Api --startup-project src/FormForge.Api`
        The generated `Up()` must include:
        ```csharp
        migrationBuilder.AddColumn<string>(
            name: "mode",
            table: "component_schemas",
            type: "character varying(5)",
            maxLength: 5,
            nullable: false,
            defaultValue: "CRUD");
        migrationBuilder.Sql(
            "ALTER TABLE component_schemas ADD CONSTRAINT ck_component_schemas_mode CHECK (mode IN ('CRUD', 'VIEW'));");
        ```
        The `AddColumn` default `"CRUD"` backfills all existing rows in the same transaction (FR-54 AC-6). No explicit UPDATE statement is needed — the DEFAULT handles it.
        The `Down()` must drop the constraint before dropping the column:
        ```csharp
        migrationBuilder.Sql("ALTER TABLE component_schemas DROP CONSTRAINT IF EXISTS ck_component_schemas_mode;");
        migrationBuilder.DropColumn(name: "mode", table: "component_schemas");
        ```

- [x] Task 2: Backend — DTO updates (AC-1, AC-2, AC-8)
  - [x] Update `CreateDesignerRequest.cs`:
        ```csharp
        // Before: internal sealed record CreateDesignerRequest(string DesignerId, string DisplayName);
        internal sealed record CreateDesignerRequest(string DesignerId, string DisplayName, string Mode);
        ```
  - [x] Update `CreateDesignerRequestValidator.cs` — add Mode validation after the existing DisplayName rule:
        ```csharp
        RuleFor(x => x.Mode)
            .NotEmpty().WithMessage("mode is required.")
            .Must(m => m == "CRUD" || m == "VIEW")
            .WithMessage("mode must be 'CRUD' or 'VIEW'.");
        ```
        Note: FluentValidation's generic 422 body is fine here (no special error code needed for mode — unlike designerId where the AC-2 code lives in the service layer).
  - [x] Update `DesignerResponse.cs` — add `string Mode` after `DisplayName`:
        ```csharp
        internal sealed record DesignerResponse(
            string DesignerId,
            string DisplayName,
            string Mode,    // "CRUD" | "VIEW" — FR-54
            string Status,
            int LatestVersion,
            JsonNode? RootElement,
            DateTimeOffset CreatedAt,
            DateTimeOffset? UpdatedAt,
            DateTimeOffset? PublishedAt,
            IReadOnlyList<DesignerVersionSummary>? Versions = null);
        ```
        The comment at the top of `DesignerResponse.cs` says **"Shape MUST match ComponentSchemaDto in web/src/types/designer.ts exactly."** — update the frontend type in Task 7.
  - [x] Update `DesignerListItem.cs` — add `string Mode` after `DisplayName`:
        ```csharp
        internal sealed record DesignerListItem(
            string DesignerId,
            string DisplayName,
            string Mode,    // "CRUD" | "VIEW" — FR-54
            string Status,
            int LatestVersion,
            DateTimeOffset CreatedAt,
            DateTimeOffset? UpdatedAt,
            string? CreatorDisplayName);
        ```
        The comment says **"Shape MUST match ComponentSchemaListItem in web/src/types/designer.ts exactly."** — update the frontend type in Task 7.

- [x] Task 3: Backend — wire mode through DesignerService (AC-1, AC-2, AC-3, AC-7)
  - [x] `CreateAsync` — add `Mode = request.Mode` to the `ComponentSchema` initializer:
        ```csharp
        var schema = new ComponentSchema
        {
            DesignerId = designerId,
            DisplayName = request.DisplayName.Trim(),
            Mode = request.Mode,   // FR-54 — persisted on component_schemas.mode
            CreatedBy = createdBy,
            CreatedAt = now,
        };
        ```
  - [x] `ListAsync` — add `s.Mode` to the `DesignerListItem` projection at line ~199:
        ```csharp
        .Select(s => new DesignerListItem(
            s.DesignerId,
            s.DisplayName,
            s.Mode,    // add this
            s.Versions.OrderByDescending(v => v.Version).Select(v => v.Status).FirstOrDefault() ?? "Draft",
            ...))
        ```
  - [x] `ToResponse` (private static, line ~602) — add `schema.Mode` as the third argument:
        ```csharp
        return new DesignerResponse(
            schema.DesignerId,
            schema.DisplayName,
            schema.Mode,    // add this
            version.Status,
            ...);
        ```
  - [x] `DuplicateAsync` — copy mode from the source schema when building `newSchema`:
        ```csharp
        var newSchema = new ComponentSchema
        {
            DesignerId = candidate,
            DisplayName = $"Copy of {source.DisplayName}",
            Mode = source.Mode,   // duplicate inherits the source's mode (FR-54 AC-2 — immutable, so copy as-is)
            CreatedBy = createdBy,
            CreatedAt = now,
        };
        ```
        The `source` variable (the `ComponentSchema` loaded at the top of `DuplicateAsync`) must include `Mode`. Verify the query loads the full entity (it uses `Include` for versions — check that `Mode` is a scalar property on the loaded entity, not a navigation; it is, so it's already included).
  - [x] Server-side guard for VIEW property-picker references (AC-7) — add to `SaveVersionAsync` AND `UpdateVersionAsync`, after fieldKey validation and before DB write:
        ```csharp
        // FR-54 AC-5: reject a VIEW-mode reference in a Dropdown or Repeater
        var viewRefError = await FindViewReferenceAsync(rootElement, db, ct).ConfigureAwait(false);
        if (viewRefError is not null)
            return new SaveVersionResult(SaveVersionOutcome.ViewReferenceRejected, ViewReferenceDesignerId: viewRefError);
        ```
        Add the helper (private static async):
        ```csharp
        // Returns the first VIEW-mode designerId found in a Repeater.rowDesignerId or
        // Dropdown.optionsDesignerId property, or null if none.
        private static async Task<string?> FindViewReferenceAsync(
            JsonNode? rootElement, FormForgeDbContext db, CancellationToken ct)
        {
            if (rootElement is null) return null;

            var referencedIds = new HashSet<string>(StringComparer.Ordinal);
            CollectDesignerRefs(rootElement, referencedIds);
            if (referencedIds.Count == 0) return null;

            return await db.ComponentSchemas
                .AsNoTracking()
                .Where(s => referencedIds.Contains(s.DesignerId) && s.Mode == "VIEW")
                .Select(s => s.DesignerId)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
        }

        private static void CollectDesignerRefs(JsonNode node, HashSet<string> ids)
        {
            if (node is JsonObject obj)
            {
                // Repeater: properties.rowDesignerId
                var t = obj["type"]?.GetValue<string>();
                var props = obj["properties"] as JsonObject;
                if (props is not null)
                {
                    if (t == "Repeater")
                    {
                        var rowId = props["rowDesignerId"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(rowId)) ids.Add(rowId);
                    }
                    if (t == "Dropdown")
                    {
                        var src = props["optionsSource"]?.GetValue<string>();
                        var optId = props["optionsDesignerId"]?.GetValue<string>();
                        if (src == "designer" && !string.IsNullOrEmpty(optId)) ids.Add(optId);
                    }
                }
                // Recurse into children
                if (obj["children"] is JsonArray children)
                    foreach (var child in children) if (child is not null) CollectDesignerRefs(child, ids);
            }
        }
        ```
        Add the new outcome and result field:
        ```csharp
        internal enum SaveVersionOutcome { Success, DesignerNotFound, FieldKeyValidationFailed, VersionConflict, ViewReferenceRejected }
        internal sealed record SaveVersionResult(
            SaveVersionOutcome Outcome,
            IReadOnlyList<FieldKeyValidationError>? FieldKeyErrors = null,
            string? ViewReferenceDesignerId = null,    // non-null only for ViewReferenceRejected
            DesignerResponse? Designer = null);
        // Mirror changes for UpdateVersionOutcome / UpdateVersionResult
        ```
        Add a new `DesignerEndpoints.cs` problem method:
        ```csharp
        private static IResult ViewReferenceRejectedProblem(string referencedId) =>
            Results.Problem(
                detail: $"Designer '{referencedId}' is VIEW-mode and cannot be referenced as a data source.",
                title: "VIEW-mode designer cannot be a data source",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "VIEW_REFERENCE_REJECTED",
                    ["messageKey"] = "designers.viewReferenceRejected",
                    ["designerId"] = referencedId,
                });
        ```
        And wire the new outcome in `SaveVersionHandler` / `UpdateVersionHandler`:
        ```csharp
        SaveVersionOutcome.ViewReferenceRejected => ViewReferenceRejectedProblem(result.ViewReferenceDesignerId!),
        ```

- [x] Task 4: Backend — `MenuService.BindDesignerAsync`: VIEW mode gate (AC-4)
  - [x] In `MenuService.BindDesignerAsync` (lines ~482–552), after the `schemaVersion.Status != "Published"` check and **before** the cycle detector and pgType validator calls, insert the VIEW mode gate:
        ```csharp
        // FR-54 AC-3: VIEW-mode components are not provisioned — skip DDL and mark NotApplicable.
        // Checked BEFORE cycle detection and pgType validation (both are DDL-prep concerns
        // irrelevant for VIEW; skipping them avoids unnecessary DB round-trips).
        var designerMode = await db.ComponentSchemas
            .AsNoTracking()
            .Where(s => s.DesignerId == designerId)
            .Select(s => s.Mode)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (designerMode == "VIEW")
        {
            menu.DesignerId = designerId;
            menu.BoundVersion = version;
            menu.ProvisioningStatus = "NotApplicable";
            menu.ProvisioningError = null;
            menu.RoutePath = null;
            menu.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await cache.InvalidateAsync(CancellationToken.None).ConfigureAwait(false);
            eventBus.Publish(new MenuBindingCreated(designerId));
            // Do NOT call provisioning.EnqueueAsync — VIEW bindings never provision a table.
            return new BindMenuResult(BindMenuOutcome.Success);
        }
        ```
        Note: `designerMode` will be `null` (not found) only if the ComponentSchema row doesn't exist — but we already verified `schemaVersion` exists and has the same `designerId`, so the ComponentSchema row is guaranteed to exist. If somehow `null` (data integrity break), fall through to the CRUD provisioning path (safe: provisioning will fail gracefully if no table can be built).

- [x] Task 5: Backend — MenuResponse: surface "NotApplicable" in admin UI (AC-4)
  - [x] Verify that `MenuResponse.cs` (and `MenuAdminEndpoints.cs`) already returns `provisioningStatus` as a string — it does (`"Pending" | "Success" | "Error" | null` per `Menu.cs:20`). Adding `"NotApplicable"` requires no schema changes; it's a new string value flowing through the existing nullable string property.
  - [x] Verify that the frontend `menus` admin page displays `provisioningStatus` and add the "Not applicable (view-only)" display label for `"NotApplicable"` in `en.json` (see Task 12).

- [x] Task 6: Backend — DynamicDataEndpoints: VIEW mode gate via schema registry miss (AC-5)
  - [x] No code change required. The schema registry gate is already implicit: VIEW designers are never provisioned → never registered → `schemaRegistry.GetAsync(designerId)` returns null → existing "TABLE_NOT_PROVISIONED" 404 path fires.
  - [x] Verify the existing `ListRecordsHandler` (and other handlers) do short-circuit with `TABLE_NOT_PROVISIONED` when the schema is not in the registry. Search `DynamicDataEndpoints.cs` for `TABLE_NOT_PROVISIONED` to confirm the guard exists. If it doesn't exist yet (i.e., the registry is only used to get columns and the 404 comes from a missing table), add an explicit check:
        ```csharp
        var schema = await schemaRegistry.GetAsync(designerId, ct).ConfigureAwait(false);
        if (schema is null)
            return TableNotProvisionedProblem();
        ```
        Where `TableNotProvisionedProblem()` returns HTTP 404 `{ code: "TABLE_NOT_PROVISIONED" }`.
  - [x] **Do NOT add an explicit mode column check** in the data endpoints — the registry miss is the architecturally correct gate (Decision 1.8: "A registry miss for a VIEW designer is expected and resolves to the 404 above rather than a lazy population attempt.").

- [x] Task 7: Frontend — types and API layer (AC-1, AC-2, AC-7, AC-8, AC-9)
  - [x] Update `web/src/types/designer.ts`:
        - Add `mode: 'CRUD' | 'VIEW'` to `ComponentSchemaDto` after `displayName`
        - Add `mode: 'CRUD' | 'VIEW'` to `ComponentSchemaListItem` after `displayName`
        - `ComponentSchemaVersion` does **not** get a mode field (mode lives on the schema, not versions)
  - [x] Update `web/src/features/designer/designerApi.ts` — `createSchema` now requires `mode`:
        ```typescript
        createSchema: (body: { designerId: string; displayName: string; mode: 'CRUD' | 'VIEW' }) =>
            httpClient.post<ComponentSchemaDto>('/api/designers', body),
        ```

- [x] Task 8: Frontend — `CreateSchemaDialog` in `designer.library.tsx`: add mode selector (AC-9)
  - [x] Extend the Zod schema `createSchemaSchema` (line ~608) to require mode:
        ```typescript
        const createSchemaSchema = z.object({
          designerId: z.string().trim().min(1).max(63).regex(
            /^[a-z_][a-z0-9_]{0,62}$/,
            'Use lowercase letters, digits, and underscores. ...',
          ),
          displayName: z.string().trim().min(1).max(200),
          mode: z.enum(['CRUD', 'VIEW'], { required_error: 'Select a mode.' }),
        })
        ```
  - [x] In `CreateSchemaDialog`, switch `useForm` to use react-hook-form's `Controller` for the new `mode` field (shadcn `RadioGroup` or `Select`). Use `RadioGroup` for clarity (two options, no default):
        ```tsx
        // Inside the dialog form body, after the displayName field:
        <div className="space-y-1.5">
          <label className="text-sm font-medium">{t('designer.library.modeLabel')}</label>
          <Controller
            control={control}
            name="mode"
            render={({ field }) => (
              <RadioGroup value={field.value ?? ''} onValueChange={field.onChange} className="flex gap-4">
                <RadioGroupItem value="CRUD" id="mode-crud" />
                <label htmlFor="mode-crud" className="text-sm">
                  {t('designer.library.modeCRUD')} — {t('designer.library.modeCRUDDesc')}
                </label>
                <RadioGroupItem value="VIEW" id="mode-view" />
                <label htmlFor="mode-view" className="text-sm">
                  {t('designer.library.modeVIEW')} — {t('designer.library.modeVIEWDesc')}
                </label>
              </RadioGroup>
            )}
          />
          {errors.mode && <p className="text-xs text-destructive">{errors.mode.message}</p>}
        </div>
        ```
        Import `RadioGroup, RadioGroupItem` from `@/components/ui/radio-group` and `Controller` from `react-hook-form`.
  - [x] Pass `mode: values.mode` in the `createMutation.mutateAsync(values)` call — it's already part of the Zod type so `values.mode` is available. Update `onSubmit`:
        ```typescript
        const onSubmit = async (values: CreateSchemaFormValues) => {
          try {
            await createMutation.mutateAsync(values)  // values now includes mode
          ...
        ```
  - [x] The `createMutation.mutationFn` passes `body: CreateSchemaFormValues` directly to `designerApi.createSchema(body)` — since `createSchema` now accepts `mode`, no separate change is needed here.

- [x] Task 9: Frontend — Designer Library table: add mode badge (AC-8)
  - [x] Add `MODE_STYLES` constant alongside `STATUS_STYLES` (line ~72):
        ```typescript
        const MODE_STYLES: Record<'CRUD' | 'VIEW', { chip: string; labelKey: string }> = {
          CRUD: { chip: 'bg-blue-100 text-blue-800', labelKey: 'designer.library.modeCRUD' },
          VIEW: { chip: 'bg-violet-100 text-violet-800', labelKey: 'designer.library.modeVIEW' },
        }
        ```
  - [x] In the library table row render (wherever `STATUS_STYLES` is applied), add the mode badge next to or below the status chip:
        ```tsx
        {(() => {
          const ms = MODE_STYLES[row.mode as 'CRUD' | 'VIEW'] ?? MODE_STYLES.CRUD
          return (
            <span className={cn('inline-flex items-center rounded px-1.5 py-0.5 text-xs font-medium', ms.chip)}>
              {t(ms.labelKey)}
            </span>
          )
        })()}
        ```
  - [x] `ComponentSchemaListItem` now includes `mode: 'CRUD' | 'VIEW'`, so `row.mode` is available in the map.

- [x] Task 10: Frontend — `PropertyInspector.tsx`: filter pickers to CRUD-mode only (AC-7)
  - [x] In `RepeaterRowFormFields` (line ~500), change the `schemaOptions` filter:
        ```typescript
        // Before:
        const schemaOptions = allSchemas.filter(
          (s) => currentSchemaId === null || s.designerId !== currentSchemaId,
        )
        // After — exclude VIEW-mode AND self:
        const schemaOptions = allSchemas.filter(
          (s) => s.mode === 'CRUD' && (currentSchemaId === null || s.designerId !== currentSchemaId),
        )
        ```
  - [x] In `DropdownDesignerSourceFields` (line ~654), apply the same filter:
        ```typescript
        // Before:
        const schemaOptions = (schemasQuery.data?.data ?? []).filter(
          (s) => currentSchemaId === null || s.designerId !== currentSchemaId,
        )
        // After:
        const schemaOptions = (schemasQuery.data?.data ?? []).filter(
          (s) => s.mode === 'CRUD' && (currentSchemaId === null || s.designerId !== currentSchemaId),
        )
        ```
  - [x] `ComponentSchemaListItem` now has `mode` — the filter is a simple string comparison, no type guard needed since the backend contract guarantees `'CRUD' | 'VIEW'`.

- [x] Task 11: Frontend — `data.$designerId.tsx`: VIEW-mode rendering path (AC-5)
  - [x] In `RouteComponent` (line ~39), fetch the designer's schema to learn its mode:
        ```typescript
        const schemaQuery = useQuery({
          queryKey: ['designer', 'schema', designerId],
          queryFn: () => designerApi.getSchema(designerId),
          staleTime: 60_000,
        })
        ```
  - [x] After the existing `isChildActive` check (line ~54), add a VIEW-mode branch:
        ```tsx
        // VIEW-mode: render the schema read-only (preview). No record list, no data API calls.
        if (schemaQuery.data?.mode === 'VIEW') {
          return (
            <div className="flex flex-col gap-4 p-6">
              <h1 className="text-xl font-semibold">{schemaQuery.data.displayName}</h1>
              <DynamicComponent
                designerId={designerId}
                schema={schemaQuery.data.rootElement}
                // No onSave → read-only preview (matches ComponentPreviewModal pattern)
              />
            </div>
          )
        }
        ```
        Import `DynamicComponent` from `@/components/designer/DynamicComponent`.
  - [x] Handle loading / error states for `schemaQuery` before the VIEW branch (so the branch only fires when data is present):
        ```tsx
        if (schemaQuery.isLoading) return <div className="p-6 text-muted-foreground">{t('common.loading')}</div>
        if (schemaQuery.isError) return <div className="p-6 text-destructive">{t('errors.genericError')}</div>
        ```
  - [x] The CRUD path (RecordListPage) continues unchanged after the VIEW branch — no refactoring.
  - [x] **Do NOT** navigate to `data.$designerId.new.tsx` or `data.$designerId.$recordId.tsx` for VIEW-mode designers. These child routes will 404 at the API level anyway (no data endpoints), but the link/button should not be rendered in VIEW-mode. This is already guaranteed by the VIEW branch returning early with a read-only render.

- [x] Task 12: Frontend — i18n keys (AC-7, AC-8, AC-9)
  - [x] Add to `web/src/lib/i18n/locales/en.json` under `"designer"` → `"library"` section:
        ```json
        "modeLabel": "Component mode",
        "modeCRUD": "CRUD",
        "modeCRUDDesc": "Provisions a table; supports full data entry",
        "modeVIEW": "VIEW",
        "modeVIEWDesc": "Display-only; no table or data entry"
        ```
  - [x] Add to `"menus"` section (admin provisioning status display):
        ```json
        "provisioningNotApplicable": "Not applicable (view-only)"
        ```
  - [x] Add to `"designers"` section (toast / error messages):
        ```json
        "viewReferenceRejected": "A VIEW-mode component cannot be used as a data source."
        ```
  - [x] **Only `en.json` exists** — no other locale files. No change needed to the i18n-lint baseline (the pre-existing 1 failed / 242 passed is unrelated to this story).

- [x] Task 13: Tests
  - [x] **Backend — `CreateDesignerRequestValidatorTests.cs`**: add tests for the new `Mode` field:
        - Valid request with `mode: "CRUD"` passes
        - Valid request with `mode: "VIEW"` passes
        - Empty mode fails
        - Invalid mode (e.g., `"READ"`) fails
        Note: `CreateDesignerRequest` now takes 3 positional args — update ALL existing test instantiations in this file from `new CreateDesignerRequest("id", "Name")` to `new CreateDesignerRequest("id", "Name", "CRUD")`.
  - [x] **Backend — `DesignerIntegrationTests.cs`**: add / update tests:
        - `POST /api/designers` without `mode` → 422
        - `POST /api/designers` with `mode: "VIEW"` → 201, response includes `"mode": "VIEW"`
        - `GET /api/designers` list response includes `"mode"` field on each item
        - Bind a VIEW-mode designer to a menu → `provisioningStatus = "NotApplicable"`, no provisioning job enqueued
        - Save a designer version with a VIEW-mode `rowDesignerId` → 422 `VIEW_REFERENCE_REJECTED`
        Note: existing integration tests that create a designer will need their `POST` body updated to include `mode: "CRUD"`.
  - [x] **Frontend — `PropertyInspector` tests** (if they exist): update mocked `listSchemas` response to include `mode: 'CRUD'` on all items; add test that VIEW-mode items are excluded from picker options.
  - [x] **Frontend — `designer.library` tests** (if they exist): add test that mode badge renders.

- [x] Task 14: Build and verify
  - [x] `dotnet build` — 0 errors
  - [x] `dotnet test` — target: 762 + N new tests passed / 2 pre-existing failures (audit DELETE→405, unrelated)
  - [x] `pnpm run build` — 0 errors (Vite + `tsc -b --noEmit`)
  - [x] `pnpm run lint` — 0 new errors above the 23-error baseline (react-refresh fires on route files — expected)
  - [x] `pnpm run test` — 74 + N new tests passed / 0 new failures
        The pre-existing 1 failed / 242 passed from i18n-lint (missing `designer.inspector.placeholders.label`) is unrelated to this story

## Dev Notes

### What mode lives on — `component_schemas`, NOT `component_schema_versions`

The architecture spec (Decision 1.8) is explicit: **`component_schemas.mode`** — the mode lives on the root designer entity, not on individual versions. FR-54 says "immutable across versions." This means:
- `ComponentSchema.cs` gets the `Mode` property
- `ComponentSchemaVersion.cs` does NOT get a `Mode` property
- `DuplicateAsync` must copy `source.Mode` from the loaded `ComponentSchema` entity

The code explorer in earlier analysis incorrectly suggested putting mode on `ComponentSchemaVersion` — **do not do this.**

### `DesignerResponse.cs` / `DesignerListItem.cs` comment contract

Both files have a comment: **"Shape MUST match ComponentSchemaDto / ComponentSchemaListItem in web/src/types/designer.ts exactly."** Adding `string Mode` to the C# records and `mode: 'CRUD' | 'VIEW'` to the TypeScript interfaces simultaneously satisfies this contract. Both must be updated in the same PR.

### `CreateDesignerRequest` positional record — all existing tests break

`CreateDesignerRequest` is a C# positional record. Adding `string Mode` as the third constructor argument will break ALL existing call sites and tests that use the 2-argument form. Update them all:
- `CreateDesignerRequestValidatorTests.cs` — 4 test instantiations
- `DesignerIntegrationTests.cs` — any test that POSTs to `/api/designers`
- Any other test file instantiating `CreateDesignerRequest` directly

Pattern: `new CreateDesignerRequest("some_id", "Some Name")` → `new CreateDesignerRequest("some_id", "Some Name", "CRUD")`

### ToResponse and ListAsync projections — add `schema.Mode` in the right position

`ToResponse` (line ~602) builds `DesignerResponse` as a positional record. The `DesignerResponse` constructor now has `Mode` as the third positional argument (after `DisplayName`). Make sure the `schema.Mode` call appears in position 3 in `ToResponse`.

`ListAsync` projection (line ~199) builds `DesignerListItem` inline with a LINQ `.Select(...)`. `Mode` is the third positional argument in `DesignerListItem`. Pass `s.Mode` as position 3.

### VIEW mode gate ordering in `BindDesignerAsync`

Insert the VIEW gate AFTER the Published check and BEFORE cycle detection and pgType validation. The full ordering in `BindDesignerAsync` after this change:
1. Menu not found? → return MenuNotFound
2. SchemaVersion not found? → return DesignerNotFound
3. SchemaVersion.Status != "Published"? → return VersionNotPublished
4. **VIEW mode gate** — look up `ComponentSchema.Mode`; if `"VIEW"`, commit NotApplicable and return Success (skips steps 5–9)
5. Cycle detection (`cycleDetector.HasCycleAsync`)
6. PgType validation (`DesignerPgTypeValidator.FindFirstInvalid`)
7. Capture `fromVersion = menu.BoundVersion`
8. Set menu fields, `SaveChangesAsync`, `cache.InvalidateAsync`, publish event
9. `provisioning.EnqueueAsync(new ProvisioningJob(...))`

### DynamicComponent in data.$designerId.tsx — read-only via no `onSave` prop

The VIEW-mode data view reuses the read-only code path already used by `ComponentPreviewModal` (Story 3.5). Looking at `DynamicComponent.tsx` props, the `onSave` callback is optional — when absent, the component renders in read-only/preview mode (no Save button, no submit). Pass only `designerId` and `schema` (the rootElement from the fetched schema). No `onSave` = read-only. This is the established preview pattern.

### Server-side VIEW reference guard — property names in RootElement JSON

The RootElement is stored as JSON. The property names to scan:
- `Repeater` elements: `properties.rowDesignerId` (string) → the referenced designer
- `Dropdown` elements: `properties.optionsSource` (string, `"designer"` when designer-backed) + `properties.optionsDesignerId` (string) → the referenced designer

These are set by `updateProp(id, 'rowDesignerId', ...)` and `updateProp(id, 'optionsDesignerId', ...)` in `PropertyInspector.tsx` — confirmed by reading lines 521–530 and 680–686 of `PropertyInspector.tsx`.

### EF Core migration — CHECK constraint approach

EF Core 8 doesn't natively generate `CHECK` constraints from `HasCheckConstraint` for PostgreSQL without the `Npgsql.EntityFrameworkCore.PostgreSQL` check-constraint extension. The safest approach is raw SQL in the migration's `Up()` via `migrationBuilder.Sql(...)`. This is already used in the project for other DDL (see existing migrations for examples).

### Frontend `data.$designerId.tsx` hook ordering

TanStack Router's `useParams`, `useSearch`, `useNavigate`, `useMatchRoute` hooks must all fire before any early return (React rules-of-hooks). The VIEW-mode branch currently returns early AFTER the `isChildActive` branch. Structure:

```typescript
function RouteComponent() {
  const { designerId } = Route.useParams()
  const { page, pageSize, sort, filter, showDeleted } = Route.useSearch()
  const navigate = useNavigate({ from: '/data/$designerId' })
  const matchRoute = useMatchRoute()
  const schemaQuery = useQuery({ ... })  // ← new hook, must be before any return

  const isChildActive = ...
  if (isChildActive) return <Outlet />

  if (schemaQuery.isLoading) return <Loading />
  if (schemaQuery.isError) return <Error />
  if (schemaQuery.data?.mode === 'VIEW') return <ViewModeRender />

  return <RecordListPage .../>
}
```

All hooks before all conditional returns — this is the existing pattern in the file.

### `provisioningStatus = "NotApplicable"` — existing string field, no enum change

`Menu.cs` line 20: `public string? ProvisioningStatus { get; set; }  // "Pending" | "Success" | "Error" | null`

Just use `"NotApplicable"` as a new string value. No enum type to update — it's stringly-typed throughout. The frontend `menus` admin page currently renders the raw string — update the display label in `en.json` (Task 12) and wherever `provisioningStatus` is displayed in the menus admin UI.

### Architecture Compliance Summary

| Requirement | How satisfied |
|---|---|
| FR-54 AC-1 (mode required) | `CreateDesignerRequestValidator` — FluentValidation |
| FR-54 AC-2 (mode immutable) | No update paths accept `mode`; `DuplicateAsync` copies source mode |
| FR-54 AC-3 (VIEW skips provisioning) | `MenuService.BindDesignerAsync` VIEW gate → `NotApplicable` |
| FR-54 AC-4 (VIEW 404 on data endpoints) | Schema registry miss → TABLE_NOT_PROVISIONED 404 (implicit) |
| FR-54 AC-5 (CRUD-only pickers + server guard) | Frontend filter + `FindViewReferenceAsync` in service |
| FR-54 AC-6 (backfill existing rows) | EF migration `defaultValue: "CRUD"` |
| Decision 1.8 (`component_schemas.mode`) | EF entity + migration on `component_schemas`, not versions |
| Decision 4.10 (VIEW read-only DynamicComponent) | `data.$designerId.tsx` VIEW branch → DynamicComponent w/o `onSave` |
| Decision 4.11 (CRUD-only pickers) | `PropertyInspector.tsx` `s.mode === 'CRUD'` filter |

### Previous Story Learnings (from 3.10)

- **i18n keys only in `en.json`** — no other locale files.
- **Lint baseline is 23** — 0 new errors allowed. `react-refresh/only-export-components` fires on route files — normal, not new.
- **`vi.mock('react-i18next', ...)` identity pattern** — use for all new component tests (see `RepeaterRowDrawer.test.tsx:8-12`).
- **`cleanup` from `@testing-library/react`** in `afterEach` — required for all component tests.
- **Backend pre-existing failures**: 2 audit DELETE→405 tests (unrelated). Always subtract these from the failure count.
- **`pnpm run test` baseline: 74 passed** — add new tests on top.
- **`dotnet test` baseline: 762 passed / 2 failed** — add new tests on top.

### Project Structure — Files to Change / Create

**Backend new files:**
- EF Core migration file (auto-generated by `dotnet ef migrations add AddComponentSchemaMode`)

**Backend modified files:**
- `src/FormForge.Api/Domain/Entities/ComponentSchema.cs` — add `Mode` property
- `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` — add EF config for `mode`
- `src/FormForge.Api/Features/Designer/Dtos/CreateDesignerRequest.cs` — add `Mode` field
- `src/FormForge.Api/Features/Designer/Dtos/DesignerResponse.cs` — add `Mode` field
- `src/FormForge.Api/Features/Designer/Dtos/DesignerListItem.cs` — add `Mode` field
- `src/FormForge.Api/Features/Designer/Validators/CreateDesignerRequestValidator.cs` — add Mode validation
- `src/FormForge.Api/Features/Designer/DesignerService.cs` — wire mode through CreateAsync / ListAsync / ToResponse / DuplicateAsync; add ViewReferenceRejected guard
- `src/FormForge.Api/Features/Designer/DesignerEndpoints.cs` — add ViewReferenceRejectedProblem helper; wire new SaveVersionOutcome / UpdateVersionOutcome
- `src/FormForge.Api/Features/Menus/MenuService.cs` — add VIEW mode gate in BindDesignerAsync
- `src/FormForge.Api.Tests/Features/Designer/CreateDesignerRequestValidatorTests.cs` — add mode tests, update 2-arg constructors → 3-arg
- `src/FormForge.Api.Tests/Features/Designer/DesignerIntegrationTests.cs` — add mode tests, update POST bodies

**Frontend modified files:**
- `web/src/types/designer.ts` — add `mode` to `ComponentSchemaDto`, `ComponentSchemaListItem`
- `web/src/features/designer/designerApi.ts` — add `mode` to `createSchema` param
- `web/src/routes/_app/designer.library.tsx` — add mode to Zod schema, RadioGroup in dialog, mode badge in table
- `web/src/components/designer/PropertyInspector.tsx` — filter CRUD-only in `RepeaterRowFormFields` and `DropdownDesignerSourceFields`
- `web/src/routes/_app/data.$designerId.tsx` — add VIEW-mode branch with DynamicComponent read-only
- `web/src/lib/i18n/locales/en.json` — add mode labels and error messages

### References

- [Architecture Decision 1.8 — Component Mode `component_schemas.mode`]: architecture.md
- [Architecture Decision 4.10 — DynamicComponent VIEW read-only rendering]: architecture.md
- [Architecture Decision 4.11 — CRUD-only property pickers]: architecture.md
- [Architecture Decision 2.2 — VIEW mode access via allowedRoles only]: architecture.md
- [Architecture Decision 1.4 — Schema Registry (VIEW designers never registered)]: architecture.md
- [Architecture Decision 1.6 — Provisioning gate (VIEW skips EnqueueAsync)]: architecture.md
- [FR-54 — Component Mode spec]: epics.md
- `src/FormForge.Api/Domain/Entities/ComponentSchema.cs` — entity to modify
- `src/FormForge.Api/Features/Designer/Dtos/DesignerResponse.cs` — contract note: matches frontend DTO
- `src/FormForge.Api/Features/Designer/Dtos/DesignerListItem.cs` — contract note: matches frontend DTO
- `src/FormForge.Api/Features/Menus/MenuService.cs` lines 482–552 — `BindDesignerAsync` insert point
- `web/src/components/designer/PropertyInspector.tsx` lines 494–503 / 649–656 — filter targets
- `web/src/routes/_app/designer.library.tsx` lines 608–619 — Zod schema to extend
- `web/src/routes/_app/data.$designerId.tsx` — VIEW-mode branch insertion point

## Dev Agent Record

### Agent Model Used

claude-opus-4-8[1m] (Opus 4.8, 1M context)

### Debug Log References

- `dotnet build src/FormForge.Api` — 0 warnings, 0 errors
- `dotnet ef migrations add AddComponentSchemaMode` — generated; CHECK constraint + ThrowIfNull guards added by hand to match project convention
- `dotnet test` — Passed: 776, Failed: 2 (the 2 failures are the pre-existing audit DELETE→405 tests, unrelated to this story; baseline was 762 passed so all ~14 new tests pass)
- `pnpm run build` — 0 errors (Vite + tsc)
- `pnpm run lint` — no new errors above baseline (the single error on `designer.library.tsx:67` is the expected `react-refresh/only-export-components` that fires on every route file)
- `pnpm run test` — 242 passed / 1 failed (the 1 failure is the pre-existing i18n-lint `designer.inspector.placeholders.label` missing key, unrelated)
- `node scripts/i18n-check.mjs` — restored to the single pre-existing missing key after adding `common.loading` (which I had newly referenced in `data.$designerId.tsx`)

### Completion Notes List

- **Mode lives on `component_schemas`** (not versions) per Decision 1.8 — `ComponentSchema.Mode` (TEXT NOT NULL, CHECK IN ('CRUD','VIEW'), DEFAULT 'CRUD'). Migration backfills existing rows via the column DEFAULT in the same transaction (FR-54 AC-6).
- **AC-1** mode required + must be CRUD|VIEW via `CreateDesignerRequestValidator` (generic 422).
- **AC-2** mode persisted and round-tripped through `CreateAsync`/`ListAsync`/`ToResponse`/`DuplicateAsync`.
- **AC-3** immutability is naturally protected (no update path accepts `mode`); the defensive guard noted in the AC was unnecessary because the request shapes have no `mode` field.
- **AC-4** VIEW gate in `MenuService.BindDesignerAsync` sets `ProvisioningStatus = "NotApplicable"`, skips cycle/pgType checks and `EnqueueAsync` entirely, and returns Success synchronously.
- **AC-5** data endpoints need no change — a VIEW designer is never provisioned → never `ProvisioningStatus == "Success"` → `boundVersion` query returns null → existing `TABLE_NOT_PROVISIONED` 404 fires. Frontend `data.$designerId.tsx` renders a read-only `DynamicComponent` (no `onSave`) for VIEW mode and makes no data API calls.
- **AC-7** frontend pickers filter `s.mode === 'CRUD'`; independent server guard `FindViewReferenceAsync` scans Repeater `rowDesignerId` / designer-backed Dropdown `optionsDesignerId` in `SaveVersionAsync` AND `UpdateVersionAsync`, returning 422 `VIEW_REFERENCE_REJECTED`.
- **AC-8** Designer Library shows a `Mode` badge (new column) next to the Status chip.
- **AC-9** New Designer dialog has a required mode radio group (no default); the Create button stays disabled until the form (including mode) is valid.
- **Deviations from the story's literal code:** (1) `DynamicComponent`'s `schema` prop takes the full `ComponentSchemaDto`, not `rootElement` — passed `schemaQuery.data` accordingly. (2) No `radio-group` shadcn component exists, so the mode selector uses native `<input type="radio">` with react-hook-form `register` instead of `Controller`/`RadioGroup` (no new dependency). (3) Zod v4 in this repo uses `{ message }` not `required_error`. (4) Added `'NotApplicable'` to the frontend `ProvisioningStatus` union + `ProvisioningStatusBadge` and a new `common.loading` i18n key.
- **Test fan-out:** `CreateDesignerRequest` is a positional record, so `mode` had to be threaded through every `CreateDesignerViaApiAsync` helper and direct create-POST body across 10 backend test files (Designer, Provisioning ×2, 6× DynamicCrud, SchemaAuditLog) plus the frontend `makeRow` factory and 3 `ComponentSchemaDto` test fixtures + `ComponentPreviewModal`.

### File List

**Backend — new:**
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260602012319_AddComponentSchemaMode.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260602012319_AddComponentSchemaMode.Designer.cs`

**Backend — modified:**
- `src/FormForge.Api/Domain/Entities/ComponentSchema.cs`
- `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/FormForgeDbContextModelSnapshot.cs`
- `src/FormForge.Api/Features/Designer/Dtos/CreateDesignerRequest.cs`
- `src/FormForge.Api/Features/Designer/Dtos/DesignerResponse.cs`
- `src/FormForge.Api/Features/Designer/Dtos/DesignerListItem.cs`
- `src/FormForge.Api/Features/Designer/Validators/CreateDesignerRequestValidator.cs`
- `src/FormForge.Api/Features/Designer/DesignerService.cs`
- `src/FormForge.Api/Features/Designer/DesignerEndpoints.cs`
- `src/FormForge.Api/Features/Menus/MenuService.cs`

**Backend — tests modified:**
- `src/FormForge.Api.Tests/Features/Designer/CreateDesignerRequestValidatorTests.cs`
- `src/FormForge.Api.Tests/Features/Designer/DesignerIntegrationTests.cs`
- `src/FormForge.Api.Tests/Features/Provisioning/ProvisioningIntegrationTests.cs`
- `src/FormForge.Api.Tests/Features/Provisioning/ProvisioningRecoveryIntegrationTests.cs`
- `src/FormForge.Api.Tests/Features/Audit/SchemaAuditLogIntegrationTests.cs`
- `src/FormForge.Api.Tests/Features/DynamicCrud/DynamicCrudIntegrationTests.cs`
- `src/FormForge.Api.Tests/Features/DynamicCrud/CreateRecordIntegrationTests.cs`
- `src/FormForge.Api.Tests/Features/DynamicCrud/UpdateRecordIntegrationTests.cs`
- `src/FormForge.Api.Tests/Features/DynamicCrud/GetRecordIntegrationTests.cs`
- `src/FormForge.Api.Tests/Features/DynamicCrud/SoftDeleteIntegrationTests.cs`
- `src/FormForge.Api.Tests/Features/DynamicCrud/RestoreIntegrationTests.cs`
- `src/FormForge.Api.Tests/Features/DynamicCrud/RepeaterWriteIntegrationTests.cs`

**Frontend — modified:**
- `web/src/types/designer.ts`
- `web/src/features/designer/designerApi.ts`
- `web/src/routes/_app/designer.library.tsx`
- `web/src/components/designer/PropertyInspector.tsx`
- `web/src/routes/_app/data.$designerId.tsx`
- `web/src/components/designer/ComponentPreviewModal.tsx`
- `web/src/features/menu/types.ts`
- `web/src/components/shared/ProvisioningStatusBadge.tsx`
- `web/src/lib/i18n/locales/en.json`

**Frontend — tests modified:**
- `web/src/routes/_app/-designer.library.test.tsx`
- `web/src/components/designer/__tests__/DynamicComponentA11y.test.tsx`
- `web/src/components/designer/__tests__/DynamicComponentChildren.test.tsx`
- `web/src/store/__tests__/designerCanvas.test.ts`

### Review Findings

- [x] [Review][Decision] AC-3 mode immutability: natural protection accepted — no update path accepts `mode`; explicit defensive guard omitted as dead code. Resolved 2026-06-02.

- [x] [Review][Patch] RetryBindingAsync bypasses VIEW-mode gate — fixed: guard on `ProvisioningStatus == "NotApplicable"` before setting Pending [src/FormForge.Api/Features/Menus/MenuService.cs]
- [x] [Review][Patch] ProvisioningStatusBadge crashes on unknown status — fixed: `if (!spec) return null` guard added after styles lookup [web/src/components/shared/ProvisioningStatusBadge.tsx]
- [x] [Review][Patch] ProvisioningStatusBadge NotApplicable uses `var(--muted)` as background — fixed: changed to `hsl(var(--muted))` / `hsl(var(--muted-foreground))` [web/src/components/shared/ProvisioningStatusBadge.tsx]
- [x] [Review][Patch] CreateSchemaDialog form not reset on X/Escape close — fixed: Dialog `onOpenChange` now calls `reset()` when `next === false` [web/src/routes/_app/designer.library.tsx]
- [x] [Review][Patch] PropertyInspector picker fetches only 100 designers and client-filters; with >100 designers, CRUD ones beyond page 1 are invisible — fixed: server-side `?mode=CRUD` filter added to `listSchemas` call + `ListAsync` + `ListDesignersHandler` [web/src/components/designer/PropertyInspector.tsx]
- [x] [Review][Patch] No test for VIEW-mode branch in data.$designerId.tsx — fixed: new `-data.$designerId.viewMode.test.tsx` with 4 tests covering VIEW render, CRUD render, loading, and error states [web/src/routes/_app/data.$designerId.tsx]
- [x] [Review][Patch] Migration comment cites AC-6 instead of AC-2 — fixed: comment corrected to "FR-54 AC-2" [src/FormForge.Api/Infrastructure/Persistence/Migrations/20260602012319_AddComponentSchemaMode.cs]
- [x] [Review][Patch] CollectDesignerRefs silently skips non-JsonObject children — fixed: `else if (node is JsonArray arr)` branch added to recurse into array nodes [src/FormForge.Api/Features/Designer/DesignerService.cs]

- [x] [Review][Defer] VIEW designer child routes (/new, /$recordId) reachable via direct URL — isChildActive short-circuit returns Outlet before mode check; API-level TABLE_NOT_PROVISIONED 404 is the true defence; spec acknowledges this [web/src/routes/_app/data.$designerId.tsx] — deferred, pre-existing routing architecture
- [x] [Review][Defer] AC-6 allowedRoles enforcement has no explicit test — existing GetNavMenusForUserAsync already gates by allowedRoles; no new test added asserting CRUD flags are bypassed for VIEW items; pre-existing pattern [src/FormForge.Api/Features/Menus/MenuService.cs] — deferred, pre-existing
- [x] [Review][Defer] Schema useQuery fires on every child route navigation — query is mounted before isChildActive early-return so a network request fires even when Outlet is rendered; query result is unused in that path [web/src/routes/_app/data.$designerId.tsx] — deferred, performance only

### Change Log

| Date | Change |
|---|---|
| 2026-06-02 | Implemented Story 3.11 — Component Mode (CRUD/VIEW): `component_schemas.mode` column + migration; mode required on create and immutable; VIEW bindings skip provisioning (NotApplicable) and expose no data endpoints; VIEW components excluded from Dropdown/Repeater pickers with an independent server-side `VIEW_REFERENCE_REJECTED` guard; Library mode badge; required mode selector in the New Designer dialog; VIEW read-only render in the data route. All ACs satisfied; full backend + frontend suites green against documented baselines. |
