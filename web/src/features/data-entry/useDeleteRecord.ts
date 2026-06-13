import { useMutation, useQueryClient } from '@tanstack/react-query'
import { deleteRecord } from './deleteRecordApi'
import type { DynamicRecord } from './recordListApi'

// Story 6.5 — TanStack mutation hook for DELETE /api/data/{designerId}/{id}.
// AR-48 + AR-49: pessimistic mutation (await server confirmation). Invalidates
// the list root key and the single-record key on success so list / get views
// re-fetch. The soft-deleted row will reappear in lists only when isDeleted
// filtering allows it.
export function useDeleteRecord(designerId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: (id: string) => deleteRecord(designerId, id),
    onSuccess: (_deleted: DynamicRecord, id) => {
      queryClient.invalidateQueries({ queryKey: ['data', designerId] })
      queryClient.invalidateQueries({ queryKey: ['data', designerId, 'record', id] })
    },
  })
}
