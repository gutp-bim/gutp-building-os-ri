using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.JetStream;

namespace BuildingOS.Shared.Test.Infrastructure.Oss;

/// <summary>
/// 空バケットを Ready と決めるまでの判断（#452 / #460 レビュー）。
///
/// <para>watch の配線そのものは NATS が要るのでここでは触らない（このワーカーは薄いアダプタで、
/// 状態機械は <c>PointLastSeenIndexStore</c> が持つ）。テストするのは<b>唯一の判断らしい判断</b>である
/// 「空バケットを Ready にしてよいか」だけ。ここを誤ると起動直後に全 Point が
/// <c>Missing / NeverReceived</c> と<b>確定表示</b>される（<c>dataComplete=true</c> なので画面は
/// 暫定バナーすら出さない）。本設計が防ぎたい誤判定そのものなので、分岐を固定しておく。</para>
/// </summary>
public class NatsKvPointLastSeenIndexWorkerTest
{
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// 猶予は 1ms に縮める（待ち時間の長さは仕様ではない）。<see cref="INatsJSContext"/> は
    /// この経路では一切触られないので、Moq のダミーで足りる。
    /// </summary>
    private static (NatsKvPointLastSeenIndexWorker worker, PointLastSeenIndexStore store) Build()
    {
        var store = new PointLastSeenIndexStore();
        var worker = new NatsKvPointLastSeenIndexWorker(
            new Mock<INatsJSContext>().Object,
            store,
            NullLogger<NatsKvPointLastSeenIndexWorker>.Instance,
            new FixedClock(Now),
            TimeSpan.FromMilliseconds(1));
        return (worker, store);
    }

    [Fact]
    public async Task StillEmptyAfterTheGrace_MarksReady()
    {
        var (worker, store) = Build();

        await worker.MarkReadyIfStillEmptyAsync(_ => Task.FromResult(0L), default);

        Assert.Equal(PointLastSeenIndexState.Ready, store.State);
        Assert.Equal(Now, store.LastSyncAt);
    }

    /// <summary>
    /// 猶予中に書き込みが入った場合。その watch 配信がまだ届いていなければ store は Warming のままなので、
    /// 「Warming かどうか」だけでは弾けない。件数を読み直して 0 でなければ Ready にせず、
    /// 配信された <c>Delta == 0</c> に判断を譲る。
    /// </summary>
    [Fact]
    public async Task BucketFilledDuringTheGrace_StaysWarming()
    {
        var (worker, store) = Build();

        await worker.MarkReadyIfStillEmptyAsync(_ => Task.FromResult(5_000L), default);

        Assert.Equal(PointLastSeenIndexState.Warming, store.State);
    }

    /// <summary>件数を読み直せないときも Ready にしない（「判定できない」の方が誤った確定表示より安全）。</summary>
    [Fact]
    public async Task ReCheckFails_StaysWarming()
    {
        var (worker, store) = Build();

        await worker.MarkReadyIfStillEmptyAsync(
            _ => Task.FromException<long>(new InvalidOperationException("bucket unreachable")), default);

        Assert.Equal(PointLastSeenIndexState.Warming, store.State);
    }

    /// <summary>
    /// 猶予中に watch 側が読み切って Ready にしていたら、件数の読み直しすら行わない
    /// （Degraded に落ちていた場合も、こちらが勝手に Ready へ引き上げない）。
    /// </summary>
    [Fact]
    public async Task AlreadyReady_DoesNotReCheck()
    {
        var (worker, store) = Build();
        store.MarkReady(Now.AddSeconds(-1));
        var reChecked = false;

        await worker.MarkReadyIfStillEmptyAsync(
            _ =>
            {
                reChecked = true;
                return Task.FromResult(0L);
            },
            default);

        Assert.False(reChecked);
        Assert.Equal(PointLastSeenIndexState.Ready, store.State);
    }

    [Fact]
    public async Task Degraded_IsNotPromotedToReady()
    {
        var (worker, store) = Build();
        store.MarkDegraded();

        await worker.MarkReadyIfStillEmptyAsync(_ => Task.FromResult(0L), default);

        Assert.Equal(PointLastSeenIndexState.Degraded, store.State);
    }

    [Fact]
    public async Task Cancelled_DoesNotMarkReady()
    {
        var (worker, store) = Build();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            worker.MarkReadyIfStillEmptyAsync(_ => Task.FromResult(0L), cts.Token));

        Assert.Equal(PointLastSeenIndexState.Warming, store.State);
    }
}
