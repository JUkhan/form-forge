// Shared types + helpers for the native HTML5 drag-and-drop pipeline used by
// the component designer. The browser handles auto-scroll, hit-testing, and
// drag-image rendering natively, which fixes a class of scroll-during-drag
// bugs that synthetic DnD libraries (dnd-kit) can't track without manual
// rect-refresh plumbing.

export const DRAG_MIME = 'application/x-formforge-designer'

// Containers that accept drops as children. Mirrored from the designerCanvas
// store (which is the source of truth for the schema invariant) so renderers
// and DropZones can check `canAccept` without an extra store hop.
export const CONTAINER_TYPES = new Set<string>(['Stack', 'Row', 'Tabs', 'Repeater', 'TreeView', 'DatasetComponent'])

export type DragData =
  | { source: 'PALETTE'; elementType: string; displayLabel?: string }
  | { source: 'CANVAS'; id: string; type: string }

// Read dataTransfer payload safely. `e.dataTransfer.getData(MIME)` is only
// available on `drop` (not `dragover`/`dragenter` — those return an empty
// string per the WHATWG drag-and-drop protected mode), so callers must only
// invoke this from a `drop` handler.
export function readDragData(e: React.DragEvent): DragData | null {
  const raw = e.dataTransfer.getData(DRAG_MIME)
  if (!raw) return null
  try {
    const parsed = JSON.parse(raw) as DragData
    if (parsed.source !== 'PALETTE' && parsed.source !== 'CANVAS') return null
    return parsed
  } catch {
    return null
  }
}

// Whether the MIME is present on the event. Safe to call during `dragover` /
// `dragenter` — `types` is exposed in protected mode, unlike `getData`.
export function hasDragMime(e: React.DragEvent): boolean {
  return e.dataTransfer.types.includes(DRAG_MIME)
}
