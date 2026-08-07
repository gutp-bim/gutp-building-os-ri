namespace BuildingOS.Shared.Domain.TwinAdmin;

/// <summary>
/// Admin twin operations (#322): staged import preview/apply and read-only SPARQL. Wraps the OxiGraph
/// primitives so the controller (and its tests) depend on a small seam, not the HTTP client.
/// </summary>
public interface ITwinAdminService
{
    /// <summary>
    /// Stage the Turtle in a temp graph and report triple/gateway counts + collisions, then discard it.
    /// <paramref name="mode"/> must be the mode the import would be applied with: the hierarchy check
    /// (#291) has to see the graph the Turtle actually lands in — an append merges into the default
    /// graph, a replace drops it — so previewing under the wrong mode misreports orphans.
    /// </summary>
    Task<TwinImportPreview> PreviewImportAsync(string turtle, TwinImportMode mode, CancellationToken ct = default);

    /// <summary>Apply the Turtle to the default graph (append or replace). Caller validates first.</summary>
    Task ApplyImportAsync(string turtle, TwinImportMode mode, CancellationToken ct = default);

    /// <summary>
    /// Run a read-only SPARQL query (caller has already guarded it), capping returned rows at
    /// <paramref name="maxRows"/> and aborting after <paramref name="timeout"/>.
    /// </summary>
    Task<SparqlQueryResult> RunReadOnlyQueryAsync(
        string query, int maxRows, TimeSpan timeout, CancellationToken ct = default);
}
