using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain;
using BuildingOS.Shared.Domain.PointControl;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.ControlRouting;
using BuildingOS.Shared.Infrastructure.PointControl;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOs.ApiServer.Authorization;
using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using Microsoft.AspNetCore.Mvc;

namespace BuildingOs.ApiServer.Controllers;

[ApiController]
[Route("/points")]
[Produces("application/json")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[AuthorizeFilter]
public class PointController(
    IAuthorizedTwinView twinView,
    IControlTypeResolver controlTypeResolver,
    IControlSchemaResolver controlSchemaResolver,
    IPointControlCommandPublisher commandPublisher,
    IPointControlRepository pointControlRepository) : ControllerBase
{
    /// <summary>
    /// ポイント情報の一括取得
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(Point[]), StatusCodes.Status200OK)]
    public async Task<ActionResult<Point[]>> List([FromQuery] string? deviceDtId, CancellationToken ct)
    {
        var auth = HttpContext.GetAuthorizationContext();
        if (string.IsNullOrEmpty(deviceDtId) && !auth.IsAdmin) return Forbid();
        return await twinView.ListPointsAsync(auth, deviceDtId, ct);
    }

    /// <summary>
    /// ポイント情報の取得
    /// </summary>
    [HttpGet]
    [Route("{pointId}")]
    [ProducesResponseType(typeof(Point), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Point>> Get(string pointId, CancellationToken ct)
        => await twinView.GetPointAsync(HttpContext.GetAuthorizationContext(), Uri.UnescapeDataString(pointId), ct) switch
        {
            TwinGetResult<Point>.Ok ok      => ok.Resource,
            TwinGetResult<Point>.Forbidden  => Forbid(),
            TwinGetResult<Point>.NotFound   => NotFound(),
            _                               => throw new UnreachableException()
        };

    /// <summary>
    /// 機器制御コマンドを送信する。
    /// 結果は gRPC PointControlService.WaitForResult ストリームで通知される。
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ControlAcceptedResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Route("{pointId}/control")]
    public async Task<ActionResult> Control(string pointId, [FromBody] PointControlRequest request, CancellationToken ct)
    {
        if (request?.Value is null)
            return BadRequest(new { error = "value is required" });

        var auth = HttpContext.GetAuthorizationContext();
        var decodedPointId = Uri.UnescapeDataString(pointId);
        if (!await twinView.CanWritePointAsync(auth, decodedPointId, ct).ConfigureAwait(false))
            return Forbid();

        PointDetail detail;
        switch (await twinView.GetPointDetailAsync(auth, decodedPointId, ct))
        {
            case TwinGetResult<PointDetail>.Ok ok:    detail = ok.Resource; break;
            case TwinGetResult<PointDetail>.Forbidden: return Forbid();
            case TwinGetResult<PointDetail>.NotFound:  return NotFound();
            default: throw new UnreachableException();
        }

        // Resolve the egress ControlType + Body from the point's gateway binding type
        // (replaces the previous hard-coded Hono). null = this point cannot be controlled
        // (not writable, or the gateway's binding type is unsupported / not API-wired).
        var dispatch = controlTypeResolver.Resolve(detail.Point, detail.Device, request.Value.Value);
        if (dispatch is null)
            return BadRequest(new { error = "this point cannot be controlled with its current gateway/identity configuration" });

        // Input validation against the point's control schema (#153). The schema (from the point list)
        // is the source of truth for type / enum allowed-values / number range. When no schema is
        // resolved the point is unschematized → value validation is skipped (writable gate #139 still
        // governs authorization).
        var schema = await controlSchemaResolver.ResolveAsync(detail.Point, detail.Device).ConfigureAwait(false);
        if (schema is not null)
        {
            var validation = ControlValueValidator.Validate(schema, request.Value.Value);
            if (!validation.IsValid)
                return BadRequest(new { error = validation.Error, dataType = schema.DataType });
        }

        try
        {
            var pointControlInfo = new PointControlInfo
            {
                id = Guid.NewGuid(),
                PointId = decodedPointId,
                Type = dispatch.ControlType,
                Body = dispatch.Body,
                GatewayId = dispatch.GatewayId,
            };

            var delivery = await commandPublisher.PublishAsync(pointControlInfo, ct);
            if (delivery == ControlDeliveryStatus.GatewayOffline)
            {
                // The target gateway has no live egress stream → fail fast instead of letting the
                // client wait out the result timeout (#186).
                BuildingOsMetrics.ControlRequests.Add(1,
                    new KeyValuePair<string, object?>("handler", dispatch.ControlType),
                    new KeyValuePair<string, object?>("result", "gateway_offline"));
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    error = "target gateway is not currently connected",
                    gatewayId = dispatch.GatewayId,
                });
            }

            return Accepted(new ControlAcceptedResponse { ControlId = pointControlInfo.id });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// ポイントの制御コマンド履歴（point_control_audit）を新しい順に取得する（#162）。監査データは
    /// 記録済みだが閲覧画面が無かったギャップを埋める。閲覧にはポイントの**読み取り**権限を要求する
    /// （制御=書き込み権限とは別軸で、履歴の閲覧は読み取りで許可する）。管理者は全ポイントを閲覧可。
    /// </summary>
    [HttpGet]
    [Route("{pointId}/control-audit")]
    [ProducesResponseType(typeof(PointControlAuditResponse[]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PointControlAuditResponse[]>> ControlAudit(
        string pointId, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var auth = HttpContext.GetAuthorizationContext();
        var decodedPointId = Uri.UnescapeDataString(pointId);

        // Read-authorization: the history reveals control activity on the point, so gate it on read
        // access to the point itself — the same check as GET /points/{id} (admin bypasses via twinView).
        switch (await twinView.GetPointAsync(auth, decodedPointId, ct).ConfigureAwait(false))
        {
            case TwinGetResult<Point>.Ok: break;
            case TwinGetResult<Point>.Forbidden: return Forbid();
            case TwinGetResult<Point>.NotFound: return NotFound();
            default: throw new UnreachableException();
        }

        var capped = Math.Clamp(limit, 1, 200);
        var entries = await pointControlRepository
            .ListAuditByPointAsync(decodedPointId, capped, ct)
            .ConfigureAwait(false);
        return entries.Select(PointControlAuditResponse.From).ToArray();
    }

    public class PointControlRequest
    {
        public double? Value { get; set; }
    }

    public class ControlAcceptedResponse
    {
        [Required]
        public Guid ControlId { get; init; }
    }
}

/// <summary>
/// 制御監査履歴の API レスポンス DTO（#162）。`Result` の生 JSON はそのまま露出せず、`Status`
/// （"success" / "failed" / "pending"）に正規化して返す。`Request` は送信時のコマンド JSON。
/// </summary>
public sealed record PointControlAuditResponse(
    Guid ControlId,
    string? PointId,
    string Request,
    string Status,
    DateTime CreatedAt,
    DateTime? CompletedAt)
{
    public static PointControlAuditResponse From(PointControlAuditEntry e) => new(
        e.Id,
        e.PointId,
        e.Request,
        PointControlAuditSerializer.ReadStatus(e.Result),
        e.CreatedAt,
        e.CompletedAt);
}
