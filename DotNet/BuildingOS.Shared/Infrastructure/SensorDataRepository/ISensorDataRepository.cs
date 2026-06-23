namespace BuildingOS.Shared
{
    public interface ISensorDataRepository
    {
        Task<List<ValidTelemetryData>> GetLatestAsync(int count);
    }
}