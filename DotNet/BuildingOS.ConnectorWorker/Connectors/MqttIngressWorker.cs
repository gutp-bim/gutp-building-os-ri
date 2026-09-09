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
        var payloadText = Encoding.UTF8.GetString(msg.PayloadSegment.ToArray());

        // Requires telemetry/{tenant}/{deviceId} and a JSON payload; anything else is refused.
        // #415: every outcome is counted, so a refusal is a visible drop rather than only a log line.
        var classified = MqttIngressMessageClassifier.Classify(topic, payloadText);
        if (!classified.IsPublishable)
        {
            Count(classified.Result);
            logger.LogWarning(
                "MqttIngressWorker: skipping message on topic {Topic}, reason {Reason}", topic, classified.Result);
            return;
        }

        var envelope = JsonSerializer.Serialize(new IngressEnvelope(
            topic, classified.Tenant, classified.DeviceId, classified.Payload, DateTimeOffset.UtcNow));

        await publisher.PublishAsync(RawMqttSubject, envelope, ct);
        Count(classified.Result);
        logger.LogDebug("MQTT→NATS: {Topic} → {Subject}", topic, RawMqttSubject);
    }

    private static void Count(string result) =>
        BuildingOsMetrics.IngressMessages.Add(
            1,
            new KeyValuePair<string, object?>("source", "mqtt"),
            new KeyValuePair<string, object?>("result", result));

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
