using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Test.Infrastructure.Oss;

/// <summary>
/// #625 slice A: <see cref="CoalescingHotTelemetryStore"/> turns the per-frame synchronous KV put
/// into a per-point "latest wins" table flushed on an interval / pending-count trigger, without
/// changing what the inner store ends up holding or how failures are counted.
/// </summary>
public class CoalescingHotTelemetryStoreTest
{
    private sealed class RecordingStore : IHotTelemetryStore
    {
        public ConcurrentQueue<(string PointId, ValidTelemetryData Data)> Puts { get; } = new();
        public ConcurrentDictionary<string, ValidTelemetryData> Latest { get; } = new();
        public Func<string, Exception?>? FailFor { get; set; }
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldPuts { get; set; }
        public int MaxObservedConcurrency;
        private int _inFlight;

        public async Task PutAsync(string pointId, ValidTelemetryData data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref MaxObservedConcurrency, now);
            try
            {
                if (HoldPuts) await Gate.Task.WaitAsync(cancellationToken);
                if (FailFor?.Invoke(pointId) is { } ex) throw ex;
                Puts.Enqueue((pointId, data));
                Latest[pointId] = data;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public Task<ValidTelemetryData?> GetAsync(string pointId, CancellationToken cancellationToken = default)
            => Task.FromResult(Latest.TryGetValue(pointId, out var d) ? d : null);

        private static void InterlockedMax(ref int location, int value)
        {
            int current;
            while ((current = Volatile.Read(ref location)) < value && Interlocked.CompareExchange(ref location, value, current) != current) { }
        }
    }

    private static ValidTelemetryData Row(string pointId, double value, string? datetime = "2026-11-07T00:00:00Z")
        => new() { PointId = pointId, Value = value, Datetime = datetime };

    private static CoalescingHotTelemetryStore NewStore(RecordingStore inner, CoalescingHotStoreOptions? options = null)
        => new(inner, options ?? new CoalescingHotStoreOptions { Interval = TimeSpan.FromHours(1) }, NullLogger<CoalescingHotTelemetryStore>.Instance);

    [Fact]
    public async Task Disabled_IsSynchronousPassThrough()
    {
        var inner = new RecordingStore();
        await using var store = NewStore(inner, new CoalescingHotStoreOptions { Interval = TimeSpan.Zero });

        await store.PutAsync("p1", Row("p1", 1), "test");

        Assert.Single(inner.Puts);
        Assert.Equal(0, store.PendingCount);
    }

    [Fact]
    public async Task Disabled_SynchronousFailurePropagatesToCaller()
    {
        // The pre-#625 contract: ValidatedTelemetryHotStore owns the catch + metric, so the put must throw.
        var inner = new RecordingStore { FailFor = id => id == "p1" ? new InvalidOperationException("kv down") : null };
        await using var store = NewStore(inner, new CoalescingHotStoreOptions { Interval = TimeSpan.Zero });

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PutAsync("p1", Row("p1", 1), "test"));
    }

    [Fact]
    public async Task Enabled_PutReturnsWithoutWriting_FlushWritesLatest()
    {
        var inner = new RecordingStore();
        await using var store = NewStore(inner);

        await store.PutAsync("p1", Row("p1", 1, "2026-11-07T00:00:00Z"), "test");
        await store.PutAsync("p1", Row("p1", 2, "2026-11-07T00:00:01Z"), "test");
        await store.PutAsync("p2", Row("p2", 3), "test");

        Assert.Empty(inner.Puts);
        Assert.Equal(2, store.PendingCount);

        await store.FlushAsync();

        Assert.Equal(2, inner.Puts.Count);
        Assert.Equal(2, inner.Latest["p1"].Value);
        Assert.Equal(3, inner.Latest["p2"].Value);
        Assert.Equal(0, store.PendingCount);
    }

