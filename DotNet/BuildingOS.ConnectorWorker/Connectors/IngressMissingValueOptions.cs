namespace BuildingOS.ConnectorWorker.Connectors;

/// <summary>What the gRPC ingress does with a <c>TelemetryFrame</c> whose <c>value</c> oneof is unset (#663).</summary>
public enum IngressMissingValuePolicy
{
    /// <summary>Drop the frame as a missing reading, counted as <c>result=no_value</c> (default).</summary>
    Skip,

    /// <summary>
    /// Publish it as numeric 0.0 — the pre-#663 behaviour, for a legacy numeric gateway (pre-#152, no
    /// oneof) whose 0.0 reading proto3 left off the wire because it equals the field default.
    /// </summary>
    Zero,
}

/// <summary>
/// The missing-value policy of <see cref="GatewayIngressService"/> (#663). A frame with no value case
/// set used to be published as 0.0, so a missing reading was indistinguishable from a real 0 in history
/// and the latest value (a room temperature of "0 ℃"). Current gateways set the oneof for every reading,
/// 0.0 included, so an unset one is either a missing value or a legacy gateway's omitted 0.0 — which
/// were never distinguishable on the wire. The default treats it as missing: the absence of a row is
/// what data health (stale / missing) already reads as "no value", and a null value would ripple
/// through the writer, the lake, aggregation and the UI.
/// </summary>
public sealed class IngressMissingValueOptions
{
    public const string EnvironmentVariable = "GRPC_INGRESS_MISSING_VALUE_POLICY";

    public IngressMissingValuePolicy Policy { get; init; } = IngressMissingValuePolicy.Skip;

    /// <summary>
    /// Builds options from the raw config value: <c>zero</c> (case-insensitive, trimmed) selects
    /// <see cref="IngressMissingValuePolicy.Zero"/>; anything else — unset, <c>skip</c>, or an unknown
    /// value — is <see cref="IngressMissingValuePolicy.Skip"/>, so a typo never turns missing readings
    /// back into zeros. The effective value is logged at startup.
    /// </summary>
    public static IngressMissingValueOptions Parse(string? raw) => new()
    {
        Policy = string.Equals(raw?.Trim(), "zero", StringComparison.OrdinalIgnoreCase)
            ? IngressMissingValuePolicy.Zero
            : IngressMissingValuePolicy.Skip,
    };
}
