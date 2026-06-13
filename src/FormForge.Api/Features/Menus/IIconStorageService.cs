namespace FormForge.Api.Features.Menus;

internal interface IIconStorageService
{
    /// <summary>
    /// Stores <paramref name="content"/> in MinIO under menus/icons/.
    /// Returns the object key, e.g. "menus/icons/a1b2c3d4...ef.png".
    /// </summary>
    Task<string> StoreAsync(Stream content, string extension, long length, CancellationToken ct);

    /// <summary>
    /// Generates a time-limited presigned GET URL for <paramref name="objectKey"/> in the
    /// FormForge bucket. The URL is valid for one hour and requires no credentials to use.
    /// </summary>
    Task<string> GetPresignedUrlAsync(string objectKey, CancellationToken ct);

    /// <summary>
    /// Uploads <paramref name="content"/> to MinIO at the exact <paramref name="objectKey"/> path.
    /// Used by the File designer field to persist form attachments.
    /// </summary>
    Task UploadFileAsync(Stream content, string objectKey, string contentType, long length, CancellationToken ct);

    /// <summary>
    /// Removes the object at <paramref name="objectKey"/> from MinIO.
    /// Non-fatal if the object does not exist — MinIO returns 404 which we swallow.
    /// </summary>
    Task DeleteFileAsync(string objectKey, CancellationToken ct);
}
