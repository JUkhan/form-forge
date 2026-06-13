using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FormForge.Api.Infrastructure.HealthChecks;

internal sealed partial class LoggingHealthCheckPublisher(ILogger<LoggingHealthCheckPublisher> logger) : IHealthCheckPublisher
{
    public Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        var level = report.Status == HealthStatus.Healthy
            ? Microsoft.Extensions.Logging.LogLevel.Information
            : Microsoft.Extensions.Logging.LogLevel.Warning;

        if (logger.IsEnabled(level))
        {
            var overallStatus = report.Status.ToString();
            var checkSummary = string.Join(",", report.Entries.Select(
                e => $"{e.Key}={e.Value.Status}"));
            LogHealthReport(logger, level, overallStatus, checkSummary);
        }

        return Task.CompletedTask;
    }

    [Microsoft.Extensions.Logging.LoggerMessage(EventId = 20,
        Message = "Health check report: {OverallStatus}; checks: {CheckSummary}")]
    public static partial void LogHealthReport(
        ILogger logger,
        Microsoft.Extensions.Logging.LogLevel level,
        string overallStatus,
        string checkSummary);
}
