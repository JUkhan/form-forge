import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import type { ReactNode } from 'react'

vi.mock('react-i18next', () => ({
  useTranslation: () => ({
    t: (key: string, vars?: Record<string, unknown>) =>
      vars ? `${key}:${JSON.stringify(vars)}` : key,
  }),
}))

vi.mock('@tanstack/react-router', () => ({
  Link: ({ to, children, ...rest }: { to: string; children: ReactNode } & Record<string, unknown>) => (
    <a href={to} {...rest}>
      {children}
    </a>
  ),
  createFileRoute: () => () => ({}),
}))

// Imports must come AFTER vi.mock so the module binds the stubs.
import { AuditLogsPage } from '../admin/audit'

describe('AuditLogsPage', () => {
  afterEach(cleanup)

  it('renders both schema and mutation audit sections', () => {
    render(<AuditLogsPage />)
    screen.getByText('admin.audit.title')
    screen.getByText('admin.audit.subtitle')
    screen.getByText('admin.audit.schemaSection')
    screen.getByText('admin.audit.schemaDesc')
    screen.getByText('admin.audit.mutationSection')
    screen.getByText('admin.audit.mutationDesc')
  })

  it('schema section links to /designer/library and mutation section links to /admin/menus', () => {
    render(<AuditLogsPage />)
    const libraryLink = screen.getByText('admin.audit.goToLibrary')
    expect(libraryLink.tagName).toBe('A')
    expect(libraryLink.getAttribute('href')).toBe('/designer/library')

    const menusLink = screen.getByText('admin.audit.goToMenus')
    expect(menusLink.tagName).toBe('A')
    expect(menusLink.getAttribute('href')).toBe('/admin/menus')
  })
})
