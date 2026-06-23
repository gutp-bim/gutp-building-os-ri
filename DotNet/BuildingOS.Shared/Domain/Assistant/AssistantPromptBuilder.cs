using System.Text;

namespace BuildingOS.Shared.Domain.Assistant;

/// <summary>
/// Pure prompt construction for the help assistant (#151). Injects the client-provided D-1 help
/// context and strict read-only guardrails into a system prompt, and drops any client-supplied
/// "system" message so the guardrails cannot be overridden (prompt-injection hardening).
/// </summary>
public static class AssistantPromptBuilder
{
    /// <summary>Fixed guardrails: usage-help only, answer from the provided context, never control.</summary>
    public const string Guardrails =
        "あなたは Building OS の操作ヘルプ アシスタントです。次のルールを厳守してください。\n" +
        "- 提供された「画面コンテキスト」と一般的な操作知識のみに基づいて、使い方を日本語で簡潔に説明します。\n" +
        "- 機器の制御・設定変更などの操作は絶対に実行・提案しません（読み取り専用の案内のみ）。\n" +
        "- コンテキストに無い具体的な値やデータを推測で答えず、わからない場合は「わかりません」と述べ、画面の『?』ヘルプを案内します。";

    /// <summary>Builds the system prompt, appending the screen context when present.</summary>
    public static string BuildSystemPrompt(AssistantHelpContext? context)
    {
        var sb = new StringBuilder(Guardrails);
        if (context is not null)
        {
            sb.Append("\n\n# 画面コンテキスト");
            if (!string.IsNullOrWhiteSpace(context.Title))
            {
                sb.Append("\n## ").Append(context.Title);
            }
            foreach (var para in context.Body)
            {
                if (!string.IsNullOrWhiteSpace(para))
                {
                    sb.Append('\n').Append(para);
                }
            }
            if (context.Terms.Count > 0)
            {
                sb.Append("\n\n## 用語");
                foreach (var term in context.Terms)
                {
                    sb.Append("\n- ").Append(term.Term).Append(": ").Append(term.Definition);
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Builds the message list sent upstream: the system prompt followed by the user/assistant turns.
    /// Any client-supplied "system" message is dropped so the guardrails cannot be overridden.
    /// </summary>
    public static IReadOnlyList<ChatMessage> BuildMessages(
        AssistantHelpContext? context, IEnumerable<ChatMessage> userMessages)
    {
        var messages = new List<ChatMessage> { new("system", BuildSystemPrompt(context)) };
        messages.AddRange(userMessages.Where(m =>
            string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)));
        return messages;
    }
}
