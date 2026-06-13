import { useMutation, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { useTranslation } from 'react-i18next'
import {
  createDataset,
  updateDataset,
  deleteDataset,
  type CreateDatasetPayload,
  type UpdateDatasetPayload,
} from './datasetApi'
import { DATASETS_LIST_QUERY_KEY } from './useDatasetListQuery'

function invalidateDatasetList(queryClient: ReturnType<typeof useQueryClient>) {
  void queryClient.invalidateQueries({ queryKey: DATASETS_LIST_QUERY_KEY })
}

export function useCreateDatasetMutation() {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation({
    mutationFn: (payload: CreateDatasetPayload) => createDataset(payload),
    onSuccess: () => {
      invalidateDatasetList(queryClient)
      toast.success(t('admin.datasets.createSuccess'))
    },
  })
}

// id is a hook argument (not a mutationFn arg) — same pattern as useUpdateRoleMutation(roleId).
// Instantiate this hook inside EditDatasetForm, not at the page level (id unknown there).
// Concurrency 409 is intentionally NOT handled here — EditDatasetForm's try/catch
// surfaces it inline via setError('root', ...) per AC-5. Do NOT add an onError toast.
export function useUpdateDatasetMutation(id: string) {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation({
    mutationFn: (payload: UpdateDatasetPayload) => updateDataset(id, payload),
    onSuccess: () => {
      invalidateDatasetList(queryClient)
      void queryClient.invalidateQueries({ queryKey: ['datasets', id] })
      toast.success(t('admin.datasets.updateSuccess'))
    },
  })
}

export function useDeleteDatasetMutation() {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation({
    mutationFn: (id: string) => deleteDataset(id),
    onSuccess: () => {
      invalidateDatasetList(queryClient)
      toast.success(t('admin.datasets.deleteSuccess'))
    },
  })
}
