using System.Net;
using System.Text.Json;
using FormForge.Api.Tests.Common.Logging;

namespace FormForge.Api.Tests.Infrastructure.HealthChecks;

public sealed class HealthCheckEndpointsTests(FormForgeApiFactory factory) : IClassFixture<FormForgeApiFactory>
{
    [Fact]
    public async Task LiveEndpoint_AlwaysReturns200()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\"", body, StringComparison.Ordinal);
        Assert.Contains("\"healthy\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadyEndpoint_Returns503_WhenDependenciesUnavailable()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/ready");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            body.Contains("\"unhealthy\"", StringComparison.Ordinal)
            || body.Contains("\"degraded\"", StringComparison.Ordinal),
            $"Expected unhealthy or degraded in body, got: {body}");
    }

    [Fact]
    public async Task ReadyEndpoint_ResponseBodyIsValidJson()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/ready");

        using var response = await client.SendAsync(request);

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("status", out _), "Expected 'status' key in JSON");
    }

    [Fact]
    public async Task HealthEndpoint_Unauthenticated_Returns401()
    {
        // Story 2.6 / AR-25: /health requires platform-admin. Anonymous → 401.
        // The platform-admin-passes path is covered in PermissionsIntegrationTests
        // (which has both a real Postgres testcontainer and a stripped MinIO check).
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HealthEndpoint_ResponseContainsCorrelationId_WhenResponseHeaderSet()
    {
        // CorrelationIdMiddleware runs before auth, so the header is set even on 401.
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");

        using var response = await client.SendAsync(request);

        Assert.True(
            response.Headers.Contains("X-Correlation-ID"),
            "Expected X-Correlation-ID response header");
    }
}