    [Fact]
    public async Task Enabled_NewerDatetimeWins_RegardlessOfArrivalOrder()
    {
        var inner = new RecordingStore();
        await using var store = NewStore(inner);

        await store.PutAsync("p1", Row("p1", 20, "2026-11-07T00:00:20Z"), "test");
        await store.PutAsync("p1", Row("p1", 10, "2026-11-07T00:00:10Z"), "test"); // older → dropped
        await store.FlushAsync();

        Assert.Equal(20, Assert.Single(inner.Puts).Data.Value);
    }

    [Fact]
    public async Task Enabled_EqualDatetime_LastArrivalWins()
    {
        var inner = new RecordingStore();
        await using var store = NewStore(inner);

        await store.PutAsync("p1", Row("p1", 1, "2026-11-07T00:00:00Z"), "test");
        await store.PutAsync("p1", Row("p1", 2, "2026-11-07T00:00:00Z"), "test");
        await store.FlushAsync();

        Assert.Equal(2, Assert.Single(inner.Puts).Data.Value);
    }

    [Fact]
    public async Task Enabled_UnparsableDatetime_LastArrivalWins()
    {
        var inner = new RecordingStore();
        await using var store = NewStore(inner);

        await store.PutAsync("p1", Row("p1", 1, "2026-11-07T00:00:00Z"), "test");
        await store.PutAsync("p1", Row("p1", 2, "not-a-date"), "test");
        await store.FlushAsync();

        Assert.Equal(2, Assert.Single(inner.Puts).Data.Value);
    }

    [Fact]
    public async Task Enabled_OlderValueAfterFlush_IsStillDropped()
    {
        // The table keeps the last flushed value, so "newer datetime wins" holds across flush
        // boundaries too — a late, older reading cannot regress the KV after the newer one shipped.
        var inner = new RecordingStore();
        await using var store = NewStore(inner);

        await store.PutAsync("p1", Row("p1", 20, "2026-11-07T00:00:20Z"), "test");
        await store.FlushAsync();
        await store.PutAsync("p1", Row("p1", 10, "2026-11-07T00:00:10Z"), "test");
        await store.FlushAsync();

        Assert.Equal(20, Assert.Single(inner.Puts).Data.Value);
        Assert.Equal(0, store.PendingCount);
    }

    [Fact]
    public async Task Enabled_MaxPointsTriggersFlushBeforeInterval()
    {
        var inner = new RecordingStore();
        await using var store = NewStore(inner, new CoalescingHotStoreOptions { Interval = TimeSpan.FromHours(1), MaxPoints = 3 });

        await store.PutAsync("p1", Row("p1", 1), "test");
        await store.PutAsync("p2", Row("p2", 2), "test");
        await Task.Delay(50);
        Assert.Empty(inner.Puts);

        await store.PutAsync("p3", Row("p3", 3), "test");

        await WaitUntilAsync(() => inner.Puts.Count == 3);
        Assert.Equal(0, store.PendingCount);
    }

    [Fact]
    public async Task Enabled_IntervalTriggersFlush()
    {
        var inner = new RecordingStore();
        await using var store = NewStore(inner, new CoalescingHotStoreOptions { Interval = TimeSpan.FromMilliseconds(20) });

        await store.PutAsync("p1", Row("p1", 1), "test");

        await WaitUntilAsync(() => inner.Puts.Count == 1);
    }

