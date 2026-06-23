namespace BuildingOS.Shared.Domain.Assistant;

/// <summary>One chat message. Role is "user" / "assistant" (a client-supplied "system" role is dropped).</summary>
public sealed record ChatMessage(string Role, string Content);

/// <summary>A glossary term passed as screen context (#151).</summary>
public sealed record AssistantContextTerm(string Term, string Definition);

/// <summary>
/// The screen's help context the client sends with a question (#151). This is the D-1 help (#149)
/// resolved client-side, so D-1 stays the single source of truth and no content is mirrored server-side.
/// </summary>
public sealed record AssistantHelpContext
{
    public string? Title { get; init; }
    public IReadOnlyList<string> Body { get; init; } = [];
    public IReadOnlyList<AssistantContextTerm> Terms { get; init; } = [];
}

/// <summary>A chat request: prior messages + the current screen's help context.</summary>
public sealed record AssistantChatRequest
{
    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];
    public AssistantHelpContext? Context { get; init; }
}

public enum AssistantStatus
{
    Ok,
    Disabled,
    UpstreamError,
}

/// <summary>Result of a chat turn: the reply, or a disabled/upstream-error status.</summary>
public sealed record AssistantChatResult(AssistantStatus Status, string? Reply, string? Error)
{
    public static AssistantChatResult Ok(string reply) => new(AssistantStatus.Ok, reply, null);
    public static AssistantChatResult Disabled() => new(AssistantStatus.Disabled, null, null);
    public static AssistantChatResult UpstreamError(string error) => new(AssistantStatus.UpstreamError, null, error);
}
