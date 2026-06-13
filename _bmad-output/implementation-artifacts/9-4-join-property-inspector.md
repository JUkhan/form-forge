# Story 9.4: Join Property Inspector

Status: done

## Story

As a user building a Dataset,
I want to click a join edge and configure the join type in a property inspector,
so that the SQL JOIN clause is fully defined.

## Acceptance Criteria

**AC-1 — Clicking a join edge opens a property panel**
**Given** a join edge exists on the canvas
**When** I click it
**Then** a property panel opens (per FR-65 AC-1 / AR-68 `JoinInspector.tsx`) showing:
- (a) the two joined columns — for each side: table name + column name (e.g., `orders . customer_id`)
- (b) a join type selector with options: INNER / LEFT / RIGHT / FULL OUTER

**AC-2 — Newly created edge defaults to INNER**
**Given** the inspector opens for a newly created edge
**When** I inspect the default join type
**Then** it is INNER (per FR-65 AC-2)
(This is satisfied by Story 9.3, which initialises `data.joinType: 'INNER'` on every new edge; the inspector simply reflects this stored value.)

**AC-3 — Inspector shows Left / Right side labels**
**Given** the inspector shows the two join sides
**When** I inspect the side labels
**Then** each column line is labelled with the `side` field of its respective table node (`data.side`: `'left'` → "Left", `'right'` → "Right") (per FR-65 AC-3 / Story 9.5 will make the toggle interactive; the label is already stored in `TableNodeData.side`)

**AC-4 — Changing the join type persists to builder_state**
**Given** I change the join type in the selector
**When** I close the inspector (by clicking the canvas background, pressing Escape, or clicking the × button)
**Then** the selected join type is saved to `builder_state.edges[i].data.joinType` and emitted to the parent via `onChange` (per FR-65 AC-4 / AR-67)

---

## Tasks / Subtasks

### Task 1 — Frontend: Create `JoinInspector.tsx` (AC-1, AC-2, AC-3, AC-4)

Create `web/src/components/query-builder/JoinInspector.tsx`.

```typescript
import { X } from 'lucide-react'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import type { JoinEdgeType } from './JoinEdge'
import type { TableNodeType } from './TableNode'
import type { JoinType } from '../../features/datasets/types/builderState'

interface JoinInspectorProps {
  edge: JoinEdgeType
  nodes: TableNodeType[]
  onJoinTypeChange: (edgeId: string, joinType: JoinType) => void
  onClose: () => void
}

const JOIN_TYPE_OPTIONS: { value: JoinType; label: string }[] = [
  { value: 'INNER', label: 'INNER' },
  { value: 'LEFT', label: 'LEFT' },
  { value: 'RIGHT', label: 'RIGHT' },
  { value: 'FULL OUTER', label: 'FULL OUTER' },
]

export function JoinInspector({ edge, nodes, onJoinTypeChange, onClose }: JoinInspectorProps) {
  const sourceNode = nodes.find((n) => n.id === edge.source)
  const targetNode = nodes.find((n) => n.id === edge.target)

  // Graceful fallback if node was removed while inspector is open
  const sourceSide = sourceNode?.data.side === 'left' ? 'Left' : 'Right'
  const targetSide = targetNode?.data.side === 'left' ? 'Left' : 'Right'
  const sourceLabel = sourceNode
    ? `${sourceNode.data.tableName} . ${edge.sourceHandle}`
    : `(removed) . ${edge.sourceHandle}`
  const targetLabel = targetNode
    ? `${targetNode.data.tableName} . ${edge.targetHandle}`
    : `(removed) . ${edge.targetHandle}`

  return (
    <div
      className="nodrag nopan absolute right-4 top-4 z-50 w-64 rounded-lg border border-border bg-card text-card-foreground shadow-lg"
      role="complementary"
      aria-label="Join inspector"
    >
      {/* Header */}
      <div className="flex items-center justify-between border-b border-border px-3 py-2">
        <span className="text-sm font-semibold">Join</span>
        <button
          type="button"
          onClick={onClose}
          className="flex h-5 w-5 items-center justify-center rounded text-muted-foreground hover:bg-overlay-hover hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          aria-label="Close inspector"
        >
          <X className="h-3.5 w-3.5" />
        </button>
      </div>

      <div className="space-y-3 p-3">
        {/* Joined columns — AC-1(a), AC-3 */}
        <div className="space-y-1.5">
          <p className="text-xs font-medium text-muted-foreground">Joined columns</p>
          <div className="space-y-1 rounded-md bg-muted px-2.5 py-2 text-xs">
            <div className="flex items-center gap-1.5">
              <span className="shrink-0 rounded bg-background px-1 py-0.5 text-xs font-medium text-muted-foreground">
                {sourceSide}
              </span>
              <span className="truncate font-mono">{sourceLabel}</span>
            </div>
            <div className="border-t border-border pt-1 flex items-center gap-1.5">
              <span className="shrink-0 rounded bg-background px-1 py-0.5 text-xs font-medium text-muted-foreground">
                {targetSide}
              </span>
              <span className="truncate font-mono">{targetLabel}</span>
            </div>
          </div>
        </div>

        {/* Join type selector — AC-1(b), AC-2, AC-4 */}
        <div className="space-y-1.5">
          <p className="text-xs font-medium text-muted-foreground">Join type</p>
          <Select
            value={edge.data?.joinType ?? 'INNER'}
            onValueChange={(value) => onJoinTypeChange(edge.id, value as JoinType)}
          >
            <SelectTrigger className="w-full" size="sm" aria-label="Join type">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {JOIN_TYPE_OPTIONS.map((opt) => (
                <SelectItem key={opt.value} value={opt.value}>
                  {opt.label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
      </div>
    </div>
  )
}
```

