using Amqp;
using Amqp.Framing;
using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using BuildingOS.Shared.Infrastructure.Messaging;
using BuildingOS.Shared.Infrastructure.Telemetry;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using System.Text;
using System.Text.Json;

namespace BuildingOS.ConnectorWorker.Connectors;

/// <summary>
/// Subscribes to Eclipse Hono AMQP 1.0 Northbound and forwards messages to NATS building-os.raw.hono.
/// Transport layer for Scenario B: IoT devices → Hono → AmqpIngressWorker → HonoConnectorWorker.
///
/// Connects to /telemetry/{tenant} and /events/{tenant} AMQP addresses.
/// Envelope format published to NATS is compatible with MqttIngressWorker.
/// </summary>
public sealed class AmqpIngressWorker(
    INatsJSContext js,
    INatsPublisher publisher,
    string amqpHost,
    int amqpPort,
    string tenant,
    string? amqpUser,
    string? amqpPassword,
    bool useTls,
    ILogger<AmqpIngressWorker> logger) : BackgroundService
{
    private const string RawHonoSubject = "building-os.raw.hono";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EnsureStreamExistsAsync(stoppingToken);

        // Log before the first connect attempt so "registered but cannot reach the broker"
        // is distinguishable from "never registered" (issue #131). The post-connect log at
        // line below only fires on success; this one fires regardless.
        logger.LogInformation(
            "AmqpIngressWorker starting: connecting to {Scheme}://{Host}:{Port} tenant={Tenant}",
            useTls ? "amqps" : "amqp", amqpHost, amqpPort, tenant);

        while (!stoppingToken.IsCancellationRequested)
        {
            Connection? connection = null;
            try
            {
                var address = BuildAddress();
                connection = await Connection.Factory.CreateAsync(address);
                var session = new Session(connection);

                var telemetryAddress = $"/telemetry/{tenant}";
                var eventsAddress = $"/events/{tenant}";
                var telemetryReceiver = CreateReceiver(session, telemetryAddress, "telemetry");
                var eventsReceiver = CreateReceiver(session, eventsAddress, "events");

                logger.LogInformation("AmqpIngressWorker connected to {Host}:{Port}, tenant={Tenant}", amqpHost, amqpPort, tenant);

                telemetryReceiver.Start(50, (link, message) => HandleMessage(link, message, telemetryAddress, stoppingToken));
                eventsReceiver.Start(10, (link, message) => HandleMessage(link, message, eventsAddress, stoppingToken));

                // Exit when the connection is closed remotely (without an exception being thrown).
                var connectionClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                connection.Closed += (_, _) => connectionClosed.TrySetResult();
                await Task.WhenAny(Task.Delay(Timeout.Infinite, stoppingToken), connectionClosed.Task);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "AmqpIngressWorker connection error, reconnecting in 5s");
            }
            finally
            {
                try { connection?.Close(); } catch { }
            }

            try { await Task.Delay(5000, stoppingToken); } catch { break; }
        }
    }

    private void HandleMessage(IReceiverLink link, Message message, string topicAddress, CancellationToken ct)
        => _ = HandleMessageAsync(link, message, topicAddress, ct);

    private async Task HandleMessageAsync(IReceiverLink link, Message message, string topicAddress, CancellationToken ct)
    {
        try
        {
            var deviceId = ExtractDeviceId(message);
            var payloadText = ExtractPayload(message);

            if (payloadText == null || !TryParseJson(payloadText, out var payloadElement))
            {
                logger.LogWarning("AmqpIngressWorker: non-JSON payload from device={DeviceId}, skipping", deviceId);
                link.Accept(message);
                return;
            }

            var messageId = message.Properties?.MessageId?.ToString();
            var envelope = JsonSerializer.Serialize(
                new IngressEnvelope(topicAddress, tenant, deviceId, payloadElement, DateTimeOffset.UtcNow, messageId));

            try
            {
                await publisher.PublishAsync(RawHonoSubject, envelope, ct);
                BuildingOsMetrics.IngressMessages.Add(1, new KeyValuePair<string, object?>("source", "amqp"));
                link.Accept(message);
                logger.LogDebug("AMQP→NATS: device={DeviceId} → {Subject}", deviceId, RawHonoSubject);
            }
            catch (Exception ex)
            {
                // Release (not Reject) so the broker can redeliver on transient NATS failures
                logger.LogWarning(ex, "AmqpIngressWorker: NATS publish failed for device={DeviceId}, releasing for redelivery", deviceId);
                try { link.Release(message); } catch { }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AmqpIngressWorker: message handling failed");
            try { link.Reject(message); } catch { }
        }
    }

    private static string ExtractDeviceId(Message message)
    {
        if (message.ApplicationProperties?["device_id"] is string deviceId)
            return deviceId;

        // Hono encodes device ID in orig_address: telemetry/{tenant}/{deviceId}
        if (message.ApplicationProperties?["orig_address"] is string origAddress)
        {
            var parts = origAddress.Split('/', 3);
            if (parts.Length == 3) return parts[2];
        }

        return message.Properties?.To ?? string.Empty;
    }

    private static string? ExtractPayload(Message message)
    {
        if (message.Body is byte[] bytes) return Encoding.UTF8.GetString(bytes);
        if (message.Body is string s) return s;
        // AMQPNetLite AmqpValue section
        if (message.Body is Amqp.Framing.AmqpValue amqpValue)
        {
            if (amqpValue.Value is byte[] valueBytes) return Encoding.UTF8.GetString(valueBytes);
            if (amqpValue.Value is string valueStr) return valueStr;
        }
        return null;
    }

    private static bool TryParseJson(string text, out JsonElement element)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            element = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }

    private static ReceiverLink CreateReceiver(Session session, string address, string name)
        => new(session, $"amqp-ingress-{name}", new Source { Address = address }, null);

    private Address BuildAddress()
    {
        // AMQPNetLite's Address ctor defaults scheme to "amqps", which forces a TLS
        // handshake even on plaintext brokers (e.g. Artemis on 5672) and fails with
        // "protocol CORE not found in map: [AMQP]". Select the scheme explicitly so
        // plaintext (amqp) and TLS (amqps) are both supported (issue #131).
        var scheme = useTls ? "amqps" : "amqp";
        return new Address(amqpHost, amqpPort, amqpUser, amqpPassword, "/", scheme);
    }

    private async Task EnsureStreamExistsAsync(CancellationToken ct)
    {
        var (streamName, streamSubjects) = NatsStreamTopology.Resolve(RawHonoSubject);
        try { await js.GetStreamAsync(streamName, cancellationToken: ct); }
        catch { await js.CreateStreamAsync(new StreamConfig(streamName, streamSubjects), ct); }
    }
}
