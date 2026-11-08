using System.Text.Json;
using BuildingOS.IntegrationTest.Collections;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared;
using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.KeyValueStore;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>
/// #636 against a real NATS: a put to the <c>telemetry-latest</c> KV bucket reaches a push subscriber through the
/// API server's one existing watch (the same one that feeds the data-health last-seen index).
/// </summary>
[Collection(Names.Nats)]
public class LatestValueFeedNatsTest(NatsFixture fixture) : IntegrationTestBase
{
    [Fact]
    public async Task AKvPut_ReachesTheSubscriber_AndANewSubscriberSeesItInTheSnapshot()
    {
        var (conn, js) = await fixture.CreateJetStreamAsync();
        await using var _ = conn;
        var kv = await new NatsKVContext(js).CreateStoreAsync(new NatsKVConfig("telemetry-latest") { History = 1 });

        var feed = new LatestValueFeed();
        var store = new PointLastSeenIndexStore();
        using var worker = new NatsKvPointLastSeenIndexWorker(
            js, store, NullLogger<NatsKvPointLastSeenIndexWorker>.Instance, feed: feed);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await worker.StartAsync(cts.Token);

        var pointId = "P-636-" + Guid.NewGuid().ToString("N")[..8];
        var (sub, snapshot) = feed.Subscribe([pointId]);
        using var __ = sub;
        Assert.Empty(snapshot);

        var value = new ValidTelemetryData { PointId = pointId, Value = 21.5, ValueType = "number", Datetime = "2026-11-08T00:00:00Z" };
        await kv.PutAsync(pointId, JsonSerializer.SerializeToUtf8Bytes(value), cancellationToken: cts.Token);

        LatestValue? got = null;
        await foreach (var v in sub.ReadAllAsync(cts.Token)) { got = v; break; }
        Assert.Equal(21.5, got!.Value);
        Assert.Equal("2026-11-08T00:00:00Z", got.Datetime);

        var (late, lateSnapshot) = feed.Subscribe([pointId]);
        using var ___ = late;
        Assert.Equal(21.5, lateSnapshot.Single().Value);

        await worker.StopAsync(CancellationToken.None);
    }
}
