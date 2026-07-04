using System.Text;
using BuildingOS.IntegrationTest.Collections;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared.Infrastructure.BlobStorage;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

[Collection("Minio")]
public class MinioBlobStorageTest : IntegrationTestBase
{
    private readonly MinioBlobStorage _storage;
    private const string Bucket = "test-bucket";

    public MinioBlobStorageTest(MinioFixture fixture)
    {
        _storage = new MinioBlobStorage(fixture.S3Client);
    }

    [Fact]
    public async Task PutAsync_AndGetAsync_RoundTrips()
    {
        var content = "hello from minio";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        await _storage.PutAsync(Bucket, "roundtrip.txt", stream, "text/plain");

        var result = await _storage.GetAsync(Bucket, "roundtrip.txt");
        Assert.NotNull(result);
        using var reader = new StreamReader(result!);
        Assert.Equal(content, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task ExistsAsync_ReturnsTrueAfterPut()
    {
        using var stream = new MemoryStream("exist-check"u8.ToArray());
        await _storage.PutAsync(Bucket, "exists.txt", stream);

        Assert.True(await _storage.ExistsAsync(Bucket, "exists.txt"));
    }

    [Fact]
    public async Task ExistsAsync_ReturnsFalse_WhenKeyMissing()
    {
        Assert.False(await _storage.ExistsAsync(Bucket, "nonexistent-key-xyz.txt"));
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenKeyMissing()
    {
        var result = await _storage.GetAsync(Bucket, "missing-key-abc.txt");
        Assert.Null(result);
    }

    [Fact]
    public async Task ListAsync_ReturnsUploadedKeys()
    {
        var prefix = $"list-test/{Guid.NewGuid()}/";
        var keys = new[] { $"{prefix}a.json", $"{prefix}b.json", $"{prefix}c.json" };

        foreach (var key in keys)
        {
            using var stream = new MemoryStream("data"u8.ToArray());
            await _storage.PutAsync(Bucket, key, stream, "application/json");
        }

        var listed = await _storage.ListAsync(Bucket, prefix);
        Assert.Equal(keys.Order(), listed.Order());
    }

    [Fact]
    public async Task DeleteAsync_RemovesKey()
    {
        using var stream = new MemoryStream("to-delete"u8.ToArray());
        await _storage.PutAsync(Bucket, "delete-me.txt", stream);
        Assert.True(await _storage.ExistsAsync(Bucket, "delete-me.txt"));

        await _storage.DeleteAsync(Bucket, "delete-me.txt");

        Assert.False(await _storage.ExistsAsync(Bucket, "delete-me.txt"));
    }

    [Fact]
    public async Task PutAsync_OverwritesExistingKey()
    {
        using var first = new MemoryStream("first version"u8.ToArray());
        await _storage.PutAsync(Bucket, "overwrite.txt", first, "text/plain");

        using var second = new MemoryStream("second version"u8.ToArray());
        await _storage.PutAsync(Bucket, "overwrite.txt", second, "text/plain");

        var result = await _storage.GetAsync(Bucket, "overwrite.txt");
        using var reader = new StreamReader(result!);
        Assert.Equal("second version", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task PutAsync_LargePayload_Succeeds()
    {
        var largeData = new string('x', 1024 * 1024); // 1 MB
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(largeData));

        await _storage.PutAsync(Bucket, "large-payload.txt", stream, "text/plain");

        Assert.True(await _storage.ExistsAsync(Bucket, "large-payload.txt"));
    }
}
