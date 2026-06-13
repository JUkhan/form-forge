# Story 10.7: Nested Filter Groups

Status: done

## Story

As a user building a Dataset,
I want to nest filter groups to arbitrary depth,
So that complex boolean logic can be expressed visually.

## Acceptance Criteria

1. Given a group in the Filter Conditions dialog, when I click "Add group" inside it, then a sub-group is added with its own AND/OR combinator (per FR-68 J-7 AC-1)

2. Given groups at multiple depth levels, when the UI renders, then indentation and color differentiation per depth level clearly show nesting structure (per FR-68 J-7 AC-2)

3. Given the SQL generator runs (Story 11.1), when it processes nested groups, then it emits correctly parenthesized SQL from the recursive group tree: `((A AND B) OR (C AND D))` — this AC is Story 11.1's responsibility; Story 10.7 only ensures the `FilterGroup` tree is correctly structured for the generator to consume (per FR-68 J-7 AC-3)

4. Given deeply nested groups (depth > 5 or any depth), when the application processes them, then no artificial depth limit is enforced in v1 (per FR-68 J-7 AC-4)

## Tasks / Subtasks

- [x] Task 1: Add depth-varying border colors in `FilterConditionsDialog.tsx` (AC: 2)
  - [x] Define `DEPTH_BORDER_COLORS` constant array at module scope (after the tree helpers, before `FilterGroupContent`) with 4 full Tailwind class strings: `['border-blue-500/40', 'border-violet-500/40', 'border-emerald-500/40', 'border-amber-500/40']`
  - [x] Update `indentClass` at line 134 in `FilterGroupContent`: replace `depth > 0 ? 'pl-4 border-l-2 border-border' : ''` with a two-liner that derives `depthColor = DEPTH_BORDER_COLORS[(depth - 1) % DEPTH_BORDER_COLORS.length]` then uses `` `pl-4 border-l-2 ${depthColor}` `` for the non-root branch
  - [x] Do NOT use dynamic interpolation like `` `border-${color}-500` `` — Tailwind JIT requires full class strings in the source

- [x] Task 2: Export tree mutation helpers from `FilterConditionsDialog.tsx` (AC: 1, 4 — prerequisite for unit testing)
  - [x] Add `export` keyword to each of the 5 tree helpers at lines 32, 46, 54, 63, 87: `treeSetCombinator`, `treeAddItem`, `treeRemoveItem`, `treeMoveItem`, `treeUpdateCondition`
  - [x] Keep all functions in `FilterConditionsDialog.tsx` — do NOT move them to a separate file; they are correctly co-located

- [x] Task 3: Unit tests for the tree mutation helpers (AC: 1, 4)
  - [x] Create directory `web/src/components/query-builder/__tests__/` (does not exist yet) and file `filterTreeUtils.test.ts` inside it
  - [x] Imports: `{ describe, it, expect }` from `'vitest'`; the 5 tree helper functions from `'../FilterConditionsDialog'`; `FilterGroup`, `FilterCondition` types from `'../../../features/datasets/types/builderState'`
  - [x] Define module-level factory helpers `makeCond(id)` and `makeGroup(id, combinator?, ...items)` (see Dev Notes §4)
  - [x] `treeSetCombinator` tests: sets combinator on root; sets combinator on a nested sub-group by id; leaves sibling items unchanged
  - [x] `treeAddItem` tests: appends to root group; appends to a sub-group nested one level inside root; returns an unchanged (re-cloned) tree when `groupId` not found (silent no-op, as documented)
  - [x] `treeRemoveItem` tests: removes a condition from root; removes a condition from a nested sub-group; removes a sub-group node itself from root
  - [x] `treeMoveItem` tests: moves item up when `idx > 0`; moves item down when `idx < last`; no-op when at bounds (up at idx=0, down at idx=last); moves item correctly inside a nested group
  - [x] `treeUpdateCondition` tests: replaces a condition in root group; replaces a condition nested two levels deep; leaves unrelated conditions unchanged
  - [x] Arbitrary-depth test (AC-4): programmatically build a 7-level-deep `FilterGroup` tree (each level wraps the previous as its sole sub-group); call `treeAddItem` targeting the deepest group's id; verify the resulting tree contains the new item at depth 7 — confirms no artificial depth limit

