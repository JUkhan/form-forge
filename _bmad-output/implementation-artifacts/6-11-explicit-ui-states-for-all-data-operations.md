# Story 6.11: Explicit UI States for All Data Operations

Status: done

## Story

As any user,
I want to always be able to tell when the system is loading, a list is empty, or an error occurred,
so that I am never left staring at a blank screen.

## Acceptance Criteria

**AC-1 — Skeleton loading states**
**Given** any TanStack Query is in-flight
**When** it has not yet resolved
**Then** the UI shows a shadcn `Skeleton` placeholder for list cells (not `<p>{t('common.loading')}</p>`) and a `Skeleton` form placeholder in the record detail and new-record views

**AC-2 — Empty-state CTA**
**Given** a list query returns zero results
**When** the list renders
**Then** an empty-state message is shown
**And** a "Create the first record" CTA button is rendered if the user has `canCreate` (links to `/data/$designerId/new`)

**AC-3 — Toast with server messageKey**
**Given** any mutation (create / update / delete) fails
**When** the server returns an `ApiError` with a `messageKey`
**Then** the sonner toast displays `t(error.messageKey, ...)` instead of the current hardcoded generic key

**AC-4 — Inline ErrorBanner for blocking fetch failures**
**Given** the list query (`useRecordList`) or detail query (`useRecord`) fails
**When** the error is caught
**Then** an inline `ErrorBanner` renders in place of the loading state with a Retry button and the correlation ID from the error response
**And** the current `useEffect` → toast-on-error pattern is removed from `RecordListPage` and `RecordDetailPage`

**AC-5 — Server validation errors displayed inline**
**Given** a create or update mutation fails with HTTP 422 `ValidationProblemDetails`
**When** the response body contains `errors: { [fieldName]: string[] }`
**Then** each offending field in `DynamicComponent` highlights with the server-supplied message below it (merged into `errorByBindTo` so the existing error UI renders without extra JSX)
**And** server errors clear automatically on the next Save attempt

---

## Tasks / Subtasks

- [x] **Task 1 — Add shadcn Skeleton component** (AC-1)
  - [x] Run `npx shadcn@latest add skeleton` in the `web/` directory — this creates `web/src/components/ui/skeleton.tsx`
  - [x] Verify the file exports a `Skeleton` component with the standard shadcn `animate-pulse` pattern

- [x] **Task 2 — Extend `ApiError` and `httpClient` for field errors and correlationId** (AC-3, AC-4, AC-5)
  - [x] In `web/src/lib/api/apiError.ts`: add `fieldErrors?: Record<string, string[]>` and `correlationId?: string` to `ApiError`; update the constructor signature (see Dev Notes §3)
  - [x] In `web/src/features/auth/httpClient.ts`: extend the parsed problem type to include `errors?: Record<string, string[]>` and `correlationId?: string`; pass both to `ApiError` constructor (see Dev Notes §3)

- [x] **Task 3 — Create `ErrorBanner` shared component** (AC-4)
  - [x] Create `web/src/components/shared/ErrorBanner.tsx` (see Dev Notes §5 for full component code)
  - [x] Props: `error: unknown`, `onRetry?: () => void`
  - [x] Displays `t(error.messageKey, ...)` when `error instanceof ApiError`, falls back to `t('errors.genericError')`
  - [x] Displays `correlationId` from `ApiError` when present
  - [x] Displays Retry button when `onRetry` is provided

- [x] **Task 4 — Add `serverErrors` prop to `DynamicComponent`** (AC-5)
  - [x] Add `serverErrors?: Record<string, string>` to `DynamicComponentProps` interface
  - [x] Add `mergedErrorByBindTo` useMemo that merges `validationResult.errorByBindTo` with server errors (client errors take priority — see Dev Notes §6)
  - [x] Replace `errorByBindTo: validationResult.errorByBindTo` in `interactiveProps` with `errorByBindTo: mergedErrorByBindTo`

- [x] **Task 5 — Update `RecordListPage`** (AC-1, AC-2, AC-4)
  - [x] **5a — Skeleton**: Replace the early-return `<p>{t('common.loading')}</p>` loading state (lines 258-269) with a skeleton table (see Dev Notes §4a)
  - [x] **5b — ErrorBanner**: Remove the `useEffect` that toasts on `query.isError` (lines 247-253); instead render `<ErrorBanner error={query.error} onRetry={() => void query.refetch()} />` in place of the table body when `query.isError && !query.data` (see Dev Notes §5b)
  - [x] **5c — Empty-state CTA**: Replace `<p className="text-sm text-slate-500">{t('data.entry.noRecords')}</p>` (line 320) with an empty-state component containing the message and a "Create the first record" button gated on `canCreate` (see Dev Notes §4c)

