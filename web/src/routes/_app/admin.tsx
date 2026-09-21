import { createFileRoute, Link, Outlet, redirect, useLocation } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { Settings as SettingsIcon } from 'lucide-react'
import { httpClient } from '../../features/auth/httpClient'
import {
  PERMISSIONS_QUERY_KEY,
  type PermissionsResponse,
} from '../../features/auth/usePermissionsQuery'
import {
  getSettingsAccess,
  useSettingsAccess,
  type SettingsTab,
} from '../../features/auth/settingsAccess'

export const Route = createFileRoute('/_app/admin')({
  beforeLoad: async ({ context, location }) => {
    // _app.beforeLoad already ensured an authenticated session by the time this
    // runs. Permissions may or may not be in the cache yet — use ensureQueryData
    // so a cold boot fetches synchronously BEFORE the admin layout (and its
    // children, which fire admin-only API calls on mount) renders. Otherwise a
    // non-admin would see the admin chrome flash and the network tab leak a
    // GET /api/admin/users 403 before redirecting. (Story 2.8 review patch P11.)
    const data = await context.queryClient
      .ensureQueryData<PermissionsResponse>({
        queryKey: PERMISSIONS_QUERY_KEY,
        queryFn: () => httpClient.get<PermissionsResponse>('/api/users/me/permissions'),
        staleTime: Infinity,
      })
      .catch(() => null)

    const access = getSettingsAccess(data)
    if (!access.hasAccess) {
      throw redirect({ to: '/' })
    }

    // Per-role section guard: a platform-admin hitting /admin/datasets (or a platform-dev
    // hitting /admin/users or /admin/audit) is bounced to its own Settings home. The
    // server 403s the matching /api/admin/* routes regardless.
    const section = (location?.pathname ?? '').replace(/^\/admin\/?/, '').split('/')[0] ?? ''
    if (section && !access.sections.includes(section)) {
      throw redirect({ to: access.homePath })
    }
  },
  component: AdminLayout,
})

export function AdminBreadcrumb() {
  const { t } = useTranslation()
  const { pathname } = useLocation()

  const section = pathname.replace(/^\/admin\/?/, '').split('/')[0] ?? ''

  const sectionLabels: Record<string, string> = {
    users: t('admin.users.title'),
    roles: t('admin.roles.title'),
    menus: t('admin.menus.title'),
    audit: t('admin.audit.navTitle'),
    designers: t('admin.designers.navTitle'),
    data: t('admin.data.navTitle'),
    datasets: t('admin.datasets.navTitle'),
    constraints: t('admin.constraints.navTitle'),
    'table-provisioning': t('admin.tableProvisioning.navTitle'),
  }

  const sectionLabel = sectionLabels[section]

  return (
    <nav aria-label={t('admin.settings.breadcrumbAriaLabel')} className="text-sm">
      <ol className="flex items-center gap-1.5 text-muted-foreground">
        <li>
          <Link
            to="/admin/users"
            className="inline-flex items-center gap-1 text-muted-foreground hover:text-foreground"
          >
            <SettingsIcon className="h-3.5 w-3.5" />
            {t('admin.settings.breadcrumb')}
          </Link>
        </li>
        {sectionLabel && (
          <>
            {/* Literal '›' (not a ChevronRight icon) — admin-layout.test.tsx
                asserts separator.textContent === '›'. */}
            <li aria-hidden className="text-muted-foreground/50">{'›'}</li>
            <li aria-current="page" className="font-medium text-foreground">
              {sectionLabel}
            </li>
          </>
        )}
      </ol>
    </nav>
  )
}

// Only neutral, non-conflicting classes live in the always-applied base. The
// border-color and text-color classes are split into activeProps/inactiveProps
// so they're mutually exclusive — otherwise TanStack concatenates the base's
// `border-transparent` alongside the active `border-primary` and CSS source
// order (not class order) decides the winner, which silently hides the active
// underline.
const TAB_LINK_BASE_CLASS =
  'inline-flex items-center border-b-2 px-1 pb-3 pt-1 text-sm font-medium transition-colors'

const TAB_LINK_ACTIVE_CLASS = 'border-primary font-semibold text-foreground'

const TAB_LINK_INACTIVE_CLASS =
  'border-transparent text-muted-foreground hover:border-border hover:text-foreground'


const TAB_LINKS: Record<
  SettingsTab,
  {
    to:
      | '/admin/users'
      | '/admin/roles'
      | '/admin/menus'
      | '/admin/datasets'
      | '/admin/constraints'
      | '/admin/table-provisioning'
      | '/designer/library'
      | '/admin/audit'
    labelKey: string
  }
> = {
  users: { to: '/admin/users', labelKey: 'admin.users.title' },
  roles: { to: '/admin/roles', labelKey: 'admin.roles.title' },
  menus: { to: '/admin/menus', labelKey: 'admin.menus.title' },
  datasets: { to: '/admin/datasets', labelKey: 'admin.datasets.navTitle' },
  constraints: { to: '/admin/constraints', labelKey: 'admin.constraints.navTitle' },
  'table-provisioning': {
    to: '/admin/table-provisioning',
    labelKey: 'admin.tableProvisioning.navTitle',
  },
  library: { to: '/designer/library', labelKey: 'designer.nav.library' },
  audit: { to: '/admin/audit', labelKey: 'admin.audit.navTitle' },
}

export function AdminLayout() {
  const { t } = useTranslation()
  // Per-role tab set: platform-admin sees Users/Roles/Menus/Audit Logs; the hidden
  // platform-dev role sees Roles/Menus/Datasets/Constraints/Table Provisioning/Library.
  const { tabs } = useSettingsAccess()
  return (
    <div className="space-y-6">
      <AdminBreadcrumb />
      <div className="border-b border-border">
        <nav aria-label="admin" className="-mb-px flex flex-wrap gap-x-6">
          {tabs.map((tab) => (
            <Link
              key={tab}
              to={TAB_LINKS[tab].to}
              className={TAB_LINK_BASE_CLASS}
              activeProps={{ className: TAB_LINK_ACTIVE_CLASS }}
              inactiveProps={{ className: TAB_LINK_INACTIVE_CLASS }}
            >
              {t(TAB_LINKS[tab].labelKey)}
            </Link>
          ))}
        </nav>
      </div>
      <Outlet />
    </div>
  )
}
