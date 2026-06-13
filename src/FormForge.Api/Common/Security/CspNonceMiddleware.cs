using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace FormForge.Api.Common.Security;

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
    Justification = "Instantiated at runtime by app.UseMiddleware<CspNonceMiddleware>().")]
internal sealed class CspNonceMiddleware
{
    internal const string ContextItemsKey = "CspNonce";

    private readonly RequestDelegate _next;

    public CspNonceMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 16 bytes (128 bits) — exceeds the CSP spec's 128-bit minimum recommendation.
        // Base64Url (RFC 4648 §5): '-' and '_' replace '+' and '/'; no '=' padding.
        // Safer than standard base64 because some older CDNs/WAFs misparse '+' and '/'
        // inside the CSP header value or the HTML nonce="" attribute.
        var bytes = RandomNumberGenerator.GetBytes(16);
        var nonce = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        context.Items[ContextItemsKey] = nonce;

        await _next(context).ConfigureAwait(false);
    }

    internal static string GetNonce(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items[ContextItemsKey] as string
            ?? throw new InvalidOperationException(
                "CSP nonce not found on HttpContext. Ensure CspNonceMiddleware is registered before any middleware that reads the nonce.");
    }
}
