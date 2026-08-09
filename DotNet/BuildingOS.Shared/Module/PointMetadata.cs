namespace BuildingOS.Shared.Module;

/// <summary>
/// Per-point static metadata held in the digital twin (shared point list), used to enrich a
/// point-id-based ingress frame into validated telemetry without the gateway re-sending it each
/// frame. All string fields may be empty when the twin does not define them.
/// </summary>
/// <param name="Building">
/// The denormalized <c>sbco:building</c> literal on the point. This is the enrichment value — it is
/// published as the validated-telemetry <c>building</c> field and becomes the Parquet lake's
/// partition key — so it stays exactly as the twin records it.
/// <para>
/// It is NOT evidence that the point is placed in the hierarchy: it is a string nobody joins, so it
/// can be present on a point no building actually contains, and absent from a point a building
/// plainly does (the twin's own building→equipment join goes through <c>sbco:floor</c>, not this).
/// Use <see cref="HasBuildingPath"/> for that question.
/// </para>
/// </param>
/// <param name="HasBuildingPath">
/// Whether the twin actually places this point under a <c>sbco:Building</c> node, by the same
/// definition the import-time orphan preview uses (#291): traversed from the owning equipment, via
/// the spatial chain (<c>locatedIn</c> → Room → Level → Building) OR the <c>sbco:floor</c> literal
/// join (equipment's floor name → Level → Building). Either path counts; a twin that models no Rooms
/// is legitimate here.
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
