using System.Diagnostics;
using BuildingOS.Shared.Domain.TwinAdmin;
using BuildingOS.Shared.Infrastructure.ControlRouting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Infrastructure.OxiGraph;

/// <summary>
/// <see cref="ITwinAdminService"/> over <see cref="OxiGraphClient"/> (#322). Import preview stages the
/// Turtle and materializes it into a second temporary named graph before running scoped
/// count/uniqueness queries; both graphs are then dropped, so a destructive replace is validated
/// before it touches the default graph.
/// </summary>
public sealed class OxiGraphTwinAdminService : ITwinAdminService
{
    // internal: ControlSchemaIssueDetection (#336) reuses this namespace and the Link() graph-scoping
    // helper below to run the same query-building convention for its own detection.
    internal const string Sbco = "https://www.sbco.or.jp/ont/";

    /// <summary>
    /// Cap on the enumerated orphan sample (the count is exact). Same value as the SPARQL console's
    /// row ceiling (TwinAdminController.MaxQueryRows), which is also what a request that names no
    /// smaller limit gets, so an admin never sees more rows here than that console would return.
    /// </summary>
    private const int MaxOrphans = 1000;

    private readonly OxiGraphClient _client;
    private readonly OxiGraphIngestMaterializer _materializer;
    private readonly ILogger<OxiGraphTwinAdminService> _logger;
    private readonly IPointListUpdatePublisher? _pointListUpdatePublisher;

    public OxiGraphTwinAdminService(
        OxiGraphClient client,
        OxiGraphIngestMaterializer materializer,
        ILogger<OxiGraphTwinAdminService>? logger = null,
        IPointListUpdatePublisher? pointListUpdatePublisher = null)
    {
        _client = client;
        _materializer = materializer;
        _logger = logger ?? NullLogger<OxiGraphTwinAdminService>.Instance;
        _pointListUpdatePublisher = pointListUpdatePublisher;
    }

