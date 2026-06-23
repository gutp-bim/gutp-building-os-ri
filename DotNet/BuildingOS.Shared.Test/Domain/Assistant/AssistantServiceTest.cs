using BuildingOS.Shared.Domain.Assistant;

namespace BuildingOS.Shared.Test.Domain.Assistant;

public class AssistantServiceTest
{
    private static AssistantChatRequest Request() => new()
    {
        Messages = new[] { new ChatMessage("user", "使い方は？") },
    };

    [Fact]
    public async Task ChatAsync_WhenNoClient_ReportsDisabled()
    {
        var service = new AssistantService(llm: null);
        Assert.False(service.IsEnabled);

        var result = await service.ChatAsync(Request());

        Assert.Equal(AssistantStatus.Disabled, result.Status);
    }

    [Fact]
    public async Task ChatAsync_ForwardsGuardedMessages_AndReturnsReply()
    {
        var llm = new Mock<IAssistantLlmClient>();
        IReadOnlyList<ChatMessage>? captured = null;
        llm.Setup(c => c.CompleteAsync(It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<ChatMessage>, CancellationToken>((m, _) => captured = m)
            .ReturnsAsync("これは説明です");

        var service = new AssistantService(llm.Object);
        Assert.True(service.IsEnabled);

        var result = await service.ChatAsync(Request());

        Assert.Equal(AssistantStatus.Ok, result.Status);
        Assert.Equal("これは説明です", result.Reply);
        Assert.NotNull(captured);
        Assert.Equal("system", captured![0].Role); // guardrails always prepended
    }

    [Fact]
    public async Task ChatAsync_WhenClientThrows_ReportsUpstreamError()
    {
        var llm = new Mock<IAssistantLlmClient>();
        llm.Setup(c => c.CompleteAsync(It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));

        var service = new AssistantService(llm.Object);

        var result = await service.ChatAsync(Request());

        Assert.Equal(AssistantStatus.UpstreamError, result.Status);
        Assert.Contains("connection refused", result.Error);
    }
}
