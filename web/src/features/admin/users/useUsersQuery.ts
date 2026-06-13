import { useQuery } from '@tanstack/react-query'
import { httpClient } from '../../auth/httpClient'
import type { PagedResult, UserListItem } from './types'

export const USERS_QUERY_KEY = ['admin', 'users'] as const

export interface UsersQueryOpts {
  sort?: string
  search?: string
  status?: string
}

export function useUsersQuery(page: number, pageSize: number, opts?: UsersQueryOpts) {
  return useQuery({
    queryKey: [...USERS_QUERY_KEY, page, pageSize, opts?.sort, opts?.search, opts?.status] as const,
    queryFn: () => {
      const sp = new URLSearchParams()
      sp.set('page', String(page))
      sp.set('pageSize', String(pageSize))
      if (opts?.sort) sp.set('sort', opts.sort)
      if (opts?.search) sp.set('search', opts.search)
      if (opts?.status) sp.set('status', opts.status)
      return httpClient.get<PagedResult<UserListItem>>(`/api/admin/users?${sp.toString()}`)
    },
    // Admin-list table reads are cheap and pagination changes are user-driven —
    // keep the default staleness; placeholderData keeps the previous page on
    // screen while the next one loads.
    placeholderData: (previous) => previous,
  })
}
