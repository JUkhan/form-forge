import { httpClient } from '../auth/httpClient'
import type { DynamicRecord } from './recordListApi'

// TreeView — typed client for the self-referencing tree endpoints.
//
// A node is a DynamicRecord (system columns camelCase, user fieldKeys verbatim)
// plus a derived `_has_children` flag the server computes per row so the UI can
// show an expand affordance without an extra round-trip.
export interface TreeNode extends DynamicRecord {
  _has_children?: boolean
}

export interface TreeLevelResult {
  rows: TreeNode[]
  hasNextPage: boolean
  page: number
  pageSize: number
}

// A dataset-backed tree reads its levels from a dataset VIEW: `keyField` is the column
// holding the node id (mapped to `id`), `parentField` the parent self-reference column.
export interface TreeDatasetSource {
  datasetId: string
  keyField: string
  parentField: string
}

export interface ListTreeNodesParams {
  // Omit (or null) for root nodes; a node id fetches that node's direct children.
  parentId?: string | null
  page?: number
  pageSize?: number
  search?: string
  // Optional per-component auth filter — scopes the level to the user's own nodes.
  authFilterColumn?: string
  // When set, the level is read from the dataset VIEW instead of the provisioned table.
  dataset?: TreeDatasetSource
}

// GET /api/data/{designerId}/tree — one level of the tree, lazy + paginated.
export async function listTreeNodes(
  designerId: string,
  params: ListTreeNodesParams = {},
): Promise<TreeLevelResult> {
  const qs = new URLSearchParams()
  if (params.parentId) qs.set('parentId', params.parentId)
  if (params.page && params.page > 1) qs.set('page', String(params.page))
  if (params.pageSize) qs.set('pageSize', String(params.pageSize))
  if (params.search && params.search.trim() !== '') qs.set('search', params.search.trim())
  if (params.authFilterColumn) qs.set('authFilterColumn', params.authFilterColumn)
  if (params.dataset) {
    qs.set('datasetId', params.dataset.datasetId)
    qs.set('keyField', params.dataset.keyField)
    qs.set('parentField', params.dataset.parentField)
  }
  const query = qs.toString()
  const res = await httpClient.get<TreeLevelResult>(
    `/api/data/${encodeURIComponent(designerId)}/tree${query ? `?${query}` : ''}`,
  )
  // Dataset-backed nodes expose the id as the chosen keyField column; map it to `id` so
  // expand / select / CRUD-by-id work exactly like the provisioned-table tree.
  const keyField = params.dataset?.keyField
  if (keyField) {
    for (const row of res.rows) {
      const rec = row as Record<string, unknown>
      if (rec.id == null && rec[keyField] != null) rec.id = rec[keyField]
    }
  }
  return res
}

export interface SearchTreeParams {
  // Already-resolved filter tree (a ResolvedFilterGroup from resolveTreeSearchFilter, or null
  // when the search box is empty). Kept as `unknown` so the designer's treeSearchFilter resolver
  // stays the single source of the shape — mirrors fetchDatasetRows.
  filters: unknown
  // Optional per-component auth filter — scopes the match + the whole ancestor walk to the user.
  authFilterColumn?: string
  // When set, the search runs over the dataset source instead of the provisioned table.
  dataset?: TreeDatasetSource
  // Parameterized "query"-type dataset: the {_placeholder} values as a JSON object string.
  queryParameters?: string
}

// POST /api/data/{designerId}/tree/search — entire-tree search. Returns the first matching
// node's ancestor path, ROOT-FIRST (the matched node is last), each row carrying `_has_children`
// like a lazily-loaded level, so the caller can reveal/expand straight down to the match. An
// empty/absent filter short-circuits to [] without a round-trip (an empty search must not match).
export async function searchTree(
  designerId: string,
  params: SearchTreeParams,
): Promise<TreeNode[]> {
  if (params.filters == null) return []
  const body: Record<string, unknown> = { filters: params.filters }
  if (params.authFilterColumn) body.authFilterColumn = params.authFilterColumn
  if (params.dataset) {
    body.datasetId = params.dataset.datasetId
    body.keyField = params.dataset.keyField
    body.parentField = params.dataset.parentField
  }
  if (params.queryParameters) body.queryParameters = params.queryParameters
  const res = await httpClient.post<{ rows: TreeNode[] }>(
    `/api/data/${encodeURIComponent(designerId)}/tree/search`,
    body,
  )
  const rows = res.rows ?? []
  // Dataset-backed rows expose the id as the chosen keyField column; map it to `id` so the
  // path nodes expand/select by id exactly like the provisioned-table tree (see listTreeNodes).
  const keyField = params.dataset?.keyField
  if (keyField) {
    for (const row of rows) {
      const rec = row as Record<string, unknown>
      if (rec.id == null && rec[keyField] != null) rec.id = rec[keyField]
    }
  }
  return rows
}

// GET /api/data/{designerId}/tree/descendants — ids of every live descendant of a node.
// Used by the "All select" behavior to cascade selection across un-expanded branches.
export function listTreeDescendantIds(
  designerId: string,
  parentId: string,
  authFilterColumn?: string,
  dataset?: TreeDatasetSource,
): Promise<string[]> {
  const qs = new URLSearchParams({ parentId })
  if (authFilterColumn) qs.set('authFilterColumn', authFilterColumn)
  if (dataset) {
    qs.set('datasetId', dataset.datasetId)
    qs.set('keyField', dataset.keyField)
    qs.set('parentField', dataset.parentField)
  }
  return httpClient
    .get<{ ids: string[] }>(
      `/api/data/${encodeURIComponent(designerId)}/tree/descendants?${qs.toString()}`,
    )
    .then((r) => r.ids ?? [])
}

// GET /api/data/{designerId}/tree/ancestors — ids of every ancestor of the given
// nodes (recursive, walks up). Used to reveal/mark the path to a selection seeded
// when editing a record. Empty `ids` short-circuits without a round-trip.
export function listTreeAncestorIds(
  designerId: string,
  ids: string[],
  authFilterColumn?: string,
  dataset?: TreeDatasetSource,
): Promise<string[]> {
  const cleaned = ids.map((s) => s.trim()).filter(Boolean)
  if (cleaned.length === 0) return Promise.resolve([])
  const qs = new URLSearchParams({ ids: cleaned.join(',') })
  if (authFilterColumn) qs.set('authFilterColumn', authFilterColumn)
  if (dataset) {
    qs.set('datasetId', dataset.datasetId)
    qs.set('keyField', dataset.keyField)
    qs.set('parentField', dataset.parentField)
  }
  return httpClient
    .get<{ ids: string[] }>(
      `/api/data/${encodeURIComponent(designerId)}/tree/ancestors?${qs.toString()}`,
    )
    .then((r) => r.ids ?? [])
}

// POST /api/data/{designerId}/tree — create a node. `parentId` null → a root node;
// otherwise the new node is a child of that parent (writes the self-FK server-side).
// `authFilterColumn`, when set, makes the server stamp it with the current user.
export function createTreeNode(
  designerId: string,
  parentId: string | null,
  payload: Record<string, unknown>,
  authFilterColumn?: string,
): Promise<DynamicRecord> {
  const body: Record<string, unknown> = { ...payload, parentId }
  if (authFilterColumn) body.authFilterColumn = authFilterColumn
  return httpClient.post<DynamicRecord>(
    `/api/data/${encodeURIComponent(designerId)}/tree`,
    body,
  )
}
