using BuildingOS.Shared.Infrastructure.BlobStorage;
using Microsoft.Extensions.Caching.Memory;

namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>
/// Records that lake partition keys changed (#527) — after a twin topology change, or after the #527
/// upgrade moved the key from the <c>sbco:building</c> literal to the topology's building. Lake reads
/// whose range starts before that time (plus a grace) are then never pruned to a learned building.
/// </summary>
public interface ILakePartitionKeyChanges
{
    /// <summary>Records "keys changed now" in the lake and forgets this process's learned buildings.</summary>
    Task MarkChangedAsync(CancellationToken ct);
}

public sealed class LakePartitionKeyChanges(IBlobStorage storage, IMemoryCache cache) : ILakePartitionKeyChanges
{
    private readonly ParquetLakeScan _scan = new(storage, cache);

    public Task MarkChangedAsync(CancellationToken ct) => _scan.MarkKeysChangedAsync(DateTime.UtcNow, ct);
}
