using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FormForge.Api.Features.Tenancy;

internal sealed record CurrentTenantResponse(string Name);

// Self-service read of the calling user's own tenant (the navbar brand). Mounted on the
// /api/users group next to /me/permissions: any authenticated user may read their own
// tenant's display name, nothing else about it.
internal static class TenantSelfEndpoints
{
    internal static RouteGroupBuilder MapTenantSelfEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/me/tenant", GetMyTenantHandler)
             .WithSummary("Returns the display name of the calling user's tenant")
             .Produces<CurrentTenantResponse>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> GetMyTenantHandler(
        ITenantContext tenantContext,
        FormForgeDbContext db,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(db);

        // No tenant claim (legacy token) — nothing to show; the client keeps its default brand.
        if (tenantContext.TenantId is not { } tenantId)
        {
            return Results.NotFound();
        }

        var name = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => t.Name)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return name is null ? Results.NotFound() : Results.Ok(new CurrentTenantResponse(name));
    }
}
