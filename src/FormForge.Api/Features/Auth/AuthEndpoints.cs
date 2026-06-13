using FormForge.Api.Common.Endpoints;
using FormForge.Api.Common.Logging;
using FormForge.Api.Features.Auth.Dtos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FormForge.Api.Features.Auth;

internal static class AuthEndpoints
{
    internal static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/login", LoginHandler)
             .AddValidationFilter<LoginRequest>()
             .RequireRateLimiting("auth-login")
             .WithSummary("Authenticate with email and password")
             .WithDescription(
                 "Returns a 15-minute JWT access token and a 7-day opaque refresh token " +
                 "(also set as HttpOnly SameSite=Strict cookie). " +
                 "Rate-limited to 10 requests per IP per minute.");

        group.MapPost("/refresh", RefreshHandler)
             .RequireRateLimiting("auth-refresh")
             .WithSummary("Rotate refresh token and obtain a new access token")
             .WithDescription(
                 "Reads the refresh token from the HttpOnly cookie. " +
                 "On success, revokes the old token and issues a new pair. " +
                 "Rate-limited to 30 requests per IP per minute.");

        group.MapPost("/logout", LogoutHandler)
             .RequireRateLimiting("auth-refresh")
             .Produces(StatusCodes.Status204NoContent)
             .WithSummary("Revoke the refresh token and clear the refresh cookie")
             .WithDescription(
                 "Reads the refresh token from the HttpOnly cookie. " +
                 "On success returns 204 No Content with a Set-Cookie that expires the refresh_token cookie immediately. " +
                 "Idempotent: returns 204 even when no cookie is present or the token is already revoked.");

        // Story 2.11 — self-service password reset. Both endpoints live in the open
        // /api/auth group (no RequireAuth on the group), so no AllowAnonymous needed.
        group.MapPost("/forgot-password", ForgotPasswordHandler)
             .AddValidationFilter<ForgotPasswordRequest>()
             .RequireRateLimiting("auth-forgot-password")
             .WithSummary("Request a password reset email")
             .WithDescription(
                 "Always returns 200 with a generic message regardless of whether the email is registered " +
                 "(anti-enumeration). When the email matches an active user, a single-use reset link valid for " +
                 "1 hour is dispatched asynchronously. Rate-limited to 5 requests per IP per minute.");

        group.MapPost("/reset-password", ResetPasswordHandler)
             .AddValidationFilter<ResetPasswordRequest>()
             .RequireRateLimiting("auth-reset-password")
             .WithSummary("Consume a password reset token and set a new password")
             .WithDescription(
                 "Validates the single-use token (hash match, not expired, not already used), sets the new " +
                 "BCrypt password hash, and revokes all active refresh tokens. Returns 400 RESET_TOKEN_INVALID " +
                 "for a bad/expired/used token and 422 if the new password matches the current one. " +
                 "Rate-limited to 10 requests per IP per minute.");

