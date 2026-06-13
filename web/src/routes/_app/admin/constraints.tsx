import { useCallback, useMemo, useState } from 'react'
import { createFileRoute } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { KeyRound, ShieldCheck, Table2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Label } from '@/components/ui/label'
import { Combobox, type ComboboxOption } from '@/components/ui/combobox'
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
import { ApiError } from '../../../lib/api/apiError'
import type { UniqueConstraint } from '../../../features/admin/constraints/constraintApi'
import {
  useAddConstraintMutation,
  useDropConstraintMutation,
  useProvisionedDesignersQuery,
  useUniqueConstraintsQuery,
} from '../../../features/admin/constraints/useConstraintQueries'

export const Route = createFileRoute('/_app/admin/constraints')({
  component: ConstraintsPage,
})

function ConstraintsPage() {
  const { t } = useTranslation()
  const designersQuery = useProvisionedDesignersQuery()
  const [designerId, setDesignerId] = useState<string | null>(null)

  const designers = designersQuery.data?.designers ?? []
  const noDesigners = !!designersQuery.data && designers.length === 0

  // Combobox options — searchable by display name, designerId, AND the menus the
  // component is bound to (the Combobox matches typed text against label + value).
  const designerOptions = useMemo<ComboboxOption[]>(
    () =>
      designers.map((d) => ({
        value: d.designerId,
        label:
          d.menuNames.length > 0
            ? `${d.displayName} · ${d.menuNames.join(', ')}`
            : d.displayName,
      })),
    [designers],
  )

  return (
    <div className="space-y-6">
      <div className="min-w-0">
        <div className="flex items-center gap-2.5">
          <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary/10 text-primary">
            <ShieldCheck className="h-5 w-5" />
          </div>
          <h1 className="text-2xl font-bold tracking-tight">{t('admin.constraints.title')}</h1>
        </div>
        <p className="mt-1 ml-0 text-sm text-muted-foreground sm:ml-12">
          {t('admin.constraints.subtitle')}
        </p>
      </div>

      {designersQuery.isError && (
        <div
          role="alert"
          className="rounded-lg border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive"
        >
          {t('admin.constraints.loadDesignersError')}
        </div>
      )}

      {/* Designer picker — searchable combobox */}
      <div className="space-y-1.5">
        <Label htmlFor="constraint-designer">{t('admin.constraints.designerLabel')}</Label>
        <Combobox
          id="constraint-designer"
          className="w-full sm:w-96"
          value={designerId ?? ''}
          onValueChange={(v) => setDesignerId(v || null)}
          options={designerOptions}
          loading={designersQuery.isLoading}
          disabled={designersQuery.isLoading || noDesigners}
          placeholder={t('admin.constraints.designerPlaceholder')}
          searchPlaceholder={t('admin.constraints.designerSearchPlaceholder')}
          emptyText={t('admin.constraints.noDesignerMatches')}
          aria-label={t('admin.constraints.designerLabel')}
        />
        {noDesigners && (
          <p className="text-sm text-muted-foreground">{t('admin.constraints.noDesigners')}</p>
        )}
      </div>

      {designerId && <DesignerConstraintsPanel key={designerId} designerId={designerId} />}
    </div>
  )
}

function DesignerConstraintsPanel({ designerId }: { designerId: string }) {
  const { t } = useTranslation()
  const detailQuery = useUniqueConstraintsQuery(designerId)

  if (detailQuery.isLoading) {
    return (
      <p className="text-sm text-muted-foreground">{t('admin.constraints.loadingDetail')}</p>
    )
  }
  if (detailQuery.isError || !detailQuery.data) {
    return (
      <div
        role="alert"
        className="rounded-lg border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive"
      >
        {t('admin.constraints.loadDetailError')}
      </div>
    )
  }

  const { tableName, columns, constraints } = detailQuery.data

  return (
    <div className="space-y-6">
      <div className="flex items-center gap-2 text-sm text-muted-foreground">
        <Table2 className="h-4 w-4" />
        <span>
          {t('admin.constraints.tableLabel')}{' '}
          <code className="rounded bg-muted px-1.5 py-0.5 font-mono text-xs text-foreground">
            {tableName}
          </code>
        </span>
      </div>

      <AddConstraintCard designerId={designerId} columns={columns} />
      <ExistingConstraintsCard designerId={designerId} constraints={constraints} />
    </div>
  )
}

