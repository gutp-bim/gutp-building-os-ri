using BuildingOS.Shared.Domain;
using BuildingOS.Shared.Infrastructure.ControlRouting;
using BuildingOS.Shared.Infrastructure.DeviceControlHandler;
using Microsoft.Extensions.Logging;

namespace BuildingOS.ConnectorWorker.Infrastructure.DeviceControlHandler;

/// <summary>
/// OSS / CI 用シミュレーション制御ハンドラー。
/// ENABLE_SIM_CONTROL=true の場合のみ Program.cs で登録される。
/// 接続設定（<see cref="GatewayConnection"/>）は使用しない（実機接続を持たないため）。
///
/// SIM_CONTROL_DELAY_MS  : 人工遅延 ms（default: 100）
/// SIM_CONTROL_FAIL_RATE : 0.0–1.0 の失敗注入率（default: 0.0）
/// </summary>
public sealed class SimulatedDeviceControlHandler(ILogger<SimulatedDeviceControlHandler> logger) : IDeviceControlHandler
{
    /// <summary>シミュレータの binding 種別（実ゲートウェイ binding とは区別する）。</summary>
    public string BindingType => BindingTypes.Simulated;

    public async Task<PointControlInfo> ExecuteControlAsync(
        PointControlInfo info, GatewayConnection connection, CancellationToken cancellationToken)
    {
        var delayMs = Math.Max(0, int.TryParse(Environment.GetEnvironmentVariable("SIM_CONTROL_DELAY_MS"), out var d) ? d : 100);
        var failRate = Math.Clamp(
            double.TryParse(
                Environment.GetEnvironmentVariable("SIM_CONTROL_FAIL_RATE"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var f) ? f : 0.0,
            0.0, 1.0);

        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);

        if (Random.Shared.NextDouble() < failRate)
        {
            logger.LogInformation("Simulated failure injected for {ControlId}", info.id);
            info.Result = PointControlResult.Failed;
            info.Response = "Simulated failure (SIM_CONTROL_FAIL_RATE)";
        }
        else
        {
            info.Result = PointControlResult.Success;
            info.Response = "Simulated success";
        }

        return info;
    }
}
