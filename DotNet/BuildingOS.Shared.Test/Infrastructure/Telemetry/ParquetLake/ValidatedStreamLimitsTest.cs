using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;
using NATS.Client.JetStream.Models;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry.ParquetLake;

public class ValidatedStreamLimitsTest
{
    [Fact]
    public void Apply_SetsMaxAge_AndDiscardOldLimitsPolicy()
    {
        var cfg = new StreamConfig("BUILDING_OS_VALIDATED", new[] { "building-os.validated.>" });
        ValidatedStreamLimits.Apply(cfg, TimeSpan.FromHours(24), 0);

        Assert.Equal(TimeSpan.FromHours(24), cfg.MaxAge);
        Assert.Equal(StreamConfigRetention.Limits, cfg.Retention);
        Assert.Equal(StreamConfigDiscard.Old, cfg.Discard);
    }

    [Fact]
    public void Apply_SetsMaxBytes_WhenPositive_OmitsWhenZero()
    {
        var withCap = ValidatedStreamLimits.Apply(
            new StreamConfig("S", new[] { "s.>" }), TimeSpan.FromHours(1), 5_000_000);
        Assert.Equal(5_000_000, withCap.MaxBytes);

        var noCap = ValidatedStreamLimits.Apply(
            new StreamConfig("S", new[] { "s.>" }), TimeSpan.FromHours(1), 0);
        Assert.Equal(0, noCap.MaxBytes); // default (unbounded)
    }
}
