namespace BuildingOS.Shared.Infrastructure.Telemetry;

/// <summary>
/// Resolves the warm-tier storage mode from the <c>WARM_STORE</c> env value (#216). The default is
/// <b>parquet</b> (the cost-minimal configuration: validated telemetry is written straight to the
/// Parquet lake and TimescaleDB can be switched off). <c>WARM_STORE=timescale</c> is the explicit
/// opt-in that preserves the previous TimescaleDB warm/aggregate behaviour. Shared by the ApiServer DI
/// and the ConnectorWorker registration so both sides agree on the mode.
/// </summary>
public static class WarmStoreMode
{
    public const string EnvVar = "WARM_STORE";
    public const string Timescale = "timescale";

    /// <summary>True for parquet mode — i.e. anything except an explicit (case-insensitive) "timescale".</summary>
    public static bool IsParquet(string? value)
        => !string.Equals(value?.Trim(), Timescale, StringComparison.OrdinalIgnoreCase);
}
