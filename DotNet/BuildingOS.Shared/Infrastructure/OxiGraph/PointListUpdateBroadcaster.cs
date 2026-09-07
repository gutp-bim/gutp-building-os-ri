using BuildingOS.Shared.Infrastructure.ControlRouting;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Infrastructure.OxiGraph;

/// <summary>
/// #224/push fan-out shared by every twin-mutation path that must revalidate gateways afterwards
/// (startup seed, twin admin import apply, ...): lists every distinct gateway id currently present in
/// the twin and signals each one via <see cref="IPointListUpdatePublisher"/> — best-effort and
/// per-gateway, so one gateway's publish failure never blocks the others or faults the caller.
/// </summary>
internal static class PointListUpdateBroadcaster
{
    // internal (not private): lets tests route a fake OxiGraph response by exact query text instead
    // of a fragile content heuristic.
    internal const string DistinctGatewayQuery = """
        PREFIX sbco: <https://www.sbco.or.jp/ont/>
        SELECT DISTINCT ?gatewayId WHERE {
          ?point a sbco:PointExt ; sbco:gatewayId ?gatewayId .
        }
        """;

    internal static async Task PublishAllAsync(
        OxiGraphClient client,
        IPointListUpdatePublisher? publisher,
        ILogger logger,
        string context,
        CancellationToken ct)
    {
        if (publisher is null) return;

        IReadOnlyList<IReadOnlyDictionary<string, string>> rows;
        try
        {
            rows = await client.QueryAsync(DistinctGatewayQuery, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Point-list-update publish {Context} failed (non-fatal): could not list gateway ids", context);
            CountPush("*", "query_failed");
            return;
        }

        // Each gateway is published independently (#114): one gateway's publish failure must not
        // prevent the others from being signalled, so the try/catch is per-iteration, not around the
        // whole loop.
        var published = 0;
        foreach (var r in rows)
        {
            var gatewayId = r.GetValueOrDefault("gatewayId");
            if (string.IsNullOrEmpty(gatewayId)) continue;
            try
            {
                // Empty revision → gateway revalidates via ETag (the caller does not compute one).
                await publisher.PublishAsync(gatewayId, string.Empty, ct).ConfigureAwait(false);
                published++;
                CountPush(gatewayId, "published");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Point-list-update publish failed for gateway {GatewayId} {Context} (non-fatal)", gatewayId, context);
                CountPush(gatewayId, "failed");
            }
        }
        logger.LogInformation("Published point-list-update signals for {Count} gateway(s) {Context}", published, context);
    }

    private static void CountPush(string gatewayId, string result) =>
        BuildingOsMetrics.PointListPushSignals.Add(
            1,
            new KeyValuePair<string, object?>("gateway", gatewayId),
            new KeyValuePair<string, object?>("result", result));
}
