using FormForge.Api.Common;
using FormForge.Api.Features.Audit.Dtos;

namespace FormForge.Api.Features.Audit;

// Story 5.7 — handler for GET /api/admin/designers/{designerId}/audit. The route
// itself is registered in DesignerAdminEndpoints.MapDesignerAdminEndpoints; this
// class only owns the body so the registration stays alongside the other
// /designers admin routes. AC-2 (append-only) is satisfied by construction: no
// DELETE/PUT/POST is mapped on /audit, so ASP.NET returns 405 automatically.
internal static class AuditEndpoints
{
    internal static async Task<IResult> GetSchemaAuditLogHandler(
        string designerId,
        int page,
        int pageSize,
        AuditService auditService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(auditService);

        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 25;

        var result = await auditService
            .GetSchemaAuditLogAsync(designerId, page, pageSize, ct)
            .ConfigureAwait(false);

        return result is null
            ? Results.Problem(
                detail: "Designer not found.",
                title: "Designer not found",
                statusCode: StatusCodes.Status404NotFound,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "DESIGNER_NOT_FOUND",
                    ["messageKey"] = "admin.designers.notFound",
                })
            : Results.Ok(result);
    }

    // Story 6.8 — handler for GET /api/admin/data/{designerId}/audit. Route is
    // registered in DataAdminEndpoints.MapDataAdminEndpoints. Mirrors the schema
    // audit handler exactly; AC-2 (append-only) is satisfied by construction.
    internal static async Task<IResult> GetMutationAuditLogHandler(
        string designerId,
        int page,
        int pageSize,
        AuditService auditService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(auditService);

        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 25;

        var result = await auditService
            .GetMutationAuditLogAsync(designerId, page, pageSize, ct)
            .ConfigureAwait(false);

        return result is null
            ? Results.Problem(
                detail: "Designer not found.",
                title: "Designer not found",
                statusCode: StatusCodes.Status404NotFound,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "DESIGNER_NOT_FOUND",
                    ["messageKey"] = "admin.designers.notFound",
                })
            : Results.Ok(result);
    }
}
