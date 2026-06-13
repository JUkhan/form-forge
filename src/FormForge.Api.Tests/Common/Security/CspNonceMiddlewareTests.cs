using FormForge.Api.Common.Security;
using Microsoft.AspNetCore.Http;

namespace FormForge.Api.Tests.Common.Security;

public sealed class CspNonceMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_StoresBase64NonceOnHttpContextItems()
    {
        var middleware = new CspNonceMiddleware(_ => Task.CompletedTask);
        var ctx = new DefaultHttpContext();

        await middleware.InvokeAsync(ctx);

        var nonce = ctx.Items[CspNonceMiddleware.ContextItemsKey] as string;
        Assert.NotNull(nonce);
        // 16 bytes base64url → 22 chars (no '=' padding); chars are URL-safe.
        Assert.Equal(22, nonce.Length);
        Assert.DoesNotContain('+', nonce);
        Assert.DoesNotContain('/', nonce);
        Assert.DoesNotContain('=', nonce);
        // Round-trip back through standard base64 to verify the underlying 16 bytes are recoverable.
        var standardBase64 = nonce.Replace('-', '+').Replace('_', '/');
        var padded = standardBase64.PadRight(standardBase64.Length + (4 - standardBase64.Length % 4) % 4, '=');
        Assert.True(Convert.TryFromBase64String(padded, new byte[16], out var decoded));
        Assert.Equal(16, decoded);
    }

    [Fact]
    public async Task InvokeAsync_TwoRequests_ProduceDistinctNonces()
    {
        var middleware = new CspNonceMiddleware(_ => Task.CompletedTask);
        var ctx1 = new DefaultHttpContext();
        var ctx2 = new DefaultHttpContext();

        await middleware.InvokeAsync(ctx1);
        await middleware.InvokeAsync(ctx2);

        Assert.NotEqual(
            ctx1.Items[CspNonceMiddleware.ContextItemsKey],
            ctx2.Items[CspNonceMiddleware.ContextItemsKey]);
    }

    [Fact]
    public async Task GetNonce_AfterInvoke_ReturnsStoredNonce()
    {
        var middleware = new CspNonceMiddleware(_ => Task.CompletedTask);
        var ctx = new DefaultHttpContext();

        await middleware.InvokeAsync(ctx);

        var nonce = CspNonceMiddleware.GetNonce(ctx);
        Assert.Equal(ctx.Items[CspNonceMiddleware.ContextItemsKey] as string, nonce);
    }

    [Fact]
    public void GetNonce_WhenNotSet_ThrowsInvalidOperationException()
    {
        var ctx = new DefaultHttpContext();
        Assert.Throws<InvalidOperationException>(() => CspNonceMiddleware.GetNonce(ctx));
    }
}
