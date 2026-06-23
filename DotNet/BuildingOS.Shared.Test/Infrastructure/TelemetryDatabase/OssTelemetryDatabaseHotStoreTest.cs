using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuildingOS.Shared.Test.Infrastructure.TelemetryDatabase;

public class OssTelemetryDatabaseHotStoreTest
{
    private readonly Mock<IHotTelemetryStore> _hot = new();
    private readonly Mock<IWarmTelemetryStore> _warm = new();

    private OssTelemetryDatabase SutWithBoth => new(
        NullLogger<OssTelemetryDatabase>.Instance,
        _warm.Object,
        _hot.Object);

    [Fact]
    public async Task GetHotTelemetry_UsesHotStore_WhenValuePresent()
    {
        var expected = new ValidTelemetryData { PointId = "p1", Value = 42.0 };
        _hot.Setup(h => h.GetAsync("p1", It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var result = await SutWithBoth.GetHotTelemetry("p1");

        Assert.Equal(expected, result);
        _warm.Verify(w => w.QueryLatestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetHotTelemetry_FallsBackToWarm_WhenHotStoreMiss()
    {
        var expected = new ValidTelemetryData { PointId = "p1", Value = 22.0 };
        _hot.Setup(h => h.GetAsync("p1", It.IsAny<CancellationToken>())).ReturnsAsync((ValidTelemetryData?)null);
        _warm.Setup(w => w.QueryLatestAsync("p1", It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var result = await SutWithBoth.GetHotTelemetry("p1");

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task GetHotTelemetry_FallsBackToWarm_WhenHotStoreThrows()
    {
        var expected = new ValidTelemetryData { PointId = "p1", Value = 22.0 };
        _hot.Setup(h => h.GetAsync("p1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("NATS unreachable"));
        _warm.Setup(w => w.QueryLatestAsync("p1", It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var result = await SutWithBoth.GetHotTelemetry("p1");

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task GetHotTelemetry_ReturnsNull_WhenNeitherStoreConfigured()
    {
        var sut = new OssTelemetryDatabase(NullLogger<OssTelemetryDatabase>.Instance);
        var result = await sut.GetHotTelemetry("p1");
        Assert.Null(result);
    }
}