// ---- Add constraint -------------------------------------------------------

function AddConstraintCard({
  designerId,
  columns,
}: {
  designerId: string
  columns: { columnName: string; pgDataType: string }[]
}) {
  const { t } = useTranslation()
  const addMutation = useAddConstraintMutation(designerId)
  const [selected, setSelected] = useState<string[]>([])
  const [error, setError] = useState<string | null>(null)

  const toggle = useCallback((columnName: string, checked: boolean) => {
    setError(null)
    setSelected((prev) =>
      checked ? [...prev, columnName] : prev.filter((c) => c !== columnName),
    )
  }, [])

  const onAdd = useCallback(() => {
    if (selected.length === 0) return
    setError(null)
    // Preserve column order as it appears in the table (selection order is
    // irrelevant to enforcement; declared order only affects the index layout).
    const ordered = columns.map((c) => c.columnName).filter((c) => selected.includes(c))
    void addMutation
      .mutateAsync(ordered)
      .then(() => setSelected([]))
      .catch((err) => {
        if (err instanceof ApiError) {
          setError(err.messageKey ? t(err.messageKey) : t('errors.genericError'))
        } else {
          setError(t('errors.genericError'))
        }
      })
  }, [selected, columns, addMutation, t])

  if (columns.length === 0) {
    return (
      <div className="rounded-xl border border-border bg-card p-6 shadow-sm">
        <h2 className="text-lg font-semibold text-foreground">
          {t('admin.constraints.addTitle')}
        </h2>
        <p className="mt-2 text-sm text-muted-foreground">{t('admin.constraints.noColumns')}</p>
      </div>
    )
  }

  return (
    <div className="space-y-4 rounded-xl border border-border bg-card p-6 shadow-sm">
      <div>
        <h2 className="text-lg font-semibold text-foreground">
          {t('admin.constraints.addTitle')}
        </h2>
        <p className="mt-1 text-sm text-muted-foreground">{t('admin.constraints.addHint')}</p>
      </div>

      <fieldset className="space-y-2">
        <legend className="sr-only">{t('admin.constraints.columnsLegend')}</legend>
        <div className="grid grid-cols-1 gap-2 sm:grid-cols-2 lg:grid-cols-3">
          {columns.map((col) => {
            const checked = selected.includes(col.columnName)
            return (
              <label
                key={col.columnName}
                className="flex items-center gap-2.5 rounded-lg border border-border bg-background px-3 py-2 text-sm hover:bg-overlay-hover"
              >
                <Checkbox
                  checked={checked}
                  onCheckedChange={(v) => toggle(col.columnName, v === true)}
                  aria-label={col.columnName}
                />
                <span className="min-w-0 flex-1 truncate font-mono text-xs text-foreground">
                  {col.columnName}
                </span>
                <span className="shrink-0 text-[10px] uppercase tracking-wide text-muted-foreground">
                  {col.pgDataType}
                </span>
              </label>
            )
          })}
        </div>
      </fieldset>

      {error && (
        <div
          role="alert"
          className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive"
        >
          {error}
        </div>
      )}

      <div className="flex items-center gap-3">
        <Button onClick={onAdd} disabled={selected.length === 0 || addMutation.isPending}>
          {addMutation.isPending
            ? t('admin.constraints.addingButton')
            : t('admin.constraints.addButton')}
        </Button>
        <span className="text-xs text-muted-foreground">
          {selected.length > 0
            ? t('admin.constraints.selectedCount', { count: selected.length })
            : t('admin.constraints.selectPrompt')}
        </span>
      </div>
    </div>
  )
}

