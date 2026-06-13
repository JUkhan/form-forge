import { useQuery } from '@tanstack/react-query'
import { useMemo } from 'react'
import { designerApi } from '@/features/designer/designerApi'
import { getFieldKey, type DesignerElement } from '@/types/designer'

// Story 6.10 — produces the stable list of user fieldKeys that drives
// RecordListPage's column headers (AC-1). Shares its queryKey
// (`['component-schema', designerId, 'latest']`) with DynamicComponent so the
// first to fetch warms the cache for the other; typical navigation through
// the list → detail flow makes zero extra network round trips.
//
// Story B-1-4-followup — also derives the richer `columns` descriptor that lets
// authors control which fields appear in the table, their header, and their order,
// and surfaces derived (subquery-backed) columns for Designer-backed Dropdowns
// (label) and Repeaters (child-row count). The legacy `fields`/`fieldKeys` views
// are kept for back-compat.

// Filter input kind per fieldKey. Mirrors the backend ColumnDefinition.PgType
// (TEXT | NUMERIC | BOOLEAN | TIMESTAMPTZ | JSONB) but collapsed to the four
// kinds the filter UI actually has distinct widgets for; 'other' is the
// fallback (JSONB / unknown component types) and renders as a plain text input.
export type FieldFilterKind = 'text' | 'number' | 'boolean' | 'datetime' | 'other'

export interface FieldInfo {
  key: string
  kind: FieldFilterKind
}

// Story B-1-4-followup — config for a Designer-backed Dropdown's reference
// filter. The displayed column is the derived label (dataKey = subquery alias),
// but filtering happens on the stored FK column (the dropdown's fieldKey, which
// holds the referenced row's value). The combobox is fed by the same
// /options endpoint the form's DesignerDropdownField uses, so the user picks an
// option and we filter this table on `filterColumn = <picked value>`.
export interface ReferenceFilterConfig {
  // Physical column to filter on — the dropdown's fieldKey, NOT dataKey (which
  // is the display-only label alias).
  filterColumn: string
  designerId: string
  version: number
  labelField: string
  valueField: string
}

// Story B-1-4-followup — one resolved record-list column.
export interface TableColumn {
  // Key into the row object the backend returns. For scalar fields this is the
  // fieldKey; for derived columns it is the subquery alias (see buildDerivedAlias).
  dataKey: string
  // Header text: author override (columnHeader) or the humanized fieldKey.
  header: string
  // Ascending display position; columnOrder when set, else document order.
  order: number
  // Derived (subquery-backed) columns are display-only — no sort affordance.
  sortable: boolean
  // Filter widget kind, or undefined when the column is not filterable (derived
  // columns and reference columns — the latter use referenceFilter instead).
  filterKind?: FieldFilterKind
  // Present only for Designer-backed Dropdown columns: drives a combobox filter
  // that picks an option from the referenced designer and filters this table on
  // the stored FK value. Absent when the dropdown lacks the version/valueField
  // needed to fetch options.
  referenceFilter?: ReferenceFilterConfig
}

// Mirror of FormForge.Api.Features.SchemaRegistry.ComponentTypeMapper.
// Keep in sync — backend authoritative for column PG types, but the filter UI
// needs the mapping client-side to pick the right input widget without an
// extra round trip. Both shorthand and SPA "Title Case" component types are
// covered (Story 5.4 bridge mappings).
function mapComponentTypeToKind(componentType: string): FieldFilterKind {
  switch (componentType) {
    case 'TextInput':
    case 'TextArea':
    case 'Dropdown':
    case 'ColorPicker':
    case 'File':
    case 'Text Input':
    case 'Text Area':
    case 'Color Picker':
      return 'text'
    case 'NumberInput':
    case 'Number Input':
      return 'number'
    case 'Checkbox':
      return 'boolean'
    case 'DateTimePicker':
    case 'DateTime Picker':
      return 'datetime'
    default:
      return 'other'
  }
}

// Scalar field types whose own column value is displayed directly. Dropdown and
// Repeater are handled separately (they may produce a derived column).
const SCALAR_COLUMN_TYPES = new Set<string>([
  'TextInput', 'Text Input',
  'TextArea', 'Text Area',
  'NumberInput', 'Number Input',
  'DateTimePicker', 'DateTime Picker',
  'ColorPicker', 'Color Picker',
  'Checkbox',
  'File',
])

// Humanize a snake_case fieldKey for column-header display: underscores become
// spaces and each word is capitalized (report_title → "Report Title").
export function humanizeFieldKey(key: string): string {
  return key
    .split('_')
    .filter((word) => word.length > 0)
    .map((word) => word.charAt(0).toUpperCase() + word.slice(1))
    .join(' ')
}

// MUST mirror RootElementParser.BuildDerivedAlias on the backend: Postgres
// truncates result-column labels to 63 bytes, so we apply the identical rule to
// recompute the exact key the SELECT subquery is aliased to.
export function buildDerivedAlias(referencedId: string, suffix: string): string {
  const combined = `${referencedId}_${suffix}`
  return combined.length <= 63 ? combined : combined.slice(0, 63)
}

function str(value: unknown): string {
  return typeof value === 'string' ? value : ''
}

interface RawColumn {
  docIndex: number
  column: TableColumn
}

