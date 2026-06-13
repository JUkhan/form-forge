import { useCallback, useEffect, useMemo, useRef, useState, type MutableRefObject } from 'react'
import { useQuery } from '@tanstack/react-query'
import { toast } from 'sonner'
import { designerApi } from '@/features/designer/designerApi'
import { filesApi } from '@/features/designer/filesApi'
import { Skeleton } from '@/components/ui/skeleton'
import { getFieldKey, type ComponentSchemaDto, type DesignerElement } from '@/types/designer'
import ElementRenderer, { type InteractiveFormProps } from './ElementRenderer'
import { isDesignerElementShape } from './schemaShape'
import { EMPTY_VALIDATION, validateForm, type ValidationResult } from './validation'
import { computeVisibility, EMPTY_VISIBILITY, type VisibilityState } from './visibility'

// Collect the bindTo keys of every File element in the schema tree (excluding
// Repeater subtrees — file uploads inside repeater rows are not yet supported).
function collectFileBindTos(el: DesignerElement, out: string[] = []): string[] {
  if (el.type === 'File') {
    const k = getFieldKey(el.properties)
    if (k !== '') out.push(k)
  }
  // Skip Repeater (row scope) and DatasetComponent (its children are view-local filter
  // inputs, not record fields) subtrees.
  if (el.type !== 'Repeater' && el.type !== 'DatasetComponent') {
    for (const child of el.children ?? []) collectFileBindTos(child, out)
  }
  return out
}

// Collect the bindTo keys of every Text Input / TextArea leaf so their submitted
// values can be whitespace-trimmed at payload-build time. Mirrors extractBindToKeys'
// subtree exclusions: Repeater row templates and DatasetComponent filter inputs bind
// to their own scope (not this outer form), so they're skipped — a Repeater row's own
// fields are trimmed by the nested DynamicComponent that renders that row.
function collectTextBindTos(el: DesignerElement, out: Set<string> = new Set()): Set<string> {
  if (el.type === 'Text Input' || el.type === 'TextArea') {
    const k = getFieldKey(el.properties)
    if (k !== '') out.add(k)
  }
  if (el.type !== 'Repeater' && el.type !== 'DatasetComponent') {
    for (const child of el.children ?? []) collectTextBindTos(child, out)
  }
  return out
}

interface DynamicComponentProps {
  designerId: string
  version?: number
  // When supplied, the renderer skips its internal authenticated schema fetch
  // and uses this prop directly. Required for read-only preview hosts that
  // already have the schema in hand and don't want a duplicate authenticated
  // GET firing inside the renderer.
  schema?: ComponentSchemaDto
  initialData?: Record<string, unknown>
  onSave?: (data: Record<string, unknown>) => void
  fallbackUI?: React.ReactNode
  // Called whenever form-level validity toggles. Sourced from the same useMemo
  // over validateForm() that drives the inline error UI, so it cannot drift
  // from the renderer's notion of validity.
  onValidityChange?: (isValid: boolean) => void
  // Called when the schema-load state toggles. `true` once the schema has
  // fetched AND parsed successfully (parsedRoot non-null + not loading + not
  // errored). Lets a host gate Save against a no-op handlePrimaryButtonClick
  // while the row schema is still in flight.
  onReadyChange?: (isReady: boolean) => void
  // The component assigns submitRef.current to a closure that runs the same
  // validate-then-onSave path the in-form primary Button uses. Lets a host's
  // external Save button fire submission without requiring a Button leaf
  // inside the embedded schema.
  submitRef?: MutableRefObject<(() => void) | null>
  // Story 6.11 AC-5: server-side validation errors per fieldKey. Merged into
  // the client-validator's errorByBindTo so the existing inline error UI
  // displays both client and server failures without extra JSX. Client errors
  // win when both sides flag the same field.
  serverErrors?: Record<string, string>
  // When provided (UPDATE flow), used as the recordId segment in file object
  // keys: {designerId}/{fieldKey}_{recordId}.{ext}. Absent for CREATE — the
  // backend generates a UUID for the key segment instead.
  recordId?: string
  // SingleRecord host: an extra fieldKey to hide (and exclude from the submitted
  // payload), beyond the version's configured authFilterFieldKey. The SingleRecord
  // component's authFilterColumn names the owning-user column, which the backend stamps
  // server-side — the user must never see or edit it, even when the version itself has
  // no authFilterFieldKey configured.
  hiddenFieldKey?: string
}