Key decisions:
- Positioned `absolute right-4 top-4` within the canvas wrapper `div` (which already has `relative`-compatible layout). This avoids any React Flow coordinate math and requires no changes to `JoinEdge.tsx`.
- `nodrag nopan` classes prevent React Flow from treating pointer events on the inspector as canvas drag/pan gestures.
- `role="complementary"` / `aria-label="Join inspector"` for accessibility.
- `edge.data?.joinType ?? 'INNER'` uses optional chaining because `Edge<D>` from @xyflow/react v12 allows `data` to be `D | undefined` on restored edges before the canvas fully hydrates. In practice `data` is always set (Story 9.3 initialises it), but the guard is defensive.
- The `(removed)` fallback label handles the edge case where a node is removed while the inspector is open. This prevents a runtime error from `sourceNode?.data.tableName` being undefined.
- Uses `Select` / `SelectTrigger` / `SelectContent` / `SelectItem` from `@/components/ui/select` (shadcn/ui component that already exists in the project).

---

### Task 2 — Frontend: Update `QueryBuilderCanvas.tsx` (AC-1, AC-3, AC-4)

Open `web/src/features/datasets/QueryBuilderCanvas.tsx`.

**Step 1 — Add `useState` import.**

`useCallback` and `useRef` are already imported from `'react'`. Add `useState`:

Change:
```typescript
import { useCallback, useRef } from 'react'
```
to:
```typescript
import { useCallback, useRef, useState } from 'react'
```

**Step 2 — Add `JoinInspector` import.**

After the existing `JoinEdgeComponent` import line:
```typescript
import { JoinEdgeComponent, type JoinEdgeType } from '../../components/query-builder/JoinEdge'
```
Add immediately after:
```typescript
import { JoinInspector } from '../../components/query-builder/JoinInspector'
```

**Step 3 — Add `JoinType` to the `builderState` import.**

Change:
```typescript
import type {
  BuilderState,
  ColumnSelection,
  JoinEdgeState,
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
  JoinType,
  TableNodeData,
  TableNodeState,
} from './types/builderState'
```

**Step 4 — Add `selectedEdge` state inside `QueryBuilderCanvasInner`.**

After the `extraStateRef` declaration (line ~52), add:
```typescript
const [selectedEdge, setSelectedEdge] = useState<JoinEdgeType | null>(null)
```

**Step 5 — Add `onEdgeClick` callback after `onConnect`.**

