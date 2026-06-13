// Table-view column configuration for a DatasetComponent. The author picks which dataset
// columns are visible and an optional header override; stored on properties.tableColumns.

export type TableColumnConfig = {
  column: string
  visible: boolean
  header: string // '' ⇒ use the column name
}

export function parseTableColumns(raw: unknown): TableColumnConfig[] {
  if (!Array.isArray(raw)) return []
  const out: TableColumnConfig[] = []
  for (const it of raw) {
    if (it === null || typeof it !== 'object') continue
    const c = it as Record<string, unknown>
    if (typeof c.column !== 'string' || c.column === '') continue
    out.push({
      column: c.column,
      visible: c.visible !== false, // absent ⇒ visible
      header: typeof c.header === 'string' ? c.header : '',
    })
  }
  return out
}

// The ordered, visible columns to render — resolved against the dataset's live column list.
// Columns with no config default to visible (header = column name). Configured order wins;
// any live columns not mentioned in the config follow, in dataset order.
export function resolveVisibleColumns(
  config: TableColumnConfig[],
  allColumns: string[],
): { column: string; header: string }[] {
  const known = new Set(allColumns)
  const configured = config.filter((c) => known.has(c.column))
  const mentioned = new Set(configured.map((c) => c.column))
  const out: { column: string; header: string }[] = []
  for (const c of configured) {
    if (c.visible) out.push({ column: c.column, header: c.header !== '' ? c.header : c.column })
  }
  for (const col of allColumns) {
    if (!mentioned.has(col)) out.push({ column: col, header: col })
  }
  return out
}
