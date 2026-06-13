import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ApiError } from '@/lib/api/apiError'

// react-i18next: identity translator (`t(key)` returns the key). For interpolated
// values we serialize the values object so assertions can match on `version`.
vi.mock('react-i18next', () => ({
  useTranslation: () => ({
    t: (key: string, values?: Record<string, unknown>) =>
      values ? `${key}:${JSON.stringify(values)}` : key,
  }),
}))

// TanStack Router: stub Link and useNavigate so the components can render
// outside a real router context.
const navigateMock = vi.fn()
vi.mock('@tanstack/react-router', async () => {
  const actual =
    await vi.importActual<typeof import('@tanstack/react-router')>(
      '@tanstack/react-router',
    )
  return {
    ...actual,
    useNavigate: () => navigateMock,
    Link: ({ children, ...rest }: { children: React.ReactNode }) => (
      <a {...rest}>{children}</a>
    ),
  }
})

// sonner toast: record success/error calls for assertion.
const toastSuccess = vi.fn()
const toastError = vi.fn()
vi.mock('sonner', () => ({
  toast: {
    success: (...args: unknown[]) => toastSuccess(...args),
    error: (...args: unknown[]) => toastError(...args),
  },
}))

// designerApi: control responses per test.
const getSchemaMock = vi.fn()
const createVersionMock = vi.fn()
const duplicateSchemaMock = vi.fn()
const publishVersionMock = vi.fn()
const archiveVersionMock = vi.fn()
vi.mock('@/features/designer/designerApi', () => ({
  designerApi: {
    getSchema: (...args: unknown[]) => getSchemaMock(...args),
    createVersion: (...args: unknown[]) => createVersionMock(...args),
    duplicateSchema: (...args: unknown[]) => duplicateSchemaMock(...args),
    publishVersion: (...args: unknown[]) => publishVersionMock(...args),
    archiveVersion: (...args: unknown[]) => archiveVersionMock(...args),
  },
}))

// ComponentPreviewModal pulls in DynamicComponent which has heavy deps; stub
// it because the library tests never expand the preview affordance.
vi.mock('@/components/designer/ComponentPreviewModal', () => ({
  default: () => null,
}))

// Import AFTER mocks so the components see the stubbed modules.
import { RowMenu, VersionFlyout } from './designer.library'
import type { ComponentSchemaListItem } from '@/types/designer'

function makeQC() {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } })
}

function makeRow(
  overrides: Partial<ComponentSchemaListItem> = {},
): ComponentSchemaListItem {
  return {
    designerId: 'demo',
    displayName: 'Demo',
    mode: 'CRUD',
    status: 'Draft',
    latestVersion: 2,
    createdAt: '2026-05-01T00:00:00Z',
    updatedAt: '2026-05-10T00:00:00Z',
    creatorDisplayName: 'Alice',
    ...overrides,
  }
}

beforeEach(() => {
  navigateMock.mockReset()
  toastSuccess.mockReset()
  toastError.mockReset()
  getSchemaMock.mockReset()
  createVersionMock.mockReset()
  duplicateSchemaMock.mockReset()
  publishVersionMock.mockReset()
  archiveVersionMock.mockReset()
})

// RTL's auto-cleanup only fires when vitest globals are enabled; this project
// runs without them, so cleanup must be explicit between tests.
afterEach(() => {
  cleanup()
})

