namespace BuildingOs.ApiServer.Routing;

/// <summary>
/// The versioned route prefix every REST controller is mounted under (#507, ADR-0008). A breaking
/// change ships as a new version (<c>api/v2</c>) beside the old one instead of changing v1 in place.
/// </summary>
public static class ApiRoutes
{
    public const string V1 = "api/v1";

    /// <summary>
    /// The gateway point-list endpoint (#224) is deliberately <b>not</b> versioned under <c>/api</c>: it
    /// authenticates the gateway by a trusted header that only the mTLS ingress may set, and the
    /// general <c>PathPrefix(/api)</c> ingress route neither requires mTLS nor strips that header.
    /// Keeping it at <c>/gateways/…</c> keeps it off that route (ADR-0008 §1).
    /// </summary>
    public const string GatewayProvisioning = "gateways";
}
