import { describe, it, expect, vi, afterEach } from 'vitest'
import { cleanup, fireEvent, render, within } from '@testing-library/react'
import type { NavMenuItem } from '../../../features/menu/types'

// Identity translator so we can assert on i18n keys directly.
vi.mock('react-i18next', () => ({
  useTranslation: () => ({
    t: (key: string, vars?: Record<string, unknown>) =>
      vars ? `${key}:${JSON.stringify(vars)}` : key,
  }),
}))

interface QueryStub {
  data?: NavMenuItem[]
  isPending: boolean
  isError: boolean
}

let queryStub: QueryStub = { data: [], isPending: false, isError: false }

vi.mock('../../../features/menu/useNavMenusQuery', () => ({
  useNavMenusQuery: () => queryStub,
  NAV_MENUS_QUERY_KEY: ['menus', 'nav'],
}))

// Imports must come AFTER vi.mock so the module binds the stub.
import { Navbar } from '../Navbar'

function setQuery(stub: QueryStub) {
  queryStub = stub
}

describe('Navbar', () => {
  afterEach(() => {
    cleanup()
    setQuery({ data: [], isPending: false, isError: false })
  })

  it('renders the empty message when data is an empty array', () => {
    setQuery({ data: [], isPending: false, isError: false })
    const { getAllByText } = render(<Navbar />)
    expect(getAllByText(/nav\.emptyMessage/).length).toBeGreaterThan(0)
  })

  it('renders the skeleton (no items, no empty message) while loading', () => {
    setQuery({ data: undefined, isPending: true, isError: false })
    const { queryByText, container } = render(<Navbar />)
    expect(queryByText(/nav\.emptyMessage/)).toBeNull()
    // P4 — lock the skeleton behavior: 3 animate-pulse rows render during isPending.
    expect(container.querySelectorAll('.animate-pulse').length).toBe(3)
  })

  it('isError silently renders an empty nav (no emptyMessage, no skeleton)', () => {
    // P1 — spec Task 10: error state is silent fail, not the empty-list copy.
    setQuery({ data: undefined, isPending: false, isError: true })
    const { queryByText, container } = render(<Navbar />)
    expect(queryByText(/nav\.emptyMessage/)).toBeNull()
    expect(container.querySelectorAll('.animate-pulse').length).toBe(0)
  })

  it('renders top-level items in server order', () => {
    setQuery({
      data: [
        { id: 'a', name: 'Alpha', order: 0, icon: null, parentId: null, designerId: null, routePath: null, children: [] },
        { id: 'b', name: 'Bravo', order: 1, icon: null, parentId: null, designerId: null, routePath: null, children: [] },
        { id: 'c', name: 'Charlie', order: 2, icon: null, parentId: null, designerId: null, routePath: null, children: [] },
      ],
      isPending: false,
      isError: false,
    })
    const { getByText } = render(<Navbar />)
    expect(getByText('Alpha')).toBeInTheDocument()
    expect(getByText('Bravo')).toBeInTheDocument()
    expect(getByText('Charlie')).toBeInTheDocument()
  })

  it('reveals sub-menu rows only after the disclosure button is toggled', () => {
    setQuery({
      data: [
        {
          id: 'p',
          name: 'Parent',
          order: 0,
          icon: null,
          parentId: null,
          designerId: null,
          routePath: null,
          children: [
            { id: 'c1', name: 'Child One', order: 0, icon: null, parentId: 'p', designerId: null, routePath: null, children: [] },
            { id: 'c2', name: 'Child Two', order: 1, icon: null, parentId: 'p', designerId: null, routePath: null, children: [] },
          ],
        },
      ],
      isPending: false,
      isError: false,
    })
    const { getByLabelText, queryByText, getByText } = render(<Navbar />)
    expect(queryByText('Child One')).toBeNull()
    expect(queryByText('Child Two')).toBeNull()

    const disclosure = getByLabelText(/nav\.expandSubmenu/)
    fireEvent.click(disclosure)

    expect(getByText('Child One')).toBeInTheDocument()
    expect(getByText('Child Two')).toBeInTheDocument()

    // Toggle closed — children disappear again.
    fireEvent.click(getByLabelText(/nav\.collapseSubmenu/))
    expect(queryByText('Child One')).toBeNull()
  })

  it('tapping a menu item closes the mobile drawer (AC-4)', () => {
    // P2 — AC-4 + spec Task 14 bullet 4: tapping any menu item auto-closes
    // the drawer. The dev's other hamburger test clicks the backdrop instead,
    // which does NOT cover the leaf-button auto-close wiring at Navbar.tsx
    // onLeafClick={closeDrawer}. A regression that removes onLeafClick from
    // the leaf button would silently break AC-4 without this test failing.
    setQuery({
      data: [
        { id: 'a', name: 'Alpha', order: 0, icon: null, parentId: null, designerId: null, routePath: null, children: [] },
      ],
      isPending: false,
      isError: false,
    })
    const { getByLabelText, getByText, queryByLabelText, queryAllByLabelText } = render(<Navbar />)
    // Open the drawer.
    fireEvent.click(getByLabelText(/nav\.openMenu/))
    // closeMenu appears on TWO elements (hamburger toggle + backdrop) — proves drawer is open.
    expect(queryAllByLabelText(/nav\.closeMenu/).length).toBeGreaterThanOrEqual(2)
    // Click the leaf menu item.
    fireEvent.click(getByText('Alpha'))
    // Drawer is closed — hamburger flips back to openMenu, backdrop is gone.
    expect(queryByLabelText(/nav\.openMenu/)).not.toBeNull()
    expect(queryAllByLabelText(/nav\.closeMenu/).length).toBe(0)
  })

  it('mobile hamburger toggles drawer open/closed via aria-expanded', () => {
    setQuery({
      data: [
        { id: 'a', name: 'Alpha', order: 0, icon: null, parentId: null, designerId: null, routePath: null, children: [] },
      ],
      isPending: false,
      isError: false,
    })
    const { getByLabelText, getAllByLabelText, queryByLabelText } = render(<Navbar />)
    const hamburger = getByLabelText(/nav\.openMenu/)
    expect(hamburger.getAttribute('aria-expanded')).toBe('false')
    fireEvent.click(hamburger)
    // After opening, closeMenu appears on TWO elements (hamburger toggle + backdrop).
    const closeButtons = getAllByLabelText(/nav\.closeMenu/)
    expect(closeButtons.length).toBeGreaterThanOrEqual(2)
    // Click backdrop — last in document order — and the hamburger should flip back to openMenu.
    fireEvent.click(closeButtons[closeButtons.length - 1])
    expect(queryByLabelText(/nav\.openMenu/)).not.toBeNull()
  })

  it('MinIO icon renders the placeholder Lucide Box, not an <img> element', () => {
    setQuery({
      data: [
        {
          id: 'a',
          name: 'Has Minio Icon',
          order: 0,
          icon: { type: 'minio', objectKey: 'menus/icons/x.png' },
          parentId: null,
          designerId: null,
          routePath: null,
          children: [],
        },
      ],
      isPending: false,
      isError: false,
    })
    const { container, getByLabelText } = render(<Navbar />)
    // The placeholder NavLucideIcon for MinIO carries the menuIconPlaceholder aria-label.
    const placeholder = getByLabelText(/nav\.menuIconPlaceholder/)
    expect(placeholder).toBeInTheDocument()
    // No <img> tag rendered for MinIO items in v1 (deferred to files-api story).
    expect(container.querySelector('img')).toBeNull()
  })

  it('renders an external routePath as a new-tab anchor', () => {
    setQuery({
      data: [
        {
          id: 'a',
          name: 'External',
          order: 0,
          icon: null,
          parentId: null,
          designerId: null,
          routePath: 'https://example.com',
          children: [],
        },
      ],
      isPending: false,
      isError: false,
    })
    const { getByText } = render(<Navbar />)
    const link = getByText('External').closest('a')
    expect(link).not.toBeNull()
    expect(link?.getAttribute('href')).toBe('https://example.com')
    expect(link?.getAttribute('target')).toBe('_blank')
    expect(link?.getAttribute('rel')).toBe('noopener noreferrer')
  })

  it('unknown lucide icon name falls back to the Box placeholder without throwing', () => {
    setQuery({
      data: [
        {
          id: 'a',
          name: 'Bad Icon',
          order: 0,
          icon: { type: 'lucide', name: 'NotARealIcon_XYZ' },
          parentId: null,
          designerId: null,
          routePath: null,
          children: [],
        },
      ],
      isPending: false,
      isError: false,
    })
    // Renders without throwing AND the item is visible.
    const { getByText, container } = render(<Navbar />)
    expect(getByText('Bad Icon')).toBeInTheDocument()
    // No img — fallback is still a Lucide SVG.
    expect(within(container).queryByRole('img')).toBeNull()
  })
})