describe('VersionFlyout', () => {
  it('renders an error message when the schema query fails', async () => {
    getSchemaMock.mockRejectedValue(new Error('boom'))

    render(
      <QueryClientProvider client={makeQC()}>
        <VersionFlyout designerId="demo" latestVersion={3} />
      </QueryClientProvider>,
    )

    fireEvent.click(screen.getByRole('button', { name: 'designer.library.versionHistory' }))

    await waitFor(() => {
      expect(screen.getByRole('alert').textContent).toBe(
        'designer.library.versionHistoryError',
      )
    })
    // Must NOT fall through to the empty-state message.
    expect(screen.queryByText('designer.library.versionHistoryEmpty')).toBeNull()
  })

  it('lazy-loads and renders the version list with status chips on expand', async () => {
    getSchemaMock.mockResolvedValue({
      designerId: 'demo',
      displayName: 'Demo',
      status: 'Published',
      latestVersion: 3,
      rootElement: null,
      createdAt: '2026-05-01T00:00:00Z',
      updatedAt: '2026-05-10T00:00:00Z',
      publishedAt: '2026-05-08T00:00:00Z',
      versions: [
        { version: 1, status: 'Archived', createdAt: '2026-05-01T00:00:00Z' },
        { version: 2, status: 'Archived', createdAt: '2026-05-05T00:00:00Z' },
        { version: 3, status: 'Published', createdAt: '2026-05-08T00:00:00Z' },
      ],
    })

    render(
      <QueryClientProvider client={makeQC()}>
        <VersionFlyout designerId="demo" latestVersion={3} />
      </QueryClientProvider>,
    )

    // Lazy: no fetch until the trigger is clicked.
    expect(getSchemaMock).not.toHaveBeenCalled()

    fireEvent.click(screen.getByRole('button', { name: 'designer.library.versionHistory' }))

    await waitFor(() => expect(getSchemaMock).toHaveBeenCalledWith('demo'))

    // All three versions render, newest first.
    const options = await screen.findAllByRole('option')
    expect(options).toHaveLength(3)
    expect(options[0].textContent).toContain('v3')
    expect(options[1].textContent).toContain('v2')
    expect(options[2].textContent).toContain('v1')

    // Status chips render their localized status labels for each row.
    expect(options[0].textContent).toContain('designer.library.statusPublished')
    expect(options[1].textContent).toContain('designer.library.statusArchived')
    expect(options[2].textContent).toContain('designer.library.statusArchived')
  })
})

describe('RowMenu — New Version', () => {
  it('on success: invalidates list, toasts success, navigates to canvas', async () => {
    const row = makeRow({ designerId: 'demo', latestVersion: 2 })
    getSchemaMock.mockResolvedValue({
      designerId: 'demo',
      rootElement: { id: 'root', type: 'Stack', properties: {}, children: [] },
    })
    createVersionMock.mockResolvedValue({
      designerId: 'demo',
      latestVersion: 3,
    })
    const qc = makeQC()
    const invalidateSpy = vi.spyOn(qc, 'invalidateQueries')

    render(
      <QueryClientProvider client={qc}>
        <RowMenu row={row} qc={qc} />
      </QueryClientProvider>,
    )

    fireEvent.click(screen.getByRole('button', { name: 'designer.library.rowMenuLabel' }))
    fireEvent.click(screen.getByRole('menuitem', { name: /designer.library.newVersion/ }))
    // Dialog opens with "Create v{latestVersion+1}" preview.
    expect(screen.getByText(/designer\.library\.newVersionLabel.*"version":3/)).toBeTruthy()

    // Click Save to fire the mutation chain (getSchema → createVersion).
    fireEvent.click(screen.getByRole('button', { name: 'common.save' }))

    await waitFor(() => {
      expect(getSchemaMock).toHaveBeenCalledWith('demo')
      expect(createVersionMock).toHaveBeenCalledWith('demo', {
        id: 'root',
        type: 'Stack',
        properties: {},
        children: [],
      })
    })

    await waitFor(() => {
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['designer', 'list'] })
      // Also invalidates the per-designer schema cache so the VersionFlyout
      // picks up the new version immediately (rather than waiting for its
      // 30s staleTime to expire).
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['designer', 'schema', 'demo'] })
      // Success toast carries the interpolated new version number.
      expect(toastSuccess).toHaveBeenCalledWith(
        expect.stringMatching(/designer\.library\.newVersionSuccess.*"version":3/),
      )
      expect(navigateMock).toHaveBeenCalledWith({
        to: '/designer/$designerId',
        params: { designerId: 'demo' },
        // New version opens at its own version axis so the canvas edits the
        // freshly created snapshot, not whatever the latest happens to be.
        search: { version: 3 },
      })
    })
  })

  it('on error: surfaces a generic error toast and closes the dialog', async () => {
    const row = makeRow({ designerId: 'demo', latestVersion: 2 })
    // Fail on the createVersion step (post-getSchema) — emulates a 422
    // FIELD_KEY_INVALID returning when the existing schema has bad keys.
    getSchemaMock.mockResolvedValue({
      designerId: 'demo',
      rootElement: { id: 'root', type: 'Stack', properties: {}, children: [] },
    })
    createVersionMock.mockRejectedValue(
      new ApiError(422, 'FIELD_KEY_INVALID', 'designers.fieldKeyInvalid'),
    )
    const qc = makeQC()

    render(
      <QueryClientProvider client={qc}>
        <RowMenu row={row} qc={qc} />
      </QueryClientProvider>,
    )

    fireEvent.click(screen.getByRole('button', { name: 'designer.library.rowMenuLabel' }))
    fireEvent.click(screen.getByRole('menuitem', { name: /designer.library.newVersion/ }))
    fireEvent.click(screen.getByRole('button', { name: 'common.save' }))

    await waitFor(() => {
      // ApiError.messageKey wins: the error toast renders the backend's i18n
      // key, not the route-specific fallback.
      expect(toastError).toHaveBeenCalledWith('designers.fieldKeyInvalid')
    })
  })
})

