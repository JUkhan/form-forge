import { useState } from 'react'
import { Link } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { ChevronDown, ChevronRight, LayoutDashboard, Menu as MenuIcon, X } from 'lucide-react'
import { cn } from '@/lib/utils'
import { useNavMenusQuery } from '../../features/menu/useNavMenusQuery'
import { NavLucideIcon } from '../icons/NavLucideIcon'
import { Skeleton } from '@/components/ui/skeleton'
import type { NavMenuItem } from '../../features/menu/types'
import type { MenuIcon as MenuIconType } from '../../features/menu/types'

// Story 4.7 — desktop sidebar + mobile drawer for the permission-filtered navbar.
//
// Route binding: Epic 5 (Story 5.2 Schema Binding) wires menus to designer routes.
// For v1 we render every menu item as a non-link <button> so TanStack Router does
// not throw on unknown paths.
//
// Touch targets: every interactive control (hamburger, disclosure toggles, leaf
// buttons) gets `min-h-[44px] min-w-[44px]` per FR-37 AC-3 / WCAG 2.5.5.
//
// Bundle: NavLucideIcon (named imports) — must NOT use LucideIcon.tsx (`import *`)
// per architecture line 645.
export function Navbar() {
  const { t } = useTranslation()
  const { data, isPending, isError } = useNavMenusQuery()
  const [drawerOpen, setDrawerOpen] = useState(false)
  const [openParents, setOpenParents] = useState<Set<string>>(() => new Set())

  function toggleParent(id: string) {
    setOpenParents((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  function closeDrawer() {
    setDrawerOpen(false)
  }

  const items = data ?? []

  return (
    <>
      {/* Skip-link for keyboard users (FR-42 boundary). Visually hidden until focused. */}
      <a
        href="#main-content"
        className="sr-only focus:not-sr-only focus:absolute focus:left-2 focus:top-2 focus:z-50 focus:rounded-md focus:bg-popover focus:px-3 focus:py-2 focus:text-sm focus:text-popover-foreground focus:shadow-lg focus:ring-2 focus:ring-ring"
      >
        {t('nav.skipToContent')}
      </a>

      {/* Mobile hamburger — hidden at md+. Positioned over the global header
          on mobile (the global header has py-2 + the hamburger is top-2). */}
      <button
        type="button"
        aria-label={drawerOpen ? t('nav.closeMenu') : t('nav.openMenu')}
        aria-expanded={drawerOpen}
        onClick={() => setDrawerOpen((v) => !v)}
        className="fixed left-2 top-2 z-30 inline-flex min-h-[44px] min-w-[44px] items-center justify-center rounded-md border border-border bg-card text-foreground shadow-sm transition-colors hover:bg-overlay-hover active:bg-overlay-active md:hidden"
      >
        {drawerOpen ? <X className="h-5 w-5" /> : <MenuIcon className="h-5 w-5" />}
      </button>

      {/* Mobile drawer backdrop */}
      {drawerOpen && (
        <button
          type="button"
          aria-label={t('nav.closeMenu')}
          onClick={closeDrawer}
          className="fixed inset-0 z-20 bg-black/40 backdrop-blur-sm md:hidden"
        />
      )}

      <nav
        role="navigation"
        aria-label={t('nav.primaryAriaLabel')}
        className={cn(
          // bg-sidebar base + a theme-neutral white→black overlay gives the
          // panel a soft top-to-bottom gradient (lighter top, deeper bottom).
          'z-30 flex w-64 flex-col border-r border-sidebar-border bg-sidebar bg-gradient-to-b from-white/[0.06] to-black/[0.08] md:fixed md:inset-y-0 md:left-0 md:block',
          drawerOpen ? 'fixed inset-y-0 left-0 block' : 'hidden',
        )}
      >
        {/* Brand mark — small icon-tile + wordmark across the top of the
            sidebar. */}
        <div className="flex shrink-0 items-center gap-2 px-4 py-4">
          <div className="flex h-8 w-8 items-center justify-center rounded-lg bg-primary text-primary-foreground shadow-sm">
            <LayoutDashboard className="h-4 w-4" />
          </div>
          <span className="text-sm font-bold tracking-tight text-sidebar-foreground">
            {t('nav.brandName')}
          </span>
        </div>

        {/* Menu items — scrolls if the list grows beyond the viewport. */}
        <div className="flex-1 overflow-y-auto p-3">
          {isPending ? (
            <NavSkeleton />
          ) : isError ? (
            // P1 — spec Task 10: render an empty nav rather than a misleading
            // "No menus available" copy when the backend is down.
            null
          ) : items.length === 0 ? (
            <p className="px-2 text-sm text-muted-foreground">{t('nav.emptyMessage')}</p>
          ) : (
            <ul className="space-y-0.5">
              {items.map((item) => (
                <NavListRow
                  key={item.id}
                  item={item}
                  expanded={openParents.has(item.id)}
                  onToggle={() => toggleParent(item.id)}
                  onLeafClick={closeDrawer}
                />
              ))}
            </ul>
          )}
        </div>
      </nav>
    </>
  )
}

interface NavListRowProps {
  item: NavMenuItem
  expanded: boolean
  onToggle: () => void
  onLeafClick: () => void
}

function NavListRow({ item, expanded, onToggle, onLeafClick }: NavListRowProps) {
  const { t } = useTranslation()
  const hasChildren = item.children.length > 0

  return (
    <li>
      <div className="flex items-center gap-0.5">
        <NavLeaf item={item} onLeafClick={onLeafClick} className="flex-1" />
        {hasChildren && (
          <button
            type="button"
            onClick={onToggle}
            aria-expanded={expanded}
            aria-label={
              expanded
                ? t('nav.collapseSubmenu', { name: item.name })
                : t('nav.expandSubmenu', { name: item.name })
            }
            className="inline-flex min-h-[44px] min-w-[44px] items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-overlay-hover active:bg-overlay-active hover:text-sidebar-foreground"
          >
            {expanded ? (
              <ChevronDown className="h-4 w-4" />
            ) : (
              <ChevronRight className="h-4 w-4" />
            )}
          </button>
        )}
      </div>
      {hasChildren && expanded && (
        <ul className="mt-0.5 ml-3 space-y-0.5 border-l border-sidebar-border pl-2">
          {item.children.map((child) => (
            <li key={child.id}>
              <NavLeaf item={child} onLeafClick={onLeafClick} className="w-full" />
            </li>
          ))}
        </ul>
      )}
    </li>
  )
}

// A menu row's clickable area. Renders a TanStack Router <Link> to
// /data/{designerId} when the menu has a binding, falling back to a plain
// non-link <button> for section headers and unbound menus. Both forms call
// onLeafClick so the mobile drawer auto-closes (AC-4) when a leaf is tapped.
function NavLeaf({
  item,
  onLeafClick,
  className,
}: {
  item: NavMenuItem
  onLeafClick: () => void
  className?: string
}) {
  // Color-carrying classes live in the active/inactive variants (not the
  // always-on base) so TanStack's Link — which concatenates base + activeProps —
  // can't leave a stale inactive background/text overriding the active one by
  // CSS source order.
  const itemBase = cn(
    'group flex min-h-[44px] items-center gap-2.5 rounded-md px-2.5 text-left text-sm transition-colors',
    className,
  )
  const itemInactive =
    'text-sidebar-foreground/80 hover:bg-overlay-hover hover:text-sidebar-foreground'
  const itemActive =
    'bg-primary/10 text-primary hover:bg-primary/15 hover:text-primary'

  // Custom route path takes precedence over a binding (they're mutually exclusive
  // server-side, so only one is ever set). An http(s) value opens externally in a
  // new tab via a plain anchor; an internal path uses <Link> for client-side SPA
  // navigation (instantiated only here, so the router context is never required
  // for unbound/section-header rows).
  if (item.routePath) {
    if (/^https?:\/\//.test(item.routePath)) {
      return (
        <a
          href={item.routePath}
          target="_blank"
          rel="noopener noreferrer"
          onClick={onLeafClick}
          className={cn(itemBase, itemInactive)}
        >
          <NavIcon icon={item.icon} />
          <span className="truncate">{item.name}</span>
        </a>
      )
    }
    return (
      <Link
        // The route string is dynamic (admin-authored), so it can't satisfy
        // TanStack Router's typed `to` union — cast to never. Link still builds
        // the href and navigates client-side at runtime.
        to={item.routePath as never}
        onClick={onLeafClick}
        className={itemBase}
        activeProps={{ className: cn(itemBase, itemActive) }}
        inactiveProps={{ className: cn(itemBase, itemInactive) }}
      >
        <NavIcon icon={item.icon} />
        <span className="truncate">{item.name}</span>
      </Link>
    )
  }

  if (item.designerId) {
    return (
      <Link
        to="/data/$designerId"
        params={{ designerId: item.designerId }}
        // /data/$designerId's validateSearch defaults page/pageSize, so the
        // TanStack Router types require an explicit search object here. Pass
        // the route's own defaults so the user lands on a clean first page.
        search={{ page: 1, pageSize: 25 }}
        onClick={onLeafClick}
        // activeProps highlights the menu when the user is currently viewing
        // its bound designer's data; visible feedback for "this is the page
        // you're on" inside the sidebar.
        className={itemBase}
        activeProps={{ className: cn(itemBase, itemActive) }}
        inactiveProps={{ className: cn(itemBase, itemInactive) }}
      >
        <NavIcon icon={item.icon} />
        <span className="truncate">{item.name}</span>
      </Link>
    )
  }

  return (
    <button type="button" onClick={onLeafClick} className={cn(itemBase, itemInactive)}>
      <NavIcon icon={item.icon} />
      <span className="truncate">{item.name}</span>
    </button>
  )
}

// Story 4.7 Task 12 — MinIO icons render a placeholder for now; the actual image
// requires POST /api/files/refresh-urls (architecture 4.1), which is deferred to a
// future files-api story. Unknown lucide names also fall back to the Box placeholder.
function NavIcon({ icon }: { icon: MenuIconType | null }) {
  const { t } = useTranslation()
  if (icon?.type === 'lucide' && icon.name) {
    return <NavLucideIcon name={icon.name} size={16} aria-hidden className="shrink-0 text-muted-foreground group-hover:text-sidebar-foreground" />
  }
  if (icon?.type === 'minio') {
    return <NavLucideIcon name="Box" size={16} aria-label={t('nav.menuIconPlaceholder')} className="shrink-0 text-muted-foreground group-hover:text-sidebar-foreground" />
  }
  return <NavLucideIcon name="Box" size={16} aria-hidden className="shrink-0 text-muted-foreground group-hover:text-sidebar-foreground" />
}

function NavSkeleton() {
  return (
    // Test asserts exactly 3 .animate-pulse rows during isPending — keep
    // <Skeleton> (which adds animate-pulse) and the 3-entry length.
    <ul className="space-y-2" aria-hidden>
      {[0, 1, 2].map((i) => (
        <li key={i}>
          <Skeleton className="h-9 w-full" />
        </li>
      ))}
    </ul>
  )
}
