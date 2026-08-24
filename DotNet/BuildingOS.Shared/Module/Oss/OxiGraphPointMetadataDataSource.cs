using System.Diagnostics;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Module.Oss;

/// <summary>
/// Loads all point metadata (building / name / gatewayId / owning device id) from OxiGraph via
/// SPARQL. Performs no caching — <see cref="PointMetadataCache"/> owns the cache lifecycle.
/// <para>
/// <b>Three queries, joined in memory (#371).</b> This used to be ONE query. See the comment above
/// <see cref="PointProjectionQuery"/> for why it must stay split.
/// </para>
/// </summary>
public sealed class OxiGraphPointMetadataDataSource : IPointMetadataDataSource
{
    /// <summary>
    /// Per-call bound on each of the three SPARQL round trips.
    /// <para>
    /// The shared <c>AddHttpClient("oxigraph")</c> configures no timeout, so .NET's 100 s default
    /// applies — and that client is shared with the bulk twin seed upload, which legitimately takes a
    /// long time, so it must not be tightened globally. This data source therefore bounds its OWN
    /// calls. 30 s is ~100× headroom over the post-#371 cost (~0.3 s at 3,000 points) while still
    /// failing fast enough that <c>PointMetadataCache.LoadWithRetryAsync</c> gets to retry instead of
    /// leaving every parked ingress stream waiting out 100 s.
    /// </para>
    /// </summary>
    public static readonly TimeSpan DefaultQueryTimeout = TimeSpan.FromSeconds(30);

    private readonly OxiGraphClient _client;
    private readonly ILogger<OxiGraphPointMetadataDataSource> _logger;
    private readonly TimeSpan _queryTimeout;

    public OxiGraphPointMetadataDataSource(OxiGraphClient client, TimeSpan? queryTimeout = null)
        : this(client, null, queryTimeout)
    {
    }

    public OxiGraphPointMetadataDataSource(
        OxiGraphClient client,
        ILogger<OxiGraphPointMetadataDataSource>? logger,
        TimeSpan? queryTimeout = null)
    {
        _client = client;
        _logger = logger ?? NullLogger<OxiGraphPointMetadataDataSource>.Instance;
        _queryTimeout = queryTimeout is { } t && (t > TimeSpan.Zero || t == Timeout.InfiniteTimeSpan)
            ? t
            : DefaultQueryTimeout;
    }

