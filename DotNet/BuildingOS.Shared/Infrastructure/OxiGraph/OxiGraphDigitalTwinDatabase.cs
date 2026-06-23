using BuildingOS.Shared.Entities;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.Caching.Memory;

namespace BuildingOS.Shared.Infrastructure;

using static OxiGraphOntology;

/// <summary>
/// IDigitalTwinDatabase implementation backed by OxiGraph SPARQL endpoint.
/// Queries use SBCO vocabulary (https://www.sbco.or.jp/ont/); the node URI serves as DtId.
/// Note: SBCO TTL may not include Space (SpaceExt) nodes or sbco:locatedIn relationships.
/// In that case, space-filtered queries return empty and space fields in detail responses are empty.
/// Equipment–floor association uses sbco:floor string matching against Level sbco:name.
/// </summary>
public class OxiGraphDigitalTwinDatabase : IDigitalTwinDatabase
{
    private readonly OxiGraphClient _client;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public OxiGraphDigitalTwinDatabase(OxiGraphClient client, IMemoryCache cache)
    {
        _client = client;
        _cache = cache;
    }

    public async Task<Building[]> ListBuildings()
        => await CachedQueryAsync("buildings", () => QueryEntitiesAsync(
            $"{Prefixes} SELECT ?dt ?id ?name WHERE {{ ?dt a <{Cls_Building}> ; <{Prop_Id}> ?id ; <{Prop_Name}> ?name . }}",
            r => new Building { DtId = r["dt"], Id = r["id"], Name = r["name"] }));

    public async Task<Building?> GetBuilding(string dtId)
        => (await ListBuildings()).FirstOrDefault(b => b.DtId == dtId);

    public async Task<Floor[]> ListFloors(string? buildingDtId)
    {
        if (string.IsNullOrEmpty(buildingDtId))
            return await CachedQueryAsync("floors_all", () => QueryEntitiesAsync(
                $"{Prefixes} SELECT ?dt ?id ?name WHERE {{ ?dt a <{Cls_Level}> ; <{Prop_Id}> ?id ; <{Prop_Name}> ?name . }}",
                r => new Floor { DtId = r["dt"], Id = r.GetValueOrDefault("id", ""), Name = r.GetValueOrDefault("name", "") }));

        return await QueryEntitiesAsync(
            $"{Prefixes} SELECT ?dt ?id ?name WHERE {{ <{buildingDtId}> <{Prop_HasPart}> ?dt . ?dt a <{Cls_Level}> ; <{Prop_Id}> ?id ; <{Prop_Name}> ?name . }}",
            r => new Floor { DtId = r["dt"], Id = r.GetValueOrDefault("id", ""), Name = r.GetValueOrDefault("name", "") });
    }

    public async Task<Floor?> GetFloor(string dtId)
        => (await ListFloors(null)).FirstOrDefault(f => f.DtId == dtId);

    public async Task<Space[]> ListSpaces(string? floorDtId)
    {
        if (string.IsNullOrEmpty(floorDtId))
            return await QueryEntitiesAsync(
                $"{Prefixes} SELECT ?dt ?id ?name WHERE {{ ?dt a <{Cls_Space}> ; <{Prop_Id}> ?id ; <{Prop_Name}> ?name . }}",
                r => new Space { DtId = r["dt"], Id = r.GetValueOrDefault("id", ""), Name = r.GetValueOrDefault("name", "") });

        return await QueryEntitiesAsync(
            $"{Prefixes} SELECT ?dt ?id ?name WHERE {{ <{floorDtId}> <{Prop_HasPart}> ?dt . ?dt a <{Cls_Space}> ; <{Prop_Id}> ?id ; <{Prop_Name}> ?name . }}",
            r => new Space { DtId = r["dt"], Id = r.GetValueOrDefault("id", ""), Name = r.GetValueOrDefault("name", "") });
    }

    public async Task<Space?> GetSpace(string dtId)
        => (await ListSpaces(null)).FirstOrDefault(s => s.DtId == dtId);

