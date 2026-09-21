using FormForge.Api.Features.Menus;
using FormForge.Api.Features.Tenancy;
using Microsoft.AspNetCore.Http.Features;

namespace FormForge.Api.Features.Files;

internal static class FilesEndpoints
{
    private static readonly System.Text.RegularExpressions.Regex SafeSegmentRe =
        new(@"^[a-zA-Z0-9_\-]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private const long MaxFileBytes = 10L * 1024 * 1024; // 10 MB

    internal static RouteGroupBuilder MapFilesEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/presign", GetPresignedUrlHandler)
             .WithSummary("Return a presigned MinIO GET URL for the given object key (1-hour TTL).")
             .Produces<PresignResponse>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status400BadRequest);

        group.MapPost("/upload", UploadFileHandler)
             .WithSummary("Upload a form-field file to MinIO. Returns the object key stored in the record.")
             .Produces<UploadFileResponse>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status400BadRequest);

        group.MapDelete("/", DeleteFileHandler)
             .WithSummary("Delete a file from MinIO by its object key.")
             .Produces(StatusCodes.Status204NoContent)
             .Produces(StatusCodes.Status400BadRequest);

        return group;
    }

    // ── presign ─────────────────────────────────────────────────────────────────

    private sealed record PresignResponse(string Url);

    // Every object lives under a per-tenant root ("{schemaName}/…") in the shared bucket.
    // Keys stored in records / Image `src` stay tenant-relative; the root is applied here,
    // at the backend, so a tenant can never address another tenant's objects. A request with
    // no tenant claim (legacy token) keeps the old un-rooted layout.
    private static string TenantKey(ITenantContext tenant, string key) =>
        string.IsNullOrEmpty(tenant.SchemaName) ? key : $"{tenant.SchemaName}/{key}";

    // Objects saved before tenant roots existed sit at the bucket root. Prefer the tenant
    // location and fall back to the legacy one only when just that one exists.
    private static async Task<string> ResolveExistingKeyAsync(
        ITenantContext tenant, IIconStorageService storage, string key, CancellationToken ct)
    {
        var tenantKey = TenantKey(tenant, key);
        if (tenantKey == key) return key;
        if (await storage.ObjectExistsAsync(tenantKey, ct).ConfigureAwait(false)) return tenantKey;
        return await storage.ObjectExistsAsync(key, ct).ConfigureAwait(false) ? key : tenantKey;
    }

    private static async Task<IResult> GetPresignedUrlHandler(
        string? key,
        bool? download,
        ITenantContext tenant,
        IIconStorageService storage,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)
            || key.Length > 512
            || key.Contains("..", StringComparison.Ordinal)
            || key.StartsWith('/')
            || key.StartsWith('\\'))
        {
            return Results.BadRequest(new { code = "INVALID_KEY", messageKey = "files.invalidKey" });
        }

        var physicalKey = await ResolveExistingKeyAsync(tenant, storage, key, ct).ConfigureAwait(false);
        var fileName = download == true ? key[(key.LastIndexOf('/') + 1)..] : null;
        var url = await storage.GetPresignedUrlAsync(physicalKey, ct, fileName).ConfigureAwait(false);
        return Results.Ok(new PresignResponse(url));
    }

    // ── upload ──────────────────────────────────────────────────────────────────

    private sealed record UploadFileResponse(string ObjectKey);

    private static async Task<IResult> UploadFileHandler(
        HttpContext httpContext,
        ITenantContext tenant,
        IIconStorageService storage,
        CancellationToken ct)
    {
        if (!httpContext.Request.HasFormContentType)
            return Results.BadRequest(new { code = "INVALID_CONTENT_TYPE" });

        IFormCollection form;
        try
        {
            var opts = new FormOptions { MultipartBodyLengthLimit = MaxFileBytes };
            form = await httpContext.Request.ReadFormAsync(opts, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return Results.BadRequest(new { code = "FILE_TOO_LARGE", messageKey = "files.tooLarge" });
        }

        var file = form.Files.GetFile("file") ?? (form.Files.Count > 0 ? form.Files[0] : null);
        if (file is null || file.Length <= 0)
            return Results.BadRequest(new { code = "NO_FILE", messageKey = "files.noFile" });
        if (file.Length > MaxFileBytes)
            return Results.BadRequest(new { code = "FILE_TOO_LARGE", messageKey = "files.tooLarge" });

        var designerId = form["designerId"].ToString().Trim();
        var fieldKey   = form["fieldKey"].ToString().Trim();
        var recordId   = form["recordId"].ToString().Trim();

        if (string.IsNullOrWhiteSpace(designerId) || !SafeSegmentRe.IsMatch(designerId))
            return Results.BadRequest(new { code = "INVALID_DESIGNER_ID" });
        if (string.IsNullOrWhiteSpace(fieldKey) || !SafeSegmentRe.IsMatch(fieldKey))
            return Results.BadRequest(new { code = "INVALID_FIELD_KEY" });

        var idSegment = (!string.IsNullOrWhiteSpace(recordId) && Guid.TryParse(recordId, out _))
            ? recordId
            : Guid.NewGuid().ToString("N");

        var ext = ExtFromFile(file.FileName, file.ContentType);
        var objectKey = $"{designerId}/{fieldKey}_{idSegment}.{ext}";

        using var stream = file.OpenReadStream();
        await storage.UploadFileAsync(stream, TenantKey(tenant, objectKey), file.ContentType, file.Length, ct)
            .ConfigureAwait(false);

        // The tenant-relative key is what callers persist; TenantKey() re-applies the root.
        return Results.Ok(new UploadFileResponse(objectKey));
    }

    private static string ExtFromFile(string fileName, string contentType)
    {
        var fromName = System.IO.Path.GetExtension(fileName).TrimStart('.');
        if (!string.IsNullOrEmpty(fromName)) return fromName.ToLowerInvariant();
        return contentType switch
        {
            "image/jpeg"       => "jpg",
            "image/png"        => "png",
            "image/gif"        => "gif",
            "image/webp"       => "webp",
            "image/svg+xml"    => "svg",
            "application/pdf"  => "pdf",
            "text/plain"       => "txt",
            _                  => "bin",
        };
    }

    // ── delete ──────────────────────────────────────────────────────────────────

    private static async Task<IResult> DeleteFileHandler(
        string? key,
        ITenantContext tenant,
        IIconStorageService storage,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)
            || key.Length > 512
            || key.Contains("..", StringComparison.Ordinal)
            || key.StartsWith('/')
            || key.StartsWith('\\'))
        {
            return Results.BadRequest(new { code = "INVALID_KEY", messageKey = "files.invalidKey" });
        }

        var physicalKey = await ResolveExistingKeyAsync(tenant, storage, key, ct).ConfigureAwait(false);
        await storage.DeleteFileAsync(physicalKey, ct).ConfigureAwait(false);
        return Results.NoContent();
    }
}
