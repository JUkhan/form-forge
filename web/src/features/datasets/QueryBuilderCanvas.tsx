import { useCallback, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import {
  ReactFlow,
  Background,
  Controls,
  MiniMap,
  useNodesState,
  useEdgesState,
  useReactFlow,
  ReactFlowProvider,
  ConnectionMode,
  type NodeChange,
  type EdgeChange,
  type Connection,
  type IsValidConnection,
} from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import { useTranslation } from 'react-i18next'
import {
  TableNode,
  TableSideChangeContext,
  ColumnCheckContext,
  ColumnAggregateContext,
  ColumnAliasContext,
  CaseColumnsContext,
  AddCaseColumnContext,
  DeleteCaseColumnContext,
  SelectCaseColumnContext,
  CalculatedColumnsContext,
  AddCalculatedColumnContext,
  DeleteCalculatedColumnContext,
  SelectCalculatedColumnContext,
  type TableNodeType,
} from '../../components/query-builder/TableNode'
import { CaseColumnEditor } from '../../components/query-builder/CaseColumnEditor'
import { CalculatedColumnEditor } from '../../components/query-builder/CalculatedColumnEditor'
import {
  FilterConditionsDialog,
  seedFilterIdCounter,
} from '../../components/query-builder/FilterConditionsDialog'
import { OrderByPanel } from '../../components/query-builder/OrderByPanel'
import { JoinEdgeComponent, type JoinEdgeType } from '../../components/query-builder/JoinEdge'
import { JoinInspector } from '../../components/query-builder/JoinInspector'
import {
  getLeftTableValidationError,
  getColumnSelectionValidationError,
  getCaseColumnAliasError,
  getCalculatedColumnAliasError,
  type AggregateFunction,
  type BuilderState,
  type CalculatedColumn,
  type CaseColumn,
  type CaseWhen,
  type ColumnSelection,
  type FilterGroup,
  type JoinEdgeState,
  type JoinType,
  type OrderByClause,
  type TableNodeData,
  type TableNodeState,
  type TableSide,
} from './types/builderState'
import { getTableColumns } from './datasetApi'
import type { CatalogTable, CatalogColumn } from './datasetApi'

// Register custom node types — object must be stable (defined outside component)
const nodeTypes = { tableNode: TableNode }
const edgeTypes = { joinEdge: JoinEdgeComponent }

// Story 11.2 (FR-71 AC-2): extract the highest `{prefix}-{number}` suffix from a list of
// persisted ids so a restored canvas seeds its id counters past the saved values — the
// first newly-added CASE/calculated column then gets an id that cannot collide with a
// restored one (a duplicate React key silently dedupes and corrupts state — Dev Notes §4).
function maxSuffixFromIds(ids: string[]): number {
  return ids.reduce((max, id) => {
    const m = id.match(/-(\d+)$/)
    return m ? Math.max(max, parseInt(m[1], 10)) : max
  }, 0)
}

interface QueryBuilderCanvasProps {
  initialState: BuilderState
  onChange: (state: BuilderState) => void
}

// Story 10.4: unified floating-panel selector — avoids N² mutual-exclusivity pairs.
// Story 10.3 Dev Notes §5 prescribed this for Story 10.4. Replaces the two separate
// `selectedEdge` and `selectedCaseColumnId` states from Story 10.3.
type SelectedPanel =
  | { type: 'join'; edge: JoinEdgeType }
  | { type: 'case'; id: string }
  | { type: 'calculated'; id: string }
  | null

// Inner component — must be wrapped by ReactFlowProvider (see export below)
// Story 10.5 adds Filter Conditions Dialog + "Filter" toolbar button
//   (filters state, filtersRef, isFilterDialogOpen state,
//    handleSaveFilters, FilterConditionsDialog).
function QueryBuilderCanvasInner({ initialState, onChange }: QueryBuilderCanvasProps) {
  const { t } = useTranslation()
  const [nodes, setNodes, onNodesChange] = useNodesState<TableNodeType>(
    initialState.nodes as TableNodeType[],
  )
  // Restore persisted edges from initialState (Story 9.3+ will write them; W2 fix).
  const [edges, setEdges, onEdgesChange] = useEdgesState<JoinEdgeType>(
    initialState.edges as JoinEdgeType[],
  )
  const reactFlowWrapper = useRef<HTMLDivElement>(null)
  const { screenToFlowPosition } = useReactFlow()
  // Used to lazily fetch (and cache) a table's columns on drop — the palette only
  // carries names + counts, so columns are loaded when a table is added to the canvas.
  const queryClient = useQueryClient()

  // Fix deferred W1: hold non-node/edge parts of builder state in a ref so that
  // notify() always emits up-to-date filters/orderBy/caseColumns/calculatedColumns
  // regardless of which closure captured the `initialState` prop.
  const extraStateRef = useRef<Omit<BuilderState, 'nodes' | 'edges'>>({
    filters: initialState.filters,
    orderBy: initialState.orderBy,
    caseColumns: initialState.caseColumns,
    calculatedColumns: initialState.calculatedColumns,
  })

  // Story 10.3: caseColumns state drives the TableNode CASE column rows and the
  // CaseColumnEditor panel. caseColumnsRef lets handlers read the latest array
  // without closing over stale state (same posture as nodesRef/edgesRef).
  const [caseColumns, setCaseColumns] = useState<CaseColumn[]>(initialState.caseColumns)
  const caseColumnsRef = useRef(caseColumns)
  caseColumnsRef.current = caseColumns
  // Story 11.2: seed past restored ids so new ids never collide (Dev Notes §4).
  const caseIdCounterRef = useRef(maxSuffixFromIds(initialState.caseColumns.map((c) => c.id)))

  // Story 10.4: replaces Story 10.3's separate selectedCaseColumnId + Story 9.4's selectedEdge.
  // Unified selector eliminates mutual-exclusivity boilerplate (Story 10.3 Dev Notes §5).
  const [selectedPanel, setSelectedPanel] = useState<SelectedPanel>(null)

  // Story 10.4: calculatedColumns state + ref + counter, mirroring caseColumns pattern.
  const [calculatedColumns, setCalculatedColumns] = useState<CalculatedColumn[]>(
    initialState.calculatedColumns,
  )
  const calculatedColumnsRef = useRef(calculatedColumns)
  calculatedColumnsRef.current = calculatedColumns
  // Story 11.2: seed past restored ids so new ids never collide (Dev Notes §4).
  const calcIdCounterRef = useRef(
    maxSuffixFromIds(initialState.calculatedColumns.map((c) => c.id)),
  )

  // Story 10.5: filters state — mirrors extraStateRef.current.filters for render.
  // filtersRef lets handlers read the latest value without closure staleness.
  const [filters, setFilters] = useState<FilterGroup>(initialState.filters)
  const filtersRef = useRef(filters)
  filtersRef.current = filters

  // Story 10.5: controls Filter Conditions Dialog open/closed state.
  const [isFilterDialogOpen, setIsFilterDialogOpen] = useState(false)

  // Story 10.8: orderBy state — mirrors extraStateRef.current.orderBy for render.
  // orderByRef lets handlers read the latest value without closure staleness.
  const [orderBy, setOrderBy] = useState<OrderByClause[]>(initialState.orderBy)
  const orderByRef = useRef(orderBy)
  orderByRef.current = orderBy

  // Story 10.8: controls Order By panel open/closed state.
  const [isOrderByPanelOpen, setIsOrderByPanelOpen] = useState(false)

  // Story 11.2 (FR-71 AC-2): seed the module-level filter id counter past any restored
  // filter ids exactly once per mount. Gated by a ref (not a useEffect) so it runs
  // synchronously before the first filter Add — same posture as the useRef(maxSuffix…)
  // counter seeding above (Task 4 / Dev Notes §4).
  const _filterSeedRef = useRef(false)
  if (!_filterSeedRef.current) {
    _filterSeedRef.current = true
    seedFilterIdCounter(initialState.filters)
  }

  // Stable helper: emit current canvas state to parent.
  const notify = useCallback(
    (currentNodes: TableNodeType[], currentEdges: JoinEdgeType[]) => {
      onChange({
        ...extraStateRef.current,
        nodes: currentNodes as TableNodeState[],
        edges: currentEdges as JoinEdgeState[],
      })
    },
    [onChange],
  )

  // AC-2: Reject same-node connections.
  // AC-4: Reject duplicate node-pair connections (one edge per pair, per V1 spec).
  const isValidConnection = useCallback<IsValidConnection<JoinEdgeType>>(
    (connection) => {
      if (connection.source === connection.target) return false
      const alreadyConnected = edges.some(
        (e) =>
          (e.source === connection.source && e.target === connection.target) ||
          (e.source === connection.target && e.target === connection.source),
      )
      return !alreadyConnected
    },
    [edges],
  )

  // AC-1: Create a JoinEdge when a valid handle-to-handle connection is completed.
  // sourceHandle = column name on source node; targetHandle = column name on target node.
  // Belt-and-suspenders: duplicate isValidConnection guards here so programmatic
  // onConnect calls also respect AC-2 (same-node) and AC-4 (duplicate pair).
  const onConnect = useCallback(
    (connection: Connection) => {
      if (!connection.source || !connection.target) return
      if (!connection.sourceHandle || !connection.targetHandle) return
      if (connection.source === connection.target) return
      const alreadyConnected = edges.some(
        (e) =>
          (e.source === connection.source && e.target === connection.target) ||
          (e.source === connection.target && e.target === connection.source),
      )
      if (alreadyConnected) return

      const newEdge: JoinEdgeType = {
        id: `e-${connection.source}:${connection.sourceHandle}--${connection.target}:${connection.targetHandle}`,
        source: connection.source,
        target: connection.target,
        sourceHandle: connection.sourceHandle,
        targetHandle: connection.targetHandle,
        type: 'joinEdge',
        data: {
          sourceColumn: connection.sourceHandle,
          targetColumn: connection.targetHandle,
          joinType: 'INNER',
        },
      }

      const updatedEdges = [...edges, newEdge]
      setEdges(updatedEdges)
      notify(nodes, updatedEdges)
    },
    [edges, nodes, setEdges, notify],
  )

  // AC-1: Open the JoinInspector when the user clicks a join edge.
  const onEdgeClick = useCallback(
    (_event: React.MouseEvent, edge: JoinEdgeType) => {
      setSelectedPanel({ type: 'join', edge })
    },
    [],
  )

  // Close the inspector when the user clicks the canvas background.
  const onPaneClick = useCallback(() => {
    setSelectedPanel(null)
  }, [])

  // AC-4: Update joinType in edges state and emit via notify.
  // Also refreshes selectedEdge so the inspector reflects the new value immediately
  // (the stale `edge` prop would otherwise reset the Select on the next render).
  const handleJoinTypeChange = useCallback(
    (edgeId: string, joinType: JoinType) => {
      // Narrow `data` before spreading: JoinEdgeType.data is `JoinEdgeData | undefined`
      // (xyflow's Edge.data is optional), so the `&& e.data` guard keeps the merged
      // object a complete JoinEdgeData. In practice data is always set (Story 9.3).
      const updatedEdges = edges.map((e) =>
        e.id === edgeId && e.data ? { ...e, data: { ...e.data, joinType } } : e,
      )
      setEdges(updatedEdges)
      notify(nodes, updatedEdges)
      setSelectedPanel((prev) =>
        prev?.type === 'join' && prev.edge.id === edgeId && prev.edge.data
          ? { ...prev, edge: { ...prev.edge, data: { ...prev.edge.data, joinType } } }
          : prev,
      )
    },
    [edges, nodes, setEdges, notify],
  )

  // Keep refs mirroring the latest committed nodes/edges. handleSideChange is
  // provided via context to EVERY TableNode; if it changed identity each render
  // (i.e. closed over `nodes`/`edges` with them in its deps) the provider value
  // would change every render and re-render all nodes, defeating TableNode's
  // memo(). Reading from refs lets the callback stay referentially stable. Same
  // ref-mirror posture as extraStateRef above.
  const nodesRef = useRef(nodes)
  nodesRef.current = nodes
  const edgesRef = useRef(edges)
  edgesRef.current = edges

  // Story 9.5 AC-1/AC-5 + single-Left invariant: set the clicked node's side and,
  // when promoting a node to 'left', demote any other 'left' node to 'right' so
  // exactly one FROM anchor exists for Story 11.1. Emits to the parent via notify().
  const handleSideChange = useCallback(
    (nodeId: string, side: TableSide) => {
      const updatedNodes = nodesRef.current.map((n) => {
        if (n.id === nodeId) {
          return { ...n, data: { ...n.data, side } }
        }
        if (side === 'left' && n.data.side === 'left') {
          return { ...n, data: { ...n.data, side: 'right' as TableSide } }
        }
        return n
      })
      setNodes(updatedNodes)
      notify(updatedNodes, edgesRef.current)
    },
    [setNodes, notify],
  )

  // Story 10.1 AC-1/AC-2: toggle one column's `checked` flag and emit via notify().
  // Reads refs (not closure state) to stay referentially stable — same posture as
  // handleSideChange, so the ColumnCheckContext.Provider value doesn't change every
  // render and defeat TableNode's memo().
  const handleColumnCheck = useCallback(
    (nodeId: string, columnName: string, checked: boolean) => {
      const updatedNodes = nodesRef.current.map((n) => {
        if (n.id !== nodeId) return n
        return {
          ...n,
          data: {
            ...n.data,
            columns: n.data.columns.map((c) =>
              c.columnName === columnName ? { ...c, checked } : c,
            ),
          },
        }
      })
      setNodes(updatedNodes)
      notify(updatedNodes, edgesRef.current)
    },
    [setNodes, notify],
  )

  // Story 10.2 AC-1/AC-5: set one column's aggregate function and emit via notify().
  // Reads refs (not closure state) — same stable-ref posture as handleColumnCheck so the
  // ColumnAggregateContext.Provider value stays referentially stable and does not defeat
  // TableNode's memo(). Deps [setNodes, notify] only — do NOT add nodes/edges.
  const handleAggregateChange = useCallback(
    (nodeId: string, columnName: string, aggregate: AggregateFunction) => {
      const updatedNodes = nodesRef.current.map((n) => {
        if (n.id !== nodeId) return n
        return {
          ...n,
          data: {
            ...n.data,
            columns: n.data.columns.map((c) =>
              c.columnName === columnName ? { ...c, aggregate } : c,
            ),
          },
        }
      })
      setNodes(updatedNodes)
      notify(updatedNodes, edgesRef.current)
    },
    [setNodes, notify],
  )

  // Story 10.2 AC-4/AC-5: update one column's alias and emit via notify().
  // Invalid aliases are stored as-is (displayed with inline error in TableNode.tsx).
  // Blocking save on invalid alias is deferred to Story 11.2. Same stable-ref posture.
  const handleAliasChange = useCallback(
    (nodeId: string, columnName: string, alias: string) => {
      const updatedNodes = nodesRef.current.map((n) => {
        if (n.id !== nodeId) return n
        return {
          ...n,
          data: {
            ...n.data,
            columns: n.data.columns.map((c) =>
              c.columnName === columnName ? { ...c, alias } : c,
            ),
          },
        }
      })
      setNodes(updatedNodes)
      notify(updatedNodes, edgesRef.current)
    },
    [setNodes, notify],
  )

  // Story 10.3 AC-1/AC-5: create a new CASE column for the given node, auto-open
  // the editor, and emit via notify(). Reads refs (not closure state) for stable-ref
  // posture so AddCaseColumnContext.Provider value is referentially stable.
  const handleAddCaseColumn = useCallback(
    (nodeId: string) => {
      const newId = `case-${nodeId}-${++caseIdCounterRef.current}`
      const firstColumn =
        nodesRef.current.find((n) => n.id === nodeId)?.data.columns[0]?.columnName ?? ''
      const defaultWhen: CaseWhen = {
        nodeId,
        columnName: firstColumn,
        operator: '=',
        operandValue: '',
        thenValue: '',
      }
      const newCase: CaseColumn = {
        id: newId,
        nodeId,
        alias: '',
        whens: [defaultWhen],
        elseValue: '',
      }
      const updated = [...caseColumnsRef.current, newCase]
      setCaseColumns(updated)
      extraStateRef.current = { ...extraStateRef.current, caseColumns: updated }
      notify(nodesRef.current, edgesRef.current)
      setSelectedPanel({ type: 'case', id: newId })
    },
    [notify],
  )

  // Story 10.3 AC-5: delete a CASE column by id and emit via notify().
  const handleDeleteCaseColumn = useCallback(
    (caseId: string) => {
      const updated = caseColumnsRef.current.filter((c) => c.id !== caseId)
      setCaseColumns(updated)
      extraStateRef.current = { ...extraStateRef.current, caseColumns: updated }
      notify(nodesRef.current, edgesRef.current)
      setSelectedPanel((prev) => (prev?.type === 'case' && prev.id === caseId ? null : prev))
    },
    [notify],
  )

  // Story 10.3: open the CaseColumnEditor for the given case id.
  // Deps []: setSelectedPanel is a stable setter.
  const handleSelectCaseColumn = useCallback((caseId: string) => {
    setSelectedPanel({ type: 'case', id: caseId })
  }, [])

  // Story 10.3 AC-5: update a CASE column in-place and emit via notify().
  // Called by CaseColumnEditor on every field change (live editing).
  const handleUpdateCaseColumn = useCallback(
    (updated: CaseColumn) => {
      const updatedAll = caseColumnsRef.current.map((c) => (c.id === updated.id ? updated : c))
      setCaseColumns(updatedAll)
      extraStateRef.current = { ...extraStateRef.current, caseColumns: updatedAll }
      notify(nodesRef.current, edgesRef.current)
    },
    [notify],
  )

  // Story 10.4 AC-1/AC-5: create a new calculated column for the given node, auto-open
  // the editor, and emit via notify(). Reads refs for stable-ref posture.
  const handleAddCalculatedColumn = useCallback(
    (nodeId: string) => {
      const newId = `calc-${nodeId}-${++calcIdCounterRef.current}`
      const newCalc: CalculatedColumn = {
        id: newId,
        nodeId,
        alias: '',
        expression: '',
      }
      const updated = [...calculatedColumnsRef.current, newCalc]
      setCalculatedColumns(updated)
      extraStateRef.current = { ...extraStateRef.current, calculatedColumns: updated }
      notify(nodesRef.current, edgesRef.current)
      setSelectedPanel({ type: 'calculated', id: newId })
    },
    [notify],
  )

  // Story 10.4 AC-5: delete a calculated column by id and emit via notify().
  const handleDeleteCalculatedColumn = useCallback(
    (calcId: string) => {
      const updated = calculatedColumnsRef.current.filter((c) => c.id !== calcId)
      setCalculatedColumns(updated)
      extraStateRef.current = { ...extraStateRef.current, calculatedColumns: updated }
      notify(nodesRef.current, edgesRef.current)
      setSelectedPanel((prev) => (prev?.type === 'calculated' && prev.id === calcId ? null : prev))
    },
    [notify],
  )

  // Story 10.4: open the CalculatedColumnEditor for the given calculated column id.
  const handleSelectCalculatedColumn = useCallback((calcId: string) => {
    setSelectedPanel({ type: 'calculated', id: calcId })
  }, [])

  // Story 10.4 AC-5: update a calculated column in-place and emit via notify().
  // Called by CalculatedColumnEditor on every field change (live editing).
  const handleUpdateCalculatedColumn = useCallback(
    (updated: CalculatedColumn) => {
      const updatedAll = calculatedColumnsRef.current.map((c) =>
        c.id === updated.id ? updated : c,
      )
      setCalculatedColumns(updatedAll)
      extraStateRef.current = { ...extraStateRef.current, calculatedColumns: updatedAll }
      notify(nodesRef.current, edgesRef.current)
    },
    [notify],
  )

  // Story 10.5 AC-6: called when the dialog's "Apply" button is clicked.
  // Updates extraStateRef.current.filters before notify() so the parent's
  // onChange receives the complete updated BuilderState (same ordering requirement
  // as caseColumns / calculatedColumns updates — extraStateRef BEFORE notify).
  const handleSaveFilters = useCallback(
    (newFilters: FilterGroup) => {
      setFilters(newFilters)
      filtersRef.current = newFilters
      extraStateRef.current = { ...extraStateRef.current, filters: newFilters }
      notify(nodesRef.current, edgesRef.current)
    },
    [notify],
  )

  // Story 10.8 AC-3/AC-6: called when the Order By panel's "Apply" button is clicked.
  // Updates extraStateRef.current.orderBy BEFORE notify() so the parent's onChange
  // receives the complete updated BuilderState (same ordering requirement as
  // handleSaveFilters / caseColumns / calculatedColumns — extraStateRef BEFORE notify).
  const handleUpdateOrderBy = useCallback(
    (newOrderBy: OrderByClause[]) => {
      setOrderBy(newOrderBy)
      orderByRef.current = newOrderBy
      extraStateRef.current = { ...extraStateRef.current, orderBy: newOrderBy }
      notify(nodesRef.current, edgesRef.current)
    },
    [notify],
  )

  // AC-3: When node(s) are deleted, cascade removal to all connected edges.
  // onNodesChange(changes) QUEUES the state update — `nodes` still reflects the
  // pre-deletion list here, so filtering gives correct post-deletion arrays.
  const handleNodesChange = useCallback(
    (changes: NodeChange<TableNodeType>[]) => {
      onNodesChange(changes)

      const removedIds = new Set(
        changes.filter((c) => c.type === 'remove').map((c) => c.id),
      )

      if (removedIds.size > 0) {
        const survivingEdges = edges.filter(
          (e) => !removedIds.has(e.source) && !removedIds.has(e.target),
        )
        setEdges(survivingEdges)
        const survivingNodes = nodes.filter((n) => !removedIds.has(n.id))

        // Cascade CASE column removal (Story 10.3) — extraStateRef MUST be updated before notify().
        const survivingCaseColumns = caseColumnsRef.current.filter(
          (c) => !removedIds.has(c.nodeId),
        )
        if (survivingCaseColumns.length !== caseColumnsRef.current.length) {
          setCaseColumns(survivingCaseColumns)
          extraStateRef.current = { ...extraStateRef.current, caseColumns: survivingCaseColumns }
        }

        // Story 10.4: cascade calculated column removal — same ordering requirement.
        const survivingCalcColumns = calculatedColumnsRef.current.filter(
          (c) => !removedIds.has(c.nodeId),
        )
        if (survivingCalcColumns.length !== calculatedColumnsRef.current.length) {
          setCalculatedColumns(survivingCalcColumns)
          extraStateRef.current = {
            ...extraStateRef.current,
            calculatedColumns: survivingCalcColumns,
          }
        }

        notify(survivingNodes, survivingEdges)

        // Close the floating panel if it references a removed node/edge.
        // Uses refs (pre-update values) to check nodeId membership — correct because
        // removedIds captures the deletion batch and refs hold pre-batch state.
        setSelectedPanel((prev) => {
          if (!prev) return null
          if (prev.type === 'join') {
            return removedIds.has(prev.edge.source) || removedIds.has(prev.edge.target)
              ? null
              : prev
          }
          if (prev.type === 'case') {
            const target = caseColumnsRef.current.find((c) => c.id === prev.id)
            return target && removedIds.has(target.nodeId) ? null : prev
          }
          if (prev.type === 'calculated') {
            const target = calculatedColumnsRef.current.find((c) => c.id === prev.id)
            return target && removedIds.has(target.nodeId) ? null : prev
          }
          return null
        })
      }
    },
    [onNodesChange, nodes, edges, setEdges, notify],
  )

  // AC-1: Propagate final node positions to parent when a drag ends.
  // React Flow v12's third argument is ONLY the dragged node(s) (the dragged node
  // plus any multi-selected nodes) — NOT the full canvas. Merge their updated
  // positions over the full `nodes` list so non-dragged tables are not dropped.
  const onNodeDragStop = useCallback(
    (_event: MouseEvent | TouchEvent, _node: TableNodeType, draggedNodes: TableNodeType[]) => {
      const movedById = new Map(draggedNodes.map((n) => [n.id, n]))
      const mergedNodes = nodes.map((n) => movedById.get(n.id) ?? n)
      notify(mergedNodes, edges)
    },
    [nodes, edges, notify],
  )

  // AC-3: Edge deletion (via deleteKeyCode on a selected edge) must also reach the
  // parent. onEdgesChange alone only updates local state — mirror handleNodesChange
  // so removed edges are cleared from builder_state.
  const handleEdgesChange = useCallback(
    (changes: EdgeChange<JoinEdgeType>[]) => {
      onEdgesChange(changes)

      const removedEdgeIds = new Set(
        changes.filter((c) => c.type === 'remove').map((c) => c.id),
      )

      if (removedEdgeIds.size > 0) {
        const survivingEdges = edges.filter((e) => !removedEdgeIds.has(e.id))
        notify(nodes, survivingEdges)
        // Story 9.4 §8 (Option A): close the inspector if its edge was deleted,
        // so it never lingers showing an edge that no longer exists.
        setSelectedPanel((prev) =>
          prev?.type === 'join' && removedEdgeIds.has(prev.edge.id) ? null : prev,
        )
      }
    },
    [onEdgesChange, nodes, edges, notify],
  )

  const onDragOver = useCallback((event: React.DragEvent) => {
    event.preventDefault()
    event.dataTransfer.dropEffect = 'copy'
  }, [])

  const onDrop = useCallback(
    (event: React.DragEvent) => {
      event.preventDefault()

      const raw = event.dataTransfer.getData('application/formforge-table')
      if (!raw) return

      let table: CatalogTable
      try {
        table = JSON.parse(raw) as CatalogTable
      } catch {
        return
      }

      // Capture the drop position synchronously — the React event is recycled before the
      // async column fetch below resolves, so clientX/Y must be read now.
      const position = screenToFlowPosition({ x: event.clientX, y: event.clientY })

      // The palette ships names + counts only; fetch this table's columns on demand
      // (cached per-table via react-query), then add the node. Reads nodes/edges from refs
      // because the closure's copies may be stale after the await.
      void (async () => {
        let columns: CatalogColumn[]
        try {
          const result = await queryClient.fetchQuery({
            queryKey: ['datasets', 'catalog', 'columns', table.tableName],
            queryFn: () => getTableColumns(table.tableName),
            staleTime: 5 * 60 * 1000,
          })
          columns = result.columns
        } catch {
          toast.error(t('datasets.builder.palette.columnsLoadError'))
          return
        }

        // Unique id: tableName + timestamp ensures distinct nodes even for the same
        // table dragged multiple times (AC-5, enables self-joins per FR-63 AC-4).
        const newNodeId = `${table.tableName}-${Date.now()}`

        const newNodeData: TableNodeData = {
          tableName: table.tableName,
          // First node defaults to 'left', subsequent to 'right' (Story 9.5 wires the
          // real toggle; for now derive a sensible default from the existing node count).
          side: nodesRef.current.length === 0 ? 'left' : 'right',
          columns: columns.map(
            (col): ColumnSelection => ({
              columnName: col.columnName,
              pgType: col.pgType,
              checked: false,
              aggregate: 'none',
              alias: '',
            }),
          ),
        }

        const newNode: TableNodeType = {
          id: newNodeId,
          type: 'tableNode',
          position,
          data: newNodeData,
        }

        const updatedNodes = [...nodesRef.current, newNode]
        setNodes(updatedNodes)
        // Notify parent so it can track state (persisted on save in Story 11.2).
        notify(updatedNodes, edgesRef.current)
      })()
    },
    [screenToFlowPosition, setNodes, notify, queryClient, t],
  )

  // Story 9.5 AC-3: gate Save/Preview when >1 table and none designated 'left'.
  // The helper is the SSOT consumed here (banner) and by Stories 11.2/11.3 (buttons).
  const leftTableError = getLeftTableValidationError(nodes)
  // Story 10.1 AC-3: gate Save/Preview when no column is checked on any table node.
  // Same SSOT posture as leftTableError — consumed here (banner) and by 11.2/11.3 (buttons).
  const columnSelectionError = getColumnSelectionValidationError(nodes)
  // Story 10.3 AC-4: gate Save/Preview when any CASE column has an empty alias.
  const caseColumnAliasError = getCaseColumnAliasError(caseColumns)
  // Story 10.4 AC-3: gate Save/Preview when any calculated column has an empty alias.
  const calculatedColumnAliasError = getCalculatedColumnAliasError(calculatedColumns)

  // Unified panel derivations — null if id is stale after deletion
  const selectedCaseColumn =
    selectedPanel?.type === 'case'
      ? (caseColumns.find((c) => c.id === selectedPanel.id) ?? null)
      : null

  const selectedCalculatedColumn =
    selectedPanel?.type === 'calculated'
      ? (calculatedColumns.find((c) => c.id === selectedPanel.id) ?? null)
      : null

  return (
    <div ref={reactFlowWrapper} className="relative h-full w-full">
      {/* Canvas toolbar — Story 10.5: Filter button. Story 10.8: Order By button. */}
      <div className="absolute left-4 top-4 z-40 flex items-center gap-2">
        <button
          type="button"
          onClick={() => setIsFilterDialogOpen(true)}
          aria-label={t('datasets.builder.filterDialog.openButtonAria')}
          className="rounded-md border border-border bg-card px-3 py-1.5 text-xs font-medium text-foreground shadow-sm hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          {t('datasets.builder.filterDialog.openButton')}
        </button>
        <button
          type="button"
          onClick={() => setIsOrderByPanelOpen(true)}
          aria-label={t('datasets.builder.orderByPanel.openButtonAria')}
          className="rounded-md border border-border bg-card px-3 py-1.5 text-xs font-medium text-foreground shadow-sm hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          {t('datasets.builder.orderByPanel.openButton')}
        </button>
      </div>
      <TableSideChangeContext.Provider value={handleSideChange}>
        <ColumnCheckContext.Provider value={handleColumnCheck}>
          <ColumnAggregateContext.Provider value={handleAggregateChange}>
            <ColumnAliasContext.Provider value={handleAliasChange}>
              <CaseColumnsContext.Provider value={caseColumns}>
                <AddCaseColumnContext.Provider value={handleAddCaseColumn}>
                  <DeleteCaseColumnContext.Provider value={handleDeleteCaseColumn}>
                    <SelectCaseColumnContext.Provider value={handleSelectCaseColumn}>
                      <CalculatedColumnsContext.Provider value={calculatedColumns}>
                        <AddCalculatedColumnContext.Provider value={handleAddCalculatedColumn}>
                          <DeleteCalculatedColumnContext.Provider
                            value={handleDeleteCalculatedColumn}
                          >
                            <SelectCalculatedColumnContext.Provider
                              value={handleSelectCalculatedColumn}
                            >
                              <ReactFlow
                                nodes={nodes}
                                edges={edges}
                                nodeTypes={nodeTypes}
                                edgeTypes={edgeTypes}
                                onNodesChange={handleNodesChange}
                                onEdgesChange={handleEdgesChange}
                                onNodeDragStop={onNodeDragStop}
                                onConnect={onConnect}
                                isValidConnection={isValidConnection}
                                onEdgeClick={onEdgeClick}
                                onPaneClick={onPaneClick}
                                onDragOver={onDragOver}
                                onDrop={onDrop}
                                deleteKeyCode={['Backspace', 'Delete']}
                                // Loose mode lets a join be drawn between any two column
                                // handles regardless of which is source/target — without it
                                // a natural drag (one node's edge to the next) is frequently
                                // a source→source / target→target pair, which strict mode
                                // rejects, so the connection line vanished on mouse-up.
                                connectionMode={ConnectionMode.Loose}
                                fitView
                              >
                                <Background />
                                <Controls />
                                <MiniMap />
                              </ReactFlow>
                            </SelectCalculatedColumnContext.Provider>
                          </DeleteCalculatedColumnContext.Provider>
                        </AddCalculatedColumnContext.Provider>
                      </CalculatedColumnsContext.Provider>
                    </SelectCaseColumnContext.Provider>
                  </DeleteCaseColumnContext.Provider>
                </AddCaseColumnContext.Provider>
              </CaseColumnsContext.Provider>
            </ColumnAliasContext.Provider>
          </ColumnAggregateContext.Provider>
        </ColumnCheckContext.Provider>
      </TableSideChangeContext.Provider>
      {(leftTableError ||
        columnSelectionError ||
        caseColumnAliasError ||
        calculatedColumnAliasError) && (
        <div className="nodrag nopan pointer-events-none absolute left-1/2 top-4 z-40 flex -translate-x-1/2 flex-col items-center gap-1">
          {leftTableError && (
            <div
              role="alert"
              className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-1.5 text-xs font-medium text-destructive shadow-sm"
            >
              {t('datasets.builder.validation.noLeftTable')}
            </div>
          )}
          {columnSelectionError && (
            <div
              role="alert"
              className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-1.5 text-xs font-medium text-destructive shadow-sm"
            >
              {t('datasets.builder.validation.noColumnsSelected')}
            </div>
          )}
          {caseColumnAliasError && (
            <div
              role="alert"
              className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-1.5 text-xs font-medium text-destructive shadow-sm"
            >
              {t('datasets.builder.validation.caseColumnRequiresAlias')}
            </div>
          )}
          {calculatedColumnAliasError && (
            <div
              role="alert"
              className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-1.5 text-xs font-medium text-destructive shadow-sm"
            >
              {t('datasets.builder.validation.calculatedColumnRequiresAlias')}
            </div>
          )}
        </div>
      )}
      {selectedPanel?.type === 'join' && (
        <JoinInspector
          edge={selectedPanel.edge}
          nodes={nodes}
          onJoinTypeChange={handleJoinTypeChange}
          onClose={() => setSelectedPanel(null)}
        />
      )}
      {selectedCaseColumn && (
        <CaseColumnEditor
          caseColumn={selectedCaseColumn}
          nodes={nodes}
          onUpdate={handleUpdateCaseColumn}
          onClose={() => setSelectedPanel(null)}
        />
      )}
      {selectedCalculatedColumn && (
        <CalculatedColumnEditor
          calculatedColumn={selectedCalculatedColumn}
          onUpdate={handleUpdateCalculatedColumn}
          onClose={() => setSelectedPanel(null)}
        />
      )}
      <FilterConditionsDialog
        isOpen={isFilterDialogOpen}
        filters={filters}
        nodes={nodes}
        onSave={handleSaveFilters}
        onClose={() => setIsFilterDialogOpen(false)}
      />
      <OrderByPanel
        isOpen={isOrderByPanelOpen}
        orderBy={orderBy}
        nodes={nodes}
        onSave={handleUpdateOrderBy}
        onClose={() => setIsOrderByPanelOpen(false)}
      />
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
