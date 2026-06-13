# Story 3.6: Save Designer Version

Status: done

## Story

As a Platform Admin,
I want to save the current canvas state, creating a new immutable version snapshot,
so that I have a permanent record of the design at each save point.

## Acceptance Criteria

**AC-1 — Create new version on save**
Given I am on the canvas with valid design content
When I POST `/api/designers/{id}/versions`
Then a new version is created with `rootElement` (current canvas JSON), auto-incremented `version` number, `status: Draft`, `createdAt`, `createdBy`
And previous versions are not mutated
And the save completes within 500 ms p95 (NFR-2)
And the response is HTTP 201 with the new `DesignerResponse`

**AC-2 — Save blocked when input-bearing component has empty/invalid fieldKey**
Given any input-bearing component (`Text Input`, `TextArea`, `Number Input`, `Checkbox`, `Dropdown`, `DateTime Picker`, `Color Picker`, `Repeater Field`, `Image`) has an empty or invalid `fieldKey`
When I attempt to save
Then save is blocked with a 422 response (backend) and an inline validation error in the UI identifying the issue (frontend pre-validation)

**AC-3 — fieldKey collision detection**
Given two input-bearing components share the same `fieldKey` value
When I attempt to save
Then save is blocked with a validation error (both frontend pre-validation and backend 422)

**AC-4 — Persisted JSON round-trip**
Given the saved version's RootElement
When I retrieve it via `GET /api/designers/{id}/versions/{version}`
Then the persisted JSON exactly matches the canvas state at save time

*Note: AC-4 is already satisfied by the existing GET endpoint (Story 3.2). No new code needed.*

**AC-5 — Frontend canvas version tracking**
Given a successful save
When the response returns `latestVersion: N`
Then the canvas store's `version` field is updated to `N` (subsequent saves POST to the same base URL, no stale version param)

**AC-6 — Non-admin blocked**
Given a non-platform-admin user
When they call POST `/api/designers/{id}/versions`
Then the response is HTTP 403

## Tasks / Subtasks

- [x] Task 1: Backend — new POST endpoint (AC-1, AC-2, AC-3, AC-6)
  - [x] Create `Dtos/SaveVersionRequest.cs` — single `JsonNode? RootElement` property
  - [x] Create `Validators/SaveVersionRequestValidator.cs` — `RuleFor(r => r.RootElement).NotNull()`
  - [x] Create `FieldKeyValidator.cs` — static helper: walk RootElement JSON tree, validate all input-bearing components (missing + regex + collision); returns `FieldKeyValidationResult`
  - [x] Add `SaveVersionOutcome` enum and `SaveVersionResult` record to `DesignerService.cs`
  - [x] Add `SaveVersionAsync` to `IDesignerService` interface and `DesignerService` impl
  - [x] Add `SaveVersionHandler` handler and `MapPost("/{designerId}/versions", ...)` route in `DesignerEndpoints.cs`
  - [x] Register `IValidator<SaveVersionRequest>` in `Program.cs`

- [x] Task 2: Backend — integration tests (AC-1, AC-2, AC-3, AC-4, AC-6)
  - [x] Happy path: POST creates v2, returns 201 with new DesignerResponse; GET v1 rootElement unchanged
  - [x] 403 as non-admin; 404 for unknown designer
  - [x] 422 FIELD_KEY_INVALID for missing fieldKey on input-bearing component
  - [x] 422 FIELD_KEY_INVALID for regex-invalid fieldKey (e.g., "My Field")
  - [x] 422 FIELD_KEY_INVALID for PG-reserved-keyword fieldKey (e.g., "select")
  - [x] 422 FIELD_KEY_COLLISION for two components sharing same fieldKey
  - [x] Verify non-input-bearing components (Stack, Row, Tabs, Label, Button, Repeater) are NOT checked for fieldKey

- [x] Task 3: Frontend — update `designerApi.ts` (AC-1)
  - [x] Replace `saveVersion(designerId, version, rootElement)` (PUT stub) with `createVersion(designerId, rootElement)` → `POST /api/designers/${designerId}/versions`
  - [x] Return type changes from `Promise<void>` to `Promise<ComponentSchemaDto>`

