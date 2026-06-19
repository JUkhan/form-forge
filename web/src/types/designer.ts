// Shared designer types — ported from ESG `@/types/api` and pared down to
// the surface area the designer feature actually uses. Backend DTOs will be
// formalised in Story 3.2+; until then these shapes are the authoritative
// contract for the FormForge designer client.

export interface DesignerElementProperties {
  // Well-known properties (all optional — schema is open)
  label?: string
  /**
   * @deprecated Use `fieldKey` for the form-state binding AND the DB column
   * name. The legacy `bindTo` field is read as a fallback when `fieldKey` is
   * absent (older saved schemas) but the Property Inspector no longer offers
   * it as an editable input.
   */
  bindTo?: string
  fieldKey?: string
  tabName?: string
  rows?: number
  paddingPx?: number
  contentGapPx?: number
  orientation?: 'horizontal' | 'vertical'
  placeholder?: string
  required?: boolean
  // Text Input regex validation: `regexPattern` is a user-authored pattern enforced at
  // render time (HTML5 `pattern`-attribute semantics — whole-value match); `regexMessage`
  // is the error shown when it fails (falls back to a generic message when blank).
  regexPattern?: string
  regexMessage?: string
  readOnly?: boolean
  hideWhenAll?: Record<string, unknown>[]
  hideWhenAny?: Record<string, unknown>[]
  options?: string
  dependsOn?: string
  apiPath?: string
  // Story B-1-4-followup — table-column controls for the record-list view.
  // showInTable defaults to true when absent (back-compat); columnHeader overrides
  // the auto-humanized header; columnOrder sets ascending column position.
  showInTable?: boolean
  columnHeader?: string
  columnOrder?: number
  // Field element (type "Repeater Field") — reusable display field.
  // `mapExpression` is an optional JS expression that transforms the field's own
  // value before display (e.g. `name.toUpperCase()`); see features/data-entry/mapExpression.ts.
  // `isTableColumn` (default false) opts a top-level Field — one placed directly on a
  // CRUD form, NOT inside a Repeater/TreeView — into the record-list table as a
  // display-only column, reusing columnHeader/columnOrder above for its header + position.
  mapExpression?: string
  isTableColumn?: boolean
  // TreeView (extends Repeater) — a standalone self-referencing tree over the
  // selected node-template table (`rowDesignerId`). `pageSize` controls the
  // on-demand pagination per tree level. The CRUD flags gate the per-node
  // add/edit/delete action buttons (mutate mode, immediate to backend);
  // `isMultiSelect` enables checkbox multi-selection (view mode). CRUD and
  // isMultiSelect are MUTUALLY EXCLUSIVE — enabling one clears the other.
  // With no CRUD flag set the TreeView is in view/select mode and binds the
  // selected node ids (comma-separated) to `fieldKey`.
  canCreate?: boolean
  canEdit?: boolean
  canDelete?: boolean
  isMultiSelect?: boolean
  pageSize?: number
  // `viewMode` makes the tree read-only: it overrides and disables ALL mutation
  // (add/edit/delete) AND selection — users can only browse/expand/search.
  // `selectAll` (only meaningful with isMultiSelect) cascades a node's selection to
  // its entire subtree: checking a node selects all of its descendants too.
  viewMode?: boolean
  selectAll?: boolean
  // Presentation only: render the tree inside a compact dropdown (a trigger button that
  // opens a popover holding the tree) so it fits a tight space. Behaviour is unchanged.
  isDropdownUi?: boolean
  // Optional per-component auth filter: the node-template column that owns each record
  // per user. Stamped with the current user on create (server-side) and the field is
  // hidden on the form; reads/edits/deletes are scoped so each user only sees their own.
  authFilterColumn?: string
  // Dataset-backed TreeView (optional). When `optionsDatasetId` is set, the tree's
  // hierarchical levels, per-level paging and search are read from that dataset's VIEW
  // instead of the provisioned table, and the RepeaterField pickers list the dataset's
  // columns. `datasetKeyField` / `datasetParentField` name the VIEW columns that act as
  // the node id and the parent self-reference. The chosen key value is mapped back to
  // `id` so per-node open/edit/delete (on the base table) keep working.
  optionsDatasetId?: string
  datasetKeyField?: string
  datasetParentField?: string
  // Entire-tree search (opt-in). Level-wise search is the default; when
  // `enableEntireTreeSearch` is on it is replaced by a single search box that runs a
  // recursive server-side search across the WHOLE tree and reveals the ancestor path of
  // the first matching node. `treeSearchConditions` is the design-time filter that builds
  // the SEARCH_CONDITIONS predicate: a flat group of `<column> <operator>` rows joined by
  // one AND/OR, where the value side of every condition is supplied at runtime by the
  // single search box (so, unlike the DatasetComponent filter, there is no bound
  // `valueFieldKey`). Left operands are the designer's row-template fieldKeys, or the
  // dataset columns when a dataset source is configured. Persisted as a plain object;
  // parsed defensively by the inspector (see web/src/components/designer/treeSearchFilter).
  enableEntireTreeSearch?: boolean
  treeSearchConditions?: {
    combinator: 'AND' | 'OR'
    items: { id: string; columnName: string; operator: string }[]
  }
  // Allow arbitrary properties
  [key: string]: unknown
}