function extractBindToKeys(el: DesignerElement): string[] {
  // A Repeater's row data is submitted via the `children` map (keyed by
  // rowDesignerId), not as a flat parent column — its own fieldKey names the
  // one-to-many relation, not a column. Skip it AND its row-template children
  // (those bind to row-level keys on the child form, not this outer form).
  // A DatasetComponent's filter inputs bind to its own view-local state, not the outer
  // form — exclude the subtree exactly like a Repeater's row-template children.
  if (el.type === 'Repeater' || el.type === 'DatasetComponent') return []
  const keys: string[] = []
  // Use the canonical field key (fieldKey, with legacy bindTo fallback). The
  // function and local variable retain the "bindTo" name to keep the diff
  // narrow — the meaning is unchanged: this is the form-state binding key.
  const bindTo = getFieldKey(el.properties)
  if (bindTo !== '') {
    keys.push(bindTo)
  }
  for (const child of el.children ?? []) {
    keys.push(...extractBindToKeys(child))
  }
  return keys
}

interface RepeaterRelation {
  // Where the row array lives in formData (the Repeater's own fieldKey).
  fieldKey: string
  // The child designer the backend keys the `children` payload / response by.
  rowDesignerId: string
}

// Each Repeater is a one-to-many relation. The designer stores rows in
// formData[fieldKey], but the data API addresses the collection by
// rowDesignerId (the child table identity) — so we translate between the two on
// read (seed) and write (submit). Row-template children are NOT traversed: they
// belong to the child form, not this one.
function collectRepeaterRelations(
  el: DesignerElement,
  out: RepeaterRelation[] = [],
): RepeaterRelation[] {
  if (el.type === 'Repeater') {
    const fieldKey = getFieldKey(el.properties)
    const rowDesignerId =
      typeof el.properties.rowDesignerId === 'string' ? el.properties.rowDesignerId.trim() : ''
    if (fieldKey !== '' && rowDesignerId !== '') {
      out.push({ fieldKey, rowDesignerId })
    }
    return out
  }
  // DatasetComponent subtree is view-local — no repeater relations live inside it.
  if (el.type === 'DatasetComponent') return out
  for (const child of el.children ?? []) collectRepeaterRelations(child, out)
  return out
}

// Shallow-equal two initialData props by content rather than reference. A
// reference check ate user input when a host passed `initialData={{}}` as an
// inline literal: every parent re-render allocated a fresh `{}`, which
// triggered the setState-during-render reset block below and wiped formData.
// `undefined`/`null` are treated as `{}`, matching how the formData seed line
// already spreads `initialData ?? {}`.
function shallowEqualInitialData(
  a: Record<string, unknown> | undefined,
  b: Record<string, unknown> | undefined,
): boolean {
  if (a === b) return true
  const aObj = a ?? {}
  const bObj = b ?? {}
  const aKeys = Object.keys(aObj)
  const bKeys = Object.keys(bObj)
  if (aKeys.length !== bKeys.length) return false
  for (const k of aKeys) {
    if (!Object.prototype.hasOwnProperty.call(bObj, k)) return false
    if (aObj[k] !== bObj[k]) return false
  }
  return true
}

// `<input type="color">` only accepts 6-digit hex; anything else (named CSS
// color, rgb(), 3-digit hex) silently displays as #000000 while formData
// still holds the bad string, so submit ships the broken value. Normalize at
// the seam.
const HEX_COLOR = /^#[0-9a-f]{6}$/i

function normalizeHex(raw: unknown): string {
  return typeof raw === 'string' && HEX_COLOR.test(raw) ? raw : '#000000'
}

function extractDefaults(el: DesignerElement): Record<string, unknown> {
  const defaults: Record<string, unknown> = {}
  const p = el.properties
  const bindTo = getFieldKey(p)
  if (bindTo !== '') {
    if (el.type === 'Checkbox') {
      defaults[bindTo] = p?.defaultChecked === true || p?.defaultChecked === 'true'
    } else if (el.type === 'Color Picker' && p?.defaultColor !== undefined) {
      defaults[bindTo] = normalizeHex(p.defaultColor)
    }
  }
  // Same rationale as extractBindToKeys: row template defaults live in row
  // scope, not in the outer formData. DatasetComponent filter inputs are likewise view-local.
  if (el.type === 'Repeater' || el.type === 'DatasetComponent') return defaults
  for (const child of el.children ?? []) {
    Object.assign(defaults, extractDefaults(child))
  }
  return defaults
}

// Collects all Color Picker bindTos so the formData seed can normalize host-
// provided initialData values that aren't 6-digit hex.
function collectColorBindTos(el: DesignerElement, out: Set<string>): void {
  if (el.type === 'Color Picker') {
    const b = getFieldKey(el.properties)
    if (b !== '') out.add(b)
  }
  if (el.type === 'Repeater' || el.type === 'DatasetComponent') return
  for (const child of el.children ?? []) collectColorBindTos(child, out)
}

