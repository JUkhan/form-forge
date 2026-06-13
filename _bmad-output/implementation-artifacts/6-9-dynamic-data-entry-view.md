# Story 6.9: Dynamic Data Entry View

Status: done

## Story

As a Content Editor,
I want to open any Menu Item and see a record list plus a DynamicComponent form for creating new records,
so that I can enter data into any dynamically provisioned module.

## Acceptance Criteria

**AC-1 — List view on `data.$designerId.tsx`**
**Given** I navigate to a Menu Item with a Schema Binding
**When** the route `/_app/data/$designerId` loads
**Then** I see a paginated record list AND a "New Record" button
**And** the "New Record" button is visible only if `usePermission(designerId, 'canCreate')` returns true (per Story 2.7)

**AC-2 — New record route with DynamicComponent form**
**Given** I click "New Record"
**When** the new-record route (`data.$designerId.new.tsx`) opens
**Then** a DynamicComponent form renders using `{ designerId }` (component fetches its own schema internally)
**And** an external Save button on the page invokes `submitRef.current()` (per AR-35)
**And** on `onSave(payload)`, `useCreateRecord(designerId).mutate(payload)` is called
**And** on success: `toast.success(t('data.entry.createSuccess'))` fires and TanStack Query invalidates `['data', designerId]` (per AR-48)
**And** on error: `toast.error(t('data.entry.createError'))` fires (per AR-29)
**And** the Save button is disabled until DynamicComponent fires `onReadyChange(true)` AND `onValidityChange(true)` AND mutation is not pending

**AC-3 — Record detail/edit route**
**Given** I click a record row
**When** the detail route (`data.$designerId.$recordId.tsx`) opens
**Then** the record is loaded via `useRecord(designerId, recordId)` and pre-filled into DynamicComponent via `initialData`
**And** if `usePermission(designerId, 'canUpdate')` is false: no Save button is rendered (read-only view — no `submitRef` or `onSave` wired)
**And** if `canUpdate` is true: a Save button invokes `submitRef.current()`, which calls `useUpdateRecord(designerId).mutate({ id, payload })`
**And** on update success: `toast.success(t('data.entry.updateSuccess'))` fires and `['data', designerId]` is invalidated

**AC-4 — Soft-delete with confirmation dialog**
**Given** I click the Delete button on a record detail page (visible only if `usePermission(designerId, 'canDelete')` is true)
**When** I confirm the dialog
**Then** `useDeleteRecord(designerId).mutate(id)` fires
**And** the list row disappears from the active list (optimistic update per AR-49 — record removed from list cache before response)
**And** on success: `toast.success(t('data.entry.deleteSuccess'))` fires and `['data', designerId]` is re-invalidated to sync
**And** on cancel: dialog closes, no mutation fires

**AC-5 — Form save success feedback**
**Given** any form save (create or update) succeeds
**When** the mutation `onSuccess` fires
**Then** a `sonner` toast appears (per AR-29)
**And** TanStack Query invalidates `['data', designerId]` keys (per AR-48) causing list/detail to re-fetch

---

## Tasks / Subtasks

- [x] **Task 1 — Transform `data.$designerId.tsx` into the record list view** (AC-1)
  - [x] Replace the current `DataEntryPage` wrapper with a `RecordListPage` component
  - [x] `RecordListPage` calls `useRecordList(designerId, { page, pageSize: 25 })` with local `page` state (default 1)
  - [x] Render a simple table: columns = `id` (truncated), `createdAt`, `updatedAt`, plus first 2–3 user fieldKeys derived from `data[0]` keys (skip system columns). Story 6.10 will add full schema-driven columns.
  - [x] "New Record" button: visible only when `usePermission(designerId, 'canCreate')`. Navigates to `/$designerId/new` via `useNavigate()`.
  - [x] Clicking a data row navigates to `/$designerId/$recordId`.
  - [x] Basic pagination: prev/next buttons, disable when at boundary. Story 6.10 will add full paginator with 10/25/50 picker.
  - [x] Loading state: render a `<p>{t('common.loading')}</p>` skeleton placeholder while `isLoading` is true. Story 6.11 will polish with `Skeleton` components.
  - [x] Error state: `toast.error(t('data.entry.loadError'))` in `onError`. Story 6.11 will add `ErrorBanner`.
  - [x] Add i18n key `data.entry.loadError` to `en.json`.

