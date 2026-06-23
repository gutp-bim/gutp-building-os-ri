namespace BuildingOS.Shared.Domain.PointControl;

public class PointControlAuditEntry
{
    public Guid Id { get; set; }
    public string? PointId { get; set; }
    public string Request { get; set; } = "";
    public string? Result { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
