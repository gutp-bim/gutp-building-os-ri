using System.Text.Json;
using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using BuildingOs.ApiServer.Health;
using BuildingOs.ApiServer.Routing;
using BuildingOS.Shared.Domain.AdminAudit;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.Health;
using BuildingOS.Shared.Infrastructure.HealthEvents;
using Microsoft.AspNetCore.Mvc;

namespace BuildingOs.ApiServer.Controllers;

/// <summary>ヘルスイベント 1 件（#455）。lifecycle（<c>ClearedAt</c>）と確認応答（<c>AcknowledgedAt</c>）は独立した軸。</summary>
/// <param name="Id">イベント ID</param>
/// <param name="SubjectType">point | gateway</param>
/// <param name="SubjectId">pointId または gatewayId</param>
/// <param name="SubjectName">Point の名前（Gateway は ID と同じ。台帳から引けなければ null）</param>
/// <param name="BuildingName">所属建物の名前（引けなければ null）</param>
/// <param name="Kind">stale | missing | alarm | gateway_offline</param>
/// <param name="Severity">warn | critical</param>
/// <param name="RaisedAt">発生時刻（UTC）</param>
/// <param name="ClearedAt">解消時刻（UTC）。null の間は open</param>
/// <param name="IsOpen">未解消か</param>
/// <param name="AcknowledgedAt">確認応答の時刻（UTC）。未確認なら null。解消後の確認応答もありうる</param>
/// <param name="AcknowledgedBy">確認した人の表示名（無ければ sub）</param>
/// <param name="Detail">発生時のスナップショット（閾値・値・欠測理由・経過秒など）</param>
public sealed record HealthEventResponse(
    Guid Id,
    string SubjectType,
    string SubjectId,
    string? SubjectName,
    string? BuildingName,
    string Kind,
    string Severity,
    DateTime RaisedAt,
    DateTime? ClearedAt,
    bool IsOpen,
    DateTime? AcknowledgedAt,
    string? AcknowledgedBy,
    JsonElement Detail);

/// <summary>ヘルスイベント一覧の応答。<c>Total</c> はページング前の該当件数。</summary>
public sealed record HealthEventListResponse(IReadOnlyList<HealthEventResponse> Items, int Total, int Limit, int Offset);

