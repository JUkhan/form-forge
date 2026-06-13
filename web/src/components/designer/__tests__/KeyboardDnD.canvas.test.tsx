import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { createRef } from 'react'
import { cleanup, fireEvent, render } from '@testing-library/react'

// Identity translator so we can locate keyboard-DnD slots by their i18n key.
vi.mock('react-i18next', () => ({
  useTranslation: () => ({
    t: (key: string) => key,
  }),
}))

// In-memory mock store. Mirrors the Zustand selector contract: the production
// store reads `useDesignerCanvasStore((s) => s.foo)`, so the mock returns the
// selector applied to a fixed snapshot. The root is a Stack with one Label
// leaf so the canvas paints three DropZones (before, between, after) plus
// makes the leaf itself focusable for canvas-pickup tests.
const mockStore = {
  rootElement: {
    id: 'root',
    type: 'Stack',
    properties: {},
    children: [
      { id: 'leaf1', type: 'Label', properties: {}, children: [] },
    ],
  },
  selectedElementId: null,
  addElement: vi.fn(),
  moveElement: vi.fn(),
  removeElement: vi.fn(),
  selectElement: vi.fn(),
  schemaId: 'test-schema',
  version: 1,
  displayName: 'Test',
  isDirty: false,
  setSchema: vi.fn(),
  updateDisplayName: vi.fn(),
  updateElementProperty: vi.fn(),
  resetCanvas: vi.fn(),
}

vi.mock('@/store/designerCanvas', () => ({
  useDesignerCanvasStore: (selector: (s: typeof mockStore) => unknown) =>
    selector(mockStore),
  createNewElement: (type: string) => ({
    id: `new-${type}`,
    type,
    properties: {},
    children: [],
  }),
  createNewTabStack: (n: number) => ({
    id: `tab-${n}`,
    type: 'Stack',
    properties: {},
    children: [],
  }),
}))

// Imports must come AFTER vi.mock so DesignerCanvas binds the stub store.
import DesignerCanvas from '../DesignerCanvas'
import type { DragData } from '../dnd'
import type { UseKeyboardDnDReturn } from '../useKeyboardDnD'

function makeStubHook(
  pickedUp: DragData | null,
): UseKeyboardDnDReturn<DragData> {
  return {
    pickedUp,
    announcement: '',
    pickUp: vi.fn(),
    commit: vi.fn(),
    cancel: vi.fn(),
    originatorRef: createRef<HTMLElement | null>() as React.MutableRefObject<HTMLElement | null>,
    insertedIdRef: createRef<string | null>() as React.MutableRefObject<string | null>,
  }
}

