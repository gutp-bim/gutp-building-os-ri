using BuildingOS.Shared.Infrastructure.ControlRouting;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Infrastructure.OxiGraph;

/// <summary>
/// On startup: (1) imports a Turtle seed file into OxiGraph when OXIGRAPH_SEED_TTL_PATH is set
/// and the store is empty; (2) validates device templates against OxiGraph when
/// OXIGRAPH_DEVICE_TEMPLATE_PATH is set — throws InvalidOperationException on mismatch to stop startup.
/// Note: designed for single-instance deployments. Multiple simultaneous instances may
/// each observe an empty store and import concurrently; add a distributed lock if needed.
/// </summary>
public sealed class OxiGraphSeedHostedService(
    OxiGraphClient client,
    ILogger<OxiGraphSeedHostedService> logger,
    IPointListUpdatePublisher? pointListUpdatePublisher = null) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var seedTtlPath = Environment.GetEnvironmentVariable("OXIGRAPH_SEED_TTL_PATH");
        var templatePath = Environment.GetEnvironmentVariable("OXIGRAPH_DEVICE_TEMPLATE_PATH");
        await RunAsync(seedTtlPath, templatePath, ct).ConfigureAwait(false);
    }

    // Internal for testing — allows injecting paths directly without env var manipulation.
    internal async Task RunAsync(string? seedTtlPath, string? templatePath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(seedTtlPath))
            logger.LogDebug("OXIGRAPH_SEED_TTL_PATH not set; skipping seed import");
        else
        {
            await TrySeedAsync(seedTtlPath, ct).ConfigureAwait(false);

            // gateway_id must be globally unique: a gateway addresses a point by gateway_id +
            // point_id (ingress provenance/ownership, egress per-gateway routing), so the same id
            // reused across buildings would misroute. Validate the imported store and fail startup
            // loudly rather than silently corrupt routing.
            await ValidateGatewayUniquenessAsync(ct).ConfigureAwait(false);

            // #224/push: the twin (point-list source of truth) just changed — signal each gateway to
            // revalidate. Best-effort: never fault startup, and skip when no publisher is wired
            // (OSS/local without GatewayBridge).
            await PublishPointListUpdatesAsync(ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(templatePath))
            await ValidateDeviceTemplatesAsync(templatePath, ct).ConfigureAwait(false);
    }

    // internal (not private): lets tests route a fake OxiGraph response by exact query text instead
    // of a fragile content heuristic — see OxiGraphSeedHostedServicePointListPushTest.
    internal const string DistinctGatewayQuery = """
        PREFIX sbco: <https://www.sbco.or.jp/ont/>
        SELECT DISTINCT ?gatewayId WHERE {
          ?point a sbco:PointExt ; sbco:gatewayId ?gatewayId .
        }
        """;

    private async Task PublishPointListUpdatesAsync(CancellationToken ct)
    {
        if (pointListUpdatePublisher is null) return;

        IReadOnlyList<IReadOnlyDictionary<string, string>> rows;
        try
        {
            rows = await client.QueryAsync(DistinctGatewayQuery, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Point-list-update publish after seed failed (non-fatal): could not list gateway ids");
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
                // Empty revision → gateway revalidates via ETag (the seed does not compute the etag).
                await pointListUpdatePublisher.PublishAsync(gatewayId, string.Empty, ct).ConfigureAwait(false);
                published++;
                CountPush(gatewayId, "published");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Point-list-update publish failed for gateway {GatewayId} (non-fatal)", gatewayId);
                CountPush(gatewayId, "failed");
            }
        }
        logger.LogInformation("Published point-list-update signals for {Count} gateway(s) after seed", published);
    }

    private static void CountPush(string gatewayId, string result) =>
        BuildingOsMetrics.PointListPushSignals.Add(
            1,
            new KeyValuePair<string, object?>("gateway", gatewayId),
            new KeyValuePair<string, object?>("result", result));

    // Building membership is read from the denormalized sbco:building literal on PointExt — the same
    // convention the ingress metadata enrichment (OxiGraphPointMetadataDataSource) relies on. Points
    // that omit it are not covered; if a future twin models building only via the
    // Site→Building→Level→Room hierarchy, derive ?building through that path instead.
    internal const string GatewayUniquenessQuery = """
        PREFIX sbco: <https://www.sbco.or.jp/ont/>
        SELECT ?gatewayId (COUNT(DISTINCT ?building) AS ?buildings) WHERE {
          ?point a sbco:PointExt ;
                 sbco:gatewayId ?gatewayId ;
                 sbco:building ?building .
        }
        GROUP BY ?gatewayId
        HAVING (COUNT(DISTINCT ?building) > 1)
        """;

    // Throws if any gateway_id is associated with points in more than one building.
    private async Task ValidateGatewayUniquenessAsync(CancellationToken ct)
    {
        var rows = await client.QueryAsync(GatewayUniquenessQuery, ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            logger.LogDebug("Gateway id uniqueness check passed");
            return;
        }

        var detail = string.Join("; ", rows.Select(r =>
            $"{r.GetValueOrDefault("gatewayId", "?")} → {r.GetValueOrDefault("buildings", "?")} buildings"));
        throw new InvalidOperationException(
            $"Gateway id uniqueness violated — a gateway_id must belong to a single building, but {rows.Count} " +
            $"gateway(s) span multiple buildings: {detail}");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task TrySeedAsync(string seedTtlPath, CancellationToken ct)
    {
        if (!File.Exists(seedTtlPath))
        {
            logger.LogWarning("OxiGraph seed file not found at {Path}; skipping", seedTtlPath);
            return;
        }

        try
        {
            var turtle = await File.ReadAllTextAsync(seedTtlPath, ct).ConfigureAwait(false);
            await client.ReplaceDefaultGraphAsync(turtle, ct).ConfigureAwait(false);
            logger.LogInformation("Imported OxiGraph seed RDF from {Path}", seedTtlPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OxiGraph seed import failed; continuing startup");
        }
    }

    private async Task ValidateDeviceTemplatesAsync(string templatePath, CancellationToken ct)
    {
        if (!File.Exists(templatePath))
        {
            logger.LogWarning(
                "Device template file not found at {Path}; skipping validation", templatePath);
            return;
        }

        logger.LogInformation("Validating device templates from {Path}", templatePath);
        var templates = await DeviceTemplateParser.LoadAsync(templatePath, ct).ConfigureAwait(false);

        if (templates.Length == 0)
        {
            logger.LogWarning(
                "Device template file {Path} contains no parseable templates; skipping validation", templatePath);
            return;
        }

        var errors = await DeviceTemplateValidator.ValidateAsync(templates, client, ct).ConfigureAwait(false);

        if (errors.Length == 0)
        {
            logger.LogInformation("Device template validation passed ({Count} template(s))", templates.Length);
            return;
        }

        var detail = string.Join("; ", errors.Select(e =>
            $"{e.EquipmentId} ({e.DeviceType}): missing [{string.Join(", ", e.MissingPointTypes)}]"));
        throw new InvalidOperationException(
            $"Device template validation failed — {errors.Length} equipment instance(s) have missing points. {detail}");
    }
}
