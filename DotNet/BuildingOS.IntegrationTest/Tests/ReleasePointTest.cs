using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared.Domain.TwinAdmin;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

public class ReleasePointTest(OxiGraphFixture oxiGraph)
    : IntegrationTestBase, IClassFixture<OxiGraphFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => oxiGraph.ClearAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private OxiGraphTwinAdminService Service() =>
        new(oxiGraph.Client, new OxiGraphIngestMaterializer(oxiGraph.Client));

    private const string Target = """
        @prefix sbco: <https://www.sbco.or.jp/ont/> .
        @prefix bos: <http://buildingos.gutp.jp/ontology#> .
        <urn:test:b> a sbco:Building ; sbco:id "B" ; sbco:name "Building" ; sbco:hasPart <urn:test:f> .
        <urn:test:f> a sbco:Level ; sbco:id "F" ; sbco:name "Floor" .
        <urn:test:d> a sbco:EquipmentExt ; sbco:id "D" ; sbco:name "Device" ; sbco:locatedIn <urn:test:f> ;
            sbco:hasPoint <urn:test:target> .
        <urn:test:target> a sbco:PointExt ; sbco:id "TARGET" ; sbco:name "Target" ; sbco:gatewayId "GW" ;
            sbco:writable "true" ; sbco:deviceIdBacnet "100" ;
            sbco:objectTypeBacnet "analog-output" ; sbco:instanceNoBacnet "1" ;
            bos:dataType "number" .
        """;

    private const string Release = """
        @prefix sbco: <https://www.sbco.or.jp/ont/> .
        @prefix bos: <http://buildingos.gutp.jp/ontology#> .
        <urn:test:d> sbco:hasPoint <urn:test:release> .
        <urn:test:release> a sbco:PointExt ; sbco:id "RELEASE" ; sbco:name "Release" ; sbco:gatewayId "GW" ;
            sbco:writable "true" ; bos:dataType "enum" ;
            bos:enumLabels "{\"1\":\"release\"}" ; bos:releaseOf <urn:test:target> .
        """;

    [Fact]
    public async Task Replace_ValidRelease_ExportsTargetIdAndInheritedProtocol()
    {
        var ct = CancellationToken.None;
        var preview = await Service().PreviewImportAsync(Target + Release, TwinImportMode.Replace, ct);
        Assert.True(preview.Valid);
        Assert.Empty(preview.ReleaseOfIssues);
        await Service().ApplyImportAsync(Target + Release, TwinImportMode.Replace, ct);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var points = await new OxiGraphDigitalTwinDatabase(oxiGraph.Client, cache).ListGatewayPointList("GW");
        Assert.Equal(2, points.Length);
        var release = Assert.Single(points, p => p.PointId == "RELEASE");
        Assert.Equal("TARGET", release.ReleaseOf);
        Assert.Equal("bacnet", release.Protocol);
        Assert.Null(release.BacnetDeviceId);
        Assert.Null(Assert.Single(points, p => p.PointId == "TARGET").ReleaseOf);
    }

    [Fact]
    public async Task Append_Release_ResolvesTargetInExistingGraph()
    {
        var ct = CancellationToken.None;
        await Service().ApplyImportAsync(Target, TwinImportMode.Replace, ct);
        var preview = await Service().PreviewImportAsync(Release, TwinImportMode.Append, ct);
        Assert.True(preview.Valid);
        Assert.Empty(preview.ReleaseOfIssues);
    }

    [Theory]
    [InlineData("<urn:test:target>", "<urn:test:missing>", "target_missing")]
    [InlineData("bos:dataType \"enum\"", "bos:dataType \"boolean\"", "schema_not_release_only")]
    [InlineData("sbco:gatewayId \"GW\"", "sbco:gatewayId \"OTHER\"", "target_other_gateway")]
    public async Task Replace_InvalidRelease_IsRejectedByPreview(string from, string to, string reason)
    {
        var preview = await Service().PreviewImportAsync(
            Target + Release.Replace(from, to, StringComparison.Ordinal), TwinImportMode.Replace,
            CancellationToken.None);
        Assert.False(preview.Valid);
        Assert.Equal(reason, Assert.Single(preview.ReleaseOfIssues).Reason);
        Assert.Equal(1, preview.ReleaseOfIssueCount);
    }
}
