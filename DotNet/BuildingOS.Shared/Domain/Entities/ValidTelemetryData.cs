namespace BuildingOS.Shared;

// ValidTelemetryEntity と同様の型
// TODO: ValidTelemetryEntity と共通化 or 自動生成時にこのクラスの型も更新する
public class ValidTelemetryData
{
    public string? Building { get; set; }
    public string? Data { get; set; } // JSON文字列として受け取る
    public string? Datetime { get; set; }
    public string? DeviceId { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? PointId { get; set; }
    public double? Value { get; set; } 
}