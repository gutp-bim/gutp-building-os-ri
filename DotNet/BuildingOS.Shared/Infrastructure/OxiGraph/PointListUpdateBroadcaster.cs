using BuildingOS.Shared.Infrastructure.ControlRouting;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Infrastructure.OxiGraph;

/// <summary>
/// Signals every gateway named in the twin to revalidate its point list (#224 push optimization —
/// ETag polling remains the reliability backstop). Shared by every path that changes the twin's
/// point set: the startup seed (<see cref="OxiGraphSeedHostedService"/>) and admin twin import
/// apply (<see cref="OxiGraphTwinAdminService.ApplyImportAsync"/>, #414 — that path previously
/// never called this, so an admin edit was invisible to gateways until their next 10-minute poll).
/// Best-effort and per-gateway: a publish failure is logged and metered but never thrown, so it
/// never faults the caller's own operation.
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

    public static async Task PublishAllAsync(
        OxiGraphClient client,
        IPointListUpdatePublisher? publisher,
        ILogger logger,
        CancellationToken ct)
    {
        if (publisher is null) return;

        IReadOnlyList<IReadOnlyDictionary<string, string>> rows;
        try
        {
            rows = await client.QueryAsync(DistinctGatewayQuery, ct).ConfigureAwait(false);
        }
        // Cancellation (e.g. the caller's own request aborting, or host shutdown for the seed path)
        // is not a publish failure — propagate it rather than logging a misleading "failed" and
        // returning as if the broadcast had merely found no gateways.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Point-list-update publish failed (non-fatal): could not list gateway ids");
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
                // Empty revision → gateway revalidates via ETag (the caller does not compute the etag).
                await publisher.PublishAsync(gatewayId, string.Empty, ct).ConfigureAwait(false);
                published++;
                CountPush(gatewayId, "published");
            }
            // Same reasoning as above: stop promptly on cancellation instead of logging every
            // remaining gateway as a "failed" publish and continuing to loop over a dead token.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Point-list-update publish failed for gateway {GatewayId} (non-fatal)", gatewayId);
                CountPush(gatewayId, "failed");
            }
        }
        logger.LogInformation("Published point-list-update signals for {Count} gateway(s)", published);
    }

    private static void CountPush(string gatewayId, string result) =>
        BuildingOsMetrics.PointListPushSignals.Add(
            1,
            new KeyValuePair<string, object?>("gateway", gatewayId),
            new KeyValuePair<string, object?>("result", result));
}
