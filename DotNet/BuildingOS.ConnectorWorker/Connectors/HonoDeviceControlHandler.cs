using Amqp;
using BuildingOS.Shared.Domain;
using BuildingOS.Shared.Infrastructure.ControlRouting;
using BuildingOS.Shared.Infrastructure.DeviceControlHandler;
using BuildingOS.Shared.Module;
using Microsoft.Extensions.Logging;
using System.Text;

namespace BuildingOS.ConnectorWorker.Connectors;

/// <summary>
/// Sends device control commands via Hono AMQP Northbound (Artemis broker).
/// Resolves PointId → localId via IPointIdFactory (OxiGraph cache), then
/// publishes to /command/{tenant}/{localId}.
/// Scenario B counterpart to AmqpIngressWorker (which receives telemetry).
/// The AMQP connection target (host/port/tenant/credentials) comes from the resolved
/// <see cref="GatewayConnection"/> (#154 Phase 2) — the handler itself reads no env, so two Hono
/// gateways on different hosts route correctly.
/// </summary>
public class HonoDeviceControlHandler(
    IPointIdFactory pointIdFactory,
    ILogger<HonoDeviceControlHandler> logger) : IDeviceControlHandler
{
    public string BindingType => BindingTypes.Hono;

    public async Task<PointControlInfo> ExecuteControlAsync(
        PointControlInfo info, GatewayConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(info.PointId))
            {
                info.Result = PointControlResult.Failed;
                info.Response = "PointId is missing";
                logger.LogWarning("Hono control: PointId is missing for control id={ControlId}", info.id);
                return info;
            }

            var host = connection.Get("host");
            if (string.IsNullOrEmpty(host))
            {
                info.Result = PointControlResult.Failed;
                info.Response = "Hono connection host is not configured";
                logger.LogWarning(
                    "Hono control: host not configured for gateway={GatewayId}", connection.GatewayId);
                return info;
            }

            var (found, localId) = await pointIdFactory.TryGetLocalIdAsync(info.PointId).ConfigureAwait(false);
            if (!found)
            {
                info.Result = PointControlResult.Failed;
                info.Response = $"localId not found for pointId={info.PointId}";
                logger.LogWarning("Hono control: localId not found for pointId={PointId}", info.PointId);
                return info;
            }

            var tenant = connection.Get("tenant", "building-os");
            var commandAddress = $"/command/{tenant}/{localId}";
            logger.LogInformation(
                "Executing Hono control: pointId={PointId} localId={LocalId} addr={Addr} host={Host}",
                info.PointId, localId, commandAddress, host);

            await SendCommandAsync(connection, host, commandAddress, info.Body).ConfigureAwait(false);

            info.Result = PointControlResult.Success;
            info.Response = "ok";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Hono control failed: pointId={PointId}", info.PointId);
            info.Result = PointControlResult.Failed;
            info.Response = ex.Message;
        }
        return info;
    }

    private static async Task SendCommandAsync(
        GatewayConnection connection, string host, string commandAddress, string payload)
    {
        var port = int.TryParse(connection.Get("port"), out var p) ? p : 5672;
        var user = connection.Get("user");
        var password = connection.Get("password");
        var useTls = string.Equals(connection.Get("tls"), "true", StringComparison.OrdinalIgnoreCase);

        // AMQPNetLite Address(host, port, user, pass) defaults to "amqps" scheme.
        // Use the 6-arg overload to explicitly control TLS.
        var scheme = useTls ? "amqps" : "amqp";
        var address = string.IsNullOrEmpty(user)
            ? new Address($"{scheme}://{host}:{port}")
            : new Address(host, port, user, password, "/", scheme);

        var amqpConnection = await Connection.Factory.CreateAsync(address).ConfigureAwait(false);
        Session? session = null;
        SenderLink? sender = null;
        try
        {
            session = new Session(amqpConnection);
            sender = new SenderLink(session, "hono-control-sender", commandAddress);
            var message = new Message(Encoding.UTF8.GetBytes(payload));
            await sender.SendAsync(message).ConfigureAwait(false);
        }
        finally
        {
            if (sender is not null) await sender.CloseAsync().ConfigureAwait(false);
            if (session is not null) await session.CloseAsync().ConfigureAwait(false);
            await amqpConnection.CloseAsync().ConfigureAwait(false);
        }
    }
}
