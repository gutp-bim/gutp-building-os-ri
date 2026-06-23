using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure.OxiGraph;

namespace BuildingOS.Shared.Infrastructure.Authorization;

using static OxiGraphOntology;

/// <summary>
/// IResourceHierarchyResolver backed by OxiGraph SPARQL.
/// Queries use SBCO vocabulary; Space (SpaceExt) and sbco:locatedIn are optional
/// since current SBCO TTL may not include spatial hierarchy below Level.
/// </summary>
public class OxiGraphHierarchyResolver : IResourceHierarchyResolver
{
    private readonly OxiGraphClient _client;

    public OxiGraphHierarchyResolver(OxiGraphClient client) => _client = client;

    public async Task<IReadOnlyList<(string ResourceType, string ResourceId)>> GetAncestorsAsync(
        string resourceType, string resourceId, CancellationToken ct = default)
    {
        return resourceType.ToLowerInvariant() switch
        {
            "point"    => await GetPointAncestors(resourceId, ct),
            "device"   => await GetDeviceAncestors(resourceId, ct),
            "space"    => await GetSpaceAncestors(resourceId, ct),
            "floor"    => await GetFloorAncestors(resourceId, ct),
            _          => Array.Empty<(string, string)>(),
        };
    }

    private async Task<IReadOnlyList<(string, string)>> GetPointAncestors(string pointId, CancellationToken ct)
    {
        var dtId = await ResolvePointDtId(pointId, ct);
        if (dtId is null) return Array.Empty<(string, string)>();

        var pointUri = NodeUri(dtId);
        var sparql = $@"{Prefixes}
SELECT ?buildingId ?floorId ?spaceId ?devId
WHERE {{
  ?dev <{Prop_HasPoint}> <{pointUri}> .
  ?dev a <{Cls_Equipment}> ; <{Prop_Id}> ?devId .
  OPTIONAL {{
    ?dev <{Prop_LocatedIn}> ?space .
    ?space a <{Cls_Space}> ; <{Prop_Id}> ?spaceId .
    ?floor <{Prop_HasPart}> ?space .
    ?floor a <{Cls_Level}> ; <{Prop_Id}> ?floorId .
    ?building <{Prop_HasPart}> ?floor .
    ?building a <{Cls_Building}> ; <{Prop_Id}> ?buildingId .
  }}
}}";

        var rows = await _client.QueryAsync(sparql, ct);
        if (rows.Count == 0) return Array.Empty<(string, string)>();
        var r = rows[0];
        var result = new List<(string, string)>();
        if (r.TryGetValue("buildingId", out var bid)) result.Add(("building", bid));
        if (r.TryGetValue("floorId",    out var fid)) result.Add(("floor",    fid));
        if (r.TryGetValue("spaceId",    out var sid)) result.Add(("space",    sid));
        if (r.TryGetValue("devId",      out var did)) result.Add(("device",   did));
        return result;
    }

    private async Task<IReadOnlyList<(string, string)>> GetDeviceAncestors(string deviceId, CancellationToken ct)
    {
        var sparql = $@"{Prefixes}
SELECT ?buildingId ?floorId ?spaceId
WHERE {{
  ?dev a <{Cls_Equipment}> ; <{Prop_Id}> ""{EscapeLiteral(deviceId)}"" .
  OPTIONAL {{
    ?dev <{Prop_LocatedIn}> ?space .
    ?space a <{Cls_Space}> ; <{Prop_Id}> ?spaceId .
    ?floor <{Prop_HasPart}> ?space .
    ?floor a <{Cls_Level}> ; <{Prop_Id}> ?floorId .
    ?building <{Prop_HasPart}> ?floor .
    ?building a <{Cls_Building}> ; <{Prop_Id}> ?buildingId .
  }}
}}";

        var rows = await _client.QueryAsync(sparql, ct);
        if (rows.Count == 0) return Array.Empty<(string, string)>();
        var r = rows[0];
        var result = new List<(string, string)>();
        if (r.TryGetValue("buildingId", out var bid)) result.Add(("building", bid));
        if (r.TryGetValue("floorId",    out var fid)) result.Add(("floor",    fid));
        if (r.TryGetValue("spaceId",    out var sid)) result.Add(("space",    sid));
        return result;
    }

    private async Task<IReadOnlyList<(string, string)>> GetSpaceAncestors(string spaceId, CancellationToken ct)
    {
        var sparql = $@"{Prefixes}
SELECT ?buildingId ?floorId
WHERE {{
  ?space a <{Cls_Space}> ; <{Prop_Id}> ""{EscapeLiteral(spaceId)}"" .
  ?floor <{Prop_HasPart}> ?space .
  ?floor a <{Cls_Level}> ; <{Prop_Id}> ?floorId .
  ?building <{Prop_HasPart}> ?floor .
  ?building a <{Cls_Building}> ; <{Prop_Id}> ?buildingId .
}}";

        var rows = await _client.QueryAsync(sparql, ct);
        if (rows.Count == 0) return Array.Empty<(string, string)>();
        var r = rows[0];
        return new (string, string)[]
        {
            ("building", r["buildingId"]),
            ("floor",    r["floorId"]),
        };
    }

    private async Task<IReadOnlyList<(string, string)>> GetFloorAncestors(string floorId, CancellationToken ct)
    {
        var sparql = $@"{Prefixes}
SELECT ?buildingId
WHERE {{
  ?floor a <{Cls_Level}> ; <{Prop_Id}> ""{EscapeLiteral(floorId)}"" .
  ?building <{Prop_HasPart}> ?floor .
  ?building a <{Cls_Building}> ; <{Prop_Id}> ?buildingId .
}}";

        var rows = await _client.QueryAsync(sparql, ct);
        if (rows.Count == 0) return Array.Empty<(string, string)>();
        return new (string, string)[] { ("building", rows[0]["buildingId"]) };
    }

    private async Task<string?> ResolvePointDtId(string pointId, CancellationToken ct)
    {
        var sparql = $@"{Prefixes}
SELECT ?dt WHERE {{
  ?dt a <{Cls_Point}> ; <{Prop_Id}> ""{EscapeLiteral(pointId)}"" .
}}";
        var rows = await _client.QueryAsync(sparql, ct);
        return rows.Count > 0 ? rows[0]["dt"] : null;
    }

    private static string EscapeLiteral(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
