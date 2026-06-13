# Story 9.3: Column-to-Column Join Edge Creation

Status: done

## Story

As a user building a Dataset,
I want to connect a column handle on one table node to a column handle on another to create a JOIN,
so that I can define the join predicate visually.

## Acceptance Criteria

**AC-1 — Column handles connect to create a Join Edge**
**Given** each column in a TableNode exposes a source handle (left side) and a target handle (right side)
**When** I drag from any column handle on one table node to any column handle on a different table node
**Then** a JoinEdge is created between the two nodes (per FR-64 AC-1, AR-68 `JoinEdge.tsx`)
**And** the edge is stored in `builder_state.edges` as `{ id, source, target, sourceHandle, targetHandle, type: 'joinEdge', data: { sourceColumn, targetColumn, joinType: 'INNER' } }`

**AC-2 — Same-node connection is rejected**
**Given** two column handles on the same table node instance
**When** I attempt to drag from one to the other
**Then** the connection is rejected — React Flow shows an invalid indicator and no edge is created (per FR-64 AC-2)

**AC-3 — Join edge renders as a styled curve with a delete control**
**Given** a join edge exists on the canvas
**When** I click it (selecting it)
**Then** it renders as a styled bezier curve
**And** a delete button appears at the edge midpoint
**And** clicking the delete button removes the edge from both the canvas and `builder_state.edges`

**AC-4 — One edge per node-pair**
**Given** a join edge already exists between node A and node B
**When** I attempt to drag to create a second edge between the same two node instances (in either direction)
**Then** the connection is rejected — React Flow shows an invalid indicator and no duplicate edge is created
(per FR-64 I-3 AC-3 / A20 — additional join conditions are expressed via filter conditions, not multiple edges)

**AC-5 — `edgeTypes` registered (closes deferred W3 from Story 9.2 review)**
**Given** the React Flow canvas restores persisted `type: 'joinEdge'` edges from `initialState` (Story 9.2 W2 fix)
**When** the canvas renders
**Then** those edges render using the `JoinEdgeComponent` custom renderer, not the default React Flow edge type

---

## Tasks / Subtasks

### Task 1 — Frontend: Create `JoinEdge.tsx` (AC-1, AC-3, AC-5)

Create the new file `web/src/components/query-builder/JoinEdge.tsx`.

```typescript
import { memo } from 'react'
import {
  BaseEdge,
  EdgeLabelRenderer,
  getBezierPath,
  useReactFlow,
  type Edge,
  type EdgeProps,
} from '@xyflow/react'
import type { JoinEdgeData } from '../../features/datasets/types/builderState'

// v12 edge type: Edge<data, typeDiscriminator> — mirrors the TableNodeType pattern.
// JoinEdgeData is a `type` alias (not `interface`) to satisfy the
// Edge<D extends Record<string,unknown>> constraint (see Memory: @xyflow/react v12 typing gotchas).
export type JoinEdgeType = Edge<JoinEdgeData, 'joinEdge'>

export const JoinEdgeComponent = memo(function JoinEdgeComponent({
  id,
  sourceX,
  sourceY,
  targetX,
  targetY,
  sourcePosition,
  targetPosition,
  selected,
}: EdgeProps<JoinEdgeType>) {
  const { deleteElements } = useReactFlow()

  const [edgePath, labelX, labelY] = getBezierPath({
    sourceX,
    sourceY,
    sourcePosition,
    targetX,
    targetY,
    targetPosition,
  })

  return (
    <>
      <BaseEdge
        id={id}
        path={edgePath}
        style={{
          stroke: selected ? 'hsl(var(--primary))' : 'hsl(var(--muted-foreground))',
          strokeWidth: selected ? 2 : 1.5,
        }}
      />
      {selected && (
        <EdgeLabelRenderer>
          <button
            style={{
              position: 'absolute',
              transform: `translate(-50%, -50%) translate(${labelX}px,${labelY}px)`,
              pointerEvents: 'all',
            }}
            className="nodrag nopan flex h-5 w-5 items-center justify-center rounded-full bg-destructive text-xs text-destructive-foreground shadow hover:bg-destructive/90"
            onClick={(e) => {
              e.stopPropagation()
              deleteElements({ edges: [{ id }] })
            }}
            aria-label="Remove join"
          >
            ×
          </button>
        </EdgeLabelRenderer>
      )}
    </>
  )
})
```

