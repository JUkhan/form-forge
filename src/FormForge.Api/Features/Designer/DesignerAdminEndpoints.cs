using FormForge.Api.Common;
using FormForge.Api.Features.Audit;
using FormForge.Api.Features.Audit.Dtos;
using FormForge.Api.Features.Designer.Dtos;

namespace FormForge.Api.Features.Designer;

// Story 5.6 — admin endpoints for inspecting and curating provisioned tables.
// Mounted under /api/admin/designers (RequirePlatformAdmin inherited from the
// parent /api/admin group in Program.cs).
internal static class DesignerAdminEndpoints
{
    internal static RouteGroupBuilder MapDesignerAdminEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/{designerId}/drift", GetDriftHandler)
             .WithSummary("List orphaned columns for a provisioned designer table")
             .Produces<SchemaDriftResponse>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{designerId}/columns/{columnName}", DropColumnHandler)
             .WithSummary("Drop an orphaned column from a provisioned designer table")
             .Produces(StatusCodes.Status204NoContent)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        // Story 5.7 — paginated DDL history per designer. Only GET is mapped;
        // ASP.NET returns 405 for any other verb on this path (AC-2).
        group.MapGet("/{designerId}/audit", AuditEndpoints.GetSchemaAuditLogHandler)
             .WithSummary("Paginated schema change audit log for a designer")
             .Produces<PagedResult<SchemaAuditEntryDto>>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status404NotFound);

        // UNIQUE-constraint management for provisioned CRUD tables.
        // /provisioned is a literal segment — it never collides with the
        // /{designerId}/… routes above (different segment count).
        group.MapGet("/provisioned", GetProvisionedDesignersHandler)
             .WithSummary("List CRUD designers that have a provisioned table")
             .Produces<ProvisionedDesignersResponse>(StatusCodes.Status200OK);

        group.MapGet("/{designerId}/unique-constraints", GetUniqueConstraintsHandler)
             .WithSummary("List columns and existing UNIQUE constraints for a provisioned table")
             .Produces<UniqueConstraintsResponse>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{designerId}/unique-constraints", AddUniqueConstraintHandler)
             .WithSummary("Add a UNIQUE constraint on one or more columns")
             .Produces<UniqueConstraintInfo>(StatusCodes.Status201Created)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status409Conflict)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/{designerId}/unique-constraints/{constraintName}", DropUniqueConstraintHandler)
             .WithSummary("Drop a UNIQUE constraint from a provisioned table")
             .Produces(StatusCodes.Status204NoContent)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        return group;
    }

    private static async Task<IResult> GetProvisionedDesignersHandler(
        UniqueConstraintService service,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service);
        var result = await service.ListProvisionedDesignersAsync(ct).ConfigureAwait(false);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetUniqueConstraintsHandler(
        string designerId,
        UniqueConstraintService service,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service);
        var result = await service.GetConstraintsAsync(designerId, ct).ConfigureAwait(false);
        return result is null ? DesignerNotFoundProblem() : Results.Ok(result);
    }

    private static async Task<IResult> AddUniqueConstraintHandler(
        string designerId,
        AddUniqueConstraintRequest request,
        UniqueConstraintService service,
        HttpContext httpContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(httpContext);

        var actorId = ActorIdFrom(httpContext);
        var result = await service
            .AddAsync(designerId, request?.Columns, actorId, ct)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            AddUniqueConstraintOutcome.Success =>
                Results.Created(
                    $"/api/admin/designers/{designerId}/unique-constraints/{result.Created!.Name}",
                    result.Created),
            AddUniqueConstraintOutcome.DesignerNotFound => DesignerNotFoundProblem(),
            AddUniqueConstraintOutcome.NoColumns => ConstraintProblem(
                StatusCodes.Status422UnprocessableEntity,
                "At least one column is required.",
                "UNIQUE_NO_COLUMNS",
                "admin.constraints.errorNoColumns"),
            AddUniqueConstraintOutcome.TooManyColumns => ConstraintProblem(
                StatusCodes.Status422UnprocessableEntity,
                "Too many columns for a single constraint.",
                "UNIQUE_TOO_MANY_COLUMNS",
                "admin.constraints.errorTooManyColumns"),
            AddUniqueConstraintOutcome.DuplicateColumns => ConstraintProblem(
                StatusCodes.Status422UnprocessableEntity,
                "The same column was listed more than once.",
                "UNIQUE_DUPLICATE_COLUMNS",
                "admin.constraints.errorDuplicateColumns"),
            AddUniqueConstraintOutcome.InvalidColumn => ConstraintProblem(
                StatusCodes.Status422UnprocessableEntity,
                $"Column '{result.OffendingColumn}' is not a valid identifier.",
                "UNIQUE_INVALID_COLUMN",
                "admin.constraints.errorInvalidColumn"),
            AddUniqueConstraintOutcome.ColumnNotFound => ConstraintProblem(
                StatusCodes.Status422UnprocessableEntity,
                $"Column '{result.OffendingColumn}' does not exist on this table.",
                "UNIQUE_COLUMN_NOT_FOUND",
                "admin.constraints.errorColumnNotFound"),
            AddUniqueConstraintOutcome.AlreadyExists => ConstraintProblem(
                StatusCodes.Status409Conflict,
                "A UNIQUE constraint already covers these columns.",
                "UNIQUE_ALREADY_EXISTS",
                "admin.constraints.errorAlreadyExists"),
            AddUniqueConstraintOutcome.DuplicateValues => ConstraintProblem(
                StatusCodes.Status422UnprocessableEntity,
                "Existing rows contain duplicate values for the selected column(s), so the constraint cannot be created.",
                "UNIQUE_DUPLICATE_VALUES",
                "admin.constraints.errorDuplicateValues"),
            _ => GenericProblem(),
        };
    }

    private static async Task<IResult> DropUniqueConstraintHandler(
        string designerId,
        string constraintName,
        UniqueConstraintService service,
        HttpContext httpContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(httpContext);

        var actorId = ActorIdFrom(httpContext);
        var outcome = await service
            .DropAsync(designerId, constraintName, actorId, ct)
            .ConfigureAwait(false);

        return outcome switch
        {
            DropUniqueConstraintOutcome.Success => Results.NoContent(),
            DropUniqueConstraintOutcome.DesignerNotFound => DesignerNotFoundProblem(),
            DropUniqueConstraintOutcome.ConstraintNotFound => ConstraintProblem(
                StatusCodes.Status404NotFound,
                "Constraint not found.",
                "UNIQUE_CONSTRAINT_NOT_FOUND",
                "admin.constraints.errorConstraintNotFound"),
            DropUniqueConstraintOutcome.NotUniqueConstraint => ConstraintProblem(
                StatusCodes.Status422UnprocessableEntity,
                "This constraint is not a UNIQUE constraint and cannot be dropped here.",
                "UNIQUE_NOT_UNIQUE_CONSTRAINT",
                "admin.constraints.errorNotUnique"),
            _ => GenericProblem(),
        };
    }

    private static Guid? ActorIdFrom(HttpContext httpContext) =>
        httpContext.User.FindFirst("userId")?.Value is { } s && Guid.TryParse(s, out var uid)
            ? uid
            : null;

    private static IResult ConstraintProblem(int statusCode, string detail, string code, string messageKey) =>
        Results.Problem(
            detail: detail,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = code,
                ["messageKey"] = messageKey,
            });

    private static IResult GenericProblem() =>
        Results.Problem(
            detail: "Something went wrong. Please try again.",
            statusCode: StatusCodes.Status500InternalServerError,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = "errors.genericError",
            });

    private static async Task<IResult> GetDriftHandler(
        string designerId,
        SchemaDriftService schemaDriftService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(schemaDriftService);
        var drift = await schemaDriftService.GetDriftAsync(designerId, ct).ConfigureAwait(false);
        return drift is null ? DesignerNotFoundProblem() : Results.Ok(drift);
    }

    private static async Task<IResult> DropColumnHandler(
        string designerId,
        string columnName,
        SchemaDriftService schemaDriftService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(schemaDriftService);
        ArgumentNullException.ThrowIfNull(httpContext);

        var actorId = httpContext.User.FindFirst("userId")?.Value is { } s
            && Guid.TryParse(s, out var uid)
                ? uid
                : (Guid?)null;

        var outcome = await schemaDriftService
            .DropColumnAsync(designerId, columnName, actorId, ct)
            .ConfigureAwait(false);

        return outcome switch
        {
            DropColumnOutcome.Success => Results.NoContent(),
            DropColumnOutcome.DesignerNotFound => DesignerNotFoundProblem(),
            DropColumnOutcome.ColumnNotFound => ColumnNotFoundProblem(),
            DropColumnOutcome.ColumnProtected => Results.Problem(
                detail: "This column cannot be dropped.",
                title: "This column cannot be dropped",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "COLUMN_NOT_ORPHANED",
                    ["messageKey"] = "admin.designers.drift.columnProtected",
                }),
            DropColumnOutcome.ColumnNotOrphaned => Results.Problem(
                detail: "This column is part of the current Designer schema and cannot be dropped.",
                title: "Column is part of the current Designer schema",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "COLUMN_NOT_ORPHANED",
                    ["messageKey"] = "admin.designers.drift.columnNotOrphaned",
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

    private static IResult DesignerNotFoundProblem() =>
        Results.Problem(
            detail: "Designer not found.",
            title: "Designer not found",
            statusCode: StatusCodes.Status404NotFound,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "DESIGNER_NOT_FOUND",
                ["messageKey"] = "admin.designers.notFound",
            });

    private static IResult ColumnNotFoundProblem() =>
        Results.Problem(
            detail: "Column not found.",
            title: "Column not found",
            statusCode: StatusCodes.Status404NotFound,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "COLUMN_NOT_FOUND",
                ["messageKey"] = "admin.designers.drift.columnNotFound",
            });
}
