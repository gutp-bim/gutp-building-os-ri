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
}
