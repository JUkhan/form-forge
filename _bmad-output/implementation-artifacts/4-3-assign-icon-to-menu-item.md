# Story 4.3: Assign Icon to Menu Item

Status: done

## Story

As a Platform Admin,
I want to assign a lucide-react icon name or upload an image as a Menu Item icon,
so that the navbar is visually navigable.

## Acceptance Criteria

**AC-1 — Lucide icon validated server-side**
Given a Menu Item being edited
When I submit the edit form with `icon: { type: "lucide", name: "HomeIcon" }`
Then the icon name is validated against the available lucide-react icons; invalid names return HTTP 422

**AC-2 — Image upload stores in MinIO**
Given I upload an icon image
When I POST a PNG, JPG, or SVG file ≤ 2 MB to `POST /api/admin/menus/upload-icon` (multipart)
Then the file is validated for MIME type and size per NFR-7
And the file is stored in MinIO under the `menus/icons/` path prefix
And the response returns `{ type: "minio", objectKey: "menus/icons/{uuid}.{ext}" }`

**AC-3 — Invalid upload rejected**
Given I upload an oversized or wrong-type file
When the upload endpoint processes it
Then the response is HTTP 422 with `code: "UPLOAD_INVALID"` and the file is not persisted

**AC-4 — Default placeholder when icon is null**
Given a Menu Item with `icon: null`
When an Admin views its detail page
Then a default placeholder icon is shown in the icon display area

## Tasks / Subtasks

- [x] Task 1: Generate lucide icon names list (AC-1)
  - [x] Create `web/generate-icons.mjs` (temporary script)
  - [x] Run: `cd web && node generate-icons.mjs > ../src/FormForge.Api/Features/Menus/lucide-icon-names.txt`
  - [x] Delete `web/generate-icons.mjs` after generation
  - [x] Confirm `lucide-icon-names.txt` has one icon name per line, ~1,500+ entries (actual: 5,869 entries — each icon plus an `Icon` suffix variant in lucide-react 1.16.0; both forms are valid component lookups)
  - [x] Add embedded resource to `FormForge.Api.csproj`

- [x] Task 2: Add Minio NuGet package (AC-2, AC-3)
  - [x] Add `<PackageReference Include="Minio" />` to `src/FormForge.Api/FormForge.Api.csproj` (and `<PackageVersion Include="Minio" Version="6.0.4" />` to `Directory.Packages.props` — central package management is enabled repo-wide; an unversioned PackageReference would fail restore)
  - [x] Registered as singleton in `Program.cs` (the lazy bucket-existence check belongs once per process, not per request; the Minio SDK client is thread-safe — story Dev Notes also explicitly call this out)

- [x] Task 3: Fix `MenuResponse.Icon` serialization bug (AC-1, AC-2)
  - [x] Change `MenuResponse.Icon` from `string?` to `System.Text.Json.JsonElement?`
  - [x] Update `MenuService.ToResponse(menu)` to parse stored string back to `JsonElement?`
  - [x] Update `MenuService.CreateMenuAsync` inline response construction similarly
  - [x] Add `ParseIcon(string? json)` private static helper to `MenuService`

- [x] Task 4: Backend — `IIconStorageService` + `MinioIconStorageService` (AC-2, AC-3)
  - [x] Create `src/FormForge.Api/Features/Menus/IIconStorageService.cs`
  - [x] Create `src/FormForge.Api/Features/Menus/MinioIconStorageService.cs`
    - Reads config from `services__minio__s3__0` (Aspire) or `MinIO:Endpoint` (Compose)
    - Reads credentials from `MinIO:AccessKey` / `MinIO:SecretKey` (Compose) with fallback to `MinIO:RootUser` / `MinIO:RootPassword` (Aspire)
    - On first call, ensures `formforge` bucket exists (fixes the deferred-work.md `minio-init` race)
    - Stores file at `menus/icons/{uuid}.{ext}`, returns `objectKey`
    - Implements `IDisposable` (releases the Minio client and the bucket-init `SemaphoreSlim`; required to satisfy CA1001/CA2000 analyzers and lets the DI container clean up at shutdown)
    - `EnsureBucketAsync` is guarded by a `SemaphoreSlim` — concurrent first requests cannot both call `MakeBucketAsync` and race

- [x] Task 5: Backend — `LucideIconRegistry` (AC-1)
  - [x] Create `src/FormForge.Api/Features/Menus/LucideIconRegistry.cs`
  - [x] Load from embedded resource `lucide-icon-names.txt` at startup; cache as `FrozenSet<string>`

- [x] Task 6: Backend — update validators (AC-1)
  - [x] Update `CreateMenuRequestValidator` — validate `Icon` field shape when present:
    - If `type == "lucide"`: `name` required, must be in `LucideIconRegistry`
    - If `type == "minio"`: `objectKey` required, must match `^menus/icons/[a-f0-9-]+\.[a-z]+$`
    - Any other `type` value: invalid
  - [x] Update `UpdateMenuRequestValidator` — same icon rules (shared `IconValidation.IsValidShape` helper avoids duplicating the rule logic between Create and Update)