    // -------------------------------------------------------------------------------------------
    // Why this is three queries and must not be merged back into one (#371).
    //
    // The single bulk query this replaces was QUADRATIC in point count: 0.25 s at 100 points, 7.6 s
    // at 1,000, 23 s at 1,865 (the THX scale), 57 s at 3,000 — far enough that under load it reached
    // the HttpClient 100 s default and the load failed outright, stalling every ingress stream parked
    // behind the shared cache load.
    //
    // Ablation isolated the quadratic term to ONE line:
    //   OPTIONAL { ?equip a sbco:EquipmentExt ; sbco:hasPoint ?point ; sbco:id ?deviceId }
    // Dropping just that OPTIONAL took 1,000 points from 15.6 s to 0.04 s and 3,000 from 131 s to
    // 0.09 s. Reordering its triples does not help; the GROUP BY/SAMPLE costs nothing; the hierarchy
    // UNION is expensive only while it sits in the same query as that OPTIONAL (0.17 s at 1,000 and
    // 1.18 s at 3,000 when asked on its own).
    //
    // So the load is now: point projection + an EQUIPMENT-DRIVEN point→device link + a building
    // reachability set, joined by point id in Merge below (~0.15 s at 3,000 points, linear).
    // Do not fold them back together, and do not "simplify" either helper query by dropping a type
    // triple — see the next comment block, which is about exactly that.
    //
    // WHAT THE SPLIT COSTS, AND WHY EVERY QUERY REPEATS `a sbco:PointExt`.
    //
    // The old single query bound ONE `?point` variable, constrained `a sbco:PointExt` once, and both
    // OPTIONALs joined it by NODE IDENTITY. Three separate queries cannot share a variable, so they
    // re-join on the `sbco:id` LITERAL instead — which is a weaker key, and silently so. Each query
    // must therefore re-assert the type constraint the shared node used to carry, or the join leaks
    // across any two nodes that happen to publish the same sbco:id:
    //
    //   <pt>    a sbco:PointExt ; sbco:id "COL" .                      # orphan: no equipment, no place
    //   <ghost> a sbco:Thing    ; sbco:id "COL" .                      # NOT a point
    //   <eq>    a sbco:EquipmentExt ; sbco:id "DEV-GHOST" ;
    //           sbco:hasPoint <ghost> ; sbco:locatedIn <room-under-a-building> .
    //
    // Without `?pt a sbco:PointExt` / `?point a sbco:PointExt` the ghost's device id and the ghost's
    // placement are both credited to the real point — verified against a live OxiGraph: the old query
    // answers (deviceId "", HasBuildingPath false) and the untyped split answers ("DEV-GHOST", true).
    // That is a fail-OPEN of the #292 ingress gate (a point the twin places nowhere gets accepted) and
    // it contradicts #291, whose OxiGraphTwinAdminService.OrphanPattern anchors on `?pt a sbco:PointExt`
    // by node and still calls that point an orphan. The type triples cost ~10 ms at 3,000 points
    // (device link 0.035 s → 0.042 s, reachability 0.033 s → 0.044 s) — they are nowhere near the
    // quadratic term, which was the OPTIONAL.
    //
    // The one case the literal join still cannot reproduce is two distinct PointExt NODES sharing one
    // sbco:id — see AmbiguousPointIds below for why that is not closed by joining on the node instead.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The point rows themselves. <c>?building</c> is the denormalized literal, kept verbatim for
    /// telemetry enrichment / the Parquet partition key — it is deliberately NOT the answer to whether
    /// the twin places the point under a Building (#292); that is <see cref="ReachabilityQuery"/>.
    /// <para>
    /// <c>?point</c> is selected but never merged: it is only there so <see cref="AmbiguousPointIds"/>
    /// can see when one point id spans several nodes, which is the one twin shape the id join cannot
    /// reproduce faithfully. It adds a column, not a join (0.056 s → 0.063 s at 3,000 points).
    /// </para>
    /// </summary>
    private const string PointProjectionQuery = """
        PREFIX sbco: <https://www.sbco.or.jp/ont/>
        SELECT ?point ?pointId ?building ?name ?gatewayId WHERE {
          ?point a sbco:PointExt ;
                 sbco:id ?pointId .
          OPTIONAL { ?point sbco:building ?building }
          OPTIONAL { ?point sbco:name ?name }
          OPTIONAL { ?point sbco:gatewayId ?gatewayId }
        }
        """;

    /// <summary>
    /// Point → owning device id. Driven from the equipment side so the <c>a sbco:EquipmentExt</c> type
    /// check is a plain pattern rather than the quadratic OPTIONAL it used to be. Merged as a LEFT
    /// join: a point no equipment owns still yields a row, with an empty device id.
    /// <para>
    /// BOTH type triples are load-bearing and for the same reason, on opposite ends of the edge. The
    /// old query's <c>?point</c> was already `a sbco:PointExt`, so <c>sbco:hasPoint</c>'s object was
    /// necessarily a point; re-joining on the id literal loses that, and an <c>E1 sbco:hasPoint E2</c>
    /// edge between two EquipmentExt (which also carry <c>sbco:id</c>) is enough to put a device id
    /// into the point-id keyspace and hand a real point the wrong <c>DeviceId</c> — the value written
    /// onto every telemetry frame for that point and into the Parquet lake.
    /// </para>
    /// </summary>
    private const string DeviceLinkQuery = """
        PREFIX sbco: <https://www.sbco.or.jp/ont/>
        SELECT ?pointId ?deviceId WHERE {
          ?equip a sbco:EquipmentExt ;
                 sbco:hasPoint ?pt ;
                 sbco:id ?deviceId .
          ?pt a sbco:PointExt ;
              sbco:id ?pointId .
        }
        """;

