import { useState, useEffect, useMemo } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from '@tanstack/react-router'
import { useQuery } from '@tanstack/react-query'
import { Layers, Link2 } from 'lucide-react'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import {
  useBindDesignerMutation,
  useRetryBindingMutation,
  useSetRoutePathMutation,
} from './menuAdminMutations'
import { useBindingDiff } from '../../menu/useBindingDiff'
import { designerApi } from '../../designer/designerApi'
import type { BindingDiffResponse, ProvisioningStatus } from '../../menu/types'
import { ProvisioningStatusBadge } from '../../../components/shared/ProvisioningStatusBadge'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '../../../components/ui/select'
import { Combobox, type ComboboxOption } from '../../../components/ui/combobox'

// Page size for the designer picker. The picker is a searchable combobox running
// in server-search mode, so the search box (not this cap) is how admins reach
// designers beyond the first page; this just bounds the unfiltered initial list.
const DESIGNER_LIST_PAGE_SIZE = 100

// Story 5.2 — AC-1, AC-3, AC-4, AC-6. P3/P5 fix: replaced free-form text inputs
// (which required admins to memorise designerIds and version numbers) with
// dropdowns sourced from /api/designers and the per-designer versions list,
// filtered to Published-only since only Published versions are bindable.
interface DesignerBindingSectionProps {
  menuId: string
  designerId: string | null
  boundVersion: number | null
  provisioningStatus: ProvisioningStatus | null
  provisioningError: string | null
  routePath: string | null
}

// Mirrors the backend SetMenuRoutePathRequestValidator: an internal path (single
// leading slash, not protocol-relative "//") OR an http(s) URL. Keep these in sync.
const ROUTE_PATH_PATTERN = /^(\/(?!\/)\S*|https?:\/\/\S+)$/

