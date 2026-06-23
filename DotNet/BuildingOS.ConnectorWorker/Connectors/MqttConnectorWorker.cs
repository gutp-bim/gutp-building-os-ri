using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using BuildingOS.Shared.Infrastructure.Messaging;
using BuildingOS.Shared.Module;

namespace BuildingOS.ConnectorWorker.Connectors;

/// <summary>
/// Processes envelopes on NATS building-os.raw.mqtt published by MqttIngressWorker.
/// Supports validated-telemetry passthrough (see IoTIngressConnectorBase) and
/// point-ID-based transformation for devices connecting via Mosquitto.
/// </summary>
public sealed class MqttConnectorWorker(
    IMessageSubscription subscription,
    INatsPublisher publisher,
    IPointIdFactory pointIdFactory,
    ILogger<MqttConnectorWorker> logger)
    : IoTIngressConnectorBase(subscription, publisher, pointIdFactory, "mqtt", logger);
