using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.Configuration;
using BuildingOS.Shared.Domain.Health;
using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOs.ApiServer.Authorization;
using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using Microsoft.AspNetCore.Mvc;

namespace BuildingOs.ApiServer.Controllers;

/// <summary>データ健全性一覧の応答。<c>Total</c> はページング前の該当件数。</summary>
/// <param name="Items">この頁ぶんの行</param>
/// <param name="Total">絞り込み後・ページング前の件数</param>
/// <param name="Limit">実際に適用した最大件数（1..500 に丸めた後の値）</param>
/// <param name="Offset">実際に適用したオフセット</param>
/// <param name="DataComplete">最終受信インデックスが Ready で、全 Point ぶんの判定材料が揃っていたか</param>
/// <param name="IndexState">インデックスの状態（Warming / Ready / Degraded）</param>
public sealed record PointHealthListResponse(
    IReadOnlyList<PointHealthItem> Items,
    int Total,
    int Limit,
    int Offset,
    bool DataComplete,
    PointLastSeenIndexState IndexState);

/// <summary>データ健全性の軸別集計。<c>Suppressed</c> な警報は警報件数に数えない。</summary>
public sealed record PointHealthSummaryResponse(
    int TotalPoints,
    int Fresh,
    int Stale,
    int Missing,
    int Unknown,
    int AlarmWarn,
    int AlarmCritical,
    bool DataComplete,
    PointLastSeenIndexState IndexState);

