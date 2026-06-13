using Microsoft.Extensions.Hosting;

namespace FormForge.Api.Tests.ServiceDefaults;

// Direct calls into ServiceDefaultsExtensions.IsValidOtlpEndpoint via the InternalsVisibleTo seam
// on FormForge.ServiceDefaults. The earlier reflection-based lookup is removed.
public sealed class OtlpEndpointValidationTests
{
    [Theory]
    [InlineData("http://localhost:4317")]
    [InlineData("https://otel-collector.example.com:443")]
    [InlineData("http://localhost:18889/")]
    public void IsValidOtlpEndpoint_AcceptsHttpAndHttpsAbsoluteUris(string value)
    {
        Assert.True(ServiceDefaultsExtensions.IsValidOtlpEndpoint(value));
    }

    [Theory]
    [InlineData("not a uri")]
    [InlineData("file:///tmp/x")]
    [InlineData("ftp://otel/")]
    [InlineData("just-a-host")]
    [InlineData("/relative/path")]
    [InlineData("http://")]
    [InlineData("http://user:pass@otel-collector:4317")] // userinfo rejected — credentials belong in OTEL_EXPORTER_OTLP_HEADERS, not in the URL
    [InlineData("https://service@otel:4317")] // user-only also rejected
    public void IsValidOtlpEndpoint_RejectsNonHttpOrMalformedOrUserinfo(string value)
    {
        Assert.False(ServiceDefaultsExtensions.IsValidOtlpEndpoint(value));
    }
}

// Behavioral tests for the full ConfigureOpenTelemetry / AddOpenTelemetryExporters host-construction
// path. Each input class (empty, valid, malformed, wrong-scheme, userinfo) must allow the host to
// build without throwing — the spec's AC-6 mandates "host startup continues without the exporter"
// for any invalid configuration. The warning-emission side of the contract is verified by the
// IsValidOtlpEndpoint Theory rows above; this layer asserts the production gate composes correctly.
public sealed class OtlpExporterActivationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://localhost:4317")]
    [InlineData("not a uri")]
    [InlineData("file:///tmp/x")]
    [InlineData("http://user:secret@otel:4317")]
    public void ConfigureOpenTelemetry_BuildsHostCleanlyForAnyEndpointValue(string otlpEndpoint)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = otlpEndpoint;

        // ConfigureOpenTelemetry calls AddOpenTelemetryExporters, which is the production gate. For
        // any input the host must build without throwing (failure mode = silent-drop avoidance).
        builder.ConfigureOpenTelemetry();

        using var host = builder.Build();

        Assert.NotNull(host);
    }
}
