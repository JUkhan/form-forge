import { describe, it, expect, vi, beforeEach } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { useEffect } from 'react'
import type { MutableRefObject } from 'react'

// Identity translator — `t(key)` returns the key so a disabled / enabled Save
// button can be located by its label text.
vi.mock('react-i18next', () => ({
  useTranslation: () => ({
    t: (key: string, opts?: Record<string, unknown>) => {
      if (opts && typeof opts === 'object') {
        // Render i18n interpolation tokens deterministically for assertions.
        // If a key carries `{{token}}` literals, replace them; otherwise the
        // i18n value (which lives in en.json, not in the mocked t()) is the
        // one that holds the template — fall back to appending opt values so
        // the count/page numbers still surface in the DOM for assertions.
        let out = key
        let replacedAny = false
        for (const [k, v] of Object.entries(opts)) {
          const re = new RegExp(`{{\\s*${k}\\s*}}`, 'g')
          if (re.test(out)) {
            out = out.replace(new RegExp(`{{\\s*${k}\\s*}}`, 'g'), String(v))
            replacedAny = true
          }
        }
        if (!replacedAny) {
          return `${key} ${Object.values(opts).join(' ')}`
        }
        return out
      }
      return key
    },
  }),
}))

// sonner toast — silenced; the data-entry page calls toast.success on submit.
const toastSuccess = vi.fn()
const toastError = vi.fn()
vi.mock('sonner', () => ({
  toast: {
    success: (...args: unknown[]) => toastSuccess(...args),
    error: (...args: unknown[]) => toastError(...args),
  },
}))

// TanStack Router — useNavigate is called by DataEntryPage (post-create) and
// by RecordListPage (row click + New Record button). Capture the calls.
const navigateMock = vi.fn()
vi.mock('@tanstack/react-router', () => ({
  useNavigate: () => navigateMock,
}))

// Permissions query — returns a loaded stub by default so DataEntryPage's
// permissionsData guard passes. Override `data` to `undefined` in tests that
// need the loading state.
const permissionsQueryHandles = vi.hoisted(() => ({
  data: { isActive: true, roleIds: [] as string[], perResource: {} as Record<string, Record<string, boolean>> } as
    | { isActive: boolean; roleIds: string[]; perResource: Record<string, Record<string, boolean>> }
    | undefined,
}))
vi.mock('@/features/auth/usePermissionsQuery', () => ({
  usePermissionsQuery: () => ({ data: permissionsQueryHandles.data }),
  // RecordListPage reads PLATFORM_ADMIN_ROLE_ID from this module to gate the
  // 'Show deleted' toggle; mocking the whole module means we must re-export it.
  PLATFORM_ADMIN_ROLE_ID: '00000000-0000-0000-0000-000000000001',
}))

// Permission gate — flips between tests via the hoisted handle.
const permissionHandles = vi.hoisted(() => ({ canCreate: true, canExport: false }))
vi.mock('@/features/auth/usePermission', () => ({
  usePermission: (_resource: string, action: string) => {
    if (action === 'canCreate') return permissionHandles.canCreate
    if (action === 'canExport') return permissionHandles.canExport
    return false
  },
}))

// useCreateRecord — mock that exposes a captured `mutate` handle so tests can
// assert the payload and trigger the onSuccess/onError callbacks the host
// passes inline.
const createMutationHandles = vi.hoisted(() => ({
  mutate: vi.fn(),
  isPending: false,
}))
vi.mock('@/features/data-entry/useCreateRecord', () => ({
  useCreateRecord: () => ({
    mutate: createMutationHandles.mutate,
    isPending: createMutationHandles.isPending,
  }),
}))

// useRecordList — mock that returns a configurable result for the list view.
const recordListHandles = vi.hoisted(() => ({
  data: undefined as
    | undefined
    | {
        data: Array<Record<string, unknown> & { id: string }>
        total: number
        page: number
        pageSize: number
        totalPages: number
      },
  isLoading: false,
  isFetching: false,
  isError: false,
  error: undefined as unknown,
  refetch: vi.fn(),
}))
vi.mock('@/features/data-entry/useRecordList', () => ({
  useRecordList: () => ({ ...recordListHandles }),
}))

// useRestoreRecord — Story 7-followup. Per-row Restore button on RecordListPage
// (visible only when the 'Show deleted' toggle is on). Stub returns a stable
// handle so existing tests that don't trigger restore need no configuration.
const restoreMutationHandles = vi.hoisted(() => ({
  mutate: vi.fn(),
  isPending: false,
}))
vi.mock('@/features/data-entry/useRestoreRecord', () => ({
  useRestoreRecord: () => ({ ...restoreMutationHandles }),
}))

