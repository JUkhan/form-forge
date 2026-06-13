# Story 3.2: Create New Designer

Status: done

## Story

As a Platform Admin,
I want to create a new Designer by providing a `displayName` and `designerId`,
so that I can start building a new form layout.

## Acceptance Criteria

1. **AC-1 — Create Designer (Happy Path)**
   Given I am authenticated as a Platform Admin
   When I POST `/api/designers` with `{ displayName, designerId }`
   Then the response is HTTP 201 with the created Designer (`version: 1`, `status: "Draft"`, `rootElement: null`, `createdAt`, `updatedAt: null`)
   And the `Location` response header is `/api/designers/{designerId}`.

2. **AC-2 — Invalid designerId Rejected**
   Given an invalid `designerId` (uppercase letters, leading digit, hyphen, space, length > 63, length < 1, reserved PG 17 keyword, or otherwise failing regex `^[a-z_][a-z0-9_]{0,62}$`)
   When I POST `/api/designers`
   Then the response is HTTP 422 with `code: "IDENTIFIER_INVALID"` and a `detail` field describing the specific validation failure.

3. **AC-3 — Duplicate designerId Rejected**
   Given a `designerId` that already exists in `component_schemas`
   When I POST `/api/designers` with that same `designerId`
   Then the response is HTTP 409 with `code: "DESIGNER_EXISTS"`.

4. **AC-4 — Canvas Opens After Create**
   Given a Designer is created successfully
   When the SPA navigates to `/designer/$designerId`
   Then the canvas page loads `GET /api/designers/{designerId}` and the backend responds 200
   And the response contains `rootElement: null`, `latestVersion: 1`, `status: "Draft"`
   And the canvas shows the empty drop zone ("Drop a Stack, Row, or Tabs container here to start building.") — NOT an error state.

5. **AC-5 — List Designers**
   Given one or more Designers exist
   When I GET `/api/designers?page=1&pageSize=25`
   Then the response is HTTP 200 with a `PagedResult<DesignerListItem>` using the standard AR-21 envelope
   And each item contains `designerId`, `displayName`, `status` (status of the latest version), `latestVersion`, `createdAt`, `updatedAt`.

6. **AC-6 — Get Specific Version**
   Given a Designer and version exist
   When I GET `/api/designers/{designerId}/versions/{version}`
   Then the response is HTTP 200 with full `DesignerResponse` including `rootElement`.
   When the `designerId` or `version` does not exist
   Then the response is HTTP 404 with `code: "DESIGNER_NOT_FOUND"`.

## Tasks / Subtasks

- [x] Task 1: Fix frontend API path mismatch (AC-4)
  - [x] In `web/src/features/designer/designerApi.ts` — change ALL occurrences of `/api/designer` (without 's') to `/api/designers`. This is a 4-line change touching `listSchemas`, `getSchema`, `createSchema`, `saveVersion`, `publishVersion`, `archiveVersion`, `duplicateSchema`.
  - [x] In `web/src/routes/_app/designer.library.tsx` — fix `createSchemaSchema` Zod schema:
    - Change `max(64)` to `max(63)`
    - Replace the current regex `^[a-z][a-z0-9-]*$` (allows hyphens) with `^[a-z_][a-z0-9_]{0,62}$` (underscores only, 1-63 chars) matching the architecture spec AR-4
    - Update the user-facing error message: `'Use lowercase letters, digits, and underscores. Must start with a letter or underscore. Max 63 characters.'`
  - [x] In `web/src/lib/i18n/locales/en.json` — verify missing keys under `"designer"."library"`:
    - `"designerIdConflict"`, `"statusPublished"`, `"statusDraft"`, `"statusArchived"`, `"rowMenuLabel"` already present from Story 3.1 review patches; no edits needed.

- [x] Task 2: Create `SafeIdentifier` value type (AC-2)
  - [x] Create `src/FormForge.Api/Features/Designer/SafeIdentifier.cs`

- [x] Task 3: Create PG reserved keywords list (AC-2)
  - [x] Create `src/FormForge.Api/Features/Designer/PgReservedKeywords.cs`

- [x] Task 4: Create domain entities (AC-1, AC-5, AC-6)
  - [x] Create `src/FormForge.Api/Domain/Entities/ComponentSchema.cs`
  - [x] Create `src/FormForge.Api/Domain/Entities/ComponentSchemaVersion.cs`

- [x] Task 5: Create DTOs (AC-1, AC-5, AC-6)
  - [x] Create `src/FormForge.Api/Features/Designer/Dtos/CreateDesignerRequest.cs`
  - [x] Create `src/FormForge.Api/Features/Designer/Dtos/DesignerResponse.cs`
  - [x] Create `src/FormForge.Api/Features/Designer/Dtos/DesignerListItem.cs`

- [x] Task 6: Create FluentValidation validator (AC-2)
  - [x] Create `src/FormForge.Api/Features/Designer/Validators/CreateDesignerRequestValidator.cs`

- [x] Task 7: Create `IDesignerService` + `DesignerService` (AC-1, AC-3, AC-4, AC-5, AC-6)
  - [x] Create `src/FormForge.Api/Features/Designer/DesignerService.cs`
  - [x] `CreateAsync` creates schema + version 1 in one `SaveChangesAsync`; race-window 23505 mapped to `DesignerExists`
  - [x] `ListAsync` paginated with latest-version status correlated subquery
  - [x] `GetLatestAsync` includes versions, picks latest in C#
  - [x] `GetVersionAsync` loads schema + version separately so missing-version still returns 404

