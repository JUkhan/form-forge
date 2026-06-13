import { useQuery } from '@tanstack/react-query'
import { httpClient } from '../../auth/httpClient'
import type { RoleDetail } from './types'

export const ROLE_DETAIL_QUERY_KEY = ['admin', 'roles', 'detail'] as const

export function useRoleDetailQuery(roleId: string) {
  return useQuery({
    queryKey: [...ROLE_DETAIL_QUERY_KEY, roleId] as const,
    queryFn: () => httpClient.get<RoleDetail>(`/api/admin/roles/${roleId}`),
  })
}
