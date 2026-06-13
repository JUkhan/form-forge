import { useQuery } from '@tanstack/react-query'
import { httpClient } from '../../auth/httpClient'
import type { PagedResult } from '../users/types'
import type { MenuListItem } from '../../menu/types'

export const MENUS_ADMIN_QUERY_KEY = ['admin', 'menus'] as const

export interface MenusQueryOpts {
  sort?: string
  search?: string
  active?: string
}

export function useMenusAdminQuery(page: number, pageSize: number, opts?: MenusQueryOpts) {
  return useQuery({
    queryKey: [...MENUS_ADMIN_QUERY_KEY, page, pageSize, opts?.sort, opts?.search, opts?.active] as const,
    queryFn: () => {
      const sp = new URLSearchParams()
      sp.set('page', String(page))
      sp.set('pageSize', String(pageSize))
      if (opts?.sort) sp.set('sort', opts.sort)
      if (opts?.search) sp.set('search', opts.search)
      if (opts?.active) sp.set('active', opts.active)
      return httpClient.get<PagedResult<MenuListItem>>(`/api/admin/menus?${sp.toString()}`)
    },
    placeholderData: (previous) => previous,
  })
}
