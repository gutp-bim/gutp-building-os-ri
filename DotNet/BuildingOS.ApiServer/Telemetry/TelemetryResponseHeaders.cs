namespace BuildingOs.ApiServer.Telemetry;

/// <summary>
/// Response headers that mark a telemetry read as incomplete (#499). A store can cap how much of a
/// range it reads (<c>PARQUET_QUERY_MAX_FILES</c>) and keep the newest part; these headers let the
/// caller see that the older side was dropped instead of trusting a silent 200.
/// </summary>
public static class TelemetryResponseHeaders
{
    /// <summary><c>true</c> when the response holds only part of the requested range. Absent otherwise.</summary>
    public const string PartialResult = "X-Partial-Result";

    /// <summary>
    /// ISO-8601 UTC instant from which the returned data is complete; the requested range before it
    /// may be missing rows. Sent only together with <see cref="PartialResult"/>.
    /// </summary>
    public const string CoveredFrom = "X-Covered-From";
}
