// The File field shows a Download action (between Change and Remove) only when a
// file is already saved — not when empty and not for a just-picked pending file.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import DynamicComponent from '../DynamicComponent'
import type { ComponentSchemaDto, DesignerElement } from '@/types/designer'

const { getDownloadUrl } = vi.hoisted(() => ({
  getDownloadUrl: vi.fn(async () => ({ url: 'https://minio.local/acme/d/doc_1.pdf?dl=1' })),
}))

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (k: string) => k }),
}))
vi.mock('@/features/auth/httpClient', () => ({ httpClient: { get: vi.fn() } }))
vi.mock('@/features/designer/filesApi', () => ({
  filesApi: {
    uploadFile: vi.fn(),
    deleteFile: vi.fn(),
    getPresignedUrl: vi.fn(async () => ({ url: 'https://minio.local/x' })),
    getDownloadUrl,
  },
}))

afterEach(() => {
  cleanup()
  getDownloadUrl.mockClear()
})

const root: DesignerElement = {
  id: 'root',
  type: 'Stack',
  properties: {},
  children: [
    { id: 'f', type: 'File', properties: { label: 'Doc', fieldKey: 'doc' }, children: [] },
  ],
}

const schema: ComponentSchemaDto = {
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

function renderWith(initialData: Record<string, unknown>) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={qc}>
      <DynamicComponent designerId="parent" schema={schema} initialData={initialData} />
    </QueryClientProvider>,
  )
}

describe('File field — download action', () => {
  it('is hidden when no file has been saved', () => {
    const { queryByTitle } = renderWith({})
    expect(queryByTitle('designer.renderer.fileDownload')).toBeNull()
  })

  it('appears between Change and Remove for a saved file and requests a download URL', async () => {
    const { getByTitle } = renderWith({ doc: 'parent/doc_1.pdf' })
    const change = getByTitle('designer.renderer.fileChange')
    const download = getByTitle('designer.renderer.fileDownload')
    const remove = getByTitle('designer.renderer.fileRemove')

    expect(change.nextElementSibling).toBe(download)
    expect(download.nextElementSibling).toBe(remove)

    fireEvent.click(download)
    await waitFor(() => expect(getDownloadUrl).toHaveBeenCalledWith('parent/doc_1.pdf'))
  })
})
