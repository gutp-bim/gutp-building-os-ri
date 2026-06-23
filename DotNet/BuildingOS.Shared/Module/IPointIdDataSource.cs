namespace BuildingOS.Shared.Module;

/// <summary>
/// PointIdマッピングデータのソースを抽象化するインターフェース
/// </summary>
public interface IPointIdDataSource
{
    Task<PointIdInfo[]> GetPointIdInfosAsync();
}

/// <summary>
/// 従来の静的データソースからPointIdマッピングを提供する（後方互換用）
/// </summary>
public class StaticPointIdDataSource : IPointIdDataSource
{
    public Task<PointIdInfo[]> GetPointIdInfosAsync()
    {
        return Task.FromResult(PointIdDataSource.PointIdInfos);
    }
}
