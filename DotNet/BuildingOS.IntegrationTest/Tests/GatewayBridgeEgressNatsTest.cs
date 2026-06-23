using System.Text;
using System.Text.Json;
using BuildingOS.GatewayBridge.Infrastructure;
using BuildingOS.GatewayBridge.Mapping;
using BuildingOS.IntegrationTest.Collections;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared.Domain;
using BuildingOS.Shared.Infrastructure.ControlRouting;
using NATS.Client.Core;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>
/// In-cluster leg of the bbc-sim downlink E2E (#163): the GatewayBridge ⇄ NATS transport against a
/// real NATS server. Proves that a control command published to the per-gateway subject reaches a
/// bridge replica's egress subscription, and that a result published back lands on the result subject
/// consumed by WaitForResult. The external bbc-sim/BOWS leg (gRPC → BACnet WriteProperty) is covered
/// by the runbook in docs/oss-bbc-sim-e2e-runbook.md and tracked by takashikasuya/bacnet-sim-gateway#67.
/// </summary>
[Collection(Names.Nats)]
public class GatewayBridgeEgressNatsTest(NatsFixture fixture) : IntegrationTestBase
{
    [Fact]
    public async Task Command_PublishedToPerGatewaySubject_ReachesEgressSubscription()
    {
        await using var conn = fixture.CreateConnection();
        await conn.ConnectAsync();
        var bus = new NatsEgressCommandBus(conn);

        const string gatewayId = "gw-int-1";
        var controlId = Guid.NewGuid();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await bus.SubscribeAsync(gatewayId,
            cmdJson => { received.TrySetResult(cmdJson); return Task.CompletedTask; }, CancellationToken.None);

        var commandJson = JsonSerializer.Serialize(new PointControlInfo
        {
            id = controlId, PointId = "PT-int", Type = DeviceControlType.BacnetSim, GatewayId = gatewayId,
            Body = JsonSerializer.Serialize(new { value = 24.5 }),
        });

        // Core-NATS subscription may not be live the instant SubscribeAsync returns → publish with retry.
        var json = await PublishUntilReceived(conn, EgressSubjects.PerGatewayRequest(gatewayId), commandJson, received);

        var command = ControlCommandMapper.ToControlCommand(json);
        Assert.NotNull(command);
        Assert.Equal(controlId.ToString(), command!.ControlId);
        Assert.Equal("PT-int", command.PointId);
        Assert.Equal(24.5, command.PresentValue);
    }

    [Fact]
    public async Task Request_ToConnectedGateway_IsAcked_SoPublisherSeesDelivered()
    {
        // #186: the bridge subscription acks each request after forwarding it down the stream. A
        // publisher using RequestAsync therefore gets a reply (→ ControlDeliveryStatus.Delivered).
        await using var conn = fixture.CreateConnection();
        await conn.ConnectAsync();
        var bus = new NatsEgressCommandBus(conn);

        const string gatewayId = "gw-ack-1";
        var forwarded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await bus.SubscribeAsync(gatewayId,
            _ => { forwarded.TrySetResult(); return Task.CompletedTask; }, CancellationToken.None);

        var subject = EgressSubjects.PerGatewayRequest(gatewayId);
        var payload = Encoding.UTF8.GetBytes("{}");

        // Core-NATS subscription may not be propagated the instant SubscribeAsync returns → retry the
        // request until the live subscription acks it.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var acked = false;
        while (!acked)
        {
            try
            {
                await conn.RequestAsync<byte[], byte[]>(
                    subject, payload, replyOpts: new NatsSubOpts { Timeout = TimeSpan.FromSeconds(1) },
                    cancellationToken: timeout.Token);
                acked = true;
            }
            catch (NatsNoRespondersException)
            {
                await Task.Delay(150, timeout.Token); // subscription not live yet
            }
        }

        Assert.True(acked);
        Assert.True(forwarded.Task.IsCompleted); // the command also reached the onCommand callback
    }

    [Fact]
    public async Task Request_ToOfflineGateway_YieldsNoResponders_ForImmediateFailFast()
    {
        // #186: no replica subscribes for this gateway → NATS reports no-responders immediately, which
        // the publisher maps to ControlDeliveryStatus.GatewayOffline (no timeout wait).
        await using var conn = fixture.CreateConnection();
        await conn.ConnectAsync();

        var subject = EgressSubjects.PerGatewayRequest("gw-never-connected");
        var payload = Encoding.UTF8.GetBytes("{}");

        await Assert.ThrowsAsync<NatsNoRespondersException>(async () =>
            await conn.RequestAsync<byte[], byte[]>(
                subject, payload, replyOpts: new NatsSubOpts { Timeout = TimeSpan.FromSeconds(5) },
                cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task Result_Published_LandsOnResultSubject_ForWaitForResult()
    {
        await using var conn = fixture.CreateConnection();
        await conn.ConnectAsync();
        var bus = new NatsEgressCommandBus(conn);

        var controlId = Guid.NewGuid().ToString();
        var resultSubject = EgressSubjects.Result(controlId);
        var got = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var sub = Task.Run(async () =>
        {
            await foreach (var msg in conn.SubscribeAsync<byte[]>(resultSubject, cancellationToken: cts.Token))
            {
                if (msg.Data is { } data) { got.TrySetResult(Encoding.UTF8.GetString(data)); break; }
            }
        }, cts.Token);

        var resultJson = JsonSerializer.Serialize(new { success = true, response = "ack" });
        try
        {
            // Re-publish until the late-starting subscriber observes it.
            while (!got.Task.IsCompleted)
            {
                await bus.PublishResultAsync(controlId, resultJson, CancellationToken.None);
                await Task.WhenAny(got.Task, Task.Delay(150));
                cts.Token.ThrowIfCancellationRequested();
            }

            using var doc = JsonDocument.Parse(await got.Task);
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("ack", doc.RootElement.GetProperty("response").GetString());
        }
        finally
        {
            // Always cancel + observe the subscriber task so a timeout can't leak an
            // unobserved (faulted) task that fails a later, unrelated test.
            cts.Cancel();
            try { await sub; } catch (OperationCanceledException) { }
        }
    }

    private static async Task<string> PublishUntilReceived(
        NATS.Client.Core.NatsConnection conn, string subject, string payload, TaskCompletionSource<string> received)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var bytes = Encoding.UTF8.GetBytes(payload);
        while (!received.Task.IsCompleted)
        {
            await conn.PublishAsync(subject, bytes, cancellationToken: timeout.Token);
            await Task.WhenAny(received.Task, Task.Delay(150, timeout.Token));
            timeout.Token.ThrowIfCancellationRequested();
        }
        return await received.Task;
    }
}
