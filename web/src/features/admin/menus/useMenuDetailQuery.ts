import { useQuery } from '@tanstack/react-query'
import { httpClient } from '../../auth/httpClient'
import type { MenuItem } from '../../menu/types'

export const MENU_DETAIL_QUERY_KEY = ['admin', 'menus', 'detail'] as const

export function useMenuDetailQuery(menuId: string) {
  return useQuery({
    queryKey: [...MENU_DETAIL_QUERY_KEY, menuId] as const,
    queryFn: () => httpClient.get<MenuItem>(`/api/admin/menus/${menuId}`),
    enabled: Boolean(menuId),
    // Story 5.2 — poll every 2 s while provisioning is in flight so the admin SPA
    // observes the Pending → Success | Error transition without a manual refresh.
    // When status leaves Pending (or there's no binding at all) the function
    // returns false and the interval stops automatically; cancellation cost is zero.
    refetchInterval: (query) =>
      query.state.data?.provisioningStatus === 'Pending' ? 2000 : false,
  })
}