```typescript
// AC-1: Open the JoinInspector when the user clicks a join edge.
const onEdgeClick = useCallback(
  (_event: React.MouseEvent, edge: JoinEdgeType) => {
    setSelectedEdge(edge)
  },
  [],
)
```

**Step 6 — Add `onPaneClick` callback after `onEdgeClick`.**

```typescript
// Close the inspector when the user clicks the canvas background.
const onPaneClick = useCallback(() => {
  setSelectedEdge(null)
}, [])
```

**Step 7 — Add `handleJoinTypeChange` callback after `onPaneClick`.**

```typescript
// AC-4: Update joinType in edges state and emit via notify.
// Also refreshes selectedEdge so the inspector reflects the new value immediately.
const handleJoinTypeChange = useCallback(
  (edgeId: string, joinType: JoinType) => {
    const updatedEdges = edges.map((e) =>
      e.id === edgeId ? { ...e, data: { ...e.data, joinType } } : e,
    )
    setEdges(updatedEdges)
    notify(nodes, updatedEdges)
    setSelectedEdge((prev) =>
      prev?.id === edgeId ? { ...prev, data: { ...prev.data, joinType } } : prev,
    )
  },
  [edges, nodes, setEdges, notify],
)
```

**Step 8 — Update the `<ReactFlow>` JSX to wire `onEdgeClick` and `onPaneClick`.**

Add `onEdgeClick` and `onPaneClick` props to the existing `<ReactFlow>` block:
```tsx
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
  fitView
>
  <Background />
  <Controls />
  <MiniMap />
</ReactFlow>
```

**Step 9 — Add `JoinInspector` to the canvas wrapper `div`, and ensure the wrapper has `relative` positioning.**

Change the return block from:
```tsx
return (
  <div ref={reactFlowWrapper} className="h-full w-full">
```
to:
```tsx
return (
  <div ref={reactFlowWrapper} className="relative h-full w-full">
```

Then add the inspector after `</ReactFlow>` and before `</div>`:
```tsx
  </ReactFlow>
  {selectedEdge && (
    <JoinInspector
      edge={selectedEdge}
      nodes={nodes}
      onJoinTypeChange={handleJoinTypeChange}
      onClose={() => setSelectedEdge(null)}
    />
  )}
</div>
```

The full updated return block:
```tsx
return (
  <div ref={reactFlowWrapper} className="relative h-full w-full">
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
      fitView
    >
      <Background />
      <Controls />
      <MiniMap />
    </ReactFlow>
    {selectedEdge && (
      <JoinInspector
        edge={selectedEdge}
        nodes={nodes}
        onJoinTypeChange={handleJoinTypeChange}
        onClose={() => setSelectedEdge(null)}
      />
    )}
  </div>
)
```

---

### Task 3 — Frontend: Build and test verification

- [x] `cd web && npx tsc -b --noEmit` → 0 errors ✅
- [x] `cd web && npx vitest run` → 281 passed / 1 pre-existing i18n-lint failure — same baseline as Story 9.3. No new failures. ✅
- [x] Confirm no orphaned i18n keys introduced (none added in this story — consistent with 9.1/9.2/9.3 pattern for canvas UI) ✅
- [ ] Manual smoke test (AC-1): open a builder-mode dataset; place two table nodes; draw a join edge; click the edge; confirm the inspector panel appears in the top-right of the canvas showing both column labels and the join type selector. _pending user browser verification_
- [ ] Manual smoke test (AC-2): confirm the join type selector defaults to "INNER" on a freshly created edge. _pending user browser verification_
- [ ] Manual smoke test (AC-3): confirm the side label ("Left" / "Right") reflects `data.side` on each table node. _pending user browser verification_
- [ ] Manual smoke test (AC-4): change the join type to "LEFT"; click the canvas background to close the inspector; click the edge again to re-open; confirm the join type is still "LEFT". _pending user browser verification_
- [ ] Manual smoke test (close behavior): click canvas background → inspector closes; drag a new node → inspector closes (pane click fires); re-click the edge → inspector re-opens. _pending user browser verification_
- [x] Manual smoke test (delete while open): **DECISION — implemented §8 Option A.** `handleEdgesChange` now clears `selectedEdge` when the open edge is removed, so the inspector closes on delete rather than lingering with a stale edge. _Code-verified; final browser confirmation pending user verification._

