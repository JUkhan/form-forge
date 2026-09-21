import { useQuery } from '@tanstack/react-query'
import { httpClient } from '../auth/httpClient'

export interface CurrentTenantResponse {
  name: string
}

export const CURRENT_TENANT_QUERY_KEY = ['tenant', 'current'] as const

// The tenant name only changes on tenant creation, so cache it for the session.
export function useCurrentTenantQuery() {
  return useQuery({
    queryKey: CURRENT_TENANT_QUERY_KEY,
    queryFn: () => httpClient.get<CurrentTenantResponse>('/api/users/me/tenant'),
    staleTime: Infinity,
    refetchOnWindowFocus: false,
    retry: false,
  })
}
