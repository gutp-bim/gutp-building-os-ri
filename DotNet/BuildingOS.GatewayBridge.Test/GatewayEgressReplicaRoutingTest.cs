using System.Collections.Concurrent;
using System.Text.Json;
using BuildingOS.GatewayBridge.Infrastructure;
using BuildingOS.GatewayBridge.Protos;
using BuildingOS.GatewayBridge.Services;
using BuildingOS.Shared.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.GatewayBridge.Test;

/// <summary>
/// Models multiple GatewayBridge replicas sharing one NATS spine (the <see cref="SharedEgressBus"/>).
/// Each replica subscribes only to the gateways it holds; a command published to a gateway's subject
/// is fanned-in to whichever replica currently holds that gateway's stream (plan §3-3). Replicas keep
/// no persistent state, so failover = reconnect on another replica and re-subscribe (plan §3-4).
/// </summary>
public class GatewayEgressReplicaRoutingTest
{
    private static GatewayEgressService Replica(SharedEgressBus bus)
        => new(bus, new GatewayConnectionRegistry(), NullLogger<GatewayEgressService>.Instance);

    private static string Command(Guid id) => JsonSerializer.Serialize(new PointControlInfo
    {
        id = id, PointId = "PT", Type = DeviceControlType.BacnetSim,
        Body = JsonSerializer.Serialize(new { value = 1.0 }),
    });

    [Fact]
    public async Task Command_ReachesTheReplicaHoldingTheGateway()
    {
        var bus = new SharedEgressBus();
        var replicaA = Replica(bus);
        var replicaB = Replica(bus);

        var readerA = new FakeStreamReader<EgressUp>();
        var writerA = new FakeStreamWriter<EgressDown>();
        var readerB = new FakeStreamReader<EgressUp>();
        var writerB = new FakeStreamWriter<EgressDown>();

        readerA.Push(new EgressUp { Hello = new Hello { GatewayId = "gw-1" } });
        readerB.Push(new EgressUp { Hello = new Hello { GatewayId = "gw-2" } });
        var runA = replicaA.RunAsync(readerA, writerA, CancellationToken.None);
        var runB = replicaB.RunAsync(readerB, writerB, CancellationToken.None);
        await bus.WaitForSubscription("gw-1");
        await bus.WaitForSubscription("gw-2");

        // gw-1 command → only replica A's stream; gw-2 command → only replica B's stream.
        var id1 = Guid.NewGuid();
        await bus.Deliver("gw-1", Command(id1));
        var downA = await writerA.ReadAsync();
        Assert.Equal(id1.ToString(), downA.Command.ControlId);
        Assert.False(writerB.TryReadImmediately(out _));

        var id2 = Guid.NewGuid();
        await bus.Deliver("gw-2", Command(id2));
        var downB = await writerB.ReadAsync();
        Assert.Equal(id2.ToString(), downB.Command.ControlId);

        readerA.Complete();
        readerB.Complete();
        await Task.WhenAll(runA, runB);
    }

    [Fact]
    public async Task Command_RoutesCorrectly_WithThreeConcurrentGateways()
    {
        // Extends the two-replica routing test to three simultaneous gateway connections spread
        // across three replicas, asserting each command reaches only its own gateway's stream.
        var bus = new SharedEgressBus();
        var replicas = new[] { Replica(bus), Replica(bus), Replica(bus) };
        var gatewayIds = new[] { "gw-1", "gw-2", "gw-3" };
        var readers = gatewayIds.Select(_ => new FakeStreamReader<EgressUp>()).ToArray();
        var writers = gatewayIds.Select(_ => new FakeStreamWriter<EgressDown>()).ToArray();

        for (var i = 0; i < gatewayIds.Length; i++)
            readers[i].Push(new EgressUp { Hello = new Hello { GatewayId = gatewayIds[i] } });

        var runs = new Task[gatewayIds.Length];
        for (var i = 0; i < gatewayIds.Length; i++)
            runs[i] = replicas[i].RunAsync(readers[i], writers[i], CancellationToken.None);
        foreach (var gatewayId in gatewayIds)
            await bus.WaitForSubscription(gatewayId);

        for (var i = 0; i < gatewayIds.Length; i++)
        {
            var id = Guid.NewGuid();
            await bus.Deliver(gatewayIds[i], Command(id));
            var down = await writers[i].ReadAsync();
            Assert.Equal(id.ToString(), down.Command.ControlId);

            // No other gateway's writer received this command.
            for (var j = 0; j < gatewayIds.Length; j++)
            {
                if (j == i) continue;
                Assert.False(writers[j].TryReadImmediately(out _));
            }
        }

        foreach (var reader in readers) reader.Complete();
        await Task.WhenAll(runs);
    }

