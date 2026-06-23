using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry.ParquetLake;

/// <summary>Tests for TailMergePolicy (pure) and TailMergedTelemetryStore (#220).</summary>
public class TailMergeTest
{
    // ── TailMergePolicy ────────────────────────────────────────────────────

    [Fact]
    public void ShouldMergeTail_WhenEndWithinLookback_ReturnsTrue()
    {
        var now = new DateTime(2026, 6, 13, 10, 0, 0, DateTimeKind.Utc);
        var end = now.AddSeconds(-100);
        Assert.True(TailMergePolicy.ShouldMergeTail(end, now, lookbackSec: 900));
    }

    [Fact]
    public void ShouldMergeTail_WhenEndOutsideLookback_ReturnsFalse()
    {
        var now = new DateTime(2026, 6, 13, 10, 0, 0, DateTimeKind.Utc);
        var end = now.AddSeconds(-1000); // > 900s
        Assert.False(TailMergePolicy.ShouldMergeTail(end, now, lookbackSec: 900));
    }

    [Fact]
    public void ShouldMergeTail_WhenEndInFuture_ReturnsTrue()
    {
        var now = new DateTime(2026, 6, 13, 10, 0, 0, DateTimeKind.Utc);
        var end = now.AddSeconds(10);
        Assert.True(TailMergePolicy.ShouldMergeTail(end, now, lookbackSec: 900));
    }

    [Fact]
    public void ShouldMergeTail_WhenLookbackZeroOrNegative_ReturnsFalse()
    {
        var now = new DateTime(2026, 6, 13, 10, 0, 0, DateTimeKind.Utc);
        Assert.False(TailMergePolicy.ShouldMergeTail(now.AddSeconds(-1), now, lookbackSec: 0));
        Assert.False(TailMergePolicy.ShouldMergeTail(now.AddSeconds(-1), now, lookbackSec: -1));
    }

    // ── TailMergedTelemetryStore ────────────────────────────────────────────

    private static ValidTelemetryData Row(string id, double v, DateTime t) => new()
    {
        Id = id, PointId = "p1", Building = "B", DeviceId = "D", Name = "temp",
        Value = v, Datetime = t.ToString("O"),
    };

    [Fact]
    public async Task QueryAsync_WhenEndFarInPast_SkipsTailMerge()
    {
        var lakeRows = new[] { Row("a", 1.0, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)) };
        var inner = new FixedStore(lakeRows);
        var tailReader = new CountingTailReader(Array.Empty<ValidTelemetryData>());
        var store = new TailMergedTelemetryStore(inner, tailReader, new TailMergeOptions { LookbackSec = 900 });

        var end = DateTime.UtcNow.AddDays(-1); // far past
        await store.QueryAsync("p1", end.AddHours(-1), end);

        Assert.Equal(0, tailReader.CallCount);
    }

    [Fact]
    public async Task QueryAsync_WhenEndRecent_MergesTailRows()
    {
        var now = DateTime.UtcNow;
        var lakeRow = Row("a", 1.0, now.AddMinutes(-10));
        var tailRow = Row("b", 2.0, now.AddSeconds(-30));
        var inner = new FixedStore(new[] { lakeRow });
        var tailReader = new CountingTailReader(new[] { tailRow });
        var store = new TailMergedTelemetryStore(inner, tailReader, new TailMergeOptions { LookbackSec = 900 });

        var result = await store.QueryAsync("p1", now.AddHours(-1), now);

        Assert.Equal(1, tailReader.CallCount);
        Assert.Equal(2, result.Length);
    }

    [Fact]
    public async Task QueryAsync_TailDuplicatesLake_DeduplicatedById()
    {
        var now = DateTime.UtcNow;
        var t = now.AddMinutes(-5);
        var lakeRow = Row("dup-id", 1.0, t);
        var tailRow = Row("dup-id", 1.0, t); // same id — tail duplicate
        var inner = new FixedStore(new[] { lakeRow });
        var tailReader = new CountingTailReader(new[] { tailRow });
        var store = new TailMergedTelemetryStore(inner, tailReader, new TailMergeOptions { LookbackSec = 900 });

        var result = await store.QueryAsync("p1", now.AddHours(-1), now);

        Assert.Single(result);
    }

    [Fact]
    public async Task QueryAsync_WhenTailFails_DegradeToLakeOnlyResult()
    {
        var now = DateTime.UtcNow;
        var lakeRow = Row("a", 1.0, now.AddMinutes(-10));
        var inner = new FixedStore(new[] { lakeRow });
        var tailReader = new FailingTailReader();
        var store = new TailMergedTelemetryStore(inner, tailReader, new TailMergeOptions { LookbackSec = 900 });

        var result = await store.QueryAsync("p1", now.AddHours(-1), now);

        Assert.Single(result);
        Assert.Equal("a", result[0].Id);
    }

    [Fact]
    public async Task QueryLatestAsync_DelegatesDirectlyToInner()
    {
        var latest = Row("x", 42.0, DateTime.UtcNow);
        var inner = new FixedStore(Array.Empty<ValidTelemetryData>(), latest);
        var tailReader = new CountingTailReader(Array.Empty<ValidTelemetryData>());
        var store = new TailMergedTelemetryStore(inner, tailReader, new TailMergeOptions { LookbackSec = 900 });

        var result = await store.QueryLatestAsync("p1");

        Assert.Equal("x", result!.Id);
        Assert.Equal(0, tailReader.CallCount); // tail not involved in latest
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private sealed class FixedStore : IWarmTelemetryStore
    {
        private readonly ValidTelemetryData[] _rows;
        private readonly ValidTelemetryData? _latest;
        public FixedStore(ValidTelemetryData[] rows, ValidTelemetryData? latest = null) { _rows = rows; _latest = latest; }
        public Task<ValidTelemetryData[]> QueryAsync(string pid, DateTime start, DateTime end, CancellationToken ct = default) => Task.FromResult(_rows);
        public Task<ValidTelemetryData?> QueryLatestAsync(string pid, CancellationToken ct = default) => Task.FromResult(_latest);
    }

    private sealed class CountingTailReader : IJetStreamTailReader
    {
        private readonly ValidTelemetryData[] _rows;
        public int CallCount { get; private set; }
        public CountingTailReader(ValidTelemetryData[] rows) => _rows = rows;
        public Task<ValidTelemetryData[]> ReadSinceAsync(DateTime since, string pointId, int maxMsgs, TimeSpan timeout, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_rows);
        }
    }

    private sealed class FailingTailReader : IJetStreamTailReader
    {
        public Task<ValidTelemetryData[]> ReadSinceAsync(DateTime since, string pointId, int maxMsgs, TimeSpan timeout, CancellationToken ct)
            => throw new InvalidOperationException("NATS unavailable (simulated)");
    }
}
