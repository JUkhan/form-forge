// Native HTML5 drag-and-drop pipeline. Browser handles auto-scroll, drag
// previews, and hit-testing — every container interleaves explicit <DropZone>
// elements between its children so insertion indices are exact and stale rect
// caches can't exist.
import {
  Fragment,
  createContext,
  memo,
  useContext,
  useEffect,
  useRef,
  useState,
} from 'react'
import { useTranslation } from 'react-i18next'
import { useQuery } from '@tanstack/react-query'
import { Pencil, Plus, Trash2 } from 'lucide-react'
import type { DesignerElement } from '@/types/designer'
import { listDatasets } from '@/features/datasets/datasetApi'
import { Tooltip, TooltipTrigger, TooltipContent } from '@/components/ui/tooltip'
import {
  createNewElement,
  createNewTabStack,
  useDesignerCanvasStore,
} from '@/store/designerCanvas'
import { cn } from '@/lib/utils'
import ElementRenderer from './ElementRenderer'
import {
  CONTAINER_TYPES,
  DRAG_MIME,
  hasDragMime,
  readDragData,
  type DragData,
} from './dnd'
import type { UseKeyboardDnDReturn } from './useKeyboardDnD'

// File-local context: gives DropZones and CanvasElements access to the
// keyboard-DnD hook without prop-drilling through every memoised level. The
// context is NOT exported — callers must thread `kbdDnD` via the DesignerCanvas
// prop, which is the documented public surface.
const KbdDnDCtx = createContext<UseKeyboardDnDReturn<DragData> | null>(null)

interface DesignerCanvasProps {
  className?: string
  kbdDnD?: UseKeyboardDnDReturn<DragData>
}

export default function DesignerCanvas({ className, kbdDnD }: DesignerCanvasProps) {
  const rootElement = useDesignerCanvasStore((s) => s.rootElement)
  return (
    <KbdDnDCtx.Provider value={kbdDnD ?? null}>
      <CanvasRoot rootElement={rootElement} className={className} />
    </KbdDnDCtx.Provider>
  )
}

function CanvasRoot({
  rootElement,
  className,
}: {
  rootElement: DesignerElement | null
  className?: string
}) {
  const addElement = useDesignerCanvasStore((s) => s.addElement)
  const moveElement = useDesignerCanvasStore((s) => s.moveElement)

  // Root-level drop: when canvas is empty, the dropped element BECOMES the
  // root. When the root is a container, drops append to its children. When the
  // root is a leaf, drops are silently rejected (the empty-state copy steers
  // users toward containers first). Routing through the store keeps the
  // schema-invariant check in one place.
  function handleRootDrop(data: DragData) {
    if (data.source === 'PALETTE') {
      if (rootElement === null) {
        // empty canvas — accept containers only (a leaf root traps the user
        // with nowhere to drop further elements)
        if (!CONTAINER_TYPES.has(data.elementType)) return
        addElement(null, createNewElement(data.elementType), 0)
        return
      }
      if (!CONTAINER_TYPES.has(rootElement.type)) return
      addElement(
        rootElement.id,
        createNewElement(data.elementType),
        rootElement.children?.length ?? 0,
      )
      return
    }
    // CANVAS-source root drop: only meaningful when an existing container root
    // is the target — append to its children list.
    if (!rootElement || !CONTAINER_TYPES.has(rootElement.type)) return
    moveElement(data.id, rootElement.id, rootElement.children?.length ?? 0)
  }

  return (
    <div
      data-testid="designer-canvas-root"
      className={cn(
        'flex flex-1 flex-col gap-3 overflow-y-auto p-6 transition-colors',
        className,
      )}
    >
      {rootElement ? (
        <CanvasElement element={rootElement} isRoot />
      ) : (
        <EmptyCanvasDrop onDrop={handleRootDrop} />
      )}
    </div>
  )
}

function EmptyCanvasDrop({ onDrop }: { onDrop: (data: DragData) => void }) {
  const { t } = useTranslation()
  const [state, setState] = useState<'idle' | 'ok'>('idle')
  const enterCountRef = useRef(0)
  // Clear residual highlight when a drag ends anywhere (e.g. user releases over
  // browser chrome) — dragleave alone can miss the abort.
  useEffect(() => {
    const clear = () => {
      enterCountRef.current = 0
      setState('idle')
    }
    document.addEventListener('dragend', clear)
    document.addEventListener('drop', clear)
    return () => {
      document.removeEventListener('dragend', clear)
      document.removeEventListener('drop', clear)
    }
  }, [])
  return (
    <div
      onDragEnter={(e) => {
        if (!hasDragMime(e)) return
        enterCountRef.current += 1
        setState('ok')
      }}
      onDragOver={(e) => {
        if (!hasDragMime(e)) return
        e.preventDefault()
        // Don't pin dropEffect — sources mark themselves 'copy' (palette) or
        // 'move' (canvas). A hard 'copy' here cancels CANVAS drags because
        // effectAllowed='move' isn't compatible with dropEffect='copy' per
        // the WHATWG HTML drag-and-drop spec. Let the browser pick.
      }}
      onDragLeave={() => {
        // dragleave fires once per child boundary crossed — counter avoids the
        // ok→idle→ok flicker when the cursor moves over inner text/elements.
        enterCountRef.current = Math.max(0, enterCountRef.current - 1)
        if (enterCountRef.current === 0) setState('idle')
      }}
      onDrop={(e) => {
        e.preventDefault()
        e.stopPropagation()
        enterCountRef.current = 0
        setState('idle')
        const data = readDragData(e)
        if (!data) return
        onDrop(data)
      }}
      className={cn(
        'm-auto flex max-w-sm flex-col items-center gap-2 rounded-lg border-2 border-dashed bg-muted px-6 py-10 text-center transition-colors',
        state === 'ok' ? 'border-primary bg-primary/5' : 'border-border',
      )}
    >
      <p className="text-sm font-medium text-foreground">{t('designer.canvas.emptyCanvasTitle')}</p>
      <p className="text-xs text-muted-foreground">{t('designer.canvas.emptyCanvas')}</p>
    </div>
  )
}

