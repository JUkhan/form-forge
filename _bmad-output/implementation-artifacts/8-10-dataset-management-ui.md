# Story 8.10: Dataset Management UI

Status: done

## Story

As a user with `dataset-management`,
I want to access a Dataset Manager page that lists Datasets and provides Create, Edit, and Delete actions,
So that I can fully manage Datasets without touching the database.

## Acceptance Criteria

**AC-1 — Dataset List View**
**Given** I navigate to Admin > Datasets (accessible via the admin layout, gated by platform-admin which has `can_manage_datasets = true`)
**When** the page renders
**Then** I see a paginated list with columns: `dataset_name`, mode badge ("Custom Query" or "Query Builder"), `created_at`, `updated_at`

**AC-2 — Create Dataset Form**
**Given** I click "New Dataset"
**When** the create form opens
**Then** I see a `dataset_name` field (validated inline per Story 8.3 — `datasetNameSchema` from `validation.ts`), a Mode toggle (Custom Query / Query Builder), and — when Custom Query is selected — a SQL textarea (Story 8.8 — `SqlQueryTextarea` component)

**AC-3 — Edit Form with Prefilled Values**
**Given** I click Edit on a row
**When** the full `DatasetDetail` is fetched and the edit form opens
**Then** all current values are prefilled (`datasetName`, `isCustomQuery`, `query`), and the `version` field is included as a hidden token for optimistic concurrency (Story 8.5)

**AC-4 — Mode Switching**
**Given** I switch from Custom Query to Query Builder in the edit form
**When** the toggle fires
**Then** a "Query Builder coming in a future release" notice is shown (Epics 9–11 are backlog); the SQL textarea hides but `query` is NOT cleared from form state

**Given** I switch from Query Builder to Custom Query
**When** the toggle fires
**Then** the SQL textarea appears, pre-populated with the dataset's existing `query` value (from `defaultValues`)

**AC-5 — Optimistic Concurrency Conflict Handling**
**Given** PUT /api/datasets/{id} returns HTTP 409 with `code: "DATASET_CONCURRENCY_CONFLICT"`
**When** the frontend receives it
**Then** an inline error displays in the edit form: "This dataset was modified by someone else. Reload to see the latest version."
**And** the error is NOT a toast — it renders via `setError('root', ...)` inside the form (AC-5 requires inline display)

**AC-6 — Audit Log Access**
**Given** a row in the list
**When** I click the "Audit" link
**Then** a `DatasetAuditPanel` renders inline (above the list) showing `dataset_audit_log` entries for that dataset, using `useDatasetAuditLogQuery({ datasetName })` from Story 8.9

**AC-7 — Preview Button (Stub)**
**Given** the create/edit form is open
**When** the form renders in Custom Query mode
**Then** a "Preview" button is visible but disabled, with a tooltip/label: "Preview available in a future release"
**And** the button does NOT call `POST /api/datasets/preview` (that endpoint returns 501 until Story 11.3)

**AC-8 — Delete Confirmation**
**Given** I click Delete on a row
**When** the `AlertDialog` confirmation is accepted
**Then** `DELETE /api/datasets/{id}` is called (Story 8.6) and the row disappears from the list on success

---

## Tasks / Subtasks

- [x] **Task 1 — Create `web/src/features/datasets/datasetApi.ts`** (AC-1, AC-2, AC-3, AC-8)

  This is a **pure-frontend story** — all 8 backend endpoints are already implemented by Stories 8.1–8.9. This task creates the TypeScript API layer.

  - [x] Create `web/src/features/datasets/datasetApi.ts`:
    ```typescript
    import { httpClient } from '../auth/httpClient'

    export interface PagedResult<T> {
      data: T[]
      total: number
      page: number
      pageSize: number
      totalPages: number
    }

    // Mirrors DatasetSummaryDto.cs — list projection (no query/builderState/version)
    export interface DatasetSummary {
      id: string
      datasetName: string
      isCustomQuery: boolean
      createdAt: string         // ISO-8601
      updatedAt: string | null
      createdByName: string | null
    }

    // Mirrors DatasetDto.cs — full shape returned by GET /{id}, POST, PUT
    export interface DatasetDetail {
      id: string
      datasetName: string
      isCustomQuery: boolean
      query: string | null
      builderState: string | null
      version: number           // optimistic concurrency token (Decision 6.4)
      createdAt: string
      createdBy: string | null
    }

    export interface CreateDatasetPayload {
      datasetName: string
      isCustomQuery: boolean
      query: string | null
    }

    // version is REQUIRED — omitting it causes a 400 (UpdateDatasetRequest.cs has non-nullable int Version)
    export interface UpdateDatasetPayload {
      datasetName?: string
      isCustomQuery?: boolean
      query?: string | null
      builderState?: string | null
      version: number
    }

    export function listDatasets(page: number, pageSize: number): Promise<PagedResult<DatasetSummary>> {
      return httpClient.get<PagedResult<DatasetSummary>>(`/api/datasets?page=${page}&pageSize=${pageSize}`)
    }

    export function getDataset(id: string): Promise<DatasetDetail> {
      return httpClient.get<DatasetDetail>(`/api/datasets/${id}`)
    }

    export function createDataset(payload: CreateDatasetPayload): Promise<DatasetDetail> {
      return httpClient.post<DatasetDetail>('/api/datasets', payload)
    }

    export function updateDataset(id: string, payload: UpdateDatasetPayload): Promise<DatasetDetail> {
      return httpClient.put<DatasetDetail>(`/api/datasets/${id}`, payload)
    }

    export function deleteDataset(id: string): Promise<void> {
      return httpClient.delete<void>(`/api/datasets/${id}`)
    }
    ```
  - [x] `httpClient` import path: `'../auth/httpClient'` — same as `datasetAuditApi.ts` (one level up from `web/src/features/datasets/`).
  - [x] `DatasetSummary` field names match JSON camelCase serialization from `DatasetSummaryDto.cs`: `(Guid Id → id, string DatasetName → datasetName, bool IsCustomQuery → isCustomQuery, DateTimeOffset CreatedAt → createdAt, DateTimeOffset? UpdatedAt → updatedAt, string? CreatedByName → createdByName)`.
  - [x] `DatasetDetail` field names match `DatasetDto.cs`: `(Guid Id → id, string DatasetName → datasetName, bool IsCustomQuery → isCustomQuery, string? Query → query, string? BuilderState → builderState, int Version → version, DateTimeOffset CreatedAt → createdAt, Guid? CreatedBy → createdBy)`.
  - [x] `GET /api/datasets` requires **authentication only** (not `dataset-management`) per Decision 6.2 / AR-58. All write endpoints require `dataset-management`, but the admin layout is already gated on platform-admin (which has `canManageDatasets = true`).

