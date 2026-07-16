using BuildingOs.ApiServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NATS.Client.Core;
using System.Threading.Channels;

namespace BuildingOS.ApiServer.Test;

public class NatsControlResultBusTest
{
    [Fact]
    public async Task PrepareAsync_WaitsForNatsPingFence()
    {
        var messages = Channel.CreateUnbounded<NatsMsg<byte[]>>();
        var subscription = new Mock<INatsSub<byte[]>>();
        subscription.SetupGet(s => s.Msgs).Returns(messages.Reader);
        subscription.Setup(s => s.UnsubscribeAsync()).Returns(ValueTask.CompletedTask);

        var pingCompleted = new TaskCompletionSource<TimeSpan>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var nats = new Mock<INatsConnection>();
        nats.Setup(n => n.SubscribeCoreAsync<byte[]>(
                It.IsAny<string>(), null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(subscription.Object);
        nats.Setup(n => n.PingAsync(It.IsAny<CancellationToken>()))
            .Returns(() => new ValueTask<TimeSpan>(pingCompleted.Task));

        await using var bus = new NatsControlResultBus(
            nats.Object,
            NullLogger<NatsControlResultBus>.Instance,
            TimeSpan.FromMinutes(1));

        var prepareTask = bus.PrepareAsync("control-1", CancellationToken.None);
        await Task.Yield();

        Assert.False(prepareTask.IsCompleted);
        pingCompleted.SetResult(TimeSpan.Zero);
        await prepareTask;
    }

    [Fact]
    public async Task PreparedResultSubscription_Expires_WhenNoGrpcWaiterSubscribes()
    {
        var messages = Channel.CreateUnbounded<NatsMsg<byte[]>>();
        var unsubscribed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = new Mock<INatsSub<byte[]>>();
        subscription.SetupGet(s => s.Msgs).Returns(messages.Reader);
        subscription.Setup(s => s.UnsubscribeAsync())
            .Callback(() => unsubscribed.TrySetResult())
            .Returns(ValueTask.CompletedTask);

        var nats = new Mock<INatsConnection>();
        nats.Setup(n => n.SubscribeCoreAsync<byte[]>(
                It.IsAny<string>(), null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(subscription.Object);
        nats.Setup(n => n.PingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TimeSpan.Zero);

        await using var bus = new NatsControlResultBus(
            nats.Object,
            NullLogger<NatsControlResultBus>.Instance,
            TimeSpan.FromMilliseconds(20));

        await bus.PrepareAsync("control-1", CancellationToken.None);
        await unsubscribed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        subscription.Verify(s => s.UnsubscribeAsync(), Times.Once);
    }
}
