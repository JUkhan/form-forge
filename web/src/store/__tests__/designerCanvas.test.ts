import { beforeEach, describe, expect, it } from 'vitest'
import { createNewElement, useDesignerCanvasStore } from '@/store/designerCanvas'
import type { DesignerElement } from '@/types/designer'

const CONTAINER_TYPES = new Set(['Stack', 'Row', 'Tabs', 'Repeater'])

function makeEl(type: string, id?: string): DesignerElement {
  return {
    id: id ?? crypto.randomUUID(),
    type,
    properties: {},
    children: CONTAINER_TYPES.has(type) ? [] : undefined,
  }
}

function findIdDeep(node: DesignerElement | null, id: string): boolean {
  if (!node) return false
  if (node.id === id) return true
  return node.children?.some((c) => findIdDeep(c, id)) ?? false
}

beforeEach(() => {
  // Use the store's own reset action so future state-shape changes can't
  // leave partial state behind between tests.
  useDesignerCanvasStore.getState().resetCanvas()
})

describe('designerCanvas store — addElement (AC-2, AC-4, AC-6)', () => {
  it('addElement on empty canvas sets rootElement and isDirty', () => {
    const { addElement } = useDesignerCanvasStore.getState()
    const el = makeEl('Stack')
    addElement(null, el, 0)
    const s = useDesignerCanvasStore.getState()
    expect(s.rootElement).not.toBeNull()
    expect(s.isDirty).toBe(true)
    expect(s.selectedElementId).toBe(el.id)
  })

  it('addElement inserts at the specified index (AC-2)', () => {
    const store = useDesignerCanvasStore.getState()
    const root = makeEl('Stack', 'root')
    store.addElement(null, root, 0)

    const a = makeEl('Label', 'a')
    const b = makeEl('Label', 'b')
    useDesignerCanvasStore.getState().addElement('root', a, 0)
    useDesignerCanvasStore.getState().addElement('root', b, 0)

    const children = useDesignerCanvasStore.getState().rootElement!.children!
    expect(children[0].id).toBe('b')
    expect(children[1].id).toBe('a')
  })

  it('addElement clamps out-of-range index to insert within bounds (AC-2)', () => {
    const store = useDesignerCanvasStore.getState()
    const root = makeEl('Stack', 'root')
    store.addElement(null, root, 0)
    const a = makeEl('Label', 'a')
    useDesignerCanvasStore.getState().addElement('root', a, 0)
    // Index above length clamps to the end.
    const big = makeEl('Label', 'big')
    useDesignerCanvasStore.getState().addElement('root', big, 99)
    // Negative index clamps to the start.
    const neg = makeEl('Label', 'neg')
    useDesignerCanvasStore.getState().addElement('root', neg, -5)

    const ids = useDesignerCanvasStore.getState().rootElement!.children!.map((c) => c.id)
    expect(ids).toEqual(['neg', 'a', 'big'])
  })

  it('addElement into a leaf type is silently rejected (AC-4)', () => {
    const store = useDesignerCanvasStore.getState()
    const root = makeEl('Stack', 'root')
    store.addElement(null, root, 0)
    const leaf = makeEl('Label', 'leaf')
    useDesignerCanvasStore.getState().addElement('root', leaf, 0)
    // Reset the dirty flag so we can assert the rejection itself didn't re-flip it.
    useDesignerCanvasStore.setState({ isDirty: false })

    const child = makeEl('Button', 'child')
    useDesignerCanvasStore.getState().addElement('leaf', child, 0)

    const state = useDesignerCanvasStore.getState()
    const leafNode = state.rootElement!.children![0]
    expect(leafNode.children).toBeUndefined()
    expect(state.rootElement!.children).toHaveLength(1)
    expect(findIdDeep(state.rootElement, 'child')).toBe(false)
    expect(state.isDirty).toBe(false)
  })

  it('addElement into Tabs rejects non-Stack children', () => {
    // Use the production factory so the Tabs shape matches what the canvas
    // actually drops onto the tree (one default tab Stack child).
    const tabs = createNewElement('Tabs')
    useDesignerCanvasStore.getState().addElement(null, tabs, 0)
    const tabsId = useDesignerCanvasStore.getState().rootElement!.id

    const label = makeEl('Label', 'lbl')
    useDesignerCanvasStore.getState().addElement(tabsId, label, 1)

    const tabsNode = useDesignerCanvasStore.getState().rootElement!
    expect(tabsNode.children).toHaveLength(1) // unchanged from the default tab
    expect(tabsNode.children?.some((c) => c.id === 'lbl') ?? false).toBe(false)
  })

  it('addElement into Tabs accepts Stack children', () => {
    const tabs = createNewElement('Tabs')
    useDesignerCanvasStore.getState().addElement(null, tabs, 0)
    const tabsId = useDesignerCanvasStore.getState().rootElement!.id

    const stack = makeEl('Stack', 'newStack')
    useDesignerCanvasStore.getState().addElement(tabsId, stack, 1)

    const tabsNode = useDesignerCanvasStore.getState().rootElement!
    expect(tabsNode.children).toHaveLength(2)
    expect(tabsNode.children?.some((c) => c.id === 'newStack') ?? false).toBe(true)
  })
})

