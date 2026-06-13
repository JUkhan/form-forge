import { useQuery, keepPreviousData } from '@tanstack/react-query'
import { listDatasets } from './datasetApi'
import type { ListDatasetsParams, PagedResult, DatasetSummary } from './datasetApi'

// AR-69 Decision 6.13 — canonical query key prefix for list invalidation.
// datasetMutations.ts references this so invalidation matches by prefix without
// duplicating the string literal.
export const DATASETS_LIST_QUERY_KEY = ['datasets', 'list'] as const

export function useDatasetListQuery(page: number, pageSize: number, params?: ListDatasetsParams) {
  return useQuery<PagedResult<DatasetSummary>>({
    queryKey: [...DATASETS_LIST_QUERY_KEY, { page, pageSize, search: params?.search, sort: params?.sort }],
    queryFn: () => listDatasets(page, pageSize, params),
    staleTime: 30_000,
    placeholderData: keepPreviousData,
  })
}