### Review Findings (code review 2026-06-04)

_Three adversarial layers (Blind Hunter, Edge Case Hunter, Acceptance Auditor). Auditor: all ACs (AC-1..AC-4) PASS, §4/§6/§8/§9/§5/§12 satisfied, `JoinEdge.tsx` untouched, TS narrowing fix correct & behavior-neutral. Final tally: 1 patch (applied), 2 deferred, 7 dismissed as noise (the 1 decision-needed was resolved → patch)._

- [x] [Review][Patch] **Node deletion while inspector open now closes it (was decision-needed)** [`QueryBuilderCanvas.tsx` `handleNodesChange`] — **Decision (jukhan, 2026-06-04): close for consistency with §8 Option A.** Deleting a *node* cascades edge removal through `handleNodesChange` (not `handleEdgesChange`), so the §8 edge-deletion close did not fire. **Applied:** `handleNodesChange` now clears `selectedEdge` when a removed node's id matches the open edge's `source` or `target`, so edge-delete and node-delete behave identically. Verified: `tsc -b --noEmit` → 0 errors; `vitest run` → 281 passed (unchanged baseline). This also moots the removed-node "Right" badge cosmetic (the inspector closes instead of rendering it).
- [x] [Review][Defer] **`selectedEdge` stored as a click-time snapshot, not derived from live `edges` by id** [`QueryBuilderCanvas.tsx` `onEdgeClick`/`selectedEdge`] — deferred, no current reproducible bug. The only edge-data mutation path (`handleJoinTypeChange`) manually re-syncs `selectedEdge`, so the Select never goes stale today; but any future mutation path (undo/redo, state restore) would desync. Robustness refactor: store `selectedEdgeId` and derive the edge from `edges`/`nodes` at render — would also dissolve the decision-needed item above. (blind+edge)
- [x] [Review][Defer] **Self-join can render two visually identical inspector rows** [`JoinInspector.tsx` `sourceLabel`/`targetLabel`] — deferred, future epic. A self-join (same table dragged twice) shows both rows as `table . column` with the same table name; only the Left/Right badge disambiguates. Per-table column aliasing arrives in Epic 10 (Story 10.2). (edge)

#### Dismissed (noise / false-positive / spec-acknowledged)

- `handleJoinTypeChange` skips edges with falsy `data` — `data` is always set by Story 9.3 `onConnect`; the `&& e.data` guard is a TS-narrowing no-op.
- `onEdgeClick`/`onPaneClick` could close the inspector immediately — confirmed handled; React Flow fires `onPaneClick` only on background clicks, not edge clicks (Edge Case Hunter verified; AC-1 works).
- Manual `edges.map` vs functional `setEdges(prev => …)` — style nit; correct under React's single-threaded model and consistent with the existing `onConnect` pattern.
- `edge.sourceHandle`/`targetHandle` could render `". null"` — handles are guaranteed non-null by `onConnect`'s early-return guards.
- `value as JoinType` cast — safe by construction; option values come from the typed `JOIN_TYPE_OPTIONS`.
- AC-4 wording mentions "pressing Escape" but no Escape handler — Dev Notes §6 explicitly defers Escape to Story 9.5+; spec-acknowledged.
- Removed-node side badge shows "Right" — cosmetic, matches the spec's literal code; **now mooted** by the applied patch (the inspector closes on node deletion, so this path no longer renders).

---

## Dev Notes

### §1 — Where Story 9.3 Left Off for Story 9.4

Story 9.3 (`_bmad-output/implementation-artifacts/9-3-column-to-column-join-edge-creation.md`) explicitly deferred Story 9.4 setup in its §13:

