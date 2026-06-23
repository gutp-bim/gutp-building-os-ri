using BuildingOS.Shared.Infrastructure.Gateway;
using Microsoft.AspNetCore.Http;

namespace BuildingOs.ApiServer.GatewayProvisioning;

/// <summary>
/// Resolves the calling gateway's id (#224). The mTLS-terminating ingress (Traefik
/// <c>passTLSClientCert</c>) maps the verified client certificate to a single trusted header; this
/// resolver reads that header. The route MUST only be reachable via that ingress, and the header MUST
/// be stripped on every untrusted path (see docs/oss-gateway-pointlist-sync.md トラスト境界).
/// </summary>
public interface IGatewayIdentityResolver
{
    /// <summary>The gateway id from the trusted header, or null when absent/blank.</summary>
    string? ResolveGatewayId(IHeaderDictionary headers);
}

public sealed class HeaderGatewayIdentityResolver : IGatewayIdentityResolver
{
    /// <summary>Default trusted header name the ingress injects from the client cert identity (shared with #296 ingress).</summary>
    public const string DefaultHeaderName = GatewayIdentityDefaults.TrustedHeaderName;

    private readonly string _headerName;

    public HeaderGatewayIdentityResolver(string? headerName = null)
        => _headerName = string.IsNullOrWhiteSpace(headerName) ? DefaultHeaderName : headerName!;

    public string? ResolveGatewayId(IHeaderDictionary headers)
    {
        if (!headers.TryGetValue(_headerName, out var value)) return null;
        var s = value.ToString().Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }
}
