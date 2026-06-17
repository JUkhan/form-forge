import { useCallback, useEffect, useState } from 'react'
import { createFileRoute, useNavigate, Link } from '@tanstack/react-router'
import { useMutation } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { Database, FolderOpen, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { SortHeader } from '@/components/shared/SortHeader'
import { SearchBox } from '@/components/shared/SearchBox'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from '@/components/ui/alert-dialog'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { SqlQueryTextarea } from '../../../features/datasets/SqlQueryTextarea'
import { PreviewResultDialog } from '../../../features/datasets/PreviewResultDialog'
import { ParameterInputDialog } from '../../../features/datasets/ParameterInputDialog'
import { extractPlaceholders } from '../../../features/datasets/queryParameters'
import { datasetNameSchema } from '../../../features/datasets/validation'
import {
  getDataset,
  previewDataset,
  type DatasetDetail,
  type DatasetQueryType,
  type DatasetSummary,
  type PreviewResult,
} from '../../../features/datasets/datasetApi'
import { useDatasetListQuery } from '../../../features/datasets/useDatasetListQuery'
import {
  useCreateDatasetMutation,
  useDeleteDatasetMutation,
  useUpdateDatasetMutation,
} from '../../../features/datasets/datasetMutations'
import { useDatasetAuditLogQuery } from '../../../features/datasets/useDatasetAuditLogQuery'
import { ApiError } from '../../../lib/api/apiError'

const datasetsSearchSchema = z.object({
  page: z.coerce.number().int().min(1).default(1),
  pageSize: z.coerce.number().int().min(1).max(100).default(25),
  // Whitelisted single-column sort, "field:dir" (name | mode | createdAt | updatedAt).
  sort: z.string().optional(),
  // Free-text dataset_name search.
  q: z.string().optional(),
})

export const Route = createFileRoute('/_app/admin/datasets')({
  validateSearch: datasetsSearchSchema,
  component: DatasetsPage,
})

function DatasetsPage() {
  const { t } = useTranslation()
  const navigate = useNavigate({ from: '/admin/datasets' })
  const { page, pageSize, sort, q } = Route.useSearch()
  const listQuery = useDatasetListQuery(page, pageSize, { sort, search: q })
  const deleteMutation = useDeleteDatasetMutation()

  const [showCreate, setShowCreate] = useState(false)
  const [editTarget, setEditTarget] = useState<DatasetDetail | null>(null)
  const [deleteTarget, setDeleteTarget] = useState<{ id: string; name: string } | null>(null)
  const [deleteError, setDeleteError] = useState<string | null>(null)
  const [auditDatasetName, setAuditDatasetName] = useState<string | null>(null)
  // Tracks whether any row's edit prefetch is in-flight to prevent concurrent fetches.
  const [isEditFetching, setIsEditFetching] = useState(false)

  const goToPage = useCallback(
    (next: number) => void navigate({ search: (prev) => ({ ...prev, page: next }) }),
    [navigate],
  )
  // navigate() is stable; useCallback keeps these handlers referentially stable so
  // SearchBox's debounce timer isn't reset on every parent render. Changing the sort
  // or search resets to page 1 so results aren't hidden on an out-of-range page.
  const setSort = useCallback(
    (next: string | undefined) =>
      void navigate({ search: (prev) => ({ ...prev, sort: next, page: 1 }) }),
    [navigate],
  )
  const setSearch = useCallback(
    (next: string) =>
      void navigate({ search: (prev) => ({ ...prev, q: next || undefined, page: 1 }) }),
    [navigate],
  )

  const openCreate = useCallback(() => {
    setEditTarget(null)
    setShowCreate(true)
  }, [])

  const openEdit = useCallback((detail: DatasetDetail) => {
    setShowCreate(false)
    setEditTarget(detail)
  }, [])

  const toggleAudit = useCallback((name: string) => {
    setAuditDatasetName((current) => (current === name ? null : name))
  }, [])

  const totalPages = Math.max(1, listQuery.data?.totalPages ?? 1)
  const clampedPage = Math.min(page, totalPages)
  const rows = listQuery.data?.data ?? []
  const hasSearch = !!q
  const showEmpty = !!listQuery.data && rows.length === 0

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <div className="flex items-center gap-2.5">
            <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary/10 text-primary">
              <Database className="h-5 w-5" />
            </div>
            <h1 className="text-2xl font-bold tracking-tight">{t('admin.datasets.title')}</h1>
          </div>
          <p className="mt-1 ml-0 text-sm text-muted-foreground sm:ml-12">{t('admin.datasets.subtitle')}</p>
        </div>
        <Button onClick={openCreate}>{t('admin.datasets.createButton')}</Button>
      </div>

      {showCreate && !editTarget && <CreateDatasetForm onDone={() => setShowCreate(false)} />}
      {editTarget && (
        <EditDatasetForm
          key={editTarget.id}
          dataset={editTarget}
          onDone={(savedName) => {
            // P7: clear audit panel if the displayed dataset was just renamed.
            if (auditDatasetName === editTarget.datasetName && savedName !== editTarget.datasetName) {
              setAuditDatasetName(null)
            }
            setEditTarget(null)
          }}
        />
      )}

      {auditDatasetName && (
        <DatasetAuditPanel datasetName={auditDatasetName} onClose={() => setAuditDatasetName(null)} />
      )}

      <div className="flex flex-col gap-3 sm:flex-row sm:items-center">
        <SearchBox
          value={q ?? ''}
          onChange={setSearch}
          placeholder={t('admin.datasets.searchPlaceholder')}
        />
        {hasSearch && (
          <Tooltip>
            <TooltipTrigger asChild>
              <Button
                variant="ghost"
                size="icon-sm"
                onClick={() => setSearch('')}
                aria-label={t('common.clearFilters')}
              >
                <X />
              </Button>
            </TooltipTrigger>
            <TooltipContent>{t('common.clearFilters')}</TooltipContent>
          </Tooltip>
        )}
      </div>

      {listQuery.isError && (
        <div
          role="alert"
          className="rounded-lg border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive"
        >
          {t('admin.datasets.loadError')}
        </div>
      )}

      <div className="overflow-hidden rounded-xl border border-border bg-card shadow-sm">
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="bg-muted text-left">
                <th className="px-5 py-3">
                  <SortHeader colKey="name" label={t('admin.datasets.columnName')} sort={sort} onSort={setSort} />
                </th>
                <th className="px-5 py-3">
                  <SortHeader colKey="mode" label={t('admin.datasets.columnMode')} sort={sort} onSort={setSort} />
                </th>
                <th className="px-5 py-3">
                  <SortHeader colKey="createdAt" label={t('admin.datasets.columnCreatedAt')} sort={sort} onSort={setSort} />
                </th>
                <th className="px-5 py-3">
                  <SortHeader colKey="updatedAt" label={t('admin.datasets.columnUpdatedAt')} sort={sort} onSort={setSort} />
                </th>
                <th className="px-5 py-3 text-right">{t('admin.datasets.columnActions')}</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border">
              {listQuery.isLoading && (
                <tr>
                  <td colSpan={5} className="px-5 py-8 text-center text-sm text-muted-foreground">
                    {t('admin.datasets.loading')}
                  </td>
                </tr>
              )}
              {showEmpty && (
                <tr>
                  <td colSpan={5} className="px-5 py-16 text-center">
                    <div className="flex flex-col items-center gap-3">
                      <div className="flex h-12 w-12 items-center justify-center rounded-full bg-primary/10 text-primary">
                        <FolderOpen className="h-6 w-6" />
                      </div>
                      {hasSearch ? (
                        <>
                          <p className="font-semibold text-foreground">{t('admin.datasets.noMatches')}</p>
                          <Button variant="outline" onClick={() => setSearch('')}>
                            {t('common.clearFilters')}
                          </Button>
                        </>
                      ) : (
                        <>
                          <p className="font-semibold text-foreground">{t('admin.datasets.noDatasets')}</p>
                          <p className="text-sm text-muted-foreground">{t('admin.datasets.noDatasetsCta')}</p>
                          <Button onClick={openCreate}>{t('admin.datasets.createButton')}</Button>
                        </>
                      )}
                    </div>
                  </td>
                </tr>
              )}
              {rows.map((row) => (
                <DatasetRow
                  key={row.id}
                  row={row}
                  isAuditOpen={row.datasetName === auditDatasetName}
                  isAnyEditFetching={isEditFetching}
                  onEdit={openEdit}
                  onEditFetchingChange={setIsEditFetching}
                  onDelete={() => setDeleteTarget({ id: row.id, name: row.datasetName })}
                  onAudit={() => toggleAudit(row.datasetName)}
                />
              ))}
            </tbody>
          </table>
        </div>
      </div>

      {rows.length > 0 && (
        <nav
          aria-label="pagination"
          className="flex items-center justify-end gap-3 text-sm text-muted-foreground"
        >
          <Button
            variant="outline"
            size="sm"
            onClick={() => goToPage(clampedPage - 1)}
            disabled={clampedPage <= 1 || listQuery.isFetching}
          >
            {t('admin.datasets.previousPage')}
          </Button>
          <span>{t('admin.datasets.pageIndicator', { page: clampedPage, totalPages })}</span>
          <Button
            variant="outline"
            size="sm"
            onClick={() => goToPage(clampedPage + 1)}
            disabled={clampedPage >= totalPages || listQuery.isFetching}
          >
            {t('admin.datasets.nextPage')}
          </Button>
        </nav>
      )}

      <AlertDialog
        open={deleteTarget !== null}
        onOpenChange={(open) => {
          if (!open) {
            setDeleteTarget(null)
            setDeleteError(null)
          }
        }}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{t('admin.datasets.deleteConfirmTitle')}</AlertDialogTitle>
            <AlertDialogDescription>
              {t('admin.datasets.deleteConfirmDescription', { name: deleteTarget?.name ?? '' })}
            </AlertDialogDescription>
          </AlertDialogHeader>
          {deleteError && (
            <div
              role="alert"
              className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive"
            >
              {deleteError}
            </div>
          )}
          <AlertDialogFooter>
            <AlertDialogCancel disabled={deleteMutation.isPending}>
              {t('admin.datasets.cancelButton')}
            </AlertDialogCancel>
            <AlertDialogAction
              disabled={deleteMutation.isPending}
              onClick={(e) => {
                // Keep the dialog mounted until the request resolves so the
                // pending state is visible; close it ourselves on success.
                e.preventDefault()
                if (!deleteTarget) return
                setDeleteError(null)
                void deleteMutation
                  .mutateAsync(deleteTarget.id)
                  .then(() => setDeleteTarget(null))
                  .catch(() => setDeleteError(t('errors.genericError')))
              }}
            >
              {t('admin.datasets.deleteConfirm')}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  )
}

