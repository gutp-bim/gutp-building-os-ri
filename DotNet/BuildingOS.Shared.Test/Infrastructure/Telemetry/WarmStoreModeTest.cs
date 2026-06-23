using BuildingOS.Shared.Infrastructure.Telemetry;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry;

public class WarmStoreModeTest
{
    [Theory]
    [InlineData(null)]      // unset → default parquet (cost-minimal default, #216)
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("parquet")]
    [InlineData("PARQUET")]
    [InlineData("anything-else")]
    public void IsParquet_DefaultsToParquet(string? value)
    {
        Assert.True(WarmStoreMode.IsParquet(value));
    }

    [Theory]
    [InlineData("timescale")]
    [InlineData("TimeScale")]
    [InlineData("  timescale  ")] // trimmed
    public void IsParquet_FalseOnlyForExplicitTimescale(string value)
    {
        Assert.False(WarmStoreMode.IsParquet(value));
    }
}