function seedFormData(
  root: DesignerElement | null,
  initialData: Record<string, unknown> | undefined,
): Record<string, unknown> {
  const seeded = {
    ...(root ? extractDefaults(root) : {}),
    ...(initialData ?? {}),
  }
  if (root) {
    const colorBindTos = new Set<string>()
    collectColorBindTos(root, colorBindTos)
    for (const k of colorBindTos) {
      if (k in seeded) seeded[k] = normalizeHex(seeded[k])
    }

    // Distribute loaded child rows (initialData.children, keyed by rowDesignerId)
    // into each Repeater's formData slot (keyed by its fieldKey) so the repeater
    // renders existing rows. Default to [] so a fresh form still holds an array
    // and a save without edits prunes nothing (the submitted array == reality).
    const childrenBlob =
      initialData && typeof initialData.children === 'object' && initialData.children !== null
        ? (initialData.children as Record<string, unknown>)
        : null
    for (const rel of collectRepeaterRelations(root)) {
      const loaded = childrenBlob ? childrenBlob[rel.rowDesignerId] : undefined
      if (Array.isArray(loaded)) {
        seeded[rel.fieldKey] = loaded
      } else if (!Array.isArray(seeded[rel.fieldKey])) {
        seeded[rel.fieldKey] = []
      }
    }
    // `children` is a transport wrapper from the GET response, not a form field.
    delete seeded.children
  }
  return seeded
}

