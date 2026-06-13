import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { getMutationAuditLog } from './mutationAuditApi'

// Story 6.8 — TanStack query hook for the paginated mutation audit log.
// `keepPreviousData` keeps the table on-screen while a new page loads so the
// pager doesn't flash empty between page transitions.
export function useMutationAuditLogQuery(
  designerId: string,
  page: number,
  pageSize: number,
) {
  return useQuery({
    queryKey: ['admin', 'data', designerId, 'audit', page, pageSize],
    queryFn: () => getMutationAuditLog(designerId, page, pageSize),
    staleTime: 30_000,
    placeholderData: keepPreviousData,
  })
}
