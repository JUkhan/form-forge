# Story 3.7: Version Status Management

Status: done

## Story

As a Platform Admin,
I want to promote a Designer version from Draft to Published, or Archive a Published version,
so that I control which version is used in production bindings.

## Acceptance Criteria

**AC-1 — Publish a Draft or Archived version**
Given a Draft or Archived version exists
When I PUT `/api/designers/{id}/versions/{version}/status` with `{ "status": "Published" }`
Then the version's status becomes "Published"
And `publishedAt` is set to the current UTC timestamp
And if a different version of the same Designer was already Published, it is auto-demoted to "Archived" (at-most-one-Published-per-Designer invariant)
And the `SchemaPublished` domain event fires (per AR-7 / AR-47)
And the response is HTTP 200 with the updated `DesignerResponse`

**AC-2 — Archive a version**
Given a Published or Draft version exists
When I PUT `/api/designers/{id}/versions/{version}/status` with `{ "status": "Archived" }`
Then the version's status becomes "Archived"
And the response is HTTP 200 with the updated `DesignerResponse`

**AC-3 — Re-publish an Archived version succeeds**
Given an Archived version exists
When I PUT the status endpoint to `Published`
Then the operation succeeds (Archived → Published is a valid transition)
And the at-most-one-Published invariant is still enforced (any other Published version is demoted)

**AC-4 — No-op when already in target status**
Given a version already in the target status
When I PUT the status endpoint
Then the response is HTTP 200 with the current state (no DB write, no event fired)

**AC-5 — "Draft" is not a valid target status**
Given any version
When I PUT the status endpoint with `{ "status": "Draft" }`
Then the response is HTTP 422 with `code: "STATUS_INVALID"`

**AC-6 — Version not found**
Given the designerId exists but the requested version number does not
When I PUT the status endpoint
Then the response is HTTP 404 with `code: "VERSION_NOT_FOUND"`

**AC-7 — Designer not found**
Given the designerId does not exist
When I PUT the status endpoint
Then the response is HTTP 404 with `code: "DESIGNER_NOT_FOUND"`

**AC-8 — Non-admin blocked**
Given a non-platform-admin user
When they call the status endpoint
Then the response is HTTP 403

**AC-9 — Draft version cannot be bound (Epic 5 constraint pre-established)**
*This AC is architectural — it establishes the error shape that Epic 5's bind endpoint will return. No new endpoint is added in Story 3.7; the `VersionNotPublishedProblem()` helper is added to `DesignerEndpoints.cs` now so Epic 5 can reuse it.*
Given a Menu Binding attempt targets a Draft version (constraint enforced by Story 5.2)
When the bind endpoint processes the request
Then the response shape is HTTP 422 with `code: "VERSION_NOT_PUBLISHED"` (established here; wired in Epic 5)

## Tasks / Subtasks

- [x] Task 1: EF Core — add at-most-one-Published partial unique index
  - [x] Add filtered index to `FormForgeDbContext.OnModelCreating()` under `ComponentSchemaVersion` entity: `.HasIndex(v => v.DesignerId).HasFilter("(status = 'Published')").IsUnique().HasDatabaseName("uq_one_published_per_designer")`
  - [x] Generate migration: `dotnet ef migrations add AddPublishedVersionUniqueIndex -p src/FormForge.Api -s src/FormForge.Api`
  - [x] Verify generated migration SQL is `CREATE UNIQUE INDEX uq_one_published_per_designer ON component_schema_versions (designer_id) WHERE (status = 'Published')`

- [x] Task 2: Domain event declaration
  - [x] Add `internal sealed record SchemaPublished(string DesignerId, int Version);` to `src/FormForge.Api/Infrastructure/EventBus/IDomainEventBus.cs` (co-located with existing event records)

- [x] Task 3: DTO + Validator
  - [x] Create `src/FormForge.Api/Features/Designer/Dtos/UpdateVersionStatusRequest.cs` — single `string Status` property
  - [x] Create `src/FormForge.Api/Features/Designer/Validators/UpdateVersionStatusRequestValidator.cs` — reject empty status and any value not in `["Published", "Archived"]`; `WithMessage("Status must be 'Published' or 'Archived'.")`

- [x] Task 4: Service layer — outcome types + implementation
  - [x] Add `UpdateVersionStatusOutcome` enum and `UpdateVersionStatusResult` record to `DesignerService.cs`
  - [x] Inject `IDomainEventBus` into `DesignerService` constructor (alongside existing `FormForgeDbContext db`)
  - [x] Add `UpdateVersionStatusAsync` to `IDesignerService` interface
  - [x] Implement `UpdateVersionStatusAsync` in `DesignerService` (see Dev Notes for algorithm)

- [x] Task 5: Endpoint
  - [x] Add `MapPut("/{designerId}/versions/{version:int}/status", UpdateVersionStatusHandler)` in `DesignerEndpoints.cs`
  - [x] Add `VersionNotFoundProblem()` static helper
  - [x] Add `VersionNotPublishedProblem()` static helper (for Epic 5 reuse — see AC-9)
  - [x] Wire handler: extract `userId`, call service, switch on outcome
  - [x] Register `IValidator<UpdateVersionStatusRequest>` in `Program.cs`

- [x] Task 6: Integration tests (AC-1 through AC-8)
  - [x] Publish Draft → 200, status "Published", publishedAt set
  - [x] Publish with existing Published version → old demoted to Archived, new is Published
  - [x] Archive Published → 200, status "Archived"
  - [x] Re-publish Archived → 200, status "Published" (AC-3)
  - [x] Same-status no-op: publish already-Published version → 200 OK (AC-4)
  - [x] Status = "Draft" → 422 STATUS_INVALID (AC-5)
  - [x] Unknown status ("foo") → 422 (AC-5)
  - [x] Version not found → 404 VERSION_NOT_FOUND (AC-6)
  - [x] Designer not found → 404 DESIGNER_NOT_FOUND (AC-7)
  - [x] Non-admin (viewer) → 403 (AC-8)