- [x] **Task 2 — Create `web/src/routes/_app/data.$designerId.new.tsx`** (AC-2)
  - [x] Create the new-record route file: `createFileRoute('/_app/data/$designerId/new')`.
  - [x] Component wraps `DataEntryPage` passing `designerId` from `Route.useParams()`.
  - [x] `DataEntryPage` is the component responsible for all create-mutation logic (see Task 3).

- [x] **Task 3 — Wire real create mutation in `DataEntryPage.tsx`** (AC-2, AC-5)
  - [x] Import and call `useCreateRecord(designerId)` — the hook already exists at `web/src/features/data-entry/useCreateRecord.ts`.
  - [x] Replace the `handleSave` stub: `mutation.mutate(payload)` where `payload` is the data received from `onSave(payload)`.
    - `onSave` receives the filtered, validated payload from DynamicComponent — pass it directly to `mutate()`.
    - No transformations needed; `createRecord()` accepts `RecordPayloadWithChildren`.
  - [x] Replace `isSaving` local state with `mutation.isPending` (remove `setIsSaving`).
  - [x] Add `toast.success(t('data.entry.createSuccess'))` in `onSuccess` callback of `useMutation`'s options OR in `useCreateRecord`. Because `useCreateRecord` doesn't currently show a toast, add it in the component's `onSuccess` handler passed to `useMutation`:
    ```typescript
    const mutation = useCreateRecord(designerId)
    const handleSave = useCallback((payload: Record<string, unknown>) => {
      mutation.mutate(payload as RecordPayloadWithChildren, {
        onSuccess: () => toast.success(t('data.entry.createSuccess')),
        onError: () => toast.error(t('data.entry.createError')),
      })
    }, [mutation, t])
    ```
  - [x] Fix the existing disabled logic: `disabled={!isReady || !isValid || mutation.isPending}` — current code already has this right (no `!isSaving` bug exists in the file; verify before changing).
  - [x] Add a `canCreate` guard: if `usePermission(designerId, 'canCreate')` is false, render a permission-denied message instead of the form (or redirect to the list route).
  - [x] Remove `saveStub` toast call; keep the `saveStub` i18n key in en.json for now (delete it only after confirming no tests reference it — the existing test asserts `'data.entry.saveStub'`).
  - [x] Add i18n keys `data.entry.createSuccess` and `data.entry.createError` to `en.json`.

- [x] **Task 4 — Create `web/src/routes/_app/data.$designerId.$recordId.tsx`** (AC-3, AC-4)
  - [x] Create the file: `createFileRoute('/_app/data/$designerId/$recordId')`.
  - [x] Component extracts `{ designerId, recordId }` from `Route.useParams()` and renders `RecordDetailPage`.

- [x] **Task 5 — Create `web/src/components/dataEntry/RecordDetailPage.tsx`** (AC-3, AC-4)
  - [x] Props: `{ designerId: string; recordId: string }`
  - [x] Load record: `useRecord(designerId, recordId)` — hook at `web/src/features/data-entry/useRecord.ts`.
  - [x] Pass `initialData={record}` to DynamicComponent (the `DynamicRecord` object; DynamicComponent spreads `initialData ?? {}` into its formData seed — works because user fieldKeys are verbatim in the record object per AR-46 Option C).
  - [x] Permission check for update:
    ```typescript
    const canUpdate = usePermission(designerId, 'canUpdate')
    const canDelete = usePermission(designerId, 'canDelete')
    ```
  - [x] Edit mode (canUpdate=true): wire `submitRef` + `onSave` + external Save button. On `onSave(payload)`: call `useUpdateRecord(designerId).mutate({ id: recordId, payload })`. On success: `toast.success(t('data.entry.updateSuccess'))`.
  - [x] Read-only mode (canUpdate=false): render DynamicComponent WITHOUT `submitRef`, `onSave`, and without a Save button. The form renders visually but does not accept submissions.
  - [x] Delete button: visible only when `canDelete`. Uses Radix `Dialog` (shadcn Dialog primitive at `web/src/components/ui/dialog.tsx`) for confirmation:
    - Trigger: Button variant=`destructive`
    - Title: `t('data.entry.deleteConfirmTitle')`
    - Description: `t('data.entry.deleteConfirmDescription')`
    - Confirm button: variant=`destructive`, calls `deleteMutation.mutate(recordId)`, then navigates back to list (`useNavigate()` to `/$designerId`)
    - Cancel button: variant=`outline`, closes dialog
    - Disable both buttons while `deleteMutation.isPending`
  - [x] Optimistic delete (AC-4 / AR-49): In the `onMutate` callback of a **local** `useMutation` wrapping `deleteRecord`, snapshot and optimistically remove the record from the list cache:
    ```typescript
    const queryClient = useQueryClient()
    const deleteMutation = useMutation({
      mutationFn: (id: string) => deleteRecord(designerId, id),
      onMutate: async (id) => {
        await queryClient.cancelQueries({ queryKey: ['data', designerId] })
        const snapshot = queryClient.getQueriesData({ queryKey: ['data', designerId, 'list'] })
        queryClient.setQueriesData(
          { queryKey: ['data', designerId, 'list'] },
          (old: RecordPagedResult | undefined) =>
            old ? { ...old, data: old.data.filter(r => r.id !== id) } : old,
        )
        return { snapshot }
      },
      onError: (_err, _id, context) => {
        if (context?.snapshot) {
          for (const [key, value] of context.snapshot) {
            queryClient.setQueryData(key, value)
          }
        }
        toast.error(t('data.entry.deleteError'))
      },
      onSuccess: () => {
        queryClient.invalidateQueries({ queryKey: ['data', designerId] })
        toast.success(t('data.entry.deleteSuccess'))
        navigate({ to: '/$designerId', params: { designerId } }) // go back to list
      },
    })
    ```
    **Do NOT modify `useDeleteRecord.ts`** — that hook is pessimistic for other consumers. The optimistic logic lives only in this component.
  - [x] Loading state: while `useRecord` is loading, show a brief placeholder. Story 6.11 will add `Skeleton`.
  - [x] Error state: if `useRecord` returns an error, `toast.error(...)` and navigate back. Story 6.11 will add inline `ErrorBanner`.
  - [x] Add i18n keys to `en.json`: `data.entry.updateSuccess`, `data.entry.updateError`, `data.entry.deleteSuccess`, `data.entry.deleteError`, `data.entry.deleteConfirmTitle`, `data.entry.deleteConfirmDescription`.

