import { createFileRoute, Outlet, useMatchRoute, useNavigate } from '@tanstack/react-router'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { z } from 'zod'
import { RecordListPage } from '@/components/dataEntry/RecordListPage'
import DynamicComponent from '@/components/designer/DynamicComponent'
import { designerApi } from '@/features/designer/designerApi'

// Story 6.10 — the record list owns no URL state itself; this route owns the
// search params (page, pageSize, sort, filter) via TanStack Router's
// validateSearch and passes them plus navigation callbacks down. RecordListPage
// stays a pure display component, mirroring the menus.tsx/users.tsx pattern.
// pageSize is constrained to the discrete picker options the UI exposes
// (RecordListPage.tsx:PAGE_SIZE_OPTIONS). A `.catch(25)` falls bad URLs back to
// the default instead of erroring the whole route, so a hostile ?pageSize=37
// degrades gracefully rather than producing a select with a controlled-value
// mismatch.
const dataSearchSchema = z.object({
  page: z.coerce.number().int().min(1).default(1),
  pageSize: z.coerce
    .number()
    .refine((v) => v === 10 || v === 25 || v === 50)
    .catch(25),
  sort: z.string().optional(),
  // zod 4 requires both key and value schemas (zod 3 defaulted the key to
  // z.string()). Filter is { columnKey: matchValue } — both strings.
  filter: z.record(z.string(), z.string()).optional(),
  // Story 7-followup — 'Show deleted' toggle. Boolean in URL state, coerced
  // from the "true"/"false" string TanStack Router serializes.
  showDeleted: z.coerce.boolean().optional(),
})

export const Route = createFileRoute('/_app/data/$designerId')({
  validateSearch: dataSearchSchema,
  component: RouteComponent,
})

// react-refresh/only-export-components fires here because this file also
// exports a non-component (`Route`). Every file-based route in this repo
// has the same shape; suppress the rule on this internal component so the
// new route does not raise the baseline lint error count.
// eslint-disable-next-line react-refresh/only-export-components
export function RouteComponent() {
  const { designerId } = Route.useParams()
  const { page, pageSize, sort, filter, showDeleted } = Route.useSearch()
  // useNavigate takes the EXTERNAL path (no `/_app/` prefix). createFileRoute
  // takes the INTERNAL path (with prefix); the two are different identifiers.
  const navigate = useNavigate({ from: '/data/$designerId' })
  const matchRoute = useMatchRoute()
  const { t } = useTranslation()

  // FR-54 AC-4 — fetch the schema to learn its mode. VIEW-mode designers render
  // read-only (no record list, no data API calls); CRUD falls through to the
  // RecordListPage. Hook must run before any conditional return (rules of hooks).
  const schemaQuery = useQuery({
    queryKey: ['designer', 'schema', designerId],
    queryFn: () => designerApi.getSchema(designerId),
    staleTime: 60_000,
  })

  // File-based routing makes data.$designerId.tsx a layout PARENT for
  // data.$designerId.new.tsx and data.$designerId.$recordId.tsx. When the
  // user navigates to either child, this component re-mounts; if it just
  // renders RecordListPage without an <Outlet />, the child has nowhere to
  // render and the URL change appears silent. See
  // routing_dot_nested_parent_layouts memory — same fix as
  // menus.tsx / users.tsx / roles.tsx. Hooks must run before this branch.
  const isChildActive =
    !!matchRoute({ to: '/data/$designerId/new', fuzzy: false }) ||
    !!matchRoute({ to: '/data/$designerId/$recordId', fuzzy: false })
  if (isChildActive) {
    return <Outlet />
  }

  // VIEW-mode rendering path (AC-5). Gate on the schema query so the branch only
  // fires once data is present. The CRUD path (RecordListPage) is unchanged.
  if (schemaQuery.isLoading) {
    return <div className="p-6 text-muted-foreground">{t('common.loading')}</div>
  }
  if (schemaQuery.isError) {
    return <div className="p-6 text-destructive">{t('errors.genericError')}</div>
  }
  if (schemaQuery.data?.mode === 'VIEW') {
    return (
      <div className="flex flex-col gap-4 p-6">
        <h1 className="text-xl font-semibold">{schemaQuery.data.displayName}</h1>
        {/* No onSave → read-only preview (matches ComponentPreviewModal pattern). */}
        <DynamicComponent designerId={designerId} schema={schemaQuery.data} />
      </div>
    )
  }

  return (
    <RecordListPage
      designerId={designerId}
      page={page}
      pageSize={pageSize}
      sort={sort}
      filter={filter}
      showDeleted={showDeleted ?? false}
      onPageChange={(p) =>
        void navigate({ search: (prev) => ({ ...prev, page: p }) })
      }
      onPageSizeChange={(ps) =>
        void navigate({ search: (prev) => ({ ...prev, pageSize: ps, page: 1 }) })
      }
      onSortChange={(s) =>
        void navigate({ search: (prev) => ({ ...prev, sort: s, page: 1 }) })
      }
      onFilterChange={(f) =>
        void navigate({ search: (prev) => ({ ...prev, filter: f, page: 1 }) })
      }
      onShowDeletedChange={(v) =>
        void navigate({
          search: (prev) => ({ ...prev, showDeleted: v || undefined, page: 1 }),
        })
      }
    />
  )
}
