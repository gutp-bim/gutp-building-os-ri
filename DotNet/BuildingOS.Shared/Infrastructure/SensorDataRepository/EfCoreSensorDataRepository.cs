using Microsoft.EntityFrameworkCore;

namespace BuildingOS.Shared
{
    public class EfCoreSensorDataRepository(ColdTelemetryContext context) : ISensorDataRepository
    {
        public async Task<List<ValidTelemetryData>> GetLatestAsync(int count)
        {
            return await context.Telemetries
                .OrderByDescending(x => x.Datetime)
                .Take(count)
                .ToListAsync();
        }
    }
}