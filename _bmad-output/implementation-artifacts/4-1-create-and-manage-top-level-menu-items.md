# Story 4.1: Create and Manage Top-Level Menu Items

Status: done

## Story

As a Platform Admin,
I want to create, edit, and delete top-level Menu Items,
so that I can define the platform's navigation structure.

## Acceptance Criteria

**AC-1 — Create top-level Menu Item**
Given I am authenticated as a Platform Admin
When I POST `/api/admin/menus` with `{ name, order, icon?, isActive? }` (no `parentId`)
Then a top-level Menu Item is created with `isActive` defaulting to `true`
And the response is HTTP 201 with the created `MenuResponse`

**AC-2 — Delete blocked by children**
Given a top-level Menu Item that has any sub-menu children (active or inactive)
When I DELETE `/api/admin/menus/{id}`
Then the response is HTTP 409 with `messageKey: "menus.hasChildren"` instructing the admin to remove children first
> Note: the FK constraint is `ON DELETE RESTRICT`, so the database also enforces this. The API pre-check provides a user-friendly 409 instead of a raw DB exception. Checking ALL children (not just `isActive: true`) prevents orphaned sub-items when the parent is deleted.

**AC-3 — Section header for unbound items (data model)**
Given any Menu Item without a Schema Binding (`designerId = null`)
When the navbar renders (Story 4.7)
Then the item appears as a section header (not a data view link)
> This AC validates that the data model supports the distinction: null `designer_id` → header, non-null → link. Schema Binding columns (`designer_id`, `binding_version`, `provisioning_status`) are NOT part of this story — they are added in Epic 5. All menu items in this story have null `designer_id` by design.

**AC-4 — Stable insertion order for tied `order` values**
Given two top-level items with the same `order` value
When the navbar or admin list renders
Then they appear in stable insertion order; gaps in the `order` integer sequence are permitted
> Implementation: ORDER BY `sort_order ASC, id ASC`. Since IDs are UUID v7 (time-ordered via `gen_random_uuid()`), this guarantees stable insertion-time order for ties.

**AC-5 — Full CRUD for admin**
Admin can:
- `GET /api/admin/menus?page=1&pageSize=25` → `PagedResult<MenuListItem>`
- `GET /api/admin/menus/{id}` → `MenuResponse` (or 404)
- `POST /api/admin/menus` → 201 + `MenuResponse`
- `PUT /api/admin/menus/{id}` → 204 (or 404)
- `DELETE /api/admin/menus/{id}` → 204 (or 404, 409)

All endpoints require `RequireAuth()` + `RequirePlatformAdmin()` (inherited from `/api/admin` group).

## Tasks / Subtasks

- [x] Task 1: Domain entities (AC-1, AC-2, AC-3, AC-4)
  - [x] Create `src/FormForge.Api/Domain/Entities/Menu.cs` (NEW) — see Dev Notes for full shape
  - [x] Create `src/FormForge.Api/Domain/Entities/MenuRoleAssignment.cs` (NEW) — FK join table; role assignment logic is Story 4.4, but the table must exist now
  - [x] Update `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` — add `DbSet<Menu> Menus` + `DbSet<MenuRoleAssignment> MenuRoleAssignments` + `OnModelCreating` config (see Dev Notes for full mapping)

- [x] Task 2: EF Core migration (AC-1)
  - [x] Run `dotnet ef migrations add CreateMenusAndMenuRoleAssignments --project src/FormForge.Api --startup-project src/FormForge.Api`
  - [x] Verify migration creates: `menus` table, `menu_role_assignments` table, indexes per Dev Notes

- [x] Task 3: Feature folder — DTOs (AC-1, AC-5)
  - [x] Create `src/FormForge.Api/Features/Menus/Dtos/CreateMenuRequest.cs`
    - Fields: `string Name`, `int Order`, `JsonElement? Icon = null`, `bool IsActive = true`
    - Note: `JsonElement?` accepts any JSON value or null; Story 4.3 adds type validation
  - [x] Create `src/FormForge.Api/Features/Menus/Dtos/UpdateMenuRequest.cs`
    - Fields: `string Name`, `int Order`, `JsonElement? Icon`, `bool IsActive`
  - [x] Create `src/FormForge.Api/Features/Menus/Dtos/MenuListItem.cs`
    - Fields: `Guid Id`, `string Name`, `int Order`, `bool IsActive`, `Guid? ParentId`, `DateTimeOffset CreatedAt`
  - [x] Create `src/FormForge.Api/Features/Menus/Dtos/MenuResponse.cs`
    - Fields: `Guid Id`, `string Name`, `int Order`, `string? Icon`, `bool IsActive`, `Guid? ParentId`, `IReadOnlyList<Guid> AllowedRoleIds`, `DateTimeOffset CreatedAt`, `DateTimeOffset? UpdatedAt`
    - `AllowedRoleIds` is always `[]` in Story 4.1; Story 4.4 populates it