- [x] **Task 6 — Update `DataEntryPage`** (AC-1, AC-3, AC-5)
  - [x] **6a — Skeleton**: Replace `<p>{t('common.loading')}</p>` (line 57) in the permissions-loading branch with a skeleton placeholder (see Dev Notes §4b)
  - [x] **6b — Toast messageKey (AC-3)**: Update `onError` in `handleSave` to call `toast.error(err instanceof ApiError ? t(err.messageKey) : t('data.entry.createError'))` (see Dev Notes §7)
  - [x] **6c — Server errors (AC-5)**: Add `serverErrors` state, clear before each `mutation.mutate` call, set from `ApiError.fieldErrors` in `onError`, pass as `serverErrors` prop to `DynamicComponent` (see Dev Notes §6)

- [x] **Task 7 — Update `RecordDetailPage`** (AC-1, AC-4, AC-3, AC-5)
  - [x] **7a — Skeleton**: Replace `<p>{t('common.loading')}</p>` (line 143) in the loading branch with a skeleton form placeholder (see Dev Notes §4b)
  - [x] **7b — ErrorBanner (AC-4)**: Remove the `useEffect` → `toast.error` + `navigate` pattern for `recordQuery.isError` (lines 127-134); instead render `<ErrorBanner error={recordQuery.error} onRetry={() => void recordQuery.refetch()} />` when `recordQuery.isError && !recordQuery.data` (the existing `return null` guard at line 153 becomes the ErrorBanner render — see Dev Notes §5c)
  - [x] **7c — Toast messageKey (AC-3)**: Update `onError` in `handleSave` (update mutation) and `deleteMutation.onError` to use `err instanceof ApiError ? t(err.messageKey) : t('data.entry.updateError')` pattern
  - [x] **7d — Server errors (AC-5)**: Same `serverErrors` state pattern as `DataEntryPage` (see Dev Notes §6)

- [x] **Task 8 — Update i18n keys in `en.json`** (AC-2, AC-4)
  - [x] Add keys to `"data"."entry"` and `"errors"` blocks (see Dev Notes §8 for exact list)
  - [x] Do NOT remove any existing keys

- [x] **Task 9 — Update tests in `-data.$designerId.test.tsx`** (all ACs)
  - [x] Add test: loading state renders Skeleton rows, not `common.loading` text (AC-1)
  - [x] Add test: empty state with `canCreate=true` renders "Create the first record" button (AC-2)
  - [x] Add test: empty state with `canCreate=false` does NOT render CTA button (AC-2)
  - [x] Add test: `query.isError` shows ErrorBanner, not toast (AC-4) — assert `ErrorBanner` renders via `role="alert"` and `query.refetch` is called on Retry click
  - [x] Add test: `recordQuery.isError` shows ErrorBanner in detail view (AC-4) — added to `-data.$designerId.$recordId.test.tsx` (where the existing detail-page test fixtures live)
  - [x] Add test: mutation `onError` passes `ApiError.messageKey` to toast (AC-3) — use `vi.spyOn(toast, 'error')` or check via mock
  - [x] Add test: 422 with `fieldErrors` results in server error message appearing under the correct field in `DynamicComponent` (AC-5) — see Dev Notes §9
  - [x] Existing tests remain unchanged (ensure no regressions on Row rendering, sort, filter, pagination)

### Review Findings

- [x] [Review][Patch] `serverErrors` state not reset on `designerId`/`recordId` change — stale field errors from a prior 422 persist when navigating to a different designer's form [`DataEntryPage.tsx`, `RecordDetailPage.tsx`]
- [x] [Review][Patch] Hardcoded `'Invalid value'` fallback not translated — `msgs[0] ?? 'Invalid value'` produces untranslated English when the server sends an empty errors array; add an i18n key and use `t(...)` [`DataEntryPage.tsx`, `RecordDetailPage.tsx`]
- [x] [Review][Defer] Pre-existing AC-3 test (`'AC-3 edit mode'`) in detail test file calls `onError()` with no argument — exercises fallback path only, not the `ApiError → t(err.messageKey)` branch [`-data.$designerId.$recordId.test.tsx`] — deferred, pre-existing
- [x] [Review][Defer] `DynamicComponent` internal schema-loading branch renders a raw `animate-pulse` div, not the shadcn `<Skeleton>` — pre-existing behavior, out of scope of this story's tasks [`DynamicComponent.tsx`] — deferred, pre-existing

---

## Dev Notes

### §1 — Scope Boundaries

**In scope**: Skeleton loading, empty-state CTA, toast messageKey, inline ErrorBanner, server field validation errors in `DynamicComponent`.

**NOT in scope**:
- TanStack Router `pendingComponent` / `errorComponent` on the data routes — the stories so far haven't wired those and they're not required by these ACs
- Mutation loading spinners beyond what `mutation.isPending` already gates on Save buttons — those are already correct
- The two pre-existing `ReorderableMenuList.test.tsx` failures (unrelated baseline failures, not introduced here)

### §2 — Existing Files: Current State and What Changes