        // Story 2.14 — TOTP login-time MFA challenge. Open auth group (no RequireAuth):
        // the mfaSessionToken issued by /login is the proof the password step passed.
        group.MapPost("/mfa/verify", VerifyMfaLoginHandler)
             .AddValidationFilter<MfaVerifyLoginRequest>()
             .RequireRateLimiting("auth-mfa-verify")
             .WithSummary("Complete MFA-gated login — verify TOTP or backup code, receive JWT pair")
             .Produces<LoginResponse>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status401Unauthorized)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        return group;
    }

    private static async Task<IResult> LoginHandler(
        LoginRequest request,
        IAuthService authService,
        HttpContext httpContext,
        IOptions<JwtOptions> jwtOptions,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authService);
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(jwtOptions);

        var result = await authService.LoginAsync(request.Email, request.Password, ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            AuthLoginOutcome.InvalidCredentials => Results.Problem(
                detail: "Invalid email or password.",
                title: "Invalid credentials",
                statusCode: StatusCodes.Status401Unauthorized,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "INVALID_CREDENTIALS",
                    ["messageKey"] = "auth.invalidCredentials",
                    ["correlationId"] = httpContext.GetCorrelationId(),
                }),

            AuthLoginOutcome.AccountInactive => Results.Problem(
                detail: "Your account has been deactivated. Contact an administrator.",
                title: "Account inactive",
                statusCode: StatusCodes.Status403Forbidden,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "ACCOUNT_INACTIVE",
                    ["messageKey"] = "auth.accountInactive",
                    ["correlationId"] = httpContext.GetCorrelationId(),
                }),

            // Story 2.14 — account has MFA enabled: return the challenge envelope
            // with the session token; no JWT or refresh cookie is issued here.
            AuthLoginOutcome.MfaRequired => Results.Ok(new MfaRequiredResponse(true, result.MfaSessionToken!)),

            AuthLoginOutcome.Success => SetRefreshCookieAndReturn(httpContext, result.Response!, jwtOptions.Value.RefreshTokenTtlDays),

            _ => Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                    ["correlationId"] = httpContext.GetCorrelationId(),
                }),
        };
    }

    // Story 2.14 — complete an MFA-gated login: verify the second factor, then
    // issue the JWT pair via the shared CompleteMfaLoginAsync path.
    private static async Task<IResult> VerifyMfaLoginHandler(
        MfaVerifyLoginRequest request,
        IMfaService mfaService,
        IAuthService authService,
        HttpContext httpContext,
        IOptions<JwtOptions> jwtOptions,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mfaService);
        ArgumentNullException.ThrowIfNull(authService);
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(jwtOptions);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var mfaResult = await mfaService.VerifyMfaLoginAsync(request.MfaSessionToken, request.Code, ct)
            .ConfigureAwait(false);

        if (mfaResult.Outcome == MfaLoginVerifyOutcome.SessionInvalid)
        {
            return MfaSessionInvalidProblem(httpContext.GetCorrelationId());
        }

        if (mfaResult.Outcome == MfaLoginVerifyOutcome.InvalidCode)
        {
            return Results.Problem(
                detail: "The provided code is incorrect.",
                title: "Invalid MFA code",
                statusCode: StatusCodes.Status401Unauthorized,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "MFA_CODE_INVALID",
                    ["messageKey"] = "auth.mfaCodeInvalid",
                    ["correlationId"] = httpContext.GetCorrelationId(),
                });
        }

        // MfaLoginVerifyOutcome.Success — issue JWT pair (same flow as non-MFA login).
        var loginResult = await authService.CompleteMfaLoginAsync(mfaResult.UserId!.Value, ct)
            .ConfigureAwait(false);

        if (loginResult.Outcome != AuthLoginOutcome.Success)
        {
            // Expected race: the user was deleted between MFA-session creation and verify.
            // Surface as 401 (the session is no longer valid), not a generic 500 that would
            // pollute error dashboards as a server fault. Log so the race stays observable.
            var correlationId = httpContext.GetCorrelationId();
            var logger = loggerFactory.CreateLogger(nameof(AuthEndpoints));
            AuthEndpointsLog.MfaCompleteUserMissing(
                logger, mfaResult.UserId.Value, correlationId ?? "(none)");

            return MfaSessionInvalidProblem(correlationId);
        }

        return SetRefreshCookieAndReturn(httpContext, loginResult.Response!, jwtOptions.Value.RefreshTokenTtlDays);
    }

    private static async Task<IResult> RefreshHandler(
        IAuthService authService,
        HttpContext httpContext,
        IOptions<JwtOptions> jwtOptions,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authService);
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(jwtOptions);

        var rawToken = httpContext.Request.Cookies["refresh_token"];
        if (string.IsNullOrEmpty(rawToken))
        {
            return RefreshTokenInvalidResponse(httpContext);
        }

        var result = await authService.RefreshAsync(rawToken, ct).ConfigureAwait(false);

        // Spec AC-2: all non-Success outcomes (missing cookie, not-found, expired,
        // replayed, inactive account) share the same public envelope so the wire
        // response leaks no information about *why* the token was rejected.
        // Differentiation lives only in server metrics + logs.
        return result.Outcome == AuthRefreshOutcome.Success
            ? SetRefreshCookieAndReturn(httpContext, result.Response!, jwtOptions.Value.RefreshTokenTtlDays)
            : RefreshTokenInvalidResponse(httpContext);
    }

    private static async Task<IResult> LogoutHandler(
        IAuthService authService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authService);
        ArgumentNullException.ThrowIfNull(httpContext);

        var rawToken = httpContext.Request.Cookies["refresh_token"];

        _ = await authService.LogoutAsync(rawToken, ct).ConfigureAwait(false);

        // Always clear the cookie, even on NoOp. Attributes mirror SetRefreshCookieAndReturn
        // so the browser overwrites the existing cookie rather than creating a parallel one.
        var secure = httpContext.Request.IsHttps;
        httpContext.Response.Cookies.Append("refresh_token", string.Empty, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = secure,
            Path = "/api/auth",
            Expires = DateTimeOffset.UnixEpoch,
            MaxAge = TimeSpan.Zero,
        });

        return Results.NoContent();
    }

    private static async Task<IResult> ForgotPasswordHandler(
        ForgotPasswordRequest request,
        IAuthService authService,
        IEmailService emailService,
        IOptions<SmtpOptions> smtpOptions,
        HttpContext httpContext,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authService);
        ArgumentNullException.ThrowIfNull(emailService);
        ArgumentNullException.ThrowIfNull(smtpOptions);
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var result = await authService.InitiatePasswordResetAsync(request.Email, ct).ConfigureAwait(false);

        if (result.Outcome == PasswordResetInitiateOutcome.Success && result.RawToken is not null)
        {
            // Prefer the configured BaseUrl to avoid Host-header phishing (the D1
            // review patch from Story 2.10); fall back to the request origin.
            var opts = smtpOptions.Value;
            var host = httpContext.Request.Host;
            var baseUrl = !string.IsNullOrEmpty(opts.BaseUrl)
                ? opts.BaseUrl
                : host.HasValue ? $"{httpContext.Request.Scheme}://{host}" : null;

            var correlationId = httpContext.GetCorrelationId() ?? "(none)";

            if (baseUrl is not null)
            {
                var resetUrl = $"{baseUrl}/reset-password?token={result.RawToken}";
                // True fire-and-forget (AR-53): CancellationToken.None so the send
                // survives request completion. The response is always the same, so
                // there is nothing to await — unlike the welcome-email path (2.10).
                _ = Task.Run(
                    () => emailService.TrySendPasswordResetEmailAsync(
                        request.Email, resetUrl, correlationId, CancellationToken.None),
                    CancellationToken.None);
            }
            else
            {
                // Token committed but reset URL cannot be determined — Smtp:BaseUrl is
                // unset and the Host header was absent. Log so ops can detect misconfiguration.
                var endpointLogger = loggerFactory.CreateLogger(nameof(AuthEndpoints));
                AuthEndpointsLog.ResetEmailSuppressedNoBaseUrl(endpointLogger, correlationId);
            }
        }

        // Always the same body — no enumeration leak (AC-1).
        return Results.Ok(new { message = "If that email is registered, a reset link has been sent." });
    }

    private static async Task<IResult> ResetPasswordHandler(
        ResetPasswordRequest request,
        IAuthService authService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authService);
        ArgumentNullException.ThrowIfNull(httpContext);

        var result = await authService.ResetPasswordAsync(request.Token, request.NewPassword, ct)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            PasswordResetOutcome.TokenInvalid => Results.Problem(
                detail: "Reset link is invalid or has expired.",
                title: "Reset token invalid",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "RESET_TOKEN_INVALID",
                    ["messageKey"] = "auth.resetTokenInvalid",
                    ["correlationId"] = httpContext.GetCorrelationId(),
                }),

            PasswordResetOutcome.PasswordSameAsCurrent => Results.ValidationProblem(
                errors: new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["newPassword"] = ["New password must differ from your current password."],
                },
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "auth.passwordSameAsCurrent",
                    ["correlationId"] = httpContext.GetCorrelationId(),
                }),

            PasswordResetOutcome.Success => Results.Ok(),

            _ => Results.Problem(
                detail: "Something went wrong. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.genericError",
                    ["correlationId"] = httpContext.GetCorrelationId(),
                }),
        };
    }

    private static IResult RefreshTokenInvalidResponse(HttpContext httpContext) =>
        Results.Problem(
            detail: "Your session has expired. Please sign in again.",
            title: "Refresh token invalid or expired",
            statusCode: StatusCodes.Status401Unauthorized,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "REFRESH_TOKEN_INVALID",
                ["messageKey"] = "auth.refreshTokenInvalid",
                ["correlationId"] = httpContext.GetCorrelationId(),
            });

    // Story 2.14 — both the SessionInvalid outcome and the user-deleted race after a
    // valid second factor collapse to the same 401: the session can no longer be
    // completed. Single builder so the contract (code/messageKey) stays in lockstep.
    private static IResult MfaSessionInvalidProblem(string? correlationId) =>
        Results.Problem(
            detail: "MFA session is invalid or has expired. Please sign in again.",
            title: "MFA session invalid",
            statusCode: StatusCodes.Status401Unauthorized,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "MFA_SESSION_INVALID",
                ["messageKey"] = "auth.mfaSessionInvalid",
                ["correlationId"] = correlationId,
            });

    private static IResult SetRefreshCookieAndReturn(HttpContext ctx, LoginResponse response, int refreshTokenTtlDays)
    {
        // Decide Secure on the actual request scheme alone — NOT the environment.
        // UseForwardedHeaders() has already rewritten Request.Scheme from the proxy's
        // X-Forwarded-Proto, so IsHttps reflects the real client connection. Gating on
        // !IsDevelopment() forced Secure=true in any non-Development env (e.g. "Compose"),
        // which is served over plain HTTP — the browser then drops the cookie and every
        // silent refresh 401s straight to /login. A real HTTPS deploy yields IsHttps=true
        // here (provided the proxy is trusted via Security:ForwardedHeaders:KnownProxies).
        var secure = ctx.Request.IsHttps;

        ctx.Response.Cookies.Append("refresh_token", response.RefreshToken, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = secure,
            Path = "/api/auth",
            // Cookie lifetime mirrors the refresh token's server-side ExpiresAt.
            Expires = DateTimeOffset.UtcNow.AddDays(refreshTokenTtlDays),
        });

        return Results.Ok(response);
    }
}

internal static partial class AuthEndpointsLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Password reset email suppressed — Smtp:BaseUrl is unset and Host header was absent. correlationId={CorrelationId}")]
    public static partial void ResetEmailSuppressedNoBaseUrl(ILogger logger, string correlationId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "MFA verified but user no longer exists — deleted between session creation and verify. UserId={UserId} correlationId={CorrelationId}")]
    public static partial void MfaCompleteUserMissing(ILogger logger, Guid userId, string correlationId);
}
