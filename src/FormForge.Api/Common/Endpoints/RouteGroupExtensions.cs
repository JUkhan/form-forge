using FormForge.Api.Common.Endpoints.EndpointFilters;
using FormForge.Api.Features.Permissions;

namespace FormForge.Api.Common.Endpoints;

internal static class RouteGroupExtensions
{
    internal static RouteHandlerBuilder AddValidationFilter<T>(
        this RouteHandlerBuilder builder) where T : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddEndpointFilter<ValidationFilter<T>>();
        return builder;
    }

    internal static RouteGroupBuilder RequireAuth(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.RequireAuthorization();
        return group;
    }

    internal static RouteGroupBuilder RequirePlatformAdmin(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        // Requires the "platform-admin" role claim (RoleClaimType = "roles" in JwtBearerOptions).
        // The named policy is registered in Program.cs via AddAuthorization.
        group.RequireAuthorization("platform-admin");
        return group;
    }

    // Per-endpoint filter (not group-level) because each endpoint specifies its own
    // CRUD action ("create" / "read" / "update" / "delete"). The filter resolves the
    // {designerId} route value and checks effective CRUD flags via IPermissionService.
    internal static RouteHandlerBuilder RequirePermission(
        this RouteHandlerBuilder builder,
        string action)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(action);

        return builder.AddEndpointFilter(async (ctx, next) =>
        {
            var designerId = ctx.HttpContext.Request.RouteValues["designerId"]?.ToString();
            if (string.IsNullOrEmpty(designerId))
            {
                return Results.Problem(
                    detail: "Request is missing the designerId route value.",
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["messageKey"] = "errors.badRequest",
                    });
            }

            var userIdClaim = ctx.HttpContext.User.FindFirst("userId")?.Value;
            if (!Guid.TryParse(userIdClaim, out var userId))
            {
                return Results.Unauthorized();
            }

            var permissionService = ctx.HttpContext.RequestServices
                .GetRequiredService<IPermissionService>();

            var flags = await permissionService
                .GetCrudFlagsAsync(userId, designerId, ctx.HttpContext.RequestAborted)
                .ConfigureAwait(false);

            // InvariantGlobalization=true: callers pass lowercase, but normalize defensively
            // so a future caller passing "Create" doesn't silently fall to false.
            var normalizedAction = action.ToLowerInvariant();
            var allowed = normalizedAction switch
            {
                "create" => flags.CanCreate,
                "read" => flags.CanRead,
                "update" => flags.CanUpdate,
                "delete" => flags.CanDelete,
                "export" => flags.CanExport,
                _ => false,
            };

            if (!allowed)
            {
                return Results.Problem(
                    detail: "You don't have permission to perform this action.",
                    title: "Permission denied",
                    statusCode: StatusCodes.Status403Forbidden,
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["code"] = "FORBIDDEN",
                        ["resource"] = designerId,
                        ["action"] = action,
                        ["messageKey"] = "errors.forbidden",
                    });
            }

            return await next(ctx).ConfigureAwait(false);
        });
    }

    // Per-endpoint filter (like RequirePermission) because read endpoints in the same
    // /api/datasets group are auth-only — only the write endpoints attach this. Unlike
    // RequirePermission this is a platform-level capability, not per-resource, so the
    // FORBIDDEN envelope carries "action": "dataset-management" with no "resource" key.
    // (Story 8.2, FR-56 / AR-58.)
    internal static RouteHandlerBuilder RequireDatasetManagement(
        this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEndpointFilter(async (ctx, next) =>
        {
            var userIdClaim = ctx.HttpContext.User.FindFirst("userId")?.Value;
            if (!Guid.TryParse(userIdClaim, out var userId))
            {
                return Results.Unauthorized();
            }

            var permissionService = ctx.HttpContext.RequestServices
                .GetRequiredService<IPermissionService>();

            var permissions = await permissionService
                .GetEffectivePermissionsAsync(userId, ctx.HttpContext.RequestAborted)
                .ConfigureAwait(false);

            if (!permissions.CanManageDatasets)
            {
                return Results.Problem(
                    detail: "Dataset management permission required.",
                    title: "Permission denied",
                    statusCode: StatusCodes.Status403Forbidden,
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["code"] = "FORBIDDEN",
                        ["action"] = "dataset-management",
                        ["messageKey"] = "errors.forbidden",
                    });
            }

            return await next(ctx).ConfigureAwait(false);
        });
    }
}
