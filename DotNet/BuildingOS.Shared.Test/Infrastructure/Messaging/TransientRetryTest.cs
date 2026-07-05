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
}