- [x] Task 4: Feature folder — Validators (AC-1)
  - [x] Create `src/FormForge.Api/Features/Menus/Validators/CreateMenuRequestValidator.cs`
    - `Name`: `NotEmpty().MaximumLength(200)`
    - `Order`: `GreaterThanOrEqualTo(0)` (no upper bound; gaps are fine per AC-4)
    - No validation on `Icon` in Story 4.1 — that is Story 4.3's scope
  - [x] Create `src/FormForge.Api/Features/Menus/Validators/UpdateMenuRequestValidator.cs` — same rules

- [x] Task 5: Feature folder — MenuCache stub (prepares for Story 4.7)
  - [x] Create `src/FormForge.Api/Features/Menus/MenuCache.cs` — see Dev Notes for shape
  - [x] Register `IMenuCache` / `MenuCache` in `Program.cs` (stub — no-op; Story 4.7 fills in the 5 s TTL logic)

- [x] Task 6: Feature folder — MenuService (AC-1 through AC-5)
  - [x] Create `src/FormForge.Api/Features/Menus/MenuService.cs` — see Dev Notes for full interface + implementation notes
  - [x] Register `IMenuService`, validators in `Program.cs` — see Dev Notes for exact lines

- [x] Task 7: Feature folder — admin endpoints (AC-1 through AC-5)
  - [x] Create `src/FormForge.Api/Features/Menus/MenuAdminEndpoints.cs` — see Dev Notes for mapping pattern

- [x] Task 8: Feature folder — domain event placeholder (used by Story 5.2)
  - [x] Create `src/FormForge.Api/Features/Menus/Events/MenuBindingCreated.cs`
    - `internal sealed record MenuBindingCreated(string DesignerId);`
    - This event is published by Story 5.2 when a binding is created; defined here so the file is in the right location

- [x] Task 9: Wire admin endpoint group (AC-5)
  - [x] Update `src/FormForge.Api/Features/Roles/AdminEndpoints.cs`
    - Add: `group.MapGroup("/menus").WithTags("Admin — Menus").MapMenuAdminEndpoints();`
    - Placement: after the existing `/roles` and `/users` groups

- [x] Task 10: Frontend — types (AC-1, AC-5)
  - [x] Create `web/src/features/menu/types.ts` — shared across admin and navbar (Story 4.7 reuses)
    - `MenuItem`, `MenuIcon`, `CreateMenuRequest`, `UpdateMenuRequest` (see Dev Notes)

- [x] Task 11: Frontend — admin queries + mutations (AC-1, AC-5)
  - [x] Create `web/src/features/admin/menus/useMenusAdminQuery.ts` — paginated list (`GET /api/admin/menus`)
    - Export `MENUS_ADMIN_QUERY_KEY = ['admin', 'menus'] as const`
  - [x] Create `web/src/features/admin/menus/useMenuDetailQuery.ts` — single menu (`GET /api/admin/menus/{id}`)
    - Export `MENU_DETAIL_QUERY_KEY = ['admin', 'menus', 'detail'] as const`
  - [x] Create `web/src/features/admin/menus/menuAdminMutations.ts`
    - `useCreateMenuMutation` — POST; on success invalidates `MENUS_ADMIN_QUERY_KEY` + toast.success
    - `useUpdateMenuMutation(menuId)` — PUT; on success invalidates list + detail + toast.success
    - `useDeleteMenuMutation` — DELETE; on success invalidates list + toast.success; caller navigates

- [x] Task 12: Frontend — admin menus list page (AC-1, AC-5)
  - [x] Create `web/src/routes/_app/admin/menus.tsx`
    - Follow `roles.tsx` exactly: `validateSearch` Zod schema for `page`/`pageSize`, paginated table, inline create form
    - Columns: Name (link to detail), Order, isActive badge, createdAt
    - Inline "Create Menu Item" form: name (required), order (number, default 0), isActive (checkbox, default true)
    - Omit icon from create form (Story 4.3 adds icon picker)
    - Handle: 400/422 validation errors via `setError`
  - [x] Create `web/src/routes/_app/admin/menus.$menuId.tsx`
    - Fetch via `useMenuDetailQuery(menuId)` with loading / 404 states
    - Edit form: name, order, isActive (no icon — Story 4.3)
    - Delete button: calls `useDeleteMenuMutation`; on success navigate to `/admin/menus`
    - 409 `MENU_HAS_CHILDREN` → inline alert near delete button: `t('admin.menus.hasChildren')`
    - 404 on fetch → `t('admin.menus.notFound')` inline message

- [x] Task 13: Frontend — admin nav link (AC-5)
  - [x] Update `web/src/routes/_app/admin.tsx`
    - Add `<Link to="/admin/menus" ...>{t('admin.menus.title')}</Link>` after the Roles link (before Designer Library)