- [x] Task 8: Create `DesignerEndpoints` (AC-1, AC-2, AC-3, AC-4, AC-5, AC-6)
  - [x] Create `src/FormForge.Api/Features/Designer/DesignerEndpoints.cs`
  - [x] `POST /` requires platform-admin + `AddValidationFilter<CreateDesignerRequest>()`
  - [x] `GET /`, `GET /{designerId}`, `GET /{designerId}/versions/{version:int}` open to all authenticated users
  - [x] Outcome switch + static `*Problem()` helpers (`DesignerNotFoundProblem`, `DesignerExistsProblem`)

- [x] Task 9: Update `FormForgeDbContext` (AC-1, AC-5, AC-6)
  - [x] DbSets `ComponentSchemas` and `ComponentSchemaVersions` added
  - [x] `OnModelCreating` configures both tables with FKs, unique `(designer_id, version)` index, `root_element jsonb`

- [x] Task 10: Create EF Core migration (AC-1)
  - [x] `20260523112633_CreateComponentSchemas` generates `component_schemas` (PK varchar(63)) and `component_schema_versions` (`root_element jsonb`, unique `(designer_id, version)`)

- [x] Task 11: Register services and route group in `Program.cs` (AC-1)
  - [x] `using` for `FormForge.Api.Features.Designer{,.Dtos,.Validators}` added
  - [x] `IDesignerService` + `IValidator<CreateDesignerRequest>` registered as Scoped
  - [x] `/api/designers` route group registered before `app.Run()` with `RequireAuth()` + `admin` rate limiter

- [x] Task 12: Build and verify (all ACs)
  - [x] Backend: `dotnet build FormForge.sln` — 0 errors, 0 warnings
  - [x] Frontend: `pnpm run build` — built cleanly
  - [x] Frontend: `pnpm run lint` — 26 errors, all pre-existing `react-refresh/only-export-components` (no new errors introduced)
  - [x] `web/src/routeTree.gen.ts` confirmed untouched by `git status`

## Dev Notes

### Story Scope: Full-Stack Feature — Backend is Primary

This story adds the first real backend work in Epic 3. Story 3.1 was frontend-only (porting the ESG designer with stub API calls). Story 3.2 implements the backend endpoints that make those stubs live. The frontend changes are small fixes (path correction, regex fix, missing i18n key).

The canvas page and library page already exist from Story 3.1 — do NOT recreate them. Only update the files listed above.

---

### Critical Frontend Fixes (Do These First)

**1. API Path Mismatch** — `designerApi.ts` uses `/api/designer` (no 's'). The architecture specifies `/api/designers` (with 's'). The AC stories confirm `/api/designers`. This is a typo from Story 3.1 that must be fixed before the backend is added.

```typescript
// web/src/features/designer/designerApi.ts — BEFORE (wrong)
listSchemas: (page, pageSize) => httpClient.get<...>(`/api/designer?page=...`)
createSchema: body => httpClient.post<...>('/api/designer', body)

// AFTER (correct — matches architecture + epics)
listSchemas: (page, pageSize) => httpClient.get<...>(`/api/designers?page=...`)
createSchema: body => httpClient.post<...>('/api/designers', body)
```
Fix ALL 7 lines: `listSchemas`, `getSchema` (×2), `createSchema`, `saveVersion`, `publishVersion`, `archiveVersion`, `duplicateSchema`.

**2. Frontend Zod Regex** — The library page validates `designerId` client-side with `^[a-z][a-z0-9-]*$` (allows hyphens). The architecture spec AR-4 and epics both say hyphens are INVALID: `^[a-z_][a-z0-9_]{0,62}$`. Fix the regex and the `max` length (63, not 64).

**3. Missing i18n key** — `designer.library.designerIdConflict` is referenced in the library page mutation handler but was not included in Story 3.1's i18n list. Add it to `en.json`.

---

### SafeIdentifier Value Type

The `SafeIdentifier` type validates the `designerId` at construction time so that invalid identifiers can never be passed to SQL. It is also used by Stories 5.x when constructing DDL. Implement it exactly as follows:

```csharp
// src/FormForge.Api/Features/Designer/SafeIdentifier.cs
using System.Text.RegularExpressions;

namespace FormForge.Api.Features.Designer;

/// <summary>
/// A validated PostgreSQL-safe identifier (designerId or fieldKey).
/// Re-validates on construction — raw strings are never interpolated into SQL.
/// </summary>
internal sealed class SafeIdentifier
{
    // Regex: lowercase letters, digits, underscores. Must start with letter or
    // underscore. 1–63 characters total (matching PG's NAMEDATALEN-1 limit).
    private static readonly Regex ValidPattern =
        new(@"^[a-z_][a-z0-9_]{0,62}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    public string Value { get; }

    private SafeIdentifier(string value) => Value = value;

    public static bool TryCreate(string? raw, out SafeIdentifier? result, out string? error)
    {
        result = null;
        error = null;

        if (string.IsNullOrEmpty(raw))
        {
            error = "Identifier must not be empty.";
            return false;
        }

        if (!ValidPattern.IsMatch(raw))
        {
            error = $"'{raw}' is not a valid identifier. Use lowercase letters (a-z), digits (0-9), and underscores (_). Must start with a letter or underscore. Maximum 63 characters.";
            return false;
        }

        if (PgReservedKeywords.IsReserved(raw))
        {
            error = $"'{raw}' is a reserved PostgreSQL keyword and cannot be used as an identifier.";
            return false;
        }

        result = new SafeIdentifier(raw);
        return true;
    }

    public override string ToString() => Value;
}
```

---

### PostgreSQL Reserved Keywords

The list below covers the most dangerous PG 17 reserved keywords (those that would break DDL if used as table names). The full list is ~100 entries from `SELECT word FROM pg_get_keywords() WHERE catcode IN ('R', 'U')` for PG 17.

