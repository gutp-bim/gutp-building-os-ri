using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>
/// Integration tests for customTags (KeyBoolMapEntry) tag search on /resources/search (#332).
/// Seeds OxiGraph with tagged points and verifies SearchResources matches customTags[key] == true,
/// excludes false-valued tags, and ANDs multiple tags. Authorization is layered on top elsewhere
/// (AuthorizedTwinView); this exercises the SPARQL path end-to-end against a real OxiGraph.
/// </summary>
public class ResourceSearchTagFilterTest(OxiGraphFixture oxiGraph)
    : IntegrationTestBase, IClassFixture<OxiGraphFixture>, IAsyncLifetime
{
    private const string TaggedTtl = """
        @prefix sbco: <https://www.sbco.or.jp/ont/> .
        @prefix xsd: <http://www.w3.org/2001/XMLSchema#> .

        <https://www.sbco.or.jp/ont/resource/PT-TAG-1> a sbco:PointExt ;
          sbco:id "PT-TAG-1" ; sbco:name "Tagged Point" ;
          sbco:customTags [ a sbco:KeyBoolMapEntry ; sbco:key "hvac" ;        sbco:value "true"^^xsd:boolean ] ;
          sbco:customTags [ a sbco:KeyBoolMapEntry ; sbco:key "temperature" ; sbco:value "true"^^xsd:boolean ] .

        <https://www.sbco.or.jp/ont/resource/PT-TAG-2> a sbco:PointExt ;
          sbco:id "PT-TAG-2" ; sbco:name "Untagged Point" ;
          sbco:customTags [ a sbco:KeyBoolMapEntry ; sbco:key "hvac" ;     sbco:value "false"^^xsd:boolean ] ;
          sbco:customTags [ a sbco:KeyBoolMapEntry ; sbco:key "tenant-a" ; sbco:value "false"^^xsd:boolean ] .
        """;

    private OxiGraphDigitalTwinDatabase Db() =>
        new(oxiGraph.Client, new MemoryCache(new MemoryCacheOptions()));

    public async Task InitializeAsync()
    {
        await oxiGraph.ClearAsync();
        await oxiGraph.Client.ReplaceDefaultGraphAsync(TaggedTtl);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SearchByTag_ReturnsOnlyPointsWhereTagIsTrue()
    {
        var hits = await Db().SearchResources(null, "point", null, ["hvac"], 50, 0);

        Assert.Single(hits);
        Assert.Equal("PT-TAG-1", hits[0].Id);
    }

    [Fact]
    public async Task SearchByTag_ExcludesPointsWhereTagIsFalse()
    {
        // PT-TAG-2 has hvac=false and tenant-a=false → never matched by a true-tag search.
        var hits = await Db().SearchResources(null, "point", null, ["tenant-a"], 50, 0);

        Assert.Empty(hits);
    }

    [Fact]
    public async Task SearchByMultipleTags_IsAnd()
    {
        var both = await Db().SearchResources(null, "point", null, ["hvac", "temperature"], 50, 0);
        Assert.Single(both);
        Assert.Equal("PT-TAG-1", both[0].Id);

        // PT-TAG-1 has hvac=true but no "lighting" tag → AND yields nothing.
        var none = await Db().SearchResources(null, "point", null, ["hvac", "lighting"], 50, 0);
        Assert.Empty(none);
    }

    [Fact]
    public async Task SearchWithoutTags_ReturnsAllPoints()
    {
        var hits = await Db().SearchResources(null, "point", null, [], 50, 0);

        Assert.Equal(2, hits.Length);
    }
}
