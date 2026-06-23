using System.Net;
using System.Net.Http;
using System.Text;
using BuildingOS.Shared.Infrastructure.Monitoring;

namespace BuildingOS.Shared.Test.Infrastructure.Monitoring;

public class PrometheusQueryClientTest
{
    private static PrometheusQueryClient BuildClient(string responseJson, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new JsonHttpHandler(responseJson, status);
        var http = new HttpClient(handler);
        return new PrometheusQueryClient(http, "http://prometheus:9090");
    }

    [Fact]
    public void IsConfigured_FalseWhenBaseUrlEmpty()
    {
        var client = new PrometheusQueryClient(new HttpClient(), null);
        Assert.False(client.IsConfigured);
    }

    [Fact]
    public void IsConfigured_TrueWhenBaseUrlProvided()
    {
        var client = new PrometheusQueryClient(new HttpClient(), "http://prometheus:9090");
        Assert.True(client.IsConfigured);
    }

    [Fact]
    public async Task QueryScalarAsync_ReturnsNull_WhenNotConfigured()
    {
        // Must not hit the network at all when unconfigured (graceful degrade).
        var handler = new ThrowingHttpHandler();
        var client = new PrometheusQueryClient(new HttpClient(handler), "");
        var value = await client.QueryScalarAsync("up", CancellationToken.None);
        Assert.Null(value);
    }

    [Fact]
    public async Task QueryScalarAsync_ReturnsFirstSampleValue_FromVector()
    {
        const string json = @"{
  ""status"": ""success"",
  ""data"": { ""resultType"": ""vector"", ""result"": [
    { ""metric"": { ""__name__"": ""x"" }, ""value"": [ 1717689600, ""1240.5"" ] }
  ]}}";
        var client = BuildClient(json);
        var value = await client.QueryScalarAsync("sum(rate(x[1m]))", CancellationToken.None);
        Assert.Equal(1240.5, value);
    }

    [Fact]
    public async Task QueryScalarAsync_ParsesScalarResultType()
    {
        const string json = @"{
  ""status"": ""success"",
  ""data"": { ""resultType"": ""scalar"", ""result"": [ 1717689600, ""42"" ] }}";
        var client = BuildClient(json);
        var value = await client.QueryScalarAsync("scalar(x)", CancellationToken.None);
        Assert.Equal(42, value);
    }

    [Fact]
    public async Task QueryScalarAsync_ReturnsNull_OnEmptyResult()
    {
        const string json = @"{ ""status"": ""success"", ""data"": { ""resultType"": ""vector"", ""result"": [] } }";
        var client = BuildClient(json);
        var value = await client.QueryScalarAsync("missing", CancellationToken.None);
        Assert.Null(value);
    }

    [Fact]
    public async Task QueryScalarAsync_ReturnsNull_OnHttpError()
    {
        // Prometheus unreachable / erroring must degrade to null, never throw.
        var client = BuildClient("oops", HttpStatusCode.InternalServerError);
        var value = await client.QueryScalarAsync("up", CancellationToken.None);
        Assert.Null(value);
    }

    [Fact]
    public async Task QueryVectorAsync_ParsesLabelsAndValues()
    {
        const string json = @"{
  ""status"": ""success"",
  ""data"": { ""resultType"": ""vector"", ""result"": [
    { ""metric"": { ""__name__"": ""up"", ""job"": ""building-os-api"" }, ""value"": [ 1717689600, ""1"" ] },
    { ""metric"": { ""__name__"": ""up"", ""job"": ""building-os-connector-worker"" }, ""value"": [ 1717689600, ""0"" ] }
  ]}}";
        var client = BuildClient(json);
        var samples = await client.QueryVectorAsync("up", CancellationToken.None);

        Assert.Equal(2, samples.Count);
        Assert.Equal("building-os-api", samples[0].Labels["job"]);
        Assert.Equal(1, samples[0].Value);
        Assert.Equal("building-os-connector-worker", samples[1].Labels["job"]);
        Assert.Equal(0, samples[1].Value);
    }

    [Fact]
    public async Task QueryVectorAsync_ReturnsEmpty_WhenNotConfigured()
    {
        var client = new PrometheusQueryClient(new HttpClient(new ThrowingHttpHandler()), "");
        var samples = await client.QueryVectorAsync("up", CancellationToken.None);
        Assert.Empty(samples);
    }

    [Fact]
    public async Task QueryScalarAsync_ReturnsNull_OnInvalidJson()
    {
        var client = BuildClient("this is not json");
        var value = await client.QueryScalarAsync("up", CancellationToken.None);
        Assert.Null(value);
    }

    [Fact]
    public async Task QueryScalarAsync_ReturnsNull_WhenStatusNotSuccess()
    {
        const string json = @"{ ""status"": ""error"", ""errorType"": ""bad_data"", ""error"": ""parse error"" }";
        var client = BuildClient(json);
        var value = await client.QueryScalarAsync("up(", CancellationToken.None);
        Assert.Null(value);
    }

    [Fact]
    public async Task QueryScalarAsync_ReturnsNull_OnCancellationOrTimeout()
    {
        // A request that cancels (e.g. HttpClient timeout) must degrade to null, not throw.
        var client = new PrometheusQueryClient(new HttpClient(new CancelingHttpHandler()), "http://prometheus:9090");
        var value = await client.QueryScalarAsync("up", CancellationToken.None);
        Assert.Null(value);
    }

    [Fact]
    public async Task QueryVectorAsync_SkipsSamplesWithMissingValue()
    {
        const string json = @"{
  ""status"": ""success"",
  ""data"": { ""resultType"": ""vector"", ""result"": [
    { ""metric"": { ""job"": ""a"" }, ""value"": [ 1717689600, ""1"" ] },
    { ""metric"": { ""job"": ""b"" } }
  ]}}";
        var client = BuildClient(json);
        var samples = await client.QueryVectorAsync("up", CancellationToken.None);
        Assert.Single(samples);
        Assert.Equal("a", samples[0].Labels["job"]);
    }
}

internal sealed class JsonHttpHandler : HttpMessageHandler
{
    private readonly string _body;
    private readonly HttpStatusCode _status;

    public JsonHttpHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _body = body;
        _status = status;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Task.FromResult(new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json")
        });
}

internal sealed class ThrowingHttpHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => throw new InvalidOperationException("HTTP must not be called when Prometheus is unconfigured");
}

internal sealed class CancelingHttpHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => throw new TaskCanceledException("simulated timeout", new TimeoutException());
}
