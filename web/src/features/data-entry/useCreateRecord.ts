import { useMutation, useQueryClient } from '@tanstack/react-query'
import { createRecord, type RecordPayloadWithChildren } from './createRecordApi'
import type { DynamicRecord } from './recordListApi'

// Story 6.3 — TanStack Query mutation hook for POST /api/data/{designerId}.
// AR-48 + AR-49: pessimistic mutation (await server confirmation) and broad
// invalidation of the 'data', designerId queryKey root so list / get views
// re-fetch the new state. No optimistic update — server-generated id and
// audit row make rollback noisy for v1.
export function useCreateRecord(designerId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: (payload: RecordPayloadWithChildren) =>
      createRecord(designerId, payload),
    onSuccess: (created: DynamicRecord) => {
      queryClient.invalidateQueries({ queryKey: ['data', designerId] })
      return created
    },
  })
}