Key decisions in this file:
- `JoinEdgeType = Edge<JoinEdgeData, 'joinEdge'>` mirrors the `TableNodeType` pattern from `TableNode.tsx`.
- Stroke styling uses inline `style` with CSS variables (`hsl(var(--primary))`), not Tailwind stroke utilities — SVG `stroke` CSS must override React Flow's default edge stroke, and inline styles are more reliable than `!stroke-*` Tailwind utilities on SVG `path` elements.
- Delete button is rendered only when `selected` is true. React Flow sets `selected` when the user clicks the edge. This matches the expected UX: click → select → delete option appears.
- `e.stopPropagation()` on the delete button prevents the click from deselecting the edge immediately.
- `deleteElements({ edges: [{ id }] })` from `useReactFlow()` triggers `onEdgesChange` with a `{type: 'remove'}` change, which Story 9.2's `handleEdgesChange` already intercepts and propagates to `builder_state` via `notify`.
- **Do NOT add** `onEdgeClick` or `setSelectedEdge` logic here — Story 9.4 adds the inspector via `onEdgeClick` on the parent canvas.

---

### Task 2 — Frontend: Update `QueryBuilderCanvas.tsx` (AC-1, AC-2, AC-4, AC-5)

Open `web/src/features/datasets/QueryBuilderCanvas.tsx`.

**Step 1 — Update imports.**