describe('DesignerCanvas keyboard DnD wiring', () => {
  beforeEach(() => {
    cleanup()
    mockStore.addElement.mockReset()
    mockStore.moveElement.mockReset()
    mockStore.removeElement.mockReset()
    mockStore.selectElement.mockReset()
  })
  afterEach(() => {
    cleanup()
  })

  it('DropZones are aria-hidden (not keyboard-focusable) when kbdDnD prop is omitted', () => {
    const { container } = render(<DesignerCanvas />)
    const slots = container.querySelectorAll('[data-dropzone]')
    expect(slots.length).toBe(0)
    // Scope the assertion to elements that ARE DropZones — they live as direct
    // descendants of CanvasContainer's layout div with the rounded-sm class.
    // A loose `[aria-hidden="true"]` query would match decorative lucide icons.
    const droplikeHidden = container.querySelectorAll(
      'div.rounded-sm[aria-hidden="true"]',
    )
    expect(droplikeHidden.length).toBeGreaterThanOrEqual(2)
  })

  it('DropZones become focusable buttons with aria-label when kbdDnD.pickedUp is non-null', () => {
    const stub = makeStubHook({ source: 'PALETTE', elementType: 'Label' })
    const { container } = render(<DesignerCanvas kbdDnD={stub} />)
    const slots = container.querySelectorAll<HTMLElement>('[data-dropzone]')
    // Stack with one child → 2 inter-child DropZones (before + after).
    expect(slots.length).toBeGreaterThanOrEqual(2)
    for (const slot of slots) {
      expect(slot.getAttribute('tabindex')).toBe('0')
      expect(slot.getAttribute('role')).toBe('button')
      expect(slot.getAttribute('aria-label')).toBeTruthy()
    }
  })

  it('Space on a focused DropZone with pickedUp set triggers addElement + commit (with inserted id)', () => {
    const stub = makeStubHook({
      source: 'PALETTE',
      elementType: 'Label',
      displayLabel: 'Label',
    })
    const { container } = render(<DesignerCanvas kbdDnD={stub} />)
    const firstSlot = container.querySelector<HTMLElement>(
      '[data-dropzone="root:0"]',
    )
    expect(firstSlot).toBeTruthy()
    if (!firstSlot) return
    fireEvent.keyDown(firstSlot, { key: ' ' })
    expect(mockStore.addElement).toHaveBeenCalledTimes(1)
    expect(mockStore.addElement).toHaveBeenCalledWith(
      'root',
      expect.objectContaining({ type: 'Label' }),
      0,
    )
    expect(stub.commit).toHaveBeenCalledTimes(1)
    // Second arg is the inserted element id so DesignerPage can restore focus.
    expect(stub.commit).toHaveBeenCalledWith(
      expect.stringContaining('designer.keyboard.dropped'),
      'new-Label',
    )
  })

  it('Escape on a focused DropZone with pickedUp set fires kbdDnD.cancel', () => {
    const stub = makeStubHook({ source: 'PALETTE', elementType: 'Label' })
    const { container } = render(<DesignerCanvas kbdDnD={stub} />)
    const firstSlot = container.querySelector<HTMLElement>(
      '[data-dropzone="root:0"]',
    )
    expect(firstSlot).toBeTruthy()
    if (!firstSlot) return
    fireEvent.keyDown(firstSlot, { key: 'Escape' })
    expect(stub.cancel).toHaveBeenCalledTimes(1)
    expect(stub.cancel).toHaveBeenCalledWith('designer.keyboard.cancelled')
    expect(mockStore.addElement).not.toHaveBeenCalled()
  })

  it('Space on a CanvasLeaf fires kbdDnD.pickUp with CANVAS source and originator', () => {
    const stub = makeStubHook(null)
    const { container } = render(<DesignerCanvas kbdDnD={stub} />)
    const leaf = container.querySelector<HTMLElement>('[data-element-id="leaf1"]')
    expect(leaf).toBeTruthy()
    if (!leaf) return
    fireEvent.keyDown(leaf, { key: ' ' })
    expect(stub.pickUp).toHaveBeenCalledTimes(1)
    expect(stub.pickUp).toHaveBeenCalledWith(
      { source: 'CANVAS', id: 'leaf1', type: 'Label' },
      'designer.keyboard.pickedUp',
      leaf,
    )
  })

  it('Space on a CanvasLeaf does NOT pickUp when a pickup is already active', () => {
    const stub = makeStubHook({ source: 'PALETTE', elementType: 'Label' })
    const { container } = render(<DesignerCanvas kbdDnD={stub} />)
    const leaf = container.querySelector<HTMLElement>('[data-element-id="leaf1"]')
    expect(leaf).toBeTruthy()
    if (!leaf) return
    fireEvent.keyDown(leaf, { key: ' ' })
    expect(stub.pickUp).not.toHaveBeenCalled()
  })

  it('CanvasLeaf gets no tabIndex when kbdDnD prop is omitted; never carries deprecated aria-grabbed', () => {
    // Story 7.4: aria-grabbed (deprecated in ARIA 1.1, removed in ARIA 1.2) is
    // replaced by the page-level aria-live region; the canvas container itself
    // must NEVER expose aria-grabbed, with or without keyboard DnD wired.
    const { container: c1 } = render(<DesignerCanvas />)
    const leaf1 = c1.querySelector<HTMLElement>('[data-element-id="leaf1"]')
    expect(leaf1).toBeTruthy()
    if (!leaf1) return
    expect(leaf1.hasAttribute('tabindex')).toBe(false)
    expect(leaf1.hasAttribute('aria-grabbed')).toBe(false)

    const stub = makeStubHook(null)
    const { container: c2 } = render(<DesignerCanvas kbdDnD={stub} />)
    const leaf2 = c2.querySelector<HTMLElement>('[data-element-id="leaf1"]')
    expect(leaf2).toBeTruthy()
    if (!leaf2) return
    expect(leaf2.hasAttribute('aria-grabbed')).toBe(false)
  })
})