const CanvasElement = memo(function CanvasElement({
  element,
  isRoot,
}: {
  element: DesignerElement
  isRoot?: boolean
}) {
  if (element.type === 'Tabs') {
    return <CanvasTabsContainer element={element} isRoot={isRoot} />
  }
  if (element.type === 'Repeater') {
    return <CanvasRepeaterContainer element={element} isRoot={isRoot} />
  }
  if (element.type === 'TreeView') {
    return <CanvasTreeViewContainer element={element} isRoot={isRoot} />
  }
  if (element.type === 'DatasetComponent') {
    return <CanvasDatasetContainer element={element} isRoot={isRoot} />
  }
  if (CONTAINER_TYPES.has(element.type)) {
    return <CanvasContainer element={element} isRoot={isRoot} />
  }
  return <CanvasLeaf element={element} />
})

// DropZone — one sits between every pair of children (and at the very start /
// end) inside each container. Browser auto-scrolls canvas-root when the cursor
// approaches its top/bottom edge, so a deeply scrolled tree stays fully
// reachable without any custom scroll handling.
function DropZone({
  parentId,
  index,
  orientation = 'vertical',
  // Accept predicate runs only on `drop` (dataTransfer's `getData` is in
  // protected mode during `dragover`/`dragenter` — only `types` is readable
  // there, so a per-kind reject highlight on hover isn't possible without
  // shipping the kind in the MIME suffix, which we deliberately don't do).
  accepts = () => true,
  // When the container has no children we render a single DropZone in `block`
  // variant that fills the interior instead of a thin inter-child sliver, so
  // the user has a generous target. The text inside is rendered as children.
  variant = 'inline',
  // Human-readable description of the drop slot — used as `aria-label` when
  // the slot becomes keyboard-focusable during a keyboard pickup. Callers
  // build it from the parent type + index (see Task 4 spec).
  label,
  onDrop,
  children,
}: {
  parentId: string
  index: number
  orientation?: 'vertical' | 'horizontal'
  accepts?: (kind: string) => boolean
  variant?: 'inline' | 'block'
  label?: string
  // Returns the id of the inserted/moved element so the keyboard path can
  // restore focus there on commit. Mouse callers can ignore the return value.
  onDrop: (parentId: string, index: number, data: DragData) => string | null | void
  children?: React.ReactNode
}) {
  const { t } = useTranslation()
  const kbdDnD = useContext(KbdDnDCtx)
  const [state, setState] = useState<'idle' | 'ok'>('idle')
  const enterCountRef = useRef(0)
  useEffect(() => {
    const clear = () => {
      enterCountRef.current = 0
      setState('idle')
    }
    document.addEventListener('dragend', clear)
    document.addEventListener('drop', clear)
    return () => {
      document.removeEventListener('dragend', clear)
      document.removeEventListener('drop', clear)
    }
  }, [])
  // During an active keyboard pickup, every DropZone the parent will accept
  // becomes a focusable button so the user can Tab/arrow into it; outside a
  // pickup it stays aria-hidden so SR users aren't drowned in empty slots.
  // Filtering by accepts() future-proofs for stricter parent rules — today's
  // `accepts = () => true` default means no behavior change.
  const pickedUp = kbdDnD?.pickedUp ?? null
  const pickedKind =
    pickedUp === null
      ? null
      : pickedUp.source === 'PALETTE'
        ? pickedUp.elementType
        : pickedUp.type
  const isKbdInteractive = pickedUp !== null && pickedKind !== null && accepts(pickedKind)
  return (
    <div
      {...(isKbdInteractive
        ? {
            tabIndex: 0,
            role: 'button' as const,
            'aria-label': label ?? t('designer.keyboard.dropZoneFallback'),
            'data-dropzone': `${parentId}:${index}`,
          }
        : { 'aria-hidden': true as const })}
      onDragEnter={(e) => {
        if (!hasDragMime(e)) return
        enterCountRef.current += 1
        setState('ok')
      }}
      onDragOver={(e) => {
        if (!hasDragMime(e)) return
        e.preventDefault()
        // Palette sources set effectAllowed='copy', canvas sources set 'move';
        // pinning dropEffect would silently cancel every move drag.
      }}
      onDragLeave={() => {
        // Counter avoids ok→idle→ok flicker as the cursor crosses inner
        // boundaries inside the DropZone.
        enterCountRef.current = Math.max(0, enterCountRef.current - 1)
        if (enterCountRef.current === 0) setState('idle')
      }}
      onDrop={(e) => {
        e.preventDefault()
        e.stopPropagation()
        enterCountRef.current = 0
        setState('idle')
        const data = readDragData(e)
        if (!data) return
        const kind = data.source === 'PALETTE' ? data.elementType : data.type
        if (!accepts(kind)) return
        onDrop(parentId, index, data)
      }}
      onKeyDown={(e) => {
        if (!isKbdInteractive || !kbdDnD || !pickedUp) return
        if (e.key === ' ' || e.key === 'Enter') {
          e.preventDefault()
          const insertedId = onDrop(parentId, index, pickedUp) ?? null
          const dropType =
            pickedUp.source === 'PALETTE'
              ? (pickedUp.displayLabel ?? pickedUp.elementType)
              : pickedUp.type
          kbdDnD.commit(
            t('designer.keyboard.dropped', { type: dropType, index: index + 1 }),
            insertedId,
          )
        }
        if (e.key === 'Escape') {
          // Don't preventDefault on Escape — the browser uses it to exit
          // fullscreen / close modals, and overriding that is hostile.
          kbdDnD.cancel(t('designer.keyboard.cancelled'))
        }
        if (
          e.key === 'ArrowDown' ||
          e.key === 'ArrowRight' ||
          e.key === 'ArrowUp' ||
          e.key === 'ArrowLeft'
        ) {
          e.preventDefault()
          // Scope to the current canvas root so a future second DesignerCanvas
          // instance (preview, side-by-side compare, etc.) can't capture focus
          // from this one. Fall back to document for test renders that mount
          // DesignerCanvas without the route wrapper's data-testid.
          const root =
            (e.currentTarget as HTMLElement).closest(
              '[data-testid="designer-canvas-root"]',
            ) ?? document
          const zones = [
            ...root.querySelectorAll('[data-dropzone]'),
          ] as HTMLElement[]
          if (zones.length === 0) return
          const current = zones.indexOf(e.currentTarget as HTMLElement)
          if (current === -1) return
          const delta =
            e.key === 'ArrowDown' || e.key === 'ArrowRight' ? 1 : -1
          const next = (current + delta + zones.length) % zones.length
          zones[next]?.focus()
        }
      }}
      className={cn(
        'rounded-sm transition-colors',
        // Block variant: empty container — fill the interior with a clearly
        // visible target. Inline variant: sliver between two children that
        // doesn't waste layout space. Horizontal swaps width<->height so it
        // sits correctly between Row children.
        variant === 'block'
          ? state === 'ok'
            ? 'flex min-h-[60px] items-center justify-center border-2 border-dashed border-primary bg-primary/10 text-xs text-primary'
            : 'flex min-h-[60px] items-center justify-center border-2 border-dashed border-border bg-muted text-xs text-muted-foreground hover:border-foreground/40'
          : orientation === 'vertical'
            ? state === 'ok'
              ? 'min-h-3 bg-primary/40'
              : 'min-h-2 bg-transparent hover:bg-overlay-hover'
            : state === 'ok'
              ? 'min-w-3 bg-primary/40 self-stretch'
              : 'min-w-2 bg-transparent hover:bg-overlay-hover self-stretch',
        // Visible focus ring while the slot is keyboard-interactive.
        isKbdInteractive &&
          'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/60',
      )}
    >
      {children}
    </div>
  )
}

