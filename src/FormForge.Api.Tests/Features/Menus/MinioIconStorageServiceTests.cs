using FormForge.Api.Features.Menus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FormForge.Api.Tests.Features.Menus;

// Presigned URLs are signed offline (pure HMAC — no MinIO server needed), so these run as
// plain unit tests. They guard the deploy-only fix: in a container the API uploads via the
// internal endpoint (minio:9000) but must SIGN presigned GET URLs with a browser-reachable
// host (MinIO:PublicEndpoint, e.g. http://localhost:9000), or images never render.
public sealed class MinioIconStorageServiceTests
{
    private static MinioIconStorageService Build(params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s =>
                new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        return new MinioIconStorageService(config, NullLogger<MinioIconStorageService>.Instance);
    }

    [Fact]
    public async Task GetPresignedUrl_SignsWithPublicEndpoint_WhenConfigured()
    {
        // Internal endpoint is the unreachable-from-browser container host; the public one is
        // what the browser uses. The presigned URL must carry the PUBLIC host.
        using var service = Build(
            ("MinIO:Endpoint", "http://minio:9000"),
            ("MinIO:PublicEndpoint", "http://localhost:9000"));

        var url = await service.GetPresignedUrlAsync("agents/picture_abc.png", CancellationToken.None);

        Assert.StartsWith("http://localhost:9000/formforge/agents/picture_abc.png", url, StringComparison.Ordinal);
        Assert.DoesNotContain("minio:9000", url, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Signature=", url, StringComparison.Ordinal); // genuinely presigned
    }

    [Fact]
    public async Task GetPresignedUrl_FallsBackToInternalEndpoint_WhenPublicUnset()
    {
        // No PublicEndpoint configured (dev/Aspire): the internal endpoint is already a
        // browser-reachable localhost URL, so presigning against it is correct.
        using var service = Build(("MinIO:Endpoint", "http://localhost:9000"));

        var url = await service.GetPresignedUrlAsync("agents/picture_abc.png", CancellationToken.None);

        Assert.StartsWith("http://localhost:9000/formforge/agents/picture_abc.png", url, StringComparison.Ordinal);
    }
}