```csharp
// src/FormForge.Api/Features/Designer/PgReservedKeywords.cs
namespace FormForge.Api.Features.Designer;

internal static class PgReservedKeywords
{
    // Curated subset of PostgreSQL 17 reserved and unreserved-reserved keywords.
    // Expanded from pg_get_keywords() catcode 'R'/'U'. Refreshed per PG major version.
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "all", "analyse", "analyze", "and", "any", "array", "as", "asc",
        "asymmetric", "authorization", "binary", "both", "case", "cast",
        "check", "collate", "collation", "column", "concurrently", "constraint",
        "create", "cross", "current_catalog", "current_date", "current_role",
        "current_schema", "current_time", "current_timestamp", "current_user",
        "default", "deferrable", "desc", "distinct", "do", "else", "end",
        "except", "false", "fetch", "for", "foreign", "freeze", "from",
        "full", "grant", "group", "having", "ilike", "in", "initially",
        "inner", "intersect", "into", "is", "isnull", "join", "lateral",
        "leading", "left", "like", "limit", "localtime", "localtimestamp",
        "natural", "not", "notnull", "null", "offset", "on", "only", "or",
        "order", "outer", "overlaps", "placing", "primary", "references",
        "returning", "right", "select", "session_user", "similar", "some",
        "symmetric", "table", "tablesample", "then", "to", "trailing",
        "true", "union", "unique", "user", "using", "variadic", "verbose",
        "when", "where", "window", "with",
        // Identifiers that collide with system columns added to every dynamic table
        "id", "created_at", "created_by", "updated_at", "updated_by",
        "is_deleted", "cascade_event_id",
    };

    public static bool IsReserved(string identifier) =>
        Keywords.Contains(identifier);
}
```

Note: The system-column names (`id`, `created_at`, etc.) are also blocked because each dynamic table will have those columns by default (Story 5.x). Blocking them early prevents silent collisions.

---

### Domain Entities

```csharp
// src/FormForge.Api/Domain/Entities/ComponentSchema.cs
namespace FormForge.Api.Domain.Entities;

internal sealed class ComponentSchema
{
    public string DesignerId { get; set; } = string.Empty;  // PK — TEXT, not UUID
    public string DisplayName { get; set; } = string.Empty;
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    // Navigation
    public List<ComponentSchemaVersion> Versions { get; set; } = [];
    public User? Creator { get; set; }
}
```

```csharp
// src/FormForge.Api/Domain/Entities/ComponentSchemaVersion.cs
namespace FormForge.Api.Domain.Entities;

internal sealed class ComponentSchemaVersion
{
    public Guid Id { get; set; }
    public string DesignerId { get; set; } = string.Empty;  // FK → component_schemas
    public int Version { get; set; }
    public string Status { get; set; } = "Draft";   // "Draft" | "Published" | "Archived"
    public string? RootElement { get; set; }         // JSONB stored as JSON string in C#
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }

    // Navigation
    public ComponentSchema Schema { get; set; } = null!;
    public User? Creator { get; set; }
}
```

**Why `string? RootElement` not `JsonElement?`**: EF Core's JSONB support via `JsonElement` requires `UseJsonColumns()` or Npgsql-specific mapping that adds complexity. Storing as `string?` with `HasColumnType("jsonb")` lets Npgsql pass the JSON through transparently, while the service layer handles serialization/deserialization using `System.Text.Json.JsonSerializer`. This is simpler and avoids accidental double-serialization.

---

### DTOs

```csharp
// src/FormForge.Api/Features/Designer/Dtos/CreateDesignerRequest.cs
namespace FormForge.Api.Features.Designer.Dtos;

internal sealed record CreateDesignerRequest(string DesignerId, string DisplayName);
```

```csharp
// src/FormForge.Api/Features/Designer/Dtos/DesignerResponse.cs
// Shape MUST match ComponentSchemaDto in web/src/types/designer.ts exactly.
using System.Text.Json.Nodes;

namespace FormForge.Api.Features.Designer.Dtos;

internal sealed record DesignerResponse(
    string DesignerId,
    string DisplayName,
    string Status,          // "Draft" | "Published" | "Archived"
    int LatestVersion,
    JsonNode? RootElement,  // null for new designers; parsed JSON for existing
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<DesignerVersionSummary>? Versions = null);

internal sealed record DesignerVersionSummary(
    int Version,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt);
```

```csharp
// src/FormForge.Api/Features/Designer/Dtos/DesignerListItem.cs
// Shape MUST match ComponentSchemaListItem in web/src/types/designer.ts exactly.
namespace FormForge.Api.Features.Designer.Dtos;

internal sealed record DesignerListItem(
    string DesignerId,
    string DisplayName,
    string Status,          // status of the latest version
    int LatestVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);
```

**JSON serialization**: `RootElement` is returned as `JsonNode?` (not a string) so that ASP.NET's `System.Text.Json` serializer emits it as a raw JSON object, not a double-encoded string. In the service, parse the stored JSON string to `JsonNode` using `JsonNode.Parse(versionRow.RootElement)` before returning. Return `null` if `RootElement` is null.

---

### DbContext Configuration

Add to `FormForgeDbContext.OnModelCreating`:

```csharp
// In FormForgeDbContext.cs, inside OnModelCreating:
modelBuilder.Entity<ComponentSchema>(e =>
{
    e.ToTable("component_schemas");
    e.HasKey(s => s.DesignerId);
    e.Property(s => s.DesignerId).HasColumnName("designer_id").IsRequired().HasMaxLength(63);
    e.Property(s => s.DisplayName).HasColumnName("display_name").IsRequired().HasMaxLength(200);
    e.Property(s => s.CreatedBy).HasColumnName("created_by");
    e.Property(s => s.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
    e.Property(s => s.UpdatedAt).HasColumnName("updated_at");
    e.HasMany(s => s.Versions)
     .WithOne(v => v.Schema)
     .HasForeignKey(v => v.DesignerId)
     .HasConstraintName("fk_component_schema_versions_schema")
     .OnDelete(DeleteBehavior.Cascade);
    e.HasOne(s => s.Creator)
     .WithMany()
     .HasForeignKey(s => s.CreatedBy)
     .HasConstraintName("fk_component_schemas_users")
     .OnDelete(DeleteBehavior.SetNull);
});

modelBuilder.Entity<ComponentSchemaVersion>(e =>
{
    e.ToTable("component_schema_versions");
    e.HasKey(v => v.Id);
    e.Property(v => v.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
    e.Property(v => v.DesignerId).HasColumnName("designer_id").IsRequired().HasMaxLength(63);
    e.Property(v => v.Version).HasColumnName("version").IsRequired();
    e.Property(v => v.Status).HasColumnName("status").IsRequired().HasDefaultValue("Draft").HasMaxLength(20);
    e.Property(v => v.RootElement).HasColumnName("root_element").HasColumnType("jsonb");
    e.Property(v => v.CreatedBy).HasColumnName("created_by");
    e.Property(v => v.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
    e.Property(v => v.PublishedAt).HasColumnName("published_at");
    e.HasIndex(v => new { v.DesignerId, v.Version })
     .IsUnique()
     .HasDatabaseName("uq_component_schema_versions_designer_version");
    e.HasIndex(v => v.DesignerId)
     .HasDatabaseName("idx_component_schema_versions_designer_id");
    e.HasOne(v => v.Creator)
     .WithMany()
     .HasForeignKey(v => v.CreatedBy)
     .HasConstraintName("fk_component_schema_versions_users")
     .OnDelete(DeleteBehavior.SetNull);
});
```

Add the two DbSets:
```csharp
public DbSet<ComponentSchema> ComponentSchemas => Set<ComponentSchema>();
public DbSet<ComponentSchemaVersion> ComponentSchemaVersions => Set<ComponentSchemaVersion>();
```

---

### DesignerService

```csharp
// src/FormForge.Api/Features/Designer/DesignerService.cs — interface + implementation

internal enum CreateDesignerOutcome { Success, DesignerExists, IdentifierInvalid }
internal sealed record CreateDesignerResult(
    CreateDesignerOutcome Outcome,
    string? ErrorDetail = null,
    DesignerResponse? Designer = null);

internal interface IDesignerService
{
    Task<CreateDesignerResult> CreateAsync(CreateDesignerRequest request, Guid createdBy, CancellationToken ct);
    Task<PagedResult<DesignerListItem>> ListAsync(int page, int pageSize, CancellationToken ct);
    Task<DesignerResponse?> GetLatestAsync(string designerId, CancellationToken ct);
    Task<DesignerResponse?> GetVersionAsync(string designerId, int version, CancellationToken ct);
}
```

**`CreateAsync` implementation steps:**

1. Validate `SafeIdentifier.TryCreate(request.DesignerId, out var safeId, out var idError)` — if false, return `CreateDesignerOutcome.IdentifierInvalid` with `idError` as `ErrorDetail`.

2. Check duplicate: `var exists = await db.ComponentSchemas.AnyAsync(s => s.DesignerId == safeId!.Value, ct)`. If true, return `CreateDesignerOutcome.DesignerExists`.

3. Create entities:
   ```csharp
   var now = DateTimeOffset.UtcNow;
   var schema = new ComponentSchema
   {
       DesignerId = safeId!.Value,
       DisplayName = request.DisplayName.Trim(),
       CreatedBy = createdBy,
       CreatedAt = now,
   };
   var version1 = new ComponentSchemaVersion
   {
       DesignerId = safeId!.Value,
       Version = 1,
       Status = "Draft",
       RootElement = null,
       CreatedBy = createdBy,
       CreatedAt = now,
   };
   db.ComponentSchemas.Add(schema);
   db.ComponentSchemaVersions.Add(version1);
   ```

4. Save: wrap `SaveChangesAsync` in a try/catch for PG 23505 (unique violation on `uq_component_schema_versions_designer_version` or PK) → return `DesignerExists` (race-window guard, same pattern as `RoleService`).

5. Return `CreateDesignerOutcome.Success` with `ToResponse(schema, version1, includeVersions: false)`.

**`ListAsync` implementation:**

Use correlated subqueries via EF LINQ `let` pattern — translates cleanly to SQL with Npgsql:

```csharp
var total = await db.ComponentSchemas.LongCountAsync(ct).ConfigureAwait(false);
var skip = (int)Math.Min(int.MaxValue, ((long)page - 1L) * pageSize);

var items = await db.ComponentSchemas
    .OrderBy(s => s.DesignerId)
    .Skip(skip)
    .Take(pageSize)
    .Select(s => new DesignerListItem(
        s.DesignerId,
        s.DisplayName,
        s.Versions
            .OrderByDescending(v => v.Version)
            .Select(v => v.Status)
            .FirstOrDefault() ?? "Draft",
        s.Versions
            .OrderByDescending(v => v.Version)
            .Select(v => (int?)v.Version)
            .FirstOrDefault() ?? 1,
        s.CreatedAt,
        s.UpdatedAt))
    .ToListAsync(ct)
    .ConfigureAwait(false);

return new PagedResult<DesignerListItem>(items, total, page, pageSize);
```

Correlated subqueries inside `Select` translate to SQL `(SELECT TOP 1 ...)` with Npgsql EF Core provider. If you get a translation error at runtime, fall back to: load schemas + all versions, group in C#.

**`GetLatestAsync` implementation:**

Load all versions for the designer and pick the latest in C# (designers have few versions; the N+1 is negligible). Using a filtered include with `OrderByDescending + Take` is complex with EF Core LINQ — the simpler approach avoids translation issues:

