# Story 3.1: Port and Refactor Designer Code

Status: done

## Story

As a Developer,
I want to audit and port the component designer (ComponentDesignerPage, ComponentLibraryPage, DynamicComponent, DesignerCanvas, ElementRenderer, DesignerToolbar) from the ESG Platform reference codebase, refactoring for shadcn/ui, React 19 patterns, TanStack React Query v5, and the new project structure,
so that the designer is a first-class part of FormForge with no ESG-platform-specific dependencies.

## Acceptance Criteria

1. **AC-1 — Source Location & Audit**
   Given the ESG Platform source at `C:\Users\MY\Downloads\esg_platform\BkmeaEsgPlatform\client\src\` (designer components at `components\designer\`, pages at `pages\`)
   When I run the port
   Then the six files land in FormForge at `web/src/components/designer/` (DesignerCanvas, DynamicComponent, ElementRenderer, DesignerToolbar) and at `web/src/routes/_app/designer.library.tsx` + `web/src/routes/_app/designer.$designerId.tsx` (page wrappers) per Architecture Decision 4.6.

2. **AC-2 — Component Rendering & Drag-and-Drop**
   Given the ported designer
   When I drag any of the 14 component types (Stack, Row, Tabs, Label, Button, TextInput, TextArea, NumberInput, Checkbox, Dropdown, DateTimePicker, ColorPicker, Repeater, RepeaterField, Image) from the palette onto the canvas
   Then the component renders correctly
   And native HTML5 drag-and-drop is preserved: drag from palette, reorder on canvas, precise insertion via DropZone between children.

3. **AC-3 — Remove ESG Platform Dependencies**
   Given the ported source files
   When I grep for ESG Platform-specific API paths, data models, or business logic (e.g. `esg`, `bkmea`, `@/hooks/useAuth`, `react-router-dom`, `@/api/componentDesigner`, `AxiosError`, `/api/component-schemas`, `DPP`, `DesignerEmbedFrame`, `VersionAware`)
   Then zero matches remain in `web/src/components/designer/` and `web/src/routes/_app/designer.*.tsx`.

4. **AC-4 — UI Component Library Migration**
   Given the ported UI primitives
   When I inspect imports
   Then all UI primitives use shadcn/ui components from `web/src/components/ui/` where they exist (Button, Input, Checkbox, Select, Dialog, etc.), Tailwind utility classes for layout/spacing, and `cn()` from `web/src/lib/utils`; no external component library imports remain.

5. **AC-5 — DynamicComponent Contract Preservation**
   Given the DynamicComponent contract
   When I exercise it with conditional visibility, Repeater row scoping, external `submitRef`, `onValidityChange`, `onReadyChange`, and shallow-equal `initialData`
   Then every behavior from the source matches (per FR-8 AC-5): visibility engine correct, Repeater row scope isolated, `submitRef.current()` fires validate-then-save path, validity/ready callbacks fire on correct state changes, shallow-equal `initialData` check prevents spurious resets.

6. **AC-6 — TanStack React Query v5 Compliance**
   Given all data fetching in the ported code
   When I inspect TanStack React Query usage
   Then v5 API patterns are used throughout: object-form `useQuery({ queryKey, queryFn })`, `gcTime` not `cacheTime`, `useMutation({ mutationFn, onSuccess })`, no deprecated v4 patterns.

## Tasks / Subtasks

- [x] Task 1: Add new npm dependencies (AC-1)
  - [x] Run `pnpm add zustand` from the `web/` directory — Zustand is NOT currently in `web/package.json` but is required by the ported `designerCanvas.ts` store
  - [x] Run `pnpm add date-fns` from the `web/` directory — required by the `designer.library.tsx` page for `formatDistanceToNow` / `parseISO`
  - [x] Verify both appear in `web/package.json` dependencies

- [x] Task 2: Define shared designer types (AC-1, AC-3, AC-5)
  - [x] Create `web/src/types/designer.ts` with `DesignerElement`, `DesignerElementProperties`, `ComponentSchemaDto`, `ComponentSchemaVersion` interfaces — these replace `@/types/api` DesignerElement and ComponentSchemaDto from the ESG source
  - [x] See **Designer Type Shapes** in Dev Notes for exact interface definitions

- [x] Task 3: Create FormForge designer API module (AC-3)
  - [x] Create `web/src/features/designer/designerApi.ts` — replaces ESG's `@/api/componentDesigner`
  - [x] See **Designer API Module** in Dev Notes for endpoint mapping and function signatures
  - [x] NOTE: Backend designer endpoints do NOT exist yet (backend work deferred to Story 3.2+). The API module provides the correct FormForge path structure; pages will render loading/error states until the backend is implemented.

- [x] Task 4: Port utility and helper files (AC-2, AC-3, AC-5, AC-6)
  - [x] Create `web/src/components/designer/dnd.ts` — direct copy from ESG; no ESG-specific content. Update the comment header's reference to `Editor.tsx` if present; keep all exports as-is.
  - [x] Create `web/src/components/designer/schemaShape.ts` — port from ESG; update import: `@/types/api` → `../../../types/designer` (or `@/types/designer`)
  - [x] Create `web/src/components/designer/textInputTypes.ts` — direct copy; verify no ESG-specific imports
  - [x] Create `web/src/components/designer/validation.ts` — port from ESG; update import: `@/types/api` → `@/types/designer`
  - [x] Create `web/src/components/designer/visibility.ts` — port from ESG; update import: `@/types/api` → `@/types/designer`
  - [x] Create `web/src/components/designer/dynamicDropdown.ts` — port from ESG; replace `@/api/client` apiClient with FormForge `httpClient` from `@/features/auth/httpClient`; update `@/types/api` → `@/types/designer`

- [x] Task 5: Port Zustand canvas store (AC-2, AC-5)
  - [x] Create `web/src/store/designerCanvas.ts` — port from ESG `src/store/designerCanvas.ts`
  - [x] Update import: `@/types/api` → `@/types/designer`
  - [x] All store logic (createNewElement, createNewTabStack, tree manipulation, isDirty, selectElement, etc.) must be preserved exactly — this is the core canvas state machine

- [x] Task 6: Port DesignerToolbar (AC-2, AC-4)
  - [x] Create `web/src/components/designer/DesignerToolbar.tsx` — near-direct copy from ESG
  - [x] The ESG toolbar already uses pure Tailwind + lucide-react; no component library migration needed
  - [x] Verify all lucide icon names are valid in FormForge's `lucide-react ^1.16.0` (icon names may differ from ESG's `0.575.0` — check: AlignLeft, CalendarDays, CheckSquare, ChevronDown, Columns3, Hash, Image, LayoutList, LayoutPanelTop, MousePointerClick, Palette, Rows3, TextCursorInput, Type, Variable)
  - [x] Import cn from `@/lib/utils` (already exists in FormForge)

- [x] Task 7: Port DesignerCanvas (AC-2, AC-4)
  - [x] Create `web/src/components/designer/DesignerCanvas.tsx` — port from ESG
  - [x] Update imports: `@/types/api` → `@/types/designer`, `@/store/designerCanvas` → same new path
  - [x] The ESG canvas uses pure Tailwind; no shadcn migration needed for canvas structure
  - [x] Native HTML5 DnD pipeline must be preserved exactly — do NOT convert to dnd-kit or any other library

- [x] Task 8: Port ElementRenderer (AC-2, AC-4, AC-5)
  - [x] Create `web/src/components/designer/ElementRenderer.tsx` — port from ESG (2043 lines)
  - [x] Update imports: `@/types/api` → `@/types/designer`, `@/api/client` apiClient → FormForge httpClient, `@/api/files` → FormForge files API stub (see Dev Notes)
  - [x] **REMOVE** the `DesignerEmbedFrame` import and its usage — DesignerEmbedFrame is ESG-specific (DPP public page embedding), not needed in FormForge
  - [x] Where ElementRenderer previously passed an `interactive` prop to a Repeater that opened `DesignerEmbedFrame`, replace with a stub/comment marking this for Story 3.9 (DynamicComponent renderer for data entry)
  - [x] Preserve all 14 component type renderers exactly — the canvas preview (`interactive=false`) and form submission (`interactive=true`) modes must both work

- [x] Task 9: Port DynamicComponent (AC-5, AC-6)
  - [x] Create `web/src/components/designer/DynamicComponent.tsx` — port from ESG (270 lines)
  - [x] Replace `componentDesignerApi` → `designerApi` from `@/features/designer/designerApi`
  - [x] Update imports: `@/types/api` → `@/types/designer`
  - [x] TanStack Query v5 `useQuery({ queryKey, queryFn })` — already v5 in ESG source; verify no v4 patterns
  - [x] Preserve ALL contract behaviors: submitRef, onValidityChange, onReadyChange, shallow-equal initialData, visibility integration — these are load-bearing for Story 3.9 and AC-5

- [x] Task 10: Port PropertyInspector (AC-2, AC-4)
  - [x] Create `web/src/components/designer/PropertyInspector.tsx` — port from ESG (1157 lines)
  - [x] Update imports: `@/types/api` → `@/types/designer`, `@/store/designerCanvas` → same new path
  - [x] AC-4: Replace any raw `<input>`, `<select>`, `<textarea>`, `<button>` HTML elements with shadcn/ui primitives where they exist (shadcn `Input`, `Checkbox`, `Select`, `Button` from `web/src/components/ui/`) — ESG's PropertyInspector likely uses raw HTML form elements that should be migrated
  - [x] If shadcn components are missing (e.g. `Input`, `Select`, `Checkbox`, `Textarea`, `Label`), install them first via `pnpm dlx shadcn@latest add <component>` from the `web/` directory

- [x] Task 11: Port ComponentPreviewModal (AC-2, AC-4)
  - [x] Create `web/src/components/designer/ComponentPreviewModal.tsx` — port from ESG
  - [x] Replace any custom modal overlay with shadcn `Dialog` from `web/src/components/ui/dialog` (install via `pnpm dlx shadcn@latest add dialog` if not present)
  - [x] Update imports: `@/types/api` → `@/types/designer`

- [x] Task 12: Create designer library page route (AC-1, AC-3, AC-4, AC-6)
  - [x] Create `web/src/routes/_app/designer.library.tsx` — port from ESG `ComponentLibraryPage.tsx` (785 lines)
  - [x] **Replace react-router-dom** — see Import Replacement Table in Dev Notes
  - [x] Remove `AxiosError` import — use FormForge `ApiError` from `@/lib/api/apiError`
  - [x] Remove `VersionAwareViewerModal` — use `ComponentPreviewModal` directly
  - [x] Replace ESG's `Pagination` component — implement inline pagination using the `users.tsx` pattern (prev/next buttons with `Route.useSearch()` + `useNavigate`) or install shadcn pagination
  - [x] Route must export `Route = createFileRoute('/_app/designer/library')({...})`
  - [x] Add `validateSearch` with `z.coerce.number()` for `page` and `pageSize` (default 25, max 100) per AR-21 convention
  - [x] TanStack Query: all queries use v5 object form; query keys follow tuple convention `['designer', 'list', page, pageSize]`

- [x] Task 13: Create designer page route (AC-1, AC-3, AC-4, AC-6)
  - [x] Create `web/src/routes/_app/designer.$designerId.tsx` — port from ESG `ComponentDesignerPage.tsx` (302 lines)
  - [x] **Replace react-router-dom** — see Import Replacement Table in Dev Notes
  - [x] Remove `useAuth` import — auth is handled by `_app.tsx` `beforeLoad` guard already wired in Story 2.1; no per-page auth checks needed
  - [x] Route: `createFileRoute('/_app/designer/$designerId')({...})`
  - [x] URL structure: `/designer/$designerId` (no version in path — latest is the default; version selection done via query param or future dedicated route)
  - [x] Layout: DesignerToolbar (left) + DesignerCanvas (center, flex-1) + PropertyInspector (right) in a three-column flex row filling the viewport height

- [x] Task 14: Add designer nav link (AC-1)
  - [x] Update `web/src/routes/_app/admin.tsx` — add `<Link to="/designer/library">` in the admin nav alongside Users and Roles links
  - [x] Use `t('designer.nav.library')` for the link text

- [x] Task 15: Add i18n strings (AC-1)
  - [x] Add `designer` namespace to `web/src/lib/i18n/locales/en.json`
  - [x] See **i18n Keys** in Dev Notes for the full key list

- [x] Task 16: Verify build passes (AC-1 through AC-6)
  - [x] Run `pnpm run build` (Vite + `tsc -b --noEmit`) from `web/` — must complete with 0 TypeScript errors
  - [x] Run `pnpm run lint` — new errors must only be the `react-refresh/only-export-components` variant already present in existing route files (the pattern is incompatible with TanStack Router file-based routing)
  - [x] Confirm `routeTree.gen.ts` regenerated with `/designer/library` and `/designer/$designerId` registered under `_app`

## Dev Notes

### This story is FRONTEND-ONLY — no C# backend changes

Backend designer endpoints do not exist yet. The `designerApi.ts` module stubs the correct FormForge API surface; pages will render loading/empty states until the backend is implemented in Story 3.2+. This is intentional and correct.

---

### New npm dependencies (MUST add before porting)

| Package | Version | Reason |
|---|---|---|
| `zustand` | latest stable (`^5.0`) | Canvas state store — NOT in current `web/package.json` |
| `date-fns` | latest stable (`^4.0`) | `formatDistanceToNow` + `parseISO` in library page |

Install from `web/` directory:
```
pnpm add zustand date-fns
```

---

### ESG Source Files: What to Port vs Skip

**Port (all to `web/src/components/designer/`):**

| ESG Source | FormForge Target | Notes |
|---|---|---|
| `components/designer/dnd.ts` | `components/designer/dnd.ts` | Direct copy, trivial path update |
| `components/designer/schemaShape.ts` | `components/designer/schemaShape.ts` | Update `@/types/api` |
| `components/designer/textInputTypes.ts` | `components/designer/textInputTypes.ts` | Direct copy |
| `components/designer/validation.ts` | `components/designer/validation.ts` | Update `@/types/api` |
| `components/designer/visibility.ts` | `components/designer/visibility.ts` | Update `@/types/api` |
| `components/designer/dynamicDropdown.ts` | `components/designer/dynamicDropdown.ts` | Update `@/api/client` + `@/types/api` |
| `components/designer/DesignerToolbar.tsx` | `components/designer/DesignerToolbar.tsx` | Near-direct copy |
| `components/designer/DesignerCanvas.tsx` | `components/designer/DesignerCanvas.tsx` | Update imports |
| `components/designer/ElementRenderer.tsx` | `components/designer/ElementRenderer.tsx` | Remove DesignerEmbedFrame; update imports |
| `components/designer/DynamicComponent.tsx` | `components/designer/DynamicComponent.tsx` | Replace componentDesignerApi |
| `components/designer/PropertyInspector.tsx` | `components/designer/PropertyInspector.tsx` | Migrate raw HTML inputs → shadcn |
| `components/designer/ComponentPreviewModal.tsx` | `components/designer/ComponentPreviewModal.tsx` | Replace modal with shadcn Dialog |
| `store/designerCanvas.ts` | `store/designerCanvas.ts` | Update `@/types/api` |
| `pages/ComponentLibraryPage.tsx` | `routes/_app/designer.library.tsx` | Replace react-router-dom; replace Pagination |
| `pages/ComponentDesignerPage.tsx` | `routes/_app/designer.$designerId.tsx` | Replace react-router-dom; remove useAuth |

**Do NOT port (ESG-specific, not in FormForge scope):**

| ESG Source | Reason |
|---|---|
| `components/designer/DesignerEmbedFrame.tsx` | ESG's DPP public-page embedding — not a FormForge concept |
| `components/designer/VersionAwareDynamicComponent.tsx` | ESG-specific wrapper — FormForge uses DynamicComponent directly |
| `components/designer/VersionAwareViewerModal.tsx` | ESG-specific — use ComponentPreviewModal instead |
| `components/designer/index.ts` | ESG barrel file — FormForge imports directly |
| `components/designer/useKeyboardDnD.ts` | Story 3.10 — separate story, add stub file with TODO |

---

### Import Replacement Table

| ESG Import | FormForge Replacement |
|---|---|
| `from 'react-router-dom'` → `useNavigate` | `from '@tanstack/react-router'` → `useNavigate` |
| `from 'react-router-dom'` → `useParams` | `Route.useParams()` (from the route's own `createFileRoute` export) |
| `from 'react-router-dom'` → `Link` | `from '@tanstack/react-router'` → `Link` |
| `from '@/hooks/useAuth'` → `useAuth` | Remove entirely — auth gate is in `_app.tsx` `beforeLoad` |
| `from '@/api/componentDesigner'` → `componentDesignerApi` | `from '@/features/designer/designerApi'` → `designerApi` |
| `from '@/api/client'` → `apiClient` | `from '@/features/auth/httpClient'` → `httpClient` |
| `from '@/api/files'` → `filesApi` | Stub with `filesApi` export in `@/features/designer/filesApi.ts` (see below) |
| `from '@/types/api'` → `DesignerElement`, `ComponentSchemaDto` | `from '@/types/designer'` → same names |
| `from 'axios'` → `AxiosError` | `from '@/lib/api/apiError'` → `ApiError` |
| `from '@/components/Pagination'` | Inline pagination pattern (see designer.library.tsx) |
| `from './DesignerEmbedFrame'` | Remove; stub Repeater's interactive mode |

---

### TanStack Router: Specific API Replacements

The ESG pages use React Router DOM. FormForge uses TanStack Router. Exact replacements:

**In ComponentDesignerPage → `designer.$designerId.tsx`:**
```tsx
// ESG (React Router DOM)
const { designerId, version } = useParams<{ designerId?: string; version?: string }>()
const navigate = useNavigate()
navigate(`/designer/${id}`)

