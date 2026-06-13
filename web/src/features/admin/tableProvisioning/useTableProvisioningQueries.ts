import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { toast } from 'sonner'
import {
  listTableProvisioning,
  provisionTable,
  type TableProvisioningPage,
} from './tableProvisioningApi'

export const TABLE_PROVISIONING_QUERY_KEY = ['admin', 'tableProvisioning'] as const

export function useTableProvisioningQuery(params: {
  page: number
  pageSize: number
  search?: string
}) {
  return useQuery<TableProvisioningPage>({
    queryKey: [...TABLE_PROVISIONING_QUERY_KEY, params.page, params.pageSize, params.search ?? ''],
    queryFn: () => listTableProvisioning(params),
    staleTime: 15_000,
    // Keep the previous page visible while the next page/search loads — avoids a
    // flash of empty table on every keystroke or page change.
    placeholderData: keepPreviousData,
  })
}

export function useProvisionTableMutation() {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation({
    mutationFn: ({ designerId, version }: { designerId: string; version: number }) =>
      provisionTable(designerId, version),
    onSuccess: () => {
      toast.success(t('admin.tableProvisioning.provisionStarted'))
      // Provisioning runs asynchronously (202 Accepted). Refetch now, then again
      // after short delays so the derived "Provisioned" status appears once the
      // background DDL has committed without the admin needing to refresh.
      const invalidate = () =>
        void queryClient.invalidateQueries({ queryKey: TABLE_PROVISIONING_QUERY_KEY })
      invalidate()
      window.setTimeout(invalidate, 1_500)
      window.setTimeout(invalidate, 4_000)
    },
  })
}
