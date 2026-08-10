using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>
/// Integration tests for <see cref="OxiGraphIngestMaterializer"/> (REC/Brick → SBCO ingest
/// materialization). The upstream pipeline (smartbuilding_datamodels → smartbuilding_datamodel_builder)
/// emits the building hierarchy in RealEstateCore (rec:) vocabulary; these tests confirm that RDF
/// becomes queryable through the existing sbco:-only read paths (<see cref="OxiGraphDigitalTwinDatabase"/>)
/// without any change to those query builders — the whole point of materializing at ingest instead of
/// teaching every query site a second vocabulary.
/// </summary>
public class OxiGraphIngestMaterializerTest(OxiGraphFixture oxiGraph)
    : IntegrationTestBase, IClassFixture<OxiGraphFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => oxiGraph.ClearAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // Mirrors the existing "THX shape" fixture in OxiGraphImportTest.cs (Building → Level, equipment
    // joined by the sbco:floor literal, no Room) but with the hierarchy classes/relations expressed in
    // REC vocabulary — exactly what the canonical pipeline actually emits today. EquipmentExt/PointExt
    // and their SBCO-specific fields stay sbco: (the pipeline emits those as sbco: directly; they are
    // not part of the REC/Brick materialization gap). sbco:building is the denormalized literal #292's
    // ingress hierarchy policy relies on — carried through untouched, not part of any materialization
    // rule, so this fixture doubles as a regression check that it survives materialization.
    private const string RecVocabularyTtl = """
        @prefix rec: <https://w3id.org/rec/> .
        @prefix sbco: <https://www.sbco.or.jp/ont/> .

        <https://www.sbco.or.jp/ont/resource/bldg-rec-1> a rec:Building ;
          sbco:id "REC-BLDG-1" ; rec:name "REC Tower" ;
          rec:hasPart <https://www.sbco.or.jp/ont/resource/level-rec-9f> .
        <https://www.sbco.or.jp/ont/resource/level-rec-9f> a rec:Level ;
          sbco:id "9F" ; rec:name "9F" ;
          rec:hasPart <https://www.sbco.or.jp/ont/resource/room-rec-1> .
        <https://www.sbco.or.jp/ont/resource/dev-rec-1> a sbco:EquipmentExt ;
          sbco:id "REC-AHU-01" ; rec:name "AHU (REC-sourced)" ;
          rec:locatedIn <https://www.sbco.or.jp/ont/resource/room-rec-1> ;
          rec:hasPoint <https://www.sbco.or.jp/ont/resource/pt-rec-1> .
        <https://www.sbco.or.jp/ont/resource/pt-rec-1> a sbco:PointExt ;
          sbco:id "REC-PT-01" ; rec:name "Supply Air Temperature" ;
          sbco:pointType "TemperatureSensor" ; sbco:pointSpecification "Measurement" ;
          sbco:writable "false" ; sbco:gatewayId "GW-REC-01" ;
          sbco:site "rec-site" ; sbco:building "REC-BLDG-1" .

        <https://www.sbco.or.jp/ont/resource/room-rec-1> a rec:Room ;
          sbco:id "REC-ROOM-1" ; rec:name "REC Room 901" .
        """;

    [Fact]
    public async Task MaterializeAsync_RecVocabularyTwin_IsQueryableThroughExistingSbcoReadPath()
    {
        await Seed(RecVocabularyTtl);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var db = new OxiGraphDigitalTwinDatabase(oxiGraph.Client, cache);

        // Same read path OxiGraphImportTest.GetPointDetailByPointId_ResolvesBuildingWithoutRooms
        // exercises against sbco:-native input — proves the REC-sourced twin resolves identically.
        var detail = await db.GetPointDetailByPointId("REC-PT-01");

        Assert.NotNull(detail);
        Assert.Equal("REC Tower", detail!.Device?.BuildingName);
        Assert.Equal("TemperatureSensor", detail.Point.Type);
        Assert.Equal("GW-REC-01", detail.Point.GatewayName);
    }

    [Fact]
    public async Task MaterializeAsync_CalledTwice_TripleCountIsStable()
    {
        var materializer = new OxiGraphIngestMaterializer(oxiGraph.Client);

        await materializer.MaterializeAsync(RecVocabularyTtl);
        var countFirst = await CountDefaultGraphTriplesAsync();

        await materializer.MaterializeAsync(RecVocabularyTtl);
        var countSecond = await CountDefaultGraphTriplesAsync();

        Assert.True(countFirst > 0, "should have materialized triples");
        Assert.Equal(countFirst, countSecond);
    }

    [Fact]
    public async Task MaterializeAsync_PreservesSbcoBuildingLiteralUsedByIngressHierarchyPolicy()
    {
        await Seed(RecVocabularyTtl);

        var rows = await oxiGraph.Client.QueryAsync("""
            PREFIX sbco: <https://www.sbco.or.jp/ont/>
            SELECT ?building WHERE {
              <https://www.sbco.or.jp/ont/resource/pt-rec-1> sbco:building ?building .
            }
            """);

        Assert.Single(rows);
        Assert.Equal("REC-BLDG-1", rows[0]["building"]);
    }

    // smartbuilding_datamodels defines sbco:Room owl:equivalentClass rec:Room. Materializing the
    // complete hierarchy here guards all current sbco:-only consumers (tree, search, and point detail).
    [Fact]
    public async Task MaterializeAsync_RecRoom_IsVisibleInTheResourceTreeAndScopedSearch()
    {
        await Seed(RecVocabularyTtl);

        const string building = "https://www.sbco.or.jp/ont/resource/bldg-rec-1";
        const string floor = "https://www.sbco.or.jp/ont/resource/level-rec-9f";
        const string room = "https://www.sbco.or.jp/ont/resource/room-rec-1";
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var db = new OxiGraphDigitalTwinDatabase(oxiGraph.Client, cache);

        Assert.Single(await db.ListBuildings());
        Assert.Equal(floor, Assert.Single(await db.ListFloors(building)).DtId);
        Assert.Equal(room, Assert.Single(await db.ListSpaces(floor)).DtId);

        var hits = await db.SearchResources("Room 901", "space", building, [], 10, 0);
        var hit = Assert.Single(hits);
        Assert.Equal(room, hit.DtId);
        Assert.Equal("space", hit.Type);

        var detail = await db.GetPointDetailByPointId("REC-PT-01");
        Assert.NotNull(detail);
        Assert.Equal("REC Tower", detail!.Device?.BuildingName);
        Assert.Equal("REC Room 901", detail.Space?.Name);

        var sourceRows = await oxiGraph.Client.QueryAsync("""
            PREFIX rec: <https://w3id.org/rec/>
            SELECT ?room WHERE { GRAPH <urn:bos:twin-source> {
              ?room a rec:Room .
            }}
            """);

        Assert.Equal(room, Assert.Single(sourceRows)["room"]);
    }

    // A second, independent twin fragment (different building/device/point ids) used to verify Append
    // merges on top of whatever MaterializeAsync (Replace) already seeded, rather than requiring a
    // clean store.
    private const string SecondRecFragmentTtl = """
        @prefix rec: <https://w3id.org/rec/> .
        @prefix sbco: <https://www.sbco.or.jp/ont/> .

        <https://www.sbco.or.jp/ont/resource/bldg-rec-2> a rec:Building ;
          sbco:id "REC-BLDG-2" ; rec:name "REC Annex" ;
          rec:hasPart <https://www.sbco.or.jp/ont/resource/level-rec-3f> .
        <https://www.sbco.or.jp/ont/resource/level-rec-3f> a rec:Level ;
          sbco:id "3F" ; rec:name "3F" ;
          rec:hasPart <https://www.sbco.or.jp/ont/resource/room-rec-2> .
        <https://www.sbco.or.jp/ont/resource/room-rec-2> a rec:Room ;
          sbco:id "REC-ROOM-2" ; rec:name "REC Room 301" .
        <https://www.sbco.or.jp/ont/resource/dev-rec-2> a sbco:EquipmentExt ;
          sbco:id "REC-AHU-02" ; rec:name "AHU (appended)" ;
          rec:locatedIn <https://www.sbco.or.jp/ont/resource/room-rec-2> ;
          rec:hasPoint <https://www.sbco.or.jp/ont/resource/pt-rec-2> .
        <https://www.sbco.or.jp/ont/resource/pt-rec-2> a sbco:PointExt ;
          sbco:id "REC-PT-02" ; rec:name "Return Air Temperature" ;
          sbco:pointType "TemperatureSensor" ; sbco:pointSpecification "Measurement" ;
          sbco:writable "false" ; sbco:gatewayId "GW-REC-02" ;
          sbco:site "rec-site" ; sbco:building "REC-BLDG-2" .
        """;

    [Fact]
    public async Task MaterializeAppendAsync_RecVocabularyFragment_MergesWithoutDroppingExistingTwin()
    {
        var materializer = new OxiGraphIngestMaterializer(oxiGraph.Client);
        await materializer.MaterializeAsync(RecVocabularyTtl);
        await materializer.MaterializeAppendAsync(SecondRecFragmentTtl);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var db = new OxiGraphDigitalTwinDatabase(oxiGraph.Client, cache);

        // The originally-seeded (Replace) point is still there...
        var original = await db.GetPointDetailByPointId("REC-PT-01");
        Assert.NotNull(original);
        Assert.Equal("REC Tower", original!.Device?.BuildingName);

        // ...and the appended REC-vocabulary fragment is materialized and queryable too, exactly like
        // the Replace path (the gap the review comment on #313 flagged: Append used to silently import
        // rec:/brick: triples verbatim, invisible to this same sbco:-only read path).
        var appended = await db.GetPointDetailByPointId("REC-PT-02");
        Assert.NotNull(appended);
        Assert.Equal("REC Annex", appended!.Device?.BuildingName);
        Assert.Equal("REC Room 301", appended.Space?.Name);

        await materializer.MaterializeAppendAsync(SecondRecFragmentTtl);
        var rooms = await db.SearchResources("REC Room 301", "space", null, [], 10, 0);
        Assert.Single(rooms);
    }

    [Fact]
    public async Task MaterializeAsync_SbcoOnlyRecOnlyAndDualTypedRooms_AreQueryableOnceEach()
    {
        const string roomsTtl = """
            @prefix rec: <https://w3id.org/rec/> .
            @prefix sbco: <https://www.sbco.or.jp/ont/> .

            <urn:test:room-sbco> a sbco:Room ; sbco:id "ROOM-SBCO" ; sbco:name "SBCO room" .
            <urn:test:room-rec> a rec:Room ; sbco:id "ROOM-REC" ; rec:name "REC room" .
            <urn:test:room-both> a sbco:Room, rec:Room ; sbco:id "ROOM-BOTH" ; sbco:name "Dual room" .
            """;

        await new OxiGraphIngestMaterializer(oxiGraph.Client).MaterializeAsync(roomsTtl);

        var rows = await oxiGraph.Client.QueryAsync("""
            PREFIX sbco: <https://www.sbco.or.jp/ont/>
            SELECT ?room WHERE { ?room a sbco:Room . }
            ORDER BY ?room
            """);

        Assert.Equal(
            ["urn:test:room-both", "urn:test:room-rec", "urn:test:room-sbco"],
            rows.Select(r => r["room"]));
    }

    [Fact]
    public async Task MaterializeAppendAsync_DoesNotRetainStagingGraphAsProvenance()
    {
        var materializer = new OxiGraphIngestMaterializer(oxiGraph.Client);
        await materializer.MaterializeAppendAsync(SecondRecFragmentTtl);

        // Append staging graphs are per-call GUIDs, always dropped -- unlike Replace's fixed
        // urn:bos:twin-source, there should be no leftover named graph after an append completes.
        var rows = await oxiGraph.Client.QueryAsync("""
            SELECT (COUNT(*) AS ?c) WHERE {
              GRAPH ?g { ?s ?p ?o }
              FILTER(STRSTARTS(STR(?g), "urn:bos:twin-append-source:"))
            }
            """);

        Assert.Equal("0", rows[0]["c"]);
    }

    private async Task Seed(string turtle)
    {
        var tmp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmp, turtle);
            var materializer = new OxiGraphIngestMaterializer(oxiGraph.Client);
            var svc = new OxiGraphSeedHostedService(
                oxiGraph.Client, materializer, NullLogger<OxiGraphSeedHostedService>.Instance);
            await svc.RunAsync(tmp, null, CancellationToken.None);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    private async Task<int> CountDefaultGraphTriplesAsync()
    {
        var rows = await oxiGraph.Client.QueryAsync(
            "SELECT (COUNT(*) AS ?c) WHERE { ?s ?p ?o }");
        return int.Parse(rows[0]["c"]);
    }
}