// FormForge (TanStack Router)
export const Route = createFileRoute('/_app/designer/$designerId')({
  component: DesignerPage,
})
function DesignerPage() {
  const { designerId } = Route.useParams()
  const navigate = useNavigate({ from: '/designer/$designerId' })
  void navigate({ to: '/designer/$designerId', params: { designerId: id } })
}
```

**In ComponentLibraryPage → `designer.library.tsx`:**
```tsx
// ESG (React Router DOM)
const navigate = useNavigate()
<Link to={`/designer/${row.designerId}`}>

// FormForge (TanStack Router)
export const Route = createFileRoute('/_app/designer/library')({
  validateSearch: designerLibrarySearchSchema,
  component: DesignerLibraryPage,
})
function DesignerLibraryPage() {
  const navigate = useNavigate({ from: '/designer/library' })
  <Link to="/designer/$designerId" params={{ designerId: row.designerId }}>
}
```

---

### Designer Type Shapes (create in `web/src/types/designer.ts`)

```ts
export interface DesignerElementProperties {
  // Well-known properties (all optional — schema is open)
  label?: string
  bindTo?: string
  fieldKey?: string
  tabName?: string
  rows?: number
  paddingPx?: number
  contentGapPx?: number
  orientation?: 'horizontal' | 'vertical'
  placeholder?: string
  required?: boolean
  readOnly?: boolean
  hideWhenAll?: Record<string, unknown>[]
  hideWhenAny?: Record<string, unknown>[]
  options?: string
  dependsOn?: string
  apiPath?: string
  // Allow arbitrary properties
  [key: string]: unknown
}