> Story 9.4 (Join Property Inspector) will need to open a panel/popover when the user clicks a join edge. The standard React Flow way is via `onEdgeClick` prop on `<ReactFlow>`. Story 9.3 does NOT wire `onEdgeClick` — the click on the edge causes it to become `selected` (React Flow default), showing the delete button. Story 9.4 will add `onEdgeClick` to the `<ReactFlow>` JSX and a `selectedEdge: JoinEdgeType | null` state to the canvas component.
>
> The `JoinEdgeComponent` already has access to `selected` via `EdgeProps`. No changes to `JoinEdge.tsx` will be needed for Story 9.4 to wire the inspector — Story 9.4 handles it at the canvas level.

This means: **no changes to `JoinEdge.tsx`**. All new logic is in `QueryBuilderCanvas.tsx` and the new `JoinInspector.tsx`.

### §2 — Why a Panel, Not a Radix Popover Anchored to the Edge

The `JoinInspector` is rendered as an absolutely-positioned panel in the canvas wrapper div, not as a Radix `<Popover>` anchored to the edge midpoint. The reasons:

1. **Edge midpoint coordinates are only available inside `JoinEdge.tsx`** (`labelX, labelY` from `getBezierPath`). To pass them up to the canvas level for popover positioning would require: (a) storing them in component state visible to the canvas, or (b) modifying `JoinEdge.tsx`. Both approaches violate the "no changes to JoinEdge.tsx" constraint from Story 9.3.

2. **A fixed `absolute top-4 right-4` panel is a valid UX pattern** for canvas inspectors. It avoids edge coordinate tracking, is trivially responsive, and doesn't occlude the edge or adjacent table nodes.

3. **`onEdgeClick` fires at the canvas level** with `(event, edge)`. The mouse `event` has `clientX/clientY`, but converting these to `reactFlowWrapper`-relative offsets for precise popover placement adds complexity without meaningful UX benefit.

### §3 — Interaction Between `selected` (Edge Highlight) and `selectedEdge` (Inspector)

React Flow maintains its own `selected` state on edges. When the user clicks an edge:
1. React Flow fires `onEdgeClick` → our handler sets `selectedEdge` to open the inspector.
2. React Flow simultaneously marks the edge as `selected=true` → `JoinEdgeComponent` renders the delete button at the midpoint.

Both behaviors are active at the same time: the inspector opens AND the delete button appears. This is intentional — the user can both inspect the join type and delete the edge from the same click.

Clicking the canvas background (pane):
1. React Flow fires `onPaneClick` → our handler sets `selectedEdge(null)` → inspector closes.
2. React Flow simultaneously deselects all edges → `selected=false` → delete button disappears.

Clicking a different edge:
1. React Flow fires `onEdgeClick` → our handler replaces `selectedEdge` with the new edge → inspector updates.
2. React Flow deselects the old edge and selects the new one.

### §4 — `handleJoinTypeChange` Must Also Update `selectedEdge`

When the user changes the join type in the inspector, `handleJoinTypeChange`:
1. Updates `edges` state (the source of truth for the canvas).
2. Calls `notify(nodes, updatedEdges)` to emit to the parent.
3. Updates `selectedEdge` state to reflect the new joinType.

Step 3 is critical. Without it, the inspector's `edge` prop still holds the stale `joinType` value, so the `Select` component would reset to the old value on the next render (the stale `edge` object is passed as a prop). By updating `selectedEdge` in the same callback, the inspector's displayed value stays in sync with the actual edge data.

### §5 — `nodrag nopan` on the Inspector Panel

The inspector div carries `nodrag nopan` CSS classes. These are React Flow's built-in escape hatches: pointer events on elements with these classes are not intercepted by React Flow's drag and pan handlers. Without them, clicking/scrolling inside the inspector would trigger canvas pan or node drag.

This is the same pattern used by the delete button in `JoinEdge.tsx` (`className="nodrag nopan ..."`).

### §6 — Closing the Inspector

The inspector closes in three ways:
1. **× button**: `onClose={() => setSelectedEdge(null)}` called by the button in `JoinInspector`.
2. **Canvas background click**: `onPaneClick` sets `setSelectedEdge(null)`.
3. **Implicit**: clicking a different edge replaces `selectedEdge` with the new edge (inspector content updates, not strictly "closed").