export default function DynamicComponent({
  designerId,
  version,
  schema: providedSchema,
  initialData,
  onSave,
  fallbackUI,
  onValidityChange,
  onReadyChange,
  submitRef,
  serverErrors,
  recordId,
  hiddenFieldKey,
}: DynamicComponentProps) {
  // Keep a stable ref to initialData so handlePrimaryButtonClick can read the
  // original file keys (before the user swapped them) without being in its dep
  // array (initialData is a frequently-recreated object literal at call sites).
  const initialDataRef = useRef(initialData)
  useEffect(() => { initialDataRef.current = initialData }, [initialData])
  const { data: fetchedSchema, isLoading: fetchedIsLoading, isError: fetchedIsError } = useQuery({
    queryKey: ['component-schema', designerId, version ?? 'latest'],
    queryFn: () => designerApi.getSchema(designerId, version),
    staleTime: 300_000,
    enabled: !!designerId && !providedSchema,
  })
  const schema = providedSchema ?? fetchedSchema
  const isLoading = !providedSchema && fetchedIsLoading
  const isError = !providedSchema && fetchedIsError

  // FormForge's ComponentSchemaDto carries rootElement as a parsed object (not
  // a JSON string). Still gate on the runtime shape probe so a backend bug or
  // malformed payload can't crash the renderer — we'd rather show the
  // fallbackUI than a stack trace.
  const parsedRoot = useMemo<DesignerElement | null>(() => {
    const v = schema?.rootElement
    if (!v) return null
    return isDesignerElementShape(v) ? v : null
  }, [schema])

  // Reset formData when the schema identity (designerId/version), the parsed
  // schema tree, or initialData content changes. We seed formData with each
  // leaf's defaults (extractDefaults) and let initialData spread on top so
  // user-supplied values override schema defaults. Tracking parsedRoot
  // identity is necessary because the schema arrives async via TanStack
  // Query — without it, formData would stay {} after the schema lands and a
  // required Checkbox with `defaultChecked` would render checked but validate
  // as missing.
  const cacheKey = `${designerId}::${version ?? 'latest'}`
  const [trackedKey, setTrackedKey] = useState(cacheKey)
  const [trackedInitialData, setTrackedInitialData] = useState(initialData)
  const [trackedRoot, setTrackedRoot] = useState<DesignerElement | null>(parsedRoot)
  const [formData, setFormData] = useState<Record<string, unknown>>(() =>
    seedFormData(parsedRoot, initialData),
  )

  if (
    trackedKey !== cacheKey ||
    !shallowEqualInitialData(trackedInitialData, initialData) ||
    trackedRoot !== parsedRoot
  ) {
    setTrackedKey(cacheKey)
    setTrackedInitialData(initialData)
    setTrackedRoot(parsedRoot)
    setFormData(seedFormData(parsedRoot, initialData))
  }

  const handleChange = useCallback((key: string, value: unknown) => {
    setFormData((prev) => ({ ...prev, [key]: value }))
  }, [])

  // Visibility is recomputed whenever formData or the parsed tree changes —
  // the fixed-point loop in computeVisibility is O(elements²) worst-case but
  // small schemas keep this trivially cheap. The result feeds both the
  // validator (skip hidden subtrees) and the renderer (short-circuit hidden
  // elements), so a single source of truth keeps "what the user can see"
  // aligned with "what the form submits" and "what gets validated".
  const visibility = useMemo<VisibilityState>(
    () => (parsedRoot ? computeVisibility(parsedRoot, formData) : EMPTY_VISIBILITY),
    [parsedRoot, formData],
  )

  // Auth filter field — when the version configures an authFilterFieldKey, hide
  // the element that binds it. The backend owns this column (it stamps the owning
  // user on create and scopes the list to it), so the user must never see or edit
  // it. Folding it into the visibility sets means it is excluded from rendering,
  // validation (a hidden required field can't block submit), AND the submitted
  // payload — all through the existing hidden-field plumbing.
  const authFilterFieldKey = schema?.authFilterFieldKey ?? ''
  const effectiveVisibility = useMemo<VisibilityState>(() => {
    // Hide both the version's configured auth field AND the SingleRecord host's
    // owning-user column (hiddenFieldKey). Both are backend-owned and must never be
    // rendered, validated, or submitted.
    const keysToHide = new Set<string>()
    if (authFilterFieldKey !== '') keysToHide.add(authFilterFieldKey)
    if (hiddenFieldKey && hiddenFieldKey !== '') keysToHide.add(hiddenFieldKey)
    if (!parsedRoot || keysToHide.size === 0) return visibility
    const targetIds: string[] = []
    const walk = (el: DesignerElement): void => {
      // Auth field is a top-level column — Repeater / DatasetComponent / SingleRecord
      // subtrees are not columns of this table, so skip them (mirrors the backend).
      if (el.type === 'Repeater' || el.type === 'DatasetComponent' || el.type === 'SingleRecord') return
      const fk = getFieldKey(el.properties)
      if (fk !== '' && keysToHide.has(fk)) targetIds.push(el.id)
      for (const child of el.children ?? []) walk(child)
    }
    walk(parsedRoot)
    if (targetIds.length === 0) return visibility
    const hiddenElementIds = new Set(visibility.hiddenElementIds)
    for (const tid of targetIds) hiddenElementIds.add(tid)
    const hiddenBindTos = new Set(visibility.hiddenBindTos)
    for (const k of keysToHide) hiddenBindTos.add(k)
    return { hiddenElementIds, hiddenBindTos }
  }, [visibility, parsedRoot, authFilterFieldKey, hiddenFieldKey])

  const validationResult = useMemo<ValidationResult>(
    () => (parsedRoot ? validateForm(parsedRoot, formData, effectiveVisibility.hiddenElementIds) : EMPTY_VALIDATION),
    [parsedRoot, formData, effectiveVisibility.hiddenElementIds],
  )

  // Server errors merged with client validation. Client errors take priority;
  // server errors display for fields that pass client validation but fail
  // server-side business rules. Re-merges whenever validationResult or
  // serverErrors changes — the returned Map identity stays stable when neither
  // input has changed so interactiveProps below is not invalidated needlessly.
  const mergedErrorByBindTo = useMemo(() => {
    if (!serverErrors || Object.keys(serverErrors).length === 0) {
      return validationResult.errorByBindTo
    }
    const merged = new Map(validationResult.errorByBindTo)
    for (const [k, v] of Object.entries(serverErrors)) {
      if (!merged.has(k)) merged.set(k, v)
    }
    return merged
  }, [validationResult.errorByBindTo, serverErrors])

  const handlePrimaryButtonClick = useCallback(async () => {
    if (!onSave || !parsedRoot) return
    if (!validationResult.isValid) return

    // ── 1. Upload any pending File objects ───────────────────────────────────
    const fileBindTos = collectFileBindTos(parsedRoot)
    const resolvedFileKeys: Record<string, string> = {}
    for (const bindTo of fileBindTos) {
      if (effectiveVisibility.hiddenBindTos.has(bindTo)) continue
      const val = formData[bindTo]
      if (!(val instanceof File)) continue

      // The value stored in initialData for this field is the old object key
      // (if the record already had a file). Captured via ref to avoid stale closure.
      const oldKey =
        typeof initialDataRef.current?.[bindTo] === 'string'
          ? (initialDataRef.current[bindTo] as string)
          : null

      try {
        const result = await filesApi.uploadFile({ file: val, designerId, fieldKey: bindTo, recordId })
        resolvedFileKeys[bindTo] = result.objectKey
        // Delete the replaced file when the key actually changed (e.g. extension differed).
        if (oldKey && oldKey !== result.objectKey) {
          filesApi.deleteFile(oldKey).catch(() => {
            // Non-fatal — orphaned objects don't affect data integrity.
          })
        }
      } catch {
        toast.error(`File upload failed for '${bindTo}'. Please try again.`)
        return
      }
    }

    // ── 2. Build the payload (file fields resolved to object keys) ───────────
    const boundKeys = extractBindToKeys(parsedRoot)
    const defaults = extractDefaults(parsedRoot)
    // Text Input / TextArea values are trimmed of surrounding whitespace before
    // submission so stray leading/trailing spaces don't get persisted.
    const textBindTos = collectTextBindTos(parsedRoot)
    const payload: Record<string, unknown> = {}
    for (const k of boundKeys) {
      // Conditionally hidden fields are excluded from the payload — the user
      // never saw them, so any value still sitting in formData (e.g. typed
      // before the hiding condition fired) must not be persisted.
      if (effectiveVisibility.hiddenBindTos.has(k)) continue
      // Use the uploaded key for File fields; fall back to formData for everything else.
      const v = k in resolvedFileKeys ? resolvedFileKeys[k] : formData[k]
      if (v !== undefined) {
        // Trim text-field strings only; non-string values (and non-text fields)
        // pass through untouched so nothing else is coerced.
        payload[k] = textBindTos.has(k) && typeof v === 'string' ? v.trim() : v
      } else if (k in defaults) {
        payload[k] = defaults[k]
      }
    }
    // Repeater rows → `children` map keyed by rowDesignerId (the backend's child
    // table identity). Skip a conditionally-hidden Repeater so its rows are
    // neither submitted nor pruned.
    const children: Record<string, unknown[]> = {}
    for (const rel of collectRepeaterRelations(parsedRoot)) {
      if (effectiveVisibility.hiddenBindTos.has(rel.fieldKey)) continue
      const rows = formData[rel.fieldKey]
      if (Array.isArray(rows)) {
        children[rel.rowDesignerId] = rows
      }
    }
    if (Object.keys(children).length > 0) {
      payload.children = children
    }
    onSave(payload)
  }, [onSave, parsedRoot, formData, validationResult.isValid, effectiveVisibility.hiddenBindTos, designerId, recordId])

  // Surface validity to a host so it can dim/disable its own Save chrome.
  // Re-fires only when the boolean flips — `validationResult.isValid` is
  // stable identity-wise across renders that don't change validity, so this
  // effect is cheap.
  useEffect(() => {
    onValidityChange?.(validationResult.isValid)
  }, [validationResult.isValid, onValidityChange])

  // Surface schema-readiness to the host. `ready` is true only once the schema
  // has fetched and parsed successfully — empty designerId / loading / errored /
  // unparseable-schema all keep it false.
  const isReady = !!designerId && !isLoading && !isError && parsedRoot !== null
  useEffect(() => {
    onReadyChange?.(isReady)
  }, [isReady, onReadyChange])

  // Expose the submit trigger so a host's external Save button can drive the
  // same validate-then-onSave path the internal primary Button uses. Null-out
  // on unmount so a stale closure can't fire after the host has moved on.
  useEffect(() => {
    if (!submitRef) return
    submitRef.current = handlePrimaryButtonClick
    return () => {
      submitRef.current = null
    }
  }, [submitRef, handlePrimaryButtonClick])

  const interactiveProps = useMemo<InteractiveFormProps>(
    () => ({
      formData,
      onChange: handleChange,
      onPrimaryButtonClick: handlePrimaryButtonClick,
      errorByBindTo: mergedErrorByBindTo,
      formIsValid: validationResult.isValid,
      hiddenElementIds: effectiveVisibility.hiddenElementIds,
    }),
    [
      formData,
      handleChange,
      handlePrimaryButtonClick,
      mergedErrorByBindTo,
      validationResult.isValid,
      effectiveVisibility.hiddenElementIds,
    ],
  )

  if (isLoading) {
    return <Skeleton className="h-32 w-full" />
  }

  if (!designerId || isError || (schema && !parsedRoot)) {
    return (
      fallbackUI ?? (
        <div className="flex h-20 w-full items-center justify-center rounded-lg bg-muted text-sm text-muted-foreground">
          Component unavailable
        </div>
      )
    )
  }

  if (!parsedRoot) return null

  return (
    <ElementRenderer element={parsedRoot} interactive={true} interactiveProps={interactiveProps} />
  )
}