// useDesignerFieldKeys — Story 6.10 supplies the user-column fieldKeys from
// the bound designer schema. Tests configure the hoisted handle directly.
const designerFieldKeysHandles = vi.hoisted(() => ({
  fieldKeys: [] as string[],
  // Story 7-followup — type-aware FilterRow uses `fields` (key + kind) for
  // widget dispatch. Tests that just set fieldKeys get a default 'text' kind
  // for each key so existing assertions still find the plain Input widget.
  fields: [] as Array<{ key: string; kind: 'text' | 'number' | 'boolean' | 'datetime' | 'other' }>,
  // Story B-1-4-followup — tests can set explicit columns; otherwise derived from
  // fieldKeys/fields in the mock factory below.
  columns: [] as Array<{
    dataKey: string
    header: string
    order: number
    sortable: boolean
    filterKind?: 'text' | 'number' | 'boolean' | 'datetime' | 'other'
  }>,
  isLoading: false,
  isError: false,
}))
vi.mock('@/features/data-entry/useDesignerFieldKeys', () => ({
  useDesignerFieldKeys: () => {
    const h = designerFieldKeysHandles
    // Derive fields from fieldKeys when the test only sets fieldKeys, so old
    // tests continue to exercise the text-input branch by default.
    const fields = h.fields.length > 0
      ? h.fields
      : h.fieldKeys.map((key) => ({ key, kind: 'text' as const }))
    // Story B-1-4-followup — RecordListPage now consumes `columns`. Derive a
    // sortable text column per fieldKey (header = humanized key) so the legacy
    // fieldKeys-only tests keep asserting against the same headers/widgets.
    const humanize = (key: string) =>
      key
        .split('_')
        .filter((w) => w.length > 0)
        .map((w) => w.charAt(0).toUpperCase() + w.slice(1))
        .join(' ')
    const columns =
      h.columns.length > 0
        ? h.columns
        : fields.map((f, i) => ({
            dataKey: f.key,
            header: humanize(f.key),
            order: i,
            sortable: true,
            filterKind: f.kind,
          }))
    return { ...h, fields, columns }
  },
}))

// Hoisted handle so the vi.mock factory and tests share one capture of the
// onSave prop. `submit` is what submitRef.current() calls — we wire it to fire
// the captured onSave with a fake payload so the host's handleSave path is
// actually exercised (not just the button→ref plumbing). `lastServerErrors`
// captures the serverErrors prop DynamicComponent receives so Story 6.11 AC-5
// tests can assert the field-error mapping flows through to the renderer.
const dynamicHandles = vi.hoisted(() => {
  return {
    submit: { fn: null as null | (() => void) },
    onSaveProp: {
      fn: null as null | ((data: Record<string, unknown>) => void),
    },
    lastServerErrors: undefined as undefined | Record<string, string>,
    reset() {
      this.submit.fn = null
      this.onSaveProp.fn = null
      this.lastServerErrors = undefined
    },
  }
})

// DynamicComponent mock — captures onSave so the test can fire it via submitRef
// and verify the host's handleSave (mutation.mutate + toast) runs end-to-end.
vi.mock('@/components/designer/DynamicComponent', () => ({
  default: function MockDynamicComponent({
    submitRef,
    onReadyChange,
    onValidityChange,
    onSave,
    designerId,
    serverErrors,
  }: {
    submitRef?: MutableRefObject<(() => void) | null>
    onReadyChange?: (v: boolean) => void
    onValidityChange?: (v: boolean) => void
    onSave?: (data: Record<string, unknown>) => void
    designerId: string
    serverErrors?: Record<string, string>
  }) {
    useEffect(() => {
      dynamicHandles.onSaveProp.fn = onSave ?? null
    }, [onSave])
    useEffect(() => {
      dynamicHandles.lastServerErrors = serverErrors
    }, [serverErrors])
    useEffect(() => {
      if (!submitRef) return
      submitRef.current = () => dynamicHandles.submit.fn?.()
      return () => {
        submitRef.current = null
      }
    }, [submitRef])
    return (
      <div data-testid="mock-dynamic-component" data-designer-id={designerId}>
        <button
          type="button"
          data-testid="mock-set-ready"
          onClick={() => onReadyChange?.(true)}
        >
          set ready
        </button>
        <button
          type="button"
          data-testid="mock-set-valid"
          onClick={() => onValidityChange?.(true)}
        >
          set valid
        </button>
      </div>
    )
  },
}))

// Import AFTER mocks so the pages pick up the stubbed dependencies.
import { DataEntryPage } from '@/components/dataEntry/DataEntryPage'
import { RecordListPage } from '@/components/dataEntry/RecordListPage'
import { ApiError } from '@/lib/api/apiError'

function resetAll() {
  cleanup()
  toastSuccess.mockReset()
  toastError.mockReset()
  navigateMock.mockReset()
  dynamicHandles.reset()
  permissionHandles.canCreate = true
  permissionHandles.canExport = false
  permissionsQueryHandles.data = { isActive: true, roleIds: [], perResource: {} }
  createMutationHandles.mutate = vi.fn()
  createMutationHandles.isPending = false
  recordListHandles.data = undefined
  recordListHandles.isLoading = false
  recordListHandles.isFetching = false
  recordListHandles.isError = false
  recordListHandles.error = undefined
  recordListHandles.refetch = vi.fn()
  designerFieldKeysHandles.fieldKeys = []
  designerFieldKeysHandles.fields = []
  designerFieldKeysHandles.columns = []
  designerFieldKeysHandles.isLoading = false
  designerFieldKeysHandles.isError = false
}