There is no explicit Escape key handler in this story. React Flow does not natively propagate Escape as a pane click. If Escape-to-close is desired, it requires a `useEffect` with a `keydown` listener on `window`; this is a story 9.5+ polish item.

### §7 — TypeScript: `onEdgeClick` Signature

React Flow v12's `onEdgeClick` prop type is:
```typescript
onEdgeClick?: (event: React.MouseEvent, edge: T) => void
```
where `T` is the generic edge type. Since our `<ReactFlow>` infers `T = JoinEdgeType` from `edges`, `onEdgeClick` receives a `JoinEdgeType` — no cast needed.

However, `useCallback` must be typed to match:
```typescript
const onEdgeClick = useCallback(
  (_event: React.MouseEvent, edge: JoinEdgeType) => {
    setSelectedEdge(edge)
  },
  [],
)
```
The empty dependency array `[]` is correct — `setSelectedEdge` from `useState` is a stable reference (does not need to be in deps).

### §8 — Edge Deletion While Inspector is Open (Implementation Decision Required)

When the user deletes a join edge while the inspector is open, `handleEdgesChange` fires (due to `deleteKeyCode` or the delete button in `JoinEdge.tsx`). This updates `edges` and calls `notify`, but does NOT automatically clear `selectedEdge`.

Result: the inspector remains open displaying a stale edge that no longer exists in `edges`. The `JoinInspector`'s `nodes.find((n) => n.id === edge.source)` calls still work (nodes haven't changed), but calling `onJoinTypeChange(edgeId, ...)` would update `edges.map(...)` with no match — the updatedEdges array would be identical to the current `edges` (edge already gone), so `notify` would be called with no actual change. This is harmless but confusing UX.

**The dev agent MUST decide at implementation time which approach to use:**

**Option A — Clear `selectedEdge` in `handleEdgesChange` (recommended)**
```typescript
const handleEdgesChange = useCallback(
  (changes: EdgeChange<JoinEdgeType>[]) => {
    onEdgesChange(changes)
    const removedEdgeIds = new Set(
      changes.filter((c) => c.type === 'remove').map((c) => c.id),
    )
    if (removedEdgeIds.size > 0) {
      const survivingEdges = edges.filter((e) => !removedEdgeIds.has(e.id))
      notify(nodes, survivingEdges)
      // Close inspector if the selected edge was deleted
      setSelectedEdge((prev) => (prev && removedEdgeIds.has(prev.id) ? null : prev))
    }
  },
  [onEdgesChange, nodes, edges, notify],
)
```

**Option B — Do nothing (accept stale inspector)**
Leave `handleEdgesChange` as-is from Story 9.3. The inspector stays open showing the deleted edge. The UX is slightly awkward but harmless. The user can close with the × button or by clicking the canvas.

**Recommendation:** Option A. The stale-inspector UX is surprising. The fix is two lines and has no downside.

### §9 — `relative` on the Canvas Wrapper

Step 9 adds `relative` to the wrapper `div` class:
```tsx
<div ref={reactFlowWrapper} className="relative h-full w-full">
```

Without `relative`, the `absolute top-4 right-4` position on `JoinInspector` would be computed relative to the nearest positioned ancestor — which could be any parent element in the tree, making the inspector appear far off-screen. `relative` anchors absolute children to the canvas wrapper div.

Check that no existing CSS on the parent chain already supplies a positioned ancestor. If the canvas is already inside a `relative`/`absolute`/`fixed` container (e.g., the dataset edit page layout), then adding `relative` is redundant but harmless.

### §10 — AC-3 and Story 9.5 Interaction

AC-3 says the inspector side label "matches the Left/Right toggle on each respective table node (Story 9.5)."

Story 9.5 (Table Left/Right Designation) will make the side toggle interactive. After Story 9.5, a user can change a node's `data.side` value. When they then click a join edge, the inspector re-reads `sourceNode.data.side` from the current `nodes` array — so the side label automatically reflects the latest designation without any changes to Story 9.4's code.