// DFS pre-order walk collecting candidate columns. `counter` increments once per
// emitted column so document order is a stable tiebreak for columnOrder.
function collectColumns(
  el: DesignerElement | null,
  out: RawColumn[],
  counter: { i: number },
): void {
  if (!el) return
  const type = el.type
  const p = el.properties
  const key = getFieldKey(p)
  const shown = p.showInTable !== false // default true when absent

  const header = (() => {
    const override = str(p.columnHeader).trim()
    return override !== '' ? override : humanizeFieldKey(key)
  })()
  const push = (column: Omit<TableColumn, 'order'>) => {
    const docIndex = counter.i++
    const order =
      p.columnOrder !== undefined && Number.isFinite(Number(p.columnOrder))
        ? Number(p.columnOrder)
        : docIndex
    out.push({ docIndex, column: { ...column, order } })
  }

  if (type === 'Repeater') {
    // Repeater rows live in a child table; the column shows their live count via
    // a backend subquery aliased {rowDesignerId}_row_count. Children are NOT
    // top-level columns, so do not recurse into them.
    const rowDesignerId = str(p.rowDesignerId)
    if (shown && rowDesignerId !== '') {
      push({
        dataKey: buildDerivedAlias(rowDesignerId, 'row_count'),
        header,
        sortable: false,
      })
    }
    return
  }

  if (type === 'Dropdown') {
    if (shown && key !== '') {
      const isDesignerBacked = p.optionsSource === 'designer'
      const isDatasetBacked = p.optionsSource === 'dataset'
      const optionsDesignerId = str(p.optionsDesignerId)
      const labelField = str(p.labelField)
      if (isDesignerBacked && optionsDesignerId !== '' && labelField !== '') {
        // Designer-backed: show the referenced label (derived), not the stored id.
        // A reference filter (combobox over the referenced designer) is offered
        // when we have the version + valueField needed to fetch its options; the
        // pick filters this table on the dropdown's own fieldKey (the stored FK).
        const version = Number(p.optionsVersion)
        const valueField = str(p.valueField).trim()
        const canFilter =
          Number.isInteger(version) && version > 0 && valueField !== ''
        push({
          dataKey: buildDerivedAlias(optionsDesignerId, labelField),
          header,
          sortable: false,
          referenceFilter: canFilter
            ? {
                filterColumn: key,
                designerId: optionsDesignerId,
                version,
                labelField,
                valueField,
              }
            : undefined,
        })
      } else if (isDatasetBacked) {
        // Dataset-backed: show the label pulled from the dataset's VIEW (derived),
        // not the stored value. optionsDatasetId holds the VIEW name; the backend
        // aliases the subquery buildDerivedAlias(viewName, labelField) — mirror it.
        // (No reference filter for now — dataset dropdowns display the label only.)
        const optionsDatasetId = str(p.optionsDatasetId)
        const valueField = str(p.valueField).trim()
        if (optionsDatasetId !== '' && labelField !== '' && valueField !== '') {
          push({
            dataKey: buildDerivedAlias(optionsDatasetId, labelField),
            header,
            sortable: false,
          })
        } else {
          // Misconfigured dataset dropdown: fall back to its own column value.
          push({ dataKey: key, header, sortable: true, filterKind: 'text' })
        }
      } else {
        // Static (or misconfigured designer) dropdown: own column value.
        push({ dataKey: key, header, sortable: true, filterKind: 'text' })
      }
    }
    return
  }

  if (shown && key !== '' && SCALAR_COLUMN_TYPES.has(type)) {
    push({ dataKey: key, header, sortable: true, filterKind: mapComponentTypeToKind(type) })
  }

  for (const child of el.children ?? []) {
    collectColumns(child, out, counter)
  }
}

// Exported for unit testing — pure resolution of a schema tree into ordered
// table columns (no React / network involved).
export function extractColumns(el: DesignerElement | null): TableColumn[] {
  const raw: RawColumn[] = []
  collectColumns(el, raw, { i: 0 })
  // Two elements producing the same dataKey would collide on the row accessor and
  // React key — first occurrence (lowest docIndex) wins.
  const seen = new Set<string>()
  const unique = raw.filter((r) => {
    if (seen.has(r.column.dataKey)) return false
    seen.add(r.column.dataKey)
    return true
  })
  // Sort by author order, then document order as a stable tiebreak.
  unique.sort((a, b) => a.column.order - b.column.order || a.docIndex - b.docIndex)
  return unique.map((r) => r.column)
}

export function useDesignerFieldKeys(designerId: string) {
  const query = useQuery({
    queryKey: ['component-schema', designerId, 'latest'] as const,
    queryFn: () => designerApi.getSchema(designerId),
    staleTime: 300_000,
    enabled: !!designerId,
  })
  const columns = useMemo(
    () => extractColumns(query.data?.rootElement ?? null),
    [query.data?.rootElement],
  )
  // Legacy keys-only / FieldInfo views — derived from the resolved columns so
  // existing call sites (and test mocks) keep working. Only filterable scalar
  // columns carry a kind; derived columns are display-only and excluded here.
  const fields = useMemo<FieldInfo[]>(
    () =>
      columns
        .filter((c) => c.filterKind !== undefined)
        .map((c) => ({ key: c.dataKey, kind: c.filterKind as FieldFilterKind })),
    [columns],
  )
  const fieldKeys = useMemo(() => columns.map((c) => c.dataKey), [columns])
  // displayName is the human-readable schema title (e.g. "Patient Intake")
  // — surfaced so list pages can show it as the H1 instead of the raw
  // designerId. Undefined while the schema query is loading; callers
  // should fall back to designerId in that window.
  const displayName = query.data?.displayName
  return {
    columns,
    fields,
    fieldKeys,
    displayName,
    isLoading: query.isLoading,
    isError: query.isError,
  }
}
