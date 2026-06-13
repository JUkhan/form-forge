import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { GripVertical } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { useKeyboardDnD } from '../../../components/designer/useKeyboardDnD'
import type { MenuListItem } from '../../menu/types'
import { useMenusAdminQuery } from './useMenusAdminQuery'
import { useMenuChildrenQuery } from './useMenuChildrenQuery'
import { useReorderMenusMutation } from './menuAdminMutations'

// Fingerprint includes both id-sequence AND order values so the outer
// key={fingerprint(serverList)} remount fires when a concurrent admin session
// changes sort_order without changing the menu set (P1 — id-only hash missed
// order-value-only changes from concurrent reorders).
function fingerprint(rows: MenuListItem[]) {
  return rows.map((r) => `${r.id}:${r.order}`).join(',')
}

// Story 4.5 — drag-reorder list used for both top-level (parentId=null) and
// sub-menu scopes. The mutation is parent-agnostic — the backend infers the
// scope from the supplied ids — but the UI rejects cross-scope drops so a
// user cannot even produce a mixed-scope payload (AC-2 client gate).
//
// ≤256 items per scope so the UI can never exceed the backend MaxItems cap.
// New MIME so a stray designer-canvas drag cannot land on a menu row
// (architecture explicitly keeps DnD contexts isolated).
const REORDER_PAGE_SIZE = 256
const REORDER_MIME = 'application/x-formforge-menu-reorder'

export interface ReorderableMenuListProps {
  parentId: string | null
}

interface PickedPayload {
  id: string
  parentId: string | null
}

// Router: delegates to TopLevelReorderList or SubMenuReorderList so each can
// call the appropriate query hook without violating the rules-of-hooks
// conditional-call prohibition.
export function ReorderableMenuList({ parentId }: ReorderableMenuListProps) {
  if (parentId === null) {
    return <TopLevelReorderList />
  }
  return <SubMenuReorderList parentId={parentId} />
}

function TopLevelReorderList() {
  const { t } = useTranslation()
  const reorderQuery = useMenusAdminQuery(1, REORDER_PAGE_SIZE)

  if (reorderQuery.isLoading) {
    return <p className="text-sm text-muted-foreground">{t('admin.menus.loading')}</p>
  }
  if (reorderQuery.isError) {
    return (
      <p role="alert" className="text-sm text-destructive">
        {t('admin.menus.loadError')}
      </p>
    )
  }

  const serverList: MenuListItem[] = (reorderQuery.data?.data ?? [])
    .filter((m) => m.parentId == null)
    .slice()
    .sort((a, b) => a.order - b.order || a.id.localeCompare(b.id))

  return (
    <ReorderableMenuListInner
      key={fingerprint(serverList)}
      parentId={null}
      serverList={serverList}
    />
  )
}

function SubMenuReorderList({ parentId }: { parentId: string }) {
  const { t } = useTranslation()
  // Use the API-level parentId filter (P3 fix) — avoids the global-sort
  // truncation that occurs when total menus > 256 and child rows fall beyond
  // position 256 in the unfiltered all-menus response. Client-side filter on
  // the all-menus query silently drops any siblings not in the first 256.
  const reorderQuery = useMenuChildrenQuery(parentId, 1, REORDER_PAGE_SIZE)

  if (reorderQuery.isLoading) {
    return <p className="text-sm text-muted-foreground">{t('admin.menus.loading')}</p>
  }
  if (reorderQuery.isError) {
    return (
      <p role="alert" className="text-sm text-destructive">
        {t('admin.menus.loadError')}
      </p>
    )
  }

  const serverList: MenuListItem[] = (reorderQuery.data?.data ?? [])
    .slice()
    .sort((a, b) => a.order - b.order || a.id.localeCompare(b.id))

  return (
    <ReorderableMenuListInner
      key={fingerprint(serverList)}
      parentId={parentId}
      serverList={serverList}
    />
  )
}

interface ReorderableMenuListInnerProps {
  parentId: string | null
  serverList: MenuListItem[]
}

