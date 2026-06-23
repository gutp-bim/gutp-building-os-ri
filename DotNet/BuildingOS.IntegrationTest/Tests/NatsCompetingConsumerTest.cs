using BuildingOS.IntegrationTest.Collections;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared.Infrastructure.Messaging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>
/// NATS JetStream の competing consumer（pull durable）が正しく機能することを検証する。
/// Issue #114: ConnectorWorker 水平スケール時の重複なし・再配信の保証。
/// </summary>
[Collection(Names.Nats)]
public class NatsCompetingConsumerTest(NatsFixture fixture) : IntegrationTestBase
{
    // --- Test 1: 重複なし・欠損なし -------------------------------------------

    /// <summary>
    /// 同一 durable 名の NatsMessageSubscription を2インスタンス起動したとき、
    /// publish した N メッセージが合計 N 回だけ処理される（重複も欠損もない）。
    /// </summary>
    [Fact]
    public async Task TwoSubscribers_SameDurable_EachMessageProcessedOnce()
    {
        const int messageCount = 20;
        const string subject = "building-os.raw.test-competing";
        const string durableName = "test-competing-consumer";

        var processedMessages = new System.Collections.Concurrent.ConcurrentBag<string>();

        // 2つの独立した接続で同一 durable の NatsMessageSubscription を作成
        await using var conn1 = fixture.CreateConnection();
        await conn1.ConnectAsync();
        await using var conn2 = fixture.CreateConnection();
        await conn2.ConnectAsync();

        await using var sub1 = new NatsMessageSubscription(subject, durableName, conn1);
        await using var sub2 = new NatsMessageSubscription(subject, durableName, conn2);

        sub1.Register(msg => { processedMessages.Add(msg); return Task.CompletedTask; });
        sub2.Register(msg => { processedMessages.Add(msg); return Task.CompletedTask; });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await sub1.StartAsync(cts.Token);
        await sub2.StartAsync(cts.Token);

        // メッセージを publish
        var (pubConn, js) = await fixture.CreateJetStreamAsync();
        await using var _ = pubConn;
        for (var i = 0; i < messageCount; i++)
            await js.PublishAsync(subject, $"msg-{i}");

        // 全メッセージが処理されるまで待機（最大 15 秒）
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (processedMessages.Count < messageCount && DateTime.UtcNow < deadline)
            await Task.Delay(100, cts.Token);

        cts.Cancel();

        Assert.Equal(messageCount, processedMessages.Count);
        // 重複がないことを確認
        Assert.Equal(messageCount, processedMessages.Distinct().Count());
    }

    // --- Test 2: 未 Ack メッセージの再配信 ------------------------------------

    /// <summary>
    /// ack_wait 以内に Ack されなかったメッセージは別のコンシューマーに再配信される。
    /// NatsMessageSubscription を使わず NATS API を直接使い、ack_wait = 3s で検証する。
    /// </summary>
    [Fact]
    public async Task UnackedMessage_IsRedeliveredAfterAckWait()
    {
        // UUID suffix で他テストのストリームと subject の競合を避ける
        var id = Guid.NewGuid().ToString("N")[..8];
        var streamName = $"TEST_REDELIVERY_{id}";
        var subject = $"test.redelivery.{id}";
        var durableName = $"test-redelivery-consumer-{id}";

        var (conn1, js1) = await fixture.CreateJetStreamAsync();
        await using var _1 = conn1;

        // ack_wait = 3 秒のコンシューマーを作成
        await js1.CreateStreamAsync(new StreamConfig(streamName, [subject])
        {
            Storage = StreamConfigStorage.Memory,
        });

        var consumerConfig = new ConsumerConfig(durableName)
        {
            FilterSubject = subject,
            AckPolicy = ConsumerConfigAckPolicy.Explicit,
            AckWait = TimeSpan.FromSeconds(3),
        };
        await js1.CreateOrUpdateConsumerAsync(streamName, consumerConfig);

        // メッセージを1件 publish
        await js1.PublishAsync(subject, "redelivery-test");

        // sub1: メッセージを受け取るが Ack しない（接続を即閉じる）
        var consumer1 = await js1.GetConsumerAsync(streamName, durableName);
        var receivedByFirst = false;
        await foreach (var msg in consumer1.FetchAsync<string>(opts: new NatsJSFetchOpts { MaxMsgs = 1 }))
        {
            receivedByFirst = true;
            // 意図的に Ack しない（conn1 を閉じる前に break）
            break;
        }
        Assert.True(receivedByFirst, "sub1 should have received the message");

        // conn1 を閉じる → Ack されないまま接続が切れる
        await conn1.DisposeAsync();

        // ack_wait（3s）が過ぎるまで待機
        await Task.Delay(TimeSpan.FromSeconds(4));

        // sub2: 再配信されたメッセージを受け取る
        var (conn2, js2) = await fixture.CreateJetStreamAsync();
        await using var _2 = conn2;

        var consumer2 = await js2.GetConsumerAsync(streamName, durableName);
        var receivedBySecond = false;
        await foreach (var msg in consumer2.FetchAsync<string>(opts: new NatsJSFetchOpts { MaxMsgs = 1 }))
        {
            receivedBySecond = true;
            await msg.AckAsync();
            break;
        }

        Assert.True(receivedBySecond, "sub2 should have received the redelivered message");
    }
}
