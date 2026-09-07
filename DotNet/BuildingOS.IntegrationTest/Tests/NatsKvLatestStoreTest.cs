using BuildingOS.Shared;
using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.IntegrationTest.Collections;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

[Collection(Names.Nats)]
public class NatsKvLatestStoreTest(NatsFixture fixture) : IntegrationTestBase
{
    private async Task<NatsKvLatestStore> CreateStoreAsync()
    {
        var (_, js) = await fixture.CreateJetStreamAsync();
        return new NatsKvLatestStore(js, NullLogger<NatsKvLatestStore>.Instance);
    }

    [Fact]
    public async Task PutAsync_And_GetAsync_Returns_LatestValue()
    {
        var store = await CreateStoreAsync();
        var pointId = $"test-point-{Guid.NewGuid():N}";
        var data = new ValidTelemetryData { PointId = pointId, Value = 42.0 };

        await store.PutAsync(pointId, data);
        var result = await store.GetAsync(pointId);

        Assert.NotNull(result);
        Assert.Equal(42.0, result!.Value);
        Assert.Equal(pointId, result!.PointId);
    }

    [Fact]
    public async Task GetAsync_Returns_Null_WhenKeyNotFound()
    {
        var store = await CreateStoreAsync();
        var result = await store.GetAsync($"nonexistent-{Guid.NewGuid():N}");
        Assert.Null(result);
    }

    [Fact]
    public async Task PutAsync_Overwrites_Previous_Value()
    {
        var store = await CreateStoreAsync();
        var pointId = $"overwrite-{Guid.NewGuid():N}";

        await store.PutAsync(pointId, new ValidTelemetryData { PointId = pointId, Value = 10.0 });
        await store.PutAsync(pointId, new ValidTelemetryData { PointId = pointId, Value = 20.0 });

        var result = await store.GetAsync(pointId);

        Assert.NotNull(result);
        Assert.Equal(20.0, result!.Value);
    }

    [Fact]
    public async Task PutAsync_Sanitizes_PointId_With_Special_Chars()
    {
        var store = await CreateStoreAsync();
        var pointId = "building/floor1 zone#2";
        var data = new ValidTelemetryData { PointId = pointId, Value = 1.0 };

        await store.PutAsync(pointId, data);
        var result = await store.GetAsync(pointId);

        Assert.NotNull(result);
        Assert.Equal(1.0, result!.Value);
    }

    // #417: GetAsync must stamp IngestedAt from the KV revision's own write time, not from the stored
    // payload's Datetime — the whole point is to tell "value just written" apart from "value written a
    // while ago, still the latest KV revision because the point was invisible in the twin meanwhile".
    [Fact]
    public async Task GetAsync_StampsIngestedAt_FromTheKvRevisionWriteTime_NotFromDatetime()
    {
        var store = await CreateStoreAsync();
        var pointId = $"ingested-at-{Guid.NewGuid():N}";
        var before = DateTime.UtcNow;
        // Datetime deliberately far from "now" — IngestedAt must not just echo it back.
        var staleDatetime = DateTime.UtcNow.AddHours(-2).ToString("O");

        await store.PutAsync(pointId, new ValidTelemetryData { PointId = pointId, Value = 1.0, Datetime = staleDatetime });
        var result = await store.GetAsync(pointId);
        var after = DateTime.UtcNow;

        Assert.NotNull(result);
        Assert.Equal(staleDatetime, result!.Datetime);
        Assert.NotNull(result.IngestedAt);
        var ingestedAt = DateTime.Parse(result.IngestedAt!, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.InRange(ingestedAt, before.AddSeconds(-1), after.AddSeconds(1));
    }

    [Fact]
    public async Task PutAsync_NeverSetsIngestedAt_OnTheStoredPayload()
    {
        // IngestedAt is derived at read time from KV metadata (entry.Created); a producer setting it
        // on the object handed to PutAsync would be meaningless (the real revision timestamp isn't
        // known until the server acks the write) and must not leak into what gets stored.
        var store = await CreateStoreAsync();
        var pointId = $"put-ignores-ingested-at-{Guid.NewGuid():N}";

        await store.PutAsync(pointId, new ValidTelemetryData { PointId = pointId, Value = 1.0, IngestedAt = "bogus" });
        var result = await store.GetAsync(pointId);

        Assert.NotNull(result);
        Assert.NotEqual("bogus", result!.IngestedAt);
    }
}