// ---- Existing constraints -------------------------------------------------

function ExistingConstraintsCard({
  designerId,
  constraints,
}: {
  designerId: string
  constraints: UniqueConstraint[]
}) {
  const { t } = useTranslation()
  const dropMutation = useDropConstraintMutation(designerId)
  const [dropTarget, setDropTarget] = useState<UniqueConstraint | null>(null)
  const [dropError, setDropError] = useState<string | null>(null)

  const sorted = useMemo(
    () => [...constraints].sort((a, b) => a.name.localeCompare(b.name)),
    [constraints],
  )

  return (
    <div className="space-y-4 rounded-xl border border-border bg-card p-6 shadow-sm">
      <h2 className="text-lg font-semibold text-foreground">
        {t('admin.constraints.existingTitle')}
      </h2>

      <div className="overflow-hidden rounded-lg border border-border">
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="bg-muted text-left">
                <th className="px-4 py-2">{t('admin.constraints.columnConstraintName')}</th>
                <th className="px-4 py-2">{t('admin.constraints.columnColumns')}</th>
                <th className="px-4 py-2 text-right">{t('admin.constraints.columnActions')}</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border">
              {sorted.length === 0 && (
                <tr>
                  <td colSpan={3} className="px-4 py-10 text-center text-sm text-muted-foreground">
                    <div className="flex flex-col items-center gap-2">
                      <KeyRound className="h-6 w-6 text-muted-foreground/60" />
                      {t('admin.constraints.noConstraints')}
                    </div>
                  </td>
                </tr>
              )}
              {sorted.map((c) => (
                <tr key={c.name} className="hover:bg-overlay-hover">
                  <td className="px-4 py-2 font-mono text-xs text-foreground">{c.name}</td>
                  <td className="px-4 py-2">
                    <div className="flex flex-wrap gap-1.5">
                      {c.columns.map((col) => (
                        <span
                          key={col}
                          className="inline-flex items-center rounded-full bg-primary/10 px-2.5 py-0.5 font-mono text-xs text-primary"
                        >
                          {col}
                        </span>
                      ))}
                    </div>
                  </td>
                  <td className="px-4 py-2 text-right">
                    <Button
                      variant="destructive"
                      size="sm"
                      onClick={() => {
                        setDropError(null)
                        setDropTarget(c)
                      }}
                    >
                      {t('admin.constraints.dropButton')}
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      <AlertDialog
        open={dropTarget !== null}
        onOpenChange={(open) => {
          if (!open) {
            setDropTarget(null)
            setDropError(null)
          }
        }}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{t('admin.constraints.dropConfirmTitle')}</AlertDialogTitle>
            <AlertDialogDescription>
              {t('admin.constraints.dropConfirmDescription', {
                name: dropTarget?.name ?? '',
                columns: dropTarget?.columns.join(', ') ?? '',
              })}
            </AlertDialogDescription>
          </AlertDialogHeader>
          {dropError && (
            <div
              role="alert"
              className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive"
            >
              {dropError}
            </div>
          )}
          <AlertDialogFooter>
            <AlertDialogCancel disabled={dropMutation.isPending}>
              {t('admin.constraints.cancelButton')}
            </AlertDialogCancel>
            <AlertDialogAction
              disabled={dropMutation.isPending}
              onClick={(e) => {
                e.preventDefault()
                if (!dropTarget) return
                setDropError(null)
                void dropMutation
                  .mutateAsync(dropTarget.name)
                  .then(() => setDropTarget(null))
                  .catch((err) => {
                    if (err instanceof ApiError) {
                      setDropError(err.messageKey ? t(err.messageKey) : t('errors.genericError'))
                    } else {
                      setDropError(t('errors.genericError'))
                    }
                  })
              }}
            >
              {t('admin.constraints.dropConfirmButton')}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  )
}