    [Fact]
    public async Task PointListUpdate_ReachesOnlyTheOwningGatewaysStream()
    {
        // #114/#224: a point-list push signal for gw-1 must not leak to gw-2's concurrently-open
        // stream (or vice versa), mirroring the command-routing isolation above.
        var bus = new SharedEgressBus();
        var replicaA = Replica(bus);
        var replicaB = Replica(bus);

        var readerA = new FakeStreamReader<EgressUp>();
        var writerA = new FakeStreamWriter<EgressDown>();
        var readerB = new FakeStreamReader<EgressUp>();
        var writerB = new FakeStreamWriter<EgressDown>();

        readerA.Push(new EgressUp { Hello = new Hello { GatewayId = "gw-1" } });
        readerB.Push(new EgressUp { Hello = new Hello { GatewayId = "gw-2" } });
        var runA = replicaA.RunAsync(readerA, writerA, CancellationToken.None);
        var runB = replicaB.RunAsync(readerB, writerB, CancellationToken.None);
        await bus.WaitForPointListSubscription("gw-1");
        await bus.WaitForPointListSubscription("gw-2");

        await bus.DeliverPointListUpdate("gw-1", "\"sha256:gw1-rev\"");
        var downA = await writerA.ReadAsync();
        Assert.Equal(EgressDown.MOneofCase.PointListUpdate, downA.MCase);
        Assert.Equal("gw-1", downA.PointListUpdate.GatewayId);
        Assert.Equal("\"sha256:gw1-rev\"", downA.PointListUpdate.Revision);
        Assert.False(writerB.TryReadImmediately(out _));

        await bus.DeliverPointListUpdate("gw-2", "\"sha256:gw2-rev\"");
        var downB = await writerB.ReadAsync();
        Assert.Equal("gw-2", downB.PointListUpdate.GatewayId);
        Assert.Equal("\"sha256:gw2-rev\"", downB.PointListUpdate.Revision);
        Assert.False(writerA.TryReadImmediately(out _));

        readerA.Complete();
        readerB.Complete();
        await Task.WhenAll(runA, runB);
    }

    [Fact]
    public async Task Gateway_FailsOverToAnotherReplica_OnReconnect()
    {
        var bus = new SharedEgressBus();
        var replicaA = Replica(bus);
        var replicaB = Replica(bus);

        // gw-1 initially on replica A.
        var readerA = new FakeStreamReader<EgressUp>();
        var writerA = new FakeStreamWriter<EgressDown>();
        readerA.Push(new EgressUp { Hello = new Hello { GatewayId = "gw-1" } });
        var runA = replicaA.RunAsync(readerA, writerA, CancellationToken.None);
        await bus.WaitForSubscription("gw-1");

        // Replica A drops (BOWS disconnect) → subscription torn down, no persistent state.
        readerA.Complete();
        await runA;
        Assert.False(bus.HasSubscriber("gw-1"));

        // BOWS reconnects to replica B for the same gateway.
        var readerB = new FakeStreamReader<EgressUp>();
        var writerB = new FakeStreamWriter<EgressDown>();
        readerB.Push(new EgressUp { Hello = new Hello { GatewayId = "gw-1" } });
        var runB = replicaB.RunAsync(readerB, writerB, CancellationToken.None);
        await bus.WaitForSubscription("gw-1");

        // Commands for gw-1 now reach replica B.
        var id = Guid.NewGuid();
        await bus.Deliver("gw-1", Command(id));
        var downB = await writerB.ReadAsync();
        Assert.Equal(id.ToString(), downB.Command.ControlId);

        readerB.Complete();
        await runB;
    }