function CanvasContainer({
  element,
  isRoot,
}: {
  element: DesignerElement
  isRoot?: boolean
}) {
  const { t } = useTranslation()
  const kbdDnD = useContext(KbdDnDCtx)
  const selectedElementId = useDesignerCanvasStore((s) => s.selectedElementId)
  const selectElement = useDesignerCanvasStore((s) => s.selectElement)
  const removeElement = useDesignerCanvasStore((s) => s.removeElement)
  const addElement = useDesignerCanvasStore((s) => s.addElement)
  const moveElement = useDesignerCanvasStore((s) => s.moveElement)

  const isSelected = selectedElementId === element.id
  const orientation: 'vertical' | 'horizontal' =
    element.type === 'Row' ? 'horizontal' : 'vertical'
  const layoutClass =
    orientation === 'horizontal' ? 'flex flex-row gap-2' : 'flex flex-col gap-2'
  const children = element.children ?? []

  function handleDrop(parentId: string, index: number, data: DragData): string {
    if (data.source === 'PALETTE') {
      const created = createNewElement(data.elementType)
      addElement(parentId, created, index)
      return created.id
    }
    // Same-parent reorder off-by-one fix: when moving within the same parent
    // and the source sits before the target slot, removing it first shifts
    // every following sibling left, so the requested geometric index must
    // subtract 1 to land at the slot the user saw before release.
    let finalIndex = index
    const sourceChildren = element.children ?? []
    const sourceIdx = sourceChildren.findIndex((c) => c.id === data.id)
    if (sourceIdx !== -1 && sourceIdx < index) finalIndex = index - 1
    moveElement(data.id, parentId, finalIndex)
    return data.id
  }

  return (
    <div
      data-element-id={element.id}
      draggable={!isRoot}
      tabIndex={kbdDnD && !isRoot ? 0 : undefined}
      onDragStart={(e) => {
        if (isRoot) return
        e.stopPropagation()
        const data: DragData = { source: 'CANVAS', id: element.id, type: element.type }
        e.dataTransfer.setData(DRAG_MIME, JSON.stringify(data))
        e.dataTransfer.effectAllowed = 'move'
      }}
      onKeyDown={
        !isRoot && kbdDnD
          ? (e) => {
              // Inner controls (NodeBadge buttons, "+ Add Tab", etc.) bubble
              // their Space/Enter here — don't pick up the container in that
              // case, or we'd suppress the inner button's activation AND fire
              // a stray pickup of the parent.
              if (e.target !== e.currentTarget) return
              if (e.key === ' ' || e.key === 'Enter') {
                // Guard: if a pickup is already active, don't silently replace
                // it with this container — the user must commit or cancel
                // first via a DropZone or Escape.
                if (kbdDnD.pickedUp) return
                // stopPropagation prevents an ancestor container from also
                // picking itself up when the user hits Space inside this one.
                e.stopPropagation()
                e.preventDefault()
                kbdDnD.pickUp(
                  { source: 'CANVAS', id: element.id, type: element.type },
                  t('designer.keyboard.pickedUp', { type: element.type }),
                  e.currentTarget as HTMLElement,
                )
              }
              if (e.key === 'Escape' && kbdDnD.pickedUp) {
                kbdDnD.cancel(t('designer.keyboard.cancelled'))
              }
            }
          : undefined
      }
      onClick={(e) => {
        if (isRoot) return
        e.stopPropagation()
        selectElement(element.id)
      }}
      className={cn(
        // shrink-0 — canvas-root is a flex-col with overflow-y-auto, so
        // children would otherwise be compressed to viewport height.
        'relative shrink-0 rounded-md border border-dashed border-border bg-card p-2 transition-colors',
        !isRoot && 'min-h-[48px] cursor-grab',
        isSelected && 'ring-2 ring-primary/60',
        !isRoot &&
          'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/60',
      )}
    >
      {!isRoot && (
        <NodeBadge
          label={element.type}
          isSelected={isSelected}
          onDelete={() => removeElement(element.id)}
        />
      )}
      <div className={cn(layoutClass)}>
        {children.length === 0 ? (
          // Empty container: render one big drop target instead of a 2px-wide
          // sliver — especially important for horizontal Rows, where an
          // inter-child DropZone would be a vertical line ~2px wide and
          // visually invisible.
          <DropZone
            parentId={element.id}
            index={0}
            variant="block"
            label={t('designer.keyboard.dropInEmpty', { parent: element.type })}
            onDrop={handleDrop}
          >
            {t('designer.canvas.dropElementsHere')}
          </DropZone>
        ) : (
          <>
            <DropZone
              parentId={element.id}
              index={0}
              orientation={orientation}
              label={t('designer.keyboard.dropInContainer', {
                parent: element.type,
                pos: 1,
              })}
              onDrop={handleDrop}
            />
            {children.map((child, i) => (
              <Fragment key={child.id}>
                <CanvasElement element={child} />
                <DropZone
                  parentId={element.id}
                  index={i + 1}
                  orientation={orientation}
                  label={t('designer.keyboard.dropInContainer', {
                    parent: element.type,
                    pos: i + 2,
                  })}
                  onDrop={handleDrop}
                />
              </Fragment>
            ))}
          </>
        )}
      </div>
    </div>
  )
}

