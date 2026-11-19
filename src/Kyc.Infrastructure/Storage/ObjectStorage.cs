using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace Kyc.Infrastructure.Storage;

public sealed class StorageOptions
{
    public const string Section = "Storage";
    public string Bucket { get; set; } = "kyc-artifacts";
    /// <summary>Endpoint used by our services, e.g. http://s3:8333 inside docker.</summary>
    public string ServiceUrl { get; set; } = "http://localhost:9000";
    /// <summary>Endpoint clients use. Pre-signed URLs are signed for this host since the signature covers Host.</summary>
    public string PublicServiceUrl { get; set; } = "http://localhost:9000";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string Region { get; set; } = "us-east-1";
    public int UploadUrlTtlSeconds { get; set; } = 300;
    public int DownloadUrlTtlSeconds { get; set; } = 120;
    public long MaxUploadBytes { get; set; } = 10 * 1024 * 1024;
}

public sealed record ObjectMetadata(long SizeBytes, string? ContentType);

public interface IObjectStorage
{
    Task<Uri> CreateUploadUrlAsync(string key, string contentType, TimeSpan ttl);
    Task<Uri> CreateDownloadUrlAsync(string key, TimeSpan ttl);
    Task<ObjectMetadata?> GetMetadataAsync(string key, CancellationToken ct);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
    Task EnsureBucketAsync(CancellationToken ct);
}

// S3-compatible storage: SeaweedFS locally, S3 in production.
public sealed class S3ObjectStorage : IObjectStorage, IDisposable
{
    private readonly StorageOptions _options;
    private readonly AmazonS3Client _internal;
    private readonly AmazonS3Client _public;

    public S3ObjectStorage(IOptions<StorageOptions> options)
    {
        _options = options.Value;
        var credentials = new BasicAWSCredentials(_options.AccessKey, _options.SecretKey);
        _internal = new AmazonS3Client(credentials, Config(_options.ServiceUrl));
        _public = new AmazonS3Client(credentials, Config(_options.PublicServiceUrl));
    }

    private AmazonS3Config Config(string url) => new()
    {
        ServiceURL = url,
        ForcePathStyle = true,
        AuthenticationRegion = _options.Region,
        UseHttp = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
    };

    public async Task<Uri> CreateUploadUrlAsync(string key, string contentType, TimeSpan ttl)
    {
        // Content type is signed, so the client can't upload a different type.
        var url = await _public.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Expires = DateTime.UtcNow.Add(ttl),
            Protocol = ProtocolFor(_options.PublicServiceUrl),
        });
        return new Uri(url);
    }

    public async Task<Uri> CreateDownloadUrlAsync(string key, TimeSpan ttl)
    {
        var url = await _public.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(ttl),
            Protocol = ProtocolFor(_options.PublicServiceUrl),
        });
        return new Uri(url);
    }

    public async Task<ObjectMetadata?> GetMetadataAsync(string key, CancellationToken ct)
    {
        try
        {
            var meta = await _internal.GetObjectMetadataAsync(_options.Bucket, key, ct);
            return new ObjectMetadata(meta.ContentLength, meta.Headers.ContentType);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        var response = await _internal.GetObjectAsync(_options.Bucket, key, ct);
        return response.ResponseStream;
    }

    public Task DeleteAsync(string key, CancellationToken ct) =>
        _internal.DeleteObjectAsync(_options.Bucket, key, ct);

    public async Task EnsureBucketAsync(CancellationToken ct)
    {
        try
        {
            await _internal.PutBucketAsync(new PutBucketRequest { BucketName = _options.Bucket }, ct);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
        }
    }

    private static Protocol ProtocolFor(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTPS : Protocol.HTTP;

    public void Dispose()
    {
        _internal.Dispose();
        _public.Dispose();
    }
}
