using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared.Domain.TwinAdmin;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>
/// Integration tests for the import preview's hierarchy-completeness check (#291). The check is one
/// SPARQL pattern spanning the staging graph and (for an append) the default graph, so only a real
/// OxiGraph can tell a correct pattern from one that silently orphans every import. Covers both
/// reachable paths (spatial chain / sbco:floor literal), the append-vs-replace scope, and the case
/// that motivated the feature: a TTL with no Site/Building/Level/Room at all (nexus-gateway#118).
/// </summary>
public class TwinAdminOrphanPreviewTest(OxiGraphFixture oxiGraph)
    : IntegrationTestBase, IClassFixture<OxiGraphFixture>, IAsyncLifetime
{
    // The twin as it already stands in the default graph: Building →hasPart→ Level →hasPart→ Room.
    private const string ExistingHierarchyTtl = """
        @prefix sbco: <https://www.sbco.or.jp/ont/> .

        <urn:test:bldg-1> a sbco:Building ; sbco:id "bldg-1" ; sbco:name "Building 1" ;
          sbco:hasPart <urn:test:floor-1> .
        <urn:test:floor-1> a sbco:Level ; sbco:id "floor-1" ; sbco:name "floor-1" ;
          sbco:hasPart <urn:test:room-1> .
        <urn:test:room-1> a sbco:Room ; sbco:id "room-1" ; sbco:name "Room 1" .
        """;

    // The central use case: new equipment + points hung off a Room that is already in the twin.
    private const string NewEquipmentUnderExistingRoomTtl = """
        @prefix sbco: <https://www.sbco.or.jp/ont/> .

        <urn:test:eq-new> a sbco:EquipmentExt ; sbco:id "EQ-NEW" ; sbco:name "New AHU" ;
          sbco:locatedIn <urn:test:room-1> ;
          sbco:hasPoint <urn:test:pt-new> .
        <urn:test:pt-new> a sbco:PointExt ; sbco:id "PT-NEW" ; sbco:name "New Point" .
        """;

    // Equipment joined to its Level by the sbco:floor literal only — no Room anywhere, which is what
    // the read side (OxiGraphDigitalTwinDatabase.ListPointDetails) traverses.
    private const string FloorLiteralTtl = """
        @prefix sbco: <https://www.sbco.or.jp/ont/> .

        <urn:test:bldg-2> a sbco:Building ; sbco:id "bldg-2" ; sbco:name "Building 2" ;
          sbco:hasPart <urn:test:floor-2> .
        <urn:test:floor-2> a sbco:Level ; sbco:id "floor-2" ; sbco:name "floor-2" .
        <urn:test:eq-2> a sbco:EquipmentExt ; sbco:id "EQ-2" ; sbco:floor "floor-2" ;
          sbco:hasPoint <urn:test:pt-2> .
        <urn:test:pt-2> a sbco:PointExt ; sbco:id "PT-2" ; sbco:name "Floor Literal Point" .
        """;

    // nexus-gateway#118: equipment and points only, with no spatial hierarchy of any kind.
    private const string NoHierarchyTtl = """
        @prefix sbco: <https://www.sbco.or.jp/ont/> .

        <urn:test:thx-eq> a sbco:EquipmentExt ; sbco:id "THX-EQ" ; sbco:name "THX" ;
          sbco:hasPoint <urn:test:thx-pt> .
        <urn:test:thx-pt> a sbco:PointExt ; sbco:id "THX-PT" ; sbco:name "THX Point" .
        """;

    private OxiGraphTwinAdminService Service() => new(oxiGraph.Client);

    public Task InitializeAsync() => oxiGraph.ClearAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task PreviewImport_Append_NewEquipmentUnderAnExistingRoom_IsNotOrphaned()
    {
        // The chain straddles both graphs — EquipmentExt/PointExt staged, Room/Level/Building already
        // in the default graph the append merges into — so nothing may be reported.
        await oxiGraph.Client.ReplaceDefaultGraphAsync(ExistingHierarchyTtl);

        var preview = await Service().PreviewImportAsync(
            NewEquipmentUnderExistingRoomTtl, TwinImportMode.Append);

        Assert.Equal(0, preview.OrphanCount);
        Assert.Empty(preview.Orphans);
        Assert.True(preview.Valid);
    }

    [Fact]
    public async Task PreviewImport_Replace_JudgesTheStagedTriplesAlone()
    {
        // The same Turtle as a replace: the default graph is dropped on apply, so the Room it points
        // at would be gone and the point really would be unreachable.
        await oxiGraph.Client.ReplaceDefaultGraphAsync(ExistingHierarchyTtl);

        var preview = await Service().PreviewImportAsync(
            NewEquipmentUnderExistingRoomTtl, TwinImportMode.Replace);

        Assert.Equal(1, preview.OrphanCount);
        var orphan = Assert.Single(preview.Orphans);
        Assert.Equal("urn:test:pt-new", orphan.ResourceId);
        Assert.Equal(TwinOrphanReasons.NoRoom, orphan.Reason);
    }

    [Fact]
    public async Task PreviewImport_EquipmentJoinedByTheFloorLiteral_IsNotOrphaned()
    {
        // Room/sbco:locatedIn are optional in SBCO TTL; reaching the Building through the sbco:floor
        // literal is a complete hierarchy as far as the read side is concerned.
        var preview = await Service().PreviewImportAsync(FloorLiteralTtl, TwinImportMode.Replace);

        Assert.Equal(0, preview.OrphanCount);
        Assert.True(preview.Valid);
    }

    [Fact]
    public async Task PreviewImport_EquipmentWithNoHierarchyAtAll_IsStillOrphaned()
    {
        // The motivating case must survive the widened reachability: an unrelated hierarchy in the
        // default graph does not connect equipment that links to none of it.
        await oxiGraph.Client.ReplaceDefaultGraphAsync(ExistingHierarchyTtl);

        var preview = await Service().PreviewImportAsync(NoHierarchyTtl, TwinImportMode.Append);

        Assert.Equal(1, preview.OrphanCount);
        var orphan = Assert.Single(preview.Orphans);
        Assert.Equal("urn:test:thx-pt", orphan.ResourceId);
        Assert.Equal(TwinOrphanReasons.NoRoom, orphan.Reason);
        Assert.False(preview.Valid);
    }

    [Fact]
    public async Task PreviewImport_ClassifiesEachUnreachablePointOnce()
    {
        // One point per break: no device at all / a device with no spatial anchor / an anchor whose
        // floor literal matches no Level. The branches are mutually exclusive, so each point appears
        // exactly once and the count matches the sample.
        const string mixedTtl = """
            @prefix sbco: <https://www.sbco.or.jp/ont/> .

            <urn:test:pt-loose> a sbco:PointExt ; sbco:id "PT-LOOSE" .

            <urn:test:eq-anchorless> a sbco:EquipmentExt ; sbco:id "EQ-ANCHORLESS" ;
              sbco:hasPoint <urn:test:pt-anchorless> .
            <urn:test:pt-anchorless> a sbco:PointExt ; sbco:id "PT-ANCHORLESS" .

            <urn:test:eq-unknown-floor> a sbco:EquipmentExt ; sbco:id "EQ-UNKNOWN-FLOOR" ;
              sbco:floor "no-such-floor" ; sbco:hasPoint <urn:test:pt-unknown-floor> .
            <urn:test:pt-unknown-floor> a sbco:PointExt ; sbco:id "PT-UNKNOWN-FLOOR" .
            """;
        await oxiGraph.Client.ReplaceDefaultGraphAsync(ExistingHierarchyTtl);

        var preview = await Service().PreviewImportAsync(mixedTtl, TwinImportMode.Append);

        Assert.Equal(3, preview.OrphanCount);
        Assert.Equal(3, preview.Orphans.Count);
        var reasons = preview.Orphans.ToDictionary(o => o.ResourceId, o => o.Reason);
        Assert.Equal(TwinOrphanReasons.NoDevice, reasons["urn:test:pt-loose"]);
        Assert.Equal(TwinOrphanReasons.NoRoom, reasons["urn:test:pt-anchorless"]);
        Assert.Equal(TwinOrphanReasons.NoBuildingPath, reasons["urn:test:pt-unknown-floor"]);
    }
}
