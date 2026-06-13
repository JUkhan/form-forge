# Story 11.2: builder_state Persistence and Restore

Status: done

## Story

As a user in Query Builder Mode,
I want to save my canvas and reopen the same Dataset later to find my canvas exactly as I left it,
So that work is not lost between sessions.

## Acceptance Criteria

1. **Given** PUT /api/datasets/{id} with `is_custom_query = false`, **When** the save transaction commits, **Then** the full `builder_state` JSON (nodes with positions and all configuration, edges, column selections, filter state, order clauses, CASE columns, calculated columns) is persisted to `custom_dataset.builder_state` (per FR-71 AC-1 / AR-67). *(Backend: already done in 11.1 for UpdateAsync; this story gates the UI Save button and wires CreateAsync.)*

2. **Given** I open an existing Dataset in Query Builder Mode, **When** the canvas loads, **Then** the frontend restores the canvas from `builder_state`: table nodes at saved positions, edges drawn, column checkboxes checked, aggregate/alias values filled, filter dialog state loaded, ORDER BY clauses listed (per FR-71 AC-2). *(Frontend: Save button + `parseBuilderState` normalization; the canvas already reads `initialState` but the Save path is not yet wired.)*

3. **Given** `builder_state` is null, **When** the canvas loads, **Then** the canvas opens empty (per FR-71 AC-3). *(Already works via `parseBuilderState` returning `EMPTY_BUILDER_STATE` on null; confirmed by the normalization tests in Task 7.)*

4. **Given** any successful builder-mode save, **When** I inspect `custom_dataset`, **Then** both `query` (server-generated SQL) and `builder_state` (raw canvas state) are updated in the same transaction — they are always in sync (per FR-71 AC-4 / AR-66). *(UpdateAsync already does this from 11.1; CreateAsync wired in Task 3.)*

## Tasks / Subtasks

---

### Task 1: Frontend — Wire Save button in `DatasetBuilderPage` (AC: 2)

- [x] Modify `web/src/routes/_app/admin/datasets_.$id.tsx` (MODIFY existing file)
  - [x] Import `useState` (already imported), `useCallback` (already imported), `useMutation`, `useQueryClient` from `@tanstack/react-query`; import `toast` from `sonner`; import `useTranslation` from `react-i18next`; import `updateDataset` from `../../../features/datasets/datasetApi`; import the four validation functions from `types/builderState`:
    ```typescript
    import { useMutation, useQueryClient } from '@tanstack/react-query'
    import { toast } from 'sonner'
    import { useTranslation } from 'react-i18next'
    import { updateDataset } from '../../../features/datasets/datasetApi'
    import {
      getLeftTableValidationError,
      getColumnSelectionValidationError,
      getCaseColumnAliasError,
      getCalculatedColumnAliasError,
    } from '../../../features/datasets/types/builderState'
    import { ApiError } from '../../../lib/api/apiError'
    import { DATASETS_LIST_QUERY_KEY } from '../../../features/datasets/useDatasetListQuery'
    ```
  - [x] In `DatasetBuilderPage`, add a `currentVersion` state variable initialized from `dataset.version`:
    ```typescript
    const [currentVersion, setCurrentVersion] = useState(dataset.version)
    ```
    This local state tracks the version so subsequent saves (without page reload) increment correctly.
  - [x] Add the save mutation:
    ```typescript
    const { t } = useTranslation()
    const queryClient = useQueryClient()
    const saveMutation = useMutation({
      mutationFn: (state: BuilderState) =>
        updateDataset(dataset.id, {
          isCustomQuery: false,
          builderState: JSON.stringify(state),
          version: currentVersion,
        }),
      onSuccess: (saved) => {
        setCurrentVersion(saved.version)
        void queryClient.invalidateQueries({ queryKey: DATASETS_LIST_QUERY_KEY })
        void queryClient.invalidateQueries({ queryKey: ['datasets', dataset.id] })
        toast.success(t('datasets.builder.saveSuccess'))
      },
      onError: (err) => {
        if (
          err instanceof ApiError &&
          err.status === 422 &&
          err.code === 'BUILDER_STATE_INVALID'
        ) {
          toast.error(err.detail?.trim() ? err.detail : t('datasets.builderStateInvalid'))
          return
        }
        if (
          err instanceof ApiError &&
          err.status === 409 &&
          err.code === 'DATASET_CONCURRENCY_CONFLICT'
        ) {
          toast.error(t('datasets.concurrencyConflict'))
          return
        }
        toast.error(t('errors.genericError'))
      },
    })
    ```
  - [x] Derive the validation gate in the component body (after `builderState` state declaration):
    ```typescript
    const hasValidationError =
      builderState.nodes.length === 0 ||
      !!getLeftTableValidationError(builderState.nodes) ||
      !!getColumnSelectionValidationError(builderState.nodes) ||
      !!getCaseColumnAliasError(builderState.caseColumns) ||
      !!getCalculatedColumnAliasError(builderState.calculatedColumns)
    ```
    This gates the Save button without duplicating the banner logic inside the canvas; the canvas still shows validation banners independently.
  - [x] Remove the TODO comment from `handleStateChange`:
    ```typescript
    const handleStateChange = useCallback((state: BuilderState) => {
      setBuilderState(state)
    }, [])
    ```
  - [x] Add a Save button to the page header (next to the dataset name):
    ```tsx
    <Button
      size="sm"
      disabled={hasValidationError || saveMutation.isPending}
      onClick={() => saveMutation.mutate(builderState)}
    >
      {saveMutation.isPending
        ? t('datasets.builder.savingButton')
        : t('datasets.builder.saveButton')}
    </Button>
    ```
    Import `Button` from `@/components/ui/button`.

---

### Task 2: Frontend — `parseBuilderState` normalization (AC: 2, 3)