This works because `JoinInspector` receives the live `nodes` array from the canvas (not a snapshot), so re-opening the inspector after a side toggle in Story 9.5 will show the updated label.

### §11 — No Backend Changes

Story 9.4 is entirely frontend. The `builder_state.edges[i].data.joinType` field was already defined in `builderState.ts` (type `JoinType = 'INNER' | 'LEFT' | 'RIGHT' | 'FULL OUTER'`) and already persisted by the existing Dataset save path (Story 11.2 adds full builder-state persistence; for now `notify`/`onChange` propagates the updated state to the parent which is responsible for saving). No new API endpoints, no new backend validators, no new migrations.

### §12 — `Select` Component Import Path

The `Select` family of components is imported from `@/components/ui/select` (the `@` alias resolves to `web/src` per the project's Vite/TypeScript config). The component exists at `web/src/components/ui/select.tsx` (verified). Use named imports:
```typescript
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
```

Do NOT import from `radix-ui` directly — always use the shadcn/ui wrapper components, which apply the project's Tailwind theme tokens.

### §13 — Test State Expectation

**Frontend** (`npx vitest run`): 281 passed / 1 pre-existing i18n-lint failure — same baseline as Stories 9.1/9.2/9.3. No new test files required. Canvas click/inspector interactions are covered by manual smoke testing.

**Backend** (`dotnet test`): no backend changes; 918 passed / 2 pre-existing failures unchanged.

### §14 — Files to Create / Modify

```
NEW (frontend):
  web/src/components/query-builder/JoinInspector.tsx
    — JoinInspector component: displays joined columns (with side labels) + join type Select

MODIFIED (frontend):
  web/src/features/datasets/QueryBuilderCanvas.tsx
    — import useState from 'react'
    — import JoinInspector
    — import JoinType from builderState
    — add selectedEdge: JoinEdgeType | null state
    — add onEdgeClick callback (sets selectedEdge)
    — add onPaneClick callback (clears selectedEdge)
    — add handleJoinTypeChange callback (updates edges + selectedEdge + notify)
    — optionally update handleEdgesChange to clear selectedEdge on edge removal (§8 Option A)
    — wrapper div: add 'relative' class
    — <ReactFlow>: add onEdgeClick + onPaneClick props
    — wrapper div: render <JoinInspector> when selectedEdge is set

NEW / MODIFIED (backend): none
NEW / MODIFIED (i18n): none
NEW / MODIFIED (types): none (JoinType, JoinEdgeType, TableNodeType all already defined)
```

### §15 — Deferred Items from Story 9.3 Relevant Here

From `_bmad-output/implementation-artifacts/deferred-work.md`:

- **W3 (9.3) — `deleteElements()` Promise not awaited** — unrelated to Story 9.4; remains deferred.
- **W1 (9.3) / W2 (9.3)** — closure race conditions in `isValidConnection`/`onConnect` — unrelated to Story 9.4; remain deferred.

The only deferred item that Story 9.4 should address is **§8 above** — clearing `selectedEdge` when the open edge is deleted (Option A recommended).

### §16 — References

- [Source: `_bmad-output/planning-artifacts/epics.md` — Epic 9, Story 9.4 ACs (FR-65)]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.12 — AR-68 React Flow integration (`JoinInspector.tsx` design)]
- [Source: `_bmad-output/planning-artifacts/architecture.md` §6.11 — AR-67 BuilderState schema (`JoinEdge.data.joinType`)]
- [Source: `_bmad-output/implementation-artifacts/9-3-column-to-column-join-edge-creation.md` §13 — Story 9.4 forward compatibility notes]
- [Source: `web/src/features/datasets/QueryBuilderCanvas.tsx` — current canvas after Story 9.3]
- [Source: `web/src/components/query-builder/JoinEdge.tsx` — JoinEdgeType, JoinEdgeComponent]
- [Source: `web/src/components/query-builder/TableNode.tsx` — TableNodeType, TableNodeData.side]
- [Source: `web/src/features/datasets/types/builderState.ts` — JoinType, JoinEdgeData]
- [Source: `web/src/components/ui/select.tsx` — Select, SelectContent, SelectItem, SelectTrigger, SelectValue]
- [Source: `web/src/components/ui/popover.tsx` — Popover (not used here; documented for future if popover positioning is needed)]
- [Source: Memory: @xyflow/react v12 typing gotchas — Edge data must be `type` alias, not `interface`]

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (1M context) — BMad dev-story workflow

