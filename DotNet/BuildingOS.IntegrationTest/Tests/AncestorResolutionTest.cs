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

    [Fact]
    public async Task PointDetail_ForADeviceWithTwoPlacements_IsDeterministic_RoomPlacementFirst()
    {
        await oxiGraph.Client.ReplaceDefaultGraphAsync(Ttl);
        var db = new BuildingOS.Shared.Infrastructure.OxiGraphDigitalTwinDatabase(
            oxiGraph.Client, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));

        for (var i = 0; i < 5; i++)
        {
            var detail = await db.GetPointDetailByPointId("P1");
            Assert.NotNull(detail);
            Assert.Equal("R501", detail!.Space?.Id);
            Assert.Equal("F5", detail.Floor?.Id);
        }
    }

    // #548: the batched union must equal the union of the per-id chains — it is what lets a user who
    // holds only a room/point grant see that node's building and floor as navigation containers.
    [Theory]
    [InlineData("point", new[] { "P1", "P2" })]
    [InlineData("device", new[] { "DUAL", "LITERAL" })]
    [InlineData("space", new[] { "R501" })]
    [InlineData("floor", new[] { "F5", "F6" })]
    public async Task AncestorUnion_EqualsTheUnionOfPerIdChains(string type, string[] ids)
    {
        await oxiGraph.Client.ReplaceDefaultGraphAsync(Ttl);
        var resolver = new OxiGraphHierarchyResolver(oxiGraph.Client);

        var expected = new HashSet<(string, string)>();
        foreach (var id in ids)
            foreach (var a in await resolver.GetAncestorsAsync(type, id)) expected.Add(a);

        var union = await resolver.GetAncestorUnionAsync(type, ids);

        Assert.Equal(expected.OrderBy(a => a), union.OrderBy(a => a));
    }

    [Fact]
    public async Task AncestorUnion_IgnoresUnknownIds_EscapesLiterals_AndSpansChunks()
    {
        await oxiGraph.Client.ReplaceDefaultGraphAsync(Ttl);
        var resolver = new OxiGraphHierarchyResolver(oxiGraph.Client);

        // More ids than one VALUES chunk, a quote that must be escaped, and the real one last.
        // CR / LF are not allowed raw in a SPARQL short string; an id carrying them must not break the
        // whole query (and with it every hierarchy list for that user).
        var ids = Enumerable.Range(0, 450).Select(i => $"NOPE-{i}")
            .Append("x\"y").Append("line\nbreak").Append("cr\rret").Append("tab\tbed").Append("R501").ToArray();

        var union = await resolver.GetAncestorUnionAsync("space", ids);

        Assert.Equal(new[] { ("building", "B1"), ("floor", "F5") }, union.OrderBy(a => a));
    }

    [Fact]
    public async Task AncestorUnion_EmptyInput_AsksNothing()
    {
        var resolver = new OxiGraphHierarchyResolver(oxiGraph.Client);
        Assert.Empty(await resolver.GetAncestorUnionAsync("point", []));
    }

    /// <summary>
    /// A legacy grant recorded against a dtId (#504 migration) still authorizes its node, so its
    /// ancestors must be found too: an input that is an absolute IRI also matches the node itself.
    /// </summary>
    [Theory]
    [InlineData("space", "urn:t:r501", new[] { "building:B1", "floor:F5" })]
    [InlineData("floor", "urn:t:f6", new[] { "building:B1" })]
    [InlineData("device", "urn:t:dual", new[] { "building:B1", "floor:F5", "floor:F6", "space:R501" })]
    [InlineData("point", "urn:t:p1", new[] { "building:B1", "device:DUAL", "floor:F5", "floor:F6", "space:R501" })]
    public async Task AncestorUnion_MatchesLegacyDtIdGrants(string type, string dtId, string[] expected)
    {
        await oxiGraph.Client.ReplaceDefaultGraphAsync(Ttl);
        var resolver = new OxiGraphHierarchyResolver(oxiGraph.Client);

        var union = await resolver.GetAncestorUnionAsync(type, [dtId, "R501-not-an-iri>"]);

        Assert.Equal(expected, union.Select(a => $"{a.ResourceType}:{a.ResourceId}").Order());
    }
}
