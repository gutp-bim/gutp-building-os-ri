using BuildingOS.Shared;
using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuildingOS.Shared.Test.Infrastructure.TelemetryDatabase;

public class OssTelemetryQueryRouterTest
{
    private readonly Mock<IHotTelemetryStore> _hot = new();
    private readonly Mock<IWarmTelemetryStore> _warm = new();
    private readonly Mock<IColdTelemetryStore> _cold = new();
    private readonly Mock<IAggregatedTelemetryStore> _agg = new();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    private static readonly TimeSpan WarmRetention = TimeSpan.FromDays(90);

    private OssTelemetryQueryRouter CreateSut() => new(
        NullLogger<OssTelemetryQueryRouter>.Instance,
        _cache,
        _hot.Object,
        _warm.Object,
        _cold.Object,
        _agg.Object,
        WarmRetention);

    [Fact]
    public async Task QueryAsync_Latest_ReturnsHotValue()
    {
        var expected = new ValidTelemetryData { PointId = "p1", Value = 42.0 };
        _hot.Setup(h => h.GetAsync("p1", It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var result = await CreateSut().QueryAsync(new TelemetryQueryRequest("p1", Latest: true));

        Assert.Single(result);
        Assert.Equal(expected, result[0]);
        _warm.Verify(w => w.QueryLatestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task QueryAsync_Latest_FallsBackToWarm_WhenHotMiss()
    {
        var expected = new ValidTelemetryData { PointId = "p1", Value = 22.0 };
        _hot.Setup(h => h.GetAsync("p1", It.IsAny<CancellationToken>())).ReturnsAsync((ValidTelemetryData?)null);
        _warm.Setup(w => w.QueryLatestAsync("p1", It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var result = await CreateSut().QueryAsync(new TelemetryQueryRequest("p1", Latest: true));

        Assert.Single(result);
        Assert.Equal(expected, result[0]);
    }

    [Fact]
    public async Task QueryAsync_Latest_FallsBackToWarm_WhenHotThrows()
    {
        var expected = new ValidTelemetryData { PointId = "p1", Value = 22.0 };
        _hot.Setup(h => h.GetAsync("p1", It.IsAny<CancellationToken>())).ThrowsAsync(new Exception("NATS down"));
        _warm.Setup(w => w.QueryLatestAsync("p1", It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var result = await CreateSut().QueryAsync(new TelemetryQueryRequest("p1", Latest: true));

        Assert.Single(result);
        Assert.Equal(expected, result[0]);
    }

    [Fact]
    public async Task QueryAsync_Raw_RecentRange_UsesWarmStore()
    {
        var start = DateTime.UtcNow.AddDays(-1);
        var end = DateTime.UtcNow;
        var expected = new[] { new ValidTelemetryData { PointId = "p1" } };
        _warm.Setup(w => w.QueryAsync("p1", It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(expected);

        var result = await CreateSut().QueryAsync(
            new TelemetryQueryRequest("p1", start, end, TelemetryGranularity.Raw));

        Assert.Equal(expected, result);
        _cold.Verify(c => c.QueryAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task QueryAsync_Raw_OldRange_UsesColdStore()
    {
        var start = DateTime.UtcNow.AddDays(-200);
        var end = DateTime.UtcNow.AddDays(-150);
        var expected = new[] { new ValidTelemetryData { PointId = "p1" } };
        _cold.Setup(c => c.QueryAsync("p1", It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(expected);

        var result = await CreateSut().QueryAsync(
            new TelemetryQueryRequest("p1", start, end, TelemetryGranularity.Raw));

        Assert.Equal(expected, result);
        _warm.Verify(w => w.QueryAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task QueryAsync_Raw_SpansBoundary_MergesWarmAndCold()
    {
        var start = DateTime.UtcNow.AddDays(-200);
        var end = DateTime.UtcNow.AddDays(-1);
        var coldRows = new[] { new ValidTelemetryData { PointId = "p1", Value = 1.0 } };
        var warmRows = new[] { new ValidTelemetryData { PointId = "p1", Value = 2.0 } };

        _cold.Setup(c => c.QueryAsync("p1", It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(coldRows);
        _warm.Setup(w => w.QueryAsync("p1", It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(warmRows);

        var result = await CreateSut().QueryAsync(
            new TelemetryQueryRequest("p1", start, end, TelemetryGranularity.Raw));

        Assert.Equal(2, result.Length);
        Assert.Contains(result, r => r.Value == 1.0);
        Assert.Contains(result, r => r.Value == 2.0);
    }

    [Fact]
    public async Task QueryAsync_Raw_BoundaryEdge_WarmOnlyWhenStartJustAfterBoundary()
    {
        // start is just inside warm retention — cold must never be called
        var start = DateTime.UtcNow - WarmRetention + TimeSpan.FromHours(1);
        var end = DateTime.UtcNow.AddDays(-1);
        var warmRows = new[] { new ValidTelemetryData { PointId = "p1", Value = 5.0 } };

        _warm.Setup(w => w.QueryAsync("p1", It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(warmRows);

        var result = await CreateSut().QueryAsync(
            new TelemetryQueryRequest("p1", start, end, TelemetryGranularity.Raw));

        Assert.Equal(warmRows, result);
        _cold.Verify(c => c.QueryAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task QueryAsync_Hour_UsesAggregatedStore()
    {
        var start = DateTime.UtcNow.AddDays(-7);
        var end = DateTime.UtcNow;
        var expected = new[] { new ValidTelemetryData { PointId = "p1", Value = 100.0 } };
        _agg.Setup(a => a.QueryHourlyAsync("p1", It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await CreateSut().QueryAsync(
            new TelemetryQueryRequest("p1", start, end, TelemetryGranularity.Hour));

        Assert.Equal(expected, result);
        _warm.Verify(w => w.QueryAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task QueryAsync_Day_UsesAggregatedStore()
    {
        var start = DateTime.UtcNow.AddDays(-30);
        var end = DateTime.UtcNow;
        var expected = new[] { new ValidTelemetryData { PointId = "p1", Value = 200.0 } };
        _agg.Setup(a => a.QueryDailyAsync("p1", It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await CreateSut().QueryAsync(
            new TelemetryQueryRequest("p1", start, end, TelemetryGranularity.Day));

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task QueryAsync_Hour_UsesCache_OnSecondCall()
    {
        var start = DateTime.UtcNow.AddHours(-24);
        var end = DateTime.UtcNow;
        var expected = new[] { new ValidTelemetryData { PointId = "p1" } };
        _agg.Setup(a => a.QueryHourlyAsync("p1", It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var sut = CreateSut();
        var req = new TelemetryQueryRequest("p1", start, end, TelemetryGranularity.Hour);
        await sut.QueryAsync(req);
        await sut.QueryAsync(req);

        _agg.Verify(a => a.QueryHourlyAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueryAsync_Hour_FallsBackToWarm_WhenAggStoreNull()
    {
        var start = DateTime.UtcNow.AddDays(-7);
        var end = DateTime.UtcNow;
        var expected = new[] { new ValidTelemetryData { PointId = "p1" } };
        _warm.Setup(w => w.QueryAsync("p1", It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(expected);

        var sut = new OssTelemetryQueryRouter(
            NullLogger<OssTelemetryQueryRouter>.Instance,
            _cache,
            warm: _warm.Object);

        var result = await sut.QueryAsync(
            new TelemetryQueryRequest("p1", start, end, TelemetryGranularity.Hour));

        Assert.Equal(expected, result);
    }
}
