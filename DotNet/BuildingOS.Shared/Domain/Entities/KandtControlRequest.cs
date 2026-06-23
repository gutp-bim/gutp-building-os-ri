namespace BuildingOS.Shared.Domain;

/// <summary>
/// Kandt ゲートウェイ制御リクエスト（Azure IoT Hub direct method 経由・下流は BACnet WriteProperty）
/// </summary>
public class KandtControlRequest
{
    public string GatewayId { get; set; }
    public string DestDevId { get; set; }
    public string ObjectType { get; set; }
    public string MethodName { get; set; }
    public int ObjectInstanceNo { get; set; }
    public int? IntValue { get; set; }
    public bool? BoolValue { get; set; }
    public string? StringValue { get; set; }
    public int? Priority { get; set; }
}