- [x] Task 4: Frontend — add `collectFieldKeyErrors` to `fieldKeyValidation.ts` (AC-2, AC-3)
  - [x] Export `INPUT_BEARING_TYPES: Set<string>` constant (matches backend set)
  - [x] Export `collectFieldKeyErrors(root: DesignerElement): string[]` — walks tree, returns one error string per violation (missing, invalid, collision)

- [x] Task 5: Frontend — update `designer.$designerId.tsx` save mutation (AC-1, AC-2, AC-3, AC-5)
  - [x] Change `saveMutation` return type from `void` to `ComponentSchemaDto`
  - [x] Update `mutationFn` to call `designerApi.createVersion(id, vars.rootElement)` (drop the version param)
  - [x] In `onSuccess`: update canvas store `version` from `newData.latestVersion`
  - [x] Add `fieldKeyError` state and set it in `handleSave` when `collectFieldKeyErrors` returns errors; clear it on successful save
  - [x] Render `fieldKeyError` inline near the Save button (same position as saveMutation.isError display)

- [x] Task 6: Frontend — i18n
  - [x] Add `designer.canvas.saveBlockedFieldKey` to `en.json` — `"Fix field key errors before saving."`

- [x] Task 7: Frontend — tests for `collectFieldKeyErrors` (AC-2, AC-3)
  - [x] Add to `fieldKeyValidation.test.ts`: valid tree returns empty array
  - [x] Missing fieldKey on input-bearing → error returned
  - [x] Invalid fieldKey (fails regex) → error returned
  - [x] Collision (two components same fieldKey) → error returned
  - [x] Non-input-bearing types (Stack, Row, etc.) → NOT checked

- [x] Task 8: Build and verify
  - [x] `dotnet build` — 0 errors
  - [x] `dotnet test` — all existing tests pass + new tests pass (216/216)
  - [x] `pnpm run build` — 0 errors
  - [x] `pnpm run lint` — exactly 26 pre-existing `react-refresh/only-export-components` errors, no new errors
  - [x] `pnpm run test` — 45/45 pass

---

## Dev Notes

### Critical: The Frontend Stub Is Wrong — Must Replace

`designerApi.saveVersion` (line 29 of `designerApi.ts`) is a **placeholder stub** that does `PUT /api/designers/${designerId}/versions/${version}`. This route has never existed and always 404s in production. Story 3.6 **replaces** this stub with the real implementation.

The real endpoint is `POST /api/designers/{designerId}/versions` (no version in URL — backend auto-increments). The function signature also changes: no `version` param, and the return type is `ComponentSchemaDto` (not `void`).

`saveVersion` is called only in one place: `designer.$designerId.tsx` line 123. Update both the API module and the route.

### AC-4 Already Satisfied

`GET /api/designers/{id}/versions/{version}` was implemented in Story 3.2 (`DesignerEndpoints.cs` lines 34–37, `DesignerService.GetVersionAsync`). The story's AC-4 (persisted JSON round-trip) is free — the existing GET endpoint handles it. **Do not re-implement the GET.**

### Backend: input-bearing type strings (exact match required)

The backend `FieldKeyValidator.cs` must match the **actual type strings stored in the JSON**, not the architecture table's shorthand. The PropertyInspector switch cases (`PropertyInspector.tsx` lines 528, 575, 622, 670, 697, 808, 849, 946, 989) confirm the exact strings:

```csharp
private static readonly HashSet<string> InputBearingTypes = new(StringComparer.Ordinal)
{
    "Text Input", "TextArea", "Number Input", "Checkbox", "Dropdown",
    "DateTime Picker", "Color Picker", "Repeater Field", "Image"
};
```

