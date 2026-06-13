using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FormForge.Api.Infrastructure.HealthChecks;

internal sealed class MinioHealthCheck(IHttpClientFactory httpClientFactory, IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // IConfiguration indexing uses colon-delimited keys; the `MinIO__Endpoint`
        // env var is already normalized to `MinIO:Endpoint`. Reading the literal
        // double-underscore key always missed and silently fell back to localhost.
        var endpoint = configuration["services__minio__s3__0"]
            ?? configuration["MinIO:Endpoint"]
            ?? "http://localhost:9000";

        var url = $"{endpoint.TrimEnd('/')}/formforge";

        try
        {
            using var client = httpClientFactory.CreateClient("minio-health");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);

            return response.StatusCode switch
            {
                System.Net.HttpStatusCode.OK => HealthCheckResult.Healthy("MinIO bucket 'formforge' reachable"),
                System.Net.HttpStatusCode.Forbidden => HealthCheckResult.Healthy("MinIO bucket 'formforge' reachable"),
                System.Net.HttpStatusCode.NotFound => HealthCheckResult.Unhealthy("MinIO bucket 'formforge' not found (404). Bucket provisioning may be required."),
                _ => HealthCheckResult.Unhealthy($"MinIO returned unexpected status {(int)response.StatusCode}."),
            };
        }
        catch (HttpRequestException ex)
        {
            return HealthCheckResult.Unhealthy("MinIO is unreachable.", ex);
        }
        catch (OperationCanceledException ex)
        {
            return HealthCheckResult.Unhealthy("MinIO is unreachable.", ex);
        }
    }
}
