using System.Text.Json;
using BuildingOS.Shared.Domain;
using BuildingOS.Shared.Infrastructure.ControlRouting;
using Microsoft.Azure.Devices;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Infrastructure.DeviceControlHandler;

/// <summary>
/// Kandt ゲートウェイ制御ハンドラー（Azure IoT Hub direct method 経由）。
/// 下流ゲートウェイは BACnet を話すため、direct method のペイロードは BACnet 形式で構築する。
/// IoT Hub の接続文字列 / module id は env 直読みをやめ、解決済みの <see cref="GatewayConnection"/>
/// （Settings: iotHubConnectionString / moduleId）から受け取る（#154 Phase 2）。
/// </summary>
public class KandtDeviceControlHandler : IDeviceControlHandler
{
    private readonly ILogger<KandtDeviceControlHandler> _logger;

    // JSONシリアライザオプション（lowerCamelCase対応）
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public string BindingType => BindingTypes.Kandt;

    public KandtDeviceControlHandler(ILogger<KandtDeviceControlHandler> logger)
    {
        _logger = logger;
    }

    public async Task<PointControlInfo> ExecuteControlAsync(
        PointControlInfo pointControlInfo, GatewayConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var connectionString = connection.Get("iotHubConnectionString")
                ?? throw new InvalidOperationException(
                    $"Kandt connection: iotHubConnectionString not configured for gateway={connection.GatewayId}");
            var moduleId = connection.Get("moduleId")
                ?? throw new InvalidOperationException(
                    $"Kandt connection: moduleId not configured for gateway={connection.GatewayId}");

            // Bodyフィールドからリクエストをデシリアライズ（lowerCamelCase対応）
            if (string.IsNullOrEmpty(pointControlInfo.Body))
                throw new ArgumentException("Body フィールドが空です", nameof(pointControlInfo));

            var request = JsonSerializer.Deserialize<KandtControlRequest>(pointControlInfo.Body, JsonOptions)
                ?? throw new ArgumentException("Body のデシリアライズに失敗しました", nameof(pointControlInfo));

            // バリデーション
            if (request.StringValue == null && request.BoolValue == null && request.IntValue == null)
            {
                throw new ArgumentException("IntValue, BoolValue, StringValueのいずれかが必要です");
            }

            // direct method ペイロード（下流ゲートウェイが解釈する BACnet 形式）の構築
            var payload = new
            {
                protocol = "BACnet",
                bacNet = new
                {
                    functionType = "1",
                    serviceType = "102",
                    destDevId = request.DestDevId,
                    objectType = request.ObjectType,
                    values = new[]
                    {
                        new
                        {
                            instanceNo = request.ObjectInstanceNo.ToString(),
                            properties = new[]
                            {
                                new
                                {
                                    type = "85",
                                    value = request.StringValue ?? request.IntValue?.ToString() ??
                                        request.BoolValue?.ToString()
                                }
                            }
                        }
                    }
                }
            };

            // IoT Hubへのメソッド呼び出し
            using var serviceClient = ServiceClient.CreateFromConnectionString(connectionString);
            var methodInvocation = new CloudToDeviceMethod(request.MethodName)
            { ResponseTimeout = TimeSpan.FromSeconds(30) };
            methodInvocation.SetPayloadJson(JsonSerializer.Serialize(payload));

            var response = await serviceClient.InvokeDeviceMethodAsync(
                request.GatewayId,
                moduleId,
                methodInvocation).ConfigureAwait(false);

            _logger.LogInformation("Kandt制御実行: Status={Status}, GatewayId={GatewayId}",
                response.Status, request.GatewayId);

            // 結果を設定
            pointControlInfo.Result = response.Status == 200
                ? PointControlResult.Success
                : PointControlResult.Failed;
            pointControlInfo.Response = response.GetPayloadAsJson();

            return pointControlInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Kandt制御エラー: id={ControlId}", pointControlInfo.id);
            pointControlInfo.Result = PointControlResult.Failed;
            pointControlInfo.Response = ex.Message;
            return pointControlInfo;
        }
    }
}
