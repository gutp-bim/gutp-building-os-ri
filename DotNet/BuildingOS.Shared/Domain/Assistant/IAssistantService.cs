namespace BuildingOS.Shared.Domain.Assistant;

/// <summary>Sends chat turns to the local LLM (#151), building a guarded prompt from screen context.</summary>
public interface IAssistantLlmClient
{
    Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default);
}

/// <summary>
/// Help-assistant orchestrator (#151). Disabled (no upstream configured) unless an
/// <see cref="IAssistantLlmClient"/> is wired. Read-only: it only returns assistant text; it never
/// performs control actions.
/// </summary>
public interface IAssistantService
{
    bool IsEnabled { get; }
    Task<AssistantChatResult> ChatAsync(AssistantChatRequest request, CancellationToken ct = default);
}

/// <summary>
/// Default orchestrator: builds a guarded prompt (<see cref="AssistantPromptBuilder"/>) and forwards
/// to the injected <see cref="IAssistantLlmClient"/>. When no client is configured the feature reports
/// itself disabled (the experimental Ollama profile is off by default).
/// </summary>
public sealed class AssistantService : IAssistantService
{
    private readonly IAssistantLlmClient? _llm;

    public AssistantService(IAssistantLlmClient? llm = null)
    {
        _llm = llm;
    }

    public bool IsEnabled => _llm is not null;

    public async Task<AssistantChatResult> ChatAsync(AssistantChatRequest request, CancellationToken ct = default)
    {
        if (_llm is null)
        {
            return AssistantChatResult.Disabled();
        }

        var messages = AssistantPromptBuilder.BuildMessages(request.Context, request.Messages);
        try
        {
            var reply = await _llm.CompleteAsync(messages, ct).ConfigureAwait(false);
            return AssistantChatResult.Ok(reply);
        }
        catch (Exception ex)
        {
            return AssistantChatResult.UpstreamError(ex.Message);
        }
    }
}
