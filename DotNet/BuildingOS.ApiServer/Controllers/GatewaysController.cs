using System.Globalization;
using System.Text.Json;
using BuildingOS.Shared;
using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using BuildingOs.ApiServer.GatewayProvisioning;
using BuildingOS.Shared.Domain.AdminAudit;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.ControlRouting;
using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.Shared.Infrastructure.Telemetry;
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
    private readonly ITelemetryQueryRouter _telemetry;
    private readonly IGatewayConnectionStatusStore _connectionStatus;
    private readonly ILogger<GatewaysController> _logger;

    public GatewaysController(
        IDigitalTwinDatabase twin,
        IGatewayConnectionRegistry registry,
        IPointListUpdatePublisher publisher,
        IAdminAuditRecorder audit,
        ITelemetryQueryRouter telemetry,
        IGatewayConnectionStatusStore connectionStatus,
        ILogger<GatewaysController> logger)
    {
        _twin = twin;
        _registry = registry;
        _publisher = publisher;
        _audit = audit;
        _telemetry = telemetry;
        _connectionStatus = connectionStatus;
        _logger = logger;
    }

    /// <summary>
    /// Upper bound on how many of a gateway's points are sampled for the last-seen timestamp, to bound
    /// the per-gateway fan-out on the (admin-only, low-frequency) list call. #181 Phase 2.
    /// </summary>
    private const int MaxLastSeenSamplePoints = 500;

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
            views.Add(await BuildViewAsync(id, ct).ConfigureAwait(false));
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
        return Ok(await BuildViewAsync(id, ct).ConfigureAwait(false));
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

    private async Task<GatewayAdminView> BuildViewAsync(string id, CancellationToken ct)
    {
        var connection = _registry.Resolve(id);
        var entries = await _twin.ListGatewayPointList(id).ConfigureAwait(false);
        var maskedSettings = connection is null
            ? new Dictionary<string, string>()
            : GatewaySettingsMasker.Mask(connection.Settings);

        // #230/ADR-0004: live egress connection state from the cross-replica heartbeat (KV, TTL-expired).
        // Distinct from LastTelemetryAt (ingress last-seen): present here = a bridge replica is holding an
        // egress stream for this gateway right now. Best-effort — a KV miss reads as not-connected.
        var connected = await _connectionStatus.GetAsync(id, ct).ConfigureAwait(false) is not null;

        return new GatewayAdminView(
            id,
            connection?.BindingType ?? "(unconfigured)",
            maskedSettings,
            entries.Length,
            PointListEtag.Compute(entries),
            // Identity is bound to the mTLS client certificate (X-Gateway-Id) at the ingress, not a
            // Keycloak secret. Live cert expiry lives in cert-manager and is out of this surface.
            CertTrustAnchor: "mTLS client certificate (X-Gateway-Id)",
            LastTelemetryAt: await LastTelemetryAtAsync(entries, ct).ConfigureAwait(false),
            Connected: connected);
    }

    /// <summary>
    /// The most recent telemetry timestamp across a gateway's points — a **derived** last-seen signal
    /// (#181 Phase 2, option ①), not a live egress connection state. It reuses the same latest-value
    /// read path as <c>batch-latest</c> (Hot KV / Parquet lake, no EF context), so the per-point
    /// fan-out stays concurrent. Returns the max sample <c>Datetime</c> as an ISO-8601 string, or
    /// <c>null</c> when the gateway has no points or none have reported. The point set is capped at
    /// <see cref="MaxLastSeenSamplePoints"/> to bound the fan-out.
    /// True connected/disconnected requires cross-replica egress state (a NATS-KV heartbeat) and is a
    /// follow-up (option ②).
    /// </summary>
    private async Task<string?> LastTelemetryAtAsync(GatewayPointEntry[] entries, CancellationToken ct)
    {
        var pointIds = entries
            .Select(e => e.PointId)
            .Where(pid => !string.IsNullOrEmpty(pid))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxLastSeenSamplePoints)
            .ToArray();
        if (pointIds.Length == 0) return null;

        var timestamps = await Task.WhenAll(pointIds.Select(async pointId =>
        {
            var result = await _telemetry
                .QueryAsync(new TelemetryQueryRequest(pointId, null, null, TelemetryGranularity.Raw, true), ct)
                .ConfigureAwait(false);
            var datetime = result.LastOrDefault()?.Datetime;
            return DateTimeOffset.TryParse(datetime, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var dto)
                ? dto
                : (DateTimeOffset?)null;
        })).ConfigureAwait(false);

        DateTimeOffset? max = null;
        foreach (var ts in timestamps)
        {
            if (ts is { } value && (max is null || value > max)) max = value;
        }
        return max?.ToString("o", CultureInfo.InvariantCulture);
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

/// <summary>
/// Admin view of one gateway: binding + masked settings + pointlist sync status (#323), a derived
/// <see cref="LastTelemetryAt"/> last-seen signal (#181 Phase 2), and the live egress
/// <see cref="Connected"/> state (#230 Phase 2②). <c>LastTelemetryAt</c> is the most recent telemetry
/// timestamp across the gateway's points (ISO-8601), or <c>null</c> when none have reported — it is the
/// ingress last-seen, distinct from <c>Connected</c>. <c>Connected</c> is the cross-replica egress
/// heartbeat (ADR-0004): <c>true</c> when a bridge replica is holding a live egress stream for this
/// gateway right now, <c>false</c> when none is observed (TTL-expired/absent).
/// </summary>
public sealed record GatewayAdminView(
    string GatewayId,
    string BindingType,
    IReadOnlyDictionary<string, string> Settings,
    int PointCount,
    string Revision,
    string CertTrustAnchor,
    string? LastTelemetryAt,
    bool Connected);
