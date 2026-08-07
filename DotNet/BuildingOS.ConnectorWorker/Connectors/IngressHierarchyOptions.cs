namespace BuildingOS.ConnectorWorker.Connectors;

/// <summary>
/// Configuration for requiring an ingested point to be placed in the building hierarchy before its
/// telemetry is accepted (#292). The twin enrichment already resolves each point's building and
/// device, so when <see cref="Enforce"/> is set <see cref="GatewayIngressService"/> rejects a frame
/// whose point resolves to neither — no extra graph query is needed.
///
/// Default is <see cref="Enforce"/> = false to preserve the prior behaviour: a twin is routinely
/// seeded before its hierarchy is complete (#118), so enforcing there would drop every point of an
/// unfinished import. A deployment whose twin is fully modelled sets it true to fail loudly instead
/// of storing telemetry nothing can navigate to.
/// </summary>
public sealed class IngressHierarchyOptions
{
    /// <summary>When true, a frame whose point has no building or no device in the twin is rejected.</summary>
    public bool Enforce { get; init; }

    /// <summary>Builds options from the raw config value (env): GRPC_INGRESS_REQUIRE_HIERARCHY.</summary>
    public static IngressHierarchyOptions Parse(string? enforceRaw) => new()
    {
        Enforce = ParseEnforce(enforceRaw),
    };

    // Same truthy conventions as the identity toggle (true/1/yes/on) so the two ingress policies are
    // configured alike; anything else (incl. unset) is false — pair with a startup log so the
    // effective state is observable.
    private static bool ParseEnforce(string? raw) => (raw?.Trim().ToLowerInvariant()) switch
    {
        "true" or "1" or "yes" or "on" => true,
        _ => false,
    };
}
