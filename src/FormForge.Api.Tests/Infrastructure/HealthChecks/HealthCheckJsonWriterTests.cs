using System.Text.Json;
using FormForge.Api.Infrastructure.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FormForge.Api.Tests.Infrastructure.HealthChecks;

public sealed class HealthCheckJsonWriterTests
{
    [Fact]
    public async Task WriteResponse_Healthy_Returns_LowercaseHealthyStatus()
    {
        var report = BuildReport([("postgres", HealthStatus.Healthy), ("minio", HealthStatus.Healthy)]);
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new System.IO.MemoryStream();

        await HealthCheckJsonWriter.WriteResponse(ctx, report);

        var json = await ReadBodyAsync(ctx);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("healthy", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task WriteResponse_Unhealthy_Returns_LowercaseUnhealthyStatus()
    {
        var report = BuildReport([
            ("postgres", HealthStatus.Unhealthy),
            ("minio", HealthStatus.Healthy),
        ]);
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new System.IO.MemoryStream();

        await HealthCheckJsonWriter.WriteResponse(ctx, report);

        var json = await ReadBodyAsync(ctx);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("unhealthy", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task WriteResponse_Checks_ContainsExpectedKeys()
    {
        var report = BuildReport([("postgres", HealthStatus.Healthy), ("minio", HealthStatus.Healthy)]);
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new System.IO.MemoryStream();

        await HealthCheckJsonWriter.WriteResponse(ctx, report);

        var json = await ReadBodyAsync(ctx);
        using var doc = JsonDocument.Parse(json);
        var checks = doc.RootElement.GetProperty("checks");
        Assert.True(checks.TryGetProperty("postgres", out _));
        Assert.True(checks.TryGetProperty("minio", out _));
    }

    [Fact]
    public async Task WriteResponse_Exception_IncludesErrorField()
    {
        var ex = new InvalidOperationException("Connection refused");
        var entries = new Dictionary<string, HealthReportEntry>(StringComparer.Ordinal)
        {
            ["postgres"] = new HealthReportEntry(HealthStatus.Unhealthy, null, TimeSpan.Zero, ex, null),
        };
        var report = new HealthReport(entries, HealthStatus.Unhealthy, TimeSpan.Zero);
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new System.IO.MemoryStream();

        await HealthCheckJsonWriter.WriteResponse(ctx, report);

        var json = await ReadBodyAsync(ctx);
        using var doc = JsonDocument.Parse(json);
        var entry = doc.RootElement.GetProperty("checks").GetProperty("postgres");
        Assert.True(entry.TryGetProperty("error", out var errorProp));
        Assert.Equal("Connection refused", errorProp.GetString());
    }

    private static HealthReport BuildReport(IEnumerable<(string name, HealthStatus status)> checks)
    {
        var entries = checks.ToDictionary(
            c => c.name,
            c => new HealthReportEntry(c.status, null, TimeSpan.Zero, null, null),
            StringComparer.Ordinal);
        var overall = entries.Values.Any(e => e.Status == HealthStatus.Unhealthy)
            ? HealthStatus.Unhealthy
            : entries.Values.Any(e => e.Status == HealthStatus.Degraded)
                ? HealthStatus.Degraded
                : HealthStatus.Healthy;
        return new HealthReport(entries, overall, TimeSpan.Zero);
    }

    private static async Task<string> ReadBodyAsync(HttpContext ctx)
    {
        ctx.Response.Body.Seek(0, System.IO.SeekOrigin.Begin);
        using var reader = new System.IO.StreamReader(ctx.Response.Body);
        return await reader.ReadToEndAsync();
    }
}
