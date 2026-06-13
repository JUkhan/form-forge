# Story 9.1: Table Palette with Allowlisted Tables

Status: done

## Story

As a user with `dataset-management`,
I want to see the Table Palette listing allowlisted tables and drag them onto the canvas,
So that I can build a query from real database tables.

## Acceptance Criteria

**AC-1 — Catalog returns allowlisted tables**
**Given** the API is running and `DatasetManager:AllowedTables` is configured
**When** I call `GET /api/datasets/catalog` (requires `dataset-management`)
**Then** I receive a JSON body `{ "tables": [{ "tableName": "...", "columns": [{ "columnName": "...", "pgType": "...", "isNullable": true }] }] }`
**And** tables are drawn from `information_schema.columns WHERE table_schema = 'public' AND table_name = ANY(@allowlist)`
**And** the response is cached in `IMemoryCache` with a 5-minute TTL (per AR-62)

**AC-2 — Palette shows table name and columns**
**Given** I open the Query Builder canvas for a Dataset (navigate to `/admin/datasets/{id}`)
**When** the canvas loads
**Then** the left-side Table Palette lists all tables returned by `GET /api/datasets/catalog`
**And** each palette entry shows the table name and its column list (name + PG data type)

**AC-3 — Palette is searchable**
**Given** the Table Palette
**When** I type in the search input
**Then** only matching entries (table names containing the search string, case-insensitive) are shown

**AC-4 — Drag table creates a TableNode**
**Given** I drag a table from the palette onto the React Flow canvas (`@xyflow/react` v12)
**When** the drop completes on the canvas
**Then** a `TableNode` is created containing: table name header, Left/Right designation control (visual placeholder — behavior comes in Story 9.5), column list (checkboxes shown but interaction deferred to Story 10.1), and a unique node ID

**AC-5 — Multiple drags of the same table create distinct nodes**
**Given** the same table is dragged multiple times
**When** each drag completes
**Then** each instance is a distinct node with a unique node ID (enables self-joins per FR-63 AC-4)

**AC-6 — Non-allowlisted tables never appear**
**Given** a table not in `DatasetManager:AllowedTables`
**When** I inspect the palette
**Then** it is absent — the palette only renders what the server returns; no client-side allowlist bypass is possible

---

## Tasks / Subtasks

### Task 1 — Backend: Create `DatasetAllowlist.cs` (AC-1, AC-6)

Create `src/FormForge.Api/Features/Datasets/DatasetAllowlist.cs`:

```csharp
using Dapper;
using FormForge.Api.Domain.ValueTypes;
using FormForge.Api.Features.Datasets.Dtos;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace FormForge.Api.Features.Datasets;

internal interface IDatasetAllowlist
{
    Task<CatalogDto> GetCatalogAsync(CancellationToken ct);
    bool IsAllowed(string tableName);
}

internal sealed class DatasetAllowlist : IDatasetAllowlist
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private const string CacheKey = "datasets.catalog";

    // AR-57: same denylist as DatasetName.PermanentDenylist — strips internal tables
    // even if an operator accidentally lists one in config.
    private static readonly HashSet<string> InternalDenylist = new(StringComparer.Ordinal)
    {
        "users", "roles", "user_roles", "menus", "menu_role_assignments",
        "component_schemas", "refresh_tokens", "password_reset_tokens",
        "mfa_backup_codes", "mfa_sessions", "schema_audit_log",
        "mutation_audit_log", "dataset_audit_log", "custom_dataset",
    };

    private readonly IReadOnlyList<string> _effectiveAllowlist;
    private readonly IMemoryCache _cache;
    private readonly DbConnectionFactory _db;
    private readonly ILogger<DatasetAllowlist> _logger;

    public DatasetAllowlist(
        IConfiguration configuration,
        IMemoryCache cache,
        DbConnectionFactory db,
        ILogger<DatasetAllowlist> logger)
    {
        _cache = cache;
        _db = db;
        _logger = logger;

        var configured = configuration
            .GetSection("DatasetManager:AllowedTables")
            .Get<string[]>() ?? [];

        // Strip denylist entries regardless of config
        _effectiveAllowlist = configured
            .Where(t => !InternalDenylist.Contains(t))
            .ToList()
            .AsReadOnly();

        if (_effectiveAllowlist.Count < configured.Length)
        {
            var stripped = configured.Where(t => InternalDenylist.Contains(t)).ToArray();
            logger.LogWarning(
                "DatasetManager:AllowedTables contains internal table names that were stripped: {Tables}",
                string.Join(", ", stripped));
        }
    }

    public bool IsAllowed(string tableName) =>
        _effectiveAllowlist.Contains(tableName, StringComparer.Ordinal);

    public async Task<CatalogDto> GetCatalogAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(CacheKey, out CatalogDto? cached) && cached is not null)
            return cached;

        var result = await BuildCatalogAsync(ct).ConfigureAwait(false);
        _cache.Set(CacheKey, result, CacheTtl);
        return result;
    }

    private async Task<CatalogDto> BuildCatalogAsync(CancellationToken ct)
    {
        if (_effectiveAllowlist.Count == 0)
            return new CatalogDto([]);

        using var conn = _db.Create();
        await conn.OpenAsync(ct).ConfigureAwait(false);

        // Dapper does not support array parameters directly in all drivers;
        // pass as a comma-joined literal within an IN clause is SQL injection risk.
        // Instead, use Npgsql's typed array binding via an explicit ANY(@allowlist).
        const string sql = """
            SELECT table_name   AS "TableName",
                   column_name  AS "ColumnName",
                   data_type    AS "PgType",
                   is_nullable  AS "IsNullable"
            FROM   information_schema.columns
            WHERE  table_schema = 'public'
              AND  table_name   = ANY(@allowlist)
            ORDER  BY table_name, ordinal_position
            """;

        var rows = await conn.QueryAsync<(string TableName, string ColumnName, string PgType, string IsNullable)>(
            new CommandDefinition(sql,
                parameters: new { allowlist = _effectiveAllowlist.ToArray() },
                cancellationToken: ct))
            .ConfigureAwait(false);

        var tables = rows
            .GroupBy(r => r.TableName, StringComparer.Ordinal)
            .Select(g => new CatalogTableDto(
                g.Key,
                g.Select(r => new CatalogColumnDto(
                    r.ColumnName,
                    r.PgType,
                    string.Equals(r.IsNullable, "YES", StringComparison.OrdinalIgnoreCase)))
                 .ToList()))
            .ToList();

        // Preserve config order: tables present in allowlist but absent from info schema
        // are logged as a warning (supports pre-migration deploys, per AR-62).
        var found = tables.Select(t => t.TableName).ToHashSet(StringComparer.Ordinal);
        foreach (var t in _effectiveAllowlist.Where(t => !found.Contains(t)))
        {
            _logger.LogWarning(
                "DatasetManager:AllowedTables entry '{Table}' was not found in information_schema.tables (schema=public). " +
                "This may be a pre-migration state; the table will be absent from the catalog.",
                t);
        }

        return new CatalogDto(tables);
    }
}
```

