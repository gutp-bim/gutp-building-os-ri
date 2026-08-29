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
    OxiGraphIngestMaterializer materializer,
    ILogger<OxiGraphSeedHostedService> logger,
    IPointListUpdatePublisher? pointListUpdatePublisher = null,
    TimeSpan? startupTimeout = null) : IHostedService
{
    /// <summary>
    /// How long to wait for OxiGraph to start accepting connections before giving up (#321).
    /// Override with OXIGRAPH_STARTUP_TIMEOUT_SEC.
    /// </summary>
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _startupTimeout = startupTimeout ?? ResolveStartupTimeout();

    private static TimeSpan ResolveStartupTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("OXIGRAPH_STARTUP_TIMEOUT_SEC");
        return int.TryParse(raw, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : DefaultStartupTimeout;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var seedTtlPath = Environment.GetEnvironmentVariable("OXIGRAPH_SEED_TTL_PATH");
        var templatePath = Environment.GetEnvironmentVariable("OXIGRAPH_DEVICE_TEMPLATE_PATH");
        await RunAsync(seedTtlPath, templatePath, ct).ConfigureAwait(false);
    }

    // Internal for testing — allows injecting paths directly without env var manipulation.
    internal async Task RunAsync(string? seedTtlPath, string? templatePath, CancellationToken ct)
    {
        var waited = false;

        if (string.IsNullOrEmpty(seedTtlPath))
            logger.LogDebug("OXIGRAPH_SEED_TTL_PATH not set; skipping seed import");
        else
        {
            // Compose starts OxiGraph alongside the API, and its image carries no healthcheck, so
            // the store's listener may not be bound yet. Everything below talks to OxiGraph, and
            // the uniqueness validation in particular has no error handling of its own — an
            // unbound port used to propagate straight out of StartAsync and kill the process (#321).
            await WaitForOxiGraphAsync(ct).ConfigureAwait(false);
            waited = true;

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

            // #336: a writable point with a missing/malformed bos: control schema fails open at
            // control time (ControlValueValidator skips validation) with no signal at all. The admin
            // import UI catches this on a re-import (OxiGraphTwinAdminService.PreviewImportAsync), but
            // this startup seed never goes through that path — so run the same detection here too,
            // as a non-fatal warning. Never blocks startup: fail-open for control means fail-open here.
            await LogControlSchemaIssuesAsync(ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(templatePath))
            await ValidateDeviceTemplatesAsync(templatePath, ct, alreadyWaited: waited).ConfigureAwait(false);
    }

    /// <summary>
    /// Cheapest possible round trip that proves the store is answering queries. internal for the
    /// same reason as the two query constants below — test fakes route on exact query text.
    /// </summary>
    internal const string ReadinessQuery = "SELECT ?s WHERE { ?s ?p ?o } LIMIT 1";

    /// <summary>
    /// Minimum time a probe is given once the budget is nearly spent, so the attempt made at the
    /// deadline boundary is a real one rather than cancelled on arrival. Total overshoot is capped
    /// at this, against the ~100s an unbounded HttpClient probe could otherwise take.
    /// </summary>
    private static readonly TimeSpan FinalProbeGrace = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Blocks until OxiGraph answers a query, or the startup budget elapses.
    /// </summary>
    /// <remarks>
    /// Only transport-level failures are retried. A store that answers with an error is reporting a
    /// real problem — retrying it would just delay a diagnosis by the whole budget. Backoff grows
    /// to a 2s ceiling: the wait is usually a second or two of container startup, so a long tail
    /// would add avoidable dead time to every boot.
    /// </remarks>
    private async Task WaitForOxiGraphAsync(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + _startupTimeout;
        var delay = TimeSpan.FromMilliseconds(100);
        var attempts = 0;
        Exception? last = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempts++;

            // Bound the probe itself, not just the gap between probes. A store that accepts the
            // connection and then stalls answers nothing, and the deadline is only examined
            // between attempts — so a single probe would sit for HttpClient's default 100s and
            // overrun a shorter budget wholesale. Near the deadline the boundary attempt still
            // gets a small grace, otherwise clamping the sleep to the remainder buys nothing.
            var probeBudget = deadline - DateTimeOffset.UtcNow;
            if (probeBudget < FinalProbeGrace)
                probeBudget = FinalProbeGrace;

            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(probeBudget);
            try
            {
                await client.QueryAsync(ReadinessQuery, probeCts.Token).ConfigureAwait(false);
                if (attempts > 1)
                    logger.LogInformation("OxiGraph became reachable after {Attempts} attempts", attempts);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransientStartupFailure(ex))
            {
                last = ex;
            }

            // Clamp the sleep to what is left rather than giving up as soon as a full backoff step
            // would overshoot: otherwise the budget is effectively short by up to one delay (2s at
            // the ceiling), and a store that binds just inside the configured timeout is failed.
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    $"OxiGraph did not become reachable within {_startupTimeout.TotalSeconds:0}s " +
                    $"({attempts} attempts). Startup needs it for the RDF seed import, the gateway " +
                    "uniqueness check and device-template validation. Check that the OxiGraph " +
                    "service is running and that " +
                    "OXIGRAPH_ENDPOINT points at it; raise OXIGRAPH_STARTUP_TIMEOUT_SEC if the store " +
                    "legitimately takes longer to start.", last);
            }

            var wait = delay < remaining ? delay : remaining;
            logger.LogInformation(
                "OxiGraph not reachable yet (attempt {Attempt}); retrying in {Delay}", attempts, wait);
            await Task.Delay(wait, ct).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 2000));
        }
    }

    // A refused connection or an unresolvable name is the startup race, not a misconfiguration:
    // the container is coming up. HttpRequestException also covers DNS failure while Compose is
    // still wiring the network.
    //
    // But only when no response arrived: QueryAsync calls EnsureSuccessStatusCode, which reports a
    // non-2xx as HttpRequestException too. A store answering 500 is reporting a real problem, and
    // retrying it would bury that behind the whole budget — exactly what the remarks above promise
    // not to do. StatusCode is null precisely when the request never got an HTTP response, so it is
    // the transport/application split we want.
    //
    // A request timeout also surfaces as TaskCanceledException — keyed off *our* token rather than
    // the exception's, because HttpClient's timeout cancels an internal linked source, so
    // TaskCanceledException.CancellationToken is that source's token and never `default`. Testing
    // the exception's token would silently classify every real timeout as fatal. Our own
    // cancellation is already rethrown by the filter above, so reaching here means the caller did
    // not cancel.
    private static bool IsTransientStartupFailure(Exception ex) =>
        ex is HttpRequestException { StatusCode: null } or TaskCanceledException;

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

    // #336: non-fatal by design — a schema issue must never fail startup (fail-open at control time
    // means fail-open here too). Reuses OxiGraphTwinAdminService's detection query/classifier against
    // the plain default graph (graph/mode both null — the seed has already fully materialized, so
    // there is no staging graph to scope against, unlike PreviewImportAsync).
    //
    // internal for the same reason as the three query constants above — test fakes route on exact
    // query text. static readonly (not const): built from a method call, not a literal.
    internal static readonly string ControlSchemaIssueQuery =
        $"SELECT ?pt ?dataType ?enumLabels WHERE {{ {OxiGraphTwinAdminService.ControlSchemaIssuePattern(null, null)} }}";

    private async Task LogControlSchemaIssuesAsync(CancellationToken ct)
    {
        try
        {
            var rows = await client.QueryAsync(ControlSchemaIssueQuery, ct).ConfigureAwait(false);
            var (count, issues) = OxiGraphTwinAdminService.ClassifyControlSchemaRows(rows, cap: 1000);
            if (count > 0)
            {
                var byReason = issues.GroupBy(i => i.Reason).Select(g => $"{g.Key}={g.Count()}");
                logger.LogWarning(
                    "Control-schema issues detected after seed: {Count} writable point(s) ({Reasons})",
                    count, string.Join(", ", byReason));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Control-schema issue check after seed failed (non-fatal)");
        }
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
            await materializer.MaterializeAsync(turtle, ct).ConfigureAwait(false);
            logger.LogInformation("Imported OxiGraph seed RDF from {Path}", seedTtlPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OxiGraph seed import failed; continuing startup");
        }
    }

    /// <param name="alreadyWaited">
    /// Whether the seed branch has already waited for OxiGraph, so this path does not wait twice.
    /// </param>
    private async Task ValidateDeviceTemplatesAsync(
        string templatePath, CancellationToken ct, bool alreadyWaited)
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

        // Only now is a store query certain. Waiting any earlier — on the mere presence of
        // OXIGRAPH_DEVICE_TEMPLATE_PATH — would turn a missing or empty template file, which is
        // only a warning, into a hard startup dependency on OxiGraph that can time out and fail.
        if (!alreadyWaited)
            await WaitForOxiGraphAsync(ct).ConfigureAwait(false);

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