- [x] **Task 6 — Update `web/src/lib/i18n/locales/en.json`** (AC-2, AC-3, AC-4, AC-5)
  - [x] Extend the `"data"."entry"` block:
    ```json
    "data": {
      "entry": {
        "newRecord": "New Record",
        "save": "Save",
        "saving": "Saving…",
        "saveStub": "Record submitted (data persistence coming in Epic 6).",
        "createSuccess": "Record created.",
        "createError": "Failed to create record.",
        "updateSuccess": "Record updated.",
        "updateError": "Failed to update record.",
        "deleteSuccess": "Record deleted.",
        "deleteError": "Failed to delete record.",
        "deleteConfirmTitle": "Delete record?",
        "deleteConfirmDescription": "This record will be soft-deleted. You can restore it from the admin audit view.",
        "loadError": "Failed to load records."
      }
    }
    ```
  - [x] `saveStub` key intentionally retained — existing test (`-data.$designerId.test.tsx` line 131) asserts it; remove only when that test is updated in Task 7. (Removed in this story — Task 7 updated the assertion to `createSuccess`.)

- [x] **Task 7 — Update tests** (AC-1, AC-2, AC-3, AC-4, AC-5)
  - [x] **Modify `web/src/routes/_app/-data.$designerId.test.tsx`**:
    - The existing 3 tests cover the old stub `DataEntryPage` behavior. They still pass as-is because `DataEntryPage` is now only rendered for the `.new` sub-route, not the list view. Keep these tests but update the mock for `toast` (`saveStub` key changes → update assertion or remove when stub is removed).
    - Add a test that checks the list page renders `RecordListPage` with a "New Record" button (when canCreate=true).
    - Add a test that "New Record" button is hidden when canCreate=false.
  - [x] **Create `web/src/routes/_app/-data.$designerId.$recordId.test.tsx`**:
    - Mock `useRecord`, `useUpdateRecord`, `useDeleteRecord`, `usePermission`, `sonner`, `react-i18next` (same pattern as existing test).
    - AC-3 read-only: canUpdate=false → no Save button rendered.
    - AC-3 edit mode: canUpdate=true → Save button enabled after ready+valid.
    - AC-4 delete button: canDelete=false → Delete button hidden; canDelete=true → Delete button visible, dialog opens on click, confirm calls `deleteRecord`.
    - AC-5: on mutation success → toast.success called with `data.entry.updateSuccess` key.

---

## Dev Notes

### Scope Boundaries

