using System.Diagnostics.CodeAnalysis;
using System.Text;
using FormForge.Api.Common.Security;

namespace FormForge.Api.Common.Spa;

internal static class IndexHtmlRewriter
{
    private const string NoncePlaceholder = "__CSP_NONCE__";
    private static string? _cachedIndexHtml;
    // Sentinel that flips to true once we've confirmed the file is absent, so subsequent
    // dev-mode fallback requests short-circuit without re-acquiring the lock or hitting disk.
    private static bool _cachedMissing;
    private static readonly Lock _initLock = new();

    public static async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var html = LoadOrInit(context.RequestServices);
        if (html is null)
        {
            // wwwroot/index.html does not exist (dev with Vite serving directly).
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var nonce = CspNonceMiddleware.GetNonce(context);
        var rewritten = html.Replace(NoncePlaceholder, nonce, StringComparison.Ordinal);

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(rewritten, Encoding.UTF8).ConfigureAwait(false);
    }

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code",
        Justification = "Double-checked locking pattern: the second null check inside the lock guards against a race between two threads both passing the first check.")]
    private static string? LoadOrInit(IServiceProvider services)
    {
        if (_cachedIndexHtml is not null) return _cachedIndexHtml;
        if (_cachedMissing) return null;

        lock (_initLock)
        {
            if (_cachedIndexHtml is not null) return _cachedIndexHtml;
            if (_cachedMissing) return null;

            var env = services.GetRequiredService<IWebHostEnvironment>();
            // `??` skips only null; an empty WebRootPath would resolve relative to CWD.
            var webRoot = string.IsNullOrEmpty(env.WebRootPath) ? "wwwroot" : env.WebRootPath;
            var indexPath = Path.Combine(webRoot, "index.html");

            if (!File.Exists(indexPath))
            {
                _cachedMissing = true;
                return null;
            }

            try
            {
                _cachedIndexHtml = File.ReadAllText(indexPath, Encoding.UTF8);
            }
            catch (IOException)
            {
                // TOCTOU: file disappeared (or is locked) between Exists and Read. Treat as missing.
                _cachedMissing = true;
                return null;
            }
            return _cachedIndexHtml;
        }
    }

    // Test-only seam: lets integration tests inject a fixture without restarting the host.
    internal static void ResetCacheForTests()
    {
        _cachedIndexHtml = null;
        _cachedMissing = false;
    }
}
