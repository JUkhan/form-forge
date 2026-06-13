using FormForge.Api.Common;
using FormForge.Api.Common.Endpoints;
using FormForge.Api.Common.Logging;
using FormForge.Api.Features.Auth;
using FormForge.Api.Features.Users.Dtos;
using Microsoft.Extensions.Options;

namespace FormForge.Api.Features.Users;

// Registered in AdminEndpoints.MapAdminEndpoints as group.MapGroup("/users").
internal static class UserEndpoints
{
    internal static RouteGroupBuilder MapUserAdminEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/", GetUsersHandler)
             .WithSummary("List users (paginated)")
             .Produces<PagedResult<UserListItem>>(StatusCodes.Status200OK);

        group.MapGet("/{id:guid}", GetUserHandler)
             .WithSummary("Get a user by ID")
             .Produces<UserDetailResponse>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/", CreateUserHandler)
             .AddValidationFilter<CreateUserRequest>()
             .WithSummary("Create a new user account")
             .Produces<CreateUserResponse>(StatusCodes.Status201Created)
             .Produces(StatusCodes.Status409Conflict)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/{id:guid}", UpdateUserHandler)
             .AddValidationFilter<UpdateUserRequest>()
             .WithSummary("Update a user's display name and/or password")
             .Produces(StatusCodes.Status204NoContent)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/{id:guid}/deactivate", DeactivateUserHandler)
             .WithSummary("Deactivate a user (revokes all active refresh tokens)")
             .Produces(StatusCodes.Status204NoContent)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status409Conflict);

        group.MapPut("/{id:guid}/reactivate", ReactivateUserHandler)
             .WithSummary("Reactivate a previously deactivated user")
             .Produces(StatusCodes.Status204NoContent)
             .Produces(StatusCodes.Status404NotFound);

        group.MapPut("/{id:guid}/roles", AssignRolesHandler)
             .AddValidationFilter<AssignRolesRequest>()
             .WithSummary("Replace a user's complete role set atomically")
             .Produces(StatusCodes.Status204NoContent)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status409Conflict)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/{id:guid}/mfa", ResetUserMfaHandler)
             .WithSummary("Admin: disable MFA, revoke all sessions, and delete all backup codes for a user")
             .Produces(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> GetUsersHandler(
        IUserService userService,
        int page = 1,
        int pageSize = 25,
        string? sort = null,
        string? search = null,
        string? status = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userService);
        // Same clamp as RoleEndpoints.GetRolesHandler. Floor at 1 and cap at 100 so
        // an unbounded pageSize cannot blow up memory or the SQL parameter table.
        pageSize = Math.Min(Math.Max(pageSize, 1), 100);
        page = Math.Max(page, 1);
        var result = await userService
            .GetUsersAsync(page, pageSize, sort, search, status, ct)
            .ConfigureAwait(false);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetUserHandler(
        Guid id,
        IUserService userService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(userService);
        var user = await userService.GetUserAsync(id, ct).ConfigureAwait(false);
        return user is null ? UserNotFoundProblem() : Results.Ok(user);
    }

    private static async Task<IResult> CreateUserHandler(
        CreateUserRequest request,
        IUserService userService,
        IEmailService emailService,
        IOptions<SmtpOptions> smtpOptions,
        ILoggerFactory loggerFactory,
        HttpContext httpContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(userService);
        ArgumentNullException.ThrowIfNull(emailService);
        ArgumentNullException.ThrowIfNull(smtpOptions);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(httpContext);

        var result = await userService.CreateUserAsync(request, ct).ConfigureAwait(false);

        if (result.Outcome == CreateUserOutcome.DuplicateEmail)
        {
            return UserEmailConflictProblem();
        }

        if (result.Outcome != CreateUserOutcome.Success || result.User is null)
        {
            return Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                });
        }

        // AC-1 / AC-3: send the welcome email after the user is persisted. The
        // attempt is bounded by a 3-second timeout and must NEVER fail the request —
        // on any failure (incl. timeout) we surface a non-fatal warning so the admin
        // can share credentials manually. The transport logs the underlying cause;
        // here we only need to log the caller-side timeout. (Decision 2.9 / AR-53.)
        var detail = result.User;
        var emailSent = false;
        using (var emailCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            emailCts.CancelAfter(TimeSpan.FromSeconds(3));
            var correlationId = httpContext.GetCorrelationId() ?? "(none)";
            // D1→P / P3: prefer the configured base URL to prevent Host-header spoofing;
            // fall back to the request origin, guarding against an empty Host header.
            var configuredBase = smtpOptions.Value.BaseUrl;
            var loginBaseUrl = !string.IsNullOrEmpty(configuredBase)
                ? configuredBase
                : httpContext.Request.Host.HasValue
                    ? $"{httpContext.Request.Scheme}://{httpContext.Request.Host}"
                    : string.Empty;
            try
            {
                emailSent = await emailService
                    .TrySendWelcomeEmailAsync(
                        detail.Email,
                        request.TemporaryPassword!,
                        loginBaseUrl,
                        correlationId,
                        emailCts.Token)
                    .ConfigureAwait(false);
            }
#pragma warning disable CA1031 // welcome email is best-effort; never fail user creation
            catch (Exception ex)
#pragma warning restore CA1031
            {
                var logger = loggerFactory.CreateLogger("FormForge.Api.Features.Users.UserEndpoints");
                EmailServiceLog.WelcomeEmailFailed(logger, ex, detail.Email, "welcome", correlationId);
                emailSent = false;
            }
        }

        var warnings = emailSent
            ? Array.Empty<string>()
            : new[] { "Welcome email could not be sent" };

        var response = new CreateUserResponse(
            detail.Id,
            detail.Email,
            detail.DisplayName,
            detail.IsActive,
            detail.CreatedAt,
            detail.UpdatedAt,
            detail.Roles,
            warnings);

        return Results.Created($"/api/admin/users/{result.UserId}", response);
    }

    private static async Task<IResult> UpdateUserHandler(
        Guid id,
        UpdateUserRequest request,
        IUserService userService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(userService);

        var result = await userService.UpdateUserAsync(id, request, ct).ConfigureAwait(false);
        return result.Outcome switch
        {
            UpdateUserOutcome.NotFound => UserNotFoundProblem(),
            UpdateUserOutcome.Success => Results.NoContent(),
            _ => Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                }),
        };
    }

    private static async Task<IResult> DeactivateUserHandler(
        Guid id,
        IUserService userService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(userService);
        ArgumentNullException.ThrowIfNull(httpContext);

        // Claim name is "userId" per JwtTokenService.CreateAccessToken — Program.cs
        // sets MapInboundClaims=false and NameClaimType="userId", so we read the
        // exact claim rather than the ClaimTypes.NameIdentifier alias.
        var userIdClaim = httpContext.User.FindFirst("userId")?.Value;
        if (!Guid.TryParse(userIdClaim, out var currentUserId))
        {
            return Results.Unauthorized();
        }

        var result = await userService.DeactivateUserAsync(id, currentUserId, ct).ConfigureAwait(false);
        return result.Outcome switch
        {
            DeactivateUserOutcome.NotFound => UserNotFoundProblem(),
            DeactivateUserOutcome.SelfDeactivation => SelfDeactivationProblem(),
            DeactivateUserOutcome.Success => Results.NoContent(),
            _ => Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                }),
        };
    }

    private static async Task<IResult> ReactivateUserHandler(
        Guid id,
        IUserService userService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(userService);
        var result = await userService.ReactivateUserAsync(id, ct).ConfigureAwait(false);
        return result.Outcome switch
        {
            ReactivateUserOutcome.NotFound => UserNotFoundProblem(),
            ReactivateUserOutcome.Success => Results.NoContent(),
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
        AssignRolesRequest request,
        IUserService userService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(userService);

        // RoleIds is non-null here: AddValidationFilter<AssignRolesRequest> runs the
        // NotNull rule before the handler, returning 422 if null.
        var result = await userService.AssignRolesAsync(id, request.RoleIds!, ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            AssignRolesOutcome.UserNotFound => UserNotFoundProblem(),

            AssignRolesOutcome.RolesNotFound => Results.Problem(
                detail: "One or more selected roles no longer exist.",
                title: "One or more role IDs do not exist",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "ROLES_NOT_FOUND",
                    ["messageKey"] = "users.rolesNotFound",
                    ["invalidRoleIds"] = result.InvalidRoleIds,
                }),

            AssignRolesOutcome.LastAdminLockout => Results.Problem(
                detail: "Cannot remove the last platform-admin assignment.",
                title: "Cannot remove the last platform-admin assignment",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "LAST_ADMIN_LOCKOUT",
                    ["messageKey"] = "users.lastAdminLockout",
                }),

            AssignRolesOutcome.Conflict => Results.Problem(
                detail: "Could not update roles. Please try again.",
                title: "Conflicting concurrent modification",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "ASSIGNMENT_CONFLICT",
                    ["messageKey"] = "users.assignmentConflict",
                }),

            AssignRolesOutcome.Success => Results.NoContent(),
            _ => Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                }),
        };
    }

    private static async Task<IResult> ResetUserMfaHandler(
        Guid id,
        IUserService userService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(userService);

        var result = await userService.ResetUserMfaAsync(id, ct).ConfigureAwait(false);
        return result.Outcome switch
        {
            AdminMfaResetOutcome.NotFound => UserNotFoundProblem(),
            AdminMfaResetOutcome.Success => Results.Ok(),
            _ => Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                }),
        };
    }

    private static IResult UserNotFoundProblem() =>
        Results.Problem(
            detail: "User not found.",
            title: "User not found",
            statusCode: StatusCodes.Status404NotFound,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "USER_NOT_FOUND",
                ["messageKey"] = "users.notFound",
            });

    private static IResult UserEmailConflictProblem() =>
        Results.Problem(
            detail: "A user with this email already exists.",
            title: "User email already exists",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "USER_EMAIL_CONFLICT",
                ["messageKey"] = "users.emailConflict",
            });

    private static IResult SelfDeactivationProblem() =>
        Results.Problem(
            detail: "You cannot deactivate your own account.",
            title: "Cannot deactivate your own account",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "SELF_DEACTIVATION",
                ["messageKey"] = "users.selfDeactivation",
            });
}
