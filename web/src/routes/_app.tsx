import { createFileRoute, Link, Outlet, redirect } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { KeyRound, LogOut, Settings as SettingsIcon } from 'lucide-react'
import { useLogoutMutation } from '../features/auth/authMutations'
import { tokenStore } from '../features/auth/tokenStore'
import { refreshSession } from '../features/auth/refreshCoordinator'
import { sessionCache } from '../features/auth/sessionCache'
import { useAuthQuery } from '../features/auth/useAuthQuery'
import { usePermission } from '../features/auth/usePermission'
import { usePermissionsQuery } from '../features/auth/usePermissionsQuery'
import { PermissionGate } from '../components/shared/PermissionGate'
import { Navbar } from '../components/shared/Navbar'
import { useTheme } from '../lib/theme/ThemeProvider'
import { THEMES, THEME_LABELS, type Theme } from '../lib/theme/themes'
import { Button } from '../components/ui/button'
import { Tooltip, TooltipTrigger, TooltipContent } from '../components/ui/tooltip'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '../components/ui/select'

export const Route = createFileRoute('/_app')({
  beforeLoad: async ({ location }) => {
    // Fast path — token already in memory (normal in-session navigation).
    if (tokenStore.get()) return

    // Slow path — SPA boot or page reload. Attempt a silent refresh via the
    // HttpOnly cookie so the user doesn't have to re-enter credentials. Routed
    // through the shared coordinator so a concurrent refresh (another tab, a
    // focus-refetch) can't rotate the single-use token out from under us and
    // bounce us to /login.
    const data = await refreshSession()
    if (data) {
      tokenStore.set(data.accessToken)
      sessionCache.set(data)
      return
    }

    // `pathname + searchStr` (not `href`): TanStack Router's location.search is a
    // parsed object; location.searchStr is the raw query string ("?foo=bar" or "").
    // location.href includes the origin, which fails the same-origin regex in loginSearchSchema.
    throw redirect({
      to: '/login',
      search: { redirect: location.pathname + location.searchStr },
    })
  },
  component: AppLayout,
})

function AppLayout() {
  const { t } = useTranslation()
  const logoutMutation = useLogoutMutation()
  const { theme, setTheme } = useTheme()

  // Keeps the in-memory access token fresh every 13 minutes without
  // requiring a page reload. Complements the one-shot beforeLoad above.
  const authQuery = useAuthQuery()
  // Loads the effective permission snapshot for the signed-in user once per
  // session; cache key is invalidated by useAuthQuery on every token refresh.
  usePermissionsQuery()

  const displayName = authQuery.data?.user.displayName ?? ''
  const initials = displayName
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((p) => p[0]?.toUpperCase() ?? '')
    .join('') || '·'

  // Demo gate: platform-admin role bypass returns true for any resource;
  // for every other user perResource['platform-admin'] is undefined → hidden.
  // Proves AC-1 (absent, not disabled) and AC-3 (no JWT parsing).
  const canSeeAdmin = usePermission('platform-admin', 'canRead')

  return (
    // Height-constrained app shell. The outer container is exactly 100vh and
    // hides overflow so the page itself never scrolls; vertical scrolling moves
    // into the Outlet wrapper below. This is what lets the designer's `h-full`
    // resolve to a real number — without explicit height on the parent chain,
    // its 3-panel flex row would otherwise overflow the viewport.
    <div className="flex h-screen overflow-hidden md:flex">
      <Navbar />
      <main
        id="main-content"
        className="flex min-w-0 flex-1 flex-col overflow-x-hidden md:pl-64"
      >
        <header className="relative z-10 flex shrink-0 items-center justify-end gap-2 border-b border-border bg-card bg-gradient-to-b from-white/[0.08] to-black/[0.06] px-4 py-3 shadow-[0_1px_2px_rgb(0_0_0/0.18),inset_0_1px_0_rgb(255_255_255/0.22)] md:gap-3">
          {/* User identity chip — initials avatar + display name (when known).
              Hidden on small screens to leave room for the hamburger button. */}
          {displayName && (
            <div className="hidden items-center gap-2 sm:flex">
              <div
                aria-hidden
                className="flex h-7 w-7 items-center justify-center rounded-full bg-primary/10 text-xs font-semibold text-primary"
              >
                {initials}
              </div>
              <span className="text-sm font-medium text-foreground">{displayName}</span>
            </div>
          )}

          <Select value={theme} onValueChange={(v) => setTheme(v as Theme)}>
            <SelectTrigger className="h-8 w-[160px]" aria-label={t('theme.label')}>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {THEMES.map((themeName) => (
                <SelectItem key={themeName} value={themeName}>
                  {t(THEME_LABELS[themeName])}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>

          {/* Account settings — change password etc. Visible to ALL authenticated
              users (Story 2.12, AC-8), so intentionally NOT inside PermissionGate.
              Uses KeyRound so it reads distinctly from the admin gear below. */}
          <Tooltip>
            <TooltipTrigger asChild>
              <Button asChild variant="ghost" size="icon-sm">
                <Link to="/settings" aria-label={t('settings.title')}>
                  <KeyRound className="h-4 w-4" />
                </Link>
              </Button>
            </TooltipTrigger>
            <TooltipContent>{t('settings.title')}</TooltipContent>
          </Tooltip>

          <PermissionGate allowed={canSeeAdmin}>
            <Tooltip>
              <TooltipTrigger asChild>
                <Button asChild variant="ghost" size="icon-sm">
                  <Link to="/admin/users" aria-label={t('nav.settings')}>
                    <SettingsIcon className="h-4 w-4" />
                  </Link>
                </Button>
              </TooltipTrigger>
              <TooltipContent>{t('nav.settings')}</TooltipContent>
            </Tooltip>
          </PermissionGate>

          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={() => logoutMutation.mutate()}
            disabled={logoutMutation.isPending}
            aria-busy={logoutMutation.isPending}
          >
            <LogOut className="h-4 w-4" />
            {logoutMutation.isPending ? t('auth.logout.submitting') : t('auth.logout.button')}
          </Button>
        </header>
        {/* min-h-0 lets this flex child shrink below its content size so
            overflow-y-auto can engage; without it the wrapper grows to fit
            content and the scroll never triggers. `relative` is the positioning
            anchor for pages (e.g. the designer) that opt into full-bleed via
            `absolute inset-0` to escape the p-4 padding. */}
        <div className="relative min-h-0 flex-1 overflow-y-auto p-4">
          <Outlet />
        </div>
      </main>
    </div>
  )
}
