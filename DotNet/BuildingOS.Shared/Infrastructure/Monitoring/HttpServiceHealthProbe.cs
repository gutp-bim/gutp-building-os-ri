using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Infrastructure.Monitoring;

/// <summary>
/// <see cref="IServiceHealthProbe"/> that performs an HTTP GET against each target's health URL.
/// 2xx → "up", any other response or failure (unreachable / timeout) → "down". Never throws —
/// a failed probe is reported as "down" so the status endpoint stays responsive.
/// </summary>
public sealed class HttpServiceHealthProbe : IServiceHealthProbe
{
    private readonly HttpClient _http;
    private readonly IReadOnlyList<ServiceHealthTarget> _targets;
    private readonly ILogger _logger;

    public HttpServiceHealthProbe(
        HttpClient http,
        IReadOnlyList<ServiceHealthTarget> targets,
        ILogger<HttpServiceHealthProbe>? logger = null)
    {
        _http = http;
        _targets = targets;
        _logger = logger ?? NullLogger<HttpServiceHealthProbe>.Instance;
    }

    public async Task<IReadOnlyList<ServiceStatus>> ProbeAllAsync(CancellationToken ct)
    {
        if (_targets.Count == 0) return Array.Empty<ServiceStatus>();

        var results = await Task.WhenAll(_targets.Select(t => ProbeOneAsync(t, ct))).ConfigureAwait(false);
        return results;
    }

    private async Task<ServiceStatus> ProbeOneAsync(ServiceHealthTarget target, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(target.Url, ct).ConfigureAwait(false);
            return new ServiceStatus(target.Name, response.IsSuccessStatusCode ? "up" : "down");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Health probe failed for {Service} at {Url}, reporting down", target.Name, target.Url);
            return new ServiceStatus(target.Name, "down");
        }
    }
}
