using System.Globalization;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.Configuration;
using BuildingOS.Shared.Domain.Health;
using BuildingOS.Shared.Domain.Types;
using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOs.ApiServer.Authorization;

namespace BuildingOs.ApiServer.Health;

/// <summary>The classified ledger of one read, and the state of the last-seen index it was classified against.</summary>
public sealed record PointHealthSnapshot(List<PointHealthItem> Items, PointLastSeenIndexState IndexState);

/// <summary>
/// Builds the classified Point ledger (#452): <b>authorized twin inventory × last-seen index × thresholds ×
/// gateway state</b>, one <see cref="PointHealthItem"/> per point. Shared by the data-health API (one caller's
/// authorized view) and the health-event evaluator (#455, the whole twin as the system), so the two can never
/// disagree about what "stale" means. The classification itself is <see cref="PointHealthClassifier"/>; this only
/// assembles its inputs.
/// </summary>
public sealed class PointHealthLedger(
    IAuthorizedTwinView twinView,
    IPointLastSeenIndex index,
    ISystemSettingsService settings,
    IGatewayConnectionStatusStore gatewayStatus,
    TimeProvider clock)
{
    private readonly IAuthorizedTwinView _twinView = twinView;
    private readonly IPointLastSeenIndex _index = index;
    private readonly ISystemSettingsService _settings = settings;
    private readonly IGatewayConnectionStatusStore _gatewayStatus = gatewayStatus;
    private readonly TimeProvider _clock = clock;

    /// <summary>
    /// 認可された台帳と最終受信インデックスを突き合わせ、判定済みの行を組み立てる。
    /// gateway の接続状態は**リクエスト内で gateway ごとに 1 回だけ**引いて使い回す
    /// （数千 Point ぶん KV を叩かないため）。
    /// </summary>
    public async Task<PointHealthSnapshot> BuildAsync(
        AuthorizationContext auth, string? buildingDtId, CancellationToken ct)
    {
        var thresholds = await _settings.GetTelemetryThresholdsAsync(ct).ConfigureAwait(false);

        // index の状態は 1 リクエスト内で固定する（行ごとに Warming/Ready が混ざらないように）。
        var indexState = _index.State;
        var indexReady = indexState == PointLastSeenIndexState.Ready;
        var now = _clock.GetUtcNow();

        var buildings = await ResolveBuildingsAsync(auth, buildingDtId, ct).ConfigureAwait(false);

        // 1 リクエスト内で gateway ごとに 1 回だけ引いて使い回す（数千 Point ぶん KV を叩かないため）。
        var gatewayStates = new Dictionary<string, GatewayConnectionState>(StringComparer.OrdinalIgnoreCase);
        var items = new List<PointHealthItem>();

        foreach (var building in buildings)
        {
            var details = await _twinView.ListPointDetailsAsync(auth, building.DtId, ct).ConfigureAwait(false);
            foreach (var detail in details)
            {
                var point = detail.Point;
                if (point is null) continue;

                var gatewayId = ResolveGatewayId(detail);
                // gateway を持たない Point は接続軸を見ない。Connected を既定にしておけば、
                // 欠測理由の解決が gateway 断へ falsely 分岐することはない。
                var gatewayState = GatewayConnectionState.Connected;
                if (gatewayId is not null)
                {
                    if (!gatewayStates.TryGetValue(gatewayId, out gatewayState))
                    {
                        gatewayState = (await _gatewayStatus.GetAsync(gatewayId, ct).ConfigureAwait(false)).State;
                        gatewayStates[gatewayId] = gatewayState;
                    }
                }

                var hasEntry = _index.TryGet(point.Id, out var entry);
                var input = new PointHealthInput
                {
                    PointId = point.Id,
                    LastSeen = hasEntry ? entry.LastSeen : null,
                    HasIndexEntry = hasEntry,
                    Value = hasEntry ? ResolveNumericValue(entry, point.Scale) : null,
                    // 台帳が持つ期待間隔は Point の sbco:interval だけ（device / gateway 既定は未導入）。
                    Expected = new ExpectedIntervals(Point: point.Interval),
                    Thresholds = new PointAlarmThresholds(
                        ToJsonDouble(point.AlarmHigh), ToJsonDouble(point.AlarmLow),
                        ToJsonDouble(point.WarnHigh), ToJsonDouble(point.WarnLow)),
                    GatewayId = gatewayId,
                    GatewayState = gatewayState,
                };

                var result = PointHealthClassifier.Classify(input, thresholds, indexReady, now);

                items.Add(new PointHealthItem
                {
                    PointId = point.Id,
                    PointDtId = point.DtId,
                    Name = point.Name,
                    Unit = point.Unit,
                    Freshness = result.Freshness,
                    Alarm = result.Alarm,
                    Gateway = gatewayId is null
                        ? null
                        : new PointGatewayInfo(gatewayId, gatewayState.ToConnectedFlag()),
                    HealthStatus = result.HealthStatus,
                    DeviceDtId = detail.Device?.DtId,
                    DeviceName = detail.Device?.Name,
                    SpaceDtId = detail.Space?.DtId,
                    SpaceName = detail.Space?.Name,
                    FloorDtId = detail.Floor?.DtId,
                    FloorName = detail.Floor?.Name,
                    BuildingDtId = building.DtId,
                    // 建物名は PointDetail 側（Device.BuildingName）が正本。無ければ列挙した建物の名前。
                    BuildingName = detail.Device?.BuildingName ?? building.Name,
                    Tags = point.CustomTags
                        .Where(t => t.Value)
                        .Select(t => t.Key)
                        .OrderBy(t => t, StringComparer.Ordinal)
                        .ToArray(),
                });
            }
        }

        return new PointHealthSnapshot(items, indexState);
    }

    /// <summary>
    /// 読む対象の建物。スコープ指定があればそれ 1 棟だけ（認可は
    /// <see cref="IAuthorizedTwinView.ListPointDetailsAsync"/> 側が効かせる）、無ければ認可済みの全建物。
    /// </summary>
    private async Task<IReadOnlyList<(string DtId, string? Name)>> ResolveBuildingsAsync(
        AuthorizationContext auth, string? buildingDtId, CancellationToken ct)
    {
        // #506: data health is value-derived, and a group-manager reads no values. Answer empty up front
        // rather than walking every building — its structural bypass lists them all — only for each
        // ledger read to come back empty.
        if (auth.IsStructureOnly)
            return [];

        if (!string.IsNullOrWhiteSpace(buildingDtId))
            return [(buildingDtId, null)];

        var buildings = await _twinView.ListBuildingsAsync(auth, ct).ConfigureAwait(false);
        return buildings.Select(b => (b.DtId, (string?)b.Name)).ToArray();
    }

    /// <summary>Point の gatewayId（sbco:gatewayId）。無ければ所属機器のものへフォールバックする。</summary>
    private static string? ResolveGatewayId(PointDetail detail)
    {
        if (!string.IsNullOrWhiteSpace(detail.Point?.GatewayName)) return detail.Point.GatewayName;
        return string.IsNullOrWhiteSpace(detail.Device?.GatewayId) ? null : detail.Device!.GatewayId;
    }

    /// <summary>
    /// 警報判定に使う数値を決める。**数値以外は数値として扱わない** — `ValueType` が
    /// `string` / `boolean` の読み取りに古い数値が同居していることがあり（ADR-0006 の判別子）、
    /// それで閾値判定をすると「読み取りではない数」で警報が出る。フロントの
    /// `freshness.ts` の `PointLastSeen.value` も同じ規則で null に倒している。
    ///
    /// <para>数値のときは <c>sbco:scale</c> を掛けて**工学単位**に直す。KV に載るのは生値で、
    /// `bos:alarmHigh` などの閾値は工学単位で書かれているため、掛けないと桁がずれたまま比較して
    /// しまう（scale=0.1 の Point で生値 250 が「25.0 ℃」ではなく 250 として critical になる）。
    /// Point 詳細の表示も同じ換算をしているので、画面と判定がここで一致する。</para>
    /// </summary>
    private static double? ResolveNumericValue(PointLastSeenEntry entry, float? scale)
    {
        if (entry.Value is not { } raw) return null;
        if (!string.IsNullOrEmpty(entry.ValueType)
            && !string.Equals(entry.ValueType, "number", StringComparison.OrdinalIgnoreCase))
            return null;

        // scale も閾値と同じで、float を素直に広げると 0.1f が 0.10000000149… になる。
        // 画面は JSON 経由の正確な 0.1 を掛けるので、ここも往復変換で揃える。
        if (ToJsonDouble(scale) is not { } s || s == 1d) return raw;
        return RoundToSignificantDigits(raw * s, 12);
    }

    /// <summary>
    /// scale を掛けた後の 2 進浮動小数点の端数を落とす（250 × 0.1 = 25.000000000000004 → 25）。
    /// web-client の <c>applyScale</c> が <c>toPrecision(12)</c> でやっているのと同じ丸め。
    /// </summary>
    private static double RoundToSignificantDigits(double value, int digits)
    {
        if (value == 0 || !double.IsFinite(value)) return value;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Abs(value))) + 1);
        return magnitude * Math.Round(value / magnitude, digits, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// 台帳の <c>float</c> を、**JSON を経由したときと同じ <c>double</c>** に直す。
    /// 単純な暗黙変換だと <c>0.1f</c> が <c>0.10000000149011612</c> に広がり、同じ値を JSON で
    /// 受け取るフロント（`alarm.ts` / Point 詳細）は正確な <c>0.1</c> を使うので、境界ちょうどの
    /// 判定や scale 換算が画面とサーバで割れる。閾値の境界は inclusive という約束を守るための往復変換。
    /// </summary>
    private static double? ToJsonDouble(float? value) =>
        value is { } v && float.IsFinite(v)
            ? double.Parse(v.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
            : null;
}