    /// <summary>
    /// The set of point ids the twin actually places under a <c>sbco:Building</c> — i.e.
    /// <see cref="PointMetadata.HasBuildingPath"/>.
    /// <para>
    /// The traversal mirrors the import-time orphan definition exactly (#291,
    /// <c>OxiGraphTwinAdminService.OrphanPattern</c>): from the OWNING EQUIPMENT, via the spatial chain,
    /// or direct Level location, or the <c>sbco:floor</c> literal join — ANY of which counts. Anchoring
    /// on the equipment rather than the point matters: the two latter paths live on EquipmentExt.
    /// All three branches must survive; losing one silently turns reachable points into orphans at
    /// strict ingress (#292).
    /// </para>
    /// <para>
    /// <c>?anyDev sbco:hasPoint ?point</c> is hoisted out of the UNION — equivalent to the old query,
    /// which repeated it inside each branch. As there it is NOT joined to the equipment that supplied
    /// the device id, so reachability is a property of the point, not of a (point, device) pair.
    /// </para>
    /// <para>
    /// <c>?point a sbco:PointExt</c> is what keeps this a statement about POINTS. Both the old query
    /// and #291's <c>OrphanPattern</c> reach the hierarchy from a point node that is already typed;
    /// dropping the type here would let any <c>sbco:hasPoint</c> object with a colliding
    /// <c>sbco:id</c> — a node that is not a point at all — confer reachability on a real orphan, i.e.
    /// fail the #292 gate open. It is not the quadratic term and costs ~10 ms at 3,000 points.
    /// </para>
    /// </summary>
    private const string ReachabilityQuery = """
        PREFIX sbco: <https://www.sbco.or.jp/ont/>
        SELECT DISTINCT ?pointId WHERE {
          ?anyDev sbco:hasPoint ?point .
          ?point a sbco:PointExt ;
                 sbco:id ?pointId .
          {
            ?anyDev sbco:locatedIn ?anyRoom .
            ?anyRoom a sbco:Room .
            ?anyFloor sbco:hasPart ?anyRoom ;
                      a sbco:Level .
            ?bldg sbco:hasPart ?anyFloor ;
                  a sbco:Building .
          } UNION {
            ?anyDev sbco:locatedIn ?anyFloor .
            ?anyFloor a sbco:Level .
            ?bldg sbco:hasPart ?anyFloor ;
                  a sbco:Building .
          } UNION {
            ?anyDev sbco:floor ?anyFloorName .
            ?anyFloor a sbco:Level ;
                      sbco:name ?anyFloorName .
            ?bldg sbco:hasPart ?anyFloor ;
                  a sbco:Building .
          }
        }
        """;

    /// <summary>
    /// Runs the three queries and joins them. Sequential rather than concurrent: the total is ~0.3 s
    /// at 3,000 points, the per-query durations stay individually attributable in the log below, and
    /// three concurrent bulk scans would only add contention on the single-process OxiGraph store.
    /// </summary>
    public async Task<PointMetadata[]> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var total = Stopwatch.StartNew();

        var (pointRows, pointMs) =
            await QueryAsync(PointProjectionQuery, "point projection", cancellationToken).ConfigureAwait(false);
        var (deviceLinkRows, deviceLinkMs) =
            await QueryAsync(DeviceLinkQuery, "device link", cancellationToken).ConfigureAwait(false);
        var (reachableRows, reachableMs) =
            await QueryAsync(ReachabilityQuery, "building reachability", cancellationToken).ConfigureAwait(false);

        var merged = Merge(pointRows, deviceLinkRows, reachableRows);

