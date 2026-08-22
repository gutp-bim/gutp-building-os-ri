using BuildingOs.ApiServer.Protos;
using NATS.Client.Core;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace BuildingOs.ApiServer.Services;

/// <summary>
/// OSS result bus: subscribes to building-os.control.result.{controlId} on NATS and
/// delivers the ControlResultEvent to the waiting gRPC stream.
/// </summary>
public sealed class NatsControlResultBus : IControlResultBus, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly TimeSpan DefaultRetention = TimeSpan.FromMinutes(1);

    private readonly INatsConnection _nats;
    private readonly ILogger<NatsControlResultBus> _logger;
    private readonly TimeSpan _retention;
    private readonly ConcurrentDictionary<string, SubscriptionState> _subscriptions = new();

    public NatsControlResultBus(INatsConnection nats, ILogger<NatsControlResultBus> logger)
        : this(nats, logger, DefaultRetention)
    {
    }

    internal NatsControlResultBus(
        INatsConnection nats,
        ILogger<NatsControlResultBus> logger,
        TimeSpan retention)
    {
        _nats = nats;
        _logger = logger;
        _retention = retention;
    }

    public async Task PrepareAsync(string controlId, CancellationToken cancellationToken)
    {
        var candidate = new SubscriptionState();
        var state = _subscriptions.GetOrAdd(controlId, candidate);
        if (!ReferenceEquals(candidate, state))
            candidate.Dispose();

        Task startTask;
        lock (state.Gate)
        {
            state.StartTask ??= StartAsync(controlId, state);
            startTask = state.StartTask;
        }

        try
        {
            await startTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await RemoveAsync(controlId, state).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ChannelReader<ControlResultEvent>> SubscribeAsync(
        string controlId,
        CancellationToken cancellationToken)
    {
        await PrepareAsync(controlId, cancellationToken).ConfigureAwait(false);
        return _subscriptions[controlId].Channel.Reader;
    }

    public bool Publish(string controlId, ControlResultEvent evt)
        => _subscriptions.TryGetValue(controlId, out var state)
            && state.Channel.Writer.TryWrite(evt);

    public async Task UnsubscribeAsync(string controlId)
    {
        if (_subscriptions.TryRemove(controlId, out var state))
            await StopAsync(state).ConfigureAwait(false);
    }

    private async Task StartAsync(string controlId, SubscriptionState state)
    {
        var subject = $"building-os.control.result.{controlId}";
        state.Subscription = await _nats.SubscribeCoreAsync<byte[]>(
            subject,
            cancellationToken: state.Cancellation.Token).ConfigureAwait(false);

        // Fence the SUB command before the control request is published. A PONG confirms
        // the server processed all commands sent on this connection before the PING.
        await _nats.PingAsync(state.Cancellation.Token).ConfigureAwait(false);

        state.ConsumeTask = ConsumeAsync(controlId, state);
        state.ExpiryTask = ExpireAsync(controlId, state);
    }

    private async Task ConsumeAsync(string controlId, SubscriptionState state)
    {
        try
        {
            await foreach (var msg in state.Subscription!.Msgs.ReadAllAsync(state.Cancellation.Token))
            {
                if (msg.Data == null) continue;
                var json = Encoding.UTF8.GetString(msg.Data);
                var dto = JsonSerializer.Deserialize<ControlResultDto>(json, JsonOptions);
                if (dto == null) continue;

                var evt = new ControlResultEvent
                {
                    ControlId = controlId,
                    Result = dto.Success ? ControlResult.Success : ControlResult.Failed,
                    Response = dto.Response ?? string.Empty,
                };
                state.Channel.Writer.TryWrite(evt);
                break;
            }
        }
        catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NATS result subscription error for {ControlId}", controlId);
        }
        finally
        {
            state.Channel.Writer.TryComplete();
            await UnsubscribeFromNatsAsync(state).ConfigureAwait(false);
        }
    }

    private async Task ExpireAsync(string controlId, SubscriptionState state)
    {
        try
        {
            await Task.Delay(_retention, state.Cancellation.Token).ConfigureAwait(false);
            await RemoveAsync(controlId, state).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
        {
        }
    }

    private async Task RemoveAsync(string controlId, SubscriptionState state)
    {
        if (_subscriptions.TryRemove(new KeyValuePair<string, SubscriptionState>(controlId, state)))
            await StopAsync(state).ConfigureAwait(false);
    }

    private async Task StopAsync(SubscriptionState state)
    {
        if (Interlocked.Exchange(ref state.Stopped, 1) != 0)
            return;

        state.Cancellation.Cancel();
        await UnsubscribeFromNatsAsync(state).ConfigureAwait(false);

        if (state.StartTask is not null)
        {
            try
            {
                await state.StartTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "NATS result subscription stopped during startup");
            }
        }

        if (state.ConsumeTask is not null)
            await state.ConsumeTask.ConfigureAwait(false);

        state.Channel.Writer.TryComplete();
        state.Dispose();
    }

    private async Task UnsubscribeFromNatsAsync(SubscriptionState state)
    {
        if (state.Subscription is null
            || Interlocked.Exchange(ref state.NatsUnsubscribed, 1) != 0)
            return;

        try
        {
            await state.Subscription.UnsubscribeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NATS result subscription was already closed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var controlId in _subscriptions.Keys)
            await UnsubscribeAsync(controlId).ConfigureAwait(false);
    }

    private sealed class SubscriptionState : IDisposable
    {
        public object Gate { get; } = new();
        public Channel<ControlResultEvent> Channel { get; } =
            System.Threading.Channels.Channel.CreateBounded<ControlResultEvent>(1);
        public CancellationTokenSource Cancellation { get; } = new();
        public INatsSub<byte[]>? Subscription { get; set; }
        public Task? StartTask { get; set; }
        public Task? ConsumeTask { get; set; }
        public Task? ExpiryTask { get; set; }
        public int NatsUnsubscribed;
        public int Stopped;

        public void Dispose() => Cancellation.Dispose();
    }

    private record ControlResultDto(bool Success, string? Response);
}
