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
    | ((ctx: { context: { queryClient: unknown } }) => Promise<void>),
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

// Imports must come AFTER vi.mock so the module binds the stubs.
import { AdminBreadcrumb, AdminLayout } from '../admin'
import { redirect } from '@tanstack/react-router'

// PLATFORM_ADMIN_ROLE_ID = '00000000-0000-0000-0000-000000000001' (seeded constant from usePermissionsQuery.ts)
const PLATFORM_ADMIN_ROLE_ID = '00000000-0000-0000-0000-000000000001'

describe('AdminLayout', () => {
  afterEach(() => {
    cleanup()
    mockUseLocation.mockReturnValue({ pathname: '/admin/users' })
  })

  it('renders all sub-nav links: Users, Roles, Menus, Datasets, Constraints, Table Provisioning, Component Library, Audit Logs', () => {
    const { container } = render(<AdminLayout />)
    const adminNav = container.querySelector('nav[aria-label="admin"]')
    expect(adminNav).not.toBeNull()
    const links = adminNav!.querySelectorAll('a')
    expect(links.length).toBe(8)
    expect(links[0].textContent).toBe('admin.users.title')
    expect(links[0].getAttribute('href')).toBe('/admin/users')
    expect(links[1].textContent).toBe('admin.roles.title')
    expect(links[1].getAttribute('href')).toBe('/admin/roles')
    expect(links[2].textContent).toBe('admin.menus.title')
    expect(links[2].getAttribute('href')).toBe('/admin/menus')
    expect(links[3].textContent).toBe('admin.datasets.navTitle')
    expect(links[3].getAttribute('href')).toBe('/admin/datasets')
    expect(links[4].textContent).toBe('admin.constraints.navTitle')
    expect(links[4].getAttribute('href')).toBe('/admin/constraints')
    expect(links[5].textContent).toBe('admin.tableProvisioning.navTitle')
    expect(links[5].getAttribute('href')).toBe('/admin/table-provisioning')
    expect(links[6].textContent).toBe('designer.nav.library')
    expect(links[6].getAttribute('href')).toBe('/designer/library')
    expect(links[7].textContent).toBe('admin.audit.navTitle')
    expect(links[7].getAttribute('href')).toBe('/admin/audit')
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
})
