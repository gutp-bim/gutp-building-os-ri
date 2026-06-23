using BuildingOS.Shared.Infrastructure.BlobStorage;

namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>Writes one partition batch as a single Parquet object to the lake (#213).</summary>
public interface IParquetLakeWriter
{
    /// <summary>Writes the batch and returns (objectKey, bytesWritten). Deterministic key (idempotent).</summary>
    Task<(string Key, long Bytes)> WriteAsync(PartitionBatch batch, CancellationToken ct = default);
}

/// <summary>MinIO-backed <see cref="IParquetLakeWriter"/> using the canonical lake layout + serializer.</summary>
public sealed class MinioParquetLakeWriter : IParquetLakeWriter
{
    public const string LakeBucket = "cold";

    private readonly IBlobStorage _storage;

    public MinioParquetLakeWriter(IBlobStorage storage)
    {
        _storage = storage;
    }

    public async Task<(string Key, long Bytes)> WriteAsync(PartitionBatch batch, CancellationToken ct = default)
    {
        var key = LakePartitionKey.For(batch.Building, batch.HourUtc, batch.FirstSeq, batch.LastSeq);

        using var ms = new MemoryStream();
        await ParquetTelemetrySerializer.WriteAsync(batch.Rows, ms, ct).ConfigureAwait(false);
        ms.Position = 0;
        var bytes = ms.Length;

        await _storage.PutAsync(LakeBucket, key, ms, "application/octet-stream", ct).ConfigureAwait(false);
        return (key, bytes);
    }
}