// Helper: render RecordListPage with default Story 6.10 props. Tests can pass
// overrides — most commonly `sort`, `filter`, or one of the four callbacks.
type ListProps = Partial<{
  designerId: string
  page: number
  pageSize: number
  sort: string | undefined
  filter: Record<string, string> | undefined
  showDeleted: boolean
  onPageChange: (p: number) => void
  onPageSizeChange: (ps: number) => void
  onSortChange: (s: string | undefined) => void
  onFilterChange: (f: Record<string, string> | undefined) => void
  onShowDeletedChange: (v: boolean) => void
}>
function renderList(overrides: ListProps = {}) {
  const props = {
    designerId: 'my-form',
    page: 1,
    pageSize: 25,
    sort: undefined,
    filter: undefined,
    onPageChange: vi.fn(),
    onPageSizeChange: vi.fn(),
    onSortChange: vi.fn(),
    onFilterChange: vi.fn(),
    onShowDeletedChange: vi.fn(),
    ...overrides,
  }
  return { ...props, result: render(<RecordListPage {...props} />) }
}

const PLATFORM_ADMIN_ROLE_ID = '00000000-0000-0000-0000-000000000001'

describe('DataEntryPage (data.$designerId.new route body)', () => {
  beforeEach(() => {
    resetAll()
  })

  it('mounts DynamicComponent with the designerId passed in from route params (AC-2)', () => {
    render(<DataEntryPage designerId="test-form" />)
    const dc = screen.getByTestId('mock-dynamic-component')
    expect(dc.getAttribute('data-designer-id')).toBe('test-form')
  })

  it('keeps the Save button disabled until DynamicComponent fires onReadyChange(true) and onValidityChange(true) (AC-2)', () => {
    render(<DataEntryPage designerId="test-form" />)
    const saveBtn = screen.getByRole('button', { name: 'data.entry.save' }) as HTMLButtonElement
    // Initial state: neither ready nor valid — disabled.
    expect(saveBtn.disabled).toBe(true)
    // Mark ready only — still disabled because valid is false.
    fireEvent.click(screen.getByTestId('mock-set-ready'))
    expect(saveBtn.disabled).toBe(true)
    // Mark valid — now enabled.
    fireEvent.click(screen.getByTestId('mock-set-valid'))
    expect(saveBtn.disabled).toBe(false)
  })

  it('clicking Save invokes mutation.mutate(payload) then onSuccess fires toast.success (AC-2 + AC-5)', () => {
    const payload = { name: 'Alice' }
    dynamicHandles.submit.fn = () => dynamicHandles.onSaveProp.fn?.(payload)

    render(<DataEntryPage designerId="test-form" />)
    fireEvent.click(screen.getByTestId('mock-set-ready'))
    fireEvent.click(screen.getByTestId('mock-set-valid'))
    const saveBtn = screen.getByRole('button', { name: 'data.entry.save' }) as HTMLButtonElement
    expect(saveBtn.disabled).toBe(false)

    fireEvent.click(saveBtn)

    expect(createMutationHandles.mutate).toHaveBeenCalledTimes(1)
    const [arg, options] = createMutationHandles.mutate.mock.calls[0] as [
      Record<string, unknown>,
      { onSuccess: () => void; onError: () => void },
    ]
    expect(arg).toEqual(payload)
    // Trigger the inline onSuccess to exercise the toast + navigate path.
    options.onSuccess()
    expect(toastSuccess).toHaveBeenCalledWith('data.entry.createSuccess')
    expect(navigateMock).toHaveBeenCalledWith({
      to: '/data/$designerId',
      params: { designerId: 'test-form' },
      search: { page: 1, pageSize: 25 },
    })
    // Trigger onError on a fresh mutate call to exercise the error toast.
    options.onError()
    expect(toastError).toHaveBeenCalledWith('data.entry.createError')
  })

  it('renders the permission-denied message when canCreate=false (AC-2 guard)', () => {
    permissionHandles.canCreate = false
    render(<DataEntryPage designerId="test-form" />)
    expect(screen.getByText('data.entry.permissionDenied')).toBeTruthy()
    expect(screen.queryByTestId('mock-dynamic-component')).toBeNull()
  })

  it('Story 6.11 AC-1: shows skeleton placeholder while permissions are loading', () => {
    permissionsQueryHandles.data = undefined
    render(<DataEntryPage designerId="test-form" />)
    // No `common.loading` text — replaced by Skeleton.
    expect(screen.queryByText('common.loading')).toBeNull()
    // shadcn Skeleton uses `animate-pulse`; a stack of placeholders is rendered.
    expect(document.querySelectorAll('.animate-pulse').length).toBeGreaterThan(0)
  })

  it('Story 6.11 AC-3: mutation onError with ApiError toasts the server messageKey', async () => {
    const payload = { name: 'Bad' }
    dynamicHandles.submit.fn = () => dynamicHandles.onSaveProp.fn?.(payload)

    render(<DataEntryPage designerId="test-form" />)
    fireEvent.click(screen.getByTestId('mock-set-ready'))
    fireEvent.click(screen.getByTestId('mock-set-valid'))
    fireEvent.click(screen.getByRole('button', { name: 'data.entry.save' }))

    const [, options] = createMutationHandles.mutate.mock.calls[0] as [
      Record<string, unknown>,
      { onSuccess: () => void; onError: (err: unknown) => void },
    ]
    const apiError = new ApiError(
      422,
      'VALIDATION_FAILED',
      'errors.validationFailed',
      undefined,
      undefined,
      'cid-001',
    )
    options.onError(apiError)
    expect(toastError).toHaveBeenCalledWith('errors.validationFailed')
  })

  it('Story 6.11 AC-3 fallback: non-ApiError onError toasts the hardcoded createError key', () => {
    const payload = { name: 'Bad' }
    dynamicHandles.submit.fn = () => dynamicHandles.onSaveProp.fn?.(payload)

    render(<DataEntryPage designerId="test-form" />)
    fireEvent.click(screen.getByTestId('mock-set-ready'))
    fireEvent.click(screen.getByTestId('mock-set-valid'))
    fireEvent.click(screen.getByRole('button', { name: 'data.entry.save' }))
    const [, options] = createMutationHandles.mutate.mock.calls[0] as [
      Record<string, unknown>,
      { onSuccess: () => void; onError: (err: unknown) => void },
    ]
    options.onError(new Error('network down'))
    expect(toastError).toHaveBeenCalledWith('data.entry.createError')
  })

  it('Story 6.11 AC-5: 422 ApiError with fieldErrors flows into DynamicComponent.serverErrors', async () => {
    const payload = { title: '' }
    dynamicHandles.submit.fn = () => dynamicHandles.onSaveProp.fn?.(payload)

    render(<DataEntryPage designerId="test-form" />)
    fireEvent.click(screen.getByTestId('mock-set-ready'))
    fireEvent.click(screen.getByTestId('mock-set-valid'))
    fireEvent.click(screen.getByRole('button', { name: 'data.entry.save' }))

    // Initially no server errors have been pushed down.
    expect(dynamicHandles.lastServerErrors).toBeUndefined()

    const [, options] = createMutationHandles.mutate.mock.calls[0] as [
      Record<string, unknown>,
      { onSuccess: () => void; onError: (err: unknown) => void },
    ]
    const apiError = new ApiError(
      422,
      'VALIDATION_FAILED',
      'errors.validationFailed',
      undefined,
      { title: ['Title is required.'], status: ['Status must be open or closed.'] },
      'cid-002',
    )
    options.onError(apiError)
    // After the state update flushes, DynamicComponent receives the flattened
    // per-field map (first message per field).
    await waitFor(() => {
      expect(dynamicHandles.lastServerErrors).toEqual({
        title: 'Title is required.',
        status: 'Status must be open or closed.',
      })
    })
  })
})

