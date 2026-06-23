using System.Text;
using BuildingOS.Shared.Infrastructure.ControlRouting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;

namespace BuildingOS.GatewayBridge.Infrastructure;

/// <summary>
/// Core-NATS implementation of <see cref="IEgressCommandBus"/>. Egress uses core (not JetStream)
/// pub/sub: commands are ephemeral and only delivered to the replica currently subscribed for that
/// gateway. Because only the live replica subscribes to the per-gateway subject, the publisher can
/// send the command as a NATS request: this replica acks each command after forwarding it down the
/// stream, and an offline gateway (no subscriber) surfaces to the publisher as "no responders" (#186).
/// There is no persisted backlog to drain on reconnect.
/// </summary>
public sealed class NatsEgressCommandBus(INatsConnection nats, ILogger<NatsEgressCommandBus>? logger = null)
    : IEgressCommandBus
{
    private readonly ILogger<NatsEgressCommandBus> _logger = logger ?? NullLogger<NatsEgressCommandBus>.Instance;

    public async Task<IAsyncDisposable> SubscribeAsync(
        string gatewayId, Func<string, Task> onCommand, CancellationToken cancellationToken)
    {
        var subject = EgressSubjects.PerGatewayRequest(gatewayId);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var loop = Task.Run(async () =>
        {
            await foreach (var msg in nats.SubscribeAsync<string>(subject, cancellationToken: cts.Token))
            {
                if (msg.Data is not { } data) continue;

                await onCommand(data).ConfigureAwait(false);

                // Ack the request so the publisher's liveness probe confirms this replica received and
                // forwarded the command. Empty byte payload matches the publisher's byte[] reply type.
                // Fire-and-forget publishes (no reply subject) skip the ack. The ack is best-effort —
                // a failed ack publish (transient NATS drop) must not fault the loop and silently stop
                // forwarding for a still-connected stream; the publisher already treats a missing reply
                // as Delivered (the result timeout is the backstop).
                if (!string.IsNullOrEmpty(msg.ReplyTo))
                {
                    try
                    {
                        await nats.PublishAsync(msg.ReplyTo, Array.Empty<byte>(), cancellationToken: cts.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Egress ack publish failed for gateway {GatewayId}", gatewayId);
                    }
                }
            }
        }, cts.Token);

        return new Subscription(cts, loop);
    }

    public async Task PublishResultAsync(string controlId, string resultJson, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(resultJson);
        await nats.PublishAsync(EgressSubjects.Result(controlId), bytes, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<IAsyncDisposable> SubscribePointListUpdatesAsync(
        string gatewayId, Func<string, Task> onUpdate, CancellationToken cancellationToken)
    {
        var subject = EgressSubjects.PointListUpdate(gatewayId);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var loop = Task.Run(async () =>
        {
            // Empty payload is a valid "revalidate" signal (revision unknown to the publisher).
            await foreach (var msg in nats.SubscribeAsync<string>(subject, cancellationToken: cts.Token))
            {
                // Push is best-effort (ETag polling is the reliability backstop): a transient failure
                // forwarding one signal must not fault the loop and stop all future updates.
                try
                {
                    await onUpdate(msg.Data ?? string.Empty).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Point-list update forward failed for gateway {GatewayId}", gatewayId);
                }
            }
        }, cts.Token);

        return Task.FromResult<IAsyncDisposable>(new Subscription(cts, loop));
    }

    private sealed class Subscription(CancellationTokenSource cts, Task loop) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            cts.Cancel();
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            cts.Dispose();
        }
    }
}
