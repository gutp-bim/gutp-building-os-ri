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

    // Discriminated telemetry value (#152, ADR-0006). Numeric stays fully backward-compatible:
    //   - number  → Value (double), ValueType null/"number", ValueText/ValueBool null
    //   - string  → ValueText,       ValueType "string",      Value/ValueBool null
    //   - boolean → ValueBool,       ValueType "boolean",     Value/ValueText null
    // Old rows/entries have no ValueType and populate only Value → they read back as numeric (D2).
    // Charts/aggregation (avg/min/max) remain numeric-only; non-numeric is latest-display-only in Phase A.
    public double? Value { get; set; }
    public string? ValueType { get; set; }
    public string? ValueText { get; set; }
    public bool? ValueBool { get; set; }
}