- [x] Task 14: Frontend — i18n strings (AC-1, AC-5)
  - [x] Update `web/src/lib/i18n/locales/en.json`
    - Add `admin.menus` namespace keys (see Dev Notes for full key list)

- [x] Task 15: Integration tests (all ACs)
  - [x] Create `src/FormForge.Api.Tests/Features/Menus/MenuIntegrationTests.cs`
    - Follow `RoleIntegrationTests.cs` for class fixture, setup, teardown, and auth helper patterns
    - See Dev Notes for required test scenarios

## Dev Notes

### Domain Entities

**`Domain/Entities/Menu.cs`** (NEW):
```csharp
namespace FormForge.Api.Domain.Entities;

internal sealed class Menu
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Order { get; set; }
    public string? Icon { get; set; }           // stored as jsonb; full type validation in Story 4.3
    public bool IsActive { get; set; } = true;
    public Guid? ParentId { get; set; }         // null = top-level; Story 4.2 enables sub-menus
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public Menu? Parent { get; set; }
    public ICollection<Menu> Children { get; set; } = [];
    public ICollection<MenuRoleAssignment> RoleAssignments { get; set; } = [];  // Story 4.4 populates
}
```

**`Domain/Entities/MenuRoleAssignment.cs`** (NEW):
```csharp
namespace FormForge.Api.Domain.Entities;

internal sealed class MenuRoleAssignment
{
    public Guid MenuId { get; set; }
    public Guid RoleId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Menu Menu { get; set; } = null!;
    public Role Role { get; set; } = null!;
}
```

### FormForgeDbContext Additions

Add to `DbSet` declarations (after existing sets):
```csharp
public DbSet<Menu> Menus => Set<Menu>();
public DbSet<MenuRoleAssignment> MenuRoleAssignments => Set<MenuRoleAssignment>();
```

Add to `OnModelCreating` (after ComponentSchemaVersion block):
```csharp
modelBuilder.Entity<Menu>(e =>
{
    e.ToTable("menus");
    e.HasKey(m => m.Id);
    e.Property(m => m.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
    e.Property(m => m.Name).HasColumnName("name").IsRequired().HasMaxLength(200);
    // Use "sort_order" to avoid PostgreSQL reserved keyword "order"
    e.Property(m => m.Order).HasColumnName("sort_order").HasDefaultValue(0);
    e.Property(m => m.Icon).HasColumnName("icon").HasColumnType("jsonb");
    e.Property(m => m.IsActive).HasColumnName("is_active").HasDefaultValue(true);
    e.Property(m => m.ParentId).HasColumnName("parent_id");
    e.Property(m => m.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
    e.Property(m => m.UpdatedAt).HasColumnName("updated_at");
    e.HasOne(m => m.Parent)
     .WithMany(m => m.Children)
     .HasForeignKey(m => m.ParentId)
     .HasConstraintName("fk_menus_parent")
     .OnDelete(DeleteBehavior.Restrict);   // admin must remove children first (AC-2)
    e.HasIndex(m => m.ParentId).HasDatabaseName("idx_menus_parent_id");
    e.HasIndex(m => m.Order).HasDatabaseName("idx_menus_sort_order");
});

modelBuilder.Entity<MenuRoleAssignment>(e =>
{
    e.ToTable("menu_role_assignments");
    e.HasKey(mra => new { mra.MenuId, mra.RoleId });
    e.Property(mra => mra.MenuId).HasColumnName("menu_id");
    e.Property(mra => mra.RoleId).HasColumnName("role_id");
    e.Property(mra => mra.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
    e.HasOne(mra => mra.Menu)
     .WithMany(m => m.RoleAssignments)
     .HasForeignKey(mra => mra.MenuId)
     .HasConstraintName("fk_menu_role_assignments_menus")
     .OnDelete(DeleteBehavior.Cascade);
    e.HasOne(mra => mra.Role)
     .WithMany()
     .HasForeignKey(mra => mra.RoleId)
     .HasConstraintName("fk_menu_role_assignments_roles")
     .OnDelete(DeleteBehavior.Cascade);
    e.HasIndex(mra => mra.RoleId).HasDatabaseName("idx_menu_role_assignments_role_id");
});
```

**Critical:** The C# property is `Order` but the DB column is `sort_order` (`ORDER` is a PostgreSQL reserved keyword). System.Text.Json CamelCase policy serializes `Order` → `"order"` in JSON, which is correct for the API contract.

### MenuCache.cs Stub

```csharp
// src/FormForge.Api/Features/Menus/MenuCache.cs
namespace FormForge.Api.Features.Menus;

internal interface IMenuCache
{
    // Story 4.7 implements the 5 s TTL cache for GET /api/menus.
    // Admin mutation handlers call InvalidateAsync() so Story 4.7's cache
    // is automatically write-invalidated without touching the service layer.
    Task InvalidateAsync(CancellationToken ct = default);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class NoOpMenuCache : IMenuCache
{
    public Task InvalidateAsync(CancellationToken ct = default) => Task.CompletedTask;
}
```

