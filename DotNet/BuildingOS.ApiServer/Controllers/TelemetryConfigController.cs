using BuildingOs.ApiServer.Filters;
using BuildingOS.Shared.Domain.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace BuildingOs.ApiServer.Controllers;

/// <summary>
/// 鮮度判定（#183）に使う telemetry 閾値だけを全ロールへ公開する read サーフェス。編集は管理者限定の
/// <see cref="SystemConfigController"/>（<c>/api/system/settings</c>）だが、鮮度判定は home / ポイント詳細
/// など**全ロール**の画面で走るため、閾値の実効値（既定 + 管理者 override）を読める非管理者エンドポイントが
/// 要る。ここでは <see cref="TelemetryThresholds"/> の 2 値だけを返し、他の設定は一切漏らさない。
/// </summary>
[ApiController]
[Route("api/telemetry/config")]
[AuthorizeFilter]
public class TelemetryConfigController : ControllerBase
{
    private readonly ISystemSettingsService _settings;

    public TelemetryConfigController(ISystemSettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>鮮度切れ判定の実効閾値（既定値 + 管理者 override）を返す。認証済みなら全ロール可。</summary>
    [HttpGet]
    [ProducesResponseType(typeof(TelemetryThresholds), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var thresholds = await _settings.GetTelemetryThresholdsAsync(ct).ConfigureAwait(false);
        return Ok(thresholds);
    }
}
