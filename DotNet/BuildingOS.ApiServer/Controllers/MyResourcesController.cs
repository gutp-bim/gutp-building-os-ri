using BuildingOs.ApiServer.Routing;
using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using BuildingOS.Shared.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BuildingOs.ApiServer.Controllers;

/// <summary>
/// ユーザーのアクセス可能リソース取得エンドポイント
/// </summary>
[ApiController]
[Route(ApiRoutes.V1 + "/my-resources")]
[AuthorizeFilter]
public class MyResourcesController : ControllerBase
{
    private readonly BuildingOS.Shared.Domain.Authorization.IAuthorizationService _authorizationService;
    private readonly IResourceIdMappingRepository _mappingRepository;
    private readonly IResourceDescendantResolver _descendants;

    public MyResourcesController(
        BuildingOS.Shared.Domain.Authorization.IAuthorizationService authorizationService,
        IResourceIdMappingRepository mappingRepository,
        IResourceDescendantResolver descendants)
    {
        _authorizationService = authorizationService;
        _mappingRepository = mappingRepository;
        _descendants = descendants;
    }

    private const string IdFormatHash = "hash";
    private const string IdFormatOriginal = "original";
    private const string ExpandDescendants = "descendants";

    private static readonly IReadOnlyList<string> ResourceTypes = IResourceDescendantResolver.Types;

    /// <summary>
    /// Upper bound on the ids one <c>expand=descendants</c> response may carry (#509). A building grant on a
    /// large twin expands to every point in it; past this the caller narrows <c>targetType</c> instead.
    /// </summary>
    public const int MaxExpandedIds = 50_000;

