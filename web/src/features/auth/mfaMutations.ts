import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { httpClient } from './httpClient'

export interface MfaEnrolResponse {
  secret: string
  qrCodeDataUrl: string
  backupCodes: string[]
}

export interface MfaStatusResponse {
  mfaEnabled: boolean
}

export const mfaKeys = {
  status: ['mfa', 'status'] as const,
}

export function useMfaStatusQuery() {
  return useQuery({
    queryKey: mfaKeys.status,
    queryFn: () => httpClient.get<MfaStatusResponse>('/api/users/me/mfa/status'),
  })
}

// Lazy trigger — call mutateAsync() on button click, not on mount.
export function useMfaEnrolMutation() {
  return useMutation({
    mutationFn: () => httpClient.get<MfaEnrolResponse>('/api/users/me/mfa/enrol'),
  })
}

export function useMfaVerifyEnrolmentMutation() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: { code: string }) =>
      httpClient.post<void>('/api/users/me/mfa/verify', body),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: mfaKeys.status })
    },
  })
}
