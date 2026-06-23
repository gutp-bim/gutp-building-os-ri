namespace BuildingOS.Shared.Infrastructure.Telemetry;

public record TelemetryParityDiff(
    string PointId,
    int CountDiff,
    double SumDiff,
    double AvgDiff,
    double MinDiff,
    double MaxDiff
);

public static class ParityMetrics
{
    public static TelemetryParityDiff ComputeDiff(
        string pointId,
        ValidTelemetryData[] primary,
        ValidTelemetryData[] secondary)
    {
        double PrimarySum()  => primary.Sum(x => x.Value ?? 0);
        double SecondarySum() => secondary.Sum(x => x.Value ?? 0);
        double PrimaryAvg()  => primary.Length > 0 ? primary.Average(x => x.Value ?? 0) : 0;
        double SecondaryAvg() => secondary.Length > 0 ? secondary.Average(x => x.Value ?? 0) : 0;
        double PrimaryMin()  => primary.Length > 0 ? primary.Min(x => x.Value ?? 0) : 0;
        double SecondaryMin() => secondary.Length > 0 ? secondary.Min(x => x.Value ?? 0) : 0;
        double PrimaryMax()  => primary.Length > 0 ? primary.Max(x => x.Value ?? 0) : 0;
        double SecondaryMax() => secondary.Length > 0 ? secondary.Max(x => x.Value ?? 0) : 0;

        return new TelemetryParityDiff(
            PointId:   pointId,
            CountDiff: Math.Abs(primary.Length - secondary.Length),
            SumDiff:   Math.Abs(PrimarySum() - SecondarySum()),
            AvgDiff:   Math.Abs(PrimaryAvg() - SecondaryAvg()),
            MinDiff:   Math.Abs(PrimaryMin() - SecondaryMin()),
            MaxDiff:   Math.Abs(PrimaryMax() - SecondaryMax())
        );
    }
}
