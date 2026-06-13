using System.Net;
using System.Text.Json;
using FormForge.Api.Common.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FormForge.Api.Tests.Common.Logging;

// Builds a minimal in-process host that mirrors the production wiring in Program.cs for the slice
// under test — CorrelationIdMiddleware + UseExceptionHandler + AddProblemDetails(customize) — plus
// a deliberately-throwing endpoint. The production WebApplicationFactory<Program> cannot host a
// test-only endpoint without modifying Program.cs (spec forbids that), so this test owns its host.
public sealed class ProblemDetailsCorrelationIdTests
{
    [Fact]
    public async Task ThrownException_ResponseBodyCarriesCorrelationIdInExtensions()
    {
        using var host = await BuildTestHostAsync();
        var server = host.GetTestServer();
        using var client = server.CreateClient();

        const string ClientUlid = "01HX9JK8YZ4N6T2X3V5W7P9Q0R";
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__test/throw");
        request.Headers.Add("X-Correlation-ID", ClientUlid);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var headerValues));
        var headerCorrelationId = Assert.Single(headerValues);
        Assert.Equal(ClientUlid, headerCorrelationId);

        var body = await response.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(body), "Expected a non-empty ProblemDetails body for the thrown-exception path.");

        using var doc = JsonDocument.Parse(body);
        Assert.True(
            doc.RootElement.TryGetProperty("correlationId", out var corrFromExt),
            $"Expected ProblemDetails.Extensions.correlationId in body, got: {body}");
        Assert.Equal(ClientUlid, corrFromExt.GetString());
    }

    [Fact]
    public async Task ThrownException_CorrelationIdMatchesGeneratedHeaderWhenClientDoesNotProvideOne()
    {
        using var host = await BuildTestHostAsync();
        var server = host.GetTestServer();
        using var client = server.CreateClient();

        using var response = await client.GetAsync(new Uri("/__test/throw", UriKind.Relative));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var headerValues));
        var headerCorrelationId = Assert.Single(headerValues);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("correlationId", out var corrFromExt));
        Assert.Equal(headerCorrelationId, corrFromExt.GetString());
    }

    private static async Task<IHost> BuildTestHostAsync()
    {
        var hostBuilder = Host.CreateDefaultBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddProblemDetails(options =>
                    {
                        options.CustomizeProblemDetails = ctx =>
                        {
                            var corr = ctx.HttpContext.GetCorrelationId();
                            if (!string.IsNullOrEmpty(corr))
                            {
                                ctx.ProblemDetails.Extensions["correlationId"] = corr;
                            }
                        };
                    });
                });
                web.Configure(app =>
                {
                    app.UseMiddleware<CorrelationIdMiddleware>();
                    app.UseExceptionHandler();
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/__test/throw", ThrowingHandler);
                    });
                });
            });

        var host = hostBuilder.Build();
        await host.StartAsync();
        return host;
    }

    private static IResult ThrowingHandler() =>
        throw new InvalidOperationException("intentional test exception");
}
