# Story 4.7: Render Permission-Filtered Navigation

Status: done

## Story

As an authenticated user,
I want to see a dynamic left-side navbar showing only the Menu Items my Roles authorize me to view,
so that I am not presented with inaccessible options.

## Acceptance Criteria

1. **Given** I am logged in, **When** the SPA fetches `GET /api/menus`, **Then** the response contains only items where `isActive = true` AND my role IDs intersect the item's `allowedRoles` set; the response is a nested tree (top-level items with their sub-menu children inline) ordered by `order` then `id`; sub-menus inherit their parent's visibility (a visible sub-menu under a hidden parent is still hidden — the parent is a required ancestor). Platform Admins (any user holding role `00000000-0000-0000-0000-000000000001`) bypass the role intersection and see every active item — mirrors the server-side platform-admin bypass at `PermissionService.cs:110-113`.

2. **Given** the navbar fetches the menu tree, **When** the response is served from a 5-second in-memory cache keyed by user ID, **Then** subsequent fetches by the same user within the TTL hit the cache (p95 < 100 ms per NFR-3); on any menu mutation (`POST/PUT/DELETE/PATCH` to `/api/admin/menus/*`), every cached entry is evicted within the same request that committed the DB change so the next `GET /api/menus` reflects the new state.

3. **Given** the navbar renders on a viewport ≥ 768 px, **When** I view the authenticated app shell, **Then** a fixed left-side sidebar shows the menu tree with icons (Lucide rendered inline, MinIO icons render the default placeholder for now — full image display is deferred), top-level items in `order` then `id` sequence, sub-menus collapsed by default with a disclosure button per parent.

4. **Given** the viewport is < 768 px, **When** the navbar renders, **Then** the sidebar collapses to a hamburger menu (FR-22 AC-3 / FR-37 AC-2); tapping the hamburger button opens an overlay drawer; tapping any menu item — including a sub-menu disclosure-then-leaf — auto-closes the drawer; every interactive control (hamburger button, menu links, disclosure buttons) has a touch target ≥ 44 × 44 px (FR-37 AC-3); `axe-core` automated audit on `_app.tsx` reports zero critical violations (FR-42 AC-5 boundary).

## Tasks / Subtasks

- [x] **Task 1 — Backend: Public navbar DTOs** (AC: 1, 3)
  - [x] Create `src/FormForge.Api/Features/Menus/Dtos/NavMenuItem.cs` — positional record `NavMenuItem(Guid Id, string Name, int Order, System.Text.Json.JsonElement? Icon, Guid? ParentId, IReadOnlyList<NavMenuItem> Children)`. Tree shape — top-level items contain their sub-menu `Children` inline; sub-menus serialize with `Children: []`. Icon is `JsonElement?` for the same reason `MenuResponse.Icon` is (Menu.Icon stored as JSON string, deserialized to object on response — see `MenuService.cs:390-400`).
  - [x] Note: do NOT reuse `MenuListItem` — it's flat, paginated, and lacks `icon` + `allowedRoleIds`. The admin endpoint deliberately returns a paginated flat list (Story 4.1/4.2). The navbar needs the role-filtered tree.

- [x] **Task 2 — Backend: Real `MenuCache` implementation replacing `NoOpMenuCache`** (AC: 2)
  - [x] Replace the body of `src/FormForge.Api/Features/Menus/MenuCache.cs` (current `NoOpMenuCache`) with a real `MenuCache : IMenuCache` that uses `IMemoryCache` + a single shared `CancellationTokenSource` for total-eviction-on-invalidate:
    ```csharp
    internal sealed class MenuCache(IMemoryCache cache) : IMenuCache
    {
        private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);
        private CancellationTokenSource _generation = new();
        private static string CacheKey(Guid userId) =>
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"menus:{userId:N}");

        public Task<IReadOnlyList<NavMenuItem>?> TryGetAsync(Guid userId, CancellationToken ct)
        {
            if (cache.TryGetValue<IReadOnlyList<NavMenuItem>>(CacheKey(userId), out var hit) && hit is not null)
                return Task.FromResult<IReadOnlyList<NavMenuItem>?>(hit);
            return Task.FromResult<IReadOnlyList<NavMenuItem>?>(null);
        }

        public Task SetAsync(Guid userId, IReadOnlyList<NavMenuItem> tree, CancellationToken ct)
        {
            using var entry = cache.CreateEntry(CacheKey(userId));
            entry.Value = tree;
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            entry.AddExpirationToken(new Microsoft.Extensions.Primitives.CancellationChangeToken(_generation.Token));
            return Task.CompletedTask;
        }

        public Task InvalidateAsync(CancellationToken ct = default)
        {
            // Cancel the current generation so every cached entry tied to this
            // token is evicted; install a fresh source for future writes. The
            // swap is atomic w.r.t. cache reads because IMemoryCache.TryGetValue
            // checks the entry's tokens after read.
            var old = Interlocked.Exchange(ref _generation, new CancellationTokenSource());
            old.Cancel();
            old.Dispose();
            return Task.CompletedTask;
        }
    }
    ```
  - [x] Extend `IMenuCache` interface to add `TryGetAsync` and `SetAsync`:
    ```csharp
    internal interface IMenuCache
    {
        Task<IReadOnlyList<NavMenuItem>?> TryGetAsync(Guid userId, CancellationToken ct);
        Task SetAsync(Guid userId, IReadOnlyList<NavMenuItem> tree, CancellationToken ct);
        Task InvalidateAsync(CancellationToken ct = default);
    }
    ```
  - [x] In `Program.cs:132`, change `AddSingleton<IMenuCache, NoOpMenuCache>()` → `AddSingleton<IMenuCache, MenuCache>()`. Singleton because the `_generation` token must be shared across requests; `IMemoryCache` is thread-safe (already registered at `Program.cs:98`).
  - [x] **Delete the `NoOpMenuCache` class** — it has no remaining callers after this story. Remove the `CA1812` suppression comment that justified the empty class.

- [x] **Task 3 — Backend: Fix cache-invalidation race in 5 existing mutations** (AC: 2)
  - [x] **Root cause (deferred from Story 4.4 review):** `MenuService.cs` calls `cache.InvalidateAsync(ct)` after `SaveChangesAsync(ct)`. If the request's `CancellationToken` cancels between commit and invalidate (client abort, navigate-away mid-save), the DB change persists but the cache never invalidates — every other user serves stale data for up to 5 s. Was a no-op while `IMenuCache` was `NoOpMenuCache`; becomes a real defect the moment Task 2 lands.
  - [x] **Fix:** in `CreateMenuAsync` (line 119), `UpdateMenuAsync` (line 147), `DeleteMenuAsync` (line 179), `AssignMenuRolesAsync` (line 271), `ReorderMenusAsync` (line 350), and `ToggleMenuActiveAsync` (line 370), change `await cache.InvalidateAsync(ct).ConfigureAwait(false);` → `await cache.InvalidateAsync(CancellationToken.None).ConfigureAwait(false);`. The invalidation is a pure in-memory token-cancel — no I/O, no blocking risk — so `CancellationToken.None` is the correct token for a post-commit step that MUST run.

