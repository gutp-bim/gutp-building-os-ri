namespace BuildingOS.Shared.Infrastructure.Messaging;

/// <summary>
/// In-process test double for <see cref="IMessageSubscription"/>.
/// Tests call <see cref="DispatchAsync"/> to inject messages directly without a running NATS broker.
/// </summary>
/// <remarks>
/// Since .NET 10, <c>BackgroundService.StartAsync</c> runs the whole of <c>ExecuteAsync</c> on the
/// thread pool instead of its synchronous prefix inline, so a worker's <see cref="Register"/> call can
/// land after <c>StartAsync</c> has returned. <see cref="DispatchAsync"/> therefore waits (bounded by
/// <see cref="RegistrationTimeout"/>) for the first handler instead of silently dropping the message.
/// </remarks>
public class InProcessMessageSubscription : IMessageSubscription
{
    /// <summary>How long <see cref="DispatchAsync"/> waits for a handler to be registered.</summary>
    public static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly List<Func<string, Task>> _handlers = new();
    private readonly TaskCompletionSource _firstHandler = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Register(Func<string, Task> handler)
    {
        lock (_gate) _handlers.Add(handler);
        _firstHandler.TrySetResult();
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Dispatches a message to all registered handlers sequentially.
    /// Exceptions from individual handlers are re-thrown after all handlers have run.
    /// </summary>
    public async Task DispatchAsync(string message, CancellationToken cancellationToken = default)
    {
        try
        {
            await _firstHandler.Task.WaitAsync(RegistrationTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // No handler ever registered — dispatch to nobody, as before.
        }

        Func<string, Task>[] handlers;
        lock (_gate) handlers = _handlers.ToArray();

        List<Exception>? errors = null;
        foreach (var handler in handlers)
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
