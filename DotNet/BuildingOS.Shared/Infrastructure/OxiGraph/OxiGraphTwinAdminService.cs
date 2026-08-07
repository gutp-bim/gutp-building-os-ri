using System.Diagnostics;
using BuildingOS.Shared.Domain.TwinAdmin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Infrastructure.OxiGraph;

/// <summary>
/// <see cref="ITwinAdminService"/> over <see cref="OxiGraphClient"/> (#322). Import preview stages the
/// Turtle in a temporary named graph, runs scoped count/uniqueness queries, then drops the staging
/// graph — so a destructive replace is validated before it touches the default graph.
/// </summary>
public sealed class OxiGraphTwinAdminService : ITwinAdminService
{
    private const string Sbco = "https://www.sbco.or.jp/ont/";

    /// <summary>
    /// Cap on the enumerated orphan sample (the count is exact). Same value as the SPARQL console's
    /// row ceiling (TwinAdminController.MaxQueryRows), which is also what a request that names no
    /// smaller limit gets, so an admin never sees more rows here than that console would return.
    /// </summary>
    private const int MaxOrphans = 1000;

    private readonly OxiGraphClient _client;
    private readonly ILogger<OxiGraphTwinAdminService> _logger;

    public OxiGraphTwinAdminService(
        OxiGraphClient client, ILogger<OxiGraphTwinAdminService>? logger = null)
    {
        _client = client;
        _logger = logger ?? NullLogger<OxiGraphTwinAdminService>.Instance;
    }

