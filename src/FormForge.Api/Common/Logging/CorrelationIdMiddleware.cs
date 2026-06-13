using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace FormForge.Api.Common.Logging;

// Reads or generates a per-request correlation ID (26-char ULID) and propagates it through:
//   - HttpContext.Items["CorrelationId"]    -> read source of truth
//   - HttpContext.TraceIdentifier           -> aligns with OTel Activity.TraceId surface
//   - ILogger.BeginScope                    -> every log entry inside the request carries correlationId + endpoint
//   - Response header X-Correlation-ID      -> set via OnStarting to survive UseExceptionHandler.Response.Clear()
//
// Client-supplied values that are NOT valid ULIDs are rejected and a fresh ULID is generated instead
// (prevents log injection / arbitrary header echo).
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
    Justification = "Instantiated at runtime by app.UseMiddleware<CorrelationIdMiddleware>() — the analyzer cannot see the factory path.")]
internal sealed class CorrelationIdMiddleware
{
    internal const string HeaderName = "X-Correlation-ID";
    internal const string ContextItemsKey = "CorrelationId";

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);
        _next = next;
        _logger = logger;
    }

    // Cap the endpoint scope value so a pathological URL cannot bloat every log line for the request,
    // and the Method (which is ASCII per HTTP spec) plus the truncated path stays terminal-safe.
    internal const int MaxEndpointScopeLength = 512;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var id = ResolveCorrelationId(context);

        context.Items[ContextItemsKey] = id;
        context.TraceIdentifier = id;

        context.Response.OnStarting(static state =>
        {
            var (response, value) = ((HttpResponse, string))state;
            response.Headers[HeaderName] = value;
            return Task.CompletedTask;
        }, (context.Response, id));

        var scopeState = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["correlationId"] = id,
            ["endpoint"] = BuildEndpointScopeValue(context.Request.Method, context.Request.Path),
        };

        using (_logger.BeginScope(scopeState))
        {
            await _next(context).ConfigureAwait(false);
        }
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var values) && values.Count > 0)
        {
            // Use the first value rather than StringValues.ToString() (which joins with ",") so a
            // repeated header from a proxy does not silently fail Ulid.TryParse and reset the ID.
            var candidate = values[0];
            if (!string.IsNullOrEmpty(candidate) && Ulid.TryParse(candidate, out _))
            {
                // Echo the client's original value verbatim. Ulid.TryParse is case-insensitive but
                // parsed.ToString() canonicalizes to uppercase, which would diverge from clients that
                // do byte-equality on their own outbound header (AR-24 / AC-1 "same correlation ID").
                return candidate;
            }
        }

        return Ulid.NewUlid().ToString();
    }

    internal static string BuildEndpointScopeValue(string method, PathString path)
    {
        var rawPath = path.HasValue ? path.Value! : string.Empty;
        var combined = string.Create(
            CultureInfo.InvariantCulture,
            $"{method} {rawPath}");

        if (combined.Length > MaxEndpointScopeLength)
        {
            combined = combined[..MaxEndpointScopeLength];
        }

        // Replace CR / LF with the Unicode replacement char so a percent-decoded control sequence in
        // the path cannot forge a new log line or smuggle terminal escape sequences into log viewers.
        return combined.Replace('\r', '�').Replace('\n', '�');
    }
}