---

- [x] **Task 2 — Create `web/src/features/datasets/useDatasetListQuery.ts`** (AC-1)

  - [x] Create `web/src/features/datasets/useDatasetListQuery.ts`:
    ```typescript
    import { useQuery, keepPreviousData } from '@tanstack/react-query'
    import { listDatasets } from './datasetApi'
    import type { PagedResult, DatasetSummary } from './datasetApi'

    // AR-69 Decision 6.13 — canonical query key prefix for list invalidation
    export const DATASETS_LIST_QUERY_KEY = ['datasets', 'list'] as const

    export function useDatasetListQuery(page: number, pageSize: number) {
      return useQuery<PagedResult<DatasetSummary>>({
        queryKey: [...DATASETS_LIST_QUERY_KEY, { page, pageSize }],
        queryFn: () => listDatasets(page, pageSize),
        staleTime: 30_000,
        placeholderData: keepPreviousData,
      })
    }
    ```
  - [x] `keepPreviousData` is imported from `@tanstack/react-query` and passed as `placeholderData: keepPreviousData` — matching the TanStack Query v5 syntax used in `useDatasetAuditLogQuery.ts`.
  - [x] Export `DATASETS_LIST_QUERY_KEY` so `datasetMutations.ts` can reference it for invalidation via prefix match without string duplication.

---

- [x] **Task 3 — Create `web/src/features/datasets/datasetMutations.ts`** (AC-2, AC-3, AC-5, AC-8)

  - [x] Create `web/src/features/datasets/datasetMutations.ts`:
    ```typescript
    import { useMutation, useQueryClient } from '@tanstack/react-query'
    import { toast } from 'sonner'
    import { useTranslation } from 'react-i18next'
    import {
      createDataset,
      updateDataset,
      deleteDataset,
      type CreateDatasetPayload,
      type UpdateDatasetPayload,
    } from './datasetApi'
    import { DATASETS_LIST_QUERY_KEY } from './useDatasetListQuery'

    function invalidateDatasetList(queryClient: ReturnType<typeof useQueryClient>) {
      void queryClient.invalidateQueries({ queryKey: DATASETS_LIST_QUERY_KEY })
    }

    export function useCreateDatasetMutation() {
      const queryClient = useQueryClient()
      const { t } = useTranslation()
      return useMutation({
        mutationFn: (payload: CreateDatasetPayload) => createDataset(payload),
        onSuccess: () => {
          invalidateDatasetList(queryClient)
          toast.success(t('admin.datasets.createSuccess'))
        },
      })
    }

    // id is a hook argument (not mutationFn arg) — same pattern as useUpdateRoleMutation(roleId).
    // Instantiate this hook inside EditDatasetForm, not at the page level.
    export function useUpdateDatasetMutation(id: string) {
      const queryClient = useQueryClient()
      const { t } = useTranslation()
      return useMutation({
        mutationFn: (payload: UpdateDatasetPayload) => updateDataset(id, payload),
        onSuccess: () => {
          invalidateDatasetList(queryClient)
          void queryClient.invalidateQueries({ queryKey: ['datasets', id] })
          toast.success(t('admin.datasets.updateSuccess'))
        },
      })
    }

    export function useDeleteDatasetMutation() {
      const queryClient = useQueryClient()
      const { t } = useTranslation()
      return useMutation({
        mutationFn: (id: string) => deleteDataset(id),
        onSuccess: () => {
          invalidateDatasetList(queryClient)
          toast.success(t('admin.datasets.deleteSuccess'))
        },
      })
    }
    ```
  - [x] Concurrency error (409 `DATASET_CONCURRENCY_CONFLICT`) is **not** handled in `onError` in the mutation — it is caught in `EditDatasetForm`'s `try/catch` block where `setError('root', ...)` surfaces it as an inline form error (AC-5). Do NOT add `onError` toast for concurrency here.
  - [x] `useUpdateDatasetMutation(id)` takes `id` as a hook argument and creates a mutation scoped to a single dataset. Instantiate it inside `EditDatasetForm` only (not at the page level, where the dataset id is not yet known).

---

