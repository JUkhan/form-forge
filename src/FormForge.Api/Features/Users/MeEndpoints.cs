using FormForge.Api.Common.Endpoints;
using FormForge.Api.Common.Logging;
using FormForge.Api.Features.Auth;
using FormForge.Api.Features.Auth.Dtos;
using FormForge.Api.Features.Users.Dtos;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FormForge.Api.Features.Users;

internal static class MeEndpoints
{
    internal static RouteGroupBuilder MapMePreferencesEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPut("/me/preferences", UpdateMyPreferencesHandler)
             .AddValidationFilter<UpdateMyPreferencesRequest>()
             .WithSummary("Update the calling user's preferences (theme).")
             .Produces(StatusCodes.Status204NoContent)
             .Produces(StatusCodes.Status401Unauthorized)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        // Story 2.12 — authenticated user changes their own password. Dedicated
        // sliding-window rate-limit so brute-forcing currentPassword from a stolen
        // JWT is throttled even though the bearer token itself is valid.
        group.MapPut("/me/password", ChangePasswordHandler)
             .AddValidationFilter<ChangePasswordRequest>()
             .RequireRateLimiting("user-change-password")
             .WithSummary("Change the authenticated user's own password")
             .Produces(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status401Unauthorized)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        // Story 2.13 — TOTP MFA enrolment (enrolment ONLY; login challenge is 2.14).
        group.MapGet("/me/mfa/enrol", EnrolMfaHandler)
             .WithSummary("Initiate TOTP MFA enrolment — returns secret, QR code, and backup codes")
             .Produces<MfaEnrolResponse>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/me/mfa/verify", VerifyMfaEnrolmentHandler)
             .AddValidationFilter<MfaVerifyRequest>()
             .WithSummary("Verify TOTP code to complete MFA enrolment")
             .Produces(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status400BadRequest)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/me/mfa/status", GetMfaStatusHandler)
             .WithSummary("Get the caller's current MFA enabled state")
             .Produces(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status401Unauthorized);

        return group;
    }

    private static async Task<IResult> UpdateMyPreferencesHandler(
        UpdateMyPreferencesRequest request,
        FormForgeDbContext db,
        HttpContext httpContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(httpContext);

        var userIdClaim = httpContext.User.FindFirst("userId")?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return Results.Unauthorized();
        }

        var rows = await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(u => u.ThemePreference, request.ThemePreference)
                      .SetProperty(u => u.UpdatedAt, DateTimeOffset.UtcNow),
                ct)
            .ConfigureAwait(false);

        // rows == 0 means a still-valid JWT references a deleted user (admin race).
        // Treat as auth state invalid — 401, not 404 — so the SPA logs out.
        return rows == 0 ? Results.Unauthorized() : Results.NoContent();
    }

    private static async Task<IResult> ChangePasswordHandler(
        ChangePasswordRequest request,
        IAuthService authService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authService);
        ArgumentNullException.ThrowIfNull(httpContext);

        var userIdClaim = httpContext.User.FindFirst("userId")?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return Results.Unauthorized();
        }

        // Pass the current session's raw refresh token so AuthService can exclude it
        // from bulk revocation (the user stays logged in after the change).
        var currentRefreshToken = httpContext.Request.Cookies["refresh_token"];

        var result = await authService.ChangePasswordAsync(
            userId, request.CurrentPassword, request.NewPassword, currentRefreshToken, ct)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            ChangePasswordOutcome.CurrentPasswordIncorrect => Results.Problem(
                detail: "Current password is incorrect.",
                title: "Current password incorrect",
                statusCode: StatusCodes.Status401Unauthorized,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "CURRENT_PASSWORD_INCORRECT",
                    ["messageKey"] = "auth.currentPasswordIncorrect",
                    ["correlationId"] = httpContext.GetCorrelationId(),
                }),

            // AC-2 mandates 422 for a same-as-current new password; Results.ValidationProblem
            // defaults to 400, so the status is overridden explicitly. The frontend (AC-7)
            // keys its inline newPassword error on status === 422.
            ChangePasswordOutcome.NewPasswordSameAsCurrent => Results.ValidationProblem(
                errors: new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["newPassword"] = ["New password must differ from your current password."],
                },
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "PASSWORD_SAME_AS_CURRENT",
                    ["messageKey"] = "auth.passwordSameAsCurrent",
                    ["correlationId"] = httpContext.GetCorrelationId(),
                }),

            ChangePasswordOutcome.Success => Results.Ok(),

            _ => Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                    ["correlationId"] = httpContext.GetCorrelationId(),
                }),
        };
    }

    // Synchronous — InitiateEnrolment does no I/O; return IResult directly (no async overhead)
    private static IResult EnrolMfaHandler(
        IMfaService mfaService,
        HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(mfaService);
        ArgumentNullException.ThrowIfNull(httpContext);

        var userIdClaim = httpContext.User.FindFirst("userId")?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return Results.Unauthorized();
        }

        var email = httpContext.User.FindFirst("email")?.Value;
        if (string.IsNullOrEmpty(email))
        {
            return Results.Unauthorized();
        }

        var response = mfaService.InitiateEnrolment(userId, email);
        return Results.Ok(response);
    }

    private static async Task<IResult> VerifyMfaEnrolmentHandler(
        MfaVerifyRequest request,
        IMfaService mfaService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mfaService);
        ArgumentNullException.ThrowIfNull(httpContext);

        var userIdClaim = httpContext.User.FindFirst("userId")?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return Results.Unauthorized();
        }

        var result = await mfaService.VerifyEnrolmentAsync(userId, request.Code, ct)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            MfaVerifyEnrolmentOutcome.Success => Results.Ok(),

            MfaVerifyEnrolmentOutcome.NoPendingEnrolment => Results.Problem(
                detail: "No pending MFA enrolment found. Call GET /me/mfa/enrol first.",
                title: "No pending enrolment",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "MFA_NO_PENDING_ENROLMENT",
                    ["messageKey"] = "auth.mfaNoPendingEnrolment",
                    ["correlationId"] = httpContext.GetCorrelationId(),
                }),

            MfaVerifyEnrolmentOutcome.InvalidCode => Results.Problem(
                detail: "The provided TOTP code is invalid or expired.",
                title: "Invalid MFA code",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "MFA_CODE_INVALID",
                    ["messageKey"] = "auth.mfaCodeInvalid",
                    ["correlationId"] = httpContext.GetCorrelationId(),
                }),

            _ => Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                    ["correlationId"] = httpContext.GetCorrelationId(),
                }),
        };
    }

    private static async Task<IResult> GetMfaStatusHandler(
        IMfaService mfaService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mfaService);
        ArgumentNullException.ThrowIfNull(httpContext);

        var userIdClaim = httpContext.User.FindFirst("userId")?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return Results.Unauthorized();
        }

        var mfaEnabled = await mfaService.GetMfaStatusAsync(userId, ct)
            .ConfigureAwait(false);

        if (mfaEnabled is null)
            return Results.Unauthorized();

        return Results.Ok(new { mfaEnabled = mfaEnabled.Value });
    }
}
