using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using BuildingOs.ApiServer.GatewayProvisioning;
using BuildingOS.Shared.Domain.AdminAudit;
using BuildingOS.Shared.Domain.TwinAdmin;
using Microsoft.AspNetCore.Mvc;

namespace BuildingOs.ApiServer.Controllers;

/// <summary>
/// デジタルツイン管理ツール（#322）。RDF/pointlist 取込（プレビュー→検証→適用）と読み取り専用 SPARQL
/// コンソール。取込はステージング graph で件数・gateway_id 一意性・階層未接続（#291）を事前検証し、
/// 全置換/追記を選択して適用。SPARQL は SELECT/ASK のみ。全操作を共有 admin 監査に記録する。管理者のみ。
/// </summary>
[ApiController]
[Route("api/admin/twin")]
[AuthorizeFilter]
public class TwinAdminController : ControllerBase
{
    private const int DefaultQueryTimeoutSec = 15;
    private const int MaxQueryRows = 1000;

    private readonly ITwinAdminService _twin;
    private readonly IAdminAuditRecorder _audit;
    private readonly IPointListRevisionCoordinator _pointListRevisions;
    private readonly ILogger<TwinAdminController> _logger;

    public TwinAdminController(
        ITwinAdminService twin,
        IAdminAuditRecorder audit,
        IPointListRevisionCoordinator pointListRevisions,
        ILogger<TwinAdminController> logger)
    {
        _twin = twin;
        _audit = audit;
        _pointListRevisions = pointListRevisions;
        _logger = logger;
    }