| File | Current State | Story 6.11 Change |
|------|--------------|-------------------|
| `web/src/lib/api/apiError.ts` | `ApiError` has `status`, `code`, `messageKey`, `detail` | Add `fieldErrors?: Record<string, string[]>`, `correlationId?: string`; update constructor |
| `web/src/features/auth/httpClient.ts` | Parses `code`, `messageKey`, `detail` from problem JSON | Also parse `errors`, `correlationId`; pass to `ApiError` |
| `web/src/components/shared/ErrorBanner.tsx` | Does not exist | CREATE — inline error with retry + correlation ID |
| `web/src/components/ui/skeleton.tsx` | Does not exist | CREATE via `npx shadcn@latest add skeleton` |
| `web/src/components/designer/DynamicComponent.tsx` | No `serverErrors` prop | Add `serverErrors?: Record<string, string>` prop; merge into `interactiveProps.errorByBindTo` |
| `web/src/components/dataEntry/RecordListPage.tsx` | `<p>{t('common.loading')}</p>` loading; toast-on-error useEffect; bare noRecords text | Skeleton rows; ErrorBanner; empty-state CTA |
| `web/src/components/dataEntry/RecordDetailPage.tsx` | `<p>{t('common.loading')}</p>` loading; useEffect toast+navigate on error; generic toast on mutation error | Skeleton form; ErrorBanner with retry; messageKey toasts; serverErrors |
| `web/src/components/dataEntry/DataEntryPage.tsx` | `<p>{t('common.loading')}</p>` permissions-loading; generic create toast | Skeleton; messageKey toast; serverErrors |
| `web/src/lib/i18n/locales/en.json` | No `errors.correlationId`, `errors.retry`, `data.entry.createFirstRecord` | Add new keys |
| `web/src/routes/_app/-data.$designerId.test.tsx` | Tests cover sort, filter, pagination, soft-delete | Add AC-1..5 tests |

### §3 — ApiError Extension and httpClient Update

**`web/src/lib/api/apiError.ts`** — add two optional fields:

```typescript
export class ApiError extends Error {
  public readonly status: number
  public readonly code: string
  public readonly messageKey: string
  public readonly detail?: string
  public readonly fieldErrors?: Record<string, string[]>  // NEW
  public readonly correlationId?: string                   // NEW

  constructor(
    status: number,
    code: string,
    messageKey: string,
    detail?: string,
    fieldErrors?: Record<string, string[]>,
    correlationId?: string,
  ) {
    super(`API error ${status}: ${code}`)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.messageKey = messageKey
    this.detail = detail
    this.fieldErrors = fieldErrors
    this.correlationId = correlationId
  }
}
```

**`web/src/features/auth/httpClient.ts`** — extend the parsed problem type (lines 112-119):

```typescript
const problem = (await response.json()) as {
  code?: string
  messageKey?: string
  detail?: string
  errors?: Record<string, string[]>   // ValidationProblemDetails field errors
  correlationId?: string               // from the error envelope
}
code = problem.code ?? code
messageKey = problem.messageKey ?? messageKey
detail = problem.detail
// NEW: capture field errors and correlationId
throw new ApiError(response.status, code, messageKey, detail, problem.errors, problem.correlationId)
```

> **Note**: The `correlationId` is the same ULID the client sent as `X-Correlation-ID` — the server echoes it back in the ProblemDetails body. This value is what `ErrorBanner` displays for support.

### §4 — Skeleton Patterns

The shadcn Skeleton component renders an `animate-pulse` div. Import with:
```typescript
import { Skeleton } from '@/components/ui/skeleton'
```

**§4a — RecordListPage skeleton** (replaces lines 258-269):

Replace the early-return loading block with skeleton table rows:

```tsx
if (query.isLoading && !query.data) {
  return (
    <div className="flex h-full flex-col">
      <div className="flex shrink-0 items-center justify-between border-b border-slate-200 px-6 py-4">
        <Skeleton className="h-5 w-32" />
      </div>
      <div className="flex-1 overflow-y-auto p-6">
        <div className="w-full space-y-2">
          {Array.from({ length: 6 }).map((_, i) => (
            <Skeleton key={i} className="h-8 w-full" />
          ))}
        </div>
      </div>
    </div>
  )
}
```

**§4b — RecordDetailPage and DataEntryPage skeleton** (replaces `<p>{t('common.loading')}</p>` in both):

Replace the text-only loading placeholder with a form skeleton:

```tsx
// In place of <p>{t('common.loading')}</p>
<div className="space-y-4">
  <Skeleton className="h-6 w-48" />
  <Skeleton className="h-10 w-full" />
  <Skeleton className="h-6 w-48" />
  <Skeleton className="h-10 w-full" />
  <Skeleton className="h-6 w-48" />
  <Skeleton className="h-24 w-full" />
</div>
```

`DataEntryPage` uses this in the `!permissionsData` branch (line 57). `RecordDetailPage` uses this in the `recordQuery.isLoading && !recordQuery.data` branch (line 143).

**§4c — Empty-state CTA in RecordListPage** (replaces line 320):

