using BuildingOS.Shared.Module;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Test.Module;

public class PointIdFactoryTest
{
    private static PointIdInfo[] MakeInfos(params (string Key, string PointId)[] pairs)
        => pairs.Select(p => new PointIdInfo { Key = p.Key, PointId = p.PointId }).ToArray();

    private static PointIdFactory Build(
        Mock<IPointIdDataSource> source,
        TimeSpan? cacheTtl = null,
        TimeSpan? retryBaseDelay = null)
        => new(source.Object,
               NullLogger<PointIdFactory>.Instance,
               cacheTtl: cacheTtl ?? TimeSpan.FromHours(1),
               retryBaseDelay: retryBaseDelay ?? TimeSpan.Zero);

    // ── TryGetPointIdAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task TryGetPointId_KnownKey_ReturnsTrueAndPointId()
    {
        var source = new Mock<IPointIdDataSource>();
        source.Setup(s => s.GetPointIdInfosAsync())
              .ReturnsAsync(MakeInfos(("local-001", "PT001")));

        using var factory = Build(source);

        var (found, ids) = await factory.TryGetPointIdAsync("hvac", "local-001");

        Assert.True(found);
        Assert.Equal(["PT001"], ids);
    }

    [Fact]
    public async Task TryGetPointId_UnknownKey_ReturnsFalseAndEmpty()
    {
        var source = new Mock<IPointIdDataSource>();
        source.Setup(s => s.GetPointIdInfosAsync())
              .ReturnsAsync(MakeInfos(("local-001", "PT001")));

        using var factory = Build(source);

        var (found, ids) = await factory.TryGetPointIdAsync("hvac", "unknown");

        Assert.False(found);
        Assert.Empty(ids);
    }

    // ── Cache behaviour ───────────────────────────────────────────────────────

    [Fact]
    public async Task Cache_LoadsOnce_WhenCalledMultipleTimes()
    {
        var source = new Mock<IPointIdDataSource>();
        source.Setup(s => s.GetPointIdInfosAsync())
              .ReturnsAsync(MakeInfos(("k", "v")));

        using var factory = Build(source);

        await factory.TryGetPointIdAsync("x", "k");
        await factory.TryGetPointIdAsync("x", "k");
        await factory.TryGetPointIdAsync("x", "k");

        source.Verify(s => s.GetPointIdInfosAsync(), Times.Once);
    }

    [Fact]
    public async Task Cache_Refreshes_AfterTtlExpiry()
    {
        var source = new Mock<IPointIdDataSource>();
        source.Setup(s => s.GetPointIdInfosAsync())
              .ReturnsAsync(MakeInfos(("k", "v")));

        // TTL of zero forces refresh on every call
        using var factory = Build(source, cacheTtl: TimeSpan.Zero);

        await factory.TryGetPointIdAsync("x", "k");
        await factory.TryGetPointIdAsync("x", "k");

        source.Verify(s => s.GetPointIdInfosAsync(), Times.AtLeast(2));
    }

    [Fact]
    public async Task Cache_RefreshesOnMiss_PicksUpNewlyAddedKey()
    {
        // #188: a key added to the twin after the last load must resolve without waiting out the full
        // TTL (otherwise GetPointIdAsync falls back to a GUID). A zero miss-interval reloads on miss.
        var data = MakeInfos();
        var source = new Mock<IPointIdDataSource>();
        source.Setup(s => s.GetPointIdInfosAsync()).ReturnsAsync(() => data);

        using var factory = new PointIdFactory(
            source.Object, NullLogger<PointIdFactory>.Instance,
            cacheTtl: TimeSpan.FromHours(1), retryBaseDelay: TimeSpan.Zero, missRefreshInterval: TimeSpan.Zero);

        var (found1, _) = await factory.TryGetPointIdAsync("x", "k"); // miss → reload (still empty)
        Assert.False(found1);

        data = MakeInfos(("k", "PT1")); // key added to the twin

        var (found2, ids) = await factory.TryGetPointIdAsync("x", "k"); // miss → reload → resolves
        Assert.True(found2);
        Assert.Equal(["PT1"], ids);
    }

