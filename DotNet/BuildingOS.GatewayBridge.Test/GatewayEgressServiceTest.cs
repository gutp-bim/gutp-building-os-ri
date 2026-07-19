using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using BuildingOS.GatewayBridge.Infrastructure;
using BuildingOS.GatewayBridge.Protos;
using BuildingOS.GatewayBridge.Services;
using BuildingOS.Shared.Domain;
using BuildingOS.Shared.Infrastructure.Oss;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.GatewayBridge.Test;

public class GatewayEgressServiceTest
{
    [Fact]
    public async Task Connect_DeliversCommandDownAndPublishesResultUp_RoundTrip()
    {
        var bus = new FakeEgressCommandBus();
        var registry = new GatewayConnectionRegistry();
        var service = new GatewayEgressService(bus, registry, NullLogger<GatewayEgressService>.Instance);

        var reader = new FakeStreamReader<EgressUp>();
        var writer = new FakeStreamWriter<EgressDown>();

        // 1. gateway says hello
        reader.Push(new EgressUp { Hello = new Hello { GatewayId = "gw-1" } });

        var run = service.RunAsync(reader, writer, CancellationToken.None);

        // 2. bridge subscribes for gw-1
        await bus.WaitForSubscription("gw-1");
        Assert.True(registry.IsConnected("gw-1"));

        // 3. a command published to gw-1 is forwarded down the stream
        var controlId = Guid.NewGuid();
        var body = JsonSerializer.Serialize(new { value = 23.0 });
        await bus.Deliver("gw-1", JsonSerializer.Serialize(new PointControlInfo
        {
            id = controlId, PointId = "PT001", Type = DeviceControlType.BacnetSim, Body = body,
        }));

        var down = await writer.ReadAsync();
        Assert.Equal(controlId.ToString(), down.Command.ControlId);
        Assert.Equal("PT001", down.Command.PointId);
        Assert.Equal(23.0, down.Command.PresentValue);

        // 4. gateway returns a result up the stream → published to result subject
        reader.Push(new EgressUp { Result = new ControlResult { ControlId = controlId.ToString(), Success = true, Response = "ack" } });

        var published = await bus.WaitForResult();
        Assert.Equal(controlId.ToString(), published.ControlId);
        using (var doc = JsonDocument.Parse(published.Json))
        {
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("ack", doc.RootElement.GetProperty("response").GetString());
        }

        // 5. stream ends → registry torn down (stateless)
        reader.Complete();
        await run;
        Assert.False(registry.IsConnected("gw-1"));
    }

    [Fact]
    public async Task Connect_ForwardsPointListUpdate_DownStream()
    {
        var bus = new FakeEgressCommandBus();
        var service = new GatewayEgressService(bus, new GatewayConnectionRegistry(),
            NullLogger<GatewayEgressService>.Instance);
        var reader = new FakeStreamReader<EgressUp>();
        var writer = new FakeStreamWriter<EgressDown>();

        reader.Push(new EgressUp { Hello = new Hello { GatewayId = "gw-1" } });
        var run = service.RunAsync(reader, writer, CancellationToken.None);

        await bus.WaitForPointListSubscription("gw-1");
        await bus.DeliverPointListUpdate("gw-1", "\"sha256:abc\"");

        var down = await writer.ReadAsync();
        Assert.Equal(EgressDown.MOneofCase.PointListUpdate, down.MCase);
        Assert.Equal("gw-1", down.PointListUpdate.GatewayId);
        Assert.Equal("\"sha256:abc\"", down.PointListUpdate.Revision);

        reader.Complete();
        await run;
    }

    [Fact]
    public async Task Connect_RejectsFirstFrameThatIsNotHello()
    {
        var service = new GatewayEgressService(new FakeEgressCommandBus(), new GatewayConnectionRegistry(),
            NullLogger<GatewayEgressService>.Instance);
        var reader = new FakeStreamReader<EgressUp>();
        reader.Push(new EgressUp { Result = new ControlResult { ControlId = "x" } });
        reader.Complete();

        await Assert.ThrowsAsync<RpcException>(() =>
            service.RunAsync(reader, new FakeStreamWriter<EgressDown>(), CancellationToken.None));
    }

