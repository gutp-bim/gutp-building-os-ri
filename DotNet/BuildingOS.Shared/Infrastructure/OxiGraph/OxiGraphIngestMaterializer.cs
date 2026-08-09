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
/// the default graph — so the rest of the codebase never has to change.
///
/// Only the 完全一致 (exact-match) REC↔SBCO class/property pairs from
/// <c>docs/architecture/standard-mapping.md</c> are materialized. The 部分一致 (partial-match) pairs
/// (<c>Room</c>↔<c>rec:Room</c>, <c>EquipmentExt</c>↔<c>brick:Equipment</c>,
/// <c>PointExt</c>↔<c>brick:Point</c>, ...) are intentionally excluded pending SBCO/GUTP
/// working-group (HITL) sign-off — see that document's "マテリアライズルール" section. Add a rule
/// here only once that review lands for it.
///
/// <b>Atomicity</b>: every statement for one materialization pass (the graph clear/stage plus every
/// copy-through/rule INSERT) is sent as a single semicolon-separated SPARQL UPDATE request, not one
/// HTTP call per statement. OxiGraph executes a whole multi-statement UPDATE request inside one
/// transaction (commit happens once, after every operation in the request has run) — so a concurrent
/// reader sees either the twin exactly as it was before this call, or exactly as it is after, never a
/// part-cleared/part-rebuilt intermediate state. (OxiGraph does not implement the SPARQL 1.1
/// <c>MOVE</c>/<c>ADD</c>/<c>COPY</c> graph-management operations, which would otherwise be the more
/// obvious way to get this — confirmed against its <c>GraphUpdateOperation</c> handling, which only
/// covers <c>InsertData</c>/<c>DeleteData</c>/<c>DeleteInsert</c>/<c>Load</c>/<c>Clear</c>/<c>Create</c>/<c>Drop</c>.)
/// </summary>
public sealed class OxiGraphIngestMaterializer
{
    // Mirrors OxiGraphOntology's private SbcoNs/BosNs — duplicated here rather than exposed from that
    // class, since this is the only consumer that needs the bare namespace strings for a prefix filter.
    private const string SbcoNs = "https://www.sbco.or.jp/ont/";
    private const string BosNs = "http://buildingos.gutp.jp/ontology#";
    private const string RecNs = "https://w3id.org/rec/";

    // A fixed (not per-call GUID) graph URI, kept in place (not dropped) after materialization — it is
    // the audit copy of the last-imported pre-materialization RDF, in whatever vocabulary the source
    // used. LoadNamedGraphAsync (PUT, full replace) overwrites it on the next MaterializeAsync call, so
    // it always reflects the current twin's actual source, not a growing history. Only the Replace path
    // uses this graph; MaterializeAppendAsync stages into its own per-call graph instead (see there).
    private const string SourceGraph = "urn:bos:twin-source";

    private static readonly (string From, string To)[] ClassRules =
    {
        (RecNs + "Building", OxiGraphOntology.Cls_Building),
        (RecNs + "Level", OxiGraphOntology.Cls_Level),
    };

    private static readonly (string From, string To)[] PropertyRules =
    {
        (RecNs + "locatedIn", OxiGraphOntology.Prop_LocatedIn),
        (RecNs + "name", OxiGraphOntology.Prop_Name),
        (RecNs + "hasPart", OxiGraphOntology.Prop_HasPart),
        (RecNs + "hasPoint", OxiGraphOntology.Prop_HasPoint),
    };

    private readonly OxiGraphClient _client;
    private readonly ILogger<OxiGraphIngestMaterializer> _logger;

    public OxiGraphIngestMaterializer(
        OxiGraphClient client, ILogger<OxiGraphIngestMaterializer>? logger = null)
    {
        _client = client;
        _logger = logger ?? NullLogger<OxiGraphIngestMaterializer>.Instance;
    }