describe('RowMenu — Publish a specific version', () => {
  it('publishes the chosen version and refetches the version list', async () => {
    const row = makeRow({ designerId: 'demo', latestVersion: 2, status: 'Draft' })
    // Menu open lazy-loads versions: v2 Published (not publishable → "—"),
    // v1 Draft (publishable).
    getSchemaMock.mockResolvedValue({
      designerId: 'demo',
      displayName: 'Demo',
      status: 'Published',
      latestVersion: 2,
      rootElement: null,
      createdAt: '2026-05-01T00:00:00Z',
      updatedAt: '2026-05-10T00:00:00Z',
      publishedAt: '2026-05-08T00:00:00Z',
      versions: [
        { version: 1, status: 'Draft', createdAt: '2026-05-01T00:00:00Z' },
        { version: 2, status: 'Published', createdAt: '2026-05-08T00:00:00Z' },
      ],
    })
    publishVersionMock.mockResolvedValue({ designerId: 'demo', latestVersion: 1 })
    const qc = makeQC()
    const invalidateSpy = vi.spyOn(qc, 'invalidateQueries')

    render(
      <QueryClientProvider client={qc}>
        <RowMenu row={row} qc={qc} />
      </QueryClientProvider>,
    )

    fireEvent.click(screen.getByRole('button', { name: 'designer.library.rowMenuLabel' }))

    // Only the Draft version exposes a Publish button; the Published one shows "—".
    const publishV1 = await screen.findByLabelText(
      'designer.library.publishVersionLabel:{"version":1}',
    )
    fireEvent.click(publishV1)

    await waitFor(() => {
      expect(publishVersionMock).toHaveBeenCalledWith('demo', 1)
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['designer', 'list'] })
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['designer', 'schema', 'demo'] })
      expect(toastSuccess).toHaveBeenCalledWith('designer.library.publishSuccess')
    })
  })
})

describe('RowMenu — Duplicate error', () => {
  it('uses ApiError.messageKey when present', async () => {
    const row = makeRow({ designerId: 'demo' })
    duplicateSchemaMock.mockRejectedValue(
      new ApiError(409, 'DUPLICATE_CONFLICT', 'designers.duplicateConflict'),
    )
    const qc = makeQC()

    render(
      <QueryClientProvider client={qc}>
        <RowMenu row={row} qc={qc} />
      </QueryClientProvider>,
    )

    fireEvent.click(screen.getByRole('button', { name: 'designer.library.rowMenuLabel' }))
    fireEvent.click(screen.getByRole('menuitem', { name: /designer.library.duplicate/ }))

    await waitFor(() => {
      expect(toastError).toHaveBeenCalledWith('designers.duplicateConflict')
    })
  })

  it('falls back to route key when error is a plain Error (no messageKey)', async () => {
    const row = makeRow({ designerId: 'demo' })
    duplicateSchemaMock.mockRejectedValue(new Error('network down'))
    const qc = makeQC()

    render(
      <QueryClientProvider client={qc}>
        <RowMenu row={row} qc={qc} />
      </QueryClientProvider>,
    )

    fireEvent.click(screen.getByRole('button', { name: 'designer.library.rowMenuLabel' }))
    fireEvent.click(screen.getByRole('menuitem', { name: /designer.library.duplicate/ }))

    await waitFor(() => {
      expect(toastError).toHaveBeenCalledWith('designer.library.duplicateError')
    })
  })
})