- [x] Register as **singleton** in `Program.cs`:
  ```csharp
  // Story 9.1 (FR-63 / AR-62) — config-backed allowlist + 5-min catalog cache.
  builder.Services.AddSingleton<IDatasetAllowlist, DatasetAllowlist>();
  ```
  Place AFTER the `builder.Services.AddSingleton<ISchemaRegistry, SchemaRegistry>();` line (roughly line 211) to keep DI registrations grouped by feature area.

---

### Task 2 — Backend: Create `CatalogDto.cs` (AC-1)

Create `src/FormForge.Api/Features/Datasets/Dtos/CatalogDto.cs`:

```csharp
namespace FormForge.Api.Features.Datasets.Dtos;

// Story 9.1 (FR-63 / AR-62): GET /api/datasets/catalog response shape.
// C# naming: PascalCase properties → camelCase JSON via default serializer options.
internal sealed record CatalogColumnDto(string ColumnName, string PgType, bool IsNullable);
internal sealed record CatalogTableDto(string TableName, IReadOnlyList<CatalogColumnDto> Columns);
internal sealed record CatalogDto(IReadOnlyList<CatalogTableDto> Tables);
```

---

### Task 3 — Backend: Add `GET /api/datasets/catalog` endpoint (AC-1, AC-6)

Open `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs`.

**CRITICAL ordering**: the `/catalog` route must be registered **BEFORE** the `/{id:guid}` route. Currently, `/preview` is registered before POST `/`, and `/{id:guid}` is the second route. Insert the new catalog route after the existing `MapGet("/{id:guid}", ...)` block and **before** the `MapPost("/preview", ...)` block:

```csharp
// Story 9.1 (FR-63 / AR-62) — allowlisted tables + columns; requires dataset-management.
// Registered BEFORE /preview and MapPost("/") to avoid any static-segment-vs-guid ambiguity.
group.MapGet("/catalog", async (
    IDatasetAllowlist allowlist,
    CancellationToken ct) =>
{
    var catalog = await allowlist.GetCatalogAsync(ct).ConfigureAwait(false);
    return Results.Ok(catalog);
})
     .WithSummary("Get allowlisted tables and columns (Story 9.1 — FR-63 / AR-62)")
     .RequireDatasetManagement();
```

Add the necessary using: `IDatasetAllowlist` is in `FormForge.Api.Features.Datasets` — already in the file's namespace so no extra using is needed.

---

### Task 4 — Backend: Configure `DatasetManager:AllowedTables` in `appsettings.json` (AC-1, AC-6)

Open `src/FormForge.Api/appsettings.json`. Add the `DatasetManager` section after `"Smtp": { ... }`:

```json
"DatasetManager": {
  "AllowedTables": [],
  "PreviewTimeoutSeconds": 5
}
```

The default empty array means zero tables are allowlisted out of the box — operators must populate this intentionally, per AR-62 (no DB admin UI in v1). Dev/test overrides go in `appsettings.Development.json` or via env vars (`DatasetManager__AllowedTables__0=table_name`).

---

### Task 5 — Backend: Integration test for catalog endpoint (AC-1, AC-6)

Add `GetCatalog_ReturnsAllowlistedTables` to `src/FormForge.Api.Tests/Features/Datasets/` — follow the pattern in `DatasetMigrationTests.cs` (Testcontainers + WebApplicationFactory). The test should:
- Configure `DatasetManager:AllowedTables` in test appsettings (with at least one known table, e.g., `custom_dataset` stripped by denylist → expect 0 tables; or a test-specific table)
- Call `GET /api/datasets/catalog` with a `dataset-management` token → 200 with expected shape
- Verify a non-allowlisted table is absent from the response

---

### Task 6 — Frontend: Install `@xyflow/react` v12 (AC-4, AC-5)

```bash
cd web && npm install @xyflow/react
```

`@xyflow/react` is the React Flow v12+ package name (replaces legacy `reactflow`). Verify `package.json` gets `"@xyflow/react": "^12.x.x"`. Do NOT install the old `reactflow` package — they are mutually exclusive (AR-68 explicitly says `@xyflow/react v12`).

---

### Task 7 — Frontend: Create `BuilderState` type definitions (AC-4)

Create `web/src/features/datasets/types/builderState.ts` — the **canonical cross-layer contract** (AR-67):