    public async Task<Device[]> ListDevices(string? spaceDtId)
    {
        // SBCO TTL may not have sbco:locatedIn; space-filtered queries return empty in that case.
        // SAMPLE aggregates gatewayId across all points of a device for deterministic selection.
        if (string.IsNullOrEmpty(spaceDtId))
        {
            var rows = await _client.QueryAsync(
                $"{Prefixes} SELECT ?devDt ?devId ?devName (SAMPLE(?gwRaw) AS ?devGw) WHERE {{ ?dev a <{Cls_Equipment}> ; <{Prop_Id}> ?devId ; <{Prop_Name}> ?devName . BIND(?dev AS ?devDt) OPTIONAL {{ ?dev <{Prop_HasPoint}> ?pt . ?pt <{Prop_GatewayId}> ?gwRaw . }} }} GROUP BY ?devDt ?devId ?devName");
            return rows.Select(MapDevice).ToArray();
        }

        var spaceRows = await _client.QueryAsync(
            $"{Prefixes} SELECT ?devDt ?devId ?devName (SAMPLE(?gwRaw) AS ?devGw) WHERE {{ ?dev a <{Cls_Equipment}> ; <{Prop_Id}> ?devId ; <{Prop_Name}> ?devName ; <{Prop_LocatedIn}> <{spaceDtId}> . BIND(?dev AS ?devDt) OPTIONAL {{ ?dev <{Prop_HasPoint}> ?pt . ?pt <{Prop_GatewayId}> ?gwRaw . }} }} GROUP BY ?devDt ?devId ?devName");
        return spaceRows.Select(MapDevice).ToArray();
    }

    public async Task<Device?> GetDevice(string dtId)
        => (await ListDevices(null)).FirstOrDefault(d => d.DtId == dtId);

    public async Task<BuildingOS.Shared.Point[]> ListPoints(string? deviceDtId)
    {
        if (string.IsNullOrEmpty(deviceDtId))
            return await QueryEntitiesAsync(BuildPointSelect(null), r => MapPoint(r));

        return await QueryEntitiesAsync(BuildPointSelect(deviceDtId), r => MapPoint(r));
    }

    public async Task<BuildingOS.Shared.Point?> GetPoint(string pointId)
    {
        var sparql = $"{Prefixes} SELECT ?ptDt ?ptId ?ptName ?ptWritable WHERE {{ ?pt a <{Cls_Point}> ; <{Prop_Id}> ?ptId ; <{Prop_Name}> ?ptName . OPTIONAL {{ ?pt <{Prop_Writable}> ?ptWritable . }} FILTER(?ptId = \"{EscapeStringLiteral(pointId)}\") BIND(?pt AS ?ptDt) }}";
        var rows = await _client.QueryAsync(sparql);
        if (rows.Count == 0) return null;
        return MapPoint(rows[0]);
    }

    public async Task<PointDetail?> GetPointDetailByPointId(string pointId)
    {
        var point = await GetPoint(pointId);
        if (point == null) return null;

        var pointUri = point.DtId;
        var sparql = $@"{Prefixes}
SELECT ?floorDt ?floorId ?floorName ?spaceDt ?spaceId ?spaceName ?devDt ?devId ?devName (SAMPLE(?gwRaw) AS ?devGw)
WHERE {{
  ?dev <{Prop_HasPoint}> <{pointUri}> ;
       a <{Cls_Equipment}> ; <{Prop_Id}> ?devId ; <{Prop_Name}> ?devName .
  BIND(?dev AS ?devDt)
  OPTIONAL {{ ?dev <{Prop_HasPoint}> ?gwPt . ?gwPt <{Prop_GatewayId}> ?gwRaw . }}
  OPTIONAL {{
    ?dev <{Prop_LocatedIn}> ?space .
    BIND(?space AS ?spaceDt)
    ?space <{Prop_Id}> ?spaceId ; <{Prop_Name}> ?spaceName .
    ?floor <{Prop_HasPart}> ?space .
    ?floor a <{Cls_Level}> ; <{Prop_Id}> ?floorId ; <{Prop_Name}> ?floorName .
    BIND(?floor AS ?floorDt)
  }}
}}
GROUP BY ?floorDt ?floorId ?floorName ?spaceDt ?spaceId ?spaceName ?devDt ?devId ?devName";

        var rows = await _client.QueryAsync(sparql);
        if (rows.Count == 0) return null;
        var r = rows[0];
        return new PointDetail
        {
            Point = point,
            Floor = new Floor { DtId = r.GetValueOrDefault("floorDt", ""), Id = r.GetValueOrDefault("floorId", ""), Name = r.GetValueOrDefault("floorName", "") },
            Space = new Space { DtId = r.GetValueOrDefault("spaceDt", ""), Id = r.GetValueOrDefault("spaceId", ""), Name = r.GetValueOrDefault("spaceName", "") },
            Device = MapDevice(r),
        };
    }

