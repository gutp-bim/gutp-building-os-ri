using System.Diagnostics.Metrics;
using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using BuildingOS.Shared.Infrastructure.Messaging;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuildingOS.Shared.Test.Infrastructure;

/// <summary>
/// Verifies ConnectorWorkerBase emits building_os.connector.messages_processed with the
/// correct result tag for each processing outcome (published / skipped / error).
/// </summary>
public class ConnectorWorkerBaseMetricsTest
{
    private const string InstrumentName = "building_os.connector.messages_processed";

    /// <summary>Minimal concrete connector that returns whatever the supplied func produces.</summary>
    private sealed class TestConnector(
        IMessageSubscription subscription,
        INatsPublisher publisher,
        Func<string, string?> process)
        : ConnectorWorkerBase(subscription, publisher, "test.output.subject", NullLogger.Instance)
    {
        protected override Task<string?> ProcessAsync(string rawMessage, CancellationToken cancellationToken)
            => Task.FromResult(process(rawMessage));
    }

    /// <summary>Subscription double that captures the registered handler for manual invocation.</summary>
    private sealed class CapturingSubscription : IMessageSubscription
    {
        public Func<string, Task>? Handler { get; private set; }
        public void Register(Func<string, Task> handler) => Handler = handler;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static async Task<List<string>> CaptureResultTagsAsync(Func<string, string?> process)
    {
        var results = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == OtelSetup.MeterName && instrument.Name == InstrumentName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "result")
                    results.Add(tag.Value?.ToString() ?? string.Empty);
        });
        listener.Start();

        var subscription = new CapturingSubscription();
        var publisher = new Mock<INatsPublisher>();
        var connector = new TestConnector(subscription, publisher.Object, process);

        // Register() is the first call in ExecuteAsync (before any await). BackgroundService
        // calls ExecuteAsync synchronously until the first real suspension; since
        // CapturingSubscription.StartAsync returns Task.CompletedTask, ExecuteAsync
        // completes synchronously and Handler is guaranteed non-null after StartAsync returns.
        await connector.StartAsync(CancellationToken.None);
        Assert.NotNull(subscription.Handler);
        await subscription.Handler!("raw-message");

        return results;
    }

    [Fact]
    public async Task Published_Message_Tags_Result_Published()
    {
        var results = await CaptureResultTagsAsync(_ => "validated-json");
        Assert.Contains("published", results);
    }

    [Fact]
    public async Task Skipped_Message_Tags_Result_Skipped()
    {
        var results = await CaptureResultTagsAsync(_ => null);
        Assert.Contains("skipped", results);
    }

    [Fact]
    public async Task Failed_Message_Tags_Result_Error()
    {
        var results = await CaptureResultTagsAsync(_ => throw new InvalidOperationException("boom"));
        Assert.Contains("error", results);
    }
}
