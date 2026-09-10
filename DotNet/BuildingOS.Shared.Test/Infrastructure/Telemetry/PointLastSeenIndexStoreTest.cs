using BuildingOS.Shared.Infrastructure.Telemetry;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry;

/// <summary>
/// 最終受信インデックス（#452）の状態機械の仕様。
///
/// <para><b>NATS はここでは触らない</b>。<c>NatsKvPointLastSeenIndexWorker</c> は
/// <c>telemetry-latest</c> の watch イベントを <see cref="PointLastSeenIndexStore"/> の
/// <c>Apply</c>/<c>Remove</c>/<c>MarkReady</c>/<c>MarkDegraded</c> に横流しするだけの薄いアダプタで、
/// 判断はすべてこの store 側に置く。だからここが単体テストの対象になる。</para>
///
/// <para><b>状態の意味</b>: <c>Warming</c> は「まだ全件読み切っていない＝エントリが無いことが
/// 欠測の証拠にならない」、<c>Ready</c> は「初期リプレイ完了＝エントリが無い Point は本当に
/// 一度も来ていない」、<c>Degraded</c> は「watch が切れて追随できていない＝手元の値は使えるが
/// 完全ではない」。<c>PointHealthClassifier</c> 側の <c>indexReady</c> はこの状態から導出される
/// （<c>Ready</c> のみ true）。</para>
/// </summary>
public class PointLastSeenIndexStoreTest
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    private static PointLastSeenEntry Entry(
        string pointId, double? value = 23.4, DateTimeOffset? lastSeen = null, string? valueType = "number")
        => new(pointId, lastSeen ?? T0, value, valueType);

    // ---------------------------------------------------------------------
    // 初期状態と Ready への遷移
    // ---------------------------------------------------------------------

    [Fact]
    public void NewStore_IsWarmingAndEmpty()
    {
        var store = new PointLastSeenIndexStore();

        Assert.Equal(PointLastSeenIndexState.Warming, store.State);
        Assert.Equal(0, store.Count);
        Assert.Null(store.LastSyncAt);
    }

    [Fact]
    public void Apply_AloneDoesNotBecomeReady()
    {
        // リプレイ途中の 1 件は「読み切った」証拠にならない。Ready にできるのは
        // 初期リプレイ完了（Delta == 0）を見たアダプタだけ。
        var store = new PointLastSeenIndexStore();

        store.Apply(Entry("PT-1"), T0);

        Assert.Equal(PointLastSeenIndexState.Warming, store.State);
    }

    [Fact]
    public void MarkReady_BecomesReadyAndRecordsSyncTime()
    {
        var store = new PointLastSeenIndexStore();

        store.MarkReady(T0.AddSeconds(5));

        Assert.Equal(PointLastSeenIndexState.Ready, store.State);
        Assert.Equal(T0.AddSeconds(5), store.LastSyncAt);
    }

    [Fact]
    public void Apply_AfterMarkReady_StaysReady()
    {
        var store = new PointLastSeenIndexStore();
        store.MarkReady(T0);

        store.Apply(Entry("PT-1"), T0.AddSeconds(1));

        Assert.Equal(PointLastSeenIndexState.Ready, store.State);
    }

    // ---------------------------------------------------------------------
    // 読み書き
    // ---------------------------------------------------------------------

    [Fact]
    public void Apply_ThenTryGet_ReturnsStoredValueAndLastSeen()
    {
        var store = new PointLastSeenIndexStore();
        var lastSeen = T0.AddMinutes(-3);

        store.Apply(Entry("PT-1", value: 23.4, lastSeen: lastSeen), T0);

        Assert.True(store.TryGet("PT-1", out var entry));
        Assert.Equal("PT-1", entry.PointId);
        Assert.Equal(lastSeen, entry.LastSeen);
        Assert.Equal(23.4, entry.Value);
        Assert.Equal("number", entry.ValueType);
    }

    [Fact]
    public void TryGet_UnknownPoint_IsFalse()
    {
        var store = new PointLastSeenIndexStore();
        store.MarkReady(T0);

        Assert.False(store.TryGet("PT-404", out _));
    }

    [Fact]
    public void Apply_UnparsableTimestamp_KeepsEntryWithNullLastSeen()
    {
        // datetime が読めなかった値は「エントリはあるが lastSeen が無い」。捨ててしまうと
        // 「一度も来ていない（NeverReceived）」と区別できなくなるので残す。
        var store = new PointLastSeenIndexStore();

        store.Apply(new PointLastSeenEntry("PT-1", null, 1, "number"), T0);

        Assert.True(store.TryGet("PT-1", out var entry));
        Assert.Null(entry.LastSeen);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Apply_SamePointTwice_ReplacesEntryWithoutGrowing()
    {
        var store = new PointLastSeenIndexStore();

        store.Apply(Entry("PT-1", value: 1, lastSeen: T0.AddMinutes(-10)), T0);
        store.Apply(Entry("PT-1", value: 2, lastSeen: T0.AddMinutes(-1)), T0.AddSeconds(1));

        Assert.Equal(1, store.Count);
        Assert.True(store.TryGet("PT-1", out var entry));
        Assert.Equal(2, entry.Value);
        Assert.Equal(T0.AddMinutes(-1), entry.LastSeen);
    }

    [Fact]
    public void Count_ReflectsDistinctPoints()
    {
        var store = new PointLastSeenIndexStore();

        store.Apply(Entry("PT-1"), T0);
        store.Apply(Entry("PT-2"), T0);
        store.Apply(Entry("PT-1"), T0);

        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void Remove_MakesPointUnreadable()
    {
        var store = new PointLastSeenIndexStore();
        store.Apply(Entry("PT-1"), T0);

        store.Remove("PT-1", T0.AddSeconds(1));

        Assert.False(store.TryGet("PT-1", out _));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Remove_UnknownPoint_IsHarmless()
    {
        var store = new PointLastSeenIndexStore();
        store.Apply(Entry("PT-1"), T0);

        store.Remove("PT-404", T0.AddSeconds(1));

        Assert.Equal(1, store.Count);
        Assert.True(store.TryGet("PT-1", out _));
    }

    // ---------------------------------------------------------------------
    // LastSyncAt（「いつ時点の値か」の表示に使う）
    // ---------------------------------------------------------------------

    [Fact]
    public void LastSyncAt_TracksLatestApply()
    {
        var store = new PointLastSeenIndexStore();

        store.Apply(Entry("PT-1"), T0.AddSeconds(1));
        store.Apply(Entry("PT-2"), T0.AddSeconds(7));

        Assert.Equal(T0.AddSeconds(7), store.LastSyncAt);
    }

    [Fact]
    public void LastSyncAt_IsUpdatedByRemoveToo()
    {
        // 削除イベントも「その時刻まで追随できている」証拠なので観測時刻を進める。
        var store = new PointLastSeenIndexStore();
        store.Apply(Entry("PT-1"), T0.AddSeconds(1));

        store.Remove("PT-1", T0.AddSeconds(9));

        Assert.Equal(T0.AddSeconds(9), store.LastSyncAt);
    }

    // ---------------------------------------------------------------------
    // Degraded / Warming（劣化しても手元の値は捨てない）
    // ---------------------------------------------------------------------

    [Fact]
    public void MarkDegraded_KeepsExistingEntriesReadable()
    {
        // watch が切れても結果は返す。落とすのは dataComplete だけで、画面を白紙にはしない。
        var store = new PointLastSeenIndexStore();
        store.Apply(Entry("PT-1", value: 5), T0);
        store.MarkReady(T0);

        store.MarkDegraded();

        Assert.Equal(PointLastSeenIndexState.Degraded, store.State);
        Assert.True(store.TryGet("PT-1", out var entry));
        Assert.Equal(5, entry.Value);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void MarkReady_AfterDegraded_RecoversToReady()
    {
        var store = new PointLastSeenIndexStore();
        store.MarkDegraded();

        store.MarkReady(T0.AddMinutes(1));

        Assert.Equal(PointLastSeenIndexState.Ready, store.State);
        Assert.Equal(T0.AddMinutes(1), store.LastSyncAt);
    }

    [Fact]
    public void MarkWarming_ReturnsToWarming()
    {
        // 再接続して全件読み直す間は Warming に戻す（エントリ無し＝欠測と断定させない）。
        var store = new PointLastSeenIndexStore();
        store.MarkReady(T0);

        store.MarkWarming();

        Assert.Equal(PointLastSeenIndexState.Warming, store.State);
    }

    // ---------------------------------------------------------------------
    // KV キー sanitize の罠（NatsKvLatestStore.SanitizeKey は非可逆）
    // ---------------------------------------------------------------------

    [Fact]
    public void TryGet_FallsBackToSanitizedKey()
    {
        // 値 JSON に pointId が無い古いエントリは KV キー（sanitize 済み）で載る。
        // 台帳側は生の pointId で引くので、外れたら sanitize してもう一度引く。
        var store = new PointLastSeenIndexStore();
        store.Apply(Entry("SOS_PT-1"), T0);
        store.MarkReady(T0);

        Assert.True(store.TryGet("SOS/PT-1", out var entry));
        Assert.Equal("SOS_PT-1", entry.PointId);
        Assert.True(store.TryGet("SOS_PT-1", out _));
    }

    [Fact]
    public void TryGet_PrefersExactPointIdOverSanitizedFallback()
    {
        // 生の pointId で載っているエントリが正。sanitize 後に衝突する別 Point の値を
        // 取り違えないよう、完全一致を先に見る。
        var store = new PointLastSeenIndexStore();
        store.Apply(Entry("SOS/PT-1", value: 1), T0);
        store.Apply(Entry("SOS_PT-1", value: 2), T0);
        store.MarkReady(T0);

        Assert.True(store.TryGet("SOS/PT-1", out var entry));
        Assert.Equal(1, entry.Value);
    }

    [Fact]
    public void Remove_AcceptsTheSanitizedKeyForAPointStoredUnderIt()
    {
        // 削除イベントは KV キーしか運ばない。Apply と同じキー空間で消せること。
        var store = new PointLastSeenIndexStore();
        store.Apply(Entry("SOS_PT-1"), T0);

        store.Remove("SOS_PT-1", T0.AddSeconds(1));

        Assert.False(store.TryGet("SOS/PT-1", out _));
        Assert.Equal(0, store.Count);
    }
}
