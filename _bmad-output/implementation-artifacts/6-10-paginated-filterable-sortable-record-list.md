# Story 6.10: Paginated, Filterable, Sortable Record List

Status: done

## Story

As a Viewer,
I want to view a paginated list of records, filter by column values, and sort by multiple columns,
so that I can find data quickly.

## Acceptance Criteria

**AC-1 — Schema-driven columns**
**Given** I navigate to a Menu Item with a Schema Binding
**When** the `RecordListPage` loads
**Then** the column headers are derived from the bound designer schema's `fieldKeys` (the `bindTo` values of all leaf elements, stopping at Repeater boundaries)
**And** system columns `id`, `createdAt`, and `updatedAt` are always shown (in that order: id, …user fieldKeys…, createdAt, updatedAt)
**And** columns are stable across pages (not derived from `rows[0]` — fixes the deferred instability from Story 6.9)

**AC-2 — Multi-column sort**
**Given** the record list is displayed
**When** I click a column header (no Shift key)
**Then** the list is re-fetched with `sort=<col>:asc` if the column was unsorted, `sort=<col>:desc` if it was asc, or sort removed if it was desc (cycle: none → asc → desc → none)
**And** if I Shift-click a column header
**Then** that column is appended/toggled as a secondary sort without replacing the primary sort
**And** the sort state is stored in the URL search params as the `sort` query param
**And** a sort indicator (↑ asc / ↓ desc) and priority badge are shown on sorted column headers

**AC-3 — Per-column filter bar**
**Given** the record list is displayed
**When** I type into the filter input below a column header
**Then** the list is re-fetched after a 300 ms debounce with `filter[<col>]=<value>` applied
**And** the filter state is stored in the URL search params as the `filter` object
**And** clearing a filter input removes that key from the filter object
**And** page is reset to 1 whenever any filter changes

**AC-4 — Pagination controls with page-size picker**
**Given** the record list is displayed
**When** I select a page size (10, 25, or 50) from the picker
**Then** the list is re-fetched with the new `pageSize` and page resets to 1
**And** the current page and total page count are displayed
**And** the total record count is displayed (e.g. "42 records")
**And** all pagination state (page, pageSize) is stored in URL search params

**AC-5 — Soft-delete row indicator**
**Given** the record list contains a record where `isDeleted === true`
**When** that row is rendered
**Then** the row text is shown with a strikethrough style
**And** a "Deleted" badge is shown in the `id` cell

---

## Tasks / Subtasks

- [x] **Task 1 — Add `validateSearch` to `data.$designerId.tsx` route** (AC-3, AC-4)
  - [x] Import `z` from `zod`
  - [x] Define the Zod search schema (see Dev Notes §4 for the exact schema)
  - [x] Add `validateSearch: dataSearchSchema` to the `createFileRoute` call
  - [x] Update `RouteComponent` to call `Route.useSearch()` and pass all search params + navigation callbacks as props to `RecordListPage` (see Dev Notes §4 for the prop-drilling pattern)
  - [x] The route component owns URL navigation; `RecordListPage` is a pure display component that calls provided callbacks on sort/filter/page/pageSize changes

- [x] **Task 2 — Create `useDesignerFieldKeys` hook** (AC-1)
  - [x] Create new file: `web/src/features/data-entry/useDesignerFieldKeys.ts`
  - [x] Reuse the same TanStack Query call that `DynamicComponent.tsx` makes: `queryKey: ['component-schema', designerId, 'latest']`, `queryFn: () => designerApi.getSchema(designerId)`, `staleTime: 300_000`
  - [x] Implement a local `extractFieldKeys(el: DesignerElement | null): string[]` function that mirrors `DynamicComponent.tsx:extractBindToKeys` — collects `el.properties.bindTo` values recursively, stopping at Repeater nodes (see Dev Notes §5 for exact logic)
  - [x] Hook returns `{ fieldKeys: string[], isLoading: boolean, isError: boolean }`
  - [x] Since both `DynamicComponent` and this hook use the same query key, the schema is served from cache when the user has already loaded the form view — no extra network round-trip in typical use

