using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using BuildingOS.Shared.Domain.AdminAudit;
using BuildingOS.Shared.Domain.TwinAdmin;
using Microsoft.AspNetCore.Mvc;

namespace BuildingOs.ApiServer.Controllers;

/// <summary>
/// デジタルツイン管理ツール（#322）。RDF/pointlist 取込（プレビュー→検証→適用）と読み取り専用 SPARQL
/// コンソール。取込はステージング graph で件数・gateway_id 一意性を事前検証し、全置換/追記を選択して適用。
/// SPARQL は SELECT/ASK のみ。全操作を共有 admin 監査に記録する。管理者のみ。
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
    private readonly ILogger<TwinAdminController> _logger;

    public TwinAdminController(
        ITwinAdminService twin, IAdminAuditRecorder audit, ILogger<TwinAdminController> logger)
    {
        _twin = twin;
        _audit = audit;
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

    /// <summary>取込のプレビュー（件数 + gateway_id 一意性検証）。適用はしない。監査必須。管理者のみ。</summary>
    [HttpPost("import/preview")]
    [ProducesResponseType(typeof(TwinImportPreview), StatusCodes.Status200OK)]
    public async Task<IActionResult> PreviewImport([FromBody] TwinImportRequest request, CancellationToken ct)
    {
        if (!IsAdmin()) return Forbid();
        if (string.IsNullOrWhiteSpace(request.Turtle)) return BadRequest(new { error = "turtle は必須です" });
        var auth = HttpContext.GetAuthorizationContext();

        try
        {
            var preview = await _twin.PreviewImportAsync(request.Turtle, ct).ConfigureAwait(false);
            await AuditAsync(auth, "import-preview", null, AdminAuditResult.Success,
                Meta(request.Turtle, mode: null, preview), ct).ConfigureAwait(false);
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
    /// 取込を適用する（append / replace）。gateway_id 一意性違反があれば 409 で拒否。監査必須。管理者のみ。
    /// </summary>
    [HttpPost("import/apply")]
    [ProducesResponseType(typeof(TwinImportPreview), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ApplyImport([FromBody] TwinImportRequest request, CancellationToken ct)
    {
        if (!IsAdmin()) return Forbid();
        if (string.IsNullOrWhiteSpace(request.Turtle)) return BadRequest(new { error = "turtle は必須です" });
        var auth = HttpContext.GetAuthorizationContext();

        var mode = string.Equals(request.Mode, "replace", StringComparison.OrdinalIgnoreCase)
            ? TwinImportMode.Replace : TwinImportMode.Append;

        try
        {
            // Validate via staging before mutating the live default graph.
            var preview = await _twin.PreviewImportAsync(request.Turtle, ct).ConfigureAwait(false);
            if (!preview.Valid)
            {
                await AuditAsync(auth, "import-apply", null, AdminAuditResult.Failure,
                    Meta(request.Turtle, mode.ToString(), preview), ct).ConfigureAwait(false);
                return Conflict(new { error = "gateway_id 一意性違反のため適用できません", preview });
            }

            await _twin.ApplyImportAsync(request.Turtle, mode, ct).ConfigureAwait(false);
            await AuditAsync(auth, "import-apply", null, AdminAuditResult.Success,
                Meta(request.Turtle, mode.ToString(), preview), ct).ConfigureAwait(false);
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

    private static object Meta(string turtle, string? mode, TwinImportPreview preview) => new
    {
        mode,
        bytes = Encoding.UTF8.GetByteCount(turtle),
        sha256 = Sha256(turtle),
        tripleCount = preview.TripleCount,
        gatewayCount = preview.GatewayCount,
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
        /// <summary>"append" (default) or "replace".</summary>
        public string? Mode { get; init; }
    }
}