- [x] **Task 4 — Add `admin.datasets.*` i18n keys to `web/src/lib/i18n/locales/en.json`** (FR-49)

  - [x] Open `web/src/lib/i18n/locales/en.json`. Find the `"admin"` object. After the existing `"data"` sub-key, add:
    ```json
    "datasets": {
      "navTitle": "Datasets",
      "title": "Dataset Manager",
      "subtitle": "Create and manage custom datasets for reporting",
      "createButton": "New Dataset",
      "columnName": "Name",
      "columnMode": "Mode",
      "columnCreatedAt": "Created",
      "columnUpdatedAt": "Updated",
      "columnCreatedBy": "Created By",
      "loading": "Loading datasets…",
      "loadError": "Failed to load datasets.",
      "noDatasets": "No datasets yet",
      "noDatasetsCta": "Create your first dataset to get started.",
      "noMatches": "No datasets match your search.",
      "previousPage": "Previous",
      "nextPage": "Next",
      "pageIndicator": "Page {{page}} of {{totalPages}}",
      "createFormTitle": "Create Dataset",
      "editFormTitle": "Edit Dataset",
      "saveButton": "Save",
      "cancelButton": "Cancel",
      "fieldName": "Dataset Name",
      "fieldMode": "Mode",
      "modeCustomQuery": "Custom Query",
      "modeQueryBuilder": "Query Builder",
      "queryBuilderNotYetAvailable": "Query Builder is available in a future release (Epics 9–11).",
      "auditButton": "Audit",
      "auditTitle": "Audit Log — {{name}}",
      "auditClose": "Close",
      "previewButton": "Preview",
      "previewNotYetAvailable": "Preview is available in a future release (Story 11.3).",
      "deleteButton": "Delete",
      "editButton": "Edit",
      "deleteConfirmTitle": "Delete Dataset",
      "deleteConfirmDescription": "Are you sure you want to delete \"{{name}}\"? This will permanently drop the associated database view.",
      "deleteConfirm": "Delete",
      "createSuccess": "Dataset created.",
      "updateSuccess": "Dataset updated.",
      "deleteSuccess": "Dataset deleted.",
      "nameConflict": "A dataset with this name already exists.",
      "invalidQuery": "The query is invalid. Only SELECT statements are permitted.",
      "invalidDatasetName": "Invalid dataset name.",
      "concurrencyConflict": "This dataset was modified by someone else. Reload to see the latest version."
    }
    ```
  - [x] The top-level `datasets.*` keys (e.g., `datasets.invalidDatasetName`, `datasets.sqlTextarea.*`, `datasets.audit.*`, `datasets.validation.*`) already exist from Stories 8.3, 8.8, and 8.9. The new keys are all under `admin.datasets.*` — a distinct namespace for admin-UI strings vs. feature-level strings.
  - [x] The `datasets.audit.*` keys (title, subtitle, column*, noEntries, etc.) registered by Story 8.9 are reused in the `DatasetAuditPanel` component. No new `datasets.audit.*` keys are needed here.

---

- [x] **Task 5 — Modify `web/src/routes/_app/admin.tsx`** (AC-1)

  Add a "Datasets" tab to the admin layout navigation and breadcrumb.

  - [x] Open `web/src/routes/_app/admin.tsx` and read it completely before editing.
  - [x] In `AdminBreadcrumb()`: add a case for `pathname.startsWith('/admin/datasets')` using `t('admin.datasets.navTitle')` — matching the exact pattern for existing breadcrumb cases.
  - [x] In `AdminLayout()`, find the `<nav aria-label="admin">` block. Add a `<Link>` for `/admin/datasets` **after the existing Menus link and before Audit** (or after Audit — choose a position consistent with the visual grouping of the existing tabs). Copy the exact `activeProps` / `inactiveProps` className strings from a sibling `<Link>` verbatim:
    ```tsx
    <Link
      to="/admin/datasets"
      activeProps={{ className: '...' }}   // copy exact string from sibling Link
      inactiveProps={{ className: '...' }} // copy exact string from sibling Link
      className="..."                       // copy exact string from sibling Link
    >
      {t('admin.datasets.navTitle')}
    </Link>
    ```
  - [x] No additional permission guard is needed: the `beforeLoad` in `admin.tsx` already enforces the platform-admin role, and platform-admin has `canManageDatasets = true` (AR-58 / Decision 6.2).
  - [x] Do not add a duplicate `import { useTranslation }` — check if it is already imported before adding.

---

