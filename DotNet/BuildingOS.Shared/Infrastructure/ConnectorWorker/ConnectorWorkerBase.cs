using System.Diagnostics;
using BuildingOS.Shared.Infrastructure.Messaging;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Infrastructure.ConnectorWorker;

/// <summary>
/// Base BackgroundService for OSS connector workers.
/// Subscribes to inbound NATS subject, processes each message, and publishes
/// validated JSON to the output subject. Replaces Azure Functions EventHubTrigger.
/// </summary>
public abstract class ConnectorWorkerBase(
    IMessageSubscription subscription,
    INatsPublisher publisher,
    string outputSubject,
    ILogger logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        subscription.Register(async rawMessage =>
        {
            var connector = GetType().Name;
            var stopwatch = Stopwatch.StartNew();
            var result = "error";
            try
            {
                var processed = await ProcessAsync(rawMessage, stoppingToken);
                if (processed != null)
                {
                    await publisher.PublishAsync(outputSubject, processed, stoppingToken);
                    result = "published";
                }
                else
                {
                    result = "skipped";
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Connector {Name} failed to process message", connector);
            }
            finally
            {
                stopwatch.Stop();
                BuildingOsMetrics.ConnectorMessagesProcessed.Add(1,
                    new KeyValuePair<string, object?>("connector", connector),
                    new KeyValuePair<string, object?>("result", result));
                BuildingOsMetrics.ConnectorProcessDuration.Record(stopwatch.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("connector", connector));
            }
        });

        await subscription.StartAsync(stoppingToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses and validates the raw inbound message.
    /// Returns a validated JSON string to publish, or null to skip publishing.
    /// </summary>
    protected abstract Task<string?> ProcessAsync(string rawMessage, CancellationToken cancellationToken);
}
