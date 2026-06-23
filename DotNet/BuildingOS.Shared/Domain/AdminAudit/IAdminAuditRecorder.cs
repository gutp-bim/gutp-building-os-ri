namespace BuildingOS.Shared.Domain.AdminAudit;

/// <summary>
/// Write + read access to the shared admin audit log. All admin tools record their mutating
/// operations here (success and failure) and the admin audit list view reads from here.
/// </summary>
public interface IAdminAuditRecorder
{
    /// <summary>Append one audit record. The implementation assigns Id/CreatedAt if left at defaults.</summary>
    Task RecordAsync(AdminAuditRecord record, CancellationToken ct = default);

    /// <summary>List audit records most-recent first, filtered by <paramref name="query"/>.</summary>
    Task<IReadOnlyList<AdminAuditRecord>> ListAsync(AdminAuditQuery query, CancellationToken ct = default);
}
