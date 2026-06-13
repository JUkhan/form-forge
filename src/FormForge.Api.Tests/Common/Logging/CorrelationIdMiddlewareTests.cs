using Microsoft.AspNetCore.Mvc.Testing;

namespace FormForge.Api.Tests.Common.Logging;

public sealed class CorrelationIdMiddlewareTests : IClassFixture<FormForgeApiFactory>
{
    private readonly FormForgeApiFactory _factory;

    public CorrelationIdMiddlewareTests(FormForgeApiFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("01HX9JK8YZ4N6T2X3V5W7P9Q0R")]
    [InlineData("01hx9jk8yz4n6t2x3v5w7p9q0r")]
    public async Task HappyPath_ClientSuppliesValidUlid_ServerEchoesVerbatim(string clientUlid)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Correlation-ID", clientUlid);

        using var response = await client.SendAsync(request);

        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var values));
        var echoed = Assert.Single(values);
        Assert.Equal(clientUlid, echoed);
    }

    [Fact]
    public async Task NoHeader_ServerGeneratesUlid()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");

        using var response = await client.SendAsync(request);

        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var values));
        var generated = Assert.Single(values);
        Assert.Equal(26, generated.Length);
        Assert.True(Ulid.TryParse(generated, out _), $"Expected a parseable ULID, got: {generated}");
    }

    [Fact]
    public async Task MalformedHeader_IsIgnoredAndUlidIsGenerated()
    {
        const string Malformed = "not-a-ulid-value";

        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Correlation-ID", Malformed);

        using var response = await client.SendAsync(request);

        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var values));
        var echoed = Assert.Single(values);
        Assert.NotEqual(Malformed, echoed);
        Assert.True(Ulid.TryParse(echoed, out _), $"Expected a fresh parseable ULID, got: {echoed}");
    }
}

public sealed class FormForgeApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Point the connection string at an unreachable host so Database.Migrate() fails fast inside the
        // existing try/catch in Program.cs. The fixture only exercises middleware; DB interaction is not needed.
        builder.UseSetting(
            "ConnectionStrings:formforge",
            "Host=ignored.invalid;Port=1;Database=ignored;Username=u;Password=p;Timeout=1;CommandTimeout=1");
    }
}