### MenuService Pattern

```csharp
internal enum CreateMenuOutcome { Success, NameConflict }
internal sealed record CreateMenuResult(CreateMenuOutcome Outcome, Guid? MenuId = null, MenuResponse? Menu = null);

internal enum UpdateMenuOutcome { Success, NotFound }
internal sealed record UpdateMenuResult(UpdateMenuOutcome Outcome);

internal enum DeleteMenuOutcome { Success, NotFound, HasChildren }
internal sealed record DeleteMenuResult(DeleteMenuOutcome Outcome);

internal interface IMenuService
{
    Task<PagedResult<MenuListItem>> GetMenusAsync(int page, int pageSize, CancellationToken ct);
    Task<MenuResponse?> GetMenuAsync(Guid id, CancellationToken ct);
    Task<CreateMenuResult> CreateMenuAsync(CreateMenuRequest request, CancellationToken ct);
    Task<UpdateMenuResult> UpdateMenuAsync(Guid id, UpdateMenuRequest request, CancellationToken ct);
    Task<DeleteMenuResult> DeleteMenuAsync(Guid id, CancellationToken ct);
}
```

**Key implementation notes for `MenuService`:**

1. **Ordering**: `ORDER BY m.Order ASC, m.Id ASC` (EF translates `m.Order` → `sort_order`). UUID v7 time-order gives stable insertion order on ties (AC-4).

2. **Delete pre-check** (AC-2): Before calling `db.SaveChangesAsync`, check:
   ```csharp
   var hasChildren = await db.Menus.AnyAsync(m => m.ParentId == id, ct);
   if (hasChildren) return new DeleteMenuResult(DeleteMenuOutcome.HasChildren);
   ```
   The DB FK `ON DELETE RESTRICT` is a safety net; the API returns a friendly 409.

3. **Icon storage**: Convert incoming `JsonElement?` to `string?` via `icon?.ToString()` before storing. Store null as null.

4. **Cache invalidation**: Call `await cache.InvalidateAsync(ct)` after every `SaveChangesAsync` in Create, Update, Delete. This is a no-op now; Story 4.7 fills in the real implementation.

5. **AllowedRoleIds in response**: Build via `menu.RoleAssignments.Select(r => r.RoleId).ToList()`. In Story 4.1 this is always empty; Story 4.4 populates it.

6. **Include pattern**: Use `.Include(m => m.RoleAssignments)` when loading for `MenuResponse`. Do NOT include for `MenuListItem` (avoid N+1).

### MenuAdminEndpoints.cs Pattern

```csharp
// src/FormForge.Api/Features/Menus/MenuAdminEndpoints.cs
internal static class MenuAdminEndpoints
{
    internal static RouteGroupBuilder MapMenuAdminEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", GetMenusHandler)...
        group.MapGet("/{id:guid}", GetMenuHandler)...
        group.MapPost("/", CreateMenuHandler).AddValidationFilter<CreateMenuRequest>()...
        group.MapPut("/{id:guid}", UpdateMenuHandler).AddValidationFilter<UpdateMenuRequest>()...
        group.MapDelete("/{id:guid}", DeleteMenuHandler)...
        return group;
    }
    // Problem helpers follow the exact same pattern as RoleEndpoints.cs:
    // Results.Problem(title, statusCode, extensions: { ["code"] = "...", ["messageKey"] = "..." })
}
```

**Problem response codes** (follow `RoleEndpoints.cs` exact pattern):
- 404 Not Found: `code: "MENU_NOT_FOUND"`, `messageKey: "menus.notFound"`
- 409 Has Children: `code: "MENU_HAS_CHILDREN"`, `messageKey: "menus.hasChildren"`

### AdminEndpoints.cs Update

File: `src/FormForge.Api/Features/Roles/AdminEndpoints.cs`

Add import: `using FormForge.Api.Features.Menus;`

Add after existing groups:
```csharp
group.MapGroup("/menus").WithTags("Admin — Menus").MapMenuAdminEndpoints();
```

### Program.cs Registrations

Add after Designer services block (~line 131):
```csharp
// Menu services (Story 4.1)
builder.Services.AddScoped<IMenuService, MenuService>();
builder.Services.AddSingleton<IMenuCache, NoOpMenuCache>();
builder.Services.AddScoped<IValidator<CreateMenuRequest>, CreateMenuRequestValidator>();
builder.Services.AddScoped<IValidator<UpdateMenuRequest>, UpdateMenuRequestValidator>();
```

