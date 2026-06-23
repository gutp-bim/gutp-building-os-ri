using System.Net;
using System.Net.Http;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BuildingOS.Shared.Test.Infrastructure.OxiGraph;

/// <summary>
/// Tests for the gateway-scoped point list export (#224 / U1): twin → GatewayPointEntry mapping
/// including native addressing (localId / BACnet), control schema, and device grouping.
/// </summary>
public class ListGatewayPointListTest
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
    public async Task ListGatewayPointList_MapsNativeAddressingControlSchemaAndDevice()
    {
        var db = BuildDb(@"{
  ""results"": { ""bindings"": [
    { ""ptId"": {""type"":""literal"",""value"":""PT001""},
      ""ptName"": {""type"":""literal"",""value"":""Room Temp""},
      ""localId"": {""type"":""literal"",""value"":""LOCAL001""},
      ""devIdBac"": {""type"":""literal"",""value"":""BAC001""},
      ""objType"": {""type"":""literal"",""value"":""Analog-Input""},
      ""instNo"": {""type"":""literal"",""value"":""1001""},
      ""unit"": {""type"":""literal"",""value"":""C""},
      ""writable"": {""type"":""literal"",""value"":""true""},
      ""dataType"": {""type"":""literal"",""value"":""number""},
      ""minV"": {""type"":""literal"",""value"":""16""},
      ""maxV"": {""type"":""literal"",""value"":""30""},
      ""devDt"": {""type"":""uri"",""value"":""urn:dtid:dev1""},
      ""devId"": {""type"":""literal"",""value"":""DEV1""},
      ""devName"": {""type"":""literal"",""value"":""AC-1""} }
  ]}}");

        var entries = await db.ListGatewayPointList("GW001");

        Assert.Single(entries);
        var e = entries[0];
        Assert.Equal("PT001", e.PointId);
        Assert.Equal("LOCAL001", e.LocalId);
        Assert.Equal("BAC001", e.BacnetDeviceId);
        Assert.Equal("Analog-Input", e.BacnetObjectType);
        Assert.Equal("1001", e.BacnetInstanceNo);
        Assert.Equal("C", e.Unit);
        Assert.True(e.Writable);
        Assert.Equal("number", e.DataType);
        Assert.Equal("16", e.MinValue);
        Assert.Equal("30", e.MaxValue);
        Assert.Equal("urn:dtid:dev1", e.DeviceDtId);
        Assert.Equal("DEV1", e.DeviceId);
        Assert.Equal("AC-1", e.DeviceName);
    }

    [Fact]
    public async Task ListGatewayPointList_ReturnsPointId_EvenWhenNativeAddressingMissing()
    {
        // A point with no localId/BACnet/control-schema must still appear (just with nulls).
        var db = BuildDb(@"{
  ""results"": { ""bindings"": [
    { ""ptId"": {""type"":""literal"",""value"":""PT002""},
      ""ptName"": {""type"":""literal"",""value"":""CO2""} }
  ]}}");

        var entries = await db.ListGatewayPointList("GW001");

        Assert.Single(entries);
        Assert.Equal("PT002", entries[0].PointId);
        Assert.Null(entries[0].LocalId);
        Assert.Null(entries[0].BacnetObjectType);
        Assert.Null(entries[0].Writable);
    }

    [Fact]
    public async Task ListGatewayPointList_ReturnsEmpty_WhenGatewayOwnsNoPoints()
    {
        var db = BuildDb(@"{ ""results"": { ""bindings"": [] } }");
        var entries = await db.ListGatewayPointList("GW404");
        Assert.Empty(entries);
    }

    [Fact]
    public async Task ListGatewayPointList_ParsesWritableFalse()
    {
        var db = BuildDb(@"{
  ""results"": { ""bindings"": [
    { ""ptId"": {""type"":""literal"",""value"":""PT003""},
      ""writable"": {""type"":""literal"",""value"":""false""} }
  ]}}");

        var entries = await db.ListGatewayPointList("GW001");
        Assert.False(entries[0].Writable);
    }
}
