using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared.Domain.TwinAdmin;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>
/// #517: authorization identifies a twin node by its business id (sbco:id, #504), so two nodes of the
/// same type sharing one would share every grant — a tenant granted space "501" in one building would
/// read the other building's room 501. The import preview reports such duplicates and apply refuses
/// them. The check is per type (a Room and an Equipment may share an id) and, for an append, spans
/// the existing twin too — but re-importing the very same node is not a duplicate.
/// </summary>
public class TwinAdminIdCollisionTest(OxiGraphFixture oxiGraph)
    : IntegrationTestBase, IClassFixture<OxiGraphFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => oxiGraph.ClearAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private OxiGraphTwinAdminService Service() => new(oxiGraph.Client, new OxiGraphIngestMaterializer(oxiGraph.Client));

    private const string TwoBuildingsTtl = """
        @prefix sbco: <https://www.sbco.or.jp/ont/> .
        <urn:t:b1> a sbco:Building ; sbco:id "B1" ; sbco:hasPart <urn:t:b1-f5> .
        <urn:t:b1-f5> a sbco:Level ; sbco:id "B1-5F" ; sbco:name "5F" ; sbco:hasPart <urn:t:b1-501> .
        <urn:t:b1-501> a sbco:Room ; sbco:id "501" ; sbco:name "501" .
        <urn:t:b2> a sbco:Building ; sbco:id "B2" ; sbco:hasPart <urn:t:b2-f5> .
        <urn:t:b2-f5> a sbco:Level ; sbco:id "B2-5F" ; sbco:name "5F" ; sbco:hasPart <urn:t:b2-501> .
        <urn:t:b2-501> a sbco:Room ; sbco:id "501" ; sbco:name "501" .
        """;

    [Fact]
    public async Task Replace_TwoRoomsSharingAnId_AreReported_AndInvalid()
    {
        var preview = await Service().PreviewImportAsync(TwoBuildingsTtl, TwinImportMode.Replace);

        var collision = Assert.Single(preview.IdCollisions);
        Assert.Equal("space", collision.ResourceType);
        Assert.Equal("501", collision.Id);
        Assert.Equal(2, collision.NodeCount);
        Assert.Equal(1, preview.IdCollisionCount);
        Assert.False(preview.Valid);
    }

    [Fact]
    public async Task DifferentTypesSharingAnId_AreNotACollision()
    {
        const string ttl = """
            @prefix sbco: <https://www.sbco.or.jp/ont/> .
            <urn:t:b1> a sbco:Building ; sbco:id "X" ; sbco:hasPart <urn:t:f1> .
            <urn:t:f1> a sbco:Level ; sbco:id "F1" ; sbco:name "1F" .
            <urn:t:dev> a sbco:EquipmentExt ; sbco:id "X" ; sbco:locatedIn <urn:t:f1> ; sbco:hasPoint <urn:t:pt> .
            <urn:t:pt> a sbco:PointExt ; sbco:id "PT" .
            """;

        var preview = await Service().PreviewImportAsync(ttl, TwinImportMode.Replace);

        Assert.Empty(preview.IdCollisions);
        Assert.True(preview.Valid);
    }

    [Fact]
    public async Task Append_ANewNodeReusingAnExistingId_IsReported()
    {
        await oxiGraph.Client.ReplaceDefaultGraphAsync("""
            @prefix sbco: <https://www.sbco.or.jp/ont/> .
            <urn:t:b1> a sbco:Building ; sbco:id "B1" ; sbco:hasPart <urn:t:f1> .
            <urn:t:f1> a sbco:Level ; sbco:id "F1" ; sbco:name "1F" .
            <urn:t:dev-old> a sbco:EquipmentExt ; sbco:id "AHU-1" ; sbco:locatedIn <urn:t:f1> ; sbco:hasPoint <urn:t:pt-old> .
            <urn:t:pt-old> a sbco:PointExt ; sbco:id "PT-OLD" .
            """);

        var preview = await Service().PreviewImportAsync("""
            @prefix sbco: <https://www.sbco.or.jp/ont/> .
            <urn:t:dev-new> a sbco:EquipmentExt ; sbco:id "AHU-1" ; sbco:locatedIn <urn:t:f1> ; sbco:hasPoint <urn:t:pt-new> .
            <urn:t:pt-new> a sbco:PointExt ; sbco:id "PT-NEW" .
            """, TwinImportMode.Append);

        var collision = Assert.Single(preview.IdCollisions);
        Assert.Equal(("device", "AHU-1", 2), (collision.ResourceType, collision.Id, collision.NodeCount));
    }

    [Fact]
    public async Task Append_ReimportingTheSameNode_IsNotACollision()
    {
        const string ttl = """
            @prefix sbco: <https://www.sbco.or.jp/ont/> .
            <urn:t:b1> a sbco:Building ; sbco:id "B1" ; sbco:hasPart <urn:t:f1> .
            <urn:t:f1> a sbco:Level ; sbco:id "F1" ; sbco:name "1F" .
            <urn:t:dev> a sbco:EquipmentExt ; sbco:id "AHU-1" ; sbco:locatedIn <urn:t:f1> ; sbco:hasPoint <urn:t:pt> .
            <urn:t:pt> a sbco:PointExt ; sbco:id "PT" .
            """;
        await oxiGraph.Client.ReplaceDefaultGraphAsync(ttl);

        var preview = await Service().PreviewImportAsync(ttl, TwinImportMode.Append);

        Assert.Empty(preview.IdCollisions);
    }

    [Fact]
    public async Task Append_DuplicatesOnlyWithinTheExistingTwin_AreNotBlamedOnTheImport()
    {
        // The existing twin already has two "501"s; an unrelated append must not be refused for it.
        await oxiGraph.Client.ReplaceDefaultGraphAsync(TwoBuildingsTtl);

        var preview = await Service().PreviewImportAsync("""
            @prefix sbco: <https://www.sbco.or.jp/ont/> .
            <urn:t:dev> a sbco:EquipmentExt ; sbco:id "AHU-9" ; sbco:locatedIn <urn:t:b1-f5> ; sbco:hasPoint <urn:t:pt9> .
            <urn:t:pt9> a sbco:PointExt ; sbco:id "PT-9" .
            """, TwinImportMode.Append);

        Assert.Empty(preview.IdCollisions);
    }
}
