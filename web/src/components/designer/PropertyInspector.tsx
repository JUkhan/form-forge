import { useEffect, useMemo } from 'react'
import type { ChangeEvent, ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { MousePointerClick } from 'lucide-react'
import { useQuery } from '@tanstack/react-query'
import { getFieldKey, type DesignerElement } from '@/types/designer'
import { designerApi } from '@/features/designer/designerApi'
import {
  listDatasets,
  getDataset,
  getDatasetColumns,
  getDatasetColumnsByName,
} from '@/features/datasets/datasetApi'
import { extractPlaceholders } from '@/features/datasets/queryParameters'
import { useDesignerCanvasStore } from '@/store/designerCanvas'
import { cn } from '@/lib/utils'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { Checkbox } from '@/components/ui/checkbox'
import { Tooltip, TooltipTrigger, TooltipContent } from '@/components/ui/tooltip'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Combobox, type ComboboxOption } from '@/components/ui/combobox'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from '@/components/ui/dialog'
import { FILTER_OPERATORS } from '@/features/datasets/types/builderState'
import {
  ALLOWED_TEXT_INPUT_TYPES,
  TEXT_INPUT_TYPE_LABELS,
  resolveTextInputType,
} from './textInputTypes'
import { isValidFieldKey } from './fieldKeyValidation'
import { PgTypeField } from './PgTypeField'
import {
  addCondition,
  NO_VALUE_OPERATORS,
  parseDatasetFilter,
  removeCondition,
  setCombinator,
  updateCondition,
  type DatasetFilterGroup,
} from './datasetFilter'
import {
  addQueryParamBinding,
  parseQueryParamBindings,
  removeQueryParamBinding,
  unmappedParams,
  updateQueryParamBinding,
  type QueryParamBinding,
} from './queryParamBindings'
import { parseTableColumns, type TableColumnConfig } from './datasetTable'

// Compact override for the inspector — shadcn Input defaults to h-8 / text-sm
// (text-base on mobile). The inspector lays out two-column field rows in a
// fixed-width side panel, so we tighten height and font size.
const INPUT_COMPACT_CLASS = 'h-7 text-xs'
const TRIGGER_COMPACT_CLASS = 'h-7 text-xs w-full'

// The shadcn Select primitive (Radix-backed) won't accept an empty-string item
// value, and won't render an item whose value isn't in the SelectContent list.
// To preserve the "empty = not selected" + "orphan stale id" semantics that a
// native <select> provides, we use these sentinel values:
//   - SENTINEL_EMPTY stands in for "" (the unselected state).
//   - Stale values that aren't in the live options list are still passed to
//     Radix as the controlled value; we add a hidden helper SelectItem so the
//     trigger has something to display instead of falling back to placeholder.
const SENTINEL_EMPTY = '__empty__'

function findById(node: DesignerElement | null, id: string): DesignerElement | null {
  if (!node) return null
  if (node.id === id) return node
  for (const child of node.children ?? []) {
    const found = findById(child, id)
    if (found) return found
  }
  return null
}

// One linear walk from the root: returns the parent of the element with `id`, or
// null when `id` is the root itself or absent.
function findParent(node: DesignerElement | null, id: string): DesignerElement | null {
  if (!node) return null
  for (const child of node.children ?? []) {
    if (child.id === id) return node
    const found = findParent(child, id)
    if (found) return found
  }
  return null
}

// Walks the tree once, returning the nearest ancestor that hosts a row/node template
// — a Repeater OR a TreeView. Both carry rowDesignerId/rowVersion, so a Repeater Field
// dropped into either binds its `fieldName` against that designer's fields. Used by the
// Repeater Field inspector to populate the field-name selector and to decide whether the
// 'Header name' field should surface (only meaningful when the parent is a tabular Repeater).
function findRepeaterAncestor(
  root: DesignerElement | null,
  id: string,
): DesignerElement | null {
  if (!root) return null
  function walk(node: DesignerElement, ancestors: DesignerElement[]): DesignerElement | null {
    if (node.id === id) {
      for (let i = ancestors.length - 1; i >= 0; i--) {
        if (ancestors[i].type === 'Repeater' || ancestors[i].type === 'TreeView') return ancestors[i]
      }
      return null
    }
    for (const child of node.children ?? []) {
      const found = walk(child, [...ancestors, node])
      if (found) return found
    }
    return null
  }
  return walk(root, [])
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="flex flex-col gap-1">
      <span className="text-xs font-medium text-muted-foreground">{label}</span>
      {children}
    </label>
  )
}

function BoolField({
  label,
  checked,
  onChange,
  disabled,
}: {
  label: string
  checked: boolean
  onChange: (v: boolean) => void
  disabled?: boolean
}) {
  return (
    <label
      className={cn(
        'flex items-center gap-2 text-xs text-muted-foreground',
        disabled ? 'cursor-not-allowed opacity-70' : 'cursor-pointer',
      )}
    >
      <Checkbox
        checked={checked}
        disabled={disabled}
        onCheckedChange={(v) => onChange(v === true)}
      />
      {label}
    </label>
  )
}

type UpdateProp = (id: string, key: string, value: unknown) => void

function LabelModeField({
  id,
  value,
  updateProp,
}: {
  id: string
  value: string
  updateProp: UpdateProp
}) {
  const { t } = useTranslation()
  return (
    <Field label={t('designer.inspector.fields.labelMode')}>
      <Select value={value} onValueChange={(v) => updateProp(id, 'labelMode', v)}>
        <SelectTrigger className={TRIGGER_COMPACT_CLASS}>
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          <SelectItem value="vertical">{t('designer.inspector.options.labelModeVertical')}</SelectItem>
          <SelectItem value="horizontal">{t('designer.inspector.options.labelModeHorizontal')}</SelectItem>
        </SelectContent>
      </Select>
    </Field>
  )
}

function FieldKeyField({
  id,
  value,
  updateProp,
  // `optional` drops the required-when-empty error + the ` *` marker. Used by the
  // Repeater container, whose field key names the one-to-many relation but is not
  // required by the column-provisioning validators (which skip the Repeater).
  optional = false,
  help,
}: {
  id: string
  value: string
  updateProp: UpdateProp
  optional?: boolean
  help?: string
}) {
  const { t } = useTranslation()
  const isEmpty = value === ''
  const isInvalid = !isEmpty && !isValidFieldKey(value)
  const showRequiredError = isEmpty && !optional
  return (
    <Field label={optional ? t('designer.inspector.fieldKeyField') : `${t('designer.inspector.fieldKeyField')} *`}>
      <Input
        value={value}
        onChange={(e) => updateProp(id, 'fieldKey', e.target.value)}
        placeholder={t('designer.inspector.placeholders.fieldKey')}
        className={cn(INPUT_COMPACT_CLASS, (showRequiredError || isInvalid) && 'border-destructive')}
      />
      {showRequiredError && (
        <span className="text-[10px] text-destructive">{t('errors.required')}</span>
      )}
      {isInvalid && (
        <span className="text-[10px] text-destructive">{t('designer.inspector.fieldKeyInvalid')}</span>
      )}
      {help && !showRequiredError && !isInvalid && (
        <span className="text-[10px] text-muted-foreground">{help}</span>
      )}
    </Field>
  )
}

// Walks a row-form designer's tree and returns its scalar field keys (the
// candidates for a Repeater Field's "Field name"). Nested Repeaters are skipped
// entirely — their children are a separate collection, not columns of this row.
function extractRowFormFieldKeys(root: DesignerElement | null): string[] {
  const out: string[] = []
  const seen = new Set<string>()
  function walk(el: DesignerElement) {
    if (el.type === 'Repeater') return
    const key = getFieldKey(el.properties)
    if (key !== '' && !seen.has(key)) {
      seen.add(key)
      out.push(key)
    }
    for (const child of el.children ?? []) walk(child)
  }
  if (root) walk(root)
  return out
}