- [x] Task 7: Frontend — `designerApi.ts`
  - [x] Replace `publishVersion` stub (POST `/publish`) with `PUT .../status` returning `ComponentSchemaDto`
  - [x] Replace `archiveVersion` stub (POST `/archive`) with `PUT .../status` returning `ComponentSchemaDto`

- [x] Task 8: Frontend — `designer.library.tsx`
  - [x] Add `publishMutation` to `RowMenu` (calls `designerApi.publishVersion(row.designerId, row.latestVersion)`)
  - [x] Add "Publish" button to row menu (disabled when `row.status === 'Published'` or `publishMutation.isPending`)
  - [x] `publishMutation.onSuccess`: invalidate `['designer', 'list']`, toast success, close menu
  - [x] `publishMutation.onError`: toast error, close menu

- [x] Task 9: i18n — `en.json`
  - [x] Add `designer.library.publish` — `"Publish"`
  - [x] Add `designer.library.publishSuccess` — `"Version published."`

- [x] Task 10: Build and verify
  - [x] `dotnet build` — 0 errors
  - [x] `dotnet test` — all existing + new tests pass (227/227, including 10 new 3.7 integration tests)
  - [x] `pnpm run build` — 0 errors
  - [x] `pnpm run lint` — 24 pre-existing `react-refresh/only-export-components` errors + 3 other pre-existing errors (set-state-in-effect/useMemo), 0 new errors
  - [x] `pnpm run test` — 47/47 pass

---

## Dev Notes

### At-Most-One-Published Invariant — Partial Unique Index

The DB must enforce the "at most one Published version per Designer" invariant to prevent concurrent publishes from violating it. Add a **filtered unique index** in `FormForgeDbContext.OnModelCreating()` inside the `ComponentSchemaVersion` entity block:

```csharp
// In the ComponentSchemaVersion entity configuration block (line ~131-153):
e.HasIndex(v => v.DesignerId)
 .HasFilter("(status = 'Published')")
 .IsUnique()
 .HasDatabaseName("uq_one_published_per_designer");
```

After adding this, run:
```
dotnet ef migrations add AddPublishedVersionUniqueIndex -p src/FormForge.Api -s src/FormForge.Api
```

The generated Up method should emit something like:
```csharp
migrationBuilder.CreateIndex(
    name: "uq_one_published_per_designer",
    table: "component_schema_versions",
    column: "designer_id",
    unique: true,
    filter: "(status = 'Published')");
```

Verify the generated SQL in the designer migration is correct before proceeding. The `Down` method should drop this index.

### SchemaPublished Domain Event

Add to `IDomainEventBus.cs` alongside the existing event records (after `MenuBindingCreated`):

```csharp
internal sealed record SchemaPublished(string DesignerId, int Version);
```

The schema registry (Story 5.x) will subscribe to this event to evict its cache entry. Story 3.7 only declares and publishes the event; no subscriber exists yet. The `InProcessEventBus` handles this case gracefully — `Publish` is a no-op when no handlers are registered.

### DesignerService — Constructor Change

Add `IDomainEventBus` dependency:

```csharp
internal sealed class DesignerService(FormForgeDbContext db, IDomainEventBus eventBus) : IDesignerService
```

`DesignerService` is registered as `Scoped`; `IDomainEventBus` (`InProcessEventBus`) is `Singleton`. This is a legal dependency — Scoped can take Singleton. No `Program.cs` change needed for the bus itself; it's already registered.

### UpdateVersionStatusOutcome + Result + Interface

Add to `DesignerService.cs` alongside the existing `SaveVersionOutcome`/`SaveVersionResult`:

```csharp
internal enum UpdateVersionStatusOutcome
{
    Success,
    DesignerNotFound,
    VersionNotFound,
    StatusUnchanged,    // target status == current status — no write needed
    PublishConflict,    // concurrent publish hit the partial unique index (rare)
}

internal sealed record UpdateVersionStatusResult(
    UpdateVersionStatusOutcome Outcome,
    DesignerResponse? Designer = null);
```

Add to `IDesignerService` interface:
```csharp
Task<UpdateVersionStatusResult> UpdateVersionStatusAsync(
    string designerId, int version, string newStatus, Guid updatedBy, CancellationToken ct);
```

### UpdateVersionStatusAsync — Implementation

```csharp
public async Task<UpdateVersionStatusResult> UpdateVersionStatusAsync(
    string designerId,
    int version,
    string newStatus,
    Guid updatedBy,
    CancellationToken ct)
{
    // Load schema and all versions so we can enforce the at-most-one-Published
    // invariant by demoting any existing Published version. The version count
    // per designer is small (bounded by save frequency), so materialising all
    // is acceptable here.
    var schema = await db.ComponentSchemas
        .Include(s => s.Versions)
        .FirstOrDefaultAsync(s => s.DesignerId == designerId, ct)
        .ConfigureAwait(false);

    if (schema is null)
        return new UpdateVersionStatusResult(UpdateVersionStatusOutcome.DesignerNotFound);

    var target = schema.Versions.FirstOrDefault(v => v.Version == version);
    if (target is null)
        return new UpdateVersionStatusResult(UpdateVersionStatusOutcome.VersionNotFound);

    // No-op: already in target status — return the current state without a DB write.
    if (string.Equals(target.Status, newStatus, StringComparison.Ordinal))
    {
        return new UpdateVersionStatusResult(
            UpdateVersionStatusOutcome.StatusUnchanged,
            Designer: ToResponse(schema, target, includeVersions: false));
    }

    var now = DateTimeOffset.UtcNow;

    if (string.Equals(newStatus, "Published", StringComparison.Ordinal))
    {
        // Enforce at-most-one-Published: demote any existing Published version
        // to Archived before promoting the target. The partial unique index
        // (uq_one_published_per_designer) is a final safety net against
        // concurrent publishes that race past this in-process check.
        foreach (var v in schema.Versions)
        {
            if (v.Version != version && string.Equals(v.Status, "Published", StringComparison.Ordinal))
            {
                v.Status = "Archived";
            }
        }
        target.Status = "Published";
        target.PublishedAt = now;
    }
    else // "Archived"
    {
        target.Status = "Archived";
    }

    schema.UpdatedAt = now;

    try
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
    catch (DbUpdateException ex) when (
        ex.InnerException is PostgresException { SqlState: "23505" } pg
        && string.Equals(pg.ConstraintName, "uq_one_published_per_designer", StringComparison.Ordinal))
    {
        // Two concurrent Publish requests beat each other — the partial unique
        // index fired. Surface as 409 so the SPA can prompt for a reload.
        return new UpdateVersionStatusResult(UpdateVersionStatusOutcome.PublishConflict);
    }

    // Fire SchemaPublished AFTER the commit so the event only propagates
    // if the DB write truly succeeded. The schema registry (Epic 5) will
    // subscribe to this event; it is a no-op today (no registered handler).
    if (string.Equals(newStatus, "Published", StringComparison.Ordinal))
    {
        eventBus.Publish(new SchemaPublished(designerId, version));
    }

    var response = ToResponse(schema, target, includeVersions: false);
    return new UpdateVersionStatusResult(UpdateVersionStatusOutcome.Success, Designer: response);
}
```

