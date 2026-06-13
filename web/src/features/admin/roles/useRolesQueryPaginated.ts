import { useQuery } from '@tanstack/react-query'
import { httpClient } from '../../auth/httpClient'
import type { PagedResult } from '../users/types'
import type { RoleListItem } from './useRolesQuery'

// Distinct query key from ROLES_QUERY_KEY (the user-detail multi-select fetch)
// so that admin list invalidations do not clobber the unrelated pageSize=100
// fetch used elsewhere.
export const ROLES_PAGINATED_QUERY_KEY = ['admin', 'roles', 'list'] as const

export interface RolesQueryOpts {
  sort?: string
  search?: string
  system?: string
}

export function useRolesQueryPaginated(page: number, pageSize: number, opts?: RolesQueryOpts) {
  return useQuery({
    queryKey: [...ROLES_PAGINATED_QUERY_KEY, page, pageSize, opts?.sort, opts?.search, opts?.system] as const,
    queryFn: () => {
      const sp = new URLSearchParams()
      sp.set('page', String(page))
      sp.set('pageSize', String(pageSize))
      if (opts?.sort) sp.set('sort', opts.sort)
      if (opts?.search) sp.set('search', opts.search)
      if (opts?.system) sp.set('system', opts.system)
      return httpClient.get<PagedResult<RoleListItem>>(`/api/admin/roles?${sp.toString()}`)
    },
    placeholderData: (previous) => previous,
  })
}