export function DesignerBindingSection({
  menuId,
  designerId,
  boundVersion,
  provisioningStatus,
  provisioningError,
  routePath,
}: DesignerBindingSectionProps) {
  const { t } = useTranslation()
  const bindMutation = useBindDesignerMutation()
  const retryMutation = useRetryBindingMutation()
  const routePathMutation = useSetRoutePathMutation(menuId)

  // Default to whichever target the menu currently uses. A menu can have at most
  // one (the two are mutually exclusive server-side), so routePath wins only when
  // there is no binding.
  const [mode, setMode] = useState<'designer' | 'route'>(routePath ? 'route' : 'designer')
  const [routePathInput, setRoutePathInput] = useState(routePath ?? '')

  // Re-sync when the committed routePath changes (e.g. after a save refetches the
  // detail query) so the textbox never shows a stale value.
  useEffect(() => {
    setRoutePathInput(routePath ?? '')
  }, [routePath])

  const [designerIdInput, setDesignerIdInput] = useState(designerId ?? '')
  const [versionInput, setVersionInput] = useState<number | null>(boundVersion)
  const [diffTargetVersion, setDiffTargetVersion] = useState<number | null>(null)

  // Sync form inputs when the committed binding changes (e.g., after a successful bind
  // the detail query refetches and props update — without this the inputs show stale values).
  useEffect(() => {
    setDesignerIdInput(designerId ?? '')
  }, [designerId])

  useEffect(() => {
    setVersionInput(boundVersion)
  }, [boundVersion])

  // Server-search mode for the designer picker. Debounce so we issue one request
  // per pause, not per keystroke; placeholderData keeps the previous page visible
  // during refetch so the combobox never unmounts/flickers while typing.
  const [designerSearch, setDesignerSearch] = useState('')
  const [debouncedDesignerSearch, setDebouncedDesignerSearch] = useState('')
  useEffect(() => {
    const id = window.setTimeout(() => setDebouncedDesignerSearch(designerSearch), 250)
    return () => window.clearTimeout(id)
  }, [designerSearch])

  const designersQuery = useQuery({
    queryKey: ['designer', 'list', 1, DESIGNER_LIST_PAGE_SIZE, debouncedDesignerSearch],
    queryFn: () =>
      designerApi.listSchemas(1, DESIGNER_LIST_PAGE_SIZE, {
        search: debouncedDesignerSearch || undefined,
      }),
    staleTime: 30_000,
    placeholderData: (prev) => prev,
  })

  // getSchema() with no version returns the latest snapshot AND a versions[]
  // array with status info — that's the only place to learn which versions
  // are Published vs Draft/Archived.
  const versionsQuery = useQuery({
    queryKey: ['designer', 'schema', designerIdInput],
    queryFn: () => designerApi.getSchema(designerIdInput),
    enabled: designerIdInput.length > 0,
    staleTime: 30_000,
  })

  const publishedVersions = useMemo(() => {
    const all = versionsQuery.data?.versions ?? []
    return all
      .filter((v) => v.status === 'Published')
      .sort((a, b) => b.version - a.version)
  }, [versionsQuery.data])

  // Auto-select the latest Published version when the designer changes, unless
  // the current versionInput is already a valid published version (e.g., the
  // committed binding still maps to a Published version, or the admin made an
  // explicit pick that's still valid).
  useEffect(() => {
    if (publishedVersions.length === 0) {
      if (versionInput !== null) setVersionInput(null)
      return
    }
    const stillValid =
      versionInput !== null && publishedVersions.some((v) => v.version === versionInput)
    if (!stillValid) {
      setVersionInput(publishedVersions[0].version)
    }
  }, [publishedVersions, versionInput])

  const diffQuery = useBindingDiff(designerId ? menuId : null, diffTargetVersion)

  const onBind = (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const trimmed = designerIdInput.trim()
    if (trimmed.length === 0 || versionInput === null || versionInput < 1) return
    bindMutation.mutate({ menuId, designerId: trimmed, version: versionInput })
  }

  const onRetry = () => {
    retryMutation.mutate(menuId)
  }

  const onPreview = () => {
    if (versionInput !== null) setDiffTargetVersion(versionInput)
  }

  const trimmedRoutePath = routePathInput.trim()
  // Empty is valid — it clears the route. Non-empty must match the shared pattern.
  const isRoutePathValid = trimmedRoutePath.length === 0 || ROUTE_PATH_PATTERN.test(trimmedRoutePath)

  const onSaveRoutePath = (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    if (!isRoutePathValid) return
    routePathMutation.mutate(trimmedRoutePath.length === 0 ? null : trimmedRoutePath)
  }

  const isBinding = bindMutation.isPending
  const isRetrying = retryMutation.isPending
  const isSavingRoutePath = routePathMutation.isPending

  const designers = designersQuery.data?.data ?? []
  const designerOptions = useMemo<ComboboxOption[]>(
    () =>
      (designersQuery.data?.data ?? []).map((d) => ({
        value: d.designerId,
        label: `${d.displayName} (${d.designerId})`,
      })),
    [designersQuery.data],
  )
  const canBind =
    designerIdInput.length > 0 && versionInput !== null && publishedVersions.length > 0

  return (
    <section
      style={{
        display: 'flex',
        flexDirection: 'column',
        gap: '0.75rem',
        padding: '1rem',
        border: '1px solid var(--border)',
        borderRadius: '0.5rem',
      }}
    >
      <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between' }}>
        <h2 style={{ fontSize: '1.125rem', fontWeight: 600 }}>
          {t('admin.menus.targetSectionTitle')}
        </h2>
        {mode === 'designer' && <ProvisioningStatusBadge status={provisioningStatus} />}
      </div>

      {/* Mode toggle — a menu links to EITHER a bound Designer's data view OR a
          custom route path, never both. Switching mode only changes the editor
          shown; nothing is persisted until the admin saves within that mode.
          Rendered as a segmented control (role=radiogroup) for a polished, on-brand
          look that still exposes radio semantics to assistive tech. */}
      <div
        role="radiogroup"
        aria-label={t('admin.menus.targetSectionTitle')}
        className="inline-flex w-fit items-center gap-1 rounded-xl border border-border bg-muted p-1"
      >
        {([
          { value: 'designer', label: t('admin.menus.targetModeDesigner'), Icon: Layers },
          { value: 'route', label: t('admin.menus.targetModeRoute'), Icon: Link2 },
        ] as const).map(({ value, label, Icon }) => {
          const active = mode === value
          return (
            <button
              key={value}
              type="button"
              role="radio"
              aria-checked={active}
              onClick={() => setMode(value)}
              className={cn(
                'inline-flex items-center gap-1.5 rounded-lg px-3 py-1.5 text-sm font-medium transition-all focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/50',
                active
                  ? 'bg-card text-primary shadow-sm ring-1 ring-border'
                  : 'text-muted-foreground hover:text-foreground',
              )}
            >
              <Icon className="h-4 w-4" />
              {label}
            </button>
          )
        })}
      </div>

      {mode === 'route' ? (
        <form
          onSubmit={onSaveRoutePath}
          style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem' }}
        >
          <div style={{ display: 'flex', flexDirection: 'column', gap: '0.25rem' }}>
            <label htmlFor="menu-route-path">{t('admin.menus.routePathLabel')}</label>
            <input
              id="menu-route-path"
              type="text"
              value={routePathInput}
              onChange={(e) => setRoutePathInput(e.target.value)}
              placeholder={t('admin.menus.routePathPlaceholder')}
              aria-invalid={!isRoutePathValid}
              style={{ padding: '0.4rem 0.5rem', border: '1px solid var(--border)', borderRadius: '0.3rem' }}
            />
            <p style={{ color: 'var(--muted-foreground)', margin: 0, fontSize: '0.8rem' }}>
              {t('admin.menus.routePathHelp')}
            </p>
            {!isRoutePathValid && (
              <p role="alert" style={{ color: 'var(--destructive)', margin: 0, fontSize: '0.8rem' }}>
                {t('admin.menus.routePathInvalid')}
              </p>
            )}
            {designerId !== null && (
              <p style={{ color: 'var(--destructive)', margin: 0, fontSize: '0.8rem' }}>
                {t('admin.menus.routePathClearsBinding')}
              </p>
            )}
          </div>
          <div>
            <Button type="submit" disabled={isSavingRoutePath || !isRoutePathValid}>
              {isSavingRoutePath
                ? t('admin.menus.savingButton')
                : t('admin.menus.saveRoutePathButton')}
            </Button>
          </div>
        </form>
      ) : (
        <>
      {designerId === null ? (
        <p style={{ color: 'var(--muted-foreground)', margin: 0 }}>{t('admin.menus.noBinding')}</p>
      ) : (
        <dl style={{ display: 'grid', gridTemplateColumns: 'max-content 1fr', gap: '0.25rem 1rem', margin: 0 }}>
          <dt style={{ color: 'var(--muted-foreground)' }}>{t('admin.menus.designerIdLabel')}</dt>
          <dd style={{ margin: 0 }}>{designerId}</dd>
          <dt style={{ color: 'var(--muted-foreground)' }}>{t('admin.menus.versionLabel')}</dt>
          <dd style={{ margin: 0 }}>{boundVersion ?? '—'}</dd>
          <dt style={{ color: 'var(--muted-foreground)' }}>{t('admin.menus.provisioningStatusLabel')}</dt>
          <dd style={{ margin: 0 }}>{provisioningStatus ?? '—'}</dd>
        </dl>
      )}

      {provisioningStatus === 'Error' && provisioningError && (
        <div
          role="alert"
          style={{ color: 'var(--destructive)', fontSize: '0.85rem', whiteSpace: 'pre-wrap' }}
        >
          {t('admin.menus.provisioningError', { error: provisioningError })}
        </div>
      )}

      {provisioningStatus === 'Error' && (
        <div>
          <Button type="button" onClick={onRetry} disabled={isRetrying}>
            {isRetrying ? t('admin.menus.bindingButton') : t('admin.menus.retryButton')}
          </Button>
        </div>
      )}

      <form
        onSubmit={onBind}
        style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem' }}
      >
        <h3 style={{ fontSize: '1rem', fontWeight: 600, margin: 0 }}>
          {t('admin.menus.bindDesignerLabel')}
        </h3>

        {designersQuery.isPending ? (
          <p style={{ color: 'var(--muted-foreground)', margin: 0 }}>{t('admin.menus.loadingDesigners')}</p>
        ) : designers.length === 0 && debouncedDesignerSearch === '' ? (
          // Only treat an empty result as "no designers exist" when there is no
          // active search — a search that matches nothing is the combobox's own
          // empty state, not a reason to hide the whole binding form.
          <p style={{ color: 'var(--muted-foreground)', margin: 0 }}>
            {t('admin.menus.noDesignersAvailable')}{' '}
            <Link to="/designer/library">{t('admin.menus.openDesignerLibrary')}</Link>
          </p>
        ) : (
          <>
            <div style={{ display: 'flex', flexDirection: 'column', gap: '0.25rem' }}>
              <label htmlFor="bind-designer-id">{t('admin.menus.designerIdLabel')}</label>
              <Combobox
                id="bind-designer-id"
                value={designerIdInput}
                onValueChange={(v) => setDesignerIdInput(v)}
                options={designerOptions}
                onSearchChange={setDesignerSearch}
                loading={designersQuery.isFetching}
                placeholder={t('admin.menus.selectDesignerPlaceholder')}
                searchPlaceholder={t('admin.menus.searchDesignerPlaceholder')}
                emptyText={t('admin.menus.noDesignersMatch')}
                aria-label={t('admin.menus.selectDesignerPlaceholder')}
              />
            </div>

            <div style={{ display: 'flex', flexDirection: 'column', gap: '0.25rem' }}>
              <label htmlFor="bind-version">{t('admin.menus.versionLabel')}</label>
              {designerIdInput.length === 0 ? (
                <p style={{ color: 'var(--muted-foreground)', margin: 0, fontSize: '0.85rem' }}>
                  {t('admin.menus.selectDesignerFirst')}
                </p>
              ) : versionsQuery.isPending ? (
                <p style={{ color: 'var(--muted-foreground)', margin: 0, fontSize: '0.85rem' }}>
                  {t('admin.menus.loadingVersions')}
                </p>
              ) : publishedVersions.length === 0 ? (
                <p style={{ color: 'var(--destructive)', margin: 0, fontSize: '0.85rem' }}>
                  {t('admin.menus.noPublishedVersions')}{' '}
                  <Link
                    to="/designer/$designerId"
                    params={{ designerId: designerIdInput }}
                  >
                    {t('admin.menus.openDesignerCanvas')}
                  </Link>
                </p>
              ) : (
                <Select
                  value={versionInput !== null ? String(versionInput) : undefined}
                  onValueChange={(v) => setVersionInput(Number(v))}
                >
                  <SelectTrigger id="bind-version" className="w-full">
                    <SelectValue placeholder={t('admin.menus.selectVersionPlaceholder')} />
                  </SelectTrigger>
                  <SelectContent>
                    {publishedVersions.map((v) => (
                      <SelectItem key={v.version} value={String(v.version)}>
                        v{v.version}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              )}
            </div>

            <div style={{ display: 'flex', gap: '0.5rem' }}>
              <Button type="submit" disabled={isBinding || !canBind}>
                {isBinding
                  ? t('admin.menus.bindingButton')
                  : designerId === null
                    ? t('admin.menus.bindButton')
                    : t('admin.menus.applyBindingButton')}
              </Button>
              {designerId !== null && versionInput !== null && (
                <Button
                  type="button"
                  variant="outline"
                  onClick={onPreview}
                  disabled={diffQuery.isFetching}
                >
                  {t('admin.menus.viewDiffButton')}
                </Button>
              )}
            </div>
          </>
        )}
      </form>

      {diffTargetVersion !== null && (
        <BindingDiffPanel
          diff={diffQuery.data}
          isLoading={diffQuery.isFetching}
          onClose={() => {
            setDiffTargetVersion(null)
          }}
        />
      )}
        </>
      )}
    </section>
  )
}

interface BindingDiffPanelProps {
  diff: BindingDiffResponse | undefined
  isLoading: boolean
  onClose: () => void
}

// The design-system Dialog primitive arrives in Epic 7. For Story 5.2 an
// inline aside is sufficient — it stays in the section flow, doesn't trap
// focus prematurely, and uses role="dialog" so AT users get the modal cue.
function BindingDiffPanel({ diff, isLoading, onClose }: BindingDiffPanelProps) {
  const { t } = useTranslation()
  return (
    <aside
      role="dialog"
      aria-label={t('admin.menus.bindingDiffTitle')}
      style={{
        marginTop: '0.5rem',
        padding: '0.75rem',
        border: '1px dashed var(--border)',
        borderRadius: '0.4rem',
        display: 'flex',
        flexDirection: 'column',
        gap: '0.5rem',
      }}
    >
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline' }}>
        <h3 style={{ fontSize: '1rem', fontWeight: 600, margin: 0 }}>
          {t('admin.menus.bindingDiffTitle')}
        </h3>
        <Button type="button" variant="ghost" size="sm" onClick={onClose}>
          {t('admin.menus.cancelButton')}
        </Button>
      </div>
      {isLoading && <p>{t('admin.menus.loading')}</p>}
      {diff && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: '0.4rem', fontSize: '0.85rem' }}>
          <DiffList label={t('admin.menus.bindingDiffColumnsToAdd')} items={diff.columnsToAdd} />
          <DiffList label={t('admin.menus.bindingDiffAlreadyPresent')} items={diff.columnsAlreadyPresent} />
          <DiffList label={t('admin.menus.bindingDiffOrphaned')} items={diff.orphanedColumns} />
          <div>
            <strong>{t('admin.menus.bindingDiffEstimatedDdl')}:</strong>
            <pre style={{ background: 'var(--muted)', padding: '0.5rem', margin: '0.25rem 0 0', overflowX: 'auto' }}>
              {diff.estimatedDdl.join('\n')}
            </pre>
          </div>
        </div>
      )}
    </aside>
  )
}

function DiffList({ label, items }: { label: string; items: string[] }) {
  if (items.length === 0) {
    return (
      <div>
        <strong>{label}:</strong> <span style={{ color: 'var(--muted-foreground)' }}>—</span>
      </div>
    )
  }
  return (
    <div>
      <strong>{label}:</strong>
      <ul style={{ margin: '0.25rem 0 0 1rem', padding: 0 }}>
        {items.map((c) => (
          <li key={c}>{c}</li>
        ))}
      </ul>
    </div>
  )
}
