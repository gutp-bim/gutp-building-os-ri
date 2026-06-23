namespace BuildingOS.Shared;

public static class SchemaPathDefine
{
    private static readonly string SchemaPath = Path.Combine(Directory.GetCurrentDirectory(), "Schema");
    public static readonly string ValidTelemetrySchema = Path.Combine(SchemaPath, "valid-telemetry.json");
    public static readonly string ElectricDeviceTelemetrySchema = Path.Combine(SchemaPath, "electric-device-telemetry.json");
    public static readonly string HvacDeviceTelemetrySchema = Path.Combine(SchemaPath, "hvac-device-telemetry.json");
}