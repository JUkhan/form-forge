import { useMutation, useQueryClient } from '@tanstack/react-query'
import { httpClient } from '../../auth/httpClient'
import type { CreateTenantRequest, CreateTenantResponse } from './types'
import { TENANTS_QUERY_KEY } from './useTenantsQuery'

// Story 12.5 — create-tenant is synchronous server-side (ProvisionSchemaAsync +
// OnboardTenantAsync have both already run, one way or another, by the time this
// resolves OR rejects — the endpoint's own catch block sets Tenant.Status='Error'
// on a mid-flow failure and the row still exists). So the list is invalidated
// unconditionally on settle, with a fixed-backoff bounded retry (mirrors
// useTableProvisioningQueries.ts's 1.5s/4s pattern) rather than an indefinite
// poll — this is a cache-freshness affordance, not a wait for an async job.
export function useCreateTenantMutation() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: CreateTenantRequest) =>
      httpClient.post<CreateTenantResponse>('/api/admin/tenants', body),
    onSettled: () => {
      const invalidate = () =>
        void queryClient.invalidateQueries({ queryKey: TENANTS_QUERY_KEY })
      invalidate()
      window.setTimeout(invalidate, 1_500)
      window.setTimeout(invalidate, 4_000)
    },
  })
}
