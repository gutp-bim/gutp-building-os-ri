using BuildingOs.ApiServer.Filters;
using BuildingOS.Shared.Domain.Assistant;
using Microsoft.AspNetCore.Mvc;

namespace BuildingOs.ApiServer.Controllers;

/// <summary>
/// 実験的・任意のローカル LLM ヘルプ Q&amp;A（#151）。D-1 解説コンテンツ（クライアントが画面コンテキストとして
/// 送付）をプロンプトに注入して「使い方」を答える。**読み取り専用**で制御アクションは公開しない。既存 JWT 認証で
/// ゲートされ、バックドア API ではない。Ollama 任意プロファイル未起動（ASSISTANT_LLM_URL 未設定）なら 503。
/// </summary>
[ApiController]
[Route("api/assistant")]
[AuthorizeFilter]
public class AssistantController : ControllerBase
{
    private readonly IAssistantService _assistant;

    public AssistantController(IAssistantService assistant)
    {
        _assistant = assistant;
    }

    /// <summary>1ターンのチャット。無効時は 503、上流 LLM エラー時は 502。</summary>
    [HttpPost("chat")]
    [ProducesResponseType(typeof(AssistantChatResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Chat([FromBody] AssistantChatRequest request, CancellationToken ct)
    {
        var result = await _assistant.ChatAsync(request, ct).ConfigureAwait(false);
        return result.Status switch
        {
            AssistantStatus.Ok => Ok(new AssistantChatResponse { Reply = result.Reply ?? string.Empty }),
            AssistantStatus.Disabled => StatusCode(StatusCodes.Status503ServiceUnavailable,
                "アシスタントは無効です（ASSISTANT_LLM_URL 未設定）。"),
            AssistantStatus.UpstreamError => StatusCode(StatusCodes.Status502BadGateway, result.Error),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    public record AssistantChatResponse
    {
        public string Reply { get; init; } = string.Empty;
    }
}
