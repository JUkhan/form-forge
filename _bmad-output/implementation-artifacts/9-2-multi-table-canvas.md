# Story 9.2: Multi-Table Canvas

Status: done

## Story

As a user building a Dataset,
I want to place multiple table nodes on the React Flow canvas and freely rearrange or remove them,
so that I can construct multi-table queries visually.

## Acceptance Criteria

**AC-1 — Node positions are freely adjustable and propagated to builder_state**
**Given** I have placed table nodes on the canvas
**When** I drag one to a new position
**Then** I can drop it anywhere on the canvas (React Flow handles drag natively)
**And** `onNodeDragStop` fires and calls `onChange` with updated `nodes[*].position` values
**And** those positions will be included in `builder_state` when Story 11.2 persists on Save

**AC-2 — Zoom and pan controls are available**
**Given** the Query Builder canvas
**When** it renders
**Then** zoom-in/zoom-out, fit-view, and pan controls are visible
*(Delivered by `<Controls />`, `<MiniMap />`, `<Background />` in Story 9.1 — no change needed)*

**AC-3 — Remove node removes all connected join edges**
**Given** I have placed one or more table nodes on the canvas
**When** I select a node and press Delete or Backspace
**Then** the node is removed from the canvas
**And** all join edges where `edge.source === nodeId || edge.target === nodeId` are removed from the canvas
**And** `onChange` is called with the surviving nodes and edges (cleared from `builder_state`)

**AC-4 — Remove node clears column selections (CASE/calculated deferred)**
**Given** I remove a table node that has column selections configured
**When** the removal completes
**Then** those column selections are absent from `builder_state` (they are embedded in `node.data.columns`, removed with the node)
**Note:** CASE/calculated column cleanup deferred to Epic 10 — their stub types (`CaseColumn`, `CalculatedColumn`) lack a `tableName` reference in this story; they are not populated during Epic 9 so no cleanup is required now

---

## Tasks / Subtasks

### Task 1 — Frontend: Update `QueryBuilderCanvas.tsx` (AC-1, AC-3, fixes W1 & W2 from Story 9.1 review)

**This is the only file that needs to change.** All four ACs are addressed by targeted additions to `QueryBuilderCanvas.tsx`.

Open `web/src/features/datasets/QueryBuilderCanvas.tsx`.

**Step 1 — Add `type NodeChange` and `JoinEdgeState` imports.**

Change the import block from:
```typescript
import {
  ReactFlow,
  Background,
  Controls,
  MiniMap,
  useNodesState,
  useEdgesState,
  useReactFlow,
  ReactFlowProvider,
  type Edge,
} from '@xyflow/react'
```
to:
```typescript
import {
  ReactFlow,
  Background,
  Controls,
  MiniMap,
  useNodesState,
  useEdgesState,
  useReactFlow,
  ReactFlowProvider,
  type Edge,
  type NodeChange,
} from '@xyflow/react'
```

Change the `builderState` import block from:
```typescript
import type {
  BuilderState,
  ColumnSelection,
  TableNodeData,
  TableNodeState,
} from './types/builderState'
```
to:
```typescript
import type {
  BuilderState,
  ColumnSelection,
  JoinEdgeState,
  TableNodeData,
  TableNodeState,
} from './types/builderState'
```

**Step 2 — Fix deferred W2: restore edges from `initialState` on mount.**

Change:
```typescript
const [edges, , onEdgesChange] = useEdgesState<Edge>([])
```
to:
```typescript
// Restore persisted edges from initialState (Story 9.3+ will write them; W2 fix).
const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>(
  initialState.edges as Edge[],
)
```

**Step 3 — Add `extraStateRef` immediately after the `useRef`/`useReactFlow` lines (fixes deferred W1 — stale closure).**

After the line `const { screenToFlowPosition } = useReactFlow()`, insert:
```typescript
// Fix deferred W1: hold non-node/edge parts of builder state in a ref so that
// notify() always emits up-to-date filters/orderBy/caseColumns/calculatedColumns
// regardless of which closure captured the `initialState` prop.
const extraStateRef = useRef<Omit<BuilderState, 'nodes' | 'edges'>>({
  filters: initialState.filters,
  orderBy: initialState.orderBy,
  caseColumns: initialState.caseColumns,
  calculatedColumns: initialState.calculatedColumns,
})
```