function CanvasTabsContainer({
  element,
  isRoot,
}: {
  element: DesignerElement
  isRoot?: boolean
}) {
  const { t } = useTranslation()
  const selectedElementId = useDesignerCanvasStore((s) => s.selectedElementId)
  const selectElement = useDesignerCanvasStore((s) => s.selectElement)
  const removeElement = useDesignerCanvasStore((s) => s.removeElement)
  const addElement = useDesignerCanvasStore((s) => s.addElement)
  const kbdDnD = useContext(KbdDnDCtx)

  // Track active tab by child id so deletions/reorders don't silently shift
  // the user's selection to a different tab.
  const [activeTabId, setActiveTabId] = useState<string | null>(null)
  const children = element.children ?? []
  const activeChild: DesignerElement | undefined =
    (activeTabId !== null && children.find((c) => c.id === activeTabId)) || children[0]
  const safeIndex = activeChild ? children.findIndex((c) => c.id === activeChild.id) : -1
  const isSelected = selectedElementId === element.id
  const orientation =
    String(element.properties.orientation ?? 'horizontal').toLowerCase() === 'vertical'
      ? 'vertical'
      : 'horizontal'

  function tabLabel(child: DesignerElement, i: number): string {
    const raw = child.properties.tabName
    if (raw === undefined || raw === null) return t('designer.canvas.tabFallbackName', { n: i + 1 })
    const s = String(raw).trim()
    return s === '' ? t('designer.canvas.tabFallbackName', { n: i + 1 }) : s
  }

  function handleAddTab() {
    const count = children.length
    const next = createNewTabStack(count)
    addElement(element.id, next, count)
    setActiveTabId(next.id)
  }

  const tabButtons = children.map((c, i) => {
    const isActive = i === safeIndex
    const isChildSelected = selectedElementId === c.id
    return (
      <button
        key={c.id}
        type="button"
        // Stop dragstart propagation so clicking a tab doesn't initiate a
        // drag on the parent Tabs container.
        onDragStart={(e) => e.stopPropagation()}
        onClick={(e) => {
          e.stopPropagation()
          setActiveTabId(c.id)
          selectElement(c.id)
        }}
        className={cn(
          orientation === 'vertical'
            ? 'flex w-full min-w-0 items-center px-3 py-2 text-left text-xs font-medium transition-colors'
            : 'flex shrink-0 min-w-0 items-center whitespace-nowrap rounded-t px-3 py-1.5 text-xs font-medium transition-colors',
          isActive
            ? orientation === 'vertical'
              ? 'border-l-2 border-primary text-primary bg-card'
              : 'border-b-2 border-primary text-primary bg-card -mb-px'
            : 'text-muted-foreground hover:text-foreground',
          isChildSelected && !isActive && 'bg-primary/5',
        )}
      >
        <span className="truncate">{tabLabel(c, i)}</span>
      </button>
    )
  })

  return (
    <div
      data-element-id={element.id}
      draggable={!isRoot}
      tabIndex={kbdDnD && !isRoot ? 0 : undefined}
      onDragStart={(e) => {
        if (isRoot) return
        e.stopPropagation()
        const data: DragData = { source: 'CANVAS', id: element.id, type: element.type }
        e.dataTransfer.setData(DRAG_MIME, JSON.stringify(data))
        e.dataTransfer.effectAllowed = 'move'
      }}
      onKeyDown={
        !isRoot && kbdDnD
          ? (e) => {
              if (e.target !== e.currentTarget) return
              if (e.key === ' ' || e.key === 'Enter') {
                if (kbdDnD.pickedUp) return
                e.stopPropagation()
                e.preventDefault()
                kbdDnD.pickUp(
                  { source: 'CANVAS', id: element.id, type: element.type },
                  t('designer.keyboard.pickedUp', { type: element.type }),
                  e.currentTarget as HTMLElement,
                )
              }
              if (e.key === 'Escape' && kbdDnD.pickedUp) {
                kbdDnD.cancel(t('designer.keyboard.cancelled'))
              }
            }
          : undefined
      }
      onClick={(e) => {
        if (isRoot) return
        e.stopPropagation()
        selectElement(element.id)
      }}
      className={cn(
        'relative shrink-0 rounded-md border border-dashed border-border bg-card p-2 transition-colors',
        !isRoot && 'min-h-[48px] cursor-grab',
        isSelected && 'ring-2 ring-primary/60',
        !isRoot &&
          'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/60',
      )}
    >
      {!isRoot && (
        <NodeBadge
          label={t('designer.canvas.nodeBadgeTabs')}
          isSelected={isSelected}
          onDelete={() => removeElement(element.id)}
        />
      )}
      {orientation === 'vertical' ? (
        <div className="flex rounded-sm border border-border bg-card">
          <div className="flex min-w-[140px] flex-col border-r border-border bg-muted">
            {tabButtons}
            <button
              type="button"
              onClick={(e) => { e.stopPropagation(); handleAddTab() }}
              className="m-1 rounded-md border border-dashed border-border bg-card px-3 py-1.5 text-xs font-medium text-muted-foreground transition-colors hover:border-primary hover:text-primary"
            >
              {t('designer.canvas.addTab')}
            </button>
          </div>
          <div className="flex-1 p-2">
            {activeChild
              ? <CanvasElement element={activeChild} />
              : <p className="text-xs text-muted-foreground">{t('designer.canvas.noTabsClickAddTab')}</p>}
          </div>
        </div>
      ) : (
        <div className="rounded-sm border border-border bg-card">
          <div className="flex flex-wrap items-end gap-0.5 border-b border-border bg-muted px-1 pt-1">
            {tabButtons}
            <button
              type="button"
              onClick={(e) => { e.stopPropagation(); handleAddTab() }}
              className="mb-1 ml-1 self-start rounded-md border border-dashed border-border bg-card px-3 py-1.5 text-xs font-medium text-muted-foreground transition-colors hover:border-primary hover:text-primary"
            >
              {t('designer.canvas.addTab')}
            </button>
          </div>
          <div className="p-2">
            {activeChild
              ? <CanvasElement element={activeChild} />
              : <p className="text-xs text-muted-foreground">{t('designer.canvas.noTabsClickAddTab')}</p>}
          </div>
        </div>
      )}
    </div>
  )
}