    [Fact]
    public async Task Connect_DoesNotForwardCommandsForOtherGateways()
    {
        var bus = new FakeEgressCommandBus();
        var service = new GatewayEgressService(bus, new GatewayConnectionRegistry(),
            NullLogger<GatewayEgressService>.Instance);
        var reader = new FakeStreamReader<EgressUp>();
        var writer = new FakeStreamWriter<EgressDown>();
        reader.Push(new EgressUp { Hello = new Hello { GatewayId = "gw-1" } });
        var run = service.RunAsync(reader, writer, CancellationToken.None);
        await bus.WaitForSubscription("gw-1");

        // No subscriber exists for gw-2 on this replica → nothing is delivered/written.
        var delivered = await bus.TryDeliver("gw-2", "{}");
        Assert.False(delivered);
        Assert.False(writer.TryReadImmediately(out _));

        reader.Complete();
        await run;
    }

    [Fact]
    public async Task Connect_MarksConnected_OnOpen_AndDisconnected_OnClose()
    {
        // #230/ADR-0004: while the stream is up the replica refreshes a cross-replica heartbeat; on a
        // graceful close it clears the entry (epoch-guarded by replicaId).
        var bus = new FakeEgressCommandBus();
        var status = new FakeConnectionStatusStore();
        // Long interval so the periodic loop never fires during the test — only the initial mark counts.
        var options = new GatewayHeartbeatOptions(TimeSpan.FromMinutes(5), "replica-1");
        var service = new GatewayEgressService(bus, new GatewayConnectionRegistry(),
            NullLogger<GatewayEgressService>.Instance, status, options);
        var reader = new FakeStreamReader<EgressUp>();
        var writer = new FakeStreamWriter<EgressDown>();

        reader.Push(new EgressUp { Hello = new Hello { GatewayId = "gw-1" } });
        var run = service.RunAsync(reader, writer, CancellationToken.None);

        await bus.WaitForSubscription("gw-1");
        // Initial mark carries no applied revision yet (the gateway reports it via EgressUp.Status, #230 Phase 2b).
        Assert.Equal(("gw-1", "replica-1", (string?)null), Assert.Single(status.Connected));

        reader.Complete();
        await run;
        Assert.Equal(("gw-1", "replica-1"), Assert.Single(status.Disconnected));
    }