    [Fact]
    public async Task Cache_MissRefresh_IsRateLimited_AgainstUnknownKeyFlood()
    {
        var source = new Mock<IPointIdDataSource>();
        source.Setup(s => s.GetPointIdInfosAsync()).ReturnsAsync(MakeInfos(("k", "v")));

        using var factory = new PointIdFactory(
            source.Object, NullLogger<PointIdFactory>.Instance,
            cacheTtl: TimeSpan.FromMinutes(10), retryBaseDelay: TimeSpan.Zero,
            missRefreshInterval: TimeSpan.FromMinutes(5));

        for (var i = 0; i < 50; i++)
            await factory.TryGetPointIdAsync("x", $"unknown-{i}");

        // Only the initial load — the freshly-loaded cache served every miss without reloading.
        source.Verify(s => s.GetPointIdInfosAsync(), Times.Once);
    }

    [Fact]
    public async Task Cache_ServesStaleData_WhenRefreshFails()
    {
        var source = new Mock<IPointIdDataSource>();
        source.SetupSequence(s => s.GetPointIdInfosAsync())
              .ReturnsAsync(MakeInfos(("k", "v")))
              .ThrowsAsync(new InvalidOperationException("OxiGraph unavailable"));

        // TTL of zero forces refresh attempt on every call
        using var factory = Build(source, cacheTtl: TimeSpan.Zero);

        // First call: loads successfully
        var (found1, _) = await factory.TryGetPointIdAsync("x", "k");
        Assert.True(found1);

        // Second call: refresh fails → stale cache returned
        var (found2, _) = await factory.TryGetPointIdAsync("x", "k");
        Assert.True(found2);
    }

    [Fact]
    public async Task Cache_Throws_WhenFirstLoadExhaustsAllRetries()
    {
        var source = new Mock<IPointIdDataSource>();
        source.Setup(s => s.GetPointIdInfosAsync())
              .ThrowsAsync(new InvalidOperationException("OxiGraph unavailable"));

        using var factory = Build(source);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.TryGetPointIdAsync("x", "k"));
    }

    // ── Retry logic ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Retry_SucceedsOnSecondAttempt()
    {
        var source = new Mock<IPointIdDataSource>();
        source.SetupSequence(s => s.GetPointIdInfosAsync())
              .ThrowsAsync(new InvalidOperationException("transient"))
              .ReturnsAsync(MakeInfos(("k", "v")));

        using var factory = Build(source);

        var (found, _) = await factory.TryGetPointIdAsync("x", "k");

        Assert.True(found);
        source.Verify(s => s.GetPointIdInfosAsync(), Times.Exactly(2));
    }

    // ── TryGetLocalIdAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task TryGetLocalId_KnownPointId_ReturnsTrueAndLocalId()
    {
        var source = new Mock<IPointIdDataSource>();
        source.Setup(s => s.GetPointIdInfosAsync())
              .ReturnsAsync(MakeInfos(("local-001", "urn:pt:building/pt-001")));

        using var factory = Build(source);

        var (found, localId) = await factory.TryGetLocalIdAsync("urn:pt:building/pt-001");

        Assert.True(found);
        Assert.Equal("local-001", localId);
    }

    [Fact]
    public async Task TryGetLocalId_UnknownPointId_ReturnsFalseAndEmpty()
    {
        var source = new Mock<IPointIdDataSource>();
        source.Setup(s => s.GetPointIdInfosAsync())
              .ReturnsAsync(MakeInfos(("local-001", "urn:pt:building/pt-001")));

        using var factory = Build(source);

        var (found, localId) = await factory.TryGetLocalIdAsync("urn:pt:building/unknown");

        Assert.False(found);
        Assert.Equal(string.Empty, localId);
    }

    // ── Thread safety ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ThreadSafety_ConcurrentCalls_LoadsOnlyOnce()
    {
        var source = new Mock<IPointIdDataSource>();
        source.Setup(s => s.GetPointIdInfosAsync())
              .ReturnsAsync(MakeInfos(("k", "v")));

        using var factory = Build(source);

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => factory.TryGetPointIdAsync("x", "k"));
        await Task.WhenAll(tasks);

        source.Verify(s => s.GetPointIdInfosAsync(), Times.Once);
    }
}