```typescript
// AR-67: Canonical BuilderState interface. This file is the single source of truth
// for the builder_state JSON schema stored in custom_dataset.builder_state (JSONB).
// C# BuilderStateDto mirrors this exactly (Decision 6.11).
// Epic 10 adds column selection, aggregates, filters, ORDER BY, CASE, and calculated
// columns — extend only here and in the C# mirror together.

export type JoinType = 'INNER' | 'LEFT' | 'RIGHT' | 'FULL OUTER'
export type TableSide = 'left' | 'right'
export type AggregateFunction = 'none' | 'COUNT' | 'SUM' | 'AVG' | 'MIN' | 'MAX'

// Per-column state on a TableNode (Epic 10 fills in checked/aggregate/alias)
export interface ColumnSelection {
  columnName: string
  checked: boolean       // Epic 10: Story 10.1
  aggregate: AggregateFunction  // Epic 10: Story 10.2
  alias: string          // Epic 10: Story 10.2
}

export interface TableNodeData {
  tableName: string
  side: TableSide        // Story 9.5: designation control
  columns: ColumnSelection[]
}

// React Flow node shape for a table
export interface TableNodeState {
  id: string
  type: 'tableNode'
  position: { x: number; y: number }
  data: TableNodeData
}

export interface JoinEdgeData {
  sourceColumn: string   // column name on source node
  targetColumn: string   // column name on target node
  joinType: JoinType     // Story 9.4: defaults to INNER
}

// React Flow edge shape for a join
export interface JoinEdgeState {
  id: string
  source: string         // node id
  target: string         // node id
  sourceHandle: string   // column name (Story 9.3)
  targetHandle: string   // column name (Story 9.3)
  type: 'joinEdge'
  data: JoinEdgeData
}

// Epic 10 stubs — defined now to complete the contract; populated later
export interface FilterCondition {
  tableName: string
  columnName: string
  operator: string
  value: string | null
}

export interface FilterGroup {
  combinator: 'AND' | 'OR'
  conditions: FilterCondition[]
  groups: FilterGroup[]
}

export interface OrderByClause {
  tableName: string
  columnName: string
  direction: 'ASC' | 'DESC'
}

export interface CaseColumn {
  alias: string
  // Epic 10 Story 10.3 — WHEN/THEN/ELSE arms added
}

export interface CalculatedColumn {
  alias: string
  expression: string  // Epic 10 Story 10.4
}

export interface BuilderState {
  nodes: TableNodeState[]
  edges: JoinEdgeState[]
  filters: FilterGroup
  orderBy: OrderByClause[]
  caseColumns: CaseColumn[]
  calculatedColumns: CalculatedColumn[]
}

export const EMPTY_BUILDER_STATE: BuilderState = {
  nodes: [],
  edges: [],
  filters: { combinator: 'AND', conditions: [], groups: [] },
  orderBy: [],
  caseColumns: [],
  calculatedColumns: [],
}

// Parse builder_state from the JSON string stored in DatasetDetail.builderState
export function parseBuilderState(raw: string | null): BuilderState {
  if (!raw) return EMPTY_BUILDER_STATE
  try {
    return JSON.parse(raw) as BuilderState
  } catch {
    return EMPTY_BUILDER_STATE
  }
}
```

---

### Task 8 — Frontend: Add `getCatalog` to `datasetApi.ts` (AC-1, AC-2)

Open `web/src/features/datasets/datasetApi.ts`. Add the catalog types and function:

```typescript
// Story 9.1 (FR-63 / AR-62) — catalog response mirrors CatalogDto.cs
export interface CatalogColumn {
  columnName: string
  pgType: string
  isNullable: boolean
}

export interface CatalogTable {
  tableName: string
  columns: CatalogColumn[]
}

export interface CatalogResponse {
  tables: CatalogTable[]
}

export function getCatalog(): Promise<CatalogResponse> {
  return httpClient.get<CatalogResponse>('/api/datasets/catalog')
}
```

---

### Task 9 — Frontend: Create `useCatalogQuery.ts` (AC-1, AC-2)

Create `web/src/features/datasets/useCatalogQuery.ts`:

```typescript
import { useQuery } from '@tanstack/react-query'
import { getCatalog } from './datasetApi'
import type { CatalogResponse } from './datasetApi'

// AR-69 Decision 6.13 — catalog query key
export const CATALOG_QUERY_KEY = ['datasets', 'catalog'] as const

export function useCatalogQuery() {
  return useQuery<CatalogResponse>({
    queryKey: CATALOG_QUERY_KEY,
    queryFn: getCatalog,
    staleTime: 5 * 60 * 1000,  // 5 min — mirrors backend cache TTL (AR-62)
  })
}
```

---

### Task 10 — Frontend: Create `TableNode.tsx` (AC-4, AC-5)

Create `web/src/components/query-builder/TableNode.tsx`:

```tsx
import { memo } from 'react'
import { Handle, Position, type NodeProps } from '@xyflow/react'
import type { TableNodeData, ColumnSelection } from '../../features/datasets/types/builderState'

// Story 9.1: Structural shell. Left/Right toggle is visual-only (Story 9.5 wires it).
// Column checkboxes are visible but non-interactive (Story 10.1 adds checked behavior).
export const TableNode = memo(function TableNode({ data, selected }: NodeProps<{ data: TableNodeData }>) {
  return (
    <div
      className={`min-w-[180px] rounded-lg border bg-card text-card-foreground shadow-sm ${
        selected ? 'ring-2 ring-ring' : ''
      }`}
    >
      {/* Header */}
      <div className="flex items-center justify-between rounded-t-lg bg-muted px-3 py-2">
        <span className="truncate text-sm font-semibold">{data.tableName}</span>
        {/* Left/Right designation — Story 9.5 makes this interactive */}
        <span className="ml-2 shrink-0 rounded px-1.5 py-0.5 text-xs font-medium bg-background text-muted-foreground">
          {data.side === 'left' ? 'Left' : 'Right'}
        </span>
      </div>

      {/* Column list — checkboxes non-interactive until Story 10.1 */}
      <div className="divide-y divide-border">
        {data.columns.map((col) => (
          <div key={col.columnName} className="relative flex items-center gap-2 px-3 py-1.5">
            {/* Source handle (left edge) — used in Story 9.3 for join edges */}
            <Handle
              type="source"
              position={Position.Left}
              id={col.columnName}
              className="!h-2 !w-2 !border-border !bg-muted-foreground"
            />
            <input
              type="checkbox"
              checked={col.checked}
              readOnly
              disabled
              className="h-3.5 w-3.5 cursor-not-allowed opacity-60"
            />
            <span className="min-w-0 flex-1 truncate text-xs">{col.columnName}</span>
            <span className="shrink-0 text-xs text-muted-foreground">{col.pgType}</span>
            {/* Target handle (right edge) — used in Story 9.3 for join edges */}
            <Handle
              type="target"
              position={Position.Right}
              id={col.columnName}
              className="!h-2 !w-2 !border-border !bg-muted-foreground"
            />
          </div>
        ))}
      </div>
    </div>
  )
})
```

