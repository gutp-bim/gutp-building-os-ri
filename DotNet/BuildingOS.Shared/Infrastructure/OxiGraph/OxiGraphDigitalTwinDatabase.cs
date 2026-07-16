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
    {
        var sparql = $@"{Prefixes}
SELECT ?id ?name ?identKey ?identVal ?tagKey ?tagBoolVal WHERE {{
  <{dtId}> a <{Cls_Building}> ; <{Prop_Id}> ?id ; <{Prop_Name}> ?name .
  OPTIONAL {{
    <{dtId}> <{Prop_Identifiers}> ?identEntry .
    ?identEntry a <{Cls_KeyStringMapEntry}> ; <{Prop_Key}> ?identKey ; <{Prop_Value}> ?identVal .
  }}
  OPTIONAL {{
    <{dtId}> <{Prop_CustomTags}> ?tagEntry .
    ?tagEntry a <{Cls_KeyBoolMapEntry}> ; <{Prop_Key}> ?tagKey ; <{Prop_Value}> ?tagBoolVal .
  }}
}}";
        var rows = await _client.QueryAsync(sparql);
        if (rows.Count == 0) return null;
        return MapWithMetadata(rows, r => new Building
        {
            DtId = dtId,
            Id = r.GetValueOrDefault("id", ""),
            Name = r.GetValueOrDefault("name", ""),
        });
    }

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
    {
        var sparql = $@"{Prefixes}
SELECT ?id ?name ?identKey ?identVal ?tagKey ?tagBoolVal WHERE {{
  <{dtId}> a <{Cls_Level}> ; <{Prop_Id}> ?id ; <{Prop_Name}> ?name .
  OPTIONAL {{
    <{dtId}> <{Prop_Identifiers}> ?identEntry .
    ?identEntry a <{Cls_KeyStringMapEntry}> ; <{Prop_Key}> ?identKey ; <{Prop_Value}> ?identVal .
  }}
  OPTIONAL {{
    <{dtId}> <{Prop_CustomTags}> ?tagEntry .
    ?tagEntry a <{Cls_KeyBoolMapEntry}> ; <{Prop_Key}> ?tagKey ; <{Prop_Value}> ?tagBoolVal .
  }}
}}";
        var rows = await _client.QueryAsync(sparql);
        if (rows.Count == 0) return null;
        return MapWithMetadata(rows, r => new Floor
        {
            DtId = dtId,
            Id = r.GetValueOrDefault("id", ""),
            Name = r.GetValueOrDefault("name", ""),
        });
    }

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
    {
        var sparql = $@"{Prefixes}
SELECT ?id ?name ?identKey ?identVal ?tagKey ?tagBoolVal WHERE {{
  <{dtId}> a <{Cls_Space}> ; <{Prop_Id}> ?id ; <{Prop_Name}> ?name .
  OPTIONAL {{
    <{dtId}> <{Prop_Identifiers}> ?identEntry .
    ?identEntry a <{Cls_KeyStringMapEntry}> ; <{Prop_Key}> ?identKey ; <{Prop_Value}> ?identVal .
  }}
  OPTIONAL {{
    <{dtId}> <{Prop_CustomTags}> ?tagEntry .
    ?tagEntry a <{Cls_KeyBoolMapEntry}> ; <{Prop_Key}> ?tagKey ; <{Prop_Value}> ?tagBoolVal .
  }}
}}";
        var rows = await _client.QueryAsync(sparql);
        if (rows.Count == 0) return null;
        return MapWithMetadata(rows, r => new Space
        {
            DtId = dtId,
            Id = r.GetValueOrDefault("id", ""),
            Name = r.GetValueOrDefault("name", ""),
        });
    }

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
    {
        // Single-resource query; SAMPLE not needed — DistinctBy handles any cross-product from OPTIONALs.
        var sparql = $@"{Prefixes}
SELECT ?devId ?devName ?devGw ?identKey ?identVal ?tagKey ?tagBoolVal WHERE {{
  <{dtId}> a <{Cls_Equipment}> ; <{Prop_Id}> ?devId ; <{Prop_Name}> ?devName .
  OPTIONAL {{ <{dtId}> <{Prop_HasPoint}> ?gwPt . ?gwPt <{Prop_GatewayId}> ?devGw . }}
  OPTIONAL {{
    <{dtId}> <{Prop_Identifiers}> ?identEntry .
    ?identEntry a <{Cls_KeyStringMapEntry}> ; <{Prop_Key}> ?identKey ; <{Prop_Value}> ?identVal .
  }}
  OPTIONAL {{
    <{dtId}> <{Prop_CustomTags}> ?tagEntry .
    ?tagEntry a <{Cls_KeyBoolMapEntry}> ; <{Prop_Key}> ?tagKey ; <{Prop_Value}> ?tagBoolVal .
  }}
}}";
        var rows = await _client.QueryAsync(sparql);
        if (rows.Count == 0) return null;
        var device = new Device
        {
            DtId = dtId,
            Id = rows[0].GetValueOrDefault("devId", ""),
            Name = rows[0].GetValueOrDefault("devName", ""),
            GatewayId = rows.Select(r => r.GetValueOrDefault("devGw")).FirstOrDefault(g => g != null),
        };
        device.Identifiers = rows
            .Where(r => r.ContainsKey("identKey") && r.ContainsKey("identVal"))
            .DistinctBy(r => r["identKey"])
            .ToDictionary(r => r["identKey"], r => r["identVal"]);
        device.CustomTags = rows
            .Where(r => r.ContainsKey("tagKey") && r.ContainsKey("tagBoolVal"))
            .DistinctBy(r => r["tagKey"])
            .ToDictionary(r => r["tagKey"], r => r["tagBoolVal"] == "true");
        return device;
    }

    public async Task<BuildingOS.Shared.Point[]> ListPoints(string? deviceDtId)
    {
        if (string.IsNullOrEmpty(deviceDtId))
            return await QueryEntitiesAsync(BuildPointSelect(null), r => MapPoint(r));

        return await QueryEntitiesAsync(BuildPointSelect(deviceDtId), r => MapPoint(r));
    }

    public async Task<BuildingOS.Shared.Point?> GetPoint(string pointId)
    {
        var sparql = $@"{Prefixes}
SELECT ?ptDt ?ptId ?ptName ?ptWritable ?devIdBac ?objType ?instNo ?identKey ?identVal ?tagKey ?tagBoolVal WHERE {{
  ?pt a <{Cls_Point}> ; <{Prop_Id}> ?ptId ; <{Prop_Name}> ?ptName .
  OPTIONAL {{ ?pt <{Prop_Writable}> ?ptWritable . }}
  OPTIONAL {{ ?pt <{Prop_DeviceIdBacnet}> ?devIdBac . }}
  OPTIONAL {{ ?pt <{Prop_ObjectTypeBacnet}> ?objType . }}
  OPTIONAL {{ ?pt <{Prop_InstanceNoBacnet}> ?instNo . }}
  FILTER(?ptId = ""{EscapeStringLiteral(pointId)}"")
  BIND(?pt AS ?ptDt)
  OPTIONAL {{
    ?pt <{Prop_Identifiers}> ?identEntry .
    ?identEntry a <{Cls_KeyStringMapEntry}> ; <{Prop_Key}> ?identKey ; <{Prop_Value}> ?identVal .
  }}
  OPTIONAL {{
    ?pt <{Prop_CustomTags}> ?tagEntry .
    ?tagEntry a <{Cls_KeyBoolMapEntry}> ; <{Prop_Key}> ?tagKey ; <{Prop_Value}> ?tagBoolVal .
  }}
}}";
        var rows = await _client.QueryAsync(sparql);
        if (rows.Count == 0) return null;
        var point = MapPoint(rows[0]);
        point.Identifiers = rows
            .Where(r => r.ContainsKey("identKey") && r.ContainsKey("identVal"))
            .DistinctBy(r => r["identKey"])
            .ToDictionary(r => r["identKey"], r => r["identVal"]);
        point.CustomTags = rows
            .Where(r => r.ContainsKey("tagKey") && r.ContainsKey("tagBoolVal"))
            .DistinctBy(r => r["tagKey"])
            .ToDictionary(r => r["tagKey"], r => r["tagBoolVal"] == "true");
        return point;
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
SELECT ?ptDt ?ptId ?ptName ?ptWritable ?ptSpec ?ptType ?ptGw ?devIdBac ?objType ?instNo
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
  OPTIONAL {{ ?pt <{Prop_DeviceIdBacnet}> ?devIdBac . }}
  OPTIONAL {{ ?pt <{Prop_ObjectTypeBacnet}> ?objType . }}
  OPTIONAL {{ ?pt <{Prop_InstanceNoBacnet}> ?instNo . }}
}}
GROUP BY ?ptDt ?ptId ?ptName ?ptWritable ?ptSpec ?ptType ?ptGw ?devIdBac ?objType ?instNo
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
SELECT ?ptDt ?ptId ?ptName ?ptWritable ?ptSpec ?ptType ?ptGw ?devIdBac ?objType ?instNo
WHERE {{
  ?pt a <{Cls_Point}> ; <{Prop_Id}> ?ptId ; <{Prop_Name}> ?ptName .
  BIND(?pt AS ?ptDt)
  OPTIONAL {{ ?pt <{Prop_Writable}> ?ptWritable . }}
  OPTIONAL {{ ?pt <{Prop_PointSpec}> ?ptSpec . }}
  OPTIONAL {{ ?pt <{Prop_PointType}> ?ptType . }}
  OPTIONAL {{ ?pt <{Prop_GatewayId}> ?ptGw . }}
  OPTIONAL {{ ?pt <{Prop_DeviceIdBacnet}> ?devIdBac . }}
  OPTIONAL {{ ?pt <{Prop_ObjectTypeBacnet}> ?objType . }}
  OPTIONAL {{ ?pt <{Prop_InstanceNoBacnet}> ?instNo . }}
}}"
        : $@"{Prefixes}