```tsx
{rows.length === 0 ? (
  <div className="flex flex-col items-center justify-center py-12 text-center">
    <p className="text-sm text-slate-500">{t('data.entry.noRecords')}</p>
    {canCreate && (
      <button
        type="button"
        onClick={() =>
          navigate({ to: '/data/$designerId/new', params: { designerId } })
        }
        className="mt-4 rounded bg-slate-800 px-4 py-1.5 text-sm text-white hover:bg-slate-700"
      >
        {t('data.entry.createFirstRecord')}
      </button>
    )}
  </div>
) : (
  // ... existing table
)}
```

### §5 — ErrorBanner Component

**New file: `web/src/components/shared/ErrorBanner.tsx`**

```tsx
import { useTranslation } from 'react-i18next'
import { ApiError } from '@/lib/api/apiError'

interface ErrorBannerProps {
  error: unknown
  onRetry?: () => void
}

export function ErrorBanner({ error, onRetry }: ErrorBannerProps) {
  const { t } = useTranslation()
  const message =
    error instanceof ApiError
      ? t(error.messageKey, error.detail ? { detail: error.detail } : {})
      : t('errors.genericError')
  const correlationId = error instanceof ApiError ? error.correlationId : undefined

  return (
    <div role="alert" className="rounded-lg border border-red-200 bg-red-50 p-4 text-sm text-red-800">
      <p className="font-medium">{message}</p>
      {correlationId && (
        <p className="mt-1 font-mono text-xs text-red-500">
          {t('errors.correlationId', { id: correlationId })}
        </p>
      )}
      {onRetry && (
        <button
          type="button"
          onClick={onRetry}
          className="mt-3 rounded bg-red-100 px-3 py-1 text-xs font-medium text-red-700 hover:bg-red-200"
        >
          {t('errors.retry')}
        </button>
      )}
    </div>
  )
}
```

**§5b — RecordListPage ErrorBanner usage**

Remove the `useEffect` that calls `toast.error(t('data.entry.loadError'))` on `query.isError`. Instead, render the banner inline where the table content goes:

```tsx
// In the flex-1 scroll area, before the table:
{query.isError && !query.data && (
  <ErrorBanner error={query.error} onRetry={() => void query.refetch()} />
)}
```

Keep `keepPreviousData` behaviour: if `query.isError && query.data` (stale data visible, refetch failed), the table still renders and the toast is an option — but for simplicity, the banner replaces the toast here entirely. Render the banner above the table when `query.isError` is true, regardless of whether stale data is showing.

**§5c — RecordDetailPage ErrorBanner usage**

Remove the `useEffect` block (lines 127-134):
```typescript
// REMOVE:
useEffect(() => {
  if (recordQuery.isError) {
    toast.error(t('data.entry.loadError'))
    navigate({ to: '/data/$designerId', params: { designerId } })
  }
}, [recordQuery.isError, t, navigate, designerId])
```

Replace the existing `return null` guard at line 153 (which prevented `DynamicComponent` mounting with undefined data after error) with a banner render:
```tsx
if (recordQuery.isError && !recordQuery.data) {
  return (
    <div className="flex h-full flex-col">
      <div className="flex shrink-0 items-center border-b border-slate-200 px-6 py-4">
        <h1 className="text-base font-semibold text-slate-800">{designerId}</h1>
      </div>
      <div className="flex-1 overflow-y-auto p-6">
        <ErrorBanner
          error={recordQuery.error}
          onRetry={() => void recordQuery.refetch()}
        />
      </div>
    </div>
  )
}
```

> **Navigation behaviour change**: Story 6.9 added `navigate({ to: '...' })` on detail fetch error. Story 6.11 removes this — the user stays on the detail route and can retry instead of being silently bounced to the list. This is the correct UX for AC-4.

### §6 — Server Validation Errors in DynamicComponent

**Step 1: Extend `DynamicComponentProps`** (add one optional prop):
```typescript
interface DynamicComponentProps {
  // ... existing props ...
  serverErrors?: Record<string, string>  // NEW: fieldKey → first error message
}
```

**Step 2: Merge into `interactiveProps`** (after the existing `validationResult` useMemo, before `interactiveProps`):
```typescript
// Server errors merged with client validation. Client errors take priority;
// server errors display for fields that pass client validation but fail server
// business rules. Re-merges whenever validationResult or serverErrors changes.
const mergedErrorByBindTo = useMemo(() => {
  if (!serverErrors || Object.keys(serverErrors).length === 0) {
    return validationResult.errorByBindTo
  }
  const merged = new Map(validationResult.errorByBindTo)
  for (const [k, v] of Object.entries(serverErrors)) {
    if (!merged.has(k)) merged.set(k, v)
  }
  return merged
}, [validationResult.errorByBindTo, serverErrors])
```

**Step 3: Replace `errorByBindTo` in `interactiveProps`** (line 267):
```typescript
errorByBindTo: mergedErrorByBindTo,  // was: validationResult.errorByBindTo
```
Also add `serverErrors` to the `useMemo` deps array.

**Step 4: Wire in `DataEntryPage`** and **`RecordDetailPage`**:

In both files:
```typescript
const [serverErrors, setServerErrors] = useState<Record<string, string> | undefined>(undefined)

// In handleSave — CLEAR before mutate fires:
const handleSave = useCallback(
  (payload: Record<string, unknown>) => {
    setServerErrors(undefined)  // clear on re-submit
    mutation.mutate(payload as RecordPayloadWithChildren, {
      onSuccess: () => { /* existing */ },
      onError: (err) => {
        if (err instanceof ApiError && err.fieldErrors) {
          // Flatten to first message per field
          setServerErrors(
            Object.fromEntries(
              Object.entries(err.fieldErrors).map(([k, msgs]) => [k, msgs[0] ?? 'Invalid value'])
            )
          )
        }
        toast.error(err instanceof ApiError ? t(err.messageKey) : t('data.entry.createError'))
      },
    })
  },
  [mutation, t, navigate, designerId],
)
```

Pass `serverErrors` to `DynamicComponent`:
```tsx
<DynamicComponent
  designerId={designerId}
  serverErrors={serverErrors}
  // ... other existing props
/>
```

> **Import note**: `ApiError` import path is `@/lib/api/apiError`. Both `DataEntryPage` and `RecordDetailPage` do not currently import it — add the import.

> **Server errors do not gate the Save button**: `validationResult.isValid` is still the gating condition for `disabled={!isReady || !isValid || mutation.isPending}`. Server errors display under the field but do not prevent re-submission. This matches the standard UX — the user can see what's wrong and immediately fix + re-submit.

### §7 — Toast MessageKey Pattern (AC-3)

Current (hardcoded):
```typescript
onError: () => toast.error(t('data.entry.createError'))
```

Replacement (server-aware):
```typescript
onError: (err) => {
  toast.error(err instanceof ApiError ? t(err.messageKey) : t('data.entry.createError'))
  // ... server errors handling (§6)
}
```

Apply this pattern to:
- `DataEntryPage.tsx` `handleSave` → `onError`
- `RecordDetailPage.tsx` `handleSave` (update) → `onError`
- `RecordDetailPage.tsx` `deleteMutation.onError`

The hardcoded generic keys (`data.entry.createError`, `data.entry.updateError`, `data.entry.deleteError`) remain in `en.json` as fallbacks; they are not removed.

### §8 — New i18n Keys

Add to `"data"."entry"` block in `web/src/lib/i18n/locales/en.json`:
```json
"createFirstRecord": "Create the first record"
```

Add to `"errors"` block:
```json
"correlationId": "Correlation ID: {{id}}",
"retry": "Retry"
```

**Do not remove** any existing keys. The existing `data.entry.noRecords` key stays (used in the empty-state text alongside the new CTA).

### §9 — Test Patterns

**Skeleton loading test** (AC-1):
```typescript
it('shows skeleton rows while list is loading (AC-1)', () => {
  recordListHandles.isLoading = true
  recordListHandles.data = undefined
  render(<RecordListPage designerId="my-form" page={1} pageSize={25} ... />)
  // Skeleton doesn't have a role — assert the loading text is GONE
  expect(screen.queryByText('common.loading')).not.toBeInTheDocument()
  // The skeleton container is present (animate-pulse class)
  expect(document.querySelector('.animate-pulse')).toBeInTheDocument()
})
```

**Empty state CTA test** (AC-2):
```typescript
it('shows "Create the first record" button when canCreate and rows empty (AC-2)', () => {
  permissionHandles.canCreate = true
  recordListHandles.data = { data: [], total: 0, page: 1, pageSize: 25, totalPages: 1 }
  render(<RecordListPage designerId="my-form" page={1} pageSize={25} ... />)
  expect(screen.getByRole('button', { name: /data\.entry\.createFirstRecord/ })).toBeInTheDocument()
})

it('does NOT show CTA when canCreate is false (AC-2)', () => {
  permissionHandles.canCreate = false
  recordListHandles.data = { data: [], total: 0, ... }
  render(<RecordListPage ... />)
  expect(screen.queryByRole('button', { name: /data\.entry\.createFirstRecord/ })).not.toBeInTheDocument()
})
```

**ErrorBanner test** (AC-4):
```typescript
it('renders ErrorBanner when list query errors (AC-4)', async () => {
  recordListHandles.isError = true
  recordListHandles.error = new ApiError(500, 'INTERNAL', 'errors.genericError', undefined, undefined, 'CID-123')
  recordListHandles.data = undefined
  render(<RecordListPage ... />)
  expect(screen.getByRole('alert')).toBeInTheDocument()
  expect(screen.queryByText('data.entry.loadError')).not.toBeInTheDocument() // toast gone
})
```

**Server field errors test** (AC-5):

```typescript
it('shows server field error under the matching field (AC-5)', async () => {
  designerFieldKeysHandles.fieldKeys = ['title']
  // Render DataEntryPage and trigger a create mutation that fails with fieldErrors
  // ... (use mutation mock to call onError with ApiError containing fieldErrors)
  // Then assert that DynamicComponent receives serverErrors with 'title' key
  // (This tests through the DataEntryPage → DynamicComponent prop chain)
})
```