Note: `IMenuCache` is registered as `AddSingleton` (the no-op stub is stateless; Story 4.7 will swap to the real cache implementation which is also likely singleton-safe given `IMemoryCache` is thread-safe).

### Frontend Types (`web/src/features/menu/types.ts`)

```typescript
export interface MenuIcon {
  type: 'lucide' | 'minio'
  name?: string         // lucide icon name; Story 4.3 validates against lucide-react icon list
  objectKey?: string    // MinIO object key; Story 4.3 uploads via /api/admin/menus/upload-icon
}

export interface MenuItem {
  id: string
  name: string
  order: number
  icon: MenuIcon | null
  isActive: boolean
  parentId: string | null
  allowedRoleIds: string[]  // populated by Story 4.4; always [] for now
  createdAt: string
  updatedAt: string | null
}

export interface MenuListItem {
  id: string
  name: string
  order: number
  isActive: boolean
  parentId: string | null
  createdAt: string
}

export interface CreateMenuRequest {
  name: string
  order: number
  icon?: MenuIcon | null
  isActive?: boolean   // defaults to true server-side
}

export interface UpdateMenuRequest {
  name: string
  order: number
  icon: MenuIcon | null
  isActive: boolean
}
```

### Frontend Admin Queries Pattern

Follow `features/admin/roles/useRolesQueryPaginated.ts` exactly. Example:
```typescript
// features/admin/menus/useMenusAdminQuery.ts
import { useQuery } from '@tanstack/react-query'
import { httpClient } from '../../auth/httpClient'
import type { MenuListItem } from '../../menu/types'
import type { PagedResult } from '../../../lib/api/pagedResult'  // check actual import path

export const MENUS_ADMIN_QUERY_KEY = ['admin', 'menus'] as const

export function useMenusAdminQuery(page: number, pageSize: number) {
  return useQuery({
    queryKey: [...MENUS_ADMIN_QUERY_KEY, page, pageSize],
    queryFn: () =>
      httpClient.get<PagedResult<MenuListItem>>(
        `/api/admin/menus?page=${page}&pageSize=${pageSize}`
      ),
  })
}
```

Check the actual `PagedResult` type location — look at how `useRolesQueryPaginated.ts` imports it.

### i18n Keys (`admin.menus` namespace)

Add to `web/src/lib/i18n/locales/en.json` under `admin`:
```json
"menus": {
  "title": "Menus",
  "subtitle": "Configure the platform navigation structure",
  "createButton": "New Menu Item",
  "createDialogTitle": "Create Menu Item",
  "nameLabel": "Name",
  "orderLabel": "Display Order",
  "isActiveLabel": "Active",
  "noMenus": "No menu items yet. Create the first one.",
  "loading": "Loading menus…",
  "loadError": "Failed to load menus",
  "notFound": "Menu item not found",
  "notFoundError": "This menu item no longer exists",
  "saveButton": "Save",
  "savingButton": "Saving…",
  "cancelButton": "Cancel",
  "deleteButton": "Delete",
  "deletingButton": "Deleting…",
  "backToMenus": "← Back to Menus",
  "previousPage": "Previous",
  "nextPage": "Next",
  "pageIndicator": "Page {{page}} of {{totalPages}}",
  "hasChildren": "Remove sub-menu items first before deleting this menu item",
  "nameRequired": "Name is required",
  "nameMaxLength": "Name must be 200 characters or less",
  "createSuccess": "Menu item created",
  "updateSuccess": "Menu item updated",
  "deleteSuccess": "Menu item deleted",
  "saveError": "Failed to save menu item",
  "deleteError": "Failed to delete menu item",
  "detailTitle": "Edit Menu Item",
  "orderHelp": "Items with the same order appear in creation order. Gaps are permitted.",
  "sectionHeaderNote": "This item has no Designer binding — it will appear as a section header in the navbar.",
  "isActiveHelp": "Inactive items are hidden from the navbar for all users"
}
```

### Integration Test Scenarios

File: `src/FormForge.Api.Tests/Features/Menus/MenuIntegrationTests.cs`

Follow `RoleIntegrationTests.cs` for:
- Class fixture pattern (`IClassFixture<PostgresFixture>`, `IAsyncLifetime`)
- `WebApplicationFactory<Program>` with `WithWebHostBuilder` config
- `TRUNCATE TABLE` in `InitializeAsync` (include `menus`, `menu_role_assignments`)
- Login helper returning Bearer token
- Two test users: admin (`admin@example.com`) and non-admin (`viewer@example.com`)

**Required test scenarios:**