    [Fact]
    public async Task Connect_Status_UpdatesAppliedRevision()
    {
        // #230 Phase 2b: a gateway status frame carries the applied point-list ETag; the bridge records
        // it on the connection heartbeat so the admin read side can derive pointlist sync state.
        var bus = new FakeEgressCommandBus();
        var status = new FakeConnectionStatusStore();
        var options = new GatewayHeartbeatOptions(TimeSpan.FromMinutes(5), "replica-1");
        var service = new GatewayEgressService(bus, new GatewayConnectionRegistry(),
            NullLogger<GatewayEgressService>.Instance, status, options);
        var reader = new FakeStreamReader<EgressUp>();
        var writer = new FakeStreamWriter<EgressDown>();

        reader.Push(new EgressUp { Hello = new Hello { GatewayId = "gw-1" } });
        var run = service.RunAsync(reader, writer, CancellationToken.None);
        await bus.WaitForSubscription("gw-1");

        reader.Push(new EgressUp { Status = new GatewayStatus { AppliedRevision = "\"sha256:abc\"" } });

        // Spin until the status frame has been processed (the request loop is async).
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (status.LatestApplied("gw-1") is null && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal("\"sha256:abc\"", status.LatestApplied("gw-1"));

        reader.Complete();
        await run;
    }

    [Fact]
    public async Task Connect_WithoutStatusStore_StillRoundTrips()
    {
        // The heartbeat is optional (null in the pre-#230 wiring / unit tests) — routing is unaffected.
        var bus = new FakeEgressCommandBus();
        var service = new GatewayEgressService(bus, new GatewayConnectionRegistry(),
            NullLogger<GatewayEgressService>.Instance);
        var reader = new FakeStreamReader<EgressUp>();
        reader.Push(new EgressUp { Hello = new Hello { GatewayId = "gw-1" } });
        var run = service.RunAsync(reader, new FakeStreamWriter<EgressDown>(), CancellationToken.None);
        await bus.WaitForSubscription("gw-1");
        reader.Complete();
        await run; // no throw
    }

    [Fact]
    public async Task Connect_UnregistersGateway_WhenSubscribeFails()
    {
        // A transient failure in SubscribeAsync must not leave the gateway registered (which would
        // lock it out with AlreadyExists on every reconnect until pod restart).
        var registry = new GatewayConnectionRegistry();
        var service = new GatewayEgressService(new ThrowingSubscribeBus(), registry,
            NullLogger<GatewayEgressService>.Instance);
        var reader = new FakeStreamReader<EgressUp>();
        reader.Push(new EgressUp { Hello = new Hello { GatewayId = "gw-1" } });

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.RunAsync(reader, new FakeStreamWriter<EgressDown>(), CancellationToken.None));

        Assert.False(registry.IsConnected("gw-1"));
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeConnectionStatusStore : IGatewayConnectionStatusStore
    {
        public ConcurrentBag<(string GatewayId, string ReplicaId, string? AppliedRevision)> Connected { get; } = new();
        public ConcurrentBag<(string GatewayId, string ReplicaId)> Disconnected { get; } = new();

        /// <summary>The most recent applied revision written for a gateway (#230 Phase 2b), or null.</summary>
        public string? LatestApplied(string gatewayId) => Connected
            .Where(c => c.GatewayId == gatewayId)
            .Select(c => c.AppliedRevision)
            .LastOrDefault(r => r is not null);

        public Task MarkConnectedAsync(
            string gatewayId, string replicaId, string? appliedRevision = null, CancellationToken ct = default)
        {
            Connected.Add((gatewayId, replicaId, appliedRevision));
            return Task.CompletedTask;
        }

        public Task MarkDisconnectedAsync(string gatewayId, string replicaId, CancellationToken ct = default)
        {
            Disconnected.Add((gatewayId, replicaId));
            return Task.CompletedTask;
        }

        public Task<GatewayConnectionStatus?> GetAsync(string gatewayId, CancellationToken ct = default)
            => Task.FromResult<GatewayConnectionStatus?>(null);
    }

    private sealed class ThrowingSubscribeBus : IEgressCommandBus
    {
        public Task<IAsyncDisposable> SubscribeAsync(string gatewayId, Func<string, Task> onCommand, CancellationToken cancellationToken)
            => throw new InvalidOperationException("nats down");
        public Task PublishResultAsync(string controlId, string resultJson, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task<IAsyncDisposable> SubscribePointListUpdatesAsync(string gatewayId, Func<string, Task> onUpdate, CancellationToken cancellationToken)
            => throw new InvalidOperationException("nats down");
    }

    private sealed class FakeEgressCommandBus : IEgressCommandBus
    {
        private readonly ConcurrentDictionary<string, Func<string, Task>> _handlers = new();
        private readonly ConcurrentDictionary<string, Func<string, Task>> _plHandlers = new();
        private readonly Channel<(string ControlId, string Json)> _results = Channel.CreateUnbounded<(string, string)>();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _subscribed = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _plSubscribed = new();

        public Task<IAsyncDisposable> SubscribeAsync(string gatewayId, Func<string, Task> onCommand, CancellationToken cancellationToken)
        {
            _handlers[gatewayId] = onCommand;
            Signal(gatewayId).TrySetResult();
            return Task.FromResult<IAsyncDisposable>(new Unsub(() => _handlers.TryRemove(gatewayId, out _)));
        }

        public Task<IAsyncDisposable> SubscribePointListUpdatesAsync(string gatewayId, Func<string, Task> onUpdate, CancellationToken cancellationToken)
        {
            _plHandlers[gatewayId] = onUpdate;
            PlSignal(gatewayId).TrySetResult();
            return Task.FromResult<IAsyncDisposable>(new Unsub(() => _plHandlers.TryRemove(gatewayId, out _)));
        }

        public async Task DeliverPointListUpdate(string gatewayId, string revision)
        {
            Assert.True(_plHandlers.TryGetValue(gatewayId, out var handler), $"no point-list subscriber for {gatewayId}");
            await handler!(revision);
        }

        public Task WaitForPointListSubscription(string gatewayId) => PlSignal(gatewayId).Task;

        public async Task PublishResultAsync(string controlId, string resultJson, CancellationToken cancellationToken)
            => await _results.Writer.WriteAsync((controlId, resultJson), cancellationToken);

        public async Task Deliver(string gatewayId, string commandJson)
            => Assert.True(await TryDeliver(gatewayId, commandJson), $"no subscriber for {gatewayId}");

        public async Task<bool> TryDeliver(string gatewayId, string commandJson)
        {
            if (!_handlers.TryGetValue(gatewayId, out var handler)) return false;
            await handler(commandJson);
            return true;
        }

        public Task WaitForSubscription(string gatewayId) => Signal(gatewayId).Task;

        public async Task<(string ControlId, string Json)> WaitForResult()
            => await _results.Reader.ReadAsync();

        private TaskCompletionSource Signal(string gatewayId)
            => _subscribed.GetOrAdd(gatewayId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        private TaskCompletionSource PlSignal(string gatewayId)
            => _plSubscribed.GetOrAdd(gatewayId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        private sealed class Unsub(Action onDispose) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { onDispose(); return ValueTask.CompletedTask; }
        }
    }
}