- **Story 6.9 scope**: List (basic, paginated), new-record form (real mutation), detail/edit form, delete with confirmation. Minimal loading/error states (enough to be functional).
- **NOT in scope** (covered by later stories):
  - Story 6.10: Multi-column sort, per-column filter bar, 10/25/50 page size picker, soft-deleted indicator row styling.
  - Story 6.11: shadcn `Skeleton` loading states, empty-state with CTA, `ErrorBanner` component with retry + correlation ID display, server `ValidationProblemDetails` → `setError()` field-level errors.

### Existing Files — Current State and What Changes

**`web/src/routes/_app/data.$designerId.tsx`** (MODIFY — major rewrite)
- Currently: thin wrapper rendering `DataEntryPage` (the create-form). This is a stub from Story 3.9.
- After: renders `RecordListPage` instead. The route object (`export const Route`) still uses `createFileRoute('/_app/data/$designerId')`.

**`web/src/components/dataEntry/DataEntryPage.tsx`** (MODIFY)
- Currently: 50-line stub with `toast.success(t('data.entry.saveStub'))` and `isSaving` local state.
- After: real `useCreateRecord` mutation, `mutation.isPending` replaces `isSaving`. Remove `setIsSaving` calls.
- The `submitRef` pattern, `onReadyChange`, `onValidityChange` props remain unchanged — only the `handleSave` body changes.
- The component is now only rendered by `data.$designerId.new.tsx`.

**`web/src/routes/_app/-data.$designerId.test.tsx`** (MODIFY)
- The 3 existing tests still cover DataEntryPage directly. They remain valid after the stub is replaced because they test the component in isolation.
- Update the `toastSuccess` assertion in test 3: change `'data.entry.saveStub'` → `'data.entry.createSuccess'` after the stub is removed.

### Critical Implementation Rules

**Do NOT modify these hooks** — they are shared and work correctly as-is:
- `useCreateRecord.ts` — no toast in the hook; the component adds its own `onSuccess`/`onError` callbacks at the call site.
- `useUpdateRecord.ts` — same pattern.
- `useDeleteRecord.ts` — pessimistic, no optimistic logic. The `RecordDetailPage` implements its own inline optimistic mutation for the delete + optimistic-removal-from-list pattern.

**DynamicComponent has no `readOnly` prop** (confirmed in `DynamicComponent.tsx` interface lines 10–35). Read-only rendering = do not pass `submitRef` or `onSave`. The form renders with `initialData` but the user cannot submit.

**`initialData` for edit mode** — Pass the `DynamicRecord` object directly:
```typescript
<DynamicComponent
  designerId={designerId}
  initialData={record}      // DynamicRecord — system columns are ignored by DynamicComponent
  ...
/>
```
DynamicComponent's `extractBindToKeys` only wires `bindTo` fieldKeys into form state; system columns (`id`, `createdAt`, etc.) land in `initialData` but are never picked up as bound keys and are therefore excluded from the payload automatically.

**`onSave` payload for update** — DynamicComponent fires `onSave(payload)` where `payload` contains only visible user fieldKeys. Pass it directly to `useUpdateRecord.mutate({ id: recordId, payload })`. The server applies partial-update semantics (Story 6.4) — fields absent from payload are left unchanged.

**AR-49 optimistic delete** — Only applies to the list-cache removal when deleting from the detail page. Use `queryClient.setQueriesData` with a filter on `['data', designerId, 'list']` key prefix. The `['data', designerId, 'record', recordId]` entry can just be invalidated pessimistically — no need to optimistically clear it.

**TanStack Router navigation** — Use `useNavigate()` from `@tanstack/react-router`. For navigating back to the list after delete: `navigate({ to: '/data/$designerId', params: { designerId } })`. For navigating to new-record: `navigate({ to: '/data/$designerId/new', params: { designerId } })`. For navigating to a record: `navigate({ to: '/data/$designerId/$recordId', params: { designerId, recordId: record.id } })`.

**Radix Dialog for delete confirmation** — Use the existing `web/src/components/ui/dialog.tsx` wrapper (shadcn). Pattern from `SchemaDriftView.tsx`:
```typescript
const [showDeleteDialog, setShowDeleteDialog] = useState(false)
// ...
<Dialog open={showDeleteDialog} onOpenChange={setShowDeleteDialog}>
  <DialogContent>
    <DialogHeader>
      <DialogTitle>{t('data.entry.deleteConfirmTitle')}</DialogTitle>
      <DialogDescription>{t('data.entry.deleteConfirmDescription')}</DialogDescription>
    </DialogHeader>
    <DialogFooter>
      <Button variant="outline" onClick={() => setShowDeleteDialog(false)}>{t('common.cancel')}</Button>
      <Button variant="destructive" disabled={deleteMutation.isPending} onClick={() => deleteMutation.mutate(recordId)}>
        {t('data.entry.deleteSuccess')}
      </Button>
    </DialogFooter>
  </DialogContent>
</Dialog>
```