### UpdateVersionStatusRequest DTO

```csharp
// src/FormForge.Api/Features/Designer/Dtos/UpdateVersionStatusRequest.cs
namespace FormForge.Api.Features.Designer.Dtos;

internal sealed record UpdateVersionStatusRequest(string Status);
```

### UpdateVersionStatusRequestValidator

```csharp
// src/FormForge.Api/Features/Designer/Validators/UpdateVersionStatusRequestValidator.cs
using FluentValidation;
using FormForge.Api.Features.Designer.Dtos;

namespace FormForge.Api.Features.Designer.Validators;

internal sealed class UpdateVersionStatusRequestValidator : AbstractValidator<UpdateVersionStatusRequest>
{
    private static readonly string[] AllowedStatuses = ["Published", "Archived"];

    public UpdateVersionStatusRequestValidator()
    {
        RuleFor(r => r.Status)
            .NotEmpty()
            .Must(s => AllowedStatuses.Contains(s, StringComparer.Ordinal))
            .WithMessage("Status must be 'Published' or 'Archived'.");
    }
}
```

`Draft` is deliberately absent from `AllowedStatuses` — versions transition to Draft only at creation.

### Endpoint Handler and Helpers

Add to `DesignerEndpoints.MapDesignerEndpoints`:

```csharp
group.MapPut("/{designerId}/versions/{version:int}/status", UpdateVersionStatusHandler)
     .RequireAuthorization("platform-admin")
     .AddValidationFilter<UpdateVersionStatusRequest>()
     .WithSummary("Publish or archive a designer version")
     .Produces<DesignerResponse>(StatusCodes.Status200OK)
     .Produces(StatusCodes.Status404NotFound)
     .Produces(StatusCodes.Status409Conflict)
     .Produces(StatusCodes.Status422UnprocessableEntity);
```

Handler:

```csharp
private static async Task<IResult> UpdateVersionStatusHandler(
    string designerId,
    int version,
    UpdateVersionStatusRequest request,
    ClaimsPrincipal user,
    IDesignerService designerService,
    CancellationToken ct)
{
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(user);
    ArgumentNullException.ThrowIfNull(designerService);

    if (!Guid.TryParse(user.FindFirst("userId")?.Value, out var userId))
    {
        return Results.Unauthorized();
    }

    var result = await designerService
        .UpdateVersionStatusAsync(designerId, version, request.Status, userId, ct)
        .ConfigureAwait(false);

    return result.Outcome switch
    {
        UpdateVersionStatusOutcome.Success => Results.Ok(result.Designer),
        UpdateVersionStatusOutcome.StatusUnchanged => Results.Ok(result.Designer),
        UpdateVersionStatusOutcome.DesignerNotFound => DesignerNotFoundProblem(),
        UpdateVersionStatusOutcome.VersionNotFound => VersionNotFoundProblem(),
        UpdateVersionStatusOutcome.PublishConflict => PublishConflictProblem(),
        _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
    };
}
```

New problem helpers (add alongside existing static helpers):

```csharp
private static IResult VersionNotFoundProblem() =>
    Results.Problem(
        title: "Version not found",
        statusCode: StatusCodes.Status404NotFound,
        extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = "VERSION_NOT_FOUND",
            ["messageKey"] = "designers.versionNotFound",
        });

// AC-9 / Epic 5 pattern: used by Story 5.2's bind handler to reject Draft targets.
internal static IResult VersionNotPublishedProblem() =>
    Results.Problem(
        title: "Only Published versions can be bound to Menu Items",
        statusCode: StatusCodes.Status422UnprocessableEntity,
        extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = "VERSION_NOT_PUBLISHED",
            ["messageKey"] = "designers.versionNotPublished",
        });

private static IResult PublishConflictProblem() =>
    Results.Problem(
        title: "A concurrent publish created a Published version. Reload and try again.",
        statusCode: StatusCodes.Status409Conflict,
        extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = "PUBLISH_CONFLICT",
            ["messageKey"] = "designers.publishConflict",
        });
```

**Important**: `VersionNotPublishedProblem()` must be `internal static` (not `private static`) so Story 5.2's `BindingEndpoints` can call it. Change the visibility from `private` to `internal` for this helper only.

### Program.cs — Register New Validator

Add next to the existing `SaveVersionRequest` validator registration (line ~130):

```csharp
builder.Services.AddScoped<IValidator<UpdateVersionStatusRequest>, UpdateVersionStatusRequestValidator>();
```

### Integration Tests — Pattern

