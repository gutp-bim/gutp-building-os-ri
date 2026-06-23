using Amazon.S3;
using Amazon.S3.Model;

namespace BuildingOS.Shared.Infrastructure.BlobStorage;

public class MinioBlobStorage : IBlobStorage
{
    private readonly IAmazonS3 _s3;

    public MinioBlobStorage(IAmazonS3 s3)
    {
        _s3 = s3;
    }

    public async Task PutAsync(string container, string key, Stream content, string contentType = "application/octet-stream", CancellationToken cancellationToken = default)
    {
        await EnsureBucketAsync(container, cancellationToken);
        var request = new PutObjectRequest
        {
            BucketName = container,
            Key = key,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false
        };
        await _s3.PutObjectAsync(request, cancellationToken);
    }

    public async Task<Stream?> GetAsync(string container, string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _s3.GetObjectAsync(container, key, cancellationToken);
            var ms = new MemoryStream();
            await response.ResponseStream.CopyToAsync(ms, cancellationToken);
            ms.Position = 0;
            return ms;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> ExistsAsync(string container, string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await _s3.GetObjectMetadataAsync(container, key, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> ListAsync(string container, string prefix = "", CancellationToken cancellationToken = default)
    {
        var keys = new List<string>();
        var request = new ListObjectsV2Request { BucketName = container, Prefix = prefix };
        ListObjectsV2Response response;
        do
        {
            response = await _s3.ListObjectsV2Async(request, cancellationToken);
            // The AWS SDK leaves S3Objects null (not an empty list) when a prefix matches zero objects,
            // so a list over an empty prefix would NRE/ArgumentNullException. Coalesce to empty. This
            // breaks the Parquet latest-value fallback, which lists many (often empty) hour prefixes.
            if (response.S3Objects is { } objs)
            {
                keys.AddRange(objs.Select(o => o.Key));
            }
            request.ContinuationToken = response.NextContinuationToken;
        } while (response.IsTruncated == true);
        return keys;
    }

    public async Task DeleteAsync(string container, string key, CancellationToken cancellationToken = default)
    {
        await _s3.DeleteObjectAsync(container, key, cancellationToken);
    }

    private async Task EnsureBucketAsync(string bucket, CancellationToken cancellationToken)
    {
        try
        {
            await _s3.GetBucketLocationAsync(bucket, cancellationToken);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await _s3.PutBucketAsync(bucket, cancellationToken);
        }
    }
}
