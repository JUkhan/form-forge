using FormForge.Api.Features.Users.Dtos;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FormForge.Api.Features.Users;

// Read-only directory lookup of active users, available to ANY authenticated
// caller (the parent /api/users group applies RequireAuth — no platform-admin
// gate). Distinct from the admin user-management list under /api/admin/users,
// which is paginated and exposes status/role/audit data.
internal static class ActiveUsersEndpoints
{
    internal static RouteGroupBuilder MapActiveUserEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        // GET /api/users/active — all active users as {id, email, displayName}.
        group.MapGet("/active", GetActiveUsersHandler)
             .WithSummary("List all active users (id, email, display name). Any authenticated user.")
             .Produces<IReadOnlyList<ActiveUserDto>>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status401Unauthorized);

        return group;
    }

    private static async Task<IResult> GetActiveUsersHandler(
        FormForgeDbContext db,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        // Projection straight to the DTO keeps the query read-only (no entity
        // tracking) and never materializes the password hash / MFA secret columns.
        var users = await db.Users
            .Where(u => u.IsActive)
            .OrderBy(u => u.DisplayName)
            .ThenBy(u => u.Email)
            .Select(u => new ActiveUserDto(u.Id, u.Email, u.DisplayName))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Results.Ok(users);
    }
}
