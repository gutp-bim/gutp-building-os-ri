namespace BuildingOS.Shared.Module;

/// <summary>
/// Per-point static metadata held in the digital twin (shared point list), used to enrich a
/// point-id-based ingress frame into validated telemetry without the gateway re-sending it each
/// frame. All string fields may be empty when the twin does not define them.
/// </summary>
/// <param name="Building">
/// The enrichment value — published as the validated-telemetry <c>building</c> field and the
/// Parquet lake's partition key. Since #527 it is the <c>sbco:id</c> of the Building the TOPOLOGY
/// reaches the point in (the same traversal as <see cref="HasBuildingPath"/>); the denormalized
/// <c>sbco:building</c> literal is only the fallback for a point the topology does not place, so an
/// unplaced point keeps the partition it was already landing in.
/// <para>
/// A non-empty value is therefore NOT evidence that the point is placed in the hierarchy: for an
/// unplaced point it is that literal, a string nobody joins. Use <see cref="HasBuildingPath"/> for
/// that question.
/// </para>
/// </param>
/// <param name="HasBuildingPath">
/// Whether the twin actually places this point under a <c>sbco:Building</c> node, by the same
/// definition the import-time orphan preview uses (#291): traversed from the owning equipment, via
/// the Room spatial chain (<c>locatedIn</c> → Room → Level → Building) OR direct Level location
/// (<c>locatedIn</c> → Level → Building). Either counts, so a twin that models no Rooms is legitimate.
/// Topology only: the <c>sbco:floor</c> literal places nothing.
/// </param>
public sealed record PointMetadata(
    string PointId,
    string Building,
    string Name,
    string DeviceId,
    string GatewayId,
    bool HasBuildingPath = false);

/// <summary>Source of all <see cref="PointMetadata"/> in the digital twin (e.g. OxiGraph SPARQL).</summary>
public interface IPointMetadataDataSource
{
    Task<PointMetadata[]> GetAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves <see cref="PointMetadata"/> by point id, backed by a process-local cache so the gRPC
/// ingest hot path does not query the graph database per frame.
/// </summary>
public interface IPointMetadataCache
{
    Task<PointMetadata?> GetAsync(string pointId, CancellationToken cancellationToken = default);
}
