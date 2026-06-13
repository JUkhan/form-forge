# Story 3.8: Designer Library Page

Status: done

## Story

As a Platform Admin,
I want to browse all Designers, view their versions and statuses, preview any version, and manage version lifecycle,
so that I have a central catalog of all form definitions.

## Acceptance Criteria

**AC-1 — Paginated list with Creator column**
Given I navigate to the Designer Library
When the page renders
Then I see a paginated list of Designers: Display Name, Designer ID, Current Version, Status, Last Updated, **Creator**
*(Creator column is currently missing from the implementation — all other columns are present)*

**AC-2 — Per-row actions: New Version, Duplicate (wired), Archive, Open in Canvas**
Given any row in the Library
When I view the per-row actions
Then I see: Open in Canvas *(exists)*, Preview *(exists)*, **New Version (modal)** *(missing)*, Duplicate *(stub — backend missing)*, Publish *(exists)*, Archive *(exists)*

Given I click "New Version" on a row
When the modal opens
Then it shows "Create v{latestVersion + 1}" and on confirm:
- fetches the current rootElement from `GET /api/designers/{id}`
- POSTs it to `POST /api/designers/{id}/versions`
- invalidates the list query, toasts success, and navigates to canvas

Given I click "Duplicate" on a row
When the backend processes it
Then a new Designer is created with designerId = `{id}_copy` (or `{id}_copy2`, etc.) and displayName = "Copy of {displayName}"
And the latest rootElement is copied as v1 Draft
And the response is HTTP 201 with the new `DesignerResponse`

**AC-3 — Version history flyout**
Given any row in the Library
When I click the version badge (e.g., "v2") in the Version column
Then a flyout opens showing all versions for that Designer
And each version shows: version number, StatusChip, and relative timestamp (createdAt)

**AC-4 — Preview any version (already works)**
Given I open the Preview action on any row
When the modal opens
Then the `DynamicComponent` renders that version in-place (read-only)
*(This is already implemented via ComponentPreviewModal — no changes needed)*

## Tasks / Subtasks

- [x] Task 1: Backend — Add `CreatorDisplayName` to designer list (AC-1)
  - [x] Update `DesignerListItem` record in `DesignerService.cs`: add `string? CreatorDisplayName` as the last constructor parameter
  - [x] Update `ListAsync` projection: add `s.Creator != null ? s.Creator.DisplayName : null` as the value
  - [x] Verify `FormForgeDbContext` has the `ComponentSchema → User` FK navigation configured — EF Core must generate a LEFT JOIN in the `.Select()` projection without an explicit `.Include()`
  - [x] Update `ComponentSchemaListItem` TypeScript interface in `web/src/types/designer.ts`: add `creatorDisplayName?: string | null`

- [x] Task 2: Backend — Duplicate endpoint (AC-2)
  - [x] Add `DuplicateOutcome` enum and `DuplicateResult` record to `DesignerService.cs` (alongside existing outcome types)
  - [x] Add `DuplicateAsync(string designerId, Guid createdBy, CancellationToken ct)` to `IDesignerService` interface
  - [x] Implement `DuplicateAsync` in `DesignerService` (see Dev Notes for algorithm — tries `{id}_copy`, then `{id}_copy2` … `{id}_copy9`, returns `DuplicateConflict` if all taken)
  - [x] Add `MapPost("/{designerId}/duplicate", DuplicateDesignerHandler)` to `DesignerEndpoints.MapDesignerEndpoints()` with `RequireAuthorization("platform-admin")`
  - [x] Add `DuplicateDesignerHandler` private static method and `DuplicateConflictProblem()` static helper to `DesignerEndpoints.cs`