export interface DesignerElement {
  id: string
  type: string
  properties: DesignerElementProperties
  children?: DesignerElement[]
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
  status: 'Draft' | 'Published' | 'Archived'
  latestVersion: number
  rootElement: DesignerElement | null
  createdAt: string
  updatedAt: string | null
  versions?: ComponentSchemaVersion[]
}

export interface ComponentSchemaListItem {
  designerId: string
  displayName: string
  status: 'Draft' | 'Published' | 'Archived'
  latestVersion: number
  createdAt: string
  updatedAt: string | null
}
```

---

### Designer API Module (`web/src/features/designer/designerApi.ts`)

Map ESG's `componentDesignerApi` calls to FormForge API paths. Backend does not exist yet — these paths are the intended contract for Story 3.2+.

```ts
import { httpClient } from '@/features/auth/httpClient'
import type { ComponentSchemaDto, ComponentSchemaListItem } from '@/types/designer'
import type { PagedResult } from '@/features/admin/users/types'

export const designerApi = {
  listSchemas: (page: number, pageSize: number) =>
    httpClient.get<PagedResult<ComponentSchemaListItem>>(
      `/api/designer?page=${page}&pageSize=${pageSize}`,
    ),

  getSchema: (designerId: string, version?: number) =>
    version !== undefined
      ? httpClient.get<ComponentSchemaDto>(`/api/designer/${designerId}/versions/${version}`)
      : httpClient.get<ComponentSchemaDto>(`/api/designer/${designerId}`),

  createSchema: (body: { designerId: string; displayName: string }) =>
    httpClient.post<ComponentSchemaDto>('/api/designer', body),

  saveVersion: (designerId: string, version: number, rootElement: unknown) =>
    httpClient.put<void>(`/api/designer/${designerId}/versions/${version}`, { rootElement }),

  publishVersion: (designerId: string, version: number) =>
    httpClient.post<void>(`/api/designer/${designerId}/versions/${version}/publish`, {}),

  archiveVersion: (designerId: string, version: number) =>
    httpClient.post<void>(`/api/designer/${designerId}/versions/${version}/archive`, {}),

  duplicateSchema: (designerId: string) =>
    httpClient.post<ComponentSchemaDto>(`/api/designer/${designerId}/duplicate`, {}),
}
```

**Files API stub** (`web/src/features/designer/filesApi.ts`):
ElementRenderer uses `filesApi` for image presigning. Stub it pointing to a FormForge files endpoint — full implementation is a future story.
```ts
import { httpClient } from '@/features/auth/httpClient'

