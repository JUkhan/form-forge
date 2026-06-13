import { useMutation, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { useTranslation } from 'react-i18next'
import { httpClient } from '../../auth/httpClient'
import type { CreateRoleRequest, RoleDetail, UpdateRoleRequest } from './types'
import { ROLES_PAGINATED_QUERY_KEY } from './useRolesQueryPaginated'
import { ROLE_DETAIL_QUERY_KEY } from './useRoleDetailQuery'

// Invalidates every paginated list query (page-tuple variants share the prefix)
// so the admin sees the new/updated/removed role on returning to the list.
function invalidateAllRoles(queryClient: ReturnType<typeof useQueryClient>) {
  void queryClient.invalidateQueries({ queryKey: ROLES_PAGINATED_QUERY_KEY })
}

export function useCreateRoleMutation() {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation({
    mutationFn: (body: CreateRoleRequest) =>
      httpClient.post<RoleDetail>('/api/admin/roles', body),
    onSuccess: () => {
      invalidateAllRoles(queryClient)
      toast.success(t('admin.roles.createSuccess'))
    },
  })
}

export function useUpdateRoleMutation(roleId: string) {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation({
    mutationFn: (body: UpdateRoleRequest) =>
      httpClient.put<void>(`/api/admin/roles/${roleId}`, body),
    onSuccess: () => {
      invalidateAllRoles(queryClient)
      void queryClient.invalidateQueries({ queryKey: [...ROLE_DETAIL_QUERY_KEY, roleId] })
      toast.success(t('admin.roles.updateSuccess'))
    },
  })
}

export function useDeleteRoleMutation(roleId: string) {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation({
    mutationFn: () => httpClient.delete<void>(`/api/admin/roles/${roleId}`),
    // Caller navigates to /admin/roles after success — keeping navigation out
    // of onSuccess preserves the option for components to choose their own
    // post-delete UX (e.g., show a transient confirmation before redirecting).
    onSuccess: () => {
      invalidateAllRoles(queryClient)
      toast.success(t('admin.roles.deleteSuccess'))
    },
  })
}