Follow the exact setup pattern from `DesignerIntegrationTests` (same class file). Add a helper for creating and saving a version with a root element:

```csharp
private async Task<int> CreateVersionViaApiAsync(string token, string designerId, object rootElement)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/designers/{designerId}/versions")
    {
        Content = JsonContent.Create(new { rootElement }),
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    using var response = await _client!.SendAsync(request);
    response.EnsureSuccessStatusCode();
    var body = await response.Content.ReadFromJsonAsync<DesignerResponseDto>();
    return body!.LatestVersion;
}
```

And a helper that PUTs the status:

```csharp
private async Task<HttpResponseMessage> PutVersionStatusAsync(
    string token, string designerId, int version, string status)
{
    using var request = new HttpRequestMessage(
        HttpMethod.Put,
        $"/api/designers/{designerId}/versions/{version}/status")
    {
        Content = JsonContent.Create(new { status }),
    };
    if (token.Length > 0)
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    return await _client!.SendAsync(request);
}
```

Key test cases:

```csharp
// AC-1: Publish Draft → 200, status Published, publishedAt set
[Fact]
public async Task PublishVersion_DraftVersion_Returns200WithPublishedStatus()

// AC-1: Publish with existing Published → auto-demote old
[Fact]
public async Task PublishVersion_ExistingPublishedPresent_AutoDemotesOldToArchived()
// Verify: GET /api/designers/{id}/versions/1 returns status "Archived"
// Verify: GET /api/designers/{id}/versions/2 returns status "Published"

// AC-2: Archive Published → 200 Archived
[Fact]
public async Task ArchiveVersion_PublishedVersion_Returns200WithArchivedStatus()

// AC-3: Re-publish Archived → 200
[Fact]
public async Task PublishVersion_ArchivedVersion_Succeeds()

// AC-4: Same-status no-op → 200
[Fact]
public async Task PublishVersion_AlreadyPublished_Returns200NoWrite()

// AC-5: Status = "Draft" → 422
[Fact]
public async Task UpdateVersionStatus_DraftTarget_Returns422()

// AC-5: Unknown status → 422
[Fact]
public async Task UpdateVersionStatus_InvalidStatus_Returns422()

// AC-6: Version not found → 404 VERSION_NOT_FOUND
[Fact]
public async Task UpdateVersionStatus_VersionNotFound_Returns404()

// AC-7: Designer not found → 404 DESIGNER_NOT_FOUND
[Fact]
public async Task UpdateVersionStatus_DesignerNotFound_Returns404()

// AC-8: Non-admin → 403
[Fact]
public async Task UpdateVersionStatus_AsViewer_Returns403()
```

The auto-demote test (second test above) is the most important for verifying the invariant. After publishing v2:
- Call `GET /api/designers/{id}/versions/1` — verify `status == "Archived"`
- Call `GET /api/designers/{id}/versions/2` — verify `status == "Published"` and `publishedAt` is not null

Both GET endpoints already exist from Story 3.2. No new endpoint needed for assertions.

The `DesignerResponseDto` in tests needs a `PublishedAt` field for the publishedAt assertion. Extend the existing local record:
```csharp
private sealed record DesignerResponseDto(
    string DesignerId,
    string DisplayName,
    string Status,
    int LatestVersion,
    JsonElement? RootElement,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? PublishedAt);  // ADD THIS
```

### Frontend: designerApi.ts — Fix Stubs

The `publishVersion` and `archiveVersion` entries are currently stubs that POST to nonexistent routes. Replace both:

```typescript
// OLD (stubs — always 404)
publishVersion: (designerId: string, version: number) =>
  httpClient.post<void>(`/api/designers/${designerId}/versions/${version}/publish`, {}),

archiveVersion: (designerId: string, version: number) =>
  httpClient.post<void>(`/api/designers/${designerId}/versions/${version}/archive`, {}),

// NEW (Story 3.7 — real implementation)
publishVersion: (designerId: string, version: number) =>
  httpClient.put<ComponentSchemaDto>(
    `/api/designers/${designerId}/versions/${version}/status`,
    { status: 'Published' },
  ),

archiveVersion: (designerId: string, version: number) =>
  httpClient.put<ComponentSchemaDto>(
    `/api/designers/${designerId}/versions/${version}/status`,
    { status: 'Archived' },
  ),
```

Both return `ComponentSchemaDto` now (not `void`). The library mutations don't need to inspect the returned DTO — they just invalidate the list query on success — but the correct return type avoids silent any-cast in the future.

### Frontend: designer.library.tsx — Publish Action in RowMenu

The `RowMenu` component already has `archiveMutation` wired to `designerApi.archiveVersion`. Add a matching `publishMutation`:

```typescript
const publishMutation = useMutation({
  mutationFn: () => designerApi.publishVersion(row.designerId, row.latestVersion),
  onSuccess: () => {
    void qc.invalidateQueries({ queryKey: ['designer', 'list'] })
    toast.success(t('designer.library.publishSuccess'))
    setOpen(false)
  },
  onError: () => {
    toast.error(t('errors.genericError'))
    setOpen(false)
  },
})
```

Add a Publish button in the dropdown (before the Archive button, after the Duplicate button):

```tsx
import { CheckCircle2 } from 'lucide-react'

// ... inside the open menu:
<button
  type="button"
  role="menuitem"
  disabled={row.status === 'Published' || publishMutation.isPending}
  className="flex w-full items-center gap-2 px-3 py-2 text-left text-sm text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
  onClick={() => publishMutation.mutate()}
>
  <CheckCircle2 className="h-4 w-4" />
  {t('designer.library.publish')}
</button>
```

`CheckCircle2` is available from `lucide-react` — the file already imports other lucide icons. The publish button is disabled when:
- `row.status === 'Published'` — already published; publishing again would be a no-op (allowed by backend but no UI value in triggering it)
- `publishMutation.isPending` — request in flight

The `archived` variable already exists (`const archived = row.status === 'Archived'`). The Archive button's `disabled` guard is unchanged.

