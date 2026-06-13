import { useMutation, useQueryClient } from '@tanstack/react-query'
import { dropColumn } from './designerAdminApi'

// Story 5.6 — DROP mutation; on success, invalidates the drift query so the
// dropped row disappears from the list. The server has already cleared the
// schema-registry cache for this designer, so a subsequent Epic 6 CRUD read
// will pick up the live schema.
export function useDropColumnMutation(designerId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (columnName: string) => dropColumn(designerId, columnName),
    onSuccess: () => {
      void queryClient.invalidateQueries({
        queryKey: ['admin', 'designers', designerId, 'drift'],
      })
    },
  })
}