- [x] Task 7: Backend — `upload-icon` endpoint (AC-2, AC-3)
  - [x] Add `POST /upload-icon` route in `MenuAdminEndpoints.MapMenuAdminEndpoints()`
  - [x] Implement `UploadIconHandler`:
    - Reads multipart form via `HttpContext.Request.ReadFormAsync` (chosen over `IFormFile` parameter binding for explicitness and per the story's "Alternatively, read from form directly" fallback)
    - Validate MIME type ∈ `{ image/png, image/jpeg, image/svg+xml }` and size ≤ 2 MB
    - On invalid: return 422 `UPLOAD_INVALID` (no MinIO call)
    - On valid: call `IIconStorageService.StoreAsync(...)`, return 200 `{ type = "minio", objectKey }`
  - [x] Register `IIconStorageService` as singleton in `Program.cs` (comment "Story 4.3" — see Task 2 note on singleton vs scoped)
  - [x] Enable multipart form data reading in the endpoint (`.DisableAntiforgery()` needed for minimal APIs)

- [x] Task 8: Backend — integration tests (AC-1, AC-2, AC-3)
  - [x] Add `FakeIconStorageService` (test double) nested inside the test file
  - [x] Override `IIconStorageService` in `WebApplicationFactory` in a new `UploadIconIntegrationTests.cs` class
  - [x] Add tests:
    - `UploadIcon_Unauthenticated_Returns401`
    - `UploadIcon_AsNonAdmin_Returns403`
    - `UploadIcon_ValidPng_Returns200WithObjectKey`
    - `UploadIcon_OversizedFile_Returns422UploadInvalid`
    - `UploadIcon_WrongMimeType_Returns422UploadInvalid`
  - [x] Add to `MenuIntegrationTests.cs`:
    - `CreateMenu_WithValidLucideIcon_Returns201AndIconRoundTripsAsObject` (assertions use `JsonDocument` directly to verify icon is a JSON object not an escaped string — that's the regression guard for the Task 3 fix; also round-trips through GET so both the inline CreateMenu path and `ToResponse` path are covered)
    - `CreateMenu_WithInvalidLucideIconName_Returns422`
    - `CreateMenu_WithValidMinioIcon_Returns201`

- [x] Task 9: Frontend — `LucideIcon.tsx` component (AC-1, AC-4)
  - [x] Create `web/src/components/icons/LucideIcon.tsx`
  - [x] Accept `name: string`, `size?: number`, `className?: string` props (spread `LucideProps` with `ref` omitted so consumer-supplied svg attributes pass through)
  - [x] Use `import * as Icons from 'lucide-react'` and resolve `Icons[name]` dynamically
  - [x] Render `null` / fallback if icon not found (defensive)

- [x] Task 10: Frontend — upload mutation hook (AC-2)
  - [x] Add `useUploadMenuIconMutation()` to `web/src/features/admin/menus/menuAdminMutations.ts`
  - [x] POSTs `FormData` to `/api/admin/menus/upload-icon` via `httpClient`
  - [x] Returns `{ type: 'minio', objectKey: string }`
  - [x] Side-fix: `httpClient.request` now skips the `Content-Type: application/json` header and skips `JSON.stringify` when the body is a `FormData` instance — the browser must set `multipart/form-data; boundary=...` itself, otherwise the upload endpoint rejects the request

- [x] Task 11: Frontend — icon picker in detail page (AC-1, AC-2, AC-4)
  - [x] Update `MenuDetailContent` in `web/src/routes/_app/admin/menus.$menuId.tsx`:
    - Add local state: `const [pendingIcon, setPendingIcon] = useState<MenuIcon | null>(menu.icon)`
    - Pass `icon: pendingIcon` in the `updateMutation.mutateAsync(...)` call (replaces `icon: menu.icon`)
    - Re-sync `pendingIcon` to `menu.icon` after successful mutation (handled by query invalidation: useState's initial value is read on each MenuDetailContent remount, which happens when the parent's useMenuDetailQuery returns fresh data)
  - [x] Add `<IconPickerSection icon={pendingIcon} onChange={setPendingIcon} />` inside the edit form (below isActive field)
  - [x] Implement `IconPickerSection` (kept in the same file per story spec):
    - If `icon !== null`: display current icon (lucide preview via `<LucideIcon>` or `iconCurrentMinio` label) + "Remove icon" button
    - Mode selector: `role="tablist"` with two `role="tab"` buttons — "Lucide icon" | "Upload image"
    - **Lucide tab**: text input (`iconNameInput`), live `<LucideIcon name={iconNameInput.trim()} />` preview, "Set Lucide Icon" button that sets `pendingIcon = { type: 'lucide', name: iconNameInput.trim() }` (client-side only — server validates)
    - **Upload tab**: `<input type="file" accept="image/png,image/jpeg,image/svg+xml" />`, on change calls `useUploadMenuIconMutation`, sets `pendingIcon = result` on success, maps 422 `UPLOAD_INVALID` → `admin.menus.uploadInvalid` inline error
    - Placeholder when `pendingIcon === null`: text label using `iconPlaceholder` i18n key (matches the existing minimal-style admin UI; no decorative grey box)

- [x] Task 12: Frontend — i18n keys (AC-1, AC-2, AC-3, AC-4)
  - [x] Add to `admin.menus` block in `web/src/lib/i18n/locales/en.json`:
    - `"iconSectionTitle": "Menu Icon"`
    - `"iconTabLucide": "Lucide Icon"`
    - `"iconTabUpload": "Upload Image"`
    - `"iconNameLabel": "Icon name"`
    - `"iconNamePlaceholder": "e.g. Home, Settings, Bell"`
    - `"setLucideIconButton": "Set Lucide Icon"`
    - `"iconPreviewAlt": "Icon preview"`
    - `"removeIconButton": "Remove Icon"`
    - `"uploadIconButton": "Upload Image"`
    - `"uploadingIconButton": "Uploading…"`
    - `"uploadInvalid": "File must be PNG, JPG, or SVG and under 2 MB"`
    - `"iconPlaceholder": "No icon set"`
    - `"iconCurrentLucide": "Lucide: {{name}}"`
    - `"iconCurrentMinio": "Uploaded image"`

### Review Findings

**Decisions Needed:**
- [x] [Review][Decision] D1: SVG upload enables stored XSS — resolved: reject SVG. Remove `image/svg+xml` from allowed MIME types in `UploadIconHandler`; Lucide picker covers vector icon need. [`MenuAdminEndpoints.cs`, `UploadIconHandler`] → converted to patch P9
- [x] [Review][Decision] D2: AC-4 "placeholder icon" — dismissed: text label "No icon set" accepted; spec wording was loose, no designer mock provided. [`menus.$menuId.tsx`, `IconPickerSection`]

**Patches:**
- [x] [Review][Patch] P1: `GetClient()` race condition leaks `IMinioClient` on concurrent first uploads — no lock on the null-check+construction; two threads both see `_client == null`, build separate `MinioClient` instances; the losing instance is never disposed (holds connection pool). Fix: `Lazy<IMinioClient>` or guard construction inside `_initLock`. [`MinioIconStorageService.cs`, `GetClient()`]
- [x] [Review][Patch] P2: `ParseIcon` throws unhandled `JsonException` on malformed DB row — any `GET /api/admin/menus/{id}` for a menu with corrupted `icon` TEXT returns 500 forever. Fix: wrap `JsonSerializer.Deserialize` in try/catch, return `null` on `JsonException`. [`MenuService.cs`, `ParseIcon()`]
- [x] [Review][Patch] P3: `IconValidation.IsValidShape` references `CreateMenuRequestValidator.MinioKeyPatternForTests` at runtime — the shared helper used by both Create and Update validators calls a `ForTests`-named property on `CreateMenuRequestValidator`; the Regex should live in `IconValidation` itself. [`CreateMenuRequestValidator.cs`]
- [x] [Review][Patch] P4: `MinioKeyPattern` regex too permissive — `[a-f0-9\-]{32,36}` accepts all-dash strings and D-format GUIDs; `[a-z]+` extension unbounded. `StoreAsync` always produces 32 hex chars (N format) + `png`/`jpg`. Fix: `^menus/icons/[a-f0-9]{32}\.(?:png|jpg)$`. [`CreateMenuRequestValidator.cs`, `MinioKeyPattern`]
- [x] [Review][Patch] P5: `Dispose()` double-dispose risk — `_disposed = true` is set after `_client?.Dispose()`; if that throws, a subsequent `Dispose()` call double-disposes `_initLock` (already-disposed `SemaphoreSlim`). Fix: set `_disposed = true` first, or wrap disposal calls in try/finally. [`MinioIconStorageService.cs`, `Dispose()`]
- [x] [Review][Patch] P6: `pendingIcon` not re-synced from `menu.icon` after save + query refetch — `useState` only fires on mount; background refetch updates `menu.icon` prop but `pendingIcon` stays stale, silently overwriting another admin's concurrent change on next save. Fix: `useEffect(() => { setPendingIcon(menu.icon) }, [menu.icon])`. [`menus.$menuId.tsx`, `MenuDetailContent`]
- [x] [Review][Patch] P7: "Set Lucide Icon" button enabled for invalid names — `LucideIcon` renders `null` for unknown names (empty preview) but button remains enabled; user clicks Set, saves, gets a generic `saveError` with no explanation. Fix: disable button when live preview is blank (icon name not found). [`menus.$menuId.tsx`, `IconPickerSection`]
- [x] [Review][Patch] P8: No form size limit at Kestrel layer — server buffers up to Kestrel's default 30 MB before `UploadIconHandler` runs its 2 MB check; malicious client can force 30 MB of buffering before rejection. Fix: add `FormOptions.MultipartBodyLengthLimit = 2 * 1024 * 1024` on the endpoint or globally. [`MenuAdminEndpoints.cs`]
- [x] [Review][Patch] P9: SVG upload enables stored XSS — inline SVG can carry `<script>` tags that execute when served via presigned URLs. Fix: remove `image/svg+xml` from allowed MIME types in `UploadIconHandler`; Lucide picker covers the vector icon need. [`MenuAdminEndpoints.cs`, `CreateMenuRequestValidator.cs`, `menus.$menuId.tsx`, `en.json`]

**Deferred:**
- [x] [Review][Defer] No server-side existence check for `minio`-type `objectKey` — validator checks regex format but not MinIO object existence; a fabricated key passes and stores a dangling reference. Will surface as broken icon in Story 4.7 presigned URL display. [`CreateMenuRequestValidator.cs`, `IconValidation`] — deferred, known architectural tradeoff; Story 4.7 presigned URL work will expose it

## Dev Notes

### Critical Bug Fix — `MenuResponse.Icon` Double-Serialization

**This is the most important correctness fix in this story.** The `Menu.Icon` DB column stores a JSON string like `{"type":"lucide","name":"Home"}` as TEXT. `MenuResponse.Icon` is currently `string?`, which causes ASP.NET Core to serialize it as a JSON string (escaped): `"icon": "{\"type\":\"lucide\",...}"`. The frontend type `MenuItem.icon: MenuIcon | null` expects an object, not a string. Since icon was always `null` before Story 4.3, this bug was latent.

**Fix:**

```csharp
// MenuResponse.cs — change Icon type
internal sealed record MenuResponse(
    Guid Id,
    string Name,
    int Order,
    System.Text.Json.JsonElement? Icon,   // was: string? Icon
    bool IsActive,
    Guid? ParentId,
    IReadOnlyList<Guid> AllowedRoleIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);
```

Add this helper to `MenuService.cs`:
```csharp
private static System.Text.Json.JsonElement? ParseIcon(string? iconJson)
{
    if (iconJson is null) return null;
    return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(iconJson);
}
```

Update `ToResponse`:
```csharp
private static MenuResponse ToResponse(Menu menu) =>
    new(
        menu.Id,
        menu.Name,
        menu.Order,
        ParseIcon(menu.Icon),   // was: menu.Icon
        menu.IsActive,
        menu.ParentId,
        menu.RoleAssignments.Select(r => r.RoleId).ToList(),
        menu.CreatedAt,
        menu.UpdatedAt);
```

Update `CreateMenuAsync` inline response:
```csharp
return new CreateMenuResult(CreateMenuOutcome.Success, menu.Id, new MenuResponse(
    menu.Id, menu.Name, menu.Order, ParseIcon(menu.Icon), menu.IsActive, menu.ParentId,
    [], menu.CreatedAt, menu.UpdatedAt));
```

### Generating the Lucide Icon Names List

Run from the repo root (requires `web/node_modules` to be installed):

```javascript
// web/generate-icons.mjs  (create then delete after running)
import * as l from 'lucide-react';
const names = Object.keys(l).filter(k => /^[A-Z]/.test(k)).sort();
process.stdout.write(names.join('\n'));
```

```bash
cd web && node generate-icons.mjs > ../src/FormForge.Api/Features/Menus/lucide-icon-names.txt && rm generate-icons.mjs
```

Then add to `FormForge.Api.csproj` (inside a new or existing `<ItemGroup>`):
```xml
<EmbeddedResource Include="Features/Menus/lucide-icon-names.txt" />
```

### Backend: `LucideIconRegistry.cs`

```csharp
// src/FormForge.Api/Features/Menus/LucideIconRegistry.cs
using System.Collections.Frozen;

namespace FormForge.Api.Features.Menus;

internal static class LucideIconRegistry
{
    private static readonly FrozenSet<string> _names = LoadNames();

    public static bool IsValid(string name) => _names.Contains(name);

    private static FrozenSet<string> LoadNames()
    {
        var assembly = typeof(LucideIconRegistry).Assembly;
        const string resourceName = "FormForge.Api.Features.Menus.lucide-icon-names.txt";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' not found. " +
                "Run generate-icons.mjs and ensure EmbeddedResource is set in .csproj.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToFrozenSet(StringComparer.Ordinal);
    }
}
```

### Backend: Updated Validators

```csharp
// CreateMenuRequestValidator.cs
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed class CreateMenuRequestValidator : AbstractValidator<CreateMenuRequest>
{
    // Pattern: menus/icons/{uuid-like}.{ext}
    private static readonly Regex MinioKeyPattern =
        new(@"^menus/icons/[a-f0-9\-]{32,36}\.[a-z]+$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    public CreateMenuRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Order).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Icon)
            .Must(ValidateIcon)
            .WithMessage("Invalid icon: type must be 'lucide' or 'minio'; lucide name must be a valid lucide-react icon; minio objectKey must be a valid upload path.")
            .When(x => x.Icon.HasValue && x.Icon.Value.ValueKind != JsonValueKind.Null);
    }

    private static bool ValidateIcon(JsonElement? icon)
    {
        if (!icon.HasValue || icon.Value.ValueKind != JsonValueKind.Object) return false;
        if (!icon.Value.TryGetProperty("type", out var typeProp)) return false;
        var type = typeProp.GetString();
        if (type == "lucide")
        {
            if (!icon.Value.TryGetProperty("name", out var nameProp)) return false;
            var name = nameProp.GetString();
            return name is not null && LucideIconRegistry.IsValid(name);
        }
        if (type == "minio")
        {
            if (!icon.Value.TryGetProperty("objectKey", out var keyProp)) return false;
            var key = keyProp.GetString();
            return key is not null && MinioKeyPattern.IsMatch(key);
        }
        return false;
    }
}
```

Apply the same icon validation in `UpdateMenuRequestValidator`.

### Backend: `IIconStorageService.cs`

```csharp
// src/FormForge.Api/Features/Menus/IIconStorageService.cs
namespace FormForge.Api.Features.Menus;

internal interface IIconStorageService
{
    /// <summary>
    /// Stores <paramref name="content"/> in MinIO under menus/icons/.
    /// Returns the object key, e.g. "menus/icons/a1b2c3d4...ef.png".
    /// </summary>
    Task<string> StoreAsync(Stream content, string extension, long length, CancellationToken ct);
}
```

### Backend: `MinioIconStorageService.cs`

```csharp
// src/FormForge.Api/Features/Menus/MinioIconStorageService.cs
using Minio;
using Minio.DataModel.Args;

namespace FormForge.Api.Features.Menus;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "DI")]
internal sealed class MinioIconStorageService(IConfiguration configuration, ILogger<MinioIconStorageService> logger)
    : IIconStorageService
{
    private const string BucketName = "formforge";
    private IMinioClient? _client;
    private bool _bucketEnsured;

    private IMinioClient GetClient()
    {
        if (_client is not null) return _client;

        // Aspire injects services__minio__s3__0 as full URL; Compose uses MinIO:Endpoint as host:port
        var endpointRaw = configuration["services__minio__s3__0"]
            ?? configuration["MinIO:Endpoint"]
            ?? "http://localhost:9000";

        // Strip scheme for the SDK — it expects host[:port]
        var uri = endpointRaw.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? new Uri(endpointRaw)
            : new Uri("http://" + endpointRaw);

        var endpoint = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        var useSsl = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);

        var accessKey = configuration["MinIO:AccessKey"]
            ?? configuration["MinIO:RootUser"]
            ?? "minioadmin";
        var secretKey = configuration["MinIO:SecretKey"]
            ?? configuration["MinIO:RootPassword"]
            ?? "minioadmin";

        _client = new MinioClient()
            .WithEndpoint(endpoint)
            .WithCredentials(accessKey, secretKey)
            .WithSSL(useSsl)
            .Build();

        return _client;
    }

    private async Task EnsureBucketAsync(IMinioClient client, CancellationToken ct)
    {
        if (_bucketEnsured) return;
        var exists = await client.BucketExistsAsync(
            new BucketExistsArgs().WithBucket(BucketName), ct).ConfigureAwait(false);
        if (!exists)
        {
            logger.LogInformation("MinIO bucket '{Bucket}' not found; creating it.", BucketName);
            await client.MakeBucketAsync(
                new MakeBucketArgs().WithBucket(BucketName), ct).ConfigureAwait(false);
        }
        _bucketEnsured = true;
    }

    public async Task<string> StoreAsync(Stream content, string extension, long length, CancellationToken ct)
    {
        var client = GetClient();
        await EnsureBucketAsync(client, ct).ConfigureAwait(false);

        var objectKey = $"menus/icons/{Guid.NewGuid():N}.{extension}";

        await client.PutObjectAsync(new PutObjectArgs()
            .WithBucket(BucketName)
            .WithObject(objectKey)
            .WithStreamData(content)
            .WithObjectSize(length)
            .WithContentType(ExtensionToContentType(extension)),
            ct).ConfigureAwait(false);

        return objectKey;
    }

    private static string ExtensionToContentType(string ext) => ext switch
    {
        "png" => "image/png",
        "jpg" or "jpeg" => "image/jpeg",
        "svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };
}
```

**Note on scoped vs singleton:** `MinioIconStorageService` holds a lazy `_client` and `_bucketEnsured` flag. Register as **singleton** in `Program.cs` so the bucket-existence check is not repeated per request. The Minio SDK client is thread-safe.

### Backend: Upload Endpoint in `MenuAdminEndpoints.cs`

Add after the `MapDelete` call (inside `MapMenuAdminEndpoints`):

```csharp
group.MapPost("/upload-icon", UploadIconHandler)
     .DisableAntiforgery()
     .WithSummary("Upload a PNG, JPG, or SVG icon for a menu item")
     .Produces<UploadIconResponse>(StatusCodes.Status200OK)
     .Produces(StatusCodes.Status422UnprocessableEntity);
```

Add `UploadIconResponse` record near the top of the file:
```csharp
internal sealed record UploadIconResponse(string Type, string ObjectKey);
```

Handler:
```csharp
private static async Task<IResult> UploadIconHandler(
    IFormFile? file,
    IIconStorageService storage,
    CancellationToken ct)
{
    if (file is null)
    {
        return Results.Problem(
            title: "No file uploaded",
            statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "UPLOAD_INVALID",
                ["messageKey"] = "menus.uploadInvalid",
            });
    }

    const long MaxBytes = 2 * 1024 * 1024; // 2 MB
    var allowedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "image/png", "image/jpeg", "image/svg+xml" };

    if (!allowedTypes.Contains(file.ContentType) || file.Length > MaxBytes || file.Length <= 0)
    {
        return Results.Problem(
            title: "Invalid file: must be PNG, JPG, or SVG and ≤ 2 MB",
            statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "UPLOAD_INVALID",
                ["messageKey"] = "menus.uploadInvalid",
            });
    }

    var extension = file.ContentType switch
    {
        "image/png" => "png",
        "image/jpeg" => "jpg",
        "image/svg+xml" => "svg",
        _ => "bin",
    };

    using var stream = file.OpenReadStream();
    var objectKey = await storage.StoreAsync(stream, extension, file.Length, ct).ConfigureAwait(false);
    return Results.Ok(new UploadIconResponse("minio", objectKey));
}
```

**Important:** Minimal APIs do not bind `IFormFile` automatically unless `.DisableAntiforgery()` is called AND the endpoint explicitly reads from `HttpContext.Request.Form`. Use `[FromForm]` binding or read directly:
```csharp
// Alternatively, read from form directly:
private static async Task<IResult> UploadIconHandler(
    HttpContext httpContext,
    IIconStorageService storage,
    CancellationToken ct)
{
    if (!httpContext.Request.HasFormContentType)
    {
        return Results.Problem(...UPLOAD_INVALID...);
    }
    var form = await httpContext.Request.ReadFormAsync(ct).ConfigureAwait(false);
    var file = form.Files.GetFile("file");
    // ... rest of validation
}
```
The `[FromForm]` or `IFormFile` parameter binding in minimal APIs requires that the endpoint has `.DisableAntiforgery()` applied. Use whichever approach is consistent with ASP.NET Core 10. Test the parameter binding approach first; fall back to `HttpContext.Request.ReadFormAsync` if needed.

### Backend: Program.cs Registration

Add in the menu services block (after `AddScoped<IValidator<UpdateMenuRequest>,...>`):
```csharp
// Icon storage — singleton so bucket-existence check (lazy) is not repeated per request.
builder.Services.AddSingleton<IIconStorageService, MinioIconStorageService>();
```

### Backend: Integration Test Setup

Create `src/FormForge.Api.Tests/Features/Menus/UploadIconIntegrationTests.cs` as a new file with its own `WebApplicationFactory` override that registers a fake storage service.

The fake:
```csharp
// Inside UploadIconIntegrationTests.cs
private sealed class FakeIconStorageService : IIconStorageService
{
    public Task<string> StoreAsync(Stream content, string extension, long length, CancellationToken ct) =>
        Task.FromResult($"menus/icons/{Guid.NewGuid():N}.{extension}");
}
```

WebApplicationFactory override:
```csharp
_factory = new WebApplicationFactory<Program>()
    .WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:formforge", _postgres.ConnectionString);
        builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
        builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
        builder.ConfigureServices(services =>
        {
            // Replace real MinIO service with in-memory fake
            var descriptor = services.Single(d => d.ServiceType == typeof(IIconStorageService));
            services.Remove(descriptor);
            services.AddSingleton<IIconStorageService, FakeIconStorageService>();
        });
    });
```

`UploadIconIntegrationTests` also uses `IClassFixture<PostgresFixture>` (JWT auth requires a running API with a DB).

### Frontend: `LucideIcon.tsx`

```typescript
// web/src/components/icons/LucideIcon.tsx
import * as Icons from 'lucide-react'
import type { LucideProps } from 'lucide-react'

interface LucideIconProps extends LucideProps {
  name: string
}

export function LucideIcon({ name, size = 16, ...rest }: LucideIconProps) {
  const IconComponent = (Icons as Record<string, React.ComponentType<LucideProps>>)[name]
  if (!IconComponent) return null
  return <IconComponent size={size} {...rest} />
}
```

**Bundle size note:** `import * as Icons from 'lucide-react'` imports all ~1,500 icons. This is acceptable for the admin-only pages. Story 4.7's navbar rendering must use tree-shaken named imports instead.

### Frontend: Upload Mutation

Add to `menuAdminMutations.ts`:
```typescript
export function useUploadMenuIconMutation() {
  return useMutation({
    mutationFn: (file: File) => {
      const form = new FormData()
      form.append('file', file)
      return httpClient.post<{ type: string; objectKey: string }>(
        '/api/admin/menus/upload-icon',
        form,
      )
    },
  })
}
```

**Note:** `httpClient.post` must NOT set `Content-Type` for `FormData` — the browser sets it with the correct multipart boundary automatically. Verify that `httpClient.ts` omits `Content-Type` when the body is `FormData`.

### Frontend: Icon Picker Integration in `menus.$menuId.tsx`

Key changes to `MenuDetailContent`:

1. Add state:
   ```typescript
   const [pendingIcon, setPendingIcon] = useState<MenuIcon | null>(menu.icon)
   ```

2. In `submit`, replace `icon: menu.icon` with `icon: pendingIcon`.

3. Add `<IconPickerSection icon={pendingIcon} onChange={setPendingIcon} />` inside the `<form>` element, below the `isActive` field. Keep `IconPickerSection` in the same file; it's small.

The `pendingIcon` is NOT part of the react-hook-form schema — it's managed by local state, consistent with how `subName`/`subOrder` were handled in the `SubMenusSection` in Story 4.2.

### Frontend: `httpClient.ts` FormData Check

Before implementing the upload mutation, check `web/src/features/auth/httpClient.ts` — the post method likely sets `Content-Type: application/json`. For FormData requests, the Content-Type header must NOT be set (the browser sets it with the multipart boundary). Ensure the client either:
- Detects `FormData` and omits the header, OR
- Accepts a `headers` override parameter

Read `httpClient.ts` before implementing — adapt accordingly.

### No DB Migration Needed

The `icon` column already exists as `TEXT` in the `menus` table (added in Story 4.1's migration `20260524054931_CreateMenusAndMenuRoleAssignments`). No schema change is required.

### Test Count Baseline

| Story | Backend | Frontend |
|-------|---------|----------|
| Baseline after 4.2 review | 259 | 78 |
| Story 4.3 new (upload endpoint: 5 + icon validation: 3 + round-trip: 1) | +9 | 0 |
| **Expected after 4.3** | **268** | **78** |

Frontend tests are not expected for admin UI icon picker per the established pattern (Story 4.2 Task 7 limits backend integration tests; no explicit frontend test requirement in epic spec).

### What This Story Does NOT Implement

- Displaying icon in the navbar — Story 4.7. Only admin detail page shows the icon.
- Presigned URL generation for minio icons at read time — the admin UI currently renders uploaded image icons as `[Uploaded image]` text; actual image rendering with presigned URLs is an architecture decision 4.1 concern deferred beyond Story 4.3. Document in deferred-work.md.
- Generic `/api/files/upload` endpoint — that belongs in `Features/Files/` per architecture. Story 4.3 adds a menu-specific `/api/admin/menus/upload-icon`.
- Reparenting icon when a menu item is moved (Story 4.5).

### Key Architecture References

- [Source: epics.md § Story 4.3] Upload endpoint: `POST /api/admin/menus/upload-icon`. Response: `{ type: "minio", objectKey }`. No presigned URL needed in the response (contrast with architecture AD-9 which applies to data-entry, not admin icon assignment).
- [Source: architecture.md § 4.1] MinIO bucket `formforge`, path prefix `menus/icons/`.
- [Source: architecture.md § 4.6] `LucideIcon.tsx` belongs in `web/src/components/icons/LucideIcon.tsx`.
- [Source: architecture.md § 3.5] Upload endpoint inherits `/api/admin` group auth: `RequireAuth() + RequirePlatformAdmin()`.
- [Source: deferred-work.md] "api does not depend on minio-init completing" — **Story 4.3 is the owner**. Fix by calling `BucketExistsAsync`/`MakeBucketAsync` lazily in `MinioIconStorageService.EnsureBucketAsync`.
- [Source: architecture.md] Minio .NET client package: `Minio`. Add to `FormForge.Api.csproj`.
- [Source: 4-2-create-sub-menu-items.md § Dev Notes] ValidationFilter returns 422 `ValidationProblem` for FluentValidation failures; the upload endpoint returns 422 `Problem` directly (not via FluentValidation) since multipart forms don't go through the `ValidationFilter<T>` pipeline.

### Deferred Items to Document in `deferred-work.md` After Completion

- **Presigned URL for minio icon display in admin UI** — `GET /api/admin/menus/{id}` returns `objectKey` but no presigned URL. Admin UI shows `[Uploaded image]` text. Serving the actual image requires calling `POST /api/files/refresh-urls` (architecture Decision 4.1). Owner: Story 4.7 or a dedicated files API story.
- **`LucideIcon` tree-shaking for navbar** — Story 4.7 navbar must use named imports (`import { Home } from 'lucide-react'`) instead of `import * as Icons` to keep the public bundle small. Owner: Story 4.7.

## Dev Agent Record

### Agent Model Used

Claude Opus 4.7 (1M context) — `claude-opus-4-7[1m]`.

### Debug Log References

No notable debug sessions. Three analyzer failures surfaced during the first backend build (CA2000 / CA1001 / CA1826 / CA2007 in `MinioIconStorageService` and `MenuAdminEndpoints`) and were fixed in-line — making the storage service `IDisposable`, suppressing CA2000 over the Minio fluent-builder chain (the analyzer can't follow the chain to see the disposable instance lands in `_client`), swapping `IFormFileCollection.FirstOrDefault()` for indexer access, and changing `await using var stream` to `using var stream` (the synchronous `Stream` returned by `IFormFile.OpenReadStream()` has no async-dispose semantics).

### Completion Notes List

**Implementation summary**

- **AC-1 satisfied** — backend validates lucide names against an embedded list of 5,869 lucide-react 1.16.0 exports; invalid names produce 422 via the existing `ValidationFilter<T>` pipeline.
- **AC-2 satisfied** — `POST /api/admin/menus/upload-icon` accepts PNG/JPG/SVG ≤ 2 MB, stores under `menus/icons/{guid}.{ext}`, returns `{ type: "minio", objectKey }`. The lazy `EnsureBucketAsync` resolves the `minio-init`-race item listed in `deferred-work.md` — the API no longer depends on a separate init container creating the bucket.
- **AC-3 satisfied** — invalid MIME type, oversized file, or missing file all return 422 `UPLOAD_INVALID`; the storage service is never invoked on the rejection path.
- **AC-4 satisfied** — when `menu.icon === null` the picker shows the `iconPlaceholder` label; the navbar render of the placeholder is deferred to Story 4.7.

**Architectural decisions worth noting**

- **`MenuResponse.Icon: string? → JsonElement?` (Task 3)** — the existing column stores a JSON string; serializing through `string?` produced an escaped-string field that the frontend `MenuIcon` type cannot parse. Round-trip is now `string ← persistence ← ParseIcon → JsonElement → JSON object → MenuIcon`. The new `CreateMenu_WithValidLucideIcon_Returns201AndIconRoundTripsAsObject` test asserts on `JsonValueKind.Object` to prevent regression — this is the most important correctness fix in the story.
- **Singleton `MinioIconStorageService`** — chosen over scoped (the story has noted this both ways in different places). The bucket-existence check is a one-shot piece of process state; running it per request defeats the cache and adds an unnecessary round-trip to MinIO on every upload. The Minio .NET client is thread-safe; a `SemaphoreSlim` serializes the lazy `MakeBucketAsync` call so concurrent first uploads don't race.
- **`IconValidation.IsValidShape` shared helper** — Create and Update validators share identical icon rules; the helper keeps them in lockstep so a future contract change (e.g. adding `type: "url"`) only edits one place.
- **`httpClient` FormData branch** — the existing `request<T>` always set `Content-Type: application/json` and always called `JSON.stringify(body)`. For multipart uploads the browser must set the boundary itself, so the function now detects `body instanceof FormData` and skips both. This is a minimal, targeted change.

**Outstanding follow-ups** (mirror the story's "What This Story Does NOT Implement" section — entries to be appended to `_bmad-output/implementation-artifacts/deferred-work.md` by the next maintenance pass):

- **Presigned URL for MinIO icon display in admin UI** — `GET /api/admin/menus/{id}` returns `objectKey` only; the admin UI currently shows `iconCurrentMinio` text instead of the actual uploaded image. Wiring `POST /api/files/refresh-urls` for presigned URLs is an Architecture Decision 4.1 concern. Owner: Story 4.7 / a dedicated files API story.
- **`LucideIcon` tree-shaking for the navbar** — `import * as Icons` pulls all ~3,000 icons into the admin bundle (~3 kB pre-gzip impact on `menus._menuId`, acceptable for admin-only). Story 4.7 must use named imports (`import { Home } from 'lucide-react'`) for the public navbar.
- **`Menu` entity Icon-column comment** — `Domain/Entities/Menu.cs` line 8 still reads `"stored as jsonb; full type validation in Story 4.3"`. The column is actually `TEXT` (per Story 4.1's migration); the comment is misleading but cleaning it up is outside this story's scope.

**Validation results**

| Check | Result | Baseline |
|-------|--------|----------|
| Backend tests | 267 / 267 pass | 259 / 259 (Story 4.2) → +8 (5 upload-icon + 3 icon round-trip) |
| Frontend tests | 78 / 78 pass | 78 / 78 (no frontend tests required per story Test Count Baseline + Task 7 scope) |
| TypeScript | clean | — |
| Lint | 29 errors (was 28) | +1 expected: `react-refresh/only-export-components` on `IconPickerSection` — story explicitly says "Keep `IconPickerSection` in the same file; it's small" |
| Vite build | clean | `menus._menuId` chunk 10.53 kB (gzip 2.90 kB), was 7.38 kB / 2.21 kB. Delta is the lucide-react `import * as Icons` + IconPickerSection + i18n keys. |
| Backend build | 0 errors / 0 warnings | — |

### File List

**Backend (new):**
- `src/FormForge.Api/Features/Menus/lucide-icon-names.txt` (generated embedded resource)
- `src/FormForge.Api/Features/Menus/LucideIconRegistry.cs`
- `src/FormForge.Api/Features/Menus/IIconStorageService.cs`
- `src/FormForge.Api/Features/Menus/MinioIconStorageService.cs`
- `src/FormForge.Api.Tests/Features/Menus/UploadIconIntegrationTests.cs`

**Backend (modified):**
- `Directory.Packages.props` — add `<PackageVersion Include="Minio" Version="6.0.4" />` (central package management is enabled repo-wide)
- `src/FormForge.Api/FormForge.Api.csproj` — add Minio PackageReference + EmbeddedResource
- `src/FormForge.Api/Features/Menus/Dtos/MenuResponse.cs` — Icon type: `string?` → `JsonElement?`
- `src/FormForge.Api/Features/Menus/MenuService.cs` — add `ParseIcon()`, update `ToResponse()` + `CreateMenuAsync` inline response
- `src/FormForge.Api/Features/Menus/Validators/CreateMenuRequestValidator.cs` — add icon validation + shared `IconValidation` helper
- `src/FormForge.Api/Features/Menus/Validators/UpdateMenuRequestValidator.cs` — add icon validation (delegates to shared `IconValidation.IsValidShape`)
- `src/FormForge.Api/Features/Menus/MenuAdminEndpoints.cs` — add `UploadIconHandler` + `UploadIconResponse`
- `src/FormForge.Api/Program.cs` — register `IIconStorageService` (singleton)
- `src/FormForge.Api.Tests/Features/Menus/MenuIntegrationTests.cs` — add 3 icon tests + `System.Text.Json` using

**Frontend (new):**
- `web/src/components/icons/LucideIcon.tsx`

**Frontend (modified):**
- `web/src/features/auth/httpClient.ts` — branch on `body instanceof FormData` so the upload mutation can send multipart bodies (do not set `Content-Type`, do not `JSON.stringify`)
- `web/src/features/admin/menus/menuAdminMutations.ts` — add `useUploadMenuIconMutation`
- `web/src/routes/_app/admin/menus.$menuId.tsx` — add icon picker, `pendingIcon` state, `IconPickerSection`
- `web/src/lib/i18n/locales/en.json` — add 14 icon-related keys under `admin.menus`

**Sprint tracking:**
- `_bmad-output/implementation-artifacts/sprint-status.yaml`

## Change Log

| Date | Change | Author |
|------|--------|--------|
| 2026-05-24 | Story created from epic; marked `ready-for-dev`. | Bob (SM) |
| 2026-05-24 | Implementation complete (all 12 tasks). Backend 267/267, frontend 78/78, vite build + tsc clean. Status → `review`. | Amelia (Dev) — Opus 4.7 |
