using System.Reflection;
using System.Runtime.ExceptionServices;
using BuildingOS.ConnectorWorker.Connectors;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared;
using BuildingOS.Shared.Infrastructure.Messaging;
using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Moq;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

public class NatsStreamInitializationRaceTest : IClassFixture<NatsFixture>
{
    private readonly NatsFixture _fixture;
    public NatsStreamInitializationRaceTest(NatsFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task FirstPublish_Succeeds_WhenBothLookupsReturnNotFound(bool writerCreatesFirst)
        => VerifyFirstPublishAsync(writerCreatesFirst);

    [Fact]
    public async Task FirstPublish_Succeeds_DuringTwentyNaturalConcurrentStarts()
    {
        for (var attempt = 0; attempt < 20; attempt++)
            await VerifyFirstPublishAsync(null);
    }

    private async Task VerifyFirstPublishAsync(bool? writerCreatesFirst)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var (connection, js) = await _fixture.CreateJetStreamAsync();
        await using var lease = connection;
        var gate = new RaceGate();
        var writerJs = writerCreatesFirst.HasValue ? RacingContext.Wrap(js, gate, writerCreatesFirst.Value) : js;
        var busJs = writerCreatesFirst.HasValue ? RacingContext.Wrap(js, gate, !writerCreatesFirst.Value) : js;
        var lake = new CapturingLake();
        var logger = new Mock<ILogger<ParquetLakeWriterWorker>>();
        using var worker = new ParquetLakeWriterWorker(writerJs, lake,
            logger.Object,
            new ParquetLakeWriterOptions { FlushMaxRows = 1, FlushInterval = TimeSpan.FromSeconds(1), StreamMaxBytes = 1_000_000 });
        var hot = new Mock<IHotTelemetryStore>();
        var bus = new NatsIngressTelemetryBus(busJs, hot.Object, NullLogger<NatsIngressTelemetryBus>.Instance);
        const string message = """{"telemetries":[{"point_id":"p1","building":"b1","device_id":"d1","name":"temp","value":21.5,"datetime":"2026-10-27T00:00:00Z","data":{"clock":"test"}}]}""";
        await worker.StartAsync(ct);
        try
        {
            // No caller retry: this first call must receive a successful publish ACK.
            await bus.PublishAsync("building-os.validated.telemetry", message, ct);
            var batch = await lake.Written.Task.WaitAsync(ct);
            Assert.Equal("{\"clock\":\"test\"}", Assert.Single(batch.Rows).Data);
            hot.Verify(h => h.PutAsync("p1", It.Is<ValidTelemetryData>(r => r.Data == "{\"clock\":\"test\"}"), ct), Times.Once);
            var stream = await js.GetStreamAsync("BUILDING_OS_VALIDATED", cancellationToken: ct);
            logger.Verify(l => l.Log(LogLevel.Warning, It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Never);
            Assert.Equal(TimeSpan.FromHours(24), stream.Info.Config.MaxAge);
            Assert.Equal(1_000_000, stream.Info.Config.MaxBytes);
            if (writerCreatesFirst == true) Assert.Equal(0, gate.UpdateCalls);
            // Flush signals before the worker ACK; wait for that asynchronous state transition.
            ConsumerInfo consumer;
            do
            {
                consumer = (await js.GetConsumerAsync("BUILDING_OS_VALIDATED", "parquetlakewriter", ct)).Info;
                if (consumer.NumPending == 0 && consumer.NumAckPending == 0) break;
                await Task.Delay(20, ct);
            } while (true);
            Assert.Equal(0UL, consumer.NumPending);
            Assert.Equal(0, consumer.NumAckPending);
        }
        finally
        {
            await worker.StopAsync(ct);
            await js.DeleteStreamAsync("BUILDING_OS_VALIDATED", ct);
        }
    }

    [Fact]
    public async Task ExistingStream_KeepsStricterLimitsAndOtherSettings()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var (connection, js) = await _fixture.CreateJetStreamAsync();
        await using var lease = connection;
        var (name, subjects) = NatsStreamTopology.Resolve("building-os.validated.telemetry");
        await js.CreateStreamAsync(new StreamConfig(name, subjects)
        {
            MaxAge = TimeSpan.FromHours(1), MaxBytes = 500_000, MaxMsgs = 1000,
            Metadata = new Dictionary<string, string> { ["owner"] = "test" },
        }, ct);
        var lake = new CapturingLake();
        using var worker = new ParquetLakeWriterWorker(js, lake,
            NullLogger<ParquetLakeWriterWorker>.Instance,
            new ParquetLakeWriterOptions { FlushMaxRows = 1, FlushInterval = TimeSpan.FromSeconds(1) });
        await worker.StartAsync(ct);
        try
        {
            var bus = new NatsIngressTelemetryBus(js, Mock.Of<IHotTelemetryStore>(), NullLogger<NatsIngressTelemetryBus>.Instance);
            await bus.PublishAsync("building-os.validated.telemetry",
                """{"telemetries":[{"point_id":"p1","building":"b1","value":1,"datetime":"2026-10-27T00:00:00Z"}]}""", ct);
            await lake.Written.Task.WaitAsync(ct);
            var config = (await js.GetStreamAsync(name, cancellationToken: ct)).Info.Config;
            Assert.Equal(TimeSpan.FromHours(1), config.MaxAge);
            Assert.Equal(500_000, config.MaxBytes);
            Assert.Equal(1000, config.MaxMsgs);
            Assert.Equal("test", config.Metadata!["owner"]);
        }
        finally
        {
            await worker.StopAsync(ct);
            await js.DeleteStreamAsync(name, ct);
        }
    }

