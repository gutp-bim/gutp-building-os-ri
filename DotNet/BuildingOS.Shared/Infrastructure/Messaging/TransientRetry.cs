using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Infrastructure.Messaging;

/// <summary>
/// Retries a transient-failure-prone async operation with exponential backoff. Intended for
/// dependency-handshake calls made once at startup (e.g. NATS JetStream stream/consumer creation,
/// #61) that can fail if the dependency isn't fully warmed up yet. The caller's own cancellation is
/// never retried — it always rethrows immediately so shutdown stays responsive.
/// </summary>
public static class TransientRetry
{
    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        int maxAttempts = 5,
        TimeSpan? initialDelay = null,
        ILogger? logger = null,
        string? operationName = null)
    {
        var delay = initialDelay ?? TimeSpan.FromSeconds(1);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                logger?.LogWarning(ex,
                    "{Operation}: attempt {Attempt}/{Max} failed, retrying in {Delay}",
                    operationName ?? "operation", attempt, maxAttempts, delay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay += delay;
            }
        }
    }
}
