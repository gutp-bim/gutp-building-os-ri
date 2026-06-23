
public class PointControlInfo
{
    public Guid id { get; set; }

    /// <summary>
    /// 制御対象ポイントの point_id（API レイヤーでセット）
    /// </summary>
    public string? PointId { get; set; }

    /// <summary>
    /// 制御タイプ（Kandt, Hono等）
    /// </summary>
    public string Type { get; set; }

    /// <summary>
    /// 制御タイプ固有のパラメータ（JSON文字列）
    /// </summary>
    public string Body { get; set; }

    /// <summary>
    /// 配送先ゲートウェイ id（gateway-bridge 経由の制御で per-gateway subject ルーティングに使用）。
    /// in-process ハンドラ（Hono/Kandt）の場合は null。
    /// </summary>
    public string? GatewayId { get; set; }

    public PointControlResult? Result { get; set; }
    public string? Response { get; set; }
}

public enum PointControlResult
{
    Success,
    Failed,
}

public static class DeviceControlType
{
    /// <summary>
    /// Kandt ゲートウェイ（Azure IoT Hub direct method 経由）。下流は BACnet を話すが、
    /// Egress の制御タイプとしては実体（Kandt ゲートウェイ）に合わせて命名する。
    /// </summary>
    public const string Kandt = "Kandt";

    /// <summary>
    /// bbc-sim / BOWS（BACnet シミュレータゲートウェイ）。下流は BACnet WriteProperty。
    /// 実体はあくまで Bacnet "Sim" であり、純正 BACnet（telemetry 語彙）とは区別する。
    /// </summary>
    public const string BacnetSim = "BacnetSim";

    public const string Hono = "Hono";
}