export interface DesignerElement {
  id: string
  type: string
  properties: DesignerElementProperties
  children?: DesignerElement[]
}

/**
 * Returns the form-state binding key for an element. Prefers the canonical
 * `fieldKey` (one identifier for both the form state key AND the DB column
 * name), falling back to legacy `bindTo` for schemas saved before the
 * consolidation. Returns `''` (empty string) when neither is set or when
 * the value is whitespace — callers should treat that as "unbound".
 */
export function getFieldKey(properties: DesignerElementProperties | undefined | null): string {
  const fk = properties?.fieldKey
  if (typeof fk === 'string' && fk.trim() !== '') return fk.trim()
  const bt = properties?.bindTo
  if (typeof bt === 'string' && bt.trim() !== '') return bt.trim()
  return ''
}

export interface ComponentSchemaVersion {
  version: number
  status: 'Draft' | 'Published' | 'Archived'
  createdAt: string
  publishedAt?: string | null
}

export interface ComponentSchemaDto {
  designerId: string
  displayName: string
  // FR-54 — component mode. CRUD provisions a table; VIEW is display-only.
  // Lives on the schema, not the version; immutable for the component's life.
  mode: 'CRUD' | 'VIEW'
  status: 'Draft' | 'Published' | 'Archived'
  latestVersion: number
  rootElement: DesignerElement | null
  createdAt: string
  updatedAt: string | null
  /**
   * Timestamp of the most recent publish for this version (the one identified
   * by `latestVersion`). Set on Publish, preserved on Archive — an Archived
   * version's `publishedAt` reflects when it WAS last published, as history.
   * Consumers that need "is currently published?" MUST filter on
   * `status === 'Published'`, never on `publishedAt !== null`.
   */
  publishedAt: string | null
  /**
   * Optional per-version auth filter field key (null when none configured for
   * the version this DTO represents). When set, the record list is scoped to
   * rows whose column equals the requesting user's id, and the matching form
   * field is hidden by DynamicComponent. Admin-managed from the Component Library.
   */
  authFilterFieldKey?: string | null
  /**
   * Optional per-version dataset binding (the CustomDataset id, null when none).
   * When set, the record list reads its rows from that dataset's backing VIEW
   * (paginated/filtered/sorted/auth-scoped the same way) instead of the
   * provisioned table. The dataset's columns follow the fieldKey convention and
   * expose the record id as `<designerId>_id`. Admin-managed from the Component Library.
   */
  datasetId?: string | null
  versions?: ComponentSchemaVersion[]
}

export interface ComponentSchemaListItem {
  designerId: string
  displayName: string
  // FR-54 — component mode (see ComponentSchemaDto.mode).
  mode: 'CRUD' | 'VIEW'
  status: 'Draft' | 'Published' | 'Archived'
  latestVersion: number
  createdAt: string
  updatedAt: string | null
  creatorDisplayName?: string | null
}