describe('designerCanvas store — removeElement (AC-5)', () => {
  it('removeElement removes the element and all its children', () => {
    const store = useDesignerCanvasStore.getState()
    const root = makeEl('Stack', 'root')
    store.addElement(null, root, 0)
    const parent = makeEl('Row', 'parent')
    useDesignerCanvasStore.getState().addElement('root', parent, 0)
    const child = makeEl('Label', 'child')
    useDesignerCanvasStore.getState().addElement('parent', child, 0)

    useDesignerCanvasStore.getState().removeElement('parent')

    const rootChildren = useDesignerCanvasStore.getState().rootElement!.children!
    expect(rootChildren).toHaveLength(0)
  })

  it('removeElement on root element nullifies rootElement', () => {
    const store = useDesignerCanvasStore.getState()
    const root = makeEl('Stack', 'root')
    store.addElement(null, root, 0)
    useDesignerCanvasStore.getState().removeElement('root')

    expect(useDesignerCanvasStore.getState().rootElement).toBeNull()
  })

  it('removeElement clears selectedElementId when it matches the deleted element', () => {
    const store = useDesignerCanvasStore.getState()
    const root = makeEl('Stack', 'root')
    store.addElement(null, root, 0)
    const child = makeEl('Label', 'c')
    useDesignerCanvasStore.getState().addElement('root', child, 0)
    useDesignerCanvasStore.setState({ selectedElementId: 'c' })

    useDesignerCanvasStore.getState().removeElement('c')

    expect(useDesignerCanvasStore.getState().selectedElementId).toBeNull()
  })

  it('removeElement clears selection when selected element is inside a removed subtree', () => {
    const store = useDesignerCanvasStore.getState()
    const root = makeEl('Stack', 'root')
    store.addElement(null, root, 0)
    const parent = makeEl('Row', 'parent')
    useDesignerCanvasStore.getState().addElement('root', parent, 0)
    const child = makeEl('Label', 'child')
    useDesignerCanvasStore.getState().addElement('parent', child, 0)
    useDesignerCanvasStore.setState({ selectedElementId: 'child' })

    useDesignerCanvasStore.getState().removeElement('parent')

    expect(useDesignerCanvasStore.getState().selectedElementId).toBeNull()
  })
})

