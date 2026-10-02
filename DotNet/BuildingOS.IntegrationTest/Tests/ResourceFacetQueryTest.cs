using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>
/// The SPARQL behind the attribute filters, facet rows and tag suggestions (#454), against a real
/// OxiGraph. The builder tests only assert query fragments; this proves the queries actually run and
/// select what the facets claim — in particular that a point takes its device's type, falls back to a
/// type it carries itself (CSV-derived twins), and that a resource is not lost to the OPTIONALs.
/// </summary>
public class ResourceFacetQueryTest(OxiGraphFixture oxiGraph)
    : IntegrationTestBase, IClassFixture<OxiGraphFixture>, IAsyncLifetime
{
    private const string Ttl = """
        @prefix sbco: <https://www.sbco.or.jp/ont/> .
        @prefix xsd: <http://www.w3.org/2001/XMLSchema#> .
        @prefix r: <https://www.sbco.or.jp/ont/resource/> .

        r:AHU1 a sbco:EquipmentExt ; sbco:id "AHU1" ; sbco:name "AHU One" ; sbco:deviceType "AHU" ;
          sbco:hasPoint r:P-SAT , r:P-CO2 .
        r:P-SAT a sbco:PointExt ; sbco:id "P-SAT" ; sbco:name "Supply Air" ;
          sbco:pointType "Temperature" ; sbco:unit "degC" ; sbco:gatewayId "GW-1" ;
          sbco:customTags [ a sbco:KeyBoolMapEntry ; sbco:key "critical" ; sbco:value "true"^^xsd:boolean ] .
        r:P-CO2 a sbco:PointExt ; sbco:id "P-CO2" ; sbco:name "Return CO2" ;
          sbco:pointType "CO2" ; sbco:unit "ppm" ; sbco:gatewayId "GW-2" ;
          sbco:customTags [ a sbco:KeyBoolMapEntry ; sbco:key "critical" ; sbco:value "true"^^xsd:boolean ] ;
          sbco:customTags [ a sbco:KeyBoolMapEntry ; sbco:key "tenant-a" ; sbco:value "false"^^xsd:boolean ] .

        # CSV-derived twin: no equipment link, the point carries its own deviceType.
        r:P-LOOSE a sbco:PointExt ; sbco:id "P-LOOSE" ; sbco:name "Loose" ;
          sbco:deviceType "VAV" ; sbco:pointType "Temperature" ; sbco:unit "degC" ; sbco:gatewayId "GW-1" .
        """;

    private static readonly string[] None = [];

    private OxiGraphDigitalTwinDatabase Db() => new(oxiGraph.Client, new MemoryCache(new MemoryCacheOptions()));

    private static ResourceAttributeFilter Attrs(
        string[]? deviceTypes = null, string[]? pointTypes = null, string[]? units = null, string[]? gateways = null) =>
        new(deviceTypes ?? [], pointTypes ?? [], units ?? [], gateways ?? []);

    public async Task InitializeAsync()
    {
        await oxiGraph.ClearAsync();
        await oxiGraph.Client.ReplaceDefaultGraphAsync(Ttl);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task FilterByDeviceType_MatchesThePointsOfThatDevice_AndPointsCarryingTheTypeThemselves()
    {
        var ahu = await Db().SearchResourcesFiltered(null, "point", null, None, Attrs(deviceTypes: ["AHU"]), 50, 0);
        var vav = await Db().SearchResourcesFiltered(null, "point", null, None, Attrs(deviceTypes: ["VAV"]), 50, 0);

        Assert.Equal(["P-CO2", "P-SAT"], ahu.Select(h => h.Id).Order());
        Assert.Equal(["P-LOOSE"], vav.Select(h => h.Id));
    }

    [Fact]
    public async Task FilterGroups_AreANDed_ValuesWithinAGroupAreORed()
    {
        var both = await Db().SearchResourcesFiltered(
            null, "point", null, None, Attrs(pointTypes: ["Temperature", "CO2"], gateways: ["GW-1"]), 50, 0);

        Assert.Equal(["P-LOOSE", "P-SAT"], both.Select(h => h.Id).Order());
    }

    [Fact]
    public async Task FilterCombinesWithTags()
    {
        var hits = await Db().SearchResourcesFiltered(null, "point", null, ["critical"], Attrs(units: ["ppm"]), 50, 0);

        Assert.Equal(["P-CO2"], hits.Select(h => h.Id));
    }

    [Fact]
    public async Task FacetRows_CarryEveryAttribute_OfEveryType_WithNoTypeOrAttributeConstraint()
    {
        var rows = await Db().ListFacetRows(null, null, None, 1000);

        Assert.Equal(["AHU1", "P-CO2", "P-LOOSE", "P-SAT"], rows.Select(r => r.Id).Distinct().Order());
        var sat = rows.Single(r => r.Id == "P-SAT");
        Assert.Equal(("AHU", "Temperature", "degC", "GW-1"), (sat.DeviceType, sat.PointType, sat.Unit, sat.GatewayId));
        var loose = rows.Single(r => r.Id == "P-LOOSE");
        Assert.Equal("VAV", loose.DeviceType);   // own type, no owning device
    }

    [Fact]
    public async Task FacetRows_AreNarrowedByQAndTags_ButNotByAttributes()
    {
        var byTag = await Db().ListFacetRows(null, null, ["critical"], 1000);
        var byQ = await Db().ListFacetRows("co2", null, None, 1000);

        Assert.Equal(["P-CO2", "P-SAT"], byTag.Select(r => r.Id).Distinct().Order());
        Assert.Equal(["P-CO2"], byQ.Select(r => r.Id).Distinct());
    }

    [Fact]
    public async Task FacetRows_KeepADeviceThatHasNoPointAttributes()
    {
        var rows = await Db().ListFacetRows("AHU One", null, None, 1000);

        var ahu = Assert.Single(rows);
        Assert.Equal("AHU", ahu.DeviceType);
        Assert.Null(ahu.PointType);
    }

    [Fact]
    public async Task TagUsage_ListsOnlyTrueTags_FilteredByPrefixIgnoringCase()
    {
        var all = await Db().ListTagUsage(null);
        var crit = await Db().ListTagUsage("CRI");

        // tenant-a is false → never listed.
        Assert.DoesNotContain(all, u => u.Tag == "tenant-a");
        Assert.Equal(["P-CO2", "P-SAT"], crit.Select(u => u.Id).Order());
        Assert.All(crit, u => Assert.Equal("critical", u.Tag));
    }
}
