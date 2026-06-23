using System.Net;
using System.Net.Http;
using System.Text;
using System.Web;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using BuildingOS.Shared.Module.Oss;
using BuildingOS.Shared.Test.Infrastructure.OxiGraph;

namespace BuildingOS.Shared.Test.Module;

public class OxiGraphPointIdDataSourceTest
{
    private static OxiGraphPointIdDataSource BuildDataSource(string sparqlResponseJson, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new FakeHttpHandler(sparqlResponseJson, status);
        var http = new HttpClient(handler);
        var client = new OxiGraphClient(http, "http://oxigraph:7878");
        return new OxiGraphPointIdDataSource(client);
    }

    [Fact]
    public async Task GetPointIdInfosAsync_ReturnsEmptyArrayWhenNoBindings()
    {
        const string json = @"{ ""results"": { ""bindings"": [] } }";
        var source = BuildDataSource(json);

        var result = await source.GetPointIdInfosAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetPointIdInfosAsync_ReturnsMultiplePointIdsForSameLocalId()
    {
        const string json = @"{
  ""results"": {
    ""bindings"": [
      { ""localId"": { ""type"": ""literal"", ""value"": ""CO2006"" },
        ""pointId"": { ""type"": ""literal"", ""value"": ""Temperature_0006"" } },
      { ""localId"": { ""type"": ""literal"", ""value"": ""CO2006"" },
        ""pointId"": { ""type"": ""literal"", ""value"": ""Humidity_0006"" } }
    ]
  }
}";
        var source = BuildDataSource(json);

        var result = await source.GetPointIdInfosAsync();

        Assert.Equal(2, result.Length);
        Assert.All(result, r => Assert.Equal("CO2006", r.Key));
        Assert.Contains(result, r => r.PointId == "Temperature_0006");
        Assert.Contains(result, r => r.PointId == "Humidity_0006");
    }

    [Fact]
    public async Task GetPointIdInfosAsync_ReturnsSingleMapping()
    {
        const string json = @"{
  ""results"": {
    ""bindings"": [
      { ""localId"": { ""type"": ""literal"", ""value"": ""LOCAL001"" },
        ""pointId"": { ""type"": ""literal"", ""value"": ""PT001"" } }
    ]
  }
}";
        var source = BuildDataSource(json);

        var result = await source.GetPointIdInfosAsync();

        Assert.Single(result);
        Assert.Equal("LOCAL001", result[0].Key);
        Assert.Equal("PT001", result[0].PointId);
    }

    [Fact]
    public async Task GetPointIdInfosAsync_QueryContainsRequiredSparqlTerms()
    {
        var handler = new CapturingPointIdHttpHandler(@"{ ""results"": { ""bindings"": [] } }", HttpStatusCode.OK);
        var http = new HttpClient(handler);
        var client = new OxiGraphClient(http, "http://oxigraph:7878");
        var source = new OxiGraphPointIdDataSource(client);

        await source.GetPointIdInfosAsync();

        var body = HttpUtility.UrlDecode(handler.LastRequestBody ?? string.Empty);
        Assert.Contains("sbco:PointExt", body);
        Assert.Contains("sbco:localId", body);
        Assert.Contains("sbco:id", body);
    }
}

internal sealed class CapturingPointIdHttpHandler : HttpMessageHandler
{
    private readonly string _responseBody;
    private readonly HttpStatusCode _status;

    public string? LastRequestBody { get; private set; }

    public CapturingPointIdHttpHandler(string responseBody, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responseBody = responseBody;
        _status = status;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequestBody = request.Content is not null
            ? await request.Content.ReadAsStringAsync(ct)
            : null;
        return new HttpResponseMessage(_status)
        {
            Content = new System.Net.Http.StringContent(_responseBody, Encoding.UTF8, "application/sparql-results+json")
        };
    }
}
