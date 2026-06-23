using BuildingOS.Shared.Module;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Test.Module;

public class PointMetadataCacheTest
{
    private static PointMetadata Meta(string pointId)
        => new(pointId, Building: "b", Name: "n", DeviceId: "d", GatewayId: "gw");

    private sealed class FakeDataSource : IPointMetadataDataSource
    {
        private volatile PointMetadata[] _data;
        public int Calls { get; private set; }

        public FakeDataSource(params PointMetadata[] data) => _data = data;
        public void SetData(params PointMetadata[] data) => _data = data;

        public Task<PointMetadata[]> GetAllAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_data);
        }
    }

    private static PointMetadataCache Build(
        FakeDataSource source, TimeSpan? ttl = null, TimeSpan? missInterval = null)
        => new(source, NullLogger<PointMetadataCache>.Instance,
            cacheTtl: ttl ?? TimeSpan.FromMinutes(10),
            retryBaseDelay: TimeSpan.Zero,
            missRefreshInterval: missInterval ?? TimeSpan.FromSeconds(30));

    [Fact]
    public async Task GetAsync_ReturnsKnownPoint_FromInitialLoad()
    {
        var source = new FakeDataSource(Meta("PT1"));
        var cache = Build(source);

        var meta = await cache.GetAsync("PT1");

        Assert.NotNull(meta);
        Assert.Equal("PT1", meta!.PointId);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task GetAsync_RefreshesOnMiss_PicksUpNewlyAddedPoint()
    {
        // #188: a point added to the twin after the last load must not be skipped for up to the full
        // TTL. With a zero miss-interval the miss triggers an immediate single-flight reload.
        var source = new FakeDataSource(); // initially empty
        var cache = Build(source, missInterval: TimeSpan.Zero);

        Assert.Null(await cache.GetAsync("PT-NEW")); // miss → reload (still empty)
        source.SetData(Meta("PT-NEW"));              // point added to the twin

        var meta = await cache.GetAsync("PT-NEW");   // miss → reload → now present
        Assert.NotNull(meta);
        Assert.Equal("PT-NEW", meta!.PointId);
    }

    [Fact]
    public async Task GetAsync_MissRefresh_IsRateLimited_AgainstUnknownIdFlood()
    {
        // A flood of genuinely-unknown ids must not stampede the data source: at most one reload per
        // miss-interval. With a long miss-interval the freshly-loaded cache serves all misses.
        var source = new FakeDataSource(Meta("PT1"));
        var cache = Build(source, ttl: TimeSpan.FromMinutes(10), missInterval: TimeSpan.FromMinutes(5));

        for (var i = 0; i < 50; i++)
            Assert.Null(await cache.GetAsync($"PT-UNKNOWN-{i}"));

        // Only the initial load — the miss path saw a fresh cache and did not reload.
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task GetAsync_KnownPoint_DoesNotTriggerMissRefresh()
    {
        var source = new FakeDataSource(Meta("PT1"));
        var cache = Build(source, missInterval: TimeSpan.Zero);

        await cache.GetAsync("PT1");
        await cache.GetAsync("PT1");

        Assert.Equal(1, source.Calls); // served from cache, no reload
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_ForEmptyPointId()
    {
        var source = new FakeDataSource(Meta("PT1"));
        var cache = Build(source);

        Assert.Null(await cache.GetAsync(""));
        Assert.Equal(0, source.Calls); // no load for an empty id
    }
}