- [x] Task 4: Verify — `npx tsc -b --noEmit` 0 errors; `npm run test` all pass (baseline 323; expect ~331+ after new tests); `npm run lint:i18n` exits 0

### Review Findings

- [x] [Review][Patch] `depthColor` computed unconditionally — `undefined` at depth=0 before depth guard [web/src/components/query-builder/FilterConditionsDialog.tsx:~143]
- [x] [Review][Defer] `treeMoveItem` early-return vs `.map()` pattern in other helpers — duplicate-ID exposure [web/src/components/query-builder/FilterConditionsDialog.tsx] — deferred, pre-existing
- [x] [Review][Defer] `treeRemoveItem` filter+recurse double-deletion hazard with duplicate IDs [web/src/components/query-builder/FilterConditionsDialog.tsx] — deferred, pre-existing
- [x] [Review][Defer] `treeMoveItem` no-op boundary clones array unnecessarily — spurious reference change [web/src/components/query-builder/FilterConditionsDialog.tsx] — deferred, pre-existing
- [x] [Review][Defer] `useEffect` excludes `filters` from deps — stale-closure risk when `isOpen` stays true [web/src/components/query-builder/FilterConditionsDialog.tsx] — deferred, pre-existing
- [x] [Review][Defer] `handleAddCondition` creates condition with empty `columnName` when table has no columns [web/src/components/query-builder/FilterConditionsDialog.tsx] — deferred, pre-existing

## Dev Notes

### 1. Critical Context: What Was Already Built

**The recursive sub-group rendering is ALREADY FULLY IMPLEMENTED from Story 10.5.** Story 10.7's scope is narrowly:
1. Add depth-varying border colors to the existing `indentClass` logic (Task 1)
2. Export the existing tree helpers so they can be tested (Task 2)
3. Write unit tests proving arbitrary-depth nesting works (Task 3)

Do NOT re-implement or refactor the recursive rendering. Do NOT change `FilterGroupContent`'s props or recursive call structure.

`FilterConditionsDialog.tsx` (`web/src/components/query-builder/FilterConditionsDialog.tsx`, **406 lines** after Story 10.6) already has:
- `FilterGroupContent` recursive sub-component (lines 104–288), passes `depth + 1` to sub-group calls
- `handleAddGroup` (lines 347–355): creates `{ id: nextFilterId('grp'), kind: 'group', combinator: 'AND', items: [] }` and calls `treeAddItem` — already correct
- Opening paren `(` rendered at `depth > 0` (lines 141–143) — already correct
- Closing paren `)` rendered after the sub-group (lines 265–267) — already correct
- `indentClass` at **line 134**: `depth > 0 ? 'pl-4 border-l-2 border-border' : ''` — **this is the only structural line to change in Task 1** (the single `border-border` does not vary per depth)

### 2. Depth-Varying Border Colors (Task 1 — exact change)

Place `DEPTH_BORDER_COLORS` at module scope after `treeUpdateCondition` (line 98) and before the `FilterGroupContent` function (line 117):

```typescript
// Depth-level border colors — cycles every 4 levels. Full class strings required
// for Tailwind JIT to include them in the build output.
const DEPTH_BORDER_COLORS = [
  'border-blue-500/40',
  'border-violet-500/40',
  'border-emerald-500/40',
  'border-amber-500/40',
] as const
```

Update **line 134** inside `FilterGroupContent` (the only change inside the component):

```typescript
// Before (line 134):
const indentClass = depth > 0 ? 'pl-4 border-l-2 border-border' : ''

// After:
const depthColor = DEPTH_BORDER_COLORS[(depth - 1) % DEPTH_BORDER_COLORS.length]
const indentClass = depth > 0 ? `pl-4 border-l-2 ${depthColor}` : ''
```

`depth - 1` is always ≥ 0 in this branch because it is only reached when `depth > 0`. The modulo cycling means: depth 1 → blue, depth 2 → violet, depth 3 → emerald, depth 4 → amber, depth 5 → blue again.

### 3. Exporting Tree Helpers (Task 2)

The 5 helpers are pure functions at module scope (lines 32–98). Add `export` keyword — no other changes:

```
Line 32:  function treeSetCombinator   →  export function treeSetCombinator
Line 46:  function treeAddItem         →  export function treeAddItem
Line 54:  function treeRemoveItem      →  export function treeRemoveItem
Line 63:  function treeMoveItem        →  export function treeMoveItem
Line 87:  function treeUpdateCondition →  export function treeUpdateCondition
```

`isRoot` prop on `FilterGroupContent` (line 121, has the comment "reserved for forward-compatibility"): do NOT add root-delete gating — it was intentionally deferred.

### 4. Test File Setup (Task 3)

**File**: `web/src/components/query-builder/__tests__/filterTreeUtils.test.ts` (new directory + new file)

```typescript
import { describe, it, expect } from 'vitest'
import {
  treeSetCombinator,
  treeAddItem,
  treeRemoveItem,
  treeMoveItem,
  treeUpdateCondition,
} from '../FilterConditionsDialog'
import type { FilterCondition, FilterGroup } from '../../../features/datasets/types/builderState'

function makeCond(id: string): FilterCondition {
  return { id, kind: 'condition', tableName: 't', columnName: 'c', operator: '=', value: '' }
}

function makeGroup(
  id: string,
  combinator: 'AND' | 'OR' = 'AND',
  ...items: (FilterCondition | FilterGroup)[]
): FilterGroup {
  return { id, kind: 'group', combinator, items }
}
```

**Arbitrary-depth test skeleton** (AC-4):
```typescript
it('handles arbitrary depth — no limit enforced at depth 7', () => {
  // Build a 7-level deep tree: each level is a group containing the next
  let deepest = makeGroup('g7')
  let tree = makeGroup('g6', 'AND', deepest)
  tree = makeGroup('g5', 'AND', tree)
  tree = makeGroup('g4', 'AND', tree)
  tree = makeGroup('g3', 'AND', tree)
  tree = makeGroup('g2', 'AND', tree)
  const root = makeGroup('root', 'AND', tree)

  const newCond = makeCond('new-cond')
  const result = treeAddItem(root, 'g7', newCond)

  // Navigate to the deepest group and verify the condition was added
  const g2 = result.items[0] as FilterGroup
  const g3 = g2.items[0] as FilterGroup
  const g4 = g3.items[0] as FilterGroup
  const g5 = g4.items[0] as FilterGroup
  const g6 = g5.items[0] as FilterGroup
  const g7 = g6.items[0] as FilterGroup
  expect(g7.items).toHaveLength(1)
  expect(g7.items[0]).toEqual(newCond)
})
```

### 5. No New i18n Keys

All required keys already exist in `en.json` under `datasets.builder.filterDialog`:
- `parenOpen` / `parenClose` — already rendered at depth > 0
- No additions needed for Story 10.7

### 6. AC-3 Scope Boundary

Story 10.7 does NOT implement SQL parenthesization. `DatasetSqlGenerator.cs` (Story 11.1) is responsible for traversing the `FilterGroup` tree and emitting `((A AND B) OR (C AND D))`. Story 10.7 only confirms the data structure is correctly recursive so the generator can consume it.

### 7. Deferred Items to Carry Forward in deferred-work.md

From Story 10.5 (unresolved):
- `treeAddItem` silent no-op on unknown `groupId` — still latent; add to deferred if not already present
- Filter cascade-delete on canvas node removal — 11.1/11.2
- `parseBuilderState` normalization for `filters.items` — 11.2
- Server persistence of filters — 11.2

From Story 10.6 (unresolved):
- Tag-input UX for IN/NOT IN — future enhancement
- Stale filter references after table/column removal — 11.1/11.2

### 8. Files Changed

| File | Action |
|------|--------|
| `web/src/components/query-builder/FilterConditionsDialog.tsx` | UPDATE — add `DEPTH_BORDER_COLORS`, update `indentClass`, export 5 tree helpers |
| `web/src/components/query-builder/__tests__/filterTreeUtils.test.ts` | NEW — unit tests for the 5 tree mutation helpers (~8–12 tests) |
| `_bmad-output/implementation-artifacts/deferred-work.md` | UPDATE — Story 10.7 entries |

### References