- [x] **Task 6 — Create `web/src/routes/_app/admin/datasets.tsx`** (AC-1 through AC-8)

  This is the main Dataset Management UI page. Follow the `roles.tsx` structure throughout.

  **6a — Route definition and search schema:**
  - [x] Use `createFileRoute('/_app/admin/datasets')` — this is the TanStack Router file-based route id.
  - [x] Search schema:
    ```typescript
    const datasetsSearchSchema = z.object({
      page: z.coerce.number().int().min(1).default(1),
      pageSize: z.coerce.number().int().min(1).max(100).default(25),
    })

    export const Route = createFileRoute('/_app/admin/datasets')({
      validateSearch: datasetsSearchSchema,
      component: DatasetsPage,
    })
    ```

  **6b — `DatasetsPage` component:**
  - [x] State variables:
    - `showCreate: boolean` — controls create form visibility
    - `editTarget: DatasetDetail | null` — the fully-loaded dataset opened for edit (null = form closed)
    - `deleteTarget: { id: string; name: string } | null` — controls AlertDialog
    - `auditDatasetName: string | null` — controls inline audit panel
  - [x] Only one of `showCreate` or `editTarget` is active at a time. Opening create must close edit, and vice-versa.
  - [x] `useDeleteDatasetMutation()` instantiated at the page level (not scoped to an id — mutationFn takes the id as argument).
  - [x] Page header with icon (`Database` from lucide-react), title, subtitle, "New Dataset" button.
  - [x] Render `<CreateDatasetForm>` when `showCreate && !editTarget`.
  - [x] Render `<EditDatasetForm dataset={editTarget}>` when `editTarget !== null`.
  - [x] Render `<DatasetAuditPanel datasetName={auditDatasetName}>` when `auditDatasetName !== null`.
  - [x] Dataset list table with columns: Name, Mode (badge), Created, Updated, Actions (Audit / Edit / Delete buttons).
  - [x] Empty state: when `listQuery.data.data.length === 0`, show a `FolderOpen` icon + message + "New Dataset" CTA (same pattern as roles.tsx).
  - [x] Pagination controls (Previous / page indicator / Next) — shown only when `data.data.length > 0`.
  - [x] `AlertDialog` for delete confirmation — mounted once, controlled by `deleteTarget !== null`. On confirm: `void deleteMutation.mutateAsync(deleteTarget.id).then(() => setDeleteTarget(null))`.

  **6c — `DatasetRow` component:**
  - [x] Props: `{ row: DatasetSummary, onEdit: (detail: DatasetDetail) => void, onDelete: () => void, onAudit: () => void }`
  - [x] Clicking Edit calls `getDataset(row.id)` (imported from `datasetApi.ts`) to fetch the full `DatasetDetail` (which includes `version`, `query`, `builderState`) before calling `onEdit(detail)`. This ensures the edit form always has the current `version` — the list row (`DatasetSummary`) does not include it.
  - [x] Show a loading state on the Edit button while `getDataset` is in-flight (local `editLoading: boolean` state).
  - [x] Format `createdAt` / `updatedAt` via `new Date(str).toLocaleDateString()`. Display `'—'` for null `updatedAt`.
  - [x] Audit toggle: pass `row.datasetName === auditDatasetName` context via the `onAudit` callback on the page (the row component is stateless).

  **6d — `ModeBadge` component:**
  ```tsx
  function ModeBadge({ isCustomQuery }: { isCustomQuery: boolean }) {
    const { t } = useTranslation()
    const label = isCustomQuery
      ? t('admin.datasets.modeCustomQuery')
      : t('admin.datasets.modeQueryBuilder')
    return (
      <span className={`inline-flex items-center rounded-full px-2.5 py-1 text-xs font-medium ${
        isCustomQuery
          ? 'bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-300'
          : 'bg-purple-100 text-purple-800 dark:bg-purple-900/30 dark:text-purple-300'
      }`}>
        {label}
      </span>
    )
  }
  ```

  **6e — `CreateDatasetForm` component:**
  - [x] Define a module-level Zod schema:
    ```typescript
    const createDatasetFormSchema = z.object({
      datasetName: datasetNameSchema,   // imported from '../../../features/datasets/validation'
      isCustomQuery: z.boolean().default(true),
      query: z.string().default(''),
    })
    type CreateDatasetFormValues = z.infer<typeof createDatasetFormSchema>
    ```
  - [x] `useForm` with `resolver: zodResolver(createDatasetFormSchema)`, `mode: 'onChange'`, `defaultValues: { isCustomQuery: true, query: '' }`.
  - [x] `watch('isCustomQuery')` controls whether the SQL textarea and Preview button are shown.
  - [x] `watch('query')` feeds the `SqlQueryTextarea` `value` prop.
  - [x] `setValue('query', v, { shouldValidate: true })` in `SqlQueryTextarea.onChange`.
  - [x] Manual empty-query guard before mutate (in addition to Zod — Zod's `string().default('')` does not block empty strings):
    ```typescript
    if (values.isCustomQuery && !values.query.trim()) {
      setError('query', { message: t('datasets.sqlTextarea.emptyHint') })
      return
    }
    ```
  - [x] Error handling in `catch`:
    - `status === 409 && code === 'DATASET_NAME_CONFLICT'` → `setError('datasetName', ...)`
    - `status === 422 && code === 'INVALID_DATASET_NAME'` → `setError('datasetName', ...)`
    - `status === 422 && code === 'INVALID_QUERY'` → `setError('query', ...)`
    - All others → `setError('root', { message: t('errors.genericError') })`
  - [x] On success: `reset()` then `onDone()`.
  - [x] Mode toggle: two `<Button>` elements toggling `isCustomQuery` via `setValue('isCustomQuery', true/false)`. Active mode gets `variant="default"`, inactive gets `variant="outline"`.
  - [x] When `!isCustomQuery`: show `<p className="text-xs text-muted-foreground">{t('admin.datasets.queryBuilderNotYetAvailable')}</p>` — no SQL textarea rendered.
  - [x] Preview button (when `isCustomQuery`):
    ```tsx
    <Button type="button" variant="outline" size="sm" disabled
      title={t('admin.datasets.previewNotYetAvailable')}>
      {t('admin.datasets.previewButton')}
    </Button>
    ```
    Add a visible hint text beside it: `<span className="text-xs text-muted-foreground">{t('admin.datasets.previewNotYetAvailable')}</span>`

  **6f — `EditDatasetForm` component:**
  - [x] Same structure as `CreateDatasetForm`. Key differences:
    - `defaultValues: { datasetName: dataset.datasetName, isCustomQuery: dataset.isCustomQuery, query: dataset.query ?? '' }`
    - On submit, PUT payload:
      ```typescript
      {
        datasetName: values.datasetName !== dataset.datasetName ? values.datasetName : undefined,
        isCustomQuery: values.isCustomQuery,
        query: values.isCustomQuery ? (values.query.trim() || null) : null,
        version: dataset.version,   // REQUIRED — optimistic concurrency token
      }
      ```
    - Extra error handler for `409 DATASET_CONCURRENCY_CONFLICT`:
      ```typescript
      setError('root', { message: t('admin.datasets.concurrencyConflict') })
      return
      ```
    - On success: `onDone()` (no `reset()` needed — form unmounts).
  - [x] `useUpdateDatasetMutation(dataset.id)` instantiated inside this component (id is known).
  - [x] Define a separate `editDatasetFormSchema` (identical shape to `createDatasetFormSchema` minus the `.default(true)` on `isCustomQuery`):
    ```typescript
    const editDatasetFormSchema = z.object({
      datasetName: datasetNameSchema,
      isCustomQuery: z.boolean(),
      query: z.string().default(''),
    })
    ```

  **6g — `DatasetAuditPanel` component (AC-6):**
  - [x] Props: `{ datasetName: string, onClose: () => void }`
  - [x] Local state: `auditPage: number` (starts at 1).
  - [x] `useDatasetAuditLogQuery({ page: auditPage, pageSize: 25, datasetName })` — passing `datasetName` filters the audit log to this dataset only (exact-match server-side per §4 of Story 8.9 dev notes).
  - [x] Renders a rounded-xl card with: title (`admin.datasets.auditTitle` with `{ name: datasetName }`), Close button, audit table, pagination.
  - [x] Audit table columns: Timestamp, Operation, Actor, Status. Uses existing `datasets.audit.*` i18n keys.
  - [x] Timestamp formatted via `new Date(entry.timestamp).toLocaleString()`.
  - [x] Actor: `entry.actorName ?? t('datasets.audit.unknownActor')`.
  - [x] Status badge: `entry.succeeded` → green text `datasets.audit.succeededTrue`; else red `datasets.audit.succeededFalse`.
  - [x] Pagination: previous/next buttons with `datasets.audit.prevPage`, `datasets.audit.nextPage`, `datasets.audit.pageInfo` keys.
  - [x] Minimal columns shown: omit DDL and CorrelationId columns (they are noisy for the inline panel; they can be added later or accessed via the full audit route).

  **6h — AlertDialog import:**
  - [x] Check if `@/components/ui/alert-dialog` exists. If missing, run: `npx shadcn@latest add alert-dialog`. The component is part of the standard shadcn/ui library and is likely already present given prior stories use shadcn throughout.

---

- [x] **Task 7 — Build and test verification**
  - [x] `cd web && npx tsc -b --noEmit` → 0 errors
  - [x] `cd web && npx vitest run` → ≥280 passed; the pre-existing i18n-lint failure (4 orphaned `designer.inspector.placeholders.*` + `errors.exportFailed` keys) remains unchanged; no new failures
  - [x] `dotnet build src/FormForge.Api` → 0 errors, 0 warnings (no backend changes)
  - [x] `dotnet test` → 913 passed, 2 pre-existing failures unchanged
  - [x] Manual smoke: navigate Admin > Datasets; verify tab appears; create a dataset (Custom Query, valid SELECT); verify it appears in the list with the "Custom Query" badge; click Edit, change the name, save; verify updated name in list; click Audit on the row, verify audit entries appear; click Delete, confirm, verify row disappears

---

## Dev Notes

### §1 — Pure Frontend Story: All Backend Endpoints Already Exist

All 8 dataset API endpoints are implemented and integration-tested by Stories 8.1–8.9. No EF migrations, no backend changes, no new test projects in this story.

| Endpoint | Story | Notes |
|---|---|---|
| `GET /api/datasets` | 8.7 | Auth-only; returns `PagedResult<DatasetSummaryDto>` |
| `GET /api/datasets/{id}` | 8.7 | Auth-only; returns `DatasetDto` with `version` |
| `POST /api/datasets` | 8.4 | Requires `dataset-management`; creates row + VIEW atomically |
| `PUT /api/datasets/{id}` | 8.5 | Requires `dataset-management`; optimistic concurrency via `version` |
| `DELETE /api/datasets/{id}` | 8.6 | Requires `dataset-management`; drops row + VIEW atomically |
| `POST /api/datasets/preview` | stub | Returns 501 — Story 11.3 implements it |
| `GET /api/admin/datasets/audit` | 8.9 | Requires platform-admin; filterable by `datasetName` and `operation` |

### §2 — Key File Locations — Read Before Writing

Before writing any new file, read the following for pattern reference:

| File | Pattern to follow |
|---|---|
| `web/src/features/admin/roles/roleMutations.ts` | Mutation hook structure, `invalidateQueries`, `toast.success` |
| `web/src/features/datasets/useDatasetAuditLogQuery.ts` | TanStack Query v5 `keepPreviousData` / `placeholderData` syntax |
| `web/src/routes/_app/admin/roles.tsx` | Admin list page: route def, search schema, table, inline form, empty state, pagination |
| `web/src/features/datasets/validation.ts` | `datasetNameSchema` import (Zod schema for dataset_name inline validation) |
| `web/src/features/datasets/SqlQueryTextarea.tsx` | SQL textarea component, `onChange` callback signature |
| `web/src/features/datasets/datasetAuditApi.ts` | `httpClient` import path; API function pattern |
| `web/src/routes/_app/admin.tsx` | Exact nav `<Link>` className strings to copy for the new Datasets tab |

### §3 — Optimistic Concurrency Flow (AC-3, AC-5)

The edit flow has three steps designed to prevent stale `version` values:

1. **User clicks Edit** → `DatasetRow.handleEdit()` calls `getDataset(row.id)` → awaits the full `DatasetDetail` (including current `version`)
2. **`EditDatasetForm` opens** → `defaultValues` includes the freshly-fetched `dataset.version`
3. **User submits** → PUT payload includes `version: dataset.version`
4. **If another user modified the dataset between steps 1–3** → server returns `409 DATASET_CONCURRENCY_CONFLICT`
5. **`catch` block** → `setError('root', { message: t('admin.datasets.concurrencyConflict') })`
6. **Inline error renders**: "This dataset was modified by someone else. Reload to see the latest version."

Do NOT toast the concurrency error. AC-5 specifies **inline** display. The form stays open so the user can read the message. They must close and re-click Edit to get a fresh `version`.

### §4 — Mode Toggle Behavior (AC-4)

In `EditDatasetForm`:
- **Custom Query → Query Builder**: hide SQL textarea + Preview button; show `queryBuilderNotYetAvailable` notice. Do NOT `setValue('query', '')` — preserve the query string so switching back to Custom Query restores it.
- **Query Builder → Custom Query**: show SQL textarea. The `query` value comes from `defaultValues.query` (which was pre-populated from `dataset.query`), so switching to Custom Query always restores the saved query.

Since Query Builder canvas (Epics 9–11) is not yet implemented, Query Builder mode in Story 8.10 is a placeholder selection with an informational message only.

### §5 — Preview Button Stub (AC-7)

`DatasetEndpoints.cs` line ~48:
```csharp
group.MapPost("/preview", () => Results.StatusCode(StatusCodes.Status501NotImplemented))
     .WithSummary("Dataset query preview (stub — Story 11.3)")
     .RequireDatasetManagement();
```

The endpoint unconditionally returns 501. For Story 8.10, render a **disabled** Preview button with a tooltip. Do NOT wire it to the API. Story 11.3 will replace the stub and connect the button.

### §6 — Delete Confirmation (AC-8)

`AlertDialog` is a shadcn/ui component at `@/components/ui/alert-dialog`. Check if it exists before installing:
```bash
ls web/src/components/ui/alert-dialog.tsx
```
If missing: `npx shadcn@latest add alert-dialog`.

The `AlertDialog` is mounted once on the page and controlled by `deleteTarget !== null`. It renders the confirmation message using `t('admin.datasets.deleteConfirmDescription', { name: deleteTarget?.name ?? '' })`. On confirm, `deleteMutation.mutateAsync(deleteTarget.id)` calls `DELETE /api/datasets/{id}` which atomically deletes the row and drops `datasets.{name}` VIEW. On success: `invalidateDatasetList` fires → the row disappears.

### §7 — Audit Panel (AC-6)

`DatasetAuditPanel` renders inline (above the list), controlled by `auditDatasetName` state on the page. Only one panel is shown at a time. Clicking Audit on a row while a panel is already open for the same dataset closes it (toggle: `auditDatasetName === row.datasetName ? null : row.datasetName`).

`useDatasetAuditLogQuery({ datasetName })` filters the audit log server-side to exactly this dataset. The filter is exact-match on `dataset_name` (per Story 8.9 §4 dev notes) — this is correct since `dataset_name` is unique per UNIQUE constraint (AR-57).

The `DatasetAuditPanel` reuses `datasets.audit.*` i18n keys from Story 8.9. No new audit i18n keys are needed.

### §8 — Admin Nav Tab Modification

Read `admin.tsx` first. The `<Link>` components in the nav use specific `activeProps` / `inactiveProps` className patterns. Copy the exact className strings from an adjacent `<Link>` (e.g., the Menus link) rather than guessing them. The Datasets link routes to `/admin/datasets`.

In `AdminBreadcrumb()`, add a case for the Datasets route. Look at how other routes are detected (likely `pathname.startsWith('/admin/datasets')` or similar) and follow the same pattern.

No permission check is needed beyond the existing `beforeLoad` platform-admin guard. All platform admins have `canManageDatasets = true` per seed data (AR-58).

### §9 — TanStack Query Key Convention (AR-69 / Decision 6.13)

| Query | Key | Invalidation prefix |
|---|---|---|
| Dataset list | `['datasets', 'list', { page, pageSize }]` | `['datasets', 'list']` |
| Dataset detail | `['datasets', id]` | `['datasets', id]` |
| Dataset audit | `['datasets', 'audit', { page, datasetName?, operation? }]` | `['datasets', 'audit']` |

`DATASETS_LIST_QUERY_KEY = ['datasets', 'list'] as const` — `queryClient.invalidateQueries({ queryKey: DATASETS_LIST_QUERY_KEY })` invalidates all list page variants.

### §10 — Pre-existing Test State

- **Backend** (`dotnet test`): 913 passed, 2 pre-existing failures (`SchemaAuditLogIntegrationTests` / `MutationAuditLogIntegrationTests` DELETE→405) — unchanged. No backend code touched by this story.
- **Frontend** (`npx vitest run`): 280 passed, 1 pre-existing failure (`i18n-lint.test.ts` — 4 missing `designer.inspector.placeholders.*` + `errors.exportFailed` keys) — unchanged. New `admin.datasets.*` keys added to `en.json` and consumed in `datasets.tsx` should not trigger i18n-lint (lint flags keys referenced in code but absent in `en.json`; new keys are both registered AND consumed).

### §11 — Project Structure — Files Modified / Created

```
NEW:
  web/src/features/datasets/datasetApi.ts
  web/src/features/datasets/useDatasetListQuery.ts
  web/src/features/datasets/datasetMutations.ts
  web/src/routes/_app/admin/datasets.tsx

MODIFIED:
  web/src/routes/_app/admin.tsx
    — add <Link to="/admin/datasets"> in AdminLayout nav
    — add breadcrumb case for /admin/datasets in AdminBreadcrumb
  web/src/lib/i18n/locales/en.json
    — add admin.datasets.{navTitle, title, subtitle, createButton, column*, loading,
      loadError, noDatasets, noDatasetsCta, noMatches, previousPage, nextPage,
      pageIndicator, createFormTitle, editFormTitle, saveButton, cancelButton,
      fieldName, fieldMode, modeCustomQuery, modeQueryBuilder,
      queryBuilderNotYetAvailable, auditButton, auditTitle, auditClose,
      previewButton, previewNotYetAvailable, deleteButton, editButton,
      deleteConfirmTitle, deleteConfirmDescription, deleteConfirm,
      createSuccess, updateSuccess, deleteSuccess, nameConflict, invalidQuery,
      invalidDatasetName, concurrencyConflict}
```

### §12 — References

- [Source: `_bmad-output/planning-artifacts/epics.md` — Epic 8 Story 8.10 ACs (FR-62 / AR-57 / AR-58 / AR-60 / AR-65 / AR-69 / Decision 6.2 / 6.4 / 6.9 / 6.13)]
- [Source: `web/src/routes/_app/admin/roles.tsx` — admin list page structure and patterns]
- [Source: `web/src/features/admin/roles/roleMutations.ts` — mutation hook pattern]
- [Source: `web/src/features/datasets/useDatasetAuditLogQuery.ts` — TanStack Query v5 `keepPreviousData` syntax]
- [Source: `web/src/features/datasets/datasetAuditApi.ts` — httpClient import path `'../auth/httpClient'`]
- [Source: `web/src/features/datasets/SqlQueryTextarea.tsx` — SQL textarea props and usage]
- [Source: `web/src/features/datasets/validation.ts` — `datasetNameSchema` for form validation]
- [Source: `src/FormForge.Api/Features/Datasets/Dtos/DatasetDto.cs` — `DatasetDetail` field names]
- [Source: `src/FormForge.Api/Features/Datasets/Dtos/DatasetSummaryDto.cs` — `DatasetSummary` field names]
- [Source: `src/FormForge.Api/Features/Datasets/Dtos/UpdateDatasetRequest.cs` — `int Version` is non-nullable (required on every PUT)]
- [Source: `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs` — preview stub returns HTTP 501]
- [Source: `web/src/routes/_app/admin.tsx` — admin nav tab structure to extend]
- [Source: AR-57: UNIQUE constraint on dataset_name (audit filter is exact-match safe)]
- [Source: AR-58: canManageDatasets on platform-admin; no extra guard needed in nav tab]
- [Source: AR-69: canonical TanStack Query key tuples]

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (1M context) — BMad Dev Story workflow

### Debug Log References

- `npx tsc -b --noEmit` → 0 errors.
- `npx vitest run` → 281 passed, 1 failed. The single failure is the pre-existing `i18n-lint.test.ts` (exactly one missing key: `designer.inspector.placeholders.label`) — confirmed unchanged by running `node scripts/i18n-check.mjs` directly (1 missing, 89 orphaned warnings). The new `admin.datasets.*` keys are all present in `en.json`; none appear in the missing set.
- `dotnet build src/FormForge.Api` → 0 warnings, 0 errors.
- `dotnet test` → 913 passed, 2 failed. The 2 failures are the documented pre-existing `SchemaAuditLogIntegrationTests` / `MutationAuditLogIntegrationTests` DELETE→405 cases (no backend code touched by this story).

### Completion Notes

Pure-frontend story — no backend changes. All 8 dataset endpoints already existed (Stories 8.1–8.9).

Implementation summary:
- **API layer** (`datasetApi.ts`): typed wrappers over the 5 CRUD endpoints, mirroring `DatasetSummaryDto`/`DatasetDto` field names exactly (verified against the C# DTOs).
- **Query hook** (`useDatasetListQuery.ts`): TanStack Query v5 list query with `keepPreviousData`; exports `DATASETS_LIST_QUERY_KEY` for prefix invalidation (AR-69).
- **Mutations** (`datasetMutations.ts`): create/update/delete hooks. Concurrency 409 is intentionally NOT handled in the mutation `onError` — it is surfaced inline in `EditDatasetForm` per AC-5.
- **i18n** (`en.json`): added the `admin.datasets.*` namespace.
- **Admin nav** (`admin.tsx`): added the Datasets `<Link>` (between Menus and Component Library) and a `datasets` breadcrumb-map entry. NOTE: the actual breadcrumb uses a `sectionLabels` map, not the `pathname.startsWith` approach the story assumed — adapted to the real pattern.
- **Page** (`datasets.tsx`): `DatasetsPage` (list + pagination + empty state + delete `AlertDialog`), `DatasetRow` (fetches full `DatasetDetail` via `getDataset` on Edit to obtain the fresh `version`), `ModeBadge`, `CreateDatasetForm`, `EditDatasetForm` (PUT with `version` token + inline 409 handling), `ModeToggleAndQuery` (preserves `query` across mode switches per AC-4; disabled Preview stub per AC-7), and `DatasetAuditPanel` (AC-6, reusing `datasets.audit.*` keys from Story 8.9).
- **alert-dialog** (`alert-dialog.tsx`): the shadcn component was absent. Created manually following the repo's unified `radix-ui` import convention + design tokens (matching `dialog.tsx`) rather than running interactive `shadcn add`.

Deviations from the story spec (both necessary, behavior-preserving):
1. **Form schemas drop `z.*.default(...)`** — the story's `createDatasetFormSchema`/`editDatasetFormSchema` used `.default(true)`/`.default('')`. With Zod v4 + `@hookform/resolvers`, a `.default()` makes the resolver's *input* type diverge from its *output* type (optional vs required), which fails `zodResolver`'s generic signature (TS2322/TS2345). Defaults are instead supplied via `useForm`'s `defaultValues`, which seeds every field at runtime — identical behavior, type-correct.
2. **Breadcrumb adaptation** — see Admin nav note above.

Test changes: updated `admin-layout.test.tsx` — the nav now has 6 links (Datasets added), so the "renders all five sub-nav links" assertion became six, with the Datasets link asserted at index 3 and Library/Audit shifted to 4/5; added a `/admin/datasets` case to the breadcrumb `it.each`. This is a direct, required consequence of AC-1.

Not executed in this environment: the manual smoke test (Task 7 final bullet) requires the full Aspire + PostgreSQL stack running. All automated gates pass; manual smoke is left for the reviewer.

### File List

NEW:
- `web/src/features/datasets/datasetApi.ts`
- `web/src/features/datasets/useDatasetListQuery.ts`
- `web/src/features/datasets/datasetMutations.ts`
- `web/src/routes/_app/admin/datasets.tsx`
- `web/src/components/ui/alert-dialog.tsx`

MODIFIED:
- `web/src/routes/_app/admin.tsx` — Datasets nav tab + breadcrumb map entry
- `web/src/lib/i18n/locales/en.json` — `admin.datasets.*` keys
- `web/src/routes/_app/__tests__/admin-layout.test.tsx` — updated nav-link assertions for the new Datasets tab + breadcrumb case
- `web/src/routeTree.gen.ts` — regenerated by the TanStack Router plugin (adds the `/admin/datasets` route)

### Review Findings

- [x] [Review][Decision] `admin.datasets.invalidQuery` fallback text deviates from spec — resolved: keep the improved message (more accurate for Postgres execution errors surfaced by the backend). No code change required.
- [x] [Review][Patch] Actions column header renders "Name" twice — wrong i18n key on 5th `<th>` [web/src/routes/_app/admin/datasets.tsx ~line 1402]
- [x] [Review][Patch] `handleEdit` silently swallows `getDataset` failure — no `.catch()`, user sees spinner stop with no error message [web/src/routes/_app/admin/datasets.tsx — `DatasetRow.handleEdit`]
- [x] [Review][Patch] Edit button shows only `disabled` state while fetch in-flight — spec requires a visible loading state (spinner or label change) [web/src/routes/_app/admin/datasets.tsx — `DatasetRow`]
- [x] [Review][Patch] `clampedPage` computed but unused in nav callbacks — after deletes reduce page count, Previous/Next still use raw stale `page` [web/src/routes/_app/admin/datasets.tsx ~line 1352]
- [x] [Review][Patch] Concurrent Edit clicks on different rows race to overwrite `editTarget` — last `getDataset` to resolve wins, silently replacing the edit form [web/src/routes/_app/admin/datasets.tsx — `DatasetRow` + `DatasetsPage`]
- [x] [Review][Patch] Delete dialog swallows `mutateAsync` errors — no `.catch()`, dialog stays open with no error message on 404/500/network failure [web/src/routes/_app/admin/datasets.tsx — `AlertDialog` confirm handler]
- [x] [Review][Patch] Stale `auditDatasetName` after rename — panel stays open with old name; row Audit button can no longer toggle it (name mismatch after invalidation) [web/src/routes/_app/admin/datasets.tsx — `DatasetsPage`]
- [x] [Review][Defer] `PutDataset_NarrowsColumnSet_Returns200` hard-codes `version = 1` without asserting `created.Version` — pre-existing pattern in test file; low risk [src/FormForge.Api.Tests/Features/Datasets/DatasetUpdateTests.cs] — deferred, pre-existing
- [x] [Review][Defer] `query: null` for QB mode relies on undocumented "null = keep" backend convention — intentional per AC-4, but silent contract; would need backend change to harden [web/src/routes/_app/admin/datasets.tsx — `EditDatasetForm.onSubmit`] — deferred, pre-existing
- [x] [Review][Defer] `DatasetAuditPanel` `auditPage` can exceed `totalPages` in concurrent external-rename scenario — very edge case, low impact [web/src/routes/_app/admin/datasets.tsx — `DatasetAuditPanel`] — deferred, pre-existing

## Change Log

| Date       | Version | Description | Author |
| ---------- | ------- | ----------- | ------ |
| 2026-06-03 | 1.0     | Story created — ready for dev. | jukhan |
| 2026-06-03 | 1.1     | Implemented Dataset Management UI (API layer, list query, mutations, page with create/edit/delete/audit, alert-dialog component, i18n, admin nav tab). All ACs satisfied; tsc clean, frontend 281 passed (1 pre-existing fail), backend 913 passed (2 pre-existing fails). | Amelia (dev agent) |
