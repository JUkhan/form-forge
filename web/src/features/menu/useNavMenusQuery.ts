import { useQuery } from '@tanstack/react-query'
import { httpClient } from '../auth/httpClient'
import type { NavMenuItem } from './types'

// Story 4.7 — public navbar tree. Server filters by role intersection + isActive
// and serves a 5 s in-memory cached response (per-user key). Client mirrors the
// same staleTime so we don't refetch faster than the server can produce a fresh
// response. Pessimistic by default — drag-reorder is the only optimistic mutation
// in this codebase per architecture line 846.
export const NAV_MENUS_QUERY_KEY = ['menus', 'nav'] as const

export function useNavMenusQuery() {
  return useQuery({
    queryKey: NAV_MENUS_QUERY_KEY,
    queryFn: () => httpClient.get<NavMenuItem[]>('/api/menus'),
    staleTime: 5_000,
    refetchOnWindowFocus: false,
    retry: false,
  })
}