    /// <summary>
    /// 指定リソースタイプのアクセス可能リソースID一覧を取得
    /// </summary>
    /// <param name="resourceType">building / floor / space / device / point</param>
    /// <param name="action">read / write など</param>
    /// <param name="idFormat">
    /// <c>hash</c>（既定。従来どおり、逆引きできない ID はハッシュのまま混在）または <c>original</c>
    /// （元の業務 ID だけを <c>accessibleResourceIds</c> に返し、元 ID が分からないものは
    /// <c>unresolvedResourceIds</c> にハッシュで分けて返す。#504）。
    /// </param>
    /// <param name="ct">キャンセル</param>
    [HttpGet("accessible")]
    [ProducesResponseType(typeof(AccessibleResourcesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetAccessible(
        [FromQuery] string resourceType,
        [FromQuery] string action,
        [FromQuery] string? idFormat = null,
        CancellationToken ct = default)
    {
        if (!TryParseIdFormat(idFormat, out var original)) return InvalidIdFormat(idFormat);
        var authContext = HttpContext.GetAuthorizationContext();

        IReadOnlyList<string> ids;
        IReadOnlyList<string>? unresolved = null;
        if (original)
        {
            (ids, var rest) = await ResolveOriginalAsync(authContext, resourceType, action, ct).ConfigureAwait(false);
            unresolved = rest;
        }
        else
        {
            var hashedIds = await _authorizationService.GetAccessibleResourceIdsAsync(
                authContext, resourceType, action, ct).ConfigureAwait(false);
            ids = await ResolveToOriginalIdsAsync(hashedIds, ct).ConfigureAwait(false);
        }

        return Ok(new AccessibleResourcesResponse
        {
            UserId = authContext.UserId,
            Role = authContext.Role,
            IsAdmin = authContext.IsAdmin,
            ResourceType = resourceType,
            Action = action,
            AccessibleResourceIds = ids,
            UnresolvedResourceIds = unresolved,
        });
    }

    /// <summary>
    /// ユーザーのアクセス可能リソース一覧を取得（全リソースタイプ）
    /// </summary>
    /// <param name="idFormat">
    /// <c>hash</c>（既定。従来どおり）または <c>original</c>（<c>resources</c> は元の業務 ID だけ。
    /// 元 ID が分からない権限は <c>unresolved</c> にハッシュで分けて返す。#504）。admin は常に
    /// <c>resources: null</c>（全件）。
    /// </param>
    /// <param name="expand">
    /// <c>descendants</c> を指定すると、読めるリソースの twin 上の子孫（<c>targetType</c> まで）も各種別に加える
    /// （#509）。子孫は認可の祖先判定と同じ経路で辿るので、返る ID はすべて読める。業務 ID で返すため
    /// <c>idFormat=original</c> と併用する（それ以外は 400）。<c>unresolved</c> の権限は twin 上の位置が
    /// 分からないので展開しない。
    /// </param>
    /// <param name="targetType">
    /// 展開する深さ（building / floor / space / device / point、既定 point）。<c>expand</c> が無ければ無視。
    /// 展開後の ID が <see cref="MaxExpandedIds"/> を超えると 422（浅い <c>targetType</c> を指定する）。
    /// </param>
    /// <param name="ct">キャンセル</param>
    [HttpGet]
    [ProducesResponseType(typeof(MyResourcesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> GetMyResources(
        [FromQuery] string? idFormat = null,
        [FromQuery] string? expand = null,
        [FromQuery] string? targetType = null,
        CancellationToken ct = default)
    {
        if (!TryParseIdFormat(idFormat, out var original)) return InvalidIdFormat(idFormat);
        var expandDescendants = false;
        if (!string.IsNullOrEmpty(expand))
        {
            if (!string.Equals(expand, ExpandDescendants, StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = $"expand must be '{ExpandDescendants}'", expand });
            if (!original)
                return BadRequest(new { error = $"expand={ExpandDescendants} returns business ids; use it with idFormat={IdFormatOriginal}" });
            expandDescendants = true;
        }
        targetType = string.IsNullOrEmpty(targetType) ? "point" : targetType.ToLowerInvariant();
        // Only meaningful with expand; without it the parameter is ignored rather than validated.
        if (expandDescendants && !ResourceTypes.Contains(targetType))
            return BadRequest(new { error = $"targetType must be one of {string.Join(", ", ResourceTypes)}", targetType });
        var authContext = HttpContext.GetAuthorizationContext();

        if (authContext.IsAdmin)
        {
            return Ok(new MyResourcesResponse { IsAdmin = true });
        }

        var resources = new Dictionary<string, IReadOnlyList<string>>();
        Dictionary<string, IReadOnlyList<string>>? unresolved = original ? new() : null;

        foreach (var type in ResourceTypes)
        {
            if (original)
            {
                var (ids, rest) = await ResolveOriginalAsync(authContext, type, "read", ct).ConfigureAwait(false);
                resources[type] = ids;
                unresolved![type] = rest;
            }
            else
            {
                var hashedIds = await _authorizationService.GetAccessibleResourceIdsAsync(
                    authContext, type, "read", ct).ConfigureAwait(false);

                // ハッシュIDを元IDに逆引き（逆引きできないものはハッシュのまま — 従来の挙動）
                resources[type] = await ResolveToOriginalIdsAsync(hashedIds, ct).ConfigureAwait(false);
            }
        }

        if (expandDescendants)
        {
            var roots = resources.SelectMany(kv => kv.Value.Select(id => (kv.Key, id))).ToList();
            var descendants = roots.Count == 0
                ? new Dictionary<string, IReadOnlyList<string>>()
                : await _descendants.GetDescendantsAsync(roots, targetType, ct).ConfigureAwait(false);
            foreach (var (type, ids) in descendants)
            {
                if (ids.Count == 0 || !resources.ContainsKey(type)) continue;
                resources[type] = resources[type].Concat(ids).Distinct(StringComparer.Ordinal).ToList();
            }

            var total = resources.Values.Sum(v => v.Count);
            if (total > MaxExpandedIds)
                return StatusCode(StatusCodes.Status422UnprocessableEntity, new
                {
                    error = $"the expansion yields {total} ids, over the limit of {MaxExpandedIds}; " +
                            "use a shallower targetType (e.g. device or space)",
                    count = total,
                    limit = MaxExpandedIds,
                });
        }

        return Ok(new MyResourcesResponse { IsAdmin = false, Resources = resources, Unresolved = unresolved });
    }

    private static bool TryParseIdFormat(string? idFormat, out bool original)
    {
        original = string.Equals(idFormat, IdFormatOriginal, StringComparison.OrdinalIgnoreCase);
        return original || string.IsNullOrEmpty(idFormat)
            || string.Equals(idFormat, IdFormatHash, StringComparison.OrdinalIgnoreCase);
    }

    private BadRequestObjectResult InvalidIdFormat(string? idFormat)
        => BadRequest(new { error = $"idFormat must be '{IdFormatHash}' or '{IdFormatOriginal}'", idFormat });

    /// <summary>
    /// Original ids for <paramref name="resourceType"/>: those a Group supplies, then those the
    /// resource-id mapping table knows; hashes resolvable by neither are returned separately.
    /// </summary>
    private async Task<(IReadOnlyList<string> Ids, IReadOnlyList<string> Unresolved)> ResolveOriginalAsync(
        AuthorizationContext authContext, string resourceType, string action, CancellationToken ct)
    {
        var accessible = await _authorizationService.GetAccessibleResourcesAsync(
            authContext, resourceType, action, ct).ConfigureAwait(false);

        var unknown = accessible.Where(r => r.OriginalId is null).Select(r => r.Hash).ToList();
        var mapping = unknown.Count == 0
            ? new Dictionary<string, string>()
            : await _mappingRepository.ResolveOriginalIdsAsync(unknown, ct).ConfigureAwait(false);

        var ids = new List<string>();
        var unresolved = new List<string>();
        foreach (var r in accessible)
        {
            var id = r.OriginalId ?? (mapping.TryGetValue(r.Hash, out var o) ? o : null);
            if (id is null) unresolved.Add(r.Hash);
            else ids.Add(id);
        }
        return (ids.Distinct(StringComparer.Ordinal).ToList(), unresolved);
    }

    /// <summary>
    /// ハッシュ化されたリソースIDリストを元のIDリストに変換する
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveToOriginalIdsAsync(
        IReadOnlyList<string> hashedIds, CancellationToken ct)
    {
        if (hashedIds.Count == 0) return hashedIds;

        var mapping = await _mappingRepository.ResolveOriginalIdsAsync(hashedIds, ct).ConfigureAwait(false);

        return hashedIds
            .Select(h => mapping.TryGetValue(h, out var original) ? original : h)
            .ToList();
    }
}

public class MyResourcesResponse
{
    public bool IsAdmin { get; set; }
    public Dictionary<string, IReadOnlyList<string>>? Resources { get; set; }

    /// <summary>
    /// <c>idFormat=original</c> のときだけ: 元の業務 ID が分からない権限（直接付与され、ID 対応表にも
    /// 無いもの）の種別ごとのハッシュ。既定では null（#504）。
    /// </summary>
    public Dictionary<string, IReadOnlyList<string>>? Unresolved { get; set; }
}

public class AccessibleResourcesResponse
{
    public string UserId { get; set; } = "";
    public string Role { get; set; } = "";
    public bool IsAdmin { get; set; }
    public string ResourceType { get; set; } = "";
    public string Action { get; set; } = "";
    public IReadOnlyList<string> AccessibleResourceIds { get; set; } = [];

    /// <summary><c>idFormat=original</c> のときだけ: 元 ID が分からない権限のハッシュ。既定では null（#504）。</summary>
    public IReadOnlyList<string>? UnresolvedResourceIds { get; set; }
}
