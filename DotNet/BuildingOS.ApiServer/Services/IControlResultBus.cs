using BuildingOs.ApiServer.Protos;
using System.Threading.Channels;

namespace BuildingOs.ApiServer.Services;

/// <summary>
/// Abstracts result delivery for the gRPC streaming layer.
/// In Azure mode backed by ControlEventBus (in-memory); in OSS mode backed by NATS subscription.
/// </summary>
public interface IControlResultBus
{
    ChannelReader<ControlResultEvent> Subscribe(string controlId);
    bool Publish(string controlId, ControlResultEvent evt);
    void Unsubscribe(string controlId);
}
