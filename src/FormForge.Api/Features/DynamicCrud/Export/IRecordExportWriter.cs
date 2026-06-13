namespace FormForge.Api.Features.DynamicCrud.Export;

// Story 7-followup — one writer per export format. The endpoint resolves the
// requested format string ("csv" | "xlsx" | "pdf") to one of these, then
// streams the result back as the HTTP response body. Each writer is
// stateless and safe to construct per-request (no DI registration needed).
internal interface IRecordExportWriter
{
    // MIME type for the Content-Type response header.
    string ContentType { get; }

    // Extension (no leading dot) used to build the Content-Disposition filename.
    string FileExtension { get; }

    // Renders `rows` into `output`. Caller is responsible for any row-count cap
    // and for column ordering (`columnNames` is the authoritative display order).
    // Each row is a name → value map matching the Dapper IDictionary<string,object>
    // shape returned by Npgsql.
    Task WriteAsync(
        Stream output,
        string schemaTitle,
        IReadOnlyList<string> columnNames,
        IReadOnlyList<IDictionary<string, object?>> rows,
        CancellationToken ct);
}