- [x] **Task 3 — Major enhancement of `RecordListPage`** (AC-1, AC-2, AC-3, AC-4, AC-5)
  - [x] **3a — New props interface**: Remove local `page` / `pageSize` state; accept them as props along with `sort`, `filter`, and four navigation callbacks (see Dev Notes §4)
  - [x] **3b — Schema-driven columns (AC-1)**: Call `useDesignerFieldKeys(designerId)` instead of `deriveUserFieldKeys(rows[0])`. While the schema is loading show a loading indicator (Story 6.11 will replace with Skeleton). If schema returns an error, fall back to empty user columns (show only `id`, `createdAt`, `updatedAt`)
  - [x] **3c — Sort headers (AC-2)**: Render each column header as a clickable `<button>`. Show a sort direction indicator (↑ / ↓) and a numeric priority badge for multi-column sort. Click handler calls `onSortChange(newSortString)` (see Dev Notes §6 for the sort string builder logic)
  - [x] **3d — Filter bar (AC-3)**: Render a `<tr>` below the header row with an `<input type="text">` per column. Use local state for the filter input value and debounce URL update by 300 ms via `useEffect` + `setTimeout` cleanup pattern (see Dev Notes §7). Call `onFilterChange(newFilter)` and `onPageChange(1)` after debounce fires
  - [x] **3e — Page-size picker (AC-4)**: Add a `<select>` before the pagination nav with options 10, 25, 50. On change: call `onPageSizeChange(newSize)` (the route resets page to 1 automatically — see Dev Notes §4)
  - [x] **3f — Total count display (AC-4)**: Show `t('data.entry.totalRecords', { total: query.data?.total ?? 0 })` near the pagination controls
  - [x] **3g — Soft-delete indicator (AC-5)**: When `row.isDeleted === true`, apply `line-through` text-decoration to all cells and render a small "Deleted" badge in the id cell (see Dev Notes §8)
  - [x] **3h — Preserve existing behaviours**: Loading state, error toast, "New Record" button, row-click navigation, and all existing i18n keys must remain unchanged

- [x] **Task 4 — Update i18n keys in `en.json`** (AC-2, AC-3, AC-4, AC-5)
  - [x] Add new keys to the `"data"."entry"` block (see Dev Notes §9 for the exact list)
  - [x] Do NOT remove any existing keys — other tests reference them

- [x] **Task 5 — Update tests in `-data.$designerId.test.tsx`** (all ACs)
  - [x] Add mock for `useDesignerFieldKeys` (see Dev Notes §10 for mock pattern)
  - [x] Update the `RecordListPage` describe block's `resetAll()` to reset the new mock
  - [x] Add test: schema-driven columns render headers matching `fieldKeys` result (AC-1)
  - [x] Add test: clicking a column header with no sort cycles to `asc`, calls `onSortChange('col:asc')` (AC-2)
  - [x] Add test: clicking same column header again (sort=`col:asc`) cycles to `desc` (AC-2)
  - [x] Add test: clicking same column header again (sort=`col:desc`) removes sort — calls `onSortChange(undefined)` (AC-2)
  - [x] Add test: filter input change triggers `onFilterChange` after 300 ms debounce via fake timer (AC-3); use `vi.useFakeTimers()` / `vi.advanceTimersByTime(300)` pattern
  - [x] Add test: clearing filter input calls `onFilterChange` with the key removed (AC-3)
  - [x] Add test: page-size picker change calls `onPageSizeChange(10)` (AC-4)
  - [x] Add test: total record count rendered with correct value (AC-4)
  - [x] Add test: row with `isDeleted: true` has `line-through` class and "Deleted" badge (AC-5)
  - [x] Existing 3 `RecordListPage` tests (New Record button visibility, row click) remain unchanged — they pass `onSortChange`, `onFilterChange`, `onPageChange`, `onPageSizeChange` props as `vi.fn()` stubs

---

## Dev Notes

### §1 — Scope Boundaries

**Story 6.10 scope**: Schema-driven columns, multi-column sort, per-column filter bar, 10/25/50 page-size picker, soft-delete row indicator, total count display, URL-based state for all list controls.

**NOT in scope** (covered by later stories):
- Story 6.11: shadcn `Skeleton` loading states replacing `<p>{t('common.loading')}</p>`, empty-state CTA component, `ErrorBanner` with retry + correlation ID display, server `ValidationProblemDetails` → `setError()` field-level form errors.
- **No backend changes** — `GET /api/data/{designerId}` already supports `sort`, `filter[key]=value`, `page`, and `pageSize` params (confirmed in `recordListApi.ts`).

### §2 — Existing Files: Current State and What Changes

**`web/src/routes/_app/data.$designerId.tsx`** (MODIFY)
- Currently: thin wrapper, no search params, renders `<RecordListPage designerId={designerId} />`
- After: adds `validateSearch` with Zod schema; `RouteComponent` extracts search params via `Route.useSearch()` and passes them + navigation callbacks as props to `RecordListPage`

**`web/src/components/dataEntry/RecordListPage.tsx`** (MODIFY — major enhancement)
- Currently: `page` is local `useState(1)`. `userKeys` derived from `rows[0]` (unstable). No sort, no filter, no page-size picker.
- After: all pagination/sort/filter state comes from props (passed down from route). `fieldKeys` from `useDesignerFieldKeys`. Full sort + filter + page-size UI.
- The `useEffect(() => setPage(1), [designerId])` pattern is **removed** — no longer needed because each `designerId` is its own route URL with fresh default search params.

**`web/src/features/data-entry/useRecordList.ts`** (NO CHANGE)
- Already accepts `RecordListParams` with `sort`, `filter`, `page`, `pageSize`. No modification needed.

**`web/src/features/data-entry/recordListApi.ts`** (NO CHANGE)
- `RecordListParams.sort` is `string` (comma-separated `col:dir` pairs) — already correct.
- `RecordListParams.filter` is `Record<string, string>` — already correct.

