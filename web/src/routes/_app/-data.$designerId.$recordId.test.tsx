import { describe, it, expect, vi, beforeEach } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { useEffect } from 'react'
import type { MutableRefObject, ReactNode } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

// Identity translator — `t(key)` returns the key.
vi.mock('react-i18next', () => ({
  useTranslation: () => ({
    t: (key: string, opts?: Record<string, unknown>) => {
      if (opts && typeof opts === 'object') {
        let out = key
        for (const [k, v] of Object.entries(opts)) {
          out = out.replace(new RegExp(`{{\\s*${k}\\s*}}`, 'g'), String(v))
        }
        return out
      }
      return key
    },
  }),
}))

// sonner toast — captured.
const toastSuccess = vi.fn()
const toastError = vi.fn()
vi.mock('sonner', () => ({
  toast: {
    success: (...args: unknown[]) => toastSuccess(...args),
    error: (...args: unknown[]) => toastError(...args),
  },
}))

// TanStack Router useNavigate — captured.
const navigateMock = vi.fn()
vi.mock('@tanstack/react-router', () => ({
  useNavigate: () => navigateMock,
}))

// Permission gate — flips per action via hoisted handle.
const permissionHandles = vi.hoisted(() => ({
  canUpdate: true,
  canDelete: true,
}))
vi.mock('@/features/auth/usePermission', () => ({
  usePermission: (_resource: string, action: string) => {
    if (action === 'canUpdate') return permissionHandles.canUpdate
    if (action === 'canDelete') return permissionHandles.canDelete
    return false
  },
}))

// useRecord — return a configurable record fixture.
const recordHandles = vi.hoisted(() => ({
  data: undefined as
    | undefined
    | (Record<string, unknown> & { id: string; isDeleted: boolean }),
  isLoading: false,
  isError: false,
  error: undefined as unknown,
  refetch: vi.fn(),
}))
vi.mock('@/features/data-entry/useRecord', () => ({
  useRecord: () => ({ ...recordHandles }),
}))

// useUpdateRecord — capture mutate.
const updateMutationHandles = vi.hoisted(() => ({
  mutate: vi.fn(),
  isPending: false,
}))
vi.mock('@/features/data-entry/useUpdateRecord', () => ({
  useUpdateRecord: () => ({
    mutate: updateMutationHandles.mutate,
    isPending: updateMutationHandles.isPending,
  }),
}))

// deleteRecord (used inside inline useMutation in RecordDetailPage). Returns
// a controllable promise so onSuccess/onError fire deterministically.
const deleteHandles = vi.hoisted(() => ({
  resolveWith: undefined as undefined | Record<string, unknown>,
  rejectWith: undefined as undefined | Error,
  calledWith: undefined as undefined | { designerId: string; id: string },
  deferred: false,
  _resolve: null as null | ((v: unknown) => void),
  resolveDeferred(val?: Record<string, unknown>) {
    this._resolve?.(val ?? this.resolveWith ?? {})
  },
}))
vi.mock('@/features/data-entry/deleteRecordApi', () => ({
  deleteRecord: (designerId: string, id: string) => {
    deleteHandles.calledWith = { designerId, id }
    if (deleteHandles.rejectWith) return Promise.reject(deleteHandles.rejectWith)
    if (deleteHandles.deferred) {
      return new Promise((resolve) => {
        deleteHandles._resolve = resolve
      })
    }
    return Promise.resolve(deleteHandles.resolveWith ?? { id, isDeleted: true })
  },
}))