describe('RecordListPage (data.$designerId route body)', () => {
  beforeEach(() => {
    resetAll()
  })

  it('renders the "New Record" button when canCreate=true and navigates to /new on click (AC-1)', () => {
    recordListHandles.data = { data: [], total: 0, page: 1, pageSize: 25, totalPages: 1 }
    permissionHandles.canCreate = true

    renderList()
    const newBtn = screen.getByRole('button', { name: 'data.entry.newRecord' })
    expect(newBtn).toBeTruthy()
    fireEvent.click(newBtn)
    expect(navigateMock).toHaveBeenCalledWith({
      to: '/data/$designerId/new',
      params: { designerId: 'my-form' },
      // /data/$designerId/new inherits the parent's defaulted search schema;
      // the navigate call passes explicit defaults so TanStack Router accepts
      // the typed-navigate call.
      search: { page: 1, pageSize: 25 },
    })
  })

  it('hides the "New Record" button when canCreate=false (AC-1)', () => {
    recordListHandles.data = { data: [], total: 0, page: 1, pageSize: 25, totalPages: 1 }
    permissionHandles.canCreate = false

    renderList()
    expect(screen.queryByRole('button', { name: 'data.entry.newRecord' })).toBeNull()
  })

  it('renders rows and navigates to detail on row click (AC-1)', () => {
    designerFieldKeysHandles.fieldKeys = ['name']
    recordListHandles.data = {
      data: [
        {
          id: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
          createdAt: '2026-05-26T10:00:00Z',
          updatedAt: '2026-05-26T10:00:00Z',
          createdBy: null,
          updatedBy: null,
          isDeleted: false,
          cascadeEventId: null,
          name: 'Alpha',
        },
      ],
      total: 1,
      page: 1,
      pageSize: 25,
      totalPages: 1,
    }

    renderList()
    // The raw id column was removed — click the row via a data cell instead.
    const cell = screen.getByText('Alpha')
    fireEvent.click(cell.closest('tr')!)
    expect(navigateMock).toHaveBeenCalledWith({
      to: '/data/$designerId/$recordId',
      params: { designerId: 'my-form', recordId: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee' },
      search: { page: 1, pageSize: 25 },
    })
  })

  it('renders schema-driven column headers from useDesignerFieldKeys (AC-1)', () => {
    designerFieldKeysHandles.fieldKeys = ['title', 'status']
    recordListHandles.data = {
      data: [
        {
          id: 'r1',
          createdAt: '2026-05-26T10:00:00Z',
          updatedAt: '2026-05-26T10:00:00Z',
          createdBy: null,
          updatedBy: null,
          isDeleted: false,
          cascadeEventId: null,
          title: 'Hello',
          status: 'open',
        },
      ],
      total: 1,
      page: 1,
      pageSize: 25,
      totalPages: 1,
    }

    renderList()
    // Schema-derived user columns rendered as sortable header buttons, with the
    // snake_case fieldKey humanized for display (title → "Title").
    expect(screen.getByRole('button', { name: /Title/ })).toBeTruthy()
    expect(screen.getByRole('button', { name: /Status/ })).toBeTruthy()
    // System columns rendered after the user columns: createdAt, updatedAt. The
    // raw id column was removed — the record id is not surfaced as a table column.
    expect(screen.queryByRole('button', { name: /data\.entry\.columnId/ })).toBeNull()
    expect(screen.getByRole('button', { name: /data\.entry\.columnCreatedAt/ })).toBeTruthy()
    expect(screen.getByRole('button', { name: /data\.entry\.columnUpdatedAt/ })).toBeTruthy()
  })

  it('clicking an unsorted column header calls onSortChange with col:asc (AC-2)', () => {
    designerFieldKeysHandles.fieldKeys = ['title']
    recordListHandles.data = {
      data: [
        {
          id: 'r1',
          createdAt: '2026-05-26T10:00:00Z',
          updatedAt: '2026-05-26T10:00:00Z',
          createdBy: null,
          updatedBy: null,
          isDeleted: false,
          cascadeEventId: null,
          title: 'Hello',
        },
      ],
      total: 1,
      page: 1,
      pageSize: 25,
      totalPages: 1,
    }
    const onSortChange = vi.fn()
    renderList({ onSortChange })
    fireEvent.click(screen.getByRole('button', { name: /Title/ }))
    expect(onSortChange).toHaveBeenCalledWith('title:asc')
  })

  it('clicking an asc-sorted column header calls onSortChange with col:desc (AC-2)', () => {
    designerFieldKeysHandles.fieldKeys = ['title']
    recordListHandles.data = {
      data: [{ id: 'r1', createdAt: '2026-05-26T10:00:00Z', updatedAt: '2026-05-26T10:00:00Z',
        createdBy: null, updatedBy: null, isDeleted: false, cascadeEventId: null, title: 'x' }],
      total: 1, page: 1, pageSize: 25, totalPages: 1,
    }
    const onSortChange = vi.fn()
    renderList({ sort: 'title:asc', onSortChange })
    fireEvent.click(screen.getByRole('button', { name: /Title/ }))
    expect(onSortChange).toHaveBeenCalledWith('title:desc')
  })

  it('clicking a desc-sorted column header removes the sort — onSortChange(undefined) (AC-2)', () => {
    designerFieldKeysHandles.fieldKeys = ['title']
    recordListHandles.data = {
      data: [{ id: 'r1', createdAt: '2026-05-26T10:00:00Z', updatedAt: '2026-05-26T10:00:00Z',
        createdBy: null, updatedBy: null, isDeleted: false, cascadeEventId: null, title: 'x' }],
      total: 1, page: 1, pageSize: 25, totalPages: 1,
    }
    const onSortChange = vi.fn()
    renderList({ sort: 'title:desc', onSortChange })
    fireEvent.click(screen.getByRole('button', { name: /Title/ }))
    expect(onSortChange).toHaveBeenCalledWith(undefined)
  })

  it('filter input calls onFilterChange after a 300 ms debounce (AC-3)', () => {
    vi.useFakeTimers()
    try {
      designerFieldKeysHandles.fieldKeys = ['title']
      recordListHandles.data = {
        data: [{ id: 'r1', createdAt: '2026-05-26T10:00:00Z', updatedAt: '2026-05-26T10:00:00Z',
          createdBy: null, updatedBy: null, isDeleted: false, cascadeEventId: null, title: 'x' }],
        total: 1, page: 1, pageSize: 25, totalPages: 1,
      }
      const onFilterChange = vi.fn()
      const onPageChange = vi.fn()
      renderList({ onFilterChange, onPageChange })

      // Filter panel is collapsed by default — open it from the header toggle.
      fireEvent.click(screen.getByRole('button', { name: 'data.entry.toggleFilters' }))

      const filterInput = screen.getByPlaceholderText('data.entry.filterPlaceholder') as HTMLInputElement
      fireEvent.change(filterInput, { target: { value: 'foo' } })

      // Not called yet — within the 300 ms debounce window.
      expect(onFilterChange).not.toHaveBeenCalled()

      vi.advanceTimersByTime(300)
      expect(onFilterChange).toHaveBeenCalledWith({ title: 'foo' })
      // Filter changes also reset the page so a stale page > totalPages cannot stick.
      expect(onPageChange).toHaveBeenCalledWith(1)
    } finally {
      vi.useRealTimers()
    }
  })

  it('clearing a filter input calls onFilterChange with the key removed (AC-3)', () => {
    vi.useFakeTimers()
    try {
      designerFieldKeysHandles.fieldKeys = ['title']
      recordListHandles.data = {
        data: [{ id: 'r1', createdAt: '2026-05-26T10:00:00Z', updatedAt: '2026-05-26T10:00:00Z',
          createdBy: null, updatedBy: null, isDeleted: false, cascadeEventId: null, title: 'x' }],
        total: 1, page: 1, pageSize: 25, totalPages: 1,
      }
      const onFilterChange = vi.fn()
      // Start with an active filter so clearing produces a delta vs. the URL state.
      renderList({ filter: { title: 'foo' }, onFilterChange })

      const filterInput = screen.getByPlaceholderText('data.entry.filterPlaceholder') as HTMLInputElement
      expect(filterInput.value).toBe('foo')
      fireEvent.change(filterInput, { target: { value: '' } })

      vi.advanceTimersByTime(300)
      // When the buffer empties to {}, we send undefined so the URL drops the key entirely.
      expect(onFilterChange).toHaveBeenCalledWith(undefined)
    } finally {
      vi.useRealTimers()
    }
  })

  it('hides the filter row until the header filter toggle is clicked', () => {
    designerFieldKeysHandles.fieldKeys = ['title']
    recordListHandles.data = {
      data: [{ id: 'r1', createdAt: '2026-05-26T10:00:00Z', updatedAt: '2026-05-26T10:00:00Z',
        createdBy: null, updatedBy: null, isDeleted: false, cascadeEventId: null, title: 'x' }],
      total: 1, page: 1, pageSize: 25, totalPages: 1,
    }
    renderList({})

    // Collapsed by default (no active filter) — no filter input in the DOM.
    expect(screen.queryByPlaceholderText('data.entry.filterPlaceholder')).toBeNull()

    fireEvent.click(screen.getByRole('button', { name: 'data.entry.toggleFilters' }))
    expect(screen.getByPlaceholderText('data.entry.filterPlaceholder')).toBeTruthy()
  })

  it('auto-opens the panel and clears all filters via the header Clear button', () => {
    designerFieldKeysHandles.fieldKeys = ['title']
    recordListHandles.data = {
      data: [{ id: 'r1', createdAt: '2026-05-26T10:00:00Z', updatedAt: '2026-05-26T10:00:00Z',
        createdBy: null, updatedBy: null, isDeleted: false, cascadeEventId: null, title: 'x' }],
      total: 1, page: 1, pageSize: 25, totalPages: 1,
    }
    const onFilterChange = vi.fn()
    const onPageChange = vi.fn()
    // Active filter → panel auto-opens, so the filter input + Clear are visible.
    renderList({ filter: { title: 'foo' }, onFilterChange, onPageChange })
    expect(screen.getByPlaceholderText('data.entry.filterPlaceholder')).toBeTruthy()

    fireEvent.click(screen.getByRole('button', { name: 'data.entry.clearFilters' }))
    expect(onFilterChange).toHaveBeenCalledWith(undefined)
    expect(onPageChange).toHaveBeenCalledWith(1)
  })

  it('page-size picker change calls onPageSizeChange (AC-4)', () => {
    recordListHandles.data = { data: [], total: 0, page: 1, pageSize: 25, totalPages: 1 }
    const onPageSizeChange = vi.fn()
    renderList({ onPageSizeChange })

    const select = screen.getByRole('combobox') as HTMLSelectElement
    fireEvent.change(select, { target: { value: '10' } })
    expect(onPageSizeChange).toHaveBeenCalledWith(10)
  })

  it('renders the total record count from query.data.total (AC-4)', () => {
    recordListHandles.data = { data: [], total: 42, page: 1, pageSize: 25, totalPages: 2 }
    renderList()
    // The identity translator surfaces unmatched opts as a suffix on the key,
    // so the rendered text proves the count flowed from query.data.total
    // through to the i18n call site.
    expect(screen.getByText('data.entry.totalRecords 42')).toBeTruthy()
  })

  it('shift-clicking a second column appends it as a secondary sort (AC-2 multi)', () => {
    designerFieldKeysHandles.fieldKeys = ['title', 'status']
    recordListHandles.data = {
      data: [{ id: 'r1', createdAt: '2026-05-26T10:00:00Z', updatedAt: '2026-05-26T10:00:00Z',
        createdBy: null, updatedBy: null, isDeleted: false, cascadeEventId: null, title: 'x', status: 'open' }],
      total: 1, page: 1, pageSize: 25, totalPages: 1,
    }
    const onSortChange = vi.fn()
    renderList({ sort: 'title:asc', onSortChange })
    fireEvent.click(screen.getByRole('button', { name: /Status/ }), { shiftKey: true })
    expect(onSortChange).toHaveBeenCalledWith('title:asc,status:asc')
  })

  it('renders a priority badge with aria-label on each multi-sorted header (AC-2 multi)', () => {
    designerFieldKeysHandles.fieldKeys = ['title', 'status']
    recordListHandles.data = {
      data: [{ id: 'r1', createdAt: '2026-05-26T10:00:00Z', updatedAt: '2026-05-26T10:00:00Z',
        createdBy: null, updatedBy: null, isDeleted: false, cascadeEventId: null, title: 'x', status: 'open' }],
      total: 1, page: 1, pageSize: 25, totalPages: 1,
    }
    renderList({ sort: 'title:asc,status:desc' })
    // The identity translator surfaces `{n}` as a trailing token on the key, so
    // the rendered aria-label proves both that the priority is exposed to AT and
    // that the priority position flowed through to the i18n call site.
    expect(screen.getByLabelText('data.entry.sortPriority 1')).toBeTruthy()
    expect(screen.getByLabelText('data.entry.sortPriority 2')).toBeTruthy()
  })

  it('exposes aria-sort on the active sort column header cell (AC-2 a11y)', () => {
    designerFieldKeysHandles.fieldKeys = ['title']
    recordListHandles.data = {
      data: [{ id: 'r1', createdAt: '2026-05-26T10:00:00Z', updatedAt: '2026-05-26T10:00:00Z',
        createdBy: null, updatedBy: null, isDeleted: false, cascadeEventId: null, title: 'x' }],
      total: 1, page: 1, pageSize: 25, totalPages: 1,
    }
    renderList({ sort: 'title:asc' })
    const titleHeaderBtn = screen.getByRole('button', { name: /Title/ })
    const th = titleHeaderBtn.closest('th')
    expect(th?.getAttribute('aria-sort')).toBe('ascending')
  })

  it('sends snake_case sort keys for system columns so the backend whitelist accepts them (AC-2)', () => {
    designerFieldKeysHandles.fieldKeys = []
    recordListHandles.data = {
      data: [{ id: 'r1', createdAt: '2026-05-26T10:00:00Z', updatedAt: '2026-05-26T10:00:00Z',
        createdBy: null, updatedBy: null, isDeleted: false, cascadeEventId: null }],
      total: 1, page: 1, pageSize: 25, totalPages: 1,
    }
    const onSortChange = vi.fn()
    renderList({ onSortChange })
    fireEvent.click(screen.getByRole('button', { name: /data\.entry\.columnCreatedAt/ }))
    expect(onSortChange).toHaveBeenCalledWith('created_at:asc')
  })

  it('renders a soft-deleted row with strikethrough text and a Deleted badge (AC-5)', () => {
    designerFieldKeysHandles.fieldKeys = ['name']
    recordListHandles.data = {
      data: [
        {
          id: 'deleted00-1111-2222-3333-444444444444',
          createdAt: '2026-05-26T10:00:00Z',
          updatedAt: '2026-05-26T10:00:00Z',
          createdBy: null,
          updatedBy: null,
          isDeleted: true,
          cascadeEventId: null,
          name: 'Gone',
        },
      ],
      total: 1,
      page: 1,
      pageSize: 25,
      totalPages: 1,
    }
    renderList()
    // Badge relocated to the first data cell after the id column removal; its
    // cell carries the line-through (muted) styling for the soft-deleted row.
    const badge = screen.getByText('data.entry.deletedBadge')
    expect(badge).toBeTruthy()
    expect(badge.closest('td')!.className).toContain('line-through')
  })

  it("hides the 'Show deleted' toggle for non-admin users", () => {
    permissionsQueryHandles.data = { isActive: true, roleIds: [], perResource: {} }
    recordListHandles.data = { data: [], total: 0, page: 1, pageSize: 25, totalPages: 1 }
    renderList()
    expect(screen.queryByText('data.entry.showDeleted')).toBeNull()
  })

  it("shows the 'Show deleted' toggle for platform admins", () => {
    permissionsQueryHandles.data = {
      isActive: true,
      roleIds: [PLATFORM_ADMIN_ROLE_ID],
      perResource: {},
    }
    recordListHandles.data = { data: [], total: 0, page: 1, pageSize: 25, totalPages: 1 }
    renderList()
    expect(screen.getByText('data.entry.showDeleted')).toBeTruthy()
  })

  it('non-admin with showDeleted=true in URL still does not request deleted rows', () => {
    // Defence-in-depth: a hand-crafted ?showDeleted=true must not leak deleted
    // rows into a non-admin's list — effectiveShowDeleted gates includeDeleted.
    permissionsQueryHandles.data = { isActive: true, roleIds: [], perResource: {} }
    recordListHandles.data = { data: [], total: 0, page: 1, pageSize: 25, totalPages: 1 }
    renderList({ showDeleted: true })
    // No Actions column header when the toggle is ineffective.
    expect(screen.queryByText('data.entry.columnActions')).toBeNull()
  })

  it('hides export buttons when the role lacks canExport', () => {
    permissionHandles.canExport = false
    recordListHandles.data = {
      data: [{ id: 'r1', createdAt: '2026-05-26T10:00:00Z', updatedAt: '2026-05-26T10:00:00Z',
        createdBy: null, updatedBy: null, isDeleted: false, cascadeEventId: null }],
      total: 1, page: 1, pageSize: 25, totalPages: 1,
    }
    renderList()
    expect(screen.queryByText('data.entry.exportCsv')).toBeNull()
    expect(screen.queryByText('data.entry.exportXlsx')).toBeNull()
    expect(screen.queryByText('data.entry.exportPdf')).toBeNull()
  })

  it('shows export buttons when the role grants canExport', () => {
    permissionHandles.canExport = true
    recordListHandles.data = {
      data: [{ id: 'r1', createdAt: '2026-05-26T10:00:00Z', updatedAt: '2026-05-26T10:00:00Z',
        createdBy: null, updatedBy: null, isDeleted: false, cascadeEventId: null }],
      total: 1, page: 1, pageSize: 25, totalPages: 1,
    }
    renderList()
    expect(screen.getByText('data.entry.exportCsv')).toBeTruthy()
    expect(screen.getByText('data.entry.exportXlsx')).toBeTruthy()
    expect(screen.getByText('data.entry.exportPdf')).toBeTruthy()
  })

  it('Story 6.11 AC-1: loading state renders Skeleton rows, not common.loading text', () => {
    recordListHandles.isLoading = true
    recordListHandles.data = undefined
    renderList()
    // The old `<p>common.loading</p>` text is gone.
    expect(screen.queryByText('common.loading')).toBeNull()
    // Skeleton rows render with `animate-pulse`.
    expect(document.querySelectorAll('.animate-pulse').length).toBeGreaterThan(0)
  })

  it('Story 6.11 AC-2: empty state with canCreate=true renders "Create the first record" CTA', () => {
    permissionHandles.canCreate = true
    recordListHandles.data = { data: [], total: 0, page: 1, pageSize: 25, totalPages: 1 }
    renderList()
    expect(
      screen.getByRole('button', { name: 'data.entry.createFirstRecord' }),
    ).toBeTruthy()
  })

  it('Story 6.11 AC-2: empty-state CTA navigates to /new on click', () => {
    permissionHandles.canCreate = true
    recordListHandles.data = { data: [], total: 0, page: 1, pageSize: 25, totalPages: 1 }
    renderList()
    fireEvent.click(
      screen.getByRole('button', { name: 'data.entry.createFirstRecord' }),
    )
    expect(navigateMock).toHaveBeenCalledWith({
      to: '/data/$designerId/new',
      params: { designerId: 'my-form' },
      search: { page: 1, pageSize: 25 },
    })
  })

  it('Story 6.11 AC-2: empty state with canCreate=false hides the CTA', () => {
    permissionHandles.canCreate = false
    recordListHandles.data = { data: [], total: 0, page: 1, pageSize: 25, totalPages: 1 }
    renderList()
    expect(
      screen.queryByRole('button', { name: 'data.entry.createFirstRecord' }),
    ).toBeNull()
    // The bare empty-state message is still rendered.
    expect(screen.getByText('data.entry.noRecords')).toBeTruthy()
  })

  it('Story 6.11 AC-4: list query.isError renders ErrorBanner (role=alert), not toast', () => {
    recordListHandles.isError = true
    recordListHandles.data = undefined
    recordListHandles.error = new ApiError(
      500,
      'INTERNAL',
      'errors.genericError',
      undefined,
      undefined,
      'cid-list-001',
    )
    renderList()
    // ErrorBanner uses role="alert" so screen readers announce it.
    expect(screen.getByRole('alert')).toBeTruthy()
    // The legacy toast-on-error effect was removed in Story 6.11.
    expect(toastError).not.toHaveBeenCalled()
  })

  it('Story 6.11 AC-4: clicking the ErrorBanner Retry button calls query.refetch', () => {
    recordListHandles.isError = true
    recordListHandles.data = undefined
    recordListHandles.error = new ApiError(
      500,
      'INTERNAL',
      'errors.genericError',
      undefined,
      undefined,
      'cid-list-002',
    )
    const refetchSpy = vi.fn()
    recordListHandles.refetch = refetchSpy
    renderList()
    fireEvent.click(screen.getByRole('button', { name: 'errors.retry' }))
    expect(refetchSpy).toHaveBeenCalledTimes(1)
  })

  it('Story 6.11 AC-4: ErrorBanner shows the ApiError correlationId for support', () => {
    recordListHandles.isError = true
    recordListHandles.data = undefined
    recordListHandles.error = new ApiError(
      500,
      'INTERNAL',
      'errors.genericError',
      undefined,
      undefined,
      'cid-list-003',
    )
    renderList()
    // Identity translator passes opts through as a token suffix → `cid-list-003`
    // appears verbatim in the rendered markup.
    expect(screen.getByText(/cid-list-003/)).toBeTruthy()
  })
})
