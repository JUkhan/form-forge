// Text Input / TextArea values are trimmed of surrounding whitespace before the
// payload reaches onSave. Other field types (and non-string values) pass through
// untouched.
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
    latestVersion: 1,
    rootElement: root,
    createdAt: '2026-05-27T00:00:00Z',
    updatedAt: null,
    publishedAt: '2026-05-27T00:00:00Z',
  }
}

const schema: DesignerElement = {
  id: 'root',
  type: 'Stack',
  properties: {},
  children: [
    { id: 'a', type: 'Text Input', properties: { label: 'Name', fieldKey: 'name' }, children: [] },
    { id: 'b', type: 'TextArea', properties: { label: 'Bio', fieldKey: 'bio' }, children: [] },
    { id: 'c', type: 'Number Input', properties: { label: 'Age', fieldKey: 'age' }, children: [] },
  ],
}

function submitWith(initialData: Record<string, unknown>) {
  const onSave = vi.fn()
  const submitRef: { current: (() => void) | null } = { current: null }
  render(
    <QueryClientProvider client={makeQC()}>
      <DynamicComponent
        designerId="parent"
        schema={wrapSchema(schema)}
        initialData={initialData}
        onSave={onSave}
        submitRef={submitRef}
      />
    </QueryClientProvider>,
  )
  act(() => {
    submitRef.current?.()
  })
  return onSave.mock.calls[0]?.[0] as Record<string, unknown> | undefined
}

describe('DynamicComponent — text field trimming', () => {
  it('trims leading/trailing whitespace on Text Input and TextArea values', () => {
    const payload = submitWith({ name: '  Ada  ', bio: '\n hello \t', age: 30 })
    expect(payload?.name).toBe('Ada')
    expect(payload?.bio).toBe('hello')
  })

  it('leaves non-text field values untouched', () => {
    const payload = submitWith({ name: 'x', bio: 'y', age: 30 })
    // Number stays a number, not coerced or trimmed.
    expect(payload?.age).toBe(30)
  })

  it('collapses an all-whitespace text value to an empty string', () => {
    const payload = submitWith({ name: '   ', bio: 'y', age: 1 })
    expect(payload?.name).toBe('')
  })
})
