using System.Net.Http;
using System.Text.RegularExpressions;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BuildingOS.Shared.Test.Infrastructure.OxiGraph;

/// <summary>
/// #294: the single-point read paths returned less metadata than the list they belong to.
/// `GetPoint` did not SELECT ?ptSpec/?ptType/?ptGw, so `MapPoint`'s GetValueOrDefault quietly
/// produced null and the detail screen rendered "-" for metadata the twin actually held —
/// a failure mode with no error anywhere to notice.
/// </summary>
public class OxiGraphPointDetailMetadataTest
{
    private static (OxiGraphDigitalTwinDatabase Db, CapturingHttpHandler Handler) BuildCapturing(string responseJson)
    {
        var handler = new CapturingHttpHandler(responseJson);
        var http = new HttpClient(handler);
        var client = new OxiGraphClient(http, "http://oxigraph:7878");
        var cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        return (new OxiGraphDigitalTwinDatabase(client, cache), handler);
    }

    private static OxiGraphDigitalTwinDatabase BuildDb(string responseJson)
        => BuildCapturing(responseJson).Db;

    /// <summary>Variables in the SELECT clause, i.e. what the caller can actually read back.</summary>
    private static HashSet<string> ProjectedVariables(string sparql)
    {
        var select = Regex.Match(sparql, @"SELECT\s+(.*?)\s+WHERE", RegexOptions.Singleline);
        Assert.True(select.Success, $"could not locate a SELECT ... WHERE clause in:\n{sparql}");
        return Regex.Matches(select.Groups[1].Value, @"\?(\w+)")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();
    }

    // The bug in #294 is *divergence* between two paths reading the same entity, so the test is a
    // parity assertion rather than a hardcoded field list: whatever ListPoints learns to project,
    // GetPoint must project too. A list that had to be maintained by hand would drift exactly the
    // way the queries did.
    [Fact]
    public async Task GetPoint_ProjectsEverythingListPointsDoes()
    {
        var (listDb, listHandler) = BuildCapturing(@"{ ""results"": { ""bindings"": [] } }");
        await listDb.ListPoints(null);
        var listVars = ProjectedVariables(listHandler.LastRequestBody!);

        var (pointDb, pointHandler) = BuildCapturing(@"{ ""results"": { ""bindings"": [] } }");
        await pointDb.GetPoint("PT001");
        var pointVars = ProjectedVariables(pointHandler.LastRequestBody!);

        var missing = listVars.Except(pointVars).OrderBy(v => v).ToArray();
        Assert.True(
            missing.Length == 0,
            $"GetPoint omits {string.Join(", ", missing)} — a single-point lookup must not return "
            + "less metadata than the list it belongs to (#294)");
    }

    [Fact]
    public async Task GetPoint_MapsSpecificationTypeAndGateway()
    {
        var db = BuildDb(@"{
  ""results"": { ""bindings"": [
    { ""ptDt"": {""type"":""uri"",""value"":""urn:dtid:pt1""},
      ""ptId"": {""type"":""literal"",""value"":""172_31_105_17-3002""},
      ""ptName"": {""type"":""literal"",""value"":""On/Off Status""},
      ""ptSpec"": {""type"":""literal"",""value"":""Status""},
      ""ptType"": {""type"":""literal"",""value"":""On_Off_Status""},
      ""ptGw"": {""type"":""literal"",""value"":""GW-THX-001""} }
  ]}}");

        var point = await db.GetPoint("172_31_105_17-3002");

        Assert.NotNull(point);
        Assert.Equal("Status", point!.Specification);
        Assert.Equal("On_Off_Status", point.Type);
        Assert.Equal("GW-THX-001", point.GatewayName);
    }

