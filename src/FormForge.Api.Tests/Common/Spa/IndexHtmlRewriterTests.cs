using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using FormForge.Api.Common.Spa;
using FormForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FormForge.Api.Tests.Common.Spa;

[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class IndexHtmlRewriterTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private string? _tempRoot;

    public IndexHtmlRewriterTests(PostgresFixture postgres) => _postgres = postgres;

    public Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempRoot);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (_tempRoot is not null && Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetRoot_BodyAndHeaderShareSameNonce()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_tempRoot!, "index.html"),
            "<!doctype html><html><head><script nonce=\"__CSP_NONCE__\"></script></head><body></body></html>");

        IndexHtmlRewriter.ResetCacheForTests();

        // Production env so the per-request CSP middleware emits the Content-Security-Policy
        // header; Development gates it off to keep Vite HMR working. AC-4's "body nonce ==
        // header nonce" invariant only exists in non-dev modes.
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Production");
                b.UseSetting("ConnectionStrings:formforge", _postgres.ConnectionString);
                b.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                b.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
                b.UseWebRoot(_tempRoot!);
            });

        using var client = factory.CreateClient();
        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // Placeholder must be replaced
        Assert.DoesNotContain("__CSP_NONCE__", body, StringComparison.Ordinal);

        // Extract the nonce from the body — base64url chars only.
        var match = Regex.Match(body, @"nonce=""([A-Za-z0-9_\-]+)""");
        Assert.True(match.Success, "Expected nonce attribute in rewritten HTML body");
        var bodyNonce = match.Groups[1].Value;
        Assert.Equal(22, bodyNonce.Length); // 16 bytes → 22 base64url chars

        // The Content-Security-Policy header must contain that exact nonce.
        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out var cspValues),
            "Production-mode response must include Content-Security-Policy header");
        var csp = string.Join(" ", cspValues);
        Assert.Contains($"'nonce-{bodyNonce}'", csp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRoot_WhenIndexHtmlMissing_Returns404()
    {
        // No index.html written to temp dir
        IndexHtmlRewriter.ResetCacheForTests();

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("ConnectionStrings:formforge", _postgres.ConnectionString);
                b.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                b.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
                b.UseWebRoot(_tempRoot!);
            });

        using var client = factory.CreateClient();
        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetRoot_CacheControlHeader_IsNoStore()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_tempRoot!, "index.html"),
            "<html><head><script nonce=\"__CSP_NONCE__\"></script></head><body></body></html>");

        IndexHtmlRewriter.ResetCacheForTests();

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("ConnectionStrings:formforge", _postgres.ConnectionString);
                b.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                b.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
                b.UseWebRoot(_tempRoot!);
            });

        using var client = factory.CreateClient();
        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.True(
            response.Headers.CacheControl?.NoStore == true,
            "Cache-Control: no-store must be set on every index.html response");
    }
}
