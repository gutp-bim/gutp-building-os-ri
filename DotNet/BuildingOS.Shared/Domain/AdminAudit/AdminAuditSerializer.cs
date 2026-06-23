namespace BuildingOS.Shared.Domain.AdminAudit;

/// <summary>
/// Pure 1:1 mapping between <see cref="AdminAuditRecord"/> (domain) and <see cref="AdminAuditEntry"/>
/// (EF). No I/O, no clock, no id generation — those are the store's responsibility — so this is
/// deterministic and unit-testable.
/// </summary>
public static class AdminAuditSerializer
{
    private const string SuccessText = "success";
    private const string FailureText = "failure";

    public static AdminAuditEntry ToEntry(AdminAuditRecord record) => new()
    {
        Id = record.Id,
        SubjectType = record.SubjectType,
        Action = record.Action,
        TargetId = record.TargetId,
        ActorSub = record.ActorSub,
        ActorName = record.ActorName,
        Result = ToResultText(record.Result),
        Detail = record.DetailJson,
        CreatedAt = record.CreatedAt,
    };

    public static AdminAuditRecord ToDomain(AdminAuditEntry entry) => new(
        entry.Id,
        entry.SubjectType,
        entry.Action,
        entry.TargetId,
        entry.ActorSub,
        entry.ActorName,
        ParseResult(entry.Result),
        entry.Detail,
        entry.CreatedAt);

    public static string ToResultText(AdminAuditResult result) =>
        result == AdminAuditResult.Failure ? FailureText : SuccessText;

    public static AdminAuditResult ParseResult(string? text) =>
        string.Equals(text, FailureText, StringComparison.OrdinalIgnoreCase)
            ? AdminAuditResult.Failure
            : AdminAuditResult.Success;
}
