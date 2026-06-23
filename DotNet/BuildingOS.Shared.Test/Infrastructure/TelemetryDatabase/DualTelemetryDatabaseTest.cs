using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Moq;

namespace BuildingOS.Shared.Test.Infrastructure.TelemetryDatabase;

public class DualTelemetryDatabaseTest
{
    private readonly Mock<ITelemetryDatabase> _primary = new();
    private readonly Mock<ITelemetryDatabase> _secondary = new();
    private DualTelemetryDatabase Sut => new(_primary.Object, _secondary.Object);

    [Fact]
    public async Task GetWarmTelemetries_ReadFromPrimary()
    {
        var start = DateTime.UtcNow.AddDays(-1);
        var end = DateTime.UtcNow;
        var expected = new[] { new ValidTelemetryData { PointId = "p1", Value = 1.0 } };
        _primary.Setup(p => p.GetWarmTelemetries("p1", start, end)).ReturnsAsync(expected);

        var result = await Sut.GetWarmTelemetries("p1", start, end);

        Assert.Equal(expected, result);
        _secondary.Verify(s => s.GetWarmTelemetries(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task GetHotTelemetry_ReadFromPrimary()
    {
        var expected = new ValidTelemetryData { PointId = "p1", Value = 42.0 };
        _primary.Setup(p => p.GetHotTelemetry("p1")).ReturnsAsync(expected);

        var result = await Sut.GetHotTelemetry("p1");

        Assert.Equal(expected, result);
        _secondary.Verify(s => s.GetHotTelemetry(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetColdTelemetries_ReadFromPrimary()
    {
        var start = DateTime.UtcNow.AddDays(-5);
        var end = DateTime.UtcNow;
        var expected = new[] { new ValidTelemetryData { PointId = "p1", Value = 5.0 } };
        _primary.Setup(p => p.GetColdTelemetries("p1", start, end)).ReturnsAsync(expected);

        var result = await Sut.GetColdTelemetries("p1", start, end);

        Assert.Equal(expected, result);
        _secondary.Verify(s => s.GetColdTelemetries(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task GetColdTelemetries_MultiPoint_ReadFromPrimary()
    {
        var start = DateTime.UtcNow.AddDays(-5);
        var end = DateTime.UtcNow;
        var expected = new Dictionary<string, ValidTelemetryData[]> { ["p1"] = Array.Empty<ValidTelemetryData>() };
        _primary.Setup(p => p.GetColdTelemetries(new[] { "p1" }, start, end)).ReturnsAsync(expected);

        var result = await Sut.GetColdTelemetries(new[] { "p1" }, start, end);

        Assert.Same(expected, result);
    }
}
