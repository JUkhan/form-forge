import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import type { ReactNode } from 'react'

vi.mock('react-i18next', () => ({
  useTranslation: () => ({
    t: (key: string, vars?: Record<string, unknown>) =>
      vars ? `${key}:${JSON.stringify(vars)}` : key,
  }),
}))

const mockUseLocation = vi.fn<() => { pathname: string }>(() => ({ pathname: '/admin/users' }))

// vi.hoisted ensures this is initialised before vi.mock factories run, so the
// createFileRoute factory below can safely write into it.
const routeCapture = vi.hoisted(() => ({
  beforeLoad: undefined as
    | undefined
    | ((ctx: {
        context: { queryClient: unknown }
        location?: { pathname: string }
      }) => Promise<void>),
}))

vi.mock('@tanstack/react-router', () => ({
  useLocation: () => mockUseLocation(),
  Link: ({ to, children, ...rest }: { to: string; children: ReactNode } & Record<string, unknown>) => (
    <a href={to} {...rest}>
      {children}
    </a>
  ),
  Outlet: () => null,
  createFileRoute: () => (options: { beforeLoad?: (ctx: unknown) => Promise<void> }) => {
    routeCapture.beforeLoad = options.beforeLoad as typeof routeCapture.beforeLoad
    return {}
  },
  redirect: vi.fn().mockImplementation((args: unknown) => args),
}))

// AdminLayout reads the signed-in user's role set via usePermissionsQuery; stub it so the
// layout can render without a QueryClientProvider. Everything else in the module is real.
const permissionsHandle = vi.hoisted(() => ({
  data: undefined as undefined | { isActive: boolean; roleIds: string[] },
}))
vi.mock('../../../features/auth/usePermissionsQuery', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../features/auth/usePermissionsQuery')>()),
  usePermissionsQuery: () => ({ data: permissionsHandle.data }),
}))

// Imports must come AFTER vi.mock so the module binds the stubs.
import { AdminBreadcrumb, AdminLayout } from '../admin'
import { redirect } from '@tanstack/react-router'

// Seeded constants from usePermissionsQuery.ts
const PLATFORM_ADMIN_ROLE_ID = '00000000-0000-0000-0000-000000000001'
const PLATFORM_DEV_ROLE_ID = '00000000-0000-0000-0000-000000000003'

function renderTabs(roleIds: string[]) {
  permissionsHandle.data = { isActive: true, roleIds }
  const { container } = render(<AdminLayout />)
  const adminNav = container.querySelector('nav[aria-label="admin"]')
  expect(adminNav).not.toBeNull()
  return Array.from(adminNav!.querySelectorAll('a')).map((a) => ({
    text: a.textContent,
    href: a.getAttribute('href'),
  }))
}

describe('AdminLayout', () => {
  afterEach(() => {
    cleanup()
    permissionsHandle.data = undefined
    mockUseLocation.mockReturnValue({ pathname: '/admin/users' })
  })

  it('platform-admin sees exactly Users, Roles, Menus, Audit Logs', () => {
    expect(renderTabs([PLATFORM_ADMIN_ROLE_ID])).toEqual([
      { text: 'admin.users.title', href: '/admin/users' },
      { text: 'admin.roles.title', href: '/admin/roles' },
      { text: 'admin.menus.title', href: '/admin/menus' },
      { text: 'admin.audit.navTitle', href: '/admin/audit' },
    ])
  })

  it('platform-dev sees exactly Roles, Menus, Datasets, Constraints, Table Provisioning, Component Library', () => {
    expect(renderTabs([PLATFORM_DEV_ROLE_ID])).toEqual([
      { text: 'admin.roles.title', href: '/admin/roles' },
      { text: 'admin.menus.title', href: '/admin/menus' },
      { text: 'admin.datasets.navTitle', href: '/admin/datasets' },
      { text: 'admin.constraints.navTitle', href: '/admin/constraints' },
      { text: 'admin.tableProvisioning.navTitle', href: '/admin/table-provisioning' },
      { text: 'designer.nav.library', href: '/designer/library' },
    ])
  })

  it('renders no tabs for a user with neither role', () => {
    expect(renderTabs(['00000000-0000-0000-0000-000000000002'])).toEqual([])
  })
})

