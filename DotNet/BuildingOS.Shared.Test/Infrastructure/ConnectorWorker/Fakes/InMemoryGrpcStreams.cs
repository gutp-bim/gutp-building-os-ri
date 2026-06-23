using System.Threading.Channels;
using Grpc.Core;

namespace BuildingOS.Shared.Test.Infrastructure.ConnectorWorker.Fakes;

/// <summary>Channel-backed <see cref="IAsyncStreamReader{T}"/> the test controls: push frames, then complete.</summary>
public sealed class FakeStreamReader<T> : IAsyncStreamReader<T>
{
    private readonly Channel<T> _channel = Channel.CreateUnbounded<T>();
    public T Current { get; private set; } = default!;

    public void Push(T item) => _channel.Writer.TryWrite(item);
    public void Complete() => _channel.Writer.TryComplete();

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        if (await _channel.Reader.WaitToReadAsync(cancellationToken) && _channel.Reader.TryRead(out var item))
        {
            Current = item;
            return true;
        }
        return false;
    }
}
