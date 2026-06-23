using BuildingOS.IntegrationTest.Collections;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using NATS.Client.JetStream.Models;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

[Collection(Names.Nats)]
public class NatsConnectionTest(NatsFixture fixture) : IntegrationTestBase
{
    [Fact]
    public async Task Can_Connect_To_Nats()
    {
        await using var conn = fixture.CreateConnection();
        await conn.ConnectAsync();
        Assert.NotNull(conn);
    }

    [Fact]
    public async Task JetStream_Can_Create_Stream()
    {
        var (conn, js) = await fixture.CreateJetStreamAsync();
        await using var _ = conn;

        var config = new StreamConfig("telemetry-test", ["telemetry.>"])
        {
            Storage = StreamConfigStorage.Memory,
        };
        var stream = await js.CreateStreamAsync(config);
        Assert.Equal("telemetry-test", stream.Info.Config.Name);
    }

    [Fact]
    public async Task JetStream_Can_Publish_And_Consume()
    {
        var (conn, js) = await fixture.CreateJetStreamAsync();
        await using var _ = conn;

        var streamName = $"pub-consume-{Guid.NewGuid():N}";
        var subject = $"sensor.data.{Guid.NewGuid():N}";

        await js.CreateStreamAsync(new StreamConfig(streamName, [subject])
        {
            Storage = StreamConfigStorage.Memory,
        });

        var ack = await js.PublishAsync(subject, System.Text.Encoding.UTF8.GetBytes("""{"pointId":"p1","value":22.5}"""));
        Assert.Equal(1UL, ack.Seq);

        var consumer = await js.CreateOrUpdateConsumerAsync(streamName,
            new ConsumerConfig($"consumer-{Guid.NewGuid():N}")
            {
                DeliverPolicy = ConsumerConfigDeliverPolicy.All,
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
            });

        await foreach (var msg in consumer.ConsumeAsync<byte[]>())
        {
            await msg.AckAsync();
            var body = System.Text.Encoding.UTF8.GetString(msg.Data ?? []);
            Assert.Contains("p1", body);
            break;
        }
    }
}
