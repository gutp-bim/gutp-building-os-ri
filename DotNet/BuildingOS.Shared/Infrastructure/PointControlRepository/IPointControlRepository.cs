using BuildingOS.Shared.Domain;

public interface IPointControlRepository
{
    Task<PointControlInfo?> GetPointControlInfoAsync(Guid id);
    Task CreatePointControlInfoAsync(PointControlInfo pointControlInfo);
    Task UpdatePointControlInfoAsync(PointControlInfo pointControlInfo);
}