```csharp
var schema = await db.ComponentSchemas
    .Include(s => s.Versions)
    .FirstOrDefaultAsync(s => s.DesignerId == designerId, ct);

if (schema is null) return null;
var latest = schema.Versions.OrderByDescending(v => v.Version).FirstOrDefault();
return ToResponse(schema, latest, includeVersions: true);
```

**`GetVersionAsync` implementation:**

```csharp
var schema = await db.ComponentSchemas
    .Include(s => s.Versions.Where(v => v.Version == version))
    .FirstOrDefaultAsync(s => s.DesignerId == designerId, ct);

if (schema is null || schema.Versions.Count == 0) return null;
var ver = schema.Versions[0];
return ToResponse(schema, ver, includeVersions: false);
```

Note: The filtered include `s.Versions.Where(v => v.Version == version)` IS supported in EF Core 6+ (Npgsql provider). If EF Core throws at runtime about this translate to: load schema first, then query the version separately.

**`ToResponse` helper:**

```csharp
private static DesignerResponse ToResponse(ComponentSchema schema, ComponentSchemaVersion? version, bool includeVersions)
{
    JsonNode? rootNode = null;
    if (version?.RootElement is not null)
    {
        rootNode = JsonNode.Parse(version.RootElement);
    }

    IReadOnlyList<DesignerVersionSummary>? versions = null;
    if (includeVersions && schema.Versions.Count > 0)
    {
        versions = schema.Versions
            .OrderBy(v => v.Version)
            .Select(v => new DesignerVersionSummary(v.Version, v.Status, v.CreatedAt, v.PublishedAt))
            .ToList();
    }

    return new DesignerResponse(
        schema.DesignerId,
        schema.DisplayName,
        version?.Status ?? "Draft",
        version?.Version ?? 1,
        rootNode,
        schema.CreatedAt,
        schema.UpdatedAt,
        versions);
}
```

---

### DesignerEndpoints

Follow the exact same pattern as `RoleEndpoints.cs`:

```csharp
// src/FormForge.Api/Features/Designer/DesignerEndpoints.cs
internal static class DesignerEndpoints
{
    internal static RouteGroupBuilder MapDesignerEndpoints(this RouteGroupBuilder group)
    {
        // POST requires platform-admin; GET endpoints are open to all authenticated users
        group.MapPost("/", CreateDesignerHandler)
             .RequireAuthorization("platform-admin")
             .AddValidationFilter<CreateDesignerRequest>()
             .WithSummary("Create a new designer")
             .Produces<DesignerResponse>(StatusCodes.Status201Created)
             .Produces(StatusCodes.Status409Conflict)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/", ListDesignersHandler)
             .WithSummary("List all designers (paginated)")
             .Produces<PagedResult<DesignerListItem>>(StatusCodes.Status200OK);

        group.MapGet("/{designerId}", GetDesignerHandler)
             .WithSummary("Get the latest version of a designer")
             .Produces<DesignerResponse>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{designerId}/versions/{version:int}", GetDesignerVersionHandler)
             .WithSummary("Get a specific version of a designer")
             .Produces<DesignerResponse>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> CreateDesignerHandler(
        CreateDesignerRequest request,
        ClaimsPrincipal user,
        IDesignerService designerService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(designerService);

        if (!Guid.TryParse(user.FindFirst("userId")?.Value, out var userId))
            return Results.Unauthorized();

        var result = await designerService.CreateAsync(request, userId, ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            CreateDesignerOutcome.IdentifierInvalid => Results.Problem(
                title: "Invalid designer identifier",
                detail: result.ErrorDetail,
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "IDENTIFIER_INVALID",
                    ["messageKey"] = "designers.identifierInvalid",
                }),
            CreateDesignerOutcome.DesignerExists => Results.Problem(
                title: "A designer with this ID already exists",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "DESIGNER_EXISTS",
                    ["messageKey"] = "designers.designerExists",
                }),
            CreateDesignerOutcome.Success => Results.Created(
                $"/api/designers/{result.Designer!.DesignerId}",
                result.Designer),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    private static async Task<IResult> ListDesignersHandler(
        IDesignerService designerService,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(designerService);
        pageSize = Math.Min(Math.Max(pageSize, 1), 100);
        page = Math.Max(page, 1);
        var result = await designerService.ListAsync(page, pageSize, ct).ConfigureAwait(false);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetDesignerHandler(
        string designerId,
        IDesignerService designerService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(designerService);
        var result = await designerService.GetLatestAsync(designerId, ct).ConfigureAwait(false);
        return result is null ? DesignerNotFoundProblem() : Results.Ok(result);
    }

    private static async Task<IResult> GetDesignerVersionHandler(
        string designerId,
        int version,
        IDesignerService designerService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(designerService);
        var result = await designerService.GetVersionAsync(designerId, version, ct).ConfigureAwait(false);
        return result is null ? DesignerNotFoundProblem() : Results.Ok(result);
    }

    private static IResult DesignerNotFoundProblem() =>
        Results.Problem(
            title: "Designer not found",
            statusCode: StatusCodes.Status404NotFound,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "DESIGNER_NOT_FOUND",
                ["messageKey"] = "designers.notFound",
            });
}
```

---

### Program.cs Registration

Add these blocks in the service registration section (near the other `AddScoped` calls):

```csharp
// Designer services (Story 3.2)
using FormForge.Api.Features.Designer;
using FormForge.Api.Features.Designer.Dtos;
using FormForge.Api.Features.Designer.Validators;

// In the DI registration section:
builder.Services.AddScoped<IDesignerService, DesignerService>();
builder.Services.AddScoped<IValidator<CreateDesignerRequest>, CreateDesignerRequestValidator>();
```

Add the route group after the existing `/api/data/{designerId}` group registration (before `app.Run()`):

