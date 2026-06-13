using Minio;
using Minio.DataModel.Args;

namespace FormForge.Api.Features.Menus;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class MinioIconStorageService(IConfiguration configuration, ILogger<MinioIconStorageService> logger)
    : IIconStorageService, IDisposable
{
    private const string BucketName = "formforge";
    private readonly SemaphoreSlim _initLock = new(1, 1);
    // Lazy<T> with default ExecutionAndPublication mode guarantees at most one IMinioClient
    // is ever constructed even under concurrent first-upload load. P1: replaces the unguarded
    // null-check+assign in GetClient() that could leak a client on concurrent first calls.
    private readonly Lazy<IMinioClient> _lazyClient = new(() => BuildClient(configuration, presign: false));
    // Separate client for SIGNING presigned URLs. Presigned URLs are handed to the browser,
    // so they must be signed with a host the browser can reach (MinIO:PublicEndpoint, e.g.
    // http://localhost:9000) — NOT the internal container host (minio:9000) the API uploads
    // through. PresignedGetObjectAsync is pure offline HMAC (no network call), so this client
    // never needs to actually connect to the public endpoint from inside the container.
    private readonly Lazy<IMinioClient> _lazyPresignClient = new(() => BuildClient(configuration, presign: true));
    private bool _bucketEnsured;
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        // P5: set _disposed first so a second Dispose() call exits before touching _initLock.
        _disposed = true;
        _initLock.Dispose();
        if (_lazyClient.IsValueCreated)
            _lazyClient.Value.Dispose();
        if (_lazyPresignClient.IsValueCreated)
            _lazyPresignClient.Value.Dispose();
    }

    private IMinioClient GetClient() => _lazyClient.Value;

    private IMinioClient GetPresignClient() => _lazyPresignClient.Value;

    // CA2000: MinioClient implements IDisposable. The fluent builder returns the same
    // instance; .Build() yields IMinioClient over that instance. We hold it in _lazyClient
    // and dispose in Dispose(). The analyzer can't see through the chain.
#pragma warning disable CA2000
    private static IMinioClient BuildClient(IConfiguration configuration, bool presign)
    {
        // Internal endpoint the API uses to upload/download (reaches MinIO over the container
        // network). Aspire injects services__minio__s3__0 as a full URL; Compose uses
        // MinIO:Endpoint as host:port.
        var internalRaw = configuration["services__minio__s3__0"]
            ?? configuration["MinIO:Endpoint"]
            ?? "http://localhost:9000";

        // For presigning, prefer MinIO:PublicEndpoint — the host the BROWSER will use to GET
        // the object (e.g. http://localhost:9000). The SigV4 signature is computed over the
        // host, so the URL must be SIGNED with the public host; rewriting it afterwards would
        // break the signature. Falls back to the internal endpoint when unset — correct for
        // dev/Aspire, where the internal endpoint is already a browser-reachable localhost URL.
        var endpointRaw = presign
            ? (configuration["MinIO:PublicEndpoint"] ?? internalRaw)
            : internalRaw;

        var uri = endpointRaw.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? new Uri(endpointRaw)
            : new Uri("http://" + endpointRaw);

        var endpoint = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        var useSsl = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);

        var accessKey = configuration["MinIO:AccessKey"]
            ?? configuration["MinIO:RootUser"]
            ?? "minioadmin";
        var secretKey = configuration["MinIO:SecretKey"]
            ?? configuration["MinIO:RootPassword"]
            ?? "minioadmin";

        return new MinioClient()
            .WithEndpoint(endpoint)
            .WithCredentials(accessKey, secretKey)
            .WithSSL(useSsl)
            .Build();
    }
#pragma warning restore CA2000

    private async Task EnsureBucketAsync(IMinioClient client, CancellationToken ct)
    {
        if (_bucketEnsured) return;
        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_bucketEnsured) return;
            var exists = await client.BucketExistsAsync(
                new BucketExistsArgs().WithBucket(BucketName), ct).ConfigureAwait(false);
            if (!exists)
            {
                MinioStorageLog.CreatingBucket(logger, BucketName);
                await client.MakeBucketAsync(
                    new MakeBucketArgs().WithBucket(BucketName), ct).ConfigureAwait(false);
            }
            _bucketEnsured = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<string> StoreAsync(Stream content, string extension, long length, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(extension);

        var client = GetClient();
        await EnsureBucketAsync(client, ct).ConfigureAwait(false);

        var objectKey = $"menus/icons/{Guid.NewGuid():N}.{extension}";

        await client.PutObjectAsync(new PutObjectArgs()
            .WithBucket(BucketName)
            .WithObject(objectKey)
            .WithStreamData(content)
            .WithObjectSize(length)
            .WithContentType(ExtensionToContentType(extension)),
            ct).ConfigureAwait(false);

        return objectKey;
    }

    public async Task UploadFileAsync(Stream content, string objectKey, string contentType, long length, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(objectKey);
        var client = GetClient();
        await EnsureBucketAsync(client, ct).ConfigureAwait(false);
        await client.PutObjectAsync(new PutObjectArgs()
            .WithBucket(BucketName)
            .WithObject(objectKey)
            .WithStreamData(content)
            .WithObjectSize(length)
            .WithContentType(contentType),
            ct).ConfigureAwait(false);
    }

    public async Task DeleteFileAsync(string objectKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(objectKey);
        var client = GetClient();
        try
        {
            await client.RemoveObjectAsync(new RemoveObjectArgs()
                .WithBucket(BucketName)
                .WithObject(objectKey),
                ct).ConfigureAwait(false);
        }
        catch (Minio.Exceptions.ObjectNotFoundException)
        {
            // Object already gone — deletion is idempotent.
        }
    }

    public async Task<string> GetPresignedUrlAsync(string objectKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(objectKey);
        // Sign with the presign client so the URL host is browser-reachable (see field doc).
        var client = GetPresignClient();
        // PresignedGetObjectAsync is pure HMAC URL construction — no network call, no CT.
        return await client.PresignedGetObjectAsync(
            new PresignedGetObjectArgs()
                .WithBucket(BucketName)
                .WithObject(objectKey)
                .WithExpiry(3600)).ConfigureAwait(false);
    }

    private static string ExtensionToContentType(string ext) => ext switch
    {
        "png" => "image/png",
        "jpg" or "jpeg" => "image/jpeg",
        _ => "application/octet-stream",
    };
}

internal static partial class MinioStorageLog
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "MinIO bucket '{Bucket}' not found; creating it.")]
    public static partial void CreatingBucket(ILogger logger, string bucket);
}
