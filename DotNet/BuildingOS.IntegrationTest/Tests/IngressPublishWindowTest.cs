using BuildingOS.ConnectorWorker.Connectors;
using BuildingOS.ConnectorWorker.Protos;
using BuildingOS.IntegrationTest.Collections;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOS.Shared.Module;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>
/// #625 slices A + B against a real NATS: the windowed publish keeps #187 (accepted = persisted),
/// an ack failure mid-stream is not counted but does not stop the stream, and the coalesced hot
/// store lands the newest value per point in the real KV bucket.
/// </summary>
[Collection(Names.Nats)]
public class IngressPublishWindowTest(NatsFixture fixture) : IntegrationTestBase
{
    private const string StreamName = "BUILDING_OS_VALIDATED";

    private sealed class StreamReader(IEnumerable<TelemetryFrame> frames) : IAsyncStreamReader<TelemetryFrame>
    {
        private readonly IEnumerator<TelemetryFrame> _frames = frames.GetEnumerator();
        public TelemetryFrame Current => _frames.Current;
        public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromResult(_frames.MoveNext());
    }

    private static GatewayIngressService NewService(IIngressTelemetryBus bus, int window)
    {
        var cache = new Mock<IPointMetadataCache>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((id, _) => Task.FromResult<PointMetadata?>(new PointMetadata(id, "bldg-1", "Temp", "DEV001", "GW-625")));
        return new GatewayIngressService(bus, cache.Object, new IngressIdentityOptions(), new IngressHierarchyOptions(),
            NullLogger<GatewayIngressService>.Instance, publish: new IngressPublishOptions { Window = window });
    }

    private static IEnumerable<TelemetryFrame> Frames(int count, string pointPrefix, DateTimeOffset start) =>
        Enumerable.Range(0, count).Select(i => new TelemetryFrame
        {
            GatewayId = "GW-625", PointId = $"{pointPrefix}-{i % 10}", ValueNum = i,
            Timestamp = start.AddSeconds(i).ToString("O"),
        });

    [Fact]
    public async Task WindowedPublish_AcceptedEqualsPersisted_AndCoalescedKvHoldsTheNewestValue()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        var (connection, js) = await fixture.CreateJetStreamAsync();
        await using var lease = connection;
        var kv = new NatsKvLatestStore(js, NullLogger<NatsKvLatestStore>.Instance);
        await using var hot = new CoalescingHotTelemetryStore(kv,
            new CoalescingHotStoreOptions { Interval = TimeSpan.FromMilliseconds(50) },
            NullLogger<CoalescingHotTelemetryStore>.Instance);
        var bus = new NatsIngressTelemetryBus(js, hot, NullLogger<NatsIngressTelemetryBus>.Instance);
        var prefix = $"P625-{Guid.NewGuid():N}";
        var before = await CountAsync(js, ct);

        var accepted = await NewService(bus, window: 32).RunAsync(new StreamReader(Frames(200, prefix, DateTimeOffset.UtcNow)), ct, "GW-625");

        Assert.Equal(200, accepted);
        Assert.Equal(before + 200, await CountAsync(js, ct));

        // 200 frames over 10 points: the KV must end with the newest reading of each (value 190..199).
        await hot.StopAsync(ct);
        for (var p = 0; p < 10; p++)
        {
            var latest = await kv.GetAsync($"{prefix}-{p}", ct);
            Assert.NotNull(latest);
            Assert.Equal(190 + p, latest!.Value);
        }
    }

    [Fact]
    public async Task WindowedPublish_AckFailureIsNotAccepted_AndTheStreamContinues()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = timeout.Token;
        var (connection, js) = await fixture.CreateJetStreamAsync();
        await using var lease = connection;
        var hot = new Mock<IHotTelemetryStore>();
        var bus = new NatsIngressTelemetryBus(js, hot.Object, NullLogger<NatsIngressTelemetryBus>.Instance);
        var prefix = $"P625F-{Guid.NewGuid():N}";
        var service = NewService(bus, window: 16);

        // Warm-up: the bus ensures the stream once and caches that; later frames rely on the cache.
        Assert.Equal(5, await service.RunAsync(new StreamReader(Frames(5, prefix, DateTimeOffset.UtcNow)), ct, "GW-625"));

        // Pull the stream away: every in-flight ack now fails (no responder for the subject), and
        // none of those frames may be counted — the bus must not recreate the stream behind our back.
        await js.DeleteStreamAsync(StreamName, ct);
        var acceptedWhileGone = await service.RunAsync(new StreamReader(Frames(40, prefix, DateTimeOffset.UtcNow)), ct, "GW-625");
        Assert.Equal(0, acceptedWhileGone);

        // Bring it back and prove the same service instance keeps going (the gRPC stream was not torn down).
        await js.CreateStreamAsync(new StreamConfig(StreamName, ["building-os.validated.>"]), ct);
        var acceptedAfter = await service.RunAsync(new StreamReader(Frames(7, prefix, DateTimeOffset.UtcNow)), ct, "GW-625");
        Assert.Equal(7, acceptedAfter);
        Assert.Equal(7, await CountAsync(js, ct));
    }

    private static async Task<long> CountAsync(INatsJSContext js, CancellationToken ct)
    {
        try
        {
            var stream = await js.GetStreamAsync(StreamName, cancellationToken: ct);
            return (long)stream.Info.State.Messages;
        }
        catch (NatsJSApiException ex) when (ex.Error.Code == 404)
        {
            return 0;
        }
    }
}