    [Fact]
    public async Task RealServer_RejectsIncompatibleStorage_AndOversizedPublish()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var (connection, js) = await _fixture.CreateJetStreamAsync();
        await using var lease = connection;
        var (name, subjects) = NatsStreamTopology.Resolve("building-os.validated.telemetry");
        try
        {
            await js.CreateStreamAsync(new StreamConfig(name, subjects) { Storage = StreamConfigStorage.Memory }, ct);
            var hot = new Mock<IHotTelemetryStore>(MockBehavior.Strict);
            var bus = new NatsIngressTelemetryBus(js, hot.Object, NullLogger<NatsIngressTelemetryBus>.Instance);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                bus.PublishAsync("building-os.validated.telemetry", """{"telemetries":[]}""", ct));
            hot.VerifyNoOtherCalls();
            await js.DeleteStreamAsync(name, ct);

            await js.CreateStreamAsync(new StreamConfig(name, subjects) { MaxMsgSize = 10 }, ct);
            // Same bus: a failed initialization must not be cached. Real server quota errors must
            // still throw and must not write Hot or be counted as an accepted publish.
            await Assert.ThrowsAsync<NatsJSApiException>(() =>
                bus.PublishAsync("building-os.validated.telemetry", """{"telemetries":[]}""", ct));
            hot.VerifyNoOtherCalls();
        }
        finally { await js.DeleteStreamAsync(name, ct); }
    }

    private sealed class CapturingLake : IParquetLakeWriter
    {
        public TaskCompletionSource<PartitionBatch> Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<(string Key, long Bytes)> WriteAsync(PartitionBatch batch, CancellationToken ct = default)
        {
            Written.TrySetResult(batch);
            return Task.FromResult(("test.parquet", 1L));
        }
    }

    public sealed class RaceGate
    {
        public int Missing;
        public int UpdateCalls;
        public TaskCompletionSource BothMissing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Forward every call to real NATS. Only order actual 404 responses and successful creates.
    public class RacingContext : DispatchProxy
    {
        private INatsJSContext _inner = null!;
        private RaceGate _gate = null!;
        private bool _createsFirst;
        private bool _observedMissing;
        public static INatsJSContext Wrap(INatsJSContext inner, RaceGate gate, bool createsFirst)
        {
            var proxy = Create<INatsJSContext, RacingContext>();
            var state = (RacingContext)proxy;
            state._inner = inner;
            state._gate = gate;
            state._createsFirst = createsFirst;
            return proxy;
        }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(INatsJSContext.GetStreamAsync))
                return GetAsync((string)args![0]!, (StreamInfoRequest?)args[1], (CancellationToken)args[2]!);
            if (method.Name == nameof(INatsJSContext.CreateStreamAsync))
                return CreateAsync((StreamConfig)args![0]!, (CancellationToken)args[1]!);
            if (method.Name == nameof(INatsJSContext.UpdateStreamAsync))
            {
                Interlocked.Increment(ref _gate.UpdateCalls);
                // A newly configured writer stream must not need UPDATE privileges.
                if (_createsFirst)
                    return ValueTask.FromException<INatsJSStream>(new NatsJSApiException(
                        new ApiError { Code = 403, ErrCode = 0, Description = "test update denied" }));
            }
            try { return method.Invoke(_inner, args); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
        private async ValueTask<INatsJSStream> GetAsync(string name, StreamInfoRequest? request, CancellationToken ct)
        {
            try { return await _inner.GetStreamAsync(name, request, ct); }
            catch (NatsJSApiException ex) when (ex.Error.Code == 404 && !_observedMissing)
            {
                _observedMissing = true;
                if (Interlocked.Increment(ref _gate.Missing) == 2) _gate.BothMissing.TrySetResult();
                await _gate.BothMissing.Task.WaitAsync(ct);
                throw;
            }
        }
        private async ValueTask<INatsJSStream> CreateAsync(StreamConfig config, CancellationToken ct)
        {
            if (!_createsFirst) await _gate.FirstCreated.Task.WaitAsync(ct);
            var stream = await _inner.CreateStreamAsync(config, ct);
            if (_createsFirst) _gate.FirstCreated.TrySetResult();
            return stream;
        }
    }
}
