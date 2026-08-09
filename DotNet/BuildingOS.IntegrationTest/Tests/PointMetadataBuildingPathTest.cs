using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared.Module;
using BuildingOS.Shared.Module.Oss;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>
/// Integration tests for the ingress metadata source's building-reachability resolution (#292).
///
/// <para>
/// Strict ingress used to gate on the denormalized <c>sbco:building</c> literal, which answers a
/// different question than the one being asked: nothing joins that string, so it can be absent from
/// a point a building plainly contains and present on a point no building does. The gate now uses
/// <c>HasBuildingPath</c>, resolved by the same traversal the import-time orphan preview uses (#291).
/// </para>
///
/// <para>
/// These run against a real OxiGraph deliberately. The failure mode is a traversal that silently
/// matches nothing — it parses perfectly and simply returns no binding, which is indistinguishable
/// from "not reachable" unless a real graph is on the other end.
/// </para>
/// </summary>
public class PointMetadataBuildingPathTest(OxiGraphFixture oxiGraph)
    : IntegrationTestBase, IClassFixture<OxiGraphFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => oxiGraph.ClearAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // Path A: the spatial chain, locatedIn → Room → Level → Building.
    private const string SpatialTtl = """
        @prefix sbco: <https://www.sbco.or.jp/ont/> .
        <urn:test:bldg-1> a sbco:Building ; sbco:id "bldg-1" ; sbco:name "Building 1" ;
          sbco:hasPart <urn:test:floor-1> .
        <urn:test:floor-1> a sbco:Level ; sbco:id "floor-1" ; sbco:name "floor-1" ;
          sbco:hasPart <urn:test:room-1> .
        <urn:test:room-1> a sbco:Room ; sbco:id "room-1" ; sbco:name "Room 1" .
        <urn:test:dev-1> a sbco:EquipmentExt ; sbco:id "DEV001" ; sbco:name "AHU" ;
          sbco:locatedIn <urn:test:room-1> ;
          sbco:hasPoint <urn:test:pt-1> .
        <urn:test:pt-1> a sbco:PointExt ; sbco:id "PT001" ; sbco:name "Room Temp" .
        """;

    // Path B: no Room anywhere; equipment joins its Level through the sbco:floor literal. This is the
    // THX shape, and the one the old literal-based gate rejected outright — note the point carries no
    // sbco:building.
    private const string FloorJoinTtl = """
        @prefix sbco: <https://www.sbco.or.jp/ont/> .
        <urn:test:bldg-thx> a sbco:Building ; sbco:id "THX" ; sbco:name "THX" ;
          sbco:hasPart <urn:test:level-7f> .
        <urn:test:level-7f> a sbco:Level ; sbco:id "7F" ; sbco:name "7F" .
        <urn:test:dev-thx> a sbco:EquipmentExt ; sbco:id "172_31_105_17" ; sbco:name "AHU" ;
          sbco:floor "7F" ;
          sbco:hasPoint <urn:test:pt-thx> .
        <urn:test:pt-thx> a sbco:PointExt ; sbco:id "172_31_105_17-3002" ; sbco:name "On/Off Status" .
        """;

    // A stale/mistyped literal naming a building that contains nothing. The old gate accepted this.
    private const string StaleLiteralTtl = """
        @prefix sbco: <https://www.sbco.or.jp/ont/> .
        <urn:test:bldg-real> a sbco:Building ; sbco:id "bldg-1" ; sbco:name "Building 1" .
        <urn:test:dev-orphan> a sbco:EquipmentExt ; sbco:id "DEV999" ; sbco:name "Stray" ;
          sbco:hasPoint <urn:test:pt-orphan> .
        <urn:test:pt-orphan> a sbco:PointExt ; sbco:id "PT999" ; sbco:name "Stray Point" ;
          sbco:building "bldg-typo" .
        """;

    private async Task<PointMetadata> LoadAsync(string ttl, string pointId)
    {
        await oxiGraph.Client.ReplaceDefaultGraphAsync(ttl);
        var all = await new OxiGraphPointMetadataDataSource(oxiGraph.Client).GetAllAsync();
        return Assert.Single(all, m => m.PointId == pointId);
    }

    [Fact]
    public async Task SpatialChain_IsReachable()
    {
        var meta = await LoadAsync(SpatialTtl, "PT001");

        Assert.True(meta.HasBuildingPath);
        Assert.Equal("DEV001", meta.DeviceId);
    }

    [Fact]
    public async Task FloorLiteralJoin_IsReachable_EvenWithoutTheBuildingLiteral()
    {
        var meta = await LoadAsync(FloorJoinTtl, "172_31_105_17-3002");

        // The case the literal-based gate got backwards: reachable, but no sbco:building to show for it.
        Assert.True(meta.HasBuildingPath);
        Assert.Equal(string.Empty, meta.Building);
    }

    [Fact]
    public async Task StaleBuildingLiteral_IsNotReachable()
    {
        var meta = await LoadAsync(StaleLiteralTtl, "PT999");

        Assert.False(meta.HasBuildingPath);
        // The literal is still surfaced verbatim — it stays the enrichment value and the lake's
        // partition key. Only the hierarchy gate stops trusting it.
        Assert.Equal("bldg-typo", meta.Building);
    }

    [Fact]
    public async Task PointWithNoEquipment_IsNotReachable()
    {
        const string ttl = """
            @prefix sbco: <https://www.sbco.or.jp/ont/> .
            <urn:test:pt-lonely> a sbco:PointExt ; sbco:id "PT000" ; sbco:name "Lonely" .
            """;

        var meta = await LoadAsync(ttl, "PT000");

        Assert.False(meta.HasBuildingPath);
        Assert.Equal(string.Empty, meta.DeviceId);
    }

    // Resolution must not multiply rows: a point reachable by BOTH paths is still one point, and a
    // duplicated row would silently double every metadata load.
    [Fact]
    public async Task ReachableByBothPaths_YieldsExactlyOneRow()
    {
        const string ttl = """
            @prefix sbco: <https://www.sbco.or.jp/ont/> .
            <urn:test:bldg-1> a sbco:Building ; sbco:id "bldg-1" ; sbco:name "Building 1" ;
              sbco:hasPart <urn:test:floor-1> .
            <urn:test:floor-1> a sbco:Level ; sbco:id "floor-1" ; sbco:name "floor-1" ;
              sbco:hasPart <urn:test:room-1> .
            <urn:test:room-1> a sbco:Room ; sbco:id "room-1" ; sbco:name "Room 1" .
            <urn:test:dev-1> a sbco:EquipmentExt ; sbco:id "DEV001" ; sbco:name "AHU" ;
              sbco:locatedIn <urn:test:room-1> ;
              sbco:floor "floor-1" ;
              sbco:hasPoint <urn:test:pt-1> .
            <urn:test:pt-1> a sbco:PointExt ; sbco:id "PT001" ; sbco:name "Room Temp" .
            """;

        await oxiGraph.Client.ReplaceDefaultGraphAsync(ttl);
        var all = await new OxiGraphPointMetadataDataSource(oxiGraph.Client).GetAllAsync();

        var meta = Assert.Single(all, m => m.PointId == "PT001");
        Assert.True(meta.HasBuildingPath);
    }
}
