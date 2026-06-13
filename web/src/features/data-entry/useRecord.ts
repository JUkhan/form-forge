import { useQuery } from '@tanstack/react-query'
import { getRecord } from './recordApi'
import type { RecordWithChildren } from './recordApi'

// Story 6.2 — TanStack Query hook for a single record. AR-48 queryKey convention
// matches useRecordList: `['data', designerId, 'record', id, ...]`. Including the
// `{ includeChildren }` discriminator keeps the with-children and without-children
// responses in separate cache entries so toggling the flag doesn't return stale data.
//
// AR-49: staleTime is 0 — record data is mutable (matches the list hook).
// No keepPreviousData; single-record fetch has no page-transition UX to preserve.
export function useRecord(
  designerId: string,
  id: string,
  includeChildren = false,
) {
  return useQuery({
    queryKey: ['data', designerId, 'record', id, { includeChildren }] as const,
    queryFn: () => getRecord(designerId, id, includeChildren),
    staleTime: 0,
    enabled: !!designerId && !!id,
  })
}

export type { RecordWithChildren }
