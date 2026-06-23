using BuildingOS.GatewayBridge.Infrastructure;
using BuildingOS.GatewayBridge.Mapping;
using BuildingOS.GatewayBridge.Protos;
using Grpc.Core;

namespace BuildingOS.GatewayBridge.Services;

/// <summary>
/// gRPC <c>GatewayEgress</c>: one bidirectional stream per gateway. On Hello the bridge subscribes
/// to the gateway's per-gateway request subject and forwards each command down the stream; results
/// returned up the stream are published to the result subject for WaitForResult. State is in-memory
/// only — disconnect tears the subscription down (plan §3-3).
/// </summary>
public sealed class GatewayEgressService(
    IEgressCommandBus bus,
    GatewayConnectionRegistry registry,
    ILogger<GatewayEgressService> logger) : Protos.GatewayEgress.GatewayEgressBase
{
    public override Task Connect(
        IAsyncStreamReader<EgressUp> requestStream,
        IServerStreamWriter<EgressDown> responseStream,
        ServerCallContext context)
        => RunAsync(requestStream, responseStream, context.CancellationToken);

    /// <summary>Transport-agnostic core (testable without a live gRPC channel / NATS).</summary>
    internal async Task RunAsync(
        IAsyncStreamReader<EgressUp> requestStream,
        IServerStreamWriter<EgressDown> responseStream,
        CancellationToken ct)
    {
        if (!await requestStream.MoveNext(ct).ConfigureAwait(false)) return;
        var first = requestStream.Current;
        if (first.MCase != EgressUp.MOneofCase.Hello || string.IsNullOrEmpty(first.Hello.GatewayId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "first frame must be Hello with gateway_id"));

        var gatewayId = first.Hello.GatewayId;
        if (!registry.TryRegister(gatewayId))
            throw new RpcException(new Status(StatusCode.AlreadyExists, $"gateway {gatewayId} already connected to this replica"));

        // Everything after a successful TryRegister runs under try/finally so the gateway is always
        // unregistered — including when bus.SubscribeAsync itself throws (e.g. a transient NATS drop).
        // Otherwise a failed subscribe would leave the gateway registered and locked out (AlreadyExists)
        // on every reconnect until the pod restarts.
        try
        {
            logger.LogInformation("Gateway {GatewayId} connected (egress)", gatewayId);

            // Two subscription loops (commands + point-list updates) write to responseStream, so
            // serialize writes — gRPC does not allow concurrent WriteAsync on one stream.
            var writeGate = new SemaphoreSlim(1, 1);
            async Task WriteDownAsync(EgressDown down)
            {
                await writeGate.WaitAsync(ct).ConfigureAwait(false);
                try { await responseStream.WriteAsync(down).ConfigureAwait(false); }
                finally { writeGate.Release(); }
            }

            await using var subscription = await bus.SubscribeAsync(gatewayId, async commandJson =>
            {
                var command = ControlCommandMapper.ToControlCommand(commandJson);
                if (command is null)
                {
                    logger.LogWarning("Dropping unmappable command for gateway {GatewayId}", gatewayId);
                    return;
                }
                await WriteDownAsync(new EgressDown { Command = command }).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            // #224/push: forward point-list-changed signals so the gateway revalidates its shared list.
            await using var pointListSub = await bus.SubscribePointListUpdatesAsync(gatewayId, async revision =>
            {
                await WriteDownAsync(new EgressDown
                {
                    PointListUpdate = new PointListUpdate { GatewayId = gatewayId, Revision = revision ?? string.Empty },
                }).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            while (await requestStream.MoveNext(ct).ConfigureAwait(false))
            {
                var up = requestStream.Current;
                if (up.MCase != EgressUp.MOneofCase.Result) continue;
                var result = up.Result;
                await bus.PublishResultAsync(result.ControlId, ControlCommandMapper.ToResultJson(result), ct)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* client/stream cancelled — fall through to cleanup */ }
        finally
        {
            registry.Unregister(gatewayId);
            logger.LogInformation("Gateway {GatewayId} disconnected (egress)", gatewayId);
        }
    }
}
