// Regression: a Repeater's rows must round-trip through the data API as a
// `children` map keyed by rowDesignerId (the child table identity) — NOT as a
// flat field under the Repeater's own fieldKey. Covers the read seed
// (children[rowDesignerId] → formData[fieldKey]) and the write assembly
// (formData[fieldKey] → payload.children[rowDesignerId]).
import { afterEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, render } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import DynamicComponent from '../DynamicComponent'
import type { ComponentSchemaDto, DesignerElement } from '@/types/designer'

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (k: string) => k }),
}))
vi.mock('@/features/auth/httpClient', () => ({ httpClient: { get: vi.fn() } }))
vi.mock('@/features/designer/filesApi', () => ({
  filesApi: { upload: vi.fn(), getPresignedUrl: vi.fn() },
}))

afterEach(cleanup)

function makeQC() {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } })
}

function wrapSchema(root: DesignerElement): ComponentSchemaDto {
  return {
    designerId: 'parent',
    displayName: 'Parent',
    mode: 'CRUD',
    status: 'Published',
    latestVersion: 3,
    rootElement: root,
    createdAt: '2026-05-27T00:00:00Z',
    updatedAt: null,
    publishedAt: '2026-05-27T00:00:00Z',
  }
}

const repeaterSchema: DesignerElement = {
  id: 'root',
  type: 'Stack',
  properties: {},
  children: [
    { id: 't', type: 'Text Input', properties: { label: 'Title', fieldKey: 'title' }, children: [] },
    {
      id: 'rep',
      type: 'Repeater',
      // fieldKey (relation name) differs from rowDesignerId (child designer id),
      // so the test proves the translation actually happens.
      properties: { label: 'Lines', fieldKey: 'line_items', rowDesignerId: 'line_item', rowVersion: 3 },
      children: [],
    },
  ],
}

describe('DynamicComponent — repeater children mapping', () => {
  it('seeds loaded children into the repeater and re-emits them keyed by rowDesignerId', () => {
    const onSave = vi.fn()
    const submitRef: { current: (() => void) | null } = { current: null }

    render(
      <QueryClientProvider client={makeQC()}>
        <DynamicComponent
          designerId="parent"
          schema={wrapSchema(repeaterSchema)}
          initialData={{ title: 'Invoice', children: { line_item: [{ id: 'g1', note: 'a' }] } }}
          onSave={onSave}
          submitRef={submitRef}
        />
      </QueryClientProvider>,
    )

    act(() => {
      submitRef.current?.()
    })

    expect(onSave).toHaveBeenCalledTimes(1)
    const payload = onSave.mock.calls[0][0] as Record<string, unknown>
    expect(payload.title).toBe('Invoice')
    // Rows submitted under the child designer id, not the repeater fieldKey.
    expect(payload.children).toEqual({ line_item: [{ id: 'g1', note: 'a' }] })
    expect(payload).not.toHaveProperty('line_items')
  })
})
