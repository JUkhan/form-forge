using FormForge.Api.Features.Audit;
using Microsoft.AspNetCore.Mvc;

namespace FormForge.Api.Features.Datasets;

// Story 8.9 (FR-61 / AR-65) — platform-admin endpoint for the dataset audit log.
// Registered at GET /api/admin/datasets/audit. No DELETE/PUT/POST is mapped — the
// log is append-only (FR-61 AC-3); ASP.NET Core returns HTTP 405 automatically for
// any method not registered on this path.
internal static class DatasetAdminEndpoints
{
    internal static RouteGroupBuilder MapDatasetAdminEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        // AC-4 (FR-61 / AR-65): paginated dataset audit log, filterable by dataset_name
        // and operation. No DatasetName.TryCreate validation needed — datasetName is a
        // read filter, not a write identifier; an invalid pattern just returns 0 rows.
        group.MapGet("/audit", async (
            AuditService auditService,
            CancellationToken ct,
            [FromQuery] string? datasetName = null,
            [FromQuery] string? operation = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 25) =>
        {
            ArgumentNullException.ThrowIfNull(auditService);

            var result = await auditService
                .GetDatasetAuditLogAsync(datasetName, operation, page, pageSize, ct)
                .ConfigureAwait(false);

            return Results.Ok(result);
        })
        .WithSummary("Dataset audit log (Story 8.9 — FR-61)");

        return group;
    }
}
