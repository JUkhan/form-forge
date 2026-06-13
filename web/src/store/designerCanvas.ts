import { create } from 'zustand'
import type { ComponentSchemaDto, DesignerElement } from '../types/designer'
import { DEFAULT_PGTYPE_BY_TYPE } from '../components/designer/pgTypes'

const CONTAINER_TYPES = ['Stack', 'Row', 'Tabs', 'Repeater', 'TreeView', 'DatasetComponent'] as const
const CONTAINER_TYPE_SET = new Set<string>(CONTAINER_TYPES)

// Properties only meaningful when a Stack is a direct Tabs child. Stripped on
// move-out so a future move back in doesn't resurface stale tab metadata.
const TAB_CHILD_PROPS = ['tabName', 'paddingPx', 'contentGapPx'] as const

function stripTabChildProps(el: DesignerElement): DesignerElement {
  if (!el.properties) return el
  const nextProps = { ...el.properties }
  let changed = false
  for (const k of TAB_CHILD_PROPS) {
    if (k in nextProps) {
      delete nextProps[k]
      changed = true
    }
  }
  return changed ? { ...el, properties: nextProps } : el
}

function generateId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID()
  }
  // Fallback for non-secure-context environments (older browsers, embedded webviews)
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0
    const v = c === 'x' ? r : (r & 0x3) | 0x8
    return v.toString(16)
  })
}

// Default per-tab properties shared between createNewElement('Tabs') and the
// canvas's "+ Add Tab" button so the shape of a freshly minted tab is in one place.
export function createNewTabStack(index: number): DesignerElement {
  return {
    id: generateId(),
    type: 'Stack',
    children: [],
    properties: {
      tabName: `Tab ${index + 1}`,
      paddingPx: 8,
      contentGapPx: 8,
    },
  }
}

export function createNewElement(type: string): DesignerElement {
  if (type === 'Tabs') {
    // A fresh Tabs element ships with one default tab so the canvas never paints an
    // empty Tabs container on first drop. Existing schemas that have no tab children
    // are NOT mutated on load — that path stays in setSchema and renders the empty
    // state with the "+ Add Tab" affordance still available.
    return {
      id: generateId(),
      type: 'Tabs',
      children: [createNewTabStack(0)],
      properties: { orientation: 'horizontal' },
    }
  }
  // Story B — seed the required pgType property with the per-component default so a
  // freshly dropped input field is immediately valid (the PgType inspector lets the
  // user change it). Non-input components have no default and stay property-less.
  const defaultPgType = DEFAULT_PGTYPE_BY_TYPE[type]
  return {
    id: generateId(),
    type,
    children: CONTAINER_TYPE_SET.has(type) ? [] : undefined,
    properties: defaultPgType ? { pgType: defaultPgType } : {},
  }
}

function findById(node: DesignerElement, id: string): DesignerElement | null {
  if (node.id === id) return node
  for (const child of node.children ?? []) {
    const found = findById(child, id)
    if (found) return found
  }
  return null
}

function cloneTree(node: DesignerElement): DesignerElement {
  return {
    ...node,
    properties: structuredClone(node.properties),
    children: node.children?.map(cloneTree),
  }
}

function removeById(node: DesignerElement, id: string): DesignerElement | null {
  const children = node.children
  if (!children) return null
  const idx = children.findIndex((c) => c.id === id)
  if (idx !== -1) {
    const [removed] = children.splice(idx, 1)
    return removed
  }
  for (const child of children) {
    const removed = removeById(child, id)
    if (removed) return removed
  }
  return null
}

function insertAt(
  root: DesignerElement,
  parentId: string,
  element: DesignerElement,
  index: number,
): boolean {
  if (root.id === parentId) {
    root.children = root.children ?? []
    const safeIndex = Math.max(0, Math.min(index, root.children.length))
    root.children.splice(safeIndex, 0, element)
    return true
  }
  return (root.children ?? []).some((c) => insertAt(c, parentId, element, index))
}

interface DesignerCanvasState {
  schemaId: string | null
  version: number | null
  displayName: string
  isDirty: boolean
  selectedElementId: string | null
  rootElement: DesignerElement | null

  selectElement: (id: string | null) => void
  updateDisplayName: (displayName: string) => void
  updateElementProperty: (id: string, key: string, value: unknown) => void
  addElement: (parentId: string | null, element: DesignerElement, index: number) => void
  removeElement: (id: string) => void
  moveElement: (id: string, targetParentId: string | null, newIndex: number) => void
  setSchema: (schema: ComponentSchemaDto) => void
  resetCanvas: () => void
}

const INITIAL_STATE = {
  schemaId: null,
  version: null,
  displayName: 'Untitled Component',
  isDirty: false,
  selectedElementId: null,
  rootElement: null,
} as const

