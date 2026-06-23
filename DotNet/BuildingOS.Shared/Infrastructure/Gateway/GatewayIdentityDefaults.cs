namespace BuildingOS.Shared.Infrastructure.Gateway;

/// <summary>
/// Shared defaults for gateway machine-authentication across subsystems. The mTLS-terminating ingress
/// (Traefik <c>passTLSClientCert</c>) injects the verified gateway id into this trusted header; both
/// the ApiServer pointlist endpoint (#224) and the ConnectorWorker gRPC ingress (#296) read it.
///
/// Kept here so the two subsystems share one source of truth — renaming the ingress-injected header
/// is a single change, not two literals that can silently drift.
/// </summary>
public static class GatewayIdentityDefaults
{
    /// <summary>Default trusted header name carrying the ingress-verified gateway id.</summary>
    public const string TrustedHeaderName = "X-Gateway-Id";
}