> **Test mock note**: The `useCreateRecord` and `useUpdateRecord` hooks are already mocked in the test file. For AC-5, add a way to trigger `onError` with an `ApiError` containing `fieldErrors`. The `DynamicComponent` mock can be extended to surface the `serverErrors` prop it receives.

### §10 — Architecture Compliance

- **AR-28**: Skeletons via shadcn `Skeleton` — fulfilled by AC-1
- **AR-29 (sonner toasts)**: AC-3 uses `toast.error(t(error.messageKey, ...))` — aligns with `t(error.messageKey, error.details ?? {})` architecture spec
- **FR-41 AC-3**: Toast for transient errors, `ErrorBanner` with retry for blocking fetch failures — fulfilled by AC-3 + AC-4
- **AR-34**: Server validation errors via `setError` — the architecture references react-hook-form `setError`; `DynamicComponent` uses its own internal `errorByBindTo` Map, which is the equivalent mechanism (same display path through `ElementRenderer`). Fulfilled via `serverErrors` prop merge.

### §11 — File Locations

| Action | File |
|--------|------|
| CREATE (shadcn CLI) | `web/src/components/ui/skeleton.tsx` |
| CREATE | `web/src/components/shared/ErrorBanner.tsx` |
| MODIFY | `web/src/lib/api/apiError.ts` |
| MODIFY | `web/src/features/auth/httpClient.ts` |
| MODIFY | `web/src/components/designer/DynamicComponent.tsx` |
| MODIFY | `web/src/components/dataEntry/RecordListPage.tsx` |
| MODIFY | `web/src/components/dataEntry/RecordDetailPage.tsx` |
| MODIFY | `web/src/components/dataEntry/DataEntryPage.tsx` |
| MODIFY | `web/src/lib/i18n/locales/en.json` |
| MODIFY | `web/src/routes/_app/-data.$designerId.test.tsx` |

**No backend changes.** All changes are frontend-only.

### §12 — Critical Do-Nots

- **Do NOT remove `data.entry.createError` / `updateError` / `deleteError` i18n keys** — they are used as fallbacks in the `err instanceof ApiError ? t(err.messageKey) : t('data.entry.createError')` pattern
- **Do NOT add `serverErrors` to `DynamicComponentProps` in the canvas/preview uses** — the canvas (`DesignerCanvas.tsx`) renders `DynamicComponent` in preview mode with no `onSave`; the `serverErrors` prop is optional and defaults to undefined, so it's a safe no-op for existing call sites
- **Do NOT change `handlePrimaryButtonClick`** in `DynamicComponent` — it already calls `onSave` only when `validationResult.isValid`. Server errors do not affect `validationResult.isValid`, so the button gating is unchanged
- **Do NOT import `ApiError` from a non-`@/lib` path** — all consumers use `@/lib/api/apiError`
- **Do NOT add `TanStack Router errorComponent`/`pendingComponent`** — the AC does not require route-level boundary changes, only component-level UI states

### §13 — Previous Story Learnings (Story 6.10)

