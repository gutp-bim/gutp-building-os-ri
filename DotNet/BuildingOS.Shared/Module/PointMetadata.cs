namespace BuildingOS.Shared.Module;

/// <summary>
/// Per-point static metadata held in the digital twin (shared point list), used to enrich a
/// point-id-based ingress frame into validated telemetry without the gateway re-sending it each
/// frame. All fields may be empty when the twin does not define them.
/// </summary>
public sealed record PointMetadata(
    string PointId,
    string Building,
    string Name,
    string DeviceId,
    string GatewayId);

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
