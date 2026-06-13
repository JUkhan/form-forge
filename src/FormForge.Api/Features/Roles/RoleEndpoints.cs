using FormForge.Api.Common;
using FormForge.Api.Common.Endpoints;
using FormForge.Api.Features.Roles.Dtos;

namespace FormForge.Api.Features.Roles;

internal static class RoleEndpoints
{
    internal static RouteGroupBuilder MapRoleEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/", GetRolesHandler)
             .WithSummary("List all roles (paginated)")
             .Produces<PagedResult<RoleListItem>>(StatusCodes.Status200OK);

        group.MapGet("/{id:guid}", GetRoleHandler)
             .WithSummary("Get a role by ID")
             .Produces<RoleResponse>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/", CreateRoleHandler)
             .AddValidationFilter<CreateRoleRequest>()
             .WithSummary("Create a new role")
             .Produces<RoleResponse>(StatusCodes.Status201Created)
             .Produces(StatusCodes.Status409Conflict)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/{id:guid}", UpdateRoleHandler)
             .AddValidationFilter<UpdateRoleRequest>()
             .WithSummary("Update an existing role and replace its permissions")
             .Produces(StatusCodes.Status204NoContent)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status409Conflict)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/{id:guid}", DeleteRoleHandler)
             .WithSummary("Delete a role (fails if it has active user assignments)")
             .Produces(StatusCodes.Status204NoContent)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status409Conflict);

        return group;
    }

    private static async Task<IResult> GetRolesHandler(
        IRoleService roleService,
        int page = 1,
        int pageSize = 25,
        string? sort = null,
        string? search = null,
        string? system = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(roleService);
        pageSize = Math.Min(Math.Max(pageSize, 1), 100);
        page = Math.Max(page, 1);
        var result = await roleService
            .GetRolesAsync(page, pageSize, sort, search, system, ct)
            .ConfigureAwait(false);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetRoleHandler(
        Guid id,
        IRoleService roleService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roleService);
        var role = await roleService.GetRoleAsync(id, ct).ConfigureAwait(false);
        return role is null
            ? RoleNotFoundProblem()
            : Results.Ok(role);
    }

    private static async Task<IResult> CreateRoleHandler(
        CreateRoleRequest request,
        IRoleService roleService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(roleService);

        var result = await roleService.CreateRoleAsync(request, ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            CreateRoleOutcome.DuplicateName => RoleNameConflictProblem(),
            CreateRoleOutcome.Success => Results.Created(
                $"/api/admin/roles/{result.RoleId}",
                result.Role),
            _ => Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                }),
        };
    }

    private static async Task<IResult> UpdateRoleHandler(
        Guid id,
        UpdateRoleRequest request,
        IRoleService roleService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(roleService);

        var result = await roleService.UpdateRoleAsync(id, request, ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            UpdateRoleOutcome.NotFound => RoleNotFoundProblem(),
            UpdateRoleOutcome.SystemProtected => RoleSystemProtectedProblem("System roles cannot be modified"),
            UpdateRoleOutcome.DuplicateName => RoleNameConflictProblem(),
            UpdateRoleOutcome.Success => Results.NoContent(),
            _ => Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                }),
        };
    }

    private static async Task<IResult> DeleteRoleHandler(
        Guid id,
        IRoleService roleService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roleService);

        var result = await roleService.DeleteRoleAsync(id, ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            DeleteRoleOutcome.NotFound => RoleNotFoundProblem(),
            DeleteRoleOutcome.SystemProtected => RoleSystemProtectedProblem("System roles cannot be deleted"),
            DeleteRoleOutcome.HasAssignments => Results.Problem(
                detail: "This role has active user assignments and cannot be deleted.",
                title: "Role has active user assignments",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "ROLE_HAS_ASSIGNMENTS",
                    ["messageKey"] = "roles.hasAssignments",
                }),
            DeleteRoleOutcome.Success => Results.NoContent(),
            _ => Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                }),
        };
    }

    private static IResult RoleNotFoundProblem() =>
        Results.Problem(
            detail: "Role not found.",
            title: "Role not found",
            statusCode: StatusCodes.Status404NotFound,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "ROLE_NOT_FOUND",
                ["messageKey"] = "roles.notFound",
            });

    private static IResult RoleNameConflictProblem() =>
        Results.Problem(
            detail: "A role with this name already exists.",
            title: "Role name already exists",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "ROLE_NAME_CONFLICT",
                ["messageKey"] = "roles.nameConflict",
            });

    private static IResult RoleSystemProtectedProblem(string title) =>
        Results.Problem(
            detail: "System roles cannot be modified.",
            title: title,
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "ROLE_SYSTEM_PROTECTED",
                ["messageKey"] = "roles.systemProtected",
            });
}