### Frontend: en.json — New Keys

Add inside `designer.library`:

```json
"publish": "Publish",
"publishSuccess": "Version published."
```

Also add to `designers` namespace (for backend messageKey resolution):

```json
"versionNotFound": "Version not found.",
"publishConflict": "A concurrent publish occurred. Reload and try again.",
"versionNotPublished": "Only Published versions can be bound."
```

### Do NOT Implement in Story 3.7

- **The Menu Binding endpoint** — that's Story 5.2. `VersionNotPublishedProblem()` is declared here but not consumed by any endpoint yet.
- **Schema Registry** (`ISchemaRegistry`, `SchemaRegistryCache`) — Epic 5. The `SchemaPublished` event is fired but has no subscriber yet.
- **Version history flyout in the library** — Story 3.8. The `Versions` array in `DesignerResponse` is only populated when `includeVersions: true` (via `GetLatestAsync`). The status endpoint uses `includeVersions: false`.
- **"New Version" from library row** — Story 3.8.
- **Duplicate** — Story 3.8. The `duplicateSchema` stub already exists and is non-functional; do not wire it.
- **Canvas-page Publish button** — The canvas designer (`designer.$designerId.tsx`) does NOT get a Publish button in this story. Publish is scoped to the Library page's row menu.
- **displayName rename on Publish** — not in scope; display name is only changed by a separate (not yet implemented) update endpoint.

### Previous Story Learnings

From Story 3.6:
- **Type strings with spaces**: component type strings in JSON have spaces (`"Text Input"` not `"TextInput"`). Not directly relevant to 3.7 (no fieldKey tree walk), but keep in mind if extending `FieldKeyValidator`.
- **`ToResponse` helper**: already exists in `DesignerService.cs` at line 255. Reuse it for the status update response — do NOT duplicate it.
- **`DesignerNotFoundProblem()` helper**: already exists in `DesignerEndpoints.cs` at line 181. Use it directly.
- **Test setup**: `CreateDesignerViaApiAsync` helper + `MinimalStackRoot()` exist in the test class. Use them to seed state before status tests.
- **`SaveVersionRequest` validator registration pattern**: the new `UpdateVersionStatusRequest` validator follows the same `builder.Services.AddScoped<IValidator<T>, TValidator>()` pattern.
- **Concurrent-save race catch pattern**: the `DbUpdateException` filter pattern (catch by `SqlState` + `ConstraintName`) is established in `SaveVersionAsync`. Use the same shape for the publish-conflict catch.
- **`IDomainEventBus` injection is Singleton**: `InProcessEventBus` is registered as Singleton (line 124 of `Program.cs`). `DesignerService` is Scoped. Singleton into Scoped is valid in .NET DI. Confirm via `dotnet build` — no lifetime warnings expected.

### Project Structure — Files Changed

**Backend — new**
- `src/FormForge.Api/Features/Designer/Dtos/UpdateVersionStatusRequest.cs`
- `src/FormForge.Api/Features/Designer/Validators/UpdateVersionStatusRequestValidator.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/[timestamp]_AddPublishedVersionUniqueIndex.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/[timestamp]_AddPublishedVersionUniqueIndex.Designer.cs`

