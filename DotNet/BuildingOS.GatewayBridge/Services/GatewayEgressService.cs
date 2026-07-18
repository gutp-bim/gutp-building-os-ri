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

        // Supersede policy: always accept the connection. If the gateway already had a stream on this
        // replica (a reconnect over a still-half-open stream), Register cancels the old one's
        // SupersededToken so it tears down — no AlreadyExists lock-out on reconnect (plan §3-3).
        var connection = registry.Register(gatewayId);

        // Fold "the newer connection superseded me" into the same cancellation the transport uses, so
        // both the request loop and the two subscription writers stop on either signal.
        var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct, connection.SupersededToken);
        var streamCt = streamCts.Token;

        // Everything after Register runs under try/finally so the gateway is always unregistered —
        // including when bus.SubscribeAsync itself throws (e.g. a transient NATS drop). Otherwise a
        // failed subscribe would leave the gateway registered and unreachable until the pod restarts.
        try
        {
            logger.LogInformation("Gateway {GatewayId} connected (egress)", gatewayId);

            // Two subscription loops (commands + point-list updates) write to responseStream, so
            // serialize writes — gRPC does not allow concurrent WriteAsync on one stream.
            var writeGate = new SemaphoreSlim(1, 1);
            async Task WriteDownAsync(EgressDown down)
            {
                await writeGate.WaitAsync(streamCt).ConfigureAwait(false);
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
            }, streamCt).ConfigureAwait(false);

            // #224/push: forward point-list-changed signals so the gateway revalidates its shared list.
            await using var pointListSub = await bus.SubscribePointListUpdatesAsync(gatewayId, async revision =>
            {
                await WriteDownAsync(new EgressDown
                {
                    PointListUpdate = new PointListUpdate { GatewayId = gatewayId, Revision = revision ?? string.Empty },
                }).ConfigureAwait(false);
            }, streamCt).ConfigureAwait(false);

            while (await requestStream.MoveNext(streamCt).ConfigureAwait(false))
            {
                var up = requestStream.Current;
                if (up.MCase != EgressUp.MOneofCase.Result) continue;
                var result = up.Result;
                await bus.PublishResultAsync(result.ControlId, ControlCommandMapper.ToResultJson(result), streamCt)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* client/stream cancelled or superseded — fall through to cleanup */ }
        finally
        {
            streamCts.Dispose();
            registry.Unregister(connection); // epoch-guarded: a no-op if a newer connection already took over
            connection.Dispose();
            logger.LogInformation(
                "Gateway {GatewayId} disconnected (egress){Reason}",
                gatewayId,
                connection.IsSuperseded ? " — superseded by a newer connection" : string.Empty);
        }
    }
}
