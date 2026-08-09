using System.Net;
using System.Net.Http;
using System.Text;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BuildingOS.Shared.Test.Infrastructure.OxiGraph;

public class OxiGraphDigitalTwinDatabaseTest
{
    private static OxiGraphDigitalTwinDatabase BuildDb(string responseJson)
    {
        var handler = new FakeHttpHandler(responseJson);
        var http = new HttpClient(handler);
        var client = new OxiGraphClient(http, "http://oxigraph:7878");
        var cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        return new OxiGraphDigitalTwinDatabase(client, cache);
    }

    [Fact]
    public async Task ListBuildings_ReturnsMappedBuildings()
    {
        var db = BuildDb(@"{
  ""results"": { ""bindings"": [
    { ""dt"": {""type"":""uri"",""value"":""urn:dtid:b1""},
      ""id"": {""type"":""literal"",""value"":""ENG2""},
      ""name"": {""type"":""literal"",""value"":""Eng Bldg 2""} }
  ]}}");

        var buildings = await db.ListBuildings();
        Assert.Single(buildings);
        Assert.Equal("urn:dtid:b1", buildings[0].DtId);
        Assert.Equal("ENG2", buildings[0].Id);
        Assert.Equal("Eng Bldg 2", buildings[0].Name);
    }

    [Fact]
    public async Task ListBuildings_ReturnsEmptyArrayWhenNoResults()
    {
        var db = BuildDb(@"{ ""results"": { ""bindings"": [] } }");
        var buildings = await db.ListBuildings();
        Assert.Empty(buildings);
    }

    [Fact]
    public async Task ListFloors_WithBuildingDtId_ReturnsFloors()
    {
        var db = BuildDb(@"{
  ""results"": { ""bindings"": [
    { ""dt"": {""type"":""uri"",""value"":""urn:dtid:f1""},
      ""id"": {""type"":""literal"",""value"":""F1""},
      ""name"": {""type"":""literal"",""value"":""1F""} }
  ]}}");

        var floors = await db.ListFloors("building-001");
        Assert.Single(floors);
        Assert.Equal("urn:dtid:f1", floors[0].DtId);
        Assert.Equal("1F", floors[0].Name);
    }

    [Fact]
    public async Task ListDevices_ReturnsMappedDevices()
    {
        var db = BuildDb(@"{
  ""results"": { ""bindings"": [
    { ""devDt"": {""type"":""uri"",""value"":""urn:dtid:dev1""},
      ""devId"": {""type"":""literal"",""value"":""DEV1""},
      ""devName"": {""type"":""literal"",""value"":""AC Unit""},
      ""devGw"": {""type"":""literal"",""value"":""gw-001""} }
  ]}}");

        var devices = await db.ListDevices("space-001");
        Assert.Single(devices);
        Assert.Equal("urn:dtid:dev1", devices[0].DtId);
        Assert.Equal("gw-001", devices[0].GatewayId);
    }

    [Fact]
    public async Task ListPoints_ReturnsMappedPoints_WithWritable()
    {
        var db = BuildDb(@"{
  ""results"": { ""bindings"": [
    { ""ptDt"": {""type"":""uri"",""value"":""urn:dtid:pt1""},
      ""ptId"": {""type"":""literal"",""value"":""PT001""},
      ""ptName"": {""type"":""literal"",""value"":""Temp Sensor""},
      ""ptWritable"": {""type"":""literal"",""value"":""true""} }
  ]}}");

        var points = await db.ListPoints("dev-001");
        Assert.Single(points);
        Assert.Equal("PT001", points[0].Id);
        Assert.True(points[0].Writable);
        // BACnet-specific properties (ObjectTypeBacnet etc.) are not present in SBCO TTL.
    }

    [Fact]
    public async Task GetBuilding_ReturnsNullWhenNotFound()
    {
        var db = BuildDb(@"{ ""results"": { ""bindings"": [] } }");
        var result = await db.GetBuilding("unknown");
        Assert.Null(result);
    }