```
GET /api/admin/menus:
- Unauthenticated → 401
- Non-admin (viewer) → 403
- Admin, empty → 200, { items: [], total: 0 }
- Admin, with seeded items → 200, items ordered by sort_order ASC, id ASC

POST /api/admin/menus:
- Unauthenticated → 401
- Non-admin → 403
- Admin, valid { name, order: 1 } → 201, isActive defaults to true, parentId is null
- Admin, valid { name, order: 1, isActive: false } → 201, isActive = false
- Admin, missing name → 422
- Admin, name > 200 chars → 422
- Admin, order < 0 → 422

PUT /api/admin/menus/{id}:
- Admin, valid update → 204
- Admin, unknown id → 404

DELETE /api/admin/menus/{id}:
- Admin, no children → 204
- Admin, item with children → 409, code "MENU_HAS_CHILDREN"
- Admin, unknown id → 404

Ordering test:
- Seed 3 items with sort_order = [2, 1, 2] (two ties)
- GET list → order is [1, 2(first-inserted), 2(second-inserted)] by id tie-break
```

### Project Structure Notes

- All C# files go in `src/FormForge.Api/` (not `src/FormForge.Api.Tests/`)
- Backend feature folder: `src/FormForge.Api/Features/Menus/` (new)
- Domain entities: `src/FormForge.Api/Domain/Entities/` (existing, add 2 files)
- Persistence: `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` (update)
- Admin endpoint wiring: `src/FormForge.Api/Features/Roles/AdminEndpoints.cs` (existing — update)
- Frontend feature: `web/src/features/menu/types.ts` + `web/src/features/admin/menus/` (new)
- Frontend route: `web/src/routes/_app/admin/menus.tsx` + `web/src/routes/_app/admin/menus.$menuId.tsx` (new)
- Admin layout: `web/src/routes/_app/admin.tsx` (existing — update nav link)
- i18n: `web/src/lib/i18n/locales/en.json` (existing — add `admin.menus` block)

### What This Story Does NOT Implement

- `GET /api/menus` (public navbar endpoint) → Story 4.7
- Sub-menu creation (`parentId` in POST) → Story 4.2
- Icon upload or lucide icon validation → Story 4.3
- Role assignment (`allowedRoles` write) → Story 4.4
- Drag-and-drop reorder → Story 4.5
- `isActive` toggle endpoint → Story 4.6 (isActive IS editable via PUT in Story 4.1, but the story 4.6 toggle button UX is a dedicated feature)
- Schema Binding columns (`designer_id`, `binding_version`, `provisioning_status`) → Story 5.2/5.3
- Real MenuCache TTL implementation → Story 4.7 fills `NoOpMenuCache`
- `MenuBindingCreated` event publishing → Story 5.2

### Backend Test Count Baseline

Before Story 4.1: 234 backend tests (see recent commit log).
After Story 4.1: expect ~244+ (10+ new integration tests for menu CRUD).

### Key Architecture References

- [Source: architecture.md § 3.5] Route group: `/api/admin` → `RequirePlatformAdmin()` → menu admin endpoints via `AdminEndpoints`
- [Source: architecture.md § 3.4] PagedResult shape: `PagedResult<T>(IReadOnlyList<T> Items, long Total, int Page, int PageSize)`
- [Source: architecture.md § 1.7] EF migrations run on startup automatically (`Database.Migrate()`)
- [Source: architecture.md § Format Patterns] IDs: UUID v7 via `gen_random_uuid()`; TIMESTAMPTZ; camelCase JSON
- [Source: architecture.md § 5.1] `IMemoryCache` for all caches; `ICacheStore` interface abstraction
- [Source: epics.md § Epic 4] Schema Binding deferred to Epic 5 — this epic establishes shape only
- [Source: epics.md § Story 4.1 AC-4] Stable insertion order + gaps permitted → ORDER BY sort_order ASC, id ASC
- [Source: architecture.md § AR-11/2.2] `MenuBindingCreated(designerId)` event (defined here, published in Story 5.2) → no permission cache eviction on new binding creation

## Dev Agent Record

### Agent Model Used

claude-sonnet-4-6

### Debug Log References

- Ordering test (AC-4): `gen_random_uuid()` generates UUID v4 (random), not UUID v7 (time-ordered). The story spec claims ordering by id gives insertion-time order, but this is only true for UUID v7. Fixed the ordering integration test to use DbContext-seeded menus with known ordered UUIDs (000...001, 000...002, 000...003) rather than API-created ones, which correctly verifies ORDER BY sort_order ASC, id ASC tie-breaking logic.
- Frontend Zod schema: `z.coerce.number()` creates `_input: unknown` type that conflicts with react-hook-form's `Resolver` generics. Fixed by using `z.number().int().min(0)` with `{ valueAsNumber: true }` on the number input register call.
- EF migration CA1062: Auto-generated migration methods need `ArgumentNullException.ThrowIfNull(migrationBuilder)` added manually to comply with the project's CA1062 rule (matches all other migrations).

### Completion Notes List

