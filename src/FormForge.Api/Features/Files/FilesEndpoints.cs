using FormForge.Api.Features.Menus;
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

    private static async Task<IResult> GetPresignedUrlHandler(
        string? key,
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

        var url = await storage.GetPresignedUrlAsync(key, ct).ConfigureAwait(false);
        return Results.Ok(new PresignResponse(url));
    }

    // ── upload ──────────────────────────────────────────────────────────────────

    private sealed record UploadFileResponse(string ObjectKey);

    private static async Task<IResult> UploadFileHandler(
        HttpContext httpContext,
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
        await storage.UploadFileAsync(stream, objectKey, file.ContentType, file.Length, ct)
            .ConfigureAwait(false);

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

        await storage.DeleteFileAsync(key, ct).ConfigureAwait(false);
        return Results.NoContent();
    }
}
