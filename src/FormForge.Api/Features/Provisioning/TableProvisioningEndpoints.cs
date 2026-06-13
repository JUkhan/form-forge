using FormForge.Api.Common;
using FormForge.Api.Features.Provisioning.Dtos;

namespace FormForge.Api.Features.Provisioning;

// Admin "Table Provisioned" tab — provision a CRUD Designer's table directly,
// without binding it to a menu item. Mounted under /api/admin/table-provisioning
// (RequirePlatformAdmin inherited from the parent /api/admin group in Program.cs).
internal static class TableProvisioningEndpoints
{
    internal static RouteGroupBuilder MapTableProvisioningEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/", ListHandler)
             .WithSummary("List CRUD designers and their derived table-provisioning status (paged, filterable)")
             .Produces<PagedResult<TableProvisioningItem>>(StatusCodes.Status200OK);

        group.MapPost("/{designerId}/provision", ProvisionHandler)
             .WithSummary("Create or sync a CRUD designer's physical table (no menu binding)")
             .Produces(StatusCodes.Status202Accepted)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        return group;
    }

    private static async Task<IResult> ListHandler(
        TableProvisioningService service,
        CancellationToken ct,
        [Microsoft.AspNetCore.Mvc.FromQuery] int page = 1,
        [Microsoft.AspNetCore.Mvc.FromQuery] int pageSize = 25,
        [Microsoft.AspNetCore.Mvc.FromQuery] string? search = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        var result = await service.ListAsync(page, pageSize, search, ct).ConfigureAwait(false);
        return Results.Ok(result);
    }

    private static async Task<IResult> ProvisionHandler(
        string designerId,
        ProvisionTableRequest? request,
        TableProvisioningService service,
        HttpContext httpContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(httpContext);

        var actorId = httpContext.User.FindFirst("userId")?.Value is { } s
            && Guid.TryParse(s, out var uid)
                ? uid
                : (Guid?)null;

        var result = await service
            .ProvisionAsync(designerId, request?.Version, actorId, ct)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            // 202 — the menu-less job is enqueued and the table is created/synced
            // asynchronously by ProvisioningBackgroundService (mirrors menu binding).
            ProvisionTableOutcome.Success => Results.Accepted(),
            ProvisionTableOutcome.DesignerNotFound => Problem(
                StatusCodes.Status404NotFound,
                "Designer not found.",
                "DESIGNER_NOT_FOUND",
                "admin.tableProvisioning.errors.designerNotFound"),
            ProvisionTableOutcome.NotCrudMode => Problem(
                StatusCodes.Status422UnprocessableEntity,
                "This is a VIEW-mode component and has no table to provision.",
                "NOT_CRUD_MODE",
                "admin.tableProvisioning.errors.notCrud"),
            ProvisionTableOutcome.VersionRequired => Problem(
                StatusCodes.Status422UnprocessableEntity,
                "A version is required.",
                "VERSION_REQUIRED",
                "admin.tableProvisioning.errors.versionRequired"),
            ProvisionTableOutcome.VersionNotFound => Problem(
                StatusCodes.Status404NotFound,
                "That version does not exist for this designer.",
                "VERSION_NOT_FOUND",
                "admin.tableProvisioning.errors.versionNotFound"),
            ProvisionTableOutcome.VersionNotPublished => Problem(
                StatusCodes.Status422UnprocessableEntity,
                "Only a Published version can be provisioned.",
                "VERSION_NOT_PUBLISHED",
                "admin.tableProvisioning.errors.versionNotPublished"),
            ProvisionTableOutcome.UnsafeIdentifier => Problem(
                StatusCodes.Status422UnprocessableEntity,
                "The designer id is not a safe table identifier.",
                "UNSAFE_IDENTIFIER",
                "admin.tableProvisioning.errors.unsafeIdentifier"),
            ProvisionTableOutcome.RepeaterCycle => Problem(
                StatusCodes.Status422UnprocessableEntity,
                "The repeater references form a cycle and cannot be provisioned.",
                "REPEATER_CYCLE",
                "admin.tableProvisioning.errors.repeaterCycle"),
            ProvisionTableOutcome.InvalidPgType => Problem(
                StatusCodes.Status422UnprocessableEntity,
                result.Detail ?? "A field has an invalid type.",
                "INVALID_PG_TYPE",
                "admin.tableProvisioning.errors.invalidPgType"),
            _ => Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                }),
        };
    }

    private static IResult Problem(int statusCode, string detail, string code, string messageKey) =>
        Results.Problem(
            detail: detail,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = code,
                ["messageKey"] = messageKey,
            });
}
