using FormForge.Api.Common.Endpoints;
using FormForge.Api.Common.Endpoints.EndpointFilters;
using FormForge.Api.Features.Menus.Dtos;
using FormForge.Api.Features.Provisioning;
using Microsoft.AspNetCore.Http.Features;

namespace FormForge.Api.Features.Menus;

internal sealed record UploadIconResponse(string Type, string ObjectKey);

internal static class MenuAdminEndpoints
{
    internal static RouteGroupBuilder MapMenuAdminEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/", GetMenusHandler)
             .WithSummary("List all menu items (paginated)")
             .Produces<Common.PagedResult<MenuListItem>>(StatusCodes.Status200OK);

        group.MapGet("/{id:guid}", GetMenuHandler)
             .WithSummary("Get a menu item by ID")
             .Produces<MenuResponse>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/", CreateMenuHandler)
             .AddValidationFilter<CreateMenuRequest>()
             .WithSummary("Create a new top-level menu item")
             .Produces<MenuResponse>(StatusCodes.Status201Created)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/{id:guid}", UpdateMenuHandler)
             .AddValidationFilter<UpdateMenuRequest>()
             .WithSummary("Update a menu item")
             .Produces(StatusCodes.Status204NoContent)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/{id:guid}", DeleteMenuHandler)
             .WithSummary("Delete a menu item (fails if it has children)")
             .Produces(StatusCodes.Status204NoContent)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status409Conflict);

        // Story 4.4 — full-sync replacement of the allowed-roles set for a menu item.
        // PUT semantics: the supplied roleIds list completely replaces the prior set.
        group.MapPut("/{id:guid}/roles", AssignRolesHandler)
             .AddValidationFilter<AssignMenuRolesRequest>()
             .WithSummary("Replace the allowed-roles set for a menu item")
             .Produces(StatusCodes.Status204NoContent)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        // Story 4.5 — batch reorder for a single scope (all top-level OR all peers
        // under one parentId). Validates the scope invariant server-side as a
        // defense-in-depth check; the UI never sends mixed-scope payloads.
        // /reorder is a literal segment, so it matches before /{id:guid} routes
        // (ASP.NET routing prefers literals; "reorder" is not a valid Guid anyway).
        group.MapPut("/reorder", ReorderMenusHandler)
             .AddValidationFilter<ReorderMenusRequest>()
             .WithSummary("Batch-reorder menu items within a single scope. Body: { items: [{ id: Guid, order: int }] }. ≤256 entries, all sharing the same parentId scope.")
             .Produces(StatusCodes.Status204NoContent)
             .Produces(StatusCodes.Status409Conflict)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        // Story 4.6 — lightweight isActive toggle. Dedicated PATCH so the admin
        // list can flip the flag without round-tripping the full UpdateMenuRequest
        // payload (which requires name + order + icon). Idempotent.
        group.MapPatch("/{id:guid}/active", ToggleActiveHandler)
             .AddValidationFilter<ToggleMenuActiveRequest>()
             .WithSummary("Activate or deactivate a menu item. Body: { isActive: bool }.")
             .Produces(StatusCodes.Status204NoContent)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        // Story 5.2 — bind a Published Designer version to a menu item. Returns 202
        // (async provisioning per Decision 1.6 / AR-23) and enqueues a ProvisioningJob.
        // The three /binding* literal suffixes do not collide with /{id:guid}/active or
        // /{id:guid}: ASP.NET routing prefers literal segments under the Guid route
        // constraint, and "binding"/"binding-diff" are not valid Guids anyway.
        group.MapPut("/{id:guid}/binding", BindDesignerHandler)
             .AddValidationFilter<BindMenuDesignerRequest>()
             .WithSummary("Bind a Published Designer version to a menu item. Returns 202 and enqueues provisioning.")
             .Produces(StatusCodes.Status202Accepted)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/{id:guid}/binding/retry", RetryBindingHandler)
             .WithSummary("Re-enqueue provisioning for an existing binding. Resets provisioningStatus to Pending.")
             .Produces(StatusCodes.Status202Accepted)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/{id:guid}/binding-diff", GetBindingDiffHandler)
             .WithSummary("Preview column diff before updating a binding to a new version.")
             .Produces<BindingDiffResponse>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status404NotFound);

        // Set or clear a custom route path — the alternative to a Designer binding.
        // A non-empty path also clears any existing binding (mutually exclusive). The
        // "route-path" literal does not collide with /{id:guid} or the /binding* literals.
        group.MapPut("/{id:guid}/route-path", SetRoutePathHandler)
             .AddValidationFilter<SetMenuRoutePathRequest>()
             .WithSummary("Set or clear a menu item's custom route path. Clears any Designer binding. Body: { routePath: string | null }.")
             .Produces(StatusCodes.Status204NoContent)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        // Story 4.3 — multipart upload, no FluentValidation pipeline. DisableAntiforgery
        // is required for minimal-API IFormFile binding outside MVC.
        group.MapPost("/upload-icon", UploadIconHandler)
             .DisableAntiforgery()
             .WithSummary("Upload a PNG or JPG icon for a menu item")
             .Produces<UploadIconResponse>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        return group;
    }

    private static async Task<IResult> GetMenusHandler(
        IMenuService menuService,
        int page = 1,
        int pageSize = 25,
        Guid? parentId = null,
        string? sort = null,
        string? search = null,
        string? active = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(menuService);
        pageSize = Math.Min(Math.Max(pageSize, 1), 100);
        page = Math.Max(page, 1);
        var result = await menuService
            .GetMenusAsync(page, pageSize, parentId, sort, search, active, ct)
            .ConfigureAwait(false);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetMenuHandler(
        Guid id,
        IMenuService menuService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(menuService);
        var menu = await menuService.GetMenuAsync(id, ct).ConfigureAwait(false);
        return menu is null ? MenuNotFoundProblem() : Results.Ok(menu);
    }

    private static async Task<IResult> CreateMenuHandler(
        CreateMenuRequest request,
        IMenuService menuService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(menuService);

        var result = await menuService.CreateMenuAsync(request, ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            CreateMenuOutcome.Success => Results.Created(
                $"/api/admin/menus/{result.MenuId}",
                result.Menu),
            CreateMenuOutcome.ParentNotFound => Results.Problem(
                detail: "Parent menu item no longer exists.",
                title: "Parent menu not found",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "MENU_PARENT_NOT_FOUND",
                    ["messageKey"] = "menus.parentNotFound",
                }),
            CreateMenuOutcome.MaxDepthExceeded => Results.Problem(
                detail: "Menu items can only be nested one level deep.",
                title: "Maximum menu depth exceeded",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "MAX_MENU_DEPTH_EXCEEDED",
                    ["messageKey"] = "menus.maxDepthExceeded",
                }),
            _ => Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                }),
        };
    }

    private static async Task<IResult> UpdateMenuHandler(
        Guid id,
        UpdateMenuRequest request,
        IMenuService menuService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(menuService);

        var result = await menuService.UpdateMenuAsync(id, request, ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            UpdateMenuOutcome.NotFound => MenuNotFoundProblem(),
            UpdateMenuOutcome.Success => Results.NoContent(),
            _ => Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                }),
        };
    }

    private static async Task<IResult> DeleteMenuHandler(
        Guid id,
        IMenuService menuService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(menuService);

        var result = await menuService.DeleteMenuAsync(id, ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            DeleteMenuOutcome.NotFound => MenuNotFoundProblem(),
            DeleteMenuOutcome.HasChildren => Results.Problem(
                detail: "Remove sub-menu items first before deleting this menu item.",
                title: "Menu item has sub-menu children",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "MENU_HAS_CHILDREN",
                    ["messageKey"] = "menus.hasChildren",
                }),
            DeleteMenuOutcome.Success => Results.NoContent(),
            _ => Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                }),
        };
    }

    private static async Task<IResult> AssignRolesHandler(
        Guid id,
        AssignMenuRolesRequest request,
        IMenuService menuService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(menuService);

        // RoleIds is non-null here: AddValidationFilter<AssignMenuRolesRequest> runs
        // the NotNull rule before the handler and returns 422 on null.
        var result = await menuService.AssignMenuRolesAsync(id, request.RoleIds!, ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            AssignMenuRolesOutcome.MenuNotFound => MenuNotFoundProblem(),
            AssignMenuRolesOutcome.RolesNotFound => Results.Problem(
                detail: "One or more selected roles no longer exist.",
                title: "One or more role IDs were not found",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "ROLES_NOT_FOUND",
                    ["messageKey"] = "admin.menus.rolesNotFound",
                    ["invalidIds"] = result.InvalidIds,
                }),
            AssignMenuRolesOutcome.Conflict => Results.Problem(
                detail: "Another change to this menu's roles happened at the same time. Reload and try again.",
                title: "Conflicting concurrent modification",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "ASSIGNMENT_CONFLICT",
                    ["messageKey"] = "admin.menus.assignmentConflict",
                }),
            AssignMenuRolesOutcome.Success => Results.NoContent(),
            _ => Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                }),
        };
    }

    private static async Task<IResult> ReorderMenusHandler(
        ReorderMenusRequest request,
        IMenuService menuService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(menuService);

        // Items is non-null here: AddValidationFilter<ReorderMenusRequest> runs
        // the NotNull rule before the handler and returns 422 on null.
        var result = await menuService.ReorderMenusAsync(request.Items!, ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            ReorderMenusOutcome.MenusNotFound => Results.Problem(
                detail: "One or more menu items no longer exist. The list has been refreshed.",
                title: "One or more menu ids were not found",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "MENUS_NOT_FOUND",
                    ["messageKey"] = "admin.menus.reorderMenusNotFound",
                    ["invalidIds"] = result.InvalidIds,
                }),
            ReorderMenusOutcome.MixedScopes => Results.Problem(
                detail: "Items in a reorder must belong to the same parent.",
                title: "Reorder batch spans multiple parent scopes",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "REORDER_MIXED_SCOPES",
                    ["messageKey"] = "admin.menus.reorderMixedScopes",
                }),
            ReorderMenusOutcome.Conflict => Results.Problem(
                detail: "Another change to this menu group happened at the same time. The list has been refreshed.",
                title: "Concurrent modification",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "REORDER_CONFLICT",
                    ["messageKey"] = "admin.menus.reorderConflict",
                }),
            ReorderMenusOutcome.Success => Results.NoContent(),
            _ => Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                }),
        };
    }

    private static async Task<IResult> ToggleActiveHandler(
        Guid id,
        ToggleMenuActiveRequest request,
        IMenuService menuService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(menuService);

        // IsActive is non-null here: AddValidationFilter<ToggleMenuActiveRequest>
        // runs the NotNull rule before the handler and returns 422 on null.
        var result = await menuService.ToggleMenuActiveAsync(id, request.IsActive!.Value, ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            ToggleMenuActiveOutcome.NotFound => MenuNotFoundProblem(),
            ToggleMenuActiveOutcome.Success => Results.NoContent(),
            _ => Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                }),
        };
    }

    // Story 5.2 — bind handler. The validator runs NotNull on DesignerId/Version
    // before the handler executes, so the null-forgiving operators below are safe.
    // The schema-level "VERSION_NOT_PUBLISHED" check happens in MenuService (needs
    // a DB lookup), not the validator.
    private static async Task<IResult> BindDesignerHandler(
        Guid id,
        BindMenuDesignerRequest request,
        IMenuService menuService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(menuService);
        ArgumentNullException.ThrowIfNull(httpContext);

        var actorId = httpContext.User.FindFirst("userId")?.Value is { } s
            && Guid.TryParse(s, out var uid)
                ? uid
                : (Guid?)null;

        var result = await menuService
            .BindDesignerAsync(id, request.DesignerId!, request.Version!.Value, actorId, ct)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            BindMenuOutcome.MenuNotFound => MenuNotFoundProblem(),
            BindMenuOutcome.DesignerNotFound => Results.Problem(
                detail: "Designer or version not found.",
                title: "Designer version not found",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "DESIGNER_VERSION_NOT_FOUND",
                    ["messageKey"] = "admin.menus.designerVersionNotFound",
                }),
            BindMenuOutcome.VersionNotPublished => Results.Problem(
                detail: "Only Published versions can be bound.",
                title: "Only Published versions can be bound",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "VERSION_NOT_PUBLISHED",
                    ["messageKey"] = "designers.versionNotPublished",
                }),
            BindMenuOutcome.RepeaterCycle => Results.Problem(
                detail: "This Designer version cannot be bound — it contains a circular Repeater reference.",
                title: "Circular Repeater reference detected",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "REPEATER_CYCLE",
                    ["messageKey"] = "admin.menus.repeaterCycle",
                }),
            BindMenuOutcome.InvalidPgType => Results.Problem(
                detail: result.Detail ?? "A field has an invalid PostgreSQL type.",
                title: "Invalid field type",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "INVALID_PGTYPE",
                    ["messageKey"] = "admin.menus.invalidPgType",
                }),
            BindMenuOutcome.Success => Results.Accepted(),
            _ => Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                }),
        };
    }

    private static async Task<IResult> RetryBindingHandler(
        Guid id,
        IMenuService menuService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(menuService);
        ArgumentNullException.ThrowIfNull(httpContext);

        var actorId = httpContext.User.FindFirst("userId")?.Value is { } s
            && Guid.TryParse(s, out var uid)
                ? uid
                : (Guid?)null;

        var result = await menuService.RetryBindingAsync(id, actorId, ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            RetryBindingOutcome.MenuNotFound => MenuNotFoundProblem(),
            RetryBindingOutcome.NoBinding => Results.Problem(
                detail: "No binding exists on this menu item.",
                title: "No binding exists on this menu item",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "MENU_NO_BINDING",
                    ["messageKey"] = "admin.menus.noBinding",
                }),
            RetryBindingOutcome.Success => Results.Accepted(),
            _ => Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                }),
        };
    }

    private static async Task<IResult> GetBindingDiffHandler(
        Guid id,
        int targetVersion,
        IMenuService menuService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(menuService);
        // Story 5.6 — deferred 5.2 fix. A 0 or negative targetVersion would cause
        // BindingDiffService to bypass its "load designer version" lookup and emit
        // a misleading 404; rejecting at the boundary surfaces the real client bug.
        if (targetVersion <= 0)
            return Results.Problem(
                detail: "Target version must be a positive number.",
                title: "targetVersion must be a positive integer",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "INVALID_TARGET_VERSION",
                    ["messageKey"] = "admin.menus.invalidTargetVersion",
                });
        var diff = await menuService.GetBindingDiffAsync(id, targetVersion, ct).ConfigureAwait(false);
        return diff is null ? MenuNotFoundProblem() : Results.Ok(diff);
    }

    private static async Task<IResult> SetRoutePathHandler(
        Guid id,
        SetMenuRoutePathRequest request,
        IMenuService menuService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(menuService);

        // RoutePath may be null (clears the route); the validator only enforces the
        // shape when it's present, so passing it through directly is safe.
        var result = await menuService.SetRoutePathAsync(id, request.RoutePath, ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            SetRoutePathOutcome.MenuNotFound => MenuNotFoundProblem(),
            SetRoutePathOutcome.Success => Results.NoContent(),
            _ => Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                }),
        };
    }

    private static IResult MenuNotFoundProblem() =>
        Results.Problem(
            detail: "Menu item not found.",
            title: "Menu item not found",
            statusCode: StatusCodes.Status404NotFound,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "MENU_NOT_FOUND",
                ["messageKey"] = "menus.notFound",
            });

    private const long MaxIconBytes = 2 * 1024 * 1024;

    // Story 4.3 — upload icon. Validates MIME and size before touching MinIO so a
    // rejection never hits storage.
    private static async Task<IResult> UploadIconHandler(
        HttpContext httpContext,
        IIconStorageService storage,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(storage);

        if (!httpContext.Request.HasFormContentType)
        {
            return UploadInvalid("Request must be multipart/form-data");
        }

        // P8: cap buffering at the Kestrel/form layer so a large payload is rejected
        // before it fully lands in memory or temp storage.
        IFormCollection form;
        try
        {
            var formOptions = new FormOptions { MultipartBodyLengthLimit = MaxIconBytes };
            form = await httpContext.Request.ReadFormAsync(formOptions, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return UploadInvalid("File exceeds 2 MB limit");
        }

        var file = form.Files.GetFile("file") ?? (form.Files.Count > 0 ? form.Files[0] : null);

        if (file is null || file.Length <= 0)
        {
            return UploadInvalid("No file uploaded");
        }

        if (file.Length > MaxIconBytes)
        {
            return UploadInvalid("File exceeds 2 MB limit");
        }

        // P9: SVG rejected — inline SVG can carry <script> tags that execute when served
        // via presigned URLs. PNG and JPG are safe; vector icons use the Lucide picker.
        var extension = file.ContentType switch
        {
            "image/png" => "png",
            "image/jpeg" => "jpg",
            _ => null,
        };

        if (extension is null)
        {
            return UploadInvalid("File must be PNG or JPG");
        }

        using var stream = file.OpenReadStream();
        var objectKey = await storage.StoreAsync(stream, extension, file.Length, ct).ConfigureAwait(false);
        return Results.Ok(new UploadIconResponse("minio", objectKey));
    }

    private static IResult UploadInvalid(string title) =>
        Results.Problem(
            detail: "File must be PNG or JPG and under 2 MB.",
            title: title,
            statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "UPLOAD_INVALID",
                ["messageKey"] = "admin.menus.uploadInvalid",
            });
}