    public async Task<TwinImportPreview> PreviewImportAsync(
        string turtle, TwinImportMode mode, CancellationToken ct = default)
    {
        var graph = $"urn:bos:import-preview:{Guid.NewGuid():N}";
        await _client.LoadNamedGraphAsync(graph, turtle, ct).ConfigureAwait(false);
        try
        {
            var triples = await ScalarCountAsync(
                $"SELECT (COUNT(*) AS ?n) WHERE {{ GRAPH <{graph}> {{ ?s ?p ?o }} }}", ct).ConfigureAwait(false);

            var gateways = await ScalarCountAsync(
                $"SELECT (COUNT(DISTINCT ?gw) AS ?n) WHERE {{ GRAPH <{graph}> {{ ?pt <{Sbco}gatewayId> ?gw }} }}",
                ct).ConfigureAwait(false);

            var collisionRows = await _client.QueryAsync($@"
SELECT ?gw (COUNT(DISTINCT ?b) AS ?n) WHERE {{
  GRAPH <{graph}> {{ ?pt a <{Sbco}PointExt> ; <{Sbco}gatewayId> ?gw ; <{Sbco}building> ?b . }}
}}
GROUP BY ?gw
HAVING (COUNT(DISTINCT ?b) > 1)", ct).ConfigureAwait(false);

            var collisions = collisionRows
                .Select(r => new GatewayCollision(
                    r.GetValueOrDefault("gw", ""),
                    int.TryParse(r.GetValueOrDefault("n", "0"), out var n) ? n : 0))
                .ToList();

            // Hierarchy completeness (#291): count every unreachable point, then enumerate a capped
            // sample of them for display — a bulk import can orphan far more points than are useful
            // (or affordable) to ship back in the preview response.
            var orphanPattern = OrphanPattern(graph, mode);
            var orphanTotal = await ScalarCountAsync(
                $"SELECT (COUNT(DISTINCT ?pt) AS ?n) WHERE {{ {orphanPattern} }}", ct).ConfigureAwait(false);

            var orphanRows = await _client.QueryAsync(
                $"SELECT DISTINCT ?pt ?reason WHERE {{ {orphanPattern} }} ORDER BY ?pt LIMIT {MaxOrphans}",
                ct).ConfigureAwait(false);

            var orphans = orphanRows
                .Select(r => new TwinOrphanResource(
                    r.GetValueOrDefault("pt", ""),
                    r.GetValueOrDefault("reason", "")))
                .ToList();

            return new TwinImportPreview(triples, (int)gateways, collisions, (int)orphanTotal, orphans);
        }
        finally
        {
            // Always discard the staging graph, even if validation queries throw. Swallow cleanup
            // errors so they never mask the original validation exception (best-effort cleanup).
            try
            {
                await _client.DropNamedGraphAsync(graph, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to drop import-preview staging graph {Graph}", graph);
            }
        }
    }

    public async Task ApplyImportAsync(string turtle, TwinImportMode mode, CancellationToken ct = default)
    {
        if (mode == TwinImportMode.Replace)
        {
            await _client.ReplaceDefaultGraphAsync(turtle, ct).ConfigureAwait(false);
        }
        else
        {
            await _client.ImportTurtleAsync(turtle, ct).ConfigureAwait(false);
        }
    }

    public async Task<SparqlQueryResult> RunReadOnlyQueryAsync(
        string query, int maxRows, TimeSpan timeout, CancellationToken ct = default)
    {
        // Defense in depth: never let a non-read-only query through even if a caller forgets to guard.
        if (!SparqlReadOnlyGuard.IsReadOnly(query))
        {
            throw new InvalidOperationException("Only read-only SELECT/ASK queries are permitted.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var sw = Stopwatch.StartNew();
        var rows = await _client.QueryAsync(query, cts.Token).ConfigureAwait(false);
        sw.Stop();

        var cap = maxRows <= 0 ? 1000 : maxRows;
        var truncated = rows.Count > cap;
        var capped = truncated ? rows.Take(cap).ToList() : rows;
        var columns = capped.Count > 0
            ? capped[0].Keys.ToList()
            : new List<string>();

        return new SparqlQueryResult(columns, capped, capped.Count, truncated, sw.ElapsedMilliseconds);
    }

    // Graph pattern binding every staged PointExt the hierarchy does not reach to ?pt + the reason
    // ?reason (#291). The candidate is always a PointExt of the staging graph, so an append judges
    // only the points it adds and never re-judges the ones already in the twin; reachability, by
    // contrast, is evaluated over the graphs the chosen mode actually leaves behind (see Link).
    //
    // A point is "connected" when either path reaches a Building:
    //   A. the spatial chain Building →hasPart→ Level →hasPart→ Room ←locatedIn← EquipmentExt →hasPoint→ PointExt
    //   B. the sbco:floor literal on EquipmentExt matched against a Level's sbco:name — the same join
    //      the read side uses (OxiGraphDigitalTwinDatabase.ListPointDetails), and the only path an
    //      SBCO TTL without Room/locatedIn offers.
    // Either one suffices: Room/locatedIn are optional throughout this repository, so requiring the
    // spatial chain would report a legitimate twin as entirely orphaned. The three UNION branches
    // stay mutually exclusive (no device / a device with no spatial anchor at all / an anchor that
    // reaches no Building), so a point is reported exactly once, under the outermost link that is
    // missing. Shared by the count and the capped enumeration so both always agree on what "orphan"
    // means.
    private static string OrphanPattern(string graph, TwinImportMode mode)
    {
        string Chain(params string[] triples) =>
            string.Join(" ", triples.Select(t => Link(graph, mode, t)));

        var hasPoint    = $"?anyDev <{Sbco}hasPoint> ?pt .";
        var inRoom      = $"?anyDev <{Sbco}locatedIn> ?anyRoom .";
        var isRoom      = $"?anyRoom a <{Sbco}Room> .";
        var roomOfFloor = $"?anyFloor <{Sbco}hasPart> ?anyRoom .";
        var devFloor    = $"?anyDev <{Sbco}floor> ?anyFloorName .";
        var floorName   = $"?anyFloor <{Sbco}name> ?anyFloorName .";
        var isFloor     = $"?anyFloor a <{Sbco}Level> .";
        var floorOfBldg = $"?anyBuilding <{Sbco}hasPart> ?anyFloor .";
        var isBuilding  = $"?anyBuilding a <{Sbco}Building> .";

        var candidate = $"GRAPH <{graph}> {{ ?pt a <{Sbco}PointExt> . }}";
        var device = Chain(hasPoint);

        // Any spatial anchor on the point's device — a Room via sbco:locatedIn, or an sbco:floor
        // literal. A device carrying neither is placed nowhere, so there is nothing to trace upwards.
        var anchor =
            $"{{ {Chain(hasPoint, inRoom, isRoom)} }} UNION {{ {Chain(hasPoint, devFloor)} }}";

        var reachable =
            $"{{ {Chain(hasPoint, inRoom, isRoom, roomOfFloor, isFloor, floorOfBldg, isBuilding)} }} UNION " +
            $"{{ {Chain(hasPoint, devFloor, floorName, isFloor, floorOfBldg, isBuilding)} }}";

        return $@"
{{
  {candidate}
  FILTER NOT EXISTS {{ {device} }}
  BIND(""{TwinOrphanReasons.NoDevice}"" AS ?reason)
}} UNION {{
  {candidate}
  FILTER EXISTS {{ {device} }}
  FILTER NOT EXISTS {{ {anchor} }}
  BIND(""{TwinOrphanReasons.NoRoom}"" AS ?reason)
}} UNION {{
  {candidate}
  FILTER EXISTS {{ {anchor} }}
  FILTER NOT EXISTS {{ {reachable} }}
  BIND(""{TwinOrphanReasons.NoBuildingPath}"" AS ?reason)
}}";
    }

    // One triple of a reachability chain, scoped to the graphs the import will actually leave behind.
    // Append merges the staged Turtle into the default graph, so a chain routinely straddles the two —
    // a new EquipmentExt/PointExt in the staging graph hanging off a Room/Level/Building that is
    // already in the twin — hence the UNION wraps every triple individually: one GRAPH around a whole
    // chain would cut exactly that case and orphan the entire import. Replace drops the default graph
    // before importing, so there only the staged triples may count.
    private static string Link(string graph, TwinImportMode mode, string triple) =>
        mode == TwinImportMode.Replace
            ? $"GRAPH <{graph}> {{ {triple} }}"
            : $"{{ {{ GRAPH <{graph}> {{ {triple} }} }} UNION {{ {triple} }} }}";

    private async Task<long> ScalarCountAsync(string sparql, CancellationToken ct)
    {
        var rows = await _client.QueryAsync(sparql, ct).ConfigureAwait(false);
        if (rows.Count == 0) return 0;
        return long.TryParse(rows[0].GetValueOrDefault("n", "0"), out var n) ? n : 0;
    }
}