**Toast pattern (AR-29)** — No payload data in toasts. Use `toast.success(t('key'))` and `toast.error(t('key'))`. Never pass record IDs, field values, or any data to the toast message. Sentry breadcrumbs capture toast calls.

**i18n** — Check whether `t('common.cancel')` already exists in `en.json`. If it does, reuse it. If not, add it.

### Architecture Compliance Checklist

- AR-28 (Error/Loading Boundaries): Basic loading/error states. Story 6.11 will replace with Skeleton + ErrorBanner.
- AR-29 (Toasts): All mutations fire sonner toasts on success and error. No payload data in toast messages.
- AR-35 (DynamicComponent Bridge): External Save button → `submitRef.current()` → `onSave(payload)` → `mutation.mutate(payload)`. No bypasses.
- AR-46 (Option C hybrid): `DynamicRecord.id` is a string UUID (camelCase system column), user fieldKeys are snake_case. `initialData` passes the record object; DynamicComponent's `extractBindToKeys` ignores system columns automatically.
- AR-48 (Query Key Tuples): Invalidate via `['data', designerId]` root prefix to catch all list/record variants.
- AR-49 (Mutation Strategy): form saves (create/update) are **pessimistic**. Soft-delete from list is **optimistic** (local cache removal in `onMutate`).

### File Locations

All new/modified files:

| Action | File |
|--------|------|
| MODIFY | `web/src/routes/_app/data.$designerId.tsx` |
| CREATE | `web/src/routes/_app/data.$designerId.new.tsx` |
| CREATE | `web/src/routes/_app/data.$designerId.$recordId.tsx` |
| CREATE | `web/src/routes/_app/-data.$designerId.$recordId.test.tsx` |
| MODIFY | `web/src/components/dataEntry/DataEntryPage.tsx` |
| CREATE | `web/src/components/dataEntry/RecordListPage.tsx` |
| CREATE | `web/src/components/dataEntry/RecordDetailPage.tsx` |
| MODIFY | `web/src/lib/i18n/locales/en.json` |
| MODIFY | `web/src/routes/_app/-data.$designerId.test.tsx` |

**No backend changes required.** All CRUD endpoints (POST/GET/PUT/DELETE `/api/data/{designerId}`) are fully implemented and tested in Stories 6.1–6.8.

### Testing Approach

Follow the pattern in `web/src/routes/_app/-data.$designerId.test.tsx`:
1. `vi.mock('react-i18next', ...)` — identity translator.
2. `vi.mock('sonner', ...)` — capture toast calls.
3. `vi.mock('@/components/designer/DynamicComponent', ...)` — expose `onSave`, `onReadyChange`, `onValidityChange`, `submitRef` hooks via `vi.hoisted()` handles.
4. `vi.mock('@/features/data-entry/useCreateRecord', ...)` — return a mock `useMutation` result with a captured `mutate` fn.
5. `vi.mock('@/features/auth/usePermission', ...)` — return configurable boolean per action.
6. Use `cleanup()` in `beforeEach`.

### Previous Story Learnings (Story 6.8)

- Story 6.8 was admin-side (backend + admin UI). No UX patterns from 6.8 directly apply to 6.9.
- Mutation hook pattern used throughout stories 6.3–6.8: hooks themselves don't show toasts; the consuming component passes `onSuccess`/`onError` callbacks to `mutate(payload, { onSuccess, onError })`. Follow this same pattern in `DataEntryPage.tsx` and `RecordDetailPage.tsx`.
- The `useRecordList` hook already uses `keepPreviousData` — the list won't flash empty on page navigation.

### References

