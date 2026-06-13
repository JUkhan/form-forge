import { describe, it, expect, vi, beforeEach } from 'vitest'
import { cleanup, renderHook } from '@testing-library/react'

const toastSuccess = vi.fn()
const toastError = vi.fn()

vi.mock('sonner', () => ({
  toast: {
    success: (msg: string) => toastSuccess(msg),
    error: (msg: string) => toastError(msg),
  },
}))

// Identity translator — i18n interpolation is verified via the key + vars payload.
vi.mock('react-i18next', () => ({
  useTranslation: () => ({
    t: (key: string, vars?: Record<string, unknown>) =>
      vars ? `${key}:${JSON.stringify(vars)}` : key,
  }),
}))

// Imports come AFTER vi.mock so the module binds the stubs.
import { usePollProvisioning } from '../usePollProvisioning'

// renderHook infers TProps from `initialProps`. With `{ status: 'Pending' as
// const }` the inferred TProps narrows to `{ status: 'Pending'; … }`, then a
// later `rerender({ status: 'Success', … })` fails the assignability check.
// Declare the wider props type once and cast initialProps to it.
type PollProps = {
  status: 'Pending' | 'Success' | 'Error' | null
  err: string | null
}

describe('usePollProvisioning', () => {
  beforeEach(() => {
    toastSuccess.mockClear()
    toastError.mockClear()
    cleanup()
  })

  it('does not toast on the initial Pending status', () => {
    // The hook's ref starts at the initial value, so the first effect sees
    // prev === current === 'Pending'. The undefined → Pending transition that
    // happens on a fresh bind must NOT fire a toast (no terminal flip yet).
    renderHook(() => {
      usePollProvisioning('Pending', null)
    })
    expect(toastSuccess).not.toHaveBeenCalled()
    expect(toastError).not.toHaveBeenCalled()
  })

  it('fires success toast on Pending → Success transition', () => {
    const { rerender } = renderHook(
      ({ status, err }: PollProps) => {
        usePollProvisioning(status, err)
      },
      { initialProps: { status: 'Pending', err: null } as PollProps },
    )

    rerender({ status: 'Success', err: null })

    expect(toastSuccess).toHaveBeenCalledTimes(1)
    expect(toastSuccess).toHaveBeenCalledWith('admin.menus.provisioningSuccess')
    expect(toastError).not.toHaveBeenCalled()
  })

  it('fires error toast on Pending → Error transition and interpolates the error message', () => {
    const { rerender } = renderHook(
      ({ status, err }: PollProps) => {
        usePollProvisioning(status, err)
      },
      { initialProps: { status: 'Pending', err: null } as PollProps },
    )

    rerender({ status: 'Error', err: 'CREATE TABLE failed: relation already exists' })

    expect(toastError).toHaveBeenCalledTimes(1)
    expect(toastError).toHaveBeenCalledWith(
      'admin.menus.provisioningError:{"error":"CREATE TABLE failed: relation already exists"}',
    )
    expect(toastSuccess).not.toHaveBeenCalled()
  })

  it('does not double-fire on Success → Success (idempotent re-renders)', () => {
    // After a successful provisioning, the detail query may still re-render
    // with the same Success status (cache refetch on focus, etc.). The ref
    // pattern is the regression guard against an extra toast on every render.
    const { rerender } = renderHook(
      ({ status, err }: PollProps) => {
        usePollProvisioning(status, err)
      },
      { initialProps: { status: 'Pending', err: null } as PollProps },
    )
    rerender({ status: 'Success', err: null })
    rerender({ status: 'Success', err: null })
    rerender({ status: 'Success', err: null })

    expect(toastSuccess).toHaveBeenCalledTimes(1)
  })
})
