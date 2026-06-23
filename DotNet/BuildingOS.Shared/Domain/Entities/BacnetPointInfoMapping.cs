using BuildingOS.Shared.Module;
using TinyCsvParser.Mapping;

namespace BuildingOS.Shared;

public class BacnetPointInfoMapping : CsvMapping<BacnetPointInfo>
{
    public BacnetPointInfoMapping() : base()
    {
        MapProperty(1, x => x.DeviceIdBacnet);
        MapProperty(2, x => x.InstanceNoBacnet);
        MapProperty(3, x => x.Name);
        MapProperty(4, x => x.ObjectTypeBacnet);
        MapProperty(5, x => x.PointId);
        MapProperty(6, x => x.PointName);
        MapProperty(7, x => x.PointSpecification);
    }
}
