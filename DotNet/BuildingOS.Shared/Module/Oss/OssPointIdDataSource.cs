using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Module.Oss;

/// <summary>
/// Phase 0 placeholder: MinIO S3 implementation will replace this in Phase 1.
/// </summary>
public class OssPointIdDataSource(ILogger<OssPointIdDataSource> logger) : IPointIdDataSource
{
    public Task<PointIdInfo[]> GetPointIdInfosAsync()
    {
        logger.LogWarning("[OSS] OssPointIdDataSource.GetPointIdInfosAsync — no-op placeholder");
        return Task.FromResult(Array.Empty<PointIdInfo>());
    }
}
