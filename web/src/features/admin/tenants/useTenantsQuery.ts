import { useQuery } from '@tanstack/react-query'
import { httpClient } from '../../auth/httpClient'
import type { PagedResult } from '../users/types'
import type { TenantListItem } from './types'

export const TENANTS_QUERY_KEY = ['admin', 'tenants', 'list'] as const

export function useTenantsQuery(page: number, pageSize: number) {
  return useQuery({
    queryKey: [...TENANTS_QUERY_KEY, page, pageSize] as const,
    queryFn: () => {
      const sp = new URLSearchParams()
      sp.set('page', String(page))
      sp.set('pageSize', String(pageSize))
      return httpClient.get<PagedResult<TenantListItem>>(`/api/admin/tenants?${sp.toString()}`)
    },
    placeholderData: (previous) => previous,
  })
}
