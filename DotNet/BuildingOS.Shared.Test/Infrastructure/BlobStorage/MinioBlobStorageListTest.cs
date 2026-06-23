using Amazon.S3;
using Amazon.S3.Model;
using BuildingOS.Shared.Infrastructure.BlobStorage;
using Moq;

namespace BuildingOS.Shared.Test.Infrastructure.BlobStorage;

/// <summary>
/// Regression: the AWS SDK leaves <c>ListObjectsV2Response.S3Objects</c> null (not an empty list) when
/// a prefix matches zero objects. `ListAsync` must coalesce to empty instead of throwing — otherwise
/// listing an empty prefix (e.g. the Parquet latest-value fallback scanning many empty hour partitions)
/// throws ArgumentNullException and the read returns 500.
/// </summary>
public class MinioBlobStorageListTest
{
    [Fact]
    public async Task ListAsync_EmptyPrefix_NullS3Objects_ReturnsEmpty_NoThrow()
    {
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(c => c.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListObjectsV2Response { S3Objects = null, IsTruncated = false });

        var storage = new MinioBlobStorage(s3.Object);

        var keys = await storage.ListAsync("cold", "building_id=GW/year=2026/month=06/day=14/hour=03/");

        Assert.Empty(keys);
    }

    [Fact]
    public async Task ListAsync_ReturnsKeys_WhenObjectsPresent()
    {
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(c => c.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListObjectsV2Response
            {
                S3Objects = new List<S3Object>
                {
                    new() { Key = "a/part-1.parquet" },
                    new() { Key = "a/part-2.parquet" },
                },
                IsTruncated = false,
            });

        var storage = new MinioBlobStorage(s3.Object);

        var keys = await storage.ListAsync("cold", "a/");

        Assert.Equal(new[] { "a/part-1.parquet", "a/part-2.parquet" }, keys);
    }
}