    public async Task<PointDetail[]> ListPointDetails(string buildingDtId)
    {
        // sbco:floor (string literal on EquipmentExt) is the only path from building to equipment in SBCO;
        // making this join OPTIONAL would remove building scoping and return all equipment everywhere.
        // Equipment without sbco:floor or with a mismatched floor literal will not appear.
        // SAMPLE aggregates gatewayId across all points of a device for deterministic selection.
        var sparql = $@"{Prefixes}
SELECT ?ptDt ?ptId ?ptName ?ptWritable ?ptSpec ?ptType ?ptGw
       ?floorDt ?floorId ?floorName ?spaceDt ?spaceId ?spaceName ?devDt ?devId ?devName (SAMPLE(?gwRaw) AS ?devGw)
WHERE {{
  <{buildingDtId}> <{Prop_HasPart}> ?floor .
  ?floor a <{Cls_Level}> ; <{Prop_Id}> ?floorId ; <{Prop_Name}> ?floorName .
  BIND(?floor AS ?floorDt)
  ?dev a <{Cls_Equipment}> ; <{Prop_Id}> ?devId ; <{Prop_Name}> ?devName ; <{Prop_Floor}> ?floorName .
  BIND(?dev AS ?devDt)
  OPTIONAL {{ ?dev <{Prop_HasPoint}> ?gwPt . ?gwPt <{Prop_GatewayId}> ?gwRaw . }}
  OPTIONAL {{
    ?dev <{Prop_LocatedIn}> ?space .
    BIND(?space AS ?spaceDt)
    ?space <{Prop_Id}> ?spaceId ; <{Prop_Name}> ?spaceName .
  }}
  ?dev <{Prop_HasPoint}> ?pt .
  ?pt a <{Cls_Point}> ; <{Prop_Id}> ?ptId ; <{Prop_Name}> ?ptName .
  BIND(?pt AS ?ptDt)
  OPTIONAL {{ ?pt <{Prop_Writable}> ?ptWritable . }}
  OPTIONAL {{ ?pt <{Prop_PointSpec}> ?ptSpec . }}
  OPTIONAL {{ ?pt <{Prop_PointType}> ?ptType . }}
  OPTIONAL {{ ?pt <{Prop_GatewayId}> ?ptGw . }}
}}
GROUP BY ?ptDt ?ptId ?ptName ?ptWritable ?ptSpec ?ptType ?ptGw
         ?floorDt ?floorId ?floorName ?spaceDt ?spaceId ?spaceName ?devDt ?devId ?devName";

        var rows = await _client.QueryAsync(sparql);
        return rows.Select(r => new PointDetail
        {
            Point = MapPoint(r),
            Floor = new Floor { DtId = r.GetValueOrDefault("floorDt", ""), Id = r.GetValueOrDefault("floorId", ""), Name = r.GetValueOrDefault("floorName", "") },
            Space = new Space { DtId = r.GetValueOrDefault("spaceDt", ""), Id = r.GetValueOrDefault("spaceId", ""), Name = r.GetValueOrDefault("spaceName", "") },
            Device = MapDevice(r),
        }).ToArray();
    }

    public async Task<DeviceDetail[]> ListDeviceDetails(string buildingDtId)
    {
        // sbco:floor (string literal on EquipmentExt) is the only path from building to equipment in SBCO;
        // making this join OPTIONAL would remove building scoping and return all equipment everywhere.
        // Equipment without sbco:floor or with a mismatched floor literal will not appear.
        var sparql = $@"{Prefixes}
SELECT ?devDt ?devId ?devName (SAMPLE(?gwRaw) AS ?devGw) ?floorDt ?floorId ?floorName ?spaceDt ?spaceId ?spaceName
WHERE {{
  <{buildingDtId}> <{Prop_HasPart}> ?floor .
  ?floor a <{Cls_Level}> ; <{Prop_Id}> ?floorId ; <{Prop_Name}> ?floorName .
  BIND(?floor AS ?floorDt)
  ?dev a <{Cls_Equipment}> ; <{Prop_Id}> ?devId ; <{Prop_Name}> ?devName ; <{Prop_Floor}> ?floorName .
  BIND(?dev AS ?devDt)
  OPTIONAL {{ ?dev <{Prop_HasPoint}> ?pt . ?pt <{Prop_GatewayId}> ?gwRaw . }}
  OPTIONAL {{
    ?dev <{Prop_LocatedIn}> ?space .
    BIND(?space AS ?spaceDt)
    ?space <{Prop_Id}> ?spaceId ; <{Prop_Name}> ?spaceName .
  }}
}}
GROUP BY ?devDt ?devId ?devName ?floorDt ?floorId ?floorName ?spaceDt ?spaceId ?spaceName";

        var rows = await _client.QueryAsync(sparql);
        return rows.Select(r => new DeviceDetail
        {
            Device = MapDevice(r),
            Floor = new Floor { DtId = r.GetValueOrDefault("floorDt", ""), Id = r.GetValueOrDefault("floorId", ""), Name = r.GetValueOrDefault("floorName", "") },
            Space = new Space { DtId = r.GetValueOrDefault("spaceDt", ""), Id = r.GetValueOrDefault("spaceId", ""), Name = r.GetValueOrDefault("spaceName", "") },
        }).ToArray();
    }