    // Regression for the M7 writable gate: GetPoint must SELECT ?ptWritable, otherwise
    // MapPoint leaves Writable=null and CanWritePointAsync (point.Writable == false)
    // never blocks — letting admins control writable=false points.
    [Fact]
    public async Task GetPoint_QueryRequestsWritable()
    {
        var handler = new CapturingHttpHandler(@"{ ""results"": { ""bindings"": [] } }");
        var http = new HttpClient(handler);
        var client = new OxiGraphClient(http, "http://oxigraph:7878");
        var cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        var db = new OxiGraphDigitalTwinDatabase(client, cache);

        await db.GetPoint("PT001");

        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("ptWritable", handler.LastRequestBody!);
    }

    // ── SELECT-omission regressions (#294 / #298) ─────────────────────────────
    //
    // Every point read path is hand-written SPARQL, and each time one was edited alone the others
    // drifted: the same point came back with different metadata depending on which endpoint served
    // it, and MapPoint's GetValueOrDefault turned the omission into a silent null rather than an
    // error. These assert by predicate that the shared projection reaches all three paths, so
    // wiring a predicate in one place and forgetting the rest fails here instead of in the UI.

    // Matching on the bare local name would not actually test anything for several of these: the
    // SPARQL also names variables after them, so `body.Contains("site")` is satisfied by the
    // surviving `?siteOwn` / `?siteRaw` even when the triple pattern is gone — the exact deletion
    // these tests exist to catch. Assert on the full predicate IRI as it is written into the query.
    private const string SbcoNs = "https://www.sbco.or.jp/ont/";

    /// <summary>Point predicates every point read path is required to SELECT.</summary>
    private static readonly string[] WiredPointPredicates =
    {
        SbcoNs + "writable", SbcoNs + "pointSpecification", SbcoNs + "pointType",
        SbcoNs + "gatewayId", SbcoNs + "interval",
        SbcoNs + "deviceIdBacnet", SbcoNs + "objectTypeBacnet", SbcoNs + "instanceNoBacnet",
        // #298: present in the seeds all along, but no query asked for them.
        SbcoNs + "unit", SbcoNs + "scale", SbcoNs + "targetArea", SbcoNs + "installationArea",
        SbcoNs + "minPresValue", SbcoNs + "maxPresValue",
    };

    /// <summary>Equipment predicates every device read path is required to SELECT (#298).</summary>
    private static readonly string[] WiredDevicePredicates =
    {
        SbcoNs + "deviceType", SbcoNs + "supplier", SbcoNs + "owner", SbcoNs + "site",
    };

    private static (OxiGraphDigitalTwinDatabase Db, CapturingHttpHandler Handler) BuildCapturingDb()
    {
        var handler = new CapturingHttpHandler(@"{ ""results"": { ""bindings"": [] } }");
        var client = new OxiGraphClient(new HttpClient(handler), "http://oxigraph:7878");
        var cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        return (new OxiGraphDigitalTwinDatabase(client, cache), handler);
    }

    private static void AssertQueryRequests(string? body, string[] predicates, string readPath)
    {
        Assert.NotNull(body);
        // Angle brackets included: the IRI must appear as a predicate position, not merely as a
        // substring of some longer IRI.
        var missing = predicates.Where(p => !body!.Contains($"<{p}>")).ToArray();
        Assert.True(
            missing.Length == 0,
            $"{readPath} does not SELECT: {string.Join(", ", missing)}");
    }

    [Fact]
    public async Task GetPoint_QueryRequestsEveryWiredPointPredicate()
    {
        var (db, handler) = BuildCapturingDb();
        await db.GetPoint("PT001");
        AssertQueryRequests(handler.LastRequestBody, WiredPointPredicates, "GetPoint");
    }

    [Fact]
    public async Task ListPoints_QueryRequestsEveryWiredPointPredicate()
    {
        var (db, handler) = BuildCapturingDb();
        await db.ListPoints("urn:dtid:dev1");
        AssertQueryRequests(handler.LastRequestBody, WiredPointPredicates, "ListPoints(device)");
    }

    [Fact]
    public async Task ListPointsUnfiltered_QueryRequestsEveryWiredPointPredicate()
    {
        var (db, handler) = BuildCapturingDb();
        await db.ListPoints(null);
        AssertQueryRequests(handler.LastRequestBody, WiredPointPredicates, "ListPoints(all)");
    }