SELECT ?ptDt ?ptId ?ptName ?ptWritable ?ptSpec ?ptType ?ptGw ?devIdBac ?objType ?instNo
WHERE {{
  <{deviceUri}> <{Prop_HasPoint}> ?pt .
  ?pt a <{Cls_Point}> ; <{Prop_Id}> ?ptId ; <{Prop_Name}> ?ptName .
  BIND(?pt AS ?ptDt)
  OPTIONAL {{ ?pt <{Prop_Writable}> ?ptWritable . }}
  OPTIONAL {{ ?pt <{Prop_PointSpec}> ?ptSpec . }}
  OPTIONAL {{ ?pt <{Prop_PointType}> ?ptType . }}
  OPTIONAL {{ ?pt <{Prop_GatewayId}> ?ptGw . }}
  OPTIONAL {{ ?pt <{Prop_DeviceIdBacnet}> ?devIdBac . }}
  OPTIONAL {{ ?pt <{Prop_ObjectTypeBacnet}> ?objType . }}
  OPTIONAL {{ ?pt <{Prop_InstanceNoBacnet}> ?instNo . }}
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
            DeviceIdBacnet = r.GetValueOrDefault("devIdBac"),
            ObjectTypeBacnet = r.GetValueOrDefault("objType"),
            InstanceNoBacnet = TryParseNullableInt(r.GetValueOrDefault("instNo")),
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

    private static int? TryParseNullableInt(string? value)
        => int.TryParse(value, out var parsed) ? parsed : null;

    // ── Metadata helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Maps one entity from the first row and attaches identifiers/customTags from all rows.
    /// Uses DistinctBy to collapse any cross-product rows produced by independent OPTIONAL joins.
    /// </summary>
    private static T MapWithMetadata<T>(
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows,
        Func<IReadOnlyDictionary<string, string>, T> mapEntity)
        where T : class
    {
        var entity = mapEntity(rows[0]);
        if (entity is Building b)
        {
            b.Identifiers = ExtractIdentifiers(rows);
            b.CustomTags   = ExtractCustomTags(rows);
        }
        else if (entity is Floor f)
        {
            f.Identifiers = ExtractIdentifiers(rows);
            f.CustomTags   = ExtractCustomTags(rows);
        }
        else if (entity is Space s)
        {
            s.Identifiers = ExtractIdentifiers(rows);
            s.CustomTags   = ExtractCustomTags(rows);
        }
        return entity;
    }

    private static Dictionary<string, string> ExtractIdentifiers(
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows) =>
        rows.Where(r => r.ContainsKey("identKey") && r.ContainsKey("identVal"))
            .DistinctBy(r => r["identKey"])
            .ToDictionary(r => r["identKey"], r => r["identVal"]);

    private static Dictionary<string, bool> ExtractCustomTags(
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows) =>
        rows.Where(r => r.ContainsKey("tagKey") && r.ContainsKey("tagBoolVal"))
            .DistinctBy(r => r["tagKey"])
            .ToDictionary(r => r["tagKey"], r => r["tagBoolVal"] == "true");

    // ── UpdateResourceMetadataAsync ───────────────────────────────────────────

    public async Task UpdateResourceMetadataAsync(
        string dtId,
        Dictionary<string, string?>? identifiers,
        Dictionary<string, bool?>? customTags,
        CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        var nodeIdx = 0;

        if (identifiers != null)
        {
            foreach (var (key, value) in identifiers)
            {
                var escapedKey = EscapeStringLiteral(key);
                // Always delete the existing entry for this key first.
                sb.Append($@"DELETE {{
  <{dtId}> <{Prop_Identifiers}> ?entry .
  ?entry ?p ?v .
}} WHERE {{
  <{dtId}> <{Prop_Identifiers}> ?entry .
  ?entry a <{Cls_KeyStringMapEntry}> ; <{Prop_Key}> ""{escapedKey}"" .
  ?entry ?p ?v .
}} ;
");
                if (value != null)
                {
                    var escapedVal = EscapeStringLiteral(value);
                    var label = SafeBlankNodeLabel(key, nodeIdx++);
                    sb.Append($@"INSERT DATA {{
  <{dtId}> <{Prop_Identifiers}> _:{label} .
  _:{label} a <{Cls_KeyStringMapEntry}> ; <{Prop_Key}> ""{escapedKey}"" ; <{Prop_Value}> ""{escapedVal}"" .
}} ;
");
                }
            }
        }

        if (customTags != null)
        {
            foreach (var (key, value) in customTags)
            {
                var escapedKey = EscapeStringLiteral(key);
                sb.Append($@"DELETE {{
  <{dtId}> <{Prop_CustomTags}> ?entry .
  ?entry ?p ?v .
}} WHERE {{
  <{dtId}> <{Prop_CustomTags}> ?entry .
  ?entry a <{Cls_KeyBoolMapEntry}> ; <{Prop_Key}> ""{escapedKey}"" .
  ?entry ?p ?v .
}} ;
");
                if (value != null)
                {
                    var boolLiteral = value.Value ? "true" : "false";
                    var label = SafeBlankNodeLabel(key, nodeIdx++);
                    sb.Append($@"INSERT DATA {{
  <{dtId}> <{Prop_CustomTags}> _:{label} .
  _:{label} a <{Cls_KeyBoolMapEntry}> ; <{Prop_Key}> ""{escapedKey}"" ; <{Prop_Value}> ""{boolLiteral}""^^xsd:boolean .
}} ;
");
                }
            }
        }

        if (sb.Length == 0) return;

        var sparqlUpdate = $"{Prefixes}{sb.ToString().TrimEnd().TrimEnd(';').TrimEnd()}";
        await _client.UpdateAsync(sparqlUpdate, ct).ConfigureAwait(false);
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static string SafeBlankNodeLabel(string key, int idx) =>
        // SPARQL PN_CHARS forbids spaces, slashes, colons, etc. — use a counter-based label instead.
        $"n{idx}";
}
