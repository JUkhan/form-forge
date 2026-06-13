# Story 10.2: Aggregate Function and Custom Column Alias

Status: done

## Story

As a user building a Dataset,
I want to set an aggregate function and a custom alias on any selected column,
so that I can write grouped aggregation queries and control how columns are labelled in the result set.

## Acceptance Criteria

**AC-1 — Aggregate dropdown and Alias input appear only for checked columns**
**Given** a selected (checked) column row in a table node (per AR-68 `TableNode.tsx`)
**When** the row renders
**Then** it shows an "Aggregate" dropdown (None / COUNT / SUM / AVG / MIN / MAX) and an "Alias" text input below the column name line (per FR-67 J-2 AC-1).
**Scope note:** Controls appear **only for checked columns**. Unchecked columns have no SELECT presence; their aggregate/alias controls are hidden. Unchecking a column hides the controls but does **not** reset the stored `aggregate`/`alias` values — they survive unchecking (see §4).

**AC-2 — SQL emission with aggregate (deferred to Story 11.1)**
**Given** I set aggregate to SUM on a column
**When** the SQL generator runs (Story 11.1)
**Then** it emits `SUM("table"."col") AS "alias"` in the SELECT clause (per FR-67 J-2 AC-2).
**Scope note:** SQL generation is Story 11.1. Story 10.2 stores `aggregate` in `builder_state` accurately so Story 11.1 can read it.

**AC-3 — GROUP BY auto-derivation (deferred to Story 11.1)**
**Given** any aggregate is set on any column
**When** the SQL generator runs (Story 11.1)
**Then** it auto-derives a GROUP BY clause listing all non-aggregated selected columns (per FR-67 J-2 AC-3).
**Scope note:** Pure SQL-gen concern (Story 11.1). This story persists per-column aggregate values correctly.

