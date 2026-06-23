using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using BuildingOS.Shared.Infrastructure.Messaging;
using BuildingOS.Shared.Module;

namespace BuildingOS.ConnectorWorker.Connectors;

/// <summary>
/// Processes envelopes on NATS building-os.raw.hono published by AmqpIngressWorker.
/// Supports validated-telemetry passthrough (see IoTIngressConnectorBase) and
/// point-ID-based transformation for devices connecting via Eclipse Hono AMQP Northbound.
/// </summary>
public sealed class HonoConnectorWorker(
    IMessageSubscription subscription,
    INatsPublisher publisher,
    IPointIdFactory pointIdFactory,
    ILogger<HonoConnectorWorker> logger)
    : IoTIngressConnectorBase(subscription, publisher, pointIdFactory, "hono", logger);
