export interface AuthenticatedUser {
  userId: string
  email: string
  displayName: string
  themePreference: string | null
  roles: string[]
}

export interface RefreshResponse {
  accessToken: string
  refreshToken: string
  expiresIn: number
  user: AuthenticatedUser
}

// Story 2.14 — POST /api/auth/login returns this instead of tokens when the
// account has MFA enabled. The caller must complete POST /api/auth/mfa/verify.
export interface MfaRequiredResponse {
  mfaRequired: true
  mfaSessionToken: string
}

// Union returned by POST /api/auth/login — either full tokens or an MFA challenge.
export type LoginApiResponse = RefreshResponse | MfaRequiredResponse