- DynamicComponent interface + submitRef wiring: `web/src/components/designer/DynamicComponent.tsx:10-35, 254-260`
- DataEntryPage current stub: `web/src/components/dataEntry/DataEntryPage.tsx`
- Existing test file to update: `web/src/routes/_app/-data.$designerId.test.tsx`
- useCreateRecord: `web/src/features/data-entry/useCreateRecord.ts`
- useUpdateRecord: `web/src/features/data-entry/useUpdateRecord.ts`
- useDeleteRecord: `web/src/features/data-entry/useDeleteRecord.ts`
- useRecord: `web/src/features/data-entry/useRecord.ts`
- useRecordList: `web/src/features/data-entry/useRecordList.ts`
- DynamicRecord type: `web/src/features/data-entry/recordListApi.ts:10-19`
- usePermission: `web/src/features/auth/usePermission.ts`
- Dialog component: `web/src/components/ui/dialog.tsx`
- Button variants: `web/src/components/ui/button.tsx`
- i18n file: `web/src/lib/i18n/locales/en.json:429-436`
- Architecture Decision 4.10 (DynamicComponent bridge): `_bmad-output/planning-artifacts/architecture.md:621-627`
- Architecture Decision 4.3 (Error strategy): `_bmad-output/planning-artifacts/architecture.md:548-552`
- Architecture Decision 4.4 (Toasts): `_bmad-output/planning-artifacts/architecture.md:554-558`
- AR-46, AR-48, AR-49 definitions: `_bmad-output/planning-artifacts/epics.md:139-142`

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-7

### Debug Log References

- Full vitest run after implementation: 12 test files / 105 passed / 2 failed. The 2 failures are pre-existing flakes in `src/features/admin/menus/__tests__/ReorderableMenuList.test.tsx` (`reorderMutation.mutate is not a function`) reproduced on a clean `git stash` of this story's changes — out of scope here. All 13 tests in this story's two changed test files pass.
- `pnpm tsc --noEmit` clean.
- `pnpm lint` reports 37 errors, all of which are pre-existing `react-refresh/only-export-components` violations across previously-merged routes (admin/menus.tsx, admin/roles*, admin/users*, designer.$designerId.tsx, designer.library.tsx, index.tsx, login.tsx) plus one `react-hooks/set-state-in-effect` in `designer.$designerId.tsx`. None of the errors reference files added or modified by this story; the three new route files (`data.$designerId.tsx`, `data.$designerId.new.tsx`, `data.$designerId.$recordId.tsx`) follow the existing per-route `eslint-disable-next-line react-refresh/only-export-components` suppression convention.

### Completion Notes List