function CanvasRepeaterContainer({
  element,
  isRoot,
}: {
  element: DesignerElement
  isRoot?: boolean
}) {
  const { t } = useTranslation()
  const kbdDnD = useContext(KbdDnDCtx)
  const selectedElementId = useDesignerCanvasStore((s) => s.selectedElementId)
  const selectElement = useDesignerCanvasStore((s) => s.selectElement)
  const removeElement = useDesignerCanvasStore((s) => s.removeElement)
  const addElement = useDesignerCanvasStore((s) => s.addElement)
  const moveElement = useDesignerCanvasStore((s) => s.moveElement)

  const isSelected = selectedElementId === element.id
  const children = element.children ?? []

  const p = element.properties
  const label = String(p.label ?? '').trim()
  const rowDesignerId = String(p.rowDesignerId ?? '').trim()
  const rowVersionRaw = p.rowVersion
  const rowVersion =
    typeof rowVersionRaw === 'number' && Number.isFinite(rowVersionRaw)
      ? rowVersionRaw
      : typeof rowVersionRaw === 'string' && /^\d+$/.test(rowVersionRaw)
        ? Number(rowVersionRaw)
        : undefined
  // Tabular-mode indicator: the canvas keeps a vertical template editor so the
  // drop UX stays simple, but the runtime renderer switches to <table> layout.
  // Surface that mismatch with a thin banner so the author understands they
  // should drop `Repeater Field` leaves only and use Preview to see the real
  // table.
  const showHeaders = p.showHeaders === true

  function handleDrop(parentId: string, index: number, data: DragData): string {
    if (data.source === 'PALETTE') {
      const created = createNewElement(data.elementType)
      addElement(parentId, created, index)
      return created.id
    }
    let finalIndex = index
    const sourceIdx = children.findIndex((c) => c.id === data.id)
    if (sourceIdx !== -1 && sourceIdx < index) finalIndex = index - 1
    moveElement(data.id, parentId, finalIndex)
    return data.id
  }

  return (
    <div
      data-element-id={element.id}
      draggable={!isRoot}
      tabIndex={kbdDnD && !isRoot ? 0 : undefined}
      onDragStart={(e) => {
        if (isRoot) return
        e.stopPropagation()
        const data: DragData = { source: 'CANVAS', id: element.id, type: element.type }
        e.dataTransfer.setData(DRAG_MIME, JSON.stringify(data))
        e.dataTransfer.effectAllowed = 'move'
      }}
      onKeyDown={
        !isRoot && kbdDnD
          ? (e) => {
              if (e.target !== e.currentTarget) return
              if (e.key === ' ' || e.key === 'Enter') {
                if (kbdDnD.pickedUp) return
                e.stopPropagation()
                e.preventDefault()
                kbdDnD.pickUp(
                  { source: 'CANVAS', id: element.id, type: element.type },
                  t('designer.keyboard.pickedUp', { type: element.type }),
                  e.currentTarget as HTMLElement,
                )
              }
              if (e.key === 'Escape' && kbdDnD.pickedUp) {
                kbdDnD.cancel(t('designer.keyboard.cancelled'))
              }
            }
          : undefined
      }
      onClick={(e) => {
        if (isRoot) return
        e.stopPropagation()
        selectElement(element.id)
      }}
      className={cn(
        'relative shrink-0 rounded-md border border-dashed border-border bg-card p-2 transition-colors',
        !isRoot && 'min-h-[48px] cursor-grab',
        isSelected && 'ring-2 ring-primary/60',
        !isRoot &&
          'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/60',
      )}
    >
      {!isRoot && (
        <NodeBadge
          label={t('designer.canvas.nodeBadgeRepeater')}
          isSelected={isSelected}
          onDelete={() => removeElement(element.id)}
        />
      )}
      <div className="rounded-sm border border-border bg-card">
        {label !== '' && (
          <div className="border-b border-border bg-muted px-3 py-1.5 text-xs font-medium text-foreground">
            {label}
          </div>
        )}
        {showHeaders && (
          // Informational (not an error) — the token system has no warning color,
          // so an advisory banner maps to the neutral muted surface (Story 8.1).
          <div className="border-b border-border bg-muted px-3 py-1.5 text-[10px] text-muted-foreground">
            {t('designer.canvas.tabularDisplayBanner')}
          </div>
        )}
        <div className="flex items-start gap-2 px-3 py-2">
          <div className="relative flex flex-1 flex-col gap-2">
            {children.length === 0 ? (
              <DropZone
                parentId={element.id}
                index={0}
                variant="block"
                label={t('designer.keyboard.dropInEmpty', { parent: element.type })}
                onDrop={handleDrop}
              >
                {t('designer.canvas.dragFieldsToRowTemplate')}
              </DropZone>
            ) : (
              <>
                <DropZone
                  parentId={element.id}
                  index={0}
                  label={t('designer.keyboard.dropInContainer', {
                    parent: element.type,
                    pos: 1,
                  })}
                  onDrop={handleDrop}
                />
                {children.map((child, i) => (
                  <Fragment key={child.id}>
                    <CanvasElement element={child} />
                    <DropZone
                      parentId={element.id}
                      index={i + 1}
                      label={t('designer.keyboard.dropInContainer', {
                        parent: element.type,
                        pos: i + 2,
                      })}
                      onDrop={handleDrop}
                    />
                  </Fragment>
                ))}
              </>
            )}
          </div>
          <div className="flex shrink-0 items-center gap-1 pt-1">
            <Pencil className="h-4 w-4 text-muted-foreground/50" aria-hidden />
            <Trash2 className="h-4 w-4 text-muted-foreground/50" aria-hidden />
          </div>
        </div>
        <div className="border-t border-border px-3 py-2">
          <button
            type="button"
            disabled
            className="cursor-not-allowed rounded bg-muted px-3 py-1.5 text-xs font-medium text-muted-foreground"
          >
            {t('designer.canvas.addPreview')}
          </button>
        </div>
        <div className="border-t border-border bg-muted px-3 py-1.5 text-[10px] text-muted-foreground">
          {t('designer.canvas.rowFormStatus', {
            designer: rowDesignerId === '' ? t('designer.canvas.rowFormStatusNotSet') : rowDesignerId,
            version: rowVersion || t('designer.canvas.rowFormStatusVersionLatest'),
          })}
        </div>
      </div>
    </div>
  )
}

