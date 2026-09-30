using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared.Infrastructure.Authorization;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>
/// The authorization ancestor chain against a real twin. It is traversed by topology alone
/// (hasPart / locatedIn / hasPoint) and is the union of every placement, so it does not depend on
/// which SPARQL row comes first, and the sbco:floor literal never places anything.
/// </summary>
public class AncestorResolutionTest(OxiGraphFixture oxiGraph)
    : IntegrationTestBase, IClassFixture<OxiGraphFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => oxiGraph.ClearAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string Ttl = """
        @prefix sbco: <https://www.sbco.or.jp/ont/> .
        <urn:t:b1> a sbco:Building ; sbco:id "B1" ; sbco:name "B1" ; sbco:hasPart <urn:t:f5> , <urn:t:f6> .
        <urn:t:f5> a sbco:Level ; sbco:id "F5" ; sbco:name "5F" ; sbco:hasPart <urn:t:r501> .
        <urn:t:f6> a sbco:Level ; sbco:id "F6" ; sbco:name "6F" .
        <urn:t:r501> a sbco:Room ; sbco:id "R501" ; sbco:name "501" .
        # Placed twice: in room R501 (on 5F) and directly on 6F. Both chains are ancestors.
        <urn:t:dual> a sbco:EquipmentExt ; sbco:id "DUAL" ; sbco:name "dual" ;
          sbco:locatedIn <urn:t:r501> , <urn:t:f6> ; sbco:floor "5F" ; sbco:hasPoint <urn:t:p1> .
        <urn:t:p1> a sbco:PointExt ; sbco:id "P1" ; sbco:name "p1" .
        # Only the literal: placed nowhere.
        <urn:t:literal> a sbco:EquipmentExt ; sbco:id "LITERAL" ; sbco:name "literal" ;
          sbco:floor "6F" ; sbco:hasPoint <urn:t:p2> .
        <urn:t:p2> a sbco:PointExt ; sbco:id "P2" ; sbco:name "p2" .
        """;

    [Fact]
    public async Task DeviceWithTwoPlacements_HasTheUnionOfBothChains()
    {
        await oxiGraph.Client.ReplaceDefaultGraphAsync(Ttl);
        var resolver = new OxiGraphHierarchyResolver(oxiGraph.Client);

        for (var i = 0; i < 5; i++) // row order must not matter
        {
            var device = await resolver.GetAncestorsAsync("device", "DUAL");
            Assert.Equal(
                new[] { ("building", "B1"), ("floor", "F5"), ("floor", "F6"), ("space", "R501") },
                device.OrderBy(a => a.ResourceType).ThenBy(a => a.ResourceId));

            var point = await resolver.GetAncestorsAsync("point", "P1");
            Assert.Equal(
                new[] { ("building", "B1"), ("device", "DUAL"), ("floor", "F5"), ("floor", "F6"), ("space", "R501") },
                point.OrderBy(a => a.ResourceType).ThenBy(a => a.ResourceId));
        }
    }

    [Fact]
    public async Task FloorLiteralOnly_HasNoSpatialAncestors()
    {
        await oxiGraph.Client.ReplaceDefaultGraphAsync(Ttl);
        var resolver = new OxiGraphHierarchyResolver(oxiGraph.Client);

        Assert.Empty(await resolver.GetAncestorsAsync("device", "LITERAL"));
        Assert.Equal(new[] { ("device", "LITERAL") }, await resolver.GetAncestorsAsync("point", "P2"));
    }
}
