namespace BuildingOS.Shared.Infrastructure;

/// <summary>
/// Point と Device 情報から ControlSchema を解決するインターフェース
/// </summary>
public interface IControlSchemaResolver
{
    /// <summary>
    /// Point と Device 情報から ControlSchema を解決する
    /// </summary>
    /// <param name="point">ポイント情報</param>
    /// <param name="device">デバイス情報（nullable）</param>
    /// <returns>ControlSchema（制御対象外の場合はnull）</returns>
    Task<ControlSchema?> ResolveAsync(Point point, Device? device);
}