    [Fact]
    public async Task ListPointDetails_QueryRequestsEveryWiredPointPredicate()
    {
        var (db, handler) = BuildCapturingDb();
        await db.ListPointDetails("urn:dtid:b1");
        AssertQueryRequests(handler.LastRequestBody, WiredPointPredicates, "ListPointDetails");
    }

    [Fact]
    public async Task GetDevice_QueryRequestsEveryWiredDevicePredicate()
    {
        var (db, handler) = BuildCapturingDb();
        await db.GetDevice("urn:dtid:dev1");
        AssertQueryRequests(handler.LastRequestBody, WiredDevicePredicates, "GetDevice");
    }

    [Fact]
    public async Task ListDevices_QueryRequestsEveryWiredDevicePredicate()
    {
        var (db, handler) = BuildCapturingDb();
        await db.ListDevices("urn:dtid:space1");
        AssertQueryRequests(handler.LastRequestBody, WiredDevicePredicates, "ListDevices(space)");
    }

    [Fact]
    public async Task ListDevicesUnfiltered_QueryRequestsEveryWiredDevicePredicate()
    {
        var (db, handler) = BuildCapturingDb();
        await db.ListDevices(null);
        AssertQueryRequests(handler.LastRequestBody, WiredDevicePredicates, "ListDevices(all)");
    }

    [Fact]
    public async Task ListDeviceDetails_QueryRequestsEveryWiredDevicePredicate()
    {
        var (db, handler) = BuildCapturingDb();
        await db.ListDeviceDetails("urn:dtid:b1");
        AssertQueryRequests(handler.LastRequestBody, WiredDevicePredicates, "ListDeviceDetails");
    }

    [Fact]
    public async Task GetPoint_MapsDescriptiveMetadata()
    {
        // The values are exactly those of PT005 in Fixtures/SeedData/sbco-sample.ttl — the seed has
        // carried them since it was written, while every API response reported them as null (#298).
        var db = BuildDb(@"{
  ""results"": { ""bindings"": [
    { ""ptDt"": {""type"":""uri"",""value"":""urn:dtid:pt5""},
      ""ptId"": {""type"":""literal"",""value"":""PT005""},
      ""ptName"": {""type"":""literal"",""value"":""Room 201 Temperature""},
      ""ptUnit"": {""type"":""literal"",""value"":""C""},
      ""ptScale"": {""type"":""literal"",""value"":""1.0""},
      ""ptTargetArea"": {""type"":""literal"",""value"":""Room 201""},
      ""ptInstallArea"": {""type"":""literal"",""value"":""Room 201""},
      ""ptMinPres"": {""type"":""literal"",""value"":""-10""},
      ""ptMaxPres"": {""type"":""literal"",""value"":""50""} }
  ]}}");

        var point = await db.GetPoint("PT005");

        Assert.NotNull(point);
        Assert.Equal("C", point!.Unit);
        Assert.Equal(1.0f, point.Scale);
        Assert.Equal("Room 201", point.TargetArea);
        Assert.Equal("Room 201", point.InstallationArea);
        Assert.Equal(-10, point.MinPresValue);
        Assert.Equal(50, point.MaxPresValue);
    }

    [Fact]
    public async Task ListPoints_MapsDescriptiveMetadata()
    {
        var db = BuildDb(@"{
  ""results"": { ""bindings"": [
    { ""ptDt"": {""type"":""uri"",""value"":""urn:dtid:pt5""},
      ""ptId"": {""type"":""literal"",""value"":""PT005""},
      ""ptName"": {""type"":""literal"",""value"":""Room 201 Temperature""},
      ""ptScale"": {""type"":""literal"",""value"":""0.1""},
      ""ptTargetArea"": {""type"":""literal"",""value"":""Room 201""} }
  ]}}");

        var points = await db.ListPoints("urn:dtid:dev4");

        Assert.Equal(0.1f, points[0].Scale);
        Assert.Equal("Room 201", points[0].TargetArea);
    }

