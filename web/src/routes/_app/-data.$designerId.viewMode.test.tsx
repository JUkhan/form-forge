import { describe, it, expect, vi, afterEach } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

// react-i18next — identity translator.
vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (key: string) => key }),
}))

// TanStack Router — provide controlled stubs for all hooks RouteComponent uses.
// createFileRoute must be mocked so the module-level Route constant can be
// constructed without a real router context.
vi.mock('@tanstack/react-router', () => ({
  createFileRoute: () => (_config: unknown) => ({
    useParams: () => ({ designerId: 'view-designer' }),
    useSearch: () => ({
      page: 1,
      pageSize: 25,
      sort: undefined,
      filter: undefined,
      showDeleted: undefined,
    }),
  }),
  useNavigate: () => () => {},
  useMatchRoute: () => () => null, // no child route active
  Outlet: () => <div data-testid="outlet" />,
}))

// designerApi — control the schema response per test.
const getSchemaMock = vi.fn()
vi.mock('@/features/designer/designerApi', () => ({
  designerApi: {
    getSchema: (...args: unknown[]) => getSchemaMock(...args),
  },
}))

// DynamicComponent — lightweight stub; renders a testid so VIEW-mode assertion
// can confirm it appears without triggering its own heavy dependency chain.
vi.mock('@/components/designer/DynamicComponent', () => ({
  default: ({ designerId }: { designerId: string }) => (
    <div data-testid="mock-dynamic-component" data-designer-id={designerId} />
  ),
}))

// RecordListPage — stub so its own deps (useRecordList, useDesignerFieldKeys, …)
// are never resolved. The VIEW-mode branch should never render this.
vi.mock('@/components/dataEntry/RecordListPage', () => ({
  RecordListPage: () => <div data-testid="record-list-page" />,
}))

// Import AFTER mocks so the route module picks up the stubbed dependencies.
import { RouteComponent } from '@/routes/_app/data.$designerId'
import type { ComponentSchemaDto } from '@/types/designer'

function makeQC() {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } })
}

function makeSchema(overrides: Partial<ComponentSchemaDto> = {}): ComponentSchemaDto {
  return {
    designerId: 'view-designer',
    displayName: 'View Designer',
    mode: 'VIEW',
    status: 'Published',
    latestVersion: 1,
    rootElement: null,
    createdAt: '2026-06-02T00:00:00Z',
    updatedAt: null,
    publishedAt: null,
    ...overrides,
  }
}

afterEach(() => {
  cleanup()
  getSchemaMock.mockReset()
})

describe('data.$designerId RouteComponent — VIEW mode (AC-5)', () => {
  it('renders DynamicComponent read-only when schema mode is VIEW', async () => {
    getSchemaMock.mockResolvedValue(makeSchema({ mode: 'VIEW' }))

    render(
      <QueryClientProvider client={makeQC()}>
        <RouteComponent />
      </QueryClientProvider>,
    )

    await waitFor(() => {
      expect(screen.getByTestId('mock-dynamic-component')).toBeInTheDocument()
    })
    // RecordListPage must NOT be rendered for VIEW-mode designers (AC-5).
    expect(screen.queryByTestId('record-list-page')).not.toBeInTheDocument()
    // The component title is the designer displayName.
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('View Designer')
  })

  it('renders RecordListPage for CRUD-mode designers', async () => {
    getSchemaMock.mockResolvedValue(makeSchema({ mode: 'CRUD', designerId: 'crud-designer' }))

    render(
      <QueryClientProvider client={makeQC()}>
        <RouteComponent />
      </QueryClientProvider>,
    )

    await waitFor(() => {
      expect(screen.getByTestId('record-list-page')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('mock-dynamic-component')).not.toBeInTheDocument()
  })

  it('shows loading state while schema is fetching', () => {
    // Never resolves — stays in loading state.
    getSchemaMock.mockReturnValue(new Promise(() => {}))

    render(
      <QueryClientProvider client={makeQC()}>
        <RouteComponent />
      </QueryClientProvider>,
    )

    expect(screen.getByText('common.loading')).toBeInTheDocument()
  })

  it('shows error state when schema fetch fails', async () => {
    getSchemaMock.mockRejectedValue(new Error('network error'))

    render(
      <QueryClientProvider client={makeQC()}>
        <RouteComponent />
      </QueryClientProvider>,
    )

    await waitFor(() => {
      expect(screen.getByText('errors.genericError')).toBeInTheDocument()
    })
  })
})
