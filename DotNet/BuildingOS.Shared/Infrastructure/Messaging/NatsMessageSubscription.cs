using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace BuildingOS.Shared.Infrastructure.Messaging;

/// <summary>
/// NATS JetStream consumer implementation for Worker Service hosts (OSS mode).
/// Subject convention: building-os.raw.{connector-name}
/// </summary>
public class NatsMessageSubscription : IMessageSubscription, IAsyncDisposable
{
    private readonly string _subject;
    private readonly string _durableName;
    private readonly INatsConnection _connection;
    private readonly ILogger _logger;
    private readonly List<Func<string, Task>> _handlers = new();
    private CancellationTokenSource? _cts;
    private Task? _consumeLoop;

    public NatsMessageSubscription(string subject, string durableName, INatsConnection connection, ILogger? logger = null)
    {
        _subject = subject;
        _durableName = durableName;
        _connection = connection;
        _logger = logger ?? NullLogger.Instance;
    }

    public void Register(Func<string, Task> handler)
        => _handlers.Add(handler);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var js = new NatsJSContext((NatsConnection)_connection);

        // Resolve the canonical stream + full subject set for this subject so
        // the stream is created with every subject it must capture, regardless
        // of which worker starts first. See docs/oss-nats-design.md.
        var (streamName, streamSubjects) = NatsStreamTopology.ResolveOrThrow(_subject);

        // Retried: right after NATS/JetStream comes up, these calls can transiently time out
        // (surfaces as OperationCanceledException from the client's own request timeout, not the
        // caller's stoppingToken) — without a retry this crashes the whole worker host on first
        // boot (#61). Genuine shutdown (cancellationToken itself signalled) still aborts immediately.
        await TransientRetry.RunAsync(async ct =>
        {
            try
            {
                await js.GetStreamAsync(streamName, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await js.CreateStreamAsync(new StreamConfig(streamName, streamSubjects), ct).ConfigureAwait(false);
            }
            return true;
        }, _cts.Token, logger: _logger, operationName: $"NatsMessageSubscription: ensure stream {streamName}")
            .ConfigureAwait(false);

        var consumer = await TransientRetry.RunAsync(
            ct => js.CreateOrUpdateConsumerAsync(streamName,
                new ConsumerConfig(_durableName) { FilterSubject = _subject }, ct).AsTask(),
            _cts.Token, logger: _logger,
            operationName: $"NatsMessageSubscription: create consumer {_durableName}")
            .ConfigureAwait(false);

        _consumeLoop = Task.Run(async () =>
        {
            await foreach (var msg in consumer.ConsumeAsync<string>(cancellationToken: _cts.Token))
            {
                foreach (var handler in _handlers)
                {
                    try
                    {
                        await handler(msg.Data ?? string.Empty);
                    }
                    catch (Exception ex)
                    {
                        // Handler exceptions are isolated so AckAsync is always reached.
                        _logger.LogError(ex, "NatsMessageSubscription: handler failed for subject {Subject}", _subject);
                    }
                }
                await msg.AckAsync(cancellationToken: _cts.Token);
            }
        }, _cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_consumeLoop is not null)
        {
            try { await _consumeLoop; }
            catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
    }
}
