import { useMutation, useQueryClient } from '@tanstack/react-query'
import { hardDeleteRecord } from './hardDeleteRecordApi'
import type { DynamicRecord } from './recordListApi'

// TanStack mutation hook for DELETE /api/data/{designerId}/{id}/hard-delete.
// Pessimistic (awaits server confirmation). Invalidates the list root key and the
// single-record key on success so the purged row — and any cascade-removed
// descendants — disappear from list / get views on re-fetch.
export function useHardDeleteRecord(designerId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: (id: string) => hardDeleteRecord(designerId, id),
    onSuccess: (_deleted: DynamicRecord, id) => {
      queryClient.invalidateQueries({ queryKey: ['data', designerId] })
      queryClient.invalidateQueries({ queryKey: ['data', designerId, 'record', id] })
    },
  })
}
