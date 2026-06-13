// Module-level token storage — NOT localStorage, NOT React state (NFR-5).
// The token lives only for the lifetime of the SPA session.
let _accessToken: string | null = null

export const tokenStore = {
  get: (): string | null => _accessToken,
  set: (token: string): void => {
    _accessToken = token
  },
  clear: (): void => {
    _accessToken = null
  },
}