**Step 4 — Add `notify` helper after `extraStateRef` (before `onDragOver`).**
```typescript
// Stable helper: emit current canvas state to parent.
const notify = useCallback(
  (currentNodes: TableNodeType[], currentEdges: Edge[]) => {
    onChange({
      ...extraStateRef.current,
      nodes: currentNodes as TableNodeState[],
      edges: currentEdges as JoinEdgeState[],
    })
  },
  [onChange],
)
```

**Step 5 — Add `handleNodesChange` after `notify` (implements AC-3 deletion cascade).**
```typescript
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
      notify(survivingNodes, survivingEdges)
    }
  },
  [onNodesChange, nodes, edges, setEdges, notify],
)
```

**Step 6 — Add `onNodeDragStop` after `handleNodesChange` (implements AC-1 position propagation).**
```typescript
// AC-1: Propagate final node positions to parent when a drag ends.
// React Flow v12 provides all current nodes as the third argument.
const onNodeDragStop = useCallback(
  (_event: React.MouseEvent, _node: TableNodeType, currentNodes: TableNodeType[]) => {
    notify(currentNodes, edges)
  },
  [edges, notify],
)
```

**Step 7 — Update `onDrop` to use `notify` (remove `initialState` dep, eliminates W1).**

Replace the `onChange` call inside `onDrop`:
```typescript
      onChange({
        ...initialState,
        nodes: updatedNodes as TableNodeState[],
        edges: initialState.edges,
      })
```
with:
```typescript
      notify(updatedNodes, edges)
```

Also update the `useCallback` deps array for `onDrop` — replace `[nodes, screenToFlowPosition, setNodes, onChange, initialState]` with:
```typescript
    [nodes, edges, screenToFlowPosition, setNodes, notify],
```

**Step 8 — Update the `<ReactFlow>` JSX element.**

Replace `onNodesChange={onNodesChange}` with `onNodesChange={handleNodesChange}`.

Add these two new props:
- `onNodeDragStop={onNodeDragStop}` — after `onEdgesChange`
- `deleteKeyCode={['Backspace', 'Delete']}` — enables keyboard-triggered node deletion

Full updated JSX block:
```tsx
  return (
    <div ref={reactFlowWrapper} className="h-full w-full">
      <ReactFlow
        nodes={nodes}
        edges={edges}
        nodeTypes={nodeTypes}
        onNodesChange={handleNodesChange}
        onEdgesChange={onEdgesChange}
        onNodeDragStop={onNodeDragStop}
        onDragOver={onDragOver}
        onDrop={onDrop}
        deleteKeyCode={['Backspace', 'Delete']}
        fitView
      >
        <Background />
        <Controls />
        <MiniMap />
      </ReactFlow>
    </div>
  )
```

---

### Task 2 — Frontend: Build and test verification

- [x] `cd web && npx tsc -b --noEmit` → 0 errors
- [x] `cd web && npx vitest run` → same baseline as Story 9.1 (281 passed, 1 pre-existing i18n-lint failure); no new failures
- [x] Confirm no new orphaned i18n keys introduced (none are added in this story)

---

## Review Findings

_bmad-code-review 2026-06-04 — Blind Hunter + Edge Case Hunter + Acceptance Auditor. Triage: 0 decision-needed, 2 patch, 3 defer, 7 dismissed._

### Patch (unchecked — must be fixed)

- [x] **[Review][Patch] CRITICAL — `onNodeDragStop` emits ONLY the dragged node(s), wiping every other table from builder_state** [QueryBuilderCanvas.tsx:92-97] — FIXED: merge dragged nodes' positions over full `nodes` list; added `nodes` to deps; corrected comment.
  The comment "React Flow v12 provides all current nodes as the third argument" is **factually wrong**. Verified against `@xyflow/system` source: `onNodeDragStop(event, node, currentNodes)` builds `currentNodes` from `getDragItems`, which the source comments as "looks for all **selected** nodes" (`node.selected || node.id === nodeId`). So on a multi-table canvas, dragging one table calls `notify(currentNodes, edges)` with a one-element array → all other tables are dropped from the emitted builder_state. This breaks AC-1 (the headline criterion) on the exact "multi-table" scenario the story exists for. Not caught by tsc/vitest (UI-only path). Fix: merge the dragged node(s)' updated positions back into the full `nodes` list and `notify` with the merged full list, then add `nodes` to the deps array; correct the misleading comment.