**Backend — modified**
- `src/FormForge.Api/Infrastructure/EventBus/IDomainEventBus.cs` (add `SchemaPublished` record)
- `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` (add filtered index to ComponentSchemaVersion entity)
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/FormForgeDbContextModelSnapshot.cs` (auto-updated by EF CLI)
- `src/FormForge.Api/Features/Designer/DesignerService.cs` (add `IDomainEventBus` dep, new outcome/result types, new service method)
- `src/FormForge.Api/Features/Designer/DesignerEndpoints.cs` (add route, handler, new problem helpers; make `VersionNotPublishedProblem` internal)
- `src/FormForge.Api/Program.cs` (add `IValidator<UpdateVersionStatusRequest>` registration)
- `src/FormForge.Api.Tests/Features/Designer/DesignerIntegrationTests.cs` (10 new tests, extend `DesignerResponseDto` with `PublishedAt`, add 2 new helpers)

**Frontend — modified**
- `web/src/features/designer/designerApi.ts` (fix `publishVersion` + `archiveVersion` stubs)
- `web/src/routes/_app/designer.library.tsx` (add `publishMutation`, Publish button; add `CheckCircle2` import)
- `web/src/lib/i18n/locales/en.json` (add `designer.library.publish`, `designer.library.publishSuccess`, `designers.versionNotFound`, `designers.publishConflict`, `designers.versionNotPublished`)

**Sprint tracking — modified**
- `_bmad-output/implementation-artifacts/sprint-status.yaml` (3-7-version-status-management: backlog → ready-for-dev)

### Architecture Compliance

| Requirement | Implementation |
|---|---|
| AR-7 SchemaPublished fires on Publish | `eventBus.Publish(new SchemaPublished(...))` after `SaveChangesAsync` success |
| AR-47 Domain events are immutable records | `sealed record SchemaPublished(string DesignerId, int Version)` |
| AR-18 RFC 7807 error envelope | All errors use `Results.Problem(...)` with `code` + `messageKey` extensions |
| AR-22 Route group filter chain | Handler added via `group.MapPut(...)` inheriting group-level auth + validation |
| AR-32 httpClient.put used for PUT | Frontend uses `httpClient.put<ComponentSchemaDto>(...)` |
| AR-48 TanStack Query key convention | `['designer', 'list']` invalidated on publish/archive success |
| AR-49 Pessimistic mutation strategy | Status mutations are pessimistic (no optimistic update on list item) |
| FR-13 At-most-one-Published invariant | Partial unique index + in-process demote loop |

## Dev Agent Record

### Implementation Plan

1. EF model: added a partial unique index `uq_one_published_per_designer` on `component_schema_versions(designer_id) WHERE status='Published'`. Used the named-index overload `HasIndex(v => v.DesignerId, "name")` to disambiguate from the existing non-unique `idx_component_schema_versions_designer_id` — successive unnamed `HasIndex` calls on the same column set are coalesced by EF. The generated migration only `CreateIndex`es the new one (the existing index is preserved). Added the project's standard `ArgumentNullException.ThrowIfNull(migrationBuilder)` guards to the generated `Up`/`Down` for CA1062 compliance.
2. Domain event: added `SchemaPublished(string DesignerId, int Version)` record to `IDomainEventBus.cs`. No subscriber wired in 3.7 — `InProcessEventBus.Publish` is a no-op when no handlers are registered.
3. DTO + validator: `UpdateVersionStatusRequest(string Status)` with FluentValidation that rejects empty and any value not in `["Published","Archived"]`. Draft is intentionally absent (versions only enter Draft at creation/save).
4. Service: added `UpdateVersionStatusOutcome` (Success/DesignerNotFound/VersionNotFound/StatusUnchanged/PublishConflict) + `UpdateVersionStatusResult`. Constructor extended to accept `IDomainEventBus` (Singleton-into-Scoped is legal). `UpdateVersionStatusAsync` loads `Include(Versions)`, returns StatusUnchanged on same-status no-op (no write, no event), demotes any prior Published version, then promotes the target with `PublishedAt=now`. Fires `SchemaPublished` only after commit succeeds.
5. Endpoint: PUT `/api/designers/{designerId}/versions/{version:int}/status` with `RequireAuthorization("platform-admin")` and `AddValidationFilter<UpdateVersionStatusRequest>`. Switch maps outcomes to 200 / 404 (VERSION_NOT_FOUND or DESIGNER_NOT_FOUND) / 409 (PUBLISH_CONFLICT) / 500. New helpers `VersionNotFoundProblem`, `PublishConflictProblem`, and `internal static VersionNotPublishedProblem` (AC-9, pre-positioned for Story 5.2 reuse). Validator registered in `Program.cs`.
6. Tests: 10 new integration tests covering AC-1 through AC-8 + the auto-demote invariant. Added `CreateVersionViaApiAsync` and `PutVersionStatusAsync` helpers. Extended local `DesignerResponseDto` with `PublishedAt`.
7. Top-level `publishedAt` on `DesignerResponse`: the integration test asserts `Assert.NotNull(body.PublishedAt)` per the dev notes; that required adding `PublishedAt` to the top-level DTO (passed `version.PublishedAt` in `ToResponse`) and to the matching TS interface `ComponentSchemaDto`. Two existing TS sites that synthesize a `ComponentSchemaDto` literal (`ComponentPreviewModal`, `designerCanvas.test.ts`) were extended with `publishedAt: null`.
8. Frontend API: replaced the 404-prone POST `/publish` / `/archive` stubs in `designerApi.ts` with PUT `/status` returning `ComponentSchemaDto`.
9. Frontend UI: `publishMutation` added to `RowMenu` in `designer.library.tsx` with the same onSuccess/onError shape as `archiveMutation` (invalidate `['designer','list']`, success/error toast, close menu). New `Publish` button (CheckCircle2 icon) inserted between Duplicate and Archive; disabled when `row.status === 'Published'` or `publishMutation.isPending`.
10. i18n: `designer.library.publish` / `publishSuccess` plus the three `designers.*` backend messageKeys (`versionNotFound`, `publishConflict`, `versionNotPublished`).

### Debug Log

- **Two-phase commit for publish (test failure → fix)**: the first test run failed on `PublishVersion_ArchivedVersion_Succeeds` (got 409 instead of 200). Root cause: PostgreSQL evaluates `uq_one_published_per_designer` per-statement, and EF batches the demote+promote UPDATEs from a single SaveChanges in an order that transiently leaves two Published rows visible to the index check (most reliably reproducible in the Archived→Published re-publish path because both the demote target and the promote target start with `published_at` set). Refactored `UpdateVersionStatusAsync` to split the demote and promote across two `SaveChangesAsync` calls inside an explicit `BeginTransactionAsync` / `CommitAsync` block. The demote SaveChanges fully resolves to zero Published rows before the promote SaveChanges runs, so per-statement index checks always see a valid state. Atomicity is preserved by the explicit transaction. Switched from `await using` to a `try/finally` with `await tx.DisposeAsync().ConfigureAwait(false)` to satisfy CA2007.
- **EF named-index overload (avoided destructive migration)**: first migration attempt used `HasIndex(v => v.DesignerId)` twice (once for the existing non-unique, once for the new filtered unique). EF coalesced them and the generated migration dropped the existing index. Reverted the snapshot for that entity, switched both `HasIndex` calls to the named overload `HasIndex(expr, name)`, and regenerated — the resulting migration cleanly only `CreateIndex`es the new filtered unique index.

### Completion Notes

- All 9 ACs (AC-1 through AC-9) satisfied:
  - AC-1: Publish Draft → 200, publishedAt set, prior Published auto-demoted, SchemaPublished fires (DesignerService.cs:298–310)
  - AC-2: Archive Published/Draft → 200, status "Archived" (DesignerService.cs:329–333)
  - AC-3: Archived → Published succeeds; invariant maintained (test `PublishVersion_ArchivedVersion_Succeeds`)
  - AC-4: Same-status no-op → 200 OK, no write, no event (DesignerService.cs:270–276)
  - AC-5: Draft and unknown statuses → 422 via FluentValidation (UpdateVersionStatusRequestValidator.cs)
  - AC-6: Version not found → 404 VERSION_NOT_FOUND (DesignerEndpoints.cs:230)
  - AC-7: Designer not found → 404 DESIGNER_NOT_FOUND (DesignerEndpoints.cs:229, reuses existing helper)
  - AC-8: Non-admin → 403 via `RequireAuthorization("platform-admin")` filter
  - AC-9: `VersionNotPublishedProblem()` declared as `internal static` for Story 5.2 reuse (DesignerEndpoints.cs:227–236)
- 227/227 backend tests pass (10 new for 3.7, 0 regressions). 47/47 vitest pass. Frontend build green; lint within story budget (24 react-refresh + 3 other, all pre-existing).
- Two divergences from the dev-notes snippets, both documented above: (a) two-phase commit for the publish path (the dev-notes single-SaveChanges snippet would fail the re-publish test under PostgreSQL's per-statement index check); (b) added top-level `PublishedAt` to `DesignerResponse` and `ComponentSchemaDto` because the dev-notes test DTO assumed a top-level field that didn't exist yet.

### File List

**Backend — new**
- `src/FormForge.Api/Features/Designer/Dtos/UpdateVersionStatusRequest.cs`
- `src/FormForge.Api/Features/Designer/Validators/UpdateVersionStatusRequestValidator.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260523235900_AddPublishedVersionUniqueIndex.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260523235900_AddPublishedVersionUniqueIndex.Designer.cs`

**Backend — modified**
- `src/FormForge.Api/Infrastructure/EventBus/IDomainEventBus.cs` (added `SchemaPublished` record)
- `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` (filtered unique index + named-index overload for the existing non-unique index)
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/FormForgeDbContextModelSnapshot.cs` (EF auto-updated for the new index)
- `src/FormForge.Api/Features/Designer/DesignerService.cs` (added IDomainEventBus dep, UpdateVersionStatusOutcome/Result, UpdateVersionStatusAsync with two-phase commit, top-level PublishedAt in ToResponse)
- `src/FormForge.Api/Features/Designer/DesignerEndpoints.cs` (added PUT /status route, UpdateVersionStatusHandler, VersionNotFoundProblem/PublishConflictProblem/VersionNotPublishedProblem helpers)
- `src/FormForge.Api/Features/Designer/Dtos/DesignerResponse.cs` (added top-level `PublishedAt`)
- `src/FormForge.Api/Program.cs` (registered `IValidator<UpdateVersionStatusRequest>`)
- `src/FormForge.Api.Tests/Features/Designer/DesignerIntegrationTests.cs` (10 new tests; PutVersionStatusAsync/CreateVersionViaApiAsync helpers; PublishedAt on DesignerResponseDto)

