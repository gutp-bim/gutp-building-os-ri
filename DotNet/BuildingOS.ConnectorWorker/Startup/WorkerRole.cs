namespace BuildingOS.ConnectorWorker.Startup;

/// <summary>
/// Which capability group the worker process runs (#400). One image, one code base — the role only
/// selects which of the <see cref="ConnectorWorkerServiceCollectionExtensions"/> registrations are
/// applied, so the OSS RI keeps its single all-in-one container while a production deployment can
/// scale the latency-sensitive ingest path independently of the batch lake path.
/// </summary>
public enum WorkerRole
{
    /// <summary>Every capability in one process — the default, and the shape the OSS/demo stack runs.</summary>
    All,

    /// <summary>External data → canonical telemetry: gRPC GatewayIngress, MQTT/Hono ingress, raw.* normalizers.</summary>
    Ingest,

    /// <summary>Telemetry persistence: Parquet lake writer, compaction, retention (or the legacy cold export).</summary>
    Lake,

    /// <summary>Physical device control: the NATS point-control worker and its binding handlers.</summary>
    Control,
}

/// <summary>Resolves <see cref="WorkerRole"/> from the <c>WORKER_ROLE</c> env value.</summary>
public static class WorkerRoles
{
    public const string EnvVar = "WORKER_ROLE";

    /// <summary>
    /// Unset / blank / "all" → <see cref="WorkerRole.All"/>, preserving the pre-#400 behaviour for every
    /// deployment that never sets the variable. Anything else must be one of the known roles: unlike
    /// <c>WARM_STORE</c>, an unrecognised value is rejected rather than folded into the default, because
    /// a typo that silently means "all" would start the twin seed and the compactor on every replica of
    /// a split deployment — the one outcome the role switch exists to prevent.
    /// </summary>
    public static WorkerRole Parse(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "all" => WorkerRole.All,
            "ingest" => WorkerRole.Ingest,
            "lake" => WorkerRole.Lake,
            "control" => WorkerRole.Control,
            var other => throw new InvalidOperationException(
                $"{EnvVar}='{value}' is not a known role. Use one of: all, ingest, lake, control " +
                $"(unset means all). Got '{other}'."),
        };

    /// <summary>
    /// The twin seed replaces the default graph, so it runs only in the all-in-one worker. A split
    /// deployment seeds from the API server (or an operator task) instead — see #400.
    /// </summary>
    public static bool RunsTwinSeed(this WorkerRole role) => role is WorkerRole.All;

    /// <summary>
    /// The read-only twin client (OxiGraphClient / IPointIdFactory / BacnetPointResolver). Needed by the
    /// protocol connectors and the gRPC ingress metadata cache, and by the Hono control handler — so
    /// every role except <see cref="WorkerRole.Lake"/>, which touches neither.
    /// </summary>
    public static bool RunsTwinClient(this WorkerRole role) => role is not WorkerRole.Lake;

    public static bool RunsControl(this WorkerRole role) => role is WorkerRole.All or WorkerRole.Control;

    public static bool RunsProtocolConnectors(this WorkerRole role) => role is WorkerRole.All or WorkerRole.Ingest;

    public static bool RunsTelemetryIngress(this WorkerRole role) => role is WorkerRole.All or WorkerRole.Ingest;

    public static bool RunsLake(this WorkerRole role) => role is WorkerRole.All or WorkerRole.Lake;
}
