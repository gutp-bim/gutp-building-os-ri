using BuildingOS.Shared.Domain;
using BuildingOS.Shared.Infrastructure.ControlRouting;
using BuildingOS.Shared.Infrastructure.PointControl;
using NATS.Client.Core;
using System.Text;
using System.Text.Json;

namespace BuildingOs.ApiServer.Services;

/// <summary>
/// OSS mode: publishes the control command to NATS. The subject is chosen by
/// <see cref="ControlRequestRouting"/> — in-process handler types (Hono/Kandt) go to the durable
/// generic request subject; gateway-bridge types (BacnetSim) go to their per-gateway subject so the
/// command reaches the GatewayBridge replica holding that gateway's stream.
///
/// For the per-gateway egress path the command is sent as a NATS request (not a fire-and-forget
/// publish): if no replica currently holds the gateway, NATS reports "no responders" immediately and
/// we return <see cref="ControlDeliveryStatus.GatewayOffline"/> so the caller fails fast instead of
/// waiting out the result timeout (#186). A live replica acks after forwarding the command down its
/// stream. An ack timeout (replica present but slow) is treated as Delivered — only an explicit
/// no-responders is offline (the result timeout remains the backstop for the slow/raced case).
/// Sets Nats-Msg-Id header to controlId for deduplication.
/// </summary>
public sealed class NatsPointControlCommandPublisher(INatsConnection nats) : IPointControlCommandPublisher
{
    // Short ack wait: a live replica acks as soon as it forwards the command down its gRPC stream.
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(2);

    public async Task<ControlDeliveryStatus> PublishAsync(
        PointControlInfo command, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command));
        var headers = new NatsHeaders { ["Nats-Msg-Id"] = command.id.ToString() };
        var subject = ControlRequestRouting.SubjectFor(command.Type, command.GatewayId);

        if (!ControlRequestRouting.IsPerGatewayEgress(command.Type, command.GatewayId))
        {
            // Durable generic request stream (Hono/Kandt in-process) — fire-and-forget, always delivered.
            await nats.PublishAsync(subject, bytes, headers: headers, cancellationToken: cancellationToken);
            return ControlDeliveryStatus.Delivered;
        }

        try
        {
            // Request-ack: no responders → gateway offline; an ack (or even a slow/lost reply) → delivered.
            await nats.RequestAsync<byte[], byte[]>(
                subject, bytes, headers: headers,
                replyOpts: new NatsSubOpts { Timeout = AckTimeout },
                cancellationToken: cancellationToken);
            return ControlDeliveryStatus.Delivered;
        }
        catch (NatsNoRespondersException)
        {
            return ControlDeliveryStatus.GatewayOffline;
        }
        catch (NatsNoReplyException)
        {
            // A replica received the request but did not ack within the window (slow/dropped ack).
            // It is still delivered; the result timeout is the backstop for the rare raced case.
            return ControlDeliveryStatus.Delivered;
        }
    }
}