function ReorderableMenuListInner({ parentId, serverList }: ReorderableMenuListInnerProps) {
  const { t } = useTranslation()
  const reorderMutation = useReorderMenusMutation()
  const kbdDnD = useKeyboardDnD<PickedPayload>()

  const preMoveDraftRef = useRef<MenuListItem[] | null>(null)
  const dragSourceParentIdRef = useRef<string | null | undefined>(undefined)

  const [draft, setDraft] = useState<MenuListItem[]>(() => serverList)

  // Auto-focus the row at its new index when pickup transitions to null
  // (commit or cancel). useKeyboardDnD's pickUp/cancel clears insertedIdRef
  // for us, so we don't write to it here. Same pattern as designer.$designerId.
  useEffect(() => {
    if (kbdDnD.pickedUp !== null) return
    const id = kbdDnD.insertedIdRef.current
    if (!id) return
    const el = document.querySelector<HTMLElement>(`[data-reorder-row-id="${id}"]`)
    el?.focus()
  }, [kbdDnD.pickedUp, kbdDnD.insertedIdRef])

  const unchanged = fingerprint(draft) === fingerprint(serverList)
  // P6: also disable Save while a keyboard pickup is active — triggering a
  // save mid-pickup and having it fail leaves the pickup highlight live while
  // the draft reverts to the pre-save snapshot, producing an inconsistent
  // visual+logical state that the user cannot easily recover from.
  const saveDisabled = reorderMutation.isPending || unchanged || kbdDnD.pickedUp !== null

  const moveItem = (fromIndex: number, toIndex: number) => {
    if (fromIndex === toIndex) return
    setDraft((prev) => {
      const next = [...prev]
      const clamped = Math.max(0, Math.min(toIndex, next.length - 1))
      const [moved] = next.splice(fromIndex, 1)
      next.splice(clamped, 0, moved)
      return next
    })
  }

  const onDragStart = (e: React.DragEvent<HTMLLIElement>, row: MenuListItem) => {
    e.dataTransfer.setData(REORDER_MIME, JSON.stringify({ id: row.id, parentId: row.parentId }))
    e.dataTransfer.effectAllowed = 'move'
    dragSourceParentIdRef.current = row.parentId
  }

  const onDragOver = (e: React.DragEvent<HTMLLIElement>, row: MenuListItem) => {
    // getData is protected during dragover per WHATWG; only `types` is visible.
    if (!e.dataTransfer.types.includes(REORDER_MIME)) return
    // AC-2: reject cross-scope drops. The dragged source's parentId must match
    // the hover target's parentId (null === null for top-level).
    if (dragSourceParentIdRef.current !== row.parentId) return
    e.preventDefault()
    e.dataTransfer.dropEffect = 'move'
  }

  const onDrop = (e: React.DragEvent<HTMLLIElement>, targetIndex: number) => {
    e.preventDefault()
    const raw = e.dataTransfer.getData(REORDER_MIME)
    if (!raw) return
    let payload: PickedPayload
    try {
      payload = JSON.parse(raw) as PickedPayload
    } catch {
      return
    }
    if (payload.parentId !== (parentId ?? null)) return
    const fromIndex = draft.findIndex((m) => m.id === payload.id)
    if (fromIndex === -1) return
    moveItem(fromIndex, targetIndex)
  }

  const onDragEnd = () => {
    dragSourceParentIdRef.current = undefined
  }

  const onKeyDown = (e: React.KeyboardEvent<HTMLLIElement>, row: MenuListItem) => {
    const picked = kbdDnD.pickedUp
    if (e.key === ' ' || e.key === 'Enter') {
      e.preventDefault()
      if (picked === null) {
        preMoveDraftRef.current = draft
        kbdDnD.pickUp(
          { id: row.id, parentId: row.parentId },
          t('admin.menus.reorderPickup', { name: row.name }),
          e.currentTarget,
        )
      } else {
        const insertedIndex = draft.findIndex((m) => m.id === picked.id)
        kbdDnD.commit(
          t('admin.menus.reorderCommit', { name: row.name, position: insertedIndex + 1 }),
          picked.id,
        )
        preMoveDraftRef.current = null
      }
      return
    }
    if (e.key === 'Escape' && picked !== null) {
      e.preventDefault()
      if (preMoveDraftRef.current) {
        setDraft(preMoveDraftRef.current)
        preMoveDraftRef.current = null
      }
      kbdDnD.cancel(t('admin.menus.reorderCancelled'))
      kbdDnD.originatorRef.current?.focus()
      return
    }
    if ((e.key === 'ArrowDown' || e.key === 'ArrowUp') && picked !== null) {
      e.preventDefault()
      const direction = e.key === 'ArrowDown' ? 1 : -1
      const currentIndex = draft.findIndex((m) => m.id === picked.id)
      if (currentIndex === -1) return
      const nextIndex = Math.max(0, Math.min(draft.length - 1, currentIndex + direction))
      if (nextIndex === currentIndex) return
      moveItem(currentIndex, nextIndex)
      // P2: announce the picked item's name (from draft), not the event-target
      // row's name — when focus has moved to a different row while an item is
      // picked up the two diverge and the wrong name is spoken to screen readers.
      const pickedName = draft.find((m) => m.id === picked.id)?.name ?? row.name
      kbdDnD.pickUp(
        picked,
        t('admin.menus.reorderMove', { name: pickedName, position: nextIndex + 1 }),
        kbdDnD.originatorRef.current,
      )
    }
  }

  const onDiscard = () => {
    setDraft(serverList)
    preMoveDraftRef.current = null
    if (kbdDnD.pickedUp) {
      kbdDnD.cancel(t('admin.menus.reorderCancelled'))
    }
  }

  const onSave = () => {
    if (unchanged) return
    const items = draft.map((row, i) => ({ id: row.id, order: i }))
    // P4: use mutate() not mutateAsync() — mutateAsync() rejects on error and
    // the void-discarded promise fires a spurious unhandledrejection event even
    // though onError already handles the rollback and toast.
    reorderMutation.mutate(items)
  }

  return (
    <section
      aria-label={t('admin.menus.reorderModeButton')}
      className="relative space-y-3"
    >
      <p className="text-xs text-muted-foreground">
        {t('admin.menus.reorderInstructions')}
      </p>

      <ul
        className="flex flex-col gap-1"
        data-testid="reorder-list"
      >
        {draft.map((row, index) => {
          const isPicked = kbdDnD.pickedUp?.id === row.id
          return (
            <li
              key={row.id}
              data-reorder-row-id={row.id}
              tabIndex={0}
              draggable
              onDragStart={(e) => {
                onDragStart(e, row)
              }}
              onDragOver={(e) => {
                onDragOver(e, row)
              }}
              onDrop={(e) => {
                onDrop(e, index)
              }}
              onDragEnd={onDragEnd}
              onKeyDown={(e) => {
                onKeyDown(e, row)
              }}
              className={cn(
                'group flex cursor-grab items-center gap-2 rounded-lg border bg-card px-3 py-2 text-sm shadow-xs transition-colors',
                'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/60',
                isPicked
                  ? 'border-primary border-dashed bg-primary/5 ring-2 ring-primary/30'
                  : 'border-border hover:border-foreground/30 hover:bg-overlay-hover active:bg-overlay-active',
              )}
            >
              <GripVertical
                aria-hidden
                className="h-4 w-4 shrink-0 text-muted-foreground group-hover:text-foreground"
              />
              <span className="flex-1 truncate text-foreground">{row.name}</span>
              <span
                className={cn(
                  'inline-flex items-center rounded-full px-2 py-0.5 text-[10px] font-medium uppercase tracking-wider',
                  row.isActive
                    ? 'bg-emerald-100 text-emerald-800'
                    : 'bg-muted text-muted-foreground',
                )}
              >
                {row.isActive ? t('admin.menus.isActiveLabel') : t('admin.menus.isInactiveLabel')}
              </span>
            </li>
          )
        })}
      </ul>

      <span
        role="status"
        aria-live="polite"
        aria-atomic="true"
        className="sr-only"
      >
        {kbdDnD.announcement}
      </span>

      <div className="flex gap-2">
        <Button type="button" onClick={onSave} disabled={saveDisabled}>
          {reorderMutation.isPending
            ? t('admin.menus.savingOrderButton')
            : t('admin.menus.saveOrderButton')}
        </Button>
        <Button
          type="button"
          variant="outline"
          onClick={onDiscard}
          disabled={reorderMutation.isPending || unchanged}
        >
          {t('admin.menus.discardOrderButton')}
        </Button>
      </div>
    </section>
  )
}
