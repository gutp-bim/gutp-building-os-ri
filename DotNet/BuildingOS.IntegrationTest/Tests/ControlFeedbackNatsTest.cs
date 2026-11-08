using System.Text.Json;
using BuildingOs.ApiServer.Protos;
using BuildingOs.ApiServer.Services;
using BuildingOS.IntegrationTest.Collections;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.ControlFeedback;
using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NATS.Client.Core;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>
/// #637 against a real NATS: a control's confirmation travels from the coordinator (gateway SUCCESS + feedback point
/// value) through <c>building-os.control.feedback.{id}</c> to the WaitForResult bus, and into the audit.
/// </summary>
[Collection(Names.Nats)]
public class ControlFeedbackNatsTest(NatsFixture fixture) : IntegrationTestBase
{
    private sealed class FakeAudit : IControlAuditWriter
    {
        public List<(string Status, string? Value)> Writes { get; } = [];
        public Task RecordRequestAsync(PointControlInfo info, BuildingOS.Shared.Domain.PointControl.ControlActor actor, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordResultAsync(string controlId, bool success, string? response, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordFailureIfPendingAsync(string controlId, string? response, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordFeedbackAsync(string controlId, string status, string? value, DateTime? completedAt, CancellationToken ct = default)
        {
            lock (Writes) Writes.Add((status, value));
            return Task.CompletedTask;
        }
    }

    private static IControlFeedbackConfigResolver Config(string? feedbackPoint, TimeSpan window)
    {
        var m = new Mock<IControlFeedbackConfigResolver>();
        m.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ControlFeedbackConfig(feedbackPoint, window, 0));
        return m.Object;
    }

    private static Point Sp(string id) => new() { DtId = "urn:" + id, Id = id, Name = id, Writable = true };

    private static LatestValue V(string id, double value, DateTimeOffset at) =>
        new(id, value, null, null, "number", at.ToString("O"), at);

    private async Task<(NatsConnection Nats, NatsControlResultBus Bus)> Connect()
    {
        var nats = fixture.CreateConnection();
        await nats.ConnectAsync();
        return (nats, new NatsControlResultBus(nats, NullLogger<NatsControlResultBus>.Instance));
    }

    private static Task GatewayAnswers(NatsConnection nats, string controlId, bool success) =>
        nats.PublishAsync($"building-os.control.result.{controlId}",
            JsonSerializer.SerializeToUtf8Bytes(new { success, response = "ok" })).AsTask();

    private static async Task<List<ControlResultEvent>> ReadAll(NatsControlResultBus bus, string controlId, int count)
    {
        var reader = await bus.SubscribeAsync(controlId, CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var got = new List<ControlResultEvent>();
        while (got.Count < count) got.Add(await reader.ReadAsync(cts.Token));
        return got;
    }

    [Fact]
    public async Task AFeedbackValueEqualToTheRequest_IsMatched_AndReachesTheWaiter()
    {
        var (nats, bus) = await Connect();
        await using var _ = nats;
        await using var __ = bus;
        var feed = new LatestValueFeed();
        var audit = new FakeAudit();
        var coordinator = new ControlFeedbackCoordinator(
            nats, feed, Config("PV-1", TimeSpan.FromSeconds(10)), audit, NullLogger<ControlFeedbackCoordinator>.Instance);
        var controlId = Guid.NewGuid().ToString();
        var sentAt = DateTimeOffset.UtcNow;

        await bus.PrepareAsync(controlId, CancellationToken.None);
        await coordinator.BeginAsync(
            new ControlFeedbackRequest(controlId, Sp("SP-1"), new ControlSchema { DataType = "number" }, 24, sentAt), default);
        var waiting = ReadAll(bus, controlId, 2);

        await GatewayAnswers(nats, controlId, success: true);
        await Task.Delay(200);
        feed.Publish(V("PV-1", 24, sentAt.AddSeconds(1)));

        var events = await waiting;
        Assert.Equal(ControlResult.Success, events[0].Result);
        Assert.Equal(ControlResult.FeedbackMatched, events[1].Result);
        Assert.Equal("24", events[1].FeedbackValue);
        await WaitUntil(() => audit.Writes.Count == 2);
        Assert.Equal([(ControlFeedbackStatuses.Pending, null), (ControlFeedbackStatuses.Matched, "24")], audit.Writes);
    }

    [Fact]
    public async Task AMatchBeforeTheGatewayAnswers_IsKept_EvenIfAnotherValueFollows()
    {
        // The feed keeps only the newest unread value per point; correlation must already be reading.
        var (nats, bus) = await Connect();
        await using var _ = nats;
        await using var __ = bus;
        var feed = new LatestValueFeed();
        var coordinator = new ControlFeedbackCoordinator(
            nats, feed, Config("PV-5", TimeSpan.FromSeconds(10)), new FakeAudit(), NullLogger<ControlFeedbackCoordinator>.Instance);
        var controlId = Guid.NewGuid().ToString();

        await bus.PrepareAsync(controlId, CancellationToken.None);
        await coordinator.BeginAsync(
            new ControlFeedbackRequest(controlId, Sp("SP-5"), new ControlSchema { DataType = "number" }, 24, DateTimeOffset.UtcNow), default);
        var waiting = ReadAll(bus, controlId, 2);
        var now = DateTimeOffset.UtcNow;
        feed.Publish(V("PV-5", 24, now.AddSeconds(1)));
        await Task.Delay(200);
        feed.Publish(V("PV-5", 23, now.AddSeconds(2)));
        await GatewayAnswers(nats, controlId, success: true);

        Assert.Equal(ControlResult.FeedbackMatched, (await waiting)[1].Result);
    }

    [Fact]
    public async Task NoValueWithinTheWindow_IsTimeout()
    {
        var (nats, bus) = await Connect();
        await using var _ = nats;
        await using var __ = bus;
        var coordinator = new ControlFeedbackCoordinator(
            nats, new LatestValueFeed(), Config("PV-2", TimeSpan.FromSeconds(1)), new FakeAudit(), NullLogger<ControlFeedbackCoordinator>.Instance);
        var controlId = Guid.NewGuid().ToString();

        await bus.PrepareAsync(controlId, CancellationToken.None);
        await coordinator.BeginAsync(
            new ControlFeedbackRequest(controlId, Sp("SP-2"), new ControlSchema { DataType = "number" }, 24, DateTimeOffset.UtcNow), default);
        var waiting = ReadAll(bus, controlId, 2);
        await GatewayAnswers(nats, controlId, success: true);

        Assert.Equal(ControlResult.FeedbackTimeout, (await waiting)[1].Result);
    }

    [Fact]
    public async Task APointWithoutFeedback_IsUnavailable_ButOnlyAfterTheGatewaySucceeded()
    {
        var (nats, bus) = await Connect();
        await using var _ = nats;
        await using var __ = bus;
        var audit = new FakeAudit();
        var coordinator = new ControlFeedbackCoordinator(
            nats, new LatestValueFeed(), Config(null, TimeSpan.FromSeconds(10)), audit, NullLogger<ControlFeedbackCoordinator>.Instance);
        var controlId = Guid.NewGuid().ToString();

        await bus.PrepareAsync(controlId, CancellationToken.None);
        await coordinator.BeginAsync(new ControlFeedbackRequest(controlId, Sp("SP-3"), null, 1, DateTimeOffset.UtcNow), default);
        var waiting = ReadAll(bus, controlId, 2);
        await GatewayAnswers(nats, controlId, success: true);

        var feedback = (await waiting)[1];
        Assert.Equal(ControlResult.FeedbackUnavailable, feedback.Result);
        Assert.Equal(ControlFeedbackUnavailableReasons.NoFeedbackPoint, feedback.FeedbackValue);
    }

    [Fact]
    public async Task AFailedControl_IsNotConfirmed()
    {
        var (nats, bus) = await Connect();
        await using var _ = nats;
        await using var __ = bus;
        var audit = new FakeAudit();
        var coordinator = new ControlFeedbackCoordinator(
            nats, new LatestValueFeed(), Config("PV-4", TimeSpan.FromSeconds(1)), audit, NullLogger<ControlFeedbackCoordinator>.Instance);
        var controlId = Guid.NewGuid().ToString();

        await bus.PrepareAsync(controlId, CancellationToken.None);
        await coordinator.BeginAsync(
            new ControlFeedbackRequest(controlId, Sp("SP-4"), new ControlSchema { DataType = "number" }, 24, DateTimeOffset.UtcNow), default);
        var reader = await bus.SubscribeAsync(controlId, CancellationToken.None);
        await GatewayAnswers(nats, controlId, success: false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal(ControlResult.Failed, (await reader.ReadAsync(cts.Token)).Result);
        await Task.Delay(1500);
        Assert.Empty(audit.Writes);
        Assert.False(reader.TryRead(out var unexpected), $"unexpected {unexpected?.Result}");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(20);
        }
    }
}
