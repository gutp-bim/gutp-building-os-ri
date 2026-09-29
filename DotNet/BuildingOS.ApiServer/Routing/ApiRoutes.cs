namespace BuildingOs.ApiServer.Routing;

/// <summary>
/// The versioned route prefix every REST controller is mounted under (#507, ADR-0008). A breaking
/// change ships as a new version (<c>api/v2</c>) beside the old one instead of changing v1 in place.
/// </summary>
public static class ApiRoutes
{
    public const string V1 = "api/v1";
}
