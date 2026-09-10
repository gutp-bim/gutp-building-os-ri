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
/// 値 JSON の <c>PointId</c> を正本として載せる（無ければ KV キーで代用）。</para>
///
/// <para><b><see cref="TryGet"/> は完全一致だけを見る。</b>sanitize したキーで引き直すと、
/// 同じキーに潰れる別の pointId（<c>SOS/PT-1</c> と <c>SOS:PT-1</c>）の値を取り違え、
/// 一度もデータを送っていない Point が別 Point の値と時刻で Fresh に見えてしまう
/// ——しかもその値で警報まで判定される。引けない側（Missing / Unknown）に倒せば画面に見える
/// 形で出るだけなので、安全側はこちら。現行の書き込み経路は値に pointId を必ず入れるため、
/// 生の pointId で完全一致する。</para>
///
/// <para>削除だけは事情が違う。KV の削除イベントは sanitize 済みキーしか運ばないので、
/// <see cref="Remove"/> は <c>_rawBySanitized</c> を使って実際に載っている pointId を引き当てる。</para>
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
        // **完全一致だけを見る。sanitize したキーで引き直さない。**
        //
        // `NatsKvLatestStore.SanitizeKey` は使えない文字を `_` に潰す非可逆変換なので、
        // 別々の pointId（`SOS/PT-1` と `SOS:PT-1`）が同じ KV キー `SOS_PT-1` に落ちる。
        // 値 JSON に pointId を持たない古いエントリはその潰れたキーで載るため、sanitize して
        // 引き直すと「一度もデータを送っていない Point」に別 Point の値と時刻を返してしまう
        // ——死んでいる Point が Fresh に見え、その値で警報まで判定される。
        //
        // 引けなければ Missing（index が Warming なら Unknown）に倒れるだけで、誤りは画面に
        // 見える形で出る。取り違えは見えない。安全側はこちら。
        // 現行の書き込み経路は値に pointId を必ず入れるので、生の pointId で完全一致する。
        if (!string.IsNullOrEmpty(pointId) && _entries.TryGetValue(pointId, out var exact))
        {
            entry = exact;
            return true;
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
