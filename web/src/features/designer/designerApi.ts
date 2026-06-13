import { httpClient } from '../auth/httpClient'
import type {
  ComponentSchemaDto,
  ComponentSchemaListItem,
} from '../../types/designer'
import type { PagedResult } from '../admin/users/types'

// FormForge designer API surface. Replaces ESG's `componentDesignerApi`.
// Story 3.2 implements the create/list/get endpoints below. Save/publish/archive/
// duplicate land in Stories 3.6/3.7/3.8 — their stubs here POST/PUT against routes
// that 404 today, surfacing in the SPA as the documented "Could not save" toast.

export const designerApi = {
  // `opts` (sort/search/status) is optional so the lookup callers that only
  // need a page of results keep working unchanged. sort is "field:dir".
  listSchemas: (
    page: number,
    pageSize: number,
    opts?: { sort?: string; search?: string; status?: string; mode?: string },
  ) => {
    const sp = new URLSearchParams()
    sp.set('page', String(page))
    sp.set('pageSize', String(pageSize))
    if (opts?.sort) sp.set('sort', opts.sort)
    if (opts?.search) sp.set('search', opts.search)
    if (opts?.status) sp.set('status', opts.status)
    if (opts?.mode) sp.set('mode', opts.mode)
    return httpClient.get<PagedResult<ComponentSchemaListItem>>(
      `/api/designers?${sp.toString()}`,
    )
  },

  getSchema: (designerId: string, version?: number) =>
    version !== undefined
      ? httpClient.get<ComponentSchemaDto>(
          `/api/designers/${designerId}/versions/${version}`,
        )
      : httpClient.get<ComponentSchemaDto>(`/api/designers/${designerId}`),

  createSchema: (body: {
    designerId: string
    displayName: string
    mode: 'CRUD' | 'VIEW'
  }) => httpClient.post<ComponentSchemaDto>('/api/designers', body),

  // Story 3.6 — POST creates a new immutable Draft version snapshot.
  // The backend auto-increments the version (no version in URL); 422 surfaces
  // fieldKey validation errors as `extensions.errors[]`. Used by the library's
  // explicit "New Version" action — the designer's Save uses updateVersion.
  createVersion: (designerId: string, rootElement: unknown) =>
    httpClient.post<ComponentSchemaDto>(
      `/api/designers/${designerId}/versions`,
      { rootElement },
    ),

  // PUT updates an existing version's content in place (no new snapshot) and
  // persists the schema-level displayName. This is the designer Save path:
  // repeated saves keep editing the same version instead of spawning v2, v3…
  updateVersion: (
    designerId: string,
    version: number,
    rootElement: unknown,
    displayName: string,
  ) =>
    httpClient.put<ComponentSchemaDto>(
      `/api/designers/${designerId}/versions/${version}`,
      { rootElement, displayName },
    ),

  publishVersion: (designerId: string, version: number) =>
    httpClient.put<ComponentSchemaDto>(
      `/api/designers/${designerId}/versions/${version}/status`,
      { status: 'Published' },
    ),

  archiveVersion: (designerId: string, version: number) =>
    httpClient.put<ComponentSchemaDto>(
      `/api/designers/${designerId}/versions/${version}/status`,
      { status: 'Archived' },
    ),

  // Sets (or clears, with null) the per-version auth filter field key. The
  // backend validates that the key is a user field on the version's schema and
  // returns the updated single-version DTO.
  setAuthFilterFieldKey: (
    designerId: string,
    version: number,
    authFilterFieldKey: string | null,
  ) =>
    httpClient.put<ComponentSchemaDto>(
      `/api/designers/${designerId}/versions/${version}/auth-filter`,
      { authFilterFieldKey },
    ),

  duplicateSchema: (designerId: string) =>
    httpClient.post<ComponentSchemaDto>(
      `/api/designers/${designerId}/duplicate`,
      {},
    ),
}
