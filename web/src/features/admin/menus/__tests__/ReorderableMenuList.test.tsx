import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { cleanup, fireEvent, render } from '@testing-library/react'
import type { MenuListItem } from '../../../menu/types'

// U+200B = zero-width space — useKeyboardDnD appends it to every other
// announcement so aria-live re-fires on identical successive messages.
// Built via fromCharCode so no-irregular-whitespace doesn't flag a literal.
const ZWSP = String.fromCharCode(0x200b)
const stripZwsp = (s: string) => s.split(ZWSP).join('')

// Identity translator so we can match strings via their i18n key.
vi.mock('react-i18next', () => ({
  useTranslation: () => ({
    t: (key: string, vars?: Record<string, unknown>) =>
      vars ? `${key}:${JSON.stringify(vars)}` : key,
  }),
}))

const seededMenus: MenuListItem[] = [
  { id: 'a', name: 'A', order: 0, isActive: true, parentId: null, createdAt: '' },
  { id: 'b', name: 'B', order: 1, isActive: true, parentId: null, createdAt: '' },
  { id: 'c', name: 'C', order: 2, isActive: true, parentId: null, createdAt: '' },
]

const reorderQueryStub = {
  data: { data: seededMenus, total: 3, page: 1, pageSize: 256, totalPages: 1 },
  isLoading: false,
  isError: false,
}

// The component calls .mutate() (NOT .mutateAsync()) — see the P4 comment on
// ReorderableMenuList.tsx:onSave. Provide both methods on the stub so a future
// switch back to mutateAsync doesn't silently break the assertions.
const mutateMock = vi.fn()
const mutateAsyncMock = vi.fn().mockResolvedValue(undefined)
const reorderMutationStub = {
  mutate: mutateMock,
  mutateAsync: mutateAsyncMock,
  isPending: false,
}

vi.mock('../useMenusAdminQuery', () => ({
  useMenusAdminQuery: () => reorderQueryStub,
  MENUS_ADMIN_QUERY_KEY: ['admin', 'menus'],
}))

vi.mock('../menuAdminMutations', () => ({
  useReorderMenusMutation: () => reorderMutationStub,
}))

// Imports must come AFTER vi.mock so the module binds the stubs.
import { ReorderableMenuList } from '../ReorderableMenuList'

function buildDataTransfer(): DataTransfer {
  const store: Record<string, string> = {}
  const types: string[] = []
  return {
    setData: (type: string, value: string) => {
      store[type] = value
      if (!types.includes(type)) types.push(type)
    },
    getData: (type: string) => store[type] ?? '',
    types,
    effectAllowed: 'move',
    dropEffect: 'move',
  } as unknown as DataTransfer
}

describe('ReorderableMenuList', () => {
  beforeEach(() => {
    mutateMock.mockClear()
    mutateAsyncMock.mockClear()
  })
  afterEach(() => {
    cleanup()
  })

  it('renders all top-level rows in server order', () => {
    const { getAllByText } = render(<ReorderableMenuList parentId={null} />)
    const labels = getAllByText(/^[ABC]$/).map((el) => el.textContent)
    expect(labels).toEqual(['A', 'B', 'C'])
  })

  it('Save is disabled when the draft equals the server snapshot', () => {
    const { getByText } = render(<ReorderableMenuList parentId={null} />)
    const saveBtn = getByText(/admin\.menus\.saveOrderButton/)
    expect((saveBtn as HTMLButtonElement).disabled).toBe(true)
  })

  it('drag-and-drop reorder calls the mutation with the expected payload', () => {
    const { container, getByText } = render(<ReorderableMenuList parentId={null} />)
    const items = container.querySelectorAll<HTMLLIElement>('[data-reorder-row-id]')
    expect(items).toHaveLength(3)

    const dt = buildDataTransfer()
    // Drag A (index 0) onto C (index 2): splice from 0, insert at index 2 → [B, C, A].
    fireEvent.dragStart(items[0], { dataTransfer: dt })
    fireEvent.dragOver(items[2], { dataTransfer: dt })
    fireEvent.drop(items[2], { dataTransfer: dt })

    const saveBtn = getByText(/admin\.menus\.saveOrderButton/) as HTMLButtonElement
    expect(saveBtn.disabled).toBe(false)
    fireEvent.click(saveBtn)

    expect(mutateMock).toHaveBeenCalledTimes(1)
    expect(mutateMock).toHaveBeenCalledWith([
      { id: 'b', order: 0 },
      { id: 'c', order: 1 },
      { id: 'a', order: 2 },
    ])
  })

  it('rejects cross-scope drops (parentId mismatch in payload)', () => {
    const { container, getByText } = render(<ReorderableMenuList parentId={null} />)
    const items = container.querySelectorAll<HTMLLIElement>('[data-reorder-row-id]')

    const dt = buildDataTransfer()
    dt.setData(
      'application/x-formforge-menu-reorder',
      JSON.stringify({ id: 'foreign', parentId: 'some-other-parent' }),
    )
    fireEvent.drop(items[1], { dataTransfer: dt })

    const saveBtn = getByText(/admin\.menus\.saveOrderButton/) as HTMLButtonElement
    expect(saveBtn.disabled).toBe(true)
    expect(mutateMock).not.toHaveBeenCalled()
  })
})

describe('ReorderableMenuList keyboard DnD', () => {
  beforeEach(() => {
    mutateMock.mockClear()
    mutateAsyncMock.mockClear()
  })
  afterEach(() => {
    cleanup()
  })

  it('Space + ArrowDown + Space reorders rows and announces every step', () => {
    const { container, getByRole, getByText } = render(<ReorderableMenuList parentId={null} />)
    const items = container.querySelectorAll<HTMLLIElement>('[data-reorder-row-id]')

    const liveRegion = getByRole('status')
    items[0].focus()

    fireEvent.keyDown(items[0], { key: ' ' })
    expect(stripZwsp(liveRegion.textContent ?? '')).toContain('admin.menus.reorderPickup')

    fireEvent.keyDown(items[0], { key: 'ArrowDown' })
    expect(stripZwsp(liveRegion.textContent ?? '')).toContain('admin.menus.reorderMove')

    fireEvent.keyDown(items[0], { key: ' ' })
    expect(stripZwsp(liveRegion.textContent ?? '')).toContain('admin.menus.reorderCommit')

    const saveBtn = getByText(/admin\.menus\.saveOrderButton/) as HTMLButtonElement
    expect(saveBtn.disabled).toBe(false)
    fireEvent.click(saveBtn)

    // After moving A down one slot: [B, A, C].
    expect(mutateMock).toHaveBeenCalledWith([
      { id: 'b', order: 0 },
      { id: 'a', order: 1 },
      { id: 'c', order: 2 },
    ])
  })

  it('Escape after pickup restores the pre-pickup draft and announces cancellation', () => {
    const { container, getByRole, getByText } = render(<ReorderableMenuList parentId={null} />)
    const items = container.querySelectorAll<HTMLLIElement>('[data-reorder-row-id]')

    items[0].focus()
    fireEvent.keyDown(items[0], { key: ' ' })
    fireEvent.keyDown(items[0], { key: 'ArrowDown' })
    fireEvent.keyDown(items[0], { key: 'Escape' })

    const liveRegion = getByRole('status')
    expect(stripZwsp(liveRegion.textContent ?? '')).toBe('admin.menus.reorderCancelled')

    const saveBtn = getByText(/admin\.menus\.saveOrderButton/) as HTMLButtonElement
    expect(saveBtn.disabled).toBe(true)
  })
})