    /// <summary>
    /// Stage <paramref name="turtle"/> in the <see cref="SourceGraph"/> named graph (retained as
    /// provenance, not dropped), then atomically replace the default graph with its
    /// SBCO/<c>bos:</c>-canonical materialization. Equivalent to
    /// <see cref="OxiGraphClient.ReplaceDefaultGraphAsync"/> except the default graph ends up
    /// vocabulary-normalized regardless of what vocabulary <paramref name="turtle"/> used.
    /// </summary>
    public async Task MaterializeAsync(string turtle, CancellationToken ct = default)
    {
        await _client.LoadNamedGraphAsync(SourceGraph, turtle, ct).ConfigureAwait(false);

        var statements = new List<string> { "DROP DEFAULT" };
        AppendMaterializationStatements(statements, SourceGraph);
        await _client.UpdateAsync(string.Join(" ;\n", statements), ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Materialized (replace) OxiGraph twin from staged RDF ({ClassRules} class rule(s), {PropertyRules} property rule(s))",
            ClassRules.Length, PropertyRules.Length);
    }

    /// <summary>
    /// Merge <paramref name="turtle"/>'s SBCO/<c>bos:</c>-canonical materialization into the existing
    /// default graph — the append counterpart of <see cref="MaterializeAsync"/>. Unlike Replace, the
    /// existing default graph is left untouched (no <c>DROP DEFAULT</c>); this only adds triples.
    /// </summary>
    public async Task MaterializeAppendAsync(string turtle, CancellationToken ct = default)
    {
        var stagingGraph = $"urn:bos:twin-append-source:{Guid.NewGuid():N}";
        await _client.LoadNamedGraphAsync(stagingGraph, turtle, ct).ConfigureAwait(false);
        try
        {
            var statements = new List<string>();
            AppendMaterializationStatements(statements, stagingGraph);
            await _client.UpdateAsync(string.Join(" ;\n", statements), ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Materialized (append) OxiGraph twin from staged RDF ({ClassRules} class rule(s), {PropertyRules} property rule(s))",
                ClassRules.Length, PropertyRules.Length);
        }
        finally
        {
            // Unlike SourceGraph (retained provenance for a full seed/replace), an append batch isn't
            // "the twin's source" in that sense — always discard the staging graph, best-effort so
            // cleanup failure never masks the original exception.
            try
            {
                await _client.DropNamedGraphAsync(stagingGraph, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to drop twin append-materialization staging graph {Graph}", stagingGraph);
            }
        }
    }

    // Builds the copy-through + class/property rule INSERT statements reading from sourceGraph, and
    // appends them to `statements` (shared by both Replace, after a leading DROP DEFAULT, and Append).
    private static void AppendMaterializationStatements(List<string> statements, string sourceGraph)
    {
        // Triples/types already expressed in sbco:/bos: vocabulary pass through unchanged — the
        // materialization rules below only need to cover the REC/Brick vocabulary gap.
        statements.Add($@"
INSERT {{ ?s ?p ?o }}
WHERE {{
  GRAPH <{sourceGraph}> {{ ?s ?p ?o }}
  FILTER(STRSTARTS(STR(?p), ""{SbcoNs}"") || STRSTARTS(STR(?p), ""{BosNs}""))
}}");
        statements.Add($@"
INSERT {{ ?s a ?type }}
WHERE {{
  GRAPH <{sourceGraph}> {{ ?s a ?type }}
  FILTER(STRSTARTS(STR(?type), ""{SbcoNs}"") || STRSTARTS(STR(?type), ""{BosNs}""))
}}");

        foreach (var (from, to) in ClassRules)
        {
            statements.Add($@"
INSERT {{ ?s a <{to}> }}
WHERE {{
  GRAPH <{sourceGraph}> {{ ?s a <{from}> }}
  FILTER NOT EXISTS {{ ?s a <{to}> }}
}}");
        }

        foreach (var (from, to) in PropertyRules)
        {
            statements.Add($@"
INSERT {{ ?s <{to}> ?o }}
WHERE {{
  GRAPH <{sourceGraph}> {{ ?s <{from}> ?o }}
  FILTER NOT EXISTS {{ ?s <{to}> ?o }}
}}");
        }
    }
}