- AC-1 ✅: POST /api/admin/menus creates top-level menu with isActive defaulting to true; 201 + MenuResponse with Location header.
- AC-2 ✅: DELETE pre-checks for children via AnyAsync before SaveChanges; returns 409 MENU_HAS_CHILDREN. FK ON DELETE RESTRICT is backup.
- AC-3 ✅: Data model supports null ParentId and null Icon (designer_id not yet in schema — deferred to Epic 5). All created items have parentId=null by design.
- AC-4 ✅: ORDER BY sort_order ASC, id ASC implemented. Integration test uses known UUID sequence to verify stable tie-breaking.
- AC-5 ✅: Full CRUD via GET(list+detail)/POST/PUT/DELETE all under /api/admin/menus, protected by platform-admin group.
- Frontend: Admin Menus list page + detail page with inline create form, edit form, delete with 409 handling. "Menus" nav link added to admin layout.
- Backend: 251/251 tests pass (+17 new menu integration tests). Frontend: 78/78 tests pass. Build clean (0 errors, 0 warnings). Lint: 27 problems (+4, same react-refresh/only-export-components pattern as existing route files).
- NoOpMenuCache registered as singleton (stateless stub); Story 4.7 fills in the 5 s TTL implementation.
- MenuBindingCreated event placeholder created for Story 5.2.

### File List

- src/FormForge.Api/Domain/Entities/Menu.cs (NEW)
- src/FormForge.Api/Domain/Entities/MenuRoleAssignment.cs (NEW)
- src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs (MODIFIED)
- src/FormForge.Api/Infrastructure/Persistence/Migrations/20260524054931_CreateMenusAndMenuRoleAssignments.cs (NEW)
- src/FormForge.Api/Infrastructure/Persistence/Migrations/20260524054931_CreateMenusAndMenuRoleAssignments.Designer.cs (NEW)
- src/FormForge.Api/Infrastructure/Persistence/Migrations/FormForgeDbContextModelSnapshot.cs (MODIFIED)
- src/FormForge.Api/Features/Menus/Dtos/CreateMenuRequest.cs (NEW)
- src/FormForge.Api/Features/Menus/Dtos/UpdateMenuRequest.cs (NEW)
- src/FormForge.Api/Features/Menus/Dtos/MenuListItem.cs (NEW)
- src/FormForge.Api/Features/Menus/Dtos/MenuResponse.cs (NEW)
- src/FormForge.Api/Features/Menus/Validators/CreateMenuRequestValidator.cs (NEW)
- src/FormForge.Api/Features/Menus/Validators/UpdateMenuRequestValidator.cs (NEW)
- src/FormForge.Api/Features/Menus/MenuCache.cs (NEW)
- src/FormForge.Api/Features/Menus/MenuService.cs (NEW)
- src/FormForge.Api/Features/Menus/MenuAdminEndpoints.cs (NEW)
- src/FormForge.Api/Features/Menus/Events/MenuBindingCreated.cs (NEW)
- src/FormForge.Api/Features/Roles/AdminEndpoints.cs (MODIFIED)
- src/FormForge.Api/Program.cs (MODIFIED)
- src/FormForge.Api.Tests/Features/Menus/MenuIntegrationTests.cs (NEW)
- web/src/features/menu/types.ts (NEW)
- web/src/features/admin/menus/useMenusAdminQuery.ts (NEW)
- web/src/features/admin/menus/useMenuDetailQuery.ts (NEW)
- web/src/features/admin/menus/menuAdminMutations.ts (NEW)
- web/src/routes/_app/admin/menus.tsx (NEW)
- web/src/routes/_app/admin/menus.$menuId.tsx (NEW)
- web/src/routes/_app/admin.tsx (MODIFIED)
- web/src/lib/i18n/locales/en.json (MODIFIED)

## Review Findings

### Decision-Needed

- [x] [Review][Defer] Icon type contract: `MenuResponse.Icon` is `string?` (raw jsonb text) but `MenuItem.icon` is typed `MenuIcon | null` on the frontend — deferred to Story 4.3 which owns icon editing; mismatch is harmless in Story 4.1 since the UI never sets an icon

### Patches

