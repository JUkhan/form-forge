import type { RefreshResponse } from './types'

// Holds the RefreshResponse captured by _app.tsx beforeLoad (slow path) or
// useLoginMutation.onSuccess (fast path) so that useAuthQuery can use it as
// initialData. This prevents the redundant mount-time silentRefresh() that
// would rotate and revoke the current refresh token before the browser cookie
// jar has been updated.
interface CachedSession {
  data: RefreshResponse
  fetchedAt: number
}

let _cache: CachedSession | null = null

export const sessionCache = {
  set(data: RefreshResponse): void {
    _cache = { data, fetchedAt: Date.now() }
  },
  getInitialData(): RefreshResponse | undefined {
    return _cache?.data
  },
  getInitialDataUpdatedAt(): number | undefined {
    return _cache?.fetchedAt
  },
  clear(): void {
    _cache = null
  },
}
