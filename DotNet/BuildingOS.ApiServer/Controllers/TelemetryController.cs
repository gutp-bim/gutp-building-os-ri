using BuildingOS.Shared;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using Microsoft.AspNetCore.Mvc;
using AuthorizationService = BuildingOS.Shared.Domain.Authorization.IAuthorizationService;

namespace BuildingOs.ApiServer.Controllers;

[ApiController]
[Route("/telemetries")]
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
    /// [非推奨] 最新のテレメトリデータを取得（Hot 層 直接）。正本は <c>GET /telemetries/query?latest=true</c>。
    /// この per-tier エンドポイントは後方互換のため残置。
    /// </summary>
    /// <param name="pointId">必須. ポイントID</param>
    /// <returns>最新のテレメトリデータ</returns>
    [Obsolete("Use GET /telemetries/query?latest=true (canonical, auto tier-selection). Retained for backward compatibility.")]
    [HttpGet("hot")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ValidTelemetryData[]>> GetHot([FromQuery] string pointId, CancellationToken ct)
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
        return Ok(result);
    }

    /// <summary>
    /// [非推奨] 指定期間の Warm テレメトリデータを取得（Warm 層 直接）。正本は <c>GET /telemetries/query</c>（tier 自動選択）。
    /// </summary>
    [Obsolete("Use GET /telemetries/query (canonical, auto tier-selection). Retained for backward compatibility.")]
    [HttpGet("warm")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ValidTelemetryData>> GetWarm(
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
        return Ok(result);
    }

    /// <summary>
    /// [非推奨] 指定期間の Cold テレメトリデータを取得（Cold 層 直接）。正本は <c>GET /telemetries/query</c>（tier 自動選択）。
    /// </summary>
    [Obsolete("Use GET /telemetries/query (canonical, auto tier-selection). Retained for backward compatibility.")]
    [HttpGet("cold")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ValidTelemetryData[]>> GetCold(
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
        return Ok(result);
    }

    /// <summary>
    /// [非推奨] 指定期間の Cold テレメトリデータを取得（複数ポイント, Cold 層 直接）。正本は <c>GET /telemetries/query</c>。
    /// </summary>
    [Obsolete("Use GET /telemetries/query (canonical, auto tier-selection). Retained for backward compatibility.")]
    [HttpGet("cold-multi-point")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Dictionary<string, ValidTelemetryData[]>>> GetColdMultiPoint(
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
        return Ok(result);
    }

    /// <summary>
    /// テレメトリ取得の正本エンドポイント。期間・粒度・latest を指定し、tier（hot/warm/cold/集計）を
    /// 自動選択する。per-tier の <c>/hot</c>・<c>/warm</c>・<c>/cold</c>・<c>/cold-multi-point</c> は非推奨。
    /// </summary>
    /// <param name="pointId">必須. ポイントID</param>
    /// <param name="start">開始時刻（latest=true の場合は不要）</param>
    /// <param name="end">終了時刻（latest=true の場合は不要）</param>
    /// <param name="granularity">集計粒度: raw / minute / hour / day（省略時: raw）</param>
    /// <param name="latest">true の場合は最新値のみ返す</param>
    [HttpGet("query")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ValidTelemetryData[]>> Query(
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

        var result = await telemetryQueryRouter.QueryAsync(
            new TelemetryQueryRequest(pointId, start, end, granularity, latest), ct);

        Response.Headers["Cache-Control"] = "max-age=60";
        return Ok(result);
    }

    private async Task<bool> CheckExistPoint(string pointId)
    {
        var point = await digitalTwinDatabase.GetPoint(pointId);
        return point != null;
    }
}
