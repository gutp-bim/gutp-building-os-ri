using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;
using BuildingOs.ApiServer.Routing;
using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using BuildingOS.Shared.Domain.Configuration;
using BuildingOS.Shared.Infrastructure.Configuration;
using BuildingOS.Shared.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOs.ApiServer.Controllers;

/// <summary>
/// プラットフォーム運用者向けの簡易モニタリングエンドポイント。
/// 各サービスの up/down と主要 KPI を1本に集約して返す。KPI は Prometheus HTTP API 経由で
/// 取得するため、Grafana を起動していなくても利用できる（Prometheus 未配線時は graceful degrade）。
/// </summary>
[ApiController]
[Route(ApiRoutes.V1 + "/system")]
[AuthorizeFilter]
public class SystemController : ControllerBase
{
    private readonly ISystemStatusService _statusService;
    private readonly IEffectiveConfigService _configService;
    private readonly IIngressRejectionStatsService _ingressRejectionStats;
    private readonly ISystemSettingsService _settings;
    private readonly ILogger<SystemController> _logger;
    private readonly ILakePartitionKeyChanges? _lakeKeys;

    public SystemController(
        ISystemStatusService statusService,
        IEffectiveConfigService configService,
        IIngressRejectionStatsService ingressRejectionStats,
        ISystemSettingsService settings,
        ILogger<SystemController>? logger = null,
        ILakePartitionKeyChanges? lakeKeys = null)
    {
        _lakeKeys = lakeKeys;
        _statusService = statusService;
        _configService = configService;
        _ingressRejectionStats = ingressRejectionStats;
        _settings = settings;
        _logger = logger ?? NullLogger<SystemController>.Instance;
    }

    /// <summary>
    /// プラットフォーム稼働状態（サービス up/down + KPI）を取得する。管理者（platform ロール）のみ。
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(SystemStatus), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin)
        {
            return Forbid();
        }

        // Thresholds first: the Parquet flush-stall window is derived from the freshness threshold.
        var thresholds = await GetThresholdsOrDefaultAsync(ct).ConfigureAwait(false);
        var status = await _statusService.GetStatusAsync(thresholds, ct).ConfigureAwait(false);
        return Ok(status with { Thresholds = thresholds });
    }

    /// <summary>
    /// パイプライン KPI の警告閾値（#456, /platform/settings で編集可）。設定ストア（PostgreSQL）が
    /// 落ちていても稼働状態画面は壊さない — 障害時こそ開かれる画面なので、既定値に縮退する。
    /// </summary>
    private async Task<PipelineKpiThresholds> GetThresholdsOrDefaultAsync(CancellationToken ct)
    {
        try
        {
            return await _settings.GetPipelineKpiThresholdsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Pipeline KPI thresholds unavailable, falling back to registry defaults");
            return PipelineKpiThresholds.Defaults;
        }
    }

    /// <summary>
    /// 実効設定（許可リストのキーのみ、シークレットはマスク）を読み取り専用で取得する。管理者のみ。
    /// IaC/ArgoCD が source of truth であり、本エンドポイントは観測専用（編集不可）。
    /// </summary>
    [HttpGet("config")]
    [ProducesResponseType(typeof(EffectiveConfig), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult GetConfig()
    {
        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin)
        {
            return Forbid();
        }

        return Ok(_configService.GetEffectiveConfig());
    }

    /// <summary>
    /// gRPC gateway-ingress の受理/拒否ポリシー（#292）による拒否件数を理由別に取得する。管理者のみ。
    /// Prometheus 未配線時は graceful degrade（<see cref="IngressRejectionStats.MetricsAvailable"/>
    /// が false、件数は空）。
    /// </summary>
    [HttpGet("ingress-rejections")]
    [ProducesResponseType(typeof(IngressRejectionStats), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetIngressRejections(CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin)
        {
            return Forbid();
        }

        var stats = await _ingressRejectionStats.GetAsync(ct).ConfigureAwait(false);
        return Ok(stats);
    }

    /// <summary>
    /// Parquet レイクのパーティションキー（建物）が「今」変わったと記録する（#527）。レイクの読み取りは
    /// Point の建物を学習して走査する建物を絞るが（#273）、記録した時刻（+1 時間の猶予）より前から
    /// 始まる期間の読み取りは、以後絞り込まない（その前の行は別の建物の下にありうるため）。記録は
    /// レイクのバケットに置くので、すべての API Server レプリカに 1 分以内に効く。twin の取り込みを
    /// 適用したときと、読み取りが 1 つの Point を 2 つの建物で見つけたときは自動で記録される。
    /// #527 への更新時など、twin の外でキーが変わったときに実行する。管理者のみ。
    /// </summary>
    [HttpPost("lake/point-buildings/reset")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ResetLakePointBuildings(CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin)
        {
            return Forbid();
        }
        if (_lakeKeys is null)
        {
            return Conflict(new { error = "the telemetry lake (MinIO) is not configured" });
        }

        await _lakeKeys.MarkChangedAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Lake partition-key change recorded by {UserId}", authContext.UserId);
        return NoContent();
    }
}
