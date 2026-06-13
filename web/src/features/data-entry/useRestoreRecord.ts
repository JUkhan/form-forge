import { useMutation, useQueryClient } from '@tanstack/react-query'
import { restoreRecord } from './restoreRecordApi'
import type { DynamicRecord } from './recordListApi'

// Story 6.6 — TanStack mutation hook for PUT /api/data/{designerId}/{id}/restore.
// AR-48 + AR-49: pessimistic mutation. Invalidates the list root key and the
// single-record key on success so list / get views re-fetch with the restored
// record appearing as active.
export function useRestoreRecord(designerId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: (id: string) => restoreRecord(designerId, id),
    onSuccess: (_restored: DynamicRecord, id) => {
      queryClient.invalidateQueries({ queryKey: ['data', designerId] })
      queryClient.invalidateQueries({ queryKey: ['data', designerId, 'record', id] })
    },
  })
}
