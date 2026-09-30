using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure.OxiGraph;

namespace BuildingOS.Shared.Infrastructure.Authorization;

using static OxiGraphOntology;

/// <summary>
/// IResourceHierarchyResolver backed by OxiGraph SPARQL, traversing the twin's topology only
/// (hasPart / locatedIn / hasPoint): equipment is located in a Room or directly in a Level. The
/// <c>sbco:floor</c> literal is metadata and places nothing. A resource with more than one placement
/// has the union of every chain as ancestors, so the answer never depends on SPARQL row order.
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
    {{
      ?dev <{Prop_LocatedIn}> ?space .
      ?space a <{Cls_Space}> ; <{Prop_Id}> ?spaceId .
      ?floor <{Prop_HasPart}> ?space .
      ?floor a <{Cls_Level}> ; <{Prop_Id}> ?floorId .
      ?building <{Prop_HasPart}> ?floor .
      ?building a <{Cls_Building}> ; <{Prop_Id}> ?buildingId .
    }} UNION {{
      ?dev <{Prop_LocatedIn}> ?floor .
      ?floor a <{Cls_Level}> ; <{Prop_Id}> ?floorId .
      ?building <{Prop_HasPart}> ?floor .
      ?building a <{Cls_Building}> ; <{Prop_Id}> ?buildingId .
    }}
  }}
}}";

        var rows = await _client.QueryAsync(sparql, ct);
        return Union(rows, ("buildingId", "building"), ("floorId", "floor"), ("spaceId", "space"), ("devId", "device"));
    }

    private async Task<IReadOnlyList<(string, string)>> GetDeviceAncestors(string deviceId, CancellationToken ct)
    {
        var sparql = $@"{Prefixes}
SELECT ?buildingId ?floorId ?spaceId
WHERE {{
  ?dev a <{Cls_Equipment}> ; <{Prop_Id}> ""{EscapeLiteral(deviceId)}"" .
  OPTIONAL {{
    {{
      ?dev <{Prop_LocatedIn}> ?space .
      ?space a <{Cls_Space}> ; <{Prop_Id}> ?spaceId .
      ?floor <{Prop_HasPart}> ?space .
      ?floor a <{Cls_Level}> ; <{Prop_Id}> ?floorId .
      ?building <{Prop_HasPart}> ?floor .
      ?building a <{Cls_Building}> ; <{Prop_Id}> ?buildingId .
    }} UNION {{
      ?dev <{Prop_LocatedIn}> ?floor .
      ?floor a <{Cls_Level}> ; <{Prop_Id}> ?floorId .
      ?building <{Prop_HasPart}> ?floor .
      ?building a <{Cls_Building}> ; <{Prop_Id}> ?buildingId .
    }}
  }}
}}";

        var rows = await _client.QueryAsync(sparql, ct);
        return Union(rows, ("buildingId", "building"), ("floorId", "floor"), ("spaceId", "space"));
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
        return Union(rows, ("buildingId", "building"), ("floorId", "floor"));
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
        return Union(rows, ("buildingId", "building"));
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

    /// <summary>
    /// Every (type, id) any row binds, deduplicated in first-seen order. A resource placed more than once
    /// (in a Room and directly on another Level, say) has several rows; each is a real chain, so the
    /// ancestors are all of them — never just whichever row the store happened to return first.
    /// </summary>
    private static IReadOnlyList<(string, string)> Union(
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows, params (string Var, string Type)[] columns)
    {
        var seen = new HashSet<(string, string)>();
        var result = new List<(string, string)>();
        foreach (var row in rows)
            foreach (var (var, type) in columns)
                if (row.TryGetValue(var, out var id) && seen.Add((type, id))) result.Add((type, id));
        return result;
    }

    private static string EscapeLiteral(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
