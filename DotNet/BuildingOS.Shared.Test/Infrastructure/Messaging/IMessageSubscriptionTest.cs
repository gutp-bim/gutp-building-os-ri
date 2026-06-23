using BuildingOS.Shared.Infrastructure.Messaging;

namespace BuildingOS.Shared.Test.Infrastructure.Messaging;

public class IMessageSubscriptionTest
{
    [Fact]
    public async Task Dispatch_CallsRegisteredHandler()
    {
        var messages = new List<string>();
        var subscription = new InProcessMessageSubscription();
        subscription.Register(msg => { messages.Add(msg); return Task.CompletedTask; });

        await subscription.DispatchAsync("hello");

        Assert.Equal(new[] { "hello" }, messages);
    }

    [Fact]
    public async Task Dispatch_WithNoHandler_DoesNotThrow()
    {
        var subscription = new InProcessMessageSubscription();
        await subscription.DispatchAsync("ignored");
    }

    [Fact]
    public async Task Dispatch_CallsAllRegisteredHandlers()
    {
        var calls = new List<string>();
        var subscription = new InProcessMessageSubscription();
        subscription.Register(m => { calls.Add($"h1:{m}"); return Task.CompletedTask; });
        subscription.Register(m => { calls.Add($"h2:{m}"); return Task.CompletedTask; });

        await subscription.DispatchAsync("msg");

        Assert.Contains("h1:msg", calls);
        Assert.Contains("h2:msg", calls);
    }
}