interface DatasetRowProps {
  row: DatasetSummary
  isAuditOpen: boolean
  isAnyEditFetching: boolean
  onEdit: (detail: DatasetDetail) => void
  onEditFetchingChange: (fetching: boolean) => void
  onDelete: () => void
  onAudit: () => void
}

function DatasetRow({ row, isAuditOpen, isAnyEditFetching, onEdit, onEditFetchingChange, onDelete, onAudit }: DatasetRowProps) {
  const { t } = useTranslation()
  const navigate = useNavigate({ from: '/admin/datasets' })
  const [editLoading, setEditLoading] = useState(false)
  const [editError, setEditError] = useState<string | null>(null)

  // Query Builder mode datasets open the full-screen canvas (Story 9.1) instead of
  // the inline edit form, which is reserved for Custom Query mode.
  const handleOpenBuilder = useCallback(() => {
    void navigate({ to: '/admin/datasets/$id', params: { id: row.id } })
  }, [navigate, row.id])

  const handleEdit = () => {
    if (isAnyEditFetching) return
    setEditLoading(true)
    setEditError(null)
    onEditFetchingChange(true)
    // Fetch the full DatasetDetail (which carries `version`, `query`,
    // `builderState`) before opening the edit form — the list row does not
    // include the optimistic-concurrency token.
    void getDataset(row.id)
      .then((detail) => onEdit(detail))
      .catch(() => setEditError(t('errors.genericError')))
      .finally(() => {
        setEditLoading(false)
        onEditFetchingChange(false)
      })
  }

  return (
    <tr className="hover:bg-overlay-hover">
      <td className="px-5 py-3 font-medium text-foreground">{row.datasetName}</td>
      <td className="px-5 py-3">
        <ModeBadge isCustomQuery={row.isCustomQuery} />
      </td>
      <td className="px-5 py-3 text-muted-foreground">
        {new Date(row.createdAt).toLocaleDateString()}
      </td>
      <td className="px-5 py-3 text-muted-foreground">
        {row.updatedAt ? new Date(row.updatedAt).toLocaleDateString() : '—'}
      </td>
      <td className="px-5 py-3">
        <div className="flex flex-col items-end gap-1">
          <div className="flex items-center justify-end gap-2">
            <Button
              variant={isAuditOpen ? 'secondary' : 'ghost'}
              size="sm"
              onClick={onAudit}
              aria-pressed={isAuditOpen}
            >
              {t('admin.datasets.auditButton')}
            </Button>
            {row.isCustomQuery ? (
              <Button
                variant="outline"
                size="sm"
                onClick={handleEdit}
                disabled={isAnyEditFetching}
                aria-busy={editLoading}
              >
                {editLoading ? t('admin.datasets.editButtonLoading') : t('admin.datasets.editButton')}
              </Button>
            ) : (
              <Button variant="outline" size="sm" onClick={handleOpenBuilder}>
                {t('admin.datasets.openBuilderButton')}
              </Button>
            )}
            <Button variant="destructive" size="sm" onClick={onDelete}>
              {t('admin.datasets.deleteButton')}
            </Button>
          </div>
          {editError && (
            <p role="alert" className="text-xs text-destructive">
              {editError}
            </p>
          )}
        </div>
      </td>
    </tr>
  )
}