**Important**: `NodeProps` generic in `@xyflow/react` v12 is `NodeProps<NodeType>` where `NodeType` extends `Record<string, unknown>`. Use `{ data: TableNodeData }` as the type argument.

The `Handle` component renders connection points for join edges (Stories 9.3–9.4). In Story 9.1, they are rendered but not connected to anything — this is correct and intentional.

---

### Task 11 — Frontend: Create `TablePalette.tsx` (AC-2, AC-3, AC-4)

Create `web/src/features/datasets/TablePalette.tsx`:

```tsx
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Input } from '@/components/ui/input'
import { useCatalogQuery } from './useCatalogQuery'
import type { CatalogTable } from './datasetApi'

interface TablePaletteProps {
  // onDragStart tells the parent (canvas) what table is being dragged
  // so the canvas onDrop can create the node with the correct data.
}

function PaletteEntry({ table }: { table: CatalogTable }) {
  const handleDragStart = (e: React.DragEvent<HTMLDivElement>) => {
    // Serialize the full table (name + columns) so the canvas drop handler
    // can build the TableNodeData directly from dataTransfer.
    e.dataTransfer.setData('application/formforge-table', JSON.stringify(table))
    e.dataTransfer.effectAllowed = 'copy'
  }

  return (
    <div
      draggable
      onDragStart={handleDragStart}
      className="cursor-grab rounded border border-border bg-card px-3 py-2 hover:bg-accent active:cursor-grabbing"
    >
      <div className="text-sm font-medium">{table.tableName}</div>
      <div className="mt-0.5 text-xs text-muted-foreground">
        {table.columns.length} column{table.columns.length !== 1 ? 's' : ''}
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
    ? tables.filter(t => t.tableName.toLowerCase().includes(search.toLowerCase()))
    : tables

  return (
    <aside className="flex h-full w-56 flex-col border-r border-border bg-background">
      <div className="p-3">
        <p className="mb-2 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
          {t('datasets.builder.palette.title')}
        </p>
        <Input
          placeholder={t('datasets.builder.palette.searchPlaceholder')}
          value={search}
          onChange={e => setSearch(e.target.value)}
          className="h-8 text-sm"
        />
      </div>

      <div className="flex-1 overflow-y-auto px-3 pb-3">
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
        <div className="flex flex-col gap-2">
          {filtered.map(table => (
            <PaletteEntry key={table.tableName} table={table} />
          ))}
        </div>
      </div>
    </aside>
  )
}
```

---

### Task 12 — Frontend: Create `QueryBuilderCanvas.tsx` (AC-4, AC-5)

Create `web/src/features/datasets/QueryBuilderCanvas.tsx`:

```tsx
import { useCallback, useRef } from 'react'
import {
  ReactFlow,
  Background,
  Controls,
  MiniMap,
  useNodesState,
  useEdgesState,
  useReactFlow,
  ReactFlowProvider,
  type Node,
  type Edge,
  type OnConnect,
} from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import { TableNode } from '../../components/query-builder/TableNode'
import type { BuilderState, TableNodeData, TableNodeState } from './types/builderState'
import type { CatalogTable } from './datasetApi'

// Register custom node types — object must be stable (defined outside component)
const nodeTypes = { tableNode: TableNode }

interface QueryBuilderCanvasProps {
  initialState: BuilderState
  onChange: (state: BuilderState) => void
}

// Inner component — must be wrapped by ReactFlowProvider (see export below)
function QueryBuilderCanvasInner({ initialState, onChange }: QueryBuilderCanvasProps) {
  const [nodes, setNodes, onNodesChange] = useNodesState<Node>(
    initialState.nodes as Node[]
  )
  const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>(
    initialState.edges as Edge[]
  )
  const reactFlowWrapper = useRef<HTMLDivElement>(null)
  const { screenToFlowPosition } = useReactFlow()

  const onDragOver = useCallback((event: React.DragEvent) => {
    event.preventDefault()
    event.dataTransfer.dropEffect = 'copy'
  }, [])

  const onDrop = useCallback(
    (event: React.DragEvent) => {
      event.preventDefault()

      const raw = event.dataTransfer.getData('application/formforge-table')
      if (!raw) return

      const table: CatalogTable = JSON.parse(raw)

      // Convert drag coordinates to canvas coordinates
      const position = screenToFlowPosition({
        x: event.clientX,
        y: event.clientY,
      })

      // Unique id: tableName + timestamp ensures distinct nodes even for same table
      const newNodeId = `${table.tableName}-${Date.now()}`

      const newNodeData: TableNodeData = {
        tableName: table.tableName,
        // First node defaults to 'left', subsequent to 'right' (Story 9.5 behavior;
        // for now derive from existing nodes count as a sensible default).
        side: nodes.length === 0 ? 'left' : 'right',
        columns: table.columns.map(col => ({
          columnName: col.columnName,
          checked: false,
          aggregate: 'none',
          alias: '',
        })),
      }

      const newNode: Node = {
        id: newNodeId,
        type: 'tableNode',
        position,
        data: newNodeData,
      }

      const updatedNodes = [...nodes, newNode]
      setNodes(updatedNodes)

      // Notify parent so it can track state (for builder_state persistence in Story 11.2)
      onChange({
        ...initialState,
        nodes: updatedNodes as TableNodeState[],
        edges: edges as never,
      })
    },
    [nodes, edges, screenToFlowPosition, setNodes, onChange, initialState],
  )

  return (
    <div ref={reactFlowWrapper} className="h-full w-full">
      <ReactFlow
        nodes={nodes}
        edges={edges}
        nodeTypes={nodeTypes}
        onNodesChange={onNodesChange}
        onEdgesChange={onEdgesChange}
        onDragOver={onDragOver}
        onDrop={onDrop}
        fitView
      >
        <Background />
        <Controls />
        <MiniMap />
      </ReactFlow>
    </div>
  )
}

// Wrap with ReactFlowProvider so useReactFlow() works inside the inner component
export function QueryBuilderCanvas(props: QueryBuilderCanvasProps) {
  return (
    <ReactFlowProvider>
      <QueryBuilderCanvasInner {...props} />
    </ReactFlowProvider>
  )
}
```

