import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { type GetDatasetAuditLogParams, getDatasetAuditLog } from './datasetAuditApi'

// Story 8.9 (FR-61 / AR-69) — dataset audit entries are append-only. 30 s staleTime
// avoids server spam on focus refetch while still surfacing new entries within a
// session. keepPreviousData holds the prior page's rows during page transitions.
// Query key matches AR-69: ['datasets', 'audit', { page, datasetName?, operation? }].
export function useDatasetAuditLogQuery(params: GetDatasetAuditLogParams) {
  return useQuery({
    queryKey: [
      'datasets',
      'audit',
      { page: params.page, datasetName: params.datasetName, operation: params.operation },
    ],
    queryFn: () => getDatasetAuditLog(params),
    staleTime: 30_000,
    placeholderData: keepPreviousData,
  })
}