function ModeBadge({ isCustomQuery }: { isCustomQuery: boolean }) {
  const { t } = useTranslation()
  const label = isCustomQuery
    ? t('admin.datasets.modeCustomQuery')
    : t('admin.datasets.modeQueryBuilder')
  return (
    <span
      className={`inline-flex items-center rounded-full px-2.5 py-1 text-xs font-medium ${
        isCustomQuery
          ? 'bg-primary/10 text-primary'
          : 'bg-muted text-muted-foreground'
      }`}
    >
      {label}
    </span>
  )
}

// ---- Create form ----------------------------------------------------------

// No .default() here: defaults are supplied via useForm's defaultValues below.
// A Zod .default() would make the resolver's input type diverge from the output
// type and break zodResolver's generic signature.
const createDatasetFormSchema = z.object({
  datasetName: datasetNameSchema,
  isCustomQuery: z.boolean(),
  queryType: z.enum(['view', 'query']),
  query: z.string(),
})
type CreateDatasetFormValues = z.infer<typeof createDatasetFormSchema>

interface CreateDatasetFormProps {
  onDone: () => void
}

function CreateDatasetForm({ onDone }: CreateDatasetFormProps) {
  const { t } = useTranslation()
  const createMutation = useCreateDatasetMutation()
  const {
    register,
    handleSubmit,
    watch,
    setValue,
    setError,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<CreateDatasetFormValues>({
    resolver: zodResolver(createDatasetFormSchema),
    mode: 'onChange',
    defaultValues: { isCustomQuery: true, queryType: 'view', query: '' },
  })

  const isCustomQuery = watch('isCustomQuery')
  const queryType = watch('queryType')
  const query = watch('query')

  const onSubmit = async (values: CreateDatasetFormValues) => {
    // Zod's string().default('') does not block empty strings — guard manually. A
    // "query"-type dataset is always custom SQL, so it requires a non-empty query too.
    const requiresQuery = values.isCustomQuery || values.queryType === 'query'
    if (requiresQuery && !values.query.trim()) {
      setError('query', { message: t('datasets.sqlTextarea.emptyHint') })
      return
    }
    try {
      await createMutation.mutateAsync({
        datasetName: values.datasetName,
        isCustomQuery: values.isCustomQuery,
        query: requiresQuery ? values.query.trim() : null,
        queryType: values.queryType,
      })
      reset()
      onDone()
    } catch (err) {
      handleDatasetFormError(err, setError, t)
    }
  }

  return (
    <DatasetFormShell
      title={t('admin.datasets.createFormTitle')}
      onSubmit={(e) => void handleSubmit(onSubmit)(e)}
      onCancel={onDone}
      isSubmitting={isSubmitting}
      rootError={errors.root?.message}
    >
      <div className="space-y-1.5">
        <Label htmlFor="dataset-name">{t('admin.datasets.fieldName')}</Label>
        <Input id="dataset-name" type="text" autoComplete="off" {...register('datasetName')} />
        {errors.datasetName && (
          <p role="alert" className="text-xs text-destructive">
            {errors.datasetName.message}
          </p>
        )}
      </div>

      <ModeToggleAndQuery
        isCustomQuery={isCustomQuery}
        queryType={queryType}
        query={query}
        queryError={errors.query?.message}
        onQueryTypeChange={(qt) => {
          setValue('queryType', qt)
          // A "query"-type dataset is custom SQL only (no visual builder / VIEW).
          if (qt === 'query') setValue('isCustomQuery', true)
        }}
        onModeChange={(custom) => setValue('isCustomQuery', custom)}
        onQueryChange={(v) => setValue('query', v, { shouldValidate: true })}
      />
    </DatasetFormShell>
  )
}

// ---- Edit form ------------------------------------------------------------

const editDatasetFormSchema = z.object({
  datasetName: datasetNameSchema,
  isCustomQuery: z.boolean(),
  queryType: z.enum(['view', 'query']),
  query: z.string(),
})
type EditDatasetFormValues = z.infer<typeof editDatasetFormSchema>

interface EditDatasetFormProps {
  dataset: DatasetDetail
  onDone: (savedName: string) => void
}

function EditDatasetForm({ dataset, onDone }: EditDatasetFormProps) {
  const { t } = useTranslation()
  const updateMutation = useUpdateDatasetMutation(dataset.id)
  const {
    register,
    handleSubmit,
    watch,
    setValue,
    setError,
    formState: { errors, isSubmitting },
  } = useForm<EditDatasetFormValues>({
    resolver: zodResolver(editDatasetFormSchema),
    mode: 'onChange',
    defaultValues: {
      datasetName: dataset.datasetName,
      isCustomQuery: dataset.isCustomQuery,
      queryType: dataset.queryType,
      query: dataset.query ?? '',
    },
  })

  const isCustomQuery = watch('isCustomQuery')
  const queryType = watch('queryType')
  const query = watch('query')

  const onSubmit = async (values: EditDatasetFormValues) => {
    const requiresQuery = values.isCustomQuery || values.queryType === 'query'
    if (requiresQuery && !values.query.trim()) {
      setError('query', { message: t('datasets.sqlTextarea.emptyHint') })
      return
    }
    try {
      await updateMutation.mutateAsync({
        datasetName: values.datasetName !== dataset.datasetName ? values.datasetName : undefined,
        isCustomQuery: values.isCustomQuery,
        query: requiresQuery ? values.query.trim() || null : null,
        queryType: values.queryType,
        version: dataset.version, // REQUIRED — optimistic concurrency token
      })
      onDone(values.datasetName)
    } catch (err) {
      if (
        err instanceof ApiError &&
        err.status === 409 &&
        err.code === 'DATASET_CONCURRENCY_CONFLICT'
      ) {
        setError('root', { message: t('admin.datasets.concurrencyConflict') })
        return
      }
      handleDatasetFormError(err, setError, t)
    }
  }

  return (
    <DatasetFormShell
      title={t('admin.datasets.editFormTitle')}
      onSubmit={(e) => void handleSubmit(onSubmit)(e)}
      onCancel={() => onDone(dataset.datasetName)}
      isSubmitting={isSubmitting}
      rootError={errors.root?.message}
    >
      <div className="space-y-1.5">
        <Label htmlFor="edit-dataset-name">{t('admin.datasets.fieldName')}</Label>
        <Input id="edit-dataset-name" type="text" autoComplete="off" {...register('datasetName')} />
        {errors.datasetName && (
          <p role="alert" className="text-xs text-destructive">
            {errors.datasetName.message}
          </p>
        )}
      </div>

      <ModeToggleAndQuery
        isCustomQuery={isCustomQuery}
        queryType={queryType}
        query={query}
        queryError={errors.query?.message}
        onQueryTypeChange={(qt) => {
          setValue('queryType', qt)
          if (qt === 'query') setValue('isCustomQuery', true)
        }}
        onModeChange={(custom) => setValue('isCustomQuery', custom)}
        onQueryChange={(v) => setValue('query', v, { shouldValidate: true })}
        datasetId={dataset.id}
      />
    </DatasetFormShell>
  )
}

// ---- Shared form pieces ---------------------------------------------------

interface DatasetFormShellProps {
  title: string
  onSubmit: (e: React.FormEvent<HTMLFormElement>) => void
  onCancel: () => void
  isSubmitting: boolean
  rootError?: string
  children: React.ReactNode
}

function DatasetFormShell({
  title,
  onSubmit,
  onCancel,
  isSubmitting,
  rootError,
  children,
}: DatasetFormShellProps) {
  const { t } = useTranslation()
  return (
    <form
      onSubmit={onSubmit}
      aria-label={title}
      className="space-y-4 rounded-xl border border-border bg-card p-6 shadow-sm"
    >
      <h2 className="text-lg font-semibold text-foreground">{title}</h2>

      {children}

      {rootError && (
        <div
          role="alert"
          className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive"
        >
          {rootError}
        </div>
      )}

      <div className="flex gap-2">
        <Button type="submit" disabled={isSubmitting}>
          {t('admin.datasets.saveButton')}
        </Button>
        <Button type="button" variant="outline" onClick={onCancel}>
          {t('admin.datasets.cancelButton')}
        </Button>
      </div>
    </form>
  )
}

interface ModeToggleAndQueryProps {
  isCustomQuery: boolean
  queryType: DatasetQueryType
  query: string
  queryError?: string
  // Present only in the edit form: the visual Query Builder lives on the dataset's
  // own page (/admin/datasets/$id), so we can only link there once the dataset exists.
  datasetId?: string
  onQueryTypeChange: (queryType: DatasetQueryType) => void
  onModeChange: (custom: boolean) => void
  onQueryChange: (value: string) => void
}

function ModeToggleAndQuery({
  isCustomQuery,
  queryType,
  query,
  queryError,
  onQueryTypeChange,
  onModeChange,
  onQueryChange,
  datasetId,
}: ModeToggleAndQueryProps) {
  const { t } = useTranslation()

  // Story 11.3 (FR-72 AC-1/2/4/5/6) — read-only LIMIT-10 preview of the custom query,
  // shown in a popup (PreviewResultDialog) opened on Preview click.
  const [previewResult, setPreviewResult] = useState<PreviewResult | null>(null)
  const [previewError, setPreviewError] = useState<string | null>(null)
  const [previewOpen, setPreviewOpen] = useState(false)
  // Parameterized-query feature — placeholders in the SQL must be filled before preview.
  const [paramDialogOpen, setParamDialogOpen] = useState(false)
  const placeholders = extractPlaceholders(query)
  const isQueryType = queryType === 'query'

  const previewMutation = useMutation({
    mutationFn: (queryParameters?: string) =>
      previewDataset({ isCustomQuery: true, query, queryType, queryParameters }),
    onSuccess: (data) => {
      setPreviewResult(data)
      setPreviewError(null)
    },
    onError: (err) => {
      setPreviewResult(null)
      if (err instanceof ApiError) {
        setPreviewError(err.detail?.trim() ? err.detail : t('errors.genericError'))
      } else {
        setPreviewError(t('errors.genericError'))
      }
    },
  })

  // On Preview: if the query has {_placeholder} tokens, prompt for their values first,
  // then run the preview with the resolved queryParameters. Otherwise preview directly.
  const handlePreview = () => {
    if (placeholders.length > 0) {
      setParamDialogOpen(true)
      return
    }
    setPreviewOpen(true)
    previewMutation.mutate(undefined)
  }

  // Clear stale preview output whenever the query text changes (Task 8). Switching to
  // Query Builder mode unmounts this branch entirely, which resets the state too.
  useEffect(() => {
    setPreviewResult(null)
    setPreviewError(null)
  }, [query])

  return (
    <div className="space-y-3">
      <div className="space-y-1.5">
        <Label>{t('admin.datasets.fieldQueryType')}</Label>
        <Select value={queryType} onValueChange={(v) => onQueryTypeChange(v as DatasetQueryType)}>
          <SelectTrigger className="w-48">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="view">{t('admin.datasets.queryTypeView')}</SelectItem>
            <SelectItem value="query">{t('admin.datasets.queryTypeQuery')}</SelectItem>
          </SelectContent>
        </Select>
        <p className="text-xs text-muted-foreground">
          {isQueryType
            ? t('admin.datasets.queryTypeQueryHint')
            : t('admin.datasets.queryTypeViewHint')}
        </p>
      </div>

      <div className="space-y-1.5">
        <Label>{t('admin.datasets.fieldMode')}</Label>
        <div className="flex gap-2">
          <Button
            type="button"
            size="sm"
            variant={isCustomQuery ? 'default' : 'outline'}
            onClick={() => onModeChange(true)}
          >
            {t('admin.datasets.modeCustomQuery')}
          </Button>
          <Button
            type="button"
            size="sm"
            variant={!isCustomQuery ? 'default' : 'outline'}
            // A "query"-type dataset never creates a VIEW, so the visual builder is disabled.
            disabled={isQueryType}
            onClick={() => onModeChange(false)}
          >
            {t('admin.datasets.modeQueryBuilder')}
          </Button>
        </div>
      </div>

      {isCustomQuery ? (
        <>
          {/* `query` is intentionally preserved when switching to Query Builder
              and back, so the textarea restores the prior SQL (AC-4). */}
          <SqlQueryTextarea value={query} onChange={onQueryChange} error={queryError} />
          <div className="flex items-center gap-2">
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={!query.trim() || previewMutation.isPending}
              onClick={handlePreview}
            >
              {previewMutation.isPending
                ? t('datasets.previewingButton')
                : t('datasets.previewButton')}
            </Button>
          </div>

          <ParameterInputDialog
            open={paramDialogOpen}
            onOpenChange={setParamDialogOpen}
            placeholders={placeholders}
            onSubmit={(queryParameters) => {
              setParamDialogOpen(false)
              setPreviewOpen(true)
              previewMutation.mutate(queryParameters)
            }}
          />

          <PreviewResultDialog
            open={previewOpen}
            onOpenChange={setPreviewOpen}
            isPending={previewMutation.isPending}
            error={previewError}
            result={previewResult}
          />
        </>
      ) : datasetId ? (
        <p className="text-xs text-muted-foreground">
          {t('admin.datasets.queryBuilderOpenHint')}{' '}
          <Link
            to="/admin/datasets/$id"
            params={{ id: datasetId }}
            className="font-medium text-primary underline-offset-2 hover:underline"
          >
            {t('admin.datasets.openBuilderButton')}
          </Link>
        </p>
      ) : (
        <p className="text-xs text-muted-foreground">
          {t('admin.datasets.queryBuilderSaveFirst')}
        </p>
      )}
    </div>
  )
}

// Maps the dataset write-endpoint ProblemDetails codes to inline field errors.
function handleDatasetFormError(
  err: unknown,
  setError: (
    field: 'datasetName' | 'query' | 'root',
    error: { type?: string; message: string },
  ) => void,
  t: (key: string) => string,
) {
  if (err instanceof ApiError) {
    if (err.status === 409 && err.code === 'DATASET_NAME_CONFLICT') {
      setError('datasetName', { type: 'server', message: t('admin.datasets.nameConflict') })
      return
    }
    if (err.status === 422 && err.code === 'INVALID_DATASET_NAME') {
      setError('datasetName', { type: 'server', message: t('admin.datasets.invalidDatasetName') })
      return
    }
    if (err.status === 422 && err.code === 'INVALID_QUERY') {
      // Prefer the server's detail — it carries the real reason the query was
      // rejected (the SELECT-only message, or the raw Postgres error such as
      // `column "x" does not exist` when a valid SELECT fails at CREATE VIEW).
      // The generic i18n string is only a fallback when no detail is present.
      setError('query', {
        type: 'server',
        message: err.detail?.trim() ? err.detail : t('admin.datasets.invalidQuery'),
      })
      return
    }
  }
  setError('root', { message: t('errors.genericError') })
}

// ---- Audit panel (AC-6) ---------------------------------------------------

interface DatasetAuditPanelProps {
  datasetName: string
  onClose: () => void
}

function DatasetAuditPanel({ datasetName, onClose }: DatasetAuditPanelProps) {
  const { t } = useTranslation()
  const [auditPage, setAuditPage] = useState(1)
  const auditQuery = useDatasetAuditLogQuery({ page: auditPage, pageSize: 25, datasetName })

  const totalPages = Math.max(1, auditQuery.data?.totalPages ?? 1)
  const entries = auditQuery.data?.data ?? []

  return (
    <div className="space-y-4 rounded-xl border border-border bg-card p-6 shadow-sm">
      <div className="flex items-center justify-between">
        <h2 className="text-lg font-semibold text-foreground">
          {t('admin.datasets.auditTitle', { name: datasetName })}
        </h2>
        <Button variant="ghost" size="sm" onClick={onClose}>
          {t('admin.datasets.auditClose')}
        </Button>
      </div>

      {auditQuery.isError && (
        <div
          role="alert"
          className="rounded-lg border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive"
        >
          {t('datasets.audit.loadError')}
        </div>
      )}

      <div className="overflow-hidden rounded-lg border border-border">
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="bg-muted text-left">
                <th className="px-4 py-2">{t('datasets.audit.columnTimestamp')}</th>
                <th className="px-4 py-2">{t('datasets.audit.columnOperation')}</th>
                <th className="px-4 py-2">{t('datasets.audit.columnActor')}</th>
                <th className="px-4 py-2">{t('datasets.audit.columnSucceeded')}</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border">
              {auditQuery.isLoading && (
                <tr>
                  <td colSpan={4} className="px-4 py-6 text-center text-sm text-muted-foreground">
                    {t('datasets.audit.loading')}
                  </td>
                </tr>
              )}
              {!auditQuery.isLoading && entries.length === 0 && (
                <tr>
                  <td colSpan={4} className="px-4 py-6 text-center text-sm text-muted-foreground">
                    {t('datasets.audit.noEntries')}
                  </td>
                </tr>
              )}
              {entries.map((entry) => (
                <tr key={entry.id} className="hover:bg-overlay-hover">
                  <td className="px-4 py-2 text-muted-foreground">
                    {new Date(entry.timestamp).toLocaleString()}
                  </td>
                  <td className="px-4 py-2">{entry.operation}</td>
                  <td className="px-4 py-2 text-muted-foreground">
                    {entry.actorName ?? t('datasets.audit.unknownActor')}
                  </td>
                  <td className="px-4 py-2">
                    <span className={entry.succeeded ? 'text-emerald-600' : 'text-destructive'}>
                      {entry.succeeded
                        ? t('datasets.audit.succeededTrue')
                        : t('datasets.audit.succeededFalse')}
                    </span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      {entries.length > 0 && (
        <nav
          aria-label="audit-pagination"
          className="flex items-center justify-end gap-3 text-sm text-muted-foreground"
        >
          <Button
            variant="outline"
            size="sm"
            onClick={() => setAuditPage((p) => Math.max(1, p - 1))}
            disabled={auditPage <= 1 || auditQuery.isFetching}
          >
            {t('datasets.audit.prevPage')}
          </Button>
          <span>{t('datasets.audit.pageInfo', { page: auditPage, totalPages })}</span>
          <Button
            variant="outline"
            size="sm"
            onClick={() => setAuditPage((p) => p + 1)}
            disabled={auditPage >= totalPages || auditQuery.isFetching}
          >
            {t('datasets.audit.nextPage')}
          </Button>
        </nav>
      )}
    </div>
  )
}
