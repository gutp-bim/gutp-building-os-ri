using BuildingOS.Shared;
using BuildingOS.Shared.Domain.GatewayPointListCache;
using BuildingOS.Shared.Infrastructure;
using BuildingOs.ApiServer.GatewayProvisioning;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BuildingOS.ApiServer.Test.GatewayProvisioning;

public class PointListMaterializerTest
{
    private static GatewayPointEntry Pt(string id) => new() { PointId = id, Unit = "C", Writable = false };

    private static (PointListMaterializer materializer, Mock<IDigitalTwinDatabase> db, Mock<IGatewayPointListCacheStore> cache)
        Build()
    {
        var db = new Mock<IDigitalTwinDatabase>();
        var cache = new Mock<IGatewayPointListCacheStore>();
        var materializer = new PointListMaterializer(db.Object, cache.Object, NullLogger<PointListMaterializer>.Instance);
        return (materializer, db, cache);
    }

    [Fact]
    public async Task RebuildGatewayAsync_UpsertsTheLiveEntriesUnderTheirComputedEtag()
    {
        var (materializer, db, cache) = Build();
        var entries = new[] { Pt("PT001"), Pt("PT002") };
        db.Setup(d => d.ListGatewayPointList("GW001")).ReturnsAsync(entries);
        var expectedEtag = PointListEtag.Compute(entries);

        await materializer.RebuildGatewayAsync("GW001", default);

        cache.Verify(c => c.UpsertAsync(
            "GW001", expectedEtag,
            It.Is<IReadOnlyList<GatewayPointEntry>>(e => e.Select(x => x.PointId).SequenceEqual(new[] { "PT001", "PT002" })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RebuildAllAsync_RebuildsEveryGatewayTheTwinReports()
    {
        var (materializer, db, cache) = Build();
        db.Setup(d => d.ListGatewayIds()).ReturnsAsync(["GW001", "GW002"]);
        db.Setup(d => d.ListGatewayPointList("GW001")).ReturnsAsync([Pt("PT001")]);
        db.Setup(d => d.ListGatewayPointList("GW002")).ReturnsAsync([Pt("PT101"), Pt("PT102")]);

        await materializer.RebuildAllAsync(default);

        cache.Verify(c => c.UpsertAsync(
            "GW001", It.IsAny<string>(), It.IsAny<IReadOnlyList<GatewayPointEntry>>(), It.IsAny<CancellationToken>()),
            Times.Once);
        cache.Verify(c => c.UpsertAsync(
            "GW002", It.IsAny<string>(), It.IsAny<IReadOnlyList<GatewayPointEntry>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RebuildAllAsync_ContinuesPastOneGatewaysFailure()
    {
        var (materializer, db, cache) = Build();
        db.Setup(d => d.ListGatewayIds()).ReturnsAsync(["GW001", "GW002"]);
        db.Setup(d => d.ListGatewayPointList("GW001")).ThrowsAsync(new InvalidOperationException("twin unreachable"));
        db.Setup(d => d.ListGatewayPointList("GW002")).ReturnsAsync([Pt("PT101")]);

        // Must not throw — a single gateway's failure is logged and swallowed, not propagated to the
        // caller (a background sweep or an inline PATCH-triggered rebuild), and every other gateway
        // still gets rebuilt.
        await materializer.RebuildAllAsync(default);

        cache.Verify(c => c.UpsertAsync(
            "GW002", It.IsAny<string>(), It.IsAny<IReadOnlyList<GatewayPointEntry>>(), It.IsAny<CancellationToken>()),
            Times.Once);
        cache.Verify(c => c.UpsertAsync(
            "GW001", It.IsAny<string>(), It.IsAny<IReadOnlyList<GatewayPointEntry>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
