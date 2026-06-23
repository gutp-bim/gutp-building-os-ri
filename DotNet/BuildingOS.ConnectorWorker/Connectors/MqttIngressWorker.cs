using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using BuildingOS.Shared.Infrastructure.Messaging;
using BuildingOS.Shared.Infrastructure.Telemetry;
using MQTTnet;
using MQTTnet.Client;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using System.Text;
using System.Text.Json;

namespace BuildingOS.ConnectorWorker.Connectors;

/// <summary>
/// Subscribes to a Mosquitto MQTT broker and forwards messages to NATS building-os.raw.mqtt.
/// Transport layer for Scenario A: IoT devices → Mosquitto → MqttIngressWorker → MqttConnectorWorker.
/// </summary>
public sealed class MqttIngressWorker(
    INatsJSContext js,
    INatsPublisher publisher,
    string mqttHost,
    int mqttPort,
    string? mqttUsername,
    string? mqttPassword,
    string topicFilter,
    ILogger<MqttIngressWorker> logger) : BackgroundService
{
    private const string RawMqttSubject = "building-os.raw.mqtt";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EnsureStreamExistsAsync(stoppingToken);

        var factory = new MqttFactory();
        using var mqttClient = factory.CreateMqttClient();

        mqttClient.ApplicationMessageReceivedAsync += async e =>
        {
            try
            {
                await HandleMessageAsync(e.ApplicationMessage, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MQTT message handling failed for topic {Topic}", e.ApplicationMessage.Topic);
            }
        };

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(mqttHost, mqttPort)
            .WithCleanSession();

        if (!string.IsNullOrEmpty(mqttUsername))
            optionsBuilder = optionsBuilder.WithCredentials(mqttUsername, mqttPassword);

        var options = optionsBuilder.Build();
        var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(topicFilter)
            .Build();

        // Log before the first connect attempt so connection failures are visible even
        // when the broker is unreachable (mirrors AmqpIngressWorker for diagnosability).
        logger.LogInformation(
            "MqttIngressWorker starting: connecting to {Host}:{Port} filter={Filter}", mqttHost, mqttPort, topicFilter);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await mqttClient.ConnectAsync(options, stoppingToken);
                if (result.ResultCode != MqttClientConnectResultCode.Success)
                {
                    logger.LogWarning("MQTT connect failed: {Code}, retrying in 5s", result.ResultCode);
                    await Task.Delay(5000, stoppingToken);
                    continue;
                }

                await mqttClient.SubscribeAsync(subscribeOptions, stoppingToken);
                logger.LogInformation("MqttIngressWorker connected to {Host}:{Port}, filter={Filter}", mqttHost, mqttPort, topicFilter);

                while (!stoppingToken.IsCancellationRequested && mqttClient.IsConnected)
                    await Task.Delay(1000, stoppingToken);

                if (!stoppingToken.IsCancellationRequested)
                    logger.LogWarning("MQTT connection dropped, reconnecting in 5s");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MQTT session error, reconnecting in 5s");
            }

            if (mqttClient.IsConnected)
            {
                try { await mqttClient.DisconnectAsync(); }
                catch { }
            }

            try { await Task.Delay(5000, stoppingToken); }
            catch { break; }
        }
    }

    private async Task HandleMessageAsync(MqttApplicationMessage msg, CancellationToken ct)
    {
        var topic = msg.Topic;
        var parts = topic.Split('/', 3);
        var tenant = parts.Length > 1 ? parts[1] : string.Empty;
        var deviceId = parts.Length > 2 ? parts[2] : string.Empty;

        // Require telemetry/{tenant}/{deviceId} — skip ambiguous topics like "telemetry/{tenant}"
        if (string.IsNullOrEmpty(tenant) || string.IsNullOrEmpty(deviceId))
        {
            logger.LogWarning("MqttIngressWorker: topic {Topic} missing tenant or deviceId, skipping", topic);
            return;
        }

        var payloadText = Encoding.UTF8.GetString(msg.PayloadSegment.ToArray());

        if (!TryParseJson(payloadText, out var payloadElement))
        {
            logger.LogWarning("MqttIngressWorker: non-JSON payload on topic {Topic}, skipping", topic);
            return;
        }

        var envelope = JsonSerializer.Serialize(
            new IngressEnvelope(topic, tenant, deviceId, payloadElement, DateTimeOffset.UtcNow));

        await publisher.PublishAsync(RawMqttSubject, envelope, ct);
        BuildingOsMetrics.IngressMessages.Add(1, new KeyValuePair<string, object?>("source", "mqtt"));
        logger.LogDebug("MQTT→NATS: {Topic} → {Subject}", topic, RawMqttSubject);
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

    private async Task EnsureStreamExistsAsync(CancellationToken ct)
    {
        var (streamName, streamSubjects) = NatsStreamTopology.Resolve(RawMqttSubject);
        try
        {
            await js.GetStreamAsync(streamName, cancellationToken: ct);
        }
        catch
        {
            await js.CreateStreamAsync(new StreamConfig(streamName, streamSubjects), ct);
        }
    }
}
