using System.Net;
using System.Net.Http;
using BuildingOS.Shared.Infrastructure.Monitoring;

namespace BuildingOS.Shared.Test.Infrastructure.Monitoring;

public class HttpServiceHealthProbeTest
{
    [Fact]
    public async Task ProbeAllAsync_ReturnsEmpty_WhenNoTargets()
    {
        var probe = new HttpServiceHealthProbe(new HttpClient(), Array.Empty<ServiceHealthTarget>());
        var result = await probe.ProbeAllAsync(CancellationToken.None);
        Assert.Empty(result);
    }

    [Fact]
    public async Task ProbeAllAsync_MapsStatusCodesToUpDown()
    {
        var handler = new RoutedHttpHandler(new Dictionary<string, HttpStatusCode>
        {
            ["http://up-svc/health"] = HttpStatusCode.OK,
            ["http://down-svc/health"] = HttpStatusCode.ServiceUnavailable,
        });
        var probe = new HttpServiceHealthProbe(new HttpClient(handler), new[]
        {
            new ServiceHealthTarget("up-svc", "http://up-svc/health"),
            new ServiceHealthTarget("down-svc", "http://down-svc/health"),
        });

        var result = await probe.ProbeAllAsync(CancellationToken.None);

        Assert.Equal("up", result.Single(s => s.Name == "up-svc").Status);
        Assert.Equal("down", result.Single(s => s.Name == "down-svc").Status);
    }

    [Fact]
    public async Task ProbeAllAsync_ReportsDown_OnUnreachableHost()
    {
        var probe = new HttpServiceHealthProbe(
            new HttpClient(new AlwaysThrowHttpHandler()),
            new[] { new ServiceHealthTarget("broken", "http://broken/health") });

        var result = await probe.ProbeAllAsync(CancellationToken.None);

        Assert.Equal("down", Assert.Single(result).Status);
    }
}

internal sealed class RoutedHttpHandler : HttpMessageHandler
{
    private readonly IReadOnlyDictionary<string, HttpStatusCode> _routes;
    public RoutedHttpHandler(IReadOnlyDictionary<string, HttpStatusCode> routes) => _routes = routes;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        var status = _routes.TryGetValue(url, out var s) ? s : HttpStatusCode.NotFound;
        return Task.FromResult(new HttpResponseMessage(status));
    }
}

internal sealed class AlwaysThrowHttpHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => throw new HttpRequestException("connection refused");
}