```csharp
// /api/designers — Designer CRUD. Create requires platform-admin (enforced per-endpoint
// inside DesignerEndpoints). GET endpoints are open to all authenticated users (DynamicComponent
// data-entry renderer uses GET /{designerId}/versions/{version} in Epic 6).
app.MapGroup("/api/designers")
   .RequireAuth()
   .RequireRateLimiting("admin")
   .WithTags("Designers")
   .MapDesignerEndpoints();
```

---

### Canvas Behavior After Create (AC-4 Technical Detail)

When the frontend navigates to `/designer/$designerId` after creation:

1. `designer.$designerId.tsx` fires `useQuery` → `GET /api/designers/{designerId}` → returns `{ rootElement: null, latestVersion: 1, status: "Draft", ... }`
2. `setSchema(data)` is called → store sets `rootElement = null`, `version = 1`, `displayName = ...`
3. `DesignerCanvas` renders. With `rootElement === null`, it renders `EmptyCanvasDrop` (the dashed drop target with "Drop a Stack, Row, or Tabs container here to start building.")
4. Canvas is functional for building. ✅

The user CAN drag elements and build on the canvas. However, clicking "Save" will call `designerApi.saveVersion(designerId, 1, rootElement)` → `PUT /api/designers/{designerId}/versions/1` → **404** (not implemented until Story 3.6). The canvas page shows the "Could not save" error toast. This is expected behavior — log it in Completion Notes.

---

### Previous Story Learnings (Story 3.1)

Story 3.1 deferred several issues that touch adjacent code:

- **`saveVersion` payload mismatch** — the frontend `designer.$designerId.tsx` calls `saveVersion` which sends only `{ rootElement }`. The `displayName` is not persisted. Story 3.2 does NOT fix this — it's marked for Story 3.6 when the PUT endpoint lands.
- **`version: 0` coerce to 1** — Store `version` starts at 0 before `setSchema` runs. The canvas save has `version || 1` to handle this. This is pre-existing from 3.1 review.
- **DesignerCanvas** `EmptyCanvasDrop` works correctly with `rootElement: null` — it shows the empty zone. Do NOT "fix" this — it's the correct behavior for a new designer.

---

### Architecture References

- AR-4: Identifier sanitization pipeline — `SafeIdentifier` value type, regex, reserved keyword list
- AR-22: Endpoint organization — route groups with filter chains
- AR-47: Domain events — `SchemaPublished` event fired in Story 3.7 (not this story)
- Decision 3.5: Route group `app.MapGroup("/api/designers")` in architecture doc

### File Summary

**New files (backend):**
- `src/FormForge.Api/Features/Designer/SafeIdentifier.cs`
- `src/FormForge.Api/Features/Designer/PgReservedKeywords.cs`
- `src/FormForge.Api/Features/Designer/DesignerService.cs` (add `[SuppressMessage("Performance", "CA1812", Justification = "Registered via DI.")]` on the class, same as RoleService)
- `src/FormForge.Api/Features/Designer/DesignerEndpoints.cs`
- `src/FormForge.Api/Features/Designer/Dtos/CreateDesignerRequest.cs`
- `src/FormForge.Api/Features/Designer/Dtos/DesignerResponse.cs` (used for both GET latest and GET version responses)
- `src/FormForge.Api/Features/Designer/Dtos/DesignerListItem.cs`
- `src/FormForge.Api/Features/Designer/Validators/CreateDesignerRequestValidator.cs` (add `[SuppressMessage("Performance", "CA1812", ...)]` on the class)
- `src/FormForge.Api/Domain/Entities/ComponentSchema.cs`
- `src/FormForge.Api/Domain/Entities/ComponentSchemaVersion.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/YYYYMMDD_CreateComponentSchemas.cs` (generated by EF CLI)

**Modified files (backend):**
- `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` (add DbSets + model config)
- `src/FormForge.Api/Program.cs` (register services + route group)

**Modified files (frontend):**
- `web/src/features/designer/designerApi.ts` (path fix: `/api/designer` → `/api/designers`)
- `web/src/routes/_app/designer.library.tsx` (regex fix in `createSchemaSchema`)
- `web/src/lib/i18n/locales/en.json` (add missing keys)

