using BuildingOs.ApiServer.Protos;
using System.Threading.Channels;

namespace BuildingOs.ApiServer.Services;

/// <summary>
/// Abstracts result delivery for the gRPC streaming layer.
/// In Azure mode backed by ControlEventBus (in-memory); in OSS mode backed by NATS subscription.
/// </summary>
public interface IControlResultBus
{
    Task PrepareAsync(string controlId, CancellationToken cancellationToken);
    Task<ChannelReader<ControlResultEvent>> SubscribeAsync(string controlId, CancellationToken cancellationToken);
    bool Publish(string controlId, ControlResultEvent evt);
    Task UnsubscribeAsync(string controlId);
}