**`web/src/routes/_app/-data.$designerId.test.tsx`** (MODIFY)
- Add `useDesignerFieldKeys` mock.
- Update `RecordListPage` tests to pass required props (sort/filter/page/pageSize callbacks).
- Add new AC-specific tests (see Task 5).

### §3 — Architecture Compliance

- **AR-46 (Option C hybrid)**: System column JSON keys are camelCase (`id`, `createdAt`, `updatedAt`); user fieldKey columns are verbatim snake_case. Sort params use the PG column name (which for user fields is the verbatim `bindTo` value; for system columns use the camelCase JSON key). No transformation needed.
- **AR-48 (Query Key Tuples)**: `useRecordList` already uses `['data', designerId, 'list', params]`. Changing `params` (sort/filter/page/pageSize) automatically creates new cache entries. `keepPreviousData` keeps the old page visible during transition.
- **AR-49 (Mutation Strategy)**: No change — mutations remain pessimistic/optimistic as Story 6.9 established.
- **FR-40**: All five requirements (schema-driven columns, multi-column sort, per-column filter, paginated with picker, soft-delete indicator) are addressed by this story.
- **validateSearch pattern**: Matches `admin/menus.tsx` and `admin/users.tsx` patterns exactly (`z.coerce.number().int().min(1).default(N)` for numeric params, `z.string().optional()` for string params).

### §4 — Route and Props Pattern

**Route file after** (`web/src/routes/_app/data.$designerId.tsx`):
```typescript
import { createFileRoute, useNavigate } from '@tanstack/react-router'
import { z } from 'zod'
import { RecordListPage } from '@/components/dataEntry/RecordListPage'

const dataSearchSchema = z.object({
  page: z.coerce.number().int().min(1).default(1),
  pageSize: z.coerce.number().int().min(10).max(100).default(25),
  sort: z.string().optional(),
  filter: z.record(z.string()).optional(),
})

export const Route = createFileRoute('/_app/data/$designerId')({
  validateSearch: dataSearchSchema,
  component: RouteComponent,
})

// eslint-disable-next-line react-refresh/only-export-components
function RouteComponent() {
  const { designerId } = Route.useParams()
  const { page, pageSize, sort, filter } = Route.useSearch()
  // `from` matches the codebase's `useNavigate({ from: '/admin/menus' })` pattern in menus.tsx:34
  const navigate = useNavigate({ from: '/_app/data/$designerId' })

  return (
    <RecordListPage
      designerId={designerId}
      page={page}
      pageSize={pageSize}
      sort={sort}
      filter={filter}
      onPageChange={(p) =>
        void navigate({ search: (prev) => ({ ...prev, page: p }) })
      }
      onPageSizeChange={(ps) =>
        void navigate({ search: (prev) => ({ ...prev, pageSize: ps, page: 1 }) })
      }
      onSortChange={(s) =>
        void navigate({ search: (prev) => ({ ...prev, sort: s, page: 1 }) })
      }
      onFilterChange={(f) =>
        void navigate({ search: (prev) => ({ ...prev, filter: f, page: 1 }) })
      }
    />
  )
}
```

**`RecordListPage` new props interface**:
```typescript
interface RecordListPageProps {
  designerId: string
  page: number
  pageSize: number
  sort?: string
  filter?: Record<string, string>
  onPageChange: (page: number) => void
  onPageSizeChange: (pageSize: number) => void
  onSortChange: (sort: string | undefined) => void
  onFilterChange: (filter: Record<string, string> | undefined) => void
}
```

> **Why props instead of `Route.useSearch()` in the component?** `RecordListPage` lives in `components/dataEntry/` — coupling it to a specific route's search schema would make it harder to test (TanStack Router mocks are complex) and would break if the component is ever reused. Passing props follows the same pattern as Story 6.9's `designerId` prop and keeps `RecordListPage` a pure display component.

> **Navigation pattern reference**: The `useNavigate({ from: '...' })` + `void navigate({ search: ... })` pattern is the established codebase convention — see `web/src/routes/_app/admin/menus.tsx:34,41-43`. The `from` option provides type-safe route context. `void` discards the navigation promise (intentional — no await needed). The function form `search: (prev) => ({ ...prev, ... })` merges partial updates, whereas the object form `search: { page, pageSize }` replaces all params (only safe if you enumerate every param).

### §5 — `useDesignerFieldKeys` Hook

**New file: `web/src/features/data-entry/useDesignerFieldKeys.ts`**

Key points:
- Uses the **same query key** as `DynamicComponent.tsx`: `['component-schema', designerId, 'latest']`. This means the first component to load (either `DynamicComponent` or `RecordListPage`) populates the cache; the other gets it for free.
- The `extractFieldKeys` function mirrors `DynamicComponent.tsx:extractBindToKeys` (lines 37–52). It is not exported from `DynamicComponent` so we duplicate the logic here. **Do NOT import from DynamicComponent** — that creates a circular dependency between features and components.
- Stops recursion at `type === 'Repeater'` boundaries to avoid collecting Repeater row-level fields as top-level columns.