/// <summary>
/// 永続ヘルスイベント（#455, ADR-0005 Phase 2b）の一覧と確認応答（ACK）。
///
/// <para><b>読める範囲</b>: 管理者は全件。それ以外は**自分が読める Point** のイベントと、その Point を持つ
/// gateway のイベントだけ（読めないものを一覧にも件数にも出さない）。group-manager は値を読まないので空。</para>
/// <para><b>lifecycle と ACK は別クエリ</b>（<c>lifecycle</c> と <c>ack</c>）。open / acked / cleared の三値には潰さない。</para>
/// </summary>
[ApiController]
[Route(ApiRoutes.V1 + "/health/events")]
[Produces("application/json")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[AuthorizeFilter]
public class HealthEventsController(
    IHealthEventStore store,
    PointHealthLedger ledger,
    TimeProvider clock,
    ILogger<HealthEventsController> logger,
    IAdminAuditRecorder? audit = null) : ControllerBase
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    /// <summary>ヘルスイベントの一覧（新しい順）。</summary>
    /// <param name="lifecycle">open（未解消）| cleared（解消済み）。省略時は両方</param>
    /// <param name="ack">acked（確認済み）| unacked（未確認）。省略時は両方。lifecycle とは独立</param>
    /// <param name="kind">stale | missing | alarm | gateway_offline。複数指定は OR</param>
    /// <param name="subjectType">point | gateway</param>
    /// <param name="subjectId">pointId / gatewayId（完全一致）</param>
    /// <param name="since">この時刻（ISO-8601）以降に発生したものだけ</param>
    /// <param name="limit">最大件数（1..500、既定 100）</param>
    /// <param name="offset">オフセット（既定 0）</param>
    /// <param name="ct">キャンセルトークン</param>
    [HttpGet]
    [ProducesResponseType(typeof(HealthEventListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<HealthEventListResponse>> List(
        [FromQuery] string? lifecycle,
        [FromQuery] string? ack,
        [FromQuery] string[]? kind,
        [FromQuery] string? subjectType,
        [FromQuery] string? subjectId,
        [FromQuery] DateTimeOffset? since,
        [FromQuery] int limit = DefaultLimit,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        if (offset < 0) return BadRequest("offset must be >= 0");
        limit = Math.Clamp(limit, 1, MaxLimit);

        // An unknown filter value is refused rather than ignored: ignoring it would silently widen the list
        // (a typo'd `lifecycle=opne` would return every event).
        if (!TryEnum<HealthEventLifecycle>(lifecycle, out var life)) return BadRequest($"unknown lifecycle '{lifecycle}'");
        if (!TryEnum<HealthEventAckFilter>(ack, out var ackFilter)) return BadRequest($"unknown ack '{ack}'");
        var kinds = (kind ?? []).Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim().ToLowerInvariant()).ToArray();
        if (kinds.FirstOrDefault(k => !HealthEventKinds.All.Contains(k)) is { } badKind) return BadRequest($"unknown kind '{badKind}'");
        var type = string.IsNullOrWhiteSpace(subjectType) ? null : subjectType.Trim().ToLowerInvariant();
        if (type is not null && !HealthEventSubjects.All.Contains(type)) return BadRequest($"unknown subjectType '{subjectType}'");

        var auth = HttpContext.GetAuthorizationContext();
        // #506: events are value-derived health, and a group-manager reads no values.
        if (auth.IsStructureOnly) return Ok(new HealthEventListResponse([], 0, limit, offset));

        var visibility = await VisibilityAsync(auth, ct).ConfigureAwait(false);
        var page = await store.QueryAsync(new HealthEventQuery
        {
            Lifecycle = life,
            Ack = ackFilter,
            Kinds = kinds,
            SubjectType = type,
            SubjectId = string.IsNullOrWhiteSpace(subjectId) ? null : subjectId.Trim(),
            Since = since?.UtcDateTime,
            Scope = visibility.Scope,
            Limit = limit,
            Offset = offset,
        }, ct).ConfigureAwait(false);

        // Names come from the ledger: admins read the whole twin anyway; for a page with no point event there
        // is nothing to name, so skip building it.
        var names = visibility.Names ?? (page.Items.Any(e => e.SubjectType == HealthEventSubjects.Point)
            ? (await LedgerAsync(auth, ct).ConfigureAwait(false)).Names
            : Names.Empty);

        return Ok(new HealthEventListResponse(page.Items.Select(e => ToResponse(e, names)).ToArray(), page.Total, limit, offset));
    }

    /// <summary>
    /// イベントを確認済みにする（冪等）。**最初に確認した人が記録に残り**、2 回目以降は現状をそのまま返す。
    /// 解消済みのイベントにも確認応答できる（「見た」の記録）。viewer は不可。
    /// </summary>
    /// <param name="id">イベント ID</param>
    /// <param name="ct">キャンセルトークン</param>
    [HttpPost("{id:guid}/ack")]
    [ProducesResponseType(typeof(HealthEventResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HealthEventResponse>> Acknowledge(Guid id, CancellationToken ct = default)
    {
        var auth = HttpContext.GetAuthorizationContext();
        if (!CanAcknowledge(auth)) return Forbid();

        var existing = await store.GetAsync(id, ct).ConfigureAwait(false);
        if (existing is null) return NotFound();

        var visibility = await VisibilityAsync(auth, ct).ConfigureAwait(false);
        // An event the caller cannot see is "not found", not "forbidden": it must not confirm that it exists.
        if (visibility.Scope is { } scope && !InScope(existing, scope)) return NotFound();

        var name = ActorName();
        var before = existing.AcknowledgedAt;
        var updated = await store.AcknowledgeAsync(id, auth.UserId, name, clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        if (updated is null) return NotFound(); // deleted between the read and the write (retention)

        // Audit only the acknowledgement that actually happened, not an idempotent repeat.
        if (before is null && updated.AcknowledgedBy == auth.UserId)
            await AuditAsync(auth, name, updated).ConfigureAwait(false);

        var names = visibility.Names ?? (await LedgerAsync(auth, ct).ConfigureAwait(false)).Names;
        return Ok(ToResponse(updated, names));
    }

    // ── authorization ────────────────────────────────────────────────────────

    // Roles that may acknowledge: an operator or an admin. A viewer reads the events; it does not act on them.
    private static bool CanAcknowledge(AuthorizationContext auth) =>
        auth.IsAdmin || auth.Role == BuildingOS.Shared.Domain.UserManagement.RoleCatalog.Operator;

    private sealed record Visibility(HealthEventScope? Scope, Names? Names);

    private sealed record Names(IReadOnlyDictionary<string, (string? Name, string? Building)> Points)
    {
        public static readonly Names Empty = new(new Dictionary<string, (string?, string?)>());
    }

    private sealed record LedgerView(HealthEventScope Scope, Names Names);

    // Admin: unrestricted (null scope); names are fetched lazily. Everyone else: only what their authorized
    // ledger contains — the same set the data-health API serves them.
    private async Task<Visibility> VisibilityAsync(AuthorizationContext auth, CancellationToken ct)
    {
        if (auth.IsAdmin) return new Visibility(null, null);
        var view = await LedgerAsync(auth, ct).ConfigureAwait(false);
        return new Visibility(view.Scope, view.Names);
    }

    private async Task<LedgerView> LedgerAsync(AuthorizationContext auth, CancellationToken ct)
    {
        var snapshot = await ledger.BuildAsync(auth, buildingDtId: null, ct).ConfigureAwait(false);
        var points = new Dictionary<string, (string?, string?)>(StringComparer.Ordinal);
        var gateways = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in snapshot.Items)
        {
            points[item.PointId] = (item.Name, item.BuildingName);
            if (item.Gateway?.Id is { Length: > 0 } g) gateways.Add(g);
        }
        return new LedgerView(new HealthEventScope(points.Keys.ToHashSet(StringComparer.Ordinal), gateways), new Names(points));
    }

    private static bool InScope(HealthEventEntry e, HealthEventScope scope) => e.SubjectType switch
    {
        HealthEventSubjects.Point => scope.PointIds.Contains(e.SubjectId),
        HealthEventSubjects.Gateway => scope.GatewayIds.Contains(e.SubjectId),
        _ => false,
    };

    // ── helpers ──────────────────────────────────────────────────────────────

    private string? ActorName()
    {
        // The Keycloak display name when the token carries one; the sub alone reads as noise in a list.
        var name = User.FindFirst("name")?.Value ?? User.FindFirst("preferred_username")?.Value;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    // Written after the acknowledgement has committed, so not on the request token; a failed audit write is
    // logged, not turned into an error (the acknowledgement it describes is already on the event row).
    private async Task AuditAsync(AuthorizationContext auth, string? actorName, HealthEventEntry e)
    {
        if (audit is null) return;
        try
        {
            await audit.RecordAsync(
                AdminAuditRecord.Create(AdminAuditSubjects.HealthEvent, "acknowledge", e.Id.ToString(), auth.UserId, actorName,
                    AdminAuditResult.Success,
                    JsonSerializer.Serialize(new { e.SubjectType, e.SubjectId, e.Kind, e.Severity })),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Health event {EventId} was acknowledged but its audit record could not be written", e.Id);
        }
    }

    private static bool TryEnum<T>(string? value, out T? result) where T : struct, Enum
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (Enum.TryParse<T>(value.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            result = parsed;
            return true;
        }
        return false;
    }

    private static HealthEventResponse ToResponse(HealthEventEntry e, Names names)
    {
        string? name = null, building = null;
        if (e.SubjectType == HealthEventSubjects.Point)
        {
            if (names.Points.TryGetValue(e.SubjectId, out var n)) (name, building) = n;
        }
        else name = e.SubjectId;

        return new HealthEventResponse(
            e.Id, e.SubjectType, e.SubjectId, name, building, e.Kind, e.Severity, Utc(e.RaisedAt), Utc(e.ClearedAt),
            e.IsOpen, Utc(e.AcknowledgedAt), e.AcknowledgedByName ?? e.AcknowledgedBy, ParseDetail(e.Detail));
    }

    private static DateTime Utc(DateTime t) => DateTime.SpecifyKind(t, DateTimeKind.Utc);
    private static DateTime? Utc(DateTime? t) => t is { } v ? Utc(v) : null;

    private static JsonElement ParseDetail(string json)
    {
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }
}
