using BuildingOS.Shared.Domain.TwinAdmin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Infrastructure.OxiGraph;

/// <summary>
/// Materializes REC/Brick-vocabulary twin RDF into the SBCO/<c>bos:</c> canonical form the rest of
/// this codebase queries against. The canonical upstream ontology (smartbuilding_datamodels) and its
/// RDF exporter (smartbuilding_datamodel_builder) use RealEstateCore (<c>rec:</c>)/Brick
/// (<c>brick:</c>) as the source-of-truth vocabulary for the building hierarchy — but
/// <see cref="OxiGraphOntology"/> and every query built against it (<c>ResourceSearchQueryBuilder</c>,
/// <c>OxiGraphDigitalTwinDatabase</c>, <c>OxiGraphHierarchyResolver</c>, the #291/#292 hierarchy
/// checks) assume <c>sbco:</c>/<c>bos:</c> exclusively. Rather than teach every one of those ~100+
/// call sites a second vocabulary, this stages the incoming Turtle in a named graph, then runs a
/// small set of <c>INSERT ... WHERE</c> statements that derive the missing <c>sbco:</c> triples into
/// an isolated candidate graph. Clock validation precedes publication of that candidate.
///
/// The complete REC↔SBCO class/property pairs from
/// <c>docs/architecture/standard-mapping.md</c> are materialized. This includes
/// <c>rec:Room</c>→<c>sbco:Room</c>, whose formal <c>owl:equivalentClass</c> axiom is supplied by
/// smartbuilding_datamodels. Partial-match pairs such as <c>brick:Equipment</c>→
/// <c>sbco:EquipmentExt</c>, <c>brick:Point</c>→<c>sbco:PointExt</c>, and <c>rec:Site</c>→
/// <c>sbco:Site</c> remain excluded pending SBCO/GUTP working-group (HITL) sign-off — an imported
/// Site node therefore keeps its materialized property edges (<c>sbco:hasPart</c>/<c>sbco:name</c>)
/// but is never given an <c>rdf:type</c> here; that is deliberate, not a missed rule (#416).
///
/// <b>Atomicity</b>: candidate preparation does not mutate the default graph. Publication compares
/// the baseline to the current twin, then replaces the default graph in one SPARQL UPDATE
/// transaction. A reader sees the complete previous or complete new twin. A concurrent edit after
/// baseline capture refuses the import instead of being overwritten. Temporary graphs are unique
/// to each call; only the successfully committed replace input is retained as provenance.
/// </summary>
public sealed class OxiGraphIngestMaterializer
{
    // Mirrors OxiGraphOntology's private SbcoNs/BosNs — duplicated here rather than exposed from that
    // class, since this is the only consumer that needs the bare namespace strings for a prefix filter.
    private const string SbcoNs = "https://www.sbco.or.jp/ont/";
    private const string BosNs = "http://buildingos.gutp.jp/ontology#";
    private const string RecNs = "https://w3id.org/rec/";
    // BOT (Building Topology Ontology, W3C LBD CG) — the source vocabulary for room-to-room
    // adjacency (#440). Note it is *not* added to the copy-through prefix filter below: bot: triples
    // reach the default graph only through an explicit rule, as bos:adjacentZone.
    private const string BotNs = "https://w3id.org/bot#";

    private static readonly (string From, string To)[] ClassRules =
    {
        (RecNs + "Building", OxiGraphOntology.Cls_Building),
        (RecNs + "Level", OxiGraphOntology.Cls_Level),
        // smartbuilding_datamodels PR #34 defines sbco:Room owl:equivalentClass rec:Room.
        (RecNs + "Room", OxiGraphOntology.Cls_Space),
    };

    // Symmetric:true additionally writes the reverse edge (?o To ?s). Symmetry is a property of the
    // *vocabulary*, not of any one query, so it is resolved once here rather than by a UNION in
    // every consumer's SPARQL — #294/#298 are what happens when a read-path obligation lives in N
    // hand-written queries. Resolving it at ingest also means Replace, Append and the import-preview
    // projection all get it for free, since they share AppendMaterializationStatements.
    private static readonly (string From, string To, bool Symmetric)[] PropertyRules =
    {
        (RecNs + "locatedIn", OxiGraphOntology.Prop_LocatedIn, false),
        (RecNs + "name", OxiGraphOntology.Prop_Name, false),
        (RecNs + "hasPart", OxiGraphOntology.Prop_HasPart, false),
        (RecNs + "hasPoint", OxiGraphOntology.Prop_HasPoint, false),
        // #440: BOT room adjacency → the bos: canonical form.
        (BotNs + "adjacentZone", OxiGraphOntology.Prop_AdjacentZone, true),
        // …and the symmetric closure for input that is already canonical. The forward half is a
        // no-op (the copy-through above already wrote it) and the FILTER NOT EXISTS keeps it so;
        // the reverse half is what this row exists for.
        (OxiGraphOntology.Prop_AdjacentZone, OxiGraphOntology.Prop_AdjacentZone, true),
    };