- [x] Modify `web/src/features/datasets/types/builderState.ts` (MODIFY existing file)
  - [x] Replace the current `parseBuilderState` body with a field-by-field normalization pass that handles every array field missing or with the wrong shape:
    ```typescript
    export function parseBuilderState(raw: string | null): BuilderState {
      if (!raw) return EMPTY_BUILDER_STATE
      try {
        const parsed: unknown = JSON.parse(raw)
        if (typeof parsed !== 'object' || parsed === null) return EMPTY_BUILDER_STATE
        const p = parsed as Record<string, unknown>

        // nodes: normalize position + data.columns per-field defaults
        const nodes: TableNodeState[] = Array.isArray(p.nodes)
          ? (p.nodes as unknown[]).flatMap((n): TableNodeState[] => {
              if (typeof n !== 'object' || n === null) return []
              const nn = n as Record<string, unknown>
              const data = (typeof nn.data === 'object' && nn.data !== null)
                ? (nn.data as Record<string, unknown>) : {}
              const columns: ColumnSelection[] = Array.isArray(data.columns)
                ? (data.columns as unknown[]).flatMap((c): ColumnSelection[] => {
                    if (typeof c !== 'object' || c === null) return []
                    const cc = c as Record<string, unknown>
                    return [{
                      columnName: typeof cc.columnName === 'string' ? cc.columnName : '',
                      pgType: typeof cc.pgType === 'string' ? cc.pgType : 'text',
                      checked: cc.checked === true,
                      aggregate: (cc.aggregate as AggregateFunction | undefined) ?? 'none',
                      alias: typeof cc.alias === 'string' ? cc.alias : '',
                    }]
                  })
                : []
              return [{
                id: typeof nn.id === 'string' ? nn.id : '',
                type: 'tableNode' as const,
                position: {
                  x: typeof (nn.position as Record<string, unknown>)?.x === 'number'
                    ? (nn.position as Record<string, unknown>).x as number : 0,
                  y: typeof (nn.position as Record<string, unknown>)?.y === 'number'
                    ? (nn.position as Record<string, unknown>).y as number : 0,
                },
                data: {
                  tableName: typeof data.tableName === 'string' ? data.tableName : '',
                  side: (data.side === 'left' || data.side === 'right') ? data.side : 'right',
                  columns,
                },
              }]
            })
          : []

        // edges: normalize required fields
        const edges: JoinEdgeState[] = Array.isArray(p.edges)
          ? (p.edges as unknown[]).flatMap((e): JoinEdgeState[] => {
              if (typeof e !== 'object' || e === null) return []
              const ee = e as Record<string, unknown>
              const edgeData = (typeof ee.data === 'object' && ee.data !== null)
                ? (ee.data as Record<string, unknown>) : {}
              return [{
                id: typeof ee.id === 'string' ? ee.id : '',
                source: typeof ee.source === 'string' ? ee.source : '',
                target: typeof ee.target === 'string' ? ee.target : '',
                sourceHandle: typeof ee.sourceHandle === 'string' ? ee.sourceHandle : '',
                targetHandle: typeof ee.targetHandle === 'string' ? ee.targetHandle : '',
                type: 'joinEdge' as const,
                data: {
                  sourceColumn: typeof edgeData.sourceColumn === 'string' ? edgeData.sourceColumn : '',
                  targetColumn: typeof edgeData.targetColumn === 'string' ? edgeData.targetColumn : '',
                  joinType: (['INNER','LEFT','RIGHT','FULL OUTER'] as JoinType[])
                    .includes(edgeData.joinType as JoinType)
                    ? (edgeData.joinType as JoinType) : 'INNER',
                },
              }]
            })
          : []

        // filters: normalize the root FilterGroup
        const rawFilters = p.filters
        const filters: FilterGroup =
          typeof rawFilters === 'object' && rawFilters !== null &&
          (rawFilters as Record<string, unknown>).kind === 'group'
            ? normalizeFilterGroup(rawFilters as Record<string, unknown>)
            : EMPTY_BUILDER_STATE.filters

        // orderBy, caseColumns, calculatedColumns: normalize arrays
        const orderBy: OrderByClause[] = Array.isArray(p.orderBy)
          ? (p.orderBy as unknown[]).flatMap((o): OrderByClause[] => {
              if (typeof o !== 'object' || o === null) return []
              const oo = o as Record<string, unknown>
              return [{
                tableName: typeof oo.tableName === 'string' ? oo.tableName : '',
                columnName: typeof oo.columnName === 'string' ? oo.columnName : '',
                direction: (oo.direction === 'DESC') ? 'DESC' : 'ASC',
              }]
            })
          : []

        const caseColumns: CaseColumn[] = Array.isArray(p.caseColumns)
          ? (p.caseColumns as unknown[]).flatMap((c): CaseColumn[] => {
              if (typeof c !== 'object' || c === null) return []
              const cc = c as Record<string, unknown>
              const whens: CaseWhen[] = Array.isArray(cc.whens)
                ? (cc.whens as unknown[]).flatMap((w): CaseWhen[] => {
                    if (typeof w !== 'object' || w === null) return []
                    const ww = w as Record<string, unknown>
                    return [{
                      nodeId: typeof ww.nodeId === 'string' ? ww.nodeId : '',
                      columnName: typeof ww.columnName === 'string' ? ww.columnName : '',
                      operator: (CASE_OPERATORS as string[]).includes(ww.operator as string)
                        ? (ww.operator as CaseOperator) : '=',
                      operandValue: typeof ww.operandValue === 'string' ? ww.operandValue : '',
                      thenValue: typeof ww.thenValue === 'string' ? ww.thenValue : '',
                    }]
                  })
                : []
              return [{
                id: typeof cc.id === 'string' ? cc.id : '',
                nodeId: typeof cc.nodeId === 'string' ? cc.nodeId : '',
                alias: typeof cc.alias === 'string' ? cc.alias : '',
                whens,
                elseValue: typeof cc.elseValue === 'string' ? cc.elseValue : '',
              }]
            })
          : []

        const calculatedColumns: CalculatedColumn[] = Array.isArray(p.calculatedColumns)
          ? (p.calculatedColumns as unknown[]).flatMap((c): CalculatedColumn[] => {
              if (typeof c !== 'object' || c === null) return []
              const cc = c as Record<string, unknown>
              return [{
                id: typeof cc.id === 'string' ? cc.id : '',
                nodeId: typeof cc.nodeId === 'string' ? cc.nodeId : '',
                alias: typeof cc.alias === 'string' ? cc.alias : '',
                expression: typeof cc.expression === 'string' ? cc.expression : '',
              }]
            })
          : []

        return { nodes, edges, filters, orderBy, caseColumns, calculatedColumns }
      } catch {
        return EMPTY_BUILDER_STATE
      }
    }

    function normalizeFilterGroup(raw: Record<string, unknown>): FilterGroup {
      return {
        id: typeof raw.id === 'string' ? raw.id : 'root',
        kind: 'group',
        combinator: raw.combinator === 'OR' ? 'OR' : 'AND',
        items: Array.isArray(raw.items)
          ? (raw.items as unknown[]).flatMap((item): (FilterCondition | FilterGroup)[] => {
              if (typeof item !== 'object' || item === null) return []
              const it = item as Record<string, unknown>
              if (it.kind === 'group') return [normalizeFilterGroup(it)]
              if (it.kind === 'condition') {
                return [{
                  id: typeof it.id === 'string' ? it.id : '',
                  kind: 'condition',
                  tableName: typeof it.tableName === 'string' ? it.tableName : '',
                  columnName: typeof it.columnName === 'string' ? it.columnName : '',
                  operator: (CASE_OPERATORS as string[]).includes(it.operator as string)
                    ? (it.operator as FilterOperator) : '=',
                  value: Array.isArray(it.value)
                    ? (it.value as string[])
                    : (it.value === null ? null : String(it.value ?? '')),
                }]
              }
              return []
            })
          : [],
      }
    }
    ```
    **Note:** The `normalizeFilterGroup` helper is a private function (not exported) added after `parseBuilderState`. It handles recursive filter group normalization.

  - [x] Export a `seedFilterIdCounter` function that seeds the `_filterIdSeq` counter from the max numeric suffix found in an existing `FilterGroup` tree (prevents ID collisions on restore). This function must be placed in `FilterConditionsDialog.tsx` — see Task 4. **Do NOT add it to `builderState.ts`**.

