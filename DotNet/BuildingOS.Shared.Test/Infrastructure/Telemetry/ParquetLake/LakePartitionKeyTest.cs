using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry.ParquetLake;

public class LakePartitionKeyTest
{
    [Fact]
    public void For_MatchesReaderLayout()
    {
        var key = LakePartitionKey.For("bldg-1", new DateTime(2026, 6, 12, 9, 0, 0, DateTimeKind.Utc), 100, 250);
        Assert.Equal("building_id=bldg-1/year=2026/month=06/day=12/hour=09/part-100-250.parquet", key);
    }

    [Fact]
    public void For_IsDeterministic_SameSeqRange_SameKey()
    {
        var hour = new DateTime(2026, 6, 12, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal(LakePartitionKey.For("b", hour, 1, 5), LakePartitionKey.For("b", hour, 1, 5));
    }

    [Fact]
    public void For_RoundtripsThroughPlannerParser()
    {
        var hour = new DateTime(2026, 6, 12, 9, 0, 0, DateTimeKind.Utc);
        var key = LakePartitionKey.For("b", hour, 1, 5);
        Assert.True(PartitionKeyRangePlanner.TryParsePartitionStart(key, out var parsed));
        Assert.Equal(hour, parsed);
    }
}
