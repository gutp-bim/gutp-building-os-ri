using BuildingOS.Shared.Infrastructure.Gateway;

namespace BuildingOS.ConnectorWorker.Connectors;

/// <summary>
/// Configuration for binding the gRPC ingress sender's claimed <c>gateway_id</c> to the gateway
/// identity verified by the mTLS-terminating ingress (#296). The ingress (Traefik
/// <c>passTLSClientCert</c>) injects the verified gateway id as a trusted header; when
/// <see cref="Enforce"/> is set, <see cref="GatewayIngressService"/> requires that header to match
/// each frame's <c>gateway_id</c>.
///
/// Default is <see cref="Enforce"/> = false to preserve the prior behaviour: the gRPC ingress is
/// itself opt-in (GRPC_INGRESS_PORT) and there is no trusted-header injection in local/CI, so
/// enforcing there would reject every frame. Production (with an mTLS ingress) sets it true.
/// </summary>
public sealed class IngressIdentityOptions
{
    /// <summary>Default trusted header name the ingress injects from the client cert identity (shared with #224 pointlist).</summary>
    public const string DefaultHeaderName = GatewayIdentityDefaults.TrustedHeaderName;

    /// <summary>When true, a frame whose <c>gateway_id</c> does not match the trusted header is rejected.</summary>
    public bool Enforce { get; init; }

    /// <summary>Trusted header name carrying the ingress-verified gateway id.</summary>
    public string HeaderName { get; init; } = DefaultHeaderName;

    /// <summary>Builds options from raw config values (env): GRPC_INGRESS_REQUIRE_GATEWAY_IDENTITY / GRPC_INGRESS_GATEWAY_ID_HEADER.</summary>
    public static IngressIdentityOptions Parse(string? enforceRaw, string? headerRaw) => new()
    {
        Enforce = ParseEnforce(enforceRaw),
        HeaderName = string.IsNullOrWhiteSpace(headerRaw) ? DefaultHeaderName : headerRaw!.Trim(),
    };

    // Accept the common truthy conventions (true/1/yes/on) so a security toggle is not silently left
    // off by a non-canonical value; anything else (incl. unset) is false — pair with a startup log so
    // the effective state is observable.
    private static bool ParseEnforce(string? raw) => (raw?.Trim().ToLowerInvariant()) switch
    {
        "true" or "1" or "yes" or "on" => true,
        _ => false,
    };
}
