import { useQuery } from '@tanstack/react-query'
import { httpClient } from '../auth/httpClient'
import type { BindingDiffResponse } from './types'

// Story 5.2 — preview the column diff between the current binding and a target
// version. Enabled only when both menuId and targetVersion are defined, so the
// admin opening the diff modal without choosing a version doesn't fire a 400.
// Pessimistic (no optimistic update — the diff is a server-derived calculation).
export function bindingDiffQueryKey(menuId: string, targetVersion: number) {
  return ['menus', 'admin', menuId, 'binding-diff', targetVersion] as const
}

export function useBindingDiff(menuId: string | null, targetVersion: number | null) {
  return useQuery({
    queryKey: bindingDiffQueryKey(menuId ?? '', targetVersion ?? 0),
    queryFn: () =>
      httpClient.get<BindingDiffResponse>(
        `/api/admin/menus/${menuId!}/binding-diff?targetVersion=${targetVersion!}`,
      ),
    enabled: Boolean(menuId) && targetVersion !== null && targetVersion > 0,
    staleTime: 0,
    refetchOnWindowFocus: false,
  })
}
