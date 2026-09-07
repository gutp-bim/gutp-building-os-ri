using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using BuildingOS.Shared.Domain.Configuration;
using BuildingOS.Shared.Infrastructure.Configuration;
using BuildingOS.Shared.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Mvc;

namespace BuildingOs.ApiServer.Controllers;

/// <summary>
/// プラットフォーム運用者向けの簡易モニタリングエンドポイント。
/// 各サービスの up/down と主要 KPI を1本に集約して返す。KPI は Prometheus HTTP API 経由で
/// 取得するため、Grafana を起動していなくても利用できる（Prometheus 未配線時は graceful degrade）。
/// </summary>
[ApiController]
[Route("api/system")]
[AuthorizeFilter]
public class SystemController : ControllerBase
{
    private readonly ISystemStatusService _statusService;
    private readonly IEffectiveConfigService _configService;
    private readonly IIngressRejectionStatsService _ingressRejectionStats;

    public SystemController(
        ISystemStatusService statusService,
        IEffectiveConfigService configService,
        IIngressRejectionStatsService ingressRejectionStats)
    {
        _statusService = statusService;
        _configService = configService;
        _ingressRejectionStats = ingressRejectionStats;
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

        var status = await _statusService.GetStatusAsync(ct).ConfigureAwait(false);
        return Ok(status);
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
}
