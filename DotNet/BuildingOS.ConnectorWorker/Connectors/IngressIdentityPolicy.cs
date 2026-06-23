namespace BuildingOS.ConnectorWorker.Connectors;

/// <summary>Outcome of the ingress gateway-identity check (#296).</summary>
public enum IngressIdentityDecision
{
    /// <summary>Accept: enforcement off, or the trusted header matches the frame's gateway_id.</summary>
    Allow,

    /// <summary>Reject: enforcement on but no trusted gateway id was injected by the ingress.</summary>
    RejectMissingIdentity,

    /// <summary>Reject: enforcement on and the trusted gateway id differs from the frame's gateway_id.</summary>
    RejectMismatch,
}

/// <summary>
/// Pure decision for binding a telemetry frame's claimed <c>gateway_id</c> to the gateway identity
/// the mTLS ingress verified (#296). Kept side-effect-free so it is exhaustively unit-tested; the
/// service layer maps the decision to a skip + metric.
/// </summary>
public static class IngressIdentityPolicy
{
    /// <param name="enforce">Whether identity binding is required (false ⇒ legacy provenance-only).</param>
    /// <param name="trustedGatewayId">Gateway id from the ingress trusted header (null when absent).</param>
    /// <param name="frameGatewayId">The gateway_id the frame claims (already validated non-empty).</param>
    public static IngressIdentityDecision Check(bool enforce, string? trustedGatewayId, string frameGatewayId)
    {
        if (!enforce) return IngressIdentityDecision.Allow;
        if (string.IsNullOrWhiteSpace(trustedGatewayId)) return IngressIdentityDecision.RejectMissingIdentity;
        return string.Equals(trustedGatewayId, frameGatewayId, StringComparison.Ordinal)
            ? IngressIdentityDecision.Allow
            : IngressIdentityDecision.RejectMismatch;
    }
}
