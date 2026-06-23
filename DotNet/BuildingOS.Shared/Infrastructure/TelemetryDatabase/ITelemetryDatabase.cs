using BuildingOS.Shared;

namespace BuildingOS.Shared.Infrastructure;

public interface ITelemetryDatabase
{
    public Task<ValidTelemetryData[]> GetWarmTelemetries(
        string pointId,
        DateTime startTime, 
        DateTime endTime
    );
    
    public Task<ValidTelemetryData[]> GetColdTelemetries(
        string pointId,
        DateTime startTime, 
        DateTime endTime
    );
    
    public Task<Dictionary<string, ValidTelemetryData[]>> GetColdTelemetries(
        string[] pointIds,
        DateTime startTime, 
        DateTime endTime
    );

    public Task<ValidTelemetryData?> GetHotTelemetry(string pointId);
} 