import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { toast } from 'sonner'
import {
  addUniqueConstraint,
  dropUniqueConstraint,
  getUniqueConstraints,
  listProvisionedDesigners,
  type ProvisionedDesignersResponse,
  type UniqueConstraintsResponse,
} from './constraintApi'

export const PROVISIONED_DESIGNERS_QUERY_KEY = ['admin', 'constraints', 'designers'] as const
export const UNIQUE_CONSTRAINTS_QUERY_KEY = ['admin', 'constraints', 'detail'] as const

export function useProvisionedDesignersQuery() {
  return useQuery<ProvisionedDesignersResponse>({
    queryKey: PROVISIONED_DESIGNERS_QUERY_KEY,
    queryFn: listProvisionedDesigners,
    staleTime: 30_000,
  })
}

// Loads columns + constraints for one designer. Disabled until a designer is picked.
export function useUniqueConstraintsQuery(designerId: string | null) {
  return useQuery<UniqueConstraintsResponse>({
    queryKey: [...UNIQUE_CONSTRAINTS_QUERY_KEY, designerId],
    queryFn: () => getUniqueConstraints(designerId as string),
    enabled: !!designerId,
    staleTime: 10_000,
  })
}

export function useAddConstraintMutation(designerId: string) {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation({
    mutationFn: (columns: string[]) => addUniqueConstraint(designerId, columns),
    onSuccess: () => {
      void queryClient.invalidateQueries({
        queryKey: [...UNIQUE_CONSTRAINTS_QUERY_KEY, designerId],
      })
      toast.success(t('admin.constraints.addSuccess'))
    },
  })
}

export function useDropConstraintMutation(designerId: string) {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation({
    mutationFn: (constraintName: string) => dropUniqueConstraint(designerId, constraintName),
    onSuccess: () => {
      void queryClient.invalidateQueries({
        queryKey: [...UNIQUE_CONSTRAINTS_QUERY_KEY, designerId],
      })
      toast.success(t('admin.constraints.dropSuccess'))
    },
  })
}
