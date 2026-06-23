using BuildingOS.Shared.Infrastructure.Telemetry;

namespace BuildingOS.Shared.Test.Infrastructure.TelemetryDatabase;

public class ParityMetricsTest
{
    [Fact]
    public void ComputeDiff_IdenticalData_ZeroDiff()
    {
        var data = new[] { new ValidTelemetryData { Value = 1.0 }, new ValidTelemetryData { Value = 2.0 } };
        var diff = ParityMetrics.ComputeDiff("p1", data, data);

        Assert.Equal(0, diff.CountDiff);
        Assert.Equal(0.0, diff.SumDiff, precision: 6);
        Assert.Equal(0.0, diff.AvgDiff, precision: 6);
    }

    [Fact]
    public void ComputeDiff_CountMismatch_ReportsCountDiff()
    {
        var primary = new[] { new ValidTelemetryData { Value = 1.0 }, new ValidTelemetryData { Value = 2.0 } };
        var secondary = new[] { new ValidTelemetryData { Value = 1.0 } };

        var diff = ParityMetrics.ComputeDiff("p1", primary, secondary);

        Assert.Equal(1, diff.CountDiff);
    }

    [Fact]
    public void ComputeDiff_ValueMismatch_ReportsSumDiff()
    {
        var primary   = new[] { new ValidTelemetryData { Value = 10.0 } };
        var secondary = new[] { new ValidTelemetryData { Value = 10.1 } };

        var diff = ParityMetrics.ComputeDiff("p1", primary, secondary);

        Assert.Equal(0.1, diff.SumDiff, precision: 6);
    }

    [Fact]
    public void ComputeDiff_EmptySecondary_AllDiff()
    {
        var primary = new[] { new ValidTelemetryData { Value = 5.0 } };
        var diff = ParityMetrics.ComputeDiff("p1", primary, Array.Empty<ValidTelemetryData>());

        Assert.Equal(1, diff.CountDiff);
        Assert.Equal(5.0, diff.SumDiff, precision: 6);
    }
}
