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
/// small set of <c>INSERT ... WHERE</c> queries that derive the missing <c>sbco:</c> triples into the
/// default graph — so the rest of the codebase never has to change.
///
/// Only the 完全一致 (exact-match) REC↔SBCO class/property pairs from
/// <c>docs/architecture/standard-mapping.md</c> are materialized. The 部分一致 (partial-match) pairs
/// (<c>Room</c>↔<c>rec:Room</c>, <c>EquipmentExt</c>↔<c>brick:Equipment</c>,
/// <c>PointExt</c>↔<c>brick:Point</c>, ...) are intentionally excluded pending SBCO/GUTP
/// working-group (HITL) sign-off — see that document's "マテリアライズルール" section. Add a rule
/// here only once that review lands for it.
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
    // it always reflects the current twin's actual source, not a growing history. This is deliberately
    // unlike OxiGraphTwinAdminService's import-preview graphs (per-call GUID, always dropped) — those
    // are throwaway validation scaffolding, this is retained provenance.
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
    /// provenance, not dropped), then replace the default graph with its SBCO/<c>bos:</c>-canonical
    /// materialization. Equivalent to <see cref="OxiGraphClient.ReplaceDefaultGraphAsync"/> except the
    /// default graph ends up vocabulary-normalized regardless of what vocabulary
    /// <paramref name="turtle"/> used.
    /// </summary>
    public async Task MaterializeAsync(string turtle, CancellationToken ct = default)
    {
        // Not wrapped in try/drop-finally on purpose (unlike OxiGraphTwinAdminService's import-preview
        // graphs): SourceGraph is retained provenance, not throwaway staging — see its doc comment.
        await _client.LoadNamedGraphAsync(SourceGraph, turtle, ct).ConfigureAwait(false);
        await _client.ClearDefaultGraphAsync(ct).ConfigureAwait(false);
        await CopyThroughCanonicalTriplesAsync(ct).ConfigureAwait(false);

        foreach (var (from, to) in ClassRules)
        {
            await _client.UpdateAsync($@"
INSERT {{ ?s a <{to}> }}
WHERE {{
  GRAPH <{SourceGraph}> {{ ?s a <{from}> }}
  FILTER NOT EXISTS {{ ?s a <{to}> }}
}}", ct).ConfigureAwait(false);
        }

        foreach (var (from, to) in PropertyRules)
        {
            await _client.UpdateAsync($@"
INSERT {{ ?s <{to}> ?o }}
WHERE {{
  GRAPH <{SourceGraph}> {{ ?s <{from}> ?o }}
  FILTER NOT EXISTS {{ ?s <{to}> ?o }}
}}", ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Materialized OxiGraph twin from staged RDF ({ClassRules} class rule(s), {PropertyRules} property rule(s))",
            ClassRules.Length, PropertyRules.Length);
    }

    // Triples/types already expressed in sbco:/bos: vocabulary pass through unchanged — the
    // materialization rules above only need to cover the REC/Brick vocabulary gap.
    private async Task CopyThroughCanonicalTriplesAsync(CancellationToken ct)
    {
        await _client.UpdateAsync($@"
INSERT {{ ?s ?p ?o }}
WHERE {{
  GRAPH <{SourceGraph}> {{ ?s ?p ?o }}
  FILTER(STRSTARTS(STR(?p), ""{SbcoNs}"") || STRSTARTS(STR(?p), ""{BosNs}""))
}}", ct).ConfigureAwait(false);

        await _client.UpdateAsync($@"
INSERT {{ ?s a ?type }}
WHERE {{
  GRAPH <{SourceGraph}> {{ ?s a ?type }}
  FILTER(STRSTARTS(STR(?type), ""{SbcoNs}"") || STRSTARTS(STR(?type), ""{BosNs}""))
}}", ct).ConfigureAwait(false);
    }
}
