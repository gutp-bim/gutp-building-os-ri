using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using BuildingOS.Shared.Domain.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace BuildingOs.ApiServer.Controllers;

/// <summary>
/// 編集可能なアプリ設定（フィーチャーフラグ/閾値など、GitOps と衝突しない設定）の CRUD（#148）。
/// 許可リスト（<see cref="SettingsRegistry"/>）のキーのみ編集可能で、値は型検証される。管理者のみ。
/// インフラ/シークレット設定は読み取り専用の実効値ビュー（#147）側で扱う。
/// </summary>
[ApiController]
[Route("api/system/settings")]
[AuthorizeFilter]
public class SystemConfigController : ControllerBase
{
    private readonly ISystemSettingsService _settings;

    public SystemConfigController(ISystemSettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>編集可能設定の実効値一覧（既定値 + override をマージ）を取得する。管理者のみ。</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<SettingView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin)
        {
            return Forbid();
        }

        var settings = await _settings.GetSettingsAsync(ct).ConfigureAwait(false);
        return Ok(settings);
    }

    /// <summary>設定値を更新する（許可リスト外は 404、型不一致は 400）。管理者のみ。</summary>
    [HttpPut("{key}")]
    [ProducesResponseType(typeof(SettingView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateSetting(string key, [FromBody] UpdateSettingRequest request, CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin)
        {
            return Forbid();
        }

        var result = await _settings
            .UpdateSettingAsync(key, request.Value, authContext.UserId, ct)
            .ConfigureAwait(false);

        return result.Status switch
        {
            SettingUpdateStatus.Ok => Ok(result.View),
            SettingUpdateStatus.UnknownKey => NotFound(),
            SettingUpdateStatus.Invalid => BadRequest(result.Error),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    /// <summary>設定を既定値にリセットする（override を削除）。許可リスト外は 404。管理者のみ。</summary>
    [HttpDelete("{key}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetSetting(string key, CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin)
        {
            return Forbid();
        }

        var existed = await _settings.ResetSettingAsync(key, ct).ConfigureAwait(false);
        return existed ? NoContent() : NotFound();
    }

    public record UpdateSettingRequest
    {
        public string? Value { get; init; }
    }
}