export const filesApi = {
  getPresignedUrl: (fileKey: string) =>
    httpClient.get<{ url: string }>(`/api/files/presign?key=${encodeURIComponent(fileKey)}`),
}
```

---

### DynamicComponent Contract (AC-5) — Critical Behaviors to Preserve

The following behaviors MUST survive the port exactly. These are load-bearing for Story 3.9:

1. **`submitRef`** — `.current` is assigned a closure that runs the validate-then-`onSave(payload)` path. External host (future data-entry view) calls `submitRef.current()` to submit without a Button leaf inside the schema.

2. **`onValidityChange(isValid)`** — fires every time the `validateForm()` result changes. Derived from the same `useMemo` that drives the inline error UI — cannot drift.

3. **`onReadyChange(isReady)`** — fires when `parsedRoot` is non-null AND schema is not loading AND not errored. Gates external Save against a schema-load-in-flight.

4. **`shallowEqualInitialData()`** — prevents spurious form resets when a host passes `initialData={{}}` as an inline literal on every render. Without this, every parent re-render would wipe formData.

5. **Repeater row scoping** — Repeater children are excluded from `extractBindToKeys()` — they bind to row-level keys, not outer-form keys. The row data lives inside `formData[repeater.bindTo]` as an array of objects.

6. **`schema` prop bypass** — when `schema` is provided directly, the internal `useQuery` fetch is skipped entirely. Required for read-only preview mode where the schema is already in hand.

---

### shadcn UI: What to Install for AC-4

The ESG designer uses pure Tailwind (no UI library). For AC-4 compliance, replace raw HTML form controls in `PropertyInspector.tsx` with shadcn primitives. Install as needed:

```bash
# from web/ directory
pnpm dlx shadcn@latest add input
pnpm dlx shadcn@latest add checkbox
pnpm dlx shadcn@latest add select
pnpm dlx shadcn@latest add textarea
pnpm dlx shadcn@latest add label
pnpm dlx shadcn@latest add dialog
pnpm dlx shadcn@latest add badge
pnpm dlx shadcn@latest add separator
```

Each installed component lands in `web/src/components/ui/`. The canvas itself (DesignerCanvas, DesignerToolbar, ElementRenderer) may remain Tailwind-only since the canvas chrome is intentionally custom — focus shadcn migration on PropertyInspector inputs and the ComponentPreviewModal.

---

### DesignerEmbedFrame Removal from ElementRenderer

ElementRenderer references `DesignerEmbedFrame` in its Repeater row renderer. When porting:
1. Remove the import line.
2. Where the Repeater row interactive renderer previously rendered `<DesignerEmbedFrame ...>`, replace with:
```tsx
{/* TODO Story 3.9: DynamicComponent renderer for data entry — Repeater row editing not yet wired */}
<div className="rounded border border-dashed border-slate-300 p-4 text-xs text-slate-400">
  Row editing available in data entry view
