using BuildingOS.Shared.Infrastructure.PointControlRepository;
using BuildingOS.Shared.Domain.Grouping;
using BuildingOS.Shared.Domain.PointControl;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BuildingOS.Shared.Infrastructure.PointControlAudit;

public sealed class EfPointControlRepository : IPointControlRepository
{
    private const string UniqueViolation = "23505";

    private readonly RelationalDbContext _context;

    public EfPointControlRepository(RelationalDbContext context) => _context = context;

    public async Task<PointControlInfo?> GetPointControlInfoAsync(Guid id, CancellationToken ct = default)
    {
        var entry = await _context.PointControlAudits
            .FindAsync([id], ct)
            .ConfigureAwait(false);
        return entry is null ? null : PointControlAuditSerializer.ToDomain(entry);
    }

    public async Task CreatePointControlInfoAsync(
        PointControlInfo info, ControlActor actor, CancellationToken ct = default)
    {
        var entry = PointControlAuditSerializer.ToEntry(info, actor);
        _context.PointControlAudits.Add(entry);
        try
        {
            await _context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == UniqueViolation)
        {
            // PK already exists — equivalent to ON CONFLICT DO NOTHING
            _context.Entry(entry).State = EntityState.Detached;
        }
    }

    public async Task UpdatePointControlInfoAsync(PointControlInfo info, CancellationToken ct = default)
    {
        var entry = await _context.PointControlAudits
            .FindAsync([info.id], ct)
            .ConfigureAwait(false);
        if (entry is null) return;

        PointControlAuditSerializer.ApplyResult(entry, info);
        await _context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PointControlAuditEntry>> ListAuditByPointAsync(
        ControlAuditQuery query, CancellationToken ct)
    {
        var rows = _context.PointControlAudits
            .AsNoTracking()
            .Where(e => e.PointId == query.PointId);

        if (query.Start is { } start) rows = rows.Where(e => e.CreatedAt >= start);
        if (query.End is { } end) rows = rows.Where(e => e.CreatedAt < end);
        if (query.After is { } after)
        {
            // Keyset: strictly before the cursor row in (created_at DESC, id DESC) order. Both the
            // comparison and the ordering run in PostgreSQL, so uuid ordering is consistent between them.
            rows = rows.Where(e => e.CreatedAt < after.CreatedAt
                                   || (e.CreatedAt == after.CreatedAt && e.Id.CompareTo(after.Id) < 0));
        }

        return await rows
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .Take(query.Limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}
