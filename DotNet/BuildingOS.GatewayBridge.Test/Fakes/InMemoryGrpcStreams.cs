using System.Threading.Channels;
using Grpc.Core;

namespace BuildingOS.GatewayBridge.Test;

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

/// <summary>Channel-backed <see cref="IServerStreamWriter{T}"/> that records what the server wrote.</summary>
public sealed class FakeStreamWriter<T> : IServerStreamWriter<T>
{
    private readonly Channel<T> _channel = Channel.CreateUnbounded<T>();
    public WriteOptions? WriteOptions { get; set; }

    public Task WriteAsync(T message)
    {
        _channel.Writer.TryWrite(message);
        return Task.CompletedTask;
    }

    public async Task<T> ReadAsync() => await _channel.Reader.ReadAsync();

    public bool TryReadImmediately(out T? item)
    {
        var ok = _channel.Reader.TryRead(out var read);
        item = read;
        return ok;
    }
}