    [Fact]
    public async Task ListDevices_MapsDescriptiveMetadata()
    {
        var db = BuildDb(@"{
  ""results"": { ""bindings"": [
    { ""devDt"": {""type"":""uri"",""value"":""urn:dtid:dev4""},
      ""devId"": {""type"":""literal"",""value"":""DEV004""},
      ""devName"": {""type"":""literal"",""value"":""Temperature Sensor 02""},
      ""devType"": {""type"":""literal"",""value"":""Sensor""},
      ""devSupplier"": {""type"":""literal"",""value"":""VendorA""},
      ""devOwner"": {""type"":""literal"",""value"":""Building Management""},
      ""devSite"": {""type"":""literal"",""value"":""site-1""} }
  ]}}");

        var devices = await db.ListDevices("urn:dtid:space1");

        Assert.Equal("Sensor", devices[0].DeviceType);
        Assert.Equal("VendorA", devices[0].Supplier);
        Assert.Equal("Building Management", devices[0].Owner);
        Assert.Equal("site-1", devices[0].Site);
    }

    [Fact]
    public async Task GetDevice_MapsDescriptiveMetadata_FromFirstBoundRow()
    {
        // GetDevice is not a GROUP BY query, so the descriptive attributes arrive per row and the
        // first row may leave them unbound (the identifiers/customTags OPTIONALs fan out first).
        var db = BuildDb(@"{
  ""results"": { ""bindings"": [
    { ""devId"": {""type"":""literal"",""value"":""DEV004""},
      ""devName"": {""type"":""literal"",""value"":""Temperature Sensor 02""} },
    { ""devId"": {""type"":""literal"",""value"":""DEV004""},
      ""devName"": {""type"":""literal"",""value"":""Temperature Sensor 02""},
      ""devTypeRaw"": {""type"":""literal"",""value"":""Sensor""},
      ""supplierRaw"": {""type"":""literal"",""value"":""VendorA""},
      ""ownerRaw"": {""type"":""literal"",""value"":""Building Management""},
      ""siteRaw"": {""type"":""literal"",""value"":""site-1""} }
  ]}}");

        var device = await db.GetDevice("urn:dtid:dev4");

        Assert.NotNull(device);
        Assert.Equal("Sensor", device!.DeviceType);
        Assert.Equal("VendorA", device.Supplier);
        Assert.Equal("Building Management", device.Owner);
        Assert.Equal("site-1", device.Site);
    }

    [Fact]
    public async Task GetDevice_LeavesDescriptiveMetadataNull_WhenTwinHasNone()
    {
        var db = BuildDb(@"{
  ""results"": { ""bindings"": [
    { ""devId"": {""type"":""literal"",""value"":""DEV1""},
      ""devName"": {""type"":""literal"",""value"":""AC Unit""} }
  ]}}");

        var device = await db.GetDevice("urn:dtid:dev1");

        Assert.NotNull(device);
        Assert.Null(device!.DeviceType);
        Assert.Null(device.Supplier);
        Assert.Null(device.Owner);
        Assert.Null(device.Site);
    }

    [Fact]
    public async Task GetPoint_MapsWritableFalse()
    {
        var db = BuildDb(@"{
  ""results"": { ""bindings"": [
    { ""ptDt"": {""type"":""uri"",""value"":""urn:dtid:pt1""},
      ""ptId"": {""type"":""literal"",""value"":""PT001""},
      ""ptName"": {""type"":""literal"",""value"":""Valve""},
      ""ptWritable"": {""type"":""literal"",""value"":""false""} }
  ]}}");

        var point = await db.GetPoint("PT001");

        Assert.NotNull(point);
        Assert.False(point!.Writable);
    }

    [Fact]
    public async Task GetPoint_MapsBacnetNativeFields()
    {
        var db = BuildDb(@"{
  ""results"": { ""bindings"": [
    { ""ptDt"": {""type"":""uri"",""value"":""urn:dtid:pt1""},
      ""ptId"": {""type"":""literal"",""value"":""PT001""},
      ""ptName"": {""type"":""literal"",""value"":""Lighting""},
      ""devIdBac"": {""type"":""literal"",""value"":""BAC-2""},
      ""objType"": {""type"":""literal"",""value"":""binaryOutput""},
      ""instNo"": {""type"":""literal"",""value"":""2001""} }
  ]}}");

        var point = await db.GetPoint("PT001");

        Assert.NotNull(point);
        Assert.Equal("BAC-2", point!.DeviceIdBacnet);
        Assert.Equal("binaryOutput", point.ObjectTypeBacnet);
        Assert.Equal(2001, point.InstanceNoBacnet);
    }

