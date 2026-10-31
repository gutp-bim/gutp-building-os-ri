using BuildingOS.ConnectorWorker.Connectors;
using BuildingOS.Shared.Infrastructure.Messaging;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace BuildingOS.Shared.Test.Infrastructure.Messaging;

public class NatsStreamInitializationTest
{
    private static StreamConfig Config() => new("BUILDING_OS_VALIDATED", ["building-os.validated.>"]);
    private static NatsJSApiException Error(int code, int errCode) => new(new ApiError { Code = code, ErrCode = errCode, Description = "test error" });
    private static INatsJSStream Stream(StreamConfig config)
    {
        var stream = new Mock<INatsJSStream>();
        stream.SetupGet(s => s.Info).Returns(new StreamInfo { Config = config });
        return stream.Object;
    }

    [Theory]
    [InlineData(403, 0)]
    [InlineData(500, 10047)]
    [InlineData(404, 10014)] // A different missing resource is not a missing stream.
    public async Task LookupError_IsPreservedWithoutCreating(int code, int errCode)
    {
        var js = new Mock<INatsJSContext>(MockBehavior.Strict);
        var error = Error(code, errCode);
        js.Setup(j => j.GetStreamAsync("BUILDING_OS_VALIDATED", null, CancellationToken.None))
            .ThrowsAsync(error);
        Assert.Same(error, await Assert.ThrowsAsync<NatsJSApiException>(() =>
            NatsStreamInitialization.EnsureAsync(js.Object, Config(), CancellationToken.None)));
        js.Verify(j => j.GetStreamAsync("BUILDING_OS_VALIDATED", null, CancellationToken.None), Times.Once);
        js.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(403, 0)]
    [InlineData(400, 10002)] // Account resources exceeded.
    [InlineData(400, 10052)] // Invalid stream configuration.
    public async Task CreateError_IsPreservedWithoutConflictLookup(int code, int errCode)
    {
        var js = new Mock<INatsJSContext>(MockBehavior.Strict);
        var requested = Config();
        var error = Error(code, errCode);
        js.Setup(j => j.GetStreamAsync(requested.Name!, null, CancellationToken.None)).ThrowsAsync(Error(404, 10059));
        js.Setup(j => j.CreateStreamAsync(requested, CancellationToken.None)).ThrowsAsync(error);
        Assert.Same(error, await Assert.ThrowsAsync<NatsJSApiException>(() =>
            NatsStreamInitialization.EnsureAsync(js.Object, requested, CancellationToken.None)));
        js.Verify(j => j.GetStreamAsync(requested.Name!, null, CancellationToken.None), Times.Once);
        js.Verify(j => j.CreateStreamAsync(requested, CancellationToken.None), Times.Once);
        js.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("subjects")]
    [InlineData("storage")]
    [InlineData("retention")]
    [InlineData("discard")]
    [InlineData("noAck")]
    [InlineData("sealed")]
    public async Task IncompatibleStream_IsRejected_EvenAfterCreateConflict(string mismatch)
    {
        var js = new Mock<INatsJSContext>(MockBehavior.Strict);
        var requested = Config();
        var actual = Config();
        switch (mismatch)
        {
            case "subjects": actual.Subjects = ["building-os.validated.other"]; break;
            case "storage": actual.Storage = StreamConfigStorage.Memory; break;
            case "retention": actual.Retention = StreamConfigRetention.Workqueue; break;
            case "discard": actual.Discard = StreamConfigDiscard.New; break;
            case "noAck": actual.NoAck = true; break;
            case "sealed": actual.Sealed = true; break;
        }
        js.SetupSequence(j => j.GetStreamAsync(requested.Name!, null, CancellationToken.None))
            .ThrowsAsync(Error(404, 10059)).ReturnsAsync(Stream(actual));
        js.Setup(j => j.CreateStreamAsync(requested, CancellationToken.None)).ThrowsAsync(Error(400, 10058));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NatsStreamInitialization.EnsureAsync(js.Object, requested, CancellationToken.None));
        js.Verify(j => j.CreateStreamAsync(requested, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task ConflictFollowedByMissingStream_StopsAfterOneRecheck()
    {
        var js = new Mock<INatsJSContext>(MockBehavior.Strict);
        var requested = Config();
        var missing = Error(404, 10059);
        js.Setup(j => j.GetStreamAsync(requested.Name!, null, CancellationToken.None)).ThrowsAsync(missing);
        js.Setup(j => j.CreateStreamAsync(requested, CancellationToken.None)).ThrowsAsync(Error(400, 10058));
        Assert.Same(missing, await Assert.ThrowsAsync<NatsJSApiException>(() =>
            NatsStreamInitialization.EnsureAsync(js.Object, requested, CancellationToken.None)));
        js.Verify(j => j.GetStreamAsync(requested.Name!, null, CancellationToken.None), Times.Exactly(2));
        js.Verify(j => j.CreateStreamAsync(requested, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task ConflictFollowedByDeniedLookup_PreservesThePermissionError()
    {
        var js = new Mock<INatsJSContext>(MockBehavior.Strict);
        var requested = Config();
        var denied = Error(403, 0);
        js.SetupSequence(j => j.GetStreamAsync(requested.Name!, null, CancellationToken.None))
            .ThrowsAsync(Error(404, 10059)).ThrowsAsync(denied);
        js.Setup(j => j.CreateStreamAsync(requested, CancellationToken.None)).ThrowsAsync(Error(400, 10058));
        Assert.Same(denied, await Assert.ThrowsAsync<NatsJSApiException>(() =>
            NatsStreamInitialization.EnsureAsync(js.Object, requested, CancellationToken.None)));
        js.Verify(j => j.GetStreamAsync(requested.Name!, null, CancellationToken.None), Times.Exactly(2));
        js.Verify(j => j.CreateStreamAsync(requested, CancellationToken.None), Times.Once);
        js.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Bus_DoesNotCacheFailedInitialization_OrPublishBeforeSuccess()
    {
        var js = new Mock<INatsJSContext>(MockBehavior.Strict);
        var denied = Error(403, 0);
        js.Setup(j => j.GetStreamAsync("BUILDING_OS_VALIDATED", null, CancellationToken.None)).ThrowsAsync(denied);
        var bus = new NatsIngressTelemetryBus(js.Object, Mock.Of<IHotTelemetryStore>(), NullLogger<NatsIngressTelemetryBus>.Instance);
        for (var attempt = 0; attempt < 2; attempt++)
            Assert.Same(denied, await Assert.ThrowsAsync<NatsJSApiException>(() =>
                bus.PublishAsync("building-os.validated.telemetry", "{\"telemetries\":[]}", CancellationToken.None)));
        js.Verify(j => j.GetStreamAsync("BUILDING_OS_VALIDATED", null, CancellationToken.None), Times.Exactly(2));
        js.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Cancellation_IsPreservedWithoutCreating()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var js = new Mock<INatsJSContext>(MockBehavior.Strict);
        js.Setup(j => j.GetStreamAsync("BUILDING_OS_VALIDATED", null, cts.Token)).ThrowsAsync(new OperationCanceledException(cts.Token));
        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            NatsStreamInitialization.EnsureAsync(js.Object, Config(), cts.Token));
        Assert.Equal(cts.Token, ex.CancellationToken);
        js.Verify(j => j.GetStreamAsync("BUILDING_OS_VALIDATED", null, cts.Token), Times.Once);
        js.VerifyNoOtherCalls();
    }
}