```typescript
import { useQuery } from '@tanstack/react-query'
import { useMemo } from 'react'
import * as designerApi from '@/features/designer/designerApi'
import type { DesignerElement } from '@/types/designer'

function extractFieldKeys(el: DesignerElement | null): string[] {
  if (!el) return []
  const keys: string[] = []
  const bindTo = el.properties?.bindTo
  if (bindTo && typeof bindTo === 'string' && bindTo.trim() !== '') {
    keys.push(bindTo)
  }
  if (el.type === 'Repeater') return keys
  for (const child of el.children ?? []) {
    keys.push(...extractFieldKeys(child))
  }
  return keys
}

export function useDesignerFieldKeys(designerId: string) {
  const query = useQuery({
    queryKey: ['component-schema', designerId, 'latest'] as const,
    queryFn: () => designerApi.getSchema(designerId),
    staleTime: 300_000,
    enabled: !!designerId,
  })
  const fieldKeys = useMemo(
    () => extractFieldKeys(query.data?.rootElement ?? null),
    [query.data?.rootElement],
  )
  return { fieldKeys, isLoading: query.isLoading, isError: query.isError }
}
```

Import path for `designerApi`: `@/features/designer/designerApi` (confirmed at `web/src/features/designer/designerApi.ts`).
Import path for `DesignerElement`: `@/types/designer` (confirmed at `web/src/types/designer.ts`).

### §6 — Multi-Column Sort Logic

Sort string format: `"col1:asc,col2:desc"` (comma-separated `name:dir` pairs, up to 3 per architecture).

**Parse** the URL `sort` string into an array:
```typescript
type SortEntry = { key: string; dir: 'asc' | 'desc' }

function parseSortString(sort: string | undefined): SortEntry[] {
  if (!sort) return []
  return sort
    .split(',')
    .map((s) => s.split(':'))
    .filter((parts): parts is [string, string] => parts.length === 2 && (parts[1] === 'asc' || parts[1] === 'desc'))
    .map(([key, dir]) => ({ key, dir: dir as 'asc' | 'desc' }))
}

function serializeSortEntries(entries: SortEntry[]): string | undefined {
  if (entries.length === 0) return undefined
  return entries.map((e) => `${e.key}:${e.dir}`).join(',')
}
```

**Click handler** (used for each column header button):
```typescript
function handleSortClick(colKey: string, shiftKey: boolean, currentSort: SortEntry[]) {
  const existing = currentSort.find((e) => e.key === colKey)

  if (!shiftKey) {
    // Replace sort entirely for this column
    if (!existing) return [{ key: colKey, dir: 'asc' as const }]
    if (existing.dir === 'asc') return [{ key: colKey, dir: 'desc' as const }]
    return [] // was desc → remove
  } else {
    // Shift-click: add/toggle/remove this column from multi-sort
    if (!existing) {
      return [...currentSort, { key: colKey, dir: 'asc' as const }]
    }
    if (existing.dir === 'asc') {
      return currentSort.map((e) => e.key === colKey ? { ...e, dir: 'desc' as const } : e)
    }
    return currentSort.filter((e) => e.key !== colKey)
  }
}
```

**In the column header render** (within the `<th>` cell):
```typescript
const sortEntries = parseSortString(sort)
const sortIndex = sortEntries.findIndex((e) => e.key === colKey) // -1 = unsorted
const sortDir = sortIndex >= 0 ? sortEntries[sortIndex].dir : null
const sortPriority = sortEntries.length > 1 ? sortIndex + 1 : null // show priority badge only for multi-sort

<th key={colKey}>
  <button
    type="button"
    onClick={(e) => {
      const newEntries = handleSortClick(colKey, e.shiftKey, sortEntries)
      onSortChange(serializeSortEntries(newEntries))
    }}
    className="flex items-center gap-1 font-mono text-xs font-medium text-slate-600 hover:text-slate-900"
  >
    {colKey}
    {sortDir === 'asc' && <span aria-label={t('data.entry.sortAsc')}>↑</span>}
    {sortDir === 'desc' && <span aria-label={t('data.entry.sortDesc')}>↓</span>}
    {sortPriority && <span className="text-xs text-slate-400">{sortPriority}</span>}
  </button>
</th>
```

Apply the same pattern to `id`, `createdAt`, and `updatedAt` system column headers (using their camelCase JSON key name as the sort key). For the filter bar, system columns `id` and `createdAt`/`updatedAt` may not support filtering — leave their filter inputs disabled or omit them. The backend `DynamicQueryBuilder` may not support filtering on system columns via the `filter[key]` mechanism. **Check `DynamicDataEndpoints.cs:ListRecordsHandler` or `DynamicQueryBuilder.cs:BuildListQuery` to confirm which columns are filterable.** If system column filtering is not supported by the backend, render filter inputs only for user `fieldKey` columns. Rendering a disabled (or omitted) `<td>` for system columns in the filter row is acceptable.

### §7 — Per-Column Filter Bar with Debounce

