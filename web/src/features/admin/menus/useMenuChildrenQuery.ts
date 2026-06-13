import { useQuery } from '@tanstack/react-query'
import { httpClient } from '../../auth/httpClient'
import type { PagedResult } from '../users/types'
import type { MenuListItem } from '../../menu/types'
import { MENUS_ADMIN_QUERY_KEY } from './useMenusAdminQuery'

export function useMenuChildrenQuery(parentId: string, page: number, pageSize: number) {
  return useQuery({
    queryKey: [...MENUS_ADMIN_QUERY_KEY, 'children', parentId, page, pageSize] as const,
    queryFn: () =>
      httpClient.get<PagedResult<MenuListItem>>(
        `/api/admin/menus?page=${String(page)}&pageSize=${String(pageSize)}&parentId=${parentId}`,
      ),
    placeholderData: (previous) => previous,
  })
}