### Debug Log References

- **TS2345 on first `tsc -b`** — the story's literal Step-7 code `{ ...e.data, joinType }` does not compile under this repo's TS config: `JoinEdgeType.data` is `JoinEdgeData | undefined` (xyflow's `Edge.data` is optional), so spreading it yields optional `sourceColumn?`/`targetColumn?`, which is not assignable to the required `JoinEdgeData`. Fixed by narrowing `data` before the spread (`e.id === edgeId && e.data ? ... : e` and the matching `prev?.id === edgeId && prev.data` for `setSelectedEdge`). No cast used; the guard is a runtime no-op since `data` is always set by Story 9.3. After the fix: `npx tsc -b --noEmit` → 0 errors.

### Completion Notes List

- **AC-1** — `onEdgeClick` wired on `<ReactFlow>` sets `selectedEdge`; `JoinInspector` renders an absolutely-positioned panel (top-right) in the now-`relative` canvas wrapper, showing both joined columns (`table . column`) and the INNER/LEFT/RIGHT/FULL OUTER selector.
- **AC-2** — Inspector reflects the stored `data.joinType`, which Story 9.3 initialises to `'INNER'` on every new edge; the `Select` value defaults to `'INNER'` via `edge.data?.joinType ?? 'INNER'`.
- **AC-3** — Each column line is labelled `Left`/`Right` from the respective node's `data.side`. The inspector reads the live `nodes` array, so it will pick up Story 9.5's interactive side toggle automatically.
- **AC-4** — `handleJoinTypeChange` updates `edges`, emits via `notify(...)`, and refreshes `selectedEdge` so the displayed value stays in sync; close via × button, canvas-background (`onPaneClick`), or selecting another edge.
- **Implementation decision (Dev Notes §8): chose Option A** — `handleEdgesChange` clears `selectedEdge` when the open edge is removed, so deleting an edge while the inspector is open closes it instead of leaving a stale view. Node-removal-while-open is still handled by the inspector's `(removed)` graceful fallback (per story design).
- **No changes to `JoinEdge.tsx`** — confirmed unnecessary, per Story 9.3 §13 forward-compat notes. No backend, i18n, or type changes (all types pre-existed).
- **Verification:** `tsc -b --noEmit` → 0 errors; `vitest run` → 281 passed / 1 pre-existing i18n-lint failure (unchanged baseline, no new keys added). Manual browser smoke tests (AC-1..AC-4 + close behaviour) remain for user verification — they require a running app/browser.

### File List

- `web/src/components/query-builder/JoinInspector.tsx` — **NEW** — JoinInspector panel: joined columns with Left/Right side labels + join-type `Select`.
- `web/src/features/datasets/QueryBuilderCanvas.tsx` — **MODIFIED** — import `useState`, `JoinInspector`, `JoinType`; add `selectedEdge` state; add `onEdgeClick`/`onPaneClick`/`handleJoinTypeChange` callbacks; clear `selectedEdge` on edge removal in `handleEdgesChange` (§8 Option A); add `relative` to wrapper; wire `onEdgeClick`/`onPaneClick` and render `<JoinInspector>`.

## Change Log

| Date       | Version | Description                                                                 | Author |
| ---------- | ------- | --------------------------------------------------------------------------- | ------ |
| 2026-06-04 | 1.0     | Implemented Story 9.4 Join Property Inspector (AC-1..AC-4); §8 Option A; TS-safe `data` narrowing fix. Status → review. | Amelia (dev-story) |
| 2026-06-04 | 1.1     | Code review (3 adversarial layers): all ACs PASS. Applied 1 patch — close inspector on node deletion (mirror §8 Option A into `handleNodesChange`). 2 LOW findings deferred (snapshot fragility, self-join labels). tsc 0 / vitest 281. Status → done. | Code review |
