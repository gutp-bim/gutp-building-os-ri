using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.Shared.Module.Oss;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Test.Infrastructure;

/// <summary>
/// Verifies that OSS no-op adapters start without crashing and return safe defaults.
/// </summary>
public class OssAdapterSmokeTest
{
    [Fact]
    public async Task OssDigitalTwinDatabase_ReturnsEmptyCollections()
    {
        var sut = new OssDigitalTwinDatabase(NullLogger<OssDigitalTwinDatabase>.Instance);
        Assert.Empty(await sut.ListBuildings());
        Assert.Empty(await sut.ListFloors(null));
        Assert.Empty(await sut.ListSpaces(null));
        Assert.Empty(await sut.ListDevices(null));
        Assert.Empty(await sut.ListPoints(null));
        Assert.Null(await sut.GetBuilding("any"));
        Assert.Null(await sut.GetPoint("any"));
    }

    [Fact]
    public async Task OssTelemetryDatabase_ReturnsEmptyCollections()
    {
        var sut = new OssTelemetryDatabase(NullLogger<OssTelemetryDatabase>.Instance);
        Assert.Empty(await sut.GetWarmTelemetries("p1", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow));
        Assert.Empty(await sut.GetColdTelemetries("p1", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow));
        Assert.Null(await sut.GetHotTelemetry("p1"));
    }

    [Fact]
    public async Task OssControlSchemaResolver_ReturnsNull_ForEmptyPointId()
    {
        // The resolver now queries OxiGraph (#153); an empty point id short-circuits to null before
        // any query, so this stays a server-free smoke test.
        var sut = new OssControlSchemaResolver(
            new BuildingOS.Shared.Infrastructure.OxiGraph.OxiGraphClient(new HttpClient(), "http://localhost:1"),
            NullLogger<OssControlSchemaResolver>.Instance);
        var result = await sut.ResolveAsync(
            new Point { DtId = "dt-1", Id = "", Name = "P1" },
            null);
        Assert.Null(result);
    }

    [Fact]
    public async Task OssPointIdDataSource_ReturnsEmptyArray()
    {
        var sut = new OssPointIdDataSource(NullLogger<OssPointIdDataSource>.Instance);
        Assert.Empty(await sut.GetPointIdInfosAsync());
    }
}
