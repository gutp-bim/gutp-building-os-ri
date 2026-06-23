using BuildingOS.Shared.Infrastructure.OxiGraph;

namespace BuildingOS.Shared.Module.Oss;

/// <summary>
/// Loads all point metadata (building / name / gatewayId / owning device id) from OxiGraph via
/// SPARQL. Performs no caching — <see cref="PointMetadataCache"/> owns the cache lifecycle.
/// </summary>
public sealed class OxiGraphPointMetadataDataSource(OxiGraphClient client) : IPointMetadataDataSource
{
    private const string Query = """
        PREFIX sbco: <https://www.sbco.or.jp/ont/>
        SELECT ?pointId ?building ?name ?gatewayId ?deviceId WHERE {
          ?point a sbco:PointExt ;
                 sbco:id ?pointId .
          OPTIONAL { ?point sbco:building ?building }
          OPTIONAL { ?point sbco:name ?name }
          OPTIONAL { ?point sbco:gatewayId ?gatewayId }
          OPTIONAL { ?equip a sbco:EquipmentExt ; sbco:hasPoint ?point ; sbco:id ?deviceId }
        }
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
                GatewayId: Get(r, "gatewayId")))
            .Where(m => !string.IsNullOrEmpty(m.PointId))
            .ToArray();
    }

    private static string Get(IReadOnlyDictionary<string, string> row, string key)
        => row.TryGetValue(key, out var v) ? v : string.Empty;
}