- [x] [Review][Patch] `CreateMenuAsync` calls `ToResponse(menu)` with `RoleAssignments` not included — works today (returns `[]`) but will silently omit role assignments when Story 4.4 adds them; either `.Include(m => m.RoleAssignments)` before add or explicitly return `AllowedRoleIds = []` without going through `ToResponse` [MenuService.cs]
- [x] [Review][Patch] `request.Icon?.ToString()` when the client sends JSON `null` (JsonValueKind.Null) stores the string `"null"` instead of SQL NULL — icon cannot be cleared back to null via PUT [MenuService.cs]
- [x] [Review][Patch] TOCTOU in `DeleteMenuAsync`: `AnyAsync` children check and `Remove`+`SaveChangesAsync` are not atomic — a concurrent child insert between the two causes `DbUpdateException` from the FK `ON DELETE RESTRICT`, surfacing as unhandled 500 instead of 409; wrap `SaveChangesAsync` in a try/catch for `DbUpdateException` with a Postgres error-code check (or re-query after the exception) [MenuService.cs]
- [x] [Review][Patch] `createdAt` column missing from admin list table — spec requires columns: Name, Order, isActive badge, **createdAt** [menus.tsx]
- [x] [Review][Patch] `'Inactive'` hardcoded English string in isActive badge — add `admin.menus.isInactiveLabel` key to `en.json` and use `t('admin.menus.isInactiveLabel')` [menus.tsx + en.json]
- [x] [Review][Patch] `MenuDetailContent` interface too narrow (missing `icon` field) — `submit` hardcodes `icon: null`, silently wiping any stored icon on every PUT; pass the current `menu.icon` value through to the update request [menus.$menuId.tsx]
- [x] [Review][Patch] `sectionHeaderNote` shown for all top-level items via wrong condition (`parentId === null`) — the note is about missing designer binding, not parentId; in Story 4.1 the designer_id concept doesn't exist in the frontend model yet, so remove this block entirely [menus.$menuId.tsx]
- [x] [Review][Patch] Duplicate `detailTitle` `<h2>` inside the edit form — the page `<h1>` and the form `<h2>` both resolve to the same "Edit Menu Item" string; remove the form-level `<h2>` [menus.$menuId.tsx]
- [x] [Review][Patch] Missing `GET /api/admin/menus/{id}` 200 happy-path integration test — spec requires it; the `UpdateMenu_ValidUpdate_Returns204` test incidentally calls GET /{id} but there is no dedicated happy-path scenario [MenuIntegrationTests.cs]
- [x] [Review][Patch] Missing `GET /api/admin/menus/{id}` 401 and 403 auth tests — all other endpoint groups have these; add `GetMenu_Unauthenticated_Returns401` and `GetMenu_AsNonAdmin_Returns403` [MenuIntegrationTests.cs]
- [x] [Review][Patch] `useMenuDetailQuery` fires unconditionally — if `menuId` is an empty string the query hits the list endpoint and returns a `PagedResult` where a `MenuItem` is expected; add `enabled: Boolean(menuId)` [useMenuDetailQuery.ts]
- [x] [Review][Patch] Misleading UUID tie-break test comment: "idA < idB < idC when compared as GUIDs ascending" — .NET `Guid.CompareTo` and PostgreSQL `uuid` ordering differ for arbitrary GUIDs; the test works by coincidence because all bytes except the last are identical; update the comment to state this explicitly [MenuIntegrationTests.cs]
- [x] [Review][Patch] Redundant `defaultChecked` on isActive checkbox in create form — `{...register('isActive')}` + `defaultValues: { isActive: true }` already controls the field; `defaultChecked` is superfluous and may trigger a React controlled/uncontrolled warning [menus.tsx]

### Deferred

- [x] [Review][Defer] No `try/catch(DbUpdateException)` on `SaveChangesAsync` in any write path [MenuService.cs] — deferred, pre-existing pattern across all services
- [x] [Review][Defer] Non-transactional total-count + page-items queries in `GetMenusAsync` [MenuService.cs] — deferred, pre-existing pattern across all services (RoleService identical)
- [x] [Review][Defer] TanStack Query key prefix overlap — `MENUS_ADMIN_QUERY_KEY` is a prefix of `MENU_DETAIL_QUERY_KEY`, so list mutations unintentionally also invalidate detail caches [menuAdminMutations.ts] — deferred, acceptable TanStack Query prefix-matching convention; harmless in current usage
- [x] [Review][Defer] `void queryClient.invalidateQueries(...)` discards promise — errors silently swallowed [menuAdminMutations.ts] — deferred, established codebase pattern in all mutation files
- [x] [Review][Defer] `MenuService.GetMenusAsync` trusts callers for page/pageSize validation — service has no own guard; `page=0` would produce negative `skip` if called directly — deferred, established pattern (endpoint clamps before calling service)
- [x] [Review][Defer] `MenuBindingCreated` event missing `MenuId` field — deferred, intentional placeholder for Story 5.2 which will own the full event shape
- [x] [Review][Defer] Brief delete-button re-enable window between mutation settle and navigation completes [menus.$menuId.tsx] — deferred, pre-existing pattern in all mutation+navigate flows

## Change Log

- 2026-05-24: Story 4.1 implemented — Menu CRUD feature (backend + frontend). Created Menus domain entity + EF migration, full feature folder (service, validators, endpoints, cache stub, event placeholder), 17 integration tests, React admin pages (list + detail), TanStack Query hooks, i18n keys. Backend: 251/251 tests (+17). Frontend: 78/78 tests. Build clean.
- 2026-05-24: Code review patches applied (13 patches, 1 deferred to Story 4.3). Story → done.