    public async Task<GatewayPointEntry[]> ListGatewayPointList(string gatewayId)
    {
        // All points the gateway owns (sbco:gatewayId), with native addressing, unit, writability,
        // control schema (bos:*) and owning device — all OPTIONAL so a bare point still returns.
        var sparql = $@"{Prefixes}
SELECT ?ptId ?ptName ?localId ?devIdBac ?objType ?instNo ?unit ?writable ?dataType ?minV ?maxV ?enumLabels ?devDt ?devId ?devName
WHERE {{
  ?pt a <{Cls_Point}> ; <{Prop_Id}> ?ptId ; <{Prop_GatewayId}> ""{EscapeStringLiteral(gatewayId)}"" .
  OPTIONAL {{ ?pt <{Prop_Name}> ?ptName . }}
  OPTIONAL {{ ?pt <{Prop_LocalId}> ?localId . }}
  OPTIONAL {{ ?pt <{Prop_DeviceIdBacnet}> ?devIdBac . }}
  OPTIONAL {{ ?pt <{Prop_ObjectTypeBacnet}> ?objType . }}
  OPTIONAL {{ ?pt <{Prop_InstanceNoBacnet}> ?instNo . }}
  OPTIONAL {{ ?pt <{Prop_Unit}> ?unit . }}
  OPTIONAL {{ ?pt <{Prop_Writable}> ?writable . }}
  OPTIONAL {{ ?pt <{Prop_DataType}> ?dataType . }}
  OPTIONAL {{ ?pt <{Prop_MinValue}> ?minV . }}
  OPTIONAL {{ ?pt <{Prop_MaxValue}> ?maxV . }}
  OPTIONAL {{ ?pt <{Prop_EnumLabels}> ?enumLabels . }}
  OPTIONAL {{ ?dev a <{Cls_Equipment}> ; <{Prop_HasPoint}> ?pt ; <{Prop_Id}> ?devId . BIND(?dev AS ?devDt) OPTIONAL {{ ?dev <{Prop_Name}> ?devName . }} }}
}}
ORDER BY ?ptId";

        var rows = await _client.QueryAsync(sparql);
        return rows.Select(r => new GatewayPointEntry
        {
            PointId = r.GetValueOrDefault("ptId", ""),
            LocalId = r.GetValueOrDefault("localId"),
            BacnetDeviceId = r.GetValueOrDefault("devIdBac"),
            BacnetObjectType = r.GetValueOrDefault("objType"),
            BacnetInstanceNo = r.GetValueOrDefault("instNo"),
            Unit = r.GetValueOrDefault("unit"),
            Writable = r.TryGetValue("writable", out var w) ? w == "true" : null,
            DataType = r.GetValueOrDefault("dataType"),
            MinValue = r.GetValueOrDefault("minV"),
            MaxValue = r.GetValueOrDefault("maxV"),
            EnumLabels = r.GetValueOrDefault("enumLabels"),
            DeviceDtId = r.GetValueOrDefault("devDt"),
            DeviceId = r.GetValueOrDefault("devId"),
            DeviceName = r.GetValueOrDefault("devName"),
        })
        // Guard against duplicate rows if a point is (incorrectly) linked to multiple devices via the
        // OPTIONAL hasPoint join — one entry per point (deterministic: ORDER BY ?ptId above).
        .DistinctBy(e => e.PointId)
        .ToArray();
    }