Update the `@xyflow/react` import block — add `addEdge` and `type Connection`:
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
  type EdgeChange,
  type Connection,
} from '@xyflow/react'
```

Add the JoinEdge import after the `TableNode` import line:
```typescript
import { JoinEdgeComponent, type JoinEdgeType } from '../../components/query-builder/JoinEdge'
```

**Step 2 — Register `edgeTypes` outside the component (closes deferred W3 from 9.2 review).**

After the existing `nodeTypes` line:
```typescript
const nodeTypes = { tableNode: TableNode }
```
Add immediately after:
```typescript
const edgeTypes = { joinEdge: JoinEdgeComponent }
```

Both must live outside the component to maintain a stable object reference. React Flow logs a warning and re-registers on every render if these are defined inside the component.

**Step 3 — Change `useEdgesState` generic from `Edge` to `JoinEdgeType`.**

Change:
```typescript
const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>(
  initialState.edges as Edge[],
)
```
to:
```typescript
const [edges, setEdges, onEdgesChange] = useEdgesState<JoinEdgeType>(
  initialState.edges as JoinEdgeType[],
)
```

**Step 4 — Update `handleEdgesChange` generic.**

Change:
```typescript
const handleEdgesChange = useCallback(
  (changes: EdgeChange<Edge>[]) => {
```
to:
```typescript
const handleEdgesChange = useCallback(
  (changes: EdgeChange<JoinEdgeType>[]) => {
```

(The rest of the `handleEdgesChange` body is unchanged.)

**Step 5 — Update `notify` signature.**

Change:
```typescript
const notify = useCallback(
  (currentNodes: TableNodeType[], currentEdges: Edge[]) => {
```
to:
```typescript
const notify = useCallback(
  (currentNodes: TableNodeType[], currentEdges: JoinEdgeType[]) => {
```

(The cast `currentEdges as JoinEdgeState[]` inside the body stays as is — it narrows to the serialized type.)

**Step 6 — Add `isValidConnection` callback after `notify` (AC-2, AC-4).**

```typescript
// AC-2: Reject same-node connections.
// AC-4: Reject duplicate node-pair connections (one edge per pair, per V1 spec).
const isValidConnection = useCallback(
  (connection: Connection) => {
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
```

**Step 7 — Add `onConnect` callback after `isValidConnection` (AC-1).**

```typescript
// AC-1: Create a JoinEdge when a valid handle-to-handle connection is completed.
// sourceHandle = column name on source node; targetHandle = column name on target node.
const onConnect = useCallback(
  (connection: Connection) => {
    if (!connection.source || !connection.target) return
    if (!connection.sourceHandle || !connection.targetHandle) return

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
```

Note: `sourceHandle` and `targetHandle` equal the column's `id` on the React Flow `<Handle>` element, which `TableNode.tsx` sets to `col.columnName`. So `sourceColumn` and `targetColumn` in `JoinEdgeData.data` will match the column names used by the SQL generator (Story 11.1).

**Step 8 — Update the `<ReactFlow>` JSX.**

Add `edgeTypes`, `isValidConnection`, and `onConnect` props.

Full updated `<ReactFlow>` block:
```tsx
return (
  <div ref={reactFlowWrapper} className="h-full w-full">
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

### Task 3 — Frontend: Build and test verification

- [x] `cd web && npx tsc -b --noEmit` → 0 errors
- [x] `cd web && npx vitest run` → same baseline as Story 9.2 (281 passed, 1 pre-existing i18n-lint failure); no new failures
- [x] Confirm no orphaned i18n keys introduced (none added in this story)
- [ ] Manual smoke test: open a builder-mode dataset in the browser; place two table nodes; drag from a left-side column handle on one node to a right-side column handle on the other; confirm a bezier edge appears; click the edge; confirm delete button appears at midpoint; click the delete button; confirm edge is removed — _pending user browser verification (no automated browser in dev workflow; consistent with 9.1/9.2 manual approach)_
- [ ] Manual smoke test (AC-2): attempt to drag from a handle to another handle on the SAME node — confirm the drag shows a red/invalid indicator and no edge is created — _pending user browser verification_
- [ ] Manual smoke test (AC-4): create an edge between two nodes, then try to create a second edge between the same two nodes — confirm the second attempt shows an invalid indicator — _pending user browser verification_

### Review Findings

- [x] [Review][Patch] `onConnect` missing same-node (AC-2) + duplicate-pair (AC-4) guards — spec §5 requires belt-and-suspenders: both `isValidConnection` AND `onConnect` must independently check. `onConnect` currently has no `connection.source === connection.target` guard and no `alreadyConnected` check; a programmatic or future-version `onConnect` call bypasses `isValidConnection` entirely. [QueryBuilderCanvas.tsx:onConnect] ✓ fixed
- [x] [Review][Patch] Delete button missing `type="button"` attribute — HTML `<button>` without explicit `type` defaults to `type="submit"` inside a `<form>`; if the dataset edit canvas is ever inside a form, clicking × submits the form instead of deleting the edge. [JoinEdge.tsx:~line 50] ✓ fixed
- [x] [Review][Defer] Stale `edges` closure race in `isValidConnection` + `onConnect` — theoretically two rapid connections (e.g., programmatic replay) could each see the same pre-update `edges` snapshot before React re-renders, allowing both to pass `isValidConnection` and reach `onConnect`. Single-threaded browser JS + React Flow event handling prevents this in normal user interaction. [QueryBuilderCanvas.tsx:isValidConnection+onConnect] — deferred, pre-existing
- [x] [Review][Defer] `notify(nodes, updatedEdges)` in `onConnect` uses `nodes` from useCallback closure — if a node change and an edge connection batch into the same React cycle, `nodes` could be the pre-update snapshot, emitting stale node positions to the parent. Theoretical in practice; `nodes` is in the useCallback dep array. [QueryBuilderCanvas.tsx:onConnect:~line 110] — deferred, pre-existing
- [x] [Review][Defer] `deleteElements()` Promise not awaited or `.catch()`'d — unhandled rejection if React Flow internally rejects. Fire-and-forget is the common React click-handler pattern; no user-visible impact in the success path. [JoinEdge.tsx:onClick:~line 57] — deferred, pre-existing

---

## Dev Notes

### §1 — What Story 9.2 Already Delivered and Deferred

Story 9.2 closed two deferred items from Story 9.1 (W1 stale closure, W2 edges ignored on mount). It also introduced `handleEdgesChange` (patch from code review) which detects `type: 'remove'` edge changes and calls `notify`. **This means the delete button's `deleteElements` call in `JoinEdge.tsx` is already handled** — no new deletion propagation logic is needed in the canvas.

Story 9.2 deferred item **W3** — "edgeTypes not registered for restored `type: 'joinEdge'` edges" — is explicitly closed by Step 2 (registering `edgeTypes`) in this story.

### §2 — The Handle Layout in `TableNode.tsx`

`TableNode.tsx` defines TWO handles per column:
- `type="source"` at `Position.Left` — the user drags FROM this handle to start a connection
- `type="target"` at `Position.Right` — the user drags TO this handle to complete a connection

React Flow enforces that source handles can only connect to target handles (not source→source or target→target). This means a connection always goes: left side of node A → right side of node B.

The `Connection` object received by `onConnect`:
- `source` = node ID of the node where the drag started (source handle)
- `target` = node ID of the node where the drag ended (target handle)
- `sourceHandle` = column name (the `id` on the `<Handle>` source element)
- `targetHandle` = column name (the `id` on the `<Handle>` target element)

These become `JoinEdgeState.sourceHandle` / `targetHandle`, which the SQL generator (Story 11.1 step 5) uses directly: `ON "<leftTable>"."<sourceHandle>" = "<targetTable>"."<targetHandle>"`.

### §3 — Why `JoinEdgeType` vs `JoinEdgeState`

Two related types exist — understand the distinction:
- **`JoinEdgeState`** (`builderState.ts`): the serialized form stored in JSONB. Only carries the fields the SQL generator needs: `id, source, target, sourceHandle, targetHandle, type, data`.
- **`JoinEdgeType`** (`JoinEdge.tsx`): `Edge<JoinEdgeData, 'joinEdge'>` — the React Flow canvas type. Extends `JoinEdgeState` with optional React Flow fields (`selected`, `hidden`, `animated`, etc.).

Use `JoinEdgeType` in canvas state (`useEdgesState`, `onConnect`, `handleEdgesChange`, etc.). Use `JoinEdgeState` as the serialized shape (the `as JoinEdgeState[]` cast in `notify` is correct — it asserts the subset we store).

This mirrors the `TableNodeType` / `TableNodeState` split already in the codebase.

### §4 — `JoinEdgeData` Must Remain a `type` Alias

`JoinEdgeData` in `builderState.ts` is already defined as `export type JoinEdgeData = { ... }`, not `interface`. **Do not change it to an interface.** React Flow v12 requires `Edge<D extends Record<string, unknown>>` — TypeScript only infers an implicit index signature for type aliases, not interfaces. Changing it to an interface would cause a TS2344 error. (See Memory: @xyflow/react v12 typing gotchas.)

### §5 — `isValidConnection` vs `onConnect` — Both Are Needed

`isValidConnection` provides real-time visual feedback: React Flow changes the handle/cursor appearance during drag to indicate whether the target is valid. It is called every time the cursor hovers over a potential target handle during a drag.

`onConnect` fires only when the user releases and completes the connection. The guards in `onConnect` (same-node, duplicate-pair) duplicate `isValidConnection` intentionally — belt-and-suspenders, since `onConnect` can theoretically be called programmatically.

### §6 — Edge ID Uniqueness

The edge ID `e-${source}:${sourceHandle}--${target}:${targetHandle}` is stable and deterministic for a given node-pair + column-pair. Since AC-4 prevents duplicate edges between the same node-pair, ID collisions are impossible in practice. The `:` and `--` separators are chosen to be distinct from the `_` and `-` characters that appear in table and column names.

### §7 — `deleteElements` From `JoinEdge.tsx` Uses `useReactFlow()`

`useReactFlow()` requires the component to be inside a `ReactFlowProvider`. `JoinEdgeComponent` is rendered by `<ReactFlow>`, which itself is inside `<ReactFlowProvider>` (from `QueryBuilderCanvas`'s outer wrapper). So `useReactFlow()` in `JoinEdgeComponent` is valid.

When `deleteElements({ edges: [{ id }] })` is called:
1. React Flow calls `onEdgesChange` with `[{ type: 'remove', id }]`
2. `handleEdgesChange` (Story 9.2) intercepts it, computes `survivingEdges`, calls `notify(nodes, survivingEdges)`
3. The parent's `builder_state.edges` is updated to exclude the removed edge

No additional deletion wiring is needed.

### §8 — `Edge` Type Not Removed from Canvas Imports

After Step 1, `Edge` is still imported from `@xyflow/react`. It remains needed for: the `handleEdgesChange` function body which uses `edges.filter(...)` (now typed as `JoinEdgeType[]`) but the filter callback operates on `Edge`-shaped objects. Actually — after the Step 3 change to `useEdgesState<JoinEdgeType>`, `edges` is now `JoinEdgeType[]`, so `Edge` may no longer be directly referenced. **Verify after Step 3–4** whether the `Edge` import can be removed. If TypeScript no longer reports it as used, remove it to keep the import clean. (The `type Edge` might still be indirectly needed; let `tsc` guide this.)

### §9 — Style Approach for BaseEdge

`BaseEdge` renders an SVG `<path>` element. The `style` prop applies directly as inline SVG styles. Using `hsl(var(--primary))` and `hsl(var(--muted-foreground))` directly matches the CSS variable names defined by the shadcn/ui theme system. This approach is more reliable than Tailwind `stroke-*` utilities, which may require specific SVG-specific utility classes and could conflict with React Flow's own inline stroke styles on the path element.

### §10 — Files to Create / Modify

```
NEW (frontend):
  web/src/components/query-builder/JoinEdge.tsx
    — JoinEdgeType export (Edge<JoinEdgeData, 'joinEdge'>)
    — JoinEdgeComponent: BaseEdge + delete button (shown when selected)

MODIFIED (frontend):
  web/src/features/datasets/QueryBuilderCanvas.tsx
    — import JoinEdgeComponent + JoinEdgeType
    — import type Connection from @xyflow/react
    — add const edgeTypes = { joinEdge: JoinEdgeComponent } (outside component)
    — useEdgesState generic: Edge → JoinEdgeType
    — handleEdgesChange generic: EdgeChange<Edge> → EdgeChange<JoinEdgeType>
    — notify signature: Edge[] → JoinEdgeType[]
    — add isValidConnection (same-node + duplicate-pair rejection)
    — add onConnect (create JoinEdgeType, update edges, notify)
    — <ReactFlow>: add edgeTypes, isValidConnection, onConnect props

NEW / MODIFIED (backend): none
NEW / MODIFIED (i18n): none
NEW / MODIFIED (types): none (JoinEdgeData, JoinEdgeState, JoinEdgeType all already defined)
```

### §11 — Test State Expectation

**Frontend** (`npx vitest run`): 281 passed / 1 pre-existing i18n-lint failure — same as Story 9.2 baseline. No new test files are required. The edge creation, rejection, and delete logic is covered by manual smoke testing (consistent with Story 9.1 and 9.2 approach for canvas UI interactions).

**Backend** (`dotnet test`): no backend changes; 918 passed / 2 pre-existing failures unchanged.

### §12 — Deferred Item Closed

This story closes **W3** from the Story 9.2 code review:
> `edgeTypes` not registered for restored `type:'joinEdge'` edges — `QueryBuilderCanvas.tsx:162`. Restored join edges render as the default edge type. Deferred to Story 9.3 (edge creation), where `edgeTypes` will be registered.

### §13 — Forward Compatibility (Story 9.4)

Story 9.4 (Join Property Inspector) will need to open a panel/popover when the user clicks a join edge. The standard React Flow way is via `onEdgeClick` prop on `<ReactFlow>`. Story 9.3 does NOT wire `onEdgeClick` — the click on the edge causes it to become `selected` (React Flow default), showing the delete button. Story 9.4 will add `onEdgeClick` to the `<ReactFlow>` JSX and a `selectedEdge: JoinEdgeType | null` state to the canvas component.

The `JoinEdgeComponent` already has access to `selected` via `EdgeProps`. No changes to `JoinEdge.tsx` will be needed for Story 9.4 to wire the inspector — Story 9.4 handles it at the canvas level.

### §14 — References

- [Source: `_bmad-output/planning-artifacts/epics.md` — Epic 9, Story 9.3 ACs]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.11 — AR-67 BuilderState canonical interface (JoinEdge shape)]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.12 — AR-68 React Flow integration (JoinEdge.tsx design)]
- [Source: `web/src/features/datasets/QueryBuilderCanvas.tsx` — current canvas (Story 9.2)]
- [Source: `web/src/components/query-builder/TableNode.tsx` — Handle layout (source=left, target=right)]
- [Source: `web/src/features/datasets/types/builderState.ts` — JoinEdgeData, JoinEdgeState types]
- [Source: `_bmad-output/implementation-artifacts/deferred-work.md` — W3 from Story 9.2 review (edgeTypes)]
- [Source: Memory: @xyflow/react v12 typing gotchas — Edge data must be `type` alias, not `interface`]

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (1M context)

### Debug Log References

- `npx tsc -b --noEmit` (web): initially failed with TS2322 on `isValidConnection` — React Flow v12's `IsValidConnection<T>` callback receives `T | Connection` (an Edge or a Connection), not just `Connection`. The spec's `(connection: Connection) => boolean` signature is too narrow. Fixed by typing the callback with `useCallback<IsValidConnection<JoinEdgeType>>(...)` and importing `type IsValidConnection`. After the fix: 0 errors.
- `npx vitest run` (web): 281 passed / 1 failed. The single failure is the pre-existing `i18n-lint.test.ts` (missing designer/theme/errors keys, unrelated to this story) — matches the Story 9.2 baseline documented in §11. No new failures.

### Completion Notes List

- **AC-1** — Created `JoinEdge.tsx` (`JoinEdgeType = Edge<JoinEdgeData, 'joinEdge'>`, `JoinEdgeComponent` rendering a bezier `BaseEdge`). Added `onConnect` to the canvas that builds a `JoinEdgeType` with `data.joinType: 'INNER'` and propagates via `notify`.
- **AC-2 / AC-4** — Added `isValidConnection`: rejects same-node connections (`source === target`) and duplicate node-pairs (either direction). React Flow shows the invalid indicator during drag and `onConnect`'s guards provide belt-and-suspenders.
- **AC-3** — Delete button renders at the edge midpoint via `EdgeLabelRenderer` only when `selected`; clicking calls `deleteElements`, which routes through Story 9.2's `handleEdgesChange` to update `builder_state.edges`.
- **AC-5** — Registered `const edgeTypes = { joinEdge: JoinEdgeComponent }` outside the component and passed `edgeTypes` to `<ReactFlow>`. This closes deferred item **W3** from the Story 9.2 review — restored `type:'joinEdge'` edges now render via the custom renderer.
- **Deviation from spec (§5/Step 6):** the spec's `isValidConnection` parameter type `(connection: Connection)` does not type-check against React Flow v12's `IsValidConnection<JoinEdgeType>` (the runtime passes `JoinEdgeType | Connection`). Used `useCallback<IsValidConnection<JoinEdgeType>>` and added the `type IsValidConnection` import. Body logic unchanged from spec.
- **§8 resolved:** the `type Edge` import was removed from `QueryBuilderCanvas.tsx` — after switching `useEdgesState`/`handleEdgesChange`/`notify` to `JoinEdgeType`, `Edge` is no longer referenced and `tsc` confirms it is unused.
- **Manual browser smoke tests** (the three drag/delete/reject interactions) are not executed in this automated dev workflow — flagged pending user verification, consistent with the manual-test approach used in Stories 9.1 and 9.2 for canvas UI.

### File List

- `web/src/components/query-builder/JoinEdge.tsx` (new)
- `web/src/features/datasets/QueryBuilderCanvas.tsx` (modified)

## Change Log

| Date       | Version | Description                                                                                  | Author |
| ---------- | ------- | -------------------------------------------------------------------------------------------- | ------ |
| 2026-06-04 | 0.1     | Implemented column-to-column join edge creation (AC-1..AC-5); closed Story 9.2 W3 (edgeTypes) | jukhan |