- [x] Task 3: Backend — Integration tests (AC-1, AC-2)
  - [x] Update existing list test to assert `creatorDisplayName` is populated (non-null string matching the seeded creator's display name)
  - [x] Duplicate success: POST → 201, verify `designerId == "{original}_copy"`, `displayName == "Copy of {original}"`, `status == "Draft"`, `latestVersion == 1`, `rootElement` matches source
  - [x] Duplicate designer not found → 404 DESIGNER_NOT_FOUND
  - [x] Duplicate as non-admin (viewer role) → 403

- [x] Task 4: Frontend — Creator column in library table (AC-1)
  - [x] Add `colCreator` column header to `<thead>` in `designer.library.tsx` (after Last Updated, before Actions)
  - [x] Add creator cell to `<tbody>` row: `{row.creatorDisplayName ?? '—'}`
  - [x] Update `TableSkeleton` `cols` prop call-site from `6` to `7`
  - [x] Add `designer.library.colCreator` i18n key: `"Creator"`

- [x] Task 5: Frontend — Version history flyout (AC-3)
  - [x] Add `VersionFlyout` component (inline in `designer.library.tsx`, co-located with `RowMenu`):
    - Props: `{ designerId: string; latestVersion: number }`
    - State: `open: boolean`
    - Lazy query: `useQuery({ queryKey: ['designer', 'schema', designerId], queryFn: () => designerApi.getSchema(designerId), enabled: open, staleTime: 30_000 })`
    - Trigger: a button rendering `v{latestVersion}` with a `ChevronDown` icon (or use existing version cell as trigger)
    - Flyout content: click-outside layer (same pattern as `RowMenu`) + dropdown listing versions sorted descending by version number
    - Each version row: `v{v.version}` label + `<StatusChip status={v.status} />` + `formatRelative(v.createdAt)` relative date
    - Loading state: spinner or "Loading…" text while `isPending`
    - Empty state (no `versions` returned): "No version history available."
  - [x] Replace the plain `v{row.latestVersion}` text in the Version column with `<VersionFlyout>` trigger
  - [x] Add i18n keys: `designer.library.versionHistory` (`"Version history"`), `designer.library.versionHistoryLoading` (`"Loading…"`)

- [x] Task 6: Frontend — "New Version" modal (AC-2)
  - [x] Add `showNewVersion` state to `RowMenu`
  - [x] Add "New Version" button to the row menu dropdown (after Open in Canvas, before Duplicate):
    ```tsx
    <button … onClick={() => { setShowNewVersion(true); setOpen(false) }}>
      <FilePlus className="h-4 w-4" />
      {t('designer.library.newVersion')}
    </button>
    ```
  - [x] Add `NewVersionDialog` component (inline in `designer.library.tsx` or as a sub-component of `RowMenu`):
    - Shows: `"Create v{row.latestVersion + 1}?"` confirmation text
    - `newVersionMutation`: chains `designerApi.getSchema(designerId)` then `designerApi.createVersion(designerId, schema.rootElement)` in one `mutationFn`
    - `onSuccess`: invalidate `['designer', 'list']`, `toast.success(t('designer.library.newVersionSuccess', { version: data.latestVersion }))`, close dialog, navigate to canvas (`/designer/$designerId`)
    - `onError`: `toast.error(t('designer.library.newVersionError'))`, close dialog
  - [x] Add i18n keys: `designer.library.newVersion` (`"New Version"`), `designer.library.newVersionSuccess` (`"Version {{version}} created."`), `designer.library.newVersionError` (`"Could not create version. Fix any field key errors first."`)

- [x] Task 7: Frontend — Wire duplicate error path (AC-2)
  - [x] Update `duplicateMutation.onError` in `RowMenu` to use the existing `errorMessage()` helper: `toast.error(errorMessage(err, 'designer.library.duplicateError'))`
  - [x] Add `designer.library.duplicateError` i18n key: `"Could not duplicate. Try again."`
  *(No change to `designerApi.ts` — `duplicateSchema` already calls the correct URL)*

- [x] Task 8: Frontend — Vitest unit tests
  - [x] `designer.library.test.tsx` (or new file): version flyout renders version list with status badges
  - [x] New version mutation success: list query invalidated, success toast shown
  - [x] New version mutation error: error toast shown
  - [x] Duplicate onError uses messageKey when ApiError has one

- [x] Task 9: Build and verify
  - [x] `dotnet build` — 0 errors
  - [x] `dotnet test` — all existing + new tests pass (≥233 expected: 229 current + 4 new)
  - [x] `pnpm run build` — 0 errors
  - [x] `pnpm run lint` — 0 new errors beyond pre-existing baseline (24 react-refresh + 3 other)
  - [x] `pnpm run test` — all pass (≥50 expected: 47 current + 3 new)

---

## Dev Notes

### What's Already Built (Do Not Re-implement)

The library page (`web/src/routes/_app/designer.library.tsx`) is largely complete from Stories 3.2–3.7:
- Paginated table with Designer ID, Name, Status, Version, Last Updated columns ✅
- Row menu with: Preview, Open in Canvas, Duplicate (stub), Publish v{N}, Archive ✅
- `CreateSchemaDialog` for creating new designers ✅
- `ComponentPreviewModal` for reading any version in preview mode ✅
- `publishMutation` / `archiveMutation` fully wired ✅
- `designerApi.ts` stubs: `publishVersion`, `archiveVersion` already use the real PUT `/status` endpoint ✅
- `duplicateSchema` stub already calls `POST /api/designers/{designerId}/duplicate` (the URL is correct — just needs the backend endpoint)

### Task 1 — Creator Column Backend Detail

`DesignerListItem` is currently:
```csharp
// DesignerService.cs (inferred from ListAsync projection)
// The record is likely declared near the top of the file or in Dtos/
// Find it and add the new parameter at the end.
```

The `ComponentSchema` entity already has `public User? Creator { get; set; }` navigation + `Guid? CreatedBy` FK. In the `.Select()` LINQ projection, EF Core generates a LEFT JOIN automatically — no `.Include()` needed:

```csharp
.Select(s => new DesignerListItem(
    s.DesignerId,
    s.DisplayName,
    s.Versions.OrderByDescending(v => v.Version).Select(v => v.Status).FirstOrDefault() ?? "Draft",
    s.Versions.OrderByDescending(v => v.Version).Select(v => (int?)v.Version).FirstOrDefault() ?? 1,
    s.CreatedAt,
    s.UpdatedAt,
    s.Creator != null ? s.Creator.DisplayName : null))   // ADD THIS
```

If EF Core throws a translation error, check `FormForgeDbContext.OnModelCreating()` for whether the `ComponentSchema → User` FK relationship is explicitly configured. If it is, the projection works. If not, add `.HasOne(s => s.Creator).WithMany().HasForeignKey(s => s.CreatedBy).IsRequired(false)` in the entity configuration block.

No migration is needed (schema unchanged; FK column already exists as `CreatedBy`).

### Task 2 — Duplicate Algorithm

```csharp
internal enum DuplicateOutcome
{
    Success,
    DesignerNotFound,
    DuplicateConflict,  // all candidate suffixes are taken (extremely rare)
}

internal sealed record DuplicateResult(
    DuplicateOutcome Outcome,
    DesignerResponse? Designer = null);
```

Service implementation:

```csharp
public async Task<DuplicateResult> DuplicateAsync(
    string designerId,
    Guid createdBy,
    CancellationToken ct)
{
    var source = await db.ComponentSchemas
        .Include(s => s.Versions)
        .FirstOrDefaultAsync(s => s.DesignerId == designerId, ct)
        .ConfigureAwait(false);

    if (source is null)
        return new DuplicateResult(DuplicateOutcome.DesignerNotFound);

    // Latest version's rootElement is the basis for the copy.
    var latestVersion = source.Versions
        .OrderByDescending(v => v.Version)
        .FirstOrDefault();
    var rootElementJson = latestVersion?.RootElement; // may be null (empty canvas)

    // Try candidate designerIds: {id}_copy, {id}_copy2 … {id}_copy9
    string? newDesignerId = null;
    var candidates = new[] { $"{designerId}_copy" }
        .Concat(Enumerable.Range(2, 8).Select(i => $"{designerId}_copy{i}"));

    foreach (var candidate in candidates)
    {
        var taken = await db.ComponentSchemas
            .AnyAsync(s => s.DesignerId == candidate, ct)
            .ConfigureAwait(false);
        if (!taken)
        {
            newDesignerId = candidate;
            break;
        }
    }

    if (newDesignerId is null)
        return new DuplicateResult(DuplicateOutcome.DuplicateConflict);

    var now = DateTimeOffset.UtcNow;
    var newSchema = new ComponentSchema
    {
        DesignerId = newDesignerId,
        DisplayName = $"Copy of {source.DisplayName}",
        CreatedBy = createdBy,
        CreatedAt = now,
    };
    var newV1 = new ComponentSchemaVersion
    {
        DesignerId = newDesignerId,
        Version = 1,
        Status = "Draft",
        RootElement = rootElementJson,
        CreatedBy = createdBy,
        CreatedAt = now,
    };
    db.ComponentSchemas.Add(newSchema);
    db.ComponentSchemaVersions.Add(newV1);
    await db.SaveChangesAsync(ct).ConfigureAwait(false);

    return new DuplicateResult(DuplicateOutcome.Success, Designer: ToResponse(newSchema, newV1, includeVersions: false));
}
```

Endpoint handler (add to `DesignerEndpoints.cs`):

```csharp
group.MapPost("/{designerId}/duplicate", DuplicateDesignerHandler)
     .RequireAuthorization("platform-admin")
     .WithSummary("Duplicate a designer to a new copy")
     .Produces<DesignerResponse>(StatusCodes.Status201Created)
     .Produces(StatusCodes.Status404NotFound)
     .Produces(StatusCodes.Status409Conflict);

private static async Task<IResult> DuplicateDesignerHandler(
    string designerId,
    ClaimsPrincipal user,
    IDesignerService designerService,
    CancellationToken ct)
{
    ArgumentNullException.ThrowIfNull(user);
    ArgumentNullException.ThrowIfNull(designerService);

    if (!Guid.TryParse(user.FindFirst("userId")?.Value, out var userId))
        return Results.Unauthorized();

    var result = await designerService.DuplicateAsync(designerId, userId, ct).ConfigureAwait(false);

    return result.Outcome switch
    {
        DuplicateOutcome.Success => Results.Created(
            $"/api/designers/{result.Designer!.DesignerId}",
            result.Designer),
        DuplicateOutcome.DesignerNotFound => DesignerNotFoundProblem(),
        DuplicateOutcome.DuplicateConflict => DuplicateConflictProblem(),
        _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
    };
}

private static IResult DuplicateConflictProblem() =>
    Results.Problem(
        title: "Could not generate a unique identifier for the duplicate.",
        statusCode: StatusCodes.Status409Conflict,
        extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = "DUPLICATE_CONFLICT",
            ["messageKey"] = "designers.duplicateConflict",
        });
```

Add `designers.duplicateConflict` to `en.json`: `"Too many copies of this designer exist. Rename one and try again."`

### Task 5 — Version Flyout

The `GET /api/designers/{designerId}` (no version) endpoint already returns `versions[]` via `GetLatestAsync(includeVersions: true)`. TypeScript type `ComponentSchemaDto.versions` is `ComponentSchemaVersion[] | undefined`.

The flyout is structured like the existing `RowMenu` click-outside pattern:

```tsx
function VersionFlyout({ designerId, latestVersion }: { designerId: string; latestVersion: number }) {
  const { t } = useTranslation()
  const [open, setOpen] = useState(false)

  const versionsQuery = useQuery({
    queryKey: ['designer', 'schema', designerId],
    queryFn: () => designerApi.getSchema(designerId),  // no version arg → returns with versions[]
    enabled: open,
    staleTime: 30_000,
  })

  const versions = versionsQuery.data?.versions ?? []
  // Sort newest first
  const sorted = [...versions].sort((a, b) => b.version - a.version)

  return (
    <div className="relative inline-flex">
      <button
        type="button"
        className="flex items-center gap-1 text-sm text-slate-500 hover:text-slate-700"
        onClick={() => setOpen((v) => !v)}
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-label={t('designer.library.versionHistory')}
      >
        v{latestVersion}
        <ChevronDown className="h-3 w-3" />
      </button>

      {open && (
        <>
          <div className="fixed inset-0 z-40" onClick={() => setOpen(false)} aria-hidden />
          <div
            role="listbox"
            className="absolute left-0 top-full z-50 mt-1 min-w-[220px] rounded-lg border border-slate-200 bg-white py-1 shadow-md"
          >
            {versionsQuery.isPending && (
              <div className="px-3 py-2 text-xs text-slate-400">{t('designer.library.versionHistoryLoading')}</div>
            )}
            {sorted.map((v) => (
              <div key={v.version} role="option" className="flex items-center gap-2 px-3 py-1.5 text-xs">
                <span className="w-7 font-mono text-slate-500">v{v.version}</span>
                <StatusChip status={v.status} />
                <span className="text-slate-400">{formatRelative(v.createdAt)}</span>
              </div>
            ))}
          </div>
        </>
      )}
    </div>
  )
}
```

Replace the plain `v{row.latestVersion}` text in the version `<td>` with `<VersionFlyout designerId={row.designerId} latestVersion={row.latestVersion} />`.

Import `ChevronDown` from `lucide-react` (already available in the file's icon imports — add to existing import statement).

### Task 6 — New Version Flow

The mutation chains two API calls:

```typescript
const newVersionMutation = useMutation({
  mutationFn: async () => {
    const schema = await designerApi.getSchema(row.designerId)
    return designerApi.createVersion(row.designerId, schema.rootElement)
  },
  onSuccess: (data) => {
    void qc.invalidateQueries({ queryKey: ['designer', 'list'] })
    toast.success(t('designer.library.newVersionSuccess', { version: data.latestVersion }))
    setShowNewVersion(false)
    void navigate({ to: '/designer/$designerId', params: { designerId: row.designerId } })
  },
  onError: () => {
    toast.error(t('designer.library.newVersionError'))
    setShowNewVersion(false)
  },
})
```

The `NewVersionDialog` is a simple `Dialog` from shadcn/ui (same component used by `CreateSchemaDialog`):

```tsx
{showNewVersion && (
  <Dialog open={showNewVersion} onOpenChange={setShowNewVersion}>
    <DialogContent>
      <DialogHeader>
        <DialogTitle>{t('designer.library.newVersion')}</DialogTitle>
      </DialogHeader>
      <p className="text-sm text-slate-600">
        {t('designer.library.newVersionLabel', { version: row.latestVersion + 1 })}
      </p>
      <DialogFooter>
        <Button variant="outline" onClick={() => setShowNewVersion(false)}>
          {t('common.cancel')}
        </Button>
        <Button
          onClick={() => newVersionMutation.mutate()}
          disabled={newVersionMutation.isPending}
        >
          {t('common.save')}
        </Button>
      </DialogFooter>
    </DialogContent>
  </Dialog>
)}
```

The `navigate` call inside `RowMenu` requires passing `navigate` from the parent `DesignerLibraryPage` down to `RowMenu` as a prop, OR defining `const navigate = useNavigate({ from: '/designer/library' })` directly inside `RowMenu`. Either works — the simpler approach is defining it directly in `RowMenu` since the component already uses hooks.

### Architecture Compliance

| Requirement | This Story |
|---|---|
| AR-48 TanStack Query key convention | `['designer', 'schema', designerId]` for version flyout lazy-load |
| AR-49 Pessimistic mutation strategy | Duplicate + NewVersion are pessimistic (no optimistic local updates) |
| AR-18 RFC 7807 error envelope | `DuplicateConflictProblem()` uses `Results.Problem(...)` with `code` + `messageKey` |
| AR-22 Route group filter chain | `DuplicateDesignerHandler` via `group.MapPost()` inheriting group auth |
| AR-32 httpClient method convention | `designerApi.duplicateSchema` uses `httpClient.post<ComponentSchemaDto>` (already correct) |
| Pagination standard shape | `DesignerListItem` change is additive — existing `PagedResult<DesignerListItem>` wrapper unchanged |
| FR-41 empty/error/loading states | Flyout shows loading spinner; creator cell shows `—` for null |

### Previous Story Learnings

From Story 3.7:
- **`DesignerNotFoundProblem()` already exists** as `private static IResult` in `DesignerEndpoints.cs` — reuse it directly in `DuplicateDesignerHandler` (no duplication).
- **`ToResponse` helper already exists** in `DesignerService.cs` — reuse it for the duplicate response (do not re-implement).
- **`errorMessage()` helper pattern** in `designer.library.tsx` (`const errorMessage = (err, fallbackKey) => ...`) — reuse it for `duplicateMutation.onError` to decode `ApiError.messageKey`.
- **`publishMutation.isPending` pattern** for disabling buttons — apply the same to `newVersionMutation.isPending`.
- **RowMenu `navigate` context**: `RowMenu` is a function component. `useNavigate` can be called directly inside it (it's already under the TanStack Router context from the route). No need for prop drilling from `DesignerLibraryPage`.
- **`staleTime: 30_000`** is the established library list query value. Use the same for the flyout query.
- **Pre-existing lint warnings**: 24 `react-refresh/only-export-components` + 3 others are pre-existing. The new inline components (`VersionFlyout`, `NewVersionDialog`) defined inside the route file will add 2–3 more react-refresh warnings — this is acceptable and pre-existing pattern.

From Story 3.6:
- **`FieldKeyValidator` rejection on Save**: if the rootElement from an old version has a malformed fieldKey (edge case), `createVersion` will return 422 FIELD_KEY_INVALID. The `newVersionMutation.onError` toast covers this with a generic message. No special handling needed.
- **`designerApi.getSchema(designerId, version)` vs `getSchema(designerId)`**: passing no version returns the full DTO including `versions[]`; passing a version returns single-version DTO without `versions[]`. The version flyout query MUST call with no version to get the `versions` array.

### Project Structure — Files to Change

**Backend — modified**
- `src/FormForge.Api/Features/Designer/DesignerService.cs` — add `DuplicateOutcome`/`DuplicateResult`, `DuplicateAsync` implementation; update `DesignerListItem` record; update `ListAsync` projection
- `src/FormForge.Api/Features/Designer/DesignerEndpoints.cs` — add `DuplicateDesignerHandler`, `DuplicateConflictProblem()`, register route
- `src/FormForge.Api.Tests/Features/Designer/DesignerIntegrationTests.cs` — 4 new tests + update list assertion for `creatorDisplayName`

**Frontend — modified**
- `web/src/types/designer.ts` — add `creatorDisplayName?: string | null` to `ComponentSchemaListItem`
- `web/src/routes/_app/designer.library.tsx` — `VersionFlyout` component, `NewVersionDialog`, "New Version" + updated Duplicate row actions, Creator column, `ChevronDown` + `FilePlus` icon imports
- `web/src/lib/i18n/locales/en.json` — new keys listed below

**i18n keys to add** (all under `designer.library`):
- `colCreator` → `"Creator"`
- `newVersion` → `"New Version"`
- `newVersionLabel` → `"Create v{{version}}"`
- `newVersionSuccess` → `"Version {{version}} created."`
- `newVersionError` → `"Could not create version. Fix any field key errors first."`
- `versionHistory` → `"Version history"`
- `versionHistoryLoading` → `"Loading…"`
- `duplicateError` → `"Could not duplicate. Try again."`

And under `designers`:
- `duplicateConflict` → `"Too many copies of this designer exist. Rename one and try again."`

## Dev Agent Record

### Agent Model Used

claude-opus-4-7

### Debug Log References

- Backend `dotnet build` — 0 warnings, 0 errors.
- Backend `dotnet test` — 233/233 pass (229 prior + 4 new duplicate tests; existing list test extended with `creatorDisplayName` assertion).
- Frontend `pnpm run build` — 0 errors (Vite + `tsc -b --noEmit`).
- Frontend `pnpm run test` — 52/52 pass (47 prior + 5 new: 1 VersionFlyout lazy-load + 2 New Version mutation + 2 Duplicate error path).
- Frontend `pnpm run lint` — 23 errors total (all pre-existing `react-refresh/only-export-components` + 1 unrelated `react-hooks/set-state-in-effect` in `designer.$designerId.tsx`). Below the story's documented baseline of 27 (24 react-refresh + 3 other); no new errors introduced.

### Completion Notes List

- **AC-1 Creator column** — Backend `DesignerListItem` record gained `CreatorDisplayName`; `ListAsync` projection LEFT-JOINs through the existing `ComponentSchema.Creator` FK navigation (no `.Include()` needed, no migration needed — the FK column existed and was wired in `FormForgeDbContext.OnModelCreating`). Frontend `ComponentSchemaListItem` mirrors the field; the Creator cell renders `creatorDisplayName ?? '—'` and the `TableSkeleton` `cols` advances from 6 to 7.
- **AC-2 Duplicate endpoint** — `POST /api/designers/{id}/duplicate` (platform-admin) probes `{id}_copy`, `{id}_copy2…{id}_copy9` for the first untaken slot, copies the source's latest `RootElement` into a fresh `v1 Draft`, and returns `201 Created` with the new `DesignerResponse`. `DuplicateConflict` surfaces as `409` with `DUPLICATE_CONFLICT` / `designers.duplicateConflict`. Candidate-length guard skips candidates > 63 chars (the column cap) so very long source IDs fail cleanly as `DuplicateConflict` rather than as a DB truncation error. Race-window duplicate catch on `PK_component_schemas` mirrors the `CreateAsync` pattern.
- **AC-2 New Version flow** — Frontend `RowMenu` adds a "New Version" menu item that opens a confirmation Dialog ("Create v{N+1}"). Save fires a chained mutation: `getSchema(id)` → `createVersion(id, schema.rootElement)`. Success invalidates `['designer', 'list']`, toasts the new version number, closes the dialog, and navigates to the canvas. Errors funnel through the shared `errorMessage()` helper so `ApiError.messageKey` (e.g., `designers.fieldKeyInvalid`) wins over the route fallback.
- **AC-2 Duplicate wiring** — `duplicateMutation.onError` now uses the same `errorMessage()` helper. The frontend `duplicateSchema` stub already hit the correct URL, so no `designerApi.ts` change was needed.
- **AC-3 Version history flyout** — `VersionFlyout` component (inline in `designer.library.tsx`) renders the existing version-cell as a `ChevronDown` button. Click lazy-loads `getSchema(designerId)` (no version arg → full DTO with `versions[]`) with `staleTime: 30_000` matching the list query. Dropdown sorts versions desc by version, renders `StatusChip` for each, and uses the same click-outside layer pattern as `RowMenu`.
- **AC-4 Preview** — No change required (already implemented via `ComponentPreviewModal` from Stories 3.5/3.7).
- **Component exports** — `VersionFlyout` and `RowMenu` are exported from the route file purely to enable Vitest coverage; this matches the story's stated tolerance for additional `react-refresh/only-export-components` warnings. Test file is renamed `-designer.library.test.tsx` (dash-prefix per TanStack Router convention) so the router code generator does not treat it as a route.

### File List

**Backend**
- `src/FormForge.Api/Features/Designer/Dtos/DesignerListItem.cs` (modified)
- `src/FormForge.Api/Features/Designer/DesignerService.cs` (modified)
- `src/FormForge.Api/Features/Designer/DesignerEndpoints.cs` (modified)
- `src/FormForge.Api.Tests/Features/Designer/DesignerIntegrationTests.cs` (modified)

**Frontend**
- `web/src/types/designer.ts` (modified)
- `web/src/routes/_app/designer.library.tsx` (modified)
- `web/src/routes/_app/-designer.library.test.tsx` (new)
- `web/src/lib/i18n/locales/en.json` (modified)

### Review Findings

- [x] [Review][Patch] VersionFlyout cache not invalidated after New Version success — flyout shows stale `versions[]` for up to 30s [`web/src/routes/_app/designer.library.tsx:228`] — fixed: `newVersionMutation.onSuccess` also invalidates `['designer','schema', row.designerId]`
- [x] [Review][Patch] VersionFlyout has no error branch — network failure falls through to empty-state and hides the failure [`web/src/routes/_app/designer.library.tsx:148-169`] — fixed: added `isError` branch + `versionHistoryError` i18n key + Vitest coverage
- [x] [Review][Patch] DuplicateAsync's 23505 catch returns conflict instead of advancing to the next free slot; backend comment claims SPA will retry but it doesn't [`src/FormForge.Api/Features/Designer/DesignerService.cs:472-486`] — fixed: candidate loop now wraps the SaveChanges and walks to the next slot on PK conflict; only returns `DuplicateConflict` after exhausting all candidates
- [x] [Review][Patch] Misleading "too many copies" error when source designerId ≥58 chars makes all candidates `>63` chars and silently get skipped [`src/FormForge.Api/Features/Designer/DesignerService.cs:432`] — fixed: filter candidates by length up front; new `DuplicateOutcome.SourceIdTooLong` → 422 with `DUPLICATE_ID_TOO_LONG` / `designers.duplicateIdTooLong`; integration test added
- [x] [Review][Patch] Two separate `QueryClient` instances per render in tests — `client={makeQC()}` and `qc={makeQC()}` are different clients [`web/src/routes/_app/-designer.library.test.tsx:213-215, 237-239, 255-257`] — fixed: hoisted a single `const qc = makeQC()` per test and threaded it to both
- [x] [Review][Defer] DuplicateAsync produces stuttering `_copy_copy` chains when source already ends in `_copy[N]?` [`src/FormForge.Api/Features/Designer/DesignerService.cs:427`] — deferred, pre-existing
- [x] [Review][Defer] VersionFlyout listbox semantics broken — `role="listbox"`/`role="option"` without keyboard handling, tabIndex, or Escape close [`web/src/routes/_app/designer.library.tsx:144-169`] — deferred to Story 7.4 (Accessibility Compliance)
- [x] [Review][Defer] Long `creatorDisplayName` (up to 200 chars) breaks table layout — no truncate/`max-w` styling [`web/src/routes/_app/designer.library.tsx:638`] — deferred to Story 7.4
- [x] [Review][Defer] `fixed inset-0 z-40` click-outside backdrop traps other rows' triggers — first click on row B closes row A without opening B [`web/src/routes/_app/designer.library.tsx:139-143, 290-297`] — deferred, UX papercut
- [x] [Review][Defer] Save → Cancel race in New Version dialog — closing dialog mid-flight still navigates on `onSuccess` [`web/src/routes/_app/designer.library.tsx:222-242`] — deferred, minor UX
- [x] [Review][Defer] `getSchema` → `createVersion` two-step is non-atomic — network drop between awaits leaves user without a recovery path [`web/src/routes/_app/designer.library.tsx:222-226`] — deferred, inherent
- [x] [Review][Defer] `getSchema` with null `rootElement` posts an empty Draft and shows the wrong error toast on validation failure [`web/src/routes/_app/designer.library.tsx:222-226`] — deferred, depends on post-3.2 invariant break
- [x] [Review][Defer] Newly duplicated copy's id (`_copy` vs `_copy2`…) is not surfaced in the success toast — user has no way to find the new copy except by scanning the list [`web/src/routes/_app/designer.library.tsx:209`] — deferred, UX polish
- [x] [Review][Defer] Stale/corrupt source `RootElement` is copied verbatim — propagates broken-state pattern to new rows [`src/FormForge.Api/Features/Designer/DesignerService.cs:417, 461`] — deferred, pre-existing data-integrity issue
- [x] [Review][Defer] Cross-tab duplicate creation invisible until 30s `staleTime` expires — no `refetchOnWindowFocus` opt-in [`web/src/routes/_app/designer.library.tsx:106-117, 537-540`] — deferred, design intent
- [x] [Review][Defer] Backend authz ordering not tested (e.g., viewer hits unknown id — 403 vs 404 ordering) [`src/FormForge.Api.Tests/Features/Designer/DesignerIntegrationTests.cs`] — deferred, minor coverage gap
- [x] [Review][Defer] `Guid.Empty` userId not rejected by the duplicate handler — depends on JWT issuer to never emit `00000000-…` [`src/FormForge.Api/Features/Designer/DesignerEndpoints.cs:187`] — deferred, defensive
- [x] [Review][Defer] `rootElementJson` size not bounded before copy — a 100 MB source throws unmapped `DbUpdateException` → 500 [`src/FormForge.Api/Features/Designer/DesignerService.cs:461`] — deferred, defensive (covered by 3.6's request-size-limit deferral)

## Change Log

| Date       | Change                                                                                                | By           |
|------------|-------------------------------------------------------------------------------------------------------|--------------|
| 2026-05-24 | Implemented all 9 tasks (AC-1 through AC-3, AC-4 already in place from 3.5/3.7). Status → review.     | claude-opus-4-7 |
| 2026-05-24 | Code review (3-reviewer parallel pass): 5 patches identified, 13 deferred, 15 dismissed. No AC violations. | claude-opus-4-7 |
| 2026-05-24 | Applied 5 patches: VersionFlyout schema-cache invalidation + error branch; DuplicateAsync retry-on-23505 + SourceIdTooLong (new 422 outcome); test QueryClient hoist. Backend 234/234, frontend 53/53. Status → done. | claude-opus-4-7 |
