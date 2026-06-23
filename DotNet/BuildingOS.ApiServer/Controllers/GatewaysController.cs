using System.Text.Json;
using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using BuildingOs.ApiServer.GatewayProvisioning;
using BuildingOS.Shared.Domain.AdminAudit;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.ControlRouting;
using Microsoft.AspNetCore.Mvc;

namespace BuildingOs.ApiServer.Controllers;

/// <summary>
/// ゲートウェイ管理（#323）。ツイン上のゲートウェイを列挙し、binding/接続設定（シークレットはマスク）と
/// pointlist 同期状態（件数 + 内容ハッシュ revision）を**読み取り専用**で観測する（binding/設定は GitOps
/// 正本のため編集不可、#147 と同方針）。唯一の mutating 操作は pointlist 再同期の push 通知（監査必須）。
/// トラストアンカーは mTLS クライアント証明書（OIDC クライアントの secret とは別系統）。管理者のみ。
/// </summary>
[ApiController]
[Route("api/admin/gateways")]
[AuthorizeFilter]
public class GatewaysController : ControllerBase
{
    private readonly IDigitalTwinDatabase _twin;
    private readonly IGatewayConnectionRegistry _registry;
    private readonly IPointListUpdatePublisher _publisher;
    private readonly IAdminAuditRecorder _audit;
    private readonly ILogger<GatewaysController> _logger;

    public GatewaysController(
        IDigitalTwinDatabase twin,
        IGatewayConnectionRegistry registry,
        IPointListUpdatePublisher publisher,
        IAdminAuditRecorder audit,
        ILogger<GatewaysController> logger)
    {
        _twin = twin;
        _registry = registry;
        _publisher = publisher;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>ゲートウェイ一覧（binding + マスク済み設定 + pointlist 件数/revision）。管理者のみ。</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<GatewayAdminView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!IsAdmin()) return Forbid();

        var ids = await _twin.ListGatewayIds().ConfigureAwait(false);
        var views = new List<GatewayAdminView>(ids.Length);
        foreach (var id in ids)
        {
            views.Add(await BuildViewAsync(id).ConfigureAwait(false));
        }
        return Ok(views);
    }

    /// <summary>ゲートウェイ詳細。ツインに存在しない id は 404。管理者のみ。</summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(GatewayAdminView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        if (!IsAdmin()) return Forbid();

        var ids = await _twin.ListGatewayIds().ConfigureAwait(false);
        if (!ids.Contains(id, StringComparer.Ordinal)) return NotFound();
        return Ok(await BuildViewAsync(id).ConfigureAwait(false));
    }

    /// <summary>
    /// pointlist の再同期 push 通知を送る（<c>building-os.pointlist.updated.gw.{id}</c>）。ゲートウェイは
    /// ETag で再検証する（push は最適化、ETag ポーリングが信頼性のバックストップ）。監査必須。管理者のみ。
    /// </summary>
    [HttpPost("{id}/resync-pointlist")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResyncPointList(string id, CancellationToken ct)
    {
        if (!IsAdmin()) return Forbid();
        var auth = HttpContext.GetAuthorizationContext();

        var ids = await _twin.ListGatewayIds().ConfigureAwait(false);
        if (!ids.Contains(id, StringComparer.Ordinal)) return NotFound();

        try
        {
            var entries = await _twin.ListGatewayPointList(id).ConfigureAwait(false);
            var revision = PointListEtag.Compute(entries);
            await _publisher.PublishAsync(id, revision, ct).ConfigureAwait(false);
            await AuditAsync(auth, "resync-pointlist", id, AdminAuditResult.Success,
                new { revision, pointCount = entries.Length }, ct).ConfigureAwait(false);
            return Accepted(new { gatewayId = id, revision });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resync pointlist for gateway {GatewayId}", id);
            await AuditAsync(auth, "resync-pointlist", id, AdminAuditResult.Failure,
                new { error = ex.Message }, ct).ConfigureAwait(false);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private bool IsAdmin() => HttpContext.GetAuthorizationContext().IsAdmin;

    private async Task<GatewayAdminView> BuildViewAsync(string id)
    {
        var connection = _registry.Resolve(id);
        var entries = await _twin.ListGatewayPointList(id).ConfigureAwait(false);
        var maskedSettings = connection is null
            ? new Dictionary<string, string>()
            : GatewaySettingsMasker.Mask(connection.Settings);

        return new GatewayAdminView(
            id,
            connection?.BindingType ?? "(unconfigured)",
            maskedSettings,
            entries.Length,
            PointListEtag.Compute(entries),
            // Identity is bound to the mTLS client certificate (X-Gateway-Id) at the ingress, not a
            // Keycloak secret. Live cert expiry lives in cert-manager and is out of this surface.
            CertTrustAnchor: "mTLS client certificate (X-Gateway-Id)");
    }

    private Task AuditAsync(
        BuildingOS.Shared.Domain.Authorization.AuthorizationContext auth,
        string action, string targetId, AdminAuditResult result, object? detail, CancellationToken ct)
    {
        var detailJson = detail is null ? null : JsonSerializer.Serialize(detail);
        var record = AdminAuditRecord.Create(
            AdminAuditSubjects.Gateway, action, targetId, auth.UserId, actorName: null, result, detailJson);
        return _audit.RecordAsync(record, ct);
    }
}

/// <summary>Admin view of one gateway: binding + masked settings + pointlist sync status (#323).</summary>
public sealed record GatewayAdminView(
    string GatewayId,
    string BindingType,
    IReadOnlyDictionary<string, string> Settings,
    int PointCount,
    string Revision,
    string CertTrustAnchor);
