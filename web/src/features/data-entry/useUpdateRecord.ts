import { useMutation, useQueryClient } from '@tanstack/react-query'
import type { RecordPayloadWithChildren } from './createRecordApi'
import { updateRecord } from './updateRecordApi'
import type { DynamicRecord } from './recordListApi'

// Story 6.4 — TanStack Query mutation hook for PUT /api/data/{designerId}/{id}.
// AR-48 + AR-49: pessimistic mutation (await server confirmation) and broad
// invalidation of both the list root key and the single-record key so list /
// get views re-fetch the new state on success.
export function useUpdateRecord(designerId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ id, payload }: { id: string; payload: RecordPayloadWithChildren }) =>
      updateRecord(designerId, id, payload),
    onSuccess: (_updated: DynamicRecord, { id }) => {
      queryClient.invalidateQueries({ queryKey: ['data', designerId] })
      queryClient.invalidateQueries({ queryKey: ['data', designerId, 'record', id] })
    },
  })
}