// TreeView canvas chrome — identical drop UX to the Repeater (its children are the
// node template), minus the tabular-headers banner (TreeView has no Show-headers
// property). The runtime renders these children per tree node.
function CanvasTreeViewContainer({
  element,
  isRoot,
}: {
  element: DesignerElement
  isRoot?: boolean
}) {
  const { t } = useTranslation()
  const kbdDnD = useContext(KbdDnDCtx)
  const selectedElementId = useDesignerCanvasStore((s) => s.selectedElementId)
  const selectElement = useDesignerCanvasStore((s) => s.selectElement)
  const removeElement = useDesignerCanvasStore((s) => s.removeElement)
  const addElement = useDesignerCanvasStore((s) => s.addElement)
  const moveElement = useDesignerCanvasStore((s) => s.moveElement)

  const isSelected = selectedElementId === element.id
  const children = element.children ?? []

  const p = element.properties
  const label = String(p.label ?? '').trim()
  const rowDesignerId = String(p.rowDesignerId ?? '').trim()
  const rowVersionRaw = p.rowVersion
  const rowVersion =
    typeof rowVersionRaw === 'number' && Number.isFinite(rowVersionRaw)
      ? rowVersionRaw
      : typeof rowVersionRaw === 'string' && /^\d+$/.test(rowVersionRaw)
        ? Number(rowVersionRaw)
        : undefined

  function handleDrop(parentId: string, index: number, data: DragData): string {
    if (data.source === 'PALETTE') {
      const created = createNewElement(data.elementType)
      addElement(parentId, created, index)
      return created.id
    }
    let finalIndex = index
    const sourceIdx = children.findIndex((c) => c.id === data.id)
    if (sourceIdx !== -1 && sourceIdx < index) finalIndex = index - 1
    moveElement(data.id, parentId, finalIndex)
    return data.id
  }

  return (
    <div
      data-element-id={element.id}
      draggable={!isRoot}
      tabIndex={kbdDnD && !isRoot ? 0 : undefined}
      onDragStart={(e) => {
        if (isRoot) return
        e.stopPropagation()
        const data: DragData = { source: 'CANVAS', id: element.id, type: element.type }
        e.dataTransfer.setData(DRAG_MIME, JSON.stringify(data))
        e.dataTransfer.effectAllowed = 'move'
      }}
      onKeyDown={
        !isRoot && kbdDnD
          ? (e) => {
              if (e.target !== e.currentTarget) return
              if (e.key === ' ' || e.key === 'Enter') {
                if (kbdDnD.pickedUp) return
                e.stopPropagation()
                e.preventDefault()
                kbdDnD.pickUp(
                  { source: 'CANVAS', id: element.id, type: element.type },
                  t('designer.keyboard.pickedUp', { type: element.type }),
                  e.currentTarget as HTMLElement,
                )
              }
              if (e.key === 'Escape' && kbdDnD.pickedUp) {
                kbdDnD.cancel(t('designer.keyboard.cancelled'))
              }
            }
          : undefined
      }
      onClick={(e) => {
        if (isRoot) return
        e.stopPropagation()
        selectElement(element.id)
      }}
      className={cn(
        'relative shrink-0 rounded-md border border-dashed border-border bg-card p-2 transition-colors',
        !isRoot && 'min-h-[48px] cursor-grab',
        isSelected && 'ring-2 ring-primary/60',
        !isRoot &&
          'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/60',
      )}
    >
      {!isRoot && (
        <NodeBadge
          label={t('designer.canvas.nodeBadgeTreeView')}
          isSelected={isSelected}
          onDelete={() => removeElement(element.id)}
        />
      )}
      <div className="rounded-sm border border-border bg-card">
        {label !== '' && (
          <div className="border-b border-border bg-muted px-3 py-1.5 text-xs font-medium text-foreground">
            {label}
          </div>
        )}
        <div className="flex items-start gap-2 px-3 py-2">
          <div className="relative flex flex-1 flex-col gap-2">
            {children.length === 0 ? (
              <DropZone
                parentId={element.id}
                index={0}
                variant="block"
                label={t('designer.keyboard.dropInEmpty', { parent: element.type })}
                onDrop={handleDrop}
              >
                {t('designer.canvas.dragFieldsToNodeTemplate')}
              </DropZone>
            ) : (
              <>
                <DropZone
                  parentId={element.id}
                  index={0}
                  label={t('designer.keyboard.dropInContainer', {
                    parent: element.type,
                    pos: 1,
                  })}
                  onDrop={handleDrop}
                />
                {children.map((child, i) => (
                  <Fragment key={child.id}>
                    <CanvasElement element={child} />
                    <DropZone
                      parentId={element.id}
                      index={i + 1}
                      label={t('designer.keyboard.dropInContainer', {
                        parent: element.type,
                        pos: i + 2,
                      })}
                      onDrop={handleDrop}
                    />
                  </Fragment>
                ))}
              </>
            )}
          </div>
          <div className="flex shrink-0 items-center gap-1 pt-1">
            <Plus className="h-4 w-4 text-muted-foreground/50" aria-hidden />
            <Pencil className="h-4 w-4 text-muted-foreground/50" aria-hidden />
            <Trash2 className="h-4 w-4 text-muted-foreground/50" aria-hidden />
          </div>
        </div>
        <div className="border-t border-border bg-muted px-3 py-1.5 text-[10px] text-muted-foreground">
          {t('designer.canvas.rowFormStatus', {
            designer: rowDesignerId === '' ? t('designer.canvas.rowFormStatusNotSet') : rowDesignerId,
            version: rowVersion || t('designer.canvas.rowFormStatusVersionLatest'),
          })}
        </div>
      </div>
    </div>
  )
}

