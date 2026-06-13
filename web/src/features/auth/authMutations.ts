import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'
import { generateCorrelationId } from '../../lib/correlationId'
import { httpClient } from './httpClient'
import { tokenStore } from './tokenStore'
import { sessionCache } from './sessionCache'
import { applyTheme } from '../../lib/theme/applyTheme'
import { THEMES, type Theme } from '../../lib/theme/themes'
import type { RefreshResponse, LoginApiResponse } from './types'

interface LoginRequest {
  email: string
  password: string
}

export function useLoginMutation(redirectTo?: string, onMfaRequired?: (token: string) => void) {
  const navigate = useNavigate()

  return useMutation({
    mutationFn: (credentials: LoginRequest) =>
      httpClient.post<LoginApiResponse>('/api/auth/login', credentials),
    onSuccess: (data) => {
      // Story 2.14 — MFA challenge: delegate to the caller; do NOT set a token or navigate.
      if ('mfaRequired' in data && data.mfaRequired) {
        onMfaRequired?.(data.mfaSessionToken)
        return
      }
      const response = data as RefreshResponse
      tokenStore.set(response.accessToken)
      sessionCache.set(response)
      const serverTheme = response.user.themePreference
      // Mirror useAuthQuery's short-circuit: skip apply if the local choice already
      // matches. Without this, a theme the user picked on the login page before
      // submitting credentials would flicker on login success.
      if (
        serverTheme !== null &&
        (THEMES as readonly string[]).includes(serverTheme) &&
        localStorage.getItem('ff-theme') !== serverTheme
      ) {
        applyTheme(serverTheme as Theme)
      }
      // Same-origin guarantee comes from loginSearchSchema (regex /^\/(?!\/)/).
      // Use `to` (typed route form) not `href` — `href` would accept absolute URLs.
      void navigate({ to: (redirectTo ?? '/') as '/', replace: true })
    },
  })
}

// Story 2.11 — request a password-reset email. Always resolves with the same
// generic message (anti-enumeration); the page renders an inline success state,
// so there is no onSuccess/onError toast here.
export function useForgotPasswordMutation() {
  return useMutation({
    mutationFn: (body: { email: string }) =>
      httpClient.post<{ message: string }>('/api/auth/forgot-password', body),
  })
}

// Story 2.11 — consume a reset token and set a new password. The server returns
// an empty 200 (httpClient resolves it to undefined). Navigation + success toast
// are handled by the route component.
export function useResetPasswordMutation() {
  return useMutation({
    mutationFn: (body: { token: string; newPassword: string }) =>
      httpClient.post<void>('/api/auth/reset-password', body),
  })
}

// Story 2.12 — authenticated user changes their own password. The server returns
// an empty 200 (httpClient resolves it to undefined). Only { currentPassword,
// newPassword } is sent — confirmNewPassword is a frontend-only check. The caller
// handles the success toast and field reset.
export function useChangePasswordMutation() {
  return useMutation({
    mutationFn: (body: { currentPassword: string; newPassword: string }) =>
      httpClient.put<void>('/api/users/me/password', body),
  })
}

// Story 2.14 — complete an MFA-gated login. Accepts mfaSessionToken + code
// (TOTP or backup). On success, stores tokens and navigates like a normal login.
export function useMfaLoginVerifyMutation(redirectTo?: string) {
  const navigate = useNavigate()

  return useMutation({
    mutationFn: (body: { mfaSessionToken: string; code: string }) =>
      httpClient.post<RefreshResponse>('/api/auth/mfa/verify', body),
    onSuccess: (data) => {
      tokenStore.set(data.accessToken)
      sessionCache.set(data)
      const serverTheme = data.user.themePreference
      if (
        serverTheme !== null &&
        (THEMES as readonly string[]).includes(serverTheme) &&
        localStorage.getItem('ff-theme') !== serverTheme
      ) {
        applyTheme(serverTheme as Theme)
      }
      void navigate({ to: (redirectTo ?? '/') as '/', replace: true })
    },
  })
}

export function useLogoutMutation() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: async (): Promise<void> => {
      // Raw fetch (not httpClient) to avoid coupling logout to the 401-retry
      // refresh machinery — same defensive pattern as useAuthQuery.
      await fetch('/api/auth/logout', {
        method: 'POST',
        credentials: 'include',
        headers: { 'X-Correlation-ID': generateCorrelationId() },
      })
    },
    onSettled: () => {
      // Unconditional client-side logout — runs on success AND failure (AC-4).
      tokenStore.clear()
      sessionCache.clear()
      queryClient.clear()
      void navigate({ to: '/login', replace: true })
    },
  })
}
