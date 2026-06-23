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

    /// <summary>
    /// Models the NATS per-gateway subject across replicas: at most one subscriber per gateway
    /// (the holding replica). Publishing a command fans in to that subscriber.
    /// </summary>
    private sealed class SharedEgressBus : IEgressCommandBus
    {
        private readonly ConcurrentDictionary<string, Func<string, Task>> _byGateway = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _subscribed = new();

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
            => Task.FromResult<IAsyncDisposable>(new Unsub(() => { }));

        public async Task Deliver(string gatewayId, string commandJson)
        {
            Assert.True(_byGateway.TryGetValue(gatewayId, out var handler), $"no subscriber for {gatewayId}");
            await handler!(commandJson);
        }

        public bool HasSubscriber(string gatewayId) => _byGateway.ContainsKey(gatewayId);

        public Task WaitForSubscription(string gatewayId) => Signal(gatewayId).Task;

        private TaskCompletionSource Signal(string gatewayId)
            => _subscribed.GetOrAdd(gatewayId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        private sealed class Unsub(Action onDispose) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { onDispose(); return ValueTask.CompletedTask; }
        }
    }
}