**`@xyflow/react` v12 import notes:**
- Import CSS: `import '@xyflow/react/dist/style.css'` is **required** — without it the canvas renders incorrectly (nodes overlap, minimap missing)
- `useNodesState` / `useEdgesState` are the v12 state management hooks
- `screenToFlowPosition` (from `useReactFlow()`) replaces the v10 `project()` for coordinate conversion on drop
- `ReactFlowProvider` is required when `useReactFlow()` is called inside the component — wrapping ensures the context exists

---

### Task 13 — Frontend: Create `datasets.$id.tsx` route (AC-2, AC-4)

Create `web/src/routes/_app/admin/datasets.$id.tsx`:

```tsx
import { useState, useCallback } from 'react'
import { createFileRoute, Link } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { ChevronLeft, Database } from 'lucide-react'
import { getDataset } from '../../../features/datasets/datasetApi'
import { QueryBuilderCanvas } from '../../../features/datasets/QueryBuilderCanvas'
import { TablePalette } from '../../../features/datasets/TablePalette'
import { parseBuilderState, EMPTY_BUILDER_STATE } from '../../../features/datasets/types/builderState'
import type { BuilderState } from '../../../features/datasets/types/builderState'

export const Route = createFileRoute('/_app/admin/datasets/$id')({
  loader: async ({ params }) => {
    const dataset = await getDataset(params.id)
    return { dataset }
  },
  component: DatasetBuilderPage,
})

function DatasetBuilderPage() {
  const { t } = useTranslation()
  const { dataset } = Route.useLoaderData()
  const [builderState, setBuilderState] = useState<BuilderState>(
    parseBuilderState(dataset.builderState)
  )

  const handleStateChange = useCallback((state: BuilderState) => {
    setBuilderState(state)
    // Story 11.2 will persist builderState via PUT on save; for now just hold in state
  }, [])

  return (
    <div className="flex h-screen flex-col">
      {/* Page header */}
      <header className="flex items-center gap-3 border-b border-border bg-background px-4 py-3">
        <Link
          to="/admin/datasets"
          className="flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground"
        >
          <ChevronLeft className="h-4 w-4" />
          {t('admin.datasets.navTitle')}
        </Link>
        <span className="text-muted-foreground">/</span>
        <div className="flex items-center gap-2">
          <Database className="h-4 w-4" />
          <span className="font-medium">{dataset.datasetName}</span>
        </div>
      </header>

      {/* Canvas area */}
      <div className="flex flex-1 overflow-hidden">
        <TablePalette />
        <main className="flex-1 overflow-hidden">
          <QueryBuilderCanvas
            initialState={builderState}
            onChange={handleStateChange}
          />
        </main>
      </div>
    </div>
  )
}
```

**Route loader pattern**: The TanStack Router `loader` pre-fetches the dataset via `getDataset(params.id)` before the component renders. This is the same pattern used in `menus.$menuId.tsx` — read that file for reference if the loader pattern is unclear.

**Note**: This route only renders the Query Builder canvas (not the Custom Query textarea). The existing `datasets.tsx` handles Custom Query mode editing inline. The `datasets.$id.tsx` route is exclusively for Query Builder mode. Navigation to this route is handled in Task 14.

---

### Task 14 — Frontend: Navigate to builder route from `datasets.tsx` (AC-2)

Open `web/src/routes/_app/admin/datasets.tsx`.

In the `DatasetRow` component, locate the `handleEdit` function. Add a branch: when the dataset is NOT `isCustomQuery`, navigate to `/admin/datasets/{id}` instead of fetching the detail and opening the inline edit form:

```tsx
// Inside DatasetRow, alongside the existing handleEdit:
const navigate = useNavigate({ from: '/admin/datasets' })

const handleOpenBuilder = useCallback(() => {
  void navigate({ to: '/admin/datasets/$id', params: { id: row.id } })
}, [navigate, row.id])
```

In the row's action buttons, replace the Edit button for Query Builder mode datasets:

```tsx
{row.isCustomQuery ? (
  <Button variant="outline" size="sm" onClick={handleEdit} disabled={editLoading || isEditFetching}>
    {editLoading ? <span className="animate-spin">…</span> : t('admin.datasets.editButton')}
  </Button>
) : (
  <Button variant="outline" size="sm" onClick={handleOpenBuilder}>
    {t('admin.datasets.openBuilderButton')}
  </Button>
)}
```

Add the i18n key `admin.datasets.openBuilderButton` in Task 15.

**Import**: `useNavigate` is already imported (`import { createFileRoute, useNavigate } from '@tanstack/react-router'`) — verify before adding.

---

### Task 15 — Frontend: Add i18n keys (AC-2, AC-3, AC-4)

Open `web/src/lib/i18n/locales/en.json`. Find the `"admin": { "datasets": { ... } }` section and add `openBuilderButton`:

```json
"openBuilderButton": "Open Builder"
```

Add a new top-level section `"datasets": { "builder": { ... } }` — this is distinct from `"admin.datasets.*"` (which is for the admin list page):

```json
"builder": {
  "palette": {
    "title": "Tables",
    "searchPlaceholder": "Search tables…",
    "loading": "Loading tables…",
    "loadError": "Failed to load tables.",
    "empty": "No tables configured.",
    "noMatches": "No tables match your search."
  }
}
```

Add this inside the existing `"datasets"` top-level object (which already has `"validation"`, `"sqlTextarea"`, `"audit"` keys from Stories 8.3, 8.8, 8.9):

```json
"datasets": {
  ...,
  "builder": { ... }   // add this
}
```

---

### Task 16 — Frontend: Build and test verification

- [x] `cd web && npm install` (picks up `@xyflow/react` from package.json after Task 6) — `@xyflow/react@^12.11.0` installed
- [x] `cd web && npx tsc -b --noEmit` → 0 errors
- [x] `cd web && npx vitest run` → 281 passed, 1 pre-existing i18n-lint failure (`designer.inspector.placeholders.label`); no new failures, new keys not orphaned
- [x] `dotnet build src/FormForge.Api` → 0 errors
- [x] `dotnet test` → 918 passed, 2 pre-existing failures unchanged (SchemaAuditLog / MutationAuditLog DELETE→405)
- [x] Manual smoke — **not run as a live browser session in this non-interactive environment.** Equivalent coverage: AC-1 + AC-6 are exercised end-to-end by the new `DatasetCatalogTests` (catalog shape, columns/types, allowlist filtering, denylist stripping, non-allowlisted absent, 401 without token), and the full Query Builder UI (palette → canvas → TableNode) type-checks and builds clean. Live drag-to-canvas verification is left for code review.

