using BuildingOS.Shared.Domain;
using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using BuildingOS.Shared.Infrastructure.ControlRouting;
using BuildingOS.Shared.Infrastructure.DeviceControlHandler;
using BuildingOS.Shared.Infrastructure.Messaging;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace BuildingOS.ConnectorWorker.Connectors;

/// <summary>
/// OSS replacement for the Azure Functions PointControlConnector (CosmosDBTrigger).
/// Subscribes to building-os.control.request via IMessageSubscription, resolves the target gateway's
/// connection from <see cref="IGatewayConnectionRegistry"/> (authoritative server-side binding — the
/// client-supplied <c>Type</c> is not trusted for dispatch), selects the matching
/// <see cref="IDeviceControlHandler"/> by binding type, and publishes the result to
/// building-os.control.result.{controlId} via INatsPublisher.
/// </summary>
public sealed class NatsPointControlWorker(
    IMessageSubscription subscription,
    IEnumerable<IDeviceControlHandler> handlers,
    IGatewayConnectionRegistry connections,
    INatsPublisher publisher,
    ILogger<NatsPointControlWorker> logger) : BackgroundService
{
    private const string ResultSubjectPrefix = "building-os.control.result.";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("NatsPointControlWorker started");

        subscription.Register(async rawMessage =>
        {
            PointControlInfo? info = null;
            try
            {
                info = JsonSerializer.Deserialize<PointControlInfo>(rawMessage);
                if (info == null) return;

                var result = await ProcessAsync(info, stoppingToken).ConfigureAwait(false);
                await publisher.PublishAsync(ResultSubjectPrefix + info.id, result, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process control request {ControlId}", info?.id);

                if (info != null)
                {
                    var errResult = JsonSerializer.Serialize(new { success = false, response = ex.Message });
                    await publisher.PublishAsync(ResultSubjectPrefix + info.id, errResult, stoppingToken)
                        .ConfigureAwait(false);
                }
            }
        });

        await subscription.StartAsync(stoppingToken).ConfigureAwait(false);
    }

    private async Task<string> ProcessAsync(PointControlInfo info, CancellationToken cancellationToken)
    {
        // Re-resolve the gateway's connection server-side from its id (config-authoritative), rather
        // than trusting the wire-supplied Type. This both carries the per-gateway connection settings
        // and lets two same-binding gateways route to different hosts.
        var connection = connections.Resolve(info.GatewayId);
        if (connection == null)
            return Unsupported(info, $"No gateway connection resolved for gateway={info.GatewayId}", "unresolved");

        var handler = handlers.FirstOrDefault(h => h.BindingType == connection.BindingType);
        if (handler == null)
            return Unsupported(info, $"Unsupported binding type: {connection.BindingType}", connection.BindingType);

        logger.LogInformation("Executing control {ControlId} binding={Binding} gateway={GatewayId}",
            info.id, connection.BindingType, info.GatewayId);
        var result = await handler.ExecuteControlAsync(info, connection, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Control {ControlId} completed: {Result}", info.id, result.Result);

        var success = result.Result == PointControlResult.Success;
        BuildingOsMetrics.ControlRequests.Add(1,
            new KeyValuePair<string, object?>("handler", connection.BindingType),
            new KeyValuePair<string, object?>("result", success ? "ok" : "failed"));

        return JsonSerializer.Serialize(new
        {
            success,
            response = result.Response ?? string.Empty,
        });
    }

    private string Unsupported(PointControlInfo info, string message, string bindingTag)
    {
        logger.LogWarning("Control {ControlId} not dispatched: {Message}", info.id, message);
        BuildingOsMetrics.ControlRequests.Add(1,
            new KeyValuePair<string, object?>("handler", bindingTag),
            new KeyValuePair<string, object?>("result", "unsupported"));
        return JsonSerializer.Serialize(new { success = false, response = message });
    }
}