describe('AdminBreadcrumb', () => {
  afterEach(() => {
    cleanup()
    mockUseLocation.mockReturnValue({ pathname: '/admin/users' })
  })

  it.each([
    ['/admin/users', 'admin.users.title'],
    ['/admin/roles', 'admin.roles.title'],
    ['/admin/menus', 'admin.menus.title'],
    ['/admin/datasets', 'admin.datasets.navTitle'],
    ['/admin/audit', 'admin.audit.navTitle'],
    ['/admin/designers/abc/drift', 'admin.designers.navTitle'],
    ['/admin/data/abc/audit', 'admin.data.navTitle'],
  ])('renders Settings root and current section label for %s', (pathname, expectedSectionKey) => {
    mockUseLocation.mockReturnValue({ pathname })
    render(<AdminBreadcrumb />)
    screen.getByText('admin.settings.breadcrumb')
    screen.getByText(expectedSectionKey)
  })

  it('renders breadcrumb nav with the configured aria-label', () => {
    const { container } = render(<AdminBreadcrumb />)
    const nav = container.querySelector('nav[aria-label="admin.settings.breadcrumbAriaLabel"]')
    expect(nav).not.toBeNull()
  })

  it('root item is an accessible link to /admin/users and current section has aria-current="page"', () => {
    mockUseLocation.mockReturnValue({ pathname: '/admin/roles' })
    render(<AdminBreadcrumb />)
    const rootLink = screen.getByText('admin.settings.breadcrumb')
    expect(rootLink.tagName).toBe('A')
    expect(rootLink.getAttribute('href')).toBe('/admin/users')
    const currentItem = screen.getByText('admin.roles.title')
    expect(currentItem.getAttribute('aria-current')).toBe('page')
  })

  it('separator <li aria-hidden> is present between root and current-section items', () => {
    mockUseLocation.mockReturnValue({ pathname: '/admin/roles' })
    const { container } = render(<AdminBreadcrumb />)
    const ol = container.querySelector('ol')
    expect(ol).not.toBeNull()
    const separator = ol!.querySelector('li[aria-hidden]')
    expect(separator).not.toBeNull()
    expect(separator!.textContent).toBe('›')
  })

  it('omits current-section item when pathname has no recognised section', () => {
    mockUseLocation.mockReturnValue({ pathname: '/admin/' })
    render(<AdminBreadcrumb />)
    screen.getByText('admin.settings.breadcrumb')
    expect(screen.queryByText(/admin\.(users|roles|menus|audit)\./)).toBeNull()
  })
})

describe('Admin route beforeLoad guard (AC-3)', () => {
  afterEach(() => {
    cleanup()
    vi.mocked(redirect).mockClear()
  })

  it('throws redirect to / when user does not have the platform-admin role', async () => {
    const mockQueryClient = {
      ensureQueryData: vi.fn().mockResolvedValue({
        isActive: true,
        roleIds: ['00000000-0000-0000-0000-000000000002'],
      }),
    }
    await expect(
      routeCapture.beforeLoad!({ context: { queryClient: mockQueryClient } })
    ).rejects.toBeDefined()
    expect(redirect).toHaveBeenCalledWith({ to: '/' })
  })

  it('allows access when user has the platform-admin role', async () => {
    const mockQueryClient = {
      ensureQueryData: vi.fn().mockResolvedValue({
        isActive: true,
        roleIds: [PLATFORM_ADMIN_ROLE_ID],
      }),
    }
    await expect(
      routeCapture.beforeLoad!({ context: { queryClient: mockQueryClient } })
    ).resolves.toBeUndefined()
    expect(redirect).not.toHaveBeenCalled()
  })

  it('allows a platform-dev into its own sections and bounces it off admin-only ones', async () => {
    const qc = {
      ensureQueryData: vi.fn().mockResolvedValue({
        isActive: true,
        roleIds: [PLATFORM_DEV_ROLE_ID],
      }),
    }
    await expect(
      routeCapture.beforeLoad!({ context: { queryClient: qc }, location: { pathname: '/admin/datasets' } }),
    ).resolves.toBeUndefined()
    expect(redirect).not.toHaveBeenCalled()

    await expect(
      routeCapture.beforeLoad!({ context: { queryClient: qc }, location: { pathname: '/admin/users' } }),
    ).rejects.toBeDefined()
    expect(redirect).toHaveBeenCalledWith({ to: '/admin/roles' })
  })

  it('bounces a platform-admin off dev-only sections', async () => {
    const qc = {
      ensureQueryData: vi.fn().mockResolvedValue({
        isActive: true,
        roleIds: [PLATFORM_ADMIN_ROLE_ID],
      }),
    }
    await expect(
      routeCapture.beforeLoad!({ context: { queryClient: qc }, location: { pathname: '/admin/datasets' } }),
    ).rejects.toBeDefined()
    expect(redirect).toHaveBeenCalledWith({ to: '/admin/users' })
  })

  it('denies an inactive platform-dev', async () => {
    const qc = {
      ensureQueryData: vi.fn().mockResolvedValue({
        isActive: false,
        roleIds: [PLATFORM_DEV_ROLE_ID],
      }),
    }
    await expect(
      routeCapture.beforeLoad!({ context: { queryClient: qc } }),
    ).rejects.toBeDefined()
    expect(redirect).toHaveBeenCalledWith({ to: '/' })
  })
})
