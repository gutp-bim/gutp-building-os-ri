using System.Collections.Concurrent;
using BuildingOS.Shared.Infrastructure.Oss;

namespace BuildingOS.Shared.Infrastructure.Telemetry;

/// <summary>
/// 最終受信インデックスの実体（#452）。**判断はすべてここに置き、NATS には触らない**
/// （<c>NatsKvPointLastSeenIndexWorker</c> は watch イベントを <see cref="Apply"/> /
/// <see cref="Remove"/> / <see cref="MarkReady"/> / <see cref="MarkDegraded"/> に横流しするだけの
/// 薄いアダプタ）。そのぶんこのクラスがそのまま単体テストの対象になる。
///
/// <para>読み取りは lock-free（<see cref="ConcurrentDictionary{TKey,TValue}"/> + volatile）。
/// 一覧 API は 1 リクエストで数千回 <see cref="TryGet"/> するので、ここにロックを置かない。</para>
///
/// <para><b>キーの罠</b>: <c>NatsKvLatestStore.SanitizeKey</c> は pointId の使えない文字を
/// <c>_</c> に潰す**非可逆**変換で、KV キーから pointId は復元できない。そこでこの index は
/// 値 JSON の <c>PointId</c> を正本として載せ（無ければ KV キーで代用）、
/// <see cref="TryGet"/> は生の pointId で引いて外れたら sanitize してもう一度引く。</para>
/// </summary>
public sealed class PointLastSeenIndexStore : IPointLastSeenIndex
{
    private readonly ConcurrentDictionary<string, PointLastSeenEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// sanitize 済みキー → 実際に載っている pointId。削除イベントは KV キー（sanitize 済み）しか
    /// 運ばないので、生の pointId で載っているエントリを消すのに要る。
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _rawBySanitized = new(StringComparer.Ordinal);

    private volatile PointLastSeenIndexState _state = PointLastSeenIndexState.Warming;

    /// <summary>未同期を表す番兵。<see cref="DateTimeOffset"/> は volatile にできないので UTC ticks で持つ。</summary>
    private long _lastSyncUtcTicks = -1;

    public PointLastSeenIndexState State => _state;

    public DateTimeOffset? LastSyncAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastSyncUtcTicks);
            return ticks < 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public int Count => _entries.Count;

    public bool TryGet(string pointId, out PointLastSeenEntry entry)
    {
        if (!string.IsNullOrEmpty(pointId))
        {
            // 完全一致が正。sanitize 後に衝突する別 Point の値を取り違えないよう先に見る。
            if (_entries.TryGetValue(pointId, out var exact))
            {
                entry = exact;
                return true;
            }

            var sanitized = NatsKvLatestStore.SanitizeKey(pointId);
            if (!string.Equals(sanitized, pointId, StringComparison.Ordinal)
                && _entries.TryGetValue(sanitized, out var fallback))
            {
                entry = fallback;
                return true;
            }
        }

        entry = null!;
        return false;
    }

    /// <summary>watch が運んできた 1 件を反映する。**これだけでは Ready にしない**（リプレイ途中の 1 件は「読み切った」証拠にならない）。</summary>
    public void Apply(PointLastSeenEntry entry, DateTimeOffset observedAt)
    {
        if (string.IsNullOrEmpty(entry.PointId)) return;

        _entries[entry.PointId] = entry;

        var sanitized = NatsKvLatestStore.SanitizeKey(entry.PointId);
        if (!string.Equals(sanitized, entry.PointId, StringComparison.Ordinal))
            _rawBySanitized[sanitized] = entry.PointId;

        Touch(observedAt);
    }

    /// <summary>
    /// 削除イベントを反映する。完全一致 → sanitize したキー → sanitize 済みキーの逆引き、の順で探す
    /// （削除イベントは KV キーしか運ばないが、載っているのは生の pointId かもしれないため）。
    /// </summary>
    public void Remove(string pointId, DateTimeOffset observedAt)
    {
        if (!string.IsNullOrEmpty(pointId))
        {
            var sanitized = NatsKvLatestStore.SanitizeKey(pointId);

            if (!_entries.TryRemove(pointId, out _)
                && (string.Equals(sanitized, pointId, StringComparison.Ordinal)
                    || !_entries.TryRemove(sanitized, out _))
                && _rawBySanitized.TryGetValue(sanitized, out var raw))
            {
                _entries.TryRemove(raw, out _);
            }

            _rawBySanitized.TryRemove(sanitized, out _);
        }

        // 削除も「その時刻まで追随できている」証拠なので観測時刻は進める。
        Touch(observedAt);
    }

    /// <summary>初期リプレイ完了。ここから先はエントリが無い Point を本当の欠測として扱ってよい。</summary>
    public void MarkReady(DateTimeOffset at)
    {
        _state = PointLastSeenIndexState.Ready;
        Touch(at);
    }

    /// <summary>
    /// watch が切れて追随できていない。**エントリは捨てない** — 結果は返し、落とすのは
    /// <c>dataComplete</c> だけで、画面を白紙にはしない。
    /// </summary>
    public void MarkDegraded() => _state = PointLastSeenIndexState.Degraded;

    /// <summary>再接続して全件読み直すあいだは Warming に戻す（エントリ無し＝欠測と断定させない）。</summary>
    public void MarkWarming() => _state = PointLastSeenIndexState.Warming;

    private void Touch(DateTimeOffset at) => Interlocked.Exchange(ref _lastSyncUtcTicks, at.UtcTicks);
}
