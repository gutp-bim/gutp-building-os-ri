using System.Net;
using System.Net.Http;
using System.Text;
using BuildingOS.Shared.Infrastructure.OxiGraph;

namespace BuildingOS.Shared.Test.Infrastructure.OxiGraph;

public class OxiGraphClientTest
{
    private static OxiGraphClient BuildClient(string responseBody, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new FakeHttpHandler(responseBody, status);
        var http = new HttpClient(handler);
        return new OxiGraphClient(http, "http://oxigraph:7878");
    }

    [Fact]
    public async Task QueryAsync_ParsesSingleBinding()
    {
        const string json = @"{
  ""results"": {
    ""bindings"": [
      { ""dt"": { ""type"": ""uri"", ""value"": ""urn:dtid:b1"" },
        ""id"": { ""type"": ""literal"", ""value"": ""ENG2"" },
        ""name"": { ""type"": ""literal"", ""value"": ""Eng Bldg 2"" } }
    ]
  }
}";
        var client = BuildClient(json);
        var rows = await client.QueryAsync("SELECT * WHERE { ?dt a <x:Building> }");

        Assert.Single(rows);
        Assert.Equal("urn:dtid:b1", rows[0]["dt"]);
        Assert.Equal("ENG2", rows[0]["id"]);
        Assert.Equal("Eng Bldg 2", rows[0]["name"]);
    }

    [Fact]
    public async Task QueryAsync_ReturnsEmptyListForNoBindings()
    {
        const string json = @"{ ""results"": { ""bindings"": [] } }";
        var client = BuildClient(json);
        var rows = await client.QueryAsync("SELECT * WHERE { ?x a <x:Missing> }");
        Assert.Empty(rows);
    }

    [Fact]
    public async Task QueryAsync_ThrowsOnNonSuccessStatus()
    {
        var client = BuildClient("Server error", HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.QueryAsync("SELECT * WHERE { ?x a <x:Foo> }"));
    }

    [Fact]
    public async Task QueryAsync_HandlesMultipleBindings()
    {
        const string json = @"{
  ""results"": {
    ""bindings"": [
      { ""dt"": { ""type"": ""uri"", ""value"": ""urn:dtid:b1"" }, ""id"": { ""type"": ""literal"", ""value"": ""B1"" }, ""name"": { ""type"": ""literal"", ""value"": ""N1"" } },
      { ""dt"": { ""type"": ""uri"", ""value"": ""urn:dtid:b2"" }, ""id"": { ""type"": ""literal"", ""value"": ""B2"" }, ""name"": { ""type"": ""literal"", ""value"": ""N2"" } }
    ]
  }
}";
        var client = BuildClient(json);
        var rows = await client.QueryAsync("SELECT * WHERE { ?dt a <x:Building> }");
        Assert.Equal(2, rows.Count);
        Assert.Equal("urn:dtid:b2", rows[1]["dt"]);
    }

    [Fact]
    public async Task UpdateAsync_SucceedsOn204()
    {
        var client = BuildClient("", HttpStatusCode.NoContent);
        await client.UpdateAsync("INSERT DATA { <urn:x> a <urn:Thing> . }");
    }

    [Fact]
    public async Task ImportTurtleAsync_SendsPostToStoreWithTurtleContentType()
    {
        var handler = new CapturingHttpHandler("", HttpStatusCode.NoContent);
        var http = new HttpClient(handler);
        var client = new OxiGraphClient(http, "http://oxigraph:7878");

        const string turtle = "<urn:x> a <urn:Thing> .";
        await client.ImportTurtleAsync(turtle);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.EndsWith("/store", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal("text/turtle", handler.LastRequest.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(turtle, handler.LastRequestBody);
    }

    [Fact]
    public async Task ImportTurtleAsync_ThrowsOnNonSuccessStatus()
    {
        var client = BuildClient("error", HttpStatusCode.BadRequest);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.ImportTurtleAsync("<urn:x> a <urn:Thing> ."));
    }

    [Fact]
    public async Task ImportTurtleAsync_SucceedsOn200()
    {
        var client = BuildClient("", HttpStatusCode.OK);
        await client.ImportTurtleAsync("<urn:x> a <urn:Thing> .");
    }
}

internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly string _body;
    private readonly HttpStatusCode _status;

    public FakeHttpHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _body = body;
        _status = status;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Task.FromResult(new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/sparql-results+json")
        });
}

internal sealed class CapturingHttpHandler : HttpMessageHandler
{
    private readonly string _responseBody;
    private readonly HttpStatusCode _status;

    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    public CapturingHttpHandler(string responseBody, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responseBody = responseBody;
        _status = status;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        LastRequestBody = request.Content is not null
            ? await request.Content.ReadAsStringAsync(ct)
            : null;
        return new HttpResponseMessage(_status)
        {
            Content = new StringContent(_responseBody, Encoding.UTF8, "text/plain")
        };
    }
}