- `FilterConditionsDialog.tsx`: `web/src/components/query-builder/FilterConditionsDialog.tsx`
  - Line 32–98: tree helpers (`treeSetCombinator`, `treeAddItem`, `treeRemoveItem`, `treeMoveItem`, `treeUpdateCondition`) — add `export`
  - Line 117: `FilterGroupContent` function definition start — insert `DEPTH_BORDER_COLORS` const just before
  - Line 134: `indentClass` — the only structural change for Task 1
  - Lines 141–143: opening paren at `depth > 0` — already correct, do not touch
  - Lines 265–267: closing paren after sub-group — already correct, do not touch
  - Lines 347–355: `handleAddGroup` — already correct, do not touch
- `builderState.ts`: `web/src/features/datasets/types/builderState.ts`
  - Lines 113–118: `FilterGroup` type — `items: (FilterCondition | FilterGroup)[]` is already recursive
  - Line 182: `EMPTY_BUILDER_STATE.filters` = `{ id: 'root', kind: 'group', combinator: 'AND', items: [] }`
- `en.json`: `web/src/lib/i18n/locales/en.json` — no changes; `filterDialog.*` keys complete as-is
- Epics: `_bmad-output/planning-artifacts/epics.md`, Story 10.7 section (FR-68 J-7 AC-1..4)
- Test baseline: 323 tests passed after Story 10.6 (`web/src/features/datasets/types/__tests__/builderState.test.ts`)
- Existing test pattern to follow: `web/src/features/datasets/types/__tests__/builderState.test.ts`

### Project Structure Notes

- `__tests__/` directory under `web/src/components/query-builder/` does not exist yet — create it
- No changes to routing, API layer, or backend
- No new packages needed
- The `@xyflow/react` v12 typing rules (NodeProps keyed on Node type; `data` must be `type` alias) do not apply to `FilterConditionsDialog.tsx` or the test file — neither is a React Flow node/edge component

## Dev Agent Record

### Agent Model Used

claude-sonnet-4-6

### Debug Log References

- `npx tsc -b --noEmit` → 0 errors
- `npm run test` (vitest) → 28 files / 344 tests passed (was 323 after Story 10.6; +21 new)
- `npm run lint:i18n` → exit 0 (advisory orphaned-key list identical to Story 10.6 baseline; no new entries)

### Completion Notes List

- **AC-1**: Sub-group add was already functional from Story 10.5 (`handleAddGroup` + recursive `FilterGroupContent`). Story 10.7 proves AC-1 with unit tests (`treeAddItem` suite — 4 tests, including nested sub-group case).
- **AC-2**: `DEPTH_BORDER_COLORS` array added at module scope; `indentClass` in `FilterGroupContent` now uses `(depth - 1) % 4` to cycle through blue/violet/emerald/amber border colors. Full class strings used throughout for Tailwind JIT compatibility.
- **AC-3**: Not implemented here — Story 11.1's `DatasetSqlGenerator.cs` responsibility. The `FilterGroup` tree is correctly recursive for the generator to consume.
- **AC-4**: Verified by the 7-level-deep arbitrary-depth test (`treeAddItem` correctly inserts at depth 7). No depth limit exists in the recursive implementation.
- **21 new tests**: `treeSetCombinator` (3), `treeAddItem` (4), `treeRemoveItem` (4), `treeMoveItem` (5), `treeUpdateCondition` (3), arbitrary-depth (2).
- Tree helpers exported from `FilterConditionsDialog.tsx` for testability without being moved to a separate file.

### File List

- `web/src/components/query-builder/FilterConditionsDialog.tsx` (MODIFIED — `DEPTH_BORDER_COLORS` const, updated `indentClass`, exported 5 tree helpers)
- `web/src/components/query-builder/__tests__/filterTreeUtils.test.ts` (NEW — 21 unit tests for tree mutation helpers)

## Change Log

| Date | Version | Description | Author |
|------|---------|-------------|--------|
| 2026-06-04 | 1.0 | Implemented Story 10.7: depth-varying border colors (AC-2), exported tree helpers, 21 unit tests covering all 5 tree helpers + arbitrary-depth AC-4. Status → review. | claude-sonnet-4-6 (Dev) |