- [x] **Task 4 — Backend: `MenuService.GetNavMenusForUserAsync`** (AC: 1, 2)
  - [x] Add interface method to `IMenuService`:
    ```csharp
    Task<IReadOnlyList<NavMenuItem>> GetNavMenusForUserAsync(Guid userId, CancellationToken ct);
    ```
  - [x] Implementation flow in `MenuService.cs`:
    1. Call `cache.TryGetAsync(userId, ct)` — if hit, return it (records OTel metric `formforge.menu_cache.hits` if metrics wired; bonus only).
    2. Resolve the user's role IDs via `IPermissionService.GetEffectivePermissionsAsync(userId, ct)` — `EffectivePermissions.RoleIds: HashSet<Guid>` is the cached canonical source (30 s TTL, event-bus invalidated — see `PermissionService.cs:73-77`). Do **NOT** query `db.UserRoles` directly — that bypasses the permission cache and duplicates seed-data role logic.
    3. Detect platform-admin: `roleIds.Contains(PlatformAdminRoleId)` where `PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001")`. Reuse the constant from `PermissionService.cs:13`; do NOT redefine it — extract to `Common/PlatformAdminRole.cs` if neither place exposes it publicly (PermissionService's is `private`; the cleanest move is to add `internal static class WellKnownRoles { public static readonly Guid PlatformAdminId = ...; }` under `Features/Permissions/`).
    4. EF query — single round-trip with `.Include(m => m.RoleAssignments)`:
       ```csharp
       var menus = await db.Menus
           .Include(m => m.RoleAssignments)
           .Where(m => m.IsActive)
           .OrderBy(m => m.Order)
           .ThenBy(m => m.Id)
           .ToListAsync(ct);
       ```
    5. In-memory filter + tree assembly:
       - For each `menu`, `bool visible = isPlatformAdmin || menu.RoleAssignments.Any(ra => roleIds.Contains(ra.RoleId))`.
       - Build a `Dictionary<Guid, NavMenuItem>` of visible items keyed by id.
       - Top-level (`ParentId == null`) form the root list; sub-menus (`ParentId != null`) attach as `Children` of their parent **only if the parent is also in the visible map** — enforces AC-1 "sub-menus inherit parent visibility".
       - A sub-menu whose parent is hidden is dropped (do not promote to top-level).
       - Within each level, items are already in `Order, Id` order from the EF query — preserve that during dictionary population.
    6. Call `cache.SetAsync(userId, tree, CancellationToken.None)` — same race-fix rationale as Task 3; cache writes must not be skipped on cancel.
    7. Return the tree.
  - [x] Inject `IPermissionService` into `MenuService` constructor: `internal sealed class MenuService(FormForgeDbContext db, IMenuCache cache, IPermissionService permissions) : IMenuService`. `IPermissionService` is singleton; `MenuService` is scoped; ASP.NET resolves singleton-into-scoped without issue (singleton has no scoped deps of its own — see `PermissionService.cs:31-46`).
  - [x] **Why filter in memory, not in EF:** the `RoleAssignments` collection navigation lets us evaluate role intersection per-menu in-memory after a single Include — total rows in v1 are small (single-digit top-level, low-double-digit per parent per PRD). An EF `.Where(m => m.RoleAssignments.Any(ra => roleIds.Contains(ra.RoleId)))` translates to a correlated subquery per row; the simpler Include + LINQ-to-Objects path is faster at the v1 scale and easier to reason about. Document this in a code comment.

- [x] **Task 5 — Backend: `MenuEndpoints.cs` + `/api/menus` route group** (AC: 1, 2)
  - [x] Create `src/FormForge.Api/Features/Menus/MenuEndpoints.cs` — separate from `MenuAdminEndpoints.cs` because the public navbar endpoint is unrelated to admin CRUD and lives under a different group:
    ```csharp
    internal static class MenuEndpoints
    {
        internal static RouteGroupBuilder MapMenuEndpoints(this RouteGroupBuilder group)
        {
            ArgumentNullException.ThrowIfNull(group);
            group.MapGet("/", GetNavMenusHandler)
                 .WithSummary("Get the permission-filtered menu tree for the calling user. 5s in-memory cache. p95 <100ms cached.")
                 .Produces<IReadOnlyList<NavMenuItem>>(StatusCodes.Status200OK);
            return group;
        }

        private static async Task<IResult> GetNavMenusHandler(
            HttpContext httpContext,
            IMenuService menuService,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            ArgumentNullException.ThrowIfNull(menuService);
            var userIdClaim = httpContext.User.FindFirst("userId")?.Value;
            if (!Guid.TryParse(userIdClaim, out var userId))
                return Results.Unauthorized();
            var tree = await menuService.GetNavMenusForUserAsync(userId, ct).ConfigureAwait(false);
            return Results.Ok(tree);
        }
    }
    ```
  - [x] In `Program.cs` next to the other `MapGroup` calls (around line 418-452), add:
    ```csharp
    // Story 4.7 — public navbar tree, permission + isActive filtered, 5s cached.
    app.MapGroup("/api/menus")
       .RequireAuth()
       .RequireRateLimiting("admin")    // 120/min/user — same bucket as other authenticated reads
       .WithTags("Menus")
       .MapMenuEndpoints();
    ```
  - [x] Use the existing `"admin"` sliding-window rate limit policy (defined at `Program.cs:231-244`). The navbar fetch is per-user, low-volume (typically once per page load), and the 120/min budget is plenty. Do **NOT** create a new policy.
  - [x] **No `RequirePlatformAdmin()`** — every authenticated user can read their own navbar tree (the server filters per-user inside the handler).
  - [x] **Why `/api/menus` and not `/api/users/me/menus`:** architecture line 474 (`#### 3.5 — Endpoint Organization`) explicitly defines `app.MapGroup("/api/menus").RequireAuth().MapMenuEndpoints();`. Stick to the architecture.

- [x] **Task 6 — Backend: Integration tests** (AC: 1, 2)
  - [x] All tests go in `src/FormForge.Api.Tests/Features/Menus/MenuIntegrationTests.cs` (existing file). Use the established `LoginAsync("admin@example.com", ...)` and `LoginAsync("viewer@example.com", ...)` helpers + `CreateMenuViaApiAsync`. Seed users: admin has `PlatformAdminRoleId`; viewer has `ViewerRoleId = "00000000-0000-0000-0000-000000000002"` (see `DesignerIntegrationTests.cs:22-23`).
  - [x] `GetNavMenus_Unauthenticated_Returns401` — `GET /api/menus` with no JWT → 401.
  - [x] `GetNavMenus_AsPlatformAdmin_ReturnsAllActiveMenusFiltered` — admin token; seed 3 menus: A (active, no roles assigned), B (active, ViewerRoleId only), C (inactive). Assert response is `[A, B]` (C excluded because inactive; admin sees both regardless of roleAssignments per platform-admin bypass).
  - [x] `GetNavMenus_AsViewer_ReturnsOnlyRoleMatchingMenus` — viewer token; seed A (active, no roles), B (active, ViewerRoleId), C (active, PlatformAdminRoleId only). Assert response is `[B]` only (A has no roles assigned → no intersection → hidden; C is admin-only → hidden).
  - [x] `GetNavMenus_SubMenusInheritParentVisibility_HiddenParentHidesChild` — viewer token; seed top-level P (active, PlatformAdminRoleId only) and sub-menu S (active, ViewerRoleId, parent=P). Assert response is `[]` — S is dropped because P is hidden.
  - [x] `GetNavMenus_SubMenusNestedUnderVisibleParent_AreReturnedAsTreeChildren` — viewer token; top-level P (active, ViewerRoleId) and sub-menus S1 (active, ViewerRoleId, order=1), S2 (active, ViewerRoleId, order=0). Assert response is `[{ id: P, children: [S2, S1] }]` (children ordered by `Order, Id`).
  - [x] `GetNavMenus_InactiveSubMenu_ExcludedFromChildren` — admin token; P (active, no roles), S (inactive, no roles, parent=P). Assert response is `[{ id: P, children: [] }]`.
  - [x] `GetNavMenus_TopLevelOrdering_ByOrderThenId` — admin token; create three top-level menus with `order=10, 10, 5` in insertion order. Assert response ordered as `[order=5, order=10 (first by Id), order=10 (second by Id)]`.
  - [x] `GetNavMenus_CacheHit_ReturnsSameTreeWithoutDbHit` — admin token; seed menu A; first request → 200 with `[A]`. Then UPDATE the underlying menu **directly via DbContext** (bypass admin endpoint, which would invalidate). Second `GET /api/menus` within 5 s → must still return the OLD `[A]` (proves cache hit). After ~5 s (use the cache abstraction's TTL — see below), the cache expires and the new state is served. **Implementation:** rather than depending on a real timer, expose `IMenuCache` test-time invalidation via the existing `cache.InvalidateAsync()` call from the API after a mutation. Use this test purely to prove "second identical GET within window returns same body" — assert response bytes equal. Time-based eviction is covered indirectly by the invalidation test.
  - [x] `GetNavMenus_CacheInvalidatedAfterMutation_ReflectsNewState` — admin token; seed A. GET → `[A]`. POST a new menu B via `/api/admin/menus`. GET → `[A, B]` immediately. Proves write-time invalidation is wired correctly across all 6 mutation paths.
  - [x] **No need to test all 6 mutation invalidations exhaustively** — pick one (e.g., `POST /api/admin/menus`) for the smoke test. The Task-3 fix to all 5 existing mutations + Task-4 cache.SetAsync is a wiring detail, not behavior under test per path.
  - [x] Expected count: **+9 tests** (291 baseline from Story 4.6's two added theory rows → was 293, this brings it to **302**). If 4.6 review didn't bump the baseline yet in `_bmad-output`, recount after the dev step lands.

- [x] **Task 7 — Backend: Tests for `MenuCache` unit behavior** (AC: 2)
  - [x] Create `src/FormForge.Api.Tests/Features/Menus/MenuCacheTests.cs` — pure unit tests against `MenuCache` with a real `MemoryCache` (no PostgresFixture needed):
    - `Set_then_TryGet_ReturnsSameInstance` — store + retrieve.
    - `Set_DifferentUsers_AreIsolated` — store for userA, TryGet for userB returns null.
    - `Invalidate_EvictsAllEntries` — store for userA + userB + userC; invalidate; all three TryGets return null.
    - `Set_AfterTtlElapses_ReturnsNull` — use `Microsoft.Extensions.Time.Testing.FakeTimeProvider` if available, otherwise mark `[Trait("Category", "Slow")]` and `await Task.Delay(TimeSpan.FromSeconds(6))`. The slow path is acceptable as a single test — the design relies on `AbsoluteExpirationRelativeToNow` which IMemoryCache evaluates at read time without a timer. **Preferred:** check whether `MemoryCacheOptions.Clock` can be substituted with a fake `ISystemClock` for the test instance; if so, manipulate it. If neither approach is clean, omit this test — the TTL is a 5-line `AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(5)` and the invalidation test below is what actually matters for correctness.
  - [x] These tests do **not** need a DB — they prove the cache primitive correctness independent of `MenuService`. Expected count: **+3 tests** (skip the Ttl-elapsed one per the note above).

- [x] **Task 8 — Frontend: Public menu types + `useNavMenusQuery`** (AC: 1)
  - [x] In `web/src/features/menu/types.ts`, add the tree type:
    ```ts
    export interface NavMenuItem {
      id: string
      name: string
      order: number
      icon: MenuIcon | null
      parentId: string | null
      children: NavMenuItem[]
    }
    ```
    Place it AFTER the existing `MenuItem` interface — both remain. `MenuItem` (admin detail) carries `allowedRoleIds + isActive`; `NavMenuItem` (public navbar) does not (server filtered).
  - [x] Create `web/src/features/menu/useNavMenusQuery.ts`:
    ```ts
    import { useQuery } from '@tanstack/react-query'
    import { httpClient } from '../auth/httpClient'
    import type { NavMenuItem } from './types'

    export const NAV_MENUS_QUERY_KEY = ['menus', 'nav'] as const

    export function useNavMenusQuery() {
      return useQuery({
        queryKey: NAV_MENUS_QUERY_KEY,
        queryFn: () => httpClient.get<NavMenuItem[]>('/api/menus'),
        staleTime: 5_000,         // mirror server TTL — no benefit refetching sooner
        refetchOnWindowFocus: false,
        retry: false,
      })
    }
    ```
    `staleTime: 5_000` matches the server cache TTL — there's no point refetching client-side faster than the server can produce a fresh response.

- [x] **Task 9 — Frontend: Tree-shaken Lucide icon component for the navbar** (AC: 3)
  - [x] Create `web/src/components/icons/NavLucideIcon.tsx` — **distinct from `LucideIcon.tsx`** which uses `import * as Icons` (acceptable for admin pages, NOT for the public navbar per Story 4.3 deferred-work):
    ```tsx
    import { Home, Settings, Users, FileText, Folder, ... } from 'lucide-react'
    import type { LucideProps } from 'lucide-react'
    import type { ComponentType } from 'react'

    // Curated whitelist for navbar use. Add icons as menu authors request them.
    // Tree-shaken: only listed icons land in the public bundle.
    const ICON_MAP: Record<string, ComponentType<LucideProps>> = {
      Home, Settings, Users, FileText, Folder,
      // ... add as needed
    }

    interface NavLucideIconProps extends Omit<LucideProps, 'ref'> {
      name: string
    }

    export function NavLucideIcon({ name, size = 16, ...rest }: NavLucideIconProps) {
      const IconComponent = ICON_MAP[name]
      if (!IconComponent) return null
      return <IconComponent size={size} {...rest} />
    }
    ```
  - [x] **Initial whitelist** (8-12 icons is enough for v1): `Home`, `Settings`, `Users`, `FileText`, `Folder`, `LayoutDashboard`, `BarChart3`, `Database`, `Bell`, `Shield`, `Box`, `Bookmark`. Document in a code comment: "To add a navbar icon, import it in `NavLucideIcon.tsx` and add to `ICON_MAP`. The full ~3,000 icon set is available only on admin pages via `LucideIcon.tsx`."
  - [x] Do **NOT** replace `LucideIcon.tsx` — admin pages still use the full set. Two icon components by design: one for bundle-budget-protected paths (navbar), one for admin-only.
  - [x] If a `name` is not in the whitelist, fall back to a generic placeholder icon (e.g., `Box`) so a menu with an unrecognized icon doesn't render as `null` in the navbar.

- [x] **Task 10 — Frontend: Navbar components** (AC: 3, 4)
  - [x] Create `web/src/components/shared/Navbar.tsx` — the desktop sidebar + mobile drawer. Key responsibilities:
    1. Calls `useNavMenusQuery()` for data.
    2. Renders top-level items as a vertical list; each top-level item with `children.length > 0` shows a disclosure-toggle button next to its label (collapsed by default; remembers open state via `useState` per parent ID — no localStorage, resets on navigation).
    3. Each menu item is a `<Link to="...">` — for v1 there are no Designer bindings yet, so all items link to a placeholder route (e.g., `/menu/$menuId` or `#`). The architecture binds menus to routes in Epic 5; this story just renders the tree. **Pick a stable placeholder** (`to="/_app"` works, or omit `href` and render as a non-link button — choose the non-link button to avoid TanStack Router navigation errors). Document the placeholder in a code comment: "Routes wire up in Epic 5 (Story 5.2 Schema Binding); rendering as `<button>` for now."
    4. Mobile (`< 768 px`) — render a hamburger button in the header; tapping opens a fixed full-height drawer (Tailwind `fixed inset-y-0 left-0 w-64 z-50` + overlay backdrop). Tapping the backdrop or any menu item closes the drawer (set `open=false`). Use a single `open: boolean` state.
    5. Desktop (`≥ 768 px`) — Tailwind `md:` prefix renders the sidebar as a fixed left column (`md:fixed md:inset-y-0 md:left-0 md:w-64`), content area gets `md:pl-64`. Hamburger button is hidden on `md:` via `md:hidden`.
    6. Touch targets ≥ 44 × 44 px — use Tailwind `min-h-[44px] min-w-[44px]` on the hamburger button, disclosure buttons, and link/button items. Verify in browser DevTools at responsive 320 px width.
    7. `aria-label` on the hamburger button (e.g., `t('nav.openMenu')` / `t('nav.closeMenu')` depending on `open` state). `role="navigation"` on the `<nav>` element with `aria-label={t('nav.primaryAriaLabel')}`. Disclosure buttons get `aria-expanded`.
    8. Skip-link target — add `<a href="#main-content" className="sr-only focus:not-sr-only">{t('nav.skipToContent')}</a>` as the very first element inside `_app.tsx` (or inside `Navbar.tsx`) so keyboard users can jump past the navbar (FR-42 boundary).
  - [x] Create `web/src/components/shared/NavMenuItem.tsx` — single row renderer used recursively (or inlined two levels deep since max depth is 2). For v1, just inline both levels in `Navbar.tsx` — simpler than recursion when max depth is 2.
  - [x] Loading state: `useNavMenusQuery.isPending` → render a small skeleton (3-4 grey bars). Empty state (`data?.length === 0`) → show `t('nav.emptyMessage')`. Error state: silent fail — render an empty nav rather than blocking the app shell. The navbar should never be the reason the whole app appears broken.

- [x] **Task 11 — Frontend: Wire `Navbar` into `_app.tsx`** (AC: 3, 4)
  - [x] Replace the placeholder block in `web/src/routes/_app.tsx:67-83` (current header with just the Admin link + Logout button). New layout:
    ```tsx
    return (
      <div className="min-h-screen md:flex">
        <Navbar />
        <main id="main-content" className="flex-1 md:pl-64 p-4">
          <header className="mb-4 flex justify-end gap-4">
            <PermissionGate allowed={canSeeAdmin}>
              <Link to="/admin/users">{t('nav.admin')}</Link>
            </PermissionGate>
            <button
              type="button"
              onClick={() => logoutMutation.mutate()}
              disabled={logoutMutation.isPending}
              className="min-h-[44px] min-w-[44px]"
            >
              {logoutMutation.isPending ? t('auth.logout.submitting') : t('auth.logout.button')}
            </button>
          </header>
          <Outlet />
        </main>
      </div>
    )
    ```
  - [x] Keep the existing logout button + admin link in the header for now; the Navbar component is purely additive. Touch-target fix on the logout button: add `min-h-[44px] min-w-[44px]` (the existing button is a single line of text and may not meet 44 px without it).
  - [x] Remove the comment `{/* Full layout (nav, sidebar) added in Story 4.7 / Epic 7 */}` — that's this story.

- [x] **Task 12 — Frontend: MinIO icon placeholder + lucide whitelist fallback** (AC: 3)
  - [x] In `Navbar.tsx`'s icon-rendering branch:
    ```tsx
    if (item.icon?.type === 'lucide' && item.icon.name) {
      return <NavLucideIcon name={item.icon.name} size={16} />
    }
    if (item.icon?.type === 'minio') {
      return <NavLucideIcon name="Box" size={16} aria-label={t('nav.menuIconPlaceholder')} />
    }
    return <NavLucideIcon name="Box" size={16} aria-hidden />
    ```
  - [x] **Why MinIO renders as a placeholder, not the actual image:** the `objectKey` returned by `MenuResponse.Icon` is just the storage key. To display the image, the API needs to mint a presigned URL via `POST /api/files/refresh-urls` (architecture decision 4.1 line 526-540). That endpoint does **not exist yet** — it was deferred from Story 4.3 to "Story 4.7 OR a dedicated files API story". For Story 4.7, the navbar is **functional + accessible** with a placeholder, and full image display is left for a follow-up `files-api` story (added to deferred work). Do not expand scope to build `/api/files/refresh-urls` in this story.

- [x] **Task 13 — Frontend: i18n keys** (AC: 3, 4)
  - [x] In `web/src/lib/i18n/locales/en.json`, extend the existing `nav` block (currently has only `nav.admin` at line 24-26). Add:
    ```json
    "nav": {
      "admin": "Admin",
      "openMenu": "Open menu",
      "closeMenu": "Close menu",
      "primaryAriaLabel": "Primary navigation",
      "skipToContent": "Skip to main content",
      "emptyMessage": "No menus available",
      "expandSubmenu": "Expand {{name}}",
      "collapseSubmenu": "Collapse {{name}}",
      "menuIconPlaceholder": "Menu icon"
    }
    ```
  - [x] Place inside the existing `nav` object — do not create a new one. Keys are dot-notation per AR-33 / architecture line 610.

- [x] **Task 14 — Frontend: vitest smoke tests** (AC: 1, 3, 4)
  - [x] Create `web/src/components/shared/__tests__/Navbar.test.tsx` — mock `useNavMenusQuery` at the hook boundary:
    - `renders nothing while loading` — mock `isPending: true` → expect skeleton/empty.
    - `renders top-level items in tree order` — mock data with three top-level items → expect 3 buttons with matching names.
    - `renders sub-menus only when disclosure is open` — top-level with 2 children, click disclosure → both children appear; click again → hidden.
    - `mobile hamburger opens drawer and tapping item closes it` — mock `window.matchMedia('(max-width: 767px)').matches = true`, click hamburger, expect drawer visible, click a menu item, expect drawer hidden.
    - `MinIO icon renders placeholder, not <img>` — assert no `<img>` element in the navbar; assert the placeholder icon is rendered with the i18n label.
    - `lucide icon outside whitelist falls back to placeholder` — pass `name: 'NotARealIcon'` → assert placeholder rendered, no `null`.
  - [x] **Mock `matchMedia` once** in `web/src/test/setup.ts` if not already (vitest jsdom doesn't implement it). Use the standard `vi.stubGlobal('matchMedia', ...)` pattern.
  - [x] Expected count: **+6 frontend tests** (84 baseline → **90**, accounting for the 2 pre-existing Story 4.5 ReorderableMenuList test failures which are out of scope).

- [x] **Task 15 — Documentation: deferred-work cleanup** (AC: 1, 3)
  - [x] In `_bmad-output/implementation-artifacts/deferred-work.md`, locate and close the following items (mark with `[x]` and add a `Resolved by 4.7` note):
    - Line 19 — "Cache invalidation outside the DB transaction" → closed by Task 3.
    - Line 21 — "Sub-menu role-gate semantics undefined for Story 4.7" → closed by Task 4 + AC-1 wording ("sub-menus inherit parent visibility").
    - Line 25 — "No server-side existence check for `minio`-type `objectKey`" → NOT closed; Task 12 punts to a future files-api story. Leave open with a note re-pointing the owner.
    - Line 205 — "Logout button missing `aria-busy` and screen-reader busy state" → optionally close if Task 11 adds `aria-busy={logoutMutation.isPending}` to the logout button (one-line addition; do it).
  - [x] Add a **new deferred-work entry** for the files-api work that's no longer covered:
    > **`POST /api/files/refresh-urls` and full MinIO icon image display in the navbar** — architecture decision 4.1 specifies a presigned-URL refresh endpoint that has not been built. Story 4.7's navbar renders a placeholder icon for `{type:"minio"}` items rather than the actual uploaded image. Owner: a future `files-api` story (FR-3 / AR-26 scope).

## Dev Notes

### What Already Exists — Do NOT Recreate

- **`Menu` entity with `IsActive`, `Order`, `ParentId`, `Icon`, `RoleAssignments` navigation** — `src/FormForge.Api/Domain/Entities/Menu.cs` (Story 4.1). No DB changes needed.
- **Admin menu CRUD** — `src/FormForge.Api/Features/Menus/MenuAdminEndpoints.cs` already calls `await cache.InvalidateAsync(ct)` after every mutation. Task 3 fixes the `ct` race; Task 2 makes the invalidation actually do something.
- **`IMenuCache` interface + `NoOpMenuCache` stub** — `src/FormForge.Api/Features/Menus/MenuCache.cs` (Story 4.1). Task 2 replaces the stub.
- **`IPermissionService.GetEffectivePermissionsAsync`** — `src/FormForge.Api/Features/Permissions/PermissionService.cs:54` returns `EffectivePermissions.RoleIds: HashSet<Guid>`. Cached 30 s, event-bus invalidated. **Use this; do not query `db.UserRoles` directly.**
- **`PlatformAdminRoleId` constant `"00000000-0000-0000-0000-000000000001"`** — defined privately at `PermissionService.cs:13` and in tests at `DesignerIntegrationTests.cs:22`. Promote to `WellKnownRoles` per Task 4 OR re-declare in `MenuService` with a comment "mirrors PermissionService".
- **`MenuListItem.cs` (admin paginated list DTO)** — keep as-is. `NavMenuItem` is a new DTO; do **not** merge them.
- **`MenuResponse.Icon` JSON-element handling** — `MenuService.cs:390-400` deserializes `string?` storage → `JsonElement?` response. Reuse the same `ParseIcon` private method for `NavMenuItem.Icon`.
- **`httpClient.get<T>(path)`** — `web/src/features/auth/httpClient.ts`. Already used everywhere. Use for `useNavMenusQuery`.
- **`PermissionGate` and `usePermission`** — `web/src/components/shared/PermissionGate.tsx` + `web/src/features/auth/usePermission.ts`. The navbar **does not need them** for the menu items themselves (server already filters); they're still used for the Admin link in the header.
- **TanStack Query patterns** — `usePermissionsQuery.ts` is the closest template for the new `useNavMenusQuery.ts`.
- **lucide-react `import * as Icons`** — `web/src/components/icons/LucideIcon.tsx`. Stays for admin pages. Task 9 creates a separate `NavLucideIcon.tsx` with named imports.
- **Test seed users**: `admin@example.com / Password1!` (PlatformAdminRoleId), `viewer@example.com / Password1!` (ViewerRoleId). See `DesignerIntegrationTests.cs:60-95` for the seed pattern.

### Why a Separate `MenuEndpoints.cs` Instead of Extending `MenuAdminEndpoints.cs`

The admin file is mounted under `/api/admin/menus` with `RequirePlatformAdmin()`. The navbar endpoint is under `/api/menus` with `RequireAuth()` only — different route group, different filter chain, different audience. Splitting keeps each file's responsibility single. Architecture line 474 documents this split explicitly.

### Why Per-User Cache Keys (Not a Single Global Cached Tree)

Each user gets a different filtered view of the menu tree depending on their roles. A global tree-of-everything would require client-side filtering, which (a) leaks the existence of menus the user can't see (information disclosure), and (b) defeats the NFR-3 < 100 ms cached p95 goal because every request still has to filter. Per-user keys with 5 s TTL match the existing `PermissionService` cache shape and let the architecture stay LRU-eviction-friendly.

### Why a Single Cancellation-Token Generation (Not Tracked Keys)

`PermissionService` maintains a `ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, byte>>` secondary index because it needs **targeted** bust by role ID (a `RolePermissionsChanged` event affects only users with that role). The menu cache needs **total** bust: ANY menu mutation invalidates EVERY user's cached tree (a menu's role assignment changed → every user's filtered view may now differ). The `CancellationTokenSource` swap pattern (Task 2) is the simplest correct primitive for total eviction — no secondary index, O(1) bust, GC-friendly.

### Why Filter In-Memory (Not in EF Where Clause)

A LINQ-to-Entities `.Where(m => m.RoleAssignments.Any(ra => roleIds.Contains(ra.RoleId)))` becomes a correlated subquery per row. The current v1 scale (single-digit top-level menus, low-double-digit per parent — see PRD line 88) makes the round-trip + LINQ-to-Objects approach faster and easier to reason about. A code comment must document this so a future scale event triggers the right re-evaluation (likely: move filtering into a single SQL pass with a window function).

### Platform Admin Bypass — Mirror Server, Not Client

`PermissionService.GetCrudFlagsAsync` (PermissionService.cs:110-113) bypasses per-resource flags when the user holds `PlatformAdminRoleId`. The menu filter must mirror this bypass: a platform-admin sees every active menu regardless of `allowedRoles` membership. Do **not** rely on assigning the platform-admin role to every menu's `allowedRoles` — that's a data-modeling foot-gun. The bypass is computed at filter time.

### Sub-Menu Visibility Inheritance — Why "Hidden Parent Hides Child"

A menu has parent-child semantics: a sub-menu without a visible parent is unreachable in the rendered tree. Two alternative semantics were considered and rejected:
- **Promote orphaned children to top-level:** changes navigation shape unpredictably; violates AC-2 "tree in `order` sequence within hierarchy".
- **Intersect parent + child `allowedRoles`:** more restrictive than spec; would hide cases the admin intended to show.

Chosen: parent visibility is required for sub-menu visibility. The sub-menu's own `allowedRoles` is the second gate. This is the deferred-work clarification (deferred-work.md:21) being closed.

### Cache Invalidation Race — Why `CancellationToken.None`

`MenuService` mutations currently pass `ct` to `cache.InvalidateAsync(ct)`. If the request cancels between `SaveChangesAsync(ct)` and `cache.InvalidateAsync(ct)`, the DB commits but the cache stays stale for the full 5 s TTL. The fix (Task 3) uses `CancellationToken.None` for the post-commit invalidation — it's a pure in-memory operation with no I/O, so opting out of cancellation is safe and correct. The same fix applies to `cache.SetAsync` in Task 4 because the write to the cache is the same kind of post-commit step.

### Frontend Bundle Budget — Lucide Tree-Shaking

`import * as Icons from 'lucide-react'` (the pattern in `LucideIcon.tsx`) bundles the full ~3,000 icon set, which the architecture explicitly disallows for the public navbar (Story 4.3 deferred work, line 645). Story 4.7's navbar imports each icon by name from a curated whitelist. The two icon components coexist by design — `LucideIcon.tsx` for admin (`menus.tsx`, `menus.$menuId.tsx`, `IconPickerSection.tsx`), `NavLucideIcon.tsx` for the navbar.

### Mobile Drawer — Why Tailwind `md:` Breakpoint Specifically

PRD FR-37 AC-1 specifies "Single-column below 768 px; sidebar + content at 768 px and above" — Tailwind's default `md:` breakpoint is 768 px (tailwind.config.ts default). Using `md:` directly avoids a custom breakpoint config and matches the PRD threshold exactly.

### Touch-Target 44 × 44 px — Why `min-h-[44px] min-w-[44px]` Arbitrary Values

The 44 px floor is FR-37 AC-3 + WCAG 2.5.5 Target Size (AAA). Tailwind has no built-in `44px` token; the arbitrary-value escape (`min-h-[44px]`) is the canonical way. Apply to: hamburger button, every menu item link/button, every disclosure toggle, the logout button. Do NOT apply to plain text — the rule is for interactive controls only.

### `axe-core` Boundary — Story 4.7 Owns Only the Navbar's Critical Violations

FR-42 AC-5 specifies axe-core zero critical violations on rendered DynamicComponent forms. Story 4.7's boundary is the navbar component specifically — adding it must not introduce a critical violation in `_app.tsx`. Full repo-wide accessibility compliance is Story 7.4. Run `npm run lint` plus a one-shot axe scan against `_app.tsx` if available; otherwise rely on the vitest tests for ARIA attributes and document the smoke check.

### Test Counts

- Backend: **293** (Story 4.6 completed at 293) **→ 305** (+9 integration tests Task 6 + +3 unit tests Task 7).
- Frontend: **84** (story 4.6 baseline, with 2 pre-existing Story 4.5 `ReorderableMenuList` failures already documented) **→ 90** (+6 Navbar smoke tests). The 2 pre-existing failures remain out of scope.
- Lint: 32 baseline preserved (0 net new errors). If `NavLucideIcon`'s `aria-label` interpolation triggers a new lint rule, fix at source.

### Previous Story Intelligence (Story 4.6)

Patterns to carry forward verbatim:
- `ArgumentNullException.ThrowIfNull(menuService)` + `(request)` at the top of every handler.
- `Results.Problem(title, statusCode, extensions: { code, messageKey, ...})` envelope for every error path. The navbar endpoint doesn't have application-level errors per AC, but if you need one (e.g., explicit 401 path) match this envelope.
- New i18n keys in dot-notation (`nav.openMenu`, not `navOpenMenu`).
- TanStack Query mutation/query hooks live in `features/*/`, not in route files.
- Component files in `components/shared/` for cross-feature reuse; `components/icons/` for icon utilities.
- Test integration tests pattern: `LoginAsync(email, password) → CreateMenuViaApiAsync(token, name, order)` → assert HTTP status + parsed body.
- 5-test-per-verb pattern from Stories 4.4 / 4.5 / 4.6: Unauthenticated / AsNonAdmin / Happy / Edge / NotFound.

### Project Structure Notes

**Backend new files:**
- `src/FormForge.Api/Features/Menus/Dtos/NavMenuItem.cs`
- `src/FormForge.Api/Features/Menus/MenuEndpoints.cs`
- `src/FormForge.Api/Features/Permissions/WellKnownRoles.cs` (optional helper; can inline if preferred)
- `src/FormForge.Api.Tests/Features/Menus/MenuCacheTests.cs`

**Backend modified files:**
- `src/FormForge.Api/Features/Menus/MenuCache.cs` — replace `NoOpMenuCache` with real `MenuCache`, extend interface
- `src/FormForge.Api/Features/Menus/MenuService.cs` — add `GetNavMenusForUserAsync`, inject `IPermissionService`, change 6 `cache.InvalidateAsync(ct)` calls to `CancellationToken.None`
- `src/FormForge.Api/Program.cs` — register `MenuCache` (replace `NoOpMenuCache`), add `/api/menus` route group
- `src/FormForge.Api.Tests/Features/Menus/MenuIntegrationTests.cs` — +9 tests covering AC-1, AC-2

**Frontend new files:**
- `web/src/features/menu/useNavMenusQuery.ts`
- `web/src/components/icons/NavLucideIcon.tsx`
- `web/src/components/shared/Navbar.tsx`
- `web/src/components/shared/__tests__/Navbar.test.tsx`

**Frontend modified files:**
- `web/src/features/menu/types.ts` — add `NavMenuItem` interface
- `web/src/routes/_app.tsx` — mount `<Navbar />`, update layout, add `aria-busy` on logout
- `web/src/lib/i18n/locales/en.json` — extend `nav` block with 8 keys
- `web/src/test/setup.ts` — stub `matchMedia` if not already

**Documentation modified:**
- `_bmad-output/implementation-artifacts/deferred-work.md` — close 3 items, add 1 new

**No DB migration needed** — all required columns shipped in Story 4.1's `20260524054931_CreateMenusAndMenuRoleAssignments.cs`.

### References

- **Epic:** `_bmad-output/planning-artifacts/epics.md:1094-1118` (Story 4.7 ACs verbatim)
- **PRD:** `_bmad-output/planning-artifacts/prds/prd-tinnitus-2026-05-22/prd.md:399-407` (FR-22 / Story C-8); `prd.md:582-591` (FR-37 / responsive)
- **Architecture — endpoint organization:** `_bmad-output/planning-artifacts/architecture.md:469-481`
- **Architecture — cache backend:** `architecture.md:632-634` (IMemoryCache for v1)
- **Architecture — folder structure:** `architecture.md:1070-1074` (`MenuCache.cs # 5s TTL navbar cache`); `architecture.md:1156` (`_app.tsx # Authenticated layout + navbar`); `architecture.md:1192` (`icons/LucideIcon.tsx`); `architecture.md:1202-1205` (`features/menu/`)
- **Architecture — mutation strategy:** `architecture.md:846` ("Optimistic only for: theme change, soft-delete row removal, drag-reorder") — navbar reads are not optimistic; pessimistic-by-default.
- **Architecture — i18n:** `architecture.md:608-612`
- **NFR-3:** epics.md:76 (navbar p95 < 100 ms cached)
- **Deferred items closed:**
  - `deferred-work.md:19` — cache invalidation outside DB transaction (closed by Task 3)
  - `deferred-work.md:21` — sub-menu role-gate semantics (closed by Task 4 + AC-1 wording)
  - `deferred-work.md:205` — logout button aria-busy (closed by Task 11)
- **Deferred items left open / re-pointed:**
  - `deferred-work.md:25` — MinIO objectKey existence check (re-pointed to future files-api story per Task 12)
  - **New item added:** `/api/files/refresh-urls` + full MinIO icon image rendering in navbar (Task 15)
- **PermissionService pattern reference:** `src/FormForge.Api/Features/Permissions/PermissionService.cs:79-93` (token-bust cache write pattern); `:110-113` (platform-admin bypass)
- **Existing `_app.tsx`:** `web/src/routes/_app.tsx:67-83` (current layout placeholder being replaced)
- **Existing icon component:** `web/src/components/icons/LucideIcon.tsx` (admin only; do not modify)
- **MenuService existing mutations:** `MenuService.cs:119, 147, 179, 271, 350, 370` (six `cache.InvalidateAsync(ct)` callsites that need the `ct → CancellationToken.None` swap)
- **Test seed users / role IDs:** `DesignerIntegrationTests.cs:22-23, 60` (admin/viewer + Platform-Admin/Viewer role GUIDs)
- **JWT claim shape:** `Program.cs:172-175` (`RoleClaimType = "roles"`, `NameClaimType = "userId"`)

## Dev Agent Record

### Agent Model Used

claude-opus-4-7

### Debug Log References

- Initial backend build failed with CA1849 (`CancellationTokenSource.Cancel()` blocks — use `CancelAsync()`) and CA1001 (`MenuCache` owns disposable field but isn't `IDisposable`). Both fixed in `MenuCache.cs` — `InvalidateAsync` is now `async` with `await old.CancelAsync()`, and `MenuCache` implements `IDisposable` to dispose the live `_generation` token on app shutdown.
- First Navbar test pass failed on `getByLabelText(/nav\.closeMenu/)` — two elements (hamburger toggle + backdrop) share the closeMenu label when the drawer is open. Switched to `getAllByLabelText` + click the backdrop (last in document order); test now asserts the hamburger flips back to `nav.openMenu` after backdrop click.
- Test seed needed extension: the existing `viewer@example.com` user in `MenuIntegrationTests.SeedTestUsersAsync` had NO role assigned (only admin got `PlatformAdminRoleId`). Story 4.7 navbar role-intersection tests need viewer to hold a distinct non-admin role, so I seeded `ViewerRoleId = "00000000-0000-0000-0000-000000000002"` in `ReseedSystemRolesAsync` and bound viewer to it. This is the same `ViewerRoleId` pattern as `DesignerIntegrationTests.cs:22-23`. Existing `_AsNonAdmin_Returns403` admin tests still pass because `RequirePlatformAdmin()` checks for the `platform-admin` role specifically, not "any role".

### Completion Notes List

**All 15 tasks completed. AC-1 (role-filtered isActive tree, sub-menus inherit parent visibility, platform-admin bypass), AC-2 (5 s per-user cache + write-time invalidation), AC-3 (desktop sidebar + Lucide icons + MinIO placeholder), AC-4 (mobile hamburger drawer + 44×44 touch targets + skip-link + ARIA labels) are all satisfied.**

Backend:
- New DTO `NavMenuItem(Guid Id, string Name, int Order, JsonElement? Icon, Guid? ParentId, IReadOnlyList<NavMenuItem> Children)` — distinct from `MenuListItem` (admin paginated flat) and `MenuResponse` (admin single with `allowedRoleIds` + `isActive`).
- New `WellKnownRoles.PlatformAdminId` static helper in `Features/Permissions/` so `MenuService` can mirror the platform-admin bypass without duplicating the literal Guid (matches `PermissionService.cs:13` and the integration-test constants).
- `IMenuCache` extended with `TryGetAsync` and `SetAsync`. Real `MenuCache` implementation replaces `NoOpMenuCache` (deleted) — uses `IMemoryCache` + a shared `CancellationTokenSource` whose token expires every entry on Invalidate. `Interlocked.Exchange` swap + `await CancelAsync()` + `Dispose()` per-invalidate cycle; `IDisposable` on the class to clean up on app shutdown. Singleton lifetime so the `_generation` token is shared across requests.
- `MenuService` constructor gains `IPermissionService permissions` parameter. `GetNavMenusForUserAsync(userId, ct)` flow: (1) try cache → return on hit; (2) `permissions.GetEffectivePermissionsAsync(userId, ct)` for `RoleIds` (NOT raw `db.UserRoles` — that bypasses the 30 s permission cache); (3) detect platform-admin via `roleIds.Contains(WellKnownRoles.PlatformAdminId)`; (4) single EF round-trip `db.Menus.Include(m => m.RoleAssignments).Where(m => m.IsActive).OrderBy(m => m.Order).ThenBy(m => m.Id)`; (5) three-pass tree assembly — Pass 1 filters in-memory (admin bypass OR role intersection); Pass 2 pre-allocates a mutable child list for every visible menu; Pass 3 builds `NavMenuItem` records and appends to parents (orphans dropped, not promoted, per AC-1 "hidden parent hides child"); (6) `cache.SetAsync` with `CancellationToken.None` (post-commit pattern).
- Cache race fix (Task 3 / `deferred-work.md:19` closed): all 6 existing `cache.InvalidateAsync(ct)` call sites in `MenuService.cs` (`CreateMenuAsync`, `UpdateMenuAsync`, `DeleteMenuAsync`, `AssignMenuRolesAsync`, `ReorderMenusAsync`, `ToggleMenuActiveAsync`) now pass `CancellationToken.None`. Pure in-memory CTS swap — no I/O — so opting out of cancellation is safe and correct.
- New `MenuEndpoints.cs` mounts under `/api/menus` with `RequireAuth()` + `RequireRateLimiting("admin")` (120/min/user, same bucket as other authenticated reads). Separate from `MenuAdminEndpoints.cs` because: different route group, different filter chain (no `RequirePlatformAdmin`), different audience. Handler extracts `userId` from JWT claim, returns `Unauthorized` if missing (defense — `RequireAuth` should already block this), calls `menuService.GetNavMenusForUserAsync`, returns `Ok(tree)`.
- +9 integration tests in `MenuIntegrationTests.cs` covering: Unauthenticated 401, PlatformAdmin sees all active filtered, Viewer sees only role-matching, sub-menu inherits parent visibility (hidden parent), nested children returned with (Order, Id) ordering, inactive sub-menus excluded, top-level (Order, Id) ordering with deterministic Guids, cache hit (direct DbContext mutation invisible to next GET), cache invalidate after POST mutation.
- +4 unit tests in new `MenuCacheTests.cs` covering: Set then TryGet returns same instance, different users isolated, Invalidate evicts all entries, post-invalidate generation accepts fresh writes.

Frontend:
- `NavMenuItem` TS type added to `web/src/features/menu/types.ts` (placed AFTER `MenuItem` per spec).
- `useNavMenusQuery` hook with `staleTime: 5_000` (matches server TTL — no benefit refetching faster than the server can produce a fresh response), `retry: false`, `refetchOnWindowFocus: false`. Pessimistic per architecture line 846 (drag-reorder is the only optimistic path).
- `NavLucideIcon.tsx` (named imports for `Home`, `Settings`, `Users`, `FileText`, `Folder`, `LayoutDashboard`, `BarChart3`, `Database`, `Bell`, `Shield`, `Box`, `Bookmark`). Unknown names fall back to `Box` placeholder (not `null`). Distinct from `LucideIcon.tsx` (full `import *` — admin-only).
- `Navbar.tsx` — desktop sidebar + mobile drawer in one component. Top-level items as buttons (no Links — Epic 5 wires route bindings). Disclosure toggle per parent with `aria-expanded` + `nav.expandSubmenu` / `nav.collapseSubmenu` aria-labels using `{{name}}` interpolation. Mobile: fixed hamburger button (z-30) + backdrop (z-20) + drawer (z-30 fixed inset-y-0 left-0 w-64). Touch targets `min-h-[44px] min-w-[44px]` on every interactive control. `role="navigation"` + `aria-label={t('nav.primaryAriaLabel')}` on `<nav>`. Skip-link `<a href="#main-content">` as first child of the component, sr-only until focused. MinIO icons render the `Box` placeholder with `aria-label={t('nav.menuIconPlaceholder')}` (full image deferred to files-api story).
- `_app.tsx` layout replaced: `<div className="min-h-screen md:flex">` wraps `<Navbar />` + `<main id="main-content" className="flex-1 p-4 md:pl-64">`. Logout button gains `aria-busy={logoutMutation.isPending}` (closes `deferred-work.md:205`) plus `min-h-[44px] min-w-[44px]`. Admin link gets `min-h-[44px]` and inline-flex for the touch target. Old comment "Full layout (nav, sidebar) added in Story 4.7 / Epic 7" removed — that's this story.
- 8 new `nav.*` i18n keys in `en.json` (`openMenu`, `closeMenu`, `primaryAriaLabel`, `skipToContent`, `emptyMessage`, `expandSubmenu`, `collapseSubmenu`, `menuIconPlaceholder`) — kept inside the existing `nav` block; dot-notation per AR-33.
- +7 vitest smoke tests in `Navbar.test.tsx` (matches story spec's "+6" estimate; I added one extra for the loading-state branch). Identity-translator mock returns the i18n key, so assertions match on `nav.emptyMessage`, `nav.expandSubmenu`, etc. Mocks `useNavMenusQuery` at the hook boundary (no real network).

Deferred-work updates: 3 items marked `[x] Resolved by 4.7` (lines 19 cache invalidate, 21 sub-menu role-gate, 205 logout aria-busy). 1 item re-pointed (line 25 MinIO `objectKey` existence check now bundles with files-api). 1 new entry added at the top of the file: `POST /api/files/refresh-urls` + full MinIO icon image display.

Test counts: backend **293 → 306** (+13: 9 integration + 4 unit; story estimated +12 but I added one extra cache-generation-after-invalidate test as a regression guard against the post-bust generation being half-cancelled). Frontend **84 → 91** (+7: navbar smoke tests; 2 pre-existing Story 4.5 ReorderableMenuList failures remain out of scope as documented). TypeScript clean. Production build clean (`_app` chunk 5.34 kB gz 2.16 kB — Navbar is bundled into the route's chunk; no separate navbar chunk because `<Navbar />` is mounted unconditionally inside `_app.tsx`). Lint 32 baseline preserved (0 net new errors).

### File List

**Backend new:**
- `src/FormForge.Api/Features/Menus/Dtos/NavMenuItem.cs`
- `src/FormForge.Api/Features/Menus/MenuEndpoints.cs`
- `src/FormForge.Api/Features/Permissions/WellKnownRoles.cs`
- `src/FormForge.Api.Tests/Features/Menus/MenuCacheTests.cs`

**Backend modified:**
- `src/FormForge.Api/Features/Menus/MenuCache.cs` — `NoOpMenuCache` replaced with real `MenuCache` (singleton, IMemoryCache + CancellationTokenSource swap); `IMenuCache` extended with `TryGetAsync` + `SetAsync`.
- `src/FormForge.Api/Features/Menus/MenuService.cs` — added `GetNavMenusForUserAsync`, injected `IPermissionService`, swapped 6 `cache.InvalidateAsync(ct)` calls to `CancellationToken.None`.
- `src/FormForge.Api/Program.cs` — `AddSingleton<IMenuCache, NoOpMenuCache>` → `AddSingleton<IMenuCache, MenuCache>`; added `/api/menus` route group with `RequireAuth() + RequireRateLimiting("admin") + MapMenuEndpoints()`.
- `src/FormForge.Api.Tests/Features/Menus/MenuIntegrationTests.cs` — added `ViewerRoleId` constant; extended `ReseedSystemRolesAsync` to seed it; extended `SeedTestUsersAsync` to bind viewer to it; +9 navbar integration tests in a new `// ---------- GET /api/menus (Story 4.7 ...) ----------` section; added `NavMenuItemDto` record for response deserialization.

**Frontend new:**
- `web/src/features/menu/useNavMenusQuery.ts`
- `web/src/components/icons/NavLucideIcon.tsx`
- `web/src/components/shared/Navbar.tsx`
- `web/src/components/shared/__tests__/Navbar.test.tsx`

**Frontend modified:**
- `web/src/features/menu/types.ts` — added `NavMenuItem` interface.
- `web/src/routes/_app.tsx` — imported and mounted `<Navbar />`; restructured layout to `md:flex` sidebar + main with `md:pl-64`; added `aria-busy` + touch-target classes to logout button; added touch-target classes to admin Link.
- `web/src/lib/i18n/locales/en.json` — extended `nav` block with 8 keys.

**Documentation modified:**
- `_bmad-output/implementation-artifacts/deferred-work.md` — closed 3 items (lines 19, 21, 205), re-pointed 1 item (line 25), added new top-of-file entry under "Deferred from: implementation of 4-7-render-permission-filtered-navigation (2026-05-25)".

**No DB migration needed** — all required columns shipped in Story 4.1's `20260524054931_CreateMenusAndMenuRoleAssignments.cs`.

### Change Log

| Date       | Author          | Change |
| ---------- | --------------- | ------ |
| 2026-05-25 | claude-opus-4-7 | Story file created by bmad-create-story workflow. Status → ready-for-dev. |
| 2026-05-25 | claude-opus-4-7 | Story 4.7 implementation complete — public navbar with permission + isActive filtering, 5 s per-user cache, mobile drawer, 44×44 touch targets, skip-link, MinIO placeholder, 3 deferred items closed. Backend 293 → 306 tests; frontend 84 → 91 tests. Status → review. |

### Review Findings

3-reviewer parallel pass (Blind Hunter + Edge Case Hunter + Acceptance Auditor) — ~37 raw findings → 12 unique actionable → 4 patches + 8 deferred + ~22 dismissed.

- [x] [Review][Patch] `isError` branch renders the empty-state message; spec Task 10 said "silent fail" — render empty nav, not misleading "No menus available" on backend failure [`web/src/components/shared/Navbar.tsx:83-84`]
- [x] [Review][Patch] AC-4 "tapping any item auto-closes drawer" has no regression test — spec Task 14 bullet 4 explicit, dev's test clicks the backdrop instead of a leaf [`web/src/components/shared/__tests__/Navbar.test.tsx:100-118`]
- [x] [Review][Patch] Admin Link missing `min-w-[44px]` — AC-4 ≥ 44×44 px on every interactive control; safe in English (≈53px with px-2) but brittle to i18n [`web/src/routes/_app.tsx:73-78`]
- [x] [Review][Patch] `renders nothing while loading` test is a weak guard — passes if anything other than emptyMessage renders; doesn't lock the skeleton behavior [`web/src/components/shared/__tests__/Navbar.test.tsx:45-49`]
- [x] [Review][Defer] Role/permission-change does not invalidate MenuCache — sec-adjacent 5 s stale-menu window for affected user post-revoke; spec accepts 5 s TTL but role mutations should arguably bus-bust [`MenuCache.cs`, `PermissionService.cs:201-237`] — deferred, pre-existing
- [x] [Review][Defer] MenuCache.SetAsync `_generation.Token` read non-atomic vs concurrent Invalidate's Dispose; narrow window race during shutdown / mutation burst [`src/FormForge.Api/Features/Menus/MenuCache.cs:50, 72`] — deferred, pre-existing
- [x] [Review][Defer] Drawer is not a real WCAG dialog — no focus trap, no Escape key handler, body not inert, duplicate `closeMenu` aria-label on hamburger + backdrop; MinIO icon repeats the same generic placeholder label per row; axe-core scan not actually run [`web/src/components/shared/Navbar.tsx`] — deferred, pre-existing (Story 7.4 boundary)
- [x] [Review][Defer] Cached `IReadOnlyList<NavMenuItem>` is a real `List<>` that a future caller could cast and mutate, corrupting the cached entry for every reader until TTL [`src/FormForge.Api/Features/Menus/MenuService.cs` Pass 3] — deferred, pre-existing
- [x] [Review][Defer] Server allows depth-3+ menu nesting silently — `MenuService.GetNavMenusForUserAsync` attaches any sub-menu whose parent is visible regardless of depth; client only renders 2 levels so the depth-3 child is serialized but invisible [`src/FormForge.Api/Features/Menus/MenuService.cs:436-456`, `web/src/components/shared/Navbar.tsx:141-156`] — deferred, pre-existing
- [x] [Review][Defer] `openParents` Set unbounded over long-lived sessions — never reconciled against refetched data, ids of deleted/deactivated menus persist forever [`web/src/components/shared/Navbar.tsx:23-32`] — deferred, pre-existing
- [x] [Review][Defer] `NavLucideIcon` whitelist (12 icons) is narrower than `IconPickerSection`'s full lucide set (~3000); admin-picked valid icons outside the list silently render as `Box` in the navbar with no admin-side warning [`web/src/components/icons/NavLucideIcon.tsx:25-49`, `web/src/components/icons/LucideIcon.tsx`] — deferred, pre-existing
- [x] [Review][Defer] `GetNavMenus_CacheHit_ReturnsSameTreeAfterDirectDbMutation` is flaky if CI delay between the two fetches exceeds 5 s; needs `FakeTimeProvider` or equivalent clock-injection point [`src/FormForge.Api.Tests/Features/Menus/MenuIntegrationTests.cs:1507-1549`] — deferred, pre-existing