- [x] **[Review][Patch] Edge-only keyboard deletion is not propagated to the parent** [QueryBuilderCanvas.tsx:167] (sources: edge + auditor) — FIXED: added `handleEdgesChange` mirroring `handleNodesChange`; wired `onEdgesChange={handleEdgesChange}`; imported `type EdgeChange`.
  `deleteKeyCode={['Backspace','Delete']}` also deletes a *selected edge*, but `onEdgesChange` is wired to the raw hook handler — edge `remove` changes never call `notify`. The edge vanishes from the canvas while the parent's `builder_state.edges` still contains it, so it reappears/persists on save. Reachable now because this story restores persisted edges from `initialState` (W2 fix). Violates AC-3's "onChange is called with surviving … edges (cleared from builder_state)". Fix: wrap `onEdgesChange` in a `handleEdgesChange` that detects `remove` changes and calls `notify(nodes, survivingEdges)` (mirror of `handleNodesChange`).

### Defer (logged to deferred-work.md)

- [x] **[Review][Defer] `extraStateRef` / `useNodesState` / `useEdgesState` are mount-only — stale if `initialState` changes after mount** [QueryBuilderCanvas.tsx:39-53] — intentional per Dev Notes §6; not triggerable in current scope (parent never replaces `initialState` post-mount). Revisit when Epic 10 makes filters/orderBy editable.
- [x] **[Review][Defer] Mixed-batch position loss when a delete batch also carries `position` changes** [QueryBuilderCanvas.tsx:83] — `survivingNodes` is computed from the pre-change `nodes` snapshot, dropping any same-batch position updates for surviving nodes. Low likelihood (keyboard-delete rarely batches with drag); self-corrects on the next drag-stop.
- [x] **[Review][Defer] `edgeTypes` not registered for restored `type:'joinEdge'` edges** [QueryBuilderCanvas.tsx:162] — restored join edges render as the default edge type. Deferred to Story 9.3 (edge creation), where `edgeTypes` will be registered.

### Dismissed (7)

