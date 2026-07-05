using BuildingOS.Shared.Infrastructure.Messaging;

namespace BuildingOS.Shared.Test.Infrastructure.Messaging;

public class TransientRetryTest
{
    [Fact]
    public async Task RunAsync_SucceedsOnFirstTry_ReturnsResultWithoutDelay()
    {
        var calls = 0;
        var result = await TransientRetry.RunAsync(
            _ => { calls++; return Task.FromResult(42); },
            CancellationToken.None,
            initialDelay: TimeSpan.FromMilliseconds(1));

        Assert.Equal(42, result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunAsync_FailsTwiceThenSucceeds_RetriesAndReturnsResult()
    {
        var calls = 0;
        var result = await TransientRetry.RunAsync(
            _ =>
            {
                calls++;
                if (calls < 3) throw new InvalidOperationException("transient");
                return Task.FromResult("ok");
            },
            CancellationToken.None,
            initialDelay: TimeSpan.FromMilliseconds(1));

        Assert.Equal("ok", result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task RunAsync_AlwaysFails_ThrowsAfterMaxAttempts()
    {
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransientRetry.RunAsync<int>(
                _ => { calls++; throw new InvalidOperationException("always fails"); },
                CancellationToken.None,
                maxAttempts: 3,
                initialDelay: TimeSpan.FromMilliseconds(1)));

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task RunAsync_CallerCancellation_RethrowsImmediatelyWithoutRetrying()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var calls = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            TransientRetry.RunAsync<int>(
                ct => { calls++; ct.ThrowIfCancellationRequested(); return Task.FromResult(0); },
                cts.Token,
                maxAttempts: 5,
                initialDelay: TimeSpan.FromSeconds(30)));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunAsync_ClientSideTimeout_RetriesEvenThoughExceptionTypeIsOperationCanceled()
    {
        // A client library's own request timeout can surface as OperationCanceledException even
        // though the caller's token was never signalled — this must be treated as transient (retried),
        // not mistaken for real shutdown. Distinguished from RunAsync_CallerCancellation... above by
        // cancellationToken.IsCancellationRequested being false here (CancellationToken.None).
        var calls = 0;
        var result = await TransientRetry.RunAsync(
            _ =>
            {
                calls++;
                if (calls < 2) throw new OperationCanceledException("client-side request timeout");
                return Task.FromResult(7);
            },
            CancellationToken.None,
            initialDelay: TimeSpan.FromMilliseconds(1));

        Assert.Equal(7, result);
        Assert.Equal(2, calls);
    }
}
