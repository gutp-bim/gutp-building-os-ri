using System.Diagnostics;
using System.Threading.Channels;

namespace BuildingOS.Shared.Infrastructure.Telemetry;

/// <summary>
/// One point's latest reading as it sits in the <c>telemetry-latest</c> KV bucket (#636). ADR-0006: numeric
/// <see cref="Value"/> stays primary; <see cref="ValueText"/> / <see cref="ValueBool"/> carry the non-numeric
/// readings, and <see cref="ValueType"/> says which one is meant.
/// </summary>
public sealed record LatestValue(
    string PointId,
    double? Value,
    string? ValueText,
    bool? ValueBool,
    string? ValueType,
    string? Datetime,
    DateTimeOffset ObservedAt);

/// <summary>A live subscription to some points' latest values. Dispose to stop it.</summary>
public interface ILatestValueSubscription : IDisposable
{
    /// <summary>The newest value per subscribed point, in the order they changed. Ends when disposed.</summary>
    IAsyncEnumerable<LatestValue> ReadAllAsync(CancellationToken ct = default);
}

/// <summary>
/// Fans the API server's one <c>telemetry-latest</c> watch out to the browsers watching single points (#636).
/// It keeps the newest value per point (so a new subscriber starts from a snapshot) and, per subscriber, at
/// most one pending value per point: a reader that falls behind skips straight to the newest value instead of
/// working through a backlog, because this is a latest-value view, not a history. Skipped values are counted.
///
/// <para>Process-local by design: each API replica serves its own subscribers from its own watch, so adding
/// replicas needs no shared state.</para>
/// </summary>
public sealed class LatestValueFeed
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LatestValue> _latest = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<Subscription>> _byPoint = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public LatestValueFeed(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>Live subscriptions (for tests and the gauge).</summary>
    public int SubscriberCount
    {
        get { lock (_gate) return _byPoint.Values.SelectMany(s => s).Distinct().Count(); }
    }

    /// <summary>The newest value known for <paramref name="pointId"/>, if any.</summary>
    public bool TryGetLatest(string pointId, out LatestValue value)
    {
        lock (_gate) return _latest.TryGetValue(pointId, out value!);
    }

    /// <summary>Records a point's new latest value and hands it to that point's subscribers.</summary>
    public void Publish(LatestValue value)
    {
        Subscription[] targets;
        lock (_gate)
        {
            _latest[value.PointId] = value;
            if (!_byPoint.TryGetValue(value.PointId, out var subs) || subs.Count == 0) return;
            targets = subs.ToArray();
        }
        foreach (var s in targets) s.Offer(value);
    }

    /// <summary>The point left the bucket (deleted / purged): forget its value. Subscribers keep what they have.</summary>
    public void Forget(string pointId)
    {
        lock (_gate) _latest.Remove(pointId);
    }

    /// <summary>
    /// Subscribes to <paramref name="pointIds"/>. Values published after this call are delivered; the current
    /// values are returned as the snapshot so the caller can send them first.
    /// </summary>
    public (ILatestValueSubscription Subscription, IReadOnlyList<LatestValue> Snapshot) Subscribe(
        IReadOnlyCollection<string> pointIds)
    {
        var sub = new Subscription(this, pointIds, _clock);
        var snapshot = new List<LatestValue>();
        lock (_gate)
        {
            foreach (var id in sub.PointIds)
            {
                if (!_byPoint.TryGetValue(id, out var set)) _byPoint[id] = set = [];
                set.Add(sub);
                if (_latest.TryGetValue(id, out var v)) snapshot.Add(v);
            }
        }
        BuildingOsMetrics.TelemetryFeedSubscribers.Add(1);
        return (sub, snapshot);
    }

    private void Unsubscribe(Subscription sub)
    {
        lock (_gate)
        {
            foreach (var id in sub.PointIds)
            {
                if (!_byPoint.TryGetValue(id, out var set)) continue;
                set.Remove(sub);
                if (set.Count == 0) _byPoint.Remove(id);
            }
        }
        BuildingOsMetrics.TelemetryFeedSubscribers.Add(-1);
    }

    private sealed class Subscription(LatestValueFeed feed, IReadOnlyCollection<string> pointIds, TimeProvider clock)
        : ILatestValueSubscription
    {
        public IReadOnlyCollection<string> PointIds { get; } = pointIds.Distinct(StringComparer.Ordinal).ToArray();

        private readonly object _gate = new();
        // Insertion order = the order points changed; a re-offered point keeps its place and takes the new value.
        private readonly Dictionary<string, LatestValue> _pending = new(StringComparer.Ordinal);
        private readonly List<string> _order = [];
        private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
        private int _disposed;

        public void Offer(LatestValue value)
        {
            lock (_gate)
            {
                if (_disposed != 0) return;
                if (_pending.ContainsKey(value.PointId)) BuildingOsMetrics.TelemetryFeedDropped.Add(1);
                else _order.Add(value.PointId);
                _pending[value.PointId] = value;
            }
            _signal.Writer.TryWrite(true);
        }

        public async IAsyncEnumerable<LatestValue> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            while (await _signal.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                _signal.Reader.TryRead(out _);
                LatestValue[] batch;
                lock (_gate)
                {
                    batch = _order.Select(id => _pending[id]).ToArray();
                    _pending.Clear();
                    _order.Clear();
                }
                foreach (var v in batch)
                {
                    BuildingOsMetrics.TelemetryFeedDeliveryLatency.Record(
                        Math.Max(0, (clock.GetUtcNow() - v.ObservedAt).TotalSeconds));
                    yield return v;
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _signal.Writer.TryComplete();
            feed.Unsubscribe(this);
        }
    }
}