    // ── Metadata read (identifiers / customTags) ──────────────────────────────

    [Fact]
    public async Task GetDevice_ReturnsIdentifiers_WhenPresentInOxiGraph()
    {
        var db = BuildDb(@"{
  ""results"": { ""bindings"": [
    { ""devId"": {""type"":""literal"",""value"":""DEV1""},
      ""devName"": {""type"":""literal"",""value"":""AC Unit""},
      ""identKey"": {""type"":""literal"",""value"":""ifcGuid""},
      ""identVal"": {""type"":""literal"",""value"":""3Skg8nAD1AJAiNfIxGkWjF""} }
  ]}}");

        var device = await db.GetDevice("urn:dtid:dev1");

        Assert.NotNull(device);
        Assert.Equal("3Skg8nAD1AJAiNfIxGkWjF", device!.Identifiers["ifcGuid"]);
    }

    [Fact]
    public async Task GetDevice_ReturnsCustomTags_WhenPresentInOxiGraph()
    {
        var db = BuildDb(@"{
  ""results"": { ""bindings"": [
    { ""devId"": {""type"":""literal"",""value"":""DEV1""},
      ""devName"": {""type"":""literal"",""value"":""AC Unit""},
      ""tagKey"": {""type"":""literal"",""value"":""geometryMapped""},
      ""tagBoolVal"": {""type"":""literal"",""value"":""true""} }
  ]}}");

        var device = await db.GetDevice("urn:dtid:dev1");

        Assert.NotNull(device);
        Assert.True(device!.CustomTags["geometryMapped"]);
    }

    [Fact]
    public async Task GetDevice_ReturnsEmptyMetadata_WhenNoneInOxiGraph()
    {
        var db = BuildDb(@"{
  ""results"": { ""bindings"": [
    { ""devId"": {""type"":""literal"",""value"":""DEV1""},
      ""devName"": {""type"":""literal"",""value"":""AC Unit""} }
  ]}}");

        var device = await db.GetDevice("urn:dtid:dev1");

        Assert.NotNull(device);
        Assert.Empty(device!.Identifiers);
        Assert.Empty(device.CustomTags);
    }

    [Fact]
    public async Task GetPoint_ReturnsIdentifiers_WhenPresentInOxiGraph()
    {
        var db = BuildDb(@"{
  ""results"": { ""bindings"": [
    { ""ptDt"": {""type"":""uri"",""value"":""urn:dtid:pt1""},
      ""ptId"": {""type"":""literal"",""value"":""PT001""},
      ""ptName"": {""type"":""literal"",""value"":""Temp Sensor""},
      ""identKey"": {""type"":""literal"",""value"":""ifcGuid""},
      ""identVal"": {""type"":""literal"",""value"":""ABCDEF""} }
  ]}}");

        var point = await db.GetPoint("PT001");

        Assert.NotNull(point);
        Assert.Equal("ABCDEF", point!.Identifiers["ifcGuid"]);
    }

    [Fact]
    public async Task GetBuilding_ReturnsIdentifiers_WhenPresentInOxiGraph()
    {
        var db = BuildDb(@"{
  ""results"": { ""bindings"": [
    { ""id"": {""type"":""literal"",""value"":""B1""},
      ""name"": {""type"":""literal"",""value"":""Bldg 1""},
      ""identKey"": {""type"":""literal"",""value"":""ifcGuid""},
      ""identVal"": {""type"":""literal"",""value"":""BLDG-GUID""} }
  ]}}");

        var building = await db.GetBuilding("urn:dtid:b1");

        Assert.NotNull(building);
        Assert.Equal("BLDG-GUID", building!.Identifiers["ifcGuid"]);
    }

    [Fact]
    public async Task GetFloor_ReturnsIdentifiers_WhenPresentInOxiGraph()
    {
        var db = BuildDb(@"{
  ""results"": { ""bindings"": [
    { ""id"": {""type"":""literal"",""value"":""F1""},
      ""name"": {""type"":""literal"",""value"":""1F""},
      ""identKey"": {""type"":""literal"",""value"":""ifcGuid""},
      ""identVal"": {""type"":""literal"",""value"":""FLOOR-GUID""} }
  ]}}");

        var floor = await db.GetFloor("urn:dtid:f1");

        Assert.NotNull(floor);
        Assert.Equal("FLOOR-GUID", floor!.Identifiers["ifcGuid"]);
    }

