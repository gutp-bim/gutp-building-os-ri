using System.Collections.Concurrent;
using BuildingOS.Shared;

namespace BuildingOs.ApiServer.Authorization;

/// <summary>
/// 建物ごとの Point 台帳（<see cref="PointDetail"/>[]）の短 TTL キャッシュ（#452）。
/// データ健全性一覧は 1 リクエストで建物全件の台帳を読むので、画面のポーリングがそのまま
/// OxiGraph への全件 SPARQL になるのを防ぐ。
///
/// <para><b>キャッシュするのは認可前の twin データだけ</b>。認可の絞り込みは必ずリクエストごとに
/// 適用する（ここを取り違えると、ある利用者の可視範囲が別の利用者に漏れる）。だから
/// <see cref="GetAsync"/> の戻り値は「その建物の全 Point」であって「その利用者が読める Point」ではない。</para>
///
/// <para>同一建物への同時アクセスは single-flight で 1 本にまとめる。読み込みは呼び出し元の
/// <see cref="CancellationToken"/> で走る — 共有ロードを 1 リクエストの離脱が巻き添えにするのは
/// <c>PointMetadataCache</c>(#371) の教訓だが、ここでロードするのは呼び出し元のスコープに属する
/// <c>IDigitalTwinDatabase</c> なので、トークンだけ切り離すと今度はスコープ破棄と競合する。
/// 代わりに<b>相乗り側が巻き添えキャンセルを検知したら自分でロードし直す</b>（<see cref="GetAsync"/>）。</para>
///
/// <para><b>エントリ数は上限つき</b>。建物 dtId はクエリで呼び出し元が自由に指定できるため、
/// 実在しない ID を並べられるとキーが無制限に増える。<see cref="MaxEntries"/> を超えたら
/// 古い順に落とす（LRU ではなく単純な読み込み時刻順。台帳は建物数ぶんしか無いのが正常系で、
/// 上限に触れること自体が異常系のため厳密さより単純さを採る）。</para>
/// </summary>
public sealed class PointDetailInventoryCache
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);

    /// <summary>保持する建物スナップショットの上限。実在する建物数を十分上回る値。</summary>
    public const int MaxEntries = 256;

    private readonly TimeSpan _ttl;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, Snapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<PointDetail[]>>> _inFlight = new(StringComparer.Ordinal);

    private sealed record Snapshot(PointDetail[] Points, DateTimeOffset LoadedAt);

    public PointDetailInventoryCache(TimeSpan? ttl = null, TimeProvider? clock = null)
    {
        _ttl = ttl is { } t && t > TimeSpan.Zero ? t : DefaultTtl;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>現在保持しているスナップショット数（テスト用）。</summary>
    public int Count => _snapshots.Count;

    /// <summary>
    /// TTL 内のスナップショットがあればそれを返し、無ければ <paramref name="load"/> を 1 本だけ走らせる。
    /// </summary>
    public async Task<PointDetail[]> GetAsync(
        string buildingDtId, Func<CancellationToken, Task<PointDetail[]>> load, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_snapshots.TryGetValue(buildingDtId, out var cached) && now - cached.LoadedAt < _ttl)
            return cached.Points;

        // Lazy(ExecutionAndPublication) で「同じ建物のロードは 1 本」を保証する
        // （ConcurrentDictionary.GetOrAdd のファクトリは同時に複数回走り得るため）。
        var lazy = _inFlight.GetOrAdd(
            buildingDtId,
            key => new Lazy<Task<PointDetail[]>>(
                () => LoadAsync(key, load, ct), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 相乗りしたロードが「先行リクエストの離脱」で落ちただけ。自分は生きているので、
            // 巻き添えで 500 を返さずに自分のトークンで読み直す。
            return await LoadAsync(buildingDtId, load, ct).ConfigureAwait(false);
        }
        finally
        {
            // 失敗したロードを残さない（次のリクエストが引き直せるように）。自分が登録した
            // Lazy と同一のときだけ外すので、後続が publish した新しいロードは消さない。
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<PointDetail[]>>>(buildingDtId, lazy));
        }
    }

    private async Task<PointDetail[]> LoadAsync(
        string buildingDtId, Func<CancellationToken, Task<PointDetail[]>> load, CancellationToken ct)
    {
        var points = await load(ct).ConfigureAwait(false);
        _snapshots[buildingDtId] = new Snapshot(points, _clock.GetUtcNow());
        Trim();
        return points;
    }

    /// <summary>上限を超えたぶんを読み込みが古い順に落とす。上限内なら何もしない。</summary>
    private void Trim()
    {
        if (_snapshots.Count <= MaxEntries) return;
        foreach (var key in _snapshots
                     .ToArray()
                     .OrderBy(kv => kv.Value.LoadedAt)
                     .Take(Math.Max(1, _snapshots.Count - MaxEntries))
                     .Select(kv => kv.Key))
        {
            _snapshots.TryRemove(key, out _);
        }
    }
}
