// Story 7.4 AC-5: axe-core component-level gate. Renders DynamicComponent
// against representative schema fixtures and asserts zero CRITICAL violations
// from the WCAG 2.1 AA ruleset.
//
// Why axe-core directly (not jest-axe / vitest-axe wrappers): fewer deps,
// the wrapper APIs add no ergonomic value over the helper below, and the
// underlying `axe-core` package is already a peer dep of @axe-core/playwright
// — installing both is one resolution, not two.
//
// Why filter `impact === 'critical'`: the AC explicitly bounds the v1 gate to
// criticals; serious/moderate/minor are surfaced for follow-up but do not
// block. Loosening the filter later only requires touching this helper.
import { afterEach, describe, it, vi } from 'vitest'
import { cleanup, render } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import axe, { type Result } from 'axe-core'
import DynamicComponent from '../DynamicComponent'
import type { ComponentSchemaDto, DesignerElement } from '@/types/designer'

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (k: string) => k }),
}))

vi.mock('@/features/auth/httpClient', () => ({ httpClient: { get: vi.fn() } }))
vi.mock('@/features/designer/filesApi', () => ({ filesApi: { upload: vi.fn(), getPresignedUrl: vi.fn() } }))

// Story 7.3 Dev Notes §16 / Story 7.4 Task 4 — this project does NOT
// auto-register cleanup in Vitest setup. Without this, the second test renders
// into the first test's DOM and duplicate-id rules misfire.
afterEach(cleanup)

function makeQC() {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } })
}

function wrapSchema(root: DesignerElement): ComponentSchemaDto {
  return {
    designerId: 'a11y-test',
    displayName: 'A11y Fixture',
    mode: 'CRUD',
    status: 'Published',
    latestVersion: 1,
    rootElement: root,
    createdAt: '2026-05-27T00:00:00Z',
    updatedAt: null,
    publishedAt: '2026-05-27T00:00:00Z',
  }
}

// Scope axe to the WCAG 2.1 AA ruleset and the rendered subtree only — running
// against `document` would pull in the jsdom <html>/<body> chrome which has no
// lang attribute and would otherwise produce a spurious 'html-has-lang'
// failure that has nothing to do with the form under test.
async function assertNoAxeCriticalViolations(container: HTMLElement) {
  const results = await axe.run(container, {
    runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa'] },
  })
  const critical: Result[] = results.violations.filter((v) => v.impact === 'critical')
  if (critical.length > 0) {
    const msg = critical
      .map((v) => `[${v.id}] ${v.description}\n  ${v.nodes.map((n) => n.html).join('\n  ')}`)
      .join('\n')
    throw new Error(`axe found ${critical.length} critical violation(s):\n${msg}`)
  }
}

const simpleSchema: DesignerElement = {
  id: 'root',
  type: 'Stack',
  properties: {},
  children: [
    {
      id: 'first-name',
      type: 'Text Input',
      properties: { label: 'First Name', bindTo: 'firstName', required: true },
    },
    {
      id: 'age',
      type: 'Number Input',
      properties: { label: 'Age', bindTo: 'age' },
    },
    {
      id: 'is-active',
      type: 'Checkbox',
      properties: { label: 'Active', bindTo: 'isActive' },
    },
  ],
}

const dropdownAndRepeaterSchema: DesignerElement = {
  id: 'root',
  type: 'Stack',
  properties: {},
  children: [
    {
      id: 'category',
      type: 'Dropdown',
      properties: { label: 'Category', bindTo: 'category', options: 'A, B, C' },
    },
    {
      id: 'items',
      type: 'Repeater',
      properties: { label: 'Items', bindTo: 'items', rowDesignerId: 'item-row' },
      children: [
        {
          id: 'item-name',
          type: 'Text Input',
          properties: { label: 'Item Name', bindTo: 'itemName' },
        },
      ],
    },
  ],
}

describe('DynamicComponent a11y', () => {
  it('simple form (text/number/checkbox) has no critical axe violations', async () => {
    const { container } = render(
      <QueryClientProvider client={makeQC()}>
        <DynamicComponent
          designerId="a11y-test"
          schema={wrapSchema(simpleSchema)}
          initialData={{}}
          onSave={vi.fn()}
        />
      </QueryClientProvider>,
    )
    await assertNoAxeCriticalViolations(container)
  })

  it('form with server-injected validation errors has no critical violations (verifies aria-describedby linkage)', async () => {
    const { container } = render(
      <QueryClientProvider client={makeQC()}>
        <DynamicComponent
          designerId="a11y-test"
          schema={wrapSchema(simpleSchema)}
          initialData={{}}
          onSave={vi.fn()}
          serverErrors={{ firstName: 'Required field' }}
        />
      </QueryClientProvider>,
    )
    await assertNoAxeCriticalViolations(container)
  })

  it('dropdown + repeater schema has no critical violations', async () => {
    const { container } = render(
      <QueryClientProvider client={makeQC()}>
        <DynamicComponent
          designerId="a11y-test"
          schema={wrapSchema(dropdownAndRepeaterSchema)}
          initialData={{}}
          onSave={vi.fn()}
        />
      </QueryClientProvider>,
    )
    await assertNoAxeCriticalViolations(container)
  })
})