// DynamicComponent — capture the onSave / submitRef wiring AND record whether
// it was mounted with `initialData`. Read-only mode is detected by the absence
// of `submitRef`. Story 6.11 adds capture of the serverErrors prop so AC-5
// tests can assert the flattened per-field map flows through.
const dynamicHandles = vi.hoisted(() => ({
  submit: { fn: null as null | (() => void) },
  onSaveProp: { fn: null as null | ((data: Record<string, unknown>) => void) },
  lastInitialData: undefined as undefined | Record<string, unknown>,
  lastHadSubmitRef: false,
  lastServerErrors: undefined as undefined | Record<string, string>,
  reset() {
    this.submit.fn = null
    this.onSaveProp.fn = null
    this.lastInitialData = undefined
    this.lastHadSubmitRef = false
    this.lastServerErrors = undefined
  },
}))
vi.mock('@/components/designer/DynamicComponent', () => ({
  default: function MockDynamicComponent({
    submitRef,
    onReadyChange,
    onValidityChange,
    onSave,
    designerId,
    initialData,
    serverErrors,
  }: {
    submitRef?: MutableRefObject<(() => void) | null>
    onReadyChange?: (v: boolean) => void
    onValidityChange?: (v: boolean) => void
    onSave?: (data: Record<string, unknown>) => void
    designerId: string
    initialData?: Record<string, unknown>
    serverErrors?: Record<string, string>
  }) {
    useEffect(() => {
      dynamicHandles.onSaveProp.fn = onSave ?? null
    }, [onSave])
    useEffect(() => {
      dynamicHandles.lastInitialData = initialData
    }, [initialData])
    useEffect(() => {
      dynamicHandles.lastServerErrors = serverErrors
    }, [serverErrors])
    useEffect(() => {
      dynamicHandles.lastHadSubmitRef = !!submitRef
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

import { RecordDetailPage } from '@/components/dataEntry/RecordDetailPage'
import { ApiError } from '@/lib/api/apiError'

function renderWithClient(node: ReactNode) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  return render(<QueryClientProvider client={client}>{node}</QueryClientProvider>)
}

function resetAll() {
  cleanup()
  toastSuccess.mockReset()
  toastError.mockReset()
  navigateMock.mockReset()
  dynamicHandles.reset()
  permissionHandles.canUpdate = true
  permissionHandles.canDelete = true
  recordHandles.data = {
    id: 'rec-1',
    createdAt: '2026-05-26T09:00:00Z',
    createdBy: null,
    updatedAt: '2026-05-26T09:00:00Z',
    updatedBy: null,
    isDeleted: false,
    cascadeEventId: null,
    name: 'Alpha',
  }
  recordHandles.isLoading = false
  recordHandles.isError = false
  recordHandles.error = undefined
  recordHandles.refetch = vi.fn()
  updateMutationHandles.mutate = vi.fn()
  updateMutationHandles.isPending = false
  deleteHandles.resolveWith = undefined
  deleteHandles.rejectWith = undefined
  deleteHandles.calledWith = undefined
  deleteHandles.deferred = false
  deleteHandles._resolve = null
}

describe('RecordDetailPage', () => {
  beforeEach(() => {
    resetAll()
  })

  it('AC-3 read-only mode: canUpdate=false → no Save button and DynamicComponent mounts without submitRef', () => {
    permissionHandles.canUpdate = false
    permissionHandles.canDelete = false

    renderWithClient(<RecordDetailPage designerId="my-form" recordId="rec-1" />)
    expect(screen.queryByRole('button', { name: 'data.entry.save' })).toBeNull()
    expect(dynamicHandles.lastHadSubmitRef).toBe(false)
    // initialData should be the loaded record (system columns included).
    expect(dynamicHandles.lastInitialData?.name).toBe('Alpha')
  })

  it('AC-3 edit mode: Save button stays disabled until ready+valid; click fires updateRecord and toasts updateSuccess (AC-5)', () => {
    permissionHandles.canUpdate = true
    const payload = { name: 'Bravo' }
    dynamicHandles.submit.fn = () => dynamicHandles.onSaveProp.fn?.(payload)

    renderWithClient(<RecordDetailPage designerId="my-form" recordId="rec-1" />)
    const saveBtn = screen.getByRole('button', { name: 'data.entry.save' }) as HTMLButtonElement
    expect(saveBtn.disabled).toBe(true)
    fireEvent.click(screen.getByTestId('mock-set-ready'))
    fireEvent.click(screen.getByTestId('mock-set-valid'))
    expect(saveBtn.disabled).toBe(false)

    fireEvent.click(saveBtn)
    expect(updateMutationHandles.mutate).toHaveBeenCalledTimes(1)
    const [arg, options] = updateMutationHandles.mutate.mock.calls[0] as [
      { id: string; payload: Record<string, unknown> },
      { onSuccess: () => void; onError: () => void },
    ]
    expect(arg).toEqual({ id: 'rec-1', payload })
    options.onSuccess()
    expect(toastSuccess).toHaveBeenCalledWith('data.entry.updateSuccess')
    options.onError()
    expect(toastError).toHaveBeenCalledWith('data.entry.updateError')
  })

  it('AC-4 delete button hidden when canDelete=false', () => {
    permissionHandles.canDelete = false
    renderWithClient(<RecordDetailPage designerId="my-form" recordId="rec-1" />)
    expect(screen.queryByRole('button', { name: 'data.entry.delete' })).toBeNull()
  })

  it('AC-4 delete button visible when canDelete=true', () => {
    permissionHandles.canDelete = true
    renderWithClient(<RecordDetailPage designerId="my-form" recordId="rec-1" />)
    expect(screen.getAllByRole('button', { name: 'data.entry.delete' }).length).toBeGreaterThanOrEqual(1)
  })

  it('AC-4 dialog opens on Delete click and Cancel closes it without firing deleteRecord', async () => {
    permissionHandles.canDelete = true
    renderWithClient(<RecordDetailPage designerId="my-form" recordId="rec-1" />)

    fireEvent.click(screen.getByRole('button', { name: 'data.entry.delete' }))
    // After click, the dialog footer's Cancel button (Radix renders the
    // DialogContent inside a portal — `screen` still finds it).
    const cancelBtn = await screen.findByRole('button', { name: 'common.cancel' })
    fireEvent.click(cancelBtn)
    expect(deleteHandles.calledWith).toBeUndefined()
  })

  it('AC-4 dialog confirm fires deleteRecord(designerId, id) → onSuccess toasts + navigates back', async () => {
    permissionHandles.canDelete = true
    renderWithClient(<RecordDetailPage designerId="my-form" recordId="rec-1" />)

    fireEvent.click(screen.getByRole('button', { name: 'data.entry.delete' }))
    // Wait for the portal-rendered confirm button to appear, then click the
    // last "Delete" button (header + dialog confirm — the confirm is last).
    await screen.findByRole('button', { name: 'common.cancel' })
    const buttons = screen.getAllByRole('button', { name: 'data.entry.delete' })
    fireEvent.click(buttons[buttons.length - 1])

    await waitFor(() => {
      expect(deleteHandles.calledWith).toEqual({ designerId: 'my-form', id: 'rec-1' })
    })
    await waitFor(() => {
      expect(toastSuccess).toHaveBeenCalledWith('data.entry.deleteSuccess')
    })
    expect(navigateMock).toHaveBeenCalledWith({
      to: '/data/$designerId',
      params: { designerId: 'my-form' },
      search: { page: 1, pageSize: 25 },
    })
  })

  it('AC-4 AR-49: onMutate removes record from list cache before server responds', async () => {
    permissionHandles.canDelete = true
    deleteHandles.deferred = true

    // Create a QueryClient and pre-seed the list cache with the target record.
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    })
    const listKey = ['data', 'my-form', 'list']
    const listData = {
      data: [{ id: 'rec-1', createdAt: '', updatedAt: '', createdBy: null, updatedBy: null, isDeleted: false, cascadeEventId: null }],
      total: 1,
      page: 1,
      pageSize: 25,
      totalPages: 1,
    }
    client.setQueryData(listKey, listData)

    const { unmount } = render(
      <QueryClientProvider client={client}>
        <RecordDetailPage designerId="my-form" recordId="rec-1" />
      </QueryClientProvider>,
    )

    // Open dialog and click the confirm Delete button.
    fireEvent.click(screen.getByRole('button', { name: 'data.entry.delete' }))
    await screen.findByRole('button', { name: 'common.cancel' })
    const btns = screen.getAllByRole('button', { name: 'data.entry.delete' })
    fireEvent.click(btns[btns.length - 1])

    // Wait until deleteRecord has been called (onMutate + mutationFn fired).
    await waitFor(() => expect(deleteHandles.calledWith).toBeDefined())

    // The list cache should reflect the optimistic removal BEFORE the server
    // responds (the deferred promise is still pending).
    const cached = client.getQueriesData<typeof listData>({ queryKey: listKey })
    expect(cached.length).toBeGreaterThan(0)
    const [, cachedData] = cached[0]
    expect(cachedData?.data.find((r) => r.id === 'rec-1')).toBeUndefined()
    expect(cachedData?.total).toBe(0)

    // Resolve the deferred delete so the mutation completes cleanly.
    deleteHandles.resolveDeferred({ id: 'rec-1', isDeleted: true })
    unmount()
  })

  it('Story 6.11 AC-1: loading state renders Skeleton form, not common.loading text', () => {
    recordHandles.isLoading = true
    recordHandles.data = undefined
    renderWithClient(<RecordDetailPage designerId="my-form" recordId="rec-1" />)
    expect(screen.queryByText('common.loading')).toBeNull()
    expect(document.querySelectorAll('.animate-pulse').length).toBeGreaterThan(0)
    expect(screen.queryByTestId('mock-dynamic-component')).toBeNull()
  })

  it('Story 6.11 AC-4: recordQuery.isError renders ErrorBanner (role=alert) — no toast, no navigate', () => {
    recordHandles.isError = true
    recordHandles.data = undefined
    recordHandles.error = new ApiError(
      500,
      'INTERNAL',
      'errors.genericError',
      undefined,
      undefined,
      'cid-detail-001',
    )
    renderWithClient(<RecordDetailPage designerId="my-form" recordId="rec-1" />)
    expect(screen.getByRole('alert')).toBeTruthy()
    // The Story 6.9 effect that fired toast.error + navigate is gone.
    expect(toastError).not.toHaveBeenCalled()
    expect(navigateMock).not.toHaveBeenCalled()
    // DynamicComponent must not mount with undefined initialData.
    expect(screen.queryByTestId('mock-dynamic-component')).toBeNull()
  })

  it('Story 6.11 AC-4: Retry button calls recordQuery.refetch and renders the correlationId line', () => {
    recordHandles.isError = true
    recordHandles.data = undefined
    recordHandles.error = new ApiError(
      500,
      'INTERNAL',
      'errors.genericError',
      undefined,
      undefined,
      'cid-detail-002',
    )
    const refetchSpy = vi.fn()
    recordHandles.refetch = refetchSpy
    renderWithClient(<RecordDetailPage designerId="my-form" recordId="rec-1" />)
    // The identity translator in this file only replaces `{{token}}` literals
    // present in the key — `errors.correlationId` has no literal, so the mock
    // returns just the key. The presence of the rendered correlationId line
    // proves the banner picked up the ApiError.correlationId and rendered the
    // dedicated line for it (the line is omitted entirely when correlationId
    // is undefined — see ErrorBanner.tsx).
    expect(screen.getByText('errors.correlationId')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'errors.retry' }))
    expect(refetchSpy).toHaveBeenCalledTimes(1)
  })

  it('Story 6.11 AC-3: update mutation onError with ApiError toasts the server messageKey', () => {
    permissionHandles.canUpdate = true
    const payload = { name: 'Bad' }
    dynamicHandles.submit.fn = () => dynamicHandles.onSaveProp.fn?.(payload)

    renderWithClient(<RecordDetailPage designerId="my-form" recordId="rec-1" />)
    fireEvent.click(screen.getByTestId('mock-set-ready'))
    fireEvent.click(screen.getByTestId('mock-set-valid'))
    fireEvent.click(screen.getByRole('button', { name: 'data.entry.save' }))

    const [, options] = updateMutationHandles.mutate.mock.calls[0] as [
      { id: string; payload: Record<string, unknown> },
      { onSuccess: () => void; onError: (err: unknown) => void },
    ]
    options.onError(
      new ApiError(
        422,
        'VALIDATION_FAILED',
        'errors.validationFailed',
        undefined,
        undefined,
        'cid-detail-003',
      ),
    )
    expect(toastError).toHaveBeenCalledWith('errors.validationFailed')
  })

  it('Story 6.11 AC-5: update mutation 422 with fieldErrors flows into DynamicComponent.serverErrors', async () => {
    permissionHandles.canUpdate = true
    const payload = { title: '' }
    dynamicHandles.submit.fn = () => dynamicHandles.onSaveProp.fn?.(payload)

    renderWithClient(<RecordDetailPage designerId="my-form" recordId="rec-1" />)
    fireEvent.click(screen.getByTestId('mock-set-ready'))
    fireEvent.click(screen.getByTestId('mock-set-valid'))
    fireEvent.click(screen.getByRole('button', { name: 'data.entry.save' }))

    expect(dynamicHandles.lastServerErrors).toBeUndefined()

    const [, options] = updateMutationHandles.mutate.mock.calls[0] as [
      { id: string; payload: Record<string, unknown> },
      { onSuccess: () => void; onError: (err: unknown) => void },
    ]
    options.onError(
      new ApiError(
        422,
        'VALIDATION_FAILED',
        'errors.validationFailed',
        undefined,
        { title: ['Title is required.'] },
        'cid-detail-004',
      ),
    )
    await waitFor(() => {
      expect(dynamicHandles.lastServerErrors).toEqual({ title: 'Title is required.' })
    })
  })

  it('Story 6.11 AC-3: delete mutation onError with ApiError toasts the server messageKey', async () => {
    permissionHandles.canDelete = true
    deleteHandles.rejectWith = new ApiError(
      409,
      'CONFLICT',
      'errors.deleteConflict',
      undefined,
      undefined,
      'cid-detail-005',
    )

    renderWithClient(<RecordDetailPage designerId="my-form" recordId="rec-1" />)
    fireEvent.click(screen.getByRole('button', { name: 'data.entry.delete' }))
    await screen.findByRole('button', { name: 'common.cancel' })
    const btns = screen.getAllByRole('button', { name: 'data.entry.delete' })
    fireEvent.click(btns[btns.length - 1])

    await waitFor(() => {
      expect(toastError).toHaveBeenCalledWith('errors.deleteConflict')
    })
  })
})
