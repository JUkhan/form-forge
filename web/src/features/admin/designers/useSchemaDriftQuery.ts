import { useQuery } from '@tanstack/react-query'
import { getSchemaDrift } from './designerAdminApi'

// Story 5.6 — drift data is admin-curated and never auto-evolves on its own, so
// staleTime: 0 keeps focus refetches accurate without flooding the server. The
// view also exposes a manual Refresh button (AC-5) that calls refetch() directly.
export function useSchemaDriftQuery(designerId: string) {
  return useQuery({
    queryKey: ['admin', 'designers', designerId, 'drift'],
    queryFn: () => getSchemaDrift(designerId),
    staleTime: 0,
  })
}
