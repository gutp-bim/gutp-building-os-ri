using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using BuildingOS.Shared.Domain.AdminAudit;
using Microsoft.AspNetCore.Mvc;

namespace BuildingOs.ApiServer.Controllers;

/// <summary>
/// 共有 admin 監査ログの読み取り（#322/#323/#324/#325）。ツイン取込/SPARQL・ゲートウェイ・OIDC
/// クライアント・ユーザー/ロールの各管理操作（mutating）が記録され、ここで横断的に時系列閲覧する。
/// 管理者のみ。
/// </summary>
[ApiController]
[Route("api/admin/audit")]
[AuthorizeFilter]
public class AdminAuditController : ControllerBase
{
    private readonly IAdminAuditRecorder _audit;

    public AdminAuditController(IAdminAuditRecorder audit)
    {
        _audit = audit;
    }

    /// <summary>監査ログを新しい順に取得する。subjectType / targetId で絞り込み可能。管理者のみ。</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AdminAuditResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAudit(
        [FromQuery] string? subjectType,
        [FromQuery] string? targetId,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin)
        {
            return Forbid();
        }

        var records = await _audit
            .ListAsync(new AdminAuditQuery(subjectType, targetId, limit), ct)
            .ConfigureAwait(false);

        return Ok(records.Select(AdminAuditResponse.From).ToList());
    }
}

/// <summary>API レスポンス DTO。監査ドメイン型をそのまま露出せず、result 文字列化して返す。</summary>
public sealed record AdminAuditResponse(
    Guid Id,
    string SubjectType,
    string Action,
    string? TargetId,
    string ActorSub,
    string? ActorName,
    string Result,
    string? Detail,
    DateTime CreatedAt)
{
    public static AdminAuditResponse From(AdminAuditRecord r) => new(
        r.Id,
        r.SubjectType,
        r.Action,
        r.TargetId,
        r.ActorSub,
        r.ActorName,
        AdminAuditSerializer.ToResultText(r.Result),
        r.DetailJson,
        r.CreatedAt);
}