Programmatic/non-drag position changes (fitView is a viewport transform, not a node-position change; no programmatic moves in scope); missing `?? []` guards on `initialState.nodes/edges` (`parseBuilderState` guarantees arrays via `EMPTY_BUILDER_STATE`); `deleteKeyCode` Backspace footgun in text inputs (React Flow's `isInputDOMNode` already suppresses delete when an input is focused); `onNodeDragStop` "stale edges closure" (`edges` is in the deps array); same-batch add+remove duplicate id (speculative); edge removed in same batch not filtered by own id (covered by the edge-deletion patch); `JoinEdgeState` cast missing fields (no edges created until 9.3).

---

## Dev Notes

### §1 — Scope and What AC-2 Already Delivered

Story 9.1 already renders `<Controls />`, `<MiniMap />`, and `<Background />` on the `<ReactFlow>` canvas, giving zoom-in/out, fit-view, and pan controls out of the box. **AC-2 has zero implementation cost** — do not add or remove anything to satisfy it.

The only work in this story is a single file: `QueryBuilderCanvas.tsx`.

### §2 — Why `onNodesChange` Queues, Not Applies

React's state updates are asynchronous. When `onNodesChange(changes)` is called inside `handleNodesChange`, the change is **queued** — `nodes` (the current state variable) still reflects the pre-change list for the remainder of that synchronous call. This is why we compute `survivingNodes = nodes.filter(...)` *after* calling `onNodesChange` but *within the same synchronous block* — we get the correct post-deletion node list without waiting for the next render.

### §3 — `onNodeDragStop` Third Argument

React Flow v12 `onNodeDragStop` signature:
```typescript
onNodeDragStop?: (
  event: React.MouseEvent,
  node: TableNodeType,          // the node that was dragged
  nodes: TableNodeType[]        // ALL current nodes with updated positions
) => void
```

The third argument `currentNodes` contains the entire up-to-date node list with final positions — use it directly in `notify`. Do not derive positions from `nodes` state variable in this callback (it may be stale).

### §4 — `NodeChange` Import and Type Guard Pattern

`NodeChange<TableNodeType>` is exported from `@xyflow/react`. The change `type` discriminant property is a string literal: `'remove'`, `'position'`, `'select'`, `'dimensions'`, `'add'`, `'reset'`. The type guard `(c) => c.type === 'remove'` works without importing a named `NodeRemoveChange` type. TypeScript narrows `c.id` correctly after the filter.

### §5 — `deleteKeyCode` Prop

`deleteKeyCode={['Backspace', 'Delete']}` on `<ReactFlow>` enables both common keyboard shortcuts for node/edge deletion. When a selected node is deleted this way, React Flow fires `onNodesChange` with `{ type: 'remove', id: string }` entries — our `handleNodesChange` intercepts these and cascades to edges.

Default React Flow behavior without `deleteKeyCode`: only `Backspace` deletes. Setting the array adds `Delete` (forward-delete key) for Windows/Linux users.

### §6 — `extraStateRef` and W1 Fix

The deferred W1 from Story 9.1 review was:
> `onChange({ ...initialState, edges: initialState.edges })` spreads from the prop snapshot, not live state.

The fix: `extraStateRef.current` is initialized from `initialState` and is always the latest non-node/edge builder state (filters, orderBy, caseColumns, calculatedColumns). In Epic 9 scope these fields are all empty/default values, so the practical difference is zero — but the pattern is correct for when Epic 10 populates them.

**Do NOT add `initialState` to any `useCallback` deps array** after this change. `extraStateRef` is mutable and always current without being a dep.

### §7 — `JoinEdgeState` Cast

`notify` casts `currentEdges as JoinEdgeState[]`. In Story 9.2 scope, edges are always `[]` (join edges arrive in Story 9.3). The cast is safe — the `Edge` type from `@xyflow/react` is a superset of `JoinEdgeState`. Story 9.3 will add proper typed edge creation; for now the cast is the same pattern Story 9.1 used for nodes.

### §8 — AC-4: Column Selection Cleanup Is Automatic

Column selections live on `node.data.columns` (a `ColumnSelection[]`). When the node is removed from `nodes`, its data disappears with it — no explicit cleanup needed. The "cleared from `builder_state`" language in the AC describes this behavior accurately.

CASE columns and calculated columns (`extraStateRef.current.caseColumns` / `.calculatedColumns`) are empty arrays throughout Epic 9. Their cleanup when a node is removed is deferred to Epic 10 when those types gain a `tableName` field, making it possible to filter by table.

### §9 — Files Modified

```
MODIFIED (frontend):
  web/src/features/datasets/QueryBuilderCanvas.tsx
    — import NodeChange + JoinEdgeState
    — fix W2: initialize edges from initialState.edges
    — add extraStateRef (fix W1 stale closure)
    — add notify() helper
    — add handleNodesChange() with deletion cascade to edges
    — add onNodeDragStop() for position propagation
    — update onDrop() to use notify() + remove initialState dep
    — update <ReactFlow>: onNodesChange=handleNodesChange, onNodeDragStop, deleteKeyCode

NEW / MODIFIED (backend): none
NEW / MODIFIED (i18n): none
NEW / MODIFIED (types): none
```

### §10 — Test State Expectation

**Frontend** (`npx vitest run`): 281 passed / 1 pre-existing i18n-lint failure — same as Story 9.1 baseline. No new test files are required by the story spec. The cascade-deletion logic is internal to the canvas and is covered by manual smoke testing. The position propagation is similarly UI-only.

**Backend** (`dotnet test`): no backend changes; 918 passed / 2 pre-existing failures unchanged.

### §11 — Deferred Items Being Closed

This story resolves two deferred items from the Story 9.1 code review (`_bmad-output/implementation-artifacts/deferred-work.md`):
- **W1** — Stale `initialState` closure in `onDrop` → resolved by `extraStateRef` + `notify`
- **W2** — `initialState.edges` ignored on canvas mount → resolved by `useEdgesState(initialState.edges as Edge[])`

Remaining deferred items (W3–W5) are not in scope for Story 9.2.

### §12 — References

- [Source: `_bmad-output/planning-artifacts/epics.md` — Epic 9, Story 9.2 ACs]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.11 — AR-67 BuilderState canonical interface]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.12 — AR-68 @xyflow/react v12 integration]
- [Source: `web/src/features/datasets/QueryBuilderCanvas.tsx` — current implementation (Story 9.1)]
- [Source: `web/src/features/datasets/types/builderState.ts` — BuilderState, JoinEdgeState, TableNodeState]
- [Source: `_bmad-output/implementation-artifacts/deferred-work.md` — W1, W2 from Story 9.1 review]
- [Source: Story 9.1 Dev Notes §2 — @xyflow/react v12 API table]
- [Source: Memory: @xyflow/react v12 typing gotchas — NodeProps keyed on Node type; data must be `type` aliases]

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (1M context)