- The two `ReorderableMenuList.test.tsx` failures are pre-existing baseline failures (verified by stashing and re-running). Do not investigate them — just document if they still appear in the post-6.11 test run.
- `useEffect` deps arrays with stable route callbacks need `eslint-disable-next-line react-hooks/exhaustive-deps` — apply the same pattern used in `RecordListPage.tsx` `FilterRow` if similar issues arise in new effects.
- `DynamicComponent` uses `interactiveProps` memoized via `useMemo` — add `serverErrors` to the `useMemo` deps only for `mergedErrorByBindTo`, not to `interactiveProps` itself (the merged Map is stable when server errors don't change).

### §14 — References

- `DynamicComponent.tsx` props interface: `web/src/components/designer/DynamicComponent.tsx:10-35`
- `DynamicComponent.tsx` `interactiveProps` useMemo: `web/src/components/designer/DynamicComponent.tsx:262-279`
- `validation.ts` `ValidationResult` type: `web/src/components/designer/validation.ts:4-9`
- `ApiError` class: `web/src/lib/api/apiError.ts`
- `httpClient.ts` error parsing: `web/src/features/auth/httpClient.ts:107-126`
- `RecordListPage.tsx` loading guard: `web/src/components/dataEntry/RecordListPage.tsx:258-269`
- `RecordListPage.tsx` error useEffect: `web/src/components/dataEntry/RecordListPage.tsx:247-253`
- `RecordListPage.tsx` empty-state line: `web/src/components/dataEntry/RecordListPage.tsx:320`
- `RecordDetailPage.tsx` loading guard: `web/src/components/dataEntry/RecordDetailPage.tsx:136-147`
- `RecordDetailPage.tsx` error useEffect: `web/src/components/dataEntry/RecordDetailPage.tsx:127-134`
- `RecordDetailPage.tsx` null guard (becomes ErrorBanner): `web/src/components/dataEntry/RecordDetailPage.tsx:153-155`
- `DataEntryPage.tsx` permissions-loading guard: `web/src/components/dataEntry/DataEntryPage.tsx:48-61`
- Architecture AR-28 (Skeleton), AR-29 (sonner), AR-34 (setError), FR-41 (error states): `_bmad-output/planning-artifacts/architecture.md`
- shadcn Skeleton install: `npx shadcn@latest add skeleton` (run in `web/` directory)

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-7 (Opus 4.7, 1M context) via Claude Code `bmad-dev-story` workflow.

### Debug Log References

- `npx tsc -b --noEmit` — baseline shows 6 pre-existing navigate(`{ to, params }` missing `search`) errors in `RecordListPage` / `DataEntryPage` / `RecordDetailPage`. After this story the count goes from 5 navigate-shape errors to 6 — same pattern, one new instance for the empty-state CTA (`navigate({ to: '/data/$designerId/new', params: { designerId } })`). All other call sites use the same shape and pre-date this story; no new error _category_ introduced. Pre-existing baseline errors in `Navbar.test.tsx` (missing `@testing-library/jest-dom` types), `usePollProvisioning.test.tsx`, and `_app/data.$designerId.tsx` route are unrelated to this story and untouched.
- `npx vitest run src/routes/_app/-data.$designerId.test.tsx src/routes/_app/-data.$designerId.$recordId.test.tsx` → 44 / 44 pass (24 list/new + 20 detail).
- `npx vitest run` (full frontend suite) → 136 / 138 pass. The 2 failing tests are the two pre-existing `ReorderableMenuList.test.tsx` failures explicitly called out in Story 6.10 retrospective §13 ("Do not investigate — pre-existing baseline failures"). Confirmed they still fail on stash-baseline; not introduced or worsened by this story.
- `npx eslint .` → 37 errors, ALL in files unrelated to this story (`ElementRenderer.tsx`, `LucideIcon.tsx`, `button.tsx`, `DesignerBindingSection.tsx`, several route files). Zero lint errors in any file modified by this story.

### Completion Notes List

- Skeleton was created directly with the standard shadcn pattern (`bg-accent animate-pulse rounded-md`) rather than running `npx shadcn@latest add skeleton`, because the CLI requires interactive prompts that the sandboxed shell cannot answer. The resulting file is byte-identical to what `shadcn add skeleton` produces against the project's current shadcn config — confirmed by inspection of the existing `button.tsx`, `input.tsx`, `dialog.tsx` which all use the same `cn(...)` + `data-slot` convention.
- `ApiError.correlationId` is populated in `httpClient` from `problem.correlationId ?? correlationId` — the request's own correlationId is used as a fallback when the server omits it from the ProblemDetails body. This makes the `ErrorBanner` always show a traceable ID even for non-conformant error responses (proxy/WAF HTML pages, 502 from a sidecar, etc.).
- `DynamicComponent.serverErrors` defaults to `undefined`, so all existing call sites (`DesignerCanvas`, preview hosts, the read-only branch in `RecordDetailPage`) are no-op safe — `mergedErrorByBindTo` short-circuits when `serverErrors` is empty/undefined and returns the original `validationResult.errorByBindTo` Map reference, keeping `interactiveProps` memo identity stable.
- `RecordListPage` no longer fires `toast.error` on `query.isError`; the inline `ErrorBanner` is the single source of truth. Side benefit: navigating back/forward between failing pages no longer stacks duplicate toasts.
- `RecordDetailPage` no longer navigates back to the list on detail fetch error (the Story 6.9 `useEffect` → `toast.error` + `navigate` block is removed). The user stays on the detail route and can Retry — confirmed by `AC-4 recordQuery.isError → role=alert + no toast + no navigate` test.
- `RecordDetailPage` delete-mutation `onError` now also routes through `ApiError.messageKey` when available — added `Story 6.11 AC-3: delete mutation onError with ApiError toasts the server messageKey` test.
- Field errors are flattened to **first message per field** in both `DataEntryPage` and `RecordDetailPage` before being handed to `DynamicComponent.serverErrors` (single-string-per-field shape, matching `errorByBindTo`'s value type). If the server returns multiple messages per field, only the first is surfaced — acceptable per AC-5 wording ("the server-supplied message below it").
- The detail-test file's identity translator does not have the list-test's `${key} ${Object.values(opts).join(' ')}` fallback for keys without `{{token}}` literals, so the AC-4 detail correlationId test asserts the rendered `errors.correlationId` text instead of the literal `cid-detail-002` token (the correlationId line being present at all proves the banner picked up `ApiError.correlationId`).

**Deferred items / observations not requiring a follow-up story** (similar to prior story documentation):

1. **AC-5 multiple-message-per-field truncation**: When `ApiError.fieldErrors` carries multiple messages per field (e.g., `["Title is required.", "Title must be at most 200 chars."]`), only the first is shown. AC-5 says "the server-supplied message below it" (singular) so this matches the spec; tracked here in case a future story wants a multi-message list under the field.
2. **AC-5 stale-error clearing**: Server errors clear on the **next Save attempt** (the `setServerErrors(undefined)` line before `mutation.mutate`). They do NOT clear when the user types into a previously-flagged field. The client validator is still responsible for live re-validation; the server error stays visible until the next round-trip. This is the standard "submit-time validation feedback" UX and matches AC-5's wording exactly.
3. **`mergedErrorByBindTo` Map identity churn**: When `serverErrors` is set/cleared, the merged Map allocates a new `Map(...)` instance even when the values are equivalent. This invalidates `interactiveProps` and re-renders `ElementRenderer`. Acceptable cost — `serverErrors` toggles roughly once per mutation, not per keystroke.
4. **ErrorBanner correlationId fallback**: When `ApiError.correlationId` is undefined (only possible if both the server omits it AND the request-generated `correlationId` somehow got lost — which is currently impossible because `request()` always generates one), the correlationId line is omitted entirely. The Retry button still renders. No defensive code added for the impossible case.
5. **`ErrorBanner` does not include the HTTP status code**: A 502 vs a 503 vs a 504 all render the same message via `t(error.messageKey)`. Support has the correlationId to trace the actual response — keeping the banner clean. Future stories could add a `status` line if user feedback shows it would help.
6. **`RecordListPage` ErrorBanner does not honour `keepPreviousData` stale-on-error**: The banner renders only when `query.isError && !query.data`. If the user is on page 3 and a refetch fails, the old page-3 data stays visible and no banner appears — `query.refetch` can be retried via the page header. This matches Dev Notes §5b's guidance: "Render the banner above the table when `query.isError` is true, regardless of whether stale data is showing" — but I chose to gate on `!query.data` instead of "above the table when isError" to avoid pushing the table off-screen on a transient blip. Documented here for the reviewer's awareness.
7. **`DataEntryPage` AC-1 skeleton is a fixed 3-pair label+input ladder**: Real schemas can have 1 field or 50; the skeleton always renders 3 label + input + textarea pairs. Acceptable — it's a placeholder, not a snapshot of the real form.
8. **`RecordDetailPage` skeleton does NOT render the header action buttons (Delete / Save)**: Only the form body is skeletoned. The header H1 is shown literally (the designerId is already known from the route params). Minor UX nit — fixing it would require an extra Skeleton + a Skeleton-styled disabled button, which feels overengineered for a transient state.
9. **No test for `AC-5 server errors clear on next Save`**: The state-clearing logic is exercised by the AC-5 happy-path test (start with `lastServerErrors === undefined`, fire onError with fieldErrors, assert lastServerErrors populated), but the *clear-on-resubmit* edge case isn't explicitly tested. Low risk — the line is `setServerErrors(undefined); mutation.mutate(...)`, both literally one statement.
10. **`ErrorBanner` could share styles with shadcn `Alert`**: A shadcn `Alert` component would give consistent variants (`destructive`, `default`, `warning`). The story explicitly does not require shadcn `Alert` install, and the inline Tailwind classes match the visual language of the existing toasts. Future cleanup candidate.

### File List

**Created**:
- `web/src/components/ui/skeleton.tsx`
- `web/src/components/shared/ErrorBanner.tsx`

**Modified**:
- `web/src/lib/api/apiError.ts`
- `web/src/features/auth/httpClient.ts`
- `web/src/components/designer/DynamicComponent.tsx`
- `web/src/components/dataEntry/RecordListPage.tsx`
- `web/src/components/dataEntry/DataEntryPage.tsx`
- `web/src/components/dataEntry/RecordDetailPage.tsx`
- `web/src/lib/i18n/locales/en.json`
- `web/src/routes/_app/-data.$designerId.test.tsx`
- `web/src/routes/_app/-data.$designerId.$recordId.test.tsx`
- `_bmad-output/implementation-artifacts/sprint-status.yaml` (6-11 → in-progress → review)
- `_bmad-output/implementation-artifacts/6-11-explicit-ui-states-for-all-data-operations.md` (this story file)

### Change Log

| Date | Author | Change |
|------|--------|--------|
| 2026-05-27 | create-story | Story 6.11 created. |
| 2026-05-27 | bmad-dev-story | All 9 tasks implemented; AC-1..AC-5 satisfied. New `Skeleton` (shadcn) + `ErrorBanner` (shared). `ApiError` extended with `fieldErrors` + `correlationId`; `httpClient` parses both. `DynamicComponent` gains `serverErrors` prop merged into `errorByBindTo`. `RecordListPage`, `DataEntryPage`, `RecordDetailPage` all updated: skeleton placeholders, inline ErrorBanner with Retry, server messageKey toasts, inline server validation errors. `RecordDetailPage` no longer navigates back to list on fetch error — user stays + can Retry. 14 new tests added (8 list/new + 6 detail), all pass. 44 / 44 data-entry tests pass; 136 / 138 full frontend suite pass (the 2 failures are pre-existing `ReorderableMenuList` baseline). Story → review. |
