using BuildingOS.ConnectorWorker.Connectors;
using BuildingOS.Shared.Domain;
using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using BuildingOS.Shared.Infrastructure.ControlRouting;
using BuildingOS.Shared.Infrastructure.DeviceControlHandler;
using BuildingOS.Shared.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace BuildingOS.Shared.Test.Infrastructure.ConnectorWorker;

/// <summary>
/// Unit tests for NatsPointControlWorker.
/// Uses InProcessMessageSubscription so no real NATS server is required. The worker re-resolves the
/// gateway's binding from <see cref="IGatewayConnectionRegistry"/> and dispatches by binding type
/// (#154 Phase 2) — the wire-supplied Type is not the dispatch key.
/// </summary>
public class NatsPointControlWorkerTest
{
    [Fact]
    public async Task DispatchesToHandler_MatchingResolvedBinding()
    {
        var (worker, subscription, publisher) = Build(
            [new FakeHandler("kandt", PointControlResult.Success, "ok")],
            registry: RegistryFor("gw-1", "kandt"));
        var info = new PointControlInfo { id = Guid.NewGuid(), GatewayId = "gw-1", Type = "ignored", Body = "{}" };

        await RunOneMessageAsync(worker, subscription, info);

        Assert.Single(publisher.Published);
        Assert.Contains("building-os.control.result." + info.id, publisher.Published[0].Subject);
    }

    [Fact]
    public async Task PublishesSuccessResult()
    {
        var (worker, subscription, publisher) = Build(
            [new FakeHandler("kandt", PointControlResult.Success, "device-ack")],
            registry: RegistryFor("gw-1", "kandt"));
        var info = new PointControlInfo { id = Guid.NewGuid(), GatewayId = "gw-1", Body = "{}" };

        await RunOneMessageAsync(worker, subscription, info);

        var json = publisher.Published[0].Payload;
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("device-ack", doc.RootElement.GetProperty("response").GetString());
    }

    [Fact]
    public async Task PublishesFailedResult_WhenNoHandlerForBinding()
    {
        // Registry resolves binding "kandt" but no handler registered for it → unsupported.
        var (worker, subscription, publisher) = Build([], registry: RegistryFor("gw-1", "kandt"));
        var info = new PointControlInfo { id = Guid.NewGuid(), GatewayId = "gw-1", Body = "{}" };

        await RunOneMessageAsync(worker, subscription, info);

        var json = publisher.Published[0].Payload;
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task ResolvesByGatewayId_NotByWireType()
    {
        // Two gateways of the same wire Type would still route by their resolved binding. Here the
        // wire Type is bogus but the gateway maps to "hono", so the hono handler is selected.
        var capturing = new CapturingHandler("hono");
        var (worker, subscription, _) = Build([capturing], registry: RegistryFor("gw-h", "hono"));
        var info = new PointControlInfo
        {
            id = Guid.NewGuid(), PointId = "urn:pt:test-123", GatewayId = "gw-h", Type = "BogusType", Body = "{}",
        };

        await RunOneMessageAsync(worker, subscription, info);

        Assert.Equal("urn:pt:test-123", capturing.CapturedInfo?.PointId);
        Assert.Equal("gw-h", capturing.CapturedConnection?.GatewayId);
    }

    [Fact]
    public async Task PassesResolvedConnectionSettings_ToHandler()
    {
        var capturing = new CapturingHandler("hono");
        var registry = new ConfigGatewayConnectionRegistry(
            new Dictionary<string, string> { ["gw-h"] = "hono" }, "hono",
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["gw-h"] = new Dictionary<string, string> { ["host"] = "hono-h" },
            },
            new Dictionary<string, IReadOnlyDictionary<string, string>>());
        var (worker, subscription, _) = Build([capturing], registry);
        var info = new PointControlInfo { id = Guid.NewGuid(), GatewayId = "gw-h", Body = "{}" };

        await RunOneMessageAsync(worker, subscription, info);

        Assert.Equal("hono-h", capturing.CapturedConnection?.Get("host"));
    }

    [Fact]
    public async Task PublishesErrorResultWhenHandlerThrows()
    {
        var (worker, subscription, publisher) = Build(
            [new ThrowingHandler("kandt")], registry: RegistryFor("gw-1", "kandt"));
        var info = new PointControlInfo { id = Guid.NewGuid(), GatewayId = "gw-1", Body = "{}" };

        await RunOneMessageAsync(worker, subscription, info);

        var json = publisher.Published[0].Payload;
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static IGatewayConnectionRegistry RegistryFor(string gatewayId, string binding)
        => new ConfigGatewayConnectionRegistry(
            new Dictionary<string, string> { [gatewayId] = binding }, binding,
            new Dictionary<string, IReadOnlyDictionary<string, string>>(),
            new Dictionary<string, IReadOnlyDictionary<string, string>>());

    private static (NatsPointControlWorker Worker, InProcessMessageSubscription Subscription, FakeNatsPublisher Publisher)
        Build(IEnumerable<IDeviceControlHandler> handlers, IGatewayConnectionRegistry registry)
    {
        var subscription = new InProcessMessageSubscription();
        var publisher = new FakeNatsPublisher();
        var worker = new NatsPointControlWorker(
            subscription, handlers, registry, publisher, NullLogger<NatsPointControlWorker>.Instance);
        return (worker, subscription, publisher);
    }

    private static async Task RunOneMessageAsync(
        NatsPointControlWorker worker,
        InProcessMessageSubscription subscription,
        PointControlInfo info)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);

        var json = JsonSerializer.Serialize(info);
        await subscription.DispatchAsync(json, cts.Token);

        await worker.StopAsync(CancellationToken.None);
    }

    // ── Fakes ──────────────────────────────────────────────────────────────

    private sealed class FakeHandler(string bindingType, PointControlResult result, string? response)
        : IDeviceControlHandler
    {
        public string BindingType => bindingType;

        public Task<PointControlInfo> ExecuteControlAsync(
            PointControlInfo info, GatewayConnection connection, CancellationToken cancellationToken)
        {
            info.Result = result;
            info.Response = response;
            return Task.FromResult(info);
        }
    }

    private sealed class CapturingHandler(string bindingType) : IDeviceControlHandler
    {
        public string BindingType => bindingType;
        public PointControlInfo? CapturedInfo { get; private set; }
        public GatewayConnection? CapturedConnection { get; private set; }

        public Task<PointControlInfo> ExecuteControlAsync(
            PointControlInfo info, GatewayConnection connection, CancellationToken cancellationToken)
        {
            CapturedInfo = info;
            CapturedConnection = connection;
            info.Result = PointControlResult.Success;
            return Task.FromResult(info);
        }
    }

    private sealed class ThrowingHandler(string bindingType) : IDeviceControlHandler
    {
        public string BindingType => bindingType;
        public Task<PointControlInfo> ExecuteControlAsync(
            PointControlInfo info, GatewayConnection connection, CancellationToken cancellationToken)
            => throw new InvalidOperationException("handler-error");
    }

    private sealed class FakeNatsPublisher : INatsPublisher
    {
        public List<(string Subject, string Payload)> Published { get; } = [];

        public Task PublishAsync(string subject, string message, CancellationToken cancellationToken = default)
        {
            Published.Add((subject, message));
            return Task.CompletedTask;
        }
    }
}
