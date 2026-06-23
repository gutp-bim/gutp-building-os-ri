using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Infrastructure.Monitoring;

/// <summary>
/// <see cref="IPrometheusQueryClient"/> backed by the Prometheus HTTP API
/// (<c>GET {baseUrl}/api/v1/query</c>). All failures degrade to null/empty so callers never have
/// to special-case a missing or unhealthy metrics backend.
/// </summary>
public sealed class PrometheusQueryClient : IPrometheusQueryClient
{
    private readonly HttpClient _http;
    private readonly string? _baseUrl;
    private readonly ILogger _logger;

    public PrometheusQueryClient(HttpClient http, string? baseUrl, ILogger<PrometheusQueryClient>? logger = null)
    {
        _http = http;
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.TrimEnd('/');
        _logger = logger ?? NullLogger<PrometheusQueryClient>.Instance;
    }

    public bool IsConfigured => _baseUrl is not null;

    public async Task<double?> QueryScalarAsync(string query, CancellationToken ct)
    {
        var data = await QueryAsync(query, ct).ConfigureAwait(false);
        if (data is null) return null;

        var root = data.Value;
        var resultType = root.TryGetProperty("resultType", out var rt) ? rt.GetString() : null;
        if (!root.TryGetProperty("result", out var result)) return null;

        // resultType "scalar": result is [ <ts>, "<value>" ]
        if (resultType == "scalar" && result.ValueKind == JsonValueKind.Array && result.GetArrayLength() == 2)
        {
            return ParseValue(result[1]);
        }

        // resultType "vector": result is [ { metric, value: [ts, "v"] }, ... ] — take the first.
        if (result.ValueKind == JsonValueKind.Array && result.GetArrayLength() > 0)
        {
            var first = result[0];
            if (first.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 2)
            {
                return ParseValue(v[1]);
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<PrometheusSample>> QueryVectorAsync(string query, CancellationToken ct)
    {
        var data = await QueryAsync(query, ct).ConfigureAwait(false);
        if (data is null) return Array.Empty<PrometheusSample>();

        if (!data.Value.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            return Array.Empty<PrometheusSample>();

        var samples = new List<PrometheusSample>(result.GetArrayLength());
        foreach (var item in result.EnumerateArray())
        {
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            if (item.TryGetProperty("metric", out var metric) && metric.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in metric.EnumerateObject())
                {
                    labels[prop.Name] = prop.Value.GetString() ?? string.Empty;
                }
            }

            double? value = null;
            if (item.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 2)
            {
                value = ParseValue(v[1]);
            }

            if (value is not null)
            {
                samples.Add(new PrometheusSample(labels, value.Value));
            }
        }

        return samples;
    }

    /// <summary>
    /// Executes the instant query and returns the <c>data</c> element, or null on any failure
    /// (unconfigured, non-success HTTP, non-"success" status, or parse error).
    /// </summary>
    private async Task<JsonElement?> QueryAsync(string query, CancellationToken ct)
    {
        if (_baseUrl is null) return null;

        try
        {
            var url = $"{_baseUrl}/api/v1/query?query={Uri.EscapeDataString(query)}";
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Prometheus query returned {StatusCode}, degrading to null. Query={Query}",
                    (int)response.StatusCode, query);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            if (!root.TryGetProperty("status", out var status) || status.GetString() != "success")
            {
                _logger.LogDebug("Prometheus query status was not 'success', degrading to null. Query={Query}", query);
                return null;
            }

            if (!root.TryGetProperty("data", out var data))
                return null;

            // Clone so the value survives disposal of the JsonDocument.
            return data.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Prometheus query failed, degrading to null. Query={Query}", query);
            return null;
        }
    }

    private static double? ParseValue(JsonElement element)
    {
        var raw = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
        if (raw is null) return null;
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}