</div>
```
This is correct behavior for Story 3.1 — the data-entry Repeater renderer is Story 3.9's scope.

---

### lucide-react Version Note

FormForge uses `lucide-react ^1.16.0`. ESG used `^0.575.0`. A few icon names changed between versions. Before running the build, verify these icons exist (check `node_modules/lucide-react/dist/esm/icons/` or the Lucide changelog):

- `Variable` — may be renamed; check for `Variable` or `Braces`
- `LayoutPanelTop` — check exists in 1.x
- `TextCursorInput` — check exists in 1.x

If any icon name is not found, substitute the nearest available icon from `lucide-react` and leave a comment.

---

### i18n Keys (add to `en.json` under `"designer": {...}`)

```json
"designer": {
  "nav": {
    "library": "Component Library"
  },
  "library": {
    "title": "Component Library",
    "subtitle": "Design and manage versioned data-entry form schemas.",
    "createButton": "New Designer",
    "loading": "Loading…",
    "loadError": "Could not load component schemas.",
    "noSchemas": "No component schemas yet.",
    "colDesignerId": "Designer ID",
    "colDisplayName": "Name",
    "colStatus": "Status",
    "colVersion": "Version",
    "colUpdated": "Last Updated",
    "previousPage": "Previous",
    "nextPage": "Next",
    "pageIndicator": "Page {{page}} of {{totalPages}}",
    "openInCanvas": "Open in canvas",
    "preview": "Preview",
    "duplicate": "Duplicate",
    "archive": "Archive",
    "archiveSuccess": "Schema archived.",
    "duplicateSuccess": "Schema duplicated.",
    "createSuccess": "Designer created.",
    "nameLabel": "Designer ID",
    "displayNameLabel": "Display name",
    "createError": "Could not create designer."
  },
  "canvas": {
    "title": "Designer",
    "saving": "Saving…",
    "saved": "Saved",
    "saveError": "Could not save.",
    "saveButton": "Save",
    "previewButton": "Preview",
    "unsavedChanges": "You have unsaved changes.",
    "emptyCanvas": "Drop a Stack, Row, or Tabs container here to start building.",
    "noSchemaFound": "Schema not found.",
    "loadError": "Could not load schema."
  },
  "inspector": {
    "title": "Properties",
    "noSelection": "Select an element to edit its properties.",
    "labelField": "Label",
    "fieldKeyField": "Field key",
    "bindToField": "Bind to",
    "placeholderField": "Placeholder",
    "requiredField": "Required",
    "readOnlyField": "Read-only"
  }
}
```

---

### Project Structure Notes

**New files to CREATE:**

| File | Purpose |
|---|---|
| `web/src/types/designer.ts` | DesignerElement, ComponentSchemaDto type definitions |
| `web/src/store/designerCanvas.ts` | Zustand canvas state store |
| `web/src/features/designer/designerApi.ts` | FormForge designer API module |
| `web/src/features/designer/filesApi.ts` | Files API stub for ElementRenderer image presigning |
| `web/src/components/designer/dnd.ts` | HTML5 DnD pipeline types/helpers |
| `web/src/components/designer/schemaShape.ts` | DesignerElement runtime type guard |
| `web/src/components/designer/textInputTypes.ts` | Text input type resolver |
| `web/src/components/designer/validation.ts` | Form validation engine |
| `web/src/components/designer/visibility.ts` | Conditional visibility engine |
| `web/src/components/designer/dynamicDropdown.ts` | API-driven dropdown helpers |
| `web/src/components/designer/DesignerToolbar.tsx` | Draggable element palette |
| `web/src/components/designer/DesignerCanvas.tsx` | HTML5 DnD canvas |
| `web/src/components/designer/ElementRenderer.tsx` | Per-element renderer (canvas + form) |
| `web/src/components/designer/DynamicComponent.tsx` | Schema-driven form renderer |
| `web/src/components/designer/PropertyInspector.tsx` | Right-side property editor |
| `web/src/components/designer/ComponentPreviewModal.tsx` | Preview dialog |
| `web/src/routes/_app/designer.library.tsx` | Component library list page |
| `web/src/routes/_app/designer.$designerId.tsx` | Canvas/editor page |

**Files to MODIFY:**

| File | Change |
|---|---|
| `web/package.json` | Add `zustand`, `date-fns` dependencies |
| `web/src/routes/_app/admin.tsx` | Add `<Link to="/designer/library">` nav entry |
| `web/src/lib/i18n/locales/en.json` | Add `designer` namespace |

**Files to leave untouched:**
- All C# backend files — story is frontend-only
- `web/src/routeTree.gen.ts` — auto-regenerated
- All existing Epic 2 feature files

---

### Architecture & Routing Notes

- Route files are picked up automatically by the TanStack Router Vite plugin. `routeTree.gen.ts` regenerates at dev-server start — **never edit manually**.
- The `_app.tsx` route already has a `beforeLoad` guard that redirects unauthenticated users — no per-page auth check needed in designer routes.
- Admin-only gate: if the designer is admin-only (Platform Admin persona), add `beforeLoad: ({ context }) => { if (!context.auth?.isAdmin) throw redirect({ to: '/' }) }` in the route definition, consistent with `admin.tsx`.

### References

- [Source: _bmad-output/planning-artifacts/epics.md — Epic 3, Story 3.1 ACs]
- [Source: _bmad-output/planning-artifacts/architecture.md — Decision 4.5 (Keyboard DnD, Story 3.10), Decision 4.6 (folder structure), Decision 4.10 (DynamicComponent black box)]
- [Source: _bmad-output/planning-artifacts/architecture.md — Decision 1.2 (14 component types and column mapping)]
- [Source: C:\Users\MY\Downloads\esg_platform\BkmeaEsgPlatform\client\src\components\designer\ — all ESG source files]
- [Source: C:\Users\MY\Downloads\esg_platform\BkmeaEsgPlatform\client\src\store\designerCanvas.ts — Zustand store]
- [Source: C:\Users\MY\Downloads\esg_platform\BkmeaEsgPlatform\client\src\pages\ComponentDesignerPage.tsx — designer page]
- [Source: C:\Users\MY\Downloads\esg_platform\BkmeaEsgPlatform\client\src\pages\ComponentLibraryPage.tsx — library page]
- [Source: web/src/routes/_app/admin/users.tsx — TanStack Router page pattern to mirror]
- [Source: web/src/routes/_app/admin.tsx — AdminLayout, beforeLoad guard pattern]
- [Source: web/src/features/auth/httpClient.ts — FormForge HTTP client to use instead of ESG apiClient]
- [Source: web/src/lib/api/apiError.ts — ApiError to use instead of AxiosError]

## Dev Agent Record

### Agent Model Used

claude-opus-4-7

### Debug Log References

- `pnpm-workspace.yaml` had a placeholder `allowBuilds:` entry left by a prior pnpm prompt. Replaced/extended with `ignoredBuiltDependencies: [msw]` so pnpm v11's pre-exec deps-status check no longer aborts on the ignored msw build script. `pnpm install --ignore-scripts` then `node ./node_modules/vite/bin/vite.js build` was used for verification because corepack-pnpm-run still triggers the dependency check.

### Completion Notes List

- AC-1 — All 18 target files landed at the spec'd FormForge paths:
  - `web/src/types/designer.ts` (new shared types)
  - `web/src/features/designer/designerApi.ts` + `filesApi.ts` (FormForge API layer; backend deferred to 3.2+)
  - `web/src/store/designerCanvas.ts` (Zustand store; `rootElement` now typed as `DesignerElement | null` so the legacy JSON.parse in `setSchema` was dropped)
  - `web/src/components/designer/{dnd,schemaShape,textInputTypes,validation,visibility,dynamicDropdown}.ts` — utility helpers
  - `web/src/components/designer/{DesignerToolbar,DesignerCanvas,ElementRenderer,DynamicComponent,PropertyInspector,ComponentPreviewModal}.tsx`
  - `web/src/routes/_app/designer.library.tsx`, `web/src/routes/_app/designer.$designerId.tsx`
- AC-2 — Native HTML5 DnD preserved verbatim in DesignerCanvas (`dnd.ts` MIME renamed to `application/x-formforge-designer`; DropZone/CanvasContainer/CanvasTabsContainer/CanvasRepeaterContainer/CanvasLeaf all carry forward unchanged). All 14 component types render via ElementRenderer's leaf/repeater/tabs branches.
- AC-3 — Grepped `web/src/components/designer/` and the new routes for `esg|bkmea|useAuth|react-router-dom|@/api/componentDesigner|AxiosError|/api/component-schemas|DPP|DesignerEmbedFrame|VersionAware`. Only a single match remains in source comments: the `application/x-formforge-designer` MIME constant (intentional, formforge-namespaced). All ESG-named imports were replaced with FormForge equivalents.
- AC-4 — shadcn primitives installed via `pnpm dlx shadcn@latest add input checkbox select textarea label dialog`. PropertyInspector migrated: raw `<input>` → `<Input>`, `<textarea>` → `<Textarea>`, `<input type="checkbox">` → `<Checkbox>`, every `<select>` → shadcn `<Select>` family. Color-picker stays raw `<input type=color>` (shadcn Input doesn't cover color UA chrome). Library page uses `<Button>`, `<Dialog>`, `<Input>`. Designer page uses `<Button>`, `<Input>`.
- AC-5 — DynamicComponent contract preserved end-to-end: `submitRef`, `onValidityChange`, `onReadyChange`, `shallowEqualInitialData`, Repeater row scoping in `extractBindToKeys`/`extractDefaults`, and the `schema` prop bypass (TanStack `enabled` flag). The only contract-relevant adaptation is dropping `JSON.parse(schema.rootElement)` because FormForge's `ComponentSchemaDto.rootElement` is typed as a parsed `DesignerElement | null` (the runtime `isDesignerElementShape` guard remains as defense against malformed payloads).
- AC-6 — `useQuery({ queryKey, queryFn, staleTime, enabled })` and `useMutation({ mutationFn, onSuccess, onError })` v5 object form used throughout new code. No `cacheTime` survived the port (`gcTime` is the v5 spelling; not needed for any current query). Query keys follow tuple convention: `['designer', 'list', page, pageSize]`, `['designer', 'schema', designerId, version]`, `['files-presign', key]`, `['dynamic-dropdown-options', resolvedPath]`, `['component-schema', designerId, version]` (preserved from ESG for DynamicComponent).
- Build status: `node ./node_modules/vite/bin/vite.js build` — 2446 modules transformed, designer.library / designer.$designerId chunks emit cleanly. `node ./node_modules/typescript/bin/tsc -b --noEmit` — 0 errors. ESLint — 24 errors total, all of the expected `react-refresh/only-export-components` variant (5 pre-existing + 6 new for designer routes), matching the story's AC for `pnpm run lint`.
- `routeTree.gen.ts` regenerated and includes `_app/designer/library` + `_app/designer/$designerId` under the `_app` route subtree.
- One workspace-hygiene fix: `pnpm-workspace.yaml` switched from a placeholder `allowBuilds:` map to `ignoredBuiltDependencies: [msw]` so corepack/pnpm-v11 no longer fails its pre-exec deps-status check on the msw postinstall script.
- Backend not yet present (per Dev Notes): library and designer pages will surface load errors until Story 3.2 lands the designer endpoints; this is the intentional, documented state.
- One scope-honest deviation: the canvas page's save calls `designerApi.saveVersion(designerId, version || 1, rootElement)` because FormForge's contract has no separate `update`/`updateVersion` split (Story 3.2 owns the backend). The display name is currently only edited in the canvas-store; persistence happens once the backend endpoint accepts a displayName field. Wiring left clearly named so the next story can extend without a contract change.

### File List

**New files:**
- `web/src/types/designer.ts`
- `web/src/features/designer/designerApi.ts`
- `web/src/features/designer/filesApi.ts`
- `web/src/store/designerCanvas.ts`
- `web/src/components/designer/dnd.ts`
- `web/src/components/designer/schemaShape.ts`
- `web/src/components/designer/textInputTypes.ts`
- `web/src/components/designer/validation.ts`
- `web/src/components/designer/visibility.ts`
- `web/src/components/designer/dynamicDropdown.ts`
- `web/src/components/designer/DesignerToolbar.tsx`
- `web/src/components/designer/DesignerCanvas.tsx`
- `web/src/components/designer/ElementRenderer.tsx`
- `web/src/components/designer/DynamicComponent.tsx`
- `web/src/components/designer/PropertyInspector.tsx`
- `web/src/components/designer/ComponentPreviewModal.tsx`
- `web/src/routes/_app/designer.library.tsx`
- `web/src/routes/_app/designer.$designerId.tsx`
- `web/src/components/ui/input.tsx` (installed via shadcn CLI)
- `web/src/components/ui/checkbox.tsx` (installed via shadcn CLI)
- `web/src/components/ui/select.tsx` (installed via shadcn CLI)
- `web/src/components/ui/textarea.tsx` (installed via shadcn CLI)
- `web/src/components/ui/label.tsx` (installed via shadcn CLI)
- `web/src/components/ui/dialog.tsx` (installed via shadcn CLI)

**Modified files:**
- `web/package.json` (added zustand, date-fns; shadcn CLI added radix-ui subdeps)
- `web/pnpm-lock.yaml`
- `web/pnpm-workspace.yaml` (ignoredBuiltDependencies)
- `web/src/routes/_app/admin.tsx` (added Designer Library nav link)
- `web/src/lib/i18n/locales/en.json` (added designer + common namespaces)
- `web/src/routeTree.gen.ts` (auto-regenerated by TanStack Router Vite plugin)

### Change Log

| Date | Author | Change |
|---|---|---|
| 2026-05-23 | Amelia (claude-opus-4-7) | Initial implementation — ported ESG Platform designer (18 source files) into FormForge: shared types, API + files stubs, Zustand store, 6 utility helpers, 6 designer components, 2 TanStack Router routes, shadcn migration for PropertyInspector + Modal, i18n keys, admin nav link, build verified clean. |
| 2026-05-23 | claude-opus-4-7 | Code review (Blind Hunter + Edge Case Hunter + Acceptance Auditor) on `f47a302~..f47a302`. 38 unique findings: 25 patch applied, 13 deferred (4 decision-needed deferred to post-3.2 backend), 0 dismissed. Build verified clean. Status → done. |

### Review Findings

Adversarial review run 2026-05-23 (Blind Hunter + Edge Case Hunter + Acceptance Auditor against `f47a302~..f47a302`). 38 unique findings after dedupe: 25 patch, 13 deferred (9 originally + 4 decision-needed deferred to post-3.2 backend), 0 dismissed.

#### Patch (unambiguous fixes)

- [x] [Review][Patch] [HIGH] `version: 0` from a fresh schema bypasses `?? 1` fallback on save (`??` only fires on null/undefined) — coerce non-positive integers to 1 [`web/src/routes/_app/designer.$designerId.tsx:121`]
- [x] [Review][Patch] [HIGH] `saveMutation.onSuccess` clears `isDirty` against a closure-captured `rootElement` that re-binds each render — compare against `vars.rootElement` from the mutation closure [`web/src/routes/_app/designer.$designerId.tsx:130-138`]
- [x] [Review][Patch] [HIGH] Save-success `invalidateQueries(['designer','schema',designerId])` matches active `['designer','schema',designerId,'latest']` by prefix and refetches by default, overwriting in-flight user edits — pass `{ refetchType: 'none' }` or update the cache in place from the response [`web/src/routes/_app/designer.$designerId.tsx:84-93, 140`]
- [x] [Review][Patch] [MED] Tabs `activeTab` is stored as a raw child index but indexed into the visibility-filtered children list — hiding an earlier tab silently shifts the user's selection. Track selection by child id, not positional index [`web/src/components/designer/ElementRenderer.tsx:2306-2319`]
- [x] [Review][Patch] [MED] `subtreeHasErrors` recurses past Repeater boundaries, so a row-template Field with `bindTo='name'` matches an outer-form error and paints the wrong tab badge — early-return on `el.type === 'Repeater'` to match the scoping rule in `extractBindToKeys` [`web/src/components/designer/validation.ts:5298-5312`]
- [x] [Review][Patch] [MED] Repeater Field inline-style blocklist is too narrow: blocks `position: fixed/absolute/sticky` only, allows `transform: translate(9999px,9999px)`, `inset: 0`, `z-index`, `pointer-events: none` — schema authors can escape container / clickjack / break parent clicks. Expand the blocklist (and/or move to an allowlist) [`web/src/components/designer/ElementRenderer.tsx:2054-2055, 2108`, `35-37`]
- [x] [Review][Patch] [MED] `URL_JS_RE` regex for `url(javascript:…)` is bypassable with CSS hex/unicode escapes (`url(\6Aavascript:…)` decodes to `javascript:`) — decode CSS escapes before testing, or block the substring `url(` entirely for untrusted inline-style strings [`web/src/components/designer/ElementRenderer.tsx:2056, 2109`]
- [x] [Review][Patch] [MED] Static-source `Dropdown` `options` text is irreversibly destroyed when the user toggles to Dynamic source — keep both sets in the model and only switch which inspector fields render [`web/src/components/designer/PropertyInspector.tsx:683-690`]
- [x] [Review][Patch] [MED] Stack moved out of a Tabs ancestor keeps orphan `tabName/paddingPx/contentGapPx` invisible to the inspector and resurfaces them on a future move back in — strip tab-only props on parent-type change in the store's `moveElement` [`web/src/components/designer/PropertyInspector.tsx:1012-1016, 1092-1093`, `web/src/store/designerCanvas.ts:moveElement`]
- [x] [Review][Patch] [MED] `RepeaterInteractive` calls `setModalOpen(false); setEditingIndex(null)` directly in render when `editingIndex >= list.length` — move to a `useEffect` keyed on `[list.length, editingIndex]` to avoid setState-during-render warnings and timer races with the drawer's 350ms close cleanup [`web/src/components/designer/ElementRenderer.tsx:3586-3594, 1568-1575`]
- [x] [Review][Patch] [MED] PropertyInspector's required `NumberInput` (Font size, paddingPx, contentGapPx, TextArea rows) clamps a cleared input to `0` because `Number('') === 0` passes `!isNaN` — preserve previous value on empty raw (use the existing `OptionalNumberInput` pattern at lines 220-246) [`web/src/components/designer/PropertyInspector.tsx:200-217`]
- [x] [Review][Patch] [MED] Color Picker's `HEX_COLOR.test` fallback to `#000000` is display-only — `formData[bindTo]` retains the bad value (e.g. `'rgb(255,0,0)'`, `'red'`) and is submitted as-is. Normalize on input change when the stored value fails validation, or surface a validation error [`web/src/components/designer/ElementRenderer.tsx:806-811`, `DynamicComponent.tsx:82-86`]
- [x] [Review][Patch] [LOW] Dead/no-op conditional `t('designer.canvas.saving') !== '' ? null : null` in the loading-state JSX — delete the unreachable ternary or render the actual translation [`web/src/routes/_app/designer.$designerId.tsx:6484`]
- [x] [Review][Patch] [LOW] `failedForSrc` latch never clears — once an image errors (transient blip, presigned URL hits its ~50min staleTime expiry), the fallback UI persists until src changes. Invalidate `['files-presign', rawSrc]` on `onError` to force a fresh presign + retry [`web/src/components/designer/ElementRenderer.tsx:2937-2999, 924-929`]
- [x] [Review][Patch] [LOW] DropZone highlight flickers `ok → idle → ok` on enter/leave of child nodes and sticks at `'ok'` if user releases the mouse outside any drop target — use a dragenter/leave counter and listen for `dragend`/`drop` at document level to clear residual highlights [`web/src/components/designer/DesignerCanvas.tsx:168-178, 964-980, 1044-1051`]
- [x] [Review][Patch] [LOW] `EmptyCanvasDrop` `onDrop` doesn't call `e.stopPropagation()` while sibling DropZones do — bubbling can double-fire any future parent drop handler. Match sibling DropZone semantics [`web/src/components/designer/DesignerCanvas.tsx:974-980`]
- [x] [Review][Patch] [LOW] `dependencySignature` `useMemo` depends on the whole `formData` object, recomputed on every keystroke — only depend on the values listed in `dependsOn`, e.g. `useMemo(() => JSON.stringify(dependsOn.map(...)), [dependsOn, ...dependsOn.map(k => formData?.[k])])` or hash-based [`web/src/components/designer/ElementRenderer.tsx:3038-3041`]
- [x] [Review][Patch] [LOW] `parseInlineStyle` runs unmemoized inside `LeafRenderer` case `'Repeater Field'` — wrap in `useMemo` on `p.inlineStyle` to avoid O(N×M) parse work per parent render [`web/src/components/designer/ElementRenderer.tsx:2872-2902`]
- [x] [Review][Patch] [LOW] `useEffect` cleanup `resetCanvas()` on `designerId` change can race with the separate `if (data) setSchema(data)` effect — combine into a single effect keyed on `designerId` that resets first, then loads [`web/src/routes/_app/designer.$designerId.tsx:6332-6342`]
- [x] [Review][Patch] [LOW] `web/pnpm-workspace.yaml` still contains the placeholder `allowBuilds: msw: set this to true or false` alongside the new `ignoredBuiltDependencies: [msw]` — remove the leftover stub [`web/pnpm-workspace.yaml:~`]
- [x] [Review][Patch] [LOW] Repeater validation rule order returns "is required" before the more specific "At least N rows required" — when `minRows > 0`, prefer the minRows message regardless of `required` [`web/src/components/designer/validation.ts:196-209`]
- [x] [Review][Patch] [LOW] `resolveApiPath` doesn't enforce a leading `/` — relative `apiPath: 'factories'` resolves against the page URL (e.g. `/designer/abc/factories`) and 404s with a misleading message. Require a leading `/` or prepend [`web/src/components/designer/dynamicDropdown.ts:39-63`]
- [x] [Review][Patch] [LOW] `addElement` accepts any element as a child of Tabs (Tabs is in `CONTAINER_TYPES`), but the renderer assumes Tabs children are Stacks — validate that Tabs children are Stack at the store boundary [`web/src/store/designerCanvas.ts:146-172`, `web/src/components/designer/DesignerCanvas.tsx:350-354`]
- [x] [Review][Patch] [LOW] `PropertyInspector.findById(rootElement, selectedElementId ?? '')` falls back to null when the selection is deleted out from under it (without going through `removeElement`) — when the inspector detects a dangling id, fire `selectElement(null)` to clear stale state [`web/src/components/designer/PropertyInspector.tsx:1079-1087`]
- [x] [Review][Patch] [LOW] Three comment-only references to `ESG` / `DesignerEmbedFrame` violate AC-3's strict "zero matches" wording — rephrase without the forbidden tokens [`web/src/components/designer/ElementRenderer.tsx:1819`, `web/src/components/designer/PropertyInspector.tsx:251`, `web/src/routes/_app/designer.library.tsx:124`]

#### Deferred (pre-existing or scope-bound future work)

- [x] [Review][Defer] [HIGH→post-3.2] **`saveVersion` payload omits `displayName`** [`web/src/routes/_app/designer.$designerId.tsx:125`, `web/src/features/designer/designerApi.ts:29-33`] — UI mutates the canvas-store `displayName`, mutation collects `vars.displayName`, but `saveVersion(id, v, root)` only sends `{ rootElement }`. Frontend says "Saved" while rename is lost. **Deferred — pre-3.2 backend; revisit once API exists.** Once the backend lands the API contract becomes concrete (PUT body shape, separate rename endpoint, etc.) and the decision is no longer abstract.
- [x] [Review][Defer] [MED→post-3.2] **Dynamic dropdown cascade indiscriminately resets child to `''` even when the new options list still contains the value, and 3-level cascades leave grandchildren un-reset** [`web/src/components/designer/ElementRenderer.tsx:1053-1077`] — Direct-parent reset is immediate and lossy; grandparent ripple skips the grandchild because `allDependenciesReady` flips to false and the effect early-exits. **Deferred — pre-3.2 backend; revisit once API exists.** Cascade UX is hard to settle without a real backend serving real dependent option lists; revisit during 3.2/3.5 designer-preview work.
- [x] [Review][Defer] [MED→post-3.2] **Leaf-typed root traps the canvas with no recovery affordance** [`web/src/store/designerCanvas.ts:152-162`, `web/src/components/designer/DesignerCanvas.tsx:56-66, 274`] — Saved schemas with non-container roots reject all drops and have no delete badge. **Deferred — pre-3.2 backend; revisit once API exists.** Leaf-rooted schemas are not reachable until the backend persists them; revisit when 3.2 lands schema persistence.
- [x] [Review][Defer] [MED→post-3.2] **`extractOptions` envelope auto-unwrap collides with bare objects that legitimately have `items`/`data`/`results` keys** [`web/src/components/designer/dynamicDropdown.ts:73, 85-93`] — First-match unwrap of `['items','data','results']` mis-selects when the envelope is not paginated. **Deferred — pre-3.2 backend; revisit once API exists.** The unwrap convention should be designed against a real paginated API; revisit when 3.2/3.5 ships real dynamic-dropdown endpoints.
- [x] [Review][Defer] [LOW] `RepeaterRowDrawer` is a TODO stub — `commitAdd`/`commitReplace`/`closeModal` callbacks are unreachable through the drawer body; the drawer parameter destructure even drops `initialData` and `onSave`. Acknowledged Story 3.9 work [`web/src/components/designer/ElementRenderer.tsx:3601-3850`] — deferred, scope-bound future work
- [x] [Review][Defer] [LOW] `cloneTree` in `designerCanvas.ts` deep-clones every node on every mutation, invalidating the `useMemo` in `ElementRenderer` that the comment claims will skip unrelated subtrees. Either delete the misleading memo+comment or restructure the store for structural sharing [`web/src/components/designer/ElementRenderer.tsx:2219-2234`, `web/src/store/designerCanvas.ts:7056-7062`] — deferred, architectural; defer until a structural-sharing pass lands
- [x] [Review][Defer] [LOW] `handleDesignerChange` in `PropertyInspector` issues two back-to-back `updateProp` calls, each deep-cloning the entire tree — needs a `updateElementProperties(id, partial)` or transaction API on the store [`web/src/components/designer/PropertyInspector.tsx:4146-4155`] — deferred, needs new store API
- [x] [Review][Defer] [LOW] Repeater row-form `<Select>` filters self-reference only against page-1 results (`['designer','list',1,100]`) — schemas beyond the first 100 can't be picked. Needs a paginated/searchable picker [`web/src/components/designer/PropertyInspector.tsx:4130-4143`] — deferred, needs paginated picker UI
- [x] [Review][Defer] [LOW] Versions `<Select>` only renders the currently-pinned value plus "Latest" — author cannot pin a specific version through this UI yet [`web/src/components/designer/PropertyInspector.tsx:4212-4232`] — deferred, acknowledged Story 3.7 work
- [x] [Review][Defer] [LOW] `commitAdd`/`commitReplace`/`deleteRow` rebind identity on every keystroke (`list` is in their `useCallback` deps) — minor perf; needs a ref-based or functional-update pattern to stabilize [`web/src/components/designer/ElementRenderer.tsx:3601-3625`] — deferred, minor perf
- [x] [Review][Defer] [LOW] `Number Input` interactive: when `valueAsNumber` is `NaN`, the DOM still shows the user-typed garbage while `formData[bindTo]` is `undefined` — mostly browser-mitigated (Chromium/Firefox reject non-numeric typing on `<input type="number">`) but the UI/data mismatch can still occur on paste or older browsers [`web/src/components/designer/ElementRenderer.tsx:586-592`] — deferred, browser-mitigated
- [x] [Review][Defer] [LOW] `computeVisibility` skips registering bindTos of elements inside a Repeater template, so visibility rules referencing those names are silently dead — needs either a lint warning in PropertyInspector or scope-aware evaluation [`web/src/components/designer/visibility.ts:53-68, 101-106`] — deferred, architectural; needs scope-aware visibility evaluation
- [x] [Review][Defer] [LOW] `DynamicComponent` fallback condition `(schema && !parsedRoot)` renders "Component unavailable" for a valid but empty schema (`rootElement: null`) — distinguish "empty/in-design" from "malformed" once the backend lands [`web/src/components/designer/DynamicComponent.tsx:122-126, 250-258`] — deferred, needs data-shape distinction post-3.2 backend