- AC-1 (list view): `RecordListPage` wired to `useRecordList`. Renders truncated id, up to 3 user fieldKeys derived from `data[0]` (system columns filtered via a `SYSTEM_COLUMN_KEYS` set), `createdAt`/`updatedAt`. Pagination via prev/next buttons with boundary disable. "New Record" button gated on `usePermission(designerId, 'canCreate')` and navigates to `/data/$designerId/new`. Row click navigates to `/data/$designerId/$recordId`. Loading shows `t('common.loading')`; load failure fires `toast.error(t('data.entry.loadError'))` via a `useEffect` that only re-runs on `isError` flips.
- AC-2 (create form): `data.$designerId.new.tsx` mounts `DataEntryPage`. `DataEntryPage` now uses `useCreateRecord(designerId)`; `mutation.isPending` replaces the old `isSaving` local state. `handleSave` passes the DynamicComponent payload to `mutation.mutate` with inline `{ onSuccess, onError }` callbacks (matches the Stories 6.3–6.8 hook convention). On success: `toast.success('data.entry.createSuccess')` + navigate back to the list. On error: `toast.error('data.entry.createError')`. Save button disabled until `onReadyChange(true)` AND `onValidityChange(true)` AND not pending. `canCreate=false` renders a permission-denied panel and skips DynamicComponent entirely.
- AC-3 (detail/edit): `data.$designerId.$recordId.tsx` mounts `RecordDetailPage`. The record loads via `useRecord(designerId, recordId)`; the row object is passed verbatim to DynamicComponent as `initialData` — system columns (`id`, `createdAt`, …) land in `initialData` but `extractBindToKeys` only picks up `bindTo` fieldKeys, so they're silently excluded from the submit payload (AR-46 Option C). `canUpdate=true` wires the external Save button + `submitRef` + `onSave`; `canUpdate=false` renders DynamicComponent without `submitRef`/`onSave`/Save chrome (read-only — Dev Notes "DynamicComponent has no `readOnly` prop").
- AC-4 (soft-delete with confirmation): Delete button gated on `usePermission(designerId, 'canDelete')` opens a Radix Dialog (shadcn primitive). Cancel closes without mutating. Confirm fires an **inline** `useMutation` wrapping `deleteRecord` (the shared `useDeleteRecord` hook is left pessimistic for other consumers per Dev Notes). The inline mutation implements AR-49 optimistic delete: `onMutate` cancels in-flight list queries, snapshots every `['data', designerId, 'list']` cache entry, and filters the deleted record out (also decrements `total`). `onError` restores the snapshot and toasts `deleteError`. `onSuccess` invalidates `['data', designerId]`, toasts `deleteSuccess`, and navigates back to the list.
- AC-5 (success feedback): All three mutations (create/update/delete) toast on success and invalidate `['data', designerId]` (create + update via the existing hooks; delete via the inline mutation's `onSuccess`).
- AR-29 compliance: no payload data is interpolated into any toast message — every call uses a static i18n key.
- AR-49 compliance: form saves are pessimistic (server confirmation before UI commits); the list-cache removal on delete is optimistic. The `['data', designerId, 'record', id]` entry is invalidated rather than optimistically cleared, per the story's explicit guidance.
- i18n: added `data.entry.{createSuccess, createError, updateSuccess, updateError, deleteSuccess, deleteError, deleteConfirmTitle, deleteConfirmDescription, loadError, noRecords, permissionDenied, delete, columnId, columnCreatedAt, columnUpdatedAt, prevPage, nextPage, pageInfo}`. Removed the now-unused `saveStub` key (the Task 3 changes deleted the only caller, and Task 7 updated the test assertion to `createSuccess`).
- Testing: `-data.$designerId.test.tsx` now mocks `usePermission`, `useCreateRecord`, `useNavigate`, `useRecordList`, `react-i18next` (interpolation-aware), `sonner`, and `DynamicComponent`. The three pre-existing tests are preserved as DataEntryPage-create tests with the toast assertion updated to `data.entry.createSuccess`; a fourth test covers the `canCreate=false` permission-denied path. Three new `RecordListPage` tests cover the canCreate-gated "New Record" button (visible + navigate) and the row-click → detail navigation. `-data.$designerId.$recordId.test.tsx` is new and covers AC-3 read-only (no Save + no `submitRef`), AC-3 edit-mode end-to-end (mutate + onSuccess toast + onError toast), AC-4 delete button visibility (both polarities), AC-4 dialog open + Cancel (no mutation), and AC-4 dialog confirm (deleteRecord called with `(designerId, id)` → toast.success + navigate). Total: 13 new/updated tests, all pass.

### File List

| Action | File |
|--------|------|
| MODIFY | `web/src/routes/_app/data.$designerId.tsx` |
| CREATE | `web/src/routes/_app/data.$designerId.new.tsx` |
| CREATE | `web/src/routes/_app/data.$designerId.$recordId.tsx` |
| CREATE | `web/src/routes/_app/-data.$designerId.$recordId.test.tsx` |
| MODIFY | `web/src/routes/_app/-data.$designerId.test.tsx` |
| MODIFY | `web/src/components/dataEntry/DataEntryPage.tsx` |
| CREATE | `web/src/components/dataEntry/RecordListPage.tsx` |
| CREATE | `web/src/components/dataEntry/RecordDetailPage.tsx` |
| MODIFY | `web/src/lib/i18n/locales/en.json` |
| MODIFY | `_bmad-output/implementation-artifacts/sprint-status.yaml` |
| MODIFY | `_bmad-output/implementation-artifacts/6-9-dynamic-data-entry-view.md` |

### Review Findings

- [x] [Review][Patch] `page` state not reset when `designerId` changes — `RecordListPage.tsx` holds `const [page, setPage] = useState(1)` with no reset effect. If TanStack Router reuses the component across navigations between different designers, `useRecordList(designerId, { page })` fires with a stale page number for the new designer. Fix: `useEffect(() => setPage(1), [designerId])`. [`RecordListPage.tsx`]
- [x] [Review][Patch] Misleading pagination display when `query.data` is undefined after a load error — `totalPages` falls back to `1` (`query.data?.totalPages ?? 1`), so after an error the UI shows "Page N / 1" and the Next button is disabled, misrepresenting state. Fix: skip rendering the pagination nav or show `?` for `totalPages` when `query.data` is undefined. [`RecordListPage.tsx`]
- [x] [Review][Patch] `RecordDetailPage` falls through to render with `initialData={undefined}` after a fetch error — when `recordQuery.isError=true` and `recordQuery.isLoading=false`, the loading guard (`isLoading && !data`) does not fire. Execution reaches `const initialData = record as ...` where `record` is `undefined`. DynamicComponent receives `initialData={undefined}` and renders an empty form, while the error `useEffect` navigates away asynchronously. Fix: add `if (recordQuery.isError && !recordQuery.data) return null` before the main render. [`RecordDetailPage.tsx`]
- [x] [Review][Patch] Optimistic delete `onMutate` decrements `total` but never updates `totalPages` — after optimistic removal `totalPages` still shows the pre-delete count; the Prev button may stay enabled pointing to a now-empty last page until `onSuccess` invalidates. Fix: also update `totalPages: Math.max(1, Math.ceil((old.total - 1) / pageSize))` in the `setQueriesData` updater (pageSize is 25). [`RecordDetailPage.tsx` — `deleteMutation.onMutate`]
- [x] [Review][Patch] Delete `onError` rollback does not restore the cancelled single-record query — `onMutate` calls `cancelQueries(['data', designerId])` which cancels `['data', designerId, 'record', recordId]`, but `getQueriesData` only snapshots `['data', designerId, 'list']` keys. After a delete error the detail query is left cancelled/stale; `recordQuery.data` may be `undefined` until the next background refetch, causing DynamicComponent to re-render with `initialData={undefined}`. Fix: expand the snapshot to also include `['data', designerId, 'record', recordId]` and restore it in `onError`. [`RecordDetailPage.tsx` — `deleteMutation.onMutate`/`onError`]
- [x] [Review][Patch] `DataEntryPage` shows the permission-denied panel during permissions loading — `usePermission` returns `false` while `usePermissionsQuery` is in-flight (default-deny pattern), so every navigation to `/data/$designerId/new` briefly renders the "permission denied" message before permissions resolve. Fix: destructure `{ data: permissionsData }` from `usePermissionsQuery()` in `DataEntryPage` and gate the denied panel behind `permissionsData && !canCreate` (show nothing / a loading placeholder while `!permissionsData`). [`DataEntryPage.tsx`]
- [x] [Review][Patch] Test gap: `onMutate` optimistic cache removal is not asserted in the delete test — the test verifies `deleteRecord` was called and toast fired, but never asserts the list cache was mutated optimistically before the server responds (AR-49 compliance unverified). Fix: add a test that captures the QueryClient cache, fires `deleteMutation.mutate`, and synchronously asserts the deleted record is absent from the list cache *before* the `deleteRecord` promise resolves. [`-data.$designerId.$recordId.test.tsx`]
- [x] [Review][Defer] `userKeys` derived from `rows[0]` — column count and order unstable across pages or when first row has sparse fields [`RecordListPage.tsx`] — deferred, Story 6.10 replaces with schema-driven columns
- [x] [Review][Defer] `isError` toast fires only once; if the query re-enters error state without flipping `isError`, subsequent failures are silent [`RecordListPage.tsx`] — deferred, pre-existing TanStack Query `isError` flip behavior
- [x] [Review][Defer] `isReady`/`isValid` not reset if `recordId` param changes while component is reused [`RecordDetailPage.tsx`] — deferred, TanStack Router remounts on param change in practice; low risk
- [x] [Review][Defer] `canUpdate` flipping true→false mid-session leaves `updateMutation` in-flight [`RecordDetailPage.tsx`] — deferred, unlikely background-permission-refetch race; acceptable for v1
- [x] [Review][Defer] `deleteConfirmDescription` i18n value exposes internal terminology ("soft-deleted", "admin audit view") to end users [`en.json`] — deferred, UX copy polish; not a functional bug
- [x] [Review][Defer] Update success invalidation delegated to `useUpdateRecord`'s built-in `onSuccess` rather than the component's inline callback — fragile coupling [`RecordDetailPage.tsx`] — deferred, works correctly today; revisit if hook changes

### Change Log

| Date       | Author      | Change |
|------------|-------------|--------|
| 2026-05-26 | dev-claude  | Story 6.9 implemented. Replaced the `data.$designerId.tsx` create-form stub with `RecordListPage` (paginated list, canCreate-gated New Record button, row-click navigation). New routes `data.$designerId.new.tsx` and `data.$designerId.$recordId.tsx` host the create form and detail/edit/delete view. `DataEntryPage` now uses `useCreateRecord` with `mutation.isPending` and inline `onSuccess`/`onError` toasts; permission-denied panel for `canCreate=false`. New `RecordDetailPage` composes `useRecord` + `useUpdateRecord` + an inline AR-49 optimistic-delete `useMutation` wrapping `deleteRecord`; Radix Dialog confirmation for soft-delete. i18n keys added (create/update/delete success/error, dialog text, column headers, pagination, permission-denied, loadError, noRecords, delete); `saveStub` removed. 13 tests added/updated, all pass. Sprint-status 6-9 → review. |
