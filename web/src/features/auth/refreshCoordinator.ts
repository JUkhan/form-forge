import { generateCorrelationId } from '../../lib/correlationId'
import type { RefreshResponse } from './types'

// Single source of truth for "refresh the session". EVERY refresh caller
// (_app.tsx beforeLoad, useAuthQuery, httpClient's 401-retry) MUST go through
// this so concurrent attempts collapse into one rotation.
//
// Why this exists: the refresh token is single-use and rotates on every
// successful POST /api/auth/refresh — the server revokes the presented token and
// issues a new one. Two refreshes sent with the SAME cookie therefore make the
// second come back 401 (replay), which bounces the user to /login even though
// their session is valid. Previously each caller — and each browser tab — fired
// its own request, so a reload + focus-refetch, a burst of expired-token API
// calls, or a second tab would race and one would 401. (The "200 OK but empty
// body" symptom was an in-flight refresh aborted by that redirect mid-stream.)
//
// Two layers of coordination:
//   1. In-tab — a module-level singleton promise dedupes concurrent callers
//      within one tab; they all await the same network request and result.
//   2. Cross-tab — the fetch runs inside a Web Lock so only one tab rotates the
//      cookie at a time. Other tabs block, then rotate the now-current cookie
//      sequentially (no replay). Falls back to a bare fetch where the Web Locks
//      API is unavailable (older browsers / non-secure contexts / jsdom).
//
// Returns the parsed RefreshResponse on success, or null on any failure (expired
// or invalid refresh token, network error). Callers decide how to react to null.

let _pending: Promise<RefreshResponse | null> | null = null

async function fetchRefresh(): Promise<RefreshResponse | null> {
  // Intentionally NO AbortSignal: one caller unmounting must not abort a refresh
  // that other callers (and other tabs via the lock) are awaiting — aborting
  // mid-body was the source of the "200 OK but empty response" the user saw.
  try {
    const response = await fetch('/api/auth/refresh', {
      method: 'POST',
      credentials: 'include',
      headers: { 'X-Correlation-ID': generateCorrelationId() },
    })
    if (!response.ok) return null
    return (await response.json()) as RefreshResponse
  } catch {
    return null
  }
}

export function refreshSession(): Promise<RefreshResponse | null> {
  if (_pending) return _pending

  _pending = (async () => {
    try {
      const locks = typeof navigator !== 'undefined' ? navigator.locks : undefined
      if (locks?.request) {
        return await locks.request('formforge-auth-refresh', fetchRefresh)
      }
      return await fetchRefresh()
    } finally {
      _pending = null
    }
  })()

  return _pending
}
