namespace BuildingOS.ConnectorWorker.Connectors;

/// <summary>
/// How many JetStream publish-acks one gRPC ingress stream may have in flight (#625 slice B).
/// <see cref="GatewayIngressService"/> issues the publish for frame <c>i+1</c> without waiting for
/// the ack of frame <c>i</c>, up to <see cref="Window"/> outstanding acks; past that the stream read
/// blocks (backpressure, memory bounded by Window × the record size limit). <c>accepted</c> still
/// counts only acked frames (#187). <c>1</c> is the pre-#625 serial behaviour.
///
/// The window is also the most frames that can be persisted but unacknowledged to the gateway when
/// a stream drops mid-flight (previously at most one) — a gateway that re-sends them produces
/// duplicates the lake does not de-duplicate — so it is kept small by default and documented in
/// <c>docs/guides/gateway-onboarding-checklist.md</c>.
/// </summary>
public sealed class IngressPublishOptions
{
    public const int DefaultWindow = 128;

    /// <summary>Maximum in-flight publish-acks per stream (≥ 1).</summary>
    public int Window { get; init; } = DefaultWindow;

    /// <summary>Builds options from the raw config value (env): GRPC_INGRESS_PUBLISH_WINDOW.
    /// Non-numeric or &lt; 1 falls back to the default.</summary>
    public static IngressPublishOptions Parse(string? windowRaw) => new()
    {
        Window = int.TryParse(windowRaw?.Trim(), out var w) && w >= 1 ? w : DefaultWindow,
    };
}
