using BuildingOS.Shared.Infrastructure.OxiGraph;

namespace BuildingOS.Shared.Module.Oss;

/// <summary>
/// Fetches all localId → pointId mappings from OxiGraph via SPARQL.
/// This class performs no caching; caching is handled by PointIdFactory
/// (lazy-loads on first call and holds the result for the process lifetime).
/// </summary>
public class OxiGraphPointIdDataSource(OxiGraphClient client) : IPointIdDataSource
{
    private const string Query = """
        PREFIX sbco: <https://www.sbco.or.jp/ont/>
        SELECT ?localId ?pointId WHERE {
          ?point a sbco:PointExt ;
                 sbco:localId ?localId ;
                 sbco:id ?pointId .
        }
        """;

    public async Task<PointIdInfo[]> GetPointIdInfosAsync()
    {
        var rows = await client.QueryAsync(Query).ConfigureAwait(false);
        return rows
            .Select(r => new PointIdInfo { Key = r["localId"], PointId = r["pointId"] })
            .ToArray();
    }
}
