using BuildingOS.Shared.Domain;
using BuildingOS.Shared.Domain.PointControl;

public interface IPointControlRepository
{
    Task<PointControlInfo?> GetPointControlInfoAsync(Guid id, CancellationToken ct = default);
    Task CreatePointControlInfoAsync(PointControlInfo pointControlInfo, CancellationToken ct = default);
    Task UpdatePointControlInfoAsync(PointControlInfo pointControlInfo, CancellationToken ct = default);

    /// <summary>
    /// 指定ポイントの制御監査エントリを新しい順（CreatedAt 降順）に最大 <paramref name="limit"/> 件返す（#162）。
    /// </summary>
    Task<IReadOnlyList<PointControlAuditEntry>> ListAuditByPointAsync(string pointId, int limit, CancellationToken ct);
}

