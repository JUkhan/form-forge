export interface AuthenticatedUser {
  userId: string
  email: string
  displayName: string
  themePreference: string | null
  roles: string[]
}

// Story 12.5 — refreshToken is null for a platform-super-admin login (Story 12.4's
// access-token-only decision: that tier's session never writes a refresh-token row),
// matching the backend's LoginResponse.RefreshToken (string?).
export interface RefreshResponse {
  accessToken: string
  refreshToken: string | null
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