---

### Task 3: Frontend — Seed canvas ID counters on mount (AC: 2)

- [x] Modify `web/src/features/datasets/QueryBuilderCanvas.tsx` (MODIFY existing file)
  - [x] Add a helper to extract the max numeric suffix from persisted IDs (place above the component, not exported):
    ```typescript
    function maxSuffixFromIds(ids: string[]): number {
      return ids.reduce((max, id) => {
        const m = id.match(/-(\d+)$/)
        return m ? Math.max(max, parseInt(m[1], 10)) : max
      }, 0)
    }
    ```
  - [x] Initialize `caseIdCounterRef` from `initialState.caseColumns` by changing its declaration:
    ```typescript
    const caseIdCounterRef = useRef(
      maxSuffixFromIds(initialState.caseColumns.map((c) => c.id))
    )
    ```
    This ensures the first new CASE column gets an id that cannot collide with restored ids (e.g., if restored has `case-n1-3`, new ids start at 4).
  - [x] Initialize `calcIdCounterRef` from `initialState.calculatedColumns` by changing its declaration:
    ```typescript
    const calcIdCounterRef = useRef(
      maxSuffixFromIds(initialState.calculatedColumns.map((c) => c.id))
    )
    ```
  - [x] These changes are in `QueryBuilderCanvasInner` where the refs are declared. The `useRef(0)` calls are replaced with `useRef(maxSuffix...)`. The function call happens once at component mount (it's in the ref initializer, not in a useEffect).

---

### Task 4: Frontend — Seed `_filterIdSeq` from restored state (AC: 2)

- [x] Modify `web/src/components/query-builder/FilterConditionsDialog.tsx` (MODIFY existing file)
  - [x] Add an exported `seedFilterIdCounter` function that bumps `_filterIdSeq` to the max found in an existing filter tree:
    ```typescript
    // Called from QueryBuilderCanvas on mount (and after parseBuilderState) to
    // ensure nextFilterId() never re-uses an id from a restored filter tree.
    export function seedFilterIdCounter(group: FilterGroup): void {
      function walkMax(g: FilterGroup): number {
        return g.items.reduce((max, item) => {
          const m = item.id.match(/-(\d+)$/)
          const here = m ? parseInt(m[1], 10) : 0
          const sub = item.kind === 'group' ? walkMax(item) : 0
          return Math.max(max, here, sub)
        }, 0)
      }
      const found = walkMax(group)
      if (found > _filterIdSeq) _filterIdSeq = found
    }
    ```
    Place this function after the `nextFilterId` function, before the component.
  - [x] Call `seedFilterIdCounter` from `QueryBuilderCanvasInner` at mount: in `QueryBuilderCanvas.tsx`, add the import and call:
    ```typescript
    import { seedFilterIdCounter } from '../../components/query-builder/FilterConditionsDialog'
    // ...
    // Inside QueryBuilderCanvasInner, at the top of the function body (after the useState/useRef declarations):
    // Use a ref to ensure it runs only once per mount, not on every render:
    const _filterSeedRef = useRef(false)
    if (!_filterSeedRef.current) {
      _filterSeedRef.current = true
      seedFilterIdCounter(initialState.filters)
    }
    ```
    **Note:** This is intentionally an unconditional-but-gated-by-ref call at render time (not a `useEffect`) so it runs synchronously before the first filter Add, matching the pattern of the `useRef(maxSuffix...)` counter seeding above.

---

### Task 5: Backend — Add blank filter-condition pre-flight gate in `DatasetSqlGenerator` (AC: 4)

- [x] Modify `src/FormForge.Api/Features/Datasets/DatasetSqlGenerator.cs` (MODIFY existing file)
  - [x] In the `Generate` method, after the existing Step 1 pre-flight validation (the four existing checks: no left node, no columns, CASE alias empty, calculated alias empty), add a fifth check that validates all filter conditions have non-blank table and column names:
    ```csharp
    // Step 1 pre-flight (continued): blank filter condition tableName/columnName
    var blankFilterCondition = HasBlankFilterCondition(state.Filters);
    if (blankFilterCondition)
        errors.Add("All filter conditions must reference a table and column.");
    ```
  - [x] Add a private helper method `HasBlankFilterCondition` that recurses the filter tree:
    ```csharp
    private static bool HasBlankFilterCondition(FilterGroupDto? group)
    {
        if (group is null) return false;
        foreach (var item in group.Items ?? [])
        {
            switch (item)
            {
                case FilterConditionDto cond when
                    string.IsNullOrWhiteSpace(cond.TableName) ||
                    string.IsNullOrWhiteSpace(cond.ColumnName):
                    return true;
                case FilterGroupDto subGroup when HasBlankFilterCondition(subGroup):
                    return true;
            }
        }
        return false;
    }
    ```
  - [x] Return the pre-flight errors before any further processing (the existing pattern already does `if (errors.Count > 0) return SqlGenerationResult.WithErrors(errors)` or equivalent — check the actual code and follow the same return pattern).

---

### Task 6: Backend — Wire `CreateAsync` for builder-mode creates (AC: 4)

- [x] Modify `src/FormForge.Api/Features/Datasets/Dtos/CreateDatasetRequest.cs` (MODIFY)
  - [x] Add `BuilderState` field:
    ```csharp
    internal sealed record CreateDatasetRequest(
        string DatasetName,
        bool IsCustomQuery,
        string? Query,
        string? BuilderState);
    ```

- [x] Modify `src/FormForge.Api/Features/Datasets/DatasetService.cs` (MODIFY)
  - [x] Add `BuilderStateInvalid` to `CreateDatasetOutcome`:
    ```csharp
    internal enum CreateDatasetOutcome { Success, NameConflict, InvalidQuery, BuilderStateInvalid }
    ```
  - [x] Update `CreateDatasetResult` to carry `ErrorDetail` (for `BuilderStateInvalid` code path):
    ```csharp
    internal sealed record CreateDatasetResult(
        CreateDatasetOutcome Outcome,
        DatasetDto? Dataset = null,
        string? ErrorDetail = null);
    ```
    *(Check if `ErrorDetail` already exists — it may already be there from Story 8.4. If so, no change needed.)*
  - [x] In `CreateAsync`, after the `effectiveQuery` assignment, add the builder-mode SQL generation block, mirroring the UpdateAsync checkpoint (b) pattern:
    ```csharp
    // Story 11.2 — checkpoint (b) for CreateAsync: for builder mode, derive SQL
    // from builder_state on the server. Only runs when a BuilderState blob is provided
    // at create time (the current UI never sends one; this gate future-proofs the API
    // and resolves the deferred item from 11.1).
    var effectiveBuilderState = request.BuilderState;
    if (!request.IsCustomQuery && !string.IsNullOrWhiteSpace(effectiveBuilderState))
    {
        var bsDto = BuilderStateSerializer.Deserialize(effectiveBuilderState);
        if (bsDto is null)
            return new CreateDatasetResult(CreateDatasetOutcome.BuilderStateInvalid,
                ErrorDetail: "builder_state could not be parsed.");

        var generated = DatasetSqlGenerator.Generate(bsDto, allowlist);
        if (generated.HasErrors)
            return new CreateDatasetResult(CreateDatasetOutcome.BuilderStateInvalid,
                ErrorDetail: string.Join("; ", generated.Errors));

        effectiveQuery = generated.ViewSql!;
    }
    ```
    Place this block **after** the `effectiveQuery` assignment and **before** the `viewDdl` pre-build. The `effectiveQuery` variable must be `var` (not `const`/`string`) — verify it is declared as `var effectiveQuery = ...` and reassign if needed.
  - [x] Update the INSERT SQL in `CreateAsync` to persist `builder_state`:
    ```csharp
    const string insertSql = """
        INSERT INTO custom_dataset
            (id, dataset_name, is_custom_query, query, builder_state, version, created_at, created_by)
        VALUES
            (@id, @datasetName, @isCustomQuery, @query, @builderState::jsonb, 1, @now, @createdBy)
        """;
    ```
    And add `builderState = string.IsNullOrWhiteSpace(effectiveBuilderState) ? (object?)DBNull.Value : effectiveBuilderState` to the anonymous object passed to Dapper. *(Check the existing INSERT — it may already have `NULL` hardcoded for `builder_state`; replace that with the parameter.)*
  - [x] Update the success `DatasetDto` returned from `CreateAsync` to include `BuilderState`:
    ```csharp
    var dto = new DatasetDto(
        Id: newId,
        DatasetName: name.Value,
        IsCustomQuery: request.IsCustomQuery,
        Query: string.IsNullOrWhiteSpace(effectiveQuery) ? null : effectiveQuery,
        BuilderState: string.IsNullOrWhiteSpace(effectiveBuilderState) ? null : effectiveBuilderState,
        Version: 1,
        CreatedAt: now,
        CreatedBy: actorId);
    ```

- [x] Modify `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs` (MODIFY)
  - [x] In the `MapPost("/")` handler, add the new outcome to the `result.Outcome switch`:
    ```csharp
    CreateDatasetOutcome.BuilderStateInvalid => Results.Problem(
        detail: result.ErrorDetail,
        title: "Builder state invalid",
        statusCode: StatusCodes.Status422UnprocessableEntity,
        extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = "BUILDER_STATE_INVALID",
            ["messageKey"] = "datasets.builderStateInvalid",
        }),
    ```
    Add this case before the `_ =>` wildcard fallback, after the `InvalidQuery` case.

- [x] Modify `web/src/features/datasets/datasetApi.ts` (MODIFY)
  - [x] Add `builderState` to `CreateDatasetPayload`:
    ```typescript
    export interface CreateDatasetPayload {
      datasetName: string
      isCustomQuery: boolean
      query: string | null
      builderState?: string | null
    }
    ```

---

### Task 7: Frontend — `parseBuilderState` normalization unit tests (AC: 2, 3)

- [x] Modify `web/src/features/datasets/types/__tests__/builderState.test.ts` (MODIFY existing file)
  - [x] Add a new `describe('parseBuilderState normalization')` block with the following tests:
    - `returns EMPTY_BUILDER_STATE for null input`
    - `returns EMPTY_BUILDER_STATE for empty string`
    - `returns EMPTY_BUILDER_STATE for invalid JSON`
    - `returns EMPTY_BUILDER_STATE for a non-object JSON value (array at root)`
    - `normalizes a blob missing orderBy to empty array`
    - `normalizes a blob missing caseColumns to empty array`
    - `normalizes a blob missing calculatedColumns to empty array`
    - `normalizes a blob missing filters to EMPTY_BUILDER_STATE.filters`
    - `normalizes a blob where filters is wrong shape (not a group) to EMPTY_BUILDER_STATE.filters`
    - `normalizes per-column fields: missing checked → false, missing aggregate → 'none', missing alias → ''`
    - `normalizes edge data: missing joinType → 'INNER'`
    - `preserves valid nodes, edges, filters, orderBy, caseColumns, calculatedColumns on a round-trip`
    - `round-trips EMPTY_BUILDER_STATE unchanged`
    - `normalizes nested filter group items (recursive)`
    - `drops malformed nodes (non-object entries in the nodes array)`

---

### Task 8: Backend — Unit tests for `DatasetSqlGenerator` blank-condition gate (AC: 4)

- [x] Modify `src/FormForge.Api.Tests/Features/Datasets/DatasetSqlGeneratorTests.cs` (MODIFY existing file)
  - [x] Add tests for the new blank filter-condition gate:
    - `Generate_FilterCondition_BlankTableName_ReturnsError`: builder state with one left node + one checked column, filter condition with empty `tableName` → `HasErrors == true`, `Errors` contains "table and column"
    - `Generate_FilterCondition_BlankColumnName_ReturnsError`: same with empty `columnName`
    - `Generate_FilterCondition_WhitespaceName_ReturnsError`: tableName/columnName with only whitespace
    - `Generate_FilterCondition_ValidNames_NoError`: condition with non-empty table and column → no error (regression guard)
    - `Generate_NestedFilterGroup_BlankCondition_ReturnsError`: blank condition inside a nested sub-group → `HasErrors == true` (verify recursion)

---

### Task 9: Backend — Integration test for builder-mode create (AC: 4)

- [x] Modify `src/FormForge.Api.Tests/Features/Datasets/DatasetBuilderModeTests.cs` (MODIFY existing file)
  - [x] **Test 5: `Post_BuilderMode_WithBuilderState_Returns201_QueryAndStatePersis ted`**:
    - POST /api/datasets with `is_custom_query: false`, `builder_state: <valid builder state with allowlisted table + 1 checked column>`
    - Assert: 201, `isCustomQuery == false`, `query` contains SELECT…FROM, `builderState` is non-null and matches the input
    - GET /api/datasets/{id} → confirms both `query` and `builderState` persisted
  - [x] **Test 6: `Post_BuilderMode_NullBuilderState_Returns201_PlaceholderView`**:
    - POST /api/datasets with `is_custom_query: false`, no `builder_state` (null)
    - Assert: 201, `query` is null or placeholder, `builderState` is null (backward compat — no generator run)
  - [x] **Test 7: `Post_BuilderMode_InvalidBuilderState_Returns422`**:
    - POST /api/datasets with `is_custom_query: false`, `builder_state: <no left node>`
    - Assert: 422 with `code: "BUILDER_STATE_INVALID"`

---

### Task 10: i18n — Add save-related strings

- [x] Modify `web/src/lib/i18n/locales/en.json` (MODIFY)
  - [x] Under the `datasets.builder` object (after `orderByPanel`), add:
    ```json
    "saveButton": "Save",
    "savingButton": "Saving…",
    "saveSuccess": "Dataset saved."
    ```
  - [x] Verify `datasets.builderStateInvalid` already exists (added in Story 11.1 — it does; do NOT add again).

---

### Task 11: Verify — Backend build + all tests pass

- [x] `dotnet build src/FormForge.Api` → 0 warnings / 0 errors
- [x] `dotnet test src/FormForge.Api.Tests` → all new tests pass; pre-existing 2 audit 405 failures remain (don't re-investigate)
- [x] `npm run test` → frontend tests pass including all new `parseBuilderState` normalization tests. Baseline was 356; new tests add ~15 normalization tests.
- [x] `npm run check` → TypeScript type-check passes; no new type errors

---

### Review Findings

- [x] [Review][Patch] Empty node IDs not dropped — nodes with missing/non-string `id` normalize to `id: ''`; two such nodes cause React key deduplication and silent canvas corruption [web/src/features/datasets/types/builderState.ts]
- [x] [Review][Patch] Edges with empty source/target not dropped — missing/non-string `source`/`target` normalize to `''`; orphan edge objects persist silently in saved state [web/src/features/datasets/types/builderState.ts]
- [x] [Review][Patch] Filter value array not element-type-checked — `it.value as string[]` cast skips per-element validation; non-string elements (numbers, nulls) pass through and can cause runtime errors in UI code calling string methods [web/src/features/datasets/types/builderState.ts]
- [x] [Review][Patch] Audit log may record null query for builder-mode creates — if `CreateAsync` audit capture still uses `request.Query` (null for builder-mode creates) instead of `persistedQuery`, the audit trail is incorrect for all builder-mode dataset creations [src/FormForge.Api/Features/Datasets/DatasetService.cs]
- [x] [Review][Patch] PostAsync test helper disposes HttpRequestMessage before caller reads response — `using var request` inside `PostAsync` disposes the request upon method exit; can cause `ObjectDisposedException` on response content reads in some HttpClient configurations [src/FormForge.Api.Tests/Features/Datasets/DatasetBuilderModeTests.cs]
- [x] [Review][Defer] `_filterIdSeq` module-level variable resets on HMR in dev mode [web/src/components/query-builder/FilterConditionsDialog.tsx] — deferred, pre-existing architecture; fixing requires significant refactor to React context
- [x] [Review][Defer] `seedFilterIdCounter` side-effect during React render phase [web/src/features/datasets/QueryBuilderCanvas.tsx] — deferred, explicitly prescribed by story spec Task 4 ("synchronous, not a useEffect")
- [x] [Review][Defer] Blank-operator filter conditions silently omitted from SQL without pre-flight error [src/FormForge.Api/Features/Datasets/DatasetSqlGenerator.cs] — deferred, pre-existing `RenderConditionView` behavior; operator validation not in this story's scope
- [x] [Review][Defer] `useRef` counter seeding not re-seeded when `initialState` prop changes after mount [web/src/features/datasets/QueryBuilderCanvas.tsx] — deferred, by-design `useRef` behavior; page never passes new `initialState` without full remount
- [x] [Review][Defer] `currentVersion` state stale under multi-tab concurrent edits [web/src/routes/_app/admin/datasets_.$id.tsx] — deferred, handled by 409 DATASET_CONCURRENCY_CONFLICT toast; expected optimistic-concurrency recovery path
- [x] [Review][Defer] `normalizeFilterGroup` could stack-overflow on pathologically deep filter trees [web/src/features/datasets/types/builderState.ts] — deferred, JSON.parse prevents circular refs; practical filter tree depth is bounded by the UI

## Dev Notes

### 1. What Story 11.1 Already Did — Do NOT Redo

- `DatasetService.UpdateAsync` — already runs the generator in checkpoint (b), already persists `builder_state` in the UPDATE SQL. The `builder_state` column was there from Story 8.1. The UPDATE already writes `builder_state = @builderState::jsonb`.
- `BuilderStateDto.cs` — already mirrors `builderState.ts`. The C# DTO hierarchy exists in `src/FormForge.Api/Features/Datasets/Dtos/BuilderStateDto.cs`.
- `DatasetSqlGenerator.cs` — the 10-step algorithm, `ExpressionSecurityValidator.cs`, the pre-flight gates (left table, columns, aliases) are all there.
- `datasets_.$id.tsx` — already fetches the dataset via the route loader (`getDataset(params.id)`), already parses `dataset.builderState` via `parseBuilderState`, already passes it as `initialState` to `QueryBuilderCanvas`. The only missing piece is the **Save button** and **version tracking**.
- `parseBuilderState` — already handles null/corrupt top-level cases. Task 2 adds field-by-field normalization for the fields that were deferred.

### 2. The `currentVersion` Local State Pattern

`datasets_.$id.tsx` uses a TanStack Router loader (not a TanStack Query hook) to fetch the initial dataset. After a successful Save, the response includes the new version number. We must update local state so the next Save does not get a 409 Concurrency Conflict. Use `useState(dataset.version)` initialized from the loader data, and update it in `onSuccess` from the mutation response's `.version` field.

Do NOT try to re-run the loader on success — that causes a full page reload and loses canvas state. The `invalidateQueries` calls ensure the list page reflects the update, but the current builder page keeps its in-memory canvas state.

### 3. `parseBuilderState` Design Contract

`parseBuilderState` is a **pure function** — it never throws. Every missing or malformed field falls back to a safe default. This is critical because a persisted `builder_state` from an older schema version (pre-Epic 10 fields) should still produce a usable (empty) canvas rather than crashing.

The `normalizeFilterGroup` helper is recursive. The depth of recursion is bounded by the JSON structure which is bounded by PostgreSQL's JSONB storage and `System.Text.Json`'s `MaxDepth` default on write. In practice, filter trees are shallow (2–3 levels).

### 4. Counter Seeding — Why It Matters

Without seeding, after a round-trip (save + reload), the counters reset to 0. The first "Add Case Column" on a node that already had `case-n1-1` would generate `case-n1-1` again. When React renders two array items with the same `key`, it silently deduplicates them and corrupts state. The `maxSuffixFromIds` helper extracts the highest `{prefix}-{number}` suffix, so new IDs always exceed the restored ones.

The `_filterIdSeq` module-level counter in `FilterConditionsDialog.tsx` has the same problem. The `seedFilterIdCounter` function bumps it past the max restored ID. It is idempotent (safe to call multiple times) because it only increases `_filterIdSeq`, never decreases it.

### 5. `JoinEdgeDataDto` Already Has `SourceColumn`/`TargetColumn` in `builderState.ts`

The TypeScript contract (`JoinEdgeData`) has `{ sourceColumn, targetColumn, joinType }`. The current C# `JoinEdgeDataDto` has only `{ JoinType }`. The normalization in Task 2 already handles the round-trip correctly by mapping `edgeData.sourceColumn` in `parseBuilderState`. However, the C# DTO mismatch means that a `data.sourceColumn` sent from the frontend is silently dropped (the generator uses `edge.SourceHandle`/`TargetHandle` instead). The deferred-work item says to align the DTO. This story does NOT fix the C# DTO alignment (low risk since the generator uses handles, not data columns) — carry it forward. The normalization in `parseBuilderState` already handles the TypeScript side.

### 6. Empty-`builder_state` Enforcement Gap (Deferred from 11.1 Review)

When `UpdateAsync` processes a builder-mode save with an empty/whitespace `effectiveBuilderState`, it skips the generator AND the SELECT-only checkpoint (both guarded by non-empty state). The stale `current.Query` flows into `effectiveViewQuery` with no enforcement. This is documented as a deferred item. Story 11.2 does NOT fix this (the risk is bounded — `current.Query` was already SELECT-only-enforced on its own previous write). Carry forward to a hardening story or fold into 11.3.

### 7. Save Button Disabled-States Logic

The Save button is disabled when:
1. `builderState.nodes.length === 0` — empty canvas would fail the server's "no left table" + "no columns" checks anyway; better to be proactive
2. `getLeftTableValidationError(builderState.nodes)` — 2+ tables, none designated Left
3. `getColumnSelectionValidationError(builderState.nodes)` — 1+ tables, no columns checked
4. `getCaseColumnAliasError(builderState.caseColumns)` — a CASE column has empty alias
5. `getCalculatedColumnAliasError(builderState.calculatedColumns)` — a calculated column has empty alias
6. `saveMutation.isPending` — in-flight save

These functions are already imported in `builderState.ts` and tested. No new validation functions are needed.

### 8. `DatasetSqlGenerator.cs` — Where to Add the Blank-Condition Gate

The existing pre-flight validation block is in `Generate()`, after the `BuilderStateDto` is passed in. Look for the block that starts:
```csharp
// Step 1 — Pre-flight validation
```
Add the blank-condition check to the SAME errors list before the early return. The existing pattern is:
```csharp
if (state.Nodes.Count(n => n.Data.Side == "left") != 1)
    errors.Add("...");
// ... other checks ...
if (errors.Count > 0)
    return SqlGenerationResult.WithErrors(errors);
```
Add the `HasBlankFilterCondition` check and its `errors.Add(...)` call BEFORE the `if (errors.Count > 0) return ...` early exit.

### 9. `CreateAsync` INSERT SQL — Verify Existing Structure

The existing INSERT SQL in `CreateAsync` (at line ~142 of `DatasetService.cs`) hard-codes `NULL` for `builder_state`:
```sql
(id, dataset_name, is_custom_query, query, builder_state, version, created_at, created_by)
VALUES (@id, @datasetName, @isCustomQuery, @query, NULL, 1, @now, @createdBy)
```
Task 6 replaces `NULL` with `@builderState::jsonb` and adds the corresponding parameter to the anonymous object. This is a minimal, targeted change.

### 10. Frontend TypeScript Compilation Considerations

- The `normalizeFilterGroup` helper uses `FilterCondition | FilterGroup` as return item types. These are already defined in `builderState.ts`.
- The `flatMap` pattern returns `T[]` from each item — `[]` for invalid/unknown items, `[normalized]` for valid ones. This avoids `filter(Boolean)` calls and keeps the type honest.
- The `CASE_OPERATORS as string[]` cast is needed because TypeScript's `includes` is strict about the array element type vs. the tested value type. This cast is safe since we're only testing membership.
- For `edgeData.sourceColumn` in the `parseBuilderState` normalization — the `JoinEdgeData` type has `sourceColumn: string` and `targetColumn: string`, so accessing them is type-safe after the cast.

### 11. Test Baseline

- Backend: Before this story, all dataset tests pass except the pre-existing 2 audit 405 failures. New tests: 5 unit tests for blank-condition gate + 3 integration tests for builder-mode create = 8 new tests.
- Frontend: 356 tests before this story. New tests: ~15 `parseBuilderState` normalization tests in the existing `builderState.test.ts`.
- `dotnet build` with `<TreatWarningsAsErrors>true` — the new `HasBlankFilterCondition` static method is `private static`, not registered via DI, so no CA1812 suppression needed.

### Project Structure Notes

**Modified files:**
- `web/src/routes/_app/admin/datasets_.$id.tsx` — Add Save button, version state, save mutation
- `web/src/features/datasets/types/builderState.ts` — Rewrite `parseBuilderState` with normalization; add `normalizeFilterGroup` private helper
- `web/src/features/datasets/QueryBuilderCanvas.tsx` — Seed `caseIdCounterRef` and `calcIdCounterRef` from `initialState`; import and call `seedFilterIdCounter`
- `web/src/components/query-builder/FilterConditionsDialog.tsx` — Export `seedFilterIdCounter` function
- `web/src/lib/i18n/locales/en.json` — Add `datasets.builder.saveButton`, `savingButton`, `saveSuccess`
- `web/src/features/datasets/datasetApi.ts` — Add `builderState?: string | null` to `CreateDatasetPayload`
- `src/FormForge.Api/Features/Datasets/Dtos/CreateDatasetRequest.cs` — Add `BuilderState?: string?`
- `src/FormForge.Api/Features/Datasets/DatasetService.cs` — Add `BuilderStateInvalid` to `CreateDatasetOutcome`; builder-mode generator block in `CreateAsync`; update INSERT SQL
- `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs` — Add `BuilderStateInvalid` case to POST handler
- `src/FormForge.Api/Features/Datasets/DatasetSqlGenerator.cs` — Add blank-condition pre-flight gate + `HasBlankFilterCondition` helper
- `web/src/features/datasets/types/__tests__/builderState.test.ts` — Add normalization tests
- `src/FormForge.Api.Tests/Features/Datasets/DatasetSqlGeneratorTests.cs` — Add blank-condition gate tests
- `src/FormForge.Api.Tests/Features/Datasets/DatasetBuilderModeTests.cs` — Add create-mode tests

**New files:** None

### References

- Epics: `_bmad-output/planning-artifacts/epics.md` §Story 11.2 (line 2864)
- Architecture: `_bmad-output/planning-artifacts/architecture.md` §6.11 (AR-67 builder_state contract), §6.12 (React Flow integration), §6.13 (TanStack Query Keys)
- Previous story dev notes: `_bmad-output/implementation-artifacts/11-1-server-authoritative-sql-generation.md` — Dev Notes §10 (deferred items owned by Story 11.2), §4 (FilterItemDto discriminated union), §2 (what already exists)
- Deferred work items relevant to this story: `_bmad-output/implementation-artifacts/deferred-work.md` — lines 8–11, 16, 28–32, 36–37, 42–46, 78 (counter seeding), 80 (parseBuilderState normalization)
- `web/src/features/datasets/types/builderState.ts` — `BuilderState`, `EMPTY_BUILDER_STATE`, `parseBuilderState`, validation functions (all SSOT)
- `web/src/routes/_app/admin/datasets_.$id.tsx` — current builder page (Save TODO comment on line 37)
- `web/src/features/datasets/QueryBuilderCanvas.tsx` — `caseIdCounterRef`, `calcIdCounterRef` declarations
- `web/src/components/query-builder/FilterConditionsDialog.tsx` — `_filterIdSeq`, `nextFilterId` (lines 22–25)
- `src/FormForge.Api/Features/Datasets/DatasetService.cs` — `CreateAsync` (INSERT at line ~142), `CreateDatasetOutcome` enum (line 18)
- `src/FormForge.Api/Features/Datasets/DatasetSqlGenerator.cs` — Step 1 pre-flight block
- Memory note: `@xyflow/react` v12 typing — `NodeData extends Record<string, unknown>` constraint; use `type` not `interface` for node/edge data shapes. Already respected in `builderState.ts` (all data shapes use `type`). No new nodes or edges created in this story.
- Memory note: pgsqlparser — no new usages in this story

## Dev Agent Record

### Agent Model Used

claude-opus-4-8[1m] (Opus 4.8, 1M context)

### Debug Log References

- Backend build: `dotnet build src/FormForge.Api` → 0 warnings / 0 errors (TreatWarningsAsErrors).
- Backend unit tests: `dotnet test --filter DatasetSqlGeneratorTests` → 38/38 pass (33 existing + 5 new blank-condition gate tests).
- Backend integration tests: `dotnet test --filter DatasetBuilderModeTests` → 7/7 pass (4 existing + 3 new create-mode tests). Required Docker Desktop (Testcontainers/PostgresFixture) to be running.
- Backend full Datasets feature: `--filter Features.Datasets` → 222/222 pass (no CreateAsync/placeholder/audit regressions).
- Backend full suite: 998 passed, 2 failed — the failures are the pre-existing audit DELETE→405 tests (SchemaAuditLog/MutationAuditLog `…DeleteVerb_Returns405`), unrelated to this story (documented as failing on a clean tree).
- Frontend tests: `npm run test` → 371 passed (356 baseline + 15 new `parseBuilderState normalization` tests).
- Type-check: `tsc -b --noEmit` → exit 0 (no type errors).
- i18n: `npm run lint:i18n` → exit 0.

### Completion Notes List

- **AC-1 / AC-4 (backend):** `UpdateAsync` already persisted `builder_state` + regenerated SQL (Story 11.1). This story wired the equivalent **checkpoint (b)** into `CreateAsync`: when a builder-mode create supplies a `builder_state` blob, the server deserializes it, runs `DatasetSqlGenerator`, and persists the generated SQL into `custom_dataset.query` and the raw state into `builder_state` in the same transaction. A new `BuilderStateInvalid` outcome maps to 422 `BUILDER_STATE_INVALID` (mirrors PUT).
- **Deviation from the literal Task 6 DTO snippet (documented):** the story snippet sets `Query` from `effectiveQuery` unconditionally, but `effectiveQuery` is never blank (defaults to `"SELECT 1 AS placeholder"`), which would change the placeholder-create contract and regress `DatasetViewLifecycleTests.Create_NoQuery_…` (asserts `Query == null`). Introduced a `builderRegenerated` flag + `persistedQuery` so the generated SQL is only persisted/returned when builder regeneration actually ran; placeholder-only creates still store/return `query = NULL` (Story 8.4 AC-2 preserved). Test 6 (`…NullBuilderState…`) asserts `query == null`, consistent with this.
- **AC-2 (frontend):** wired the Save button + `currentVersion` local-state tracking in `datasets_.$id.tsx` (loader-based fetch, so version is held in state and bumped from the mutation response — no page reload, canvas state preserved per Dev Notes §2). Save is gated by the SSOT validation helpers (empty canvas, no left table, no columns, empty CASE/calc alias). 422/409 errors surface as toasts.
- **AC-2/AC-3 (frontend):** rewrote `parseBuilderState` as a pure, never-throwing field-by-field normalizer with a private recursive `normalizeFilterGroup` helper. Every missing/malformed field falls back to a safe default; malformed array entries are dropped via `flatMap → []`.
- **Counter seeding (Dev Notes §4):** `caseIdCounterRef`/`calcIdCounterRef` now seed from the max `{prefix}-{n}` suffix in `initialState` via `maxSuffixFromIds`; `_filterIdSeq` is seeded once-per-mount via the new exported `seedFilterIdCounter`. Prevents duplicate React keys (silent state corruption) after a save+reload round-trip.
- **AC-4 (backend gate):** added a fifth pre-flight check in `DatasetSqlGenerator.Generate` — `HasBlankFilterCondition` recurses the filter tree and rejects any condition with a blank table/column name (which would otherwise be silently skipped during WHERE rendering).
- **Carried forward (not in scope, per Dev Notes §5/§6):** C# `JoinEdgeDataDto` still lacks `SourceColumn`/`TargetColumn` (generator uses handles); the empty-`builder_state` UpdateAsync enforcement gap. Both documented as deferred.
- **Lint:** `npm run lint` is already red on the baseline (89 pre-existing errors — pervasive `react-hooks/refs` ref-mirror pattern across the Query Builder + `react-refresh/only-export-components` on every TanStack route file). Lint is not in the story's Task 11 verification gate. The render-time `_filterSeedRef` access is the same rule class as the file's ~20 existing occurrences and matches the story's explicit Task 4 prescription (synchronous, not a `useEffect`).

### File List

**Modified:**
- `web/src/routes/_app/admin/datasets_.$id.tsx` — Save button, `currentVersion` state, save mutation (422/409 handling), validation gate; removed Save TODO.
- `web/src/features/datasets/types/builderState.ts` — rewrote `parseBuilderState` with field-by-field normalization; added private `normalizeFilterGroup`.
- `web/src/features/datasets/QueryBuilderCanvas.tsx` — `maxSuffixFromIds` helper; seeded `caseIdCounterRef`/`calcIdCounterRef`; imported + called `seedFilterIdCounter` (once-per-mount, ref-gated).
- `web/src/components/query-builder/FilterConditionsDialog.tsx` — exported `seedFilterIdCounter`.
- `web/src/features/datasets/datasetApi.ts` — added `builderState?: string | null` to `CreateDatasetPayload`.
- `web/src/lib/i18n/locales/en.json` — added `datasets.builder.saveButton` / `savingButton` / `saveSuccess`.
- `web/src/features/datasets/types/__tests__/builderState.test.ts` — added `parseBuilderState normalization` describe block (15 tests).
- `src/FormForge.Api/Features/Datasets/Dtos/CreateDatasetRequest.cs` — added `string? BuilderState`.
- `src/FormForge.Api/Features/Datasets/DatasetService.cs` — `BuilderStateInvalid` outcome; CreateAsync checkpoint (b) generator block + `builderRegenerated`/`persistedQuery`; INSERT persists `builder_state`; success DTO returns generated query + state.
- `src/FormForge.Api/Features/Datasets/DatasetEndpoints.cs` — POST handler `BuilderStateInvalid` → 422 case.
- `src/FormForge.Api/Features/Datasets/DatasetSqlGenerator.cs` — blank filter-condition pre-flight gate + `HasBlankFilterCondition` helper.
- `src/FormForge.Api.Tests/Features/Datasets/DatasetSqlGeneratorTests.cs` — 5 blank-condition gate tests.
- `src/FormForge.Api.Tests/Features/Datasets/DatasetBuilderModeTests.cs` — 3 builder-mode create tests + `PostAsync` helper.

**New files:** None

## Change Log

| Date | Change |
| ---- | ------ |
| 2026-06-04 | Story 11.2 implemented: Save button + version tracking, `parseBuilderState` normalization, canvas id-counter seeding, builder-mode `CreateAsync` wiring, blank filter-condition pre-flight gate, i18n save strings. 23 new tests (15 FE normalization, 5 BE gate, 3 BE integration). Status → review. |
