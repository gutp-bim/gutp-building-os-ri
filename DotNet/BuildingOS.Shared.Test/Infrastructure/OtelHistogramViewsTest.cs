using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Xunit;

namespace BuildingOS.Shared.Test.Infrastructure;

/// <summary>
/// Second-unit histograms need second-scale buckets. The SDK default boundaries
/// (0, 5, 10, 25, 50, 75, 100, 250, …) are sized for milliseconds, so an 80 ms lag lands in the
/// (0, 5] bucket and histogram_quantile interpolates p95 to ~4.75 s.
/// </summary>
public class OtelHistogramViewsTest
{
    private static readonly double[] ExpectedSeconds =
        [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200];

    private static readonly double[] SdkDefaultBounds =
        [0, 5, 10, 25, 50, 75, 100, 250, 500, 750, 1000, 2500, 5000, 7500, 10000];

    [Fact]
    public void SecondsBoundaries_AreTheDocumentedSecondScaleSet()
    {
        Assert.Equal(ExpectedSeconds, OtelSetup.SecondsHistogramBoundaries);
    }

    [Fact]
    public void DefaultSecondThresholds_AreStrictlyBelowTheTopFiniteBucket()
    {
        // histogram_quantile never returns more than the top finite bound, and the UI warns only when
        // value > threshold — a threshold at or above the top bucket could never trigger.
        var top = OtelSetup.SecondsHistogramBoundaries.Max();
        var d = BuildingOS.Shared.Domain.Configuration.PipelineKpiThresholds.Defaults;
        Assert.True(d.ParquetFreshnessWarnSeconds < top);
        Assert.True(d.EventLagP95WarnSeconds < top);
        Assert.True(d.ConsumerLagP95WarnSeconds < top);
        foreach (var key in new[]
                 {
                     BuildingOS.Shared.Domain.Configuration.SettingsRegistry.ParquetFreshnessWarnSecondsKey,
                     BuildingOS.Shared.Domain.Configuration.SettingsRegistry.EventLagP95WarnSecondsKey,
                     BuildingOS.Shared.Domain.Configuration.SettingsRegistry.ConsumerLagP95WarnSecondsKey,
                 })
        {
            var def = BuildingOS.Shared.Domain.Configuration.SettingsRegistry.Find(key)!;
            Assert.True(double.Parse(def.DefaultValue!, System.Globalization.CultureInfo.InvariantCulture) < top, key);
            Assert.Contains(top.ToString(System.Globalization.CultureInfo.InvariantCulture), def.Description);
        }
    }

    [Fact]
    public void AddOtlpTelemetry_ConfiguresSecondScaleBuckets_ForEverySecondUnitHistogram()
    {
        var exporter = new CollectingExporter();
        var services = new ServiceCollection();
        services.AddOtlpTelemetry("test-service", "http://localhost:4317");
        // Same MeterProviderBuilder as production; only an extra in-process reader is added.
        services.AddOpenTelemetry().WithMetrics(b => b.AddReader(new BaseExportingMetricReader(exporter)));
        using var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<MeterProvider>();

        BuildingOsMetrics.IngestionLag.Record(0.08);
        BuildingOsMetrics.IngressEventLag.Record(0.08);
        BuildingOsMetrics.ParquetWriterFreshnessLag.Record(12);
        BuildingOsMetrics.ConnectorProcessDuration.Record(3); // unit "ms" — must keep SDK defaults
        provider.ForceFlush();

        Assert.Equal(ExpectedSeconds, exporter.Bounds["building_os.ingestion.lag"]);
        Assert.Equal(ExpectedSeconds, exporter.Bounds["building_os.ingress.event_lag"]);
        Assert.Equal(ExpectedSeconds, exporter.Bounds["building_os.parquet_writer.freshness_lag"]);
        Assert.Equal(SdkDefaultBounds, exporter.Bounds[BuildingOsMetrics.ConnectorProcessDuration.Name]);
    }

    [Fact]
    public void SecondsHistogramInstruments_CoverExactlyTheBuildingOsHistogramsWithUnitSeconds()
    {
        var secondUnitHistograms = typeof(BuildingOsMetrics)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(f => f.GetValue(null))
            .OfType<System.Diagnostics.Metrics.Histogram<double>>()
            .Where(h => h.Unit == "s")
            .Select(h => h.Name)
            .OrderBy(n => n, StringComparer.Ordinal);

        Assert.Equal(secondUnitHistograms, OtelSetup.SecondsHistogramInstruments.OrderBy(n => n, StringComparer.Ordinal));
    }

    private sealed class CollectingExporter : BaseExporter<Metric>
    {
        public Dictionary<string, double[]> Bounds { get; } = new();

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                if (metric.MetricType != MetricType.Histogram) continue;
                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    var bounds = new List<double>();
                    foreach (var bucket in point.GetHistogramBuckets())
                    {
                        if (double.IsFinite(bucket.ExplicitBound)) bounds.Add(bucket.ExplicitBound);
                    }
                    Bounds[metric.Name] = bounds.ToArray();
                }
            }
            return ExportResult.Success;
        }
    }
}
