using BuildingOs.ApiServer.Routing;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain;
using BuildingOS.Shared.Domain.PointControl;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.ControlRouting;
using BuildingOS.Shared.Infrastructure.PointControl;
using BuildingOS.Shared.Infrastructure.PointControlRepository;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOs.ApiServer.Authorization;
using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using BuildingOs.ApiServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace BuildingOs.ApiServer.Controllers;

[ApiController]
[Route(ApiRoutes.V1 + "/points")]
[Produces("application/json")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[AuthorizeFilter]
public class PointController(
    IAuthorizedTwinView twinView,
    IControlTypeResolver controlTypeResolver,
    IControlSchemaResolver controlSchemaResolver,
    IControlResultBus controlResultBus,
    IPointControlCommandPublisher commandPublisher,
    IPointControlRepository pointControlRepository,
    IControlAuditWriter auditWriter,
    ControlSafetyOptions controlSafety) : ControllerBase
{
    /// <summary>
    /// ポイント情報の一括取得
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(Point[]), StatusCodes.Status200OK)]
    public async Task<ActionResult<Point[]>> List([FromQuery] string? deviceDtId, CancellationToken ct)
    {
        var auth = HttpContext.GetAuthorizationContext();
        if (string.IsNullOrEmpty(deviceDtId) && !auth.ReadsWholeTwinStructure) return Forbid();
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
        // is the source of truth for type / enum allowed-values / number range. When the schema cannot
        // constrain the value (none resolved, no/unknown data type, unusable enum labels) the write is
        // either sent unvalidated (policy allow, the default) or refused (policy deny, #481); both are
        // counted so an operator can find the incompletely modelled points.
        var schema = await controlSchemaResolver.ResolveAsync(detail.Point, detail.Device).ConfigureAwait(false);
        if (ControlValueValidator.UnusableReason(schema) is { } unusable)
        {
            var deny = controlSafety.OnSchemaResolutionFailure == ControlSchemaFailurePolicy.Deny;
            BuildingOsMetrics.ControlSchemaUnresolved.Add(1,
                new KeyValuePair<string, object?>("reason", unusable),
                new KeyValuePair<string, object?>("policy", deny ? "deny" : "allow"));
            if (deny)
                return BadRequest(new
                {
                    error = "this point has no usable control schema, so the value cannot be validated; " +
                            "writes to it are refused (CONTROL_SCHEMA_FAILURE_POLICY=deny)",
                    reason = unusable,
                });
        }
        if (schema is not null)
        {
            var validation = ControlValueValidator.Validate(schema, request.Value.Value);
            if (!validation.IsValid)
                return BadRequest(new { error = validation.Error, dataType = schema.DataType });
        }

        string? preparedControlId = null;
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
            var controlId = pointControlInfo.id.ToString();
            await controlResultBus.PrepareAsync(controlId, ct).ConfigureAwait(false);
            preparedControlId = controlId;

            // Record the audit row *before* publishing (#333). The result can come back within
            // milliseconds (the in-process simulated handler does), and the result writer updates an
            // existing row by id — inserting afterwards would race and silently drop the outcome.
            // The principal resolved above for the authorization check is carried into the row (#461):
            // without it the trail records what moved the equipment but not who asked for it.
            await auditWriter
                .RecordRequestAsync(pointControlInfo, ControlActor.From(auth.UserId), ct)
                .ConfigureAwait(false);

            var delivery = await commandPublisher.PublishAsync(pointControlInfo, ct).ConfigureAwait(false);
            if (delivery == ControlDeliveryStatus.GatewayOffline)
            {
                await controlResultBus.UnsubscribeAsync(controlId).ConfigureAwait(false);
                preparedControlId = null;
                // The target gateway has no live egress stream → fail fast instead of letting the
                // client wait out the result timeout (#186).
                BuildingOsMetrics.ControlRequests.Add(1,
                    new KeyValuePair<string, object?>("handler", dispatch.ControlType),
                    new KeyValuePair<string, object?>("result", "gateway_offline"));
                // Close the audit row we just opened: nothing will publish a result for a command
                // that was never delivered, so it would otherwise stay "pending" forever.
                // CancellationToken.None: this is cleanup, not request work. If the caller's token is
                // what aborted us, passing it here would cancel the very write meant to close the row.
                await auditWriter.RecordFailureIfPendingAsync(
                    controlId, "target gateway is not currently connected", CancellationToken.None)
                    .ConfigureAwait(false);
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
            if (preparedControlId is not null)
            {
                await controlResultBus.UnsubscribeAsync(preparedControlId).ConfigureAwait(false);
                // Dispatch failed after the audit row was opened (e.g. NATS is down), so close it out
                // rather than leave it pending. Only-if-pending: a connection-level failure does not
                // prove the command was not forwarded, and a real gateway outcome outranks our guess.
                // CancellationToken.None for the same reason as above — this is cleanup.
                await auditWriter
                    .RecordFailureIfPendingAsync(preparedControlId, ex.Message, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// ポイントの制御コマンド履歴（point_control_audit）を新しい順に取得する（#162）。制御実行時に
    /// 記録された監査行を閲覧する。閲覧にはポイントの**読み取り**権限を要求する
    /// （制御=書き込み権限とは別軸で、履歴の閲覧は読み取りで許可する）。管理者は全ポイントを閲覧可。
    /// <para>
    /// 期間とページング（#478）: <c>start</c>（含む）/ <c>end</c>（含まない）で <c>createdAt</c> を絞れる。
    /// 続きがある場合は応答ヘッダ <c>X-Next-Cursor</c> を返すので、同じ条件に <c>cursor</c> として渡すと
    /// 次の（より古い）ページが得られる。ヘッダが無ければ最後のページ。カーソルは行の位置
    /// （createdAt, controlId）なので、取得中に新しい制御が記録されてもページはずれない。
    /// </para>
    /// </summary>
    /// <param name="pointId">ポイントID</param>
    /// <param name="limit">1 ページの件数（1〜200、既定 50）</param>
    /// <param name="start">この時刻以降（含む）の行だけを返す</param>
    /// <param name="end">この時刻より前（含まない）の行だけを返す</param>
    /// <param name="cursor">前ページの <c>X-Next-Cursor</c> の値</param>
    /// <param name="ct">キャンセル</param>
    [HttpGet]
    [Route("{pointId}/control-audit")]
    [ProducesResponseType(typeof(PointControlAuditResponse[]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PointControlAuditResponse[]>> ControlAudit(
        string pointId,
        [FromQuery] int limit = 50,
        [FromQuery] DateTime? start = null,
        [FromQuery] DateTime? end = null,
        [FromQuery] string? cursor = null,
        CancellationToken ct = default)
    {
        start = AsUtc(start);
        end = AsUtc(end);
        if (start is { } s && end is { } e && e < s)
            return BadRequest(new { error = "end must be greater than or equal to start" });
        ControlAuditCursor? after = null;
        if (cursor is not null && (after = ControlAuditCursor.TryDecode(cursor)) is null)
            return BadRequest(new { error = "cursor is not a value returned in X-Next-Cursor" });

        var auth = HttpContext.GetAuthorizationContext();
        var decodedPointId = Uri.UnescapeDataString(pointId);

        // A group-manager reads twin structure, never values, and control history is a value (#506).
        // Refused here explicitly rather than left to whatever GetPointAsync decides for that role.
        if (!auth.IsAdmin && auth.IsGroupManager)
            return Forbid();

        // Read-authorization: the history reveals control activity on the point, so gate it on read
        // access to the point itself — the same check as GET /api/v1/points/{id} (admin bypasses via twinView).
        switch (await twinView.GetPointAsync(auth, decodedPointId, ct).ConfigureAwait(false))
        {
            case TwinGetResult<Point>.Ok: break;
            case TwinGetResult<Point>.Forbidden: return Forbid();
            case TwinGetResult<Point>.NotFound: return NotFound();
            default: throw new UnreachableException();
        }

        var capped = Math.Clamp(limit, 1, MaxControlAuditPage);
        // Read one row past the page: its presence is how we know a next page exists.
        var entries = await pointControlRepository
            .ListAuditByPointAsync(new ControlAuditQuery(decodedPointId, capped + 1, start, end, after), ct)
            .ConfigureAwait(false);
        var page = entries.Take(capped).ToList();
        if (entries.Count > capped)
        {
            var last = page[^1];
            Response.Headers[ControlAuditNextCursorHeader] = new ControlAuditCursor(last.CreatedAt, last.Id).Encode();
        }
        return page.Select(PointControlAuditResponse.From).ToArray();
    }

    /// <summary>Response header carrying the cursor of the next control-audit page (#478).</summary>
    public const string ControlAuditNextCursorHeader = "X-Next-Cursor";

    private const int MaxControlAuditPage = 200;

    // Model binding yields a Local-kind DateTime for "…Z" / "+09:00" (Npgsql refuses Local for
    // timestamptz); a value without an offset is taken as UTC, matching the stored createdAt.
    private static DateTime? AsUtc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Unspecified } v => DateTime.SpecifyKind(v, DateTimeKind.Utc),
        { } v => v.ToUniversalTime(),
    };

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
/// `ActorSub` / `ActorName` は制御を実行した principal（#461）で、`admin_audit` と同じ形。
/// </summary>
/// <param name="ControlId">制御コマンドの id。</param>
/// <param name="PointId">制御対象ポイント。</param>
/// <param name="Request">送信時のコマンド JSON。</param>
/// <param name="Status">"success" / "failed" / "pending"。</param>
/// <param name="CreatedAt">監査行を開いた時刻。</param>
/// <param name="CompletedAt">結果が確定した時刻（未確定なら null）。</param>
/// <param name="ActorSub">制御を実行した principal の識別子（JWT sub）。</param>
/// <param name="ActorName">principal の表示名（無ければ null）。</param>
public sealed record PointControlAuditResponse(
    Guid ControlId,
    string? PointId,
    string Request,
    string Status,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    string ActorSub,
    string? ActorName)
{
    /// <summary>監査エントリをレスポンス DTO に写像する。</summary>
    public static PointControlAuditResponse From(PointControlAuditEntry e) => new(
        e.Id,
        e.PointId,
        e.Request,
        PointControlAuditSerializer.ReadStatus(e.Result),
        e.CreatedAt,
        e.CompletedAt,
        e.ActorSub,
        e.ActorName);
}