---

## Dev Notes

### §1 — Scope Boundaries for This Story

This story delivers:
1. **Backend**: `DatasetAllowlist.cs` singleton + `GET /api/datasets/catalog` endpoint
2. **Frontend**: `@xyflow/react` dependency + `BuilderState` type + `TablePalette` + `TableNode` + `QueryBuilderCanvas` + `datasets.$id.tsx` route

**Deferred to other stories:**
- Left/Right designation behavior → Story 9.5
- Column checkboxes → Story 10.1
- Join edges → Stories 9.3, 9.4
- Node deletion and edge cascade cleanup → Story 9.2
- `builder_state` persistence on save (PUT) → Story 11.2
- SQL generation → Story 11.1
- Preview execution → Story 11.3

The `TableNode` renders the Left/Right toggle and column checkboxes **visually** but they are non-interactive. Story 9.5 wires the toggle; Story 10.1 wires the checkboxes.

### §2 — `@xyflow/react` v12 Key APIs

| Need | API |
|---|---|
| State management | `useNodesState()`, `useEdgesState()` |
| Coordinate conversion on drop | `useReactFlow().screenToFlowPosition()` |
| Custom node registration | `nodeTypes` object passed to `<ReactFlow nodeTypes={...} />` |
| Provider requirement | `<ReactFlowProvider>` wraps any component calling `useReactFlow()` |
| CSS | `import '@xyflow/react/dist/style.css'` **required** in canvas file |
| Handles for join edges | `<Handle type="source|target" position={...} id={columnName} />` |

**DO NOT import from `reactflow`** (old v10 package). The project uses `@xyflow/react` (v12).

Node type in `useNodesState<Node>` uses the generic `Node` from `@xyflow/react` — this is correct for v12; the custom data is carried in `node.data` typed via `NodeProps<{ data: TableNodeData }>`.

### §3 — Catalog Endpoint Registration Order

The endpoint order in `DatasetEndpoints.cs` matters for ASP.NET Minimal API routing. Confirm the order after Task 3:

1. `GET /` — list (auth only)
2. `GET /{id:guid}` — single by ID (auth only)
3. `GET /catalog` — ← **new** (dataset-management)
4. `POST /preview` — stub (dataset-management)
5. `POST /` — create (dataset-management)
6. `PUT /{id:guid}` — update (dataset-management)
7. `DELETE /{id:guid}` — delete (dataset-management)

Route `/catalog` is a static literal segment and `/preview` is also static — both must be registered before any `/{id:guid}` catch-all in the same prefix. Check that the guid constraint on route 2 (`/{id:guid}`) prevents it from matching "catalog" or "preview" (it does — `{id:guid}` only matches valid GUID strings), but registering statics first is defense-in-depth.

### §4 — `DatasetAllowlist` as Singleton vs Scoped

`DatasetAllowlist` must be registered as a **singleton** because:
1. The effective allowlist is derived once at startup from `IConfiguration`
2. `IMemoryCache` is itself a singleton — no scoping benefit
3. `DbConnectionFactory` is a singleton (see Program.cs line ~210)

Do NOT register it as `Scoped` — `DbConnectionFactory` is a singleton, injecting a singleton into a scoped service violates DI lifetime rules and causes runtime errors.

### §5 — `builderState` Round-Trip on Open

When `datasets.$id.tsx` opens, it reads `dataset.builderState` (a JSON string or null) and passes it through `parseBuilderState()`. If null, the canvas starts empty. If the JSON can't be parsed (schema change, corrupt data), `parseBuilderState()` returns `EMPTY_BUILDER_STATE` silently — this prevents a blank page on corrupt data.

The canvas `onChange` callback receives the updated `BuilderState` on every node/edge change. For Story 9.1, this is only tracked in local component state. Story 11.2 will wire it to `PUT /api/datasets/{id}` on a Save button.

### §6 — `datasets.$id.tsx` Route and TanStack Router File-Based Routing

File name `datasets.$id.tsx` maps to route `/_app/admin/datasets/$id` under the `_app` layout. The `$id` segment is a path parameter: `Route.useParams().id`.

**The route uses a `loader`**, which pre-fetches the dataset before the component renders. This requires `getDataset(params.id)` to be importable synchronously. It is — `getDataset` is exported from `datasetApi.ts`.

After creating this file, the TanStack Router plugin regenerates `routeTree.gen.ts` automatically on next `vite dev` or `vite build` run. Do not manually edit `routeTree.gen.ts`.

**Dot-nested parent layout gotcha (from memory)**: `datasets.tsx` and `datasets.$id.tsx` form a nested route pair. TanStack Router's file-based convention treats `datasets.tsx` as the parent layout for `datasets.$id.tsx`. This means if `datasets.tsx` does NOT render `<Outlet />`, the child `datasets.$id.tsx` will be swallowed. Check if `datasets.tsx` renders `<Outlet />` — it currently does NOT (it's a full standalone page). To avoid the nested layout, rename the route to `datasets_.$id.tsx` (underscore breaks the nesting in TanStack Router v1 file-based routing), OR add `<Outlet />` to `datasets.tsx`.

**Recommended**: use `datasets_.$id.tsx` as the file name (with underscore before the dot) to opt out of the parent-layout nesting. This matches the TanStack Router convention for "pathless" parent avoidance.

Alternatively: verify TanStack Router's behavior in this project by checking how `menus.$menuId.tsx` is structured relative to `menus.tsx`. If `menus.tsx` renders `<Outlet />`, follow the same pattern. If not, use the underscore convention.

### §7 — Dapper Array Binding in DatasetAllowlist

