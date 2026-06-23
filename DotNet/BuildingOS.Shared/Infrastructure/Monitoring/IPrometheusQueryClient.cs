namespace BuildingOS.Shared.Infrastructure.Monitoring;

/// <summary>
/// A read-only client over the Prometheus HTTP query API. Designed to degrade gracefully:
/// when Prometheus is not configured or unreachable, scalar queries return <c>null</c> and
/// vector queries return an empty list rather than throwing. This lets the built-in simple
/// monitoring work without Grafana, and without hard-failing when no metrics backend exists.
/// </summary>
public interface IPrometheusQueryClient
{
    /// <summary>True when a Prometheus base URL is configured.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Runs an instant query and returns the first sample's value, or <c>null</c> when there is
    /// no result, Prometheus is unconfigured, or the request fails.
    /// </summary>
    Task<double?> QueryScalarAsync(string query, CancellationToken ct);

    /// <summary>
    /// Runs an instant query and returns all samples (labels + value). Returns an empty list when
    /// Prometheus is unconfigured or the request fails.
    /// </summary>
    Task<IReadOnlyList<PrometheusSample>> QueryVectorAsync(string query, CancellationToken ct);
}

/// <summary>A single Prometheus instant-vector sample: its label set and numeric value.</summary>
public sealed record PrometheusSample(IReadOnlyDictionary<string, string> Labels, double Value);
