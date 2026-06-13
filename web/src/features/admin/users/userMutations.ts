import { useMutation, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { useTranslation } from 'react-i18next'
import { httpClient } from '../../auth/httpClient'
import type {
  AssignUserRolesRequest,
  CreateUserRequest,
  CreateUserResponse,
  UpdateUserRequest,
} from './types'
import { USERS_QUERY_KEY } from './useUsersQuery'

// Invalidates list + every detail query via prefix match — covers the case
// where the admin edits a user and immediately returns to the paginated list.
function invalidateAllUsers(queryClient: ReturnType<typeof useQueryClient>) {
  void queryClient.invalidateQueries({ queryKey: USERS_QUERY_KEY })
}

export function useCreateUserMutation() {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation({
    mutationFn: (body: CreateUserRequest) =>
      httpClient.post<CreateUserResponse>('/api/admin/users', body),
    onSuccess: (data) => {
      invalidateAllUsers(queryClient)
      toast.success(t('admin.users.createSuccess'))
      // Story 2.10 — the welcome email is best-effort; if the API reports it
      // could not be sent, warn the admin so they can share credentials manually.
      if (data.warnings && data.warnings.length > 0) {
        toast.warning(t('admin.users.emailWarning'))
      }
    },
  })
}

export function useUpdateUserMutation(userId: string) {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation({
    mutationFn: (body: UpdateUserRequest) =>
      httpClient.put<void>(`/api/admin/users/${userId}`, body),
    onSuccess: () => {
      invalidateAllUsers(queryClient)
      toast.success(t('admin.users.updateSuccess'))
    },
  })
}

export function useDeactivateUserMutation(userId: string) {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation({
    mutationFn: () => httpClient.put<void>(`/api/admin/users/${userId}/deactivate`),
    // The self-deactivation path returns 409 server-side, so this onSuccess
    // never fires for that case — the previous PERMISSIONS_QUERY_KEY bust
    // here was misleading dead code. Self-deactivation is blocked at the API
    // layer; nothing for the SPA to invalidate. (Story 2.8 review patch P16.)
    onSuccess: () => {
      invalidateAllUsers(queryClient)
      toast.success(t('admin.users.deactivateSuccess'))
    },
  })
}

export function useReactivateUserMutation(userId: string) {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation({
    mutationFn: () => httpClient.put<void>(`/api/admin/users/${userId}/reactivate`),
    onSuccess: () => {
      invalidateAllUsers(queryClient)
      toast.success(t('admin.users.reactivateSuccess'))
    },
  })
}

export function useAssignUserRolesMutation(userId: string) {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation({
    mutationFn: (body: AssignUserRolesRequest) =>
      httpClient.put<void>(`/api/admin/users/${userId}/roles`, body),
    onSuccess: () => {
      invalidateAllUsers(queryClient)
      toast.success(t('admin.users.rolesAssignedSuccess'))
    },
  })
}

export function useResetMfaMutation(userId: string) {
  const queryClient = useQueryClient()
  const { t } = useTranslation()
  return useMutation({
    mutationFn: () => httpClient.delete<void>(`/api/admin/users/${userId}/mfa`),
    onSuccess: () => {
      invalidateAllUsers(queryClient)
      toast.success(t('admin.users.resetMfaSuccess'))
    },
  })
}