    // Building reachability accepts either the spatial chain or the sbco:floor literal join, matching
    // the orphan definition settled in #291 Phase 1. Requiring only the spatial chain would leave
    // BuildingName null for every twin that models no Rooms — which this repository permits, and
    // which ListPointDetails itself depends on.
    [Fact]
    public async Task GetPointDetailByPointId_QueryAcceptsBothBuildingPaths()
    {
        var (db, handler) = BuildCapturing(@"{
  ""results"": { ""bindings"": [
    { ""ptDt"": {""type"":""uri"",""value"":""urn:dtid:pt1""},
      ""ptId"": {""type"":""literal"",""value"":""PT001""},
      ""ptName"": {""type"":""literal"",""value"":""Valve""} }
  ]}}");

        await db.GetPointDetailByPointId("PT001");

        var sparql = handler.LastRequestBody!;
        Assert.Contains("devBuilding", sparql);
        Assert.Contains("/Building>", sparql);
        Assert.Contains("UNION", sparql);
        // The sbco:floor literal join is the branch that survives a Room-less twin; without it the
        // UNION would be decorative and BuildingName would stay null for exactly the twins that
        // motivated this fix.
        Assert.Contains("/floor>", sparql);
    }

    [Fact]
    public async Task GetPointDetailByPointId_MapsBuildingNameOntoDevice()
    {
        // CapturingHttpHandler answers every request with the same payload, so these bindings serve
        // both the GetPoint lookup and the detail query that follows it.
        var db = BuildDb(@"{
  ""results"": { ""bindings"": [
    { ""ptDt"": {""type"":""uri"",""value"":""urn:dtid:pt1""},
      ""ptId"": {""type"":""literal"",""value"":""PT001""},
      ""ptName"": {""type"":""literal"",""value"":""Valve""},
      ""devDt"": {""type"":""uri"",""value"":""urn:dtid:dev1""},
      ""devId"": {""type"":""literal"",""value"":""AHU-1""},
      ""devName"": {""type"":""literal"",""value"":""AHU 1""},
      ""devBuilding"": {""type"":""literal"",""value"":""THX""} }
  ]}}");

        var detail = await db.GetPointDetailByPointId("PT001");

        Assert.NotNull(detail);
        Assert.Equal("THX", detail!.Device?.BuildingName);
    }

    // ListPointDetails is queried BY building, so the name is known without extra reachability
    // logic — there is no reason for the list to disagree with the detail.
    [Fact]
    public async Task ListPointDetails_ProjectsAndMapsBuildingName()
    {
        var (db, handler) = BuildCapturing(@"{
  ""results"": { ""bindings"": [
    { ""ptDt"": {""type"":""uri"",""value"":""urn:dtid:pt1""},
      ""ptId"": {""type"":""literal"",""value"":""PT001""},
      ""ptName"": {""type"":""literal"",""value"":""Valve""},
      ""devDt"": {""type"":""uri"",""value"":""urn:dtid:dev1""},
      ""devId"": {""type"":""literal"",""value"":""AHU-1""},
      ""devName"": {""type"":""literal"",""value"":""AHU 1""},
      ""devBuilding"": {""type"":""literal"",""value"":""THX""} }
  ]}}");

        var details = await db.ListPointDetails("urn:dtid:b1");

        Assert.Contains("devBuilding", handler.LastRequestBody!);
        Assert.Single(details);
        Assert.Equal("THX", details[0].Device?.BuildingName);
    }

    // The alarm/warn thresholds were projected by the device→points list only, so the same point
    // carried thresholds in one screen and null in another.
    [Fact]
    public async Task PointDetailPaths_ProjectAlarmThresholds()
    {
        foreach (var (name, act) in new (string, Func<OxiGraphDigitalTwinDatabase, Task>)[]
        {
            ("GetPoint", db => db.GetPoint("PT001")),
            ("ListPointDetails", db => db.ListPointDetails("urn:dtid:b1")),
        })
        {
            var (db, handler) = BuildCapturing(@"{ ""results"": { ""bindings"": [] } }");
            await act(db);

            var vars = ProjectedVariables(handler.LastRequestBody!);
            foreach (var v in new[] { "ptAlarmHigh", "ptAlarmLow", "ptWarnHigh", "ptWarnLow" })
                Assert.True(vars.Contains(v), $"{name} does not project {v} (#294)");
        }
    }
}
