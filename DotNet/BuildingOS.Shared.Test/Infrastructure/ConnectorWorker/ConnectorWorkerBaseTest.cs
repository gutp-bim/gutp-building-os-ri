using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using BuildingOS.Shared.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuildingOS.Shared.Test.Infrastructure.ConnectorWorker;

public class ConnectorWorkerBaseTest
{
    private static (FakeNatsPublisher publisher, InProcessMessageSubscription sub, TestConnectorWorker worker)
        CreateWorker(Func<string, Task<string?>> processImpl)
    {
        var publisher = new FakeNatsPublisher();
        var sub = new InProcessMessageSubscription();
        var worker = new TestConnectorWorker(sub, publisher, "test.output", processImpl,
            NullLogger<TestConnectorWorker>.Instance);
        return (publisher, sub, worker);
    }

    [Fact]
    public async Task ExecuteAsync_DispatchesToProcessor_WhenMessageArrives()
    {
        string? received = null;
        var (_, sub, worker) = CreateWorker(async msg =>
        {
            received = msg;
            return await Task.FromResult("processed");
        });

        using var cts = new CancellationTokenSource();
        _ = worker.StartAsync(cts.Token);

        await sub.DispatchAsync("hello", cts.Token);
        await cts.CancelAsync();

        Assert.Equal("hello", received);
    }

    [Fact]
    public async Task ExecuteAsync_PublishesResult_WhenProcessorReturnsValue()
    {
        var (publisher, sub, worker) = CreateWorker(_ => Task.FromResult<string?>("result-json"));

        using var cts = new CancellationTokenSource();
        _ = worker.StartAsync(cts.Token);

        await sub.DispatchAsync("input", cts.Token);
        await cts.CancelAsync();

        Assert.Single(publisher.Published);
        Assert.Equal("test.output", publisher.Published[0].Subject);
        Assert.Equal("result-json", publisher.Published[0].Message);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsPublish_WhenProcessorReturnsNull()
    {
        var (publisher, sub, worker) = CreateWorker(_ => Task.FromResult<string?>(null));

        using var cts = new CancellationTokenSource();
        _ = worker.StartAsync(cts.Token);

        await sub.DispatchAsync("input", cts.Token);
        await cts.CancelAsync();

        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task ExecuteAsync_ContinuesProcessing_AfterHandlerException()
    {
        var callCount = 0;
        var (publisher, sub, worker) = CreateWorker(async msg =>
        {
            callCount++;
            if (callCount == 1) throw new InvalidOperationException("transient error");
            return await Task.FromResult("ok");
        });

        using var cts = new CancellationTokenSource();
        _ = worker.StartAsync(cts.Token);

        await sub.DispatchAsync("msg1", cts.Token);
        await sub.DispatchAsync("msg2", cts.Token);
        await cts.CancelAsync();

        Assert.Equal(2, callCount);
        Assert.Single(publisher.Published);
    }
}

// === Test doubles ===

internal sealed class TestConnectorWorker(
    IMessageSubscription subscription,
    INatsPublisher publisher,
    string outputSubject,
    Func<string, Task<string?>> processImpl,
    Microsoft.Extensions.Logging.ILogger<TestConnectorWorker> logger)
    : ConnectorWorkerBase(subscription, publisher, outputSubject, logger)
{
    protected override Task<string?> ProcessAsync(string rawMessage, CancellationToken cancellationToken)
        => processImpl(rawMessage);
}

internal sealed class FakeNatsPublisher : INatsPublisher
{
    public List<(string Subject, string Message)> Published { get; } = [];

    public Task PublishAsync(string subject, string message, CancellationToken cancellationToken = default)
    {
        Published.Add((subject, message));
        return Task.CompletedTask;
    }
}
