using BuildingOS.Shared.Domain;
using BuildingOS.Shared.Domain.PointControl;

public interface IPointControlRepository
{
    Task<PointControlInfo?> GetPointControlInfoAsync(Guid id, CancellationToken ct = default);
    /// <summary>
    /// Opens the audit row for a control command. <paramref name="actor"/> is the authenticated
    /// principal that issued it (#461) and is persisted alongside the command, never published with it.
    /// </summary>
    Task CreatePointControlInfoAsync(
        PointControlInfo pointControlInfo, ControlActor actor, CancellationToken ct = default);
    Task UpdatePointControlInfoAsync(PointControlInfo pointControlInfo, CancellationToken ct = default);

    /// <summary>
    /// 指定ポイントの制御監査エントリを新しい順（CreatedAt 降順、同時刻は Id 降順）に最大
    /// <c>query.Limit</c> 件返す（#162）。期間 [Start, End) とカーソル（その行より後ろ）で絞る（#478）。
    /// </summary>
    Task<IReadOnlyList<PointControlAuditEntry>> ListAuditByPointAsync(
        BuildingOS.Shared.Infrastructure.PointControlRepository.ControlAuditQuery query, CancellationToken ct);
}

