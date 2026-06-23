using BuildingOS.Shared.Domain.AdminAudit;
using BuildingOS.Shared.Domain.Grouping;
using Microsoft.EntityFrameworkCore;

namespace BuildingOS.Shared.Infrastructure.AdminAudit;

/// <summary>EF Core-backed <see cref="IAdminAuditRecorder"/> over the <c>admin_audit</c> table.</summary>
public sealed class EfAdminAuditRecorder : IAdminAuditRecorder
{
    private const int MaxLimit = 500;

    private readonly RelationalDbContext _context;

    public EfAdminAuditRecorder(RelationalDbContext context) => _context = context;

    public async Task RecordAsync(AdminAuditRecord record, CancellationToken ct = default)
    {
        // The store owns id/timestamp assignment so callers can pass partially-built records.
        var normalized = record with
        {
            Id = record.Id == Guid.Empty ? Guid.NewGuid() : record.Id,
            CreatedAt = record.CreatedAt == default ? DateTime.UtcNow : record.CreatedAt,
        };

        _context.AdminAudits.Add(AdminAuditSerializer.ToEntry(normalized));
        await _context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AdminAuditRecord>> ListAsync(AdminAuditQuery query, CancellationToken ct = default)
    {
        var limit = query.Limit <= 0 ? 100 : Math.Min(query.Limit, MaxLimit);

        var q = _context.AdminAudits.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.SubjectType))
        {
            q = q.Where(e => e.SubjectType == query.SubjectType);
        }
        if (!string.IsNullOrWhiteSpace(query.TargetId))
        {
            q = q.Where(e => e.TargetId == query.TargetId);
        }

        var rows = await q
            .OrderByDescending(e => e.CreatedAt)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(AdminAuditSerializer.ToDomain).ToList();
    }
}
