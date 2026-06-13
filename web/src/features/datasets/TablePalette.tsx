import { useRef, useState } from 'react'
import { useVirtualizer } from '@tanstack/react-virtual'
import { useTranslation } from 'react-i18next'
import { Input } from '@/components/ui/input'
import { useCatalogQuery } from './useCatalogQuery'
import type { CatalogTable } from './datasetApi'

// Fixed row height (card + bottom gap) for the virtualized list. Must match the rendered
// PaletteEntry height; rows are uniform so a constant estimate is exact.
const ROW_HEIGHT = 52

function PaletteEntry({ table }: { table: CatalogTable }) {
  const handleDragStart = (e: React.DragEvent<HTMLDivElement>) => {
    // The list carries names + counts only; the canvas fetches this table's columns
    // on drop (lazy load). Serialize the lightweight descriptor for the drop handler.
    e.dataTransfer.setData('application/formforge-table', JSON.stringify(table))
    e.dataTransfer.effectAllowed = 'copy'
  }

  return (
    <div
      draggable
      onDragStart={handleDragStart}
      className="cursor-grab rounded border border-border bg-card px-3 py-2 hover:bg-accent active:cursor-grabbing"
    >
      <div className="truncate text-sm font-medium">{table.tableName}</div>
      <div className="mt-0.5 text-xs text-muted-foreground">
        {table.columnCount} column{table.columnCount !== 1 ? 's' : ''}
      </div>
    </div>
  )
}

export function TablePalette() {
  const { t } = useTranslation()
  const [search, setSearch] = useState('')
  const { data, isLoading, isError } = useCatalogQuery()

  const tables = data?.tables ?? []
  const filtered = search.trim()
    ? tables.filter((tbl) => tbl.tableName.toLowerCase().includes(search.toLowerCase()))
    : tables

  // Virtualize the list so only the visible rows render — the catalog can hold thousands
  // of tables. Client-side name filtering stays instant (it's just a string scan).
  const scrollRef = useRef<HTMLDivElement>(null)
  const virtualizer = useVirtualizer({
    count: filtered.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: 8,
  })

  const showList = !isLoading && !isError && filtered.length > 0

  return (
    <aside className="flex h-full w-56 flex-col border-r border-border bg-background">
      <div className="p-3">
        <p className="mb-2 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
          {t('datasets.builder.palette.title')}
        </p>
        <Input
          placeholder={t('datasets.builder.palette.searchPlaceholder')}
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          className="h-8 text-sm"
        />
      </div>

      <div ref={scrollRef} className="flex-1 overflow-y-auto px-3 pb-3">
        {isLoading && (
          <p className="text-xs text-muted-foreground">{t('datasets.builder.palette.loading')}</p>
        )}
        {isError && (
          <p className="text-xs text-destructive">{t('datasets.builder.palette.loadError')}</p>
        )}
        {!isLoading && !isError && filtered.length === 0 && (
          <p className="text-xs text-muted-foreground">
            {search ? t('datasets.builder.palette.noMatches') : t('datasets.builder.palette.empty')}
          </p>
        )}
        {showList && (
          <div style={{ height: virtualizer.getTotalSize(), position: 'relative', width: '100%' }}>
            {virtualizer.getVirtualItems().map((row) => {
              const table = filtered[row.index]
              return (
                <div
                  key={table.tableName}
                  className="pb-2"
                  style={{
                    position: 'absolute',
                    top: 0,
                    left: 0,
                    width: '100%',
                    height: row.size,
                    transform: `translateY(${row.start}px)`,
                  }}
                >
                  <PaletteEntry table={table} />
                </div>
              )
            })}
          </div>
        )}
      </div>
    </aside>
  )
}
