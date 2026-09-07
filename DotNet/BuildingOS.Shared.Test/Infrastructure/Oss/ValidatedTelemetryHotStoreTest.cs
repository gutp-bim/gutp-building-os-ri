using System.Diagnostics.Metrics;
using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Test.Infrastructure.Oss;

// #415: every validated-telemetry write records the gap between now and the row's own datetime, so a
// saturated pipeline (frames still arriving, just further and further behind) shows up on a metric
// instead of only as an unnoticed drift nobody is watching.
public class ValidatedTelemetryHotStoreTest
{
    private const string LagInstrumentName = "building_os.ingestion.lag";

    [Fact]
    public async Task WriteAsync_StillPutsEveryPoint_ToTheHotStore()
    {
        var hot = new FakeHotTelemetryStore();
        const string json = """
            { "telemetries": [
              { "point_id": "p1", "value": 23.5, "datetime": "2026-06-12T12:00:00Z" },
              { "point_id": "p2", "value": 1, "datetime": "2026-06-12T12:01:00Z" }
            ] }
            """;

        await ValidatedTelemetryHotStore.WriteAsync(hot, json, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(2, hot.Puts.Count);
        Assert.Contains(hot.Puts, p => p.PointId == "p1");
        Assert.Contains(hot.Puts, p => p.PointId == "p2");
    }

    [Fact]
    public async Task WriteAsync_RecordsIngestionLag_ForEveryParseableRow()
    {
        var hot = new FakeHotTelemetryStore();
        var eventTime = DateTime.UtcNow.AddMinutes(-5);
        var json = $$"""
            { "telemetries": [
              { "point_id": "p1", "value": 1, "datetime": "{{eventTime:O}}" },
              { "point_id": "p2", "value": 2, "datetime": "{{eventTime:O}}" }
            ] }
            """;

        var lags = await CaptureLagMeasurementsAsync(
            () => ValidatedTelemetryHotStore.WriteAsync(hot, json, NullLogger.Instance, CancellationToken.None));

        Assert.Equal(2, lags.Count);
        // Recorded against wall-clock "now" at write time, so allow generous slack for test execution time.
        Assert.All(lags, lag => Assert.InRange(lag, 250, 400));
    }

    [Fact]
    public async Task WriteAsync_SkipsLagRecording_ForUnparseableDatetime_WithoutThrowing()
    {
        var hot = new FakeHotTelemetryStore();
        const string json = """{ "telemetries": [ { "point_id": "p1", "value": 1, "datetime": "not-a-date" } ] }""";

        var lags = await CaptureLagMeasurementsAsync(
            () => ValidatedTelemetryHotStore.WriteAsync(hot, json, NullLogger.Instance, CancellationToken.None));

        Assert.Empty(lags);
        Assert.Single(hot.Puts); // the KV write itself is unaffected by an unparseable datetime.
    }

    /// <summary>
    /// The meter is process-wide and xUnit runs test classes in parallel, so this listens only for the
    /// duration of one call — each test's own await window — rather than filtering by a tag (the
    /// histogram carries none).
    /// </summary>
    private static async Task<List<double>> CaptureLagMeasurementsAsync(Func<Task> run)
    {
        var lags = new List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == OtelSetup.MeterName && instrument.Name == LagInstrumentName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => lags.Add(value));
        listener.Start();

        await run();
        return lags;
    }

    private sealed class FakeHotTelemetryStore : IHotTelemetryStore
    {
        public List<(string PointId, ValidTelemetryData Data)> Puts { get; } = [];

        public Task PutAsync(string pointId, ValidTelemetryData data, CancellationToken cancellationToken = default)
        {
            Puts.Add((pointId, data));
            return Task.CompletedTask;
        }

        public Task<ValidTelemetryData?> GetAsync(string pointId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ValidTelemetryData?>(null);
    }
}