Render a `<tr>` immediately below the header `<tr>` with `<td>` cells. For each user `fieldKey`, render an `<input type="text">`. System columns (`id`, `createdAt`, `updatedAt`) should have empty `<td>` cells (no filter input — see §6 note about backend support).

**Local buffer state + debounce pattern**:
```typescript
// Local buffer state mirrors the URL filter to avoid URL-thrashing on each keystroke
const [filterBuffer, setFilterBuffer] = useState<Record<string, string>>(filter ?? {})

// Sync buffer with incoming URL filter (e.g. browser back/forward)
useEffect(() => {
  setFilterBuffer(filter ?? {})
}, [filter])

// Debounce: write to URL 300ms after last keystroke
useEffect(() => {
  const id = setTimeout(() => {
    const cleanFilter = Object.fromEntries(
      Object.entries(filterBuffer).filter(([, v]) => v !== '')
    )
    onFilterChange(Object.keys(cleanFilter).length > 0 ? cleanFilter : undefined)
    onPageChange(1)
  }, 300)
  return () => clearTimeout(id)
}, [filterBuffer]) // intentionally omit onFilterChange/onPageChange from deps — they are stable callbacks from the route

// Filter input handler
function handleFilterInput(colKey: string, value: string) {
  setFilterBuffer((prev) =>
    value === '' ? Object.fromEntries(Object.entries(prev).filter(([k]) => k !== colKey)) : { ...prev, [colKey]: value }
  )
}
```

> **Warning**: the `useEffect` for the debounce depends on `filterBuffer` only. ESLint's `exhaustive-deps` rule may flag the omitted `onFilterChange` and `onPageChange`. Add an `// eslint-disable-next-line react-hooks/exhaustive-deps` comment with the explanation that these are stable route callbacks — or use `useCallback`-wrapped stable refs if preferred. The intent is to avoid re-triggering the debounce timer on every render.

### §8 — Soft-Delete Indicator

Check `row.isDeleted === true` from `DynamicRecord.isDeleted: boolean`.

```typescript
// In the <tr> for each row:
<tr
  key={row.id}
  onClick={() => navigate({ to: '/data/$designerId/$recordId', params: { designerId, recordId: row.id } })}
  className={`cursor-pointer border-b border-slate-100 hover:bg-slate-50 ${
    row.isDeleted ? 'opacity-60' : ''
  }`}
>
  <td className="px-2 py-1 font-mono text-xs">
    <span className={row.isDeleted ? 'line-through' : ''}>
      {truncateId(row.id)}
    </span>
    {row.isDeleted && (
      <span className="ml-1 rounded bg-red-100 px-1 py-0.5 text-xs font-medium text-red-700">
        {t('data.entry.deletedBadge')}
      </span>
    )}
  </td>
  {fieldKeys.map((k) => (
    <td key={k} className={`px-2 py-1 ${row.isDeleted ? 'line-through text-slate-400' : ''}`}>
      {formatCell(row[k])}
    </td>
  ))}
  <td className={`px-2 py-1 whitespace-nowrap ${row.isDeleted ? 'line-through text-slate-400' : ''}`}>
    {formatTimestamp(row.createdAt)}
  </td>
  <td className={`px-2 py-1 whitespace-nowrap ${row.isDeleted ? 'line-through text-slate-400' : ''}`}>
    {formatTimestamp(row.updatedAt)}
  </td>
</tr>
```

### §9 — New i18n Keys

Add these to `"data"."entry"` in `web/src/lib/i18n/locales/en.json`:

```json
"sortAsc": "Sort ascending",
"sortDesc": "Sort descending",
"clearSort": "Clear sort",
"filterPlaceholder": "Filter…",
"pageSizeLabel": "Per page:",
"totalRecords": "{{total}} records",
"deletedBadge": "Deleted"
```

**Do not remove** any existing keys (`newRecord`, `save`, `saving`, `delete`, `createSuccess`, `createError`, `updateSuccess`, `updateError`, `deleteSuccess`, `deleteError`, `deleteConfirmTitle`, `deleteConfirmDescription`, `loadError`, `noRecords`, `permissionDenied`, `columnId`, `columnCreatedAt`, `columnUpdatedAt`, `prevPage`, `nextPage`, `pageInfo`).

### §10 — Test Patterns for New Functionality

**New mock for `useDesignerFieldKeys`**:
```typescript
const designerFieldKeysHandles = vi.hoisted(() => ({
  fieldKeys: [] as string[],
  isLoading: false,
  isError: false,
}))
vi.mock('@/features/data-entry/useDesignerFieldKeys', () => ({
  useDesignerFieldKeys: () => ({ ...designerFieldKeysHandles }),
}))
```

Reset in `resetAll()`:
```typescript
designerFieldKeysHandles.fieldKeys = []
designerFieldKeysHandles.isLoading = false
designerFieldKeysHandles.isError = false
```

