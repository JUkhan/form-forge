import { useQuery } from '@tanstack/react-query'
import { getCatalog } from './datasetApi'
import type { CatalogResponse } from './datasetApi'

// AR-69 Decision 6.13 — catalog query key
export const CATALOG_QUERY_KEY = ['datasets', 'catalog'] as const

export function useCatalogQuery() {
  return useQuery<CatalogResponse>({
    queryKey: CATALOG_QUERY_KEY,
    queryFn: getCatalog,
    staleTime: 5 * 60 * 1000, // 5 min — mirrors backend cache TTL (AR-62)
  })
}