**Frontend — modified**
- `web/src/features/designer/designerApi.ts` (publishVersion/archiveVersion now PUT /status, return ComponentSchemaDto)
- `web/src/routes/_app/designer.library.tsx` (publishMutation in RowMenu; Publish button with CheckCircle2 icon)
- `web/src/types/designer.ts` (top-level `publishedAt` on ComponentSchemaDto)
- `web/src/components/designer/ComponentPreviewModal.tsx` (synthesized literal needed `publishedAt: null` after the type change)
- `web/src/store/__tests__/designerCanvas.test.ts` (test fixture needed `publishedAt: null` after the type change)
- `web/src/lib/i18n/locales/en.json` (added `designer.library.publish`, `designer.library.publishSuccess`, `designers.versionNotFound`, `designers.publishConflict`, `designers.versionNotPublished`)

**Sprint tracking — modified**
- `_bmad-output/implementation-artifacts/sprint-status.yaml` (3-7-version-status-management: ready-for-dev → in-progress → review)

### Review Findings (2026-05-24, 3-reviewer parallel pass)

**Decision-needed** (resolved 2026-05-24):

- [x] [Review][Decision] **AC-5 error code: spec says `STATUS_INVALID`** — resolved: handler pre-check returns `STATUS_INVALID` for `"Draft"` before the FluentValidation filter runs. Validator keeps generic `VALIDATION_FAILED` for unknown statuses. See patch P5 + dedicated test assertion.
- [x] [Review][Decision] **Frontend `publishMutation` uses possibly-stale `row.latestVersion`** — resolved: show `Publish v{row.latestVersion}` in the row-menu button label so the user sees which version they're about to publish. Smallest UX surface change. See patch P9.
- [x] [Review][Decision] **`PublishedAt` semantics on Archive** — resolved: preserve on Archive (column means "most recently published at" — history). Document in `DesignerResponse` XML doc. Consumers that need "is currently published" filter on `status == 'Published'`. Patch P8 asserts `v1.PublishedAt != null` after demote. See patch P10.

**Patches** (unambiguous fixes):

