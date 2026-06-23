namespace BuildingOS.Shared.Infrastructure.Telemetry;

public record TelemetryQueryRequest(
    string PointId,
    DateTime? Start = null,
    DateTime? End = null,
    TelemetryGranularity Granularity = TelemetryGranularity.Raw,
    bool Latest = false);