**Files NOT to touch:**
- All Epic 2 feature files
- `web/src/routeTree.gen.ts` (auto-generated)
- `web/src/components/designer/**` (story 3.1 work — don't touch)
- `web/src/store/designerCanvas.ts` (story 3.1 work — don't touch)

## Dev Agent Record

### Agent Model Used
claude-opus-4-7 (Opus 4.7, 1M context)

### Completion Notes List

- **All 6 ACs covered by automated tests.** 23 new Designer-specific tests (16 integration + 7 validator + 6 SafeIdentifier theory rows + reserved-keyword + happy paths). Full suite: 198/198 pass.
- **AC-1** verified by `CreateDesigner_HappyPath_Returns201_WithLocationAndV1Draft` — checks `version: 1`, `status: "Draft"`, `rootElement: null`, `updatedAt: null`, and `Location: /api/designers/{designerId}` header.
- **AC-2** verified by the `[Theory]` over invalid identifiers (returns 422 from FluentValidation), the reserved-keyword test (returns 422 with `IDENTIFIER_INVALID` from `SafeIdentifier` inside the service), and the unit-level `SafeIdentifierTests`.
- **AC-3** verified by `CreateDesigner_Duplicate_Returns409_DesignerExists`. The race-window `23505` catch in `SaveChangesAsync` ensures two concurrent POSTs with the same `designerId` both yield 409 (not 500).
- **AC-4** verified end-to-end: the frontend `designerApi.ts` was repathed to `/api/designers`; `GetDesigner_Existing_Returns200_LatestVersionDraftNullRoot` confirms the canvas page's GET returns `rootElement: null`, `latestVersion: 1`, `status: "Draft"` so `EmptyCanvasDrop` renders.
- **AC-5** verified by `ListDesigners_WithItems_Returns200_OrderedByDesignerId` — paginated, ordered by `designerId`, latest-version status surfaced via correlated subquery.
- **AC-6** verified by `GetDesignerVersion_*` trio: existing version returns 200 with `rootElement` as a JSON object (not a stringified object); unknown designerId AND unknown version both return 404 with `DESIGNER_NOT_FOUND`.
- **Pre-existing deferred items left intact** (per story Dev Notes "Previous Story Learnings"):
  - `saveVersion` still only sends `{ rootElement }` without `displayName` — owned by Story 3.6's PUT endpoint.
  - `version: 0` → 1 coercion in canvas page kept as-is.
  - `EmptyCanvasDrop` rendering on `rootElement: null` is the intended canvas behavior after create.
- **Migration codegen tweak**: EF's generated `Up`/`Down` methods needed `ArgumentNullException.ThrowIfNull(migrationBuilder)` added to satisfy CA1062 (matches the pattern in `CreateRolesRolePermissionsAndUserRoles.cs`). EF's templates don't add it; both prior migrations also added it by hand.
- **i18n keys already present**: All keys the story Task 1 lists (`designerIdConflict`, `statusPublished`, `statusDraft`, `statusArchived`, `rowMenuLabel`) were already added during Story 3.1's review-patch pass. No edits to `en.json` were needed; verified by re-reading the file.

### File List

**New (backend):**
- `src/FormForge.Api/Features/Designer/SafeIdentifier.cs`
- `src/FormForge.Api/Features/Designer/PgReservedKeywords.cs`
- `src/FormForge.Api/Features/Designer/DesignerService.cs`
- `src/FormForge.Api/Features/Designer/DesignerEndpoints.cs`
- `src/FormForge.Api/Features/Designer/Dtos/CreateDesignerRequest.cs`
- `src/FormForge.Api/Features/Designer/Dtos/DesignerResponse.cs`
- `src/FormForge.Api/Features/Designer/Dtos/DesignerListItem.cs`
- `src/FormForge.Api/Features/Designer/Validators/CreateDesignerRequestValidator.cs`
- `src/FormForge.Api/Domain/Entities/ComponentSchema.cs`
- `src/FormForge.Api/Domain/Entities/ComponentSchemaVersion.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260523112633_CreateComponentSchemas.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260523112633_CreateComponentSchemas.Designer.cs`

**Modified (backend):**
- `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` (added `ComponentSchemas`, `ComponentSchemaVersions` DbSets + `OnModelCreating` blocks)
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/FormForgeDbContextModelSnapshot.cs` (regenerated by EF CLI)
- `src/FormForge.Api/Program.cs` (added Designer usings, scoped service + validator registrations, `/api/designers` route group)

**New (tests):**
- `src/FormForge.Api.Tests/Features/Designer/SafeIdentifierTests.cs`
- `src/FormForge.Api.Tests/Features/Designer/CreateDesignerRequestValidatorTests.cs`
- `src/FormForge.Api.Tests/Features/Designer/DesignerIntegrationTests.cs`

**Modified (frontend):**
- `web/src/features/designer/designerApi.ts` (path fix `/api/designer` → `/api/designers` in all 7 routes; comment refreshed)
- `web/src/routes/_app/designer.library.tsx` (Zod regex `^[a-z_][a-z0-9_]{0,62}$`, `max(63)`, updated error copy)

### Change Log

| Date | Author | Change |
|---|---|---|
| 2026-05-23 | bmad-create-story | Story created — comprehensive context for Story 3.2 backend implementation |
| 2026-05-23 | bmad-dev-story (claude-opus-4-7) | Implemented all 12 tasks: frontend path/regex fix, `SafeIdentifier` + reserved-keywords, `ComponentSchema`/`Version` entities, `DesignerService` + endpoints, EF migration, Program.cs wiring, 23 new tests (198/198 passing). Status → review. |
| 2026-05-23 | bmad-code-review | 3-reviewer adversarial pass (Blind Hunter + Edge Case Hunter + Acceptance Auditor). Triaged: 8 patches, 1 decision, 11 deferred, 10 dismissed as noise. |

### Review Findings

**Decision needed:**

- [x] [Review][Defer] FK `OnDelete(Cascade)` on `component_schema_versions → component_schemas` is forward-risky — `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs:122`. Cascade wipes all version rows (including Published) when a schema is deleted. No DELETE endpoint exists today, but Story 5.x/6.x will create dynamic tables keyed by `designer_id` whose data rows reference Published versions. **Resolved: defer to Story 5.x — FK policy depends on what Story 5.x's dynamic tables decide about data-row → version coupling; choosing Cascade vs Restrict now would lock in a contract before the dependency materializes.**

**Patches (applied 2026-05-23 after the review pass):**

- [x] [Review][Patch] AC-2 body contract: FluentValidation 422 omits `code: "IDENTIFIER_INVALID"`. Resolved by moving all identifier-shape validation (empty/length/charset) out of `CreateDesignerRequestValidator` so every invalid designerId now flows through `SafeIdentifier` in `DesignerService.CreateAsync` and the endpoint emits 422 + `code: "IDENTIFIER_INVALID"` uniformly. Test `CreateDesigner_InvalidDesignerId_Returns422_IdentifierInvalid` now asserts the body code; validator tests trimmed to DisplayName only.
- [x] [Review][Patch] `23505` catch should match constraint name. Resolved by narrowing the catch in `DesignerService.CreateAsync` to `PK_component_schemas` OR `uq_component_schema_versions_designer_version` — matches `UserService`/`RoleService` precedent.
- [x] [Review][Patch] Reserved-keyword list missing PG names + `pg_*` prefix guard. Resolved in `PgReservedKeywords.cs` — added `role`, `oid`, `tableoid`, `xmin`, `xmax`, `cmin`, `cmax`, `ctid`; `IsReserved` now also rejects any identifier starting with `pg_`. New `SafeIdentifierTests` theory rows cover all of the above.
- [x] [Review][Patch] Schema with zero versions returns synthetic v1/Draft. Resolved: `GetLatestAsync` now returns `null` (→ 404) if `latest` is null, surfacing the invariant break instead of fabricating a response. `ToResponse` parameter tightened to non-nullable.
- [x] [Review][Patch] DisplayName accepts control characters. Resolved in `CreateDesignerRequestValidator` — added `.Must(name => name is null || name.All(c => !char.IsControl(c)))` with a dedicated message. Three new theory rows in `CreateDesignerRequestValidatorTests` (\n / \t / \0).
- [x] [Review][Patch] `JsonNode.Parse` whitespace-input crash. Resolved: `ToResponse` uses `string.IsNullOrWhiteSpace` so a whitespace-only RootElement no longer reaches `Parse`.
- [x] [Review][Patch] GET-as-non-admin coverage gap. Resolved with two new tests: `GetDesigner_AsNonAdmin_Returns200_LatestVersion` and `GetDesignerVersion_AsNonAdmin_Returns200_WithRootElement` (viewer token, asserts 200 + payload).
- [x] [Review][Patch] Frontend `createSchemaSchema.designerId` not trimmed. Resolved in `designer.library.tsx` — `z.string().trim().min(1).max(63).regex(...)`; `displayName` also gets `.trim()` for consistency.

**Deferred (pre-existing or out-of-scope, tracked in `deferred-work.md`):**

- [x] [Review][Defer] `ListAsync` silent saturation for huge `page` values returns 200 empty instead of 400 [`src/FormForge.Api/Features/Designer/DesignerService.cs` ListAsync] — standard pagination caveat, no real harm.
- [x] [Review][Defer] `LongCountAsync` → `int TotalPages` theoretical overflow — needs 2B+ rows.
- [x] [Review][Defer] `GetLatestAsync(includeVersions: true)` returns unbounded versions list [`src/FormForge.Api/Features/Designer/DesignerService.cs` ToResponse] — owned by Story 3.7/3.8 (version management).
- [x] [Review][Defer] Frontend `saveVersion` PUT returns 404 [`web/src/features/designer/designerApi.ts`] — explicitly deferred to Story 3.6 by story Dev Notes (PUT endpoint not yet implemented).
- [x] [Review][Defer] Migration requires `pgcrypto`/`gen_random_uuid` at runtime [`src/FormForge.Api/Infrastructure/Persistence/Migrations/20260523112633_CreateComponentSchemas.cs:16-19`] — environment precondition shared with all existing migrations.
- [x] [Review][Defer] EF correlated subquery in `ListAsync` may regress on Npgsql/EF upgrades; no in-code fallback [`src/FormForge.Api/Features/Designer/DesignerService.cs` ListAsync] — story Dev Notes already document the fallback strategy.
- [x] [Review][Defer] `ListAsync` TOCTOU between `LongCountAsync` and `ToListAsync` — standard pagination caveat under concurrent writes.
- [x] [Review][Defer] GET endpoints accept unbounded `designerId` length / `version <= 0` route values [`src/FormForge.Api/Features/Designer/DesignerEndpoints.cs`] — always return 404 via DB lookup; wasteful but harmless.
- [x] [Review][Defer] Test infra brittleness — `_factory!`/`_client!` null-forgiving, `EnsureSuccessStatusCode` in `LoginAsync`, `viewer` user with no role assignment [`src/FormForge.Api.Tests/Features/Designer/DesignerIntegrationTests.cs:939-940, 1253, 1294-1322`] — test polish.
- [x] [Review][Defer] `ListDesigners_WithItems_Returns200_OrderedByDesignerId` doesn't exercise `TotalPages` math beyond the trivial pageSize=25 / total=2 case [`src/FormForge.Api.Tests/Features/Designer/DesignerIntegrationTests.cs:1123-1124`] — test polish.
- [x] [Review][Defer] `RootElement` returned without max-depth/max-length guards — DoS surface theoretical; rate-limiter + jsonb storage bound it practically.

**Dismissed (verified non-issues during triage):**

- `userId` claim hardcoded — verified consistent with `PermissionsEndpoints.cs:28`, `UserEndpoints.cs:133`, `RouteGroupExtensions.cs:50`, `Program.cs:216/230/243`. Project convention.
- `zustand@5.0.13` lockfile-only — verified `web/package.json:35` already declares `zustand: "^5.0.13"`; diff doesn't show `package.json` because it wasn't modified in this story.
- `RequireAuthorization("platform-admin")` "bypass" via `AddValidationFilter` ordering — minimal-API authorization runs before endpoint filters; not actually bypassable.
- `ArgumentNullException.ThrowIfNull(user)` for `ClaimsPrincipal` — stylistic; harmless.
- `IX_component_schema_versions_created_by` index redundant — EF auto-creates FK indexes; serves a different lookup pattern.
- `createSchema` ignores `Location` header — backend doesn't normalize `designerId`, so SPA navigation via body's `designerId` is correct today.
- AC-3 race-window 23505 fires correctly — both `PK_component_schemas` and `uq_component_schema_versions_designer_version` raise 23505 on a concurrent duplicate; behavior is right (separate concern is the constraint-name narrowing patch above).
- `CreateDesignerOutcome.IdentifierInvalid` "unreachable" — reachable via the reserved-keyword check in `SafeIdentifier`; the real concern is the body shape (covered by the AC-2 patch).
- Test `CreateDesigner_AsNonAdmin_Returns403` — viewer's role assignment is consistent with other admin-endpoint negative tests in the suite; not actually a problem in practice.
