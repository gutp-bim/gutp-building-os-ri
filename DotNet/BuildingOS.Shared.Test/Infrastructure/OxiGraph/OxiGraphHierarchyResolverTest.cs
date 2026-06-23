using System.Net.Http;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure.Authorization;
using BuildingOS.Shared.Infrastructure.OxiGraph;

namespace BuildingOS.Shared.Test.Infrastructure.OxiGraph;

public class OxiGraphHierarchyResolverTest
{
    private static OxiGraphHierarchyResolver BuildResolver(string responseJson)
    {
        var handler = new FakeHttpHandler(responseJson);
        var client = new OxiGraphClient(new HttpClient(handler), "http://oxigraph:7878");
        return new OxiGraphHierarchyResolver(client);
    }

    [Fact]
    public async Task GetAncestors_ForBuilding_ReturnsEmpty()
    {
        var resolver = BuildResolver(@"{ ""results"": { ""bindings"": [] } }");
        var ancestors = await resolver.GetAncestorsAsync("building", "ENG2");
        Assert.Empty(ancestors);
    }

    [Fact]
    public async Task GetAncestors_ForUnknownType_ReturnsEmpty()
    {
        var resolver = BuildResolver(@"{ ""results"": { ""bindings"": [] } }");
        var ancestors = await resolver.GetAncestorsAsync("unknown", "x");
        Assert.Empty(ancestors);
    }

    [Fact]
    public async Task GetAncestors_ForFloor_ReturnsBuildingAncestor()
    {
        const string json = @"{
  ""results"": { ""bindings"": [
    { ""buildingId"": {""type"":""literal"",""value"":""ENG2""} }
  ]}}";
        var resolver = BuildResolver(json);
        var ancestors = await resolver.GetAncestorsAsync("floor", "F1");
        Assert.Single(ancestors);
        Assert.Equal(("building", "ENG2"), ancestors[0]);
    }

    [Fact]
    public async Task GetAncestors_ForSpace_ReturnsBuildingAndFloor()
    {
        const string json = @"{
  ""results"": { ""bindings"": [
    { ""buildingId"": {""type"":""literal"",""value"":""ENG2""},
      ""floorId"": {""type"":""literal"",""value"":""F1""} }
  ]}}";
        var resolver = BuildResolver(json);
        var ancestors = await resolver.GetAncestorsAsync("space", "S1");
        Assert.Equal(2, ancestors.Count);
        Assert.Equal(("building", "ENG2"), ancestors[0]);
        Assert.Equal(("floor", "F1"), ancestors[1]);
    }

    [Fact]
    public async Task GetAncestors_ForDevice_ReturnsBuildingFloorSpace()
    {
        const string json = @"{
  ""results"": { ""bindings"": [
    { ""buildingId"": {""type"":""literal"",""value"":""ENG2""},
      ""floorId"": {""type"":""literal"",""value"":""F1""},
      ""spaceId"": {""type"":""literal"",""value"":""S1""} }
  ]}}";
        var resolver = BuildResolver(json);
        var ancestors = await resolver.GetAncestorsAsync("device", "DEV1");
        Assert.Equal(3, ancestors.Count);
        Assert.Equal("space", ancestors[2].ResourceType);
        Assert.Equal("S1", ancestors[2].ResourceId);
    }

    [Fact]
    public async Task GetAncestors_ForPoint_ReturnsFourAncestors()
    {
        // First call resolves point dtId, second call returns ancestors
        var callCount = 0;
        var responses = new[]
        {
            @"{ ""results"": { ""bindings"": [{ ""dt"": {""type"":""uri"",""value"":""urn:dtid:pt1""} }] } }",
            @"{ ""results"": { ""bindings"": [
                { ""buildingId"": {""type"":""literal"",""value"":""ENG2""},
                  ""floorId"": {""type"":""literal"",""value"":""F1""},
                  ""spaceId"": {""type"":""literal"",""value"":""S1""},
                  ""devId"": {""type"":""literal"",""value"":""DEV1""} }
              ] } }"
        };
        var handler = new MultiResponseFakeHandler(responses);
        var client = new OxiGraphClient(new HttpClient(handler), "http://oxigraph:7878");
        var resolver = new OxiGraphHierarchyResolver(client);

        var ancestors = await resolver.GetAncestorsAsync("point", "PT001");
        Assert.Equal(4, ancestors.Count);
        Assert.Equal("building", ancestors[0].ResourceType);
        Assert.Equal("device", ancestors[3].ResourceType);
    }

    [Fact]
    public async Task GetAncestors_ForPoint_ReturnsEmptyWhenPointNotFound()
    {
        var resolver = BuildResolver(@"{ ""results"": { ""bindings"": [] } }");
        var ancestors = await resolver.GetAncestorsAsync("point", "UNKNOWN");
        Assert.Empty(ancestors);
    }
}

internal sealed class MultiResponseFakeHandler : HttpMessageHandler
{
    private readonly string[] _responses;
    private int _index;

    public MultiResponseFakeHandler(string[] responses) => _responses = responses;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = _index < _responses.Length ? _responses[_index++] : @"{ ""results"": { ""bindings"": [] } }";
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/sparql-results+json")
        });
    }
}
