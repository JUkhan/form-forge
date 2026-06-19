import { describe, it, expect, vi, beforeEach } from 'vitest'
import { searchTree } from '../treeApi'

vi.mock('../../auth/httpClient', () => ({
  httpClient: {
    post: vi.fn(() => Promise.resolve({ rows: [] })),
  },
}))

const RESOLVED_FILTER = {
  id: 'root',
  kind: 'group',
  combinator: 'AND',
  items: [{ id: 'c1', kind: 'condition', tableName: '', columnName: 'name', operator: 'ILIKE', value: 'foo' }],
}

describe('searchTree', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('short-circuits to [] without a request when filters is null', async () => {
    const { httpClient } = await import('../../auth/httpClient')
    const rows = await searchTree('org_unit', { filters: null })
    expect(rows).toEqual([])
    expect(httpClient.post).not.toHaveBeenCalled()
  })

  it('POSTs the filters to the tree/search endpoint and returns rows', async () => {
    const { httpClient } = await import('../../auth/httpClient')
    vi.mocked(httpClient.post).mockResolvedValueOnce({
      rows: [{ id: 'a', name: 'root' }, { id: 'b', name: 'child' }],
    })
    const rows = await searchTree('org_unit', {
      filters: RESOLVED_FILTER,
      authFilterColumn: 'owner_id',
    })
    expect(httpClient.post).toHaveBeenCalledWith('/api/data/org_unit/tree/search', {
      filters: RESOLVED_FILTER,
      authFilterColumn: 'owner_id',
    })
    expect(rows.map((r) => r.id)).toEqual(['a', 'b'])
  })

  it('sends the dataset source and maps keyField → id on the returned rows', async () => {
    const { httpClient } = await import('../../auth/httpClient')
    vi.mocked(httpClient.post).mockResolvedValueOnce({
      rows: [{ node_key: 'k1', label: 'Root' }],
    })
    const rows = await searchTree('org_unit', {
      filters: RESOLVED_FILTER,
      dataset: { datasetId: 'ds-1', keyField: 'node_key', parentField: 'parent_key' },
    })
    expect(httpClient.post).toHaveBeenCalledWith('/api/data/org_unit/tree/search', {
      filters: RESOLVED_FILTER,
      datasetId: 'ds-1',
      keyField: 'node_key',
      parentField: 'parent_key',
    })
    // keyField value is copied to `id` so the path nodes behave like the provisioned tree.
    expect(rows[0].id).toBe('k1')
  })

  it('encodes the designerId in the path', async () => {
    const { httpClient } = await import('../../auth/httpClient')
    await searchTree('with space', { filters: RESOLVED_FILTER })
    expect(httpClient.post).toHaveBeenCalledWith('/api/data/with%20space/tree/search', expect.anything())
  })
})
