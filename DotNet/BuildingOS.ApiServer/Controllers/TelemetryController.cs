using BuildingOs.ApiServer.Routing;
using BuildingOS.Shared;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using BuildingOs.ApiServer.Telemetry;
using Microsoft.AspNetCore.Mvc;
using AuthorizationService = BuildingOS.Shared.Domain.Authorization.IAuthorizationService;

namespace BuildingOs.ApiServer.Controllers;

[ApiController]
[Route(ApiRoutes.V1 + "/telemetries")]
[ProducesResponseType(StatusCodes.Status200OK)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[AuthorizeFilter]
public class TelemetryController(
    IDigitalTwinDatabase digitalTwinDatabase,
    ITelemetryDatabase telemetryDatabase,
    ITelemetryQueryRouter telemetryQueryRouter,
    AuthorizationService authorizationService)
    : ControllerBase
{
    /// <summary>
    /// [非推奨] 最新のテレメトリデータを取得（Hot 層 直接）。正本は <c>GET /api/v1/telemetries/query?latest=true</c>。
    /// この per-tier エンドポイントは後方互換のため残置。
    /// </summary>
    /// <param name="pointId">必須. ポイントID</param>
    /// <returns>最新のテレメトリデータ</returns>
    [Obsolete("Use GET /telemetries/query?latest=true (canonical, auto tier-selection). Retained for backward compatibility.")]
    [HttpGet("hot")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TelemetryReading>> GetHot([FromQuery] string pointId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(pointId))
        {
            return BadRequest("pointId is required");
        }

        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin)
        {
            var canAccess = await authorizationService.CanAccessAsync(
                authContext, "point", pointId, "read", ct).ConfigureAwait(false);
            if (!canAccess) return Forbid();
        }

        var existPoint = await CheckExistPoint(pointId);
        if (!existPoint) return NotFound();

        var result = await telemetryDatabase.GetHotTelemetry(pointId);
        return Ok(TelemetryReading.From(result));
    }

    /// <summary>
    /// [非推奨] 指定期間の Warm テレメトリデータを取得（Warm 層 直接）。正本は <c>GET /api/v1/telemetries/query</c>（tier 自動選択）。
    /// </summary>
    [Obsolete("Use GET /telemetries/query (canonical, auto tier-selection). Retained for backward compatibility.")]
    [HttpGet("warm")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TelemetryReading[]>> GetWarm(
        [FromQuery] string pointId,
        [FromQuery] DateTime startTime,
        [FromQuery] DateTime endTime,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(pointId))
        {
            return BadRequest("pointId is required");
        }

        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin)
        {
            var canAccess = await authorizationService.CanAccessAsync(
                authContext, "point", pointId, "read", ct).ConfigureAwait(false);
            if (!canAccess) return Forbid();
        }

        var existPoint = await CheckExistPoint(pointId);
        if (!existPoint) return NotFound();

        if (endTime < startTime)
        {
            return BadRequest("endTime must be greater than or equal to startTime");
        }

        var result = await telemetryDatabase.GetWarmTelemetries(pointId, startTime, endTime);
        return Ok(TelemetryReading.From(result));
    }

    /// <summary>
    /// [非推奨] 指定期間の Cold テレメトリデータを取得（Cold 層 直接）。正本は <c>GET /api/v1/telemetries/query</c>（tier 自動選択）。
    /// </summary>
    [Obsolete("Use GET /telemetries/query (canonical, auto tier-selection). Retained for backward compatibility.")]
    [HttpGet("cold")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TelemetryReading[]>> GetCold(
        [FromQuery] string pointId,
        [FromQuery] DateTime startTime,
        [FromQuery] DateTime endTime,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(pointId))
        {
            return BadRequest("pointId is required");
        }

        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin)
        {
            var canAccess = await authorizationService.CanAccessAsync(
                authContext, "point", pointId, "read", ct).ConfigureAwait(false);
            if (!canAccess) return Forbid();
        }

        var existPoint = await CheckExistPoint(pointId);
        if (!existPoint) return NotFound();

        if (endTime < startTime)
        {
            return BadRequest("endTime must be greater than or equal to startTime");
        }

        var result = await telemetryDatabase.GetColdTelemetries(pointId, startTime, endTime);
        return Ok(TelemetryReading.From(result));
    }

    /// <summary>
    /// [非推奨] 指定期間の Cold テレメトリデータを取得（複数ポイント, Cold 層 直接）。後継は <c>POST /api/v1/telemetries/query/batch</c>（#510）。
    /// </summary>
    [Obsolete("Use POST /telemetries/query/batch (#510: auto tier-selection, per-point authorization). Retained for backward compatibility.")]
    [HttpGet("cold-multi-point")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Dictionary<string, TelemetryReading[]>>> GetColdMultiPoint(
        [FromQuery] string[] pointIds,
        [FromQuery] DateTime startTime,
        [FromQuery] DateTime endTime,
        CancellationToken ct)
    {
        if (pointIds.Length == 0)
        {
            return BadRequest("pointIds is required");
        }

        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin)
        {
            foreach (var pointId in pointIds)
            {
                var canAccess = await authorizationService.CanAccessAsync(
                    authContext, "point", pointId, "read", ct).ConfigureAwait(false);
                if (!canAccess) return Forbid();
            }
        }

        if (endTime < startTime)
        {
            return BadRequest("endTime must be greater than or equal to startTime");
        }

        var result = await telemetryDatabase.GetColdTelemetries(pointIds, startTime, endTime);
        return Ok(result.ToDictionary(kv => kv.Key, kv => TelemetryReading.From(kv.Value)));
    }

    /// <summary>
    /// テレメトリ取得の正本エンドポイント。期間・粒度・latest を指定し、tier（hot/warm/cold/集計）を
    /// 自動選択する。per-tier の <c>/hot</c>・<c>/warm</c>・<c>/cold</c>・<c>/cold-multi-point</c> は非推奨。
    /// 読み取りファイル数の上限（<c>PARQUET_QUERY_MAX_FILES</c>）を超えた場合は新しい側だけの部分応答になり、
    /// 応答ヘッダ <c>X-Partial-Result: true</c> と <c>X-Covered-From</c>（その時刻以降はデータが揃っている、
    /// ISO-8601 UTC）で示す（#499）。それより前は期間を分けて再取得すること。
    /// </summary>
    /// <param name="pointId">必須. ポイントID</param>
    /// <param name="start">開始時刻（latest=true の場合は不要）</param>
    /// <param name="end">終了時刻（latest=true の場合は不要）</param>
    /// <param name="granularity">集計粒度: raw / hour / day（省略時: raw）</param>
    /// <param name="latest">
    /// true の場合は最新値のみ返す。「最新」は twin の可視性でフィルタされた最新行であり、リクエスト
    /// 時刻に近いとは限らない（#417）：ある点が一時的に twin の Point List から外れ、その間も送信元は
    /// publish を続けていた場合、非公開だった期間のテレメトリは保存されない（gateway が point-list miss
    /// として捨てる）。その点を再度公開すると、latest=true は非公開化「前」の保存済み行を返す——
    /// つまりこのレスポンスの <c>datetime</c> が取り込みより古い時刻になり得る。値の新鮮さを判定する
    /// 場合は <c>datetime</c> を現在時刻と比較すること。
    /// </param>
    [HttpGet("query")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TelemetryReading[]>> Query(
        [FromQuery] string pointId,
        [FromQuery] DateTime? start,
        [FromQuery] DateTime? end,
        [FromQuery] TelemetryGranularity granularity = TelemetryGranularity.Raw,
        [FromQuery] bool latest = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(pointId))
            return BadRequest("pointId is required");

        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin)
        {
            var canAccess = await authorizationService.CanAccessAsync(
                authContext, "point", pointId, "read", ct).ConfigureAwait(false);
            if (!canAccess) return Forbid();
        }

        var existPoint = await CheckExistPoint(pointId);
        if (!existPoint) return NotFound();

        if (!latest && end.HasValue && start.HasValue && end.Value < start.Value)
            return BadRequest("end must be greater than or equal to start");

        using var completeness = TelemetryQueryCompleteness.Begin();
        var result = await telemetryQueryRouter.QueryAsync(
            new TelemetryQueryRequest(pointId, start, end, granularity, latest), ct);

        Response.Headers["Cache-Control"] = "max-age=60";
        if (completeness.CoveredFrom is { } coveredFrom)
        {
            // #499: a store dropped the older side of the range (PARQUET_QUERY_MAX_FILES). Say so
            // rather than let a 200 imply the whole range came back; query from coveredFrom onward
            // or split the range to get the rest.
            Response.Headers[TelemetryResponseHeaders.PartialResult] = "true";
            Response.Headers[TelemetryResponseHeaders.CoveredFrom] = coveredFrom.ToString("O");
        }
        return Ok(TelemetryReading.From(result));
    }

    /// <summary>
    /// 24 時間の受信件数を 15 分ごとに数えて返す（#551）。Point 詳細の「受信状況 24h」バー（#457）用で、
    /// 高頻度ポイント（5 秒周期なら 1 日約 17,000 件）でも生データをブラウザへ送らずに 96 個の件数だけを返す。
    /// 区間は <c>end</c> から遡る 24 時間で、古い順・<c>end</c> 基準で揃えた半開区間。受信率への換算
    /// （期待件数 = 900 / 期待周期）はクライアント側で行う。読み取りが打ち切られた場合は
    /// <c>/telemetries/query</c> と同じく <c>X-Partial-Result</c> / <c>X-Covered-From</c> を付ける（#499）。
    /// </summary>
    /// <param name="pointId">必須. ポイントID</param>
    /// <param name="end">窓の終端（UTC）。省略時は現在時刻</param>
    /// <param name="ct">キャンセル</param>
    [HttpGet("coverage")]
    [ProducesResponseType(typeof(TelemetryCoverage), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TelemetryCoverage>> Coverage(
        [FromQuery] string pointId,
        [FromQuery] DateTime? end,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(pointId))
            return BadRequest("pointId is required");

        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin)
        {
            var canAccess = await authorizationService.CanAccessAsync(
                authContext, "point", pointId, "read", ct).ConfigureAwait(false);
            if (!canAccess) return Forbid();
        }

        if (!await CheckExistPoint(pointId).ConfigureAwait(false)) return NotFound();

        var windowEnd = end is { } e
            ? new DateTimeOffset(DateTime.SpecifyKind(e.ToUniversalTime(), DateTimeKind.Utc))
            : DateTimeOffset.UtcNow;
        var window = TimeSpan.FromSeconds(TelemetryCoverage.DefaultBucketSeconds * TelemetryCoverage.DefaultBucketCount);
        if (windowEnd - DateTimeOffset.MinValue < window)
            return BadRequest("end is too early to hold a 24-hour window");
        var windowStart = windowEnd - window;

        using var completeness = TelemetryQueryCompleteness.Begin();
        var rows = await telemetryQueryRouter.QueryAsync(
            new TelemetryQueryRequest(pointId, windowStart.UtcDateTime, windowEnd.UtcDateTime, TelemetryGranularity.Raw, false),
            ct).ConfigureAwait(false);

        if (completeness.CoveredFrom is { } coveredFrom)
        {
            Response.Headers[TelemetryResponseHeaders.PartialResult] = "true";
            Response.Headers[TelemetryResponseHeaders.CoveredFrom] = coveredFrom.ToString("O");
        }
        return Ok(TelemetryCoverage.Count(rows.Select(r => r.Datetime), windowEnd, pointId));
    }

    /// <summary>
    /// 複数ポイントの最新値を1リクエストで取得する（#182）。オペレーターホームの鮮度表示が行っていた
    /// ポイント単位の N+1 fan-out（ブラウザから <c>GET /query?latest=true</c> をポイント数だけ発行）を
    /// サーバー側の1往復に置き換える。各ポイントは read 権限で個別に認可し（admin はバイパス）、
    /// アクセス不可のポイントは結果から除外する（存在を漏らさない）。値が無いポイントは
    /// <c>datetime=null</c> で返る（鮮度判定では「欠測」に分類される）。
    /// </summary>
    [HttpPost("query/batch-latest")]
    [ProducesResponseType(typeof(LatestSample[]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<LatestSample[]>> QueryBatchLatest(
        [FromBody] BatchLatestRequest request,
        CancellationToken ct = default)
    {
        if (request?.PointIds is null || request.PointIds.Length == 0)
            return BadRequest("pointIds is required");

        var ids = request.PointIds
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
            return BadRequest("pointIds is required");
        if (ids.Length > MaxBatchPointIds)
            return BadRequest($"pointIds exceeds the maximum of {MaxBatchPointIds}");

        // Phase 1 — authorization, one point at a time (see AuthorizeReadSequentiallyAsync).
        // Inaccessible points are dropped — omission (not 403) keeps the batch usable for a
        // mixed-permission point set.
        var accessibleIds = await AuthorizeReadSequentiallyAsync(ids, ct).ConfigureAwait(false);

        // Phase 2 — telemetry fan-out. The latest path reads the Hot KV / Parquet lake (not the EF
        // DbContext), so this stays concurrent to keep the batch a single fast round-trip.
        var samples = await Task.WhenAll(accessibleIds.Select(async pointId =>
        {
            var result = await telemetryQueryRouter
                .QueryAsync(new TelemetryQueryRequest(pointId, null, null, TelemetryGranularity.Raw, true), ct)
                .ConfigureAwait(false);
            var latest = result.LastOrDefault();
            // Same rule as TelemetryReading.From: the discriminant describes the union value we
            // ship, derived from it rather than copied off the row.
            var value = TelemetryValueKind.Resolve(latest);
            return new LatestSample(
                pointId, latest?.Datetime, value,
                TelemetryValueKind.KindOf(value), TelemetryValueKind.ResolveState(latest));
        })).ConfigureAwait(false);

        Response.Headers["Cache-Control"] = "max-age=60";
        return Ok(samples);
    }

    /// <summary>
    /// 複数ポイントの期間履歴を1リクエストで取得する（#510）。<c>GET /query</c> の複数ポイント版で、
    /// tier の自動選択・粒度は同じ。認可は batch-latest と同じく各ポイントを read 権限で個別に判定し
    /// （admin はバイパス）、読めないポイントは 403 にせず結果から除外する（存在を漏らさない）。
    /// twin への存在確認は行わない（batch-latest と同じ）。データが無いポイントは <c>readings: []</c>、
    /// twin から削除済みでも保存済みのテレメトリがあり読み取り権限があれば、その履歴を返す。
    /// 1 リクエストの大きさは、ポイント数（最大 <see cref="MaxBatchPointIds"/>）と、集計時は
    /// 「ポイント数 × バケット数」（最大 <see cref="MaxBatchQueryBuckets"/>）、raw 時は
    /// 「ポイント数 × 時間」（最大 <see cref="MaxBatchRawPointHours"/> ポイント時間）で制限し、超えたら 400。
    /// どれかのポイントの読み取りが打ち切られた場合は <c>/query</c> と同じく
    /// <c>X-Partial-Result</c> / <c>X-Covered-From</c> を付ける（#499、全ポイントで揃っている時刻）。
    /// 非推奨の <c>/cold-multi-point</c> の後継。
    /// </summary>
    [HttpPost("query/batch")]
    [ProducesResponseType(typeof(TelemetrySeries[]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TelemetrySeries[]>> QueryBatch(
        [FromBody] BatchQueryRequest request,
        CancellationToken ct = default)
    {
        var ids = (request?.PointIds ?? [])
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
            return BadRequest("pointIds is required");
        if (ids.Length > MaxBatchPointIds)
            return BadRequest($"pointIds exceeds the maximum of {MaxBatchPointIds}");
        if (request!.Start is not { } start || request.End is not { } end)
            return BadRequest("start and end are required");
        if (end < start)
            return BadRequest("end must be greater than or equal to start");
        // A numeric value the enum does not define would match neither budget below, and the router
        // reads anything that is not hour/day as raw — so it would scan raw with no budget at all.
        if (!Enum.IsDefined(request.Granularity))
            return BadRequest("granularity must be raw, hour or day");

        var hours = (end - start).TotalHours;
        switch (request.Granularity)
        {
            case TelemetryGranularity.Raw when ids.Length * hours > MaxBatchRawPointHours:
                return BadRequest(
                    $"a raw batch may cover at most {MaxBatchRawPointHours} point-hours (pointIds × hours); " +
                    "narrow the range, send fewer points, or use granularity=hour/day");
            case TelemetryGranularity.Hour or TelemetryGranularity.Day
                when ids.Length * Math.Ceiling(request.Granularity == TelemetryGranularity.Hour ? hours : hours / 24)
                     > MaxBatchQueryBuckets:
                return BadRequest(
                    $"the batch may return at most {MaxBatchQueryBuckets} buckets (pointIds × buckets in the range); " +
                    "narrow the range, send fewer points, or use a coarser granularity");
        }

        var accessibleIds = await AuthorizeReadSequentiallyAsync(ids, ct).ConfigureAwait(false);

        // The completeness scope is an AsyncLocal opened before any read, so every store call below —
        // the multi-point read or each per-point one — reports into this one scope.
        using var completeness = TelemetryQueryCompleteness.Begin();
        var series = new TelemetrySeries[accessibleIds.Length];
        if (request.Granularity == TelemetryGranularity.Raw
            && telemetryQueryRouter is IMultiPointTelemetryQueryRouter multi)
        {
            // Raw: one pass over the lake for every point, instead of listing and decoding the same
            // objects once per point.
            var byPoint = await multi.QueryRawMultiAsync(accessibleIds, start, end, ct).ConfigureAwait(false);
            for (var i = 0; i < accessibleIds.Length; i++)
                series[i] = new TelemetrySeries(
                    accessibleIds[i], TelemetryReading.From(byPoint.GetValueOrDefault(accessibleIds[i])));
        }
        else
        {
            // Aggregates (and a router without multi-point support): one read per point, bounded rather
            // than one task per point, since each read can scan many lake objects.
            await Parallel.ForEachAsync(
                Enumerable.Range(0, accessibleIds.Length),
                new ParallelOptions { MaxDegreeOfParallelism = MaxBatchQueryConcurrency, CancellationToken = ct },
                async (i, token) =>
                {
                    var rows = await telemetryQueryRouter
                        .QueryAsync(new TelemetryQueryRequest(accessibleIds[i], start, end, request.Granularity, false), token)
                        .ConfigureAwait(false);
                    series[i] = new TelemetrySeries(accessibleIds[i], TelemetryReading.From(rows));
                }).ConfigureAwait(false);
        }

        Response.Headers["Cache-Control"] = "max-age=60";
        if (completeness.CoveredFrom is { } coveredFrom)
        {
            Response.Headers[TelemetryResponseHeaders.PartialResult] = "true";
            Response.Headers[TelemetryResponseHeaders.CoveredFrom] = coveredFrom.ToString("O");
        }
        return Ok(series);
    }

    /// <summary>
    /// The points of <paramref name="ids"/> the caller may read, in order. For a non-admin this goes
    /// through the request-scoped EF DbContext (DefaultAuthorizationService → GroupMembershipResolver →
    /// GroupRepository), which is NOT thread-safe — so one point at a time, never Task.WhenAll.
    /// </summary>
    private async Task<string[]> AuthorizeReadSequentiallyAsync(string[] ids, CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();
        if (authContext.IsAdmin) return ids;

        var allowed = new List<string>(ids.Length);
        foreach (var pointId in ids)
        {
            if (await authorizationService
                    .CanAccessAsync(authContext, "point", pointId, "read", ct)
                    .ConfigureAwait(false))
                allowed.Add(pointId);
        }
        return allowed.ToArray();
    }

    private async Task<bool> CheckExistPoint(string pointId)
    {
        var point = await digitalTwinDatabase.GetPoint(pointId);
        return point != null;
    }

    /// <summary>Upper bound on the points of one batch-latest / batch request (bounds server-side fan-out). #182 #510</summary>
    public const int MaxBatchPointIds = 500;

    /// <summary>
    /// #510: upper bound on pointIds × buckets for an hour/day batch — 100 meters × a month of hours
    /// (74,400) and 500 points × a year of days (182,500) fit; 500 points × a year of hours does not.
    /// </summary>
    public const int MaxBatchQueryBuckets = 200_000;

    /// <summary>
    /// #510: upper bound on pointIds × hours for a raw batch. A raw read returns every sample (a 5-second
    /// point is ~17,000 rows a day), so this is the budget that keeps one request from scanning the lake
    /// wholesale: e.g. 100 points × 1 day (2,400) or 500 points × 10 hours fit.
    /// </summary>
    public const int MaxBatchRawPointHours = 5_000;

    /// <summary>#510: how many per-point history reads one batch runs at once.</summary>
    public const int MaxBatchQueryConcurrency = 8;
}

/// <summary>Request body for <c>POST /api/v1/telemetries/query/batch</c> (#510).</summary>
/// <param name="PointIds">Points to read; duplicates and blanks are ignored. At most 500.</param>
/// <param name="Start">Range start (inclusive). Required.</param>
/// <param name="End">Range end. Required, and not before <paramref name="Start"/>.</param>
/// <param name="Granularity">raw / hour / day, as in <c>GET /telemetries/query</c>. Defaults to raw.</param>
public sealed record BatchQueryRequest(
    string[] PointIds, DateTime? Start, DateTime? End,
    TelemetryGranularity Granularity = TelemetryGranularity.Raw);

/// <summary>
/// One point's history in a <c>POST /api/v1/telemetries/query/batch</c> response (#510).
/// <c>Readings</c> is empty when the point has no data in the range. Existence in the twin is not
/// checked (as in batch-latest), so a point removed from the twin still returns the history it left.
/// </summary>
public sealed record TelemetrySeries(string PointId, TelemetryReading[] Readings);

/// <summary>Request body for <c>POST /api/v1/telemetries/query/batch-latest</c> (#182).</summary>
public sealed record BatchLatestRequest(string[] PointIds);

/// <summary>
/// One point's latest sample; <c>Datetime</c>/<c>Value</c> are null when it has no data (#182).
/// <para>
/// <c>Value</c> is the union-typed reading (#344) — a number, string, or boolean — described in the
/// OpenAPI document as <c>oneOf</c> by <c>TelemetryValueSchemaFilter</c>. <c>State</c> carries the
/// reading's non-numeric half, and <c>ValueType</c> describes <c>Value</c>; the legacy
/// <c>ValueText</c>/<c>ValueBool</c> pair left the wire in #359. Kept in step with
/// <see cref="BuildingOs.ApiServer.Telemetry.TelemetryReading"/>, whose docs carry the full rationale
/// — the client's value decoder is satisfied structurally by both, so a divergence here surfaces only
/// as a type error in the generated client.
/// Response-only: <c>object?</c> deserializes as a <c>JsonElement</c>, so do not reuse this for input.
/// </para>
/// </summary>
/// <param name="PointId">The point this sample belongs to.</param>
/// <param name="Datetime">ISO-8601 timestamp of the reading; <c>null</c> when the point has no data.</param>
/// <param name="Value">
/// The reading, as a <see cref="double"/>, <see cref="string"/>, <see cref="bool"/>, or <c>null</c>.
/// Widened to <c>oneOf: [number, string, boolean]</c> in the OpenAPI document by
/// <c>TelemetryValueSchemaFilter</c>, which is what makes generated clients see a real union rather
/// than an untyped hole.
/// </param>
/// <param name="ValueType">
/// <c>"number"</c> | <c>"string"</c> | <c>"boolean"</c> — the kind of <paramref name="Value"/>,
/// derived from the value actually shipped rather than copied from the stored tag, so it cannot
/// contradict it. A descriptor, not a lookup key.
/// </param>
/// <param name="State">
/// The reading's <b>non-numeric half</b> — a <see cref="string"/>, a <see cref="bool"/>, or
/// <c>null</c> — independent of any number in <paramref name="Value"/> (#359). Replaced the legacy
/// <c>ValueText</c>/<c>ValueBool</c> pair. A non-numeric reading is repeated here rather than left
/// null, so a client reads the state half with a single lookup instead of falling back to
/// <paramref name="Value"/>. Batch-latest returns raw samples only, so unlike
/// <see cref="BuildingOs.ApiServer.Telemetry.TelemetryReading.State"/> this never carries a state
/// alongside a numeric average — see that type's docs for why the field exists at all.
/// </param>
public sealed record LatestSample(
    string PointId, string? Datetime, object? Value,
    string? ValueType = null, object? State = null);
