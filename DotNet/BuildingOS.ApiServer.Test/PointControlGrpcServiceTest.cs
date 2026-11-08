using System.Threading.Channels;
using BuildingOs.ApiServer.Protos;
using BuildingOs.ApiServer.Services;
using BuildingOS.Shared.Domain.ControlFeedback;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.ApiServer.Test;

/// <summary>#637: WaitForResult sends the gateway's answer and, after a success, the confirmation.</summary>
public class PointControlGrpcServiceTest
{
    private sealed class Bus(Channel<ControlResultEvent> channel) : IControlResultBus
    {
        public Task PrepareAsync(string controlId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ChannelReader<ControlResultEvent>> SubscribeAsync(string controlId, CancellationToken cancellationToken) =>
            Task.FromResult(channel.Reader);
        public bool Publish(string controlId, ControlResultEvent evt) => channel.Writer.TryWrite(evt);
        public Task UnsubscribeAsync(string controlId) => Task.CompletedTask;
    }

    private sealed class CallContext(CancellationToken ct) : ServerCallContext
    {
        protected override string MethodCore => "WaitForResult";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "test";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => [];
        protected override CancellationToken CancellationTokenCore => ct;
        protected override Metadata ResponseTrailersCore => [];
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(null, new Dictionary<string, List<AuthProperty>>());
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }

    private sealed class Writer : IServerStreamWriter<ControlResultEvent>
    {
        public List<ControlResultEvent> Events { get; } = [];
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(ControlResultEvent message) { Events.Add(message); return Task.CompletedTask; }
    }

    private static PointControlGrpcService Service(Channel<ControlResultEvent> c) =>
        new(new Bus(c), NullLogger<PointControlGrpcService>.Instance);

    [Fact]
    public async Task AfterASuccess_TheConfirmationIsSentToo()
    {
        var c = Channel.CreateUnbounded<ControlResultEvent>();
        c.Writer.TryWrite(new ControlResultEvent { ControlId = "c1", Result = ControlResult.Success });
        c.Writer.TryWrite(new ControlResultEvent { ControlId = "c1", Result = ControlResult.FeedbackMismatch, FeedbackValue = "22" });
        var writer = new Writer();

        await Service(c).WaitForResult(new WaitForResultRequest { ControlId = "c1" }, writer, new CallContext(default));

        Assert.Equal([ControlResult.Success, ControlResult.FeedbackMismatch], writer.Events.Select(e => e.Result));
        Assert.Equal("22", writer.Events[1].FeedbackValue);
    }

    [Fact]
    public async Task AFailure_IsFinal()
    {
        var c = Channel.CreateUnbounded<ControlResultEvent>();
        c.Writer.TryWrite(new ControlResultEvent { ControlId = "c1", Result = ControlResult.Failed });
        var writer = new Writer();

        await Service(c).WaitForResult(new WaitForResultRequest { ControlId = "c1" }, writer, new CallContext(default));

        Assert.Equal([ControlResult.Failed], writer.Events.Select(e => e.Result));
    }

    [Fact]
    public async Task ASuccessWithoutAConfirmation_EndsWhenTheBusCloses()
    {
        // A replica that predates #637 handled the POST, or the bus expired: the stream still ends after the success.
        var c = Channel.CreateUnbounded<ControlResultEvent>();
        c.Writer.TryWrite(new ControlResultEvent { ControlId = "c1", Result = ControlResult.Success });
        c.Writer.Complete();
        var writer = new Writer();

        await Service(c).WaitForResult(new WaitForResultRequest { ControlId = "c1" }, writer, new CallContext(default))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([ControlResult.Success], writer.Events.Select(e => e.Result));
    }

    [Theory]
    [InlineData("number", 24.5, null, null, 24.5)]
    [InlineData("boolean", null, null, true, 1d)]
    [InlineData("string", null, "3", null, 3d)]
    [InlineData("string", null, "on", null, null)]
    public void FeedbackReadings_FollowTheValueType(string type, double? value, string? text, bool? flag, double? expected)
    {
        var r = ControlFeedbackCoordinator.ToReading(
            new LatestValue("P", value, text, flag, type, "2026-11-08T00:00:00Z", DateTimeOffset.UtcNow));
        Assert.Equal(expected, r.Value);
        Assert.Equal(new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero), r.At);
    }
}