### Debug Log References

- `npx tsc -b --noEmit` initially failed with TS2322 on `onNodeDragStop`: the spec typed the
  first arg as `React.MouseEvent`, but React Flow v12's `OnNodeDrag` passes a DOM
  `MouseEvent | TouchEvent`. Corrected the param type to `MouseEvent | TouchEvent`
  (consistent with the "@xyflow/react v12 typing gotchas" learning — story specs get the
  event typing wrong). After the fix: `tsc -b --noEmit` → 0 errors.
- `npx vitest run` → 281 passed / 1 failed. The single failure is the pre-existing
  `src/__tests__/i18n-lint.test.ts` (missing designer.inspector.placeholders.label and
  related keys) — unchanged from the Story 9.1 baseline, not introduced by this story.

### Completion Notes List

- Implemented all four ACs via targeted additions to the single file
  `web/src/features/datasets/QueryBuilderCanvas.tsx`, exactly as specified.
- AC-1: added `onNodeDragStop` which calls `notify(currentNodes, edges)` using React Flow
  v12's third callback argument (the full up-to-date node list with final positions).
- AC-2: no change — `<Controls />`, `<MiniMap />`, `<Background />` were already rendered by
  Story 9.1 (zero implementation cost, confirmed).
- AC-3: added `handleNodesChange` that intercepts `'remove'` node changes and cascades edge
  removal (`edge.source`/`edge.target` in the removed set), then `notify`s the parent with the
  surviving nodes and edges. `deleteKeyCode={['Backspace', 'Delete']}` enables keyboard deletion.
- AC-4: column selections live on `node.data.columns`, so they are removed automatically with
  the node; CASE/calculated cleanup correctly deferred to Epic 10.
- Closed deferred items W1 (stale `initialState` closure → `extraStateRef` + stable `notify`)
  and W2 (`initialState.edges` ignored on mount → `useEdgesState(initialState.edges)`).
- Deviation from spec: `onNodeDragStop` event param typed `MouseEvent | TouchEvent` rather than
  the spec's `React.MouseEvent` (required for type correctness; see Debug Log).
- Tests: tsc 0 errors; vitest 281 passed / 1 pre-existing i18n-lint failure (Story 9.1 baseline).

### File List

MODIFIED:
- web/src/features/datasets/QueryBuilderCanvas.tsx

## Change Log

| Date       | Change                                                                                   |
| ---------- | ---------------------------------------------------------------------------------------- |
| 2026-06-04 | Implemented Story 9.2 Multi-Table Canvas (AC-1..AC-4) in QueryBuilderCanvas.tsx: node    |
|            | drag position propagation (onNodeDragStop), node-deletion → edge cascade (handleNodes-   |
|            | Change + deleteKeyCode), edge restore from initialState (W2), extraStateRef/notify (W1). |
|            | Corrected onNodeDragStop event type to `MouseEvent \| TouchEvent` (v12 OnNodeDrag).      |
|            | tsc 0 errors; vitest 281 passed / 1 pre-existing i18n-lint failure. Status → review.     |