    private readonly OxiGraphClient _client;
    private readonly ILogger<OxiGraphIngestMaterializer> _logger;

    public OxiGraphIngestMaterializer(
        OxiGraphClient client, ILogger<OxiGraphIngestMaterializer>? logger = null)
    {
        _client = client;
        _logger = logger ?? NullLogger<OxiGraphIngestMaterializer>.Instance;
    }

    /// <summary>Validate and atomically replace the canonical default graph.</summary>
    public Task MaterializeAsync(string turtle, CancellationToken ct = default)
        => MaterializeValidatedAsync(turtle, TwinImportMode.Replace, ct);

    /// <summary>Validate the merged candidate and atomically publish it.</summary>
    public Task MaterializeAppendAsync(string turtle, CancellationToken ct = default)
        => MaterializeValidatedAsync(turtle, TwinImportMode.Append, ct);

    private async Task MaterializeValidatedAsync(string turtle, TwinImportMode mode, CancellationToken ct)
    {
        var source = $"urn:bos:twin-import-source:{Guid.NewGuid():N}";
        await _client.LoadNamedGraphAsync(source, turtle, ct).ConfigureAwait(false);
        try
        {
            await ClockValidatedImport.ApplyAsync(_client, this, source, mode, _logger, ct).ConfigureAwait(false);
            _logger.LogInformation("Materialized OxiGraph twin in {Mode} mode with clock validation", mode);
        }
        finally
        {
            try { await _client.DropNamedGraphAsync(source, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to drop import source graph {Graph}", source); }
        }
    }

    /// <summary>
    /// Materializes an already-staged source graph into an isolated named graph. Import preview uses
    /// this to validate the same SBCO projection that an eventual apply will write, without touching
    /// the default graph.
    /// </summary>
    internal async Task MaterializeNamedGraphAsync(
        string sourceGraph, string targetGraph, CancellationToken ct = default)
    {
        var statements = new List<string> { $"DROP SILENT GRAPH <{targetGraph}>" };
        AppendMaterializationStatements(statements, sourceGraph, targetGraph);
        await _client.UpdateAsync(string.Join(" ;\n", statements), ct).ConfigureAwait(false);
    }

    // Builds the copy-through + class/property rule INSERT statements reading from sourceGraph, and
    // appends them to `statements` (shared by both Replace, after a leading DROP DEFAULT, and Append).
    private static void AppendMaterializationStatements(
        List<string> statements, string sourceGraph, string? targetGraph = null)
    {
        string Insert(string triples) => targetGraph is null
            ? $"INSERT {{ {triples} }}"
            : $"INSERT {{ GRAPH <{targetGraph}> {{ {triples} }} }}";
        string TargetPattern(string triples) => targetGraph is null
            ? triples
            : $"GRAPH <{targetGraph}> {{ {triples} }}";

        // Triples/types already expressed in sbco:/bos: vocabulary pass through unchanged — the
        // materialization rules below only need to cover the REC/Brick vocabulary gap.
        statements.Add($@"
{Insert("?s ?p ?o")}
WHERE {{
  GRAPH <{sourceGraph}> {{ ?s ?p ?o }}
  FILTER(STRSTARTS(STR(?p), ""{SbcoNs}"") || STRSTARTS(STR(?p), ""{BosNs}""))
}}");
        statements.Add($@"
{Insert("?s a ?type")}
WHERE {{
  GRAPH <{sourceGraph}> {{ ?s a ?type }}
  FILTER(STRSTARTS(STR(?type), ""{SbcoNs}"") || STRSTARTS(STR(?type), ""{BosNs}""))
}}");

        foreach (var (from, to) in ClassRules)
        {
            statements.Add($@"
{Insert($"?s a <{to}>")}
WHERE {{
  GRAPH <{sourceGraph}> {{ ?s a <{from}> }}
  FILTER NOT EXISTS {{ {TargetPattern($"?s a <{to}>")} }}
}}");
        }

        // One property rule contributes the forward edge, plus the reverse one when symmetric. The
        // FILTER NOT EXISTS must test the triple actually being inserted (the reverse statement asks
        // about ?o <to> ?s), otherwise the reverse edge is written on every pass and the import
        // stops being idempotent.
        void AddPropertyRule(string subject, string predicate, string obj, string sourcePattern)
            => statements.Add($@"
{Insert($"{subject} <{predicate}> {obj}")}
WHERE {{
  GRAPH <{sourceGraph}> {{ {sourcePattern} }}
  FILTER NOT EXISTS {{ {TargetPattern($"{subject} <{predicate}> {obj}")} }}
}}");

        foreach (var (from, to, symmetric) in PropertyRules)
        {
            AddPropertyRule("?s", to, "?o", $"?s <{from}> ?o");
            if (symmetric) AddPropertyRule("?o", to, "?s", $"?s <{from}> ?o");
        }
    }
}
