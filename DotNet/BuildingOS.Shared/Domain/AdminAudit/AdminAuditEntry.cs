namespace BuildingOS.Shared.Domain.AdminAudit;

/// <summary>EF Core entity for the generic <c>admin_audit</c> table (shared admin audit, #322/#323/#324/#325).</summary>
public class AdminAuditEntry
{
    public Guid Id { get; set; }
    public string SubjectType { get; set; } = "";
    public string Action { get; set; } = "";
    public string? TargetId { get; set; }
    public string ActorSub { get; set; } = "";
    public string? ActorName { get; set; }
    public string Result { get; set; } = "";
    public string? Detail { get; set; }
    public DateTime CreatedAt { get; set; }
}
