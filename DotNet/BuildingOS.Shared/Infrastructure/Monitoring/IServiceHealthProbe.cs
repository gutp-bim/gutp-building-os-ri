namespace BuildingOS.Shared.Infrastructure.Monitoring;

/// <summary>
/// Probes a configured set of service health endpoints (HTTP <c>/health</c> fan-out) to determine
/// per-service up/down. This is intentionally independent of Prometheus so the simple-monitoring
/// view reports liveness even when no metrics backend is configured (Issue #144 / B-1).
/// </summary>
public interface IServiceHealthProbe
{
    /// <summary>
    /// Probes every configured target concurrently. Returns an empty list when no targets are
    /// configured (callers still report the API server itself separately).
    /// </summary>
    Task<IReadOnlyList<ServiceStatus>> ProbeAllAsync(CancellationToken ct);
}

/// <summary>A single health-probe target: a display name and the URL to GET.</summary>
public sealed record ServiceHealthTarget(string Name, string Url)
{
    /// <summary>
    /// Parses a target list of the form <c>name1=url1,name2=url2</c>. Entries that are blank or
    /// missing the <c>name=url</c> separator are skipped. Returns an empty list for null/blank input.
    /// </summary>
    public static IReadOnlyList<ServiceHealthTarget> ParseList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<ServiceHealthTarget>();

        var targets = new List<ServiceHealthTarget>();
        foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var sep = entry.IndexOf('=');
            if (sep <= 0 || sep == entry.Length - 1) continue; // need a non-empty name and url
            var name = entry[..sep].Trim();
            var url = entry[(sep + 1)..].Trim();
            if (name.Length == 0 || url.Length == 0) continue;
            targets.Add(new ServiceHealthTarget(name, url));
        }

        return targets;
    }
}