The `information_schema.columns` query uses `table_name = ANY(@allowlist)` where `@allowlist` is a `string[]`. In Dapper with Npgsql, array parameters work when the parameter type maps to a PostgreSQL array type. Pass it as:

```csharp
parameters: new { allowlist = _effectiveAllowlist.ToArray() }
```

Npgsql resolves `string[]` to `TEXT[]` automatically. If the query fails at runtime with "could not determine data type of parameter", cast explicitly: `@allowlist::text[]` in the SQL.

### §8 — `@xyflow/react` CSS Import Location

`import '@xyflow/react/dist/style.css'` must be in `QueryBuilderCanvas.tsx` (or a file that imports it before the canvas renders). Placing it in `QueryBuilderCanvas.tsx` is correct. Do NOT place it in `index.css` via a `@import` if the Vite CSS pipeline strips unused CSS — the safe path is the direct JS import in the canvas file.

### §9 — Test State After This Story

**Backend** (`dotnet test`): 913 passed + new catalog tests → total increases. 2 pre-existing failures unchanged.
**Frontend** (`npx vitest run`): 281 passed baseline. New catalog query hook can be tested with a mocked `httpClient`. No new test files are strictly required for the story gates but a basic `useCatalogQuery` test following the pattern in `useDatasetListQuery` is good practice. i18n-lint: the new `datasets.builder.*` keys must be present in `en.json` and consumed in `TablePalette.tsx` — verify no new missing-key lint errors.

### §10 — Files Created / Modified

```
NEW (backend):
  src/FormForge.Api/Features/Datasets/DatasetAllowlist.cs
  src/FormForge.Api/Features/Datasets/Dtos/CatalogDto.cs

MODIFIED (backend):
  src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs
    — add GET /catalog handler (before /preview)
  src/FormForge.Api/Program.cs
    — register IDatasetAllowlist singleton
  src/FormForge.Api/appsettings.json
    — add DatasetManager section

NEW (frontend):
  web/src/features/datasets/types/builderState.ts
  web/src/features/datasets/useCatalogQuery.ts
  web/src/features/datasets/TablePalette.tsx
  web/src/features/datasets/QueryBuilderCanvas.tsx
  web/src/components/query-builder/TableNode.tsx
  web/src/routes/_app/admin/datasets_.$id.tsx  (NOTE underscore — see §6)

MODIFIED (frontend):
  web/src/features/datasets/datasetApi.ts
    — add getCatalog(), CatalogColumn, CatalogTable, CatalogResponse types
  web/src/routes/_app/admin/datasets.tsx
    — add "Open Builder" button/navigation for isCustomQuery=false rows
  web/src/lib/i18n/locales/en.json
    — add admin.datasets.openBuilderButton, datasets.builder.palette.*
  web/package.json
    — @xyflow/react dependency (via npm install)
  web/src/routeTree.gen.ts
    — regenerated by TanStack Router plugin (adds datasets_/$id route)
```

### §11 — References

- [Source: `_bmad-output/planning-artifacts/epics.md` — Epic 9 Story 9.1 ACs (FR-63 AC-1 through AC-6)]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.6 — Table allowlist, catalog endpoint, CatalogDto shape, IMemoryCache TTL]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.9 — Dataset API contract, GET /catalog]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.11 — AR-67 BuilderState canonical interface]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.12 — AR-68 React Flow @xyflow/react v12 integration]
- [Source: `src/FormForge.Api/Domain/ValueTypes/DatasetName.cs` — PermanentDenylist (14 internal tables to strip from allowlist)]
- [Source: `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs` — existing endpoint order to maintain]
- [Source: `src/FormForge.Api/Program.cs:210-254` — DI registration patterns for new services]
- [Source: `web/src/features/datasets/datasetApi.ts` — httpClient import path and API function pattern]
- [Source: `web/src/features/datasets/useDatasetListQuery.ts` — TanStack Query v5 hook pattern]
- [Source: `web/src/routes/_app/admin/datasets.tsx` — existing DatasetRow component to extend with builder nav]
- [Source: Memory: Dot-nested parent layouts need an Outlet — `datasets.tsx` does NOT render `<Outlet />`, so use `datasets_.$id.tsx` (underscore) to avoid swallowed child route]
- [Source: Story 8.10 dev notes §11 — file structure and patterns established]
- [Source: AR-69 Decision 6.13 — TanStack Query key `['datasets', 'catalog']`]

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (Opus 4.8, 1M context)

### Debug Log References

- Backend strict analyzers (warnings-as-errors) required adapting the spec's `DatasetAllowlist`:
  `DbConnectionFactory` exposes `CreateOpenConnectionAsync(ct)` (not `.Create()`); used a private
  `CatalogRow` record instead of a value tuple (project does not set Dapper `MatchNamesWithUnderscores`,
  so PascalCase column aliases map by name); converted to a `partial` class with source-generated
  `[LoggerMessage]` delegates (CA1848); manual `try/finally` disposal instead of `await using` (CA2007);
  `ReadOnlyCollection<string>` field type (CA1859); local `#pragma` suppression of CA1873 on the
  once-at-startup `string.Join` guarded by `IsEnabled`.
- `@xyflow/react` v12 `NodeProps<NodeType>` is keyed on the **Node type**, not `{ data }` — declared
  `TableNodeType = Node<TableNodeData, 'tableNode'>`. v12's `Node<NodeData extends Record<string, unknown>>`
  constraint required `TableNodeData`/`JoinEdgeData` to be `type` aliases (implicit index signature) rather
  than `interface`. Canvas edge state starts empty (`[]`) since Story 9.1 has no edge support.
- Added `pgType` to `ColumnSelection` — the spec's `TableNode` renders `col.pgType` but the spec type omitted it.
- Route uses the `datasets_.$id.tsx` underscore convention to opt out of the `datasets.tsx` parent layout
  (which has no `<Outlet />`); regenerated `routeTree.gen.ts` resolves it to fullPath `/admin/datasets/$id`.

### Completion Notes List

- **AC-1** — `GET /api/datasets/catalog` returns `{ tables: [{ tableName, columns: [{ columnName, pgType, isNullable }] }] }`
  from `information_schema.columns` (schema `public`, `table_name = ANY(@allowlist)`), cached in `IMemoryCache`
  for 5 min. Requires `dataset-management`. Verified by `DatasetCatalogTests`.
