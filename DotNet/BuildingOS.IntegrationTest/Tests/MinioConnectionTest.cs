using Amazon.S3.Model;
using BuildingOS.IntegrationTest.Collections;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

[Collection(Names.Minio)]
public class MinioConnectionTest(MinioFixture fixture) : IntegrationTestBase
{
    [Fact]
    public async Task Can_Create_Bucket()
    {
        using var s3 = fixture.CreateS3Client();
        var bucketName = $"conn-test-{Guid.NewGuid():N}";
        var response = await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucketName });
        Assert.Equal(System.Net.HttpStatusCode.OK, response.HttpStatusCode);
    }

    [Fact]
    public async Task Can_Put_And_Get_Object()
    {
        using var s3 = fixture.CreateS3Client();
        var bucket = $"rw-test-bucket-{Guid.NewGuid():N}";
        var key = "telemetry/2024/01/01/sensor-001.json";
        var content = """{"pointId":"sensor-001","value":22.5}""";

        await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket });

        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            ContentBody = content,
            ContentType = "application/json",
        });

        var getResp = await s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = key,
        });

        using var reader = new System.IO.StreamReader(getResp.ResponseStream);
        var body = await reader.ReadToEndAsync();
        Assert.Equal(content, body);
    }

    [Fact]
    public async Task Can_List_Objects()
    {
        using var s3 = fixture.CreateS3Client();
        var bucket = $"list-test-bucket-{Guid.NewGuid():N}";

        await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "file1.json", ContentBody = "{}" });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "file2.json", ContentBody = "{}" });

        var listResp = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket });
        Assert.Equal(2, listResp.S3Objects.Count);
    }
}