/// <summary>
/// データ健全性（#452）: <b>台帳（認可済み） × 最終受信インデックス × 閾値設定 × gateway 接続状態</b>
/// を突き合わせ、3 軸（鮮度 / 警報 / gateway 接続）を判定した行を返す。
///
/// <para>判定そのものは <see cref="PointHealthClassifier"/>、絞り込み・並べ替えは
/// <see cref="PointHealthQueryFilter"/> にあり、ここは**組み立てだけ**を担う。新しいデータストアも
/// 検索エンジンも足さない — 最新値は <see cref="IPointLastSeenIndex"/> の 1 回のメモリ参照で引き、
/// <c>IHotTelemetryStore</c> を Point 件数ぶん叩くことはしない。</para>
/// </summary>
[ApiController]
[Route("api/telemetry/health")]
[Produces("application/json")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[AuthorizeFilter]
public class TelemetryHealthController : ControllerBase
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    private readonly IAuthorizedTwinView _twinView;
    private readonly IPointLastSeenIndex _index;
    private readonly ISystemSettingsService _settings;
    private readonly IGatewayConnectionStatusStore _gatewayStatus;
    private readonly TimeProvider _clock;

    public TelemetryHealthController(
        IAuthorizedTwinView twinView,
        IPointLastSeenIndex index,
        ISystemSettingsService settings,
        IGatewayConnectionStatusStore gatewayStatus,
        TimeProvider clock)
    {
        _twinView = twinView;
        _index = index;
        _settings = settings;
        _gatewayStatus = gatewayStatus;
        _clock = clock;
    }

    /// <summary>
    /// 判定済みの Point 健全性一覧。認可された建物の台帳だけを読む。
    /// </summary>
    /// <param name="buildingDtId">建物 dtId でスコープ。省略時は認可された全建物</param>
    /// <param name="floorDtId">階 dtId で絞り込み（完全一致）</param>
    /// <param name="deviceDtId">機器 dtId で絞り込み（完全一致）</param>
    /// <param name="gatewayId">gateway ID で絞り込み（大小無視）</param>
    /// <param name="freshness">鮮度で絞り込み（Fresh/Stale/Missing/Unknown）。複数指定は OR、未知の値は無視</param>
    /// <param name="alarm">警報で絞り込み（Normal/Warn/Critical/Unknown/Suppressed）。複数指定は OR</param>
    /// <param name="healthStatus">総合ステータスで絞り込み。複数指定は OR</param>
    /// <param name="olderThan">この秒数以上受信が無いものだけ。**未受信の Point は齢 ∞ として必ず含む**</param>
    /// <param name="tag">customTags で絞り込み。複数指定は AND、大小無視</param>
    /// <param name="q">pointId / 名前の部分一致（大小無視）</param>
    /// <param name="sort">並べ替え（worst（既定）/ lastSeen / name）</param>
    /// <param name="limit">最大件数（1..500、既定 100）</param>
    /// <param name="offset">オフセット（既定 0）</param>
    /// <param name="ct">キャンセルトークン</param>
    [HttpGet]
    [ProducesResponseType(typeof(PointHealthListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PointHealthListResponse>> Get(
        [FromQuery] string? buildingDtId,
        [FromQuery] string? floorDtId,
        [FromQuery] string? deviceDtId,
        [FromQuery] string? gatewayId,
        [FromQuery] string[]? freshness,
        [FromQuery] string[]? alarm,
        [FromQuery] string[]? healthStatus,
        [FromQuery] int? olderThan,
        [FromQuery] string[]? tag,
        [FromQuery] string? q,
        [FromQuery] string? sort,
        [FromQuery] int limit = DefaultLimit,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        if (offset < 0) return BadRequest("offset must be >= 0");
        limit = Math.Clamp(limit, 1, MaxLimit);

        var (items, indexState) = await BuildItemsAsync(buildingDtId, ct).ConfigureAwait(false);

        var query = BuildQuery(buildingDtId, floorDtId, deviceDtId, gatewayId, tag, q);
        // 未知の値は 400 にせず素通しする（フロントの表記ゆれで画面が丸ごと落ちるのを避ける）。
        query.Freshness = ParseEnums<FreshnessStatus>(freshness);
        query.Alarm = ParseEnums<AlarmStatus>(alarm);
        query.HealthStatuses = ParseEnums<HealthStatus>(healthStatus);
        query.OlderThanSeconds = olderThan;
        query.Sort = ParseSort(sort);
        query.Limit = limit;
        query.Offset = offset;

        var page = PointHealthQueryFilter.Apply(items, query);
        return Ok(new PointHealthListResponse(
            page.Items, page.Total, limit, offset,
            DataComplete: indexState == PointLastSeenIndexState.Ready, indexState));
    }

    /// <summary>
    /// 一覧と同じ母集団の軸別集計。ヘッダの KPI 用なので、受けるスコープ条件も一覧のうち
    /// 階層・タグ・フリーワードだけに絞ってある（鮮度などで絞った集計は意味を成さない）。
    /// </summary>
    /// <param name="buildingDtId">建物 dtId でスコープ。省略時は認可された全建物</param>
    /// <param name="floorDtId">階 dtId で絞り込み（完全一致）</param>
    /// <param name="deviceDtId">機器 dtId で絞り込み（完全一致）</param>
    /// <param name="gatewayId">gateway ID で絞り込み（大小無視）</param>
    /// <param name="tag">customTags で絞り込み。複数指定は AND、大小無視</param>
    /// <param name="q">pointId / 名前の部分一致（大小無視）</param>
    /// <param name="ct">キャンセルトークン</param>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(PointHealthSummaryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PointHealthSummaryResponse>> Summary(
        [FromQuery] string? buildingDtId,
        [FromQuery] string? floorDtId,
        [FromQuery] string? deviceDtId,
        [FromQuery] string? gatewayId,
        [FromQuery] string[]? tag,
        [FromQuery] string? q,
        CancellationToken ct = default)
    {
        var (items, indexState) = await BuildItemsAsync(buildingDtId, ct).ConfigureAwait(false);

        var query = BuildQuery(buildingDtId, floorDtId, deviceDtId, gatewayId, tag, q);
        query.Limit = int.MaxValue;
        var matched = PointHealthQueryFilter.Apply(items, query).Items;

        return Ok(new PointHealthSummaryResponse(
            TotalPoints: matched.Count,
            Fresh: matched.Count(i => i.Freshness.Status == FreshnessStatus.Fresh),
            Stale: matched.Count(i => i.Freshness.Status == FreshnessStatus.Stale),
            Missing: matched.Count(i => i.Freshness.Status == FreshnessStatus.Missing),
            Unknown: matched.Count(i => i.Freshness.Status == FreshnessStatus.Unknown),
            // Suppressed は「届いていないので判定していない」であって警報ではない。ここで数えると
            // gateway 断のたびに警報件数が跳ね上がる。
            AlarmWarn: matched.Count(i => i.Alarm.Status == AlarmStatus.Warn),
            AlarmCritical: matched.Count(i => i.Alarm.Status == AlarmStatus.Critical),
            DataComplete: indexState == PointLastSeenIndexState.Ready,
            IndexState: indexState));
    }

    /// <summary>
    /// 認可された台帳と最終受信インデックスを突き合わせ、判定済みの行を組み立てる。
    /// gateway の接続状態は**リクエスト内で gateway ごとに 1 回だけ**引いて使い回す
    /// （数千 Point ぶん KV を叩かないため）。
    /// </summary>
    private async Task<(List<PointHealthItem> Items, PointLastSeenIndexState IndexState)> BuildItemsAsync(
        string? buildingDtId, CancellationToken ct)
    {
        var auth = HttpContext.GetAuthorizationContext();
        var thresholds = await _settings.GetTelemetryThresholdsAsync(ct).ConfigureAwait(false);

        // index の状態は 1 リクエスト内で固定する（行ごとに Warming/Ready が混ざらないように）。
        var indexState = _index.State;
        var indexReady = indexState == PointLastSeenIndexState.Ready;
        var now = _clock.GetUtcNow();

        var buildings = await ResolveBuildingsAsync(auth, buildingDtId, ct).ConfigureAwait(false);

        var gatewayConnected = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var items = new List<PointHealthItem>();

        foreach (var building in buildings)
        {
            var details = await _twinView.ListPointDetailsAsync(auth, building.DtId, ct).ConfigureAwait(false);
            foreach (var detail in details)
            {
                var point = detail.Point;
                if (point is null) continue;

                var gatewayId = ResolveGatewayId(detail);
                var connected = true;
                if (gatewayId is not null)
                {
                    if (!gatewayConnected.TryGetValue(gatewayId, out connected))
                    {
                        connected = await _gatewayStatus.GetAsync(gatewayId, ct).ConfigureAwait(false) is not null;
                        gatewayConnected[gatewayId] = connected;
                    }
                }

                var hasEntry = _index.TryGet(point.Id, out var entry);
                var input = new PointHealthInput
                {
                    PointId = point.Id,
                    LastSeen = hasEntry ? entry.LastSeen : null,
                    HasIndexEntry = hasEntry,
                    Value = hasEntry ? entry.Value : null,
                    // 台帳が持つ期待間隔は Point の sbco:interval だけ（device / gateway 既定は未導入）。
                    Expected = new ExpectedIntervals(Point: point.Interval),
                    Thresholds = new PointAlarmThresholds(
                        point.AlarmHigh, point.AlarmLow, point.WarnHigh, point.WarnLow),
                    GatewayId = gatewayId,
                    GatewayConnected = connected,
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
                    Gateway = gatewayId is null ? null : new PointGatewayInfo(gatewayId, connected),
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

        return (items, indexState);
    }

    /// <summary>
    /// 読む対象の建物。スコープ指定があればそれ 1 棟だけ（認可は
    /// <see cref="IAuthorizedTwinView.ListPointDetailsAsync"/> 側が効かせる）、無ければ認可済みの全建物。
    /// </summary>
    private async Task<IReadOnlyList<(string DtId, string? Name)>> ResolveBuildingsAsync(
        AuthorizationContext auth, string? buildingDtId, CancellationToken ct)
    {
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

    private static PointHealthQuery BuildQuery(
        string? buildingDtId, string? floorDtId, string? deviceDtId, string? gatewayId,
        string[]? tag, string? q) => new()
        {
            BuildingDtId = buildingDtId,
            FloorDtId = floorDtId,
            DeviceDtId = deviceDtId,
            GatewayId = gatewayId,
            Tags = (tag ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray(),
            Q = q,
        };

    /// <summary>未知の値は無視する（絞り込み値が 1 つも解釈できなければ「その軸で絞らない」になる）。</summary>
    private static IReadOnlyList<T> ParseEnums<T>(string[]? values) where T : struct, Enum
    {
        if (values is null || values.Length == 0) return [];
        var parsed = new List<T>();
        foreach (var value in values)
        {
            if (Enum.TryParse<T>(value, ignoreCase: true, out var result) && Enum.IsDefined(result))
                parsed.Add(result);
        }
        return parsed;
    }

    private static PointHealthSort ParseSort(string? sort) =>
        Enum.TryParse<PointHealthSort>(sort, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : PointHealthSort.Worst;
}
