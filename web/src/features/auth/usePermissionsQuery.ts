import { useQuery } from '@tanstack/react-query'
import { httpClient } from './httpClient'

export interface CrudFlags {
  canCreate: boolean
  canRead: boolean
  canUpdate: boolean
  canDelete: boolean
  canExport: boolean
}

export interface PermissionsResponse {
  userId: string
  computedAt: string
  isActive: boolean
  perResource: Record<string, CrudFlags>
  roleIds: string[]
}

// Seeded platform-admin role id (FormForge.Api: SeedData.cs). A user with this
// role bypasses per-resource checks server-side at the RequirePermission filter
// (Story 2.6 AC-2), so the client must mirror that bypass — see usePermission.
export const PLATFORM_ADMIN_ROLE_ID = '00000000-0000-0000-0000-000000000001' as const

export const PERMISSIONS_QUERY_KEY = ['auth', 'permissions'] as const

export function usePermissionsQuery() {
  return useQuery({
    queryKey: PERMISSIONS_QUERY_KEY,
    queryFn: () => httpClient.get<PermissionsResponse>('/api/users/me/permissions'),
    staleTime: Infinity,
    refetchOnWindowFocus: false,
    retry: false,
  })
}
