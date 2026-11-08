using BuildingOS.Shared.Infrastructure.Telemetry;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry;

/// <summary>#636: the in-process fan-out from the API server's one KV watch to the push streams.</summary>
public class LatestValueFeedTest
{
    private static readonly DateTimeOffset T = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);

    private static LatestValue V(string id, double value, string at = "2026-11-08T00:00:00Z") =>
        new(id, value, null, null, "number", at, T);

    private static async Task<List<LatestValue>> TakeAsync(ILatestValueSubscription sub, int count)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var got = new List<LatestValue>();
        await foreach (var v in sub.ReadAllAsync(cts.Token))
        {
            got.Add(v);
            if (got.Count == count) break;
        }
        return got;
    }

    [Fact]
    public void Subscribe_ReturnsTheKnownValuesAsTheSnapshot_OnlyForTheRequestedPoints()
    {
        var feed = new LatestValueFeed();
        feed.Publish(V("P1", 1));
        feed.Publish(V("P2", 2));

        var (sub, snapshot) = feed.Subscribe(["P1", "P3"]);
        using var _ = sub;

        Assert.Equal(["P1"], snapshot.Select(v => v.PointId));
    }

    [Fact]
    public async Task ValuesPublishedAfterSubscribing_AreDelivered_AndOtherPointsAreNot()
    {
        var feed = new LatestValueFeed();
        var (sub, _) = feed.Subscribe(["P1"]);
        using var __ = sub;

        feed.Publish(V("P2", 99));
        feed.Publish(V("P1", 10));

        var got = await TakeAsync(sub, 1);
        Assert.Equal(10, got.Single().Value);
    }

    [Fact]
    public async Task ASlowReader_GetsTheNewestValuePerPoint_InTheOrderThePointsChanged()
    {
        var feed = new LatestValueFeed();
        var (sub, _) = feed.Subscribe(["P1", "P2"]);
        using var __ = sub;

        // Nothing is read while these arrive.
        feed.Publish(V("P1", 1));
        feed.Publish(V("P2", 2));
        feed.Publish(V("P1", 3));

        var got = await TakeAsync(sub, 2);
        Assert.Equal([("P1", 3d), ("P2", 2d)], got.Select(v => (v.PointId, v.Value!.Value)));
    }

    [Fact]
    public async Task Dispose_EndsTheStream_AndUnsubscribes()
    {
        var feed = new LatestValueFeed();
        var (sub, _) = feed.Subscribe(["P1"]);
        Assert.Equal(1, feed.SubscriberCount);

        sub.Dispose();
        feed.Publish(V("P1", 1));

        Assert.Equal(0, feed.SubscriberCount);
        var got = new List<LatestValue>();
        await foreach (var v in sub.ReadAllAsync()) got.Add(v); // completes instead of waiting forever
        Assert.Empty(got);
    }

    [Fact]
    public void Forget_DropsThePointFromLaterSnapshots()
    {
        var feed = new LatestValueFeed();
        feed.Publish(V("P1", 1));
        feed.Forget("P1");

        var (sub, snapshot) = feed.Subscribe(["P1"]);
        using var subscription = sub;

        Assert.Empty(snapshot);
        Assert.False(feed.TryGetLatest("P1", out _));
    }

    [Fact]
    public void Forget_BySanitizedKvKey_DropsThePointPublishedUnderItsRawId()
    {
        // A KV delete carries only the sanitized key; the value was published under the raw point id.
        var feed = new LatestValueFeed();
        const string raw = "AHU 1/temp";
        var key = BuildingOS.Shared.Infrastructure.Oss.NatsKvLatestStore.SanitizeKey(raw);
        Assert.NotEqual(raw, key);
        feed.Publish(V(raw, 1));

        feed.Forget(key);

        Assert.False(feed.TryGetLatest(raw, out _));
    }

    [Fact]
    public async Task TwoSubscribersToOnePoint_BothReceiveIt()
    {
        var feed = new LatestValueFeed();
        var (a, _) = feed.Subscribe(["P1"]);
        var (b, _) = feed.Subscribe(["P1"]);
        using var __ = a;
        using var ___ = b;

        feed.Publish(V("P1", 7));

        Assert.Equal(7, (await TakeAsync(a, 1)).Single().Value);
        Assert.Equal(7, (await TakeAsync(b, 1)).Single().Value);
    }
}