    [Fact]
    public async Task GetSpace_ReturnsIdentifiers_WhenPresentInOxiGraph()
    {
        var db = BuildDb(@"{
  ""results"": { ""bindings"": [
    { ""id"": {""type"":""literal"",""value"":""S1""},
      ""name"": {""type"":""literal"",""value"":""Room 101""},
      ""identKey"": {""type"":""literal"",""value"":""ifcGuid""},
      ""identVal"": {""type"":""literal"",""value"":""SPACE-GUID""} }
  ]}}");

        var space = await db.GetSpace("urn:dtid:s1");

        Assert.NotNull(space);
        Assert.Equal("SPACE-GUID", space!.Identifiers["ifcGuid"]);
    }

    // ── Metadata write (UpdateResourceMetadataAsync) ──────────────────────────

    [Fact]
    public async Task UpdateResourceMetadataAsync_SendsDeleteAndInsert_ForIdentifierUpsert()
    {
        var handler = new CapturingHttpHandler("", System.Net.HttpStatusCode.NoContent);
        var http = new HttpClient(handler);
        var client = new OxiGraphClient(http, "http://oxigraph:7878");
        var cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        var db = new OxiGraphDigitalTwinDatabase(client, cache);

        await db.UpdateResourceMetadataAsync(
            "urn:dtid:dev1",
            new Dictionary<string, string?> { ["ifcGuid"] = "3Skg8nAD1AJAiNfIxGkWjF" },
            null,
            CancellationToken.None);

        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("ifcGuid", handler.LastRequestBody!);
        Assert.Contains("3Skg8nAD1AJAiNfIxGkWjF", handler.LastRequestBody);
        Assert.Contains("DELETE", handler.LastRequestBody);
        Assert.Contains("INSERT", handler.LastRequestBody);
    }

    [Fact]
    public async Task UpdateResourceMetadataAsync_SendsDeleteOnly_ForNullValue()
    {
        var handler = new CapturingHttpHandler("", System.Net.HttpStatusCode.NoContent);
        var http = new HttpClient(handler);
        var client = new OxiGraphClient(http, "http://oxigraph:7878");
        var cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        var db = new OxiGraphDigitalTwinDatabase(client, cache);

        await db.UpdateResourceMetadataAsync(
            "urn:dtid:dev1",
            new Dictionary<string, string?> { ["ifcGuid"] = null },
            null,
            CancellationToken.None);

        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("ifcGuid", handler.LastRequestBody!);
        Assert.Contains("DELETE", handler.LastRequestBody);
        Assert.DoesNotContain("INSERT", handler.LastRequestBody);
    }

    [Fact]
    public async Task UpdateResourceMetadataAsync_SendsCustomTagUpdate()
    {
        var handler = new CapturingHttpHandler("", System.Net.HttpStatusCode.NoContent);
        var http = new HttpClient(handler);
        var client = new OxiGraphClient(http, "http://oxigraph:7878");
        var cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        var db = new OxiGraphDigitalTwinDatabase(client, cache);

        await db.UpdateResourceMetadataAsync(
            "urn:dtid:dev1",
            null,
            new Dictionary<string, bool?> { ["geometryMapped"] = true },
            CancellationToken.None);

        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("geometryMapped", handler.LastRequestBody!);
        Assert.Contains("true", handler.LastRequestBody);
    }

    [Fact]
    public async Task UpdateResourceMetadataAsync_DoesNothing_WhenBothMapsAreNull()
    {
        var handler = new CapturingHttpHandler("", System.Net.HttpStatusCode.NoContent);
        var http = new HttpClient(handler);
        var client = new OxiGraphClient(http, "http://oxigraph:7878");
        var cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        var db = new OxiGraphDigitalTwinDatabase(client, cache);

        await db.UpdateResourceMetadataAsync("urn:dtid:dev1", null, null, CancellationToken.None);

        Assert.Null(handler.LastRequestBody);
    }
}
