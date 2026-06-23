using System.Net.Http.Json;
using System.Text.Json.Serialization;
using BuildingOS.Shared.Domain.Assistant;

namespace BuildingOS.Shared.Infrastructure.Assistant;

/// <summary>
/// <see cref="IAssistantLlmClient"/> over an OpenAI-compatible chat-completions API (#151), e.g. the
/// local Ollama optional profile (`/v1/chat/completions`). Non-streaming. The HttpClient's BaseAddress
/// and the model name are configured at registration; nothing about control actions is ever sent.
/// </summary>
public sealed class OllamaAssistantLlmClient : IAssistantLlmClient
{
    private readonly HttpClient _http;
    private readonly string _model;

    public OllamaAssistantLlmClient(HttpClient http, string model)
    {
        _http = http;
        _model = model;
    }

    public async Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        var payload = new ChatCompletionRequest
        {
            Model = _model,
            Stream = false,
            Messages = messages.Select(m => new WireMessage { Role = m.Role.ToLowerInvariant(), Content = m.Content }).ToList(),
        };

        using var response = await _http
            .PostAsJsonAsync("chat/completions", payload, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content
            .ReadFromJsonAsync<ChatCompletionResponse>(cancellationToken: ct)
            .ConfigureAwait(false);

        return body?.Choices?.FirstOrDefault()?.Message?.Content?.Trim() ?? string.Empty;
    }

    private sealed class ChatCompletionRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = default!;
        [JsonPropertyName("stream")] public bool Stream { get; set; }
        [JsonPropertyName("messages")] public List<WireMessage> Messages { get; set; } = [];
    }

    private sealed class WireMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = default!;
        [JsonPropertyName("content")] public string Content { get; set; } = default!;
    }

    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("choices")] public List<Choice>? Choices { get; set; }
    }

    private sealed class Choice
    {
        [JsonPropertyName("message")] public WireMessage? Message { get; set; }
    }
}