**Updated `RecordListPage` render call** — all tests now need to pass the required props:
```typescript
const onPageChange = vi.fn()
const onPageSizeChange = vi.fn()
const onSortChange = vi.fn()
const onFilterChange = vi.fn()

render(
  <RecordListPage
    designerId="my-form"
    page={1}
    pageSize={25}
    sort={undefined}
    filter={undefined}
    onPageChange={onPageChange}
    onPageSizeChange={onPageSizeChange}
    onSortChange={onSortChange}
    onFilterChange={onFilterChange}
  />
)
```

**Sort test example** (AC-2):
```typescript
it('clicking an unsorted column header calls onSortChange with col:asc (AC-2)', () => {
  designerFieldKeysHandles.fieldKeys = ['title', 'status']
  recordListHandles.data = { data: [/* ... */], total: 1, page: 1, pageSize: 25, totalPages: 1 }

  render(<RecordListPage designerId="my-form" page={1} pageSize={25} sort={undefined} filter={undefined}
    onPageChange={vi.fn()} onPageSizeChange={vi.fn()} onSortChange={onSortChange} onFilterChange={vi.fn()} />)

  fireEvent.click(screen.getByRole('button', { name: /title/ }))
  expect(onSortChange).toHaveBeenCalledWith('title:asc')
})
```

**Filter debounce test example** (AC-3):
```typescript
it('filter input calls onFilterChange with debounce of 300ms (AC-3)', async () => {
  vi.useFakeTimers()
  designerFieldKeysHandles.fieldKeys = ['title']
  recordListHandles.data = { data: [], total: 0, page: 1, pageSize: 25, totalPages: 1 }

  render(<RecordListPage ... />)
  const filterInput = screen.getByPlaceholderText('data.entry.filterPlaceholder')
  fireEvent.change(filterInput, { target: { value: 'foo' } })

  // Not called yet — within debounce window
  expect(onFilterChange).not.toHaveBeenCalled()

  vi.advanceTimersByTime(300)
  expect(onFilterChange).toHaveBeenCalledWith({ title: 'foo' })

  vi.useRealTimers()
})
```

### §11 — File Locations

All new/modified files:

| Action | File |
|--------|------|
| MODIFY | `web/src/routes/_app/data.$designerId.tsx` |
| CREATE | `web/src/features/data-entry/useDesignerFieldKeys.ts` |
| MODIFY | `web/src/components/dataEntry/RecordListPage.tsx` |
| MODIFY | `web/src/routes/_app/-data.$designerId.test.tsx` |
| MODIFY | `web/src/lib/i18n/locales/en.json` |

**No backend changes.** `GET /api/data/{designerId}` already supports all required params.

### §12 — Critical Do-Nots

- **Do NOT import `extractBindToKeys` from `DynamicComponent.tsx`** — it is not exported and the import would create a circular dependency. Duplicate the ~15-line function in `useDesignerFieldKeys.ts`.
- **Do NOT modify `useRecordList.ts`** — it already accepts full `RecordListParams`. Just pass the new params object.
- **Do NOT modify `recordListApi.ts`** — already correct.
- **Do NOT put URL navigation logic in `RecordListPage`** — all `navigate()` calls belong in the route component. `RecordListPage` calls the provided callbacks only.
- **Do NOT reset `page` with a `useEffect` in `RecordListPage`** — the old `useEffect(() => setPage(1), [designerId])` is gone. URL-based state means each `designerId` URL starts with default search params.
- **Do NOT delete any existing i18n keys** — tests reference them.

### §13 — Previous Story Dev Notes (Story 6.9) — Applicable Learnings

- `useRecordList` uses `keepPreviousData` — the table stays populated during sort/filter/page transitions. No flicker.
- System column keys are `SYSTEM_COLUMN_KEYS = new Set(['id', 'createdAt', 'createdBy', 'updatedAt', 'updatedBy', 'isDeleted', 'cascadeEventId'])`. The `deriveUserFieldKeys` function and this Set can be removed in favour of `useDesignerFieldKeys`.
- `formatTimestamp`, `formatCell`, `truncateId` helper functions already exist in `RecordListPage.tsx` — keep and reuse them.
- `usePermission`, `useNavigate`, `toast` import patterns from Story 6.9 remain unchanged.
- The `aria-busy={query.isFetching}` attribute on the root `<div>` is an existing accessibility hint — preserve it.

### §14 — References

- `extractBindToKeys` source of truth: `web/src/components/designer/DynamicComponent.tsx:37-52`
- `DesignerElement` and `ComponentSchemaDto` types: `web/src/types/designer.ts:6-59`
- `designerApi.getSchema` signature: `web/src/features/designer/designerApi.ts`
- `RecordListParams` interface: `web/src/features/data-entry/recordListApi.ts`
- `useRecordList` hook: `web/src/features/data-entry/useRecordList.ts`
- `validateSearch` reference patterns: `web/src/routes/_app/admin/menus.tsx`, `web/src/routes/_app/admin/users.tsx`
- Current `RecordListPage` (to be modified): `web/src/components/dataEntry/RecordListPage.tsx`
- Current test file (to be modified): `web/src/routes/_app/-data.$designerId.test.tsx`
- i18n file: `web/src/lib/i18n/locales/en.json` (current `data.entry` block)
- Architecture FR-40, AR-46, AR-48: `_bmad-output/planning-artifacts/architecture.md`

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-7[1m]

