import {
  PLATFORM_ADMIN_ROLE_ID,
  PLATFORM_DEV_ROLE_ID,
  usePermissionsQuery,
  type PermissionsResponse,
} from './usePermissionsQuery'

// Settings (gear icon) tab visibility is per role. This mirrors the server-side gating on
// the /api/admin/* route groups — hiding a tab here is a convenience, the server is what
// actually enforces it.
//   platform-admin : Users, Roles, Menus, Audit Logs
//   platform-dev   : Roles, Menus, Datasets, Constraints, Table Provisioning, Component Library
export type SettingsTab =
  | 'users'
  | 'roles'
  | 'menus'
  | 'datasets'
  | 'constraints'
  | 'table-provisioning'
  | 'library'
  | 'audit'

const ADMIN_TABS: readonly SettingsTab[] = ['users', 'roles', 'menus', 'audit']
const DEV_TABS: readonly SettingsTab[] = [
  'roles',
  'menus',
  'datasets',
  'constraints',
  'table-provisioning',
  'library',
]

// Every tab in canonical display order; the visible set is filtered from this.
export const SETTINGS_TAB_ORDER: readonly SettingsTab[] = [
  'users',
  'roles',
  'menus',
  'datasets',
  'constraints',
  'table-provisioning',
  'library',
  'audit',
]

// /admin/<section> route sections reachable per role (sub-pages such as data/designer
// audit and drift live under a section of their own rather than a tab).
const ADMIN_SECTIONS: readonly string[] = ['users', 'roles', 'menus', 'audit', 'data', 'designers']
const DEV_SECTIONS: readonly string[] = [
  'roles',
  'menus',
  'datasets',
  'constraints',
  'table-provisioning',
  'designers',
]

export interface SettingsAccess {
  isAdmin: boolean
  isDev: boolean
  hasAccess: boolean
  tabs: SettingsTab[]
  sections: string[]
  homePath: '/admin/users' | '/admin/roles'
}

export function getSettingsAccess(
  data: Pick<PermissionsResponse, 'isActive' | 'roleIds'> | null | undefined,
): SettingsAccess {
  const active = !!data && data.isActive !== false
  const isAdmin = active && data.roleIds.includes(PLATFORM_ADMIN_ROLE_ID)
  const isDev = active && data.roleIds.includes(PLATFORM_DEV_ROLE_ID)

  const allowedTabs = new Set<SettingsTab>([
    ...(isAdmin ? ADMIN_TABS : []),
    ...(isDev ? DEV_TABS : []),
  ])
  const sections = [
    ...new Set([...(isAdmin ? ADMIN_SECTIONS : []), ...(isDev ? DEV_SECTIONS : [])]),
  ]

  return {
    isAdmin,
    isDev,
    hasAccess: isAdmin || isDev,
    tabs: SETTINGS_TAB_ORDER.filter((tab) => allowedTabs.has(tab)),
    sections,
    homePath: isAdmin ? '/admin/users' : '/admin/roles',
  }
}

export function useSettingsAccess(): SettingsAccess {
  const { data } = usePermissionsQuery()
  return getSettingsAccess(data)
}