    [Fact]
    public async Task Flush_FailureIsIsolatedPerPoint_AndCountedUnderTheProducersSource()
    {
        const string source = "test-625-coalesce-failure";
        var failures = new ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == OtelSetup.MeterName && instrument.Name == "building_os.telemetry.hot_write_failures")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "source" && tag.Value?.ToString() == source) for (var i = 0; i < value; i++) failures.Add(source);
        });
        listener.Start();

        var inner = new RecordingStore { FailFor = id => id == "p1" ? new InvalidOperationException("kv put failed") : null };
        await using var store = NewStore(inner);
        await store.PutAsync("p1", Row("p1", 1), source);
        await store.PutAsync("p2", Row("p2", 2), source);

        await store.FlushAsync(); // must not throw

        Assert.Single(failures);
        Assert.Equal("p2", Assert.Single(inner.Puts).PointId);
    }

    [Fact]
    public async Task Flush_ThroughValidatedTelemetryHotStore_CarriesTheSourceTag()
    {
        // The static writer hands the source to the coalescing store, so a deferred failure is still
        // attributed to the right producer (connector | gateway-grpc) rather than to "unknown".
        const string source = "test-625-tagged-source";
        var seen = new ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == OtelSetup.MeterName && instrument.Name == "building_os.telemetry.hot_write_failures")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags) if (tag.Key == "source") seen.Add(tag.Value?.ToString() ?? "");
        });
        listener.Start();

        var inner = new RecordingStore { FailFor = _ => new InvalidOperationException("kv put failed") };
        await using var store = NewStore(inner);
        const string message = """{"telemetries":[{"point_id":"p1","building":"b","device_id":"d","name":"n","datetime":"2026-11-07T00:00:00Z","value":1}]}""";
        await ValidatedTelemetryHotStore.WriteAsync(store, message, source, NullLogger.Instance, CancellationToken.None);
        await store.FlushAsync();

        Assert.Contains(source, seen);
    }

    [Fact]
    public async Task Flush_BoundsConcurrencyAgainstTheInnerStore()
    {
        var inner = new RecordingStore { HoldPuts = true };
        await using var store = NewStore(inner, new CoalescingHotStoreOptions { Interval = TimeSpan.FromHours(1), FlushConcurrency = 4 });
        for (var i = 0; i < 20; i++) await store.PutAsync($"p{i}", Row($"p{i}", i), "test");

        var flush = store.FlushAsync();
        await WaitUntilAsync(() => Volatile.Read(ref inner.MaxObservedConcurrency) >= 4);
        inner.Gate.SetResult();
        await flush;

        Assert.Equal(4, inner.MaxObservedConcurrency);
        Assert.Equal(20, inner.Puts.Count);
    }

    [Fact]
    public async Task StopAsync_FlushesPendingPoints()
    {
        var inner = new RecordingStore();
        var store = NewStore(inner);
        await store.StartAsync(CancellationToken.None);
        await store.PutAsync("p1", Row("p1", 1), "test");
        Assert.Empty(inner.Puts);

        await store.StopAsync(CancellationToken.None);

        Assert.Single(inner.Puts);
        await store.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_GivesUpOnAStalledStore_AfterTheShutdownTimeout()
    {
        var inner = new RecordingStore { HoldPuts = true };
        var store = NewStore(inner, new CoalescingHotStoreOptions
        {
            Interval = TimeSpan.FromHours(1),
            ShutdownFlushTimeout = TimeSpan.FromMilliseconds(100),
        });
        await store.StartAsync(CancellationToken.None);
        await store.PutAsync("p1", Row("p1", 1), "test");

        var stop = store.StopAsync(CancellationToken.None);
        Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5))));
        await stop; // does not throw
        inner.Gate.SetResult();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task GetAsync_ReadsTheInnerStore()
    {
        var inner = new RecordingStore();
        inner.Latest["p1"] = Row("p1", 42);
        await using var store = NewStore(inner);

        var got = await store.GetAsync("p1");

        Assert.Equal(42, got!.Value);
    }

    [Theory]
    [InlineData(null, null, 100, 5000)]
    [InlineData("0", "10", 0, 10)]
    [InlineData("250", "0", 250, 5000)]
    [InlineData("-5", "abc", 100, 5000)]
    [InlineData(" 50 ", " 7 ", 50, 7)]
    public void Options_Parse(string? ms, string? maxPoints, int expectedMs, int expectedMaxPoints)
    {
        var o = CoalescingHotStoreOptions.Parse(ms, maxPoints);
        Assert.Equal(expectedMs, o.Interval.TotalMilliseconds);
        Assert.Equal(expectedMaxPoints, o.MaxPoints);
        Assert.Equal(expectedMs > 0, o.Enabled);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }
}
