using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FormForge.Api.Infrastructure.HealthChecks;

internal static class HealthCheckJsonWriter
{
    public static Task WriteResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var status = report.Status.ToString().ToLowerInvariant();

        var checks = report.Entries.ToDictionary(
            e => e.Key,
            e =>
            {
                var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["status"] = e.Value.Status.ToString().ToLowerInvariant(),
                    ["description"] = e.Value.Description,
                };
                if (e.Value.Exception is not null)
                {
                    entry["error"] = e.Value.Exception.Message;
                }
                return (object)entry;
            },
            StringComparer.Ordinal);

        return context.Response.WriteAsJsonAsync(
            new { status, checks },
            cancellationToken: context.RequestAborted);
    }
}
