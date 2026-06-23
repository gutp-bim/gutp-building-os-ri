using TinyCsvParser.Mapping;

namespace BuildingOS.Shared;

public class PointIdInfoMapping : CsvMapping<PointIdInfo>
{
    public PointIdInfoMapping() : base()
    {
        MapProperty(0, x => x.Key);
        MapProperty(1, x => x.PointId);
    }
}