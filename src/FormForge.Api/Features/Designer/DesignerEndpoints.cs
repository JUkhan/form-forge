using System.Security.Claims;
using FormForge.Api.Common;
using FormForge.Api.Common.Endpoints;
using FormForge.Api.Features.Designer.Dtos;

namespace FormForge.Api.Features.Designer;

internal static class DesignerEndpoints
{
    internal static RouteGroupBuilder MapDesignerEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        // POST requires platform-admin; GET endpoints are open to all authenticated
        // users so the DynamicComponent data-entry renderer (Epic 6) can fetch
        // schemas without an admin role.
        group.MapPost("/", CreateDesignerHandler)
             .RequireAuthorization("platform-admin")
             .AddValidationFilter<CreateDesignerRequest>()
             .WithSummary("Create a new designer")
             .Produces<DesignerResponse>(StatusCodes.Status201Created)
             .Produces(StatusCodes.Status409Conflict)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/", ListDesignersHandler)
             .WithSummary("List all designers (paginated)")
             .Produces<PagedResult<DesignerListItem>>(StatusCodes.Status200OK);

        group.MapGet("/{designerId}", GetDesignerHandler)
             .WithSummary("Get the latest version of a designer")
             .Produces<DesignerResponse>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{designerId}/versions/{version:int}", GetDesignerVersionHandler)
             .WithSummary("Get a specific version of a designer")
             .Produces<DesignerResponse>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{designerId}/versions", SaveVersionHandler)
             .RequireAuthorization("platform-admin")
             .AddValidationFilter<SaveVersionRequest>()
             .WithSummary("Create a new version snapshot of a designer")
             .Produces<DesignerResponse>(StatusCodes.Status201Created)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status409Conflict)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/{designerId}/versions/{version:int}", UpdateVersionHandler)
             .RequireAuthorization("platform-admin")
             .AddValidationFilter<UpdateVersionRequest>()
             .WithSummary("Update a version's content in place (no new snapshot)")
             .Produces<DesignerResponse>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/{designerId}/versions/{version:int}/auth-filter", SetAuthFilterFieldKeyHandler)
             .RequireAuthorization("platform-admin")
             .WithSummary("Set or clear a version's auth filter fieldKey")
             .Produces<DesignerResponse>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/{designerId}/versions/{version:int}/dataset", SetDatasetHandler)
             .RequireAuthorization("platform-admin")
             .WithSummary("Set or clear a version's bound dataset")
             .Produces<DesignerResponse>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/{designerId}/duplicate", DuplicateDesignerHandler)
             .RequireAuthorization("platform-admin")
             .WithSummary("Duplicate a designer to a new copy")
             .Produces<DesignerResponse>(StatusCodes.Status201Created)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status409Conflict)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/{designerId}/versions/{version:int}/status", UpdateVersionStatusHandler)
             .RequireAuthorization("platform-admin")
             .AddValidationFilter<UpdateVersionStatusRequest>()
             .WithSummary("Publish or archive a designer version")
             .Produces<DesignerResponse>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status409Conflict)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        return group;
    }

    private static async Task<IResult> CreateDesignerHandler(
        CreateDesignerRequest request,
        ClaimsPrincipal user,
        IDesignerService designerService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(designerService);

        if (!Guid.TryParse(user.FindFirst("userId")?.Value, out var userId))
        {
            return Results.Unauthorized();
        }

        var result = await designerService.CreateAsync(request, userId, ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            CreateDesignerOutcome.IdentifierReservedKeyword => Results.Problem(
                title: "Identifier is a reserved PostgreSQL keyword",
                detail: result.ErrorDetail,
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "IDENTIFIER_RESERVED_KEYWORD",
                    ["messageKey"] = "designers.identifierReservedKeyword",
                }),
            CreateDesignerOutcome.IdentifierInvalid => Results.Problem(
                title: "Invalid designer identifier",
                detail: result.ErrorDetail,
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "IDENTIFIER_INVALID",
                    ["messageKey"] = "designers.identifierInvalid",
                }),
            CreateDesignerOutcome.DesignerExists => DesignerExistsProblem(),
            CreateDesignerOutcome.Success => Results.Created(
                $"/api/designers/{result.Designer!.DesignerId}",
                result.Designer),
            _ => Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                }),
        };
    }

    private static async Task<IResult> ListDesignersHandler(
        IDesignerService designerService,
        int page = 1,
        int pageSize = 25,
        string? sort = null,
        string? search = null,
        string? status = null,
        string? mode = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(designerService);
        pageSize = Math.Min(Math.Max(pageSize, 1), 100);
        page = Math.Max(page, 1);
        var result = await designerService
            .ListAsync(page, pageSize, sort, search, status, mode, ct)
            .ConfigureAwait(false);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetDesignerHandler(
        string designerId,
        IDesignerService designerService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(designerService);
        var result = await designerService.GetLatestAsync(designerId, ct).ConfigureAwait(false);
        return result is null ? DesignerNotFoundProblem() : Results.Ok(result);
    }

    private static async Task<IResult> GetDesignerVersionHandler(
        string designerId,
        int version,
        IDesignerService designerService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(designerService);
        var result = await designerService.GetVersionAsync(designerId, version, ct).ConfigureAwait(false);
        return result is null ? DesignerNotFoundProblem() : Results.Ok(result);
    }

    private static async Task<IResult> UpdateVersionStatusHandler(
        string designerId,
        int version,
        UpdateVersionStatusRequest request,
        ClaimsPrincipal user,
        IDesignerService designerService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(designerService);

        if (!Guid.TryParse(user.FindFirst("userId")?.Value, out var userId))
        {
            return Results.Unauthorized();
        }

        // AC-5: "Draft" is a recognized but invalid TARGET status — it has its
        // own envelope (STATUS_INVALID) distinct from the generic VALIDATION_FAILED
        // returned for unknown values like "foo". The validator recognizes Draft
        // so the request reaches the handler; the handler rejects it here.
        if (string.Equals(request.Status, "Draft", StringComparison.Ordinal))
        {
            return StatusInvalidProblem();
        }

        var result = await designerService
            .UpdateVersionStatusAsync(designerId, version, request.Status, userId, ct)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            UpdateVersionStatusOutcome.Success => Results.Ok(result.Designer),
            UpdateVersionStatusOutcome.StatusUnchanged => Results.Ok(result.Designer),
            UpdateVersionStatusOutcome.DesignerNotFound => DesignerNotFoundProblem(),
            UpdateVersionStatusOutcome.VersionNotFound => VersionNotFoundProblem(),
            UpdateVersionStatusOutcome.PublishConflict => PublishConflictProblem(),
            _ => Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                }),
        };
    }

    private static async Task<IResult> SetAuthFilterFieldKeyHandler(
        string designerId,
        int version,
        SetAuthFilterFieldKeyRequest request,
        IDesignerService designerService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(designerService);

        var result = await designerService
            .SetAuthFilterFieldKeyAsync(designerId, version, request.AuthFilterFieldKey, ct)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            SetAuthFilterFieldKeyOutcome.Success => Results.Ok(result.Designer),
            SetAuthFilterFieldKeyOutcome.DesignerNotFound => DesignerNotFoundProblem(),
            SetAuthFilterFieldKeyOutcome.VersionNotFound => VersionNotFoundProblem(),
            SetAuthFilterFieldKeyOutcome.FieldKeyNotInVersion => AuthFilterFieldKeyInvalidProblem(),
            _ => Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                }),
        };
    }

    private static async Task<IResult> SetDatasetHandler(
        string designerId,
        int version,
        SetDatasetRequest request,
        IDesignerService designerService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(designerService);

        var result = await designerService
            .SetDatasetAsync(designerId, version, request.DatasetId, ct)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            SetDatasetOutcome.Success => Results.Ok(result.Designer),
            SetDatasetOutcome.DesignerNotFound => DesignerNotFoundProblem(),
            SetDatasetOutcome.VersionNotFound => VersionNotFoundProblem(),
            SetDatasetOutcome.DatasetNotFound => DatasetBindingInvalidProblem(),
            _ => Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                }),
        };
    }

    private static async Task<IResult> DuplicateDesignerHandler(
        string designerId,
        ClaimsPrincipal user,
        IDesignerService designerService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(designerService);

        if (!Guid.TryParse(user.FindFirst("userId")?.Value, out var userId))
        {
            return Results.Unauthorized();
        }

        var result = await designerService
            .DuplicateAsync(designerId, userId, ct)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            DuplicateOutcome.Success => Results.Created(
                $"/api/designers/{result.Designer!.DesignerId}",
                result.Designer),
            DuplicateOutcome.DesignerNotFound => DesignerNotFoundProblem(),
            DuplicateOutcome.DuplicateConflict => DuplicateConflictProblem(),
            DuplicateOutcome.SourceIdTooLong => DuplicateIdTooLongProblem(),
            _ => Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                }),
        };
    }

    private static async Task<IResult> SaveVersionHandler(
        string designerId,
        SaveVersionRequest request,
        ClaimsPrincipal user,
        IDesignerService designerService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(designerService);

        if (!Guid.TryParse(user.FindFirst("userId")?.Value, out var userId))
        {
            return Results.Unauthorized();
        }

        var result = await designerService
            .SaveVersionAsync(designerId, request.RootElement, userId, ct)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            SaveVersionOutcome.Success => Results.Created(
                $"/api/designers/{designerId}/versions/{result.Designer!.LatestVersion}",
                result.Designer),
            SaveVersionOutcome.DesignerNotFound => DesignerNotFoundProblem(),
            SaveVersionOutcome.FieldKeyValidationFailed => FieldKeyValidationProblem(result.FieldKeyErrors!),
            SaveVersionOutcome.VersionConflict => VersionConflictProblem(),
            SaveVersionOutcome.ViewReferenceRejected => ViewReferenceRejectedProblem(result.ViewReferenceDesignerId!),
            _ => Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                }),
        };
    }

    private static async Task<IResult> UpdateVersionHandler(
        string designerId,
        int version,
        UpdateVersionRequest request,
        ClaimsPrincipal user,
        IDesignerService designerService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(designerService);

        if (!Guid.TryParse(user.FindFirst("userId")?.Value, out var userId))
        {
            return Results.Unauthorized();
        }

        var result = await designerService
            .UpdateVersionAsync(designerId, version, request.RootElement, request.DisplayName, userId, ct)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            UpdateVersionOutcome.Success => Results.Ok(result.Designer),
            UpdateVersionOutcome.DesignerNotFound => DesignerNotFoundProblem(),
            UpdateVersionOutcome.VersionNotFound => VersionNotFoundProblem(),
            UpdateVersionOutcome.FieldKeyValidationFailed => FieldKeyValidationProblem(result.FieldKeyErrors!),
            UpdateVersionOutcome.ViewReferenceRejected => ViewReferenceRejectedProblem(result.ViewReferenceDesignerId!),
            _ => Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                }),
        };
    }

    private static IResult FieldKeyValidationProblem(IReadOnlyList<FieldKeyValidationError> errors)
    {
        // Single envelope-code for the whole batch — the SPA reads
        // `extensions.errors` to render the per-element list inline.
        // FIELD_KEY_INVALID stays the umbrella code even when the
        // batch contains FIELD_KEY_COLLISION / FIELD_KEY_MISSING items;
        // the per-error `code` carries the specific class.
        return Results.Problem(
            detail: "Some field keys are invalid. Review the highlighted errors and try again.",
            title: "Field key validation failed",
            statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "FIELD_KEY_INVALID",
                ["messageKey"] = "designers.fieldKeyInvalid",
                ["errors"] = errors,
            });
    }

    private static IResult VersionConflictProblem() =>
        Results.Problem(
            detail: "Another save happened while you were editing. Reload the page and try again.",
            title: "A concurrent save has already created the next version. Reload and try again.",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "VERSION_CONFLICT",
                ["messageKey"] = "designers.versionConflict",
            });

    // FR-54 AC-5 / Decision 4.11 — a Dropdown or Repeater cannot reference a
    // VIEW-mode designer as its data source. The frontend pickers exclude VIEW
    // components; this is the independent server-side guard on Save.
    private static IResult ViewReferenceRejectedProblem(string referencedId) =>
        Results.Problem(
            detail: $"Designer '{referencedId}' is VIEW-mode and cannot be referenced as a data source.",
            title: "VIEW-mode designer cannot be a data source",
            statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "VIEW_REFERENCE_REJECTED",
                ["messageKey"] = "designers.viewReferenceRejected",
                ["designerId"] = referencedId,
            });

    private static IResult DesignerNotFoundProblem() =>
        Results.Problem(
            detail: "Designer not found.",
            title: "Designer not found",
            statusCode: StatusCodes.Status404NotFound,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "DESIGNER_NOT_FOUND",
                ["messageKey"] = "designers.notFound",
            });

    private static IResult DuplicateConflictProblem() =>
        Results.Problem(
            detail: "Too many copies of this designer exist. Rename one and try again.",
            title: "Could not generate a unique identifier for the duplicate.",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "DUPLICATE_CONFLICT",
                ["messageKey"] = "designers.duplicateConflict",
            });

    private static IResult DuplicateIdTooLongProblem() =>
        Results.Problem(
            detail: "Designer ID is too long to duplicate. Rename it first.",
            title: "Designer ID is too long to duplicate.",
            statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "DUPLICATE_ID_TOO_LONG",
                ["messageKey"] = "designers.duplicateIdTooLong",
            });

    private static IResult DesignerExistsProblem() =>
        Results.Problem(
            detail: "A designer with this ID already exists.",
            title: "A designer with this ID already exists",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "DESIGNER_EXISTS",
                ["messageKey"] = "designers.designerExists",
            });

    private static IResult AuthFilterFieldKeyInvalidProblem() =>
        Results.Problem(
            detail: "The selected field is not part of this designer version.",
            title: "Invalid auth filter field key",
            statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "AUTH_FILTER_FIELD_KEY_INVALID",
                ["messageKey"] = "designers.authFilterFieldKeyInvalid",
            });

    private static IResult DatasetBindingInvalidProblem() =>
        Results.Problem(
            detail: "The selected dataset does not exist.",
            title: "Invalid dataset binding",
            statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "DATASET_BINDING_INVALID",
                ["messageKey"] = "designers.datasetBindingInvalid",
            });

    private static IResult VersionNotFoundProblem() =>
        Results.Problem(
            detail: "Version not found.",
            title: "Version not found",
            statusCode: StatusCodes.Status404NotFound,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "VERSION_NOT_FOUND",
                ["messageKey"] = "designers.versionNotFound",
            });

    // AC-9 / Epic 5 pattern: declared here so Story 5.2's MenuBinding handler
    // can reuse the exact error envelope when rejecting Draft targets. Visibility
    // is internal (not private) for that cross-feature reuse.
    internal static IResult VersionNotPublishedProblem() =>
        Results.Problem(
            detail: "Only Published versions can be bound.",
            title: "Only Published versions can be bound to Menu Items",
            statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "VERSION_NOT_PUBLISHED",
                ["messageKey"] = "designers.versionNotPublished",
            });

    private static IResult PublishConflictProblem() =>
        Results.Problem(
            detail: "A concurrent publish occurred. Reload and try again.",
            title: "A concurrent publish created a Published version. Reload and try again.",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "PUBLISH_CONFLICT",
                ["messageKey"] = "designers.publishConflict",
            });

    // AC-5: "Draft" is a recognized but invalid TARGET status. Distinct envelope
    // from VALIDATION_FAILED so the SPA can render a status-specific message
    // ("Draft isn't a valid status to set — only Published or Archived are.").
    private static IResult StatusInvalidProblem() =>
        Results.Problem(
            detail: "Draft isn't a valid target status. Choose Published or Archived.",
            title: "Status is not a valid target",
            statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "STATUS_INVALID",
                ["messageKey"] = "designers.statusInvalid",
            });
}
