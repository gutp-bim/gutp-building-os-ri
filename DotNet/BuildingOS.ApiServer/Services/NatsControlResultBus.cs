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
public sealed class NatsControlResultBus(INatsConnection nats, ILogger<NatsControlResultBus> logger)
    : IControlResultBus, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ConcurrentDictionary<string, Channel<ControlResultEvent>> _channels = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _subscriptions = new();

    public void Prepare(string controlId)
    {
        var channel = _channels.GetOrAdd(controlId, _ => Channel.CreateBounded<ControlResultEvent>(1));

        _subscriptions.GetOrAdd(controlId, _controlId =>
        {
            var cts = new CancellationTokenSource();
            _ = Task.Run(() => ConsumeAsync(controlId, channel, cts.Token), cts.Token);
            return cts;
        });
    }

    public ChannelReader<ControlResultEvent> Subscribe(string controlId)
    {
        Prepare(controlId);
        return _channels[controlId].Reader;
    }

    public bool Publish(string controlId, ControlResultEvent evt)
    {
        if (_channels.TryGetValue(controlId, out var channel))
            return channel.Writer.TryWrite(evt);
        return false;
    }

    public void Unsubscribe(string controlId)
    {
        if (_subscriptions.TryRemove(controlId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        if (_channels.TryRemove(controlId, out var channel))
            channel.Writer.TryComplete();
    }

    private async Task ConsumeAsync(string controlId, Channel<ControlResultEvent> channel,
        CancellationToken cancellationToken)
    {
        var subject = $"building-os.control.result.{controlId}";
        try
        {
            await foreach (var msg in nats.SubscribeAsync<byte[]>(subject, cancellationToken: cancellationToken))
            {
                if (msg.Data == null) continue;
                var json = Encoding.UTF8.GetString(msg.Data);
                var dto = JsonSerializer.Deserialize<ControlResultDto>(json, JsonOptions);
                if (dto == null) continue;

                var evt = new ControlResultEvent
                {
                    ControlId = controlId,
                    Result = dto.Success
                        ? ControlResult.Success
                        : ControlResult.Failed,
                    Response = dto.Response ?? string.Empty,
                };
                channel.Writer.TryWrite(evt);
                break; // one result per control
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "NATS result subscription error for {ControlId}", controlId);
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, cts) in _subscriptions)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _subscriptions.Clear();
        _channels.Clear();
        await ValueTask.CompletedTask;
    }

    private record ControlResultDto(bool Success, string? Response);
}
