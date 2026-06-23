namespace BuildingOS.Shared.Domain.TwinAdmin;

/// <summary>How an RDF import is applied to the default graph.</summary>
public enum TwinImportMode
{
    /// <summary>Add triples to the existing default graph (<c>ImportTurtleAsync</c>).</summary>
    Append,

    /// <summary>Replace the entire default graph (<c>ReplaceDefaultGraphAsync</c>) — destructive.</summary>
    Replace,
}

/// <summary>A gateway_id that the staged import maps across more than one building (uniqueness violation).</summary>
public sealed record GatewayCollision(string GatewayId, int BuildingCount);

/// <summary>
/// Pre-apply analysis of an RDF import, computed by staging the Turtle in a temporary named graph
/// (#322): triple/gateway counts and any gateway_id→multiple-building collisions. <see cref="Valid"/>
/// is false when collisions exist; applying anyway is blocked by the controller.
/// </summary>
public sealed record TwinImportPreview(
    long TripleCount,
    int GatewayCount,
    IReadOnlyList<GatewayCollision> Collisions)
{
    public bool Valid => Collisions.Count == 0;
}

/// <summary>Result of a read-only SPARQL query: columns + rows (capped) + timing.</summary>
public sealed record SparqlQueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Rows,
    int RowCount,
    bool Truncated,
    long ElapsedMs);
