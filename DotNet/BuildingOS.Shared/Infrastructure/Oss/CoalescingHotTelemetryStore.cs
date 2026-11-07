using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Infrastructure.Oss;

/// <summary>
/// <see cref="IHotTelemetryStore"/> decorator that coalesces hot-store puts per point (#625 slice A).
///
/// The hot store (NATS KV <c>telemetry-latest</c>) only ever holds the latest value per point, and
/// its source of truth is JetStream → the Parquet lake: a put that fails is counted and logged but
/// never fails the telemetry publish (see <see cref="ValidatedTelemetryHotStore"/>). So there is no
/// durability reason to await a KV round-trip for every frame — before this decorator the gRPC
/// ingress paid one such round-trip per frame, serially, and a single stream topped out at ~1.5k
/// frame/s on it (S22, <c>docs/reference/ingest-throughput-sweep.md</c>).
///
/// <see cref="PutAsync(string, ValidTelemetryData, string, CancellationToken)"/> therefore records the
/// value in a per-point table and returns at once. A background loop flushes the table every
/// <see cref="CoalescingHotStoreOptions.Interval"/>, or as soon as
/// <see cref="CoalescingHotStoreOptions.MaxPoints"/> points are pending, with bounded concurrency
/// (<see cref="CoalescingHotStoreOptions.FlushConcurrency"/>) against the inner store. Within the
/// table the value with the <b>newer <c>datetime</c></b> wins — stricter than the previous
/// last-write-wins, since several streams and several frames may hit one point between flushes —
/// and a value whose datetime cannot be parsed is treated as newest (last wins, as before). The
/// table keeps the last flushed value of each point, so this comparison also holds across flushes;
/// its size is therefore bounded by the point count, the same bound as the KV bucket itself.
///
/// Failures are isolated per point and counted under the producer's <c>source</c> tag
/// (<c>building_os.telemetry.hot_write_failures</c>) exactly as the synchronous path does. The lag
/// the coalescing adds is visible as <c>building_os.hot_store.coalesce_flush_latency</c> (age of the
/// oldest put in each flush) and the collapse ratio as <c>building_os.hot_store.coalesced_points</c>
/// (points per flush). Registered as an <see cref="IHostedService"/> so shutdown flushes what is
/// still pending (bounded by <see cref="CoalescingHotStoreOptions.ShutdownFlushTimeout"/>).
///
/// With <see cref="CoalescingHotStoreOptions.Interval"/> = 0 (<c>HOT_STORE_COALESCE_MS=0</c>) the
/// decorator is a pass-through: every put is awaited synchronously, the pre-#625 behaviour.
/// Reads (<see cref="GetAsync"/>) always go to the inner store: the pending table is a write buffer,
/// not a cache, and the ApiServer (the reader) runs in another process anyway.
/// </summary>
public sealed class CoalescingHotTelemetryStore : ISourceTaggedHotTelemetryStore, IHostedService, IAsyncDisposable, IDisposable
{
    private const string UnknownSource = "unknown";

    private sealed record Entry(ValidTelemetryData Data, string Source, bool Dirty, long EnqueuedTicks);

    private readonly IHotTelemetryStore _inner;
    private readonly CoalescingHotStoreOptions _options;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Entry> _table = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _flushSignal = new(0, 1);
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly object _loopLock = new();
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private int _dirty;
    private int _disposed;