// DatasetComponent canvas chrome: shows the bound dataset, an (optional) filter-form
// drop area for input children, and a static data-view placeholder. Modeled on
// CanvasRepeaterContainer — children are the filter inputs, dropped/reordered the same way.
function CanvasDatasetContainer({
  element,
  isRoot,
}: {
  element: DesignerElement
  isRoot?: boolean
}) {
  const { t } = useTranslation()
  const kbdDnD = useContext(KbdDnDCtx)
  const selectedElementId = useDesignerCanvasStore((s) => s.selectedElementId)
  const selectElement = useDesignerCanvasStore((s) => s.selectElement)
  const removeElement = useDesignerCanvasStore((s) => s.removeElement)
  const addElement = useDesignerCanvasStore((s) => s.addElement)
  const moveElement = useDesignerCanvasStore((s) => s.moveElement)

  const isSelected = selectedElementId === element.id
  const children = element.children ?? []
  const p = element.properties
  const datasetId = String(p.optionsDatasetId ?? '').trim()
  const filterable = p.filterable === true
  const tableView = p.tableView === true
  const hasAnyView =
    tableView ||
    p.barChart === true ||
    p.lineChart === true ||
    p.pieChart === true ||
    p.donutChart === true

  // Resolve the friendly dataset name (cached + shared with the inspector picker).
  const datasetsQuery = useQuery({
    queryKey: ['datasets', 'list', 1, 100],
    queryFn: () => listDatasets(1, 100),
    staleTime: 60_000,
  })
  const datasetName =
    datasetsQuery.data?.data.find((d) => d.id === datasetId)?.datasetName ?? null

  function handleDrop(parentId: string, index: number, data: DragData): string {
    if (data.source === 'PALETTE') {
      const created = createNewElement(data.elementType)
      addElement(parentId, created, index)
      return created.id
    }
    let finalIndex = index
    const sourceIdx = children.findIndex((c) => c.id === data.id)
    if (sourceIdx !== -1 && sourceIdx < index) finalIndex = index - 1
    moveElement(data.id, parentId, finalIndex)
    return data.id
  }

  return (
    <div
      data-element-id={element.id}
      draggable={!isRoot}
      tabIndex={kbdDnD && !isRoot ? 0 : undefined}
      onDragStart={(e) => {
        if (isRoot) return
        e.stopPropagation()
        const data: DragData = { source: 'CANVAS', id: element.id, type: element.type }
        e.dataTransfer.setData(DRAG_MIME, JSON.stringify(data))
        e.dataTransfer.effectAllowed = 'move'
      }}
      onKeyDown={
        !isRoot && kbdDnD
          ? (e) => {
              if (e.target !== e.currentTarget) return
              if (e.key === ' ' || e.key === 'Enter') {
                if (kbdDnD.pickedUp) return
                e.stopPropagation()
                e.preventDefault()
                kbdDnD.pickUp(
                  { source: 'CANVAS', id: element.id, type: element.type },
                  t('designer.keyboard.pickedUp', { type: element.type }),
                  e.currentTarget as HTMLElement,
                )
              }
              if (e.key === 'Escape' && kbdDnD.pickedUp) {
                kbdDnD.cancel(t('designer.keyboard.cancelled'))
              }
            }
          : undefined
      }
      onClick={(e) => {
        if (isRoot) return
        e.stopPropagation()
        selectElement(element.id)
      }}
      className={cn(
        'relative shrink-0 rounded-md border border-dashed border-border bg-card p-2 transition-colors',
        !isRoot && 'min-h-[48px] cursor-grab',
        isSelected && 'ring-2 ring-primary/60',
        !isRoot &&
          'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/60',
      )}
    >
      {!isRoot && (
        <NodeBadge
          label={t('designer.canvas.nodeBadgeDataset')}
          isSelected={isSelected}
          onDelete={() => removeElement(element.id)}
        />
      )}
      <div className="rounded-sm border border-border bg-card">
        <div className="border-b border-border bg-muted px-3 py-1.5 text-[10px] text-muted-foreground">
          {t('designer.canvas.datasetStatus', {
            dataset:
              datasetName ??
              (datasetId === '' ? t('designer.canvas.datasetNotSelected') : datasetId),
          })}
        </div>

        {filterable && (
          <div className="border-b border-border px-3 py-2">
            <p className="mb-1 text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">
              {t('designer.canvas.datasetFilterUi')}
            </p>
            <div className="relative flex flex-col gap-2">
              {children.length === 0 ? (
                <DropZone
                  parentId={element.id}
                  index={0}
                  variant="block"
                  label={t('designer.keyboard.dropInEmpty', { parent: element.type })}
                  onDrop={handleDrop}
                >
                  {t('designer.canvas.datasetDropFilterInputs')}
                </DropZone>
              ) : (
                <>
                  <DropZone
                    parentId={element.id}
                    index={0}
                    label={t('designer.keyboard.dropInContainer', { parent: element.type, pos: 1 })}
                    onDrop={handleDrop}
                  />
                  {children.map((child, i) => (
                    <Fragment key={child.id}>
                      <CanvasElement element={child} />
                      <DropZone
                        parentId={element.id}
                        index={i + 1}
                        label={t('designer.keyboard.dropInContainer', { parent: element.type, pos: i + 2 })}
                        onDrop={handleDrop}
                      />
                    </Fragment>
                  ))}
                </>
              )}
            </div>
          </div>
        )}

        <div className="px-3 py-3 text-center text-[11px] text-muted-foreground">
          {hasAnyView
            ? t('designer.canvas.datasetTablePlaceholder')
            : t('designer.canvas.datasetNoViewPlaceholder')}
        </div>
      </div>
    </div>
  )
}