// "Field name" selector for a Repeater Field — its options are the scalar field
// keys of the parent Repeater's referenced row-form component, at the version
// pinned on that Repeater (its published version). Disabled with a hint until
// the parent Repeater has a row-form component + version. When the Field has no
// Repeater/TreeView ancestor (a top-level Field on a CRUD form), `localFieldKeys`
// supplies the options instead — the scalar fieldKeys already placed on THIS canvas.
function RepeaterFieldNameSelect({
  id,
  value,
  rowDesignerId,
  rowVersion,
  datasetId,
  localFieldKeys,
  updateProp,
}: {
  id: string
  value: string
  rowDesignerId: string
  rowVersion: number | undefined
  // When the parent TreeView is dataset-backed, the field options come from the
  // dataset's columns instead of the row form's fieldKeys.
  datasetId?: string
  // Set for a top-level Field (no Repeater/TreeView ancestor): the current
  // canvas's own scalar fieldKeys, used as the option source.
  localFieldKeys?: string[]
  updateProp: UpdateProp
}) {
  const { t } = useTranslation()
  const useDataset = (datasetId ?? '') !== ''
  // Top-level Field: no parent row-form/dataset source, so options come from the
  // fields on this same canvas (passed in by the inspector).
  const useLocal = !useDataset && rowDesignerId === '' && localFieldKeys !== undefined

  const schemaEnabled = !useDataset && !useLocal && rowDesignerId !== '' && rowVersion !== undefined
  const schemaQuery = useQuery({
    queryKey: ['designer', 'schema', rowDesignerId, rowVersion],
    queryFn: () => designerApi.getSchema(rowDesignerId, rowVersion),
    enabled: schemaEnabled,
    staleTime: 60_000,
  })
  const columnsQuery = useQuery({
    queryKey: ['dataset', 'columns', datasetId],
    queryFn: () => getDatasetColumns(datasetId as string),
    enabled: useDataset,
    staleTime: 60_000,
  })

  const fieldKeys = useLocal
    ? (localFieldKeys as string[])
    : useDataset
      ? (columnsQuery.data?.columns ?? [])
      : extractRowFormFieldKeys(schemaQuery.data?.rootElement ?? null)
  const activeQuery = useDataset ? columnsQuery : schemaQuery
  const enabled = useLocal || useDataset || schemaEnabled
  // Loading/error states only apply to the query-backed sources, never to the
  // synchronous local-fieldKeys source.
  const isLoading = !useLocal && activeQuery.isLoading
  const isError = !useLocal && activeQuery.isError
  const valueInList = value !== '' && fieldKeys.includes(value)
  const staleSuffix = useLocal
    ? '(not on this form)'
    : useDataset
      ? '(not in dataset)'
      : '(not in row form)'

  const placeholder = !enabled
    ? t('designer.inspector.placeholders.fieldNameNoSource')
    : isLoading
      ? t('designer.inspector.placeholders.componentsLoading')
      : isError
        ? t('designer.inspector.placeholders.componentsLoadError')
        : fieldKeys.length === 0
          ? t(
              useLocal
                ? 'designer.inspector.placeholders.fieldNameEmptyLocal'
                : 'designer.inspector.placeholders.fieldNameEmpty',
            )
          : t('designer.inspector.placeholders.fieldNameSelect')

  const handleChange = (next: string) => {
    updateProp(id, 'fieldName', next === SENTINEL_EMPTY ? '' : next)
  }

  return (
    <Field label={t('designer.inspector.fields.fieldName')}>
      <Select
        value={value === '' ? SENTINEL_EMPTY : value}
        onValueChange={handleChange}
        disabled={!enabled || isLoading || isError}
      >
        <SelectTrigger className={TRIGGER_COMPACT_CLASS}>
          <SelectValue placeholder={placeholder} />
        </SelectTrigger>
        <SelectContent>
          <SelectItem value={SENTINEL_EMPTY}>{placeholder}</SelectItem>
          {/* Keep a stale fieldName visible (e.g. the source removed the field). */}
          {value !== '' && !valueInList && (
            <SelectItem value={value}>{value} {staleSuffix}</SelectItem>
          )}
          {fieldKeys.map((k) => (
            <SelectItem key={k} value={k}>
              {k}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
      <span
        className="text-[10px] text-muted-foreground"
        dangerouslySetInnerHTML={{ __html: t('designer.inspector.help.fieldName') }}
      />
    </Field>
  )
}

// TreeView "Dataset source" — optionally back the tree with a dataset VIEW. Pick a dataset,
// then choose which of its columns is the node id (key) and which is the parent self-reference.
// When set, the tree's levels / paging / search read from the dataset, and the RepeaterField
// pickers list the dataset's columns. Clearing the dataset restores the provisioned-table tree.
function TreeViewDatasetSourceFields({
  id,
  datasetId,
  keyField,
  parentField,
  updateProp,
}: {
  id: string
  datasetId: string
  keyField: string
  parentField: string
  updateProp: UpdateProp
}) {
  const { t } = useTranslation()

  const datasetsQuery = useQuery({
    queryKey: ['datasets', 'list', 1, 100],
    queryFn: () => listDatasets(1, 100),
    staleTime: 60_000,
  })
  const datasets = datasetsQuery.data?.data ?? []
  const datasetInList = datasets.some((d) => d.id === datasetId)

  const columnsQuery = useQuery({
    queryKey: ['dataset', 'columns', datasetId],
    queryFn: () => getDatasetColumns(datasetId),
    enabled: datasetId !== '',
    staleTime: 60_000,
  })
  const columns = columnsQuery.data?.columns ?? []
  const columnsReady = datasetId !== '' && !columnsQuery.isLoading && !columnsQuery.isError

  const handleDatasetChange = (next: string) => {
    updateProp(id, 'optionsDatasetId', next === SENTINEL_EMPTY ? '' : next)
    // Clear column mappings whenever the dataset changes or is cleared.
    updateProp(id, 'datasetKeyField', '')
    updateProp(id, 'datasetParentField', '')
  }

  const datasetOptions: ComboboxOption[] = [
    { value: SENTINEL_EMPTY, label: t('designer.inspector.fields.treeViewDatasetNone') },
    ...(!datasetInList && datasetId !== ''
      ? [{ value: datasetId, label: `${datasetId} (not in list)` }]
      : []),
    ...datasets.map((d) => ({ value: d.id, label: d.datasetName })),
  ]

  const colOptions = (current: string): ComboboxOption[] => [
    ...(current !== '' && !columns.includes(current)
      ? [{ value: current, label: `${current} (not in dataset)` }]
      : []),
    ...columns.map((c) => ({ value: c, label: c })),
  ]

  return (
    <>
      <Field label={t('designer.inspector.fields.treeViewDataset')}>
        <Combobox
          value={datasetId === '' ? SENTINEL_EMPTY : datasetId}
          onValueChange={handleDatasetChange}
          options={datasetOptions}
          placeholder={t('designer.inspector.fields.treeViewDatasetNone')}
          searchPlaceholder={t('designer.inspector.placeholders.datasetsSelect')}
          disabled={datasetsQuery.isLoading || datasetsQuery.isError}
          loading={datasetsQuery.isLoading}
          className={TRIGGER_COMPACT_CLASS}
          aria-label={t('designer.inspector.fields.treeViewDataset')}
        />
        <span className="text-[10px] text-muted-foreground">
          {t('designer.inspector.help.treeViewDataset')}
        </span>
      </Field>
      {datasetId !== '' && (
        <>
          <Field label={`${t('designer.inspector.fields.treeViewDatasetKeyField')} *`}>
            <Combobox
              value={keyField}
              onValueChange={(v) => updateProp(id, 'datasetKeyField', v)}
              options={colOptions(keyField)}
              placeholder={t('designer.inspector.placeholders.datasetColumnSelect')}
              disabled={!columnsReady}
              loading={columnsQuery.isLoading}
              className={cn(TRIGGER_COMPACT_CLASS, keyField === '' && 'border-destructive')}
              aria-label={t('designer.inspector.fields.treeViewDatasetKeyField')}
            />
          </Field>
          <Field label={`${t('designer.inspector.fields.treeViewDatasetParentField')} *`}>
            <Combobox
              value={parentField}
              onValueChange={(v) => updateProp(id, 'datasetParentField', v)}
              options={colOptions(parentField)}
              placeholder={t('designer.inspector.placeholders.datasetColumnSelect')}
              disabled={!columnsReady}
              loading={columnsQuery.isLoading}
              className={cn(TRIGGER_COMPACT_CLASS, parentField === '' && 'border-destructive')}
              aria-label={t('designer.inspector.fields.treeViewDatasetParentField')}
            />
          </Field>
        </>
      )}
    </>
  )
}

// TreeView "Auth filter column" — a filterable combobox of the node-template designer's
// (rowDesignerId) scalar field keys. Optional; "(No filter)" clears it. Same data source
// as RepeaterFieldNameSelect, but rendered as a searchable Combobox.
function TreeViewAuthFilterSelect({
  id,
  value,
  rowDesignerId,
  rowVersion,
  updateProp,
}: {
  id: string
  value: string
  rowDesignerId: string
  rowVersion: number | undefined
  updateProp: UpdateProp
}) {
  const { t } = useTranslation()
  const enabled = rowDesignerId !== ''
  const schemaQuery = useQuery({
    queryKey: ['designer', 'schema', rowDesignerId, rowVersion ?? 'latest'],
    queryFn: () => designerApi.getSchema(rowDesignerId, rowVersion),
    enabled,
    staleTime: 60_000,
  })

  const fieldKeys = extractRowFormFieldKeys(schemaQuery.data?.rootElement ?? null)
  const options: ComboboxOption[] = [
    { value: SENTINEL_EMPTY, label: t('designer.inspector.options.authFilterNone') },
    ...fieldKeys.map((k) => ({ value: k, label: k })),
  ]

  const placeholder = !enabled
    ? t('designer.inspector.placeholders.fieldNameNoSource')
    : schemaQuery.isLoading
      ? t('designer.inspector.placeholders.componentsLoading')
      : t('designer.inspector.placeholders.fieldNameSelect')

  return (
    <Field label={t('designer.inspector.fields.authFilterColumn')}>
      <Combobox
        value={value}
        onValueChange={(v) => updateProp(id, 'authFilterColumn', v === SENTINEL_EMPTY ? '' : v)}
        options={options}
        placeholder={placeholder}
        searchPlaceholder={t('designer.treeView.searchPlaceholder')}
        emptyText={t('designer.inspector.placeholders.fieldNameEmpty')}
        disabled={!enabled || schemaQuery.isLoading}
        loading={schemaQuery.isLoading}
        aria-label={t('designer.inspector.fields.authFilterColumn')}
      />
      <span className="text-[10px] text-muted-foreground">
        {t('designer.inspector.help.treeViewAuthFilterColumn')}
      </span>
    </Field>
  )
}

function StyleClassesField({
  id,
  value,
  updateProp,
}: {
  id: string
  value: string
  updateProp: UpdateProp
}) {
  const { t } = useTranslation()
  return (
    <Field label={t('designer.inspector.fields.styleClasses')}>
      <Input
        value={value}
        onChange={(e) => updateProp(id, 'styleClasses', e.target.value)}
        placeholder={t('designer.inspector.placeholders.styleClasses')}
        className={INPUT_COMPACT_CLASS}
      />
      <span className="text-[10px] text-muted-foreground">
        {t('designer.inspector.help.styleClasses')}
      </span>
    </Field>
  )
}

// Story B-1-4-followup — shared "table column" controls emitted for every field
// type that can become a column in the menu's record-list table: Show in table,
// Column header (override), Column order. showInTable defaults to true (checked)
// when absent so designs authored before this feature keep showing every field.
// Designer-backed Dropdown and Repeater columns render a derived value (label /
// child-row count), surfaced via an extra hint under the toggle.
function TableColumnFields({
  element,
  updateProp,
}: {
  element: DesignerElement
  updateProp: UpdateProp
}) {
  const { t } = useTranslation()
  const p = element.properties
  const showInTable = p.showInTable !== false
  const columnHeader =
    p.columnHeader !== undefined && p.columnHeader !== null ? String(p.columnHeader) : ''
  const columnOrder =
    p.columnOrder !== undefined && Number.isFinite(Number(p.columnOrder))
      ? Number(p.columnOrder)
      : undefined

  // Reference fields whose column shows a derived value rather than the raw cell.
  const derivedHelpKey =
    element.type === 'Dropdown' && p.optionsSource === 'designer'
      ? 'designer.inspector.help.showInTableDropdownDesigner'
      : element.type === 'Repeater'
        ? 'designer.inspector.help.showInTableRepeater'
        : null

  return (
    <div className="mt-2 flex flex-col gap-3 border-t border-border pt-3">
      <p className="text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">
        {t('designer.inspector.tableColumnHeader')}
      </p>
      <BoolField
        label={t('designer.inspector.fields.showInTable')}
        checked={showInTable}
        onChange={(v) => updateProp(element.id, 'showInTable', v)}
      />
      {derivedHelpKey && (
        <span className="text-[10px] text-muted-foreground">{t(derivedHelpKey)}</span>
      )}
      {showInTable && (
        <>
          <Field label={t('designer.inspector.fields.columnHeader')}>
            <Input
              value={columnHeader}
              onChange={(e) => updateProp(element.id, 'columnHeader', e.target.value)}
              placeholder={t('designer.inspector.placeholders.columnHeader')}
              className={INPUT_COMPACT_CLASS}
            />
          </Field>
          <Field label={t('designer.inspector.fields.columnOrder')}>
            <OptionalNumberInput
              value={columnOrder}
              onChange={(v) => updateProp(element.id, 'columnOrder', v)}
              onClear={() => updateProp(element.id, 'columnOrder', undefined)}
            />
          </Field>
        </>
      )}
    </div>
  )
}

// Conditional visibility block emitted for every element type. hideWhenAll /
// hideWhenAny each take a comma-separated list of fieldKey names; the runtime
// hides the element when its rule fires. Hidden elements are removed from
// layout AND from the submitted payload — see visibility.ts for resolver semantics.
function VisibilityFields({
  id,
  hideWhenAll,
  hideWhenAny,
  updateProp,
}: {
  id: string
  hideWhenAll: string
  hideWhenAny: string
  updateProp: UpdateProp
}) {
  const { t } = useTranslation()
  return (
    <div className="mt-2 flex flex-col gap-3 border-t border-border pt-3">
      <p className="text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">
        {t('designer.inspector.conditionalVisibilityHeader')}
      </p>
      <Field label={t('designer.inspector.fields.hideWhenAll')}>
        <Input
          value={hideWhenAll}
          onChange={(e) => updateProp(id, 'hideWhenAll', e.target.value)}
          placeholder={t('designer.inspector.placeholders.hideWhenAll')}
          className={INPUT_COMPACT_CLASS}
        />
        <span
          className="text-[10px] text-muted-foreground"
          dangerouslySetInnerHTML={{ __html: t('designer.inspector.help.hideWhenAll') }}
        />
      </Field>
      <Field label={t('designer.inspector.fields.hideWhenAny')}>
        <Input
          value={hideWhenAny}
          onChange={(e) => updateProp(id, 'hideWhenAny', e.target.value)}
          placeholder={t('designer.inspector.placeholders.hideWhenAny')}
          className={INPUT_COMPACT_CLASS}
        />
        <span
          className="text-[10px] text-muted-foreground"
          dangerouslySetInnerHTML={{ __html: t('designer.inspector.help.hideWhenAny') }}
        />
      </Field>
    </div>
  )
}

function NumberInput({
  value,
  onChange,
}: {
  value: number
  onChange: (v: number) => void
}) {
  return (
    <Input
      type="number"
      value={value}
      onChange={(e) => {
        const raw = e.target.value
        // Treat empty input as "preserve current value" — `Number('') === 0`
        // would otherwise clamp the field to 0 the moment the user selects-all
        // and backspaces (font size disappears, padding collapses, rows count
        // goes invalid). The user explicitly types `0` to set zero.
        if (raw === '') return
        const v = Number(raw)
        if (!isNaN(v)) onChange(v)
      }}
      className={INPUT_COMPACT_CLASS}
    />
  )
}

function OptionalNumberInput({
  value,
  onChange,
  onClear,
}: {
  value: number | undefined
  onChange: (v: number) => void
  onClear: () => void
}) {
  function handle(e: ChangeEvent<HTMLInputElement>) {
    const raw = e.target.value
    if (raw === '') {
      onClear()
      return
    }
    const v = Number(raw)
    if (!isNaN(v)) onChange(v)
  }
  return (
    <Input
      type="number"
      value={value !== undefined ? value : ''}
      onChange={handle}
      className={INPUT_COMPACT_CLASS}
    />
  )
}

// Designer ID + Version No fields for the Repeater row-form pointer.
function RepeaterRowFormFields({
  id,
  rowDesignerId,
  rowVersion,
  updateProp,
}: {
  id: string
  rowDesignerId: string
  rowVersion: number | undefined
  updateProp: UpdateProp
}) {
  const { t } = useTranslation()

  // mode=CRUD server-filters so VIEW designers are never fetched regardless of
  // page size; AC-7 is satisfied for any number of designers. Cache 60s.
  const schemasQuery = useQuery({
    queryKey: ['designer', 'list', 1, 100, 'CRUD'],
    queryFn: () => designerApi.listSchemas(1, 100, { mode: 'CRUD' }),
    staleTime: 60_000,
  })

  // The hosting component is intentionally NOT excluded: authors may bind a
  // Repeater's row form to its own component. Server already excluded VIEW designers.
  const schemaOptions = schemasQuery.data?.data ?? []
  const rowDesignerInList = schemaOptions.some((s) => s.designerId === rowDesignerId)

  // A designer can have MULTIPLE Published versions; the author picks which one
  // this Repeater binds to. Shares the ['designer','schema', id] cache key with
  // the library's version flyout.
  const publishedQuery = useQuery({
    queryKey: ['designer', 'schema', rowDesignerId],
    queryFn: () => designerApi.getSchema(rowDesignerId),
    enabled: rowDesignerId !== '',
    staleTime: 60_000,
  })
  const publishedVersions = [...(publishedQuery.data?.versions ?? [])]
    .filter((v) => v.status === 'Published')
    .sort((a, b) => b.version - a.version)
  const rowVersionPublished =
    rowVersion !== undefined && publishedVersions.some((v) => v.version === rowVersion)

  const handleDesignerChange = (next: string) => {
    if (next === SENTINEL_EMPTY) {
      updateProp(id, 'rowDesignerId', '')
      updateProp(id, 'rowVersion', undefined)
      return
    }
    updateProp(id, 'rowDesignerId', next)
    // Switching component invalidates the previously chosen version — the author
    // must pick a published version of the new component.
    updateProp(id, 'rowVersion', undefined)
  }

  const handleVersionChange = (raw: string) => {
    if (raw === SENTINEL_EMPTY) {
      updateProp(id, 'rowVersion', undefined)
      return
    }
    const n = Number(raw)
    if (Number.isFinite(n)) updateProp(id, 'rowVersion', n)
  }

  const schemasDisabled = schemasQuery.isLoading || schemasQuery.isError
  const schemasPlaceholder = schemasQuery.isLoading
    ? t('designer.inspector.placeholders.componentsLoading')
    : schemasQuery.isError
      ? t('designer.inspector.placeholders.componentsLoadError')
      : schemaOptions.length === 0
        ? t('designer.inspector.placeholders.componentsEmpty')
        : t('designer.inspector.placeholders.componentsSelect')

  // "Row form — Version" dropdown state. Disabled until a component is chosen /
  // its versions load; placeholder explains why when empty.
  const versionDisabled =
    rowDesignerId === '' || publishedQuery.isLoading || publishedQuery.isError
  const versionPlaceholder =
    rowDesignerId === ''
      ? t('designer.inspector.placeholders.componentSelectFirst')
      : publishedQuery.isLoading
        ? t('designer.inspector.placeholders.rowVersionResolving')
        : publishedQuery.isError
          ? t('designer.inspector.placeholders.componentsLoadError')
          : publishedVersions.length === 0
            ? t('designer.inspector.placeholders.rowVersionNoPublished')
            : t('designer.inspector.placeholders.rowVersionSelect')

  const versionSelectValue = rowVersion === undefined ? SENTINEL_EMPTY : String(rowVersion)

  // Searchable component picker — keep the "not in current page" orphan row so a
  // saved binding to a component outside the current list stays visible + labelled.
  const componentComboOptions: ComboboxOption[] = [
    ...(!rowDesignerInList && rowDesignerId !== ''
      ? [{ value: rowDesignerId, label: `${rowDesignerId} (not in current page)` }]
      : []),
    ...schemaOptions.map((s) => ({ value: s.designerId, label: s.displayName })),
  ]

  return (
    <>
      <Field label={t('designer.inspector.fields.rowFormComponent')}>
        <Combobox
          value={rowDesignerId}
          onValueChange={handleDesignerChange}
          options={componentComboOptions}
          placeholder={schemasPlaceholder}
          searchPlaceholder={t('designer.inspector.placeholders.componentsSelect')}
          disabled={schemasDisabled}
          loading={schemasQuery.isLoading}
          className={TRIGGER_COMPACT_CLASS}
          aria-label={t('designer.inspector.fields.rowFormComponent')}
        />
        <span className="text-[10px] text-muted-foreground">
          {t('designer.inspector.help.rowFormComponent')}
        </span>
      </Field>
      <Field label={t('designer.inspector.fields.rowFormVersion')}>
        <Select
          value={versionSelectValue}
          onValueChange={handleVersionChange}
          disabled={versionDisabled}
        >
          <SelectTrigger className={TRIGGER_COMPACT_CLASS}>
            <SelectValue placeholder={versionPlaceholder} />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={SENTINEL_EMPTY}>{versionPlaceholder}</SelectItem>
            {/* Keep a saved-but-no-longer-published version visible + flagged. */}
            {rowVersion !== undefined && !rowVersionPublished && (
              <SelectItem value={String(rowVersion)}>
                v{rowVersion} ({t('designer.inspector.placeholders.rowVersionNotPublished')})
              </SelectItem>
            )}
            {publishedVersions.map((v) => (
              <SelectItem key={v.version} value={String(v.version)}>
                v{v.version}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <span className="text-[10px] text-muted-foreground">
          {t('designer.inspector.help.rowFormVersion')}
        </span>
      </Field>
    </>
  )
}

// Designer-backed Dropdown config: which designer + published version supplies
// the options, which fields are the value/label, and the cascading map.
function DropdownDesignerSourceFields({
  id,
  designerId,
  version,
  labelField,
  valueField,
  dependsOn,
  updateProp,
}: {
  id: string
  designerId: string
  version: number | undefined
  labelField: string
  valueField: string
  dependsOn: string
  updateProp: UpdateProp
}) {
  const { t } = useTranslation()
  const currentSchemaId = useDesignerCanvasStore((s) => s.schemaId)

  // mode=CRUD server-filters so VIEW designers are never fetched. AC-7.
  const schemasQuery = useQuery({
    queryKey: ['designer', 'list', 1, 100, 'CRUD'],
    queryFn: () => designerApi.listSchemas(1, 100, { mode: 'CRUD' }),
    staleTime: 60_000,
  })
  // Exclude self; server already excluded VIEW designers.
  const schemaOptions = (schemasQuery.data?.data ?? []).filter(
    (s) => currentSchemaId === null || s.designerId !== currentSchemaId,
  )
  const designerInList = schemaOptions.some((s) => s.designerId === designerId)

  const publishedQuery = useQuery({
    queryKey: ['designer', 'schema', designerId],
    queryFn: () => designerApi.getSchema(designerId),
    enabled: designerId !== '',
    staleTime: 60_000,
  })
  const publishedVersions = [...(publishedQuery.data?.versions ?? [])]
    .filter((v) => v.status === 'Published')
    .sort((a, b) => b.version - a.version)
  const versionPublished =
    version !== undefined && publishedVersions.some((v) => v.version === version)

  const fieldsQuery = useQuery({
    queryKey: ['designer', 'schema', designerId, version],
    queryFn: () => designerApi.getSchema(designerId, version!),
    enabled: designerId !== '' && version !== undefined,
    staleTime: 60_000,
  })
  const fieldKeys = extractRowFormFieldKeys(fieldsQuery.data?.rootElement ?? null)
  const fieldsReady = designerId !== '' && version !== undefined

  const handleDesignerChange = (next: string) => {
    updateProp(id, 'optionsDesignerId', next === SENTINEL_EMPTY ? '' : next)
    // Switching component invalidates version + field selections.
    updateProp(id, 'optionsVersion', undefined)
    updateProp(id, 'labelField', '')
    updateProp(id, 'valueField', '')
  }
  const handleVersionChange = (raw: string) => {
    updateProp(id, 'optionsVersion', raw === SENTINEL_EMPTY ? undefined : Number(raw))
  }

  const schemasDisabled = schemasQuery.isLoading || schemasQuery.isError
  const designerPlaceholder = schemasQuery.isLoading
    ? t('designer.inspector.placeholders.componentsLoading')
    : schemasQuery.isError
      ? t('designer.inspector.placeholders.componentsLoadError')
      : schemaOptions.length === 0
        ? t('designer.inspector.placeholders.componentsEmpty')
        : t('designer.inspector.placeholders.componentsSelect')

  const versionDisabled =
    designerId === '' || publishedQuery.isLoading || publishedQuery.isError
  const versionPlaceholder =
    designerId === ''
      ? t('designer.inspector.placeholders.componentSelectFirst')
      : publishedQuery.isLoading
        ? t('designer.inspector.placeholders.rowVersionResolving')
        : publishedQuery.isError
          ? t('designer.inspector.placeholders.componentsLoadError')
          : publishedVersions.length === 0
            ? t('designer.inspector.placeholders.rowVersionNoPublished')
            : t('designer.inspector.placeholders.rowVersionSelect')

  const fieldPlaceholder = !fieldsReady
    ? t('designer.inspector.placeholders.optionsFieldSourceFirst')
    : fieldsQuery.isLoading
      ? t('designer.inspector.placeholders.componentsLoading')
      : fieldsQuery.isError
        ? t('designer.inspector.placeholders.componentsLoadError')
        : fieldKeys.length === 0
          ? t('designer.inspector.placeholders.optionsFieldsEmpty')
          : t('designer.inspector.placeholders.fieldNameSelect')
  const fieldsDisabled = !fieldsReady || fieldsQuery.isLoading || fieldsQuery.isError

  // Searchable combobox option lists. Each keeps the same "orphan" rows the
  // Select variant surfaced (a saved value no longer in the live list, the
  // record-id value choice), so a stale selection stays visible + labelled.
  const designerComboOptions: ComboboxOption[] = [
    ...(!designerInList && designerId !== ''
      ? [{ value: designerId, label: `${designerId} (not in current page)` }]
      : []),
    ...schemaOptions.map((s) => ({ value: s.designerId, label: s.displayName })),
  ]
  const versionComboOptions: ComboboxOption[] = [
    ...(version !== undefined && !versionPublished
      ? [
          {
            value: String(version),
            label: `v${version} (${t('designer.inspector.placeholders.rowVersionNotPublished')})`,
          },
        ]
      : []),
    ...publishedVersions.map((v) => ({ value: String(v.version), label: `v${v.version}` })),
  ]
  const labelComboOptions: ComboboxOption[] = [
    ...(labelField !== '' && labelField !== 'id' && !fieldKeys.includes(labelField)
      ? [{ value: labelField, label: `${labelField} (not in component)` }]
      : []),
    ...fieldKeys.map((k) => ({ value: k, label: k })),
  ]
  const valueComboOptions: ComboboxOption[] = [
    // The record's primary key is a common value choice for a lookup.
    { value: 'id', label: t('designer.inspector.options.optionsValueId') },
    ...(valueField !== '' && valueField !== 'id' && !fieldKeys.includes(valueField)
      ? [{ value: valueField, label: `${valueField} (not in component)` }]
      : []),
    ...fieldKeys.map((k) => ({ value: k, label: k })),
  ]

  return (
    <>
      <Field label={t('designer.inspector.fields.optionsDesigner')}>
        <Combobox
          value={designerId}
          onValueChange={handleDesignerChange}
          options={designerComboOptions}
          placeholder={designerPlaceholder}
          searchPlaceholder={t('designer.inspector.placeholders.componentsSelect')}
          disabled={schemasDisabled}
          loading={schemasQuery.isLoading}
          className={TRIGGER_COMPACT_CLASS}
          aria-label={t('designer.inspector.fields.optionsDesigner')}
        />
      </Field>

      <Field label={t('designer.inspector.fields.optionsVersion')}>
        <Combobox
          value={version === undefined ? '' : String(version)}
          onValueChange={handleVersionChange}
          options={versionComboOptions}
          placeholder={versionPlaceholder}
          searchPlaceholder={t('designer.inspector.placeholders.rowVersionSelect')}
          disabled={versionDisabled}
          loading={publishedQuery.isLoading}
          className={TRIGGER_COMPACT_CLASS}
          aria-label={t('designer.inspector.fields.optionsVersion')}
        />
      </Field>

      <Field label={t('designer.inspector.fields.dropdownLabelField')}>
        <Combobox
          value={labelField}
          onValueChange={(next) => updateProp(id, 'labelField', next)}
          options={labelComboOptions}
          placeholder={fieldPlaceholder}
          searchPlaceholder={t('designer.inspector.placeholders.fieldNameSelect')}
          disabled={fieldsDisabled}
          loading={fieldsQuery.isLoading}
          className={TRIGGER_COMPACT_CLASS}
          aria-label={t('designer.inspector.fields.dropdownLabelField')}
        />
      </Field>

      <Field label={t('designer.inspector.fields.dropdownValueField')}>
        <Combobox
          value={valueField}
          onValueChange={(next) => updateProp(id, 'valueField', next)}
          options={valueComboOptions}
          placeholder={fieldPlaceholder}
          searchPlaceholder={t('designer.inspector.placeholders.fieldNameSelect')}
          disabled={fieldsDisabled}
          loading={fieldsQuery.isLoading}
          className={TRIGGER_COMPACT_CLASS}
          aria-label={t('designer.inspector.fields.dropdownValueField')}
        />
        <span className="text-[10px] text-muted-foreground">
          {t('designer.inspector.help.dropdownValueField')}
        </span>
      </Field>

      <Field label={t('designer.inspector.fields.dependsOn')}>
        <Input
          value={dependsOn}
          onChange={(e) => updateProp(id, 'dependsOn', e.target.value)}
          placeholder={t('designer.inspector.placeholders.dependsOnMapping')}
          className={INPUT_COMPACT_CLASS}
        />
        <span
          className="text-[10px] text-muted-foreground"
          dangerouslySetInnerHTML={{ __html: t('designer.inspector.help.dropdownDependsOn') }}
        />
      </Field>
    </>
  )
}

// Dataset-backed Dropdown config: which dataset supplies the options, and which of
// its columns are the value/label. Columns come from the dataset's backing VIEW via
// GET /api/datasets/{id}/columns; runtime options are read per-row at data-entry time.
function DropdownDatasetSourceFields({
  id,
  datasetId,
  labelField,
  valueField,
  dependsOn,
  updateProp,
}: {
  id: string
  datasetId: string
  labelField: string
  valueField: string
  dependsOn: string
  updateProp: UpdateProp
}) {
  const { t } = useTranslation()

  // All datasets are listable by any authenticated user (GET /api/datasets is
  // auth-only). 100 is well above any realistic dataset count; no paging UI here.
  const datasetsQuery = useQuery({
    queryKey: ['datasets', 'list', 1, 100],
    queryFn: () => listDatasets(1, 100),
    staleTime: 60_000,
  })
  const datasets = datasetsQuery.data?.data ?? []
  // `datasetId` holds optionsDatasetId, which for a Dropdown is the dataset's VIEW
  // NAME (not its GUID) — so match + fetch columns by name.
  const datasetInList = datasets.some((d) => d.datasetName === datasetId)

  const columnsQuery = useQuery({
    queryKey: ['dataset', 'columns-by-name', datasetId],
    queryFn: () => getDatasetColumnsByName(datasetId),
    enabled: datasetId !== '',
    staleTime: 60_000,
  })
  const columns = columnsQuery.data?.columns ?? []
  const columnsReady = datasetId !== ''

  const handleDatasetChange = (next: string) => {
    updateProp(id, 'optionsDatasetId', next === SENTINEL_EMPTY ? '' : next)
    // Switching dataset invalidates the previously chosen columns.
    updateProp(id, 'labelField', '')
    updateProp(id, 'valueField', '')
  }

  const datasetsDisabled = datasetsQuery.isLoading || datasetsQuery.isError
  const datasetPlaceholder = datasetsQuery.isLoading
    ? t('designer.inspector.placeholders.datasetsLoading')
    : datasetsQuery.isError
      ? t('designer.inspector.placeholders.datasetsLoadError')
      : datasets.length === 0
        ? t('designer.inspector.placeholders.datasetsEmpty')
        : t('designer.inspector.placeholders.datasetsSelect')

  const fieldPlaceholder = !columnsReady
    ? t('designer.inspector.placeholders.datasetFieldsFirst')
    : columnsQuery.isLoading
      ? t('designer.inspector.placeholders.componentsLoading')
      : columnsQuery.isError
        ? t('designer.inspector.placeholders.componentsLoadError')
        : columns.length === 0
          ? t('designer.inspector.placeholders.datasetColumnsEmpty')
          : t('designer.inspector.placeholders.fieldNameSelect')
  const fieldsDisabled = !columnsReady || columnsQuery.isLoading || columnsQuery.isError

  // Searchable combobox option lists. Each keeps an "orphan" row for a saved value
  // no longer present in the live list, so a stale selection stays visible + labelled.
  const datasetComboOptions: ComboboxOption[] = [
    ...(!datasetInList && datasetId !== ''
      ? [{ value: datasetId, label: `${datasetId} (not in list)` }]
      : []),
    // Bind the option VALUE to datasetName (the VIEW name), not the GUID id.
    ...datasets.map((d) => ({ value: d.datasetName, label: d.datasetName })),
  ]
  const labelComboOptions: ComboboxOption[] = [
    ...(labelField !== '' && !columns.includes(labelField)
      ? [{ value: labelField, label: `${labelField} (not in dataset)` }]
      : []),
    ...columns.map((c) => ({ value: c, label: c })),
  ]
  const valueComboOptions: ComboboxOption[] = [
    ...(valueField !== '' && !columns.includes(valueField)
      ? [{ value: valueField, label: `${valueField} (not in dataset)` }]
      : []),
    ...columns.map((c) => ({ value: c, label: c })),
  ]

  return (
    <>
      <Field label={t('designer.inspector.fields.optionsDataset')}>
        <Combobox
          value={datasetId}
          onValueChange={handleDatasetChange}
          options={datasetComboOptions}
          placeholder={datasetPlaceholder}
          searchPlaceholder={t('designer.inspector.placeholders.datasetsSelect')}
          disabled={datasetsDisabled}
          loading={datasetsQuery.isLoading}
          className={TRIGGER_COMPACT_CLASS}
          aria-label={t('designer.inspector.fields.optionsDataset')}
        />
      </Field>

      <Field label={t('designer.inspector.fields.dropdownLabelField')}>
        <Combobox
          value={labelField}
          onValueChange={(next) => updateProp(id, 'labelField', next)}
          options={labelComboOptions}
          placeholder={fieldPlaceholder}
          searchPlaceholder={t('designer.inspector.placeholders.fieldNameSelect')}
          disabled={fieldsDisabled}
          loading={columnsQuery.isLoading}
          className={TRIGGER_COMPACT_CLASS}
          aria-label={t('designer.inspector.fields.dropdownLabelField')}
        />
      </Field>

      <Field label={t('designer.inspector.fields.dropdownValueField')}>
        <Combobox
          value={valueField}
          onValueChange={(next) => updateProp(id, 'valueField', next)}
          options={valueComboOptions}
          placeholder={fieldPlaceholder}
          searchPlaceholder={t('designer.inspector.placeholders.fieldNameSelect')}
          disabled={fieldsDisabled}
          loading={columnsQuery.isLoading}
          className={TRIGGER_COMPACT_CLASS}
          aria-label={t('designer.inspector.fields.dropdownValueField')}
        />
        <span className="text-[10px] text-muted-foreground">
          {t('designer.inspector.help.dropdownValueField')}
        </span>
      </Field>

      <Field label={t('designer.inspector.fields.dependsOn')}>
        <Input
          value={dependsOn}
          onChange={(e) => updateProp(id, 'dependsOn', e.target.value)}
          placeholder={t('designer.inspector.placeholders.dependsOnMapping')}
          className={INPUT_COMPACT_CLASS}
        />
        <span
          className="text-[10px] text-muted-foreground"
          dangerouslySetInnerHTML={{ __html: t('designer.inspector.help.dropdownDependsOn') }}
        />
      </Field>
    </>
  )
}

// Collects the fieldKeys of every input nested anywhere under the DatasetComponent's
// filter sub-form — these are the value sources a filter condition can bind to.
function collectDescendantFieldKeys(children: DesignerElement[] | undefined): string[] {
  const out: string[] = []
  const seen = new Set<string>()
  function walk(el: DesignerElement) {
    const k = getFieldKey(el.properties)
    if (k !== '' && !seen.has(k)) {
      seen.add(k)
      out.push(k)
    }
    for (const c of el.children ?? []) walk(c)
  }
  for (const c of children ?? []) walk(c)
  return out
}

// Filter conditions editor — a flat list of `<column> <operator> <fieldKey>` rows joined by
// one AND/OR, modeled on the Query Builder's Filter Conditions look. The value side binds to
// a filter-input fieldKey (resolved to a literal at runtime), except value-less operators.
function DatasetFilterConditionsField({
  group,
  columns,
  fieldKeys,
  columnsReady,
  columnsLoading,
  onChange,
}: {
  group: DatasetFilterGroup
  columns: string[]
  fieldKeys: string[]
  columnsReady: boolean
  columnsLoading: boolean
  onChange: (next: DatasetFilterGroup) => void
}) {
  const { t } = useTranslation()

  const columnPlaceholder = !columnsReady
    ? t('designer.inspector.placeholders.datasetFieldsFirst')
    : columnsLoading
      ? t('designer.inspector.placeholders.componentsLoading')
      : columns.length === 0
        ? t('designer.inspector.placeholders.datasetColumnsEmpty')
        : t('designer.inspector.placeholders.fieldNameSelect')

  const fieldKeyPlaceholder =
    fieldKeys.length === 0
      ? t('designer.inspector.placeholders.datasetNoFilterInputs')
      : t('designer.inspector.placeholders.datasetSelectFieldKey')

  return (
    <div className="flex flex-col gap-2 rounded-md border border-border bg-muted/40 p-2">
      <div className="flex items-center justify-between">
        <span className="text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">
          {t('designer.inspector.fields.filterConditions')}
        </span>
        <Select
          value={group.combinator}
          onValueChange={(v) => onChange(setCombinator(group, v === 'OR' ? 'OR' : 'AND'))}
        >
          <SelectTrigger className="h-6 w-20 text-xs">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="AND">{t('designer.inspector.options.combinatorAnd')}</SelectItem>
            <SelectItem value="OR">{t('designer.inspector.options.combinatorOr')}</SelectItem>
          </SelectContent>
        </Select>
      </div>

      {group.items.length === 0 && (
        <p className="text-[10px] text-muted-foreground">
          {t('designer.inspector.help.datasetNoConditions')}
        </p>
      )}

      {group.items.map((cond) => {
        const colOptions: ComboboxOption[] = [
          ...(cond.columnName !== '' && !columns.includes(cond.columnName)
            ? [{ value: cond.columnName, label: `${cond.columnName} (not in dataset)` }]
            : []),
          ...columns.map((c) => ({ value: c, label: c })),
        ]
        const fkOptions: ComboboxOption[] = [
          ...(cond.valueFieldKey !== '' && !fieldKeys.includes(cond.valueFieldKey)
            ? [{ value: cond.valueFieldKey, label: `${cond.valueFieldKey} (no input)` }]
            : []),
          ...fieldKeys.map((k) => ({ value: k, label: k })),
        ]
        const noValue = NO_VALUE_OPERATORS.has(cond.operator)
        return (
          <div key={cond.id} className="flex flex-col gap-1 rounded border border-border bg-card p-1.5">
            <Combobox
              value={cond.columnName}
              onValueChange={(v) => onChange(updateCondition(group, cond.id, { columnName: v }))}
              options={colOptions}
              placeholder={columnPlaceholder}
              searchPlaceholder={t('designer.inspector.placeholders.fieldNameSelect')}
              disabled={!columnsReady}
              className={TRIGGER_COMPACT_CLASS}
              aria-label={t('designer.inspector.fields.filterColumn')}
            />
            <div className="flex items-center gap-1">
              <Select
                value={cond.operator}
                onValueChange={(v) =>
                  onChange(updateCondition(group, cond.id, { operator: v as DatasetFilterGroup['items'][number]['operator'] }))
                }
              >
                <SelectTrigger className="h-7 w-28 text-xs">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {FILTER_OPERATORS.map((op) => (
                    <SelectItem key={op} value={op}>
                      {op}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
              <button
                type="button"
                onClick={() => onChange(removeCondition(group, cond.id))}
                className="ml-auto rounded px-1.5 py-1 text-[10px] text-muted-foreground hover:bg-destructive/10 hover:text-destructive"
                aria-label={t('designer.inspector.removeCondition')}
              >
                ✕
              </button>
            </div>
            {!noValue && (
              <Combobox
                value={cond.valueFieldKey}
                onValueChange={(v) => onChange(updateCondition(group, cond.id, { valueFieldKey: v }))}
                options={fkOptions}
                placeholder={fieldKeyPlaceholder}
                searchPlaceholder={t('designer.inspector.placeholders.datasetSelectFieldKey')}
                disabled={fieldKeys.length === 0}
                className={TRIGGER_COMPACT_CLASS}
                aria-label={t('designer.inspector.fields.filterValueField')}
              />
            )}
          </div>
        )
      })}

      <Button
        type="button"
        variant="outline"
        size="sm"
        className="h-7 text-xs"
        onClick={() => onChange(addCondition(group))}
      >
        {t('designer.inspector.addCondition')}
      </Button>
    </div>
  )
}

// Query parameter bindings — for a parameterized "query"-type datasource, each {_placeholder}
// is mapped to a filter input's field key. Like the filter conditions UI but with NO operator
// (a parameter is a value substitution, not a condition); the add button reads "Add Param".
function DatasetParamBindingsField({
  bindings,
  placeholders,
  fieldKeys,
  onChange,
}: {
  bindings: QueryParamBinding[]
  placeholders: string[]
  fieldKeys: string[]
  onChange: (next: QueryParamBinding[]) => void
}) {
  const { t } = useTranslation()

  const missing = unmappedParams(bindings, placeholders)
  const fieldKeyPlaceholder =
    fieldKeys.length === 0
      ? t('designer.inspector.placeholders.datasetNoFilterInputs')
      : t('designer.inspector.placeholders.datasetSelectFieldKey')

  return (
    <div className="flex flex-col gap-2 rounded-md border border-border bg-muted/40 p-2">
      <span className="text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">
        {t('designer.inspector.fields.queryParameters')}
      </span>

      {bindings.length === 0 && (
        <p className="text-[10px] text-muted-foreground">
          {t('designer.inspector.help.datasetNoParams')}
        </p>
      )}

      {bindings.map((binding) => {
        const paramOptions: ComboboxOption[] = [
          ...(binding.param !== '' && !placeholders.includes(binding.param)
            ? [{ value: binding.param, label: `${binding.param} (not in query)` }]
            : []),
          ...placeholders.map((ph) => ({ value: ph, label: ph })),
        ]
        const fkOptions: ComboboxOption[] = [
          ...(binding.valueFieldKey !== '' && !fieldKeys.includes(binding.valueFieldKey)
            ? [{ value: binding.valueFieldKey, label: `${binding.valueFieldKey} (no input)` }]
            : []),
          ...fieldKeys.map((k) => ({ value: k, label: k })),
        ]
        return (
          <div key={binding.id} className="flex flex-col gap-1 rounded border border-border bg-card p-1.5">
            <div className="flex items-center gap-1">
              <Combobox
                value={binding.param}
                onValueChange={(v) => onChange(updateQueryParamBinding(bindings, binding.id, { param: v }))}
                options={paramOptions}
                placeholder={t('designer.inspector.placeholders.datasetSelectParam')}
                searchPlaceholder={t('designer.inspector.placeholders.datasetSelectParam')}
                className={TRIGGER_COMPACT_CLASS}
                aria-label={t('designer.inspector.fields.queryParameterName')}
              />
              <button
                type="button"
                onClick={() => onChange(removeQueryParamBinding(bindings, binding.id))}
                className="ml-auto rounded px-1.5 py-1 text-[10px] text-muted-foreground hover:bg-destructive/10 hover:text-destructive"
                aria-label={t('designer.inspector.removeParam')}
              >
                ✕
              </button>
            </div>
            <Combobox
              value={binding.valueFieldKey}
              onValueChange={(v) => onChange(updateQueryParamBinding(bindings, binding.id, { valueFieldKey: v }))}
              options={fkOptions}
              placeholder={fieldKeyPlaceholder}
              searchPlaceholder={t('designer.inspector.placeholders.datasetSelectFieldKey')}
              disabled={fieldKeys.length === 0}
              className={TRIGGER_COMPACT_CLASS}
              aria-label={t('designer.inspector.fields.filterValueField')}
            />
          </div>
        )
      })}

      {missing.length > 0 && (
        <p className="text-[10px] text-muted-foreground">
          {t('designer.inspector.help.datasetUnmappedParameters', { params: missing.join(', ') })}
        </p>
      )}

      <Button
        type="button"
        variant="outline"
        size="sm"
        className="h-7 text-xs"
        // Default the new row to the first still-unmapped placeholder for convenience.
        onClick={() => onChange(addQueryParamBinding(bindings, missing[0] ?? ''))}
      >
        {t('designer.inspector.addParam')}
      </Button>
    </div>
  )
}

// "Set columns" — a dialog listing the dataset columns; each a visibility checkbox plus a
// header-text override. Writes the tableColumns config live as the author edits.
function DatasetSetColumnsField({
  columns,
  value,
  columnsReady,
  onChange,
}: {
  columns: string[]
  value: TableColumnConfig[]
  columnsReady: boolean
  onChange: (next: TableColumnConfig[]) => void
}) {
  const { t } = useTranslation()
  const byColumn = new Map(value.map((c) => [c.column, c]))

  function upsert(column: string, patch: Partial<TableColumnConfig>) {
    const existing = byColumn.get(column) ?? { column, visible: true, header: '' }
    const merged = { ...existing, ...patch }
    const next = columns.map((c) => (byColumn.get(c) ?? { column: c, visible: true, header: '' }))
    onChange(next.map((c) => (c.column === column ? merged : c)))
  }

  return (
    <Dialog>
      <DialogTrigger asChild>
        <Button type="button" variant="outline" size="sm" className="h-7 text-xs" disabled={!columnsReady}>
          {t('designer.inspector.setColumns')}
        </Button>
      </DialogTrigger>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>{t('designer.inspector.setColumnsTitle')}</DialogTitle>
        </DialogHeader>
        {columns.length === 0 ? (
          <p className="text-sm text-muted-foreground">
            {t('designer.inspector.placeholders.datasetColumnsEmpty')}
          </p>
        ) : (
          <div className="flex max-h-[50vh] flex-col gap-2 overflow-y-auto">
            {columns.map((col) => {
              const cfg = byColumn.get(col)
              const visible = cfg?.visible !== false
              return (
                <div key={col} className="flex items-center gap-2">
                  <Checkbox
                    checked={visible}
                    onCheckedChange={(v) => upsert(col, { visible: v === true })}
                    aria-label={t('designer.inspector.columnVisibleAria', { column: col })}
                  />
                  <span className="w-32 shrink-0 truncate font-mono text-xs text-foreground">{col}</span>
                  <Input
                    value={cfg?.header ?? ''}
                    onChange={(e) => upsert(col, { header: e.target.value })}
                    placeholder={col}
                    className={INPUT_COMPACT_CLASS}
                  />
                </div>
              )
            })}
          </div>
        )}
        <DialogFooter>
          <span className="text-[10px] text-muted-foreground">
            {t('designer.inspector.setColumnsHint')}
          </span>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

// Inspector fields for the SingleRecord component: data source (CRUD designer, required),
// version (required), and auth filter column (required — the column that scopes the record
// to the current user). The chosen designer/version's form renders at runtime; here we only
// configure which designer, which version, and which column owns the per-user record.
function SingleRecordComponentFields({
  element,
  updateProp,
}: {
  element: DesignerElement
  updateProp: UpdateProp
}) {
  const { t } = useTranslation()
  const id = element.id
  const p = element.properties
  const designerId = typeof p.optionsDesignerId === 'string' ? p.optionsDesignerId : ''
  const version = typeof p.optionsVersion === 'number' ? p.optionsVersion : undefined
  const authFilterColumn = typeof p.authFilterColumn === 'string' ? p.authFilterColumn : ''
  const currentSchemaId = useDesignerCanvasStore((s) => s.schemaId)

  // Only CRUD designers can back a SingleRecord — VIEW designers have no table.
  const schemasQuery = useQuery({
    queryKey: ['designer', 'list', 1, 100, 'CRUD'],
    queryFn: () => designerApi.listSchemas(1, 100, { mode: 'CRUD' }),
    staleTime: 60_000,
  })
  // Exclude self; server already excluded VIEW designers.
  const schemaOptions = (schemasQuery.data?.data ?? []).filter(
    (s) => currentSchemaId === null || s.designerId !== currentSchemaId,
  )
  const designerInList = schemaOptions.some((s) => s.designerId === designerId)

  const publishedQuery = useQuery({
    queryKey: ['designer', 'schema', designerId],
    queryFn: () => designerApi.getSchema(designerId),
    enabled: designerId !== '',
    staleTime: 60_000,
  })
  const publishedVersions = [...(publishedQuery.data?.versions ?? [])]
    .filter((v) => v.status === 'Published')
    .sort((a, b) => b.version - a.version)
  const versionPublished =
    version !== undefined && publishedVersions.some((v) => v.version === version)

  const fieldsQuery = useQuery({
    queryKey: ['designer', 'schema', designerId, version],
    queryFn: () => designerApi.getSchema(designerId, version!),
    enabled: designerId !== '' && version !== undefined,
    staleTime: 60_000,
  })
  const fieldKeys = extractRowFormFieldKeys(fieldsQuery.data?.rootElement ?? null)
  const fieldsReady = designerId !== '' && version !== undefined

  const handleDesignerChange = (next: string) => {
    updateProp(id, 'optionsDesignerId', next === SENTINEL_EMPTY ? '' : next)
    // Switching designer invalidates version + auth column selections.
    updateProp(id, 'optionsVersion', undefined)
    updateProp(id, 'authFilterColumn', '')
  }
  const handleVersionChange = (raw: string) => {
    updateProp(id, 'optionsVersion', raw === SENTINEL_EMPTY ? undefined : Number(raw))
  }

  const schemasDisabled = schemasQuery.isLoading || schemasQuery.isError
  const designerPlaceholder = schemasQuery.isLoading
    ? t('designer.inspector.placeholders.componentsLoading')
    : schemasQuery.isError
      ? t('designer.inspector.placeholders.componentsLoadError')
      : schemaOptions.length === 0
        ? t('designer.inspector.placeholders.componentsEmpty')
        : t('designer.inspector.placeholders.componentsSelect')

  const versionDisabled =
    designerId === '' || publishedQuery.isLoading || publishedQuery.isError
  const versionPlaceholder =
    designerId === ''
      ? t('designer.inspector.placeholders.componentSelectFirst')
      : publishedQuery.isLoading
        ? t('designer.inspector.placeholders.rowVersionResolving')
        : publishedQuery.isError
          ? t('designer.inspector.placeholders.componentsLoadError')
          : publishedVersions.length === 0
            ? t('designer.inspector.placeholders.rowVersionNoPublished')
            : t('designer.inspector.placeholders.rowVersionSelect')

  const fieldPlaceholder = !fieldsReady
    ? t('designer.inspector.placeholders.optionsFieldSourceFirst')
    : fieldsQuery.isLoading
      ? t('designer.inspector.placeholders.componentsLoading')
      : fieldsQuery.isError
        ? t('designer.inspector.placeholders.componentsLoadError')
        : fieldKeys.length === 0
          ? t('designer.inspector.placeholders.optionsFieldsEmpty')
          : t('designer.inspector.placeholders.fieldNameSelect')
  const fieldsDisabled = !fieldsReady || fieldsQuery.isLoading || fieldsQuery.isError

  const designerComboOptions: ComboboxOption[] = [
    ...(!designerInList && designerId !== ''
      ? [{ value: designerId, label: `${designerId} (not in current page)` }]
      : []),
    ...schemaOptions.map((s) => ({ value: s.designerId, label: s.displayName })),
  ]
  const versionComboOptions: ComboboxOption[] = [
    ...(version !== undefined && !versionPublished
      ? [
          {
            value: String(version),
            label: `v${version} (${t('designer.inspector.placeholders.rowVersionNotPublished')})`,
          },
        ]
      : []),
    ...publishedVersions.map((v) => ({ value: String(v.version), label: `v${v.version}` })),
  ]
  const columnComboOptions: ComboboxOption[] = [
    ...(authFilterColumn !== '' && !fieldKeys.includes(authFilterColumn)
      ? [{ value: authFilterColumn, label: `${authFilterColumn} (not in component)` }]
      : []),
    ...fieldKeys.map((k) => ({ value: k, label: k })),
  ]

  return (
    <>
      <Field label={`${t('designer.inspector.fields.selectedDatasource')} *`}>
        <Combobox
          value={designerId}
          onValueChange={handleDesignerChange}
          options={designerComboOptions}
          placeholder={designerPlaceholder}
          searchPlaceholder={t('designer.inspector.placeholders.componentsSelect')}
          disabled={schemasDisabled}
          loading={schemasQuery.isLoading}
          className={cn(TRIGGER_COMPACT_CLASS, designerId === '' && 'border-destructive')}
          aria-label={t('designer.inspector.fields.selectedDatasource')}
        />
        {designerId === '' && (
          <span className="text-[10px] text-destructive">{t('errors.required')}</span>
        )}
        <span className="text-[10px] text-muted-foreground">
          {t('designer.inspector.help.singleRecordSource')}
        </span>
      </Field>

      <Field label={`${t('designer.inspector.fields.optionsVersion')} *`}>
        <Combobox
          value={version === undefined ? '' : String(version)}
          onValueChange={handleVersionChange}
          options={versionComboOptions}
          placeholder={versionPlaceholder}
          searchPlaceholder={t('designer.inspector.placeholders.rowVersionSelect')}
          disabled={versionDisabled}
          loading={publishedQuery.isLoading}
          className={cn(TRIGGER_COMPACT_CLASS, version === undefined && 'border-destructive')}
          aria-label={t('designer.inspector.fields.optionsVersion')}
        />
        {version === undefined && (
          <span className="text-[10px] text-destructive">{t('errors.required')}</span>
        )}
      </Field>

      <Field label={`${t('designer.inspector.fields.authFilterColumn')} *`}>
        <Combobox
          value={authFilterColumn}
          onValueChange={(next) => updateProp(id, 'authFilterColumn', next)}
          options={columnComboOptions}
          placeholder={fieldPlaceholder}
          searchPlaceholder={t('designer.inspector.placeholders.fieldNameSelect')}
          disabled={fieldsDisabled}
          loading={fieldsQuery.isLoading}
          className={cn(TRIGGER_COMPACT_CLASS, authFilterColumn === '' && 'border-destructive')}
          aria-label={t('designer.inspector.fields.authFilterColumn')}
        />
        {authFilterColumn === '' && (
          <span className="text-[10px] text-destructive">{t('errors.required')}</span>
        )}
        <span className="text-[10px] text-muted-foreground">
          {t('designer.inspector.help.singleRecordAuthFilterColumn')}
        </span>
      </Field>
    </>
  )
}

// Inspector fields for the DatasetComponent: data source (required), filterable + filter
// conditions, table view + Set columns, and chart views (bar/line/pie/donut) + their shared
// category / value / aggregation config.
function DatasetComponentFields({
  element,
  updateProp,
}: {
  element: DesignerElement
  updateProp: UpdateProp
}) {
  const { t } = useTranslation()
  const id = element.id
  const p = element.properties
  const datasetId = typeof p.optionsDatasetId === 'string' ? p.optionsDatasetId : ''
  const filterable = p.filterable === true
  const tableView = p.tableView === true
  const barChart = p.barChart === true
  const lineChart = p.lineChart === true
  const pieChart = p.pieChart === true
  const donutChart = p.donutChart === true
  const anyChart = barChart || lineChart || pieChart || donutChart
  const chartAggregate = typeof p.chartAggregate === 'string' ? p.chartAggregate : 'count'
  const chartCategory = typeof p.chartCategory === 'string' ? p.chartCategory : ''
  const chartValue = typeof p.chartValue === 'string' ? p.chartValue : ''
  const authFilterColumn = typeof p.authFilterColumn === 'string' ? p.authFilterColumn : ''

  const datasetsQuery = useQuery({
    queryKey: ['datasets', 'list', 1, 100],
    queryFn: () => listDatasets(1, 100),
    staleTime: 60_000,
  })
  const datasets = datasetsQuery.data?.data ?? []
  const datasetInList = datasets.some((d) => d.id === datasetId)

  const columnsQuery = useQuery({
    queryKey: ['dataset', 'columns', datasetId],
    queryFn: () => getDatasetColumns(datasetId),
    enabled: datasetId !== '',
    staleTime: 60_000,
  })
  const columns = columnsQuery.data?.columns ?? []
  const columnsReady = datasetId !== ''

  // Parameterized-query feature — load the dataset's query type + SQL so the inspector can
  // detect a parameterized "query" datasource and require its placeholders be mapped.
  const datasetDetailQuery = useQuery({
    queryKey: ['dataset', 'detail', datasetId],
    queryFn: () => getDataset(datasetId),
    enabled: datasetId !== '',
    staleTime: 60_000,
  })
  const placeholders = useMemo(
    () => extractPlaceholders(datasetDetailQuery.data?.query),
    [datasetDetailQuery.data?.query],
  )
  const isParameterized =
    datasetDetailQuery.data?.queryType === 'query' && placeholders.length > 0

  const fieldKeys = collectDescendantFieldKeys(element.children)
  const filterGroup = parseDatasetFilter(p.filterConditions)
  const paramBindings = parseQueryParamBindings(p.queryParamBindings)
  const tableColumns = parseTableColumns(p.tableColumns)

  // Placeholders not yet bound to a filter input. Until this is empty the parameterized
  // datasource isn't fully wired (the designer save is blocked too — see designer page).
  const unmappedPlaceholders = isParameterized
    ? unmappedParams(paramBindings, placeholders)
    : []

  const datasetsDisabled = datasetsQuery.isLoading || datasetsQuery.isError
  const datasetPlaceholder = datasetsQuery.isLoading
    ? t('designer.inspector.placeholders.datasetsLoading')
    : datasetsQuery.isError
      ? t('designer.inspector.placeholders.datasetsLoadError')
      : datasets.length === 0
        ? t('designer.inspector.placeholders.datasetsEmpty')
        : t('designer.inspector.placeholders.datasetsSelect')

  const datasetOptions: ComboboxOption[] = [
    ...(!datasetInList && datasetId !== ''
      ? [{ value: datasetId, label: `${datasetId} (not in list)` }]
      : []),
    ...datasets.map((d) => ({ value: d.id, label: d.datasetName })),
  ]

  const columnPlaceholder = !columnsReady
    ? t('designer.inspector.placeholders.datasetFieldsFirst')
    : columnsQuery.isLoading
      ? t('designer.inspector.placeholders.componentsLoading')
      : columns.length === 0
        ? t('designer.inspector.placeholders.datasetColumnsEmpty')
        : t('designer.inspector.placeholders.fieldNameSelect')
  // A column combobox option list that keeps a stale (no-longer-present) selection visible.
  const columnOptions = (selected: string): ComboboxOption[] => [
    ...(selected !== '' && !columns.includes(selected)
      ? [{ value: selected, label: `${selected} (not in dataset)` }]
      : []),
    ...columns.map((c) => ({ value: c, label: c })),
  ]
  const aggregateNeedsValue = chartAggregate !== 'count'

  return (
    <>
      <Field label={`${t('designer.inspector.fields.selectedDatasource')} *`}>
        <Combobox
          value={datasetId}
          onValueChange={(v) => updateProp(id, 'optionsDatasetId', v)}
          options={datasetOptions}
          placeholder={datasetPlaceholder}
          searchPlaceholder={t('designer.inspector.placeholders.datasetsSelect')}
          disabled={datasetsDisabled}
          loading={datasetsQuery.isLoading}
          className={cn(TRIGGER_COMPACT_CLASS, datasetId === '' && 'border-destructive')}
          aria-label={t('designer.inspector.fields.selectedDatasource')}
        />
        {datasetId === '' && (
          <span className="text-[10px] text-destructive">{t('errors.required')}</span>
        )}
      </Field>

      <Field label={t('designer.inspector.fields.authFilterColumn')}>
        <Combobox
          value={authFilterColumn}
          onValueChange={(v) => updateProp(id, 'authFilterColumn', v)}
          options={[
            { value: '', label: t('designer.inspector.options.authFilterNone') },
            ...columnOptions(authFilterColumn),
          ]}
          placeholder={t('designer.inspector.options.authFilterNone')}
          searchPlaceholder={t('designer.inspector.placeholders.fieldNameSelect')}
          disabled={!columnsReady}
          className={TRIGGER_COMPACT_CLASS}
          aria-label={t('designer.inspector.fields.authFilterColumn')}
        />
        <span className="text-[10px] text-muted-foreground">
          {t('designer.inspector.help.authFilterColumn')}
        </span>
      </Field>

      {/* Query parameters — a parameterized "query" datasource binds each {_placeholder}
          to a filter input. Separate from the filter conditions below (which add extra
          WHERE filters on the dataset's output columns). */}
      {isParameterized && (
        <>
          <div className="rounded-md border border-amber-500/30 bg-amber-500/10 px-2 py-1.5 text-[10px] text-amber-700 dark:text-amber-300">
            {t('designer.inspector.help.datasetParameterized')}
            {unmappedPlaceholders.length > 0 && (
              <span className="mt-1 block font-medium text-destructive">
                {t('designer.inspector.help.datasetUnmappedParameters', {
                  params: unmappedPlaceholders.join(', '),
                })}
              </span>
            )}
          </div>
          <DatasetParamBindingsField
            bindings={paramBindings}
            placeholders={placeholders}
            fieldKeys={fieldKeys}
            onChange={(next) => updateProp(id, 'queryParamBindings', next)}
          />
        </>
      )}

      <BoolField
        label={t('designer.inspector.fields.filterable')}
        checked={filterable}
        onChange={(v) => updateProp(id, 'filterable', v)}
      />
      {filterable && (
        <>
          <span className="text-[10px] text-muted-foreground">
            {t('designer.inspector.help.datasetFilterable')}
          </span>
          <DatasetFilterConditionsField
            group={filterGroup}
            columns={columns}
            fieldKeys={fieldKeys}
            columnsReady={columnsReady}
            columnsLoading={columnsQuery.isLoading}
            onChange={(next) => updateProp(id, 'filterConditions', next)}
          />
        </>
      )}

      <BoolField
        label={t('designer.inspector.fields.tableView')}
        checked={tableView}
        onChange={(v) => updateProp(id, 'tableView', v)}
      />
      {tableView && (
        <DatasetSetColumnsField
          columns={columns}
          value={tableColumns}
          columnsReady={columnsReady}
          onChange={(next) => updateProp(id, 'tableColumns', next)}
        />
      )}

      <div className="mt-2 flex flex-col gap-2 border-t border-border pt-3">
        <p className="text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">
          {t('designer.inspector.chartsHeader')}
        </p>
        <BoolField label={t('designer.inspector.fields.barChart')} checked={barChart}
          onChange={(v) => updateProp(id, 'barChart', v)} />
        <BoolField label={t('designer.inspector.fields.lineChart')} checked={lineChart}
          onChange={(v) => updateProp(id, 'lineChart', v)} />
        <BoolField label={t('designer.inspector.fields.pieChart')} checked={pieChart}
          onChange={(v) => updateProp(id, 'pieChart', v)} />
        <BoolField label={t('designer.inspector.fields.donutChart')} checked={donutChart}
          onChange={(v) => updateProp(id, 'donutChart', v)} />

        {anyChart && (
          <>
            <Field label={t('designer.inspector.fields.chartAggregate')}>
              <Select value={chartAggregate} onValueChange={(v) => updateProp(id, 'chartAggregate', v)}>
                <SelectTrigger className={TRIGGER_COMPACT_CLASS}>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="count">{t('designer.inspector.options.aggregateCount')}</SelectItem>
                  <SelectItem value="sum">{t('designer.inspector.options.aggregateSum')}</SelectItem>
                  <SelectItem value="avg">{t('designer.inspector.options.aggregateAvg')}</SelectItem>
                  <SelectItem value="min">{t('designer.inspector.options.aggregateMin')}</SelectItem>
                  <SelectItem value="max">{t('designer.inspector.options.aggregateMax')}</SelectItem>
                </SelectContent>
              </Select>
            </Field>
            <Field label={t('designer.inspector.fields.chartCategory')}>
              <Combobox
                value={chartCategory}
                onValueChange={(v) => updateProp(id, 'chartCategory', v)}
                options={columnOptions(chartCategory)}
                placeholder={columnPlaceholder}
                searchPlaceholder={t('designer.inspector.placeholders.fieldNameSelect')}
                disabled={!columnsReady}
                className={TRIGGER_COMPACT_CLASS}
                aria-label={t('designer.inspector.fields.chartCategory')}
              />
            </Field>
            {aggregateNeedsValue && (
              <Field label={t('designer.inspector.fields.chartValue')}>
                <Combobox
                  value={chartValue}
                  onValueChange={(v) => updateProp(id, 'chartValue', v)}
                  options={columnOptions(chartValue)}
                  placeholder={columnPlaceholder}
                  searchPlaceholder={t('designer.inspector.placeholders.fieldNameSelect')}
                  disabled={!columnsReady}
                  className={TRIGGER_COMPACT_CLASS}
                  aria-label={t('designer.inspector.fields.chartValue')}
                />
                <span className="text-[10px] text-muted-foreground">
                  {t('designer.inspector.help.chartValue')}
                </span>
              </Field>
            )}
          </>
        )}
      </div>
    </>
  )
}

function PropertyFields({
  element,
  repeaterAncestor,
  localFieldKeys,
  formDatasetId,
  updateProp,
}: {
  element: DesignerElement
  // The nearest Repeater ancestor of `element` (or null when none). Only the
  // 'Repeater Field' branch reads it today, to decide whether the 'Header
  // name' field should surface.
  repeaterAncestor: DesignerElement | null
  // The current canvas's own scalar fieldKeys — supplied for a top-level Field
  // (no Repeater/TreeView ancestor) so its "Field name" picker has options.
  localFieldKeys?: string[]
  // The component's per-version dataset binding (CustomDataset id) or null. When
  // set, a top-level Field's "Field name" options come from the dataset's columns
  // instead of localFieldKeys.
  formDatasetId?: string | null
  updateProp: UpdateProp
}) {
  const { t } = useTranslation()
  const id = element.id
  const p = element.properties

  function str(key: string, fallback = ''): string {
    return p[key] !== undefined && p[key] !== null ? String(p[key]) : fallback
  }
  function num(key: string, fallback: number): number {
    if (p[key] === undefined) return fallback
    const v = Number(p[key])
    return isNaN(v) ? fallback : v
  }
  function optNum(key: string): number | undefined {
    if (p[key] === undefined) return undefined
    const v = Number(p[key])
    return isNaN(v) ? undefined : v
  }
  function bool(key: string): boolean {
    return !!p[key]
  }

  switch (element.type) {
    case 'Label':
      return (
        <>
          <Field label={t('designer.inspector.fields.text')}>
            <Input
              value={str('text')}
              onChange={(e) => updateProp(id, 'text', e.target.value)}
              className={INPUT_COMPACT_CLASS}
            />
          </Field>
          <Field label={t('designer.inspector.fields.fontSize')}>
            <NumberInput
              value={num('fontSize', 14)}
              onChange={(v) => updateProp(id, 'fontSize', v)}
            />
          </Field>
          <Field label={t('designer.inspector.fields.fontWeight')}>
            <Select
              value={str('fontWeight', 'normal')}
              onValueChange={(v) => updateProp(id, 'fontWeight', v)}
            >
              <SelectTrigger className={TRIGGER_COMPACT_CLASS}>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="normal">{t('designer.inspector.options.fontWeightNormal')}</SelectItem>
                <SelectItem value="bold">{t('designer.inspector.options.fontWeightBold')}</SelectItem>
              </SelectContent>
            </Select>
          </Field>
          <StyleClassesField id={id} value={str('styleClasses')} updateProp={updateProp} />
        </>
      )
    case 'Button':
      return (
        <>
          <Field label={t('designer.inspector.fields.label')}>
            <Input
              value={str('label')}
              onChange={(e) => updateProp(id, 'label', e.target.value)}
              className={INPUT_COMPACT_CLASS}
            />
          </Field>
          <Field label={t('designer.inspector.fields.variant')}>
            <Select
              value={str('variant', 'primary')}
              onValueChange={(v) => updateProp(id, 'variant', v)}
            >
              <SelectTrigger className={TRIGGER_COMPACT_CLASS}>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="primary">{t('designer.inspector.options.variantPrimary')}</SelectItem>
                <SelectItem value="secondary">{t('designer.inspector.options.variantSecondary')}</SelectItem>
                <SelectItem value="danger">{t('designer.inspector.options.variantDanger')}</SelectItem>
              </SelectContent>
            </Select>
          </Field>
          <BoolField
            label={t('designer.inspector.fields.disabled')}
            checked={bool('disabled')}
            onChange={(v) => updateProp(id, 'disabled', v)}
          />
          <StyleClassesField id={id} value={str('styleClasses')} updateProp={updateProp} />
        </>
      )
    case 'Text Input':
      return (
        <>
          <Field label={t('designer.inspector.fields.label')}>
            <Input
              value={str('label')}
              onChange={(e) => updateProp(id, 'label', e.target.value)}
              className={INPUT_COMPACT_CLASS}
            />
          </Field>
          <LabelModeField id={id} value={str('labelMode', 'vertical')} updateProp={updateProp} />
          <FieldKeyField id={id} value={str('fieldKey')} updateProp={updateProp} />
          <PgTypeField id={id} value={str('pgType')} updateProp={updateProp} />
          <Field label={t('designer.inspector.fields.inputType')}>
            <Select
              value={resolveTextInputType(p)}
              onValueChange={(v) => updateProp(id, 'inputType', v)}
            >
              <SelectTrigger className={TRIGGER_COMPACT_CLASS}>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {ALLOWED_TEXT_INPUT_TYPES.map((typeKey) => (
                  <SelectItem key={typeKey} value={typeKey}>
                    {TEXT_INPUT_TYPE_LABELS[typeKey]}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>
          <Field label={t('designer.inspector.fields.placeholder')}>
            <Input
              value={str('placeholder')}
              onChange={(e) => updateProp(id, 'placeholder', e.target.value)}
              className={INPUT_COMPACT_CLASS}
            />
          </Field>
          <Field label={t('designer.inspector.fields.maxLength')}>
            <OptionalNumberInput
              value={optNum('maxLength')}
              onChange={(v) => updateProp(id, 'maxLength', v)}
              onClear={() => updateProp(id, 'maxLength', undefined)}
            />
          </Field>
          <Field label={t('designer.inspector.fields.regexPattern')}>
            <Input
              value={str('regexPattern')}
              onChange={(e) => updateProp(id, 'regexPattern', e.target.value)}
              className={INPUT_COMPACT_CLASS}
              placeholder={t('designer.inspector.fields.regexPatternPlaceholder')}
            />
          </Field>
          <Field label={t('designer.inspector.fields.regexMessage')}>
            <Input
              value={str('regexMessage')}
              onChange={(e) => updateProp(id, 'regexMessage', e.target.value)}
              className={INPUT_COMPACT_CLASS}
            />
          </Field>
          <BoolField
            label={t('designer.inspector.fields.required')}
            checked={bool('required')}
            onChange={(v) => updateProp(id, 'required', v)}
          />
          <TableColumnFields element={element} updateProp={updateProp} />
          <StyleClassesField id={id} value={str('styleClasses')} updateProp={updateProp} />
        </>
      )
    case 'TextArea':
      return (
        <>
          <Field label={t('designer.inspector.fields.label')}>
            <Input
              value={str('label')}
              onChange={(e) => updateProp(id, 'label', e.target.value)}
              className={INPUT_COMPACT_CLASS}
            />
          </Field>
          <LabelModeField id={id} value={str('labelMode', 'vertical')} updateProp={updateProp} />
          <FieldKeyField id={id} value={str('fieldKey')} updateProp={updateProp} />
          <PgTypeField id={id} value={str('pgType')} updateProp={updateProp} />
          <Field label={t('designer.inspector.fields.placeholder')}>
            <Input
              value={str('placeholder')}
              onChange={(e) => updateProp(id, 'placeholder', e.target.value)}
              className={INPUT_COMPACT_CLASS}
            />
          </Field>
          <Field label={t('designer.inspector.fields.rows')}>
            <NumberInput
              value={num('rows', 4)}
              onChange={(v) => updateProp(id, 'rows', v)}
            />
          </Field>
          <Field label={t('designer.inspector.fields.maxLength')}>
            <OptionalNumberInput
              value={optNum('maxLength')}
              onChange={(v) => updateProp(id, 'maxLength', v)}
              onClear={() => updateProp(id, 'maxLength', undefined)}
            />
          </Field>
          <BoolField
            label={t('designer.inspector.fields.required')}
            checked={bool('required')}
            onChange={(v) => updateProp(id, 'required', v)}
          />
          <TableColumnFields element={element} updateProp={updateProp} />
          <StyleClassesField id={id} value={str('styleClasses')} updateProp={updateProp} />
        </>
      )
    case 'Number Input':
      return (
        <>
          <Field label={t('designer.inspector.fields.label')}>
            <Input
              value={str('label')}
              onChange={(e) => updateProp(id, 'label', e.target.value)}
              className={INPUT_COMPACT_CLASS}
            />
          </Field>
          <LabelModeField id={id} value={str('labelMode', 'vertical')} updateProp={updateProp} />
          <FieldKeyField id={id} value={str('fieldKey')} updateProp={updateProp} />
          <PgTypeField id={id} value={str('pgType')} updateProp={updateProp} />
          <Field label={t('designer.inspector.fields.unit')}>
            <Input
              value={str('unit')}
              onChange={(e) => updateProp(id, 'unit', e.target.value)}
              className={INPUT_COMPACT_CLASS}
            />
          </Field>
          <Field label={t('designer.inspector.fields.min')}>
            <OptionalNumberInput
              value={optNum('min')}
              onChange={(v) => updateProp(id, 'min', v)}
              onClear={() => updateProp(id, 'min', undefined)}
            />
          </Field>
          <Field label={t('designer.inspector.fields.max')}>
            <OptionalNumberInput
              value={optNum('max')}
              onChange={(v) => updateProp(id, 'max', v)}
              onClear={() => updateProp(id, 'max', undefined)}
            />
          </Field>
          <BoolField
            label={t('designer.inspector.fields.required')}
            checked={bool('required')}
            onChange={(v) => updateProp(id, 'required', v)}
          />
          <TableColumnFields element={element} updateProp={updateProp} />
          <StyleClassesField id={id} value={str('styleClasses')} updateProp={updateProp} />
        </>
      )
    case 'Checkbox':
      // Checkbox intentionally omits labelMode — its label always renders inline.
      return (
        <>
          <Field label={t('designer.inspector.fields.label')}>
            <Input
              value={str('label')}
              onChange={(e) => updateProp(id, 'label', e.target.value)}
              className={INPUT_COMPACT_CLASS}
            />
          </Field>
          <FieldKeyField id={id} value={str('fieldKey')} updateProp={updateProp} />
          <PgTypeField id={id} value={str('pgType')} updateProp={updateProp} />
          <BoolField
            label={t('designer.inspector.fields.defaultChecked')}
            checked={bool('defaultChecked')}
            onChange={(v) => updateProp(id, 'defaultChecked', v)}
          />
          <TableColumnFields element={element} updateProp={updateProp} />
          <StyleClassesField id={id} value={str('styleClasses')} updateProp={updateProp} />
        </>
      )
    case 'Dropdown': {
      // optionsSource: 'static' (comma list), 'designer' (lookup from another
      // designer's table), 'dataset' (lookup from a Dataset Manager dataset's VIEW),
      // or 'api' (options fetched at runtime from a relative platform endpoint).
      // Anything else falls back to 'static'.
      const rawOptionsSource = str('optionsSource', 'static')
      const optionsSource =
        rawOptionsSource === 'designer' ||
        rawOptionsSource === 'dataset' ||
        rawOptionsSource === 'api'
          ? rawOptionsSource
          : 'static'
      return (
        <>
          <Field label={t('designer.inspector.fields.label')}>
            <Input
              value={str('label')}
              onChange={(e) => updateProp(id, 'label', e.target.value)}
              className={INPUT_COMPACT_CLASS}
            />
          </Field>
          <LabelModeField id={id} value={str('labelMode', 'vertical')} updateProp={updateProp} />
          <FieldKeyField id={id} value={str('fieldKey')} updateProp={updateProp} />
          <PgTypeField id={id} value={str('pgType')} updateProp={updateProp} />
          <Field label={t('designer.inspector.fields.optionsSource')}>
            <Select
              value={optionsSource}
              onValueChange={(next) => {
                updateProp(id, 'optionsSource', next)
                // A Designer-source dropdown stores the referenced row's uuid; force
                // the type to uuid, and reset a previously-uuid type back to text
                // when switching back to a static option list.
                if (next === 'designer') updateProp(id, 'pgType', 'uuid')
                else if (str('pgType') === 'uuid') updateProp(id, 'pgType', 'text')
              }}
            >
              <SelectTrigger className={TRIGGER_COMPACT_CLASS}>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="static">{t('designer.inspector.options.optionsSourceStatic')}</SelectItem>
                <SelectItem value="designer">{t('designer.inspector.options.optionsSourceDesigner')}</SelectItem>
                <SelectItem value="dataset">{t('designer.inspector.options.optionsSourceDataset')}</SelectItem>
                <SelectItem value="api">{t('designer.inspector.options.optionsSourceApi')}</SelectItem>
              </SelectContent>
            </Select>
          </Field>
          {optionsSource === 'static' && (
            <Field label={t('designer.inspector.fields.optionsStatic')}>
              <Input
                value={str('options')}
                onChange={(e) => updateProp(id, 'options', e.target.value)}
                placeholder={t('designer.inspector.placeholders.options')}
                className={INPUT_COMPACT_CLASS}
              />
            </Field>
          )}
          {optionsSource === 'designer' && (
            <DropdownDesignerSourceFields
              id={id}
              designerId={str('optionsDesignerId')}
              version={optNum('optionsVersion')}
              labelField={str('labelField')}
              valueField={str('valueField')}
              dependsOn={str('dependsOn')}
              updateProp={updateProp}
            />
          )}
          {optionsSource === 'dataset' && (
            <DropdownDatasetSourceFields
              id={id}
              datasetId={str('optionsDatasetId')}
              labelField={str('labelField')}
              valueField={str('valueField')}
              dependsOn={str('dependsOn')}
              updateProp={updateProp}
            />
          )}
          {optionsSource === 'api' && (
            <>
              <Field label={t('designer.inspector.fields.apiPath')}>
                <Input
                  value={str('apiPath')}
                  onChange={(e) => updateProp(id, 'apiPath', e.target.value)}
                  placeholder={t('designer.inspector.placeholders.apiPath')}
                  className={INPUT_COMPACT_CLASS}
                />
                <span className="text-[10px] text-muted-foreground">
                  {t('designer.inspector.help.apiPath')}
                </span>
              </Field>
              <Field label={t('designer.inspector.fields.dropdownLabelField')}>
                <Input
                  value={str('labelField')}
                  onChange={(e) => updateProp(id, 'labelField', e.target.value)}
                  placeholder={t('designer.inspector.placeholders.labelField')}
                  className={INPUT_COMPACT_CLASS}
                />
              </Field>
              <Field label={t('designer.inspector.fields.dropdownValueField')}>
                <Input
                  value={str('valueField')}
                  onChange={(e) => updateProp(id, 'valueField', e.target.value)}
                  placeholder={t('designer.inspector.placeholders.valueField')}
                  className={INPUT_COMPACT_CLASS}
                />
              </Field>
              <Field label={t('designer.inspector.fields.dependsOn')}>
                <Input
                  value={str('dependsOn')}
                  onChange={(e) => updateProp(id, 'dependsOn', e.target.value)}
                  placeholder={t('designer.inspector.placeholders.dependsOn')}
                  className={INPUT_COMPACT_CLASS}
                />
                <span className="text-[10px] text-muted-foreground">
                  {t('designer.inspector.help.dependsOn')}
                </span>
              </Field>
            </>
          )}
          <Field label={t('designer.inspector.fields.placeholder')}>
            <Input
              value={str('placeholder')}
              onChange={(e) => updateProp(id, 'placeholder', e.target.value)}
              className={INPUT_COMPACT_CLASS}
            />
          </Field>
          <BoolField
            label={t('designer.inspector.fields.required')}
            checked={bool('required')}
            onChange={(v) => updateProp(id, 'required', v)}
          />
          <TableColumnFields element={element} updateProp={updateProp} />
          <StyleClassesField id={id} value={str('styleClasses')} updateProp={updateProp} />
        </>
      )
    }
    case 'DateTime Picker':
      return (
        <>
          <Field label={t('designer.inspector.fields.label')}>
            <Input
              value={str('label')}
              onChange={(e) => updateProp(id, 'label', e.target.value)}
              className={INPUT_COMPACT_CLASS}
            />
          </Field>
          <LabelModeField id={id} value={str('labelMode', 'vertical')} updateProp={updateProp} />
          <FieldKeyField id={id} value={str('fieldKey')} updateProp={updateProp} />
          <PgTypeField id={id} value={str('pgType')} updateProp={updateProp} />
          <Field label={t('designer.inspector.fields.format')}>
            <Select
              value={str('format', 'date')}
              onValueChange={(v) => updateProp(id, 'format', v)}
            >
              <SelectTrigger className={TRIGGER_COMPACT_CLASS}>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="date">{t('designer.inspector.options.formatDate')}</SelectItem>
                <SelectItem value="datetime">{t('designer.inspector.options.formatDatetime')}</SelectItem>
              </SelectContent>
            </Select>
          </Field>
          <BoolField
            label={t('designer.inspector.fields.required')}
            checked={bool('required')}
            onChange={(v) => updateProp(id, 'required', v)}
          />
          <TableColumnFields element={element} updateProp={updateProp} />
          <StyleClassesField id={id} value={str('styleClasses')} updateProp={updateProp} />
        </>
      )
    case 'Color Picker':
      return (
        <>
          <Field label={t('designer.inspector.fields.label')}>
            <Input
              value={str('label')}
              onChange={(e) => updateProp(id, 'label', e.target.value)}
              className={INPUT_COMPACT_CLASS}
            />
          </Field>
          <LabelModeField id={id} value={str('labelMode', 'vertical')} updateProp={updateProp} />
          <FieldKeyField id={id} value={str('fieldKey')} updateProp={updateProp} />
          <PgTypeField id={id} value={str('pgType')} updateProp={updateProp} />
          <Field label={t('designer.inspector.fields.defaultColor')}>
            {/* Native <input type=color> stays raw — shadcn Input is sized for
                text-like inputs and the browser's color picker chrome relies
                on default user-agent styling. */}
            <input
              type="color"
              value={str('defaultColor', '#000000')}
              onChange={(e) => updateProp(id, 'defaultColor', e.target.value)}
              className="h-8 w-16 cursor-pointer rounded border border-border"
            />
          </Field>
          <TableColumnFields element={element} updateProp={updateProp} />
          <StyleClassesField id={id} value={str('styleClasses')} updateProp={updateProp} />
        </>
      )
    case 'Repeater':
      return (
        <>
          <Field label={t('designer.inspector.fields.label')}>
            <Input
              value={str('label')}
              onChange={(e) => updateProp(id, 'label', e.target.value)}
              className={INPUT_COMPACT_CLASS}
            />
          </Field>
          <LabelModeField id={id} value={str('labelMode', 'vertical')} updateProp={updateProp} />
          <FieldKeyField
            id={id}
            value={str('fieldKey')}
            updateProp={updateProp}
            help={t('designer.inspector.help.repeaterFieldKey')}
          />
          <RepeaterRowFormFields
            id={id}
            rowDesignerId={str('rowDesignerId')}
            rowVersion={optNum('rowVersion')}
            updateProp={updateProp}
          />
          <Field label={t('designer.inspector.fields.minRows')}>
            <OptionalNumberInput
              value={optNum('minRows')}
              onChange={(v) => updateProp(id, 'minRows', v)}
              onClear={() => updateProp(id, 'minRows', undefined)}
            />
          </Field>
          <Field label={t('designer.inspector.fields.maxRows')}>
            <OptionalNumberInput
              value={optNum('maxRows')}
              onChange={(v) => updateProp(id, 'maxRows', v)}
              onClear={() => updateProp(id, 'maxRows', undefined)}
            />
          </Field>
          {(() => {
            const min = optNum('minRows')
            const max = optNum('maxRows')
            if (min !== undefined && min > 0 && max !== undefined && max < min) {
              return (
                <p className="text-[10px] text-destructive">
                  {t('designer.inspector.help.minMaxRowsError', { min, max })}
                </p>
              )
            }
            return null
          })()}
          <BoolField
            label={t('designer.inspector.fields.required')}
            checked={bool('required')}
            onChange={(v) => updateProp(id, 'required', v)}
          />
          <BoolField
            label={t('designer.inspector.fields.showHeaders')}
            checked={bool('showHeaders')}
            onChange={(v) => updateProp(id, 'showHeaders', v)}
          />
          <TableColumnFields element={element} updateProp={updateProp} />
          <StyleClassesField id={id} value={str('styleClasses')} updateProp={updateProp} />
        </>
      )
    case 'TreeView':
      return (
        <>
          <Field label={t('designer.inspector.fields.label')}>
            <Input
              value={str('label')}
              onChange={(e) => updateProp(id, 'label', e.target.value)}
              className={INPUT_COMPACT_CLASS}
            />
          </Field>
          <LabelModeField id={id} value={str('labelMode', 'vertical')} updateProp={updateProp} />
          <FieldKeyField
            id={id}
            value={str('fieldKey')}
            updateProp={updateProp}
            help={t('designer.inspector.help.treeViewFieldKey')}
          />
          <PgTypeField id={id} value={str('pgType')} updateProp={updateProp} />
          <RepeaterRowFormFields
            id={id}
            rowDesignerId={str('rowDesignerId')}
            rowVersion={optNum('rowVersion')}
            updateProp={updateProp}
          />
          <TreeViewDatasetSourceFields
            id={id}
            datasetId={str('optionsDatasetId')}
            keyField={str('datasetKeyField')}
            parentField={str('datasetParentField')}
            updateProp={updateProp}
          />
          <TreeViewAuthFilterSelect
            id={id}
            value={str('authFilterColumn')}
            rowDesignerId={str('rowDesignerId')}
            rowVersion={optNum('rowVersion')}
            updateProp={updateProp}
          />
          <Field label={t('designer.inspector.fields.pageSize')}>
            <OptionalNumberInput
              value={optNum('pageSize')}
              onChange={(v) => updateProp(id, 'pageSize', v)}
              onClear={() => updateProp(id, 'pageSize', undefined)}
            />
            <span className="text-[10px] text-muted-foreground">
              {t('designer.inspector.help.treeViewPageSize')}
            </span>
          </Field>
          {/* Presentation-only toggle (applies in every mode): compact dropdown rendering. */}
          <BoolField
            label={t('designer.inspector.fields.isDropdownUi')}
            checked={bool('isDropdownUi')}
            onChange={(v) => updateProp(id, 'isDropdownUi', v)}
          />
          <p className="text-[10px] text-muted-foreground">
            {t('designer.inspector.help.treeViewDropdownUi')}
          </p>
          {/* View mode is the dominant toggle: read-only browse. When on it overrides
              and hides mutation + selection entirely (and clears them). */}
          <BoolField
            label={t('designer.inspector.fields.viewMode')}
            checked={bool('viewMode')}
            onChange={(v) => {
              updateProp(id, 'viewMode', v)
              if (v) {
                updateProp(id, 'canCreate', false)
                updateProp(id, 'canEdit', false)
                updateProp(id, 'canDelete', false)
                updateProp(id, 'isMultiSelect', false)
                updateProp(id, 'selectAll', false)
              }
            }}
          />
          <p className="text-[10px] text-muted-foreground">
            {t('designer.inspector.help.treeViewViewMode')}
          </p>
          {!bool('viewMode') && (
            <>
              <BoolField
                label={t('designer.inspector.fields.required')}
                checked={bool('required')}
                onChange={(v) => updateProp(id, 'required', v)}
              />
              {/* CRUD flags and multi-select are mutually exclusive (see
                  DesignerElementProperties). Checking any CRUD flag clears
                  isMultiSelect; checking isMultiSelect clears all CRUD flags.
                  No CRUD flag set ⇒ view/select mode binding fieldKey. */}
              <BoolField
                label={t('designer.inspector.fields.canCreate')}
                checked={bool('canCreate')}
                onChange={(v) => {
                  updateProp(id, 'canCreate', v)
                  if (v) {
                    updateProp(id, 'isMultiSelect', false)
                    updateProp(id, 'selectAll', false)
                  }
                }}
              />
              <BoolField
                label={t('designer.inspector.fields.canEdit')}
                checked={bool('canEdit')}
                onChange={(v) => {
                  updateProp(id, 'canEdit', v)
                  if (v) {
                    updateProp(id, 'isMultiSelect', false)
                    updateProp(id, 'selectAll', false)
                  }
                }}
              />
              <BoolField
                label={t('designer.inspector.fields.canDelete')}
                checked={bool('canDelete')}
                onChange={(v) => {
                  updateProp(id, 'canDelete', v)
                  if (v) {
                    updateProp(id, 'isMultiSelect', false)
                    updateProp(id, 'selectAll', false)
                  }
                }}
              />
              <BoolField
                label={t('designer.inspector.fields.isMultiSelect')}
                checked={bool('isMultiSelect')}
                onChange={(v) => {
                  updateProp(id, 'isMultiSelect', v)
                  if (v) {
                    updateProp(id, 'canCreate', false)
                    updateProp(id, 'canEdit', false)
                    updateProp(id, 'canDelete', false)
                  } else {
                    updateProp(id, 'selectAll', false)
                  }
                }}
              />
              {/* All select depends on Is multi-select: cascade a node's selection
                  to its whole subtree. */}
              {bool('isMultiSelect') && (
                <>
                  <BoolField
                    label={t('designer.inspector.fields.selectAll')}
                    checked={bool('selectAll')}
                    onChange={(v) => updateProp(id, 'selectAll', v)}
                  />
                  <p className="text-[10px] text-muted-foreground">
                    {t('designer.inspector.help.treeViewSelectAll')}
                  </p>
                </>
              )}
              {(bool('canCreate') || bool('canEdit') || bool('canDelete')) && bool('isMultiSelect') ? (
                <p className="text-[10px] text-destructive">
                  {t('designer.inspector.help.treeViewMutualExclusion')}
                </p>
              ) : null}
            </>
          )}
          <TableColumnFields element={element} updateProp={updateProp} />
          <StyleClassesField id={id} value={str('styleClasses')} updateProp={updateProp} />
        </>
      )
    case 'Repeater Field':
      return (
        <>
          <RepeaterFieldNameSelect
            id={id}
            value={str('fieldName')}
            rowDesignerId={
              repeaterAncestor && typeof repeaterAncestor.properties.rowDesignerId === 'string'
                ? repeaterAncestor.properties.rowDesignerId
                : ''
            }
            rowVersion={
              repeaterAncestor && Number.isFinite(Number(repeaterAncestor.properties.rowVersion))
                ? Number(repeaterAncestor.properties.rowVersion)
                : undefined
            }
            datasetId={
              repeaterAncestor
                ? typeof repeaterAncestor.properties.optionsDatasetId === 'string'
                  ? repeaterAncestor.properties.optionsDatasetId
                  : ''
                : // Top-level Field: a dataset-bound component sources options from
                  // the dataset's columns (takes precedence over localFieldKeys).
                  (formDatasetId ?? '')
            }
            localFieldKeys={repeaterAncestor === null ? localFieldKeys : undefined}
            updateProp={updateProp}
          />
          <Field label={t('designer.inspector.fields.mapExpression')}>
            <Textarea
              value={str('mapExpression')}
              onChange={(e) => updateProp(id, 'mapExpression', e.target.value)}
              placeholder={t('designer.inspector.placeholders.mapExpression')}
              rows={2}
              className={cn(INPUT_COMPACT_CLASS, 'font-mono leading-snug resize-y min-h-[44px]')}
            />
            <span
              className="text-[10px] text-muted-foreground"
              dangerouslySetInnerHTML={{ __html: t('designer.inspector.help.mapExpression') }}
            />
          </Field>
          {repeaterAncestor?.properties.showHeaders ? (
            <Field label={t('designer.inspector.fields.headerName')}>
              <Input
                value={str('headerName')}
                onChange={(e) => updateProp(id, 'headerName', e.target.value)}
                placeholder={t('designer.inspector.placeholders.headerName')}
                className={INPUT_COMPACT_CLASS}
              />
              <span
                className="text-[10px] text-muted-foreground"
                dangerouslySetInnerHTML={{ __html: t('designer.inspector.help.headerName') }}
              />
            </Field>
          ) : null}
          {/* CRUD record-list column controls — only for a top-level Field (one NOT
              inside a Repeater/TreeView). Inside a row template the Repeater owns the
              tabular columns, so these would be meaningless there. */}
          {repeaterAncestor === null ? (
            <div className="mt-2 flex flex-col gap-3 border-t border-border pt-3">
              <p className="text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">
                {t('designer.inspector.tableColumnHeader')}
              </p>
              <BoolField
                label={t('designer.inspector.fields.isTableColumn')}
                checked={bool('isTableColumn')}
                onChange={(v) => updateProp(id, 'isTableColumn', v)}
              />
              <span
                className="text-[10px] text-muted-foreground"
                dangerouslySetInnerHTML={{ __html: t('designer.inspector.help.isTableColumn') }}
              />
              {bool('isTableColumn') ? (
                <>
                  <Field label={t('designer.inspector.fields.columnHeader')}>
                    <Input
                      value={str('columnHeader')}
                      onChange={(e) => updateProp(id, 'columnHeader', e.target.value)}
                      placeholder={t('designer.inspector.placeholders.columnHeader')}
                      className={INPUT_COMPACT_CLASS}
                    />
                  </Field>
                  <Field label={t('designer.inspector.fields.columnOrder')}>
                    <OptionalNumberInput
                      value={optNum('columnOrder')}
                      onChange={(v) => updateProp(id, 'columnOrder', v)}
                      onClear={() => updateProp(id, 'columnOrder', undefined)}
                    />
                  </Field>
                </>
              ) : null}
            </div>
          ) : null}
          <StyleClassesField id={id} value={str('styleClasses')} updateProp={updateProp} />
          <Field label={t('designer.inspector.fields.inlineStyle')}>
            <Textarea
              value={str('inlineStyle')}
              onChange={(e) => updateProp(id, 'inlineStyle', e.target.value)}
              placeholder={t('designer.inspector.placeholders.inlineStyle')}
              rows={3}
              className={cn(INPUT_COMPACT_CLASS, 'font-mono leading-snug resize-y min-h-[60px]')}
            />
            <span
              className="text-[10px] text-muted-foreground"
              dangerouslySetInnerHTML={{ __html: t('designer.inspector.help.inlineStyle') }}
            />
          </Field>
        </>
      )
    case 'File':
      return (
        <>
          <LabelModeField id={id} value={str('labelMode', 'vertical')} updateProp={updateProp} />
          <FieldKeyField id={id} value={str('fieldKey')} updateProp={updateProp} />
          <Field label={t('designer.inspector.fields.label')}>
            <Input
              value={str('label')}
              onChange={(e) => updateProp(id, 'label', e.target.value)}
              placeholder={t('designer.inspector.placeholders.label')}
              className={INPUT_COMPACT_CLASS}
            />
          </Field>
          <Field label={t('designer.inspector.fields.fileAccept')}>
            <Input
              value={str('accept')}
              onChange={(e) => updateProp(id, 'accept', e.target.value)}
              placeholder="image/*, .pdf, .doc"
              className={INPUT_COMPACT_CLASS}
            />
            <p
              className="mt-1 text-[10px] text-muted-foreground"
              dangerouslySetInnerHTML={{ __html: t('designer.inspector.help.fileAccept') }}
            />
          </Field>
          <BoolField
            label={t('designer.inspector.fields.required')}
            checked={element.properties.required === true}
            onChange={(v) => updateProp(id, 'required', v)}
          />
          <TableColumnFields element={element} updateProp={updateProp} />
          <StyleClassesField id={id} value={str('styleClasses')} updateProp={updateProp} />
        </>
      )
    case 'Image': {
      const srcValue = str('src')
      const srcEmpty = srcValue === ''
      return (
        <>
          <Field label={`${t('designer.inspector.fields.imageSource')} *`}>
            <Input
              value={srcValue}
              onChange={(e) => updateProp(id, 'src', e.target.value)}
              className={cn(INPUT_COMPACT_CLASS, srcEmpty && 'border-destructive')}
            />
            {srcEmpty ? (
              <span className="text-[10px] text-destructive">{t('errors.required')}</span>
            ) : (
              <p className="mt-1 text-[10px] text-muted-foreground">
                {t('designer.inspector.help.imageSource')}
              </p>
            )}
          </Field>
          <Field label={t('designer.inspector.fields.altText')}>
            <Input
              value={str('alt')}
              onChange={(e) => updateProp(id, 'alt', e.target.value)}
              className={INPUT_COMPACT_CLASS}
            />
          </Field>
          <Field label={t('designer.inspector.fields.width')}>
            <OptionalNumberInput
              value={optNum('width')}
              onChange={(v) => updateProp(id, 'width', v)}
              onClear={() => updateProp(id, 'width', undefined)}
            />
          </Field>
          <Field label={t('designer.inspector.fields.height')}>
            <OptionalNumberInput
              value={optNum('height')}
              onChange={(v) => updateProp(id, 'height', v)}
              onClear={() => updateProp(id, 'height', undefined)}
            />
          </Field>
          <StyleClassesField id={id} value={str('styleClasses')} updateProp={updateProp} />
        </>
      )
    }
    case 'RawHtml':
      return (
        <>
          <Field label={t('designer.inspector.fields.rawHtml')}>
            <Textarea
              value={str('rawHtml')}
              onChange={(e) => updateProp(id, 'rawHtml', e.target.value)}
              placeholder={t('designer.inspector.placeholders.rawHtml')}
              rows={6}
              className={cn(INPUT_COMPACT_CLASS, 'font-mono leading-snug resize-y min-h-[120px]')}
            />
            <span
              className="text-[10px] text-muted-foreground"
              dangerouslySetInnerHTML={{ __html: t('designer.inspector.help.rawHtml') }}
            />
          </Field>
          <StyleClassesField id={id} value={str('styleClasses')} updateProp={updateProp} />
        </>
      )
    case 'Tabs': {
      // Normalize on read so legacy / hand-edited values that don't match the
      // dropdown options exactly (e.g. `"VerTical"`) coerce to the canonical form.
      const orientation =
        String(p.orientation ?? 'horizontal').toLowerCase() === 'vertical'
          ? 'vertical'
          : 'horizontal'
      return (
        <>
          <Field label={t('designer.inspector.fields.orientation')}>
            <Select
              value={orientation}
              onValueChange={(v) => updateProp(id, 'orientation', v)}
            >
              <SelectTrigger className={TRIGGER_COMPACT_CLASS}>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="horizontal">{t('designer.inspector.options.orientationHorizontal')}</SelectItem>
                <SelectItem value="vertical">{t('designer.inspector.options.orientationVertical')}</SelectItem>
              </SelectContent>
            </Select>
          </Field>
          <StyleClassesField id={id} value={str('styleClasses')} updateProp={updateProp} />
        </>
      )
    }
    case 'DatasetComponent':
      return <DatasetComponentFields element={element} updateProp={updateProp} />
    case 'SingleRecord':
      return <SingleRecordComponentFields element={element} updateProp={updateProp} />
    case 'Stack':
    case 'Row':
      // Stack and Row have no other inspector fields — Style classes is the
      // sole property exposed for these structural containers.
      return <StyleClassesField id={id} value={str('styleClasses')} updateProp={updateProp} />
    default:
      return null
  }
}

function TabChildFields({
  element,
  parent,
  updateProp,
}: {
  element: DesignerElement
  parent: DesignerElement
  updateProp: UpdateProp
}) {
  const { t } = useTranslation()
  const id = element.id
  const p = element.properties
  // findIndex returns -1 when missing; `?? 0` only catches the children-undefined case.
  const rawIdx = parent.children?.findIndex((c) => c.id === id) ?? -1
  const childIndex = rawIdx === -1 ? 0 : rawIdx
  const tabNameValue = p.tabName !== undefined && p.tabName !== null ? String(p.tabName) : ''
  function pxNum(key: string, fallback: number): number {
    if (p[key] === undefined) return fallback
    const v = Number(p[key])
    if (!Number.isFinite(v)) return fallback
    return v < 0 ? 0 : v
  }
  return (
    <>
      <Field label={t('designer.inspector.fields.tabName')}>
        <Input
          value={tabNameValue}
          placeholder={t('designer.canvas.tabFallbackName', { n: childIndex + 1 })}
          onChange={(e) => updateProp(id, 'tabName', e.target.value)}
          className={INPUT_COMPACT_CLASS}
        />
      </Field>
      <Field label={t('designer.inspector.fields.paddingPx')}>
        <NumberInput
          value={pxNum('paddingPx', 8)}
          onChange={(v) => updateProp(id, 'paddingPx', v)}
        />
      </Field>
      <Field label={t('designer.inspector.fields.contentGapPx')}>
        <NumberInput
          value={pxNum('contentGapPx', 8)}
          onChange={(v) => updateProp(id, 'contentGapPx', v)}
        />
      </Field>
      <StyleClassesField
        id={id}
        value={p.styleClasses !== undefined && p.styleClasses !== null ? String(p.styleClasses) : ''}
        updateProp={updateProp}
      />
    </>
  )
}

export default function PropertyInspector() {
  const { t } = useTranslation()
  const selectedElementId = useDesignerCanvasStore((s) => s.selectedElementId)
  const rootElement = useDesignerCanvasStore((s) => s.rootElement)
  const updateProp = useDesignerCanvasStore((s) => s.updateElementProperty)
  const selectElement = useDesignerCanvasStore((s) => s.selectElement)
  // Per-version dataset binding for the component being edited. When set, a
  // top-level Field's "Field name" options come from the dataset's columns
  // (the record list reads from the dataset, so those are the available fields).
  const formDatasetId = useDesignerCanvasStore((s) => s.datasetId)

  const selected = findById(rootElement, selectedElementId ?? '')

  // Clear a dangling selectedElementId — if the selection points at an id
  // that's no longer in the tree (e.g. removed out-of-band, or a tree-replacing
  // setSchema), the store would otherwise hold stale state indefinitely.
  useEffect(() => {
    if (selectedElementId !== null && selected === null) {
      selectElement(null)
    }
  }, [selectedElementId, selected, selectElement])

  if (!selected) {
    return (
      <div className="flex flex-col items-center gap-2 rounded-lg border border-dashed border-border bg-muted/40 px-4 py-8 text-center">
        <div className="flex h-10 w-10 items-center justify-center rounded-full bg-muted text-muted-foreground">
          <MousePointerClick className="h-5 w-5" />
        </div>
        <p className="text-sm font-medium text-foreground">
          {t('designer.inspector.selectElementHintTitle')}
        </p>
        <p className="text-xs text-muted-foreground">
          {t('designer.inspector.selectElementHint')}
        </p>
      </div>
    )
  }

  // Stack-inside-Tabs is special-cased: each direct child of a Tabs container
  // is a tab, so we expose tab-specific fields (name / padding / content gap)
  // instead of the (currently nonexistent) default Stack inspector.
  const parent = selected.type === 'Stack' ? findParent(rootElement, selected.id) : null
  const isTabChild = parent?.type === 'Tabs'

  // Repeater Field reads this to decide whether the 'Header name' field surfaces.
  const repeaterAncestor =
    selected.type === 'Repeater Field' ? findRepeaterAncestor(rootElement, selected.id) : null
  // For a top-level Field (no Repeater/TreeView ancestor) the "Field name" picker
  // sources its options from the fields on THIS canvas. extractRowFormFieldKeys
  // walks the tree collecting scalar fieldKeys and skips Repeater subtrees, which
  // is exactly the candidate set for a form-level Field.
  const localFieldKeys =
    selected.type === 'Repeater Field' && repeaterAncestor === null
      ? extractRowFormFieldKeys(rootElement)
      : undefined

  const visHideAll =
    selected.properties.hideWhenAll !== undefined && selected.properties.hideWhenAll !== null
      ? String(selected.properties.hideWhenAll)
      : ''
  const visHideAny =
    selected.properties.hideWhenAny !== undefined && selected.properties.hideWhenAny !== null
      ? String(selected.properties.hideWhenAny)
      : ''

  return (
    <div className="flex flex-col gap-4">
      <div>
        <p className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">
          {isTabChild ? t('designer.inspector.tabSection') : selected.type}
        </p>
        <Tooltip>
          <TooltipTrigger asChild>
            <p className="mt-0.5 truncate font-mono text-[10px] text-muted-foreground">
              {selected.id}
            </p>
          </TooltipTrigger>
          <TooltipContent>{selected.id}</TooltipContent>
        </Tooltip>
      </div>
      {isTabChild && parent
        ? <TabChildFields element={selected} parent={parent} updateProp={updateProp} />
        : <PropertyFields element={selected} repeaterAncestor={repeaterAncestor} localFieldKeys={localFieldKeys} formDatasetId={formDatasetId} updateProp={updateProp} />}
      <VisibilityFields
        id={selected.id}
        hideWhenAll={visHideAll}
        hideWhenAny={visHideAny}
        updateProp={updateProp}
      />
    </div>
  )
}