describe('designerCanvas store — moveElement (AC-3)', () => {
  it('moveElement reorders within the same parent correctly', () => {
    const store = useDesignerCanvasStore.getState()
    const root = makeEl('Stack', 'root')
    store.addElement(null, root, 0)
    const a = makeEl('Label', 'a')
    const b = makeEl('Label', 'b')
    const c = makeEl('Label', 'c')
    useDesignerCanvasStore.getState().addElement('root', a, 0)
    useDesignerCanvasStore.getState().addElement('root', b, 1)
    useDesignerCanvasStore.getState().addElement('root', c, 2)
    useDesignerCanvasStore.getState().moveElement('a', 'root', 3)

    const children = useDesignerCanvasStore.getState().rootElement!.children!
    expect(children.map((ch) => ch.id)).toEqual(['b', 'c', 'a'])
  })

  it('moveElement transfers an element between distinct parents (AC-3)', () => {
    const store = useDesignerCanvasStore.getState()
    const root = makeEl('Stack', 'root')
    store.addElement(null, root, 0)
    const fromBox = makeEl('Row', 'from')
    useDesignerCanvasStore.getState().addElement('root', fromBox, 0)
    const toBox = makeEl('Row', 'to')
    useDesignerCanvasStore.getState().addElement('root', toBox, 1)
    const target = makeEl('Label', 'target')
    useDesignerCanvasStore.getState().addElement('from', target, 0)

    useDesignerCanvasStore.getState().moveElement('target', 'to', 0)

    const tree = useDesignerCanvasStore.getState().rootElement!
    const from = tree.children!.find((c) => c.id === 'from')!
    const to = tree.children!.find((c) => c.id === 'to')!
    expect(from.children).toHaveLength(0)
    expect(to.children).toHaveLength(1)
    expect(to.children![0].id).toBe('target')
  })

  it('moveElement ignores request when id === targetParentId', () => {
    const store = useDesignerCanvasStore.getState()
    const root = makeEl('Stack', 'root')
    store.addElement(null, root, 0)
    const box = makeEl('Row', 'box')
    useDesignerCanvasStore.getState().addElement('root', box, 0)

    const before = structuredClone(useDesignerCanvasStore.getState().rootElement)
    useDesignerCanvasStore.getState().moveElement('box', 'box', 0)
    expect(useDesignerCanvasStore.getState().rootElement).toEqual(before)
  })

  it('moveElement ignores request when target is inside the moved subtree', () => {
    const store = useDesignerCanvasStore.getState()
    const root = makeEl('Stack', 'root')
    store.addElement(null, root, 0)
    const outer = makeEl('Row', 'outer')
    useDesignerCanvasStore.getState().addElement('root', outer, 0)
    const inner = makeEl('Stack', 'inner')
    useDesignerCanvasStore.getState().addElement('outer', inner, 0)

    const before = structuredClone(useDesignerCanvasStore.getState().rootElement)
    useDesignerCanvasStore.getState().moveElement('outer', 'inner', 0)
    expect(useDesignerCanvasStore.getState().rootElement).toEqual(before)
  })

  it('moveElement strips tab-only props when a Stack moves out of a Tabs parent', () => {
    const store = useDesignerCanvasStore.getState()
    const root = makeEl('Stack', 'root')
    store.addElement(null, root, 0)
    const tabs = createNewElement('Tabs')
    useDesignerCanvasStore.getState().addElement('root', tabs, 0)

    const tree0 = useDesignerCanvasStore.getState().rootElement!
    const tabsNode = tree0.children!.find((c) => c.type === 'Tabs')!
    const tabStackId = tabsNode.children![0].id
    // Sanity: the default tab Stack carries the tab-only props.
    expect(tabsNode.children![0].properties.tabName).toBeDefined()

    useDesignerCanvasStore.getState().moveElement(tabStackId, 'root', 1)

    const tree = useDesignerCanvasStore.getState().rootElement!
    const moved = tree.children!.find((c) => c.id === tabStackId)!
    expect(moved.properties.tabName).toBeUndefined()
    expect(moved.properties.paddingPx).toBeUndefined()
    expect(moved.properties.contentGapPx).toBeUndefined()
    const tabsAfter = tree.children!.find((c) => c.type === 'Tabs')!
    expect(tabsAfter.children?.some((c) => c.id === tabStackId) ?? false).toBe(false)
  })
})

describe('designerCanvas store — setSchema (AC-6)', () => {
  it('setSchema loads schema and resets isDirty and selectedElementId', () => {
    const store = useDesignerCanvasStore.getState()
    useDesignerCanvasStore.setState({ isDirty: true, selectedElementId: 'x' })
    const root = makeEl('Stack', 'loaded-root')
    store.setSchema({
      designerId: 'form1',
      displayName: 'Form 1',
      mode: 'CRUD',
      status: 'Draft',
      latestVersion: 1,
      rootElement: root,
      createdAt: new Date().toISOString(),
      updatedAt: null,
      publishedAt: null,
    })
    const s = useDesignerCanvasStore.getState()
    expect(s.isDirty).toBe(false)
    expect(s.selectedElementId).toBeNull()
    expect(s.schemaId).toBe('form1')
    expect(s.version).toBe(1)
    expect(s.displayName).toBe('Form 1')
    expect(s.rootElement).toEqual(root)
  })
})