    /// <summary>読み取り専用 SPARQL（SELECT/ASK）を実行する。更新系は 400。監査必須。管理者のみ。</summary>
    [HttpPost("query")]
    [ProducesResponseType(typeof(SparqlQueryResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Query([FromBody] SparqlQueryRequest request, CancellationToken ct)
    {
        if (!IsAdmin()) return Forbid();
        var auth = HttpContext.GetAuthorizationContext();

        var guard = SparqlReadOnlyGuard.Validate(request.Query);
        if (!guard.Allowed)
        {
            await AuditAsync(auth, "query", null, AdminAuditResult.Failure,
                new { reason = guard.Reason }, ct).ConfigureAwait(false);
            return BadRequest(new { error = guard.Reason });
        }

        try
        {
            var maxRows = request.MaxRows is > 0 and <= MaxQueryRows ? request.MaxRows.Value : MaxQueryRows;
            var result = await _twin.RunReadOnlyQueryAsync(
                request.Query, maxRows, TimeSpan.FromSeconds(DefaultQueryTimeoutSec), ct).ConfigureAwait(false);

            await AuditAsync(auth, "query", null, AdminAuditResult.Success,
                new { query = request.Query, rowCount = result.RowCount, elapsedMs = result.ElapsedMs, truncated = result.Truncated },
                ct).ConfigureAwait(false);
            return Ok(result);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await AuditAsync(auth, "query", null, AdminAuditResult.Failure, new { error = "timeout" }, ct).ConfigureAwait(false);
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { error = "クエリがタイムアウトしました" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SPARQL query failed");
            await AuditAsync(auth, "query", null, AdminAuditResult.Failure, new { error = ex.Message }, ct).ConfigureAwait(false);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// 取込のプレビュー（件数 + gateway_id 一意性 + 階層未接続検証）。適用はしない。階層未接続の判定は
    /// mode に依存する（append は既定グラフとの併合後、replace はステージングのみ）ため、適用予定と同じ
    /// mode を渡すこと。監査必須。管理者のみ。
    /// </summary>
    [HttpPost("import/preview")]
    [ProducesResponseType(typeof(TwinImportPreview), StatusCodes.Status200OK)]
    public async Task<IActionResult> PreviewImport([FromBody] TwinImportRequest request, CancellationToken ct)
    {
        if (!IsAdmin()) return Forbid();
        if (string.IsNullOrWhiteSpace(request.Turtle)) return BadRequest(new { error = "turtle は必須です" });
        var auth = HttpContext.GetAuthorizationContext();

        var mode = ParseMode(request.Mode);

        try
        {
            var preview = await _twin.PreviewImportAsync(request.Turtle, mode, ct).ConfigureAwait(false);
            await AuditAsync(auth, "import-preview", null, AdminAuditResult.Success,
                Meta(request.Turtle, mode.ToString(), preview), ct).ConfigureAwait(false);
            return Ok(preview);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Twin import preview failed");
            await AuditAsync(auth, "import-preview", null, AdminAuditResult.Failure,
                new { error = ex.Message }, ct).ConfigureAwait(false);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// 取込を適用する（append / replace）。gateway_id 一意性違反、および階層未接続リソース（#291）があれば
    /// 409 で拒否。階層未接続のみ <c>allowOrphans</c> による明示的な上書きで適用できる（監査に記録される）。
    /// 監査必須。管理者のみ。
    /// </summary>
    [HttpPost("import/apply")]
    [ProducesResponseType(typeof(TwinImportPreview), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ApplyImport([FromBody] TwinImportRequest request, CancellationToken ct)
    {
        if (!IsAdmin()) return Forbid();
        if (string.IsNullOrWhiteSpace(request.Turtle)) return BadRequest(new { error = "turtle は必須です" });
        var auth = HttpContext.GetAuthorizationContext();

        var mode = ParseMode(request.Mode);

        try
        {
            // Validate via staging before mutating the live default graph, in the mode the import will
            // actually be applied with — the hierarchy check (#291) resolves against the default graph
            // for an append and against the staged triples alone for a replace. A gateway_id collision
            // is always fatal; unreachable resources are fatal unless the caller explicitly overrode
            // the check, which the audit records either way.
            var preview = await _twin.PreviewImportAsync(request.Turtle, mode, ct).ConfigureAwait(false);
            var blocker = BlockingReason(preview, request.AllowOrphans);
            if (blocker is not null)
            {
                await AuditAsync(auth, "import-apply", null, AdminAuditResult.Failure,
                    Meta(request.Turtle, mode.ToString(), preview, request.AllowOrphans), ct).ConfigureAwait(false);
                return Conflict(new { error = blocker, preview });
            }

            // Invalidate the shared generation before mutating OxiGraph. If NATS KV is unavailable,
            // abort the import: keeping the old generation while changing the Twin could let another
            // API replica return an incorrect 304 from its previously published revision.
            var updateToken = await _pointListRevisions.BeginUpdateAsync(ct).ConfigureAwait(false);
            try
            {
                await _twin.ApplyImportAsync(request.Turtle, mode, ct).ConfigureAwait(false);
            }
            finally
            {
                // A failed completion leaves the registry in "updating" state. Reads then fail
                // closed to a Twin query rather than trusting any pre-import ETag.
                await _pointListRevisions.CompleteUpdateAsync(updateToken, CancellationToken.None).ConfigureAwait(false);
            }
            await AuditAsync(auth, "import-apply", null, AdminAuditResult.Success,
                Meta(request.Turtle, mode.ToString(), preview, request.AllowOrphans), ct).ConfigureAwait(false);
            return Ok(preview);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Twin import apply failed");
            await AuditAsync(auth, "import-apply", null, AdminAuditResult.Failure,
                new { mode = mode.ToString(), error = ex.Message }, ct).ConfigureAwait(false);
            return BadRequest(new { error = ex.Message });
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private bool IsAdmin() => HttpContext.GetAuthorizationContext().IsAdmin;

    // "replace" (case-insensitive) or append — the default, and what an unset/unknown mode means.
    private static TwinImportMode ParseMode(string? mode) =>
        string.Equals(mode, "replace", StringComparison.OrdinalIgnoreCase)
            ? TwinImportMode.Replace : TwinImportMode.Append;

    // Why the import may not be applied, or null when it may. gateway_id collisions can never be
    // overridden; unreachable resources can, but only when the caller asked for it explicitly (#291).
    private static string? BlockingReason(TwinImportPreview preview, bool allowOrphans)
    {
        if (preview.Collisions.Count > 0) return "gateway_id 一意性違反のため適用できません";
        if (preview.OrphanCount > 0 && !allowOrphans)
            return "階層に接続されていないリソースがあるため適用できません（allowOrphans で明示的に許可できます）";
        return null;
    }

    private static object Meta(string turtle, string? mode, TwinImportPreview preview, bool allowOrphans = false) => new
    {
        mode,
        bytes = Encoding.UTF8.GetByteCount(turtle),
        sha256 = Sha256(turtle),
        tripleCount = preview.TripleCount,
        gatewayCount = preview.GatewayCount,
        orphanCount = preview.OrphanCount,
        allowOrphans,
        controlSchemaIssueCount = preview.ControlSchemaIssueCount,
        valid = preview.Valid,
    };

    private static string Sha256(string s)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private Task AuditAsync(
        BuildingOS.Shared.Domain.Authorization.AuthorizationContext auth,
        string action, string? targetId, AdminAuditResult result, object? detail, CancellationToken ct)
    {
        var detailJson = detail is null ? null : JsonSerializer.Serialize(detail);
        var record = AdminAuditRecord.Create(
            AdminAuditSubjects.Twin, action, targetId, auth.UserId, actorName: null, result, detailJson);
        return _audit.RecordAsync(record, ct);
    }

    // ── DTOs ─────────────────────────────────────────────────────────────────

    public record SparqlQueryRequest
    {
        public string Query { get; init; } = default!;
        public int? MaxRows { get; init; }
    }

    public record TwinImportRequest
    {
        public string Turtle { get; init; } = default!;
        /// <summary>"append" (default) or "replace". プレビューでも使う（階層未接続の判定範囲、#291）。</summary>
        public string? Mode { get; init; }
        /// <summary>
        /// 階層未接続リソース（#291）があっても適用する明示的な上書き。既定 false（拒否）。
        /// gateway_id 一意性違反は上書きできない。
        /// </summary>
        public bool AllowOrphans { get; init; }
    }
}
