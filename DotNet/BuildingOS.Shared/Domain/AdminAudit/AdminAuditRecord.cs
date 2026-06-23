namespace BuildingOS.Shared.Domain.AdminAudit;

/// <summary>Outcome of an audited admin operation.</summary>
public enum AdminAuditResult
{
    Success,
    Failure,
}

/// <summary>
/// One audited admin (IsAdmin-gated) mutating operation, shared across the admin tools
/// (digital-twin import/SPARQL, gateway management, OIDC client management, user/role management).
/// <para>
/// <see cref="DetailJson"/> carries operation-specific metadata (mode/byte-count/hash/row-count/
/// before→after values). It MUST NOT contain secrets (client secrets, passwords, certificate private
/// keys); record presence/operation only.
/// </para>
/// </summary>
public sealed record AdminAuditRecord(
    Guid Id,
    string SubjectType,
    string Action,
    string? TargetId,
    string ActorSub,
    string? ActorName,
    AdminAuditResult Result,
    string? DetailJson,
    DateTime CreatedAt)
{
    /// <summary>
    /// Build a record for a new audit event. The store assigns the final <see cref="Id"/>/
    /// <see cref="CreatedAt"/> if left at their defaults; this factory fills them so callers need not.
    /// </summary>
    public static AdminAuditRecord Create(
        string subjectType,
        string action,
        string? targetId,
        string actorSub,
        string? actorName,
        AdminAuditResult result,
        string? detailJson) =>
        new(
            Guid.NewGuid(),
            subjectType,
            action,
            targetId,
            actorSub,
            actorName,
            result,
            detailJson,
            DateTime.UtcNow);
}

/// <summary>Known <see cref="AdminAuditRecord.SubjectType"/> values (the four admin tool surfaces).</summary>
public static class AdminAuditSubjects
{
    public const string Twin = "twin";
    public const string Gateway = "gateway";
    public const string OidcClient = "oidc-client";
    public const string User = "user";
}

/// <summary>Filter for listing audit records (most-recent first, capped by <see cref="Limit"/>).</summary>
public sealed record AdminAuditQuery(
    string? SubjectType = null,
    string? TargetId = null,
    int Limit = 100);
