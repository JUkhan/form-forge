using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ServiceDiscovery;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

// Adds common Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/aspire/service-defaults
public static class ServiceDefaultsExtensions
{
    private const string HealthEndpointPath = "/health";
    private const string LivenessEndpointPath = "/health/live";
    private const string ReadinessEndpointPath = "/health/ready";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Exclude all /health/* requests from tracing
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath, StringComparison.Ordinal)
                    )
                    // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                    //.AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var raw = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (IsValidOtlpEndpoint(raw))
            {
                builder.Services.AddOpenTelemetry().UseOtlpExporter();
            }
            else
            {
                using var lf = LoggerFactory.Create(b => b.AddConsole());
                lf.CreateLogger("ServiceDefaults").LogOtlpEndpointInvalid();
            }
        }

        // Uncomment the following lines to enable the Azure Monitor exporter (requires the Azure.Monitor.OpenTelemetry.AspNetCore package)
        //if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        //{
        //    builder.Services.AddOpenTelemetry()
        //       .UseAzureMonitor();
        //}

        return builder;
    }

    internal static bool IsValidOtlpEndpoint(string value)
    {
        // Reject URIs carrying userinfo (e.g. http://user:pass@otel:4317). Credentials belong in
        // OTEL_EXPORTER_OTLP_HEADERS, not in the endpoint URL; accepting them here would propagate
        // secrets into UseOtlpExporter() and any log line that echoes the URI.
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrEmpty(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo);
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // /health/live and /health/ready are exposed in all environments (not just Development).
        // AR-25: both endpoints are anonymous; /health (detailed) is mapped in FormForge.Api/Program.cs
        // and receives platform-admin auth in Story 2.6.

        // Liveness: only the "self" tag check (always Healthy if process is up).
        app.MapHealthChecks(LivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live"),
            ResponseWriter = (ctx, report) =>
            {
                ctx.Response.ContentType = "application/json";
                return ctx.Response.WriteAsync("{\"status\":\"healthy\"}", ctx.RequestAborted);
            },
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status200OK,
            },
        });

        // Readiness: only "ready" tagged checks (postgres + minio).
        app.MapHealthChecks(ReadinessEndpointPath, new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("ready"),
            ResponseWriter = WriteHealthReportAsJson,
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
        });

        return app;
    }

    private static Task WriteHealthReportAsJson(HttpContext context, HealthReport report)
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
        return context.Response.WriteAsJsonAsync(new { status, checks }, cancellationToken: context.RequestAborted);
    }
}

internal static partial class ServiceDefaultsLog
{
    // The raw env-var value is deliberately NOT included in the message template so a
    // userinfo-bearing URI (http://user:secret@host) cannot leak credentials into stdout when
    // the validator rejects it.
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "OTEL_EXPORTER_OTLP_ENDPOINT is set but does not parse as a valid absolute http/https URI (no userinfo); OTLP exporter disabled.")]
    public static partial void LogOtlpEndpointInvalid(this ILogger logger);
}