function CanvasLeaf({ element }: { element: DesignerElement }) {
  const { t } = useTranslation()
  const kbdDnD = useContext(KbdDnDCtx)
  const selectedElementId = useDesignerCanvasStore((s) => s.selectedElementId)
  const selectElement = useDesignerCanvasStore((s) => s.selectElement)
  const removeElement = useDesignerCanvasStore((s) => s.removeElement)
  const isSelected = selectedElementId === element.id

  return (
    <div
      data-element-id={element.id}
      draggable
      tabIndex={kbdDnD ? 0 : undefined}
      onDragStart={(e) => {
        e.stopPropagation()
        const data: DragData = { source: 'CANVAS', id: element.id, type: element.type }
        e.dataTransfer.setData(DRAG_MIME, JSON.stringify(data))
        e.dataTransfer.effectAllowed = 'move'
      }}
      onKeyDown={
        kbdDnD
          ? (e) => {
              if (e.target !== e.currentTarget) return
              if (e.key === ' ' || e.key === 'Enter') {
                if (kbdDnD.pickedUp) return
                e.preventDefault()
                kbdDnD.pickUp(
                  { source: 'CANVAS', id: element.id, type: element.type },
                  t('designer.keyboard.pickedUp', { type: element.type }),
                  e.currentTarget as HTMLElement,
                )
              }
              if (e.key === 'Escape' && kbdDnD.pickedUp) {
                kbdDnD.cancel(t('designer.keyboard.cancelled'))
              }
            }
          : undefined
      }
      onClick={(e) => {
        e.stopPropagation()
        selectElement(element.id)
      }}
      className={cn(
        'relative cursor-grab rounded border border-dashed border-border bg-card px-3 py-2 text-xs text-muted-foreground',
        isSelected && 'ring-2 ring-primary/60',
        'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/60',
      )}
    >
      <NodeBadge
        label={element.type}
        isSelected={isSelected}
        onDelete={() => removeElement(element.id)}
        compact
      />
      {/* pointer-events: none on the preview so clicks fall through to the
          wrapper's onClick. ElementRenderer's non-interactive path produces
          `disabled` inputs/selects, and browsers absorb click events on
          disabled form controls — without this guard the user can't select
          a dropped leaf because the click lands inside the form control and
          never bubbles to selectElement. */}
      <div className="mt-1 pointer-events-none select-none">
        <ElementRenderer element={element} interactive={false} />
      </div>
    </div>
  )
}

function NodeBadge({
  label,
  isSelected,
  onDelete,
  compact,
}: {
  label: string
  isSelected: boolean
  onDelete: () => void
  compact?: boolean
}) {
  return (
    <div
      className={cn(
        'flex items-center justify-between text-[10px] font-semibold uppercase tracking-wider text-muted-foreground',
        compact ? 'mb-0' : 'mb-1',
      )}
      onClick={(e) => e.stopPropagation()}
    >
      <span>{label}</span>
      {isSelected && <DeleteControl onConfirm={onDelete} />}
    </div>
  )
}

function DeleteControl({ onConfirm }: { onConfirm: () => void }) {
  const { t } = useTranslation()
  const [confirming, setConfirming] = useState(false)
  if (confirming) {
    return (
      <span className="flex items-center gap-1" onClick={(e) => e.stopPropagation()}>
        <span className="text-[10px] text-destructive">{t('designer.canvas.deleteConfirmPrompt')}</span>
        <button
          type="button"
          onClick={(e) => {
            e.stopPropagation()
            onConfirm()
            setConfirming(false)
          }}
          className="rounded px-1 py-0.5 text-[10px] font-medium text-destructive hover:bg-destructive/10"
          aria-label={t('designer.canvas.confirmDeleteAria')}
        >
          ✓
        </button>
        <button
          type="button"
          onClick={(e) => {
            e.stopPropagation()
            setConfirming(false)
          }}
          className="rounded px-1 py-0.5 text-[10px] text-muted-foreground hover:bg-overlay-hover active:bg-overlay-active"
          aria-label={t('designer.canvas.cancelDeleteAria')}
        >
          ✕
        </button>
      </span>
    )
  }
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <button
          type="button"
          onClick={(e) => {
            e.stopPropagation()
            setConfirming(true)
          }}
          // Native HTML5 drag won't start from a button by default (its `draggable`
          // is `false`), so we don't need the dnd-kit-era mouseDown stopPropagation
          // hack here. Stop dragstart bubbling defensively so a quick click+drag
          // doesn't accidentally drag the parent container.
          onDragStart={(e) => e.stopPropagation()}
          className="flex items-center justify-center rounded p-0.5 text-muted-foreground hover:bg-destructive/10 hover:text-destructive"
          aria-label={t('designer.canvas.deleteElement')}
        >
          <Trash2 className="h-3 w-3" />
        </button>
      </TooltipTrigger>
      <TooltipContent>{t('designer.canvas.deleteElement')}</TooltipContent>
    </Tooltip>
  )
}
