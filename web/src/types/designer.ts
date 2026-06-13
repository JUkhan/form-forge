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
  // Optional per-component auth filter: the node-template column that owns each record
  // per user. Stamped with the current user on create (server-side) and the field is
  // hidden on the form; reads/edits/deletes are scoped so each user only sees their own.
  authFilterColumn?: string
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