    public async Task<TwinImportPreview> PreviewImportAsync(
        string turtle, TwinImportMode mode, CancellationToken ct = default)
    {
        var graph = $"urn:bos:import-preview:{Guid.NewGuid():N}";
        var materializedGraph = $"{graph}:materialized";
        await _client.LoadNamedGraphAsync(graph, turtle, ct).ConfigureAwait(false);
        try
        {
            // Preview must validate the normalized graph ApplyImport will leave behind. In particular,
            // a standard REC hierarchy uses rec:Room/hasPart/locatedIn, while all hierarchy checks
            // below intentionally query only canonical sbco: terms.
            await _materializer.MaterializeNamedGraphAsync(graph, materializedGraph, ct).ConfigureAwait(false);

            var triples = await ScalarCountAsync(
                $"SELECT (COUNT(*) AS ?n) WHERE {{ GRAPH <{graph}> {{ ?s ?p ?o }} }}", ct).ConfigureAwait(false);

            var gateways = await ScalarCountAsync(
                $"SELECT (COUNT(DISTINCT ?gw) AS ?n) WHERE {{ GRAPH <{materializedGraph}> {{ ?pt <{Sbco}gatewayId> ?gw }} }}",
                ct).ConfigureAwait(false);

            var collisionRows = await _client.QueryAsync($@"
SELECT ?gw (COUNT(DISTINCT ?b) AS ?n) WHERE {{
  GRAPH <{materializedGraph}> {{ ?pt a <{Sbco}PointExt> ; <{Sbco}gatewayId> ?gw ; <{Sbco}building> ?b . }}
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
            var orphanPattern = OrphanPattern(materializedGraph, mode);
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

            // Business-id uniqueness (#517): authorization identifies a node by its sbco:id, so two
            // nodes of one type sharing it would share every grant. Count every colliding (type, id),
            // then enumerate a capped sample.
            var idCollisionPattern = IdCollisionPattern(materializedGraph, mode);
            var idCollisionTotal = await ScalarCountAsync(
                $"SELECT (COUNT(?id) AS ?n) WHERE {{ {{ {idCollisionPattern} }} }}", ct).ConfigureAwait(false);
            var idCollisionRows = await _client.QueryAsync(
                $"{idCollisionPattern} ORDER BY ?cls ?id LIMIT {MaxOrphans}", ct).ConfigureAwait(false);
            var idCollisions = idCollisionRows
                .Select(r => new TwinIdCollision(
                    ResourceTypeOf(r.GetValueOrDefault("cls", "")),
                    r.GetValueOrDefault("id", ""),
                    int.TryParse(r.GetValueOrDefault("n", "0"), out var c) ? c : 0))
                .ToList();

            // Control-schema completeness (#336): a writable point missing/malformed bos: schema
            // fails open at control time (ControlValueValidator skips validation) with no signal —
            // surface it here, at import time, purely as a count (never blocks apply).
            var schemaRows = await _client.QueryAsync(
                ControlSchemaIssueDetection.BuildQuery(materializedGraph, mode), ct).ConfigureAwait(false);
            var (schemaIssueCount, schemaIssues) = ControlSchemaIssueDetection.Classify(schemaRows, MaxOrphans);

            return new TwinImportPreview(
                triples, (int)gateways, collisions, (int)orphanTotal, orphans,
                schemaIssueCount, schemaIssues, (int)idCollisionTotal, idCollisions);
        }
        finally
        {
            // Always discard both the raw and normalized staging graphs, even if validation queries
            // throw. Swallow cleanup errors so they never mask the original validation exception.
            foreach (var stagedGraph in new[] { materializedGraph, graph })
            {
                try
                {
                    await _client.DropNamedGraphAsync(stagedGraph, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to drop import-preview staging graph {Graph}", stagedGraph);
                }
            }
        }
    }

    public async Task ApplyImportAsync(string turtle, TwinImportMode mode, CancellationToken ct = default)
    {
        if (mode == TwinImportMode.Replace)
        {
            await _materializer.MaterializeAsync(turtle, ct).ConfigureAwait(false);
        }
        else
        {
            await _materializer.MaterializeAppendAsync(turtle, ct).ConfigureAwait(false);
        }

        // #414: the twin (point-list source of truth) just changed — signal every gateway to
        // revalidate, the same best-effort push the startup seed does (#224). Without this, an
        // admin edit is invisible to gateways until their next ETag poll (default 10 minutes).
        await PointListUpdateBroadcaster
            .PublishAllAsync(_client, _pointListUpdatePublisher, _logger, ct)
            .ConfigureAwait(false);
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
    // A point is "connected" when the topology reaches a Building by either chain:
    //   A. the spatial chain Building →hasPart→ Level →hasPart→ Room ←locatedIn← EquipmentExt →hasPoint→ PointExt
    //   B. the direct chain Building →hasPart→ Level ←locatedIn← EquipmentExt →hasPoint→ PointExt
    // The sbco:floor literal is metadata, not a relationship, and places nothing: building the
    // topology is the builder's job. Equipment that names a Level only through that literal gets its
    // own reason (floor_literal_only) so the twin's author knows exactly what to emit. The four UNION
    // branches stay mutually exclusive (no device / placed only by the literal / no spatial anchor at
    // all / an anchor that reaches no Building), so a point is reported exactly once, under the
    // outermost link that is missing (the literal-only / no-anchor pair shares one branch and is told
    // apart by a BIND). Shared by the count and the capped enumeration so both always agree on what
    // "orphan" means.
    private static string OrphanPattern(string graph, TwinImportMode mode)
    {
        string Chain(params string[] triples) =>
            string.Join(" ", triples.Select(t => Link(graph, mode, t)));

        var hasPoint    = $"?anyDev <{Sbco}hasPoint> ?pt .";
        var inRoom      = $"?anyDev <{Sbco}locatedIn> ?anyRoom .";
        var isRoom      = $"?anyRoom a <{Sbco}Room> .";
        var roomOfFloor = $"?anyFloor <{Sbco}hasPart> ?anyRoom .";
        var inFloor     = $"?anyDev <{Sbco}locatedIn> ?anyFloor .";
        var devFloor    = $"?anyDev <{Sbco}floor> ?anyFloorName .";
        var isFloor     = $"?anyFloor a <{Sbco}Level> .";
        var floorOfBldg = $"?anyBuilding <{Sbco}hasPart> ?anyFloor .";
        var isBuilding  = $"?anyBuilding a <{Sbco}Building> .";

        var candidate = $"GRAPH <{graph}> {{ ?pt a <{Sbco}PointExt> . }}";
        var device = Chain(hasPoint);

        // A spatial anchor on the point's device: sbco:locatedIn a Room or a Level. A device carrying
        // none is placed nowhere, so there is nothing to trace upwards.
        var anchor =
            $"{{ {Chain(hasPoint, inRoom, isRoom)} }} UNION " +
            $"{{ {Chain(hasPoint, inFloor, isFloor)} }}";
        // floor_literal_only: the device names a Level only through the literal and has no
        // sbco:locatedIn at all (one pointing at an untyped node is a different defect: no_room).
        var literalOnly = Chain(hasPoint, devFloor);
        var anyLocatedIn = Chain(hasPoint, $"?anyDev <{Sbco}locatedIn> ?anyTarget .");

        var reachable =
            $"{{ {Chain(hasPoint, inRoom, isRoom, roomOfFloor, isFloor, floorOfBldg, isBuilding)} }} UNION " +
            $"{{ {Chain(hasPoint, inFloor, isFloor, floorOfBldg, isBuilding)} }}";

        return $@"
{{
  {candidate}
  FILTER NOT EXISTS {{ {device} }}
  BIND(""{TwinOrphanReasons.NoDevice}"" AS ?reason)
}} UNION {{
  {candidate}
  FILTER EXISTS {{ {device} }}
  FILTER NOT EXISTS {{ {anchor} }}
  BIND(IF(EXISTS {{ {literalOnly} }} && NOT EXISTS {{ {anyLocatedIn} }},
          ""{TwinOrphanReasons.FloorLiteralOnly}"", ""{TwinOrphanReasons.NoRoom}"") AS ?reason)
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
    //
    // internal: ControlSchemaIssueDetection (#336) reuses this to scope its own candidate/lookup
    // triples the same way, for both admin preview and OxiGraphSeedHostedService's post-seed check.
    // The resource classes whose sbco:id authorization relies on, and the API type name of each.
    private static readonly (string Class, string Type)[] IdentifiedClasses =
    [
        ("Building", "building"), ("Level", "floor"), ("Room", "space"), ("EquipmentExt", "device"), ("PointExt", "point"),
    ];

    private static string ResourceTypeOf(string classIri)
        => IdentifiedClasses.FirstOrDefault(c => classIri == $"{Sbco}{c.Class}").Type ?? classIri;

    // (class, id) pairs held by more than one node of that class in the graphs the import leaves behind
    // (#517). The group is anchored on a node of the staging graph, so an append is judged only on what
    // it brings in — a duplicate already inside the twin is not blamed on it — while the same IRI
    // re-imported is one node, not two. Shared by the count and the enumeration.
    private static string IdCollisionPattern(string graph, TwinImportMode mode)
    {
        var classes = string.Join(" ", IdentifiedClasses.Select(c => $"<{Sbco}{c.Class}>"));
        return $@"SELECT ?cls ?id (COUNT(DISTINCT ?node) AS ?n) WHERE {{
  VALUES ?cls {{ {classes} }}
  GRAPH <{graph}> {{ ?staged a ?cls ; <{Sbco}id> ?id . }}
  {Link(graph, mode, "?node a ?cls .")}
  {Link(graph, mode, $"?node <{Sbco}id> ?id .")}
}}
GROUP BY ?cls ?id
HAVING (COUNT(DISTINCT ?node) > 1)";
    }

    internal static string Link(string graph, TwinImportMode mode, string triple) =>
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