        var ambiguous = AmbiguousPointIds(pointRows);
        if (ambiguous.Length > 0)
        {
            _logger.LogWarning(
                "Digital twin has {AmbiguousCount} point id(s) carried by more than one sbco:PointExt node " +
                "(e.g. {SamplePointIds}). Point id is the ingress key, so these points are indistinguishable: " +
                "their device id and #292 building-path flag are merged across all the nodes sharing the id, " +
                "and a point placed under a building anywhere makes every namesake reachable. Deduplicate the " +
                "twin — the point list is meant to be keyed by point id",
                ambiguous.Length, string.Join(", ", ambiguous.Take(AmbiguousPointIdSampleLimit)));
        }

        _logger.LogInformation(
            "Point metadata loaded: {MergedCount} entries in {TotalMs} ms " +
            "(point projection {PointRowCount} rows / {PointMs} ms, " +
            "device links {DeviceLinkRowCount} rows / {DeviceLinkMs} ms, " +
            "building reachability {ReachableRowCount} rows / {ReachableMs} ms)",
            merged.Length, (long)total.Elapsed.TotalMilliseconds,
            pointRows.Count, (long)pointMs,
            deviceLinkRows.Count, (long)deviceLinkMs,
            reachableRows.Count, (long)reachableMs);

        return merged;
    }

    /// <summary>
    /// Joins the three query results by point id. Pure and static so the join is testable without a
    /// graph database; the SPARQL itself is guarded by the OxiGraph integration tests.
    /// <para>
    /// The join key is the <c>sbco:id</c> literal, where the query this replaces joined by node. Each
    /// query re-asserts <c>a sbco:PointExt</c> so the key cannot leak to non-points; the residual
    /// case — one id on several point nodes — is documented and logged by
    /// <see cref="AmbiguousPointIds"/>.
    /// </para>
    /// <para>
    /// <b>Row multiplicity is preserved deliberately.</b> The query this replaces grouped by
    /// <c>?pointId ?building ?name ?gatewayId ?deviceId</c>, so a point owned by two equipment (or
    /// carrying two names) produced two (or four) rows, and <c>PointMetadataCache</c> resolves the
    /// duplicates with <c>GroupBy(PointId).Last()</c>. This emits the same cross product of point rows
    /// × device links — collapsing to one row per point here would change which value wins. The old
    /// query had no ORDER BY, so "last" was unspecified solution order; here it is at least
    /// deterministic: point rows in projection order, device links in device-link-query order.
    /// </para>
    /// </summary>
    internal static PointMetadata[] Merge(
        IReadOnlyList<IReadOnlyDictionary<string, string>> pointRows,
        IReadOnlyList<IReadOnlyDictionary<string, string>> deviceLinkRows,
        IReadOnlyList<IReadOnlyDictionary<string, string>> reachableRows)
    {
        var deviceIdsByPoint = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var row in deviceLinkRows)
        {
            var pointId = Get(row, "pointId");
            if (pointId.Length == 0) continue;   // attaches to nothing

            if (!deviceIdsByPoint.TryGetValue(pointId, out var deviceIds))
                deviceIdsByPoint[pointId] = deviceIds = [];
            deviceIds.Add(Get(row, "deviceId"));
        }

        var reachable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in reachableRows)
        {
            var pointId = Get(row, "pointId");
            if (pointId.Length != 0) reachable.Add(pointId);
        }

        var merged = new List<PointMetadata>(pointRows.Count);
        foreach (var row in pointRows)
        {
            // The point projection is the only row driver: a device link or reachability row naming a
            // point the projection did not return carries no building/name/gateway and must not
            // conjure a row.
            var pointId = Get(row, "pointId");
            if (pointId.Length == 0) continue;

            var building = Get(row, "building");
            var name = Get(row, "name");
            var gatewayId = Get(row, "gatewayId");
            // Per POINT, not per (point, device) pair — see ReachabilityQuery.
            var hasBuildingPath = reachable.Contains(pointId);

            if (deviceIdsByPoint.TryGetValue(pointId, out var deviceIds))
            {
                foreach (var deviceId in deviceIds)
                    merged.Add(new PointMetadata(pointId, building, name, deviceId, gatewayId, hasBuildingPath));
            }
            else
            {
                // LEFT join, as the OPTIONAL was: an unowned point keeps its row with an empty device id.
                merged.Add(new PointMetadata(pointId, building, name, string.Empty, gatewayId, hasBuildingPath));
            }
        }

        return [.. merged];
    }

    /// <summary>How many ambiguous point ids the warning below names before it stops listing them.</summary>
    private const int AmbiguousPointIdSampleLimit = 10;

    /// <summary>
    /// Point ids that more than one <c>sbco:PointExt</c> NODE publishes — the one twin shape the id
    /// join cannot reproduce, surfaced so it is a logged data defect rather than a silent one.
    /// <para>
    /// <b>Why it is not fixed by joining on the node instead.</b> The old single query joined by node,
    /// so two PointExt nodes sharing an id stayed two independent rows (the placed one reachable, the
    /// orphan one not) and <c>PointMetadataCache</c>'s <c>GroupBy(PointId).Last()</c> then picked
    /// between them in whatever order OxiGraph happened to emit — the query has no ORDER BY, so which
    /// one won was never specified and is not a behaviour a rewrite can preserve. Reproducing it here
    /// would mean carrying the point term as the join key across all three queries, and for a
    /// blank-node point that key is a bnode label, which SPARQL scopes to a single result set: this
    /// OxiGraph build happens to emit stable labels, but relying on that would silently strip a
    /// blank-node point of its device id AND its building path — fail-closed drops at strict ingress —
    /// the day it stopped. So the id join stands, and its consequence is stated: the flag is the union
    /// over every node sharing the id (fail-open), deterministically rather than by luck.
    /// </para>
    /// <para>
    /// Rows without a <c>?point</c> binding are no evidence either way and are ignored.
    /// </para>
    /// </summary>
    internal static string[] AmbiguousPointIds(IReadOnlyList<IReadOnlyDictionary<string, string>> pointRows)
    {
        var nodesByPointId = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var row in pointRows)
        {
            var pointId = Get(row, "pointId");
            var node = Get(row, "point");
            if (pointId.Length == 0 || node.Length == 0) continue;

            if (!nodesByPointId.TryGetValue(pointId, out var nodes))
                nodesByPointId[pointId] = nodes = new HashSet<string>(StringComparer.Ordinal);
            nodes.Add(node);
        }

        return [.. nodesByPointId.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Runs one query under its own timeout, linked to <paramref name="ct"/> so the caller can still
    /// cancel.
    /// <para>
    /// A timeout cancels only the linked source, never <paramref name="ct"/>, so
    /// <c>PointMetadataCache.LoadWithRetryAsync</c> — which classifies shutdown as
    /// <c>catch (OperationCanceledException) when (ct.IsCancellationRequested)</c> on its own lifetime
    /// token — sees an ordinary failure and retries it. A genuine lifetime cancellation still
    /// short-circuits that retry loop.
    /// </para>
    /// </summary>
    private async Task<(IReadOnlyList<IReadOnlyDictionary<string, string>> Rows, double ElapsedMs)> QueryAsync(
        string sparql, string queryName, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_queryTimeout != Timeout.InfiniteTimeSpan) timeoutCts.CancelAfter(_queryTimeout);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var rows = await _client.QueryAsync(sparql, timeoutCts.Token).ConfigureAwait(false);
            return (rows, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "Point metadata {QueryName} query exceeded its {QueryTimeoutSec}s bound after {ElapsedMs} ms; " +
                "the twin is slow or unreachable and the load will be retried",
                queryName, (long)_queryTimeout.TotalSeconds, (long)stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    private static string Get(IReadOnlyDictionary<string, string> row, string key)
        => row.TryGetValue(key, out var v) ? v : string.Empty;
}