- **AC-2 / AC-3 / AC-4 / AC-5** — Query Builder canvas route renders the searchable Table Palette (left) and a
  React Flow (`@xyflow/react` v12) canvas; dragging a palette entry drops a `TableNode` (header, Left/Right
  placeholder badge, non-interactive column checkboxes + types) with a unique `tableName-timestamp` id, so the
  same table dropped repeatedly yields distinct nodes. (UI type-checks/builds; live DnD left for review.)
- **AC-6** — Palette renders only what the server returns; the server strips the 14 internal denylist names
  (AR-57) and only emits allowlisted tables. Verified absent: a denylisted name in config and a real
  public table not in the allowlist.
- Verification: `dotnet build` 0 errors; `dotnet test` 918 passed / 2 pre-existing failures (audit DELETE→405);
  `tsc -b --noEmit` 0 errors; `vitest run` 281 passed / 1 pre-existing i18n-lint failure. No regressions.

### Review Findings

- [x] [Review][Decision] AC-2: PaletteEntry shows column count not column list — Accepted: compact count is sufficient; full column list visible in the TableNode after drop. [`web/src/features/datasets/TablePalette.tsx`]
- [x] [Review][Patch] Cache stampede in GetCatalogAsync — replaced TryGetValue/Set with `IMemoryCache.GetOrCreateAsync` [`src/FormForge.Api/Features/Datasets/DatasetAllowlist.cs:78`]
- [x] [Review][Patch] parseBuilderState unsafe cast — added shape guard checking nodes/edges are arrays before cast [`web/src/features/datasets/types/builderState.ts:106`]
- [x] [Review][Patch] ColumnSelection declared as `interface` not `type` alias — changed to `type ColumnSelection = { ... }` [`web/src/features/datasets/types/builderState.ts:12`]
- [x] [Review][Patch] Integration test missing 403 case — added viewer role/user seed + `GetCatalog_AuthenticatedWithoutDatasetManagement_Returns403` test [`src/FormForge.Api.Tests/Features/Datasets/DatasetCatalogTests.cs`]
- [x] [Review][Patch] Loader has no error handling — added `errorComponent` to route definition [`web/src/routes/_app/admin/datasets_.$id.tsx:14`]
- [x] [Review][Patch] onDrop JSON.parse unguarded — wrapped in try/catch, returns early on malformed drag data [`web/src/features/datasets/QueryBuilderCanvas.tsx:55`]
- [x] [Review][Defer] Stale initialState closure in onDrop — `onChange({ ...initialState, edges: initialState.edges })` spreads from the prop snapshot, not live state; harmless in Story 9.1 (no edges), but will silently clobber edge/filter state once Story 9.3+ edges are live; rethink in Story 11.2 when persistence is wired [`web/src/features/datasets/QueryBuilderCanvas.tsx:97`] — deferred, Story 11.2 scope
- [x] [Review][Defer] initialState.edges ignored on canvas mount — `useEdgesState<Edge>([])` always starts empty, ignoring any persisted edges; permitted by Story 9.1 scope, but will silently drop persisted edges when future stories write them [`web/src/features/datasets/QueryBuilderCanvas.tsx:39`] — deferred, Story 9.1 no-edge scope
- [x] [Review][Defer] Builder route missing isCustomQuery guard — direct URL navigation to /admin/datasets/$id loads the canvas for a custom-query dataset with no check or redirect [`web/src/routes/_app/admin/datasets_.$id.tsx`] — deferred, not in spec scope; defensive measure for a future story
- [x] [Review][Defer] Permission gap between builder loader and catalog endpoint — builder page loader (auth-only) vs catalog endpoint (dataset-management); an authed user without the permission can load the page but sees a permanent palette error with no distinction from a network failure [`web/src/features/datasets/useCatalogQuery.ts`] — deferred, route-level permission guard is a design decision
- [x] [Review][Defer] Third+ table always defaults to side:'right' — `nodes.length === 0 ? 'left' : 'right'` assigns 'right' to all tables after the first; Story 9.5 wires the actual left/right toggle [`web/src/features/datasets/QueryBuilderCanvas.tsx:71`] — deferred, Story 9.5 scope

### Change Log

| Date       | Change                                                                 |
|------------|------------------------------------------------------------------------|
| 2026-06-04 | Story 9.1 implemented: catalog endpoint + allowlist (backend) and Query Builder palette/canvas/route (frontend). Status → review. |
| 2026-06-04 | Code review complete: 1 decision-needed, 6 patch, 5 defer, 5 dismissed. |

### File List

**NEW (backend):**
- `src/FormForge.Api/Features/Datasets/DatasetAllowlist.cs`
- `src/FormForge.Api/Features/Datasets/Dtos/CatalogDto.cs`
- `src/FormForge.Api.Tests/Features/Datasets/DatasetCatalogTests.cs`

**MODIFIED (backend):**
- `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs` — add `GET /catalog` (before `/preview`)
- `src/FormForge.Api/Program.cs` — register `IDatasetAllowlist` singleton
- `src/FormForge.Api/appsettings.json` — add `DatasetManager` section

**NEW (frontend):**
- `web/src/features/datasets/types/builderState.ts`
- `web/src/features/datasets/useCatalogQuery.ts`
- `web/src/features/datasets/TablePalette.tsx`
- `web/src/features/datasets/QueryBuilderCanvas.tsx`
- `web/src/components/query-builder/TableNode.tsx`
- `web/src/routes/_app/admin/datasets_.$id.tsx`

**MODIFIED (frontend):**
- `web/src/features/datasets/datasetApi.ts` — add `getCatalog()` + catalog types
- `web/src/routes/_app/admin/datasets.tsx` — "Open Builder" navigation for Query Builder mode rows
- `web/src/lib/i18n/locales/en.json` — add `admin.datasets.openBuilderButton`, `datasets.builder.palette.*`
- `web/package.json` / `web/package-lock.json` — `@xyflow/react` dependency
- `web/src/routeTree.gen.ts` — regenerated by the TanStack Router plugin (adds `datasets_/$id` route)
