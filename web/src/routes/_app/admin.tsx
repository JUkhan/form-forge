import { createFileRoute, Link, Outlet, redirect, useLocation } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { Settings as SettingsIcon } from 'lucide-react'
import { httpClient } from '../../features/auth/httpClient'
import {
  PERMISSIONS_QUERY_KEY,
  PLATFORM_ADMIN_ROLE_ID,
  type PermissionsResponse,
} from '../../features/auth/usePermissionsQuery'

export const Route = createFileRoute('/_app/admin')({
  beforeLoad: async ({ context }) => {
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

    if (!data || !data.isActive || !data.roleIds.includes(PLATFORM_ADMIN_ROLE_ID)) {
      throw redirect({ to: '/' })
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

export function AdminLayout() {
  const { t } = useTranslation()
  return (
    <div className="space-y-6">
      <AdminBreadcrumb />
      <div className="border-b border-border">
        <nav aria-label="admin" className="-mb-px flex flex-wrap gap-x-6">
          <Link
            to="/admin/users"
            className={TAB_LINK_BASE_CLASS}
            activeProps={{ className: TAB_LINK_ACTIVE_CLASS }}
            inactiveProps={{ className: TAB_LINK_INACTIVE_CLASS }}
          >
            {t('admin.users.title')}
          </Link>
          <Link
            to="/admin/roles"
            className={TAB_LINK_BASE_CLASS}
            activeProps={{ className: TAB_LINK_ACTIVE_CLASS }}
            inactiveProps={{ className: TAB_LINK_INACTIVE_CLASS }}
          >
            {t('admin.roles.title')}
          </Link>
          <Link
            to="/admin/menus"
            className={TAB_LINK_BASE_CLASS}
            activeProps={{ className: TAB_LINK_ACTIVE_CLASS }}
            inactiveProps={{ className: TAB_LINK_INACTIVE_CLASS }}
          >
            {t('admin.menus.title')}
          </Link>
          <Link
            to="/admin/datasets"
            className={TAB_LINK_BASE_CLASS}
            activeProps={{ className: TAB_LINK_ACTIVE_CLASS }}
            inactiveProps={{ className: TAB_LINK_INACTIVE_CLASS }}
          >
            {t('admin.datasets.navTitle')}
          </Link>
          <Link
            to="/admin/constraints"
            className={TAB_LINK_BASE_CLASS}
            activeProps={{ className: TAB_LINK_ACTIVE_CLASS }}
            inactiveProps={{ className: TAB_LINK_INACTIVE_CLASS }}
          >
            {t('admin.constraints.navTitle')}
          </Link>
          <Link
            to="/admin/table-provisioning"
            className={TAB_LINK_BASE_CLASS}
            activeProps={{ className: TAB_LINK_ACTIVE_CLASS }}
            inactiveProps={{ className: TAB_LINK_INACTIVE_CLASS }}
          >
            {t('admin.tableProvisioning.navTitle')}
          </Link>
          <Link
            to="/designer/library"
            className={TAB_LINK_BASE_CLASS}
            activeProps={{ className: TAB_LINK_ACTIVE_CLASS }}
            inactiveProps={{ className: TAB_LINK_INACTIVE_CLASS }}
          >
            {t('designer.nav.library')}
          </Link>
          <Link
            to="/admin/audit"
            className={TAB_LINK_BASE_CLASS}
            activeProps={{ className: TAB_LINK_ACTIVE_CLASS }}
            inactiveProps={{ className: TAB_LINK_INACTIVE_CLASS }}
          >
            {t('admin.audit.navTitle')}
          </Link>
        </nav>
      </div>
      <Outlet />
    </div>
  )
}
