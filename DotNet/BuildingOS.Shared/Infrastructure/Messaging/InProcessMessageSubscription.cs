namespace BuildingOS.Shared.Infrastructure.Messaging;

/// <summary>
/// In-process test double for <see cref="IMessageSubscription"/>.
/// Tests call <see cref="DispatchAsync"/> to inject messages directly without a running NATS broker.
/// </summary>
public class InProcessMessageSubscription : IMessageSubscription
{
    private readonly List<Func<string, Task>> _handlers = new();

    public void Register(Func<string, Task> handler)
        => _handlers.Add(handler);

    public Task StartAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Dispatches a message to all registered handlers sequentially.
    /// Exceptions from individual handlers are re-thrown after all handlers have run.
    /// </summary>
    public async Task DispatchAsync(string message, CancellationToken cancellationToken = default)
    {
        List<Exception>? errors = null;
        foreach (var handler in _handlers)
        {
            try
            {
                await handler(message);
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }
        }
        if (errors is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(errors[0]);
        if (errors is { Count: > 1 })
            throw new AggregateException(errors);
    }
}