    public async Task<string[]> ListGatewayIds()
    {
        var sparql = $@"{Prefixes}
SELECT DISTINCT ?gw WHERE {{ ?pt a <{Cls_Point}> ; <{Prop_GatewayId}> ?gw . }}
ORDER BY ?gw";

        var rows = await _client.QueryAsync(sparql);
        return rows
            .Select(r => r.GetValueOrDefault("gw", ""))
            .Where(g => !string.IsNullOrEmpty(g))
            .ToArray();
    }

    public async Task<ResourceSearchHit[]> SearchResources(string? q, string? type, string? buildingDtId, IReadOnlyList<string> tags, int limit, int offset)
    {
        var sparql = ResourceSearchQueryBuilder.Build(q, type, buildingDtId, tags, limit, offset);
        var rows = await _client.QueryAsync(sparql);
        return rows.Select(r => new ResourceSearchHit
        {
            Type = r.GetValueOrDefault("type", ""),
            DtId = r.GetValueOrDefault("dt", ""),
            Id = r.GetValueOrDefault("id", ""),
            Name = r.GetValueOrDefault("name", ""),
            // When the search was building-scoped the owning building is known; otherwise leave null.
            BuildingDtId = string.IsNullOrEmpty(buildingDtId) ? null : buildingDtId,
        }).ToArray();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<T[]> QueryEntitiesAsync<T>(string sparql, Func<IReadOnlyDictionary<string, string>, T> map)
    {
        var rows = await _client.QueryAsync(sparql);
        return rows.Select(map).ToArray();
    }

    private async Task<T[]> CachedQueryAsync<T>(string cacheKey, Func<Task<T[]>> factory)
        => await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            return await factory();
        }) ?? Array.Empty<T>();

    private static string BuildPointSelect(string? deviceUri) => deviceUri is null
        ? $@"{Prefixes}
SELECT ?ptDt ?ptId ?ptName ?ptWritable ?ptSpec ?ptType ?ptGw
WHERE {{
  ?pt a <{Cls_Point}> ; <{Prop_Id}> ?ptId ; <{Prop_Name}> ?ptName .
  BIND(?pt AS ?ptDt)
  OPTIONAL {{ ?pt <{Prop_Writable}> ?ptWritable . }}
  OPTIONAL {{ ?pt <{Prop_PointSpec}> ?ptSpec . }}
  OPTIONAL {{ ?pt <{Prop_PointType}> ?ptType . }}
  OPTIONAL {{ ?pt <{Prop_GatewayId}> ?ptGw . }}
}}"
        : $@"{Prefixes}
SELECT ?ptDt ?ptId ?ptName ?ptWritable ?ptSpec ?ptType ?ptGw
WHERE {{
  <{deviceUri}> <{Prop_HasPoint}> ?pt .
  ?pt a <{Cls_Point}> ; <{Prop_Id}> ?ptId ; <{Prop_Name}> ?ptName .
  BIND(?pt AS ?ptDt)
  OPTIONAL {{ ?pt <{Prop_Writable}> ?ptWritable . }}
  OPTIONAL {{ ?pt <{Prop_PointSpec}> ?ptSpec . }}
  OPTIONAL {{ ?pt <{Prop_PointType}> ?ptType . }}
  OPTIONAL {{ ?pt <{Prop_GatewayId}> ?ptGw . }}
}}";

    private static BuildingOS.Shared.Point MapPoint(IReadOnlyDictionary<string, string> r) =>
        new()
        {
            DtId = r.GetValueOrDefault("ptDt", ""),
            Id = r.GetValueOrDefault("ptId", ""),
            Name = r.GetValueOrDefault("ptName", ""),
            Writable = r.TryGetValue("ptWritable", out var w) ? w == "true" : null,
            Specification = r.GetValueOrDefault("ptSpec"),
            Type = r.GetValueOrDefault("ptType"),
            GatewayName = r.GetValueOrDefault("ptGw"),
        };

    private static Device MapDevice(IReadOnlyDictionary<string, string> r) =>
        new()
        {
            DtId = r.GetValueOrDefault("devDt", ""),
            Id = r.GetValueOrDefault("devId", ""),
            Name = r.GetValueOrDefault("devName", ""),
            GatewayId = r.GetValueOrDefault("devGw"),
        };

    private static string EscapeStringLiteral(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