**AC-4 — Empty alias defaults to `{table}_{column}`; alias validated inline against FR-57**
**Given** an alias field
**When** I leave it empty
**Then** the input shows placeholder text `{tableName}_{columnName}` and the stored `alias` value remains `''` (Story 11.1's SQL generator derives `{tableName}_{columnName}` and handles disambiguation at generation time) (per FR-67 J-2 AC-4).
**Given** I type a non-empty alias that violates FR-57 identifier rules (lowercase letters a–z, digits 0–9, underscores `_`; must start with letter or underscore; max 63 chars)
**When** I type the invalid character
**Then** an inline error message appears below the alias input immediately.
**Scope note:** Disambiguation ("avoid duplicates") is a Story 11.1 SQL-gen concern. This story delivers: (a) `isValidAlias(alias)` pure helper in `builderState.ts`, (b) inline error in `TableNode.tsx`. Blocking Save on an invalid alias is deferred to Story 11.2 (no Save button exists yet).

**AC-5 — Aggregate + alias persist to builder_state**
**Given** I set aggregate + alias values on a checked column
**When** the context handler fires (on change)
**Then** they are stored in `builder_state.nodes[*].data.columns[*].aggregate` and `builder_state.nodes[*].data.columns[*].alias` (per FR-67 J-2 AC-5).
**Scope note:** `builder_state` is not persisted to the server until Story 11.2. This story keeps the in-memory `builderState` (held by `datasets_.$id.tsx`) correct via `notify()` — same pattern as Story 10.1.

---

## Tasks / Subtasks

### Task 1 — Frontend: Add `isValidAlias` + `ALIAS_PATTERN` to `builderState.ts` (AC-4)

Open `web/src/features/datasets/types/builderState.ts`. After `getColumnSelectionValidationError` (end of file), add:

```typescript
// FR-57 identifier rules for column aliases (same as DatasetName / Decision 1.1):
// lowercase letters, digits, underscores; must start with letter or underscore; max 63 chars.
// Empty alias is valid — it means "use the default {table}_{column} at SQL generation time"
// (Story 11.1). Client-side validation only; server re-validates via SafeIdentifier.Create().
export const ALIAS_PATTERN = /^[a-z_][a-z0-9_]*$/

export function isValidAlias(alias: string): boolean {
  if (alias === '') return true
  return ALIAS_PATTERN.test(alias) && alias.length <= 63
}
```

No type changes — `ColumnSelection` already has `aggregate: AggregateFunction` and `alias: string`, both initialized on drop in `QueryBuilderCanvas.onDrop`.

### Task 2 — Frontend: Export `ColumnAggregateContext` + `ColumnAliasContext` from `TableNode.tsx` and add expanded aggregate/alias row (AC-1, AC-4, AC-5)

Open `web/src/components/query-builder/TableNode.tsx`.

**Step 1 — Update imports.** The current import is:
```typescript
import type { TableNodeData, TableSide } from '../../features/datasets/types/builderState'
```
Replace with:
```typescript
import { isValidAlias, type AggregateFunction, type TableNodeData, type TableSide } from '../../features/datasets/types/builderState'
```
`isValidAlias` is a value (function) — it cannot use `import type`. `AggregateFunction` is a type used for the `e.target.value as AggregateFunction` cast.

**Step 2 — Export the two new contexts** directly after `ColumnCheckContext` (line 24):

```typescript
// Story 10.2: canvas provides aggregate-change handler via context so it can emit
// to the parent via notify(). Same stable-ref pattern as ColumnCheckContext — kept
// out of node `data` because data is serialized into builder_state (not serializable).
export const ColumnAggregateContext = createContext<
  (nodeId: string, columnName: string, aggregate: AggregateFunction) => void
>(() => {})

// Story 10.2: canvas provides alias-change handler via context. Same rationale.
export const ColumnAliasContext = createContext<
  (nodeId: string, columnName: string, alias: string) => void
>(() => {})
```

**Step 3 — Read new contexts inside `TableNode`.** After `const onColumnCheck = useContext(ColumnCheckContext)`, add:

```typescript
const onColumnAggregate = useContext(ColumnAggregateContext)
const onColumnAlias = useContext(ColumnAliasContext)
```

**Step 4 — Refactor the column list map.** The current map (lines 76–106) uses a single flat `<div className="relative flex items-center gap-2 px-3 py-1.5">` per column. Replace the entire `{/* Column list */}` block with:

```tsx
{/* Column list — Story 10.1: checkboxes; Story 10.2: aggregate + alias for checked columns */}
<div className="divide-y divide-border">
  {data.columns.map((col) => {
    const aliasError = col.alias !== '' && !isValidAlias(col.alias)
    return (
      <div key={col.columnName} className="relative">
        {/* Main column row: source handle, checkbox, name, pgType, target handle */}
        <div className="flex items-center gap-2 px-3 py-1.5">
          <Handle
            type="source"
            position={Position.Left}
            id={col.columnName}
            className="!h-2 !w-2 !border-border !bg-muted-foreground"
          />
          <input
            type="checkbox"
            checked={col.checked}
            onChange={(e) => onColumnCheck(id, col.columnName, e.target.checked)}
            aria-label={col.columnName}
            className="nodrag nopan h-3.5 w-3.5 cursor-pointer"
          />
          <span className="min-w-0 flex-1 truncate text-xs">{col.columnName}</span>
          <span className="shrink-0 text-xs text-muted-foreground">{col.pgType}</span>
          <Handle
            type="target"
            position={Position.Right}
            id={col.columnName}
            className="!h-2 !w-2 !border-border !bg-muted-foreground"
          />
        </div>
        {/* Story 10.2: aggregate + alias row, visible only for checked columns */}
        {col.checked && (
          <div className="nodrag nopan flex flex-col gap-1 px-3 pb-2">
            <div className="flex gap-1.5">
              <select
                value={col.aggregate}
                onChange={(e) =>
                  onColumnAggregate(id, col.columnName, e.target.value as AggregateFunction)
                }
                aria-label={`${col.columnName} aggregate`}
                className="nodrag nopan h-6 shrink-0 cursor-pointer rounded border border-border bg-background px-1 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
              >
                <option value="none">None</option>
                <option value="COUNT">COUNT</option>
                <option value="SUM">SUM</option>
                <option value="AVG">AVG</option>
                <option value="MIN">MIN</option>
                <option value="MAX">MAX</option>
              </select>
              <input
                type="text"
                value={col.alias}
                onChange={(e) => onColumnAlias(id, col.columnName, e.target.value)}
                placeholder={`${data.tableName}_${col.columnName}`}
                aria-label={`${col.columnName} alias`}
                aria-invalid={aliasError}
                className={`nodrag nopan h-6 min-w-0 flex-1 rounded border bg-background px-1.5 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring ${
                  aliasError ? 'border-destructive' : 'border-border'
                }`}
              />
            </div>
            {aliasError && (
              <p className="text-xs text-destructive">
                {t('datasets.builder.node.aliasInvalidPattern')}
              </p>
            )}
          </div>
        )}
      </div>
    )
  })}
</div>
```

**Critical notes on this refactor:**
- The outer `<div key={col.columnName} className="relative">` is now the React Flow handle positioning anchor. The `relative` class **must stay on this outer wrapper** — React Flow v12's `ResizeObserver` measures handle offsets within the node from their DOM position. Handles inside the inner row div are vertically centered to that row, which is the correct visual position.
- `nodrag nopan` on the expanded container div is belt-and-suspenders; individual `<select>` and `<input>` also carry it.
- `col.alias !== '' && !isValidAlias(col.alias)` — validate only non-empty aliases; empty string is the valid "use default" state.
- The `<select>` option values exactly match `AggregateFunction` union values (`'none' | 'COUNT' | 'SUM' | 'AVG' | 'MIN' | 'MAX'`). The `as AggregateFunction` cast is safe because the DOM enforces option values.
- The `placeholder` shows `{tableName}_{columnName}` as a preview of the default SQL alias. It is a computed string, not a translated string — no i18n key needed.
- Aggregate option labels (None, COUNT, SUM, etc.) are SQL keywords / UI convention — no i18n keys needed.

**Step 5 — Update min-width.** The existing `min-w-[180px]` is insufficient for the expanded row. Update the outer node wrapper:

```tsx
<div
  className={`min-w-[240px] rounded-lg border bg-card text-card-foreground shadow-sm ${
    selected ? 'ring-2 ring-ring' : ''
  }`}
>
```

`240px` accommodates the aggregate select (~65px) + gap + alias input + padding without wrapping. Adjust if actual rendering differs, but do not go below `min-w-[220px]`.

**Step 6 — Update the component-level comment** (line 26–27, after the Story 10.1 mention):

```typescript
// Story 9.1: Structural shell. Story 9.5 makes the Left/Right toggle interactive.
// Story 10.1 makes column checkboxes interactive via ColumnCheckContext.
// Story 10.2 adds aggregate dropdown + alias input for checked columns (ColumnAggregateContext,
//   ColumnAliasContext) with inline alias validation (isValidAlias).
```

### Task 3 — Frontend: Wire `handleAggregateChange` + `handleAliasChange` in `QueryBuilderCanvas.tsx` (AC-5)

Open `web/src/features/datasets/QueryBuilderCanvas.tsx`.

**Step 1 — Update the `TableNode` import.** Add `ColumnAggregateContext` and `ColumnAliasContext`:

```typescript
import {
  TableNode,
  TableSideChangeContext,
  ColumnCheckContext,
  ColumnAggregateContext,
  ColumnAliasContext,
  type TableNodeType,
} from '../../components/query-builder/TableNode'
```

**Step 2 — Update the `builderState` import.** Add `AggregateFunction`:

```typescript
import {
  getLeftTableValidationError,
  getColumnSelectionValidationError,
  type AggregateFunction,
  type BuilderState,
  type ColumnSelection,
  type JoinEdgeState,
  type JoinType,
  type TableNodeData,
  type TableNodeState,
  type TableSide,
} from './types/builderState'
```

(`isValidAlias` is not needed in the canvas — it is used only in `TableNode.tsx`.)

**Step 3 — Add `handleAggregateChange` after `handleColumnCheck` (~line 224).** Mirror the exact same stable-ref pattern — same deps, same ref reads, same `setNodes` + `notify()` pattern:

```typescript
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
```

**Step 4 — Add `handleAliasChange` after `handleAggregateChange`.** Mirror the exact same pattern:

```typescript
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
```

**Step 5 — Extend the provider stack.** Nest both new providers inside `ColumnCheckContext.Provider` (which is inside `TableSideChangeContext.Provider`):

```tsx
<TableSideChangeContext.Provider value={handleSideChange}>
  <ColumnCheckContext.Provider value={handleColumnCheck}>
    <ColumnAggregateContext.Provider value={handleAggregateChange}>
      <ColumnAliasContext.Provider value={handleAliasChange}>
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
      </ColumnAliasContext.Provider>
    </ColumnAggregateContext.Provider>
  </ColumnCheckContext.Provider>
</TableSideChangeContext.Provider>
```

The validation banner block and `{selectedEdge && <JoinInspector .../>}` remain **unchanged** after the closing `</TableSideChangeContext.Provider>`.

### Task 4 — Frontend: Add i18n key to `en.json` (AC-4 inline error)

Open `web/src/lib/i18n/locales/en.json`. Extend the existing `datasets.builder.node` object:

```json
"node": {
  "left": "Left",
  "right": "Right",
  "sideGroupAria": "Table side designation",
  "aliasInvalidPattern": "Use lowercase letters, digits, and underscores; must start with a letter or underscore (max 63 chars)."
}
```

This key is referenced by the literal `t('datasets.builder.node.aliasInvalidPattern')` in Task 2 Step 4. A literal `t()` call without the key in `en.json` → **i18n-check exit 1 (fatal)**. Both must be present together.

Do **not** reuse the existing `datasets.validation.invalidPattern` key — that is for dataset name validation in a different UI context. The new key belongs under `datasets.builder.node` (builder namespace).

### Task 5 — Frontend: Unit tests for `isValidAlias` (AC-4)

Add to the existing `web/src/features/datasets/types/__tests__/builderState.test.ts`. Update the import to add `isValidAlias`:

```typescript
import {
  getColumnSelectionValidationError,
  getLeftTableValidationError,
  isValidAlias,
} from '../builderState'
```

Add a new `describe` block after `describe('getColumnSelectionValidationError', ...)`:

```typescript
describe('isValidAlias', () => {
  it('returns true for an empty string (use-default signal)', () => {
    expect(isValidAlias('')).toBe(true)
  })

  it('returns true for a valid lowercase alias', () => {
    expect(isValidAlias('my_alias')).toBe(true)
  })

  it('returns true for an alias starting with underscore', () => {
    expect(isValidAlias('_private')).toBe(true)
  })

  it('returns true for an alias ending with digit', () => {
    expect(isValidAlias('col1')).toBe(true)
  })

  it('returns false for uppercase letters', () => {
    expect(isValidAlias('MyAlias')).toBe(false)
  })

  it('returns false for leading digit', () => {
    expect(isValidAlias('1alias')).toBe(false)
  })

  it('returns false for spaces', () => {
    expect(isValidAlias('alias name')).toBe(false)
  })

  it('returns false for hyphens', () => {
    expect(isValidAlias('alias-col')).toBe(false)
  })

  it('returns true for exactly 63 chars', () => {
    expect(isValidAlias('a'.repeat(63))).toBe(true)
  })

  it('returns false for 64 chars (exceeds PostgreSQL identifier limit)', () => {
    expect(isValidAlias('a'.repeat(64))).toBe(false)
  })
})
```

Expected result: 294 (baseline) + 10 new = **304 passed / 0 failed**.

### Task 6 — Frontend: Add deferred-work entry

Add to `_bmad-output/implementation-artifacts/deferred-work.md` at the top (after the file heading, before the 10-1 entries):

```markdown
## Deferred from: implementation of 10-2-aggregate-function-and-custom-column-alias

- **Save/Preview blocking on invalid alias (AC-4) deferred to Story 11.2** — alias validation is display-only in Story 10.2 (inline error shown, save not blocked — no Save button exists yet). Story 11.2 must add a `hasInvalidAlias(nodes)` SSOT gate in `builderState.ts` (similar to `getColumnSelectionValidationError`) and disable Save when any checked column has a non-empty, invalid alias. [`builderState.ts`, `TableNode.tsx`]
- **Server-side alias validation deferred to Story 11.1/11.2** — `isValidAlias()` is a client-side usability aid only. The server's `SafeIdentifier.Create()` re-validates every alias when SQL is generated (Story 11.1) and on builder-state save (Story 11.2). A rejected alias returns `BUILDER_STATE_INVALID` (422). [`DatasetSqlGenerator.cs`]
```

### Task 7 — Verification

- [x] `cd web && npx tsc -b --noEmit` → 0 errors
- [x] `cd web && npx vitest run` → 304 passed / 0 failed (294 baseline + 10 new `isValidAlias` tests)
- [x] `cd web && node scripts/i18n-check.mjs` → exit 0 (`datasets.builder.node.aliasInvalidPattern` referenced by literal `t()`)
- [x] Manual smoke (AC-1, controls appear for checked only): verified by code inspection — aggregate/alias row is gated behind `{col.checked && (…)}`; unchecked columns render only the main row. *(Deterministic from render logic; not run in a live browser this session.)*
- [x] Manual smoke (AC-1, aggregate default): verified — `<select value={col.aggregate}>` with `aggregate` initialized to `'none'` on drop selects the "None" option.
- [x] Manual smoke (AC-4, alias placeholder): verified — `placeholder={`${data.tableName}_${col.columnName}`}`; `aliasError` is false for empty `col.alias` (`col.alias !== '' && …`), so no error renders.
- [x] Manual smoke (AC-4, alias validation): verified — `aliasError` is true for `MyAlias` (fails `isValidAlias`), false for `my_alias`; the destructive border + error `<p>` are bound to `aliasError`.
- [x] Manual smoke (AC-5, state persistence across canvas changes): verified — `handleAggregateChange`/`handleAliasChange` write into `nodesRef.current` and `setNodes`, so column config survives subsequent canvas re-renders.
- [x] Manual smoke (nodrag/nopan): verified — `<select>`, `<input>`, and their container all carry `nodrag nopan`.
- [x] Manual smoke (AC-1, unchecked survive): verified — `handleColumnCheck` only mutates `checked`; `aggregate`/`alias` are untouched, so re-checking restores prior values (Dev Notes §4).

### Review Findings

_Code review 2026-06-04 (Blind Hunter + Edge Case Hunter + Acceptance Auditor). Auditor: all AC-1..AC-5 and Tasks 1–7 Pass. 6 findings dismissed as noise/out-of-scope (survive-uncheck intentional §4; duplicate-alias disambiguation routed to 11.1 per §7; invalid-alias emission already deferred to 11.2; aggregate cast safe; placeholder preview per §7; memo reactivity sound)._

- [x] [Review][Patch] Alias error message not programmatically linked to its input — added `aliasErrorId` + `id` on the error `<p>` and `aria-describedby={aliasError ? aliasErrorId : undefined}` on the alias `<input>`. SRs now announce the reason, not just the invalid state. Verified: tsc 0 errors, vitest 304/304. [web/src/components/query-builder/TableNode.tsx:140-160]
- [x] [Review][Defer] Legacy/partial `builder_state` with `undefined` `aggregate`/`alias` renders a spurious alias error and a controlled→uncontrolled input warning [web/src/components/query-builder/TableNode.tsx:117-145] — deferred; extends the existing `parseBuilderState` node-shape-validation deferral (9.5 W2 / 10.1 W2). Lands with builder_state persistence (Story 11.2). 10.2 widens the symptom (spurious validation error) but not the root cause.
- [x] [Review][Defer] Duplicate `columnName` within a single node mutates every matching column and collides React keys; 10.2 widens the blast radius from the checkbox to aggregate + alias [web/src/components/query-builder/TableNode.tsx:96; web/src/features/datasets/QueryBuilderCanvas.tsx] — deferred; pre-existing (10.1 W1), unreachable via the catalog (Postgres guarantees per-table column-name uniqueness). Fold per-node column-name uniqueness into `parseBuilderState` validation with 11.2.

---

## Dev Notes

### §1 — Current State Left by Story 10.1

`TableNode.tsx` (lines 76–109): each column row is a single flat `<div className="relative flex items-center gap-2 px-3 py-1.5">` containing handles, an interactive checkbox (wired by 10.1), column name, and pgType. `ColumnAggregateContext` and `ColumnAliasContext` do not exist yet.

`builderState.ts` (lines 14–20): `ColumnSelection` already has `aggregate: AggregateFunction` (initialized to `'none'` on drop) and `alias: string` (initialized to `''` on drop). No new type changes needed.

`QueryBuilderCanvas.tsx` (lines 177–224): `nodesRef`/`edgesRef` mirrors, `notify()`, and `handleColumnCheck` are established. New handlers must follow the exact same stable-ref pattern.

### §2 — Why Two Separate Contexts (Not One Combined)

One context per action type is the established project pattern: `TableSideChangeContext`, `ColumnCheckContext`. Adding `ColumnAggregateContext` + `ColumnAliasContext` is the natural extension. A combined context (e.g. `ColumnUpdateContext`) would require modifying Story 10.1's `ColumnCheckContext` (changing an established API at the call sites), introducing regression risk for a marginal reduction in context count. Two new contexts is the correct call.

### §3 — Stable Handler Identity (nodesRef/edgesRef Pattern)

`handleAggregateChange` and `handleAliasChange` read from `nodesRef.current` / `edgesRef.current`, with deps `[setNodes, notify]` only. Do **not** add `nodes` or `edges` to their `useCallback` deps. This keeps `ColumnAggregateContext.Provider value` and `ColumnAliasContext.Provider value` referentially stable across renders so `TableNode`'s `memo()` is not defeated. Identical rationale to `handleSideChange` and `handleColumnCheck`.

### §4 — Aggregate and Alias Values Survive Unchecking (Intentional)

`handleColumnCheck` toggles `checked` but does not touch `aggregate` or `alias`. When the user unchecks a column, their configured aggregate + alias are preserved in state. The SQL generator (Story 11.1) ignores aggregate/alias for unchecked columns. If the user re-checks, their settings are restored — not lost. This is deliberate UX; do not reset aggregate/alias on uncheck.

### §5 — Alias Validation Is Display-Only in Story 10.2

`isValidAlias()` is called inline in `TableNode.tsx` to show the error `<p>`. Invalid aliases **are stored** in `builder_state` as-is (they may be mid-edit). Story 11.2 must add a `hasInvalidAlias(nodes)` gate (similar to `getColumnSelectionValidationError`) that the Save button consumes to stay disabled until all aliases are valid. Story 11.1's `DatasetSqlGenerator.cs` also re-validates all aliases via `SafeIdentifier.Create()` server-side.

### §6 — FR-57 Identifier Rules for Column Aliases

Pattern: `/^[a-z_][a-z0-9_]*$/`, max 63 chars. Identical to DatasetName validation (Decision 1.1). Empty `''` alias is valid — it is the "derive at SQL gen time" signal for Story 11.1. The inline error message lives at `datasets.builder.node.aliasInvalidPattern`; do **not** reuse `datasets.validation.invalidPattern` (that key is scoped to dataset-name form validation, not the builder node UI).

### §7 — Default Alias and Disambiguation Are Story 11.1 Concerns

The alias input `placeholder={`${data.tableName}_${col.columnName}`}` previews the default for the user. Nothing is computed or stored from it. Story 11.1's `DatasetSqlGenerator.cs` receives `alias === ''` → derives `{tableName}_{columnName}` → deduplicates if needed (e.g. `orders_id`, `orders_id_2`). This story does not implement any deduplication or server-side alias computation.

### §8 — No Backend / No BuilderStateDto Changes

- `ColumnSelection` type is unchanged.
- `BuilderStateDto` in C# already has `Aggregate` + `Alias` fields (AR-67 pre-planned). No C# changes.
- No API calls, no migration. Server persistence is Story 11.2.
- Backend baseline: `dotnet test` → 918 passed / 2 pre-existing failures (memory: pre-existing audit 405 failures). No backend work in this story.

### §9 — Accessibility

- Aggregate `<select>` gets `aria-label={`${col.columnName} aggregate`}` — column name is a DB identifier (English, acceptable), compound with the word "aggregate".
- Alias `<input>` gets `aria-label={`${col.columnName} alias`}` and `aria-invalid={aliasError}` — `aria-invalid` announces the invalid state to screen readers when the error is present.
- Inline error `<p>` is conditionally rendered; no `role="alert"` is required — `aria-invalid` on the input is sufficient to signal the error. Add `role="alert"` only if user testing reveals screen-reader announcement is missed.
- Both controls carry `nodrag nopan` to prevent the select/input from triggering canvas drag or pan on click.

### §10 — Handle Positioning After Column Row Refactor

The column map is refactored from a single flat `relative` div per column to an outer `relative` wrapper + inner main row div. React Flow v12 positions handles by measuring their DOM offset within the node via `ResizeObserver`. Keeping `Handle` components inside the inner main row div positions them vertically at the center of that row (the column name line), which is the correct visual position for join handles. The outer `relative` wrapper is the CSS positioning ancestor. This is functionally equivalent to the pre-refactor structure.

**Do not** move `Handle` components to the outer wrapper or the expanded row — they must remain in the main row div.

### §11 — Node min-width Update

The existing `min-w-[180px]` is insufficient for the expanded row (aggregate select ~65px + gap + alias input). Use `min-w-[240px]`. This prevents the aggregate + alias row from overflowing or wrapping uncomfortably. The dev agent may adjust based on actual rendering, but should not go below `min-w-[220px]`.

### §12 — Files to Create / Modify

```
MODIFIED (frontend):
  web/src/features/datasets/types/builderState.ts
    — add ALIAS_PATTERN export + isValidAlias(alias) pure helper (after getColumnSelectionValidationError)
  web/src/features/datasets/types/__tests__/builderState.test.ts
    — add isValidAlias import + describe block (10 cases)
  web/src/components/query-builder/TableNode.tsx
    — import: isValidAlias (value), AggregateFunction (type), add to existing builderState import
    — export ColumnAggregateContext (no-op default)
    — export ColumnAliasContext (no-op default)
    — add: const onColumnAggregate = useContext(ColumnAggregateContext)
    — add: const onColumnAlias = useContext(ColumnAliasContext)
    — refactor column map: outer relative wrapper + inner main row + conditional expanded row
    — update min-w-[180px] → min-w-[240px]
    — update component comment to mention Story 10.2
  web/src/features/datasets/QueryBuilderCanvas.tsx
    — imports: ColumnAggregateContext, ColumnAliasContext, AggregateFunction
    — add handleAggregateChange (stable via nodesRef/edgesRef, deps [setNodes, notify])
    — add handleAliasChange (stable via nodesRef/edgesRef, deps [setNodes, notify])
    — extend provider stack: ColumnAggregateContext.Provider + ColumnAliasContext.Provider
      inside ColumnCheckContext.Provider, wrapping <ReactFlow>
  web/src/lib/i18n/locales/en.json
    — add datasets.builder.node.aliasInvalidPattern

NEW / MODIFIED (backend, i18n locales other than en, BuilderStateDto, types schema): none
DOC:
  _bmad-output/implementation-artifacts/deferred-work.md
    — add 10-2 save-blocking + server-validation deferral entries (Task 6)
```

### §13 — Git / Recent-Work Patterns

Recent commits (4368ee6 10-1 review, 85cce0f 9.5, 15c14fb 9.4) establish:
- One context per action type: `createContext` no-op default + `useContext` + stable handler via `nodesRef`/`edgesRef`, provided around `<ReactFlow>`.
- `nodrag nopan` on every interactive element inside a node.
- No hardcoded colors — use theme tokens (`bg-background`, `border-border`, `text-destructive`).
- New i18n keys must have a matching static `t('key.name')` literal call.
- New `describe` blocks appended to `builderState.test.ts` without modifying existing tests.
- `handleColumnCheck` and `handleSideChange` are the exact templates to follow for the new handlers.

### §14 — References

- [Source: `_bmad-output/planning-artifacts/epics.md` — Story 10.2 ACs (FR-67 J-2 AC-1..AC-5)]
- [Source: `_bmad-output/planning-artifacts/architecture.md` Decision 6.10 — SQL generator (aggregate in SELECT; GROUP BY derivation)]
- [Source: `_bmad-output/planning-artifacts/architecture.md` Decision 6.11 — builder_state ColumnConfig shape (aggregate?, alias?)]
- [Source: `_bmad-output/planning-artifacts/architecture.md` Decision 6.12 — TableNode.tsx renders "aggregate dropdown, alias input"]
- [Source: `_bmad-output/planning-artifacts/epics.md` FR-57 — identifier rules (same as Decision 1.1 / DatasetName validation)]
- [Source: `web/src/components/query-builder/TableNode.tsx:1-109` — current column row structure being refactored]
- [Source: `web/src/features/datasets/types/builderState.ts:9` — AggregateFunction type (`'none' | 'COUNT' | 'SUM' | 'AVG' | 'MIN' | 'MAX'`)]
- [Source: `web/src/features/datasets/types/builderState.ts:14-20` — ColumnSelection type with aggregate/alias already defined]
- [Source: `web/src/features/datasets/types/builderState.ts:127-159` — getLeftTableValidationError + getColumnSelectionValidationError (pure-helper pattern to follow for isValidAlias)]
- [Source: `web/src/features/datasets/QueryBuilderCanvas.tsx:177-224` — nodesRef/edgesRef + handleSideChange + handleColumnCheck (handler pattern to mirror exactly)]
- [Source: `web/src/features/datasets/QueryBuilderCanvas.tsx:358-414` — provider nesting + validation banner (extend this pattern)]
- [Source: `web/src/lib/i18n/locales/en.json:datasets.builder.node` — existing node namespace (`left`, `right`, `sideGroupAria`) being extended]
- [Source: `_bmad-output/implementation-artifacts/10-1-per-table-column-selection.md §2,§3,§4,§5,§10,§11` — context rationale, stable-ref, deferral, and file-list patterns]
- [Source: Memory — @xyflow/react v12 typing gotchas: NodeProps keyed on Node type; `data` must be `type` alias not `interface`]
- [Source: Memory — i18n-lint failure RESOLVED 2026-06-04; a red i18n-lint is now a real regression]

---

## Dev Agent Record

### Agent Model Used

claude-sonnet-4-6 — BMad create-story workflow

### Completion Notes List

- Implemented all 7 tasks exactly per the story spec. Frontend-only; no backend/DTO/migration changes (Dev Notes §8 — `BuilderStateDto` already carries `Aggregate`/`Alias`).
- `isValidAlias` + `ALIAS_PATTERN` added to `builderState.ts` as a pure SSOT helper (empty alias is valid = "use default at SQL-gen time"). Covered by 10 new unit tests.
- `TableNode.tsx` column map refactored from a flat row to an outer `relative` wrapper + inner main row + conditional aggregate/alias expanded row (visible only for checked columns). `Handle`s kept in the inner main row (Dev Notes §10). Node min-width raised `180px → 240px`.
- Two new contexts (`ColumnAggregateContext`, `ColumnAliasContext`) follow the established one-context-per-action pattern; handlers `handleAggregateChange`/`handleAliasChange` mirror `handleColumnCheck`'s stable-ref posture (deps `[setNodes, notify]`, read from `nodesRef`/`edgesRef`) so `TableNode`'s `memo()` is not defeated.
- i18n key `datasets.builder.node.aliasInvalidPattern` added to `en.json`, referenced by a static `t()` literal (i18n-check exit 0).
- Verification: `tsc -b --noEmit` 0 errors; `vitest run` 304 passed / 0 failed; `i18n-check` exit 0. Manual smoke items validated by code inspection (deterministic render/handler logic) — not run in a live browser this session.
- Deferred (Task 6, recorded in `deferred-work.md`): Save-blocking on invalid alias → Story 11.2 (`hasInvalidAlias` gate); server-side alias re-validation → Story 11.1/11.2 (`SafeIdentifier.Create()`).

### File List

- `web/src/features/datasets/types/builderState.ts` (modified) — `ALIAS_PATTERN` + `isValidAlias` helper
- `web/src/features/datasets/types/__tests__/builderState.test.ts` (modified) — `isValidAlias` import + 10-case describe block
- `web/src/components/query-builder/TableNode.tsx` (modified) — `ColumnAggregateContext`/`ColumnAliasContext` exports, context reads, column-row refactor with aggregate/alias row, min-w-[240px], comment update
- `web/src/features/datasets/QueryBuilderCanvas.tsx` (modified) — context imports + `AggregateFunction` type, `handleAggregateChange`/`handleAliasChange`, provider stack extension
- `web/src/lib/i18n/locales/en.json` (modified) — `datasets.builder.node.aliasInvalidPattern`
- `_bmad-output/implementation-artifacts/deferred-work.md` (modified) — 10-2 deferral entries

## Change Log

| Date       | Version | Description                                                   | Author |
| ---------- | ------- | ------------------------------------------------------------- | ------ |
| 2026-06-04 | 0.1     | Story drafted (create-story): two new contexts (ColumnAggregateContext, ColumnAliasContext) + stable handlers, column row refactor with conditional aggregate/alias expanded row, isValidAlias SSOT helper + inline validation display, min-w update, 10 unit tests. Status → ready-for-dev. | create-story |
| 2026-06-04 | 1.0     | Implemented all 7 tasks: isValidAlias + ALIAS_PATTERN, two contexts + stable handlers, TableNode column-row refactor (aggregate dropdown + alias input for checked columns, inline validation, min-w-[240px]), provider stack, i18n key, 10 unit tests, deferred-work entries. Verified: tsc 0 errors, vitest 304/304, i18n-check exit 0. Status → review. | dev-story |
| 2026-06-04 | 1.1     | Code review (3 layers): all ACs/tasks Pass. Applied 1 a11y patch (aria-describedby linking alias error to input); 2 findings deferred to 11.2 (legacy-blob undefined fields, duplicate-columnName matching); 6 dismissed. Re-verified tsc 0 / vitest 304. Status → done. | code-review |
