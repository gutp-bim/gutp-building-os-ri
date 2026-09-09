using System.Net;
using System.Net.Http;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BuildingOS.Shared.Test.Infrastructure.OxiGraph;

/// <summary>
/// #446, second layer. <c>AuthorizedTwinView</c> rejects a malformed dtId before it ever reaches the
/// twin (<c>AuthorizedTwinViewIriGuardTest</c>), but it is not the only door: three user-reachable
/// paths call <c>IDigitalTwinDatabase</c> directly today — <c>PointDetailController.List</c> and
/// <c>DeviceDetailController.List</c> (<c>?buildingDtId</c> straight into
/// <c>ListPointDetails</c>/<c>ListDeviceDetails</c>) and <c>ResourceMetadataController.PatchAsync</c>
/// (its existence probe and the <c>UpdateResourceMetadataAsync</c> write). So the implementation
/// itself refuses a dtId it cannot safely interpolate, and this test pins that no HTTP request is
/// made at all in that case.
///
/// Reads answer as if the resource were absent (null / empty), matching the authorization layer's
/// 404. The write throws instead: silently doing nothing would return the caller a 204 for a change
/// that never happened, and the write is the path an injected IRI could use to rewrite the twin.
/// </summary>
public class OxiGraphDigitalTwinDatabaseIriGuardTest
{
    private const string Injection = "urn:test:b1> } INSERT DATA { <urn:x> <urn:y> <urn:z>";

    private static (OxiGraphDigitalTwinDatabase db, CapturingHttpHandler handler) BuildDb(
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new CapturingHttpHandler(@"{ ""results"": { ""bindings"": [] } }", status);
        var http = new HttpClient(handler);
        var client = new OxiGraphClient(http, "http://oxigraph:7878");
        var cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        return (new OxiGraphDigitalTwinDatabase(client, cache), handler);
    }

    public static TheoryData<string> MalformedDtIds =>
    [
        Injection,
        "urn:test:b 1",
        "<urn:test:b1>",
        "b1",
    ];

    [Theory]
    [MemberData(nameof(MalformedDtIds))]
    public async Task SingleResourceReads_MalformedDtId_ReturnNull_WithoutQuerying(string dtId)
    {
        var (db, handler) = BuildDb();

        Assert.Null(await db.GetBuilding(dtId));
        Assert.Null(await db.GetFloor(dtId));
        Assert.Null(await db.GetSpace(dtId));
        Assert.Null(await db.GetDevice(dtId));

        Assert.Null(handler.LastRequestBody);
    }

    [Theory]
    [MemberData(nameof(MalformedDtIds))]
    public async Task CollectionReads_MalformedDtId_ReturnEmpty_WithoutQuerying(string dtId)
    {
        var (db, handler) = BuildDb();

        Assert.Empty(await db.ListFloors(dtId));
        Assert.Empty(await db.ListSpaces(dtId));
        Assert.Empty(await db.ListDevices(dtId));
        Assert.Empty(await db.ListPoints(dtId));
        Assert.Empty(await db.ListAdjacentSpaces(dtId));
        Assert.Empty(await db.ListPointDetails(dtId));
        Assert.Empty(await db.ListDeviceDetails(dtId));
        Assert.Empty(await db.SearchResources(null, null, dtId, [], 50, 0));

        Assert.Null(handler.LastRequestBody);
    }

    [Theory]
    [MemberData(nameof(MalformedDtIds))]
    public async Task UpdateResourceMetadataAsync_MalformedDtId_Throws_WithoutWriting(string dtId)
    {
        var (db, handler) = BuildDb(HttpStatusCode.NoContent);

        await Assert.ThrowsAsync<ArgumentException>(() => db.UpdateResourceMetadataAsync(
            dtId,
            new Dictionary<string, string?> { ["ifcGuid"] = "3Skg8nAD1AJAiNfIxGkWjF" },
            null,
            CancellationToken.None));

        Assert.Null(handler.LastRequestBody);
    }

    // ── Characterization (added after the guard, not part of its RED) ─────────

    [Fact]
    public async Task BlankScopeId_StillQueriesUnscoped()
    {
        // "" is the documented "no filter" input of the list reads, not a malformed IRI — the
        // unscoped branch runs before any interpolation, so the guard must not intercept it.
        var (db, handler) = BuildDb();

        Assert.Empty(await db.ListFloors(""));
        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("Level", handler.LastRequestBody!);
    }

    [Theory]
    // The dtIds a real twin holds are the RDF node IRIs themselves: urn:… in the unit fixtures, and
    // in the seeds the percent-encoded sbco resource IRIs (OxiGraphImportTest's building is exactly
    // the second one). Percent-escapes are ordinary IRI characters — they must survive the guard,
    // or every deployed twin becomes unreadable.
    [InlineData("urn:dtid:b1")]
    [InlineData("https://www.sbco.or.jp/ont/resource/building%3Asite%3Asite-1%2Fbldg-1")]
    [InlineData("https://www.sbco.or.jp/ont/resource/部屋-101")]
    public async Task WellFormedDtId_StillQueries(string dtId)
    {
        var (db, handler) = BuildDb();

        Assert.Null(await db.GetBuilding(dtId));

        Assert.NotNull(handler.LastRequestBody);
        // The body is the form-urlencoded "query=…", so compare against the decoded SPARQL.
        Assert.Contains($"<{dtId}>", Uri.UnescapeDataString(handler.LastRequestBody!));
    }
}