    [Fact]
    public async Task Gateway_ReconnectingToSameReplica_SupersedesOldStream()
    {
        // #114 supersede: a gateway reconnects to the SAME replica before its previous stream has
        // finished tearing down (half-open drop). The new Hello must be accepted, the old stream
        // torn down, and commands must flow only over the new stream — the old AlreadyExists
        // rejection would have locked the gateway out until the pod restarted.
        var bus = new SharedEgressBus();
        var registry = new GatewayConnectionRegistry();
        var replica = new GatewayEgressService(bus, registry, NullLogger<GatewayEgressService>.Instance);

        var reader1 = new FakeStreamReader<EgressUp>();
        var writer1 = new FakeStreamWriter<EgressDown>();
        reader1.Push(new EgressUp { Hello = new Hello { GatewayId = "gw-1" } });
        var run1 = replica.RunAsync(reader1, writer1, CancellationToken.None);
        await bus.WaitForSubscription("gw-1");

        // Reconnect on the same replica while stream 1 is still open.
        var reader2 = new FakeStreamReader<EgressUp>();
        var writer2 = new FakeStreamWriter<EgressDown>();
        reader2.Push(new EgressUp { Hello = new Hello { GatewayId = "gw-1" } });
        var run2 = replica.RunAsync(reader2, writer2, CancellationToken.None);

        // The old stream is superseded → RunAsync returns without the client completing it.
        await run1;
        Assert.True(registry.IsConnected("gw-1"));
        Assert.Equal(1, registry.Count);

        // Commands now flow only over the new stream.
        await bus.WaitForSubscriber("gw-1");
        var id = Guid.NewGuid();
        await bus.Deliver("gw-1", Command(id));
        var down = await writer2.ReadAsync();
        Assert.Equal(id.ToString(), down.Command.ControlId);
        Assert.False(writer1.TryReadImmediately(out _));

        reader2.Complete();
        await run2;
        Assert.False(registry.IsConnected("gw-1"));
    }

    /// <summary>
    /// Models the NATS per-gateway subject across replicas: at most one subscriber per gateway
    /// (the holding replica). Publishing a command fans in to that subscriber.
    /// </summary>
    private sealed class SharedEgressBus : IEgressCommandBus
    {
        private readonly ConcurrentDictionary<string, Func<string, Task>> _byGateway = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _subscribed = new();
        private readonly ConcurrentDictionary<string, Func<string, Task>> _pointListByGateway = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _pointListSubscribed = new();

        public Task<IAsyncDisposable> SubscribeAsync(string gatewayId, Func<string, Task> onCommand, CancellationToken cancellationToken)
        {
            _byGateway[gatewayId] = onCommand;
            Signal(gatewayId).TrySetResult();
            return Task.FromResult<IAsyncDisposable>(new Unsub(() =>
            {
                // Only clear if we are still the current subscriber — a newer subscriber may have
                // replaced us before this disposal; don't drop its handler or its signal.
                var wasCurrent = _byGateway.TryRemove(new KeyValuePair<string, Func<string, Task>>(gatewayId, onCommand));
                if (wasCurrent) _subscribed.TryRemove(gatewayId, out _);
            }));
        }

        public Task PublishResultAsync(string controlId, string resultJson, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<IAsyncDisposable> SubscribePointListUpdatesAsync(string gatewayId, Func<string, Task> onUpdate, CancellationToken cancellationToken)
        {
            _pointListByGateway[gatewayId] = onUpdate;
            PointListSignal(gatewayId).TrySetResult();
            return Task.FromResult<IAsyncDisposable>(new Unsub(() =>
            {
                var wasCurrent = _pointListByGateway.TryRemove(new KeyValuePair<string, Func<string, Task>>(gatewayId, onUpdate));
                if (wasCurrent) _pointListSubscribed.TryRemove(gatewayId, out _);
            }));
        }

        public async Task Deliver(string gatewayId, string commandJson)
        {
            Assert.True(_byGateway.TryGetValue(gatewayId, out var handler), $"no subscriber for {gatewayId}");
            await handler!(commandJson);
        }

        public async Task DeliverPointListUpdate(string gatewayId, string revision)
        {
            Assert.True(_pointListByGateway.TryGetValue(gatewayId, out var handler), $"no point-list subscriber for {gatewayId}");
            await handler!(revision);
        }

        public bool HasSubscriber(string gatewayId) => _byGateway.ContainsKey(gatewayId);

        /// <summary>Bounded poll until a (possibly re-subscribed) handler is present for the gateway.</summary>
        public async Task WaitForSubscriber(string gatewayId)
        {
            for (var i = 0; i < 200; i++)
            {
                if (_byGateway.ContainsKey(gatewayId)) return;
                await Task.Delay(5);
            }
            Assert.True(_byGateway.ContainsKey(gatewayId), $"no subscriber for {gatewayId}");
        }

        public Task WaitForSubscription(string gatewayId) => Signal(gatewayId).Task;

        public Task WaitForPointListSubscription(string gatewayId) => PointListSignal(gatewayId).Task;

        private TaskCompletionSource Signal(string gatewayId)
            => _subscribed.GetOrAdd(gatewayId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        private TaskCompletionSource PointListSignal(string gatewayId)
            => _pointListSubscribed.GetOrAdd(gatewayId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        private sealed class Unsub(Action onDispose) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { onDispose(); return ValueTask.CompletedTask; }
        }
    }
}