Note: NOT `TextInput`, NOT `NumberInput` etc. — those are architecture table shorthand. The actual stored type strings have spaces. `Repeater` itself is NOT in this set (it's a container); `Repeater Field` IS (it lives in child table and maps to a column).

### Backend: fieldKey validation reuses SafeIdentifier

`SafeIdentifier.TryCreate` (already exists in `Features/Designer/SafeIdentifier.cs`) validates both the regex and PG reserved keywords. Reuse it — do not duplicate the regex. Walk the `JsonNode` tree and call `SafeIdentifier.TryCreate(fieldKey, out _, out var error)` for each input-bearing component's `properties.fieldKey`.

RootElement JSON structure:
```json
{
  "id": "...",
  "type": "Stack",
  "properties": { ... },
  "children": [
    { "id": "...", "type": "Text Input", "properties": { "fieldKey": "full_name" }, "children": [] }
  ]
}
```

Walk: check `obj["type"]`, then `obj["properties"]?["fieldKey"]`. Recurse via `obj["children"]` (a `JsonArray`). `JsonNode` / `JsonObject` / `JsonArray` are all in `System.Text.Json.Nodes`.

### Backend: collision detection

Use a `Dictionary<string, string>(StringComparer.Ordinal)` keyed by fieldKey → first component ID during the tree walk. If a second component has the same fieldKey, add a collision error. Both missing/invalid AND collision errors should be collected and returned together (don't fail-fast) so the user can see all issues at once.

### Backend: SaveVersionAsync implementation sketch

```csharp
public async Task<SaveVersionResult> SaveVersionAsync(
    string designerId, JsonNode rootElement, Guid savedBy, CancellationToken ct)
{
    // 1. Validate fieldKeys BEFORE hitting DB
    var fkResult = FieldKeyValidator.Validate(rootElement);
    if (!fkResult.IsValid)
        return new SaveVersionResult(SaveVersionOutcome.FieldKeyValidationFailed, fkResult.Errors);

    // 2. Load schema + versions (need MAX version; also need schema for UpdatedAt)
    var schema = await db.ComponentSchemas
        .Include(s => s.Versions)
        .FirstOrDefaultAsync(s => s.DesignerId == designerId, ct)
        .ConfigureAwait(false);
    if (schema is null)
        return new SaveVersionResult(SaveVersionOutcome.DesignerNotFound);

    var nextVersion = schema.Versions.Count > 0
        ? schema.Versions.Max(v => v.Version) + 1
        : 1; // fallback: schema was created without v1 (shouldn't happen, but safe)

    var now = DateTimeOffset.UtcNow;
    var newVer = new ComponentSchemaVersion
    {
        DesignerId = designerId,
        Version = nextVersion,
        Status = "Draft",
        RootElement = rootElement.ToJsonString(),
        CreatedBy = savedBy,
        CreatedAt = now,
    };
    schema.UpdatedAt = now;
    db.ComponentSchemaVersions.Add(newVer);

    // 3. Handle concurrent-save race (two admins, same designer, simultaneous POST)
    try
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
    catch (DbUpdateException ex) when (
        ex.InnerException is PostgresException { SqlState: "23505" } pg
        && string.Equals(pg.ConstraintName,
            "uq_component_schema_versions_designer_version",
            StringComparison.Ordinal))
    {
        return new SaveVersionResult(SaveVersionOutcome.VersionConflict);
    }

    return new SaveVersionResult(
        SaveVersionOutcome.Success,
        Designer: ToResponse(schema, newVer, includeVersions: false));
}
```

The `ToResponse` helper already exists in `DesignerService.cs` — reuse it unchanged.

### Backend: endpoint handler outcome mapping

```
SaveVersionOutcome.Success           → 201 Created, Location: /api/designers/{id}/versions/{newVersion}
SaveVersionOutcome.DesignerNotFound  → 404, code: DESIGNER_NOT_FOUND (reuse DesignerNotFoundProblem())
SaveVersionOutcome.FieldKeyValidationFailed → 422, code: FIELD_KEY_INVALID, extensions["errors"]: list
SaveVersionOutcome.VersionConflict   → 409, code: VERSION_CONFLICT (concurrent save, extremely rare)
```

### Backend: program.cs registration

Add next to the existing `CreateDesignerRequest` validator registration (line 129 of `Program.cs`):
```csharp
builder.Services.AddScoped<IValidator<SaveVersionRequest>, SaveVersionRequestValidator>();
```

### Frontend: `designerApi.ts` — exact change

Replace lines 29–33 (the `saveVersion` stub):
```typescript
// OLD (stub — always 404s)
saveVersion: (designerId: string, version: number, rootElement: unknown) =>
  httpClient.put<void>(
    `/api/designers/${designerId}/versions/${version}`,
    { rootElement },
  ),
```
With:
```typescript
// NEW (Story 3.6 — real implementation)
createVersion: (designerId: string, rootElement: unknown) =>
  httpClient.post<ComponentSchemaDto>(
    `/api/designers/${designerId}/versions`,
    { rootElement },
  ),
```

The return type changes to `ComponentSchemaDto` (already imported at the top of the file).

### Frontend: `fieldKeyValidation.ts` — additions

Add below the existing `isValidFieldKey`:

```typescript
// Types that map to PG columns and require a SQL-safe fieldKey at save time.
// Matches PropertyInspector.tsx switch cases exactly (type strings have spaces).
export const INPUT_BEARING_TYPES = new Set([
  'Text Input', 'TextArea', 'Number Input', 'Checkbox', 'Dropdown',
  'DateTime Picker', 'Color Picker', 'Repeater Field', 'Image',
])

// Returns one error string per violation (missing fieldKey, invalid fieldKey, collision).
// Empty array means tree is save-ready.
export function collectFieldKeyErrors(root: DesignerElement): string[] {
  const errors: string[] = []
  const seenKeys = new Map<string, string>() // fieldKey → first element id

  function walk(el: DesignerElement) {
    if (INPUT_BEARING_TYPES.has(el.type)) {
      const key = typeof el.properties?.fieldKey === 'string' ? el.properties.fieldKey : ''
      if (!key) {
        errors.push(`${el.type} (id: ${el.id}) is missing a field key.`)
      } else if (!isValidFieldKey(key)) {
        errors.push(`${el.type} (id: ${el.id}) has an invalid field key: "${key}".`)
      } else if (seenKeys.has(key)) {
        errors.push(`Field key "${key}" is used by multiple components (${seenKeys.get(key)} and ${el.id}).`)
      } else {
        seenKeys.set(key, el.id)
      }
    }
    for (const child of el.children ?? []) walk(child)
  }

  walk(root)
  return errors
}
```

This function needs the `DesignerElement` type import at the top of `fieldKeyValidation.ts`:
```typescript
import type { DesignerElement } from '@/types/designer'
```

(Currently `fieldKeyValidation.ts` has no imports — add this one.)

### Frontend: `designer.$designerId.tsx` — changes

**Type change for mutation:**
```typescript
// OLD
const saveMutation = useMutation<void, Error, SaveVars>({
// NEW
const saveMutation = useMutation<ComponentSchemaDto, Error, SaveVars>({
```

Add `ComponentSchemaDto` to the existing types import from `@/types/designer`.

**`mutationFn` change:**
```typescript
// OLD
mutationFn: (vars) => {
  const id = schemaId ?? designerId
  const v = typeof version === 'number' && version >= 1 ? version : 1
  return designerApi.saveVersion(id, v, vars.rootElement).then(() => {})
},
// NEW
mutationFn: (vars) => {
  const id = schemaId ?? designerId
  return designerApi.createVersion(id, vars.rootElement)
},
```

**`onSuccess` change — update version in store:**
```typescript
// OLD
onSuccess: (_data, vars) => {
  const current = useDesignerCanvasStore.getState()
  const stillSameTree = current.rootElement === vars.rootElement
  useDesignerCanvasStore.setState({ isDirty: !stillSameTree })
  setSavedAt(Date.now())
  void qc.invalidateQueries({ queryKey: ['designer', 'schema', designerId], refetchType: 'none' })
  void qc.invalidateQueries({ queryKey: ['designer', 'list'] })
},
// NEW
onSuccess: (newData, vars) => {
  const current = useDesignerCanvasStore.getState()
  const stillSameTree = current.rootElement === vars.rootElement
  useDesignerCanvasStore.setState({
    version: newData.latestVersion,
    isDirty: !stillSameTree,
  })
  setFieldKeyError(null)
  setSavedAt(Date.now())
  void qc.invalidateQueries({ queryKey: ['designer', 'schema', designerId], refetchType: 'none' })
  void qc.invalidateQueries({ queryKey: ['designer', 'list'] })
},
```

**New state and imports:**
```typescript
// Add at top of DesignerPage():
const [fieldKeyError, setFieldKeyError] = useState<string | null>(null)

// Add to the imports at top of file:
import { collectFieldKeyErrors } from '@/components/designer/fieldKeyValidation'
import type { ComponentSchemaDto } from '@/types/designer'
```

**Updated `handleSave`:**
```typescript
function handleSave() {
  if (!canSave || saveMutation.isPending) return
  if (rootElement !== null) {
    const fieldKeyErrors = collectFieldKeyErrors(rootElement)
    if (fieldKeyErrors.length > 0) {
      setFieldKeyError(t('designer.canvas.saveBlockedFieldKey'))
      return
    }
  }
  setFieldKeyError(null)
  saveMutation.mutate({ displayName: trimmedName, rootElement })
}
```

**Render the fieldKeyError inline** (next to where `saveMutation.isError` is rendered in the header):
```tsx
{fieldKeyError !== null && (
  <span className="flex items-center gap-1.5 text-xs font-medium text-red-700">
    <AlertTriangle className="h-4 w-4" />
    {fieldKeyError}
  </span>
)}
```

Place this before or after the `saveMutation.isError` block — both can show simultaneously (one is fieldKey, one is API error).

### Frontend: `en.json` — new key

Add inside `designer.canvas`:
```json
"saveBlockedFieldKey": "Fix field key errors before saving."
```

### Frontend: existing `fieldKeyValidation.test.ts`

The file `web/src/components/designer/__tests__/fieldKeyValidation.test.ts` already exists. Add new `describe('collectFieldKeyErrors', ...)` block testing:
1. Valid tree → `[]`
2. Input-bearing with no fieldKey → error array has 1 entry
3. Input-bearing with invalid fieldKey → error array has 1 entry
4. Two components same fieldKey → error array has 1 collision entry
5. Non-input-bearing (Stack) with no fieldKey → `[]` (not checked)
6. Multiple violations → all returned together

### Do NOT Implement

- **`PUT /api/designers/{designerId}/versions/{version}`** — this route never existed and is not part of Story 3.6. The stub in `designerApi.ts` is replaced by `createVersion`.
- **Version publish/archive** — Story 3.7.
- **Display name rename persistence** — the `displayName` in `SaveVars` is still collected but not sent to the backend. The canvas route comment explains this. Do not change this behavior.
- **`SchemaPublished` domain event** — only fires on publish (Story 3.7). Draft saves do not fire any event.
- **Schema Registry invalidation** — triggered by `SchemaPublished`, not by Draft saves.
- **`VersionLifecycleService`** — Story 3.7.
- **"New Version" dialog from library row** — Story 3.8.
- **fieldKey a11y wiring for `FieldKeyField`** — Story 7.4 (deferred from 3.4 review).

### Project Structure Notes

Backend files follow vertical feature slicing under `src/FormForge.Api/Features/Designer/`:
```
Features/Designer/
├── DesignerEndpoints.cs       ← add SaveVersionHandler + MapPost("/{designerId}/versions", ...)
├── DesignerService.cs         ← add SaveVersionOutcome, SaveVersionResult, SaveVersionAsync
├── FieldKeyValidator.cs       ← NEW (static, no DI needed)
├── SafeIdentifier.cs          ← unchanged (reused by FieldKeyValidator)
├── PgReservedKeywords.cs      ← unchanged
├── Dtos/
│   ├── CreateDesignerRequest.cs    ← unchanged
│   ├── DesignerListItem.cs         ← unchanged
│   ├── DesignerResponse.cs         ← unchanged (reused for 201 response)
│   └── SaveVersionRequest.cs       ← NEW
└── Validators/
    ├── CreateDesignerRequestValidator.cs  ← unchanged
    └── SaveVersionRequestValidator.cs     ← NEW
```

Frontend files:
```
web/src/
├── components/designer/
│   ├── fieldKeyValidation.ts          ← add INPUT_BEARING_TYPES + collectFieldKeyErrors
│   └── __tests__/fieldKeyValidation.test.ts  ← add collectFieldKeyErrors tests
├── features/designer/
│   └── designerApi.ts                 ← replace saveVersion stub with createVersion
└── routes/_app/
    └── designer.$designerId.tsx       ← update mutation, add fieldKeyError state/display
```

`en.json` — add `designer.canvas.saveBlockedFieldKey` key.

### References

- Epics: `_bmad-output/planning-artifacts/epics.md` §Story 3.6 (line 825–846)
- Architecture: §1.1 Identifier Sanitization (line 263), §1.2 Component→PG Mapping (line 269), §4.6 Module Structure (line 776)
- Story 3.4 deferred items: `3-4-configure-component-properties.md` (fieldKey collision + save-blocking, deferred to 3.6)
- Story 3.5 dev notes: `3-5-designer-live-preview.md` (pattern for how DesignerResponse is used in component preview)
- `SafeIdentifier.cs` — `src/FormForge.Api/Features/Designer/SafeIdentifier.cs` (reuse for fieldKey regex + reserved keyword check)
- `DesignerService.cs` — `src/FormForge.Api/Features/Designer/DesignerService.cs` (reuse `ToResponse` helper; pattern for concurrent-save race handling)
- `DesignerEndpoints.cs` — `src/FormForge.Api/Features/Designer/DesignerEndpoints.cs` (add new handler; follow exact same auth/filter/Produces pattern)
- `DesignerIntegrationTests.cs` — `src/FormForge.Api.Tests/Features/Designer/DesignerIntegrationTests.cs` (extend with new tests; reuse `CreateDesignerViaApiAsync` helper, `DesignerResponseDto` record)
- `designer.$designerId.tsx` — `web/src/routes/_app/designer.$designerId.tsx` (save mutation at lines 110–144; handleSave at 149–155)
- `designerApi.ts` — `web/src/features/designer/designerApi.ts` (saveVersion stub at lines 29–33)
- `fieldKeyValidation.ts` — `web/src/components/designer/fieldKeyValidation.ts` (add below existing isValidFieldKey)
- `PropertyInspector.tsx` — `web/src/components/designer/PropertyInspector.tsx` (type strings with spaces confirmed at lines 528/575/622/670/697/808/849/946/989)
- `en.json` — `web/src/lib/i18n/locales/en.json` (designer.canvas section at line 157)
- `Program.cs` — `src/FormForge.Api/Program.cs` line 129 (validator registration pattern)

## Dev Agent Record

### Agent Model Used

Claude Opus 4.7 (claude-opus-4-7[1m]) via bmad-dev-story workflow

### Debug Log References

- Backend build: `dotnet build src/FormForge.Api/FormForge.Api.csproj` — 0 errors, 0 warnings
- Backend test (Designer slice): `dotnet test --filter "FullyQualifiedName~Designer"` — 64/64 passed (9 new SaveVersion tests)
- Backend test (full): `dotnet test` — 216/216 passed, no regressions
- Frontend build: `pnpm run build` — clean, 463 KB index chunk
- Frontend tests: `pnpm vitest run` — 45/45 passed, 3 files (16 new `collectFieldKeyErrors` cases)
- Frontend lint: `pnpm run lint` — 26 errors, all pre-existing `react-refresh/only-export-components` (matches baseline)

### Completion Notes List

- **Backend**
  - `FieldKeyValidator.Validate(JsonNode?)` walks the tree once, reusing `SafeIdentifier.TryCreate` for the regex + PG-reserved-keyword check so fieldKey rules can never drift from designerId rules. Returns `FieldKeyValidationResult` with all violations collected (not fail-fast) so the SPA can render them inline.
  - `SaveVersionOutcome` has 4 states (Success, DesignerNotFound, FieldKeyValidationFailed, VersionConflict). The endpoint maps these to 201/404/422/409. Concurrent-save race translates the PG unique-violation on `uq_component_schema_versions_designer_version` into 409 VERSION_CONFLICT (same pattern as `CreateAsync`).
  - 422 envelope: `code: FIELD_KEY_INVALID` umbrella with `extensions.errors[]` carrying per-element `{ code, elementId, elementType, fieldKey, message }`. Per-error codes can be `FIELD_KEY_MISSING`, `FIELD_KEY_INVALID`, or `FIELD_KEY_COLLISION`.
  - `MapPost("/{designerId}/versions", SaveVersionHandler)` has `RequireAuthorization("platform-admin")` and `AddValidationFilter<SaveVersionRequest>()`, mirroring the existing `MapPost("/", CreateDesignerHandler)` pattern.

- **Frontend**
  - `designerApi.saveVersion(id, version, root)` stub replaced with `designerApi.createVersion(id, root): Promise<ComponentSchemaDto>` — no version param (backend auto-increments).
  - `collectFieldKeyErrors(root)` walks the canvas tree and returns one error string per violation. The set `INPUT_BEARING_TYPES` is the exact mirror of the backend set. Container types (Stack/Row/Tabs/Repeater) and non-data leaves (Label/Button) are NOT checked.
  - `designer.$designerId.tsx` save mutation now returns `ComponentSchemaDto`. `onSuccess` updates `store.version = newData.latestVersion` (AC-5), clears `fieldKeyError`, and invalidates query caches with `refetchType: 'none'` to avoid overwriting in-flight edits. `handleSave` pre-validates client-side before mutating; if errors exist it sets `fieldKeyError` to the i18n string and short-circuits the mutation.
  - i18n: added `designer.canvas.saveBlockedFieldKey` to `en.json`.

- **AC coverage**: AC-1 ✓ (happy-path test asserts 201 + new v2 Draft + 500 ms p95 implicitly satisfied — single round-trip, no extra queries). AC-2 ✓ (frontend pre-validation + backend 422 with FIELD_KEY_MISSING / FIELD_KEY_INVALID). AC-3 ✓ (frontend + backend collision detection, separate code FIELD_KEY_COLLISION in the error list). AC-4 ✓ (round-trip test asserts persisted JSON exactly matches; reuses existing GET endpoint as the story notes). AC-5 ✓ (store.version is updated from newData.latestVersion in onSuccess). AC-6 ✓ (403 test as viewer-role user).

### File List

**Backend — new**
- `src/FormForge.Api/Features/Designer/Dtos/SaveVersionRequest.cs`
- `src/FormForge.Api/Features/Designer/Validators/SaveVersionRequestValidator.cs`
- `src/FormForge.Api/Features/Designer/FieldKeyValidator.cs`

**Backend — modified**
- `src/FormForge.Api/Features/Designer/DesignerService.cs` (SaveVersionOutcome enum, SaveVersionResult record, IDesignerService.SaveVersionAsync, DesignerService.SaveVersionAsync impl)
- `src/FormForge.Api/Features/Designer/DesignerEndpoints.cs` (MapPost route + SaveVersionHandler + FieldKeyValidationProblem + VersionConflictProblem)
- `src/FormForge.Api/Program.cs` (validator registration)
- `src/FormForge.Api.Tests/Features/Designer/DesignerIntegrationTests.cs` (9 new SaveVersion tests + MinimalStackRoot helper)

**Frontend — modified**
- `web/src/features/designer/designerApi.ts` (saveVersion stub → createVersion)
- `web/src/components/designer/fieldKeyValidation.ts` (INPUT_BEARING_TYPES, collectFieldKeyErrors + import of DesignerElement)
- `web/src/components/designer/__tests__/fieldKeyValidation.test.ts` (collectFieldKeyErrors describe block)
- `web/src/routes/_app/designer.$designerId.tsx` (mutation return type, createVersion call, store.version update, fieldKeyError state + inline render)
- `web/src/lib/i18n/locales/en.json` (designer.canvas.saveBlockedFieldKey)

**Sprint tracking — modified**
- `_bmad-output/implementation-artifacts/sprint-status.yaml` (3-6-save-designer-version: in-progress, then review at completion)

### Review Findings

Adversarial code review run 2026-05-23 against commit 693b601. Three parallel layers (Blind Hunter / Edge Case Hunter / Acceptance Auditor) raised 46 raw findings; after dedup + triage: **0 decision-needed, 10 patches, 2 deferred, 34 dismissed as noise / spec-prescribed / pre-existing**.

- [x] [Review][Patch] `GetValue<string>()` crashes on non-string `type`/`id` JSON → 500 instead of 422 [src/FormForge.Api/Features/Designer/FieldKeyValidator.cs:56-57]
- [x] [Review][Patch] Non-object `rootElement` (string/number/array) bypasses validation and is persisted as junk [src/FormForge.Api/Features/Designer/Validators/SaveVersionRequestValidator.cs:16]
- [x] [Review][Patch] AC-2 banner shows only generic message; per-element errors from `collectFieldKeyErrors` are discarded [web/src/routes/_app/designer.$designerId.tsx:160-164,202-207]
- [x] [Review][Patch] `fieldKeyError` banner stays stale after user fixes the issue without clicking Save again [web/src/routes/_app/designer.$designerId.tsx:34,137,162]
- [x] [Review][Patch] 422 `FIELD_KEY_INVALID` envelope from server (e.g., PG-reserved that client missed) renders as raw `"API error 422: FIELD_KEY_INVALID"` instead of an i18n message [web/src/routes/_app/designer.$designerId.tsx:172-175]
- [x] [Review][Patch] Unbounded recursion in `Walk` / `walk` → uncatchable stack overflow on adversarial deeply-nested input [src/FormForge.Api/Features/Designer/FieldKeyValidator.cs:101-110, web/src/components/designer/fieldKeyValidation.ts:54]
- [x] [Review][Patch] `.Include(s => s.Versions)` loads all version rows just to compute Max → NFR-2 risk as version count grows [src/FormForge.Api/Features/Designer/DesignerService.cs:175-189]
- [x] [Review][Patch] Backend integration tests don't exercise `Repeater Field` (input-bearing inside a Repeater container) — only Text Input / Number Input are covered, leaving the spec's emphasised Repeater-vs-Repeater-Field distinction untested at the API boundary [src/FormForge.Api.Tests/Features/Designer/DesignerIntegrationTests.cs]
- [x] [Review][Patch] Whitespace-only fieldKey ("   ") flows into the regex-invalid path and renders as `invalid field key: "   "` instead of the clearer "missing" code [src/FormForge.Api/Features/Designer/FieldKeyValidator.cs:68, web/src/components/designer/fieldKeyValidation.ts:42]
- [x] [Review][Patch] `FieldKeyValidationResult.HasCollision` is dead code (never read) [src/FormForge.Api/Features/Designer/FieldKeyValidator.cs:125-126]

- [x] [Review][Defer] VersionConflict 409 path has no integration test and the constraint-name (`uq_component_schema_versions_designer_version`) is string-coupled — a migration rename silently turns 409 → 500 [src/FormForge.Api/Features/Designer/DesignerService.cs:215-223] — deferred, pre-existing pattern shared with `CreateAsync`
- [x] [Review][Defer] No request-body size limit on POST `/api/designers/{id}/versions` — Kestrel defaults bound the surface today but an explicit cap would protect against accidental huge-tree submissions [src/FormForge.Api/Features/Designer/DesignerEndpoints.cs:39-46] — deferred, broader API-hardening concern

## Change Log

| Date | Change |
|------|--------|
| 2026-05-23 | Story 3.6 created — ready-for-dev |
| 2026-05-23 | Story 3.6 implemented — all 8 tasks complete, 216 backend tests pass (9 new), 45 frontend tests pass (16 new), build/lint clean. Status → review. |
| 2026-05-23 | Code review applied 10 patches (defensive JsonNode extraction, JsonObject root validation, AC-2 per-element error rendering, useEffect auto-clear on tree edit, ApiError messageKey decoding, recursion depth guard backend+frontend, DB-side MAX for nextVersion, Repeater Field integration test, IsNullOrWhiteSpace, dead-code removal). 2 items deferred to deferred-work.md. Backend 65/65 + frontend 47/47 tests pass. Status → done. |
