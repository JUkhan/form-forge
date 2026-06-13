using System.Security.Claims;
using FormForge.Api.Features.Permissions.Dtos;

namespace FormForge.Api.Features.Permissions;

internal static class PermissionsEndpoints
{
    internal static RouteGroupBuilder MapUserSelfEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/me/permissions", GetMyPermissionsHandler)
             .WithSummary("Returns the calling user's effective permissions across all resources")
             .Produces<PermissionsResponse>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status401Unauthorized);

        return group;
    }

    private static async Task<IResult> GetMyPermissionsHandler(
        ClaimsPrincipal user,
        IPermissionService permissionService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(permissionService);

        var userIdClaim = user.FindFirst("userId")?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return Results.Unauthorized();
        }

        var permissions = await permissionService
            .GetEffectivePermissionsAsync(userId, ct)
            .ConfigureAwait(false);

        // Match the source dictionary's comparer (OrdinalIgnoreCase) so the
        // rebuild can never throw ArgumentException on case-different keys
        // that survive a future non-API DB writer. (Story 2.6 bmad review.)
        var perResource = permissions.PerResource.ToDictionary(
            kvp => kvp.Key,
            kvp => new CrudFlagsResponse(
                kvp.Value.CanCreate,
                kvp.Value.CanRead,
                kvp.Value.CanUpdate,
                kvp.Value.CanDelete,
                kvp.Value.CanExport),
            StringComparer.OrdinalIgnoreCase);

        return Results.Ok(new PermissionsResponse(
            permissions.UserId,
            permissions.ComputedAt,
            permissions.IsActive,
            perResource,
            permissions.RoleIds,
            permissions.CanManageDatasets));
    }
}
