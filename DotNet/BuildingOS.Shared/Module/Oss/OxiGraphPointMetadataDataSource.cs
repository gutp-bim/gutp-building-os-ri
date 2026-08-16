using BuildingOS.Shared.Infrastructure.OxiGraph;

namespace BuildingOS.Shared.Module.Oss;

/// <summary>
/// Loads all point metadata (building / name / gatewayId / owning device id) from OxiGraph via
/// SPARQL. Performs no caching — <see cref="PointMetadataCache"/> owns the cache lifecycle.
/// </summary>
public sealed class OxiGraphPointMetadataDataSource(OxiGraphClient client) : IPointMetadataDataSource
{
    // ?building is the denormalized literal, kept verbatim for enrichment. ?bldgPath is the separate
    // question of whether the twin actually places the point under a Building node, and is resolved
    // by traversal — the two are deliberately different values (#292).
    //
    // The traversal mirrors the import-time orphan definition exactly (#291,
    // OxiGraphTwinAdminService.OrphanPattern): from the OWNING EQUIPMENT, via the spatial chain or
    // direct Level location, or the sbco:floor literal join, ANY of which counts. Anchoring on the
    // equipment rather than the point matters — the two latter paths live on EquipmentExt.
    //
    // BOUND(?bldg) rather than a second query: this is the bulk load behind a TTL cache
    // (PointMetadataCache), so the cost is periodic, not per-frame.
    private const string Query = """
        PREFIX sbco: <https://www.sbco.or.jp/ont/>
        SELECT ?pointId ?building ?name ?gatewayId ?deviceId (SAMPLE(?bldg) AS ?bldgPath) WHERE {
          ?point a sbco:PointExt ;
                 sbco:id ?pointId .
          OPTIONAL { ?point sbco:building ?building }
          OPTIONAL { ?point sbco:name ?name }
          OPTIONAL { ?point sbco:gatewayId ?gatewayId }
          OPTIONAL { ?equip a sbco:EquipmentExt ; sbco:hasPoint ?point ; sbco:id ?deviceId }
          OPTIONAL {
            {
              ?anyDev sbco:hasPoint ?point ;
                      sbco:locatedIn ?anyRoom .
              ?anyRoom a sbco:Room .
              ?anyFloor sbco:hasPart ?anyRoom ;
                        a sbco:Level .
              ?bldg sbco:hasPart ?anyFloor ;
                    a sbco:Building .
            } UNION {
              ?anyDev sbco:hasPoint ?point ;
                      sbco:locatedIn ?anyFloor .
              ?anyFloor a sbco:Level .
              ?bldg sbco:hasPart ?anyFloor ;
                    a sbco:Building .
            } UNION {
              ?anyDev sbco:hasPoint ?point ;
                      sbco:floor ?anyFloorName .
              ?anyFloor a sbco:Level ;
                        sbco:name ?anyFloorName .
              ?bldg sbco:hasPart ?anyFloor ;
                    a sbco:Building .
            }
          }
        }
        GROUP BY ?pointId ?building ?name ?gatewayId ?deviceId
        """;

    public async Task<PointMetadata[]> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var rows = await client.QueryAsync(Query, cancellationToken).ConfigureAwait(false);
        return rows
            .Select(r => new PointMetadata(
                PointId: Get(r, "pointId"),
                Building: Get(r, "building"),
                Name: Get(r, "name"),
                DeviceId: Get(r, "deviceId"),
                GatewayId: Get(r, "gatewayId"),
                HasBuildingPath: !string.IsNullOrEmpty(Get(r, "bldgPath"))))
            .Where(m => !string.IsNullOrEmpty(m.PointId))
            .ToArray();
    }

    private static string Get(IReadOnlyDictionary<string, string> row, string key)
        => row.TryGetValue(key, out var v) ? v : string.Empty;
}
