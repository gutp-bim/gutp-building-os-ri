using BuildingOS.Shared.Infrastructure.BlobStorage;
using Moq;

namespace BuildingOS.Shared.Test.Infrastructure.BlobStorage;

public class IBlobStorageTest
{
    private readonly Mock<IBlobStorage> _mockStorage = new();

    [Fact]
    public async Task PutAsync_StoresContent()
    {
        _mockStorage
            .Setup(s => s.PutAsync("bucket", "key.txt", It.IsAny<Stream>(), "text/plain", CancellationToken.None))
            .Returns(Task.CompletedTask);

        await _mockStorage.Object.PutAsync("bucket", "key.txt", new MemoryStream(), "text/plain");

        _mockStorage.Verify(s => s.PutAsync("bucket", "key.txt", It.IsAny<Stream>(), "text/plain", CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task GetAsync_ReturnsStream()
    {
        var expectedContent = "hello world"u8.ToArray();
        _mockStorage
            .Setup(s => s.GetAsync("bucket", "key.txt", CancellationToken.None))
            .ReturnsAsync(new MemoryStream(expectedContent));

        var stream = await _mockStorage.Object.GetAsync("bucket", "key.txt");
        var bytes = new byte[expectedContent.Length];
        await stream!.ReadAsync(bytes);

        Assert.Equal(expectedContent, bytes);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenNotFound()
    {
        _mockStorage
            .Setup(s => s.GetAsync("bucket", "missing.txt", CancellationToken.None))
            .ReturnsAsync((Stream?)null);

        var result = await _mockStorage.Object.GetAsync("bucket", "missing.txt");

        Assert.Null(result);
    }

    [Fact]
    public async Task ExistsAsync_ReturnsTrueWhenPresent()
    {
        _mockStorage.Setup(s => s.ExistsAsync("bucket", "key.txt", CancellationToken.None)).ReturnsAsync(true);
        _mockStorage.Setup(s => s.ExistsAsync("bucket", "missing.txt", CancellationToken.None)).ReturnsAsync(false);

        Assert.True(await _mockStorage.Object.ExistsAsync("bucket", "key.txt"));
        Assert.False(await _mockStorage.Object.ExistsAsync("bucket", "missing.txt"));
    }

    [Fact]
    public async Task ListAsync_ReturnsKeys()
    {
        var keys = new[] { "a.json", "b.json", "c.json" };
        _mockStorage
            .Setup(s => s.ListAsync("bucket", "prefix/", CancellationToken.None))
            .ReturnsAsync(keys);

        var result = await _mockStorage.Object.ListAsync("bucket", "prefix/");

        Assert.Equal(keys, result);
    }

    [Fact]
    public async Task DeleteAsync_Completes()
    {
        _mockStorage
            .Setup(s => s.DeleteAsync("bucket", "key.txt", CancellationToken.None))
            .Returns(Task.CompletedTask);

        await _mockStorage.Object.DeleteAsync("bucket", "key.txt");

        _mockStorage.Verify(s => s.DeleteAsync("bucket", "key.txt", CancellationToken.None), Times.Once);
    }
}
