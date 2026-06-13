import { PLATFORM_ADMIN_ROLE_ID, usePermissionsQuery } from './usePermissionsQuery'

export type CrudAction = 'canCreate' | 'canRead' | 'canUpdate' | 'canDelete' | 'canExport'

export function usePermission(resourceId: string, action: CrudAction): boolean {
  const { data } = usePermissionsQuery()
  // Default-deny while loading — never flash unauthorized content before the
  // first permissions response arrives.
  if (!data) return false
  // Story 2.8 AC-6: a deactivated user holding a still-valid JWT (within the
  // 15-min TTL window) must see nothing in the UI, even if they hold the
  // platform-admin role. This guard MUST run before the admin bypass below.
  if (data.isActive === false) return false
  // Server grants platform-admin all permissions at the filter level (Story 2.6
  // AC-2) and does not populate per_resource rows for it — mirror the bypass.
  if (data.roleIds.includes(PLATFORM_ADMIN_ROLE_ID)) return true
  return data.perResource[resourceId]?.[action] ?? false
}
