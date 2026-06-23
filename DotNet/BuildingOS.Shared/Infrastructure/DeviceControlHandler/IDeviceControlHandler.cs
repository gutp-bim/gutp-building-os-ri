using BuildingOS.Shared.Domain;
using BuildingOS.Shared.Infrastructure.ControlRouting;

namespace BuildingOS.Shared.Infrastructure.DeviceControlHandler;

/// <summary>
/// 機器制御ハンドラー（プロトコルアダプタ）のインターフェース。
/// アダプタは「プロトコルの喋り方」だけを担い、接続設定（host/credentials 等）は env を直読みせず、
/// ルータが解決した <see cref="GatewayConnection"/> を引数で受け取る（#154 Phase 2）。
/// </summary>
public interface IDeviceControlHandler
{
    /// <summary>
    /// このハンドラーがサポートする binding 種別（<see cref="BindingTypes"/>: hono / kandt 等）。
    /// Worker は gatewayId から解決した <see cref="GatewayConnection.BindingType"/> でハンドラを選択する。
    /// </summary>
    string BindingType { get; }

    /// <summary>
    /// 機器制御を実行する。
    /// </summary>
    /// <param name="pointControlInfo">制御情報（point_id・Body 等）。</param>
    /// <param name="connection">解決済みのゲートウェイ接続設定（接続先 host/credentials を保持）。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>制御結果を更新した PointControlInfo。</returns>
    Task<PointControlInfo> ExecuteControlAsync(
        PointControlInfo pointControlInfo, GatewayConnection connection, CancellationToken cancellationToken);
}
