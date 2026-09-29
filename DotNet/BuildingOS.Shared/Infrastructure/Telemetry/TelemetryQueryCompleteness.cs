namespace BuildingOS.Shared.Infrastructure.Telemetry;

/// <summary>
/// Carries "this read returned less than was asked for" from a telemetry store back to the HTTP
/// response (#499), without widening every store interface to return a result envelope.
///
/// The controller opens a scope with <see cref="Begin"/> around the query; a store that truncates its
/// read (e.g. <c>PARQUET_QUERY_MAX_FILES</c>) calls <see cref="ReportTruncated"/>. The scope is an
/// <see cref="AsyncLocal{T}"/>, so it flows into the awaited store call and stays per-request even
/// though the stores are singletons. Reporting with no open scope is a no-op.
/// </summary>
public sealed class TelemetryQueryCompleteness : IDisposable
{
    private static readonly AsyncLocal<TelemetryQueryCompleteness?> Current = new();

    private readonly TelemetryQueryCompleteness? _previous;
    private readonly object _gate = new();
    private DateTime? _coveredFrom;

    private TelemetryQueryCompleteness(TelemetryQueryCompleteness? previous) => _previous = previous;

    /// <summary>True when any store in this scope dropped part of the requested range.</summary>
    public bool IsPartial
    {
        get { lock (_gate) return _coveredFrom.HasValue; }
    }

    /// <summary>
    /// Earliest instant (UTC) from which the returned data is complete; data before it may be missing.
    /// Null when the result is complete.
    /// </summary>
    public DateTime? CoveredFrom
    {
        get { lock (_gate) return _coveredFrom; }
    }

    public static TelemetryQueryCompleteness Begin()
    {
        var scope = new TelemetryQueryCompleteness(Current.Value);
        Current.Value = scope;
        return scope;
    }

    /// <summary>
    /// Record that data before <paramref name="coveredFrom"/> may be missing. With several reports the
    /// latest instant wins, since the result is only complete from the point every read covers.
    /// </summary>
    public static void ReportTruncated(DateTime coveredFrom)
    {
        var scope = Current.Value;
        if (scope is null) return;
        var utc = coveredFrom.Kind == DateTimeKind.Utc ? coveredFrom : coveredFrom.ToUniversalTime();
        lock (scope._gate)
        {
            if (scope._coveredFrom is null || utc > scope._coveredFrom) scope._coveredFrom = utc;
        }
    }

    public void Dispose()
    {
        if (ReferenceEquals(Current.Value, this)) Current.Value = _previous;
    }
}
