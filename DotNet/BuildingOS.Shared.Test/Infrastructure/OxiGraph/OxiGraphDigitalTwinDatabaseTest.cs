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
      ""devGw"": {""type"":""literal"",""value"":""dkapi-gw1""} }
  ]}}");

        var devices = await db.ListDevices("space-001");
        Assert.Single(devices);
        Assert.Equal("urn:dtid:dev1", devices[0].DtId);
        Assert.Equal("dkapi-gw1", devices[0].GatewayId);
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
