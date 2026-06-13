import { useQuery } from '@tanstack/react-query'
import { httpClient } from '../../auth/httpClient'
import type { PagedResult } from '../users/types'

export interface RoleListItem {
  id: string
  name: string
  description: string | null
  permissionCount: number
  isSystem: boolean
}

export const ROLES_QUERY_KEY = ['admin', 'roles'] as const

// Role catalog is admin-managed and small in v1, so fetching pageSize=100 in
// a single request is acceptable for the role multi-select on user detail.
// Story 2.9 introduces a paginated role-management UI that will need its own
// page-driven variant of this hook.
export function useRolesQuery() {
  return useQuery({
    queryKey: ROLES_QUERY_KEY,
    queryFn: () =>
      httpClient.get<PagedResult<RoleListItem>>('/api/admin/roles?page=1&pageSize=100'),
  })
}
