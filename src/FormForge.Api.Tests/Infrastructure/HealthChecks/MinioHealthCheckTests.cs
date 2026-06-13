using System.Net;
using FormForge.Api.Infrastructure.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FormForge.Api.Tests.Infrastructure.HealthChecks;

public sealed class MinioHealthCheckTests
{
    [Fact]
    public async Task CheckAsync_Returns_Healthy_On_200Response()
    {
        using var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, null);
        using var client = new HttpClient(handler);
        var check = BuildCheck(client);

        var result = await check.CheckHealthAsync(BuildContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckAsync_Returns_Healthy_On_403Response()
    {
        using var handler = new FakeHttpMessageHandler(HttpStatusCode.Forbidden, null);
        using var client = new HttpClient(handler);
        var check = BuildCheck(client);

        var result = await check.CheckHealthAsync(BuildContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckAsync_Returns_Unhealthy_On_404Response()
    {
        using var handler = new FakeHttpMessageHandler(HttpStatusCode.NotFound, null);
        using var client = new HttpClient(handler);
        var check = BuildCheck(client);

        var result = await check.CheckHealthAsync(BuildContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.NotNull(result.Description);
        Assert.True(
            result.Description!.Contains("bucket", StringComparison.OrdinalIgnoreCase)
            || result.Description!.Contains("not found", StringComparison.OrdinalIgnoreCase),
            $"Expected bucket/not found in description, got: {result.Description}");
    }

    [Fact]
    public async Task CheckAsync_Returns_Unhealthy_On_ConnectionRefused()
    {
        using var handler = new FakeHttpMessageHandler(null, new HttpRequestException("Connection refused"));
        using var client = new HttpClient(handler);
        var check = BuildCheck(client);

        var result = await check.CheckHealthAsync(BuildContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task CheckAsync_Uses_MinioEndpointFromConfiguration()
    {
        using var captureHandler = new CapturingHttpMessageHandler(HttpStatusCode.OK);
        using var client = new HttpClient(captureHandler);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection([
                new KeyValuePair<string, string?>("services__minio__s3__0", "http://aspire-minio:9123"),
            ])
            .Build();
        var check = new MinioHealthCheck(new FakeHttpClientFactory(client), config);

        await check.CheckHealthAsync(BuildContext());

        Assert.NotNull(captureHandler.LastRequestUri);
        Assert.StartsWith("http://aspire-minio:9123", captureHandler.LastRequestUri!.ToString(), StringComparison.Ordinal);
    }

    private static MinioHealthCheck BuildCheck(HttpClient client)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection([
                new KeyValuePair<string, string?>("MinIO__Endpoint", "http://minio-test:9000"),
            ])
            .Build();
        return new MinioHealthCheck(new FakeHttpClientFactory(client), config);
    }

    private static HealthCheckContext BuildContext() =>
        new() { Registration = new HealthCheckRegistration("minio", _ => null!, null, null) };
}

file sealed class FakeHttpMessageHandler(HttpStatusCode? statusCode, Exception? throws) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (throws is not null)
            throw throws;
        return Task.FromResult(new HttpResponseMessage(statusCode!.Value));
    }
}

file sealed class CapturingHttpMessageHandler(HttpStatusCode statusCode) : HttpMessageHandler
{
    public Uri? LastRequestUri { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri;
        return Task.FromResult(new HttpResponseMessage(statusCode));
    }
}

file sealed class FakeHttpClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}