### Debug Log References

- Story 6.10 implementation cycle:
  - Typecheck (`npx tsc --noEmit`) — clean (exit 0)
  - Lint (`npx eslint` on the four changed files) — clean (exit 0)
  - Story tests (`vitest run src/routes/_app/-data.$designerId.test.tsx`) — 16/16 pass
  - Full frontend vitest — 115 passed / 2 failed (117 total). Both failures are in `src/features/admin/menus/__tests__/ReorderableMenuList.test.tsx` and are pre-existing on the baseline (verified by stashing this story's changes and re-running the same test file — same 2 failures). Not introduced by Story 6.10.
  - Production build (`npx vite build`) — clean.
- One refactor inside the implementation cycle: the original Story-6.10 plan kept the filter-buffer + URL→buffer resync inside `RecordListPage` itself. Lint flagged the resync `useEffect` (`react-hooks/set-state-in-effect`). Resolved by extracting a `FilterRow` child and using the project's outer/inner key-remount convention (the same pattern `ReorderableMenuList.tsx` uses): the parent passes `key={JSON.stringify(filter ?? {})}` so external URL changes remount the row with a fresh `initialFilter` seed, and the buffer→URL debounce stays in the child.

### Completion Notes List

- **AC-1 — Schema-driven columns**: `useDesignerFieldKeys` (new file `web/src/features/data-entry/useDesignerFieldKeys.ts`) calls `designerApi.getSchema(designerId)` under the same `['component-schema', designerId, 'latest']` queryKey that `DynamicComponent.tsx` uses, so the schema is shared with no extra round-trip in typical list→form navigation. `extractFieldKeys` is duplicated rather than imported (DynamicComponent's helper is not exported; importing would couple `features/` to `components/`). System columns `id`, `createdAt`, `updatedAt` are always rendered in that fixed order regardless of `rows`, so the table shape is stable across pages — Story 6.9's `rows[0]`-derived instability is gone. When the schema query is errored, user columns fall back to empty so the system columns alone still render.
- **AC-2 — Multi-column sort**: each column header is a `<SortableHeader>` button. Click cycle is the spec's none → asc → desc → none. Shift-click appends or toggles a secondary sort. `parseSortString`/`serializeSortEntries` handle the `col:dir,col2:dir2` URL contract. A numeric badge renders next to the indicator only when multi-sort is active.
- **AC-3 — Per-column filter bar**: `FilterRow` child owns the local input buffer and a 300 ms `setTimeout` debounce that pushes a cleaned filter object (empty keys dropped) and resets page to 1 only when the buffer differs from the seed (so initial mount is silent). Outer/inner key-remount handles external URL filter changes. No filter input on the `id` system column (Dev Notes §6 — backend `filter[key]=value` does not cover system columns today).
- **AC-4 — Pagination with page-size picker**: `<select>` of 10/25/50 calls `onPageSizeChange`; the route handler resets `page` to 1. Total count, page-size picker, prev/next, and "Page N of M" all live in one `<nav>`. `totalPages` falls back to the current `page` on undefined data so the "Page 5 / 1" Story 6.9 wart is preserved-fixed here too.
- **AC-5 — Soft-delete row indicator**: when `row.isDeleted === true` the row gets `opacity-60`, every cell's content gets `line-through text-slate-400`, and the id cell appends a small red "Deleted" badge keyed off `data.entry.deletedBadge`.
- **URL state**: `web/src/routes/_app/data.$designerId.tsx` gains `validateSearch` with the Zod schema from Dev Notes §4 (page ≥ 1 default 1, pageSize 10–100 default 25, optional sort string, optional `Record<string, string>` filter). The route owns navigation; `RecordListPage` is a pure display + callbacks surface and is therefore independently unit-testable without the TanStack Router test harness.
- **i18n**: added 7 new keys under `data.entry` (`sortAsc`, `sortDesc`, `clearSort`, `filterPlaceholder`, `pageSizeLabel`, `totalRecords`, `deletedBadge`). No existing keys removed. The test's identity translator was extended to append unmatched opt values as a suffix on the key so AC-4 can assert the count value flowed through to the t-call site (the previous mock returned the bare key with no opt visibility; that's enough to render the i18n label but not enough to prove the `total` got passed in).
- **No backend changes**: Dev Notes §11 confirmed; `GET /api/data/{designerId}` already supports `sort`, `filter[key]=value`, `page`, `pageSize`. The recordListApi and useRecordList layers were left untouched.
- **Pre-existing baseline failures**: 2 tests in `src/features/admin/menus/__tests__/ReorderableMenuList.test.tsx` fail with `reorderMutation.mutate is not a function`. Verified against the pre-story baseline by stashing Story-6.10 changes and re-running just that test file — same 2 failures. Not introduced by this story; surfaced here as visibility for the next code-review pass on Story 4.5.

### File List

| Action | Path |
|--------|------|
| MODIFY | `web/src/routes/_app/data.$designerId.tsx` |
| CREATE | `web/src/features/data-entry/useDesignerFieldKeys.ts` |
| MODIFY | `web/src/components/dataEntry/RecordListPage.tsx` |
| MODIFY | `web/src/routes/_app/-data.$designerId.test.tsx` |
| MODIFY | `web/src/lib/i18n/locales/en.json` |
| MODIFY | `_bmad-output/implementation-artifacts/sprint-status.yaml` |

### Review Findings

_Code review 2026-05-26 — Opus 4.7. Three parallel layers (Blind Hunter / Edge Case Hunter / Acceptance Auditor). 1 decision-needed, 8 patch, 10 deferred, ~11 dismissed._

- [x] [Review][Patch] System column sort key mismatch — frontend now sends `created_at` / `updated_at` for system sort headers so the backend `SystemSortPgColumns` whitelist accepts them. Cell data still uses camelCase `row.createdAt` / `row.updatedAt` per AR-46. Spec §6 documentation drift remains as follow-up. [RecordListPage.tsx system header `<th>` block]

- [x] [Review][Patch] Added `aria-sort="ascending|descending|none"` on each sortable `<th>` cell via new `ariaSortFor(colKey, sortEntries)` helper. [RecordListPage.tsx ariaSortFor + th attrs]
- [x] [Review][Patch] Multi-sort priority badge now carries `aria-label={t('data.entry.sortPriority', { n })}` via new `ariaPriorityLabelFor` prop. New i18n key `data.entry.sortPriority: "Sort priority {{n}}"`. [RecordListPage.tsx SortableHeader + en.json]
- [x] [Review][Patch] Filter input `aria-label` now uses parameterised key `data.entry.filterAriaLabel: "Filter {{column}}"` via new `ariaFilterLabelFor` prop, replacing the concatenated `"Filter… title"` string. [RecordListPage.tsx FilterRow + en.json]
- [x] [Review][Patch] `filterRowKey` sorts keys alphabetically before `JSON.stringify` so router-driven key reordering does not spuriously remount FilterRow and wipe the user's in-flight buffer. [RecordListPage.tsx filterRowKey]
- [x] [Review][Patch] `pageSize` Zod schema now `refine`s to the picker values 10/25/50 with `.catch(25)` graceful fallback for hostile URLs. [data.$designerId.tsx dataSearchSchema]
- [x] [Review][Patch] `extractFieldKeys` extracted into a thin `collectBindTos` walker + `Array.from(new Set(...))` dedupe; two schema elements sharing a `bindTo` no longer produce duplicate column headers / colliding React keys. [useDesignerFieldKeys.ts collectBindTos + extractFieldKeys]
- [x] [Review][Patch] Unused i18n key `clearSort` removed. [en.json]
- [x] [Review][Patch] Added 4 new tests: Shift-click multi-sort (`title:asc,status:asc`), priority badge aria-labels (`Sort priority 1` / `Sort priority 2`), `aria-sort="ascending"` on active sort column, snake_case sort keys for system columns (`created_at:asc`). Suite is 20/20 green; typecheck clean; lint clean. [-data.$designerId.test.tsx]

- [x] [Review][Defer] `parseSortString` silently drops malformed tokens with no UI feedback — backend rejects with helpful message, deferred to UX polish [RecordListPage.tsx:47-57]
- [x] [Review][Defer] Frontend doesn't clamp to `MaxSortPairs=3` — backend enforces, deferred [RecordListPage.tsx:64-85]
- [x] [Review][Defer] `formatCell` renders `'[object Object]'` for nested JSON / array column values [RecordListPage.tsx:88-90]
- [x] [Review][Defer] "Page N of N" / "Page 1 of 0" edge cases — `totalPages` fallback not clamped to ≥1 (Story 6.9 carryover) [RecordListPage.tsx:275]
- [x] [Review][Defer] `z.record(z.string())` permits hostile filter keys (e.g. `__proto__`, empty string) — backend whitelist catches them [data.$designerId.tsx:9]
- [x] [Review][Defer] `totalRecords` lacks `Intl.NumberFormat` for large counts — i18n polish [RecordListPage.tsx:438]
- [x] [Review][Defer] No `aria-live` announcement for soft-deleted row state — Story 6.11 polish [RecordListPage.tsx:402-406]
- [x] [Review][Defer] Keyboard users cannot trigger multi-sort (Shift+Enter not routed through `nextSortForClick`) — a11y story [RecordListPage.tsx:131-147]
- [x] [Review][Defer] No test for `schema.isLoading` / `isError` fallback branch [-data.$designerId.test.tsx]
- [x] [Review][Defer] No test for silent-on-mount filter equality skip [-data.$designerId.test.tsx]

### Change Log

| Date | Author | Change |
|------|--------|--------|
| 2026-05-26 | create-story | Story 6.10 created. |
| 2026-05-26 | dev-story (Opus 4.7) | Implementation complete; status → review. |
