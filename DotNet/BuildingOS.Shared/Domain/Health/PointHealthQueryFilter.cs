namespace BuildingOS.Shared.Domain.Health;

/// <summary>
/// 一覧の絞り込み・並べ替え・ページング（#452）。**純粋関数**で、台帳にも index にも触らない。
///
/// <para>判定済みの 3 軸に対する絞り込みは SPARQL では書けない（鮮度は台帳に無い）ので、
/// 呼び出し元が「台帳 × index → 判定」まで済ませた <see cref="PointHealthItem"/> の列を渡し、
/// ここで畳む、という分担にしている。新しい検索エンジンは足さない。</para>
/// </summary>
public static class PointHealthQueryFilter
{
    /// <summary>
    /// 絞り込み → 並べ替え → ページングをこの順で適用する。
    /// 返す <see cref="PointHealthPage.Total"/> は**ページング前**（絞り込み後）の件数。
    /// </summary>
    public static PointHealthPage Apply(IEnumerable<PointHealthItem> items, PointHealthQuery query)
    {
        var filtered = items.Where(item => Matches(item, query)).ToList();
        var total = filtered.Count;

        var sorted = Sort(filtered, query.Sort);

        var offset = Math.Max(0, query.Offset);
        var limit = Math.Max(0, query.Limit);
        var page = sorted.Skip(offset).Take(limit).ToArray();

        return new PointHealthPage(page, total);
    }

    private static bool Matches(PointHealthItem item, PointHealthQuery query)
    {
        // 階層スコープ。完全一致で、空白は「絞らない」。
        if (!MatchesScope(query.BuildingDtId, item.BuildingDtId)) return false;
        if (!MatchesScope(query.FloorDtId, item.FloorDtId)) return false;
        if (!MatchesScope(query.DeviceDtId, item.DeviceDtId)) return false;

        // gateway id の照合は registry と同じく大小無視。gateway を持たない Point は一致しない。
        if (!string.IsNullOrWhiteSpace(query.GatewayId)
            && !string.Equals(item.Gateway?.Id, query.GatewayId, StringComparison.OrdinalIgnoreCase))
            return false;

        // 同一軸の複数指定は OR、軸をまたぐと AND。
        if (query.Freshness.Count > 0 && !query.Freshness.Contains(item.Freshness.Status)) return false;
        if (query.Alarm.Count > 0 && !query.Alarm.Contains(item.Alarm.Status)) return false;
        if (query.HealthStatuses.Count > 0 && !query.HealthStatuses.Contains(item.HealthStatus)) return false;

        // 齢が無い（一度も来ていない）Point は ∞ 扱いで必ず一致させる。ここを取りこぼすと
        // 「N 秒以上来ていないもの」を探した運用者に、最も深刻な欠測だけが見えなくなる。
        if (query.OlderThanSeconds is { } olderThan
            && item.Freshness.AgeSeconds is { } age && age < olderThan)
            return false;

        // タグは AND・大小無視。
        foreach (var tag in query.Tags)
        {
            if (string.IsNullOrWhiteSpace(tag)) continue;
            if (!item.Tags.Any(t => string.Equals(t, tag.Trim(), StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        var q = query.Q?.Trim();
        if (!string.IsNullOrEmpty(q))
        {
            var hit = item.PointId.Contains(q, StringComparison.OrdinalIgnoreCase)
                      || (item.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
            if (!hit) return false;
        }

        return true;
    }

    private static bool MatchesScope(string? queried, string? actual) =>
        string.IsNullOrWhiteSpace(queried) || string.Equals(queried, actual, StringComparison.Ordinal);

    private static IEnumerable<PointHealthItem> Sort(List<PointHealthItem> items, PointHealthSort sort) => sort switch
    {
        // 「最終受信が古い順」。lastSeen 無しはその極北なので先頭に置く（新しい順ではない）。
        PointHealthSort.LastSeen => items
            .OrderBy(i => i.Freshness.LastSeen?.UtcTicks ?? long.MinValue)
            .ThenBy(DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.PointId, StringComparer.Ordinal),

        PointHealthSort.Name => items
            .OrderBy(DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.PointId, StringComparer.Ordinal),

        // 深刻度は HealthStatus の宣言順（worst-first）。同率なら「より長く来ていない方」が上で、
        // 齢が無いものは ∞ 扱いで先頭。最後に名前昇順。
        _ => items
            .OrderBy(i => (int)i.HealthStatus)
            .ThenByDescending(i => i.Freshness.AgeSeconds ?? long.MaxValue)
            .ThenBy(DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.PointId, StringComparer.Ordinal),
    };

    /// <summary>名前が空なら pointId を代用する（名前未設定の Point が並びの先頭に固まらないように）。</summary>
    private static string DisplayName(PointHealthItem item) =>
        string.IsNullOrWhiteSpace(item.Name) ? item.PointId : item.Name!;
}
