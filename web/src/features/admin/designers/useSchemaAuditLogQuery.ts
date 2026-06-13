import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { getSchemaAuditLog } from './designerAuditApi'

// Story 5.7 — audit rows are append-only and immutable. 30 s staleTime keeps
// focus refetches from spamming the server while still surfacing new entries
// within a working session. keepPreviousData holds the prior page's rows during
// transitions so the table doesn't flash empty between page clicks.
export function useSchemaAuditLogQuery(
  designerId: string,
  page: number,
  pageSize: number,
) {
  return useQuery({
    queryKey: ['admin', 'designers', designerId, 'audit', page, pageSize],
    queryFn: () => getSchemaAuditLog(designerId, page, pageSize),
    staleTime: 30_000,
    placeholderData: keepPreviousData,
  })
}
