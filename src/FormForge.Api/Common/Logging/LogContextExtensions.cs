using System.Security.Claims;

namespace FormForge.Api.Common.Logging;

internal static class LogContextExtensions
{
    // Returns the correlation ID set by CorrelationIdMiddleware, or null if invoked outside the HTTP pipeline
    // or before the middleware has run.
    public static string? GetCorrelationId(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(CorrelationIdMiddleware.ContextItemsKey, out var value)
            ? value as string
            : null;
    }

    // Pushes userId and roles onto an ILogger scope dictionary. Returns null when the principal is unauthenticated.
    // Defined here for Story 2.6 (auth filter chain) to invoke; not called by any code path in Story 1.5.
    public static IDisposable? BeginUserScope(this ILogger logger, ClaimsPrincipal? user)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("sub")?.Value
            ?? "(unknown)";

        var roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();

        return logger.BeginScope(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["userId"] = userId,
            ["roles"] = roles,
        });
    }
}
