using FormForge.Api.Features.Menus.Dtos;

namespace FormForge.Api.Features.Menus;

// Story 4.7 — public navbar tree. Separate from MenuAdminEndpoints because:
//  - Different route group (/api/menus vs /api/admin/menus)
//  - Different filter chain (RequireAuth only, no RequirePlatformAdmin)
//  - Different audience (every authenticated user vs platform-admin only)
// Architecture line 474 explicitly defines this split.
internal static class MenuEndpoints
{
    internal static RouteGroupBuilder MapMenuEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/", GetNavMenusHandler)
             .WithSummary("Get the permission-filtered menu tree for the calling user. 5 s in-memory cache. p95 <100 ms cached.")
             .Produces<IReadOnlyList<NavMenuItem>>(StatusCodes.Status200OK);

        return group;
    }

    private static async Task<IResult> GetNavMenusHandler(
        HttpContext httpContext,
        IMenuService menuService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(menuService);

        var userIdClaim = httpContext.User.FindFirst("userId")?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return Results.Unauthorized();
        }

        var tree = await menuService.GetNavMenusForUserAsync(userId, ct).ConfigureAwait(false);
        return Results.Ok(tree);
    }
}