export const useDesignerCanvasStore = create<DesignerCanvasState>((set, get) => ({
  ...INITIAL_STATE,

  selectElement: (id) => set({ selectedElementId: id }),

  updateDisplayName: (displayName) => set({ displayName, isDirty: true }),

  updateElementProperty: (id, key, value) => {
    const { rootElement } = get()
    if (!rootElement) return
    const next = cloneTree(rootElement)
    const target = findById(next, id)
    if (!target) return
    target.properties = { ...target.properties, [key]: value }
    set({ rootElement: next, isDirty: true })
  },

  addElement: (parentId, element, index) => {
    const { rootElement } = get()
    if (parentId === null) {
      // Root drop: when no rootElement yet, the dropped element becomes the root.
      // When a root already exists and the caller passes parentId=null, append to root's children —
      // but only if the existing root is a container; never coerce a leaf into having children.
      if (!rootElement) {
        set({ rootElement: element, isDirty: true, selectedElementId: element.id })
        return
      }
      if (!CONTAINER_TYPE_SET.has(rootElement.type)) return
      const next = cloneTree(rootElement)
      next.children = next.children ?? []
      const safeIndex = Math.max(0, Math.min(index, next.children.length))
      next.children.splice(safeIndex, 0, element)
      set({ rootElement: next, isDirty: true, selectedElementId: element.id })
      return
    }
    if (!rootElement) return
    // Schema invariant: only containers can host children. Bail if target is a leaf or missing.
    const target = findById(rootElement, parentId)
    if (!target || !CONTAINER_TYPE_SET.has(target.type)) return
    // Tabs children must be Stacks — the Tabs renderer assumes each child is a
    // Stack carrying `tabName`/`paddingPx`/`contentGapPx`. Reject non-Stack drops
    // at the store boundary so the invariant is enforced from a single place.
    if (target.type === 'Tabs' && element.type !== 'Stack') return
    const next = cloneTree(rootElement)
    const inserted = insertAt(next, parentId, element, index)
    if (!inserted) return
    set({ rootElement: next, isDirty: true, selectedElementId: element.id })
  },

  removeElement: (id) => {
    const { rootElement, selectedElementId } = get()
    if (!rootElement) return
    if (rootElement.id === id) {
      set({ rootElement: null, isDirty: true, selectedElementId: null })
      return
    }
    const next = cloneTree(rootElement)
    const removed = removeById(next, id)
    if (!removed) return
    // Clear the selection if it points to anything inside the removed subtree, not just the root of it.
    const selectionWasRemoved =
      selectedElementId !== null &&
      (selectedElementId === id || findById(removed, selectedElementId) !== null)
    set({
      rootElement: next,
      isDirty: true,
      selectedElementId: selectionWasRemoved ? null : selectedElementId,
    })
  },

  moveElement: (id, targetParentId, newIndex) => {
    const { rootElement } = get()
    if (!rootElement) return
    if (rootElement.id === id) return // root cannot be moved
    if (id === targetParentId) return // cannot drop into self

    // Validate target before mutating: must exist, must be a container, must not be inside the source subtree.
    let targetType: string | null = null
    if (targetParentId !== null) {
      const target = findById(rootElement, targetParentId)
      if (!target) return
      if (!CONTAINER_TYPE_SET.has(target.type)) return
      const source = findById(rootElement, id)
      if (source && findById(source, targetParentId)) return
      targetType = target.type
      // Tabs children must be Stacks — see addElement for the rationale.
      if (target.type === 'Tabs' && source && source.type !== 'Stack') return
    }

    const next = cloneTree(rootElement)
    const removedRaw = removeById(next, id)
    if (!removedRaw) return
    // Strip tab-only properties when a Stack moves out of a Tabs parent into
    // any non-Tabs context (including becoming a root child of a Stack/Row).
    // Without this, a future move back into a Tabs container would resurface
    // stale `tabName`/`paddingPx`/`contentGapPx` that the inspector no longer
    // surfaces, leading to name collisions and unexpected tab geometry.
    const removed =
      removedRaw.type === 'Stack' && targetType !== 'Tabs'
        ? stripTabChildProps(removedRaw)
        : removedRaw

    if (targetParentId === null) {
      // Append to root's children — root must already be a container (validated above on initial tree).
      if (!CONTAINER_TYPE_SET.has(next.type)) return
      next.children = next.children ?? []
      const safeIndex = Math.max(0, Math.min(newIndex, next.children.length))
      next.children.splice(safeIndex, 0, removed)
      set({ rootElement: next, isDirty: true })
      return
    }
    const inserted = insertAt(next, targetParentId, removed, newIndex)
    if (!inserted) return
    set({ rootElement: next, isDirty: true })
  },

  setSchema: (schema) => {
    // The FormForge DTO already delivers `rootElement` as a parsed object (or
    // null). ESG's variant carried it as a serialised JSON string and parsed
    // here — that conversion is no longer needed. `version` tracks the loaded
    // version; until version-specific loading lands we use `latestVersion`.
    set({
      schemaId: schema.designerId,
      version: schema.latestVersion,
      displayName: schema.displayName,
      rootElement: schema.rootElement,
      isDirty: false,
      selectedElementId: null,
    })
  },

  resetCanvas: () => set({ ...INITIAL_STATE }),
}))
