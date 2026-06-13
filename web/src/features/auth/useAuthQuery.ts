import { useEffect, useRef } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { tokenStore } from './tokenStore'
import { sessionCache } from './sessionCache'
import { refreshSession } from './refreshCoordinator'
import { PERMISSIONS_QUERY_KEY } from './usePermissionsQuery'
import { applyTheme } from '../../lib/theme/applyTheme'
import { THEMES, type Theme } from '../../lib/theme/themes'
import type { RefreshResponse } from './types'

export function useAuthQuery() {
  const queryClient = useQueryClient()

  const query = useQuery<RefreshResponse>({
    queryKey: ['auth', 'session'],
    queryFn: async ({ signal }) => {
      // Clear the boot-time cache now that we're doing a real network fetch.
      sessionCache.clear()
      // Routed through the shared coordinator (NOT httpClient — that would risk a
      // 401 → refresh-retry loop) so this background refetch can't race a reload,
      // a focus-refetch, or another tab and rotate the single-use token into a
      // replay 401.
      const data = await refreshSession()
      // If logout (or unmount) aborted this query while the shared refresh was
      // in flight, do NOT re-set the token — that would re-authenticate a user
      // who just signed out. The shared fetch still completes for other callers.
      if (signal.aborted) throw new Error('aborted')
      if (!data) {
        // AC-4: session truly expired — clear the in-memory token and
        // hard-redirect to /login.
        tokenStore.clear()
        window.location.replace('/login')
        throw new Error('session expired')
      }
      tokenStore.set(data.accessToken)
      const serverTheme = data.user.themePreference
      if (
        serverTheme !== null &&
        (THEMES as readonly string[]).includes(serverTheme) &&
        localStorage.getItem('ff-theme') !== serverTheme
      ) {
        applyTheme(serverTheme as Theme)
      } else if (serverTheme !== null && !(THEMES as readonly string[]).includes(serverTheme)) {
        // Server stored a theme the client doesn't know about (out-of-sync allow-list).
        // Log for telemetry; do not apply.
        console.warn('[theme] server returned unknown theme', serverTheme)
      }
      return data
    },
    // Finite so that returning to a backgrounded/slept tab after a long idle
    // counts the session as stale and lets refetchOnWindowFocus fire a refresh.
    // 10 min < the 15-min access-token TTL, so the token is renewed with margin
    // and in-flight API calls never race an expiry. Quick tab switches (< 10 min)
    // stay fresh, so we don't rotate the refresh token on every focus.
    staleTime: 10 * 60 * 1000,
    refetchInterval: 13 * 60 * 1000, // re-auth every 13 min; access token TTL is 15 min
    // While the tab is hidden the interval is paused (refetchIntervalInBackground
    // defaults to false), so a laptop that sleeps past the token TTL wakes with a
    // stale token. Refetching on focus renews it proactively — the user no longer
    // has to reload the page, and the httpClient 401-retry becomes a backstop
    // rather than the primary recovery path.
    refetchOnWindowFocus: true,
    // The boot refresh is already performed by _app.tsx beforeLoad, which seeds
    // initialData below. Disable the mount refetch so the now-finite staleTime
    // can't trigger a second, redundant silentRefresh() the instant we mount.
    refetchOnMount: false,
    retry: false,
    // Populated by _app.tsx beforeLoad (slow path) or useLoginMutation (fast
    // path). Prevents an immediate mount-time silentRefresh() that would rotate
    // and revoke the refresh token before the browser can store the new cookie.
    initialData: () => sessionCache.getInitialData(),
    initialDataUpdatedAt: () => sessionCache.getInitialDataUpdatedAt(),
  })

  // Every successful refresh may carry a new permission state (role
  // assignment, deactivation). TanStack Query v5 dropped useQuery.onSuccess,
  // so we watch dataUpdatedAt and invalidate the permissions cache so
  // usePermissionsQuery refetches with the new token.
  //
  // Skip the initial 0 → positive transition: usePermissionsQuery mounts in
  // the same render and already fires its own first fetch — invalidating
  // here would queue a redundant second request on every cold boot. AC-4
  // requires a refetch on token refresh, which only happens on subsequent
  // ticks (positive → positive).
  const previousUpdatedAt = useRef(0)
  useEffect(() => {
    if (previousUpdatedAt.current > 0 && query.dataUpdatedAt > previousUpdatedAt.current) {
      void queryClient.invalidateQueries({ queryKey: PERMISSIONS_QUERY_KEY })
    }
    previousUpdatedAt.current = query.dataUpdatedAt
  }, [query.dataUpdatedAt, queryClient])

  return query
}
