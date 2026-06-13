import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { listRecords } from './recordListApi'
import type { DynamicRecord, RecordListParams, RecordPagedResult } from './recordListApi'

// Story 6.1 — TanStack Query hook for the dynamic record list. AR-48 queryKey
// convention is `['data', designerId, 'list', params]` so future stories can
// invalidate every list query for a designer with one call:
//
//   queryClient.invalidateQueries({ queryKey: ['data', designerId] })
//
// AR-49: staleTime is 0 because record data is mutable (unlike the audit log,
// which is append-only). placeholderData keeps the previous page on screen
// during transitions so the table doesn't flash empty between page clicks.
export function useRecordList(designerId: string, params: RecordListParams) {
  return useQuery({
    queryKey: ['data', designerId, 'list', params] as const,
    queryFn: () => listRecords(designerId, params),
    staleTime: 0,
    placeholderData: keepPreviousData,
    enabled: !!designerId,
  })
}

export type { DynamicRecord, RecordListParams, RecordPagedResult }
