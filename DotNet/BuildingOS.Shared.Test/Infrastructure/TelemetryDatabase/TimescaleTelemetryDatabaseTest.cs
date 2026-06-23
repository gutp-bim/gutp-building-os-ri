using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Moq;

namespace BuildingOS.Shared.Test.Infrastructure.TelemetryDatabase;

/// <summary>
/// Unit tests for tier-routing logic in TimescaleTelemetryDatabase.
/// DB and MinIO access is mocked; integration tests cover the real queries.
/// </summary>
public class TimescaleTelemetryDatabaseTest
{
    private readonly Mock<IWarmTelemetryStore> _warm = new();
    private readonly Mock<IColdTelemetryStore> _cold = new();
    private readonly DateTime _now = new(2026, 5, 17, 12, 0, 0, DateTimeKind.Utc);

    private TimescaleTelemetryDatabase Sut => new(_warm.Object, _cold.Object, () => _now);

    // ── Warm-only queries ──────────────────────────────────────────────────

    [Fact]
    public async Task GetWarmTelemetries_DelegatesToWarmStore()
    {
        var start = _now.AddDays(-1);
        var end = _now;
        var expected = new[] { new ValidTelemetryData { PointId = "p1", Value = 22.5 } };
        _warm.Setup(w => w.QueryAsync("p1", start, end, CancellationToken.None)).ReturnsAsync(expected);

        var result = await Sut.GetWarmTelemetries("p1", start, end);

        Assert.Equal(expected, result);
        _cold.Verify(c => c.QueryAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Cold-only queries ──────────────────────────────────────────────────

    [Fact]
    public async Task GetColdTelemetries_WhenRangeFullyInCold_QueriesColdOnly()
    {
        // Both start and end are older than 90 days
        var start = _now.AddDays(-100);
        var end = _now.AddDays(-95);
        var expected = new[] { new ValidTelemetryData { PointId = "p1", Value = 10.0 } };
        _cold.Setup(c => c.QueryAsync("p1", start, end, CancellationToken.None)).ReturnsAsync(expected);

        var result = await Sut.GetColdTelemetries("p1", start, end);

        Assert.Equal(expected, result);
        _warm.Verify(w => w.QueryAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetColdTelemetries_WhenRangeFullyInWarm_QueriesWarmOnly()
    {
        // Both start and end are within last 3 months
        var start = _now.AddDays(-7);
        var end = _now.AddDays(-1);
        var expected = new[] { new ValidTelemetryData { PointId = "p1", Value = 25.0 } };
        _warm.Setup(w => w.QueryAsync("p1", start, end, CancellationToken.None)).ReturnsAsync(expected);

        var result = await Sut.GetColdTelemetries("p1", start, end);

        Assert.Equal(expected, result);
        _cold.Verify(c => c.QueryAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetColdTelemetries_WhenRangeSpansBoundary_MergesBothTiers()
    {
        // Range crosses the 90-day boundary (implementation uses TimeSpan.FromDays(90))
        var coldCutoff = _now.Subtract(TimeSpan.FromDays(90));
        var start = coldCutoff.AddDays(-10);  // in cold tier
        var end = coldCutoff.AddDays(10);     // in warm tier

        var coldData = new[] { new ValidTelemetryData { PointId = "p1", Datetime = start.AddHours(1).ToString("O"), Value = 1.0 } };
        var warmData = new[] { new ValidTelemetryData { PointId = "p1", Datetime = end.AddHours(-1).ToString("O"), Value = 2.0 } };

        _cold.Setup(c => c.QueryAsync("p1", start, coldCutoff, CancellationToken.None)).ReturnsAsync(coldData);
        _warm.Setup(w => w.QueryAsync("p1", coldCutoff, end, CancellationToken.None)).ReturnsAsync(warmData);

        var result = await Sut.GetColdTelemetries("p1", start, end);

        Assert.Equal(2, result.Length);
        _cold.Verify(c => c.QueryAsync("p1", start, coldCutoff, CancellationToken.None), Times.Once);
        _warm.Verify(w => w.QueryAsync("p1", coldCutoff, end, CancellationToken.None), Times.Once);
    }

    // ── Hot query ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetHotTelemetry_ReturnsLatestFromWarmStore()
    {
        var expected = new ValidTelemetryData { PointId = "p1", Value = 30.0 };
        _warm.Setup(w => w.QueryLatestAsync("p1", CancellationToken.None)).ReturnsAsync(expected);

        var result = await Sut.GetHotTelemetry("p1");

        Assert.Equal(expected, result);
    }

    // ── Multi-point cold queries ──────────────────────────────────────────

    [Fact]
    public async Task GetColdTelemetries_MultiPoint_ReturnsDictionary()
    {
        var start = _now.AddDays(-5);
        var end = _now.AddDays(-1);
        var data1 = new[] { new ValidTelemetryData { PointId = "p1", Value = 1.0 } };
        var data2 = new[] { new ValidTelemetryData { PointId = "p2", Value = 2.0 } };
        _warm.Setup(w => w.QueryAsync("p1", start, end, CancellationToken.None)).ReturnsAsync(data1);
        _warm.Setup(w => w.QueryAsync("p2", start, end, CancellationToken.None)).ReturnsAsync(data2);

        var result = await Sut.GetColdTelemetries(new[] { "p1", "p2" }, start, end);

        Assert.Equal(data1, result["p1"]);
        Assert.Equal(data2, result["p2"]);
    }
}
