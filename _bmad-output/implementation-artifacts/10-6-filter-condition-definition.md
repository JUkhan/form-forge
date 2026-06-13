# Story 10.6: Filter Condition Definition

Status: done

## Story

As a user building a Dataset,
I want to define each filter condition as a table + column + operator + value expression,
So that the WHERE clause is precise and the value input adapts to the operator.

## Acceptance Criteria

1. Given a condition row in the Filter Conditions dialog, when it renders, then it shows: (a) table selector (tables on canvas), (b) column selector (selected table's columns), (c) operator selector, (d) value input (per FR-68 J-6 AC-1)

2. Given the operator selector, when it lists operators, then the available operators are: `=`, `!=`, `<`, `<=`, `>`, `>=`, `IS NULL`, `IS NOT NULL`, `LIKE`, `ILIKE`, `IN`, `NOT IN`, `BETWEEN` (per FR-68 J-6 AC-2)

3. Given I select an operator, when the value input renders, then `IS NULL` / `IS NOT NULL` → no value input; `IN` / `NOT IN` → multi-value input (comma-separated entries); `BETWEEN` → two inputs (from / to); all others → single input (per FR-68 J-6 AC-3)

4. Given the column's PG type, when the value input type adapts, then `NUMERIC`/integer/float → `type="number"` input; `TIMESTAMPTZ`/`TIMESTAMP` → `type="datetime-local"` input; `DATE` → `type="date"` input; `BOOLEAN` → checkbox; `TEXT`/other → `type="text"` input (per FR-68 J-6 AC-4)

5. Given the SQL generator runs (Story 11.1), when it processes filter conditions, then values are emitted as parameterized placeholders `$1`, `$2`, … with a separate parameters array — never string-interpolated into SQL (per FR-68 J-6 AC-5 / NFR-17 / AR-66) — this AC is Story 11.1's responsibility; Story 10.6 only ensures `value` is stored correctly for the generator to consume

## Tasks / Subtasks

- [x] Task 1: Expand `FilterCondition.value` type in `builderState.ts` (AC: 3)
  - [x] Change `value: string | null` to `value: string | string[] | null`
  - [x] Update the comment above `FilterCondition` to document the new shape: `string` for single-value operators; `string[]` for `IN`/`NOT IN` (array of tag values) and `BETWEEN` (two-element `[from, to]`); `null` for `IS NULL`/`IS NOT NULL`
  - [x] Keep all other `FilterCondition` fields unchanged (`id`, `kind`, `tableName`, `columnName`, `operator`)

- [x] Task 2: Create `FilterConditionRow.tsx` at `web/src/components/query-builder/FilterConditionRow.tsx` (AC: 1, 2, 3, 4)
  - [x] Props: `condition: FilterCondition`, `nodes: TableNodeType[]`, `onChange: (updated: FilterCondition) => void`, `onDelete: () => void`
  - [x] Table selector: `<select>` populated from `nodes.map(n => n.data.tableName)`. On change reset `columnName` to first column of new table and reset `value` to `null` / `[]` appropriately.
  - [x] Column selector: `<select>` populated from `nodes.find(n => n.data.tableName === condition.tableName)?.data.columns ?? []`. On change reset `value` to `null` / `[]` appropriately. When table has no columns show a disabled `<option>` with a "no columns" placeholder (per CaseColumnEditor pattern).
  - [x] Operator selector: `<select>` populated from `FILTER_OPERATORS` (import from `builderState.ts`). On change reset `value` to appropriate empty shape: `null` for IS NULL/IS NOT NULL; `[]` for IN/NOT IN; `['', '']` for BETWEEN; `''` for all others.
  - [x] Value input (see helper function `renderValueInput(operator, pgType, value, onChange)`):
    - `IS NULL` / `IS NOT NULL`: render nothing (null)
    - `IN` / `NOT IN`: render a single `<input type="text">` with comma-separated representation. `value` is stored as `string[]`; display as `(value as string[]).join(', ')`; on change split by comma + trim each entry + filter empty → store as `string[]`. Show a `<span>` hint "Comma-separated" below the input.
    - `BETWEEN`: render two side-by-side inputs ("From" and "To"). `value` is stored as `string[]` with two elements. Type-adaptive (same pgType rules as single).
    - Others: single type-adaptive input. Derive `inputType` from pgType: `int4/int8/int2/int16/float4/float8/numeric/decimal/money` → `number`; `timestamptz/timestamp` → `datetime-local`; `date` → `date`; `bool/boolean` → `checkbox`; all others → `text`. For checkbox, `value` stored as `'true'`/`'false'` string (keep `string` shape uniform); render `<input type="checkbox" checked={value === 'true'} onChange={e => onChange(e.target.checked ? 'true' : 'false')}`.
  - [x] Delete button using `<X>` icon (Lucide) and i18n `deleteConditionAria` (reuse existing key from Story 10.5)
  - [x] All text via `useTranslation()` and i18n keys (new keys added in Task 5)
  - [x] Use consistent styling from CaseColumnEditor: `h-6 w-full rounded border border-border bg-background px-1 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring` for selects; `h-6 rounded border border-border bg-background px-1.5 text-xs` for text/number inputs

- [x] Task 3: Update `FilterConditionsDialog.tsx` to accept `nodes` and use `FilterConditionRow` (AC: 1)
  - [x] Add `nodes: TableNodeType[]` to `FilterConditionsDialogProps`
  - [x] Add `nodes: TableNodeType[]` to `FilterGroupContentProps`
  - [x] Thread `nodes` prop down from `FilterConditionsDialog` → `FilterGroupContent` → condition branch
  - [x] In `FilterGroupContent`, replace the condition placeholder div with `<FilterConditionRow condition={item} nodes={nodes} onChange={onUpdateCondition} onDelete={() => onDeleteItem(item.id)} />` (the `onUpdateCondition` callback wraps `treeUpdateCondition` in the dialog, matching the existing handler pattern rather than inlining `setLocalFilters` in the recursive sub-component)
  - [x] Add pure tree-mutation helper `treeUpdateCondition(root: FilterGroup, updated: FilterCondition): FilterGroup` at module scope alongside existing helpers (same immutable-update pattern as `treeSetCombinator` etc.). Recursively finds the condition by `id` and replaces it.
  - [x] Import `FilterConditionRow` from `./FilterConditionRow`
  - [x] `FilterConditionRow` renders its own delete button; the outer `onDeleteItem(item.id)` callback is passed as `onDelete` prop. The Story 10.5 placeholder div + its inline X button are removed (now encapsulated in `FilterConditionRow`).
  - [x] `handleAddCondition` updated to initialize `tableName` to first node's tableName (if nodes exist), `columnName` to first column of that table (if columns exist), `value: ''` (default operator `=` uses the single-string empty shape)
  - [x] Pass `nodes` down to `FilterGroupContent`

- [x] Task 4: Update `QueryBuilderCanvas.tsx` to pass `nodes` to `FilterConditionsDialog` (AC: 1)
  - [x] In `QueryBuilderCanvasInner`, added `nodes={nodes}` prop to the `<FilterConditionsDialog>` render
  - [x] No other changes needed in this file

- [x] Task 5: Add i18n keys to `web/src/lib/i18n/locales/en.json` (AC: 1, 3, 4)
  - [x] Inside the existing `datasets.builder.filterDialog` object, added:
    - `"tableLabel": "Table"`
    - `"columnLabel": "Column"`
    - `"operatorLabel": "Operator"`
    - `"valueLabel": "Value"`
    - `"valueBetweenFrom": "From"`
    - `"valueBetweenTo": "To"`
    - `"valueInHint": "Comma-separated"`
    - `"noColumnsOption": "No columns"`

- [x] Task 6: Unit tests in `web/src/features/datasets/types/__tests__/builderState.test.ts` (AC: 3, 4)
  - [x] Add test: `FilterCondition value type — single value stored as string`
  - [x] Add test: `FilterCondition value type — IN operator stored as string[]`
  - [x] Add test: `FilterCondition value type — BETWEEN operator stored as string[] with two elements`
  - [x] Add test: `FilterCondition value type — IS NULL operator stored as null`
  - [x] All 4 tests use simple data shapes (no component rendering needed — pure type-level shape assertions via the `parseBuilderState` round-trip). NOTE: the existing test file lives at `types/__tests__/builderState.test.ts` (not `__tests__/`); appended there per Dev Notes §12.

- [x] Task 7: Verify — tsc 0 errors, vitest all passed (323), i18n-check exits 0

### Review Findings

Adversarial code review 2026-06-04 (Blind Hunter + Edge Case Hunter + Acceptance Auditor). **Acceptance Auditor: all 5 ACs met, all 7 tasks complete — no AC violations.** Triage below.

- [x] [Review][Defer] IN / NOT IN comma-separated input fights the user mid-typing — `FilterConditionRow.tsx` array-value branch. The input is controlled from `value={(value as string[]).join(', ')}` while `onChange` does `split(',').map(trim).filter(Boolean)`. Typing a trailing comma to start a second entry produces an empty token that `filter(Boolean)` drops, so the comma is erased on the next render — multi-value entry only works by inserting commas mid-string, and comma-bearing single values cannot be represented. This logic conforms exactly to the spec (Task 2 IN/NOT IN sub-point + Dev Notes §10 v1 comma-separated approach; full tag-input UX already deferred). **Deferred (reason):** Conforms to the spec's documented v1 comma-separated approach; richer tag-input UX already deferred. (blind+edge)

- [x] [Review][Defer] Stale table/column references render a dangling `<select>` and persist a reference to a removed table [`FilterConditionRow.tsx`] — in-session repro: add a condition on table A, delete A's node from the canvas, reopen the dialog. The table/column `<select value=...>` has no matching `<option>`, so React warns and the browser shows the first option while state retains the stale name; `pgType` falls back to `'text'`. Maps to the existing deferred item "Filter cascade-delete on canvas node removal deferred to 11.1/11.2"; SQL gen (11.1) validates the filter tree against canvas state. Deferred, consistent with `CaseColumnEditor`'s same posture. (blind+edge)
- [x] [Review][Defer] Self-join filter conditions are not addressable [`FilterConditionRow.tsx`] — `FilterCondition` keys on `tableName` (per the 10.5 type + spec Task 2), so two canvas nodes with the same table name both render `<option value={tableName}>` and `nodes.find(n => n.data.tableName === …)` always resolves the first node; the second instance's columns/schema can't be targeted. `CaseColumnEditor` keys on node `id` and does not have this limit. Design-level (spec-mandated shape); record for if self-join filtering is needed. (edge)
- [x] [Review][Defer] No validation gate for filter conditions — applyable degenerate conditions reach SQL gen [`FilterConditionRow.tsx`, `builderState.ts`] — a condition with empty `tableName`/`columnName` (empty canvas or zero-column table) or an untouched boolean-equality whose value stays `''` (the `=` empty shape is `''`, not `'true'`/`'false'`) can be applied; no `getFilter*Error` SSOT gate exists (unlike alias gates). Owned by Story 11.1 ("validate the filter tree against the current canvas state") / 11.2. (blind+edge)
- [x] [Review][Defer] `treeUpdateCondition` rewrites every condition whose `id` matches and has no duplicate-id guard [`FilterConditionsDialog.tsx`] — follows the existing `treeRemoveItem`/`treeSetCombinator` pattern; the only id-collision source (`_filterIdSeq` reset on HMR / persistence restore) is already the deferred "_filterIdSeq counter resets → ID collision" item from Story 10.5. (blind)
- [x] [Review][Defer] One-element BETWEEN array from partial/round-tripped data is malformed until an input is touched [`FilterConditionRow.tsx`] — in-session `handleOperatorChange` always seeds `['', '']`, so this only arises from externally-shaped data; `arr[1] ?? ''` self-heals on first edit. Maps to the existing deferred "parseBuilderState normalization for filters.items" (11.2). (blind+edge)

Dismissed as noise / by-design (5): value-reset on operator & column change (spec-mandated — Task 2 + Dev Notes §5); BETWEEN→text fallback for boolean pgType (documented, degenerate range); `INPUT_CLASS` adds `w-full`+focus-ring vs the spec's literal string (a11y-consistent, AC-neutral); checkbox label placed beside the box rather than above (conventional, AC-neutral); orphaned `conditionPlaceholder` i18n key (intentionally retained per Dev Agent Record).

## Dev Notes

### 1. Current State After Story 10.5

`FilterConditionsDialog.tsx` (`web/src/components/query-builder/FilterConditionsDialog.tsx`, 378 lines) was created in Story 10.5. It has:
- Pure tree-mutation helpers at module scope: `treeSetCombinator`, `treeAddItem`, `treeRemoveItem`, `treeMoveItem`
- `FilterGroupContent` recursive sub-component that renders AND/OR toggle, Add popover, item list
- **Condition placeholder at line 219–233**: a `<div>` with `t('datasets.builder.filterDialog.conditionPlaceholder')` — Story 10.6 replaces this with `<FilterConditionRow>`
- The dialog does NOT currently receive `nodes` — this must be added

`builderState.ts` (`web/src/features/datasets/types/builderState.ts`) has `FilterCondition.value: string | null` with a comment at line 90: *"Story 10.6 expands multi-value (IN/NOT IN) and range (BETWEEN) inputs."* — Task 1 delivers this expansion.

`QueryBuilderCanvas.tsx` renders `<FilterConditionsDialog>` at lines 769–774 with `isOpen`, `filters`, `onSave`, `onClose` props. Task 4 adds `nodes={nodes}`.

### 2. Why `value: string | string[] | null`

The architecture spec (Decision 6.11) uses `value: unknown` with a separate `valueType`, but Story 10.5's implementation simplified to `value: string | null`. Story 10.6 expands to `string | string[] | null` as a middle path:
- Avoids losing type safety (`unknown`)  
- Handles multi-value operators without encoding tricks (no comma-encoding that the SQL generator would have to re-split)
- SQL generator (Story 11.1) reads: `Array.isArray(condition.value)` to know whether to emit `IN ($1, $2, …)` or `BETWEEN $1 AND $2`

### 3. `treeUpdateCondition` helper

Add at module scope in `FilterConditionsDialog.tsx` alongside the existing helpers:

```typescript
function treeUpdateCondition(root: FilterGroup, updated: FilterCondition): FilterGroup {
  return {
    ...root,
    items: root.items.map((item) =>
      item.kind === 'condition' && item.id === updated.id
        ? updated
        : item.kind === 'group'
          ? treeUpdateCondition(item, updated)
          : item,
    ),
  }
}
```

### 4. `FilterConditionRow` — delete button placement

In Story 10.5, the delete button for a condition row was rendered outside the condition div:

```tsx
{item.kind === 'condition' && (
  <div className="flex flex-1 ...">
    <span ...>{t('conditionPlaceholder')}</span>
    <button onClick={() => onDeleteItem(item.id)}>X</button>   {/* delete button INSIDE the div */}
  </div>
)}
```

The delete button was already inside the condition div. `FilterConditionRow` receives `onDelete` prop, calls `onDeleteItem(item.id)` internally. Remove the separate `onDeleteItem` threaded through `FilterGroupContentProps` — it is now only needed for groups. **Wait**: actually, looking at the code, `onDeleteItem` is still needed for groups. Keep `onDeleteItem` on `FilterGroupContentProps` for groups; `FilterConditionRow` gets its own `onDelete` callback. The `FilterGroupContent` component wires:
- Conditions: `onDelete={() => onDeleteItem(item.id)}` passed as prop to `FilterConditionRow`
- Groups: `onDeleteItem(item.id)` directly on the group's X button (unchanged)

### 5. Resetting `value` on operator/table/column change

When operator changes, reset value to the appropriate empty shape:
```typescript
const OPERATORS_NULL_VALUE: FilterOperator[] = ['IS NULL', 'IS NOT NULL']
const OPERATORS_ARRAY_VALUE: FilterOperator[] = ['IN', 'NOT IN']
const OPERATORS_BETWEEN_VALUE: FilterOperator[] = ['BETWEEN']

function emptyValueForOperator(op: FilterOperator): string | string[] | null {
  if (OPERATORS_NULL_VALUE.includes(op)) return null
  if (OPERATORS_ARRAY_VALUE.includes(op)) return []
  if (OPERATORS_BETWEEN_VALUE.includes(op)) return ['', '']
  return ''
}
```

When table changes: reset `columnName` to first column of new table (or `''` if none), reset `value` via `emptyValueForOperator(condition.operator)`.

When column changes: reset `value` via `emptyValueForOperator(condition.operator)` — the pgType may have changed and the stored value from a different type would be mismatched.

### 6. PG type detection

`pgType` is in `ColumnSelection.pgType` (available from `nodes.find(n => n.data.tableName === tableName)?.data.columns.find(c => c.columnName === columnName)?.pgType`). The pgType string comes from `information_schema.columns.data_type` via the catalog endpoint. Typical values: `integer`, `bigint`, `smallint`, `real`, `double precision`, `numeric`, `text`, `character varying`, `boolean`, `timestamp with time zone`, `timestamp without time zone`, `date`, `uuid`, etc.

Map them to input types:
```typescript
function inputTypeForPgType(pgType: string): 'text' | 'number' | 'datetime-local' | 'date' | 'checkbox' {
  const lower = pgType.toLowerCase()
  if (['integer','bigint','smallint','real','double precision','numeric','decimal','money','int4','int8','int2','float4','float8'].includes(lower)) return 'number'
  if (['timestamp with time zone','timestamptz','timestamp without time zone','timestamp'].includes(lower)) return 'datetime-local'
  if (['date'].includes(lower)) return 'date'
  if (['boolean','bool'].includes(lower)) return 'checkbox'
  return 'text'
}
```

For multi-value inputs (`IN`/`NOT IN`) and `BETWEEN`, use the same `inputType` for each individual input within the multi-value UI.

### 7. `handleAddCondition` initialization improvement

Currently initializes `tableName: ''`. Update to initialize with the first available node, if any:

```typescript
function handleAddCondition(groupId: string) {
  const firstNode = nodes.length > 0 ? nodes[0] : null
  const firstCol = firstNode?.data.columns[0]?.columnName ?? ''
  const newCond: FilterCondition = {
    id: nextFilterId('cond'),
    kind: 'condition',
    tableName: firstNode?.data.tableName ?? '',
    columnName: firstCol,
    operator: '=',
    value: '',
  }
  setLocalFilters((prev) => treeAddItem(prev, groupId, newCond))
}
```

`nodes` is available as a prop on `FilterConditionsDialog`, passed to the handler via closure or threaded through `FilterGroupContentProps` as `onAddCondition`.

**Cleaner approach**: keep `onAddCondition(groupId)` on `FilterGroupContentProps` but change the callback to accept initial `tableName`/`columnName` values computed in `FilterConditionsDialog.handleAddCondition` (which has access to `nodes`). The existing handler is in `FilterConditionsDialog` and can close over `nodes` — no change to `FilterGroupContentProps.onAddCondition` signature needed.

### 8. Files Created/Modified

| File | Action |
|------|--------|
| `web/src/features/datasets/types/builderState.ts` | UPDATE — expand `FilterCondition.value` type |
| `web/src/components/query-builder/FilterConditionsDialog.tsx` | UPDATE — add `nodes` prop, `treeUpdateCondition`, use `FilterConditionRow` |
| `web/src/components/query-builder/FilterConditionRow.tsx` | NEW — condition row with table/column/operator/value inputs |
| `web/src/features/datasets/QueryBuilderCanvas.tsx` | UPDATE — add `nodes={nodes}` to `FilterConditionsDialog` |
| `web/src/lib/i18n/locales/en.json` | UPDATE — add 8 new `datasets.builder.filterDialog.*` keys |
| `web/src/features/datasets/__tests__/builderState.test.ts` | UPDATE — add 4 new tests |

### 9. Operator Vocabulary

`FILTER_OPERATORS` is imported from `builderState.ts` (already defined in Story 10.5 as `[...CASE_OPERATORS]`). The 13 operators are exactly the spec's AC-2 list. Use `FILTER_OPERATORS` (not `CASE_OPERATORS`) for the filter row's operator select.

### 10. IN/NOT IN Comma-Separated UI

The spec says "multi-value tag input" — full tag-input (enter key to add, X to remove each tag) — but no tag-input component exists in the codebase. Use a simpler approach for v1:
- A `<input type="text">` where the user types comma-separated values
- Below it, show a `<span>` hint: `t('datasets.builder.filterDialog.valueInHint')` = "Comma-separated"
- On change: `value.split(',').map(s => s.trim()).filter(Boolean)` → stored as `string[]`
- On render: `(condition.value as string[]).join(', ')`

This is parseable by Story 11.1's SQL generator (`IN ($1, $2, ...)` with the array entries as separate params). Add a deferred-work entry noting the full tag-input UX as a future enhancement.

### 11. Deferred Work — Carry Forward

The deferred item from Story 10.5 is explicit:
> **Condition row inputs (table/column/operator/value) deferred to Story 10.6** — FilterCondition rows in Story 10.5 show a "(Condition)" placeholder. Story 10.6 replaces this placeholder with the actual table selector + column selector + operator dropdown + adaptive value input. [`FilterConditionsDialog.tsx`]

Story 10.6 delivers this. Add a new deferred item for the tag-input UX enhancement (IN/NOT IN uses comma-separated text input for v1).

### 12. Test File Location

Existing tests in `web/src/features/datasets/__tests__/builderState.test.ts` (confirmed: 319 passed after Story 10.5). Add new tests at the end of this file. Do NOT create a separate test file.

### 13. AC-5 (Server Parameterization) — No Backend Work

AC-5 is fulfilled by Story 11.1's `DatasetSqlGenerator.cs`. Story 10.6 only ensures the `value` field shape is correct for the generator to consume. No backend changes in this story.

### Project Structure Notes

- `FilterConditionRow.tsx` follows the same directory as `FilterConditionsDialog.tsx`: `web/src/components/query-builder/`
- The `@xyflow/react` v12 typing rules (memory: NodeProps keyed on Node type; `data` must be `type` alias not `interface`) do not apply here — `FilterConditionRow` is not a React Flow node/edge component
- `TableNodeType` type (from `TableNode.tsx`) is used for the `nodes` prop — import as a type import

### References

- `FilterConditionsDialog.tsx`: `web/src/components/query-builder/FilterConditionsDialog.tsx` (lines 219–233 for placeholder to replace; lines 86–96 for `FilterGroupContentProps`; lines 309–318 for `handleAddCondition`)
- `builderState.ts`: `web/src/features/datasets/types/builderState.ts` (lines 91–98 for `FilterCondition`; lines 122–127 for `FilterOperator`/`FILTER_OPERATORS`)
- `CaseColumnEditor.tsx`: `web/src/components/query-builder/CaseColumnEditor.tsx` — reference for table selector + column selector + operator select + value input patterns (lines 126–203)
- `QueryBuilderCanvas.tsx`: `web/src/features/datasets/QueryBuilderCanvas.tsx` (lines 769–774 for `FilterConditionsDialog` render)
- `en.json`: `web/src/lib/i18n/locales/en.json` (lines 627–646 for existing `filterDialog` keys)
- Architecture Decision 6.10: `DatasetSqlGenerator.cs` step 8 (parameterized WHERE) — confirms `string[]` for IN/NOT IN, `[from, to]` for BETWEEN
- Architecture Decision 6.11: `FilterCondition` shape contract — confirms `value: unknown` (implemented as `string | string[] | null`)
- Epics: `_bmad-output/planning-artifacts/epics.md`, Story 10.6 section (FR-68 J-6)
- Deferred work: `_bmad-output/implementation-artifacts/deferred-work.md` line 7 — explicit pointer to this story

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (1M context)

### Debug Log References

- `npx tsc -b --noEmit` → 0 errors
- `npm run test` (vitest) → 27 files / 323 tests passed (was 319 after Story 10.5; +4 new)
- `npm run lint:i18n` → exit 0 (advisory orphaned-key list only; now includes `conditionPlaceholder`, which is intentionally retained but no longer referenced after the placeholder was replaced)
- `npm run lint` (eslint) → repo baseline is red (pre-existing `react-hooks/refs` + `set-state-in-effect` errors across untouched route files / `QueryBuilderCanvas` ref-mirrors / the Story 10.5 sync effect). The new file `FilterConditionRow.tsx` is lint-clean; no new errors introduced by this story.

### Completion Notes List

- **AC-1**: `FilterConditionRow` renders table / column / operator selectors + adaptive value input; wired into `FilterConditionsDialog` for every condition at any nesting depth.
- **AC-2**: Operator `<select>` is populated from `FILTER_OPERATORS` (the canonical 13-operator vocabulary), not `CASE_OPERATORS`.
- **AC-3**: `emptyValueForOperator` centralizes value-shape reset on table/column/operator change — `null` (IS NULL/IS NOT NULL → no input), `string[]` (IN/NOT IN → comma-separated input), `['', '']` (BETWEEN → two inputs), `''` (all others → single input).
- **AC-4**: `inputTypeForPgType` maps PG types to `number` / `datetime-local` / `date` / `checkbox` / `text`. Both canonical (`integer`, `timestamp with time zone`) and short (`int4`, `timestamptz`) forms are matched. BETWEEN falls back to `text` when the pgType is boolean (a boolean range is degenerate). Checkbox stores `'true'`/`'false'` to keep the single-value shape a uniform `string`.
- **AC-5**: No backend work — `value` is stored in the correct `string | string[] | null` shape for Story 11.1's `DatasetSqlGenerator.cs` to parameterize; verified by the 4 round-trip tests.
- **Deviation from spec snippet (Task 3)**: instead of inlining `setLocalFilters(...)` inside the recursive `FilterGroupContent` (which has no access to it), an `onUpdateCondition` callback wrapping `treeUpdateCondition` was added to the dialog and threaded down — consistent with the existing `onSetCombinator`/`onAddCondition` handler pattern.
- **Path correction (Task 6)**: the existing builderState test file is at `web/src/features/datasets/types/__tests__/builderState.test.ts`; tests were appended there (Dev Notes §12: do not create a separate file).
- **Deferred work**: tag-input UX for IN/NOT IN (comma-separated text used for v1), server parameterization (Story 11.1), and stale-reference pruning (Story 11.1/11.2) recorded in `deferred-work.md`.

### File List

- `web/src/features/datasets/types/builderState.ts` (MODIFIED — expanded `FilterCondition.value` to `string | string[] | null`; updated doc comment)
- `web/src/components/query-builder/FilterConditionRow.tsx` (NEW — condition row: table/column/operator selectors + adaptive value input)
- `web/src/components/query-builder/FilterConditionsDialog.tsx` (MODIFIED — `nodes` prop, `treeUpdateCondition` helper, `onUpdateCondition` wiring, render `FilterConditionRow`, seeded `handleAddCondition`)
- `web/src/features/datasets/QueryBuilderCanvas.tsx` (MODIFIED — pass `nodes={nodes}` to `FilterConditionsDialog`)
- `web/src/lib/i18n/locales/en.json` (MODIFIED — 8 new `datasets.builder.filterDialog.*` keys)
- `web/src/features/datasets/types/__tests__/builderState.test.ts` (MODIFIED — 4 new `FilterCondition value type` tests; added `parseBuilderState`, `BuilderState`, `FilterCondition` imports)
- `_bmad-output/implementation-artifacts/deferred-work.md` (MODIFIED — Story 10.6 deferred-work entries)

## Change Log

| Date | Version | Description | Author |
|------|---------|-------------|--------|
| 2026-06-04 | 1.0 | Implemented Story 10.6: filter condition definition (table/column/operator/adaptive value inputs); expanded `FilterCondition.value` to `string \| string[] \| null`; 4 new round-trip tests. Status → review. | claude-opus-4-8 (Dev) |