- [x] [Review][Patch] **Add SchemaPublished event-firing tests (AC-1, AR-7)** [src/FormForge.Api.Tests/Features/Designer/DesignerIntegrationTests.cs] — AR-7 / AC-1's most architecturally important side-effect is untested. Register a recording `IDomainEventBus` fake in the test fixture; assert `SchemaPublished(designerId, version)` is published on (i) Draft→Published, (ii) Archived→Published, and that no event fires on AC-4 same-status no-op.
- [x] [Review][Patch] **Wire `publishMutation.onError` / `archiveMutation.onError` to surface backend `messageKey`** [web/src/routes/_app/designer.library.tsx] — both mutations swallow 409 `PUBLISH_CONFLICT`, 404 `VERSION_NOT_FOUND`, and 422s into `t('errors.genericError')`. The `designers.publishConflict` / `designers.versionNotFound` keys are added to en.json but never read. Reuse the Story 3.6 `ApiError.messageKey` decoder pattern.
- [x] [Review][Patch] **Add DB `CHECK` constraint pinning status casing** [src/FormForge.Api/Infrastructure/Persistence/Migrations/20260523235900_AddPublishedVersionUniqueIndex.cs] — partial unique index filter `(status = 'Published')` is case-sensitive; in-memory demote `string.Equals(v.Status, "Published", Ordinal)` is also case-sensitive. A lower-case `"published"` row (manual SQL, future enum-serializer change, import) silently bypasses both, violating FR-13. Add `CHECK (status IN ('Draft','Published','Archived'))` in the same migration's Up; drop in Down.
- [x] [Review][Patch] **Add AC-2 Draft → Archived integration test** [src/FormForge.Api.Tests/Features/Designer/DesignerIntegrationTests.cs] — AC-2 precondition is "Published OR Draft", but the only Archive test covers Published→Archived. Add `ArchiveVersion_DraftVersion_Returns200WithArchivedStatus`.
- [x] [Review][Patch] **AC-5 handler pre-check + strengthened test assertion (resolves D1)** [src/FormForge.Api/Features/Designer/DesignerEndpoints.cs + DesignerIntegrationTests.cs:763-773] — add `StatusInvalidProblem()` helper returning 422 with `code: "STATUS_INVALID"`, `messageKey: "designers.statusInvalid"`. In `UpdateVersionStatusHandler`, after `ArgumentNullException.ThrowIfNull(request)` and before calling the service, if `request.Status == "Draft"`, return `StatusInvalidProblem()`. Strengthen the Draft test to assert `code == "STATUS_INVALID"` (parsed from the RFC 7807 envelope). Add `designers.statusInvalid` to `en.json`. Unknown statuses ("foo") still go through the validator and return `VALIDATION_FAILED` — that test stays as-is.
- [x] [Review][Patch] **Add unauthenticated 401 test** [src/FormForge.Api.Tests/Features/Designer/DesignerIntegrationTests.cs] — `PutVersionStatusAsync` has a `token.Length > 0` branch but no test exercises the empty-token path. Add `UpdateVersionStatus_Unauthenticated_Returns401`. Validates auth-pipeline regression detection.
- [x] [Review][Patch] **AC-4 no-write verification** [src/FormForge.Api.Tests/Features/Designer/DesignerIntegrationTests.cs:833-848] — `PublishVersion_AlreadyPublished_Returns200NoWrite` only asserts 200 + status. Snapshot the version row's `UpdatedAt` (or schema's `UpdatedAt`) before the second PUT, assert unchanged after. Event-no-fire assertion piggybacks on the SchemaPublished fake added in the first patch.
- [x] [Review][Patch] **AC-1 auto-demote test should assert `v1.PublishedAt` preserved (resolves D3)** [src/FormForge.Api.Tests/Features/Designer/DesignerIntegrationTests.cs:86] — `PublishVersion_ExistingPublishedPresent_AutoDemotesOldToArchived` checks `v1Body.Status == "Archived"` but never inspects `PublishedAt`. Per D3 (preserve-on-Archive), assert `v1Body.PublishedAt != null` to pin that the demote loop preserves history.
- [x] [Review][Patch] **Show `Publish v{row.latestVersion}` in row-menu button label (resolves D2)** [web/src/routes/_app/designer.library.tsx] — makes which version is about to be published visible to the user, narrowing the stale-data UX gap. Also add `designer.library.publishVersionLabel` i18n key (e.g., `"Publish v{{version}}"`) for the formatted string.
- [x] [Review][Patch] **Document `PublishedAt` semantics on `DesignerResponse` + `ComponentSchemaDto` (supports D3)** [src/FormForge.Api/Features/Designer/Dtos/DesignerResponse.cs + web/src/types/designer.ts] — XML doc / TSDoc comment on the `PublishedAt` field: "Timestamp of the most recent publish for this designer's currently-Published version, or for an Archived version, when it was last published. Filter `status == 'Published'` to identify the currently-published version; do not infer it from `PublishedAt != null`."

**Deferred** (real, not blocking this story):

- [x] [Review][Defer] `updatedBy` parameter plumbed but discarded with `_ = updatedBy;` — per spec; awaits Story 5.x schema audit-log subscriber
- [x] [Review][Defer] `PublishConflict` (DbUpdateException 23505) catch branch has no integration test — requires parallel scoped DbContexts; hard to make deterministic
- [x] [Review][Defer] Brownfield migration data cleanup pre-flight — if any env has duplicate `Published` rows, `CREATE UNIQUE INDEX` will fail; greenfield project today
- [x] [Review][Defer] Migration uses `CREATE INDEX`, not `CREATE INDEX CONCURRENTLY` — designer table is small; revisit if it grows
- [x] [Review][Defer] `idx_component_schema_versions_designer_id` (non-unique, single-col) appears redundant alongside the existing composite `(designer_id, version)` unique index — pre-existing, not introduced by 3.7
- [x] [Review][Defer] `Include(s => s.Versions)` materializes all version rows (including `root_element` jsonb) on every status PUT — spec acknowledges "version count per designer is small"; revisit when version-history flyout lands (Story 3.8)
- [x] [Review][Defer] Post-commit `eventBus.Publish` is fire-and-forget — classic dual-write hazard if process dies between commit and publish; canonical fix is transactional outbox; no subscriber exists today, so impact is zero
- [x] [Review][Defer] No `CancellationToken` mid-publish rollback test — hard to make deterministic across the two-phase commit boundary
- [x] [Review][Defer] No "missing body" integration test for the PUT — `ValidationFilter` infrastructure covers it elsewhere; not 3.7-specific
- [x] [Review][Defer] Archive button on the currently-Published row silently revokes publication with no confirm dialog — UX hardening for Story 3.8 or Epic 7

## Change Log

| Date | Change |
|------|--------|
| 2026-05-24 | Story 3.7 created — ready-for-dev |
| 2026-05-24 | Story 3.7 implemented — status → review. Added Story 3.7 endpoint + filtered unique index + 10 integration tests + frontend Publish UI. Two-phase commit refactor for the publish path (per-statement index check under PostgreSQL). Top-level `publishedAt` added to DesignerResponse/ComponentSchemaDto. All ACs satisfied; 227/227 backend + 47/47 vitest pass. |
| 2026-05-24 | Code review (3-reviewer parallel pass: Blind Hunter, Edge Case Hunter, Acceptance Auditor). 3 decision-needed (resolved), 10 patches applied, 10 deferred, 15 dismissed. Backend 229/229 + frontend 47/47 pass; status → done. |