    public CoalescingHotTelemetryStore(IHotTelemetryStore inner, CoalescingHotStoreOptions options, ILogger<CoalescingHotTelemetryStore> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Points currently waiting to be flushed (test/diagnostic).</summary>
    public int PendingCount => Volatile.Read(ref _dirty);

    public Task PutAsync(string pointId, ValidTelemetryData data, CancellationToken cancellationToken = default)
        => PutAsync(pointId, data, UnknownSource, cancellationToken);

    public Task PutAsync(string pointId, ValidTelemetryData data, string source, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return _inner.PutAsync(pointId, data, cancellationToken);
        ArgumentNullException.ThrowIfNull(pointId);
        ArgumentNullException.ThrowIfNull(data);
        cancellationToken.ThrowIfCancellationRequested();

        var now = Stopwatch.GetTimestamp();
        while (true)
        {
            if (!_table.TryGetValue(pointId, out var current))
            {
                if (!_table.TryAdd(pointId, new Entry(data, source, Dirty: true, now))) continue;
                Interlocked.Increment(ref _dirty);
                break;
            }
            // Keep the newer reading. An equal datetime is a re-send (or two sources agreeing on the
            // same instant): the later arrival wins, as it did on the synchronous path.
            if (IsOlderThan(data, current.Data)) return Task.CompletedTask;
            var next = new Entry(data, source, Dirty: true, current.Dirty ? current.EnqueuedTicks : now);
            if (!_table.TryUpdate(pointId, next, current)) continue;
            if (!current.Dirty) Interlocked.Increment(ref _dirty);
            break;
        }

        EnsureLoopStarted();
        if (Volatile.Read(ref _dirty) >= _options.MaxPoints) Signal();
        return Task.CompletedTask;
    }

    public Task<ValidTelemetryData?> GetAsync(string pointId, CancellationToken cancellationToken = default)
        => _inner.GetAsync(pointId, cancellationToken);

    /// <summary>Writes every pending point to the inner store now (also used by the loop and at shutdown).</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return;
        // One flush at a time: the loop and an explicit caller (tests, shutdown) must not interleave
        // two writes of the same point out of order.
        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var claimed = ClaimDirty();
            if (claimed.Count == 0) return;

            var flushStart = Stopwatch.GetTimestamp();
            long oldest = long.MaxValue;
            foreach (var (_, entry) in claimed) oldest = Math.Min(oldest, entry.EnqueuedTicks);
            var oldestAge = Stopwatch.GetElapsedTime(oldest, flushStart);

            // Points whose put finished (written, or failed and counted). A cancelled flush — the loop
            // being stopped mid-flush, or the shutdown deadline — re-queues every other claimed point,
            // so the final flush in StopAsync still writes it instead of the claim silently dropping it.
            var settled = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            try
            {
                await Parallel.ForEachAsync(
                    claimed,
                    new ParallelOptions { MaxDegreeOfParallelism = _options.FlushConcurrency, CancellationToken = cancellationToken },
                    async (item, ct) =>
                    {
                        var (pointId, entry) = item;
                        try
                        {
                            await _inner.PutAsync(pointId, entry.Data, ct).ConfigureAwait(false);
                            settled[pointId] = 0;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            // Cancelled, not failed: left unsettled, so it is re-queued below.
                        }
                        catch (Exception ex)
                        {
                            settled[pointId] = 0;
                            BuildingOsMetrics.TelemetryHotWriteFailures.Add(1,
                                new KeyValuePair<string, object?>("source", entry.Source));
                            _logger.LogWarning(ex,
                                "CoalescingHotTelemetryStore: hot store put failed for point {PointId} from source {Source}",
                                pointId, entry.Source);
                        }
                    }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Requeue(claimed, settled);
                throw;
            }
            if (settled.Count < claimed.Count) Requeue(claimed, settled);

            BuildingOsMetrics.HotStoreCoalescedPoints.Record(claimed.Count);
            BuildingOsMetrics.HotStoreCoalesceFlushLatency.Record(oldestAge.TotalSeconds);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    // Puts claimed-but-unwritten values back in the queue. A point that took a newer put meanwhile is
    // already dirty with that newer value and is left alone.
    private void Requeue(List<KeyValuePair<string, Entry>> claimed, ConcurrentDictionary<string, byte> settled)
    {
        foreach (var (pointId, entry) in claimed)
        {
            if (settled.ContainsKey(pointId)) continue;
            while (_table.TryGetValue(pointId, out var current) && !current.Dirty && ReferenceEquals(current.Data, entry.Data))
            {
                if (_table.TryUpdate(pointId, current with { Dirty = true }, current))
                {
                    Interlocked.Increment(ref _dirty);
                    break;
                }
            }
        }
    }

    // Marks every dirty entry clean and returns the snapshot to write. An entry that changes
    // underneath (a concurrent put) is skipped — its new value is dirty again and the next flush
    // takes it — so the count stays exact.
    private List<KeyValuePair<string, Entry>> ClaimDirty()
    {
        var claimed = new List<KeyValuePair<string, Entry>>();
        foreach (var kv in _table)
        {
            var entry = kv.Value;
            if (!entry.Dirty) continue;
            if (_table.TryUpdate(kv.Key, entry with { Dirty = false }, entry))
            {
                Interlocked.Decrement(ref _dirty);
                claimed.Add(new KeyValuePair<string, Entry>(kv.Key, entry));
            }
        }
        return claimed;
    }

    private static bool IsOlderThan(ValidTelemetryData candidate, ValidTelemetryData current)
    {
        if (!TryParse(candidate.Datetime, out var c)) return false; // unparsable → treat as newest (last wins)
        if (!TryParse(current.Datetime, out var cur)) return false;
        return c < cur;
    }

    private static bool TryParse(string? raw, out DateTimeOffset value)
        => DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);

    private void Signal()
    {
        if (_flushSignal.CurrentCount > 0) return;
        try { _flushSignal.Release(); }
        catch (SemaphoreFullException) { /* a signal is already pending */ }
    }

    private void EnsureLoopStarted()
    {
        if (_loop is not null) return;
        lock (_loopLock)
        {
            if (_loop is not null) return;
            _loopCts = new CancellationTokenSource();
            _loop = Task.Run(() => LoopAsync(_loopCts.Token));
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Wake on the interval or when MaxPoints are pending, whichever comes first.
                try { await _flushSignal.WaitAsync(_options.Interval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                try { await FlushAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    // FlushAsync isolates per-point failures; this is a guard so the loop itself survives.
                    _logger.LogWarning(ex, "CoalescingHotTelemetryStore: flush loop iteration failed");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CoalescingHotTelemetryStore: flush loop stopped unexpectedly");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Enabled)
        {
            _logger.LogInformation(
                "Hot store coalescing enabled: interval {IntervalMs} ms, max pending points {MaxPoints}, flush concurrency {Concurrency}",
                _options.Interval.TotalMilliseconds, _options.MaxPoints, _options.FlushConcurrency);
            EnsureLoopStarted();
        }
        else
        {
            _logger.LogInformation("Hot store coalescing disabled (HOT_STORE_COALESCE_MS=0): every put is awaited synchronously");
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return;
        Task? loop;
        lock (_loopLock)
        {
            loop = _loop;
            _loopCts?.Cancel();
        }
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        // Final flush under its own deadline so a stalled KV cannot hold the host's shutdown hostage.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.ShutdownFlushTimeout);
        var pending = PendingCount;
        try
        {
            await FlushAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "CoalescingHotTelemetryStore: shutdown flush timed out after {TimeoutMs} ms with {Pending} points pending",
                _options.ShutdownFlushTimeout.TotalMilliseconds, pending);
        }
    }

    // The same instance is registered three times (concrete, IHotTelemetryStore, IHostedService), so
    // the container disposes it once per registration: both dispose paths are idempotent.

    /// <summary>Synchronous dispose (containers that are not disposed asynchronously): stops the loop without flushing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        lock (_loopLock) { _loopCts?.Cancel(); }
        // The loop observes the cancellation on its own; the semaphores are left to the finalizer-free
        // GC path on purpose, since the loop task may still be touching them.
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        Task? loop;
        lock (_loopLock)
        {
            loop = _loop;
            _loopCts?.Cancel();
        }
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _loopCts?.Dispose();
        _flushSignal.Dispose();
        _flushGate.Dispose();
    }
}
